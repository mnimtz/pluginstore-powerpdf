using AddonStore.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;

namespace AddonStore.Web.Pages.Account;

public class LoginModel : PageModel
{
    private readonly SignInManager<AppUser> _signIn;
    private readonly UserManager<AppUser> _users;
    private readonly IMemoryCache _cache;
    private const int MaxAttemptsPer15Min = 20;
    private const string Failed = "Sign-in failed. Check email and password, or try again later.";

    public string? Message { get; private set; }
    public string MessageKind { get; private set; } = "warn";

    private readonly AddonStore.Web.Auth.EntraSettings _entra;

    public LoginModel(SignInManager<AppUser> signIn, UserManager<AppUser> users, IMemoryCache cache, AddonStore.Web.Auth.EntraSettings entra)
    {
        _signIn = signIn; _users = users; _cache = cache; _entra = entra;
    }

    /// <summary>At most 20 sign-in attempts per address within 15 minutes.</summary>
    private bool Throttled()
    {
        var ip = AddonStore.Web.Services.GeoService.ClientIp(HttpContext)?.ToString() ?? "";
        var slot = DateTime.UtcNow.Ticks / TimeSpan.FromMinutes(15).Ticks;
        var n = _cache.GetOrCreate($"login:{ip}:{slot}", e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(16); return new int[1]; })!;
        lock (n) return ++n[0] > MaxAttemptsPer15Min;
    }

    public void OnGet(string? registered, string? reset, string? entra)
    {
        // result of "Sign in with Microsoft" (Auth/EntraSignIn.cs): the same answers as the password sign-in below
        switch (_entra.Enabled ? entra : null)
        {
            case "pending":
                Message = "Your access request is still awaiting admin approval.";
                break;
            case "inactive":
                Message = "This account is not active. Contact an administrator.";
                MessageKind = "error";
                break;
            case "tenant":
                Message = "Your Microsoft account belongs to an organization that cannot sign in here. Sign in with email and password instead.";
                MessageKind = "error";
                break;
            case "email":
                Message = "Your Microsoft account did not provide an email address verified by its organization. Sign in with email and password instead.";
                MessageKind = "error";
                break;
            case "failed":
                Message = "Sign-in with Microsoft failed. Try again, or sign in with email and password.";
                MessageKind = "error";
                break;
        }
        if (reset == "1")
        {
            Message = "Password changed. Sign in with the new password.";
            MessageKind = "ok";
        }
        if (registered == "1")
        {
            Message = "Your access request was submitted. You will receive an email once an admin approves it.";
            MessageKind = "ok";
        }
    }

    public async Task<IActionResult> OnPostAsync(string email, string password)
    {
        // The password is verified FIRST (lockout included: a locked account
        // checks no password at all); account status is only revealed to someone
        // who proved they own the account. Every failure gets the same message,
        // so the answer tells nothing about which guess was right.
        if (Throttled())
        {
            Message = Failed;
            MessageKind = "error";
            return Page();
        }
        var user = string.IsNullOrWhiteSpace(email) ? null : await _users.FindByEmailAsync(email);
        if (user is null || !(await _signIn.CheckPasswordSignInAsync(user, password ?? "", lockoutOnFailure: true)).Succeeded)
        {
            Message = Failed;
            MessageKind = "error";
            return Page();
        }
        switch (user.Status)
        {
            case UserStatus.Pending:
                Message = "Your access request is still awaiting admin approval.";
                return Page();
            case UserStatus.Rejected:
            case UserStatus.Disabled:
                Message = "This account is not active. Contact an administrator.";
                MessageKind = "error";
                return Page();
        }
        await _signIn.SignInAsync(user, isPersistent: true);
        // the new navigation starts on its tile page (S1.5.0 preview); the classic one on the plug-in list
        return RedirectToPage(AddonStore.Web.Services.Shell.Sidebar(HttpContext) ? "/Start" : "/Dashboard");
    }
}
