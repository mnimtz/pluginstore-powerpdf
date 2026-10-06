using Microsoft.AspNetCore.DataProtection;
using AddonStore.Web.Data;
using AddonStore.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Pages.Admin;

public class SettingsModel : PageModel
{
    private readonly SettingsService _settings;
    private readonly UserManager<AppUser> _users;
    private readonly AuditService _audit;
    private readonly NotificationService _notify;
    private readonly UsageService _usage;
    private readonly AiService _ai;
    private readonly IWebHostEnvironment _env;

    public bool HasResendKey { get; private set; }
    public string From { get; private set; } = "";
    public string BaseUrl { get; private set; } = "";
    public string TimeZone { get; private set; } = TimeDisplay.DefaultZone;
    public string SourcePolicy { get; private set; } = "required";
    public bool FourEyes { get; private set; }
    public int ApproverCount { get; private set; }
    /// <summary>Sections of the left navigation (S0.18.0); one is shown at a time.</summary>
    public static readonly (string Key, string Label)[] Views =
    {
        ("server", "Server"), ("email", "Email"), ("notifications", "Notifications"),
        ("ai", "AI assistant"), ("privacy", "IP address logging"), ("reset", "Reset statistics"),
    };
    [BindProperty(SupportsGet = true)] public string? View { get; set; }
    public string? Notice { get; private set; }
    public string NoticeKind { get; private set; } = "ok";
    public string? TestDetail { get; private set; }
    public Dictionary<string, bool> EventEnabled { get; } = new();
    public string? MyEmail { get; private set; }

    // IP logging for the reports (GDPR confirmation once, then switchable)
    public bool IpOn { get; private set; }
    public int RetentionDays { get; private set; } = UsageService.DefaultRetentionDays;
    public string IpConfirmedBy { get; private set; } = "";
    public DateTime? IpConfirmedAt { get; private set; }
    public int IpEventCount { get; private set; }

    // Reset statistics (S1.0.2): sizes shown before deleting
    public int StatUsageRows { get; private set; }
    public int StatDownloads { get; private set; }
    public int StatShareRows { get; private set; }
    public int StatRatings { get; private set; }
    public int StatFeedback { get; private set; }
    public DateTime? StatsResetAt { get; private set; }

    // Optional AI assistant (S0.12.0)
    public AiService.Config Ai { get; private set; } = new("off", "", false, false, false, false, false, false, 300);
    public bool OfferFake { get; private set; }
    public List<AiService.ModelOption> AiModels { get; private set; } = new();

    public SettingsModel(SettingsService settings, UserManager<AppUser> users, AuditService audit,
        NotificationService notify, UsageService usage, AiService ai, IWebHostEnvironment env, PackageSigning signing)
    {
        _settings = settings; _users = users; _audit = audit; _notify = notify; _usage = usage; _ai = ai; _env = env; _signing = signing;
    }

    private readonly PackageSigning _signing;
    public string? SigningProblem { get; private set; }
    public string SigningKeyId { get; private set; } = "";

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
            await _settings.SetAsync("Email.ResendApiKey", "dp:" + HttpContext.RequestServices
                .GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>()
                .CreateProtector("AddonStore.ResendKey").Protect(resendKey.Trim()));
        await _settings.SetAsync("Email.From", (from ?? "").Trim());
        await _audit.LogAsync(admin!.DisplayName, "settings.changed", "Email",
            string.IsNullOrWhiteSpace(resendKey) ? "sender updated" : "key + sender updated");
        Notice = "Settings saved.";
        await LoadAsync();
    }

    public async Task OnPostServerAsync(string? baseUrl, string? timeZone, string? sourcePolicy, bool fourEyes)
    {
        if (sourcePolicy is "off" or "recommended" or "required")
            await _settings.SetAsync("Source.Policy", sourcePolicy);
        await _settings.SetAsync("Review.FourEyes", fourEyes ? "on" : "off");
        var admin = await _users.GetUserAsync(User);
        await _settings.SetAsync("App.PublicBaseUrl", (baseUrl ?? "").Trim().TrimEnd('/'));
        if (timeZone is not null && TimeDisplay.Zones.Contains(timeZone))
            await _settings.SetAsync("App.TimeZone", timeZone);
        await _audit.LogAsync(admin!.DisplayName, "settings.changed", "Server", $"public base url, time zone {timeZone}, four-eyes {(fourEyes ? "on" : "off")}");
        Notice = "Settings saved.";
        await LoadAsync();
    }

    /// <summary>
    /// Switches IP logging on. The GDPR confirmation is needed only the first
    /// time; it is kept (who, when) and shown, also after switching off.
    /// </summary>
    public async Task OnPostIpEnableAsync(bool confirmGdpr, int retentionDays, bool permanent)
    {
        var admin = await _users.GetUserAsync(User);
        var confirmed = (await _settings.GetAsync(UsageService.IpConfirmedAtKey)).Length > 0;
        if (!confirmed && !confirmGdpr)
        {
            Notice = "Please confirm the data protection statement first.";
            NoticeKind = "error";
            await LoadAsync();
            return;
        }
        var days = UsageService.NormalizeRetention(retentionDays, permanent);
        await _settings.SetAsync(UsageService.IpRetentionKey, days.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!confirmed)
        {
            await _settings.SetAsync(UsageService.IpConfirmedByKey, admin!.DisplayName + " <" + admin.Email + ">");
            await _settings.SetAsync(UsageService.IpConfirmedAtKey, DateTime.UtcNow.ToString("o"));
        }
        await _settings.SetAsync(UsageService.IpLoggingKey, "on");
        await _audit.LogAsync(admin!.DisplayName, "usage.iplogging.enabled", "IP logging",
            (confirmed ? "switched on (GDPR confirmation on file)" : "GDPR confirmation given") +
            (days == 0 ? "; permanent storage" : $"; retention {days} days"));
        Notice = "IP logging is on.";
        await LoadAsync();
    }

    public async Task OnPostIpDisableAsync()
    {
        var admin = await _users.GetUserAsync(User);
        await _settings.SetAsync(UsageService.IpLoggingKey, "off");
        await _audit.LogAsync(admin!.DisplayName, "usage.iplogging.disabled", "IP logging",
            "stored events are kept until the retention period ends");
        Notice = "IP logging is off.";
        await LoadAsync();
    }

    public int FeedbackRetentionDays { get; private set; } = IssueService.DefaultRetentionDays;

    /// <summary>Attachments, logs and reply addresses of closed problem reports (S1.1.0).</summary>
    public async Task OnPostFeedbackRetentionAsync(int feedbackDays, [FromServices] IssueService issues)
    {
        View = "privacy";
        var admin = await _users.GetUserAsync(User);
        var days = Math.Clamp(feedbackDays, 7, 3650);
        await _settings.SetAsync(IssueService.RetentionKey, days.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var n = await issues.PurgeAsync();
        await _audit.LogAsync(admin!.DisplayName, "feedback.retention", "Problem reports", $"retention {days} days; {n} closed reports cleaned");
        Notice = "Retention period saved.";
        await LoadAsync();
    }

    public async Task OnPostIpRetentionAsync(int retentionDays, bool permanent)
    {
        var admin = await _users.GetUserAsync(User);
        var days = UsageService.NormalizeRetention(retentionDays, permanent);
        await _settings.SetAsync(UsageService.IpRetentionKey, days.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var n = await _usage.PurgeAsync();
        await _audit.LogAsync(admin!.DisplayName, "usage.iplogging.retention", "IP logging",
            days == 0 ? "permanent storage, no automatic deletion" : $"retention {days} days; {n} older events deleted");
        Notice = "Retention period saved.";
        await LoadAsync();
    }

    /// <summary>
    /// Deletes the chosen statistics after internal tests ("count from here on").
    /// A safety backup of the whole store is written first; the audit log stays.
    /// </summary>
    public async Task OnPostResetAsync(bool usage, bool shares, bool ipEvents, bool ratings, bool feedback, string? confirm)
    {
        View = "reset";
        var admin = await _users.GetUserAsync(User);
        if (confirm != "RESET")
        {
            Notice = "Type RESET to confirm."; NoticeKind = "error";
            await LoadAsync(); return;
        }
        if (!(usage || shares || ipEvents || ratings || feedback))
        {
            Notice = "Choose at least one kind of data."; NoticeKind = "error";
            await LoadAsync(); return;
        }
        var backup = HttpContext.RequestServices.GetRequiredService<BackupService>();
        var safety = await backup.CreateSafetyAsync(admin!.DisplayName + " (automatic, before statistics reset)");
        var c = await _usage.ResetAsync(usage, shares, ipEvents, ratings, feedback);
        await _audit.LogAsync(admin.DisplayName, "stats.reset", "statistics",
            $"usage rows {c.Usage}, download counters of {c.Downloads} version(s), shared-link rows {c.Shares}, IP events {c.IpEvents}, " +
            $"ratings {c.Ratings}, problem reports {c.Feedback}; safety backup {Path.GetFileName(safety)}");
        Notice = "Statistics reset. A safety backup of the previous state is listed under Backup and restore > Safety backups.";
        await LoadAsync();
    }

    public async Task OnPostIpDeleteAllAsync(string? confirm)
    {
        var admin = await _users.GetUserAsync(User);
        if (confirm != "DELETE")
        {
            Notice = "Type DELETE to confirm.";
            NoticeKind = "error";
            await LoadAsync();
            return;
        }
        var n = await _usage.DeleteAllEventsAsync();
        await _audit.LogAsync(admin!.DisplayName, "usage.iplogging.deleted", "IP logging", $"{n} events with IP address deleted");
        Notice = "All stored IP addresses were deleted.";
        await LoadAsync();
    }

    /// <summary>
    /// Step 1: provider and key. Stores both (an empty key field keeps the stored
    /// key; it is stored encrypted and never rendered back), then loads the
    /// models this key may use, which also proves that the key works.
    /// </summary>
    public async Task OnPostAiConnectAsync(string? provider, string? apiKey, bool clearKey)
    {
        var admin = await _users.GetUserAsync(User);
        provider = (provider ?? "off").Trim().ToLowerInvariant();
        if (provider is not ("off" or "claude" or "gemini" or "fake") || (provider == "fake" && !_env.IsDevelopment())) provider = "off";
        var before = await _ai.ConfigAsync();
        await _settings.SetAsync(AiService.ProviderKey, provider);
        if (clearKey) await _ai.SetApiKeyAsync("");
        else if (!string.IsNullOrWhiteSpace(apiKey)) await _ai.SetApiKeyAsync(apiKey.Trim());
        if (provider != before.Provider) await _settings.SetAsync(AiService.ModelKey, "");
        await _audit.LogAsync(admin!.DisplayName, "settings.changed", "AI assistant",
            $"provider {provider}" + (clearKey ? ", key removed" : string.IsNullOrWhiteSpace(apiKey) ? "" : ", key replaced"));
        if (provider == "off")
        {
            await _settings.SetAsync(AiService.ModelListKey, "");
            Notice = "Settings saved.";
        }
        else
        {
            var (ok, message, _) = await _ai.LoadModelsAsync(HttpContext.RequestAborted);
            Notice = ok ? "Connected. Choose a model below." : "The connection failed.";
            NoticeKind = ok ? "ok" : "error";
            TestDetail = message;
        }
        await LoadAsync();
    }

    /// <summary>Step 2: model and features (provider and key come from step 1).</summary>
    public async Task OnPostAiAsync(string? model, bool triage, bool review, bool reviewAuto, bool reviewSource, bool search, int dailyLimit,
                                   string? reviewLanguage)
    {
        var admin = await _users.GetUserAsync(User);
        var cfg = await _ai.ConfigAsync();
        // Only a model from the provider's list is accepted; anything else means "default".
        var offered = await _ai.StoredModelsAsync(cfg.Provider);
        var m = (model ?? "").Trim();
        if (!offered.Any(o => o.Id == m)) m = "";
        await _settings.SetAsync(AiService.ModelKey, m);
        await _settings.SetAsync(AiService.TriageKey, triage ? "1" : "0");
        await _settings.SetAsync(AiService.ReviewKey, review ? "1" : "0");
        await _settings.SetAsync(AiService.ReviewAutoKey, review && reviewAuto ? "1" : "0");
        await _settings.SetAsync(AiService.ReviewSourceKey, review && reviewSource ? "1" : "0");
        await _settings.SetAsync(AiService.SearchKey, search ? "1" : "0");
        await _settings.SetAsync(AiService.ReviewLanguageKey, AiAssist.ReviewLanguage(reviewLanguage, "de"));
        await _settings.SetAsync(AiService.DailyLimitKey, Math.Clamp(dailyLimit, 1, 100000).ToString(System.Globalization.CultureInfo.InvariantCulture));
        await _audit.LogAsync(admin!.DisplayName, "settings.changed", "AI assistant",
            $"model {(m.Length == 0 ? "default" : m)}, triage {triage}, review {review} (auto {reviewAuto}, source {reviewSource}, language {AiAssist.ReviewLanguage(reviewLanguage, "de")}), search {search}, limit {dailyLimit}");
        Notice = "Settings saved.";
        await LoadAsync();
    }

    public async Task OnPostAiTestAsync()
    {
        var (ok, message) = await _ai.TestAsync();
        Notice = ok ? "AI connection works." : "The AI connection test failed.";
        NoticeKind = ok ? "ok" : "error";
        TestDetail = message;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        FeedbackRetentionDays = int.TryParse(await _settings.GetAsync(IssueService.RetentionKey), out var frd) && frd is >= 7 and <= 3650
            ? frd : IssueService.DefaultRetentionDays;
        // After a POST the section follows from the handler (?handler=AiTest -> ai).
        if (!Views.Any(v => v.Key == View))
        {
            var h = (string?)Request.Query["handler"] ?? "";
            View = h.StartsWith("Ip") ? "privacy" : h.StartsWith("Ai") ? "ai" : h is "Email" or "TestMail" ? "email"
                 : h == "Notifications" ? "notifications" : h == "Reset" ? "reset" : "server";
        }
        Ai = await _ai.ConfigAsync();
        OfferFake = _env.IsDevelopment();
        AiModels = await _ai.StoredModelsAsync(Ai.Provider);
        IpOn = await _usage.IpLoggingOnAsync();
        RetentionDays = await _usage.RetentionDaysAsync();
        IpConfirmedBy = await _settings.GetAsync(UsageService.IpConfirmedByKey);
        IpConfirmedAt = DateTime.TryParse(await _settings.GetAsync(UsageService.IpConfirmedAtKey), null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at : null;
        IpEventCount = await _usage.EventCountAsync();
        if (View == "reset")
        {
            var db = HttpContext.RequestServices.GetRequiredService<AddonStore.Web.Data.AppDbContext>();
            StatUsageRows = await db.UsageStats.CountAsync();
            StatDownloads = await db.PackageVersions.SumAsync(v => v.Downloads);
            StatShareRows = await db.ShareStats.CountAsync();
            StatRatings = await db.Ratings.CountAsync();
            StatFeedback = await db.Feedbacks.CountAsync();
            StatsResetAt = DateTime.TryParse(await _settings.GetAsync("Stats.ResetAt"), null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var ra) ? ra : null;
        }
        HasResendKey = (await _settings.GetAsync("Email.ResendApiKey", "Email:ResendApiKey")).Length > 0;
        From = await _settings.GetAsync("Email.From", "Email:From");
        BaseUrl = await _settings.GetAsync("App.PublicBaseUrl", "App:PublicBaseUrl");
        var sp = await _settings.GetAsync("Source.Policy");
        SourcePolicy = sp is "off" or "recommended" or "required" ? sp : "required";
        FourEyes = await _settings.GetAsync("Review.FourEyes") == "on";
        await _signing.EnsureLoadedAsync();
        SigningProblem = _signing.Problem;
        SigningKeyId = _signing.KeyId;
        ApproverCount = (await _users.GetUsersInRoleAsync("Admin")).Concat(await _users.GetUsersInRoleAsync("Reviewer"))
            .Where(u => u.Status == UserStatus.Active).Select(u => u.Id).Distinct().Count();
        foreach (var (key, _, _) in NotificationService.Events)
            EventEnabled[key] = await _notify.IsEnabledAsync(key);
        MyEmail = (await _users.GetUserAsync(User))?.Email;
        var tz = await _settings.GetAsync("App.TimeZone", "App:TimeZone");
        TimeZone = string.IsNullOrWhiteSpace(tz) ? TimeDisplay.DefaultZone : tz;
    }
}
