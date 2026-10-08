using System.Text.Json;
using System.Text.RegularExpressions;
using AddonStore.Web.Data;
using AddonStore.Web.Validation;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Services;

public record MetaIssue(string Code, string Severity, string Message, string Hint);

/// <summary>
/// One requested change of a package's catalog entry. A field is only touched
/// when its Set flag is true; a null value with Set = true resets the field to
/// the value from the newest manifest.
/// </summary>
public class MetaChange
{
    public bool SetName, SetDescription, SetAuthor, SetContact, SetCategory, SetVisibility;
    public string? Category;
    public string? Visibility;
    public Dictionary<string, string>? Name;
    public Dictionary<string, string>? Description;
    public string? Author;
    public string? ContactEmail;
}

/// <summary>
/// The catalog entry (name, description, author, contact) can be corrected on
/// the server without uploading a new version. The package files and their
/// manifests stay untouched; catalog, web UI and Power PDF client show the
/// edited values.
/// </summary>
public class PackageMetaService
{
    public const int MaxName = 80, MaxDescription = 2000, MaxAuthor = 100;

    private static readonly HashSet<string> KnownLangs =
        new(PackageValidator.AllLanguages.Append("no"), StringComparer.Ordinal);

    private readonly AppDbContext _db;
    private readonly AuditService _audit;
    private readonly NotificationService _notify;

    public PackageMetaService(AppDbContext db, AuditService audit, NotificationService notify)
    {
        _db = db; _audit = audit; _notify = notify;
    }

    public static List<MetaIssue> Validate(MetaChange c)
    {
        var issues = new List<MetaIssue>();
        if (c.SetName && c.Name is not null)
        {
            if (!c.Name.TryGetValue("en", out var en) || string.IsNullOrWhiteSpace(en))
                issues.Add(new("NAME_INVALID", "error", "The name needs at least an English entry (\"en\").",
                    "Send name as an object, e.g. {\"en\": \"Smart Bookmarks\", \"de\": \"Smart Bookmarks\"}."));
            // every language should have its entry (S1.4.0), as for an upload
            var nameMissing = PackageValidator.RequiredLanguages.Where(l => !c.Name.ContainsKey(l) && !(l == "nb" && c.Name.ContainsKey("no"))).ToList();
            if (nameMissing.Count > 0)
                issues.Add(new("NAME_NOT_LOCALIZED", "warning", $"The name is missing languages: {string.Join(", ", nameMissing)}; the catalog shows the English name there.",
                    "Give the name an entry for every language; a product name may read the same in every language."));
            foreach (var (lang, text) in c.Name)
            {
                if (!KnownLangs.Contains(lang))
                    issues.Add(new("NAME_INVALID", "error", $"Unknown language code '{lang}' in name.",
                        $"Use the codes {string.Join(", ", PackageValidator.AllLanguages)}."));
                else if (text.Length > MaxName)
                    issues.Add(new("NAME_INVALID", "error", $"The {lang} name is longer than {MaxName} characters.",
                        "Keep the name short; the description carries the details."));
            }
        }
        if (c.SetDescription && c.Description is not null)
        {
            bool Has(string l) => c.Description.TryGetValue(l, out var t) && !string.IsNullOrWhiteSpace(t);
            var missing = PackageValidator.RequiredLanguages.Where(l => !Has(l) && !(l == "nb" && Has("no"))).ToList();
            if (missing.Count > 0)
                issues.Add(new("LANG_TEXT_INCOMPLETE", "error", $"The description is missing these languages: {string.Join(", ", missing)}.",
                    "Provide the description in all 16 European languages; translate it yourself."));
            foreach (var (lang, text) in c.Description)
            {
                if (!KnownLangs.Contains(lang))
                    issues.Add(new("LANG_TEXT_INCOMPLETE", "error", $"Unknown language code '{lang}' in description.",
                        $"Use the codes {string.Join(", ", PackageValidator.AllLanguages)}."));
                else if (text.Length > MaxDescription)
                    issues.Add(new("DESCRIPTION_TOO_LONG", "error", $"The {lang} description is longer than {MaxDescription} characters.",
                        "Shorten it; put long documentation into docs/ inside the package."));
            }
        }
        if (c.SetAuthor && c.Author is not null && c.Author.Length > MaxAuthor)
            issues.Add(new("AUTHOR_INVALID", "error", $"The author is longer than {MaxAuthor} characters.", "Use a person's or team's name."));
        if (c.SetContact && !string.IsNullOrEmpty(c.ContactEmail) && !System.Net.Mail.MailAddress.TryCreate(c.ContactEmail, out _))
            issues.Add(new("CONTACT_INVALID", "error", $"'{c.ContactEmail}' is not a valid email address.",
                "Use a reachable address such as team@example.com, or reset the field to fall back to the publishing account."));

        // control characters break the catalog TSV and the client's list (audit S1.3.1); the description may keep line breaks
        static bool Bad(string? s, bool lines) => s is not null && s.Any(ch =>
            (char.IsControl(ch) && !(lines && ch is '\n' or '\r')) || ch is '\u202A' or '\u202B' or '\u202C' or '\u202D' or '\u202E' or '\u2066' or '\u2067' or '\u2068' or '\u2069');
        if ((c.SetName && c.Name is not null && c.Name.Values.Any(t => Bad(t, false))) ||
            (c.SetDescription && c.Description is not null && c.Description.Values.Any(t => Bad(t, true))) ||
            (c.SetAuthor && Bad(c.Author, false)) || (c.SetContact && Bad(c.ContactEmail, false)))
            issues.Add(new("TEXT_CONTROL_CHARS", "error", "Name, author, contact or description contain control or text-direction characters.",
                "Remove tabs, control characters and bidi overrides; the description may contain line breaks."));

        var texts = new List<string>();
        if (c.SetName && c.Name is not null) texts.AddRange(c.Name.Values);
        if (c.SetDescription && c.Description is not null) texts.AddRange(c.Description.Values);
        var brands = PackageValidator.ForeignBrands
            .Where(b => texts.Any(t => Regex.IsMatch(t, $@"\b{Regex.Escape(b)}\b", RegexOptions.IgnoreCase))).ToList();
        if (brands.Count > 0)
            issues.Add(new("THIRDPARTY_TRADEMARK", "warning", $"Name or description mentions third-party brands: {string.Join(", ", brands)}.",
                "Do not use other companies' product names or trademarks in plugin names and catalog texts; describe the function instead."));
        return issues;
    }

    public async Task<List<MetaIssue>> ApplyAsync(Package pkg, AppUser actor, MetaChange c)
    {
        Normalize(c);
        var issues = Validate(c);
        if (c.SetCategory && !string.IsNullOrWhiteSpace(c.Category) && !await _db.Categories.AnyAsync(x => x.Slug == c.Category))
            issues.Add(new("CATEGORY_UNKNOWN", "error", $"Category '{c.Category}' does not exist.",
                "Use a slug from GET /api/categories. New categories are only created through a package upload with categoryProposal."));
        if (c.SetVisibility && (c.Visibility is not ("public" or "private") ||
                                (c.Visibility == "private" && pkg.Id == SubmissionService.ClientPackageId)))
            issues.Add(new("VISIBILITY_INVALID", "error", "visibility must be 'public' or 'private' (the store client is always public).",
                "Private add-ons appear only for customers with a delivery and code; see 'Customer deliveries' in the guide."));
        // A private add-on with its own ribbon tab stays private (S1.0.6): public add-ons share one tab.
        if (c.SetVisibility && c.Visibility == "public" && pkg.Visibility != "public")
        {
            // what clients get now or after review: the newest live, beta and waiting version (older
            // versions from before the shared-tab rule do not count; S1.6.0: waiting versions count too)
            var offered = (await _db.PackageVersions.AsNoTracking()
                    .Where(v => v.PackageId == pkg.Id && (v.Status == VersionStatus.Live || v.Status == VersionStatus.Beta ||
                                                          v.Status == VersionStatus.Submitted))
                    .Select(v => new { v.Version, v.Status, v.AtomNamespace }).ToListAsync())
                .GroupBy(v => v.Status)
                .Select(g => g.OrderByDescending(v => v.Version, new SemVerComparer()).First());
            var ownTab = offered.FirstOrDefault(v => PackageValidator.IsOwnTabNamespace(v.AtomNamespace ?? ""));
            if (ownTab is not null)
                issues.Add(new("VISIBILITY_OWN_TAB", "error",
                    $"Version {ownTab.Version} has its own ribbon tab '{PackageValidator.TabOf(ownTab.AtomNamespace!)}'; only private add-ons may have one.",
                    "Upload a version on the shared tab ('FeaturePack::...') and withdraw the own-tab versions, then switch to public."));
        }
        if (issues.Any(i => i.Severity == "error")) return issues;

        var changed = new List<string>();
        if (c.SetVisibility && pkg.Visibility != c.Visibility) { pkg.Visibility = c.Visibility!; changed.Add($"visibility {pkg.Visibility}"); }
        if (c.SetName) { pkg.NameJson = c.Name is null ? null : JsonSerializer.Serialize(c.Name); changed.Add(c.Name is null ? "name reset" : "name"); }
        if (c.SetDescription) { pkg.DescriptionJson = c.Description is null ? null : JsonSerializer.Serialize(c.Description); changed.Add(c.Description is null ? "description reset" : "description"); }
        if (c.SetAuthor) { pkg.Author = string.IsNullOrWhiteSpace(c.Author) ? null : c.Author; changed.Add(pkg.Author is null ? "author reset" : $"author '{pkg.Author}'"); }
        if (c.SetContact) { pkg.ContactEmail = string.IsNullOrWhiteSpace(c.ContactEmail) ? null : c.ContactEmail; changed.Add(pkg.ContactEmail is null ? "contact reset" : $"contact '{pkg.ContactEmail}'"); }
        if (c.SetCategory) { pkg.CategoryOverride = string.IsNullOrWhiteSpace(c.Category) ? null : c.Category!.Trim(); changed.Add(pkg.CategoryOverride is null ? "category reset" : $"category '{pkg.CategoryOverride}'"); }
        if (changed.Count == 0) return issues;

        pkg.MetaUpdatedAt = DateTime.UtcNow;
        pkg.MetaUpdatedBy = actor.DisplayName;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(actor.DisplayName, "package.catalog.updated", pkg.Id, string.Join("; ", changed));
        if (pkg.OwnerId != actor.Id && await _db.Users.FindAsync(pkg.OwnerId) is AppUser owner)
            await _notify.NotifyUserAsync("CatalogChange", owner, $"[Add-on Store] Catalog entry of {pkg.Id} changed",
                $"<p><b>{System.Net.WebUtility.HtmlEncode(actor.DisplayName)}</b> changed the catalog entry of <b>{pkg.Id}</b>: " +
                $"{System.Net.WebUtility.HtmlEncode(string.Join("; ", changed))}.</p>" + await _notify.PluginLinkAsync(pkg.Id));
        return issues;
    }

    private static void Normalize(MetaChange c)
    {
        static string Clean(string s) => new string(s.Where(ch => !char.IsControl(ch) || ch == '\n').ToArray()).Trim();
        static Dictionary<string, string>? CleanMap(Dictionary<string, string>? m) =>
            m?.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
              .GroupBy(kv => kv.Key.Trim().ToLowerInvariant()).ToDictionary(g => g.Key, g => Clean(g.Last().Value));
        c.Name = CleanMap(c.Name);
        c.Description = CleanMap(c.Description);
        if (c.Author is not null) c.Author = Clean(c.Author).Replace('\n', ' ');
        if (c.ContactEmail is not null) c.ContactEmail = c.ContactEmail.Trim();
    }

    /// <summary>Current values for an edit form: catalog entry first, then the newest manifest.</summary>
    public static (Dictionary<string, string> Name, Dictionary<string, string> Description, string Author, string Contact)
        Current(Package pkg, PackageVersion? newest)
    {
        Dictionary<string, string> Map(string? overrideJson, string field)
        {
            var json = overrideJson;
            if (string.IsNullOrWhiteSpace(json) && newest is not null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(newest.ManifestJson);
                    if (doc.RootElement.TryGetProperty(field, out var el))
                        json = el.ValueKind == JsonValueKind.String ? JsonSerializer.Serialize(new { en = el.GetString() }) : el.GetRawText();
                }
                catch (JsonException) { }
            }
            try
            {
                return string.IsNullOrWhiteSpace(json) ? new()
                    : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!
                        .Where(kv => kv.Value.ValueKind == JsonValueKind.String)
                        .ToDictionary(kv => kv.Key, kv => kv.Value.GetString() ?? "");
            }
            catch (JsonException) { return new(); }
        }

        string author = pkg.Author ?? "", contact = pkg.ContactEmail ?? "";
        if (newest is not null && (author.Length == 0 || contact.Length == 0))
        {
            try
            {
                using var doc = JsonDocument.Parse(newest.ManifestJson);
                if (author.Length == 0 && doc.RootElement.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.String) author = a.GetString() ?? "";
                if (contact.Length == 0 && doc.RootElement.TryGetProperty("contactEmail", out var e) && e.ValueKind == JsonValueKind.String) contact = e.GetString() ?? "";
            }
            catch (JsonException) { }
        }
        return (Map(pkg.NameJson, "name"), Map(pkg.DescriptionJson, "description"), author, contact);
    }
}
