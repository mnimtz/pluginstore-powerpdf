using AddonStore.Web.Data;
using AddonStore.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Pages.Admin;

public class ReviewModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly AuditService _audit;
    private readonly NotificationService _notify;

    public List<PackageVersion> Queue { get; private set; } = new();
    public List<PackageVersion> Live { get; private set; } = new();
    public string? Notice { get; private set; }

    public ReviewModel(AppDbContext db, UserManager<AppUser> users, AuditService audit, NotificationService notify)
    {
        _db = db; _users = users; _audit = audit; _notify = notify;
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task OnPostApproveAsync(int id) => await DecideAsync(id, approve: true, comment: null);

    public async Task OnPostRejectAsync(int id, string comment) => await DecideAsync(id, approve: false, comment);

    public async Task OnPostWithdrawAsync(int id)
    {
        var admin = await _users.GetUserAsync(User);
        var v = await _db.PackageVersions.FirstOrDefaultAsync(x => x.Id == id);
        if (v is not null && v.Status == VersionStatus.Live)
        {
            v.Status = VersionStatus.Withdrawn;
            await _db.SaveChangesAsync();
            await _audit.LogAsync(admin!.DisplayName, "version.withdrawn", $"{v.PackageId} {v.Version}", "by admin");
            Notice = "Version withdrawn.";
        }
        await LoadAsync();
    }

    private async Task DecideAsync(int id, bool approve, string? comment)
    {
        var admin = await _users.GetUserAsync(User);
        var v = await _db.PackageVersions.Include(x => x.Package).ThenInclude(p => p!.Owner)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (v is not null && v.Status == VersionStatus.Beta)
        {
            v.Status = approve ? VersionStatus.Live : VersionStatus.Rejected;
            v.ReviewedById = admin!.Id;
            v.ReviewedAt = DateTime.UtcNow;
            v.ReviewComment = comment;
            await _db.SaveChangesAsync();
            await _audit.LogAsync(admin.DisplayName,
                approve ? "version.approved" : "version.rejected",
                $"{v.PackageId} {v.Version}", comment ?? "");
            if (v.Package?.Owner is { } owner)
                await _notify.NotifyUserAsync(owner,
                    $"[Plugin-Store] {v.PackageId} {v.Version} {(approve ? "approved" : "rejected")}",
                    approve
                        ? $"<p>Your version <b>{v.PackageId} {v.Version}</b> was approved and is live for all users.</p>"
                        : $"<p>Your version <b>{v.PackageId} {v.Version}</b> was rejected.</p><p>Reason: {System.Net.WebUtility.HtmlEncode(comment ?? "-")}</p>");
            Notice = approve ? "Version approved and live." : "Version rejected.";
        }
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        Queue = await _db.PackageVersions.Where(v => v.Status == VersionStatus.Beta)
            .OrderBy(v => v.SubmittedAt).ToListAsync();
        Live = await _db.PackageVersions.Where(v => v.Status == VersionStatus.Live)
            .OrderBy(v => v.PackageId).ThenByDescending(v => v.SubmittedAt).ToListAsync();
    }
}
