using AddonStore.Web.Data;
using AddonStore.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Pages;

/// <summary>Audit dossier of one version (S1.3.0), printable; owner, admins and reviewers.</summary>
public class DossierModel : PageModel
{
    private readonly DossierService _dossiers;
    private readonly UserManager<AppUser> _users;
    private readonly AppDbContext _db;

    public DossierModel(DossierService dossiers, UserManager<AppUser> users, AppDbContext db) { _dossiers = dossiers; _users = users; _db = db; }

    public DossierService.Dossier? D { get; private set; }

    public async Task<IActionResult> OnGetAsync(string id, string version)
    {
        var user = await _users.GetUserAsync(User);
        if (user is null) return Challenge();
        var owner = await _db.Packages.AsNoTracking().Where(p => p.Id == id).Select(p => p.OwnerId).FirstOrDefaultAsync();
        if (owner is null) return NotFound();
        if (owner != user.Id && !User.IsInRole("Admin") && !VersionActionService.CanReview(User)) return NotFound();
        D = await _dossiers.BuildAsync(id, version, user);
        return D is null ? NotFound() : Page();
    }
}
