using AddonStore.Web.Data;
using AddonStore.Web.Services;
using AddonStore.Web.Validation;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Pages;

public class DashboardModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly SubmissionService _svc;

    public List<Package> Packages { get; private set; } = new();
    public List<Finding> Findings { get; private set; } = new();
    public string? Notice { get; private set; }
    public string NoticeKind { get; private set; } = "ok";

    public DashboardModel(AppDbContext db, UserManager<AppUser> users, SubmissionService svc)
    {
        _db = db; _users = users; _svc = svc;
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task OnPostAsync(IFormFile? package)
    {
        var user = await _users.GetUserAsync(User);
        if (user is null || package is null || package.Length == 0)
        {
            Notice = "No package received.";
            NoticeKind = "error";
            await LoadAsync();
            return;
        }

        var tmp = Path.GetTempFileName();
        try
        {
            await using (var fs = System.IO.File.Create(tmp))
                await package.CopyToAsync(fs);

            var result = await _svc.SubmitAsync(tmp, user, "web");
            Findings = result.Report.Findings;
            if (result.ErrorCode == "CLIENT_ADMIN_ONLY")
            {
                Notice = "Only administrators can publish new versions of the Plugin-Store client.";
                NoticeKind = "error";
            }
            else if (result.ErrorCode == "VERSION_EXISTS")
            {
                Notice = result.ErrorHint;
                NoticeKind = "warn";
            }
            else if (result.Version is null)
            {
                Notice = "The package did not pass the automatic checks; nothing was stored. Fix the errors below and upload again.";
                NoticeKind = "error";
            }
            else if (result.Version.Status == VersionStatus.Live)
            {
                Notice = "Plugin-Store client released. Installed clients offer the update now.";
            }
            else
            {
                Notice = "Submitted. The version is in the beta channel now and awaits admin review.";
            }
        }
        finally
        {
            try { System.IO.File.Delete(tmp); } catch { }
        }
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var userId = _users.GetUserId(User);
        Packages = await _db.Packages
            .Where(p => p.OwnerId == userId)
            .Include(p => p.Versions)
            .OrderBy(p => p.Id)
            .ToListAsync();
        foreach (var p in Packages)
            p.Versions = p.Versions.OrderByDescending(v => v.SubmittedAt).ToList();
    }
}
