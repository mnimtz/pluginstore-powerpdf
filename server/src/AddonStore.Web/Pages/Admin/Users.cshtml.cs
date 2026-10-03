using System.Security.Cryptography;
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

    public static readonly string[] Roles = { "User", "Reviewer", "Admin" };

    private readonly AppDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly AuditService _audit;
    private readonly NotificationService _notify;

    public List<AppUser> Pending { get; private set; } = new();
    public List<Row> All { get; private set; } = new();
    public List<Invite> OpenInvites { get; private set; } = new();
    public string? MyId { get; private set; }
    public string? Notice { get; private set; }
    public string NoticeKind { get; private set; } = "ok";
    public string? InviteLink { get; private set; }
    public bool InviteMailSent { get; private set; }

    public UsersModel(AppDbContext db, UserManager<AppUser> users, AuditService audit, NotificationService notify)
    {
        _db = db; _users = users; _audit = audit; _notify = notify;
    }

    public async Task OnGetAsync() => await LoadAsync();

    // ---- access requests ----------------------------------------------------

    public async Task OnPostApproveAsync(string id) => await SetStatusAsync(id, UserStatus.Active,
        "user.approved", "Account approved.",
        "[Add-on Store] Access approved",
        "<p>Your access request was approved. You can sign in and publish plugins now.</p>");

    public async Task OnPostRejectAsync(string id) => await SetStatusAsync(id, UserStatus.Rejected,
        "user.rejected", "Request rejected.",
        "[Add-on Store] Access request declined",
        "<p>Your access request to the Add-on Store was declined. Contact an administrator for details.</p>");

    public async Task OnPostDisableAsync(string id) => await SetStatusAsync(id, UserStatus.Disabled,
        "user.disabled", "Account disabled.", null, null);

    public async Task OnPostEnableAsync(string id) => await SetStatusAsync(id, UserStatus.Active,
        "user.enabled", "Account enabled.", null, null);

    // ---- create / invite / role ----------------------------------------------

    public async Task OnPostCreateAsync(string name, string email, string role, string password)
    {
        var admin = await _users.GetUserAsync(User);
        if (!Roles.Contains(role)) role = "User";

        var user = new AppUser { UserName = email, Email = email, DisplayName = name, Status = UserStatus.Active };
        var result = await _users.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            Notice = string.Join(" ", result.Errors.Select(e => e.Description));
            NoticeKind = "error";
        }
        else
        {
            await _users.AddToRoleAsync(user, role);
            await _audit.LogAsync(admin!.DisplayName, "user.created", email, $"role {role}");
            Notice = "Account created.";
        }
        await LoadAsync();
    }

    public async Task OnPostInviteAsync(string email, string role)
    {
        var admin = await _users.GetUserAsync(User);
        if (!Roles.Contains(role)) role = "User";

        if (await _users.FindByEmailAsync(email) is not null)
        {
            Notice = "A user with this email already exists.";
            NoticeKind = "error";
            await LoadAsync();
            return;
        }

        var plain = "ppiv_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        _db.Invites.Add(new Invite
        {
            Email = email,
            TokenHash = TokenService.Hash(plain),
            Role = role,
            InvitedBy = admin!.DisplayName
        });
        await _db.SaveChangesAsync();
        await _audit.LogAsync(admin.DisplayName, "user.invited", email, $"role {role}");

        var baseUrl = await _notify.BaseUrlAsync();
        if (baseUrl.Length == 0) baseUrl = $"{Request.Scheme}://{Request.Host}";
        InviteLink = $"{baseUrl}/Account/Invite?token={plain}";
        InviteMailSent = (await _notify.SendDirectAsync(email,
            "[Add-on Store] You are invited",
            $"<p><b>{System.Net.WebUtility.HtmlEncode(admin.DisplayName)}</b> invites you to the Tungsten Power PDF Add-on Store ({role}).</p>" +
            $"<p><a href=\"{InviteLink}\">Accept the invitation and choose a password</a></p>", "Invitation")).Sent;
        await LoadAsync();
    }

    public async Task OnPostRevokeInviteAsync(int id)
    {
        var admin = await _users.GetUserAsync(User);
        var invite = await _db.Invites.FirstOrDefaultAsync(i => i.Id == id && i.AcceptedAt == null);
        if (invite is not null)
        {
            _db.Invites.Remove(invite);
            await _db.SaveChangesAsync();
            await _audit.LogAsync(admin!.DisplayName, "invite.revoked", invite.Email);
            Notice = "Invitation revoked.";
        }
        await LoadAsync();
    }

    public async Task OnPostSetRoleAsync(string id, string role)
    {
        var admin = await _users.GetUserAsync(User);
        var user = await _users.FindByIdAsync(id);
        if (user is null || !Roles.Contains(role)) { await LoadAsync(); return; }
        if (user.Id == admin!.Id)
        {
            Notice = "You cannot change your own role.";
            NoticeKind = "error";
            await LoadAsync();
            return;
        }
        var current = await _users.GetRolesAsync(user);
        if (current.Contains("Admin") && role != "Admin" &&
            (await _users.GetUsersInRoleAsync("Admin")).Count(a => a.Status == UserStatus.Active) <= 1)
        {
            Notice = "The last active admin cannot be demoted.";
            NoticeKind = "error";
            await LoadAsync();
            return;
        }
        await _users.RemoveFromRolesAsync(user, current);
        await _users.AddToRoleAsync(user, role);
        await _audit.LogAsync(admin.DisplayName, "user.role-changed", user.Email ?? id, $"-> {role}");
        Notice = "Role updated.";
        await LoadAsync();
    }

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
                await _notify.NotifyUserAsync("AccountDecision", user, mailSubject, mailBody!);
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
            All.Add(new Row(u, roles.Contains("Admin") ? "Admin" : roles.Contains("Reviewer") ? "Reviewer" : "User"));
        }
        OpenInvites = await _db.Invites.Where(i => i.AcceptedAt == null)
            .OrderByDescending(i => i.CreatedAt).ToListAsync();
    }
}
