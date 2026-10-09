using System.Globalization;
using AddonStore.Web.Data;
using AddonStore.Web.Services;
using AddonStore.Web.Validation;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Pages;

public class IndexModel : PageModel
{
    /// <summary>The store client's own package id (dogfooding: it updates through the store).</summary>
    public const string ClientPackageId = "com.tungsten.pluginstore";

    private readonly AppDbContext _db;

    public List<CatalogItem> Items { get; private set; } = new();
    public string? ClientVersion { get; private set; }
    public string? ClientDownloadUrl { get; private set; }
    /// <summary>Short page name per add-on for the share links (/a/{slug}).</summary>
    public Dictionary<string, string> Slugs { get; } = new();
    public bool AiSearch { get; private set; }
    /// <summary>Admins mark highlights right on the tiles (S1.16.0).</summary>
    public bool IsAdmin { get; private set; }
    public int HighlightCount { get; private set; }
    /// <summary>Result of the last highlight change (?hl=), shown above the catalog.</summary>
    public string? Notice { get; private set; }
    public bool NoticeError { get; private set; }
    private readonly AiService _ai;
    private readonly UserManager<AppUser> _users;
    private readonly AuditService _audit;

    public IndexModel(AppDbContext db, AiService ai, UserManager<AppUser> users, AuditService audit)
    {
        _db = db; _ai = ai; _users = users; _audit = audit;
    }

    private static readonly Dictionary<string, (string Text, bool Error)> Notes = new()
    {
        ["on"] = ("Marked as highlight. It is shown first now, with a glow.", false),
        ["off"] = ("Highlight removed.", false),
        ["max"] = ("At most 9 add-ons can be highlights.", true),
        ["private"] = ("Only public add-ons can be highlights.", true),
    };

    public async Task<IActionResult> OnPostHighlightAsync(string id, bool on)
    {
        var me = await _users.GetUserAsync(User);
        if (me is null || !User.IsInRole("Admin")) return Forbid();
        var pkg = await _db.Packages.FirstOrDefaultAsync(p => p.Id == id);
        if (pkg is null) return NotFound();
        var err = await Highlights.CheckAsync(_db, pkg, on);
        if (err is null) await Highlights.SetAsync(_db, _audit, pkg, on, me);
        var key = err is null ? (on ? "on" : "off") : err.StartsWith("At most") ? "max" : "private";
        return Redirect("/?hl=" + key + "#pkg-grid");
    }

    public async Task OnGetAsync()
    {
        IsAdmin = User.IsInRole("Admin");
        if (Request.Query["hl"].ToString() is { Length: > 0 } hl && Notes.TryGetValue(hl, out var note))
        {
            Notice = note.Text; NoticeError = note.Error;
        }
        var ai = await _ai.ConfigAsync();
        AiSearch = ai.On && ai.Search;
        var culture = AddonStore.Web.Services.Lang.Current;
        var items = await CatalogUi.GetAsync(_db, culture, includeBeta: false);

        // The client add-on gets its own download box in the hero instead of a
        // catalog card; during the bootstrap phase a beta version counts too.
        var client = items.FirstOrDefault(i => i.Id == ClientPackageId);
        if (client is null)
        {
            var beta = await CatalogUi.GetAsync(_db, culture, includeBeta: true);
            client = beta.FirstOrDefault(i => i.Id == ClientPackageId);
        }
        if (client is not null)
        {
            ClientVersion = client.Version;
            ClientDownloadUrl = "/download/pluginstore.msi";
        }

        // highlights first (in the order they were marked), so on the first page; the rest as before (S1.16.0)
        Items = items.Where(i => i.Id != ClientPackageId)
            .OrderBy(i => i.FeaturedAt is null).ThenBy(i => i.FeaturedAt).ToList();
        HighlightCount = Items.Count(i => i.Featured);
        // Slugs over live and beta ids, the same set the add-on page resolves against.
        var all = (await CatalogUi.GetAsync(_db, culture, includeBeta: true))
            .Where(i => i.Id != ClientPackageId).Select(i => i.Id).ToList();
        foreach (var i in Items) Slugs[i.Id] = ShareService.Slug(i.Id, all);
    }
}
