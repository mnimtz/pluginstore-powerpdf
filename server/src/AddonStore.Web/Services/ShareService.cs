using System.Text.RegularExpressions;
using AddonStore.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Services;

/// <summary>
/// Shared add-on links (S0.10.0): every add-on has a public page /a/{slug};
/// an optional ?ref= (for example a sales person's short name) attributes page
/// views, install clicks and client installer downloads in the reports.
/// Daily counters only (ShareStats), nothing personal.
/// </summary>
public class ShareService
{
    private static readonly Regex RefRx = new(@"^[a-z0-9][a-z0-9._-]{0,39}$", RegexOptions.Compiled);
    public static readonly string[] Kinds = { "view", "install", "client" };

    private readonly AppDbContext _db;
    private readonly ILogger<ShareService> _log;

    public ShareService(AppDbContext db, ILogger<ShareService> log) { _db = db; _log = log; }

    /// <summary>Lower-case short name or "" when invalid.</summary>
    public static string CleanRef(string? r)
    {
        var v = (r ?? "").Trim().ToLowerInvariant();
        return RefRx.IsMatch(v) ? v : "";
    }

    /// <summary>Short page name: the last id segment when unique, else the full id.</summary>
    public static string Slug(string id, IEnumerable<string> allIds)
    {
        var last = id[(id.LastIndexOf('.') + 1)..];
        return allIds.Count(o => o != id && o[(o.LastIndexOf('.') + 1)..] == last) == 0 ? last : id;
    }

    /// <summary>Resolves a slug (short name or full id) to a package id among the given ids.</summary>
    public static string? Resolve(string slug, IReadOnlyCollection<string> allIds)
    {
        slug = (slug ?? "").Trim().ToLowerInvariant();
        if (allIds.Contains(slug)) return slug;
        return allIds.FirstOrDefault(id => Slug(id, allIds) == slug);
    }

    /// <summary>Link previews (Teams, Outlook, Slack, messengers) and crawlers are not counted as views.</summary>
    public static bool IsPreviewBot(string? userAgent)
    {
        var ua = (userAgent ?? "").ToLowerInvariant();
        return ua.Length == 0 || ua.Contains("bot") || ua.Contains("preview") || ua.Contains("skypeuri") ||
               ua.Contains("facebookexternalhit") || ua.Contains("whatsapp") || ua.Contains("crawler") ||
               ua.Contains("spider") || ua.Contains("teams") || ua.Contains("outlook") || ua.Contains("curl") ||
               ua.Contains("python") || ua.Contains("headless");
    }

    public async Task CountAsync(string packageId, string? reference, string kind)
    {
        if (!Kinds.Contains(kind) || packageId.Length is 0 or > 120) return;
        try
        {
            var day = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var r = CleanRef(reference);
            await _db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ShareStats (Day, PackageId, Ref, Kind, Count) VALUES ({day}, {packageId}, {r}, {kind}, 1)
                ON CONFLICT (Day, PackageId, Ref, Kind) DO UPDATE SET Count = Count + 1");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "share counter failed for {Package}", packageId);
        }
    }
}
