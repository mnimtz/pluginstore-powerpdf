using Microsoft.AspNetCore.DataProtection;
using AddonStore.Web.Data;
using Microsoft.AspNetCore.Identity;

namespace AddonStore.Web.Services;

public record MailResult(bool Sent, string Detail);

public interface IAppEmailSender
{
    /// <summary>Hands the mail to the provider; Detail explains failures (shown on the test button).</summary>
    Task<MailResult> SendAsync(string to, string subject, string html, string eventKey);
}

/// <summary>
/// Sends through Resend when an API key is configured (admin Settings page or
/// Email__ResendApiKey app setting). Every attempt is written to the audit log
/// (mail.sent / mail.failed / mail.skipped) so admins can see what went out.
/// </summary>
public class ResendEmailSender : IAppEmailSender
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly SettingsService _settings;
    private readonly AuditService _audit;
    private readonly ILogger<ResendEmailSender> _log;
    private readonly Microsoft.AspNetCore.DataProtection.IDataProtectionProvider _dp;

    public ResendEmailSender(IHttpClientFactory httpFactory, SettingsService settings, AuditService audit,
        ILogger<ResendEmailSender> log, Microsoft.AspNetCore.DataProtection.IDataProtectionProvider dp)
    {
        _httpFactory = httpFactory; _settings = settings; _audit = audit; _log = log; _dp = dp;
    }

    public async Task<MailResult> SendAsync(string to, string subject, string html, string eventKey)
    {
        var key = await _settings.GetAsync("Email.ResendApiKey", "Email:ResendApiKey");
        if (key.StartsWith("dp:"))
        {
            try { key = _dp.CreateProtector("AddonStore.ResendKey").Unprotect(key[3..]); }
            catch (System.Security.Cryptography.CryptographicException) { key = ""; }   // key ring changed: enter the key again
        }
        if (string.IsNullOrWhiteSpace(key))
        {
            await _audit.LogAsync("system", "mail.skipped", to, $"{eventKey}: no Resend key configured");
            return new MailResult(false, "No Resend API key is configured.");
        }
        var from = await _settings.GetAsync("Email.From", "Email:From");
        if (string.IsNullOrWhiteSpace(from)) from = "Add-on Store <onboarding@resend.dev>";

        try
        {
            var http = _httpFactory.CreateClient("resend");
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            req.Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(new { from, to = new[] { to }, subject, html }),
                System.Text.Encoding.UTF8, "application/json");
            using var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                if (body.Length > 300) body = body[..300];
                await _audit.LogAsync("system", "mail.failed", to, $"{eventKey}: HTTP {(int)resp.StatusCode} {body}");
                return new MailResult(false, $"Resend answered HTTP {(int)resp.StatusCode}: {body}");
            }
            await _audit.LogAsync("system", "mail.sent", to, $"{eventKey}: {subject}");
            return new MailResult(true, "sent");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Sending mail to {To} failed", to);
            await _audit.LogAsync("system", "mail.failed", to, $"{eventKey}: {ex.Message}");
            return new MailResult(false, ex.Message);
        }
    }
}

/// <summary>
/// Event notifications. Each event can be switched off on the Settings page
/// (setting "Notify.&lt;Event&gt;", default on). Mail failures never break the
/// main flow.
/// </summary>
public class NotificationService
{
    /// <summary>Configurable events: key, English label (resx key), recipients label.</summary>
    public static readonly (string Key, string Label, string Recipients)[] Events =
    {
        ("AccessRequest",   "New access request",              "Admins"),
        ("Submission",      "New plugin version submitted",    "Admins and reviewers"),
        ("SubmissionReceipt", "Upload confirmation with check result", "Submitter"),
        ("ReviewResult",    "Plugin version approved or rejected", "Submitter"),
        ("StatusChange",    "Version withdrawn or restored, plug-in taken out of the store", "Owner"),
        ("CatalogChange",   "Catalog entry changed by an admin", "Owner"),
        ("AccountDecision", "Access request approved or declined", "Applicant"),
        ("ClientRelease",   "New Add-on Store client released", "Admins"),
        ("CategoryCreated", "New catalog category created by an upload", "Admins"),
        ("Feedback",        "Problem report or comment from the store client", "Owner"),
        ("CustomerDelivery", "Customer delivery created or changed by a developer", "Admins"),
        ("BackupFailed",    "Automatic backup failed",          "Admins"),
    };

    private readonly IAppEmailSender _mail;
    private readonly UserManager<AppUser> _users;
    private readonly SettingsService _settings;
    private readonly IHttpContextAccessor _http;
    // Address of the last request, for mails sent outside a request (automatic backup)
    // while no public base URL is set.
    private static volatile string s_lastRequestBase = "";

    public NotificationService(IAppEmailSender mail, UserManager<AppUser> users, SettingsService settings, IHttpContextAccessor http)
    {
        _mail = mail; _users = users; _settings = settings; _http = http;
    }

    /// <summary>
    /// Absolute base URL for links in mails: the configured public base URL, else the
    /// address of the current (or last) request. Never relative: a link in a mail
    /// needs a host.
    /// </summary>
    public async Task<string> BaseUrlAsync()
    {
        var configured = (await _settings.GetAsync("App.PublicBaseUrl", "App:PublicBaseUrl")).TrimEnd('/');
        if (configured.Length > 0) return configured;
        if (_http.HttpContext?.Request is { Host.HasValue: true } r)
        {
            // behind the App Service front end the request itself arrives as http
            var proto = r.Headers["X-Forwarded-Proto"].ToString().Split(',')[0].Trim();
            s_lastRequestBase = $"{(proto is "https" or "http" ? proto : r.Scheme)}://{r.Host}{r.PathBase}".TrimEnd('/');
        }
        return s_lastRequestBase;
    }

    /// <summary>
    /// Base URL for links that carry a secret (password reset): the configured public base URL,
    /// else the request's address only on a host we trust (loopback, the App Service host name,
    /// or App:TrustedHosts). Null otherwise: a forged Host header must never receive a reset token
    /// (audit S1.3.1).
    /// </summary>
    public async Task<string?> SecureBaseUrlAsync()
    {
        var configured = (await _settings.GetAsync("App.PublicBaseUrl", "App:PublicBaseUrl")).TrimEnd('/');
        if (configured.Length > 0) return configured;
        var r = _http.HttpContext?.Request;
        if (r is null || !r.Host.HasValue) return null;
        var host = r.Host.Host;
        var trusted = host is "localhost" or "127.0.0.1" or "[::1]" or "::1" ||
                      string.Equals(host, Environment.GetEnvironmentVariable("WEBSITE_HOSTNAME"), StringComparison.OrdinalIgnoreCase) ||
                      (_http.HttpContext!.RequestServices.GetService<IConfiguration>()?["App:TrustedHosts"] ?? "")
                          .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                          .Any(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase));
        return trusted ? await BaseUrlAsync() : null;
    }

    public async Task<bool> IsEnabledAsync(string eventKey) =>
        await _settings.GetAsync("Notify." + eventKey) != "0";

    /// <summary>Admins (and optionally reviewers) for an event, if the event is enabled.</summary>
    public async Task NotifyStaffAsync(string eventKey, string subject, string text, bool includeReviewers = false)
    {
        if (!await IsEnabledAsync(eventKey)) return;
        var recipients = (await _users.GetUsersInRoleAsync("Admin")).ToList();
        if (includeReviewers) recipients.AddRange(await _users.GetUsersInRoleAsync("Reviewer"));
        var html = await WrapAsync(text);
        foreach (var u in recipients.Where(a => a.Status == UserStatus.Active && !string.IsNullOrEmpty(a.Email))
                                    .GroupBy(a => a.Id).Select(g => g.First()))
            await _mail.SendAsync(u.Email!, subject, html, eventKey);
    }

    /// <summary>Events about a user's own plug-ins; the user can opt out of them in the profile.</summary>
    public static readonly HashSet<string> PluginEvents = new() { "SubmissionReceipt", "ReviewResult", "StatusChange", "CatalogChange", "Feedback" };

    public async Task NotifyUserAsync(string eventKey, AppUser user, string subject, string text)
    {
        if (!await IsEnabledAsync(eventKey) || string.IsNullOrEmpty(user.Email)) return;
        if (PluginEvents.Contains(eventKey) && !user.NotifyAboutPlugins) return;
        await _mail.SendAsync(user.Email, subject, await WrapAsync(text), eventKey);
    }

    /// <summary>Link to a plug-in's detail page for mails.</summary>
    public async Task<string> PluginLinkAsync(string packageId)
    {
        var url = $"{await BaseUrlAsync()}/Plugin?id={Uri.EscapeDataString(packageId)}";
        return $"<p><a href=\"{url}\">{System.Net.WebUtility.HtmlEncode(url)}</a></p>";
    }

    /// <summary>Always sent (invitations, test mails); returns the provider result.</summary>
    public async Task<MailResult> SendDirectAsync(string to, string subject, string text, string eventKey) =>
        await _mail.SendAsync(to, subject, await WrapAsync(text), eventKey);

    private async Task<string> WrapAsync(string text) => $"""
        <div style="font-family:'Red Hat Display',Arial,sans-serif;color:#231F20">
          <div style="background:#002854;color:#fff;padding:14px 20px;font-weight:bold">Tungsten Power PDF Add-on Store</div>
          <div style="height:4px;background:linear-gradient(90deg,#00EB86,#00A0FB)"></div>
          <div style="padding:20px">{text}</div>
          <div style="padding:0 20px 16px;color:#8094AA;font-size:12px">{await BaseUrlAsync()}</div>
        </div>
        """;
}
