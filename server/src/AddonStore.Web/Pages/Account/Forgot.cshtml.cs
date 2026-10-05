using AddonStore.Web.Data;
using AddonStore.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;

namespace AddonStore.Web.Pages.Account;

/// <summary>"Forgot password?" (S1.0.6): mails a reset link to an active account.
/// The answer is always the same, so the page reveals nothing about which addresses exist.</summary>
public class ForgotModel : PageModel
{
    private readonly UserManager<AppUser> _users;
    private readonly PasswordResetService _reset;
    private readonly IMemoryCache _cache;
    private const int MaxPerHour = 5;
    public const string Answer = "If an active account exists for this address, we sent it a link to choose a new password. The link is valid for 24 hours.";

    public string? Message { get; private set; }

    public ForgotModel(UserManager<AppUser> users, PasswordResetService reset, IMemoryCache cache)
    {
        _users = users; _reset = reset; _cache = cache;
    }

    /// <summary>At most 5 requests per client address and per email address within an hour.</summary>
    private bool Throttled(string email)
    {
        var ip = GeoService.ClientIp(HttpContext)?.ToString() ?? "";
        var slot = DateTime.UtcNow.Ticks / TimeSpan.FromHours(1).Ticks;
        bool over = false;
        foreach (var key in new[] { $"forgot:ip:{ip}:{slot}", $"forgot:mail:{email.ToLowerInvariant()}:{slot}" })
        {
            var n = _cache.GetOrCreate(key, e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(61); return new int[1]; })!;
            lock (n) if (++n[0] > MaxPerHour) over = true;
        }
        return over;
    }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(string? email)
    {
        email = (email ?? "").Trim();
        Message = Answer;
        if (email.Length is 0 or > 200 || Throttled(email)) return Page();
        var user = await _users.FindByEmailAsync(email);
        if (user is not null && user.Status == UserStatus.Active)
            await _reset.SendAsync(user, user.DisplayName);
        return Page();
    }
}
