using System.Text.Json;
using AddonStore.Web.Data;
using AddonStore.Web.Validation;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Services;

public record CatalogItem(string Id, string Name, string Description, string Version,
    string Channel, string Changelog, long SizeBytes, int Downloads,
    string Sha256, string MinHost, string ZxtName);

public static class CatalogUi
{
    public static async Task<List<CatalogItem>> GetAsync(AppDbContext db, string culture, bool includeBeta = false)
    {
        var all = await db.PackageVersions
            .Where(v => v.Status == VersionStatus.Live || (includeBeta && v.Status == VersionStatus.Beta))
            .ToListAsync();
        var cmp = new SemVerComparer();
        var items = new List<CatalogItem>();
        foreach (var group in all.GroupBy(v => v.PackageId).OrderBy(g => g.Key))
        {
            var live = group.Where(v => v.Status == VersionStatus.Live).OrderByDescending(v => v.Version, cmp).FirstOrDefault();
            var beta = group.Where(v => v.Status == VersionStatus.Beta).OrderByDescending(v => v.Version, cmp).FirstOrDefault();
            var pick = live;
            var channel = "live";
            if (includeBeta && beta is not null && (live is null || cmp.Compare(beta.Version, live.Version) > 0))
            {
                pick = beta; channel = "beta";
            }
            if (pick is null) continue;
            using var doc = JsonDocument.Parse(pick.ManifestJson);
            var zxt = "";
            if (doc.RootElement.TryGetProperty("files", out var files) &&
                files.ValueKind == JsonValueKind.Object &&
                files.TryGetProperty("x64", out var fx) && fx.ValueKind == JsonValueKind.String)
                zxt = Path.GetFileNameWithoutExtension(fx.GetString() ?? "");
            items.Add(new CatalogItem(
                pick.PackageId,
                LangText(doc.RootElement, "name", culture) ?? pick.PackageId,
                LangText(doc.RootElement, "description", culture) ?? "",
                pick.Version, channel, pick.Changelog, pick.SizeBytes, pick.Downloads,
                pick.Sha256, pick.MinPowerPdfVersion, zxt));
        }
        return items;
    }

    /// <summary>Picks the best language from a {lang: text} manifest object.</summary>
    public static string? LangText(JsonElement root, string field, string culture)
    {
        if (!root.TryGetProperty(field, out var el)) return null;
        if (el.ValueKind == JsonValueKind.String) return el.GetString();
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var lang in new[] { culture, "en" })
            if (el.TryGetProperty(lang, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        var first = el.EnumerateObject().FirstOrDefault(p => p.Value.ValueKind == JsonValueKind.String);
        return first.Value.ValueKind == JsonValueKind.String ? first.Value.GetString() : null;
    }
}
