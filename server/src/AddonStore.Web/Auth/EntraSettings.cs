using System.Text.RegularExpressions;

namespace AddonStore.Web.Auth;

/// <summary>Where the effective "Sign in with Microsoft" configuration comes from.</summary>
public enum EntraSource
{
    /// <summary>Nothing configured anywhere.</summary>
    None,
    /// <summary>App settings / environment (Entra:*): they win over the database and are read once at start-up.</summary>
    AppSettings,
    /// <summary>Admin page Settings, section "Sign in with Microsoft" (AppSettings table, secret encrypted).</summary>
    Store,
}

/// <summary>
/// "Sign in with Microsoft" (Microsoft Entra ID, OpenID Connect). Two sources, the first one set wins
/// (<see cref="EntraRuntime"/>):
/// <list type="number">
/// <item>the app settings / environment Entra:ClientId, Entra:ClientSecret, Entra:TenantId or Entra:Authority,
/// Entra:AllowedTenants (<see cref="From"/>), read once at start-up;</item>
/// <item>the admin page Settings, section "Sign in with Microsoft" (<see cref="FromStore"/>), changed at run time
/// without a restart.</item>
/// </list>
/// Without a complete configuration the feature is off: no button, the challenge and callback answer 404.
/// Setup: docs/entra-setup.md.
/// </summary>
public sealed class EntraSettings
{
    /// <summary>Authentication scheme and LoginProvider in AspNetUserLogins (key "{tid}:{oid}").</summary>
    public const string Scheme = "Entra";
    public const string CallbackPath = "/signin-oidc";
    public const string ChallengePath = "/Account/Microsoft";
    public const string CompletePath = "/Account/Microsoft/Callback";
    public const string MicrosoftInstance = "https://login.microsoftonline.com/";

    public bool Enabled { get; private init; }
    public EntraSource Source { get; private init; }
    public string ClientId { get; private init; } = "";
    public string ClientSecret { get; private init; } = "";
    /// <summary>OpenID Connect authority, e.g. https://login.microsoftonline.com/{tenant}/v2.0.</summary>
    public string Authority { get; private init; } = "";
    /// <summary>Scheme and host of the authority with a trailing slash, e.g. https://login.microsoftonline.com/.</summary>
    public string Instance { get; private init; } = "";
    /// <summary>Tenant ids (lower-case GUIDs) whose accounts may sign in.</summary>
    public IReadOnlySet<string> AllowedTenants { get; private init; } = new HashSet<string>();
    /// <summary>
    /// Why the feature stays off although some Entra value is set; null when nothing is set or it is switched off
    /// on purpose. For the store source the text is a resource key (shown on the settings page).
    /// </summary>
    public string? Problem { get; private init; }

    /// <summary>Issuer of the v2.0 ID tokens of each allowed tenant.</summary>
    public IEnumerable<string> ValidIssuers => AllowedTenants.Select(t => $"{Instance}{t}/v2.0");

    public static readonly EntraSettings Off = new();

    private static readonly string[] AppSettingKeys = { "ClientId", "ClientSecret", "Authority", "TenantId", "AllowedTenants" };
    private static readonly Regex TenantName = new("^[A-Za-z0-9][A-Za-z0-9.-]{0,252}$", RegexOptions.CultureInvariant);
    // a DNS name with at least one dot: contoso.com, power-pdf.de, contoso.onmicrosoft.com
    private static readonly Regex DomainName = new(@"^(?=.{4,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$",
                                                   RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>True when any Entra:* app setting is set: then the app settings decide and the database values are ignored.</summary>
    public static bool HasAppSettings(IConfiguration config)
    {
        var s = config.GetSection("Entra");
        return AppSettingKeys.Any(k => !string.IsNullOrWhiteSpace(s[k]));
    }

    /// <summary>An application (client) id: a GUID in the usual 8-4-4-4-12 form.</summary>
    public static bool IsClientId(string? value) => Guid.TryParseExact(value?.Trim(), "D", out _);

    /// <summary>A directory as an admin enters it: a directory (tenant) id or a verified domain such as power-pdf.de.</summary>
    public static bool IsDirectory(string? value)
    {
        var v = value?.Trim() ?? "";
        return Guid.TryParseExact(v, "D", out _) || DomainName.IsMatch(v);
    }

    /// <summary>The comma separated list of additional directories; null when one entry is not a directory id or domain.</summary>
    public static List<string>? SplitDirectories(string? value, int max = 20)
    {
        var parts = (value ?? "").Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
        return parts.Count <= max && parts.All(IsDirectory) ? parts : null;
    }

    public static EntraSettings From(IConfiguration config)
    {
        var s = config.GetSection("Entra");
        string V(string key) => (s[key] ?? "").Trim();
        var clientId = V("ClientId");
        var secret = V("ClientSecret");
        var authority = V("Authority");
        var tenantId = V("TenantId");
        var allowed = V("AllowedTenants");
        if (clientId.Length + secret.Length + authority.Length + tenantId.Length + allowed.Length == 0)
            return Off;
        EntraSettings Fail(string why) => new() { Problem = why, Source = EntraSource.AppSettings };

        var tenants = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in allowed.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Guid.TryParse(part, out var g))
                return Fail("Entra:AllowedTenants must list tenant ids (GUIDs), separated by commas.");
            tenants.Add(g.ToString("D"));
        }
        // a single-tenant setup may leave AllowedTenants empty: the tenant id itself is then the only one
        if (tenants.Count == 0 && Guid.TryParse(tenantId, out var single))
            tenants.Add(single.ToString("D"));

        if (authority.Length == 0 && tenantId.Length > 0)
        {
            if (!TenantName.IsMatch(tenantId))
                return Fail("Entra:TenantId must be a tenant id, a verified domain or \"organizations\".");
            authority = $"{MicrosoftInstance}{tenantId}/v2.0";
        }
        if (clientId.Length == 0) return Fail("Entra:ClientId is missing.");
        if (secret.Length == 0) return Fail("Entra:ClientSecret is missing.");
        if (authority.Length == 0) return Fail("Entra:TenantId or Entra:Authority is missing.");
        if (!Uri.TryCreate(authority, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Query.Length > 0)
            return Fail("Entra:Authority must be an https address without query.");
        if (tenants.Count == 0)
            return Fail("Entra:AllowedTenants is missing (tenant ids that may sign in).");

        return new EntraSettings
        {
            Enabled = true,
            Source = EntraSource.AppSettings,
            ClientId = clientId,
            ClientSecret = secret,
            Authority = authority.TrimEnd('/'),
            Instance = $"{uri.Scheme}://{uri.Authority}/",
            AllowedTenants = tenants,
        };
    }

    // Problems of the store source; the texts are resource keys (Resources/SharedResource.*.resx)
    public const string ProblemClientId = "The application (client) ID is missing.";
    public const string ProblemSecret = "The client secret is missing or can no longer be read. Enter it again.";
    public const string ProblemDirectory = "The directory is missing.";
    public const string ProblemResolve = "The directory could not be resolved to a directory ID. Save the settings again.";

    /// <summary>
    /// The configuration from the admin page. <paramref name="tenantId"/> is the directory id the admin's input
    /// (id or domain) resolved to when it was saved; <paramref name="extraTenantIds"/> the resolved additional
    /// directories. A single directory signs in through its own authority; with additional directories the store
    /// signs in through "organizations" (the app registration must then be multi-tenant) and still accepts only
    /// tokens of the listed directories (issuer and tid are checked).
    /// </summary>
    public static EntraSettings FromStore(bool enabled, string? clientId, string? secret, string? tenant, string? tenantId,
                                          IEnumerable<string>? extraTenantIds)
    {
        clientId = clientId?.Trim() ?? ""; secret = secret?.Trim() ?? ""; tenant = tenant?.Trim() ?? ""; tenantId = tenantId?.Trim() ?? "";
        if (!enabled) return new EntraSettings { Source = clientId.Length + tenant.Length > 0 ? EntraSource.Store : EntraSource.None };
        EntraSettings Fail(string why) => new() { Problem = why, Source = EntraSource.Store };
        if (!IsClientId(clientId)) return Fail(ProblemClientId);
        if (!IsDirectory(tenant)) return Fail(ProblemDirectory);
        if (secret.Length == 0) return Fail(ProblemSecret);
        if (!Guid.TryParseExact(tenantId, "D", out var main)) return Fail(ProblemResolve);

        var tenants = new HashSet<string>(StringComparer.Ordinal) { main.ToString("D") };
        foreach (var t in extraTenantIds ?? Enumerable.Empty<string>())
            if (Guid.TryParseExact(t.Trim(), "D", out var g)) tenants.Add(g.ToString("D"));
        var authority = tenants.Count == 1 ? $"{MicrosoftInstance}{main:D}/v2.0" : $"{MicrosoftInstance}organizations/v2.0";
        return new EntraSettings
        {
            Enabled = true,
            Source = EntraSource.Store,
            ClientId = clientId.ToLowerInvariant(),
            ClientSecret = secret,
            Authority = authority,
            Instance = MicrosoftInstance,
            AllowedTenants = tenants,
        };
    }

    /// <summary>Same effective configuration (decides whether the cached OpenID Connect options are rebuilt).</summary>
    public bool SameAs(EntraSettings other) =>
        Enabled == other.Enabled && Source == other.Source && ClientId == other.ClientId && ClientSecret == other.ClientSecret &&
        Authority == other.Authority && Problem == other.Problem && AllowedTenants.SetEquals(other.AllowedTenants);
}
