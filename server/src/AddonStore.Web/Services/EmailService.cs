using AddonStore.Web.Data;
using Microsoft.AspNetCore.Identity;

namespace AddonStore.Web.Services;

public interface IAppEmailSender
{
    /// <summary>True when a provider is configured and the mail was handed over.</summary>
    Task<bool> SendAsync(string to, string subject, string html);
}

/// <summary>
/// Sends through Resend when an API key is configured (admin Settings page or
/// Email__ResendApiKey app setting); otherwise logs and reports false so
/// callers can fall back to showing information in the UI.
/// </summary>
public class ResendEmailSender : IAppEmailSender
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly SettingsService _settings;
    private readonly ILogger<ResendEmailSender> _log;

    public ResendEmailSender(IHttpClientFactory httpFactory, SettingsService settings, ILogger<ResendEmailSender> log)
    {
        _httpFactory = httpFactory; _settings = settings; _log = log;
    }

    public async Task<bool> SendAsync(string to, string subject, string html)
    {
        var key = await _settings.GetAsync("Email.ResendApiKey", "Email:ResendApiKey");
        if (string.IsNullOrWhiteSpace(key))
        {
            _log.LogInformation("Email suppressed (no Resend key configured): to={To} subject={Subject}", to, subject);
            return false;
        }
        var from = await _settings.GetAsync("Email.From", "Email:From");
        if (string.IsNullOrWhiteSpace(from)) from = "PluginStore <onboarding@resend.dev>";

        try
        {
            var http = _httpFactory.CreateClient("resend");
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            req.Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(new { from, to = new[] { to }, subject, html }),
                System.Text.Encoding.UTF8, "application/json");
            var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Resend returned {Status} for mail to {To}", resp.StatusCode, to);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Sending mail to {To} failed", to);
            return false;
        }
    }
}

/// <summary>Event notifications; mail failures never break the main flow.</summary>
public class NotificationService
{
    private readonly IAppEmailSender _mail;
    private readonly UserManager<AppUser> _users;
    private readonly SettingsService _settings;

    public NotificationService(IAppEmailSender mail, UserManager<AppUser> users, SettingsService settings)
    {
        _mail = mail; _users = users; _settings = settings;
    }

    public async Task<string> BaseUrlAsync() =>
        (await _settings.GetAsync("App.PublicBaseUrl", "App:PublicBaseUrl")).TrimEnd('/');

    public async Task NotifyAdminsAsync(string subject, string text)
    {
        var admins = await _users.GetUsersInRoleAsync("Admin");
        foreach (var admin in admins.Where(a => a.Status == UserStatus.Active && !string.IsNullOrEmpty(a.Email)))
            await _mail.SendAsync(admin.Email!, subject, await WrapAsync(text));
    }

    public async Task<bool> NotifyUserAsync(AppUser user, string subject, string text) =>
        !string.IsNullOrEmpty(user.Email) && await _mail.SendAsync(user.Email, subject, await WrapAsync(text));

    public async Task<bool> SendRawAsync(string to, string subject, string text) =>
        await _mail.SendAsync(to, subject, await WrapAsync(text));

    private async Task<string> WrapAsync(string text) => $"""
        <div style="font-family:'Red Hat Display',Arial,sans-serif;color:#231F20">
          <div style="background:#002854;color:#fff;padding:14px 20px;font-weight:bold">Tungsten Power PDF Plugin-Store</div>
          <div style="height:4px;background:linear-gradient(90deg,#00EB86,#00A0FB)"></div>
          <div style="padding:20px">{text}</div>
          <div style="padding:0 20px 16px;color:#8094AA;font-size:12px">{await BaseUrlAsync()}</div>
        </div>
        """;
}
