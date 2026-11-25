using AddonStore.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AddonStore.Web.Pages;

/// <summary>
/// S2.8.6: a customer's own catalog page at /{slug}, e.g. www.power-pdf.de/finance (Services/CustomerPageService.cs).
/// The route is a single free segment: every page and endpoint of the store with a fixed address wins over it.
/// </summary>
public class CustomerCatalogModel : PageModel
{
    private readonly CustomerPageService _pages;
    private readonly CustomerService _customers;
    private readonly Data.AppDbContext _db;

    public CustomerCatalogModel(CustomerPageService pages, CustomerService customers, Data.AppDbContext db)
    {
        _pages = pages; _customers = customers; _db = db;
    }

    public CustomerPageService.Page? View { get; private set; }
    public Dictionary<string, string> Slugs { get; } = new();
    public string? ClientVersion { get; private set; }

    // review S2.8.6: POST, PUT, DELETE to any free address: nothing here takes data
    public IActionResult OnPost() => NotFound();
    public IActionResult OnPut() => NotFound();
    public IActionResult OnDelete() => NotFound();

    public async Task<IActionResult> OnGetAsync(string slug)
    {
        var culture = Lang.Current;
        View = await _pages.ResolveAsync(slug, culture, _customers);
        if (View is null) return NotFound();
        var all = (await CatalogUi.GetAsync(_db, culture, includeBeta: true))
            .Where(i => i.Id != SubmissionService.ClientPackageId).Select(i => i.Id).ToList();
        foreach (var i in View.Items) Slugs[i.Id] = ShareService.Slug(i.Id, all);
        ClientVersion = (await CatalogUi.GetAsync(_db, culture, includeBeta: false))
            .FirstOrDefault(i => i.Id == SubmissionService.ClientPackageId)?.Version;
        return Page();
    }
}
