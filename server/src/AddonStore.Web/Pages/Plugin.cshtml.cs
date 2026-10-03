using AddonStore.Web.Data;
using AddonStore.Web.Services;
using AddonStore.Web.Validation;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Pages;

/// <summary>One plug-in: catalog data, all versions and every action an owner, reviewer or admin may take.</summary>
public class PluginModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly VersionActionService _actions;

    public Package? Pkg { get; private set; }
    public AppUser? Me { get; private set; }
    public List<PackageVersion> Versions { get; private set; } = new();
    public CatalogItem? InCatalog { get; private set; }
    public string Name { get; private set; } = "";
    public string Description { get; private set; } = "";
    public bool IsAdmin { get; private set; }
    public bool IsOwner { get; private set; }
    public bool CanReview { get; private set; }
    public string? Notice { get; private set; }

    public PluginModel(AppDbContext db, UserManager<AppUser> users, VersionActionService actions)
    {
        _db = db; _users = users; _actions = actions;
    }

    private async Task<bool> LoadAsync(string id)
    {
        Me = await _users.GetUserAsync(User);
        Pkg = await _db.Packages.Include(p => p.Owner).Include(p => p.Versions)
            .FirstOrDefaultAsync(p => p.Id == id);
        if (Me is null || Pkg is null) return false;
        IsAdmin = User.IsInRole("Admin");
        IsOwner = Pkg.OwnerId == Me.Id;
        CanReview = VersionActionService.CanReview(User);
        if (!IsOwner && !IsAdmin && !CanReview) return false;

        var cmp = new SemVerComparer();
        Versions = Pkg.Versions.OrderByDescending(v => v.Version, cmp).ThenByDescending(v => v.SubmittedAt).ToList();
        var lang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var shown = Versions.FirstOrDefault(v => v.Status == VersionStatus.Live)
                    ?? Versions.FirstOrDefault(v => v.Status == VersionStatus.Beta) ?? Versions.FirstOrDefault();
        Name = CatalogUi.DisplayName(Pkg, shown, lang);
        Description = CatalogUi.OverrideText(Pkg.DescriptionJson, lang)
                      ?? (shown is null ? "" : CatalogUi.ManifestText(shown, "description", lang));
        InCatalog = (await CatalogUi.GetAsync(_db, lang, includeBeta: true)).FirstOrDefault(c => c.Id == Pkg.Id);
        return true;
    }

    public async Task<IActionResult> OnGetAsync(string id) => await LoadAsync(id ?? "") ? Page() : Forbid();

    private async Task<IActionResult> ActAsync(string id, Func<Task<string?>> action)
    {
        if (!await LoadAsync(id ?? "")) return Forbid();
        Notice = await action() ?? "This action is not allowed for this version.";
        await LoadAsync(id!);
        return Page();
    }

    public Task<IActionResult> OnPostApproveAsync(string id, int versionId) =>
        ActAsync(id, () => CanReview ? _actions.DecideAsync(versionId, Me!, true, null) : Task.FromResult<string?>(null));

    public Task<IActionResult> OnPostRejectAsync(string id, int versionId, string comment) =>
        ActAsync(id, () => CanReview ? _actions.DecideAsync(versionId, Me!, false, comment) : Task.FromResult<string?>(null));

    public Task<IActionResult> OnPostWithdrawAsync(string id, int versionId) =>
        ActAsync(id, () => _actions.WithdrawAsync(versionId, Me!, IsAdmin));

    public Task<IActionResult> OnPostRestoreAsync(string id, int versionId) =>
        ActAsync(id, () => _actions.RestoreAsync(versionId, Me!, IsAdmin));

    public Task<IActionResult> OnPostWithdrawAllAsync(string id) =>
        ActAsync(id, () => _actions.WithdrawPackageAsync(id, Me!, IsAdmin));
}
