using System.Net;
using System.Text;
using AddonStore.Web.Auth;
using AddonStore.Web.Data;
using AddonStore.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AddonStore.Web.Tests;

/// <summary>
/// "Sign in with Microsoft" set up on the admin page: precedence of the app settings, input checks, the mapping of
/// Microsoft's AADSTS answers, options rebuilt without a restart, and the secret (kept when the field stays empty,
/// never handed to the page).
/// </summary>
[Collection("Settings cache")]
public class EntraSetupTests : IDisposable
{
    private const string Tenant = "11111111-2222-3333-4444-555555555555";
    private const string Extra = "99999999-8888-7777-6666-555555555555";
    private const string ClientA = "aaaaaaaa-1111-2222-3333-444444444444";
    private const string ClientB = "bbbbbbbb-1111-2222-3333-444444444444";
    private const string Secret = "Abc~very-secret-value-0123456789";

    private readonly SqliteConnection _conn;
    private ServiceProvider _sp = null!;
    private readonly StubHandler _http = new();

    public EntraSetupTests()
    {
        SettingsService.ClearCache();   // the settings cache is process-wide
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
    }

    public void Dispose() { _sp?.Dispose(); _conn.Dispose(); SettingsService.ClearCache(); }

    private IServiceProvider Build(params (string Key, string Value)[] appSettings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(appSettings.Select(v => new KeyValuePair<string, string?>("Entra:" + v.Key, v.Value))).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(_conn));
        services.AddIdentityCore<AppUser>(IdentitySetup.Apply).AddRoles<Microsoft.AspNetCore.Identity.IdentityRole>().AddEntityFrameworkStores<AppDbContext>();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddAuthentication().AddEntraSignIn(development: false);   // the same wiring as Program.cs
        services.AddHttpClient(EntraProbe.HttpName).ConfigurePrimaryHttpMessageHandler(() => _http);
        services.AddSingleton<EntraProbe>();
        services.AddScoped<SettingsService>();
        services.AddScoped<AuditService>();
        services.AddScoped<EntraSetupService>();
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
        return _sp;
    }

    private async Task<T> Scoped<T>(Func<IServiceProvider, Task<T>> f)
    {
        using var scope = _sp.CreateScope();
        return await f(scope.ServiceProvider);
    }

    private Task<EntraSetupService.Outcome> Save(string? clientId = ClientA, string? tenant = Tenant, string? secret = Secret,
                                                 string? dirs = null, bool enabled = true) =>
        Scoped(sp => sp.GetRequiredService<EntraSetupService>().SaveAsync(new(clientId, tenant, secret, dirs, enabled), "Admin", default));

    private Task<string> Stored(string key) => Scoped(sp => sp.GetRequiredService<SettingsService>().GetAsync(key));

    private EntraRuntime Runtime => _sp.GetRequiredService<EntraRuntime>();
    private OpenIdConnectOptions SchemeOptions => _sp.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(EntraSettings.Scheme);

    // --- precedence -------------------------------------------------------------------

    [Fact]
    public async Task Store_values_apply_without_app_settings()
    {
        Build();
        Assert.False(Runtime.FromAppSettings);
        Assert.False((await Runtime.ReloadAsync()).Enabled);
        Assert.True((await Save()).Ok);
        var s = Runtime.Current;
        Assert.True(s.Enabled);
        Assert.Equal(EntraSource.Store, s.Source);
        Assert.Equal(ClientA, s.ClientId);
        Assert.Equal($"https://login.microsoftonline.com/{Tenant}/v2.0", s.Authority);
        Assert.Equal(new[] { Tenant }, s.AllowedTenants);
    }

    [Fact]
    public async Task App_settings_win_over_the_store_and_cannot_be_changed_on_the_page()
    {
        Build(("ClientId", ClientB), ("ClientSecret", "from-app-settings"), ("TenantId", Extra));
        Assert.True(Runtime.FromAppSettings);
        // a value the page might have stored earlier is ignored
        await Scoped(async sp => { await sp.GetRequiredService<SettingsService>().SetAsync(EntraRuntime.ClientIdKey, ClientA); return 0; });
        var s = await Runtime.ReloadAsync();
        Assert.Equal(EntraSource.AppSettings, s.Source);
        Assert.Equal(ClientB, s.ClientId);
        Assert.False((await Save()).Ok);
        Assert.False((await Scoped(sp => sp.GetRequiredService<EntraSetupService>().RemoveSecretAsync("Admin"))).Ok);
        var section = await Scoped(sp => sp.GetRequiredService<EntraSetupService>().LoadAsync("https://store.example", false));
        Assert.True(section.FromAppSettings);
        Assert.Equal(ClientB, section.ClientId);
        Assert.True(section.HasSecret);
        Assert.DoesNotContain(section.GetType().GetProperties().Select(p => p.GetValue(section) as string), v => v?.Contains("from-app-settings") == true);
    }

    [Fact]
    public void Any_entra_app_setting_means_app_settings_mode()
    {
        static IConfiguration C(string key, string value) => new ConfigurationBuilder().AddInMemoryCollection(new[] { new KeyValuePair<string, string?>(key, value) }).Build();
        Assert.True(EntraSettings.HasAppSettings(C("Entra:TenantId", Tenant)));
        Assert.False(EntraSettings.HasAppSettings(C("Entra:TenantId", "  ")));
        Assert.False(EntraSettings.HasAppSettings(C("Email:From", "x")));
    }

    // --- input checks ---------------------------------------------------------------------

    [Theory]
    [InlineData(Tenant, true)]
    [InlineData("power-pdf.de", true)]
    [InlineData("contoso.onmicrosoft.com", true)]
    [InlineData("organizations", false)]
    [InlineData("common", false)]
    [InlineData("consumers", false)]
    [InlineData("contoso.com/../x", false)]
    [InlineData("contoso.com?x=1", false)]
    [InlineData("https://contoso.com", false)]
    [InlineData("-bad.com", false)]
    [InlineData("{11111111-2222-3333-4444-555555555555}", false)]
    [InlineData("", false)]
    public void Directory_input(string value, bool valid) => Assert.Equal(valid, EntraSettings.IsDirectory(value));

    [Theory]
    [InlineData(ClientA, true)]
    [InlineData("AAAAAAAA-1111-2222-3333-444444444444", true)]
    [InlineData("aaaaaaaa111122223333444444444444", false)]
    [InlineData("my-app", false)]
    [InlineData("", false)]
    public void Client_id_input(string value, bool valid) => Assert.Equal(valid, EntraSettings.IsClientId(value));

    [Fact]
    public void Validation_of_the_whole_input()
    {
        Assert.Null(EntraSetupService.Validate(new(ClientA, "power-pdf.de", "", $"{Extra}, tungsten.example.com", true), forSave: true));
        Assert.NotNull(EntraSetupService.Validate(new("nope", Tenant, "", null, true), forSave: true));
        Assert.NotNull(EntraSetupService.Validate(new(ClientA, "organizations", "", null, true), forSave: true));
        Assert.NotNull(EntraSetupService.Validate(new(ClientA, Tenant, new string('x', 501), null, true), forSave: true));
        Assert.NotNull(EntraSetupService.Validate(new(ClientA, Tenant, "a\nb", null, true), forSave: true));
        Assert.NotNull(EntraSetupService.Validate(new(ClientA, Tenant, "", "contoso.com, not a domain", true), forSave: true));
        Assert.NotNull(EntraSetupService.Validate(new(ClientA, Tenant, "", string.Join(",", Enumerable.Range(1, 21).Select(i => $"d{i}.example.com")), true), forSave: true));
        // the test needs client id and directory; saving "off" does not
        Assert.NotNull(EntraSetupService.Validate(new("", Tenant, "", null, false), forSave: false));
        Assert.Null(EntraSetupService.Validate(new("", "", "", null, false), forSave: true));
    }

    [Fact]
    public async Task Switching_on_needs_all_three_values()
    {
        Build();
        var r = await Save(secret: "");
        Assert.False(r.Ok);
        Assert.False((await Runtime.ReloadAsync()).Enabled);
        Assert.Equal("", await Stored(EntraRuntime.ClientIdKey));   // nothing saved
    }

    // --- AADSTS mapping -------------------------------------------------------------------

    [Theory]
    [InlineData(400, "{\"error\":\"invalid_tenant\",\"error_codes\":[90002]}", EntraProbe.Outcome.DirectoryNotFound, "AADSTS90002")]
    [InlineData(400, "{\"error\":\"unauthorized_client\",\"error_codes\":[700016]}", EntraProbe.Outcome.AppNotFound, "AADSTS700016")]
    [InlineData(401, "{\"error\":\"invalid_client\",\"error_codes\":[7000215]}", EntraProbe.Outcome.BadSecret, "AADSTS7000215")]
    [InlineData(401, "{\"error\":\"invalid_client\",\"error_codes\":[7000222]}", EntraProbe.Outcome.BadSecret, "AADSTS7000222")]
    [InlineData(401, "{\"error\":\"invalid_client\",\"error_codes\":[7000218]}", EntraProbe.Outcome.SecretMissing, "AADSTS7000218")]
    [InlineData(400, "{\"error\":\"invalid_request\",\"error_codes\":[7000216]}", EntraProbe.Outcome.SecretMissing, "AADSTS7000216")]
    [InlineData(400, "{\"error\":\"x\",\"error_description\":\"AADSTS700016: Application with identifier 'x' was not found\"}", EntraProbe.Outcome.AppNotFound, "AADSTS700016")]
    [InlineData(400, "{\"error\":\"x\",\"error_codes\":[50000]}", EntraProbe.Outcome.Unexpected, "AADSTS50000")]
    [InlineData(500, "<html>", EntraProbe.Outcome.Unexpected, "HTTP 500")]
    public void Microsoft_errors_are_mapped(int status, string body, EntraProbe.Outcome outcome, string code)
    {
        var r = EntraProbe.Map(status, body);
        Assert.Equal(outcome, r.Outcome);
        Assert.Equal(code, r.Code);
    }

    [Fact]
    public async Task Test_reports_each_step()
    {
        Build();
        var probe = _sp.GetRequiredService<EntraProbe>();
        _http.Answer = req => req.RequestUri!.AbsolutePath.Contains("openid-configuration")
            ? (HttpStatusCode.BadRequest, "{\"error\":\"invalid_tenant\",\"error_codes\":[90002]}") : (HttpStatusCode.OK, "{}");
        Assert.Equal(EntraProbe.Outcome.DirectoryNotFound, (await probe.TestAsync("nowhere.example.com", ClientA, Secret, default)).Outcome);

        _http.Answer = req => req.RequestUri!.AbsolutePath.Contains("openid-configuration")
            ? (HttpStatusCode.OK, Metadata(Tenant)) : (HttpStatusCode.BadRequest, "{\"error_codes\":[700016]}");
        var app = await probe.TestAsync("power-pdf.de", ClientA, Secret, default);
        Assert.Equal(EntraProbe.Outcome.AppNotFound, app.Outcome);
        Assert.Equal(Tenant, app.TenantId);

        _http.Answer = req => req.RequestUri!.AbsolutePath.Contains("openid-configuration")
            ? (HttpStatusCode.OK, Metadata(Tenant)) : (HttpStatusCode.OK, "{\"access_token\":\"discarded\"}");
        Assert.Equal(EntraProbe.Outcome.Ok, (await probe.TestAsync(Tenant, ClientA, Secret, default)).Outcome);
        // only login.microsoftonline.com, the token request goes to the resolved directory id
        Assert.All(_http.Seen, u => Assert.Equal("login.microsoftonline.com", u.Host));
        Assert.Equal($"/{Tenant}/oauth2/v2.0/token", _http.Seen.Last().AbsolutePath);

        // no secret anywhere: a placeholder finds out whether the application exists (Microsoft answers
        // AADSTS7000216 to a request without any secret, before it looks for the application)
        string? form = null;
        _http.Answer = req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("openid-configuration")) return (HttpStatusCode.OK, Metadata(Tenant));
            form = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return (HttpStatusCode.Unauthorized, "{\"error_codes\":[7000215]}");
        };
        Assert.Equal(EntraProbe.Outcome.SecretMissing, (await probe.TestAsync(Tenant, ClientA, "", default)).Outcome);
        Assert.Contains("client_secret=" + EntraProbe.NoSecretPlaceholder, form);
        Assert.Equal(EntraProbe.Outcome.BadSecret, (await probe.TestAsync(Tenant, ClientA, "typed-but-wrong", default)).Outcome);

        _http.Answer = _ => throw new HttpRequestException("no network");
        Assert.Equal(EntraProbe.Outcome.Network, (await probe.TestAsync(Tenant, ClientA, Secret, default)).Outcome);
    }

    [Fact]
    public async Task A_domain_is_saved_with_its_directory_id()
    {
        Build();
        _http.Answer = _ => (HttpStatusCode.OK, Metadata(Tenant));
        Assert.True((await Save(tenant: "Power-PDF.de", dirs: Extra)).Ok);
        Assert.Equal("power-pdf.de", await Stored(EntraRuntime.TenantKey));
        Assert.Equal(Tenant, await Stored(EntraRuntime.TenantIdKey));
        var s = Runtime.Current;
        Assert.Equal(new[] { Tenant, Extra }.OrderBy(x => x), s.AllowedTenants.OrderBy(x => x));
        // more than one directory: signed in through "organizations", still only the listed issuers
        Assert.Equal("https://login.microsoftonline.com/organizations/v2.0", s.Authority);
        Assert.Equal(2, s.ValidIssuers.Count());

        _http.Answer = _ => (HttpStatusCode.BadRequest, "{\"error_codes\":[90002]}");
        var refused = await Save(tenant: "nowhere.example.com");
        Assert.False(refused.Ok);
        Assert.Equal("Directory not found: {0}", refused.Message);
        Assert.Equal(Tenant, await Stored(EntraRuntime.TenantIdKey));   // the previous configuration stays
    }

    // --- no restart -------------------------------------------------------------------------

    [Fact]
    public async Task Scheme_options_follow_the_settings_without_a_restart()
    {
        Build();
        await Runtime.ReloadAsync();
        var off = SchemeOptions;
        Assert.Equal("off", off.ClientId);
        Assert.Null(off.Authority);
        Assert.IsType<Microsoft.IdentityModel.Protocols.StaticConfigurationManager<Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration>>(off.ConfigurationManager);

        Assert.True((await Save(clientId: ClientA)).Ok);
        var a = SchemeOptions;
        Assert.Equal(ClientA, a.ClientId);
        Assert.Equal(Secret, a.ClientSecret);
        Assert.Equal($"https://login.microsoftonline.com/{Tenant}/v2.0", a.Authority);
        Assert.True(a.UsePkce);
        Assert.Equal("/signin-oidc", a.CallbackPath);
        Assert.Equal(new[] { $"https://login.microsoftonline.com/{Tenant}/v2.0" }, a.TokenValidationParameters.ValidIssuers);

        Assert.True((await Save(clientId: ClientB, secret: "")).Ok);
        var b = SchemeOptions;
        Assert.Equal(ClientB, b.ClientId);
        Assert.NotSame(a.ConfigurationManager, b.ConfigurationManager);   // new metadata manager too

        Assert.True((await Save(clientId: ClientB, secret: "", enabled: false)).Ok);
        Assert.Equal("off", SchemeOptions.ClientId);
        Assert.False(Runtime.Current.Enabled);

        // the audit log has the switch, never the secret
        var audit = await Scoped(sp => sp.GetRequiredService<AppDbContext>().AuditEntries.Select(e => e.Action + " " + e.Details).ToListAsync());
        Assert.Contains(audit, e => e.StartsWith("entra.enabled"));
        Assert.Contains(audit, e => e.StartsWith("entra.disabled"));
        Assert.Contains(audit, e => e.StartsWith("entra.saved"));
        Assert.DoesNotContain(audit, e => e.Contains(Secret));
    }

    [Fact]
    public async Task Unchanged_settings_keep_the_cached_options()
    {
        Build();
        Assert.True((await Save()).Ok);
        var first = SchemeOptions;
        await Runtime.ReloadAsync();
        Assert.Same(first, SchemeOptions);
    }

    // --- the secret ---------------------------------------------------------------------------

    [Fact]
    public async Task Empty_secret_field_keeps_the_stored_secret_and_it_is_stored_encrypted()
    {
        Build();
        Assert.True((await Save()).Ok);
        var stored = await Stored(EntraRuntime.SecretKey);
        Assert.StartsWith("dp:", stored);
        Assert.DoesNotContain(Secret, stored);

        Assert.True((await Save(secret: "", dirs: Extra)).Ok);
        Assert.Equal(stored, await Stored(EntraRuntime.SecretKey));
        Assert.Equal(Secret, Runtime.Current.ClientSecret);
        Assert.True(Runtime.Current.Enabled);

        Assert.True((await Save(secret: "a-new-secret-value")).Ok);
        Assert.Equal("a-new-secret-value", Runtime.Current.ClientSecret);
    }

    [Fact]
    public async Task Secret_is_never_handed_to_the_page()
    {
        Build();
        Assert.True((await Save()).Ok);
        var section = await Scoped(sp => sp.GetRequiredService<EntraSetupService>().LoadAsync("https://store.example/", false));
        Assert.True(section.HasSecret);
        Assert.Equal("https://store.example/signin-oidc", section.RedirectUri);
        Assert.Null(section.OtherRedirectUri);
        var elsewhere = await Scoped(sp => sp.GetRequiredService<EntraSetupService>().LoadAsync("https://store.example", false, "https://other.example/signin-oidc"));
        Assert.Equal("https://other.example/signin-oidc", elsewhere.OtherRedirectUri);
        var same = await Scoped(sp => sp.GetRequiredService<EntraSetupService>().LoadAsync("https://store.example", false, "https://STORE.example/signin-oidc"));
        Assert.Null(same.OtherRedirectUri);
        foreach (var p in section.GetType().GetProperties())
            Assert.False(p.GetValue(section) is string v && v.Contains(Secret), p.Name);
    }

    [Fact]
    public async Task Removing_the_secret_switches_the_sign_in_off()
    {
        Build();
        Assert.True((await Save()).Ok);
        Assert.True((await Scoped(sp => sp.GetRequiredService<EntraSetupService>().RemoveSecretAsync("Admin"))).Ok);
        Assert.Equal("", await Stored(EntraRuntime.SecretKey));
        Assert.False(Runtime.Current.Enabled);
        Assert.Equal(EntraSettings.ProblemSecret, Runtime.Current.Problem);
        Assert.Equal("off", SchemeOptions.ClientId);
        var audit = await Scoped(sp => sp.GetRequiredService<AppDbContext>().AuditEntries.Select(e => e.Action).ToListAsync());
        Assert.Contains("entra.secret-removed", audit);
    }

    [Fact]
    public async Task Test_uses_the_stored_secret_when_the_field_is_empty()
    {
        Build();
        Assert.True((await Save()).Ok);
        string? sent = null;
        _http.Answer = req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("openid-configuration")) return (HttpStatusCode.OK, Metadata(Tenant));
            sent = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return (HttpStatusCode.OK, "{}");
        };
        var r = await Scoped(sp => sp.GetRequiredService<EntraSetupService>().TestAsync(new(ClientA, Tenant, "", null, true), default));
        Assert.True(r.Ok);
        Assert.Contains("client_secret=" + Uri.EscapeDataString(Secret).Replace("%20", "+"), sent);
        Assert.Contains("scope=https%3A%2F%2Fgraph.microsoft.com%2F.default", sent);
    }

    private static string Metadata(string tenant) =>
        $"{{\"issuer\":\"https://login.microsoftonline.com/{tenant}/v2.0\",\"token_endpoint\":\"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token\"}}";

    /// <summary>Answers instead of login.microsoftonline.com and records the addresses asked.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, (HttpStatusCode, string)> Answer { get; set; } = _ => (HttpStatusCode.OK, "{}");
        public List<Uri> Seen { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request.RequestUri!);
            var (status, body) = Answer(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
