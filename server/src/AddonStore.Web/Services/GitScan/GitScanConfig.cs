using Microsoft.AspNetCore.DataProtection;

namespace AddonStore.Web.Services.GitScan;

/// <summary>
/// Settings of "Git & Veracode" (S2.8.0), all under the keys GitScan.*. Secrets (app key, token, webhook secret,
/// Veracode key) are stored encrypted with the data protection keys (like the AI key) and never shown again.
/// Nothing is fixed to one git server: provider and address are entered on /Admin/GitScan.
/// </summary>
public class GitScanConfig
{
    public const string Prefix = "GitScan.";
    public const string EnabledKey = "GitScan.Enabled";
    public const string ProviderKey = "GitScan.Provider";
    public const string ApiUrlKey = "GitScan.ApiUrl";
    public const string AuthModeKey = "GitScan.AuthMode";
    public const string AppIdKey = "GitScan.AppId";
    public const string InstallationIdKey = "GitScan.InstallationId";
    public const string AppKeyKey = "GitScan.AppKey";                  // protected
    public const string TokenKey = "GitScan.Token";                    // protected
    public const string OwnerKey = "GitScan.Owner";
    public const string WebhookSecretKey = "GitScan.WebhookSecret";    // protected
    public const string WebhookSecretAtKey = "GitScan.WebhookSecretAt";
    public const string LastWebhookKey = "GitScan.LastWebhookAt";
    public const string LastTestKey = "GitScan.LastTest";              // "ok|<utc>|<text>" or "failed|..."

    public const string VcModeKey = "GitScan.Veracode.Mode";
    public const string VcRegionKey = "GitScan.Veracode.Region";
    public const string VcApiIdKey = "GitScan.Veracode.ApiId";
    public const string VcApiKeyKey = "GitScan.Veracode.ApiKey";       // protected
    public const string VcPolicyKey = "GitScan.Veracode.Policy";
    public const string VcScanDayKey = "GitScan.Veracode.ScanDay";
    public const string VcScanHourKey = "GitScan.Veracode.ScanHour";
    public const string VcLastTestKey = "GitScan.Veracode.LastTest";

    public const string AutoEnabledKey = "GitScan.Auto.Enabled";
    public const string AutoStageKey = "GitScan.Auto.Stage";
    public const string AutoNeedAiKey = "GitScan.Auto.NeedAi";
    public const string AutoPatchOnlyKey = "GitScan.Auto.PatchMinorOnly";
    public const string AutoFindingOnlyKey = "GitScan.Auto.AfterFindingOnly";
    public const string AutoKnownWarningsKey = "GitScan.Auto.KnownWarnings";

    public const string DeadlineDaysKey = "GitScan.Deadline.Days";
    public const string DeadlineRemindersKey = "GitScan.Deadline.Reminders";
    public const string DeadlineActionKey = "GitScan.Deadline.Action";
    public const string DeadlineDisableAfterKey = "GitScan.Deadline.DisableAfterDays";

    public const string EmergencyUsersKey = "GitScan.Suspend.EmergencyUsers";
    public const string NotifyCustomersKey = "GitScan.Suspend.NotifyCustomers";

    public static readonly string[] Regions = { "com", "eu", "us" };
    public static readonly string[] DeadlineActions = { "warn", "mark", "pause", "disable" };

    public record Git(bool Enabled, string Provider, string ApiUrl, string AuthMode, string AppId, string InstallationId,
                      bool HasAppKey, bool HasToken, string Owner, bool HasWebhookSecret, DateTime? WebhookSecretAt,
                      DateTime? LastWebhookAt, string LastTest);
    public record Veracode(string Mode, string Region, string ApiId, bool HasApiKey, string Policy, int ScanDay, int ScanHour, string LastTest)
    {
        public string Host => Region switch { "eu" => "api.veracode.eu", "us" => "api.veracode.us", _ => "api.veracode.com" };
    }
    public record Auto(bool Enabled, string Stage, bool NeedAi, bool PatchMinorOnly, bool AfterFindingOnly, bool KnownWarnings);
    public record Deadline(int Days, string Reminders, string Action, int DisableAfterDays);

    private readonly SettingsService _settings;
    private readonly IDataProtector _protector;

    public GitScanConfig(SettingsService settings, IDataProtectionProvider dp)
    {
        _settings = settings; _protector = dp.CreateProtector("AddonStore.GitScan");
    }

    public async Task<bool> EnabledAsync() => await _settings.GetAsync(EnabledKey) == "on";

    public async Task<Git> GitAsync()
    {
        var provider = await _settings.GetAsync(ProviderKey);
        if (GitProviders.Find(provider) is null) provider = GitProviders.All[0].Key;
        var apiUrl = await _settings.GetAsync(ApiUrlKey);
        if (apiUrl.Length == 0) apiUrl = GitProviders.Find(provider)!.DefaultApiUrl;
        var mode = await _settings.GetAsync(AuthModeKey) == "token" ? "token" : "app";
        return new Git(await EnabledAsync(), provider, apiUrl, mode, await _settings.GetAsync(AppIdKey), await _settings.GetAsync(InstallationIdKey),
            (await _settings.GetAsync(AppKeyKey)).Length > 0, (await _settings.GetAsync(TokenKey)).Length > 0, await _settings.GetAsync(OwnerKey),
            (await _settings.GetAsync(WebhookSecretKey)).Length > 0, Date(await _settings.GetAsync(WebhookSecretAtKey)),
            Date(await _settings.GetAsync(LastWebhookKey)), await _settings.GetAsync(LastTestKey));
    }

    public async Task<Veracode> VeracodeAsync()
    {
        var region = await _settings.GetAsync(VcRegionKey);
        if (!Regions.Contains(region)) region = "com";
        return new Veracode(await _settings.GetAsync(VcModeKey) == "verify" ? "verify" : "workflow", region,
            await _settings.GetAsync(VcApiIdKey), (await _settings.GetAsync(VcApiKeyKey)).Length > 0, await _settings.GetAsync(VcPolicyKey),
            Int(await _settings.GetAsync(VcScanDayKey), 1, 0, 6), Int(await _settings.GetAsync(VcScanHourKey), 3, 0, 23),
            await _settings.GetAsync(VcLastTestKey));
    }

    public async Task<Auto> AutoAsync() => new(
        await _settings.GetAsync(AutoEnabledKey) == "on",
        await _settings.GetAsync(AutoStageKey) == "live" ? "live" : "beta",
        await _settings.GetAsync(AutoNeedAiKey) != "off",           // default on
        await _settings.GetAsync(AutoPatchOnlyKey) == "on",
        await _settings.GetAsync(AutoFindingOnlyKey) == "on",
        await _settings.GetAsync(AutoKnownWarningsKey) != "off");   // default on

    public async Task<Deadline> DeadlineAsync()
    {
        var action = await _settings.GetAsync(DeadlineActionKey);
        if (!DeadlineActions.Contains(action)) action = "warn";
        var reminders = NormalizeReminders(await _settings.GetAsync(DeadlineRemindersKey));
        return new Deadline(Int(await _settings.GetAsync(DeadlineDaysKey), 90, 1, 365), reminders.Length > 0 ? reminders : "30,7,1",
                            action, Int(await _settings.GetAsync(DeadlineDisableAfterKey), 14, 0, 180));
    }

    /// <summary>"30, 7,1,x,400" -> "30,7,1": whole days 1 to 365, highest first, no repeats.</summary>
    public static string NormalizeReminders(string s) =>
        string.Join(",", s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, out var d) ? d : 0).Where(d => d is >= 1 and <= 365).Distinct().OrderByDescending(d => d).Take(6));

    public async Task<bool> NotifyCustomersDefaultAsync() => await _settings.GetAsync(NotifyCustomersKey) != "off";

    public async Task<HashSet<string>> EmergencyUsersAsync() =>
        (await _settings.GetAsync(EmergencyUsersKey)).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();

    // ---- secrets ------------------------------------------------------------

    public async Task SetSecretAsync(string key, string value) =>
        await _settings.SetAsync(key, value.Length == 0 ? "" : _protector.Protect(value));

    /// <summary>The decrypted secret, or null when none is stored or it cannot be read (key ring replaced).</summary>
    public async Task<string?> SecretAsync(string key)
    {
        var stored = await _settings.GetAsync(key);
        if (stored.Length == 0) return null;
        try { return _protector.Unprotect(stored); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    public Task SetAsync(string key, string value) => _settings.SetAsync(key, value);
    public Task<string> GetAsync(string key) => _settings.GetAsync(key);

    private static int Int(string s, int fallback, int min, int max) => int.TryParse(s, out var v) && v >= min && v <= max ? v : fallback;

    private static DateTime? Date(string s) =>
        DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;

    /// <summary>
    /// A git API address the store may call: https, or http only for this machine (tests against a local server).
    /// No user name or password in the address, no fragment.
    /// </summary>
    public static bool ApiUrlAllowed(string url, out string why)
    {
        why = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.UserInfo.Length > 0 || u.Fragment.Length > 0 ||
            (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp))
        {
            why = "Enter the API address as https://host/... without user name or password.";
            return false;
        }
        if (u.Scheme == Uri.UriSchemeHttp && !u.IsLoopback)
        {
            why = "Only https is allowed (http only for localhost).";
            return false;
        }
        return true;
    }
}
