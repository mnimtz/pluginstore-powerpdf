using AddonStore.Web.Data;
using AddonStore.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AddonStore.Web.Pages.Account;

/// <summary>Choose a new password from a reset link (S1.0.6).</summary>
public class ResetModel : PageModel
{
    private readonly UserManager<AppUser> _users;
    private readonly AuditService _audit;
    private const string Invalid = "This link is invalid or has expired. Request a new one with \"Forgot password?\" on the sign-in page.";

    public string? Error { get; private set; }
    public bool LinkOk { get; private set; }
    public string Uid { get; private set; } = "";
    public string Token { get; private set; } = "";
    public string Email { get; private set; } = "";

    public ResetModel(UserManager<AppUser> users, AuditService audit)
    {
        _users = users; _audit = audit;
    }

    private async Task<AppUser?> FindAsync(string? uid, string? token)
    {
        if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(token) || uid.Length > 100 || token.Length > 2000) return null;
        var user = await _users.FindByIdAsync(uid);
        if (user is null || user.Status != UserStatus.Active) return null;
        // checks the token without using it up (same purpose as ResetPasswordAsync)
        return await _users.VerifyUserTokenAsync(user, _users.Options.Tokens.PasswordResetTokenProvider,
            UserManager<AppUser>.ResetPasswordTokenPurpose, token) ? user : null;
    }

    public async Task OnGetAsync(string? uid, string? token)
    {
        var user = await FindAsync(uid, token);
        if (user is null) { Error = Invalid; return; }
        LinkOk = true; Uid = uid!; Token = token!; Email = user.Email ?? "";
    }

    public async Task<IActionResult> OnPostAsync(string? uid, string? token, string? password, string? password2)
    {
        var user = await FindAsync(uid, token);
        if (user is null) { Error = Invalid; return Page(); }
        LinkOk = true; Uid = uid!; Token = token!; Email = user.Email ?? "";
        if (string.IsNullOrEmpty(password) || password.Length > 200)
        {
            Error = "Enter a new password (up to 200 characters).";
            return Page();
        }
        if (password != password2)
        {
            Error = "The two passwords do not match.";
            return Page();
        }
        var result = await _users.ResetPasswordAsync(user, token!, password);
        if (!result.Succeeded)
        {
            Error = string.Join(" ", result.Errors.Select(e => e.Description));
            return Page();
        }
        // a forgotten password often ends in a lockout: the new password opens it again
        await _users.SetLockoutEndDateAsync(user, null);
        await _users.ResetAccessFailedCountAsync(user);
        await _audit.LogAsync(user.DisplayName, "user.password-reset", user.Email ?? user.Id);
        return RedirectToPage("/Account/Login", new { reset = "1" });
    }
}
