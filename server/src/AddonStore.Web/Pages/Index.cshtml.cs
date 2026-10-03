using System.Globalization;
using AddonStore.Web.Data;
using AddonStore.Web.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AddonStore.Web.Pages;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    public List<CatalogItem> Items { get; private set; } = new();

    public IndexModel(AppDbContext db) => _db = db;

    public async Task OnGetAsync() =>
        Items = await CatalogUi.GetAsync(_db, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
}
