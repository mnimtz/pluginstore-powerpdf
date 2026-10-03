using System.Text.Json;
using AddonStore.Web.Data;
using AddonStore.Web.Validation;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Services;

public record CatalogItem(string Id, string Name, string Description, string Version,
    string Channel, string Changelog, long SizeBytes, int Downloads,
    string Sha256, string MinHost, string ZxtName, string Category, string Author, string ContactEmail);

public static class CatalogUi
{
    /// <summary>Fixed category slugs; anything else maps to "other". The UI
    /// shows them as localized labels (resx keys = English labels).</summary>
    public static readonly string[] Categories =
        { "conversion", "forms", "signing", "navigation", "printing", "productivity", "system", "other" };

    /// <summary>English display label for a category slug; doubles as resx key.</summary>
    public static string CategoryLabel(string slug) => slug switch
    {
        "conversion" => "Conversion",
        "forms" => "Forms",
        "signing" => "Signing",
        "navigation" => "Navigation",
        "printing" => "Printing",
        "productivity" => "Productivity",
        "system" => "System",
        _ => "Other"
    };
    public static async Task<List<CatalogItem>> GetAsync(AppDbContext db, string culture, bool includeBeta = false)
    {
        var all = await db.PackageVersions
            .Where(v => v.Status == VersionStatus.Live || (includeBeta && v.Status == VersionStatus.Beta))
            .ToListAsync();
        var cmp = new SemVerComparer();
        var items = new List<CatalogItem>();
        var ownerRows = await db.Packages.Include(p => p.Owner).ToListAsync();
        var owners = ownerRows.ToDictionary(p => p.Id, p => CatalogUi.PublicName(p.Owner));
        var ownerMails = ownerRows.ToDictionary(p => p.Id, p => CatalogUi.PublicEmail(p.Owner));
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
            var category = doc.RootElement.TryGetProperty("category", out var cat) &&
                           cat.ValueKind == JsonValueKind.String
                ? (cat.GetString() ?? "other") : "other";
            if (!Categories.Contains(category)) category = "other";
            items.Add(new CatalogItem(
                pick.PackageId,
                LangText(doc.RootElement, "name", culture) ?? pick.PackageId,
                LangText(doc.RootElement, "description", culture) ?? "",
                pick.Version, channel,
                LangText(doc.RootElement, "changelog", culture) ?? pick.Changelog,
                pick.SizeBytes, pick.Downloads,
                pick.Sha256, pick.MinPowerPdfVersion, zxt, category,
                AuthorOf(doc.RootElement, owners.GetValueOrDefault(pick.PackageId, pick.SubmittedBy)),
                ContactOf(doc.RootElement, ownerMails.GetValueOrDefault(pick.PackageId, ""))));
        }
        return items;
    }

    public const string PlaceholderAuthor = "Tungsten Automation";

    /// <summary>The owner's public name, or the placeholder when they opted out.</summary>
    public static string PublicName(AppUser? owner) =>
        owner is null || !owner.ShowContactPublicly ? PlaceholderAuthor : owner.DisplayName;

    /// <summary>The owner's public email, or nothing when they opted out.</summary>
    public static string PublicEmail(AppUser? owner) =>
        owner is null || !owner.ShowContactPublicly ? "" : owner.Email ?? "";

    /// <summary>Optional manifest "author" (e.g. a team) wins; otherwise the publishing account.</summary>
    public static string AuthorOf(JsonElement root, string ownerName) =>
        root.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(a.GetString())
            ? a.GetString()!.Trim()
            : ownerName;

    /// <summary>Optional manifest "contactEmail" wins; otherwise the publishing account's email.</summary>
    public static string ContactOf(JsonElement root, string ownerEmail)
    {
        if (root.TryGetProperty("contactEmail", out var c) && c.ValueKind == JsonValueKind.String)
        {
            var v = (c.GetString() ?? "").Trim();
            if (System.Net.Mail.MailAddress.TryCreate(v, out _)) return v;
        }
        return ownerEmail;
    }

    /// <summary>Localized text of a manifest field for a stored version (e.g. changelog, name).</summary>
    public static string ManifestText(PackageVersion v, string field, string culture, string fallback = "")
    {
        try
        {
            using var doc = JsonDocument.Parse(v.ManifestJson);
            return LangText(doc.RootElement, field, culture) ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
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
