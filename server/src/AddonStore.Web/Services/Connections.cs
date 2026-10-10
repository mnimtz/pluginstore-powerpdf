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
    public string ToClientJson() => !Declared && Providers.Count == 0 ? "" : JsonSerializer.Serialize(new
    {
        p = Providers.Select(p => new { n = p.Name, w = p.Website }),
        s = Services.Select(s => new { n = s.Name, h = s.Host, d = s.Data }),
        o = Offline,
    });
}
