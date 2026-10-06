using AddonStore.Web.Data;
using AddonStore.Web.Services;
using Microsoft.AspNetCore.DataProtection;
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
    /// <summary>When the form was shown (UTC ticks, signed): requests faster than 3 s are bots.</summary>
    public string Shown { get; private set; } = "";

    public RegisterModel(UserManager<AppUser> users, AuditService audit, NotificationService notify)
    {
        _users = users; _audit = audit; _notify = notify;
    }

    public void OnGet([FromServices] Microsoft.AspNetCore.DataProtection.IDataProtectionProvider dp) =>
        Shown = dp.CreateProtector("AddonStore.Register").Protect(DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public async Task<IActionResult> OnPostAsync(string name, string email, string password, string? reason, string? website, string? shown,
                                                 [FromServices] Microsoft.Extensions.Caching.Memory.IMemoryCache cache,
                                                 [FromServices] Microsoft.AspNetCore.DataProtection.IDataProtectionProvider dp)
    {
        // Bot traps: the invisible field filled in, or the form sent within 3 seconds
        // (or without a valid timestamp). Answered like a normal request, nothing stored.
        long ticks = 0;
        try { ticks = long.Parse(dp.CreateProtector("AddonStore.Register").Unprotect(shown ?? ""), System.Globalization.CultureInfo.InvariantCulture); }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException or OverflowException) { }
        var age = DateTime.UtcNow - new DateTime(Math.Min(Math.Max(ticks, 0), DateTime.MaxValue.Ticks), DateTimeKind.Utc);
        if (!string.IsNullOrEmpty(website) || ticks == 0 || age < TimeSpan.FromSeconds(3) || age > TimeSpan.FromHours(12))
        {
            await _audit.LogAsync("anonymous", "user.access-request-dropped", "", !string.IsNullOrEmpty(website) ? "trap field" : "timing");
            return RedirectToPage("/Account/Login", new { registered = "1" });
        }

        if (AddonStore.Web.Services.LocalizedIdentityErrors.CheckAccountInput(name, email, password, reason) is { } bad)
        {
            Error = bad;
            OnGet(dp);
            return Page();
        }
        if (await AddonStore.Web.Services.DisplayNames.TakenAsync(_users.Users, name))
        {
            Error = "Another account already uses this name.";
            OnGet(dp);
            return Page();
        }

        // at most 5 access requests per address and hour
        var ip = AddonStore.Web.Services.GeoService.ClientIp(HttpContext)?.ToString() ?? "";
        var n = Microsoft.Extensions.Caching.Memory.CacheExtensions.GetOrCreate(cache, $"register:{ip}:{DateTime.UtcNow:yyyyMMddHH}",
            e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1); return new int[1]; })!;
        bool over; lock (n) over = ++n[0] > 5;
        if (over) return RedirectToPage("/Account/Login", new { registered = "1" });
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
            // An existing address gets the same answer as a new request: the page
            // must not reveal which addresses have an account.
            if (result.Errors.Any(e => e.Code is "DuplicateUserName" or "DuplicateEmail"))
                return RedirectToPage("/Account/Login", new { registered = "1" });
            Error = string.Join(" ", result.Errors.Select(e => e.Description));
            OnGet(dp);
            return Page();
        }
        await _users.AddToRoleAsync(user, SchemaUpgrade.DefaultRole);
        await _audit.LogAsync(name, "user.access-requested", email, reason ?? "");
        await _notify.NotifyStaffAsync("AccessRequest",
            "[Add-on Store] New access request",
            $"<p><b>{System.Net.WebUtility.HtmlEncode(name)}</b> ({System.Net.WebUtility.HtmlEncode(email)}) requests publisher access.</p>" +
            $"<p>Reason: {System.Net.WebUtility.HtmlEncode(reason ?? "-")}</p>" +
            $"<p><a href=\"{await _notify.BaseUrlAsync()}/Admin/Users\">Review it on the Users page</a>.</p>");
        return RedirectToPage("/Account/Login", new { registered = "1" });
    }
}
