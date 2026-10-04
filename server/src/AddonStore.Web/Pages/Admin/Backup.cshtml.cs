using System.Text.RegularExpressions;
using AddonStore.Web.Data;
using AddonStore.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AddonStore.Web.Pages.Admin;

[RequestSizeLimit(4L * 1024 * 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = 4L * 1024 * 1024 * 1024)]
public class BackupModel : PageModel
{
    private readonly BackupService _backup;
    private readonly CloudBackupService _cloud;
    private readonly SettingsService _settings;
    private readonly UserManager<AppUser> _users;
    private readonly AuditService _audit;
    private readonly IServiceScopeFactory _scopes;

    /// <summary>Sections of the left navigation (S0.18.0).</summary>
    public static readonly (string Key, string Label)[] Views =
    {
        ("auto", "Automatic backup"), ("cloud", "Backups in Azure"), ("manual", "Manual backup"),
        ("restore", "Restore"), ("safety", "Safety backups"),
    };
    [BindProperty(SupportsGet = true)] public string? View { get; set; }

    public string? Notice { get; private set; }
    public string NoticeKind { get; private set; } = "ok";
    public List<(string Name, long Size, DateTime Utc)> SafetyBackups { get; } = new();
    public CloudBackupService.Config Cloud { get; private set; } = null!;
    public DateTime? NextRunUtc { get; private set; }
    public List<CloudBackupService.BackupBlob> CloudBackups { get; } = new();
    public string? CloudListError { get; private set; }
    public bool CloudRunning => CloudBackupService.IsRunning;

    public BackupModel(BackupService backup, CloudBackupService cloud, SettingsService settings,
                       UserManager<AppUser> users, AuditService audit, IServiceScopeFactory scopes)
    {
        _backup = backup; _cloud = cloud; _settings = settings; _users = users; _audit = audit; _scopes = scopes;
    }

    public async Task OnGetAsync() => await LoadAsync();

    private const int MinKeyPassword = 12;

    // A password is optional; when given it must be long enough to protect the key ring.
    private bool BadPassword(string? password) => !string.IsNullOrEmpty(password) && password.Length < MinKeyPassword;

    // ---- automatic backup to Azure Blob Storage ----------------------------

    public async Task OnPostCloudSaveAsync(bool enabled, string? connection, string? container, string? passphrase,
                                           string? time, string[]? days, int keep)
    {
        View = "auto";
        var admin = await _users.GetUserAsync(User);
        var error = await _cloud.SaveAsync(enabled, connection, container, passphrase, time, days, keep);
        var cfg = await _cloud.ConfigAsync();
        await _audit.LogAsync(admin!.DisplayName, "settings.changed", "Automatic backup",
            $"enabled {cfg.Enabled}, account {cfg.Account}, container {cfg.Container}, {cfg.Time:HH\\:mm} on days {cfg.Days}, keep {cfg.Keep}" +
            (string.IsNullOrEmpty(connection) ? "" : ", access key changed") + (string.IsNullOrEmpty(passphrase) ? "" : ", passphrase changed"));
        if (error is not null && !error.StartsWith("Saved.")) { Fail(error); await LoadAsync(); return; }
        Notice = error ?? "Settings saved.";
        NoticeKind = error is null ? "ok" : "warn";
        await LoadAsync();
    }

    public async Task OnPostCloudTestAsync()
    {
        View = "auto";
        var (ok, message) = await _cloud.TestAsync(HttpContext.RequestAborted);
        Notice = ok ? "Connection works: {0}" : "The connection failed: {0}";
        NoticeArg = message;
        NoticeKind = ok ? "ok" : "error";
        await LoadAsync();
    }

    /// <summary>Starts a backup in the background (uploads can take longer than a request may).</summary>
    public async Task OnPostCloudRunAsync()
    {
        View = "auto";
        var admin = await _users.GetUserAsync(User);
        if (CloudBackupService.IsRunning) { Notice = "A backup is already running."; NoticeKind = "warn"; await LoadAsync(); return; }
        if (!(await _cloud.ConfigAsync()).Ready) { Fail("Enter the access key and the passphrase first."); await LoadAsync(); return; }
        var name = admin!.DisplayName;
        var scopes = _scopes;
        _ = Task.Run(async () =>
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<CloudBackupService>().RunAsync(name);
        });
        await Task.Delay(300);   // let the run take the lock, so the page shows "running"
        Notice = "The backup started. Reload the page in a minute to see the result.";
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostCloudDownloadAsync(string name)
    {
        if (!CloudBackupService.IsBackupName(name)) return NotFound();
        var admin = await _users.GetUserAsync(User);
        var tmp = Path.Combine(Path.GetTempPath(), "pluginstore-cloud-dl-" + Guid.NewGuid().ToString("N") + BackupCrypto.Extension);
        try { await _cloud.DownloadAsync(name, tmp, HttpContext.RequestAborted); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try { System.IO.File.Delete(tmp); } catch { }
            View = "cloud"; Fail("The backup could not be downloaded."); await LoadAsync(); return Page();
        }
        await _audit.LogAsync(admin!.DisplayName, "backup.cloud.downloaded", name);
        var stream = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.None, 81920, FileOptions.DeleteOnClose);
        return File(stream, "application/octet-stream", name);
    }

    /// <summary>Restores a backup straight from the container with the stored passphrase.</summary>
    public async Task OnPostCloudRestoreAsync(string name, string? confirm)
    {
        View = "cloud";
        if (!CloudBackupService.IsBackupName(name)) { Fail("No backup file received."); await LoadAsync(); return; }
        if (confirm != "RESTORE") { Fail("Type RESTORE to confirm."); await LoadAsync(); return; }
        var admin = await _users.GetUserAsync(User);
        var tmp = Path.Combine(Path.GetTempPath(), "pluginstore-cloud-rs-" + Guid.NewGuid().ToString("N") + BackupCrypto.Extension);
        try
        {
            await _cloud.DownloadAsync(name, tmp, HttpContext.RequestAborted);
            await RestoreFileAsync(tmp, name, admin!, await _cloud.PassphraseAsync(), withoutKeys: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail("The backup could not be downloaded.");
        }
        finally { try { System.IO.File.Delete(tmp); } catch { } }
        await LoadAsync();
    }

    // ---- manual download ---------------------------------------------------

    // POST (antiforgery-protected) instead of a GET link: the archive carries the key ring.
    public async Task<IActionResult> OnPostDownloadAsync(string? keyPassword)
    {
        View = "manual";
        if (BadPassword(keyPassword)) { Fail("The password must have at least 12 characters."); await LoadAsync(); return Page(); }
        var admin = await _users.GetUserAsync(User);
        var full = Path.Combine(Path.GetTempPath(), "pluginstore-backup-" + Guid.NewGuid().ToString("N") + ".zip");
        var tmp = full + ".out";
        try
        {
            await _backup.CreateAsync(full, admin!.DisplayName);
            await _backup.ExportAsync(full, tmp, string.IsNullOrEmpty(keyPassword) ? null : keyPassword);
        }
        finally { try { System.IO.File.Delete(full); } catch { } }
        await _audit.LogAsync(admin.DisplayName, "backup.created", Path.GetFileName(tmp),
            $"{new FileInfo(tmp).Length / 1024} KB, key ring " + (string.IsNullOrEmpty(keyPassword) ? "omitted" : "encrypted"));
        var stream = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.None, 81920, FileOptions.DeleteOnClose);
        return File(stream, "application/zip", $"pluginstore-backup-{DateTime.UtcNow:yyyyMMdd-HHmm}.zip");
    }

    public async Task<IActionResult> OnPostSafetyAsync(string name, string? keyPassword)
    {
        View = "safety";
        if (!Regex.IsMatch(name ?? "", @"^pre-restore-\d{8}-\d{6}\.zip$")) return NotFound();
        var path = Path.Combine(_backup.SafetyRoot, name!);
        if (!System.IO.File.Exists(path)) return NotFound();
        if (BadPassword(keyPassword)) { Fail("The password must have at least 12 characters."); await LoadAsync(); return Page(); }
        var admin = await _users.GetUserAsync(User);
        var tmp = Path.Combine(Path.GetTempPath(), "pluginstore-safety-" + Guid.NewGuid().ToString("N") + ".zip");
        await _backup.ExportAsync(path, tmp, string.IsNullOrEmpty(keyPassword) ? null : keyPassword);
        await _audit.LogAsync(admin!.DisplayName, "backup.downloaded", name!,
            "key ring " + (string.IsNullOrEmpty(keyPassword) ? "omitted" : "encrypted"));
        var stream = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.None, 81920, FileOptions.DeleteOnClose);
        return File(stream, "application/zip", name);
    }

    // ---- restore -----------------------------------------------------------

    /// <summary>Restores an uploaded backup: a .zip, or an encrypted .psbak from the automatic backup.</summary>
    public async Task OnPostRestoreAsync(IFormFile? archive, string? confirm, string? keyPassword, bool withoutKeys)
    {
        View = "restore";
        var admin = await _users.GetUserAsync(User);
        if (archive is null || archive.Length == 0) { Fail("No backup file received."); await LoadAsync(); return; }
        if (confirm != "RESTORE") { Fail("Type RESTORE to confirm."); await LoadAsync(); return; }
        var tmp = Path.Combine(Path.GetTempPath(), "pluginstore-restore-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var fs = System.IO.File.Create(tmp))
                await archive.CopyToAsync(fs);
            await RestoreFileAsync(tmp, archive.FileName, admin!, keyPassword, withoutKeys);
        }
        finally { try { System.IO.File.Delete(tmp); } catch { } }
        await LoadAsync();
    }

    /// <summary>
    /// The restore itself, shared by upload and cloud: decrypts a .psbak with
    /// the passphrase first, checks the archive, checks the key ring password
    /// BEFORE anything is replaced, then restores.
    /// </summary>
    private async Task RestoreFileAsync(string path, string label, AppUser admin, string? keyPassword, bool withoutKeys)
    {
        string? plain = null;
        try
        {
            if (BackupCrypto.IsEncrypted(path))
            {
                if (string.IsNullOrEmpty(keyPassword)) { Fail("This backup is encrypted. Enter its passphrase."); return; }
                plain = path + ".zip";
                if (!await BackupCrypto.DecryptAsync(path, plain, keyPassword))
                {
                    await _audit.LogAsync(admin.DisplayName, "backup.restore-refused", label, "wrong passphrase or damaged file");
                    Fail("The passphrase is wrong or the file is damaged.");
                    return;
                }
                path = plain;
            }

            var check = _backup.Inspect(path, admin.Id);
            if (!check.Ok)
            {
                await _audit.LogAsync(admin.DisplayName, "backup.restore-refused", label, check.Message);
                Fail(check.Message);
                return;
            }
            byte[]? keys = null;
            var keysSkipped = false;
            if (BackupService.HasEncryptedKeys(path))
            {
                if (string.IsNullOrEmpty(keyPassword))
                {
                    // Without the key ring the package signing key is lost too: clients then refuse
                    // every install until the recovery key is set. Only on explicit request.
                    if (!withoutKeys)
                    {
                        Fail("This backup contains the key ring. Enter its password, or confirm the restore without it.");
                        return;
                    }
                    keysSkipped = true;
                }
                else if ((keys = BackupService.DecryptKeys(path, keyPassword)) is null)
                {
                    await _audit.LogAsync(admin.DisplayName, "backup.restore-refused", label, "wrong key ring password");
                    Fail("The password for the key ring is wrong.");
                    return;
                }
            }
            var safety = await _backup.RestoreAsync(path, admin.DisplayName, keys);
            // An older backup may lack newer columns, tables or role names:
            // upgrade it now instead of at the next app start.
            using (var scope = _scopes.CreateScope())
                await SchemaUpgrade.RunAsync(scope.ServiceProvider);
            // The audit table itself was just replaced; record the restore in the restored log.
            await _audit.LogAsync(admin.DisplayName, "backup.restored", label, "safety backup: " + Path.GetFileName(safety));
            Notice = keysSkipped
                ? "Backup restored without its key ring. Enter the AI and Resend keys again and set the signing recovery key (Signing__PrivateKeyPem); until then clients cannot install."
                : "Backup restored. A safety backup of the previous state was kept on the server.";
            NoticeKind = "ok";
            // Restart so every connection, cache and the restored key ring start clean
            // (App Service starts the container again by itself). Not in development.
            if (!HttpContext.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment())
            {
                if (!keysSkipped) Notice = "Backup restored. The server restarts now to load it completely; sign in again in a minute.";
                var life = HttpContext.RequestServices.GetRequiredService<IHostApplicationLifetime>();
                _ = Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(3)); life.StopApplication(); });
            }
        }
        finally
        {
            if (plain is not null) try { System.IO.File.Delete(plain); } catch { }
        }
    }

    // ---- page state -------------------------------------------------------

    /// <summary>Argument for {0} in the notice (shown as text, never as markup).</summary>
    public string? NoticeArg { get; private set; }

    private void Fail(string message)
    {
        Notice = message;
        NoticeKind = "error";
    }

    private async Task LoadAsync()
    {
        if (!Views.Any(v => v.Key == View)) View = "auto";
        Cloud = await _cloud.ConfigAsync();
        NextRunUtc = CloudBackupService.NextRunUtc(Cloud, await CloudBackupScheduler.ZoneAsync(_settings), DateTime.UtcNow);
        if (View == "cloud" && Cloud.HasConnection)
        {
            try { CloudBackups.AddRange(await _cloud.ListAsync(HttpContext.RequestAborted)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { CloudListError = ex is Azure.RequestFailedException rf ? $"{rf.Status} {rf.ErrorCode}" : ex.GetType().Name; }
        }
        SafetyBackups.Clear();
        if (!Directory.Exists(_backup.SafetyRoot)) return;
        foreach (var f in new DirectoryInfo(_backup.SafetyRoot).GetFiles("pre-restore-*.zip")
                     .OrderByDescending(f => f.LastWriteTimeUtc))
            SafetyBackups.Add((f.Name, f.Length, f.LastWriteTimeUtc));
    }
}
