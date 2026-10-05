using AddonStore.Web.Data;
using Microsoft.AspNetCore.Identity;

namespace AddonStore.Web.Services;

/// <summary>
/// Password reset links (S1.0.6): requested by the user on the sign-in page
/// ("Forgot password?") or sent by an admin from the user list. The link carries
/// ASP.NET Identity's reset token (data-protected, valid 24 hours, single use:
/// a successful reset changes the security stamp and so invalidates it and
/// signs the user out everywhere).
/// </summary>
public class PasswordResetService
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private readonly UserManager<AppUser> _users;
    private readonly NotificationService _notify;
    private readonly AuditService _audit;

    public PasswordResetService(UserManager<AppUser> users, NotificationService notify, AuditService audit)
    {
        _users = users; _notify = notify; _audit = audit;
    }

    /// <summary>Creates the link and mails it to the user. Returns the link (for the admin) and whether the mail went out.</summary>
    public async Task<(string Link, bool Sent)> SendAsync(AppUser user, string actor)
    {
        var token = await _users.GeneratePasswordResetTokenAsync(user);
        var link = $"{await _notify.BaseUrlAsync()}/Account/Reset?uid={Uri.EscapeDataString(user.Id)}&token={Uri.EscapeDataString(token)}";
        var byAdmin = actor != user.DisplayName;
        var intro = byAdmin
            ? $"<p><b>{System.Net.WebUtility.HtmlEncode(actor)}</b> started a password reset for your Add-on Store account.</p>"
            : "<p>Someone (hopefully you) asked to reset the password of your Add-on Store account. If that was not you, ignore this mail; your password stays unchanged.</p>";
        var sent = !string.IsNullOrEmpty(user.Email) && (await _notify.SendDirectAsync(user.Email,
            "[Add-on Store] Reset your password",
            intro + $"<p><a href=\"{link}\">Choose a new password</a></p><p>The link is valid for 24 hours and works once.</p>",
            "PasswordReset")).Sent;
        await _audit.LogAsync(actor, byAdmin ? "user.password-reset-sent" : "user.password-reset-requested", user.Email ?? user.Id,
            sent ? "mail sent" : "mail not sent");
        return (link, sent);
    }
}
