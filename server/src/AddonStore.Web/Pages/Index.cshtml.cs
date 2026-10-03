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

    public IndexModel(AppDbContext db) => _db = db;

    public async Task OnGetAsync()
    {
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
            ClientDownloadUrl = $"/api/packages/{ClientPackageId}/{client.Version}/download";
        }

        Items = items.Where(i => i.Id != ClientPackageId).ToList();
    }
}
