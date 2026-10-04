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

    public LoginModel(SignInManager<AppUser> signIn, UserManager<AppUser> users, IMemoryCache cache)
    {
        _signIn = signIn; _users = users; _cache = cache;
    }

    /// <summary>At most 20 sign-in attempts per address within 15 minutes.</summary>
    private bool Throttled()
    {
        var ip = AddonStore.Web.Services.GeoService.ClientIp(HttpContext)?.ToString() ?? "";
        var slot = DateTime.UtcNow.Ticks / TimeSpan.FromMinutes(15).Ticks;
        var n = _cache.GetOrCreate($"login:{ip}:{slot}", e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(16); return new int[1]; })!;
        lock (n) return ++n[0] > MaxAttemptsPer15Min;
    }

    public void OnGet(string? registered)
    {
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
        return RedirectToPage("/Dashboard");
    }
}
