using System.Security.Claims;
using AddonStore.Web.Auth;
using AddonStore.Web.Data;
using AddonStore.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AddonStore.Web.Tests;

/// <summary>"Sign in with Microsoft": trust rules of the token and the account matching (never a second account per address).</summary>
public class EntraSignInTests : IDisposable
{
    private const string Tenant = "11111111-2222-3333-4444-555555555555";
    private const string OtherTenant = "99999999-8888-7777-6666-555555555555";
    private const string Msa = "9188040d-6c67-4c5b-b112-36a304b66dad";   // personal Microsoft accounts
    private static readonly IReadOnlySet<string> Allowed = new HashSet<string> { Tenant };

    private readonly SqliteConnection _conn;
    private readonly ServiceProvider _sp;

    public EntraSignInTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(_conn));
        // the same Identity rules as Program.cs
        services.AddIdentityCore<AppUser>(IdentitySetup.Apply).AddRoles<IdentityRole>().AddEntityFrameworkStores<AppDbContext>();
        services.AddScoped<EntraAccountService>();
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var r in SchemaUpgrade.Roles) roles.CreateAsync(new IdentityRole(r)).GetAwaiter().GetResult();
    }

    public void Dispose() { _sp.Dispose(); _conn.Dispose(); }

    private static List<Claim> Token(string tid = Tenant, string oid = "aaaaaaaa-0000-0000-0000-000000000001",
                                     string? upn = null, string? preferred = "alice@power-pdf.de", string? email = null,
                                     string? edov = null, string? idp = null, string? name = "Alice Example")
    {
        var c = new List<Claim> { new("tid", tid), new("oid", oid), new("iss", $"https://login.microsoftonline.com/{tid}/v2.0") };
        if (upn is not null) c.Add(new("upn", upn));
        if (preferred is not null) c.Add(new("preferred_username", preferred));
        if (email is not null) c.Add(new("email", email));
        if (edov is not null) c.Add(new("xms_edov", edov));
        if (idp is not null) c.Add(new("idp", idp));
        if (name is not null) c.Add(new("name", name));
        return c;
    }

    private static EntraIdentity Id(string email = "alice@power-pdf.de", string oid = "aaaaaaaa-0000-0000-0000-000000000001", string name = "Alice Example") =>
        EntraIdentity.Read(Token(oid: oid, preferred: email, name: name), Allowed).Identity!;

    private async Task<T> Scoped<T>(Func<IServiceProvider, Task<T>> f)
    {
        using var scope = _sp.CreateScope();
        return await f(scope.ServiceProvider);
    }

    private Task<EntraAccountService.Outcome> Resolve(EntraIdentity id) =>
        Scoped(sp => sp.GetRequiredService<EntraAccountService>().ResolveAsync(id));

    private Task<AppUser> Manual(string email, string name, UserStatus status = UserStatus.Active) => Scoped(async sp =>
    {
        var users = sp.GetRequiredService<UserManager<AppUser>>();
        var u = new AppUser { UserName = email, Email = email, DisplayName = name, Status = status };
        Assert.True((await users.CreateAsync(u, "A-long-password-1")).Succeeded);
        return u;
    });

    private Task<int> UserCount() => Scoped(sp => sp.GetRequiredService<AppDbContext>().Users.CountAsync());

    // --- trust rules -----------------------------------------------------------

    [Fact]
    public void Tenant_not_allowed_is_refused()
    {
        Assert.Equal(EntraRejection.Tenant, EntraIdentity.Read(Token(tid: OtherTenant), Allowed).Rejection);
        Assert.Equal(EntraRejection.Tenant, EntraIdentity.Read(Token(tid: Msa), Allowed).Rejection);
    }

    [Fact]
    public void Issuer_of_another_tenant_is_refused()
    {
        var claims = Token();
        claims.RemoveAll(c => c.Type == "iss");
        claims.Add(new("iss", $"https://login.microsoftonline.com/{OtherTenant}/v2.0"));
        Assert.Equal(EntraRejection.Tenant, EntraIdentity.Read(claims, Allowed).Rejection);
    }

    [Fact]
    public void Missing_tid_or_oid_fails()
    {
        var claims = Token();
        claims.RemoveAll(c => c.Type == "oid");
        Assert.Equal(EntraRejection.Failed, EntraIdentity.Read(claims, Allowed).Rejection);
    }

    [Fact]
    public void Email_claim_that_differs_from_upn_is_ignored_without_xms_edov()
    {
        var (id, rejection) = EntraIdentity.Read(Token(upn: "alice@power-pdf.de", preferred: null, email: "boss@example.com"), Allowed);
        Assert.Equal(EntraRejection.None, rejection);
        Assert.Equal("alice@power-pdf.de", id!.Email);
    }

    [Fact]
    public void Email_claim_is_used_when_its_domain_is_verified()
    {
        var (id, _) = EntraIdentity.Read(Token(upn: "alice@contoso.onmicrosoft.com", preferred: null, email: "alice@power-pdf.de", edov: "true"), Allowed);
        Assert.Equal("alice@power-pdf.de", id!.Email);
        var (same, _) = EntraIdentity.Read(Token(preferred: "Alice@Power-PDF.de", email: "alice@power-pdf.de"), Allowed);
        Assert.Equal("Alice@Power-PDF.de", same!.Email);
    }

    [Fact]
    public void Upn_wins_over_preferred_username()
    {
        var (id, _) = EntraIdentity.Read(Token(upn: "alice@power-pdf.de", preferred: "other@power-pdf.de"), Allowed);
        Assert.Equal("alice@power-pdf.de", id!.Email);
    }

    [Fact]
    public void Guests_and_accounts_without_a_verified_address_are_refused()
    {
        // B2B guest or a personal Microsoft account added to the tenant: idp names another tenant / live.com
        Assert.Equal(EntraRejection.Email, EntraIdentity.Read(Token(idp: $"https://sts.windows.net/{OtherTenant}/"), Allowed).Rejection);
        Assert.Equal(EntraRejection.Email, EntraIdentity.Read(Token(idp: "live.com"), Allowed).Rejection);
        Assert.Equal(EntraRejection.Email, EntraIdentity.Read(Token(preferred: "alice_example.com#EXT#@contoso.onmicrosoft.com"), Allowed).Rejection);
        Assert.Equal(EntraRejection.Email, EntraIdentity.Read(Token(preferred: null, email: "alice@power-pdf.de"), Allowed).Rejection);
        Assert.Equal(EntraRejection.Email, EntraIdentity.Read(Token(preferred: "not an address"), Allowed).Rejection);
        // idp of the own tenant is a member
        Assert.Equal(EntraRejection.None, EntraIdentity.Read(Token(idp: $"https://sts.windows.net/{Tenant}/"), Allowed).Rejection);
    }

    [Fact]
    public void Key_is_tenant_and_object_id_and_survives_the_cookie()
    {
        var id = Id();
        Assert.Equal($"{Tenant}:aaaaaaaa-0000-0000-0000-000000000001", id.Key);
        Assert.Equal(id, EntraIdentity.FromPrincipal(id.ToPrincipal()));
        Assert.Null(EntraIdentity.FromPrincipal(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    // --- settings ----------------------------------------------------------------

    private static EntraSettings Settings(params (string Key, string Value)[] values) =>
        EntraSettings.From(new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>("Entra:" + v.Key, v.Value))).Build());

    [Fact]
    public void Feature_is_off_without_configuration_and_with_an_incomplete_one()
    {
        var off = Settings();
        Assert.False(off.Enabled);
        Assert.Null(off.Problem);
        var noSecret = Settings(("ClientId", "c"), ("TenantId", Tenant));
        Assert.False(noSecret.Enabled);
        Assert.NotNull(noSecret.Problem);
        var noTenants = Settings(("ClientId", "c"), ("ClientSecret", "s"), ("TenantId", "organizations"));
        Assert.False(noTenants.Enabled);
        var badTenant = Settings(("ClientId", "c"), ("ClientSecret", "s"), ("TenantId", Tenant), ("AllowedTenants", "contoso.com"));
        Assert.False(badTenant.Enabled);
    }

    [Fact]
    public void Single_and_multi_tenant_settings()
    {
        var single = Settings(("ClientId", "c"), ("ClientSecret", "s"), ("TenantId", Tenant.ToUpperInvariant()));
        Assert.True(single.Enabled);
        Assert.Equal($"https://login.microsoftonline.com/{Tenant.ToUpperInvariant()}/v2.0", single.Authority);
        Assert.Equal(new[] { $"https://login.microsoftonline.com/{Tenant}/v2.0" }, single.ValidIssuers);

        var multi = Settings(("ClientId", "c"), ("ClientSecret", "s"), ("TenantId", "organizations"), ("AllowedTenants", $"{Tenant}, {OtherTenant}"));
        Assert.True(multi.Enabled);
        Assert.Equal(2, multi.AllowedTenants.Count);
        Assert.Equal("https://login.microsoftonline.com/", multi.Instance);
    }

    // --- account matching ------------------------------------------------------------

    [Fact]
    public async Task New_person_gets_a_pending_account_like_a_registration()
    {
        var outcome = await Resolve(Id());
        Assert.Equal(EntraAccountService.Kind.Created, outcome.Kind);
        await Scoped(async sp =>
        {
            var users = sp.GetRequiredService<UserManager<AppUser>>();
            var u = await users.FindByLoginAsync("Entra", $"{Tenant}:aaaaaaaa-0000-0000-0000-000000000001");
            Assert.NotNull(u);
            Assert.Equal(UserStatus.Pending, u!.Status);
            Assert.Equal("alice@power-pdf.de", u.Email);
            Assert.Equal("Alice Example", u.DisplayName);
            Assert.False(await users.HasPasswordAsync(u));
            Assert.Equal(new[] { SchemaUpgrade.DefaultRole }, await users.GetRolesAsync(u));
            return 0;
        });
        Assert.Equal("pending", EntraAccountService.Gate(outcome.User!, lockedOut: false));
    }

    [Fact]
    public async Task Existing_link_signs_in_the_same_account()
    {
        var first = await Resolve(Id());
        var second = await Resolve(Id(email: "renamed@power-pdf.de"));   // the address may change, the key does not
        Assert.Equal(EntraAccountService.Kind.Existing, second.Kind);
        Assert.Equal(first.User!.Id, second.User!.Id);
        Assert.Equal(1, await UserCount());
    }

    [Fact]
    public async Task Same_address_in_other_case_links_the_manual_account()
    {
        var manual = await Manual("alice@power-pdf.de", "Alice");
        var outcome = await Resolve(Id(email: "ALICE@Power-PDF.de"));
        Assert.Equal(EntraAccountService.Kind.Linked, outcome.Kind);
        Assert.Equal(manual.Id, outcome.User!.Id);
        Assert.Equal(1, await UserCount());
        Assert.Null(EntraAccountService.Gate(outcome.User, lockedOut: false));
        // next time through the link
        Assert.Equal(EntraAccountService.Kind.Existing, (await Resolve(Id(email: "ALICE@Power-PDF.de"))).Kind);
    }

    [Fact]
    public async Task Second_Microsoft_account_with_the_same_address_links_too()
    {
        await Resolve(Id());
        var other = await Resolve(Id(oid: "aaaaaaaa-0000-0000-0000-000000000002"));
        Assert.Equal(EntraAccountService.Kind.Linked, other.Kind);
        Assert.Equal(1, await UserCount());
    }

    [Theory]
    [InlineData(UserStatus.Pending, "pending")]
    [InlineData(UserStatus.Disabled, "inactive")]
    [InlineData(UserStatus.Rejected, "inactive")]
    public async Task Pending_and_disabled_accounts_are_not_signed_in(UserStatus status, string code)
    {
        await Manual("bob@power-pdf.de", "Bob", status);
        var outcome = await Resolve(Id(email: "bob@power-pdf.de", name: "Bob"));
        Assert.Equal(EntraAccountService.Kind.Linked, outcome.Kind);
        Assert.Equal(status, outcome.User!.Status);   // linking changes nothing about the account
        Assert.Equal(code, EntraAccountService.Gate(outcome.User, lockedOut: false));
    }

    [Fact]
    public void Locked_out_account_is_not_signed_in()
    {
        Assert.Equal("failed", EntraAccountService.Gate(new AppUser { Status = UserStatus.Active }, lockedOut: true));
    }

    [Fact]
    public async Task Taken_display_name_gets_a_number()
    {
        await Manual("someone@example.com", "Alice Example");
        var outcome = await Resolve(Id());
        Assert.Equal(EntraAccountService.Kind.Created, outcome.Kind);
        Assert.Equal("Alice Example (2)", outcome.User!.DisplayName);
    }

    [Fact]
    public async Task Later_manual_registration_with_the_address_creates_no_second_account()
    {
        await Resolve(Id());
        var result = await Scoped(sp => sp.GetRequiredService<UserManager<AppUser>>().CreateAsync(
            new AppUser { UserName = "Alice@POWER-pdf.de", Email = "Alice@POWER-pdf.de", DisplayName = "Alice 2", Status = UserStatus.Pending },
            "A-long-password-1"));
        Assert.False(result.Succeeded);
        // Register.cshtml.cs answers these codes like a new request and stores nothing
        Assert.Contains(result.Errors, e => e.Code is "DuplicateUserName" or "DuplicateEmail");
        Assert.Equal(1, await UserCount());
    }
}
