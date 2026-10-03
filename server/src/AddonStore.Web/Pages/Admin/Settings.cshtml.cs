using AddonStore.Web.Data;
using AddonStore.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AddonStore.Web.Pages.Admin;

public class SettingsModel : PageModel
{
    private readonly SettingsService _settings;
    private readonly UserManager<AppUser> _users;
    private readonly AuditService _audit;

    public bool HasResendKey { get; private set; }
    public string From { get; private set; } = "";
    public string BaseUrl { get; private set; } = "";
    public string TimeZone { get; private set; } = TimeDisplay.DefaultZone;
    public string? Notice { get; private set; }

    public SettingsModel(SettingsService settings, UserManager<AppUser> users, AuditService audit)
    {
        _settings = settings; _users = users; _audit = audit;
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task OnPostEmailAsync(string? resendKey, string? from)
    {
        var admin = await _users.GetUserAsync(User);
        // An empty key field means "keep the stored key"; the value itself is
        // never rendered back into the page.
        if (!string.IsNullOrWhiteSpace(resendKey))
            await _settings.SetAsync("Email.ResendApiKey", resendKey.Trim());
        await _settings.SetAsync("Email.From", (from ?? "").Trim());
        await _audit.LogAsync(admin!.DisplayName, "settings.changed", "Email",
            string.IsNullOrWhiteSpace(resendKey) ? "sender updated" : "key + sender updated");
        Notice = "Settings saved.";
        await LoadAsync();
    }

    public async Task OnPostServerAsync(string? baseUrl, string? timeZone)
    {
        var admin = await _users.GetUserAsync(User);
        await _settings.SetAsync("App.PublicBaseUrl", (baseUrl ?? "").Trim().TrimEnd('/'));
        if (timeZone is not null && TimeDisplay.Zones.Contains(timeZone))
            await _settings.SetAsync("App.TimeZone", timeZone);
        await _audit.LogAsync(admin!.DisplayName, "settings.changed", "Server", $"public base url, time zone {timeZone}");
        Notice = "Settings saved.";
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        HasResendKey = (await _settings.GetAsync("Email.ResendApiKey", "Email:ResendApiKey")).Length > 0;
        From = await _settings.GetAsync("Email.From", "Email:From");
        BaseUrl = await _settings.GetAsync("App.PublicBaseUrl", "App:PublicBaseUrl");
        var tz = await _settings.GetAsync("App.TimeZone", "App:TimeZone");
        TimeZone = string.IsNullOrWhiteSpace(tz) ? TimeDisplay.DefaultZone : tz;
    }
}
