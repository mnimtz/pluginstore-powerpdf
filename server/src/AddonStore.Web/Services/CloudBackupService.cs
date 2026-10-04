using System.Globalization;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.DataProtection;

namespace AddonStore.Web.Services;

/// <summary>
/// Automatic backups to Azure Blob Storage (S0.18.0). One access key (the
/// storage account's connection string, or a SAS URL of the account or of a
/// container), a passphrase that encrypts the whole backup (BackupCrypto)
/// before it leaves the server, a schedule (time and weekdays in the server's
/// time zone) and how many backups to keep. Connection string and passphrase
/// are stored encrypted with the data protection key ring.
/// </summary>
public class CloudBackupService
{
    public const string EnabledKey = "Backup.Cloud.Enabled";
    public const string ConnectionKey = "Backup.Cloud.Connection";   // protected
    public const string ContainerKey = "Backup.Cloud.Container";
    public const string PassphraseKey = "Backup.Cloud.Passphrase";   // protected
    public const string TimeKey = "Backup.Cloud.Time";               // "HH:mm", server time zone
    public const string DaysKey = "Backup.Cloud.Days";               // digits 0-6, 0 = Sunday
    public const string KeepKey = "Backup.Cloud.Keep";
    public const string LastRunKey = "Backup.Cloud.LastRunUtc";      // ISO 8601
    public const string LastResultKey = "Backup.Cloud.LastResult";   // "ok" or the error
    public const string LastBlobKey = "Backup.Cloud.LastBlob";
    public const string LastSizeKey = "Backup.Cloud.LastSize";
    public const string DefaultContainer = "addonstore-backup";
    public const string Prefix = "addonstore-backup-";
    public const int MinPassphrase = 12;

    /// <summary>One backup at a time, whether started by the schedule or by an admin.</summary>
    private static readonly SemaphoreSlim Running = new(1, 1);
    public static bool IsRunning => Running.CurrentCount == 0;

    private readonly SettingsService _settings;
    private readonly IDataProtector _protector;
    private readonly BackupService _backup;
    private readonly AuditService _audit;
    private readonly NotificationService _notify;
    private readonly ILogger<CloudBackupService> _log;

    public CloudBackupService(SettingsService settings, IDataProtectionProvider dp, BackupService backup,
                              AuditService audit, NotificationService notify, ILogger<CloudBackupService> log)
    {
        _settings = settings; _protector = dp.CreateProtector("AddonStore.BackupCloud");
        _backup = backup; _audit = audit; _notify = notify; _log = log;
    }

    public record Config(bool Enabled, bool HasConnection, string Account, string Container, bool HasPassphrase,
                         TimeOnly Time, string Days, int Keep, DateTime? LastRunUtc, string LastResult,
                         string LastBlob, long LastSize)
    {
        public bool Ready => HasConnection && HasPassphrase;
    }

    public async Task<Config> ConfigAsync()
    {
        var conn = await SecretAsync(ConnectionKey);
        var container = await _settings.GetAsync(ContainerKey);
        if (!ValidContainer(container)) container = DefaultContainer;
        var time = TimeOnly.TryParseExact(await _settings.GetAsync(TimeKey), "HH:mm", out var t) ? t : new TimeOnly(2, 30);
        var days = new string((await _settings.GetAsync(DaysKey)).Where(c => c is >= '0' and <= '6').Distinct().OrderBy(c => c).ToArray());
        if (days.Length == 0) days = "0123456";
        var keep = int.TryParse(await _settings.GetAsync(KeepKey), out var k) && k is >= 1 and <= 365 ? k : 14;
        DateTime? last = DateTime.TryParse(await _settings.GetAsync(LastRunKey), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var lr) ? lr : null;
        long.TryParse(await _settings.GetAsync(LastSizeKey), out var size);
        return new Config(await _settings.GetAsync(EnabledKey) == "1", conn.Length > 0, AccountOf(conn), container,
            (await SecretAsync(PassphraseKey)).Length > 0, time, days, keep, last,
            await _settings.GetAsync(LastResultKey), await _settings.GetAsync(LastBlobKey), size);
    }

    /// <summary>Container names: 3-63 lower-case letters, digits and single hyphens.</summary>
    public static bool ValidContainer(string? name) =>
        name is { Length: >= 3 and <= 63 } && System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9](?!.*--)[a-z0-9-]*[a-z0-9]$");

    /// <summary>
    /// Saves the settings. An empty connection or passphrase keeps the stored one.
    /// Returns an error text (resx key) or null.
    /// </summary>
    public async Task<string?> SaveAsync(bool enabled, string? connection, string? container, string? passphrase,
                                         string? time, IEnumerable<string>? days, int keep)
    {
        connection = connection?.Trim();
        if (!string.IsNullOrEmpty(connection) && Client(connection, DefaultContainer) is null)
            return "The access key is neither a connection string nor a SAS URL of the storage account.";
        if (!string.IsNullOrEmpty(passphrase) && passphrase.Length < MinPassphrase)
            return "The passphrase must have at least 12 characters.";
        container = (container ?? "").Trim().ToLowerInvariant();
        if (container.Length > 0 && !ValidContainer(container))
            return "Container names use 3 to 63 lower-case letters, digits and hyphens.";
        if (!TimeOnly.TryParseExact(time ?? "", "HH:mm", out _)) time = "02:30";
        var dayString = new string((days ?? Array.Empty<string>()).SelectMany(d => d).Where(c => c is >= '0' and <= '6')
            .Distinct().OrderBy(c => c).ToArray());

        if (!string.IsNullOrEmpty(connection)) await _settings.SetAsync(ConnectionKey, _protector.Protect(connection));
        if (!string.IsNullOrEmpty(passphrase)) await _settings.SetAsync(PassphraseKey, _protector.Protect(passphrase));
        await _settings.SetAsync(ContainerKey, container.Length > 0 ? container : DefaultContainer);
        await _settings.SetAsync(TimeKey, time!);
        await _settings.SetAsync(DaysKey, dayString.Length > 0 ? dayString : "0123456");
        await _settings.SetAsync(KeepKey, Math.Clamp(keep, 1, 365).ToString(CultureInfo.InvariantCulture));
        var cfg = await ConfigAsync();
        await _settings.SetAsync(EnabledKey, enabled && cfg.Ready ? "1" : "0");
        if (enabled && !cfg.Ready) return "Saved. The schedule stays off until an access key and a passphrase are set.";
        return null;
    }

    /// <summary>Creates the container if needed and writes, reads and deletes a small test file.</summary>
    public async Task<(bool Ok, string Message)> TestAsync(CancellationToken ct = default)
    {
        try
        {
            var c = await ContainerAsync(ct);
            if (c is null) return (false, "No access key is stored.");
            await EnsureContainerAsync(c, ct);
            var blob = c.GetBlobClient(".connection-test");
            await blob.UploadAsync(BinaryData.FromString("Add-on Store connection test " + DateTime.UtcNow.ToString("o")), overwrite: true, ct);
            await blob.DownloadContentAsync(ct);
            await blob.DeleteIfExistsAsync(cancellationToken: ct);
            return (true, $"{c.AccountName}/{c.Name}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, Short(ex));
        }
    }

    public record BackupBlob(string Name, long Size, DateTime CreatedUtc);

    /// <summary>The backups in the container, newest first.</summary>
    public async Task<List<BackupBlob>> ListAsync(CancellationToken ct = default)
    {
        var list = new List<BackupBlob>();
        var c = await ContainerAsync(ct);
        if (c is null || !await c.ExistsAsync(ct)) return list;
        await foreach (var b in c.GetBlobsAsync(BlobTraits.None, BlobStates.None, Prefix, ct))
            list.Add(new BackupBlob(b.Name, b.Properties.ContentLength ?? 0,
                (b.Properties.CreatedOn ?? b.Properties.LastModified ?? DateTimeOffset.MinValue).UtcDateTime));
        return list.OrderByDescending(b => b.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Downloads one backup (still encrypted) to a local file.</summary>
    public async Task DownloadAsync(string name, string target, CancellationToken ct = default)
    {
        if (!IsBackupName(name)) throw new ArgumentException("not a backup name");
        var c = await ContainerAsync(ct) ?? throw new InvalidOperationException("No access key is stored.");
        await c.GetBlobClient(name).DownloadToAsync(target, ct);
    }

    public static bool IsBackupName(string? name) =>
        name is not null && System.Text.RegularExpressions.Regex.IsMatch(name, @"^addonstore-backup-\d{8}-\d{6}\.psbak$");

    /// <summary>The passphrase, for decrypting a backup from the container on restore.</summary>
    public Task<string> PassphraseAsync() => SecretAsync(PassphraseKey);

    /// <summary>
    /// One backup run: full backup, key ring and whole file encrypted with the
    /// passphrase, upload, then delete backups beyond the retention count.
    /// Never throws; the result is stored for the status line and mailed to
    /// the admins when it fails.
    /// </summary>
    public async Task<(bool Ok, string Message)> RunAsync(string actor, CancellationToken ct = default)
    {
        if (!await Running.WaitAsync(0, ct)) return (false, "A backup is already running.");
        var work = Path.Combine(Path.GetTempPath(), "pluginstore-cloud-" + Guid.NewGuid().ToString("N"));
        var started = DateTime.UtcNow;
        try
        {
            var cfg = await ConfigAsync();
            if (!cfg.Ready) throw new InvalidOperationException("Access key or passphrase missing.");
            var passphrase = await SecretAsync(PassphraseKey);
            var c = await ContainerAsync(ct) ?? throw new InvalidOperationException("No access key is stored.");
            await EnsureContainerAsync(c, ct);

            Directory.CreateDirectory(work);
            var full = Path.Combine(work, "full.zip");
            var export = Path.Combine(work, "export.zip");
            var sealedFile = Path.Combine(work, "backup" + BackupCrypto.Extension);
            await _backup.CreateAsync(full, actor);
            await _backup.ExportAsync(full, export, passphrase);   // key ring as keys.enc
            File.Delete(full);
            await BackupCrypto.EncryptAsync(export, sealedFile, passphrase, ct);
            File.Delete(export);

            var name = $"{Prefix}{started:yyyyMMdd-HHmmss}{BackupCrypto.Extension}";
            var size = new FileInfo(sealedFile).Length;
            await c.GetBlobClient(name).UploadAsync(sealedFile, new Azure.Storage.Blobs.Models.BlobUploadOptions
            {
                Metadata = new Dictionary<string, string> { ["createdby"] = Ascii(actor), ["server"] = "addonstore" },
            }, ct);

            var deleted = 0;
            foreach (var old in (await ListAsync(ct)).Skip(cfg.Keep))
            {
                await c.GetBlobClient(old.Name).DeleteIfExistsAsync(cancellationToken: ct);
                deleted++;
            }
            await StoreResultAsync(started, "ok", name, size);
            await _audit.LogAsync(actor, "backup.cloud.created", name, $"{size / 1024} KB, {deleted} older backup(s) deleted");
            return (true, name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var message = Short(ex);
            _log.LogWarning(ex, "cloud backup failed");
            await StoreResultAsync(started, message, "", 0);
            await _audit.LogAsync(actor, "backup.cloud.failed", "Azure Blob Storage", message);
            try
            {
                await _notify.NotifyStaffAsync("BackupFailed", "Add-on Store: automatic backup failed",
                    $"<p>The backup to Azure Blob Storage failed ({System.Net.WebUtility.HtmlEncode(started.ToString("u"))}):</p>" +
                    $"<p><code>{System.Net.WebUtility.HtmlEncode(message)}</code></p>" +
                    $"<p><a href=\"{await _notify.BaseUrlAsync()}/Admin/Backup\">Admin &gt; Backup</a></p>");
            }
            catch (Exception mailEx) { _log.LogWarning(mailEx, "backup failure mail not sent"); }
            return (false, message);
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
            Running.Release();
        }
    }

    /// <summary>The next scheduled run after <paramref name="afterUtc"/> in the server time zone, null when off.</summary>
    public static DateTime? NextRunUtc(Config cfg, TimeZoneInfo zone, DateTime afterUtc)
    {
        if (!cfg.Enabled) return null;
        var local = TimeZoneInfo.ConvertTimeFromUtc(afterUtc, zone);
        for (var i = 0; i <= 7; i++)
        {
            var day = DateOnly.FromDateTime(local).AddDays(i);
            if (!cfg.Days.Contains((char)('0' + (int)day.DayOfWeek))) continue;
            var slot = day.ToDateTime(cfg.Time);
            if (zone.IsInvalidTime(slot)) slot = slot.AddHours(1);   // skipped hour at the change to summer time
            var slotUtc = TimeZoneInfo.ConvertTimeToUtc(slot, zone);
            if (slotUtc > afterUtc) return slotUtc;
        }
        return null;
    }

    /// <summary>
    /// Creates the container when it is missing. Checked first, so a SAS that is
    /// limited to one existing container (it may not create containers) works too.
    /// </summary>
    private static async Task EnsureContainerAsync(BlobContainerClient c, CancellationToken ct)
    {
        if (!await c.ExistsAsync(ct))
            await c.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);
    }

    private async Task StoreResultAsync(DateTime startedUtc, string result, string blob, long size)
    {
        await _settings.SetAsync(LastRunKey, startedUtc.ToString("o", CultureInfo.InvariantCulture));
        await _settings.SetAsync(LastResultKey, result.Length > 400 ? result[..400] : result);
        await _settings.SetAsync(LastBlobKey, blob);
        await _settings.SetAsync(LastSizeKey, size.ToString(CultureInfo.InvariantCulture));
    }

    private async Task<string> SecretAsync(string key)
    {
        var raw = await _settings.GetAsync(key);
        if (string.IsNullOrEmpty(raw)) return "";
        try { return _protector.Unprotect(raw); }
        catch (System.Security.Cryptography.CryptographicException) { return ""; }   // key ring lost: enter it again
    }

    private async Task<BlobContainerClient?> ContainerAsync(CancellationToken ct)
    {
        var conn = await SecretAsync(ConnectionKey);
        if (conn.Length == 0) return null;
        var container = await _settings.GetAsync(ContainerKey);
        return Client(conn, ValidContainer(container) ? container : DefaultContainer);
    }

    /// <summary>
    /// A container client for a connection string, an account SAS URL or a
    /// container SAS URL (then that container is used); null when unusable.
    /// </summary>
    private static BlobContainerClient? Client(string access, string container)
    {
        // Three short retries instead of six with growing pauses: a wrong address
        // fails the connection test within seconds.
        var options = new BlobClientOptions();
        options.Retry.MaxRetries = 3;
        options.Retry.Delay = TimeSpan.FromSeconds(1);
        options.Retry.MaxDelay = TimeSpan.FromSeconds(8);
        try
        {
            if (access.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                access.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(access);
                if (!uri.Query.Contains("sig=", StringComparison.Ordinal)) return null;
                var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                // path style (emulator): /account/container; Azure: /container
                var isEmulator = uri.Host == "127.0.0.1";
                var containerInUrl = segments.Length > (isEmulator ? 1 : 0);
                return containerInUrl ? new BlobContainerClient(uri, options)
                                      : new BlobServiceClient(uri, options).GetBlobContainerClient(container);
            }
            if (!access.Contains("AccountName=", StringComparison.OrdinalIgnoreCase) &&
                !access.Contains("UseDevelopmentStorage", StringComparison.OrdinalIgnoreCase) &&
                !access.Contains("BlobEndpoint=", StringComparison.OrdinalIgnoreCase)) return null;
            return new BlobServiceClient(access, options).GetBlobContainerClient(container);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or UriFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string AccountOf(string access)
    {
        if (access.Length == 0) return "";
        var m = System.Text.RegularExpressions.Regex.Match(access, "AccountName=([^;]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        return Uri.TryCreate(access, UriKind.Absolute, out var u) ? u.Host.Split('.')[0] : "";
    }

    private static string Ascii(string s) => new(s.Select(c => c is >= ' ' and <= '~' ? c : '_').Take(120).ToArray());

    /// <summary>Error text without secrets (the SDK's messages can contain the request URL with its SAS).</summary>
    private static string Short(Exception ex)
    {
        // "Retry failed after 6 tries ..." wraps the actual cause
        if (ex is AggregateException { InnerExceptions.Count: > 0 } agg) ex = agg.InnerExceptions[^1];
        var msg = ex is Azure.RequestFailedException rf ? $"{rf.Status} {rf.ErrorCode}: {rf.Message.Split('\n')[0]}" : ex.Message.Split('\n')[0];
        msg = System.Text.RegularExpressions.Regex.Replace(msg, @"sig=[^&\s]+", "sig=***");
        msg = System.Text.RegularExpressions.Regex.Replace(msg, @"AccountKey=[^;\s]+", "AccountKey=***", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return msg.Length > 300 ? msg[..300] : msg;
    }
}

/// <summary>Starts the scheduled cloud backups (checks every minute).</summary>
public sealed class CloudBackupScheduler : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<CloudBackupScheduler> _log;

    public CloudBackupScheduler(IServiceScopeFactory scopes, ILogger<CloudBackupScheduler> log) { _scopes = scopes; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        // Start after the web server (see UsageMaintenance).
        try { await Task.Delay(TimeSpan.FromMinutes(1), stop); } catch (TaskCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<CloudBackupService>();
                var cfg = await svc.ConfigAsync();
                if (cfg.Enabled && cfg.Ready && !CloudBackupService.IsRunning)
                {
                    var zone = await ZoneAsync(scope.ServiceProvider.GetRequiredService<SettingsService>());
                    var now = DateTime.UtcNow;
                    // The slot that is due: the next one after the last run (or after a day ago
                    // when it never ran) that lies in the past, but at most 6 hours ago, so a
                    // server that was stopped for days does not start an old run at noon.
                    var from = cfg.LastRunUtc ?? now.AddDays(-1);
                    var due = CloudBackupService.NextRunUtc(cfg, zone, from);
                    if (due is { } d && d <= now && now - d < TimeSpan.FromHours(6))
                        await svc.RunAsync("schedule", stop);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested)
            {
                _log.LogWarning(ex, "backup scheduler run failed");
            }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stop); } catch (TaskCanceledException) { break; }
        }
    }

    public static async Task<TimeZoneInfo> ZoneAsync(SettingsService settings)
    {
        var id = await settings.GetAsync("App.TimeZone", "App:TimeZone");
        if (string.IsNullOrWhiteSpace(id)) id = TimeDisplay.DefaultZone;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return TimeZoneInfo.Utc; }
    }
}
