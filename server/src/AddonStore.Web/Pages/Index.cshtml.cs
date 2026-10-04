using System.Globalization;
using AddonStore.Web.Data;
using AddonStore.Web.Services;
using AddonStore.Web.Validation;
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
    private readonly AiService _ai;

    public IndexModel(AppDbContext db, AiService ai) { _db = db; _ai = ai; }

    public async Task OnGetAsync()
    {
        var ai = await _ai.ConfigAsync();
        AiSearch = ai.On && ai.Search;
        var culture = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
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

        Items = items.Where(i => i.Id != ClientPackageId).ToList();
        // Slugs over live and beta ids, the same set the add-on page resolves against.
        var all = (await CatalogUi.GetAsync(_db, culture, includeBeta: true))
            .Where(i => i.Id != ClientPackageId).Select(i => i.Id).ToList();
        foreach (var i in Items) Slugs[i.Id] = ShareService.Slug(i.Id, all);
    }
}
