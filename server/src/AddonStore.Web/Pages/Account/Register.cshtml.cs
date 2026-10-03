using AddonStore.Web.Data;
using AddonStore.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AddonStore.Web.Pages.Account;

public class RegisterModel : PageModel
{
    private readonly UserManager<AppUser> _users;
    private readonly AuditService _audit;
    private readonly NotificationService _notify;

    public string? Error { get; private set; }

    public RegisterModel(UserManager<AppUser> users, AuditService audit, NotificationService notify)
    {
        _users = users; _audit = audit; _notify = notify;
    }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(string name, string email, string password, string? reason)
    {
        var user = new AppUser
        {
            UserName = email,
            Email = email,
            DisplayName = name,
            Status = UserStatus.Pending
        };
        var result = await _users.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            Error = string.Join(" ", result.Errors.Select(e => e.Description));
            return Page();
        }
        await _users.AddToRoleAsync(user, "User");
        await _audit.LogAsync(name, "user.access-requested", email, reason ?? "");
        await _notify.NotifyStaffAsync("AccessRequest",
            "[Add-on Store] New access request",
            $"<p><b>{System.Net.WebUtility.HtmlEncode(name)}</b> ({System.Net.WebUtility.HtmlEncode(email)}) requests publisher access.</p>" +
            $"<p>Reason: {System.Net.WebUtility.HtmlEncode(reason ?? "-")}</p><p>Review it on the Users page.</p>");
        return RedirectToPage("/Account/Login", new { registered = "1" });
    }
}
