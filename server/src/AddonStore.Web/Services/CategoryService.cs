using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AddonStore.Web.Data;
using AddonStore.Web.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace AddonStore.Web.Services;

/// <summary>
/// Catalog categories grow with the store, but only as broad functional areas:
/// an upload may propose a new one (slug plus names in 16 languages) when no
/// existing category fits; the server checks format, similarity, specificity
/// and a hard limit before it creates it on submission. Admins merge or delete.
/// </summary>
public class CategoryService
{
    public static readonly (string Slug, string Label)[] Builtin =
    {
        ("conversion", "Conversion"), ("forms", "Forms"), ("signing", "Signing"), ("navigation", "Navigation"),
        ("printing", "Printing"), ("productivity", "Productivity"), ("system", "System"), ("other", "Other"),
    };

    public const int DefaultMax = 16;
    public const int MaxNameLength = 24;
    private static readonly Regex SlugPattern = new(@"^[a-z][a-z-]{2,23}$", RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly SettingsService _settings;
    private readonly AuditService _audit;
    private readonly NotificationService _notify;

    public CategoryService(AppDbContext db, SettingsService settings, AuditService audit, NotificationService notify)
    {
        _db = db; _settings = settings; _audit = audit; _notify = notify;
    }

    public static bool IsValidSlug(string slug) => SlugPattern.IsMatch(slug) && !slug.EndsWith('-') && !slug.Contains("--");

    public async Task<int> MaxAsync() =>
        int.TryParse(await _settings.GetAsync("Categories.Max"), out var n) && n is >= 8 and <= 50 ? n : DefaultMax;

    public Task<List<Category>> AllAsync() => _db.Categories.OrderBy(c => c.Slug).ToListAsync();

    /// <summary>Localized name of a category; falls back to English, then to the slug.</summary>
    public static string Name(Category? c, string slug, string culture)
    {
        if (c?.NameJson is { Length: > 0 } json)
        {
            var t = CatalogUi.OverrideText(json, culture);
            if (!string.IsNullOrWhiteSpace(t)) return t!;
        }
        return slug;
    }

    /// <summary>Lower-case letters only, without a plural ending, for similarity checks.</summary>
    private static string Stem(string s)
    {
        var t = new string(s.ToLowerInvariant().Where(char.IsLetter).ToArray());
        foreach (var suffix in new[] { "ing", "ion", "es", "s" })
            if (t.Length > suffix.Length + 3 && t.EndsWith(suffix)) { t = t[..^suffix.Length]; break; }
        return t;
    }

    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    /// <summary>An existing category the proposal is too close to, if any.</summary>
    public static Category? TooSimilar(string slug, Dictionary<string, string> names, IEnumerable<Category> existing)
    {
        var mine = new[] { slug }.Concat(names.Values).Select(Stem).Where(s => s.Length > 0).ToList();
        foreach (var c in existing)
        {
            var theirs = new List<string> { Stem(c.Slug) };
            if (c.NameJson is { Length: > 0 })
                try { theirs.AddRange(JsonSerializer.Deserialize<Dictionary<string, string>>(c.NameJson)!.Values.Select(Stem)); }
                catch (JsonException) { }
            foreach (var a in mine)
                foreach (var b in theirs.Where(x => x.Length > 0))
                    if (a == b || (Math.Min(a.Length, b.Length) >= 4 && (a.Contains(b) || b.Contains(a))) ||
                        (Math.Min(a.Length, b.Length) >= 5 && Distance(a, b) <= 2))
                        return c;
        }
        return null;
    }

    /// <summary>
    /// Checks the manifest's category (and proposal) and reports findings.
    /// Returns the proposal names when a new category would be created.
    /// </summary>
    public async Task<Dictionary<string, string>?> CheckAsync(ValidationReport report, JsonElement root, string packageId)
    {
        string? slug = root.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()?.Trim() : null;
        if (string.IsNullOrEmpty(slug))
        {
            report.Error("CATEGORY_MISSING", "Manifest field 'category' is not set.",
                "Pick an existing category from GET /api/categories (e.g. \"signing\", \"conversion\"). Only if none fits, propose a new high-level one, see the agent guide.");
            return null;
        }
        if (!IsValidSlug(slug))
        {
            report.Error("CATEGORY_INVALID", $"Category '{slug}' is not a valid slug.",
                "Use 3 to 24 lower-case letters or hyphens, e.g. \"signing\" or \"data-capture\".");
            return null;
        }

        var all = await AllAsync();
        if (all.Any(x => x.Slug == slug)) return null;

        var list = string.Join(", ", all.Select(x => x.Slug));
        if (!root.TryGetProperty("categoryProposal", out var prop) || prop.ValueKind != JsonValueKind.Object)
        {
            report.Error("CATEGORY_UNKNOWN", $"Category '{slug}' does not exist.",
                $"Use one of: {list}. If none of them fits, add \"categoryProposal\": {{\"name\": {{...all 16 languages...}}}} to propose '{slug}' as a new high-level category.");
            return null;
        }

        var names = new Dictionary<string, string>();
        if (prop.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.Object)
            foreach (var p in n.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString()))
                    names[p.Name] = p.Value.GetString()!.Trim();

        bool ok = true;
        var missing = PackageValidator.RequiredLanguages.Where(l => !names.ContainsKey(l) && !(l == "nb" && names.ContainsKey("no"))).ToList();
        if (missing.Count > 0)
        {
            ok = false;
            report.Error("CATEGORY_PROPOSAL_INVALID", $"The proposed category name is missing these languages: {string.Join(", ", missing)}.",
                "Give categoryProposal.name in all 16 languages.");
        }
        var tooLong = names.Where(kv => kv.Value.Length > MaxNameLength || kv.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 2).Select(kv => kv.Key).ToList();
        if (tooLong.Count > 0)
        {
            ok = false;
            report.Error("CATEGORY_PROPOSAL_INVALID", $"The proposed category name is too long in: {string.Join(", ", tooLong)}.",
                $"Categories are broad areas: one or two words, at most {MaxNameLength} characters (e.g. \"Signing\", \"Data capture\").");
        }

        var idParts = packageId.Split('.', '-');
        var pkgName = root.TryGetProperty("name", out var nm) ? CatalogUi.LangText(root, "name", "en") ?? "" : "";
        if (idParts.Contains(slug) || (pkgName.Length > 0 && Stem(pkgName) == Stem(slug)))
        {
            ok = false;
            report.Error("CATEGORY_TOO_SPECIFIC", $"Category '{slug}' is named after this plug-in.",
                $"A category groups many plug-ins by function. Use one of: {list}, or propose a broader area.");
        }

        if (TooSimilar(slug, names, all) is { } close)
        {
            ok = false;
            report.Error("CATEGORY_TOO_SIMILAR", $"Category '{slug}' is too close to the existing category '{close.Slug}'.",
                $"Use \"category\": \"{close.Slug}\" instead.");
        }

        var max = await MaxAsync();
        if (all.Count >= max)
        {
            ok = false;
            report.Error("CATEGORY_LIMIT_REACHED", $"The store already has {all.Count} categories (limit {max}).",
                $"Use one of: {list}. An admin can raise the limit or merge categories.");
        }

        if (!ok) return null;
        report.Info("CATEGORY_NEW", $"Category '{slug}' is new and will be created when the package is submitted; admins are notified and may merge it later.");
        return names;
    }

    /// <summary>Creates a proposed category at submission time (limit re-checked).</summary>
    public async Task CreateAsync(string slug, Dictionary<string, string> names, AppUser user, string packageId)
    {
        if (await _db.Categories.AnyAsync(c => c.Slug == slug)) return;
        if (await _db.Categories.CountAsync() >= await MaxAsync()) return;
        _db.Categories.Add(new Category { Slug = slug, NameJson = JsonSerializer.Serialize(names), CreatedBy = user.DisplayName });
        await _db.SaveChangesAsync();
        await _audit.LogAsync(user.DisplayName, "category.created", slug, $"proposed with {packageId}; en: {names.GetValueOrDefault("en")}");
        await _notify.NotifyStaffAsync("CategoryCreated", $"[Add-on Store] New category: {slug}",
            $"<p>A new catalog category <b>{System.Net.WebUtility.HtmlEncode(slug)}</b> " +
            $"(\"{System.Net.WebUtility.HtmlEncode(names.GetValueOrDefault("en") ?? slug)}\") was created with the submission of <b>{packageId}</b> " +
            $"by {System.Net.WebUtility.HtmlEncode(user.DisplayName)}.</p><p>Admins can merge or delete categories under Settings.</p>");
    }

    /// <summary>Seeds the eight built-in categories with names taken from the resource files.</summary>
    public static async Task SeedAsync(AppDbContext db, IStringLocalizer localizer)
    {
        var saved = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var (slug, label) in Builtin)
            {
                var row = await db.Categories.FirstOrDefaultAsync(c => c.Slug == slug);
                if (row is not null && row.NameJson is not null) continue;
                var names = new Dictionary<string, string>();
                foreach (var lang in PackageValidator.RequiredLanguages)
                {
                    CultureInfo.CurrentUICulture = new CultureInfo(lang);
                    names[lang] = localizer[label].Value;
                }
                if (row is null)
                    db.Categories.Add(new Category { Slug = slug, NameJson = JsonSerializer.Serialize(names), Builtin = true, CreatedBy = "system" });
                else
                    row.NameJson = JsonSerializer.Serialize(names);
            }
            await db.SaveChangesAsync();
        }
        finally { CultureInfo.CurrentUICulture = saved; }
    }

    /// <summary>Admin: moves every package of 'from' to 'to' (catalog override) and deletes 'from'.</summary>
    public async Task<string?> MergeAsync(string from, string to, AppUser admin)
    {
        if (from == to) return null;
        var src = await _db.Categories.FirstOrDefaultAsync(c => c.Slug == from);
        var dst = await _db.Categories.FirstOrDefaultAsync(c => c.Slug == to);
        if (src is null || dst is null || src.Builtin) return null;
        var moved = 0;
        foreach (var pkg in await _db.Packages.Include(p => p.Versions).ToListAsync())
            if (EffectiveSlug(pkg) == from) { pkg.CategoryOverride = to; moved++; }
        _db.Categories.Remove(src);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(admin.DisplayName, "category.merged", from, $"into {to}; packages moved: {moved}");
        return "Category merged.";
    }

    public async Task<string?> DeleteAsync(string slug, AppUser admin)
    {
        var c = await _db.Categories.FirstOrDefaultAsync(x => x.Slug == slug);
        if (c is null || c.Builtin) return null;
        if ((await _db.Packages.Include(p => p.Versions).ToListAsync()).Any(p => EffectiveSlug(p) == slug)) return null;
        _db.Categories.Remove(c);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(admin.DisplayName, "category.deleted", slug, "");
        return "Category deleted.";
    }

    /// <summary>Category of a package: server override first, then the newest version's manifest.</summary>
    public static string EffectiveSlug(Package pkg)
    {
        if (!string.IsNullOrWhiteSpace(pkg.CategoryOverride)) return pkg.CategoryOverride!;
        var shown = pkg.Versions.Where(v => v.Status is VersionStatus.Live or VersionStatus.Beta).ToList();
        var newest = (shown.Count > 0 ? shown : pkg.Versions.ToList()).OrderByDescending(v => v.SubmittedAt).FirstOrDefault();
        if (newest is null) return "other";
        try
        {
            using var doc = JsonDocument.Parse(newest.ManifestJson);
            return doc.RootElement.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "other" : "other";
        }
        catch (JsonException) { return "other"; }
    }
}
