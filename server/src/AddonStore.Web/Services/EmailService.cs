using System.Text.Json;
using AddonStore.Web.Data;
using Microsoft.AspNetCore.Identity;

namespace AddonStore.Web.Services;

public interface IAppEmailSender
{
    Task SendAsync(string to, string subject, string html);
}

/// <summary>Used when no provider is configured; the app stays fully functional.</summary>
public class NullEmailSender : IAppEmailSender
{
    private readonly ILogger<NullEmailSender> _log;
    public NullEmailSender(ILogger<NullEmailSender> log) => _log = log;

    public Task SendAsync(string to, string subject, string html)
    {
        _log.LogInformation("Email suppressed (no provider configured): to={To} subject={Subject}", to, subject);
        return Task.CompletedTask;
    }
}

public class ResendEmailSender : IAppEmailSender
{
    private readonly HttpClient _http;
    private readonly string _from;
    private readonly ILogger<ResendEmailSender> _log;

    public ResendEmailSender(HttpClient http, IConfiguration config, ILogger<ResendEmailSender> log)
    {
        _http = http;
        _from = config["Email:From"] ?? "store@localhost";
        _http.BaseAddress = new Uri("https://api.resend.com/");
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config["Email:ResendApiKey"]);
        _log = log;
    }

    public async Task SendAsync(string to, string subject, string html)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { from = _from, to = new[] { to }, subject, html });
            var resp = await _http.PostAsync("emails",
                new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));
            if (!resp.IsSuccessStatusCode)
                _log.LogWarning("Resend returned {Status} for mail to {To}", resp.StatusCode, to);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Sending mail to {To} failed", to);
        }
    }
}

/// <summary>Event notifications; mail failures never break the main flow.</summary>
public class NotificationService
{
    private readonly IAppEmailSender _mail;
    private readonly UserManager<AppUser> _users;
    private readonly IConfiguration _config;

    public NotificationService(IAppEmailSender mail, UserManager<AppUser> users, IConfiguration config)
    {
        _mail = mail; _users = users; _config = config;
    }

    private string BaseUrl => (_config["App:PublicBaseUrl"] ?? "").TrimEnd('/');

    public async Task NotifyAdminsAsync(string subject, string text)
    {
        var admins = await _users.GetUsersInRoleAsync("Admin");
        foreach (var admin in admins.Where(a => a.Status == UserStatus.Active && !string.IsNullOrEmpty(a.Email)))
            await _mail.SendAsync(admin.Email!, subject, Wrap(text));
    }

    public Task NotifyUserAsync(AppUser user, string subject, string text) =>
        string.IsNullOrEmpty(user.Email) ? Task.CompletedTask : _mail.SendAsync(user.Email, subject, Wrap(text));

    private string Wrap(string text) => $"""
        <div style="font-family:'Red Hat Display',Arial,sans-serif;color:#231F20">
          <div style="background:#002854;color:#fff;padding:14px 20px;font-weight:bold">Tungsten Power PDF Plugin-Store</div>
          <div style="height:4px;background:linear-gradient(90deg,#00EB86,#00A0FB)"></div>
          <div style="padding:20px">{text}</div>
          <div style="padding:0 20px 16px;color:#8094AA;font-size:12px">{BaseUrl}</div>
        </div>
        """;
}
