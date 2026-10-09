using AddonStore.Web.Data;
using AddonStore.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Pages;

/// <summary>
/// Delivery templates (S1.13.0): named sets of add-ons that customers get together. Linked: a change reaches every
/// customer that has the template. Shared by all developers and admins; the creator and admins edit a template.
/// </summary>
public class TemplatesModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly TemplateService _templates;
    private readonly CustomerService _customers;

    public TemplatesModel(AppDbContext db, UserManager<AppUser> users, TemplateService templates, CustomerService customers)
    {
        _db = db; _users = users; _templates = templates; _customers = customers;
    }

    public List<TemplateService.View> List { get; private set; } = new();
    public TemplateService.View? Edit { get; private set; }
    public bool CanEdit { get; private set; }
    public bool CanCreate { get; private set; }
    /// <summary>Add-ons for the add-on choice (S1.14.0); Present = in the shown template already.</summary>
    public List<AddonPicker.Item> PickItems { get; private set; } = new();
    public Dictionary<string, string> PackageNames { get; private set; } = new();
    public List<Customer> UsedBy { get; private set; } = new();
    [BindProperty(SupportsGet = true)] public int? Id { get; set; }
    public string? Notice { get; private set; }
    public string NoticeKind { get; private set; } = "ok";

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostCreateAsync(string? name, string? description, bool restrictCatalog)
    {
        var me = await _users.GetUserAsync(User);
        if (me is null || (User.IsInRole("Reviewer") && !User.IsInRole("Admin"))) return Forbid();
        if (TemplateService.CheckName(name, description) is not null) { Notice = "Enter a name of 1 to 80 characters."; NoticeKind = "error"; await LoadAsync(); return Page(); }
        var t = await _templates.CreateAsync(name!, description, restrictCatalog, new List<TemplateService.ItemInput>(), me);
        return RedirectToPage("/Templates", new { id = t.Id });
    }

    public Task<IActionResult> OnPostSaveAsync(string? name, string? description, bool restrictCatalog) =>
        ActAsync(async (t, me) =>
        {
            if (TemplateService.CheckName(name, description) is not null) return "Enter a name of 1 to 80 characters.";
            await _templates.UpdateAsync(t, name, description ?? "", restrictCatalog, null, me);
            return null;
        }, "Template saved.");

    public Task<IActionResult> OnPostAddAsync(string? packageId, string? version)
    {
        // S1.14.0: several from the add-on choice; packageId + version: one (the form before, scripts)
        var picked = AddonPicker.Selection(Request.Form);
        if (picked.Count == 0)
            return ActAsync(async (t, me) => await _templates.AddItemAsync(t, packageId, version, me), "Add-on added. Every customer with this template gets it.");
        return ActAsync(async (t, me) => await _templates.AddItemsAsync(t, picked.Select(p => new TemplateService.ItemInput(p.Id, p.Version)).ToList(), me),
            picked.Count == 1 ? "Add-on added. Every customer with this template gets it." : "Add-ons added. Every customer with this template gets them.");
    }

    public Task<IActionResult> OnPostRemoveAsync(int itemId) =>
        ActAsync(async (t, me) => { await _templates.RemoveItemAsync(t, itemId, me); return null; }, "Add-on removed from the template.");

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var me = await _users.GetUserAsync(User);
        var t = Id is null ? null : await _db.DeliveryTemplates.FirstOrDefaultAsync(x => x.Id == Id);
        if (me is null || t is null) return NotFound();
        if (!TemplateService.CanEdit(User, t, me.Id)) return Forbid();
        await _templates.DeleteAsync(t, me);
        Id = null;
        Notice = "Template deleted. The deliveries made from it ended.";
        await LoadAsync();
        return Page();
    }

    private async Task<IActionResult> ActAsync(Func<DeliveryTemplate, AppUser, Task<string?>> action, string ok)
    {
        var me = await _users.GetUserAsync(User);
        var t = Id is null ? null : await _db.DeliveryTemplates.FirstOrDefaultAsync(x => x.Id == Id);
        if (me is null || t is null) return NotFound();
        if (!TemplateService.CanEdit(User, t, me.Id)) return Forbid();
        var err = await action(t, me);
        Notice = err ?? ok; NoticeKind = err is null ? "ok" : "error";
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        _db.ChangeTracker.Clear();
        var me = await _users.GetUserAsync(User);
        CanCreate = me is not null && !(User.IsInRole("Reviewer") && !User.IsInRole("Admin"));
        List = await _templates.ListAsync();
        var lang = Lang.Current;
        Edit = Id is null ? null : List.FirstOrDefault(v => v.Template.Id == Id);
        // only approved versions can be delivered by a template
        var inTemplate = (Edit?.Items.Select(i => i.PackageId) ?? Enumerable.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        PickItems = await AddonPicker.BuildAsync(_db, lang, me?.Id, inTemplate, withWaiting: false);
        PackageNames = PickItems.ToDictionary(i => i.Id, i => i.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var pid in await _db.Packages.AsNoTracking().Select(p => p.Id).ToListAsync())
            PackageNames.TryAdd(pid, pid);
        CanEdit = Edit is not null && me is not null && TemplateService.CanEdit(User, Edit.Template, me.Id);
        if (Edit is not null)
        {
            var ids = await _db.CustomerTemplates.AsNoTracking().Where(a => a.TemplateId == Edit.Template.Id).Select(a => a.CustomerId).ToListAsync();
            var all = await _db.Customers.AsNoTracking().Where(c => ids.Contains(c.Id)).OrderBy(c => c.Name).ToListAsync();
            UsedBy = me is null ? new() : all.Where(c => CustomerService.CanSee(User, c, me.Id)).ToList();
        }
    }
}
