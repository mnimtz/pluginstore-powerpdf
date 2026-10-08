using AddonStore.Web.Data;
using AddonStore.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Pages;

/// <summary>
/// Start page after signing in (S1.5.0 preview): one tile per area with the figures that
/// matter now, quick actions and the newest open problem reports. Each role sees its own
/// tiles; the figures come from the same services as the detail pages.
/// </summary>
public class StartModel : PageModel
{
    private readonly InsightsService _insights;
    private readonly IssueService _issues;
    private readonly UserManager<AppUser> _users;
    private readonly AppDbContext _db;

    public StartModel(InsightsService insights, IssueService issues, UserManager<AppUser> users, AppDbContext db)
    {
        _insights = insights; _issues = issues; _users = users; _db = db;
    }

    public AppUser? Me { get; private set; }
    public bool IsAdmin { get; private set; }
    public bool IsReviewer { get; private set; }
    public InsightsService.Result? R { get; private set; }
    public int ReviewQueue { get; private set; }
    public int Customers { get; private set; }
    public int AccessRequests { get; private set; }
    public List<Feedback> NewReports { get; private set; } = new();

    public async Task OnGetAsync()
    {
        Me = await _users.GetUserAsync(User);
        if (Me is null) return;
        IsAdmin = User.IsInRole("Admin");
        IsReviewer = IsAdmin || User.IsInRole("Reviewer");
        R = await _insights.ComputeAsync(Me, IsAdmin, 30, null, null, Lang.Current);
        if (IsReviewer)
            ReviewQueue = await _db.PackageVersions.CountAsync(v => v.Status == VersionStatus.Submitted && v.PackageId != SubmissionService.ClientPackageId &&
                                                                    !_db.Packages.Any(p => p.Id == v.PackageId && p.Visibility == "private"));
        Customers = IsAdmin || User.IsInRole("Reviewer")
            ? await _db.Customers.CountAsync()
            : await _db.Customers.CountAsync(c => c.OwnerId == Me.Id);
        if (IsAdmin) AccessRequests = await _db.Users.CountAsync(u => u.Status == UserStatus.Pending);
        NewReports = await _issues.Scope(Me, IsAdmin).Where(f => f.Status == "open")
            .OrderByDescending(f => f.Id).Take(5).AsNoTracking().ToListAsync();
    }
}
