using System.Text.RegularExpressions;

namespace AddonStore.Web.Auth;

/// <summary>
/// "Sign in with Microsoft" (Microsoft Entra ID, OpenID Connect). Read once at start-up from the app
/// settings / environment only (Entra:ClientId, Entra:ClientSecret, Entra:TenantId or Entra:Authority,
/// Entra:AllowedTenants); never from the repository or the database. Without a complete configuration the
/// feature is off: no button, no OpenID Connect handler, no routes. Setup: docs/entra-setup.md.
/// </summary>
public sealed class EntraSettings
{
    /// <summary>Authentication scheme and LoginProvider in AspNetUserLogins (key "{tid}:{oid}").</summary>
    public const string Scheme = "Entra";
    public const string CallbackPath = "/signin-oidc";
    public const string ChallengePath = "/Account/Microsoft";
    public const string CompletePath = "/Account/Microsoft/Callback";

    public bool Enabled { get; private init; }
    public string ClientId { get; private init; } = "";
    public string ClientSecret { get; private init; } = "";
    /// <summary>OpenID Connect authority, e.g. https://login.microsoftonline.com/{tenant}/v2.0.</summary>
    public string Authority { get; private init; } = "";
    /// <summary>Scheme and host of the authority with a trailing slash, e.g. https://login.microsoftonline.com/.</summary>
    public string Instance { get; private init; } = "";
    /// <summary>Tenant ids (lower-case GUIDs) whose accounts may sign in.</summary>
    public IReadOnlySet<string> AllowedTenants { get; private init; } = new HashSet<string>();
    /// <summary>Why the feature stays off although some Entra value is set (logged at start-up); null when nothing is set.</summary>
    public string? Problem { get; private init; }

    /// <summary>Issuer of the v2.0 ID tokens of each allowed tenant.</summary>
    public IEnumerable<string> ValidIssuers => AllowedTenants.Select(t => $"{Instance}{t}/v2.0");

    public static readonly EntraSettings Off = new();

    private static readonly Regex TenantName = new("^[A-Za-z0-9][A-Za-z0-9.-]{0,252}$", RegexOptions.CultureInvariant);

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
        EntraSettings Fail(string why) => new() { Problem = why };

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
            authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
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
            ClientId = clientId,
            ClientSecret = secret,
            Authority = authority.TrimEnd('/'),
            Instance = $"{uri.Scheme}://{uri.Authority}/",
            AllowedTenants = tenants,
        };
    }
}
