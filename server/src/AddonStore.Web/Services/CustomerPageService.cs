using System.Text.RegularExpressions;
using AddonStore.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Services;

/// <summary>
/// S2.8.6: a customer's own catalog page, e.g. www.power-pdf.de/finance: logo, the customer's name as heading, an
/// optional intro and only the PUBLIC add-ons of its current deliveries. Off by default and set up by admins only,
/// so neither a real customer's name nor its selection becomes public by accident. The page names the customer's code
/// only when it is a code of its own (e.g. FINANCE) and the admin allows it; a generated code is never shown.
/// </summary>
public class CustomerPageService
{
    public const int MaxIntro = 600, MaxLogoBytes = 1024 * 1024;
    /// <summary>Folder of the logos, fixed at start-up from the data folder (Program.cs), never from a request.</summary>
    public static string LogosDir { get; set; } = "";

    public static readonly Regex SlugRule = new(@"^[a-z0-9](?:[a-z0-9-]{0,38}[a-z0-9])?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// First path segments the store uses or may use itself: pages, API, files. A customer page never takes one of them
    /// (the store's own addresses win anyway; this keeps a later store page from colliding with a customer page).
    /// </summary>
    public static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "c", "account", "admin", "api", "app", "avatar", "avatars", "catalog", "catalogentry", "css", "customer", "customer-logo",
        "customers", "dashboard", "developer", "devkit", "disclaimer", "docs", "dossier", "download", "downloads", "error", "favicon",
        "fonts", "help", "home", "img", "images", "index", "insights", "issues", "js", "lib", "llms", "login", "logout", "logos",
        "maintenance", "plugin", "plugins", "privacy", "profile", "register", "reports", "robots", "sdk", "search", "sendto", "settings",
        "setup", "signin-oidc", "signout", "sitemap", "start", "static", "store", "support", "templates", "terms", "uat", "agent-guide",
        "set-lang", "health", "status", "ping", "addons", "add-ons", "impressum", "datenschutz", "legal", "about", "contact", "news",
    };

    private readonly AppDbContext _db;
    private readonly AuditService _audit;

    public CustomerPageService(AppDbContext db, AuditService audit) { _db = db; _audit = audit; }

    /// <summary>Null = fine, else the reason (lower-case letters, digits and hyphens, 2 to 40, not a store address).</summary>
    public static string? CheckSlug(string slug)
    {
        if (!SlugRule.IsMatch(slug) || slug.Length < 2) return "The page name has 2 to 40 lower-case letters, digits and hyphens, e.g. finance.";
        if (Reserved.Contains(slug)) return "This page name belongs to the store itself. Choose another one.";
        return null;
    }

    public record Outcome(bool Ok, string Message);

    /// <summary>Saves the page settings (admins). An empty slug removes the page address (and switches the page off).</summary>
    public async Task<Outcome> SaveAsync(Customer c, string? slug, bool enabled, bool showCode, string? intro, string actor)
    {
        var s = (slug ?? "").Trim().ToLowerInvariant();
        if (s.Length > 0 && CheckSlug(s) is { } why) return new(false, why);
        if (s.Length > 0 && await _db.Customers.AnyAsync(x => x.Id != c.Id && x.PageSlug == s))
            return new(false, "Another customer already has this page name.");
        var text = new string((intro ?? "").Where(ch => ch is '\n' || !char.IsControl(ch))
                                            .Where(ch => !(ch is >= '‪' and <= '‮') && !(ch is >= '⁦' and <= '⁩')).ToArray()).Trim();
        if (text.Length > MaxIntro) return new(false, "The intro has at most 600 characters.");
        c.PageSlug = s.Length > 0 ? s : null;
        c.PageEnabled = s.Length > 0 && enabled;
        c.PageShowCode = showCode;
        c.PageIntro = text.Length > 0 ? text : null;
        try { await _db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            return new(false, "Another customer already has this page name.");
        }
        await _audit.LogAsync(actor, "customer.page.updated", $"customer {c.Id}",
            $"page {(c.PageSlug is null ? "-" : "/" + c.PageSlug)}, {(c.PageEnabled ? "public" : "off")}, show code {c.PageShowCode}");
        return new(true, "Settings saved.");
    }

    /// <summary>A PNG or JPEG logo (checked by its first bytes, at most 1 MB), stored as data/logos/{id}.png|jpg.</summary>
    public async Task<Outcome> SaveLogoAsync(Customer c, byte[] data, string actor)
    {
        if (data.Length == 0 || data.Length > MaxLogoBytes) return new(false, "The logo is a PNG or JPEG file of at most 1 MB.");
        var ext = IsPng(data) ? ".png" : IsJpeg(data) ? ".jpg" : null;
        if (ext is null) return new(false, "The logo is a PNG or JPEG file of at most 1 MB.");
        if (LogosDir.Length == 0) return new(false, "The logo folder is not available.");
        Directory.CreateDirectory(LogosDir);
        var name = c.Id + ext;
        try
        {
            // written next to it first, then moved over: a page loading the old logo at this moment does not break the upload
            var tmp = Path.Combine(LogosDir, $".{c.Id}-{Guid.NewGuid():N}.tmp");
            await File.WriteAllBytesAsync(tmp, data);
            File.Move(tmp, Path.Combine(LogosDir, name), overwrite: true);
            var other = Path.Combine(LogosDir, c.Id + (ext == ".png" ? ".jpg" : ".png"));
            if (File.Exists(other)) { try { File.Delete(other); } catch (IOException) { } }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(false, "The logo could not be saved. Try again in a moment.");
        }
        c.LogoFile = name;
        c.LogoUpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(actor, "customer.logo.updated", $"customer {c.Id}", $"{ext[1..]}, {data.Length} bytes");
        return new(true, "Logo saved.");
    }

    public async Task RemoveLogoAsync(Customer c, string actor)
    {
        DeleteLogoFiles(c.Id);
        c.LogoFile = null;
        c.LogoUpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(actor, "customer.logo.removed", $"customer {c.Id}");
    }

    /// <summary>Removes the customer's logo files (also when the customer is deleted).</summary>
    public static void DeleteLogoFiles(int customerId)
    {
        if (LogosDir.Length == 0) return;
        foreach (var ext in new[] { ".png", ".jpg" })
        {
            var p = Path.Combine(LogosDir, customerId + ext);
            try { if (File.Exists(p)) File.Delete(p); } catch (IOException) { }
        }
    }

    /// <summary>The file of a logo the public may see: only of a customer whose page is on.</summary>
    public async Task<(string Path, string Type)?> PublicLogoAsync(int customerId)
    {
        var c = await _db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == customerId);
        if (c is null || !c.PageEnabled || c.Status != "active" || c.PageSlug is null || c.LogoFile is null || LogosDir.Length == 0) return null;
        // the path comes from the folder listing; the request only selects the customer
        var file = Directory.Exists(LogosDir)
            ? Directory.EnumerateFiles(LogosDir).FirstOrDefault(f => Path.GetFileName(f) == c.LogoFile)
            : null;
        return file is null ? null : (file, file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg");
    }

    public record Page(Customer Customer, List<CatalogItem> Items, string? Code);

    /// <summary>The public page of a slug, or null (unknown, off, or the customer paused).</summary>
    public async Task<Page?> ResolveAsync(string slug, string culture, CustomerService customers)
    {
        var s = (slug ?? "").Trim().ToLowerInvariant();
        if (!SlugRule.IsMatch(s) || Reserved.Contains(s)) return null;
        var c = await _db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.PageSlug == s);
        if (c is null || !c.PageEnabled || c.Status != "active") return null;
        var now = DateTime.UtcNow;
        var ids = (await _db.Deliveries.AsNoTracking()
                .Where(d => d.CustomerId == c.Id && d.Status == "active" && (d.StartsAt == null || d.StartsAt <= now) && (d.EndsAt == null || d.EndsAt > now))
                .Select(d => d.PackageId).ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // the public catalog only: private, blocked, suspended and unreleased add-ons are never on the page
        var items = (await CatalogUi.GetAsync(_db, culture, includeBeta: false))
            .Where(i => ids.Contains(i.Id) && i.Id != SubmissionService.ClientPackageId)
            .OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        string? code = null;
        if (c.PageShowCode)
        {
            var manual = await _db.CustomerCodes.AsNoTracking()
                .Where(x => x.CustomerId == c.Id && x.Manual && x.DeliveryId == null && x.RevokedAt == null && (x.ExpiresAt == null || x.ExpiresAt > now))
                .OrderByDescending(x => x.Id).FirstOrDefaultAsync();
            if (manual is not null) code = customers.Reveal(manual);
            if (code is not null && (code.EndsWith('…') || CustomerService.CheckManualCode(code) is not null)) code = null;   // never a prefix or another kind
        }
        return new Page(c, items, code);
    }

    private static bool IsPng(byte[] d) => d.Length > 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47 && d[4] == 0x0D && d[5] == 0x0A && d[6] == 0x1A && d[7] == 0x0A;
    private static bool IsJpeg(byte[] d) => d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF;
}
