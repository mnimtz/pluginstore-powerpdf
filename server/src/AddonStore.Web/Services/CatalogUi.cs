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
    /// <summary>Start page highlight (S1.16.0): when an admin marked it; null = no highlight.</summary>
    public DateTime? FeaturedAt { get; init; }
    public bool Featured => FeaturedAt is not null;
    /// <summary>"Connects to" (S1.19.0): providers and the declared services of the shown version.</summary>
    public Connections Connections { get; init; } = Connections.None;
    /// <summary>S2.3.0: when the shown version was released (review date, else upload date).</summary>
    public DateTime ReleasedAt { get; init; }
    /// <summary>S2.3.0: when the add-on first appeared in the catalog (earliest released version), for the "New" badge.</summary>
    public DateTime FirstReleasedAt { get; init; }
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
            Packages = await db.Packages.AsNoTracking().Include(p => p.Owner).ToDictionaryAsync(p => p.Id),
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
            pick.Sha256, MinHost.Effective(pkg, pick), zxt, category,   // S2.5.0: the admin's minimum wins
            EffectiveAuthor(pkg, doc.RootElement, pkg is null ? pick.SubmittedBy : PublicName(pkg.Owner)),
            EffectiveContact(pkg, doc.RootElement, pkg is null ? "" : PublicEmail(pkg.Owner)),
            CategoryService.Name(ctx.Known.GetValueOrDefault(category), category, culture))
        {
            Rating = ctx.Ratings.GetValueOrDefault(pick.PackageId)?.Average ?? 0,
            RatingCount = ctx.Ratings.GetValueOrDefault(pick.PackageId)?.Count ?? 0,
            Screenshots = ScreenshotService.Count(pick.ManifestJson),
            Customer = customer,
            NoUi = IsNoUi(doc.RootElement),
            FeaturedAt = pkg?.Visibility == "private" ? null : pkg?.FeaturedAt,
            Connections = Connections.From(pkg?.ProvidersJson, doc.RootElement),
            ReleasedAt = pick.ReviewedAt ?? pick.SubmittedAt,
            FirstReleasedAt = pick.ReviewedAt ?? pick.SubmittedAt,
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
    // S2.0.7: ten minutes (was one): every write to what the catalog shows starts a new generation anyway; only
    // download counters and ratings changed by raw updates may lag that long
    private static readonly TimeSpan CacheAge = TimeSpan.FromMinutes(10);

    /// <summary>Drops the cached catalogs (a write to packages, versions, categories, ratings or accounts).</summary>
    public static void Invalidate() => Interlocked.Increment(ref _generation);

    // S2.0.2: other answers that are the same for every caller (JSON catalog without customer codes, categories),
    // under the same rules: a new generation on writes, at most a minute old
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Gen, DateTime At, object Value)> _objects = new();

    /// <summary>The cached answer for <paramref name="key"/> of the current generation, or null.</summary>
    public static object? CachedObject(string key) =>
        _objects.TryGetValue(key, out var hit) && hit.Gen == Interlocked.Read(ref _generation) && DateTime.UtcNow - hit.At < CacheAge ? hit.Value : null;

    /// <summary>Keeps <paramref name="value"/> for <paramref name="key"/>; <paramref name="gen"/> is the generation read before the value was built.</summary>
    public static void CacheObject(string key, long gen, object value)
    {
        if (_objects.Count > 100) _objects.Clear();
        _objects[key] = (gen, DateTime.UtcNow, value);
    }

    /// <summary>The current generation (read it before building a value for <see cref="CacheObject"/>).</summary>
    public static long Generation => Interlocked.Read(ref _generation);

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
        // S2.0.6: the light rows from memory (LightVersions), only the package visibility from the database
        var publicIds = (await db.Packages.AsNoTracking().Where(p => p.Visibility != "private").Select(p => p.Id).ToListAsync()).ToHashSet();
        var every = await AddonStore.Web.Data.LightVersions.AllAsync(db);
        // S2.3.0: first release per add-on: the earliest reviewed version that was ever approved (withdrawn ones count)
        var first = every.Where(v => v.ReviewedAt is not null && v.Status is VersionStatus.Live or VersionStatus.Beta or VersionStatus.Withdrawn)
            .GroupBy(v => v.PackageId).ToDictionary(g => g.Key, g => g.Min(v => v.ReviewedAt!.Value));
        var all = every
            .Where(v => (v.Status == VersionStatus.Live || (includeBeta && v.Status == VersionStatus.Beta)) && publicIds.Contains(v.PackageId))
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
            var item = Item(ctx, pick, channel, culture);
            if (first.TryGetValue(pick.PackageId, out var f) && f < item.FirstReleasedAt) item = item with { FirstReleasedAt = f };
            items.Add(item);
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

    // S2.3.3: the company is never shown as an author. A manifest, a catalog entry or an old packaging template
    // that names "Tungsten Automation" (with or without a legal suffix) counts as "no author given", and an owner who
    // hides their name shows no author at all (it used to be "Tungsten Automation").
    private static readonly System.Text.RegularExpressions.Regex CompanyOnly = new(
        @"^\s*tungsten(\s+automation)?\s*[,.]?\s*(corp\.?|corporation|inc\.?|gmbh|ltd\.?|llc)?\s*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    /// <summary>The company name alone ("Tungsten Automation", "Tungsten Automation Corp." ...) is not an author.</summary>
    public static bool IsCompanyName(string? author) => !string.IsNullOrWhiteSpace(author) && CompanyOnly.IsMatch(author);

    /// <summary>The owner's public name, or nothing when they opted out (S2.3.3: no company placeholder).</summary>
    public static string PublicName(AppUser? owner) =>
        owner is null || !owner.ShowContactPublicly || IsCompanyName(owner.DisplayName) ? "" : owner.DisplayName;

    /// <summary>The owner's public email, or nothing when they opted out.</summary>
    public static string PublicEmail(AppUser? owner) =>
        owner is null || !owner.ShowContactPublicly ? "" : owner.Email ?? "";

    /// <summary>Optional manifest "author" (e.g. a team) wins; otherwise the publishing account.</summary>
    public static string AuthorOf(JsonElement root, string ownerName) =>
        root.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(a.GetString()) && !IsCompanyName(a.GetString())
            ? a.GetString()!.Trim()
            : IsCompanyName(ownerName) ? "" : ownerName;

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
        !string.IsNullOrWhiteSpace(pkg?.Author) && !IsCompanyName(pkg!.Author) ? pkg!.Author!.Trim() : AuthorOf(root, ownerName);

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
