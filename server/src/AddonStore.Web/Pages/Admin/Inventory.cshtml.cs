using AddonStore.Web.Data;
using AddonStore.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AddonStore.Web.Pages.Admin;

/// <summary>
/// Existing customers (S1.10.0): the companies whose store clients report, with their installations,
/// Power PDF and store versions and add-ons. The company name of a domain is found automatically and
/// can be corrected here. Admins only; empty unless the evaluation is switched on (Settings, data protection).
/// </summary>
public class InventoryModel : PageModel
{
    private readonly InventoryService _inventory;
    private readonly UserManager<AppUser> _users;

    public InventoryModel(InventoryService inventory, UserManager<AppUser> users) { _inventory = inventory; _users = users; }

    public bool On { get; private set; }
    public List<InventoryService.CompanyRow> Rows { get; private set; } = new();
    public List<InventoryService.CompanyRow> Visible { get; private set; } = new();
    public InventoryService.CompanyRow? Detail { get; private set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? Company { get; set; }
    public string? Notice { get; private set; }
    public string NoticeKind { get; private set; } = "ok";

    // switched off: the area does not exist (no navigation entry), the settings card switches it on
    public async Task<IActionResult> OnGetAsync()
    {
        if (!await _inventory.EnabledAsync()) return Redirect("/Admin/Settings?view=privacy#inventory");
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostRenameAsync(string domain, string name)
    {
        if (!await _inventory.EnabledAsync()) return Redirect("/Admin/Settings?view=privacy#inventory");
        var me = await _users.GetUserAsync(User);
        var err = await _inventory.CorrectAsync(domain, name, me!.DisplayName);
        Notice = err ?? "Company name saved.";
        NoticeKind = err is null ? "ok" : "error";
        if (err is null) Company = name.Trim();
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostResolveAsync(string domain)
    {
        if (!await _inventory.EnabledAsync()) return Redirect("/Admin/Settings?view=privacy#inventory");
        var me = await _users.GetUserAsync(User);
        await _inventory.ResolveAgainAsync(domain, me!.DisplayName);
        Notice = "The company was looked up again.";
        Company = null;
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string domain)
    {
        if (!await _inventory.EnabledAsync()) return Redirect("/Admin/Settings?view=privacy#inventory");
        var me = await _users.GetUserAsync(User);
        await _inventory.DeleteDomainAsync(domain, me!.DisplayName);
        Notice = "The data of this domain was deleted.";
        Company = null;
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        On = await _inventory.EnabledAsync();
        Rows = await _inventory.CompaniesAsync(Lang.Current);
        var q = (Q ?? "").Trim();
        Visible = q.Length == 0 ? Rows : Rows.Where(r =>
            r.Company.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            r.Domains.Any(d => d.Domain.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
            r.Addons.Any(a => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || a.Id.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
        Detail = Company is null ? null : Rows.FirstOrDefault(r => r.Company == Company);
    }
}
