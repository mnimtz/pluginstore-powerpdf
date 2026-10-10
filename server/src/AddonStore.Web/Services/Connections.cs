using System.Text.Json;
using System.Text.RegularExpressions;

namespace AddonStore.Web.Services;

/// <summary>A company behind the services an add-on connects to (S1.19.0), with its website.</summary>
public record AddonProvider(string Name, string Website);

/// <summary>One declared connection: what it is, the host it goes to, the data it carries.</summary>
public record ServiceLink(string Name, string Host, string Data);

/// <summary>
/// "Connects to" of an add-on (S1.19.0): the providers (catalog entry first, then the
/// manifest's "providers") and the services the developer declared in
/// complianceAudit.externalServices, checked at review. Declared = false for versions
/// from before the declaration existed: then nothing is shown rather than "offline".
/// </summary>
public record Connections(List<AddonProvider> Providers, List<ServiceLink> Services, bool Declared)
{
    public static readonly Connections None = new(new(), new(), false);
    public bool Offline => Declared && Services.Count == 0;
    /// <summary>S1.19.1: shown to users only when the add-on really connects somewhere (declared services), or for a
    /// version from before the declaration whose provider an admin entered; an offline add-on shows nothing.</summary>
    public bool Shown => Services.Count > 0 || (!Declared && Providers.Count > 0);

    public const int MaxProviders = 5, MaxName = 80, MaxWebsite = 300, MaxServices = 12, MaxData = 400;

    /// <summary>Providers and services of one manifest, with the catalog entry's providers (JSON array) winning when set.</summary>
    public static Connections From(string? providersOverrideJson, JsonElement root)
    {
        var providers = providersOverrideJson is not null ? ParseProviders(providersOverrideJson)
            : root.TryGetProperty("providers", out var mp) ? ReadProviders(mp) : new();
        JsonElement svc = default;
        var declared = (root.TryGetProperty("complianceAudit", out var ca) && ca.ValueKind == JsonValueKind.Object &&
                        ca.TryGetProperty("externalServices", out svc) && svc.ValueKind == JsonValueKind.Array) ||
                       (root.TryGetProperty("externalServices", out svc) && svc.ValueKind == JsonValueKind.Array);
        var services = new List<ServiceLink>();
        if (declared)
            foreach (var s in svc.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object || services.Count >= MaxServices) continue;
                var name = Text(s, "name", MaxName * 2);
                var host = HostOf(Text(s, "url", MaxWebsite));
                var data = Text(s, "data", MaxData);
                if (name.Length == 0 && host.Length == 0) continue;
                services.Add(new(name, host, data));
            }
        return new(providers, services, declared);
    }

    public static List<AddonProvider> ParseProviders(string json)
    {
        try { using var d = JsonDocument.Parse(json); return ReadProviders(d.RootElement); }
        catch (JsonException) { return new(); }
    }

    private static List<AddonProvider> ReadProviders(JsonElement arr)
    {
        var list = new List<AddonProvider>();
        if (arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var p in arr.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Object || list.Count >= MaxProviders) continue;
            var name = Text(p, "name", MaxName);
            var web = Text(p, "website", MaxWebsite);
            if (name.Length == 0) continue;
            list.Add(new(name, IsWebsite(web) ? web : ""));   // a website that is no clean https address is left out, never linked
        }
        return list;
    }

    /// <summary>An https address of a public host, no user name or password, no spaces: the only links shown.</summary>
    public static bool IsWebsite(string? url) =>
        !string.IsNullOrEmpty(url) && url.Length <= MaxWebsite && !url.Any(char.IsWhiteSpace) &&
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(u.UserInfo) && u.Host.Contains('.') && u.HostNameType == UriHostNameType.Dns &&
        u.Host is not ("localhost") && !u.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// S1.20.2: a declared address of a fixed public host, whatever the scheme (https, http, wss, ftp ...)
    /// or none ("api.example.com/v1"), also a public IP address; not a description such as "configured by
    /// the admin", and not localhost, *.localhost, private, loopback or link-local addresses.
    /// </summary>
    public static bool IsFixedPublicHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxWebsite || url.Trim().Any(char.IsWhiteSpace)) return false;
        var u = url.Trim();
        if (!u.Contains("://")) u = "https://" + u;
        if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return false;
        var host = uri.Host.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return false;
        if (System.Net.IPAddress.TryParse(host, out var ip))
        {
            if (System.Net.IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal) return false;
            var b = ip.MapToIPv4().GetAddressBytes();
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork || ip.IsIPv4MappedToIPv6)
                return !(b[0] == 10 || b[0] == 127 || b[0] == 0 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                         (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127));
            return true;
        }
        return uri.HostNameType == UriHostNameType.Dns && host.Contains('.');
    }

    /// <summary>True when the manifest declares a fixed public service (then it must name its providers).</summary>
    public static bool NeedsProvider(JsonElement root)
    {
        JsonElement svc = default;
        var declared = (root.TryGetProperty("complianceAudit", out var ca) && ca.ValueKind == JsonValueKind.Object &&
                        ca.TryGetProperty("externalServices", out svc) && svc.ValueKind == JsonValueKind.Array) ||
                       (root.TryGetProperty("externalServices", out svc) && svc.ValueKind == JsonValueKind.Array);
        return declared && svc.EnumerateArray().Any(s => s.ValueKind == JsonValueKind.Object &&
            s.TryGetProperty("url", out var v) && v.ValueKind == JsonValueKind.String && IsFixedPublicHost(v.GetString()));
    }

    /// <summary>The host of a declared address, shown as text only ("api.example.com", "*.example.com").</summary>
    public static string HostOf(string url)
    {
        var m = Regex.Match(url, @"^[a-z][a-z0-9+.-]*://([^/?#\s]+)", RegexOptions.IgnoreCase);
        var host = m.Success ? m.Groups[1].Value : url.Split('/', '?', '#')[0];
        host = host.Contains('@') ? host[(host.LastIndexOf('@') + 1)..] : host;
        return Regex.IsMatch(host, @"^[A-Za-z0-9*.\-:\[\]]{1,200}$") ? host.ToLowerInvariant() : "";
    }

    private static string Text(JsonElement o, string prop, int max)
    {
        if (!o.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.String) return "";
        var s = string.Concat((v.GetString() ?? "").Select(ch => ch is '\t' or '\r' or '\n' ? " "
            : char.IsControl(ch) || ch is '‪' or '‫' or '‬' or '‭' or '‮' or '⁦' or '⁧' or '⁨' or '⁩' ? ""
            : ch.ToString())).Trim();
        return s.Length > max ? s[..max].TrimEnd() + "…" : s;
    }

    /// <summary>Compact form for the client catalog (TSV column 23): ASCII JSON, empty when nothing was declared.</summary>
    public string ToClientJson() => !Shown ? "" : JsonSerializer.Serialize(new
    {
        p = Providers.Select(p => new { n = p.Name, w = p.Website }),
        s = Services.Select(s => new { n = s.Name, h = s.Host, d = s.Data }),
        o = Offline,
    });
}
