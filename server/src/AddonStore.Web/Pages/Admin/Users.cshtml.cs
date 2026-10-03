using AddonStore.Web.Data;
using AddonStore.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Pages.Admin;

public class UsersModel : PageModel
{
    public record Row(AppUser User, string Role);

    private readonly AppDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly AuditService _audit;
    private readonly NotificationService _notify;

    public List<AppUser> Pending { get; private set; } = new();
    public List<Row> All { get; private set; } = new();
    public string? MyId { get; private set; }
    public string? Notice { get; private set; }

    public UsersModel(AppDbContext db, UserManager<AppUser> users, AuditService audit, NotificationService notify)
    {
        _db = db; _users = users; _audit = audit; _notify = notify;
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task OnPostApproveAsync(string id) => await SetStatusAsync(id, UserStatus.Active,
        "user.approved", "Account approved.",
        "[Plugin-Store] Access approved",
        "<p>Your access request was approved. You can sign in and publish plugins now.</p>");

    public async Task OnPostRejectAsync(string id) => await SetStatusAsync(id, UserStatus.Rejected,
        "user.rejected", "Request rejected.",
        "[Plugin-Store] Access request declined",
        "<p>Your access request to the Plugin-Store was declined. Contact an administrator for details.</p>");

    public async Task OnPostDisableAsync(string id) => await SetStatusAsync(id, UserStatus.Disabled,
        "user.disabled", "Account disabled.", null, null);

    public async Task OnPostEnableAsync(string id) => await SetStatusAsync(id, UserStatus.Active,
        "user.enabled", "Account enabled.", null, null);

    private async Task SetStatusAsync(string id, UserStatus status, string action, string notice,
        string? mailSubject, string? mailBody)
    {
        var admin = await _users.GetUserAsync(User);
        var user = await _users.FindByIdAsync(id);
        if (user is not null && user.Id != admin!.Id)
        {
            user.Status = status;
            await _users.UpdateAsync(user);
            await _audit.LogAsync(admin.DisplayName, action, user.Email ?? user.Id);
            if (mailSubject is not null)
                await _notify.NotifyUserAsync(user, mailSubject, mailBody!);
            Notice = notice;
        }
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        MyId = _users.GetUserId(User);
        var all = await _db.Users.OrderBy(u => u.DisplayName).ToListAsync();
        Pending = all.Where(u => u.Status == UserStatus.Pending).ToList();
        All = new List<Row>();
        foreach (var u in all)
        {
            var roles = await _users.GetRolesAsync(u);
            All.Add(new Row(u, roles.Contains("Admin") ? "Admin" : "User"));
        }
    }
}
