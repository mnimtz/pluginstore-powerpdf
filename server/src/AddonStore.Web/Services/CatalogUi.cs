using System.Text.Json;
using AddonStore.Web.Data;
using AddonStore.Web.Validation;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Services;

public record CatalogItem(string Id, string Name, string Description, string Version,
    string Channel, string Changelog, long SizeBytes, int Downloads,
    string Sha256, string MinHost, string ZxtName, string Category, string Author, string ContactEmail,
    string CategoryName = "")
{
    /// <summary>Average stars (0 = no rating yet) and number of ratings (S0.11.0).</summary>
    public double Rating { get; init; }
    public int RatingCount { get; init; }
    /// <summary>Number of screenshots in the shown version's manifest.</summary>
    public int Screenshots { get; init; }
    /// <summary>Customer name when the entry comes from a customer delivery (S0.14.0), else empty.</summary>
    public string Customer { get; init; } = "";
    /// <summary>"ui": "none" in the manifest (S1.1.1): no ribbon buttons.</summary>
    public bool NoUi { get; init; }
}

public static class CatalogUi
{
    /// <summary>Effective category of a catalog entry: server override, then manifest; unknown slugs become "other".</summary>
    public static string EffectiveCategory(Package? pkg, JsonElement root, Dictionary<string, Category> known)
    {
        var slug = !string.IsNullOrWhiteSpace(pkg?.CategoryOverride) ? pkg!.CategoryOverride!
            : root.TryGetProperty("category", out var cat) && cat.ValueKind == JsonValueKind.String ? cat.GetString() ?? "other" : "other";
        return known.ContainsKey(slug) ? slug : "other";
    }
    /// <summary>Lookups shared by every catalog entry of one request.</summary>
    public sealed class Context
    {
        public Dictionary<string, Package> Packages = new();
        public Dictionary<string, Category> Known = new();
        public Dictionary<string, FeedbackService.Summary> Ratings = new();
        public static async Task<Context> LoadAsync(AppDbContext db) => new()
        {
            Packages = await db.Packages.Include(p => p.Owner).ToDictionaryAsync(p => p.Id),
            Known = await db.Categories.ToDictionaryAsync(c => c.Slug),
            Ratings = await FeedbackService.SummariesAsync(db),
        };
    }

    /// <summary>The manifest declares "ui": "none": an add-on without ribbon buttons (S1.1.1).</summary>
    public static bool IsNoUi(JsonElement root) =>
        root.TryGetProperty("ui", out var u) && u.ValueKind == JsonValueKind.String && u.GetString() == "none";

    /// <summary>One catalog entry for a given version (public catalog or customer delivery).</summary>
    public static CatalogItem Item(Context ctx, PackageVersion pick, string channel, string culture, string customer = "")
    {
        using var doc = JsonDocument.Parse(pick.ManifestJson);
        var zxt = "";
        if (doc.RootElement.TryGetProperty("files", out var files) &&
            files.ValueKind == JsonValueKind.Object &&
            files.TryGetProperty("x64", out var fx) && fx.ValueKind == JsonValueKind.String)
            zxt = Path.GetFileNameWithoutExtension(fx.GetString() ?? "");
        var pkg = ctx.Packages.GetValueOrDefault(pick.PackageId);
        var category = EffectiveCategory(pkg, doc.RootElement, ctx.Known);
        return new CatalogItem(
            pick.PackageId,
            OverrideText(pkg?.NameJson, culture) ?? LangText(doc.RootElement, "name", culture) ?? pick.PackageId,
            OverrideText(pkg?.DescriptionJson, culture) ?? LangText(doc.RootElement, "description", culture) ?? "",
            pick.Version, channel,
            LangText(doc.RootElement, "changelog", culture) ?? pick.Changelog,
            pick.SizeBytes, pick.Downloads,
            pick.Sha256, pick.MinPowerPdfVersion, zxt, category,
            EffectiveAuthor(pkg, doc.RootElement, pkg is null ? pick.SubmittedBy : PublicName(pkg.Owner)),
            EffectiveContact(pkg, doc.RootElement, pkg is null ? "" : PublicEmail(pkg.Owner)),
            CategoryService.Name(ctx.Known.GetValueOrDefault(category), category, culture))
        {
            Rating = ctx.Ratings.GetValueOrDefault(pick.PackageId)?.Average ?? 0,
            RatingCount = ctx.Ratings.GetValueOrDefault(pick.PackageId)?.Count ?? 0,
            Screenshots = ScreenshotService.Count(pick.ManifestJson),
            Customer = customer,
            NoUi = IsNoUi(doc.RootElement),
        };
    }

    /// <summary>The public catalog: newest live (or beta) version of every public package.</summary>
    /// <summary>The first store client that installs bin/ (S1.4.0).</summary>
    public static readonly Version BinClient = new(1, 4, 0);

    /// <summary>The version brings DLLs in bin/ (files.bin, S1.4.0).</summary>
    public static bool HasBin(PackageVersion v) => Validation.PackageValidator.BinFilesOf(v.ManifestJson).Any();

    /// <summary>False for store clients older than 1.4.0: they would install the .zxt without its DLLs.</summary>
    public static bool ClientSupportsBin(HttpContext ctx)
    {
        var c = UsageService.Classify(ctx);
        return c.Source != "client" || (Version.TryParse(c.ClientVersion, out var cv) && cv >= BinClient);
    }

    // S1.13.2: the public catalog is kept in memory. On the production share every query reads
    // the database over the network (about a second for the store window); a write to what the
    // catalog shows (AppDbContext.SaveChanges) or a restore starts a new generation, and an entry
    // is at most a minute old (raw updates such as download counters bypass the generation).
    private static long _generation;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Gen, DateTime At, List<CatalogItem> Items)> _cache = new();
    private static readonly TimeSpan CacheAge = TimeSpan.FromMinutes(1);

    /// <summary>Drops the cached catalogs (a write to packages, versions, categories, ratings or accounts).</summary>
    public static void Invalidate() => Interlocked.Increment(ref _generation);

    public static async Task<List<CatalogItem>> GetAsync(AppDbContext db, string culture, bool includeBeta = false, bool binOk = true)
    {
        var key = $"{culture}|{includeBeta}|{binOk}";
        var gen = Interlocked.Read(ref _generation);
        if (_cache.TryGetValue(key, out var hit) && hit.Gen == gen && DateTime.UtcNow - hit.At < CacheAge)
            return new List<CatalogItem>(hit.Items);   // callers add and remove entries: a copy each
        var items = await LoadAsync(db, culture, includeBeta, binOk);
        if (_cache.Count > 200) _cache.Clear();   // any "lang" value makes a key: keep it small
        _cache[key] = (gen, DateTime.UtcNow, items);
        return new List<CatalogItem>(items);
    }

    private static async Task<List<CatalogItem>> LoadAsync(AppDbContext db, string culture, bool includeBeta, bool binOk)
    {
        var all = (await db.PackageVersions
            .Where(v => v.Status == VersionStatus.Live || (includeBeta && v.Status == VersionStatus.Beta))
            .Where(v => db.Packages.Any(p => p.Id == v.PackageId && p.Visibility != "private"))
            .ToListAsync())
            .Where(v => binOk || !HasBin(v)).ToList();   // an older client gets the newest version it can install
        var cmp = new SemVerComparer();
        var items = new List<CatalogItem>();
        var ctx = await Context.LoadAsync(db);
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
            items.Add(Item(ctx, pick, channel, culture));
        }
        return items;
    }

    /// <summary>Resource key of a version status (S1.6.0: "Submitted" reads "Awaiting review").</summary>
    public static string StatusKey(VersionStatus s) => s switch
    {
        VersionStatus.Submitted => "Awaiting review",
        VersionStatus.Beta => "Beta",
        VersionStatus.Live => "Live",
        VersionStatus.Rejected => "Rejected",
        _ => "Withdrawn"
    };

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

    /// <summary>Server-side catalog entry wins over the manifest; see PackageMetaService.</summary>
    public static string EffectiveAuthor(Package? pkg, JsonElement root, string ownerName) =>
        !string.IsNullOrWhiteSpace(pkg?.Author) ? pkg!.Author!.Trim() : AuthorOf(root, ownerName);

    public static string EffectiveContact(Package? pkg, JsonElement root, string ownerEmail) =>
        !string.IsNullOrWhiteSpace(pkg?.ContactEmail) ? pkg!.ContactEmail!.Trim() : ContactOf(root, ownerEmail);

    /// <summary>Best language from a stored {lang: text} override, or null when there is none.</summary>
    public static string? OverrideText(string? json, string culture)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse("{\"v\":" + json + "}");
            return LangText(doc.RootElement, "v", culture);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Display name of a package: catalog entry first, then the given version's manifest.</summary>
    public static string DisplayName(Package pkg, PackageVersion? v, string culture) =>
        OverrideText(pkg.NameJson, culture) ?? (v is null ? pkg.Id : ManifestText(v, "name", culture, pkg.Id));

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
        foreach (var lang in culture == "nb" ? new[] { "nb", "no", "en" } : new[] { culture, "en" })
            if (el.TryGetProperty(lang, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        var first = el.EnumerateObject().FirstOrDefault(p => p.Value.ValueKind == JsonValueKind.String);
        return first.Value.ValueKind == JsonValueKind.String ? first.Value.GetString() : null;
    }
}
