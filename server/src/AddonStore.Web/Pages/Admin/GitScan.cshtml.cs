using AddonStore.Web.Data;
using AddonStore.Web.Services;
using AddonStore.Web.Services.GitScan;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Pages.Admin;

/// <summary>
/// "Git & Veracode" (S2.8.0): everything of the module in one place, sections on the left. Admins see all;
/// emergency users only "Take out of service". Concept: docs/konzept-git-veracode.md.
/// </summary>
public class GitScanModel : PageModel
{
    public static readonly (string Key, string Label)[] Views =
    {
        ("overview", "Overview"), ("git", "Git connection"), ("veracode", "Veracode"), ("links", "Add-on links"),
        ("release", "Automatic approval"), ("deadlines", "Deadlines"), ("suspend", "Take out of service"), ("log", "Log"),
    };

    private readonly AppDbContext _db;
    private readonly GitScanConfig _config;
    private readonly GitScanService _svc;
    private readonly SuspensionService _suspend;
    private readonly AuditService _audit;
    private readonly UserManager<AppUser> _users;
    private readonly NotificationService _notify;
    private readonly TimeDisplay _time;

    public GitScanModel(AppDbContext db, GitScanConfig config, GitScanService svc, SuspensionService suspend, AuditService audit,
                        UserManager<AppUser> users, NotificationService notify, TimeDisplay time)
    {
        _db = db; _config = config; _svc = svc; _suspend = suspend; _audit = audit; _users = users; _notify = notify; _time = time;
    }

    [BindProperty(SupportsGet = true)] public string? View { get; set; }
    /// <summary>Preselected add-on of "Take out of service" (link from the plug-in page).</summary>
    [BindProperty(SupportsGet = true)] public string? Id { get; set; }
    [BindProperty(SupportsGet = true)] public bool Repos { get; set; }
    [BindProperty(SupportsGet = true)] public int? Edit { get; set; }

    public bool IsAdmin { get; private set; }
    public bool IsEmergency { get; private set; }
    public string? Notice { get; private set; }
    public object[] NoticeArgs { get; private set; } = Array.Empty<object>();
    public string NoticeKind { get; private set; } = "ok";
    public List<string> TestLines { get; private set; } = new();
    /// <summary>A new webhook secret, shown only in the answer to its creation.</summary>
    public string? NewSecret { get; private set; }

    public GitScanConfig.Git Git { get; private set; } = null!;
    public GitScanConfig.Veracode Vc { get; private set; } = null!;
    public GitScanConfig.Auto Auto { get; private set; } = null!;
    public GitScanConfig.Deadline Deadline { get; private set; } = null!;
    public string WebhookUrl { get; private set; } = "";
    public List<GitScanLink> Links { get; private set; } = new();
    public List<string> PackageIds { get; private set; } = new();
    public List<string> RepoList { get; private set; } = new();
    public List<AddonSuspension> Suspensions { get; private set; } = new();
    /// <summary>The entry of the preselected add-on, to change it (review S2.8.0: never reset level or date unseen).</summary>
    public AddonSuspension? Current { get; private set; }
    public string CurrentReviewLocal { get; private set; } = "";
    public List<GitScanEvent> Events { get; private set; } = new();
    public record UserRow(string Id, string Name, string Role, bool Emergency);
    public List<UserRow> UserRows { get; private set; } = new();
    public bool NotifyCustomersDefault { get; private set; }
    public Dictionary<string, int> DeliveryCounts { get; private set; } = new();
    public Dictionary<string, string> NewestLive { get; private set; } = new();

    public string CurrentView => !IsAdmin ? "suspend" : Views.Any(v => v.Key == View) ? View! : "overview";

    private async Task<AppUser?> MeAsync()
    {
        var me = await _users.GetUserAsync(User);
        IsAdmin = me is not null && await _suspend.IsAdminAsync(me);
        IsEmergency = !IsAdmin && await _suspend.IsEmergencyUserAsync(me);
        return me;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        await MeAsync();
        if (!IsAdmin && !IsEmergency) return Forbid();
        await LoadAsync();
        return Page();
    }

    // ---- git connection -------------------------------------------------------------------------------------

    public async Task<IActionResult> OnPostGitAsync(bool enabled, string? provider, string? apiUrl, string? authMode, string? appId,
                                                    string? installationId, string? appKey, string? token, string? owner,
                                                    bool clearAppKey, bool clearToken)
    {
        var me = await MeAsync();
        if (!IsAdmin) return Forbid();
        View = "git";
        var p = GitProviders.Find(provider ?? "") ?? GitProviders.All[0];
        var url = (apiUrl ?? "").Trim();
        if (url.Length == 0) url = p.DefaultApiUrl;
        appId = (appId ?? "").Trim();
        installationId = (installationId ?? "").Trim();
        owner = (owner ?? "").Trim().Trim('/');
        if (url.Length == 0) return await Done("Enter the API address of the git server.", false);
        if (!GitScanConfig.ApiUrlAllowed(url, out var why)) return await Done(why, false);
        if (appId.Length > 0 && !(long.TryParse(appId, out var a) && a > 0)) return await Done("The App ID is a number.", false);
        if (installationId.Length > 0 && !(long.TryParse(installationId, out var i) && i > 0)) return await Done("The installation ID is a number.", false);
        if (owner.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(owner, "^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$"))
            return await Done("The owner is the name of a user or organisation, e.g. tungsten-automation.", false);
        var key = (appKey ?? "").Trim();
        if (key.Length > 0 && (!key.Contains("-----BEGIN") || key.Length > 16384))
            return await Done("Paste the whole .pem file of the GitHub App (it starts with -----BEGIN).", false);
        var tok = (token ?? "").Trim();
        if (tok.Length > 500 || tok.Any(char.IsWhiteSpace)) return await Done("The token is one line without spaces.", false);

        // review S2.8.0: stored credentials never go to another host; with a new host they have to be entered again
        var before = await _config.GitAsync();
        var hostChanged = !string.Equals(HostOf(before.ApiUrl), HostOf(url), StringComparison.OrdinalIgnoreCase);
        // each stored secret that is not entered anew goes (review S2.8.0: also when only one of them is entered)
        var droppedOld = hostChanged && ((before.HasAppKey && key.Length == 0) || (before.HasToken && tok.Length == 0));
        if (hostChanged)
        {
            if (key.Length == 0) clearAppKey = true;
            if (tok.Length == 0) clearToken = true;
        }
        await _config.SetAsync(GitScanConfig.EnabledKey, enabled ? "on" : "off");
        await _config.SetAsync(GitScanConfig.ProviderKey, p.Key);
        await _config.SetAsync(GitScanConfig.ApiUrlKey, url == p.DefaultApiUrl ? "" : url);
        await _config.SetAsync(GitScanConfig.AuthModeKey, authMode == "token" ? "token" : "app");
        await _config.SetAsync(GitScanConfig.AppIdKey, appId);
        await _config.SetAsync(GitScanConfig.InstallationIdKey, installationId);
        await _config.SetAsync(GitScanConfig.OwnerKey, owner);
        if (clearAppKey) await _config.SetSecretAsync(GitScanConfig.AppKeyKey, "");
        else if (key.Length > 0) await _config.SetSecretAsync(GitScanConfig.AppKeyKey, key);
        if (clearToken) await _config.SetSecretAsync(GitScanConfig.TokenKey, "");
        else if (tok.Length > 0) await _config.SetSecretAsync(GitScanConfig.TokenKey, tok);
        await _audit.LogAsync(me!.DisplayName, "settings.changed", "GitScan.Git",
            $"{(enabled ? "on" : "off")}; {p.Key}; {url}; {(authMode == "token" ? "token" : "app")}; owner {(owner.Length > 0 ? owner : "-")}" +
            (key.Length > 0 ? "; app key replaced" : clearAppKey ? "; app key removed" : "") + (tok.Length > 0 ? "; token replaced" : clearToken ? "; token removed" : ""));
        return await Done(droppedOld
            ? "Saved. The git server changed, so the stored key and token were removed; enter them for the new server." : "Settings saved.", true);
    }

    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Authority : url;

    public async Task<IActionResult> OnPostGitTestAsync()
    {
        var me = await MeAsync();
        if (!IsAdmin) return Forbid();
        View = "git";
        var r = await _svc.TestGitAsync(me!.DisplayName, HttpContext.RequestAborted);
        TestLines = r.Lines;
        return await Done(r.Ok ? "Connection test passed." : "The connection test failed.", r.Ok);
    }

    public async Task<IActionResult> OnPostWebhookSecretAsync()
    {
        var me = await MeAsync();
        if (!IsAdmin) return Forbid();
        View = "git";
        NewSecret = await _svc.NewWebhookSecretAsync(me!.DisplayName);
        return await Done("New webhook secret created. Copy it now; it is not shown again.", true);
    }

    // ---- Veracode -------------------------------------------------------------------------------------------

    public async Task<IActionResult> OnPostVeracodeAsync(string? mode, string? region, string? apiId, string? apiKey, string? policy,
                                                         int scanDay, int scanHour, bool clearKey)
    {
        var me = await MeAsync();
        if (!IsAdmin) return Forbid();
        View = "veracode";
        apiId = (apiId ?? "").Trim();
        policy = (policy ?? "").Trim();
        var key = (apiKey ?? "").Trim();
        if (apiId.Length > 100 || apiId.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_'))) return await Done("The API ID has letters, digits and hyphens.", false);
        if (key.Length > 0 && !VeracodeClient.KeyLooksValid(key)) return await Done("The API key is a hex string (as Veracode shows it when it is created).", false);
        if (policy.Length > 100 || policy.Any(char.IsControl)) return await Done("The policy name has at most 100 characters.", false);
        await _config.SetAsync(GitScanConfig.VcModeKey, mode == "verify" ? "verify" : "workflow");
        await _config.SetAsync(GitScanConfig.VcRegionKey, GitScanConfig.Regions.Contains(region) ? region! : "com");
        await _config.SetAsync(GitScanConfig.VcApiIdKey, apiId);
        await _config.SetAsync(GitScanConfig.VcPolicyKey, policy);
        await _config.SetAsync(GitScanConfig.VcScanDayKey, Math.Clamp(scanDay, 0, 6).ToString());
        await _config.SetAsync(GitScanConfig.VcScanHourKey, Math.Clamp(scanHour, 0, 23).ToString());
        if (clearKey) await _config.SetSecretAsync(GitScanConfig.VcApiKeyKey, "");
        else if (key.Length > 0) await _config.SetSecretAsync(GitScanConfig.VcApiKeyKey, key);
        await _audit.LogAsync(me!.DisplayName, "settings.changed", "GitScan.Veracode",
            $"{(mode == "verify" ? "verify" : "workflow")}; region {region}; policy {(policy.Length > 0 ? policy : "-")}; scan day {scanDay} hour {scanHour}" +
            (key.Length > 0 ? "; API key replaced" : clearKey ? "; API key removed" : ""));
        return await Done("Settings saved.", true);
    }

    public async Task<IActionResult> OnPostVeracodeTestAsync()
    {
        var me = await MeAsync();
        if (!IsAdmin) return Forbid();
        View = "veracode";
        var r = await _svc.TestVeracodeAsync(me!.DisplayName, HttpContext.RequestAborted);
        TestLines = r.Lines;
        return await Done(r.Ok ? "Connection test passed." : "The connection test failed.", r.Ok);
    }

    // ---- links ----------------------------------------------------------------------------------------------

    public async Task<IActionResult> OnPostLinkAsync(int? id, string? target, string? repo, string? path, string? tagPattern,
                                                     string? workflow, string? autoRelease)
    {
        var me = await MeAsync();
        if (!IsAdmin) return Forbid();
        View = "links";
        var (ok, message) = await _svc.SaveLinkAsync(new(id, target ?? "", repo ?? "", path ?? "", tagPattern ?? "", workflow ?? "", autoRelease ?? "default"),
                                                     me!.DisplayName);
        if (!ok) Edit = id ?? 0;
        return await Done(message, ok);
    }

    public async Task<IActionResult> OnPostLinkDeleteAsync(int id)
    {
        var me = await MeAsync();
        if (!IsAdmin) return Forbid();
        View = "links";
        return await Done(await _svc.DeleteLinkAsync(id, me!.DisplayName) ? "Link removed." : "This link no longer exists.", true);
    }

    // ---- automatic approval and deadlines --------------------------------------------------------------------

    public async Task<IActionResult> OnPostReleaseAsync(bool enabled, string? stage, bool needAi, bool patchMinorOnly, bool afterFindingOnly, bool knownWarnings)
    {
        var me = await MeAsync();
        if (!IsAdmin) return Forbid();
        View = "release";
        await _config.SetAsync(GitScanConfig.AutoEnabledKey, enabled ? "on" : "off");
        await _config.SetAsync(GitScanConfig.AutoStageKey, stage == "live" ? "live" : "beta");
        await _config.SetAsync(GitScanConfig.AutoNeedAiKey, needAi ? "on" : "off");
        await _config.SetAsync(GitScanConfig.AutoPatchOnlyKey, patchMinorOnly ? "on" : "off");
        await _config.SetAsync(GitScanConfig.AutoFindingOnlyKey, afterFindingOnly ? "on" : "off");
        await _config.SetAsync(GitScanConfig.AutoKnownWarningsKey, knownWarnings ? "on" : "off");
        await _audit.LogAsync(me!.DisplayName, "settings.changed", "GitScan.Auto",
            $"{(enabled ? "on" : "off")}; stage {(stage == "live" ? "live" : "beta")}; AI {needAi}; patch/minor only {patchMinorOnly}; after finding only {afterFindingOnly}; known warnings {knownWarnings}");
        return await Done("Settings saved.", true);
    }

    public async Task<IActionResult> OnPostDeadlinesAsync(int days, string? reminders, string? action, int disableAfterDays)
    {
        var me = await MeAsync();
        if (!IsAdmin) return Forbid();
        View = "deadlines";
        if (days is < 1 or > 365) return await Done("The deadline lies between 1 and 365 days.", false);
        if (disableAfterDays is < 0 or > 180) return await Done("Between 0 and 180 days.", false);
        var rem = GitScanConfig.NormalizeReminders(reminders ?? "");
        var act = GitScanConfig.DeadlineActions.Contains(action) ? action! : "warn";
        await _config.SetAsync(GitScanConfig.DeadlineDaysKey, days.ToString());
        await _config.SetAsync(GitScanConfig.DeadlineRemindersKey, rem);
        await _config.SetAsync(GitScanConfig.DeadlineActionKey, act);
        await _config.SetAsync(GitScanConfig.DeadlineDisableAfterKey, disableAfterDays.ToString());
        await _audit.LogAsync(me!.DisplayName, "settings.changed", "GitScan.Deadline", $"{days} days; reminders {rem}; on expiry {act}; disable after {disableAfterDays}");
        return await Done("Settings saved.", true);
    }

    // ---- take out of service --------------------------------------------------------------------------------

    /// <param name="change">true when the form was opened with "Change" and showed the stored values (an empty date then clears it)</param>
    public async Task<IActionResult> OnPostSuspendAsync(string? packageId, string? level, string? reason, string? reviewAt, bool notifyCustomers, bool change)
    {
        var me = await MeAsync();
        if (!IsAdmin && !IsEmergency) return Forbid();
        View = "suspend";
        DateTime? review = null;
        if (!string.IsNullOrWhiteSpace(reviewAt))
        {
            if (!DateTime.TryParseExact(reviewAt, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var local))
                return await Done("The review date lies between today and one year from now.", false);
            review = _time.ToUtc(local.AddHours(8));   // 08:00 local time of the store
        }
        var r = await _suspend.SuspendAsync(packageId ?? "", level ?? "", reason, review, clearReview: change && review is null,
                                            notifyCustomers, me!, IsAdmin, mustBeNew: !change);
        Id = r.Ok ? null : packageId;
        return await Done(r.Message, r.Ok, r.Args);
    }

    public async Task<IActionResult> OnPostResumeAsync(string? packageId)
    {
        var me = await MeAsync();
        if (!IsAdmin) return Forbid();
        View = "suspend";
        var r = await _suspend.LiftAsync(packageId ?? "", me!, true);
        return await Done(r.Message, r.Ok, r.Args);
    }

    public async Task<IActionResult> OnPostRollbackAsync(string? packageId)
    {
        var me = await MeAsync();
        if (!IsAdmin) return Forbid();
        View = "suspend";
        var r = await _suspend.RollbackAsync(packageId ?? "", me!, true);
        return await Done(r.Message, r.Ok, r.Args);
    }

    public async Task<IActionResult> OnPostEmergencyAsync(string[]? userIds, bool notifyCustomersDefault)
    {
        var me = await MeAsync();
        if (!IsAdmin) return Forbid();
        View = "suspend";
        var wanted = (userIds ?? Array.Empty<string>()).Where(s => s.Length is > 0 and <= 64).ToHashSet();
        var valid = await _db.Users.Where(u => wanted.Contains(u.Id) && u.Status == UserStatus.Active).Select(u => new { u.Id, u.DisplayName }).ToListAsync();
        await _config.SetAsync(GitScanConfig.EmergencyUsersKey, string.Join(",", valid.Select(v => v.Id)));
        await _config.SetAsync(GitScanConfig.NotifyCustomersKey, notifyCustomersDefault ? "on" : "off");
        await _audit.LogAsync(me!.DisplayName, "settings.changed", "GitScan.Suspend",
            $"emergency users: {(valid.Count == 0 ? "none" : string.Join(", ", valid.Select(v => v.DisplayName)))}; tell customers by default {notifyCustomersDefault}");
        return await Done("Settings saved.", true);
    }

    // ---- loading --------------------------------------------------------------------------------------------

    private async Task<IActionResult> Done(string message, bool ok, object[]? args = null)
    {
        Notice = message.Length > 0 ? message : null;
        NoticeArgs = args ?? Array.Empty<object>();
        NoticeKind = ok ? "ok" : "error";
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Suspensions = await _db.AddonSuspensions.AsNoTracking().OrderBy(s => s.PackageId).ToListAsync();
        Current = Id is null ? null : Suspensions.FirstOrDefault(s => string.Equals(s.PackageId, Id, StringComparison.OrdinalIgnoreCase));
        CurrentReviewLocal = Current?.ReviewAt is { } cra ? _time.ToLocal(cra).ToString("yyyy-MM-dd") : "";
        PackageIds = await _db.Packages.AsNoTracking().Where(p => p.Id != SubmissionService.ClientPackageId && p.BlockedAt == null)
            .OrderBy(p => p.Id).Select(p => p.Id).ToListAsync();
        var ids = Suspensions.Select(s => s.PackageId).ToList();
        DeliveryCounts = (await _db.Deliveries.AsNoTracking().Where(d => ids.Contains(d.PackageId) && d.Status == "active")
                .GroupBy(d => d.PackageId).Select(g => new { g.Key, N = g.Count() }).ToListAsync())
            .ToDictionary(x => x.Key, x => x.N);
        var cmp = new Validation.SemVerComparer();
        NewestLive = (await _db.PackageVersions.AsNoTracking().Where(v => ids.Contains(v.PackageId) && v.Status == VersionStatus.Live)
                .Select(v => new { v.PackageId, v.Version }).ToListAsync())
            .GroupBy(v => v.PackageId).ToDictionary(g => g.Key, g => g.Select(v => v.Version).OrderByDescending(v => v, cmp).First());
        NotifyCustomersDefault = await _config.NotifyCustomersDefaultAsync();
        if (!IsAdmin) return;

        Git = await _config.GitAsync();
        Vc = await _config.VeracodeAsync();
        Auto = await _config.AutoAsync();
        Deadline = await _config.DeadlineAsync();
        WebhookUrl = (await _notify.BaseUrlAsync()) + "/api/git/webhook";
        Links = await _db.GitScanLinks.AsNoTracking().OrderBy(l => l.Target).ToListAsync();
        Events = await _db.GitScanEvents.AsNoTracking().OrderByDescending(e => e.Id).Take(GitScanService.MaxEvents).ToListAsync();
        if (CurrentView == "links" && Repos) RepoList = await _svc.ReposAsync(HttpContext.RequestAborted);
        if (CurrentView == "suspend")
        {
            var emergency = await _config.EmergencyUsersAsync();
            var admins = (await _users.GetUsersInRoleAsync("Admin")).Select(u => u.Id).ToHashSet();
            var reviewers = (await _users.GetUsersInRoleAsync("Reviewer")).Select(u => u.Id).ToHashSet();
            UserRows = (await _db.Users.AsNoTracking().Where(u => u.Status == UserStatus.Active).OrderBy(u => u.DisplayName).ToListAsync())
                .Where(u => !admins.Contains(u.Id))
                .Select(u => new UserRow(u.Id, u.DisplayName, reviewers.Contains(u.Id) ? "Reviewer" : "Developer", emergency.Contains(u.Id))).ToList();
        }
    }

    /// <summary>"ok|2026-11-23T..|text" -> (ok, when, text) for the last test line.</summary>
    public static (bool Ok, DateTime? At, string Text) ParseTest(string s)
    {
        var parts = s.Split('|', 3);
        if (parts.Length < 3) return (false, null, "");
        return (parts[0] == "ok",
                DateTime.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null,
                parts[2]);
    }
}
