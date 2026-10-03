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
    private readonly NotificationService _notify;

    public bool HasResendKey { get; private set; }
    public string From { get; private set; } = "";
    public string BaseUrl { get; private set; } = "";
    public string TimeZone { get; private set; } = TimeDisplay.DefaultZone;
    public string SourcePolicy { get; private set; } = "required";
    public string? Notice { get; private set; }
    public string NoticeKind { get; private set; } = "ok";
    public string? TestDetail { get; private set; }
    public Dictionary<string, bool> EventEnabled { get; } = new();
    public string? MyEmail { get; private set; }

    public SettingsModel(SettingsService settings, UserManager<AppUser> users, AuditService audit,
        NotificationService notify)
    {
        _settings = settings; _users = users; _audit = audit; _notify = notify;
    }

    public string? TestTo { get; private set; }

    public async Task OnPostTestMailAsync(string? testTo)
    {
        var admin = await _users.GetUserAsync(User);
        TestTo = (testTo ?? "").Trim();
        if (TestTo.Length == 0) TestTo = admin!.Email ?? "";
        if (!System.Net.Mail.MailAddress.TryCreate(TestTo, out _))
        {
            Notice = "Please enter a valid email address.";
            NoticeKind = "error";
            await LoadAsync();
            return;
        }
        var result = await _notify.SendDirectAsync(TestTo, "[Add-on Store] Test email",
            $"<p>This is a test email from the Add-on Store settings page, sent by {System.Net.WebUtility.HtmlEncode(admin!.DisplayName)}. Email notifications work.</p>", "Test");
        if (result.Sent)
            Notice = "Test email sent. Please check your inbox.";
        else
        {
            Notice = "The test email could not be sent.";
            NoticeKind = "error";
            TestDetail = result.Detail;
        }
        await LoadAsync();
    }

    public async Task OnPostNotificationsAsync(string[]? enabled)
    {
        var admin = await _users.GetUserAsync(User);
        var on = (enabled ?? Array.Empty<string>()).ToHashSet();
        foreach (var (key, _, _) in NotificationService.Events)
            await _settings.SetAsync("Notify." + key, on.Contains(key) ? "1" : "0");
        await _audit.LogAsync(admin!.DisplayName, "settings.changed", "Notifications", string.Join(",", on));
        Notice = "Settings saved.";
        await LoadAsync();
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

    public async Task OnPostServerAsync(string? baseUrl, string? timeZone, string? sourcePolicy)
    {
        if (sourcePolicy is "off" or "recommended" or "required")
            await _settings.SetAsync("Source.Policy", sourcePolicy);
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
        var sp = await _settings.GetAsync("Source.Policy");
        SourcePolicy = sp is "off" or "recommended" or "required" ? sp : "required";
        foreach (var (key, _, _) in NotificationService.Events)
            EventEnabled[key] = await _notify.IsEnabledAsync(key);
        MyEmail = (await _users.GetUserAsync(User))?.Email;
        var tz = await _settings.GetAsync("App.TimeZone", "App:TimeZone");
        TimeZone = string.IsNullOrWhiteSpace(tz) ? TimeDisplay.DefaultZone : tz;
    }
}
