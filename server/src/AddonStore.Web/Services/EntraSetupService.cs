using System.Text.Json;
using System.Text.RegularExpressions;
using AddonStore.Web.Auth;

namespace AddonStore.Web.Services;

/// <summary>
/// Checks entered "Sign in with Microsoft" values against Microsoft without saving anything. Only
/// https://login.microsoftonline.com is ever called, with a 10 second timeout and no redirects:
/// <list type="number">
/// <item>the OpenID metadata of the directory (does the directory exist; which directory id it has);</item>
/// <item>a client-credentials token request with scope https://graph.microsoft.com/.default (does the application
/// exist in this directory, is the secret right). It needs no API permission; the token is discarded at once and
/// never logged or stored.</item>
/// </list>
/// </summary>
public sealed class EntraProbe
{
    public const string HttpName = "entra-setup";
    private const int MaxAnswer = 64 * 1024;
    /// <summary>Sent as client_secret when no secret is entered or stored (see <see cref="TestAsync"/>).</summary>
    internal const string NoSecretPlaceholder = "addon-store-setup-test-no-secret";

    public enum Outcome
    {
        Ok,
        /// <summary>AADSTS90002 / 400 on the metadata: no such directory.</summary>
        DirectoryNotFound,
        /// <summary>AADSTS700016: no application with this client id in the directory.</summary>
        AppNotFound,
        /// <summary>AADSTS7000215 (wrong) / AADSTS7000222 (expired).</summary>
        BadSecret,
        /// <summary>Directory and application found; no secret was entered or stored (placeholder refused, AADSTS7000216/7000218).</summary>
        SecretMissing,
        /// <summary>Microsoft not reachable (DNS, TLS, timeout).</summary>
        Network,
        /// <summary>Any other answer; <see cref="Result.Code"/> names it.</summary>
        Unexpected,
    }

    public sealed record Result(Outcome Outcome, string? TenantId = null, string? Code = null);

    private static readonly Regex IssuerTenant = new(@"^https://login\.microsoftonline\.com/([0-9a-fA-F-]{36})/v2\.0$", RegexOptions.CultureInvariant);

    private readonly IHttpClientFactory _http;

    public EntraProbe(IHttpClientFactory http) => _http = http;

    /// <summary>The directory id of a directory id or verified domain (metadata request).</summary>
    public async Task<Result> ResolveAsync(string directory, CancellationToken ct)
    {
        directory = directory.Trim().ToLowerInvariant();
        if (!EntraSettings.IsDirectory(directory)) return new(Outcome.DirectoryNotFound);
        try
        {
            using var res = await Client().GetAsync(
                $"{EntraSettings.MicrosoftInstance}{Uri.EscapeDataString(directory)}/v2.0/.well-known/openid-configuration", ct);
            var body = await ReadAsync(res, ct);
            if (!res.IsSuccessStatusCode)
            {
                var mapped = Map((int)res.StatusCode, body);
                // a 400 on the metadata means the directory is unknown, whatever the body says
                return mapped.Outcome == Outcome.Unexpected && (int)res.StatusCode is 400 or 404 ? new(Outcome.DirectoryNotFound, Code: mapped.Code) : mapped;
            }
            string? issuer = null;
            try { using var doc = JsonDocument.Parse(body); issuer = doc.RootElement.TryGetProperty("issuer", out var i) ? i.GetString() : null; }
            catch (JsonException) { }
            var m = issuer is null ? null : IssuerTenant.Match(issuer);
            return m is { Success: true } && Guid.TryParseExact(m.Groups[1].Value, "D", out var g)
                ? new(Outcome.Ok, g.ToString("D")) : new(Outcome.Unexpected, Code: "metadata");
        }
        catch (Exception ex) when (IsNetwork(ex, ct)) { return new(Outcome.Network); }
    }

    /// <summary>Directory, then application and secret. An empty secret only checks that the application exists.</summary>
    public async Task<Result> TestAsync(string directory, string clientId, string? secret, CancellationToken ct)
    {
        var dir = await ResolveAsync(directory, ct);
        if (dir.Outcome != Outcome.Ok) return dir;
        // Without a secret Microsoft refuses the request before it looks for the application (AADSTS7000216), so a
        // placeholder is sent: an unknown application still answers AADSTS700016, a known one "wrong secret"
        var noSecret = string.IsNullOrEmpty(secret);
        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId.Trim(),
            ["client_secret"] = noSecret ? NoSecretPlaceholder : secret!,
            ["scope"] = "https://graph.microsoft.com/.default",
            ["grant_type"] = "client_credentials",
        };
        try
        {
            using var content = new FormUrlEncodedContent(form);
            using var res = await Client().PostAsync($"{EntraSettings.MicrosoftInstance}{dir.TenantId}/oauth2/v2.0/token", content, ct);
            // the answer of a success holds an access token: it is not even read
            if (res.IsSuccessStatusCode) return new(Outcome.Ok, dir.TenantId);
            var mapped = Map((int)res.StatusCode, await ReadAsync(res, ct));
            if (noSecret && mapped.Outcome == Outcome.BadSecret) mapped = mapped with { Outcome = Outcome.SecretMissing };
            return mapped with { TenantId = dir.TenantId };
        }
        catch (Exception ex) when (IsNetwork(ex, ct)) { return new(Outcome.Network, dir.TenantId); }
    }

    /// <summary>Maps an error answer of login.microsoftonline.com (JSON with error_codes) to an outcome.</summary>
    public static Result Map(int status, string? body)
    {
        var codes = new List<long>();
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error_codes", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var c in arr.EnumerateArray())
                    if (c.ValueKind == JsonValueKind.Number && c.TryGetInt64(out var n)) codes.Add(n);
            if (codes.Count == 0 && doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("error_description", out var d) && d.ValueKind == JsonValueKind.String &&
                Regex.Match(d.GetString() ?? "", @"^AADSTS(\d{1,9})\b", RegexOptions.CultureInvariant) is { Success: true } m)
                codes.Add(long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (JsonException) { }
        foreach (var c in codes)
        {
            switch (c)
            {
                case 90002: case 900023: case 90023: return new(Outcome.DirectoryNotFound, Code: "AADSTS" + c);
                case 700016: return new(Outcome.AppNotFound, Code: "AADSTS" + c);
                case 7000215: case 7000222: return new(Outcome.BadSecret, Code: "AADSTS" + c);
                case 7000216: case 7000218: return new(Outcome.SecretMissing, Code: "AADSTS" + c);
            }
        }
        return new(Outcome.Unexpected, Code: codes.Count > 0 ? "AADSTS" + codes[0] : "HTTP " + status);
    }

    /// <summary>The resource key of the message for an outcome (Resources/SharedResource.*.resx).</summary>
    public static string Message(Outcome o) => o switch
    {
        Outcome.Ok => "Test passed: Microsoft accepted the directory, the application and the client secret.",
        Outcome.DirectoryNotFound => "Directory not found. Check the directory (tenant) ID or the domain.",
        Outcome.AppNotFound => "Application not found in this directory. Check the application (client) ID and that the app is registered in this directory.",
        Outcome.BadSecret => "The client secret is wrong or has expired. Create a new client secret and copy its value (not the secret ID).",
        Outcome.SecretMissing => "Directory and application found. Enter the client secret to test it as well.",
        Outcome.Network => "Microsoft could not be reached. Check the internet connection of the server and try again.",
        _ => "Microsoft answered with an unexpected error.",
    };

    private HttpClient Client() => _http.CreateClient(HttpName);

    private static bool IsNetwork(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested);

    private static async Task<string> ReadAsync(HttpResponseMessage res, CancellationToken ct)
    {
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxAnswer];
        var total = 0;
        int n;
        while (total < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(total), ct)) > 0) total += n;
        return System.Text.Encoding.UTF8.GetString(buffer, 0, total);
    }
}

/// <summary>
/// Admin page Settings, section "Sign in with Microsoft": what the section shows, saving, removing the secret
/// and the test. The secret is stored encrypted (<see cref="EntraRuntime.ProtectSecret"/>) and never leaves this
/// class towards the page; an empty secret field keeps the stored one.
/// </summary>
public sealed class EntraSetupService
{
    public const int MaxSecret = 500;
    public const int MaxDirectories = 20;

    /// <summary>Everything the section renders. Deliberately without the secret (only whether one is stored).</summary>
    public sealed record Section(
        string RedirectUri, bool RedirectFromRequest, bool FromAppSettings, bool Enabled, bool Active, string? Problem,
        string ClientId, string Tenant, string Directories, bool HasSecret, string? OtherRedirectUri = null);

    public sealed record Input(string? ClientId, string? Tenant, string? Secret, string? Directories, bool Enabled);

    /// <summary>Result of a save or test: a resource key with its arguments.</summary>
    public sealed record Outcome(bool Ok, string Message, params object[] Args);

    private readonly SettingsService _settings;
    private readonly EntraRuntime _runtime;
    private readonly EntraProbe _probe;
    private readonly AuditService _audit;
    private readonly IConfiguration _config;

    public EntraSetupService(SettingsService settings, EntraRuntime runtime, EntraProbe probe, AuditService audit, IConfiguration config)
    {
        _settings = settings; _runtime = runtime; _probe = probe; _audit = audit; _config = config;
    }

    /// <param name="requestRedirect">The redirect URI the sign-in sends when started from the address of this request;
    /// shown as an extra hint when it differs from the one of the public base URL.</param>
    public async Task<Section> LoadAsync(string baseUrl, bool baseFromRequest, string? requestRedirect = null)
    {
        var effective = await _runtime.GetAsync();
        var redirect = baseUrl.TrimEnd('/') + EntraSettings.CallbackPath;
        var other = requestRedirect is not null && !string.Equals(requestRedirect, redirect, StringComparison.OrdinalIgnoreCase) ? requestRedirect : null;
        if (_runtime.FromAppSettings)
        {
            var s = _config.GetSection("Entra");
            string V(string k) => (s[k] ?? "").Trim();
            return new Section(redirect, baseFromRequest, true, effective.Enabled, effective.Enabled, effective.Problem,
                V("ClientId"), V("TenantId").Length > 0 ? V("TenantId") : V("Authority"), V("AllowedTenants"), V("ClientSecret").Length > 0, other);
        }
        return new Section(redirect, baseFromRequest, false,
            await _settings.GetAsync(EntraRuntime.EnabledKey) == "1", effective.Enabled, effective.Problem,
            await _settings.GetAsync(EntraRuntime.ClientIdKey), await _settings.GetAsync(EntraRuntime.TenantKey),
            await _settings.GetAsync(EntraRuntime.DirectoriesKey),
            _runtime.UnprotectSecret(await _settings.GetAsync(EntraRuntime.SecretKey)).Length > 0, other);
    }

    /// <summary>Checks the entered values; null when they may be saved or tested, otherwise the resource key of the problem.</summary>
    public static string? Validate(Input input, bool forSave)
    {
        var clientId = input.ClientId?.Trim() ?? "";
        var tenant = input.Tenant?.Trim() ?? "";
        var secret = input.Secret?.Trim() ?? "";
        if (clientId.Length > 0 && !EntraSettings.IsClientId(clientId))
            return "The application (client) ID must be a GUID such as 00000000-0000-0000-0000-000000000000.";
        if (tenant.Length > 0 && !EntraSettings.IsDirectory(tenant))
            return "The directory must be a directory (tenant) ID or a verified domain such as contoso.com.";
        if (secret.Length > MaxSecret || secret.Any(char.IsControl))
            return "The client secret is too long or contains invalid characters.";
        if ((input.Directories?.Length ?? 0) > 2000 || EntraSettings.SplitDirectories(input.Directories, MaxDirectories) is null)
            return "Additional allowed directories: enter directory IDs or domains, separated by commas (at most 20).";
        if (!forSave && (clientId.Length == 0 || tenant.Length == 0))
            return "Enter the application (client) ID and the directory first.";
        return null;
    }

    public async Task<Outcome> SaveAsync(Input input, string actor, CancellationToken ct)
    {
        if (_runtime.FromAppSettings) return new(false, "The app settings Entra:* decide; change the values there.");
        if (Validate(input, forSave: true) is { } problem) return new(false, problem);
        var clientId = (input.ClientId ?? "").Trim().ToLowerInvariant();
        var tenant = (input.Tenant ?? "").Trim().ToLowerInvariant();
        var secret = (input.Secret ?? "").Trim();
        var directories = EntraSettings.SplitDirectories(input.Directories, MaxDirectories)!;
        var hadSecret = _runtime.UnprotectSecret(await _settings.GetAsync(EntraRuntime.SecretKey)).Length > 0;
        if (input.Enabled && (clientId.Length == 0 || tenant.Length == 0 || (secret.Length == 0 && !hadSecret)))
            return new(false, "To switch it on, enter the application (client) ID, the directory and the client secret.");

        // domains become directory ids now (issuer check of the tokens); an id needs no request
        var tenantId = "";
        if (tenant.Length > 0)
        {
            var r = await ResolveAsync(tenant, ct);
            if (r.Outcome == EntraProbe.Outcome.Ok) tenantId = r.TenantId!;
            else if (input.Enabled) return Unresolved(r, tenant);
        }
        var directoryIds = new List<string>();
        foreach (var d in directories)
        {
            var r = await ResolveAsync(d, ct);
            if (r.Outcome == EntraProbe.Outcome.Ok) { if (!directoryIds.Contains(r.TenantId!)) directoryIds.Add(r.TenantId!); }
            else if (input.Enabled) return Unresolved(r, d);
        }

        var wasEnabled = await _settings.GetAsync(EntraRuntime.EnabledKey) == "1";
        await _settings.SetAsync(EntraRuntime.ClientIdKey, clientId);
        await _settings.SetAsync(EntraRuntime.TenantKey, tenant);
        await _settings.SetAsync(EntraRuntime.TenantIdKey, tenantId);
        await _settings.SetAsync(EntraRuntime.DirectoriesKey, string.Join(", ", directories));
        await _settings.SetAsync(EntraRuntime.DirectoryIdsKey, string.Join(",", directoryIds));
        if (secret.Length > 0) await _settings.SetAsync(EntraRuntime.SecretKey, _runtime.ProtectSecret(secret));
        await _settings.SetAsync(EntraRuntime.EnabledKey, input.Enabled ? "1" : "0");
        var now = await _runtime.ReloadAsync();

        // never the secret, only whether it was replaced
        await _audit.LogAsync(actor, "entra.saved", "Sign in with Microsoft",
            $"application {(clientId.Length > 0 ? clientId : "-")}; directory {(tenant.Length > 0 ? tenant : "-")}" +
            (tenantId.Length > 0 && tenantId != tenant ? $" ({tenantId})" : "") +
            $"; additional directories {(directories.Count > 0 ? string.Join(",", directories) : "none")}; secret {(secret.Length > 0 ? "replaced" : hadSecret ? "kept" : "none")}");
        if (input.Enabled != wasEnabled)
            await _audit.LogAsync(actor, input.Enabled ? "entra.enabled" : "entra.disabled", "Sign in with Microsoft",
                input.Enabled ? (now.Enabled ? "button shown on the sign-in page" : "switched on but incomplete: " + now.Problem) : "button removed from the sign-in page");
        return new(true, input.Enabled ? (now.Enabled ? "Saved. The sign-in page now shows \"Sign in with Microsoft\"." : "Saved, but incomplete: the button stays hidden.")
                                       : "Saved. Sign in with Microsoft is off.");
    }

    public async Task<Outcome> RemoveSecretAsync(string actor)
    {
        if (_runtime.FromAppSettings) return new(false, "The app settings Entra:* decide; change the values there.");
        await _settings.SetAsync(EntraRuntime.SecretKey, "");
        var now = await _runtime.ReloadAsync();
        await _audit.LogAsync(actor, "entra.secret-removed", "Sign in with Microsoft", now.Enabled ? "" : "sign-in with Microsoft inactive until a new secret is saved");
        return new(true, "Client secret removed.");
    }

    /// <summary>Tests the entered values (with app settings: the effective ones); an empty secret field uses the stored secret.</summary>
    public async Task<Outcome> TestAsync(Input input, CancellationToken ct)
    {
        string clientId, tenant, secret;
        if (_runtime.FromAppSettings)
        {
            var s = _config.GetSection("Entra");
            clientId = (s["ClientId"] ?? "").Trim();
            tenant = (s["TenantId"] ?? "").Trim();
            secret = (s["ClientSecret"] ?? "").Trim();
            if (!EntraSettings.IsClientId(clientId) || !EntraSettings.IsDirectory(tenant))
                return new(false, "The test needs Entra:ClientId and Entra:TenantId (directory ID or domain) in the app settings.");
        }
        else
        {
            if (Validate(input, forSave: false) is { } problem) return new(false, problem);
            clientId = input.ClientId!.Trim();
            tenant = input.Tenant!.Trim();
            secret = (input.Secret ?? "").Trim();
            if (secret.Length == 0) secret = _runtime.UnprotectSecret(await _settings.GetAsync(EntraRuntime.SecretKey));
        }
        var r = await _probe.TestAsync(tenant, clientId, secret, ct);
        return r.Outcome == EntraProbe.Outcome.Unexpected
            ? new(false, "Microsoft answered with an unexpected error: {0}", r.Code ?? "?")
            : new(r.Outcome == EntraProbe.Outcome.Ok, EntraProbe.Message(r.Outcome));
    }

    /// <summary>A directory that could not be turned into a directory id: nothing is saved while switched on.</summary>
    private static Outcome Unresolved(EntraProbe.Result r, string directory) =>
        r.Outcome == EntraProbe.Outcome.DirectoryNotFound ? new(false, "Directory not found: {0}", directory) : new(false, EntraProbe.Message(r.Outcome));

    private Task<EntraProbe.Result> ResolveAsync(string directory, CancellationToken ct) =>
        Guid.TryParseExact(directory, "D", out var g)
            ? Task.FromResult(new EntraProbe.Result(EntraProbe.Outcome.Ok, g.ToString("D")))
            : _probe.ResolveAsync(directory, ct);
}
