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
    private readonly UserManager<AppUser> _users;
    private readonly AuditService _audit;
    private readonly IServiceScopeFactory _scopes;

    public string? Notice { get; private set; }
    public string NoticeKind { get; private set; } = "ok";
    public List<(string Name, long Size, DateTime Utc)> SafetyBackups { get; } = new();

    public BackupModel(BackupService backup, UserManager<AppUser> users, AuditService audit,
                       IServiceScopeFactory scopes)
    {
        _backup = backup; _users = users; _audit = audit; _scopes = scopes;
    }

    public void OnGet() => Load();

    private const int MinKeyPassword = 12;

    // A password is optional; when given it must be long enough to protect the key ring.
    private bool BadPassword(string? password) => !string.IsNullOrEmpty(password) && password.Length < MinKeyPassword;

    // POST (antiforgery-protected) instead of a GET link: the archive carries the key ring.
    public async Task<IActionResult> OnPostDownloadAsync(string? keyPassword)
    {
        if (BadPassword(keyPassword)) { Fail("The password must have at least 12 characters."); return Page(); }
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
        if (!Regex.IsMatch(name ?? "", @"^pre-restore-\d{8}-\d{6}\.zip$")) return NotFound();
        var path = Path.Combine(_backup.SafetyRoot, name!);
        if (!System.IO.File.Exists(path)) return NotFound();
        if (BadPassword(keyPassword)) { Fail("The password must have at least 12 characters."); return Page(); }
        var admin = await _users.GetUserAsync(User);
        var tmp = Path.Combine(Path.GetTempPath(), "pluginstore-safety-" + Guid.NewGuid().ToString("N") + ".zip");
        await _backup.ExportAsync(path, tmp, string.IsNullOrEmpty(keyPassword) ? null : keyPassword);
        await _audit.LogAsync(admin!.DisplayName, "backup.downloaded", name!,
            "key ring " + (string.IsNullOrEmpty(keyPassword) ? "omitted" : "encrypted"));
        var stream = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.None, 81920, FileOptions.DeleteOnClose);
        return File(stream, "application/zip", name);
    }

    public async Task OnPostRestoreAsync(IFormFile? archive, string? confirm, string? keyPassword)
    {
        var admin = await _users.GetUserAsync(User);
        if (archive is null || archive.Length == 0)
        {
            Fail("No backup file received.");
            return;
        }
        if (confirm != "RESTORE")
        {
            Fail("Type RESTORE to confirm.");
            return;
        }

        var tmp = Path.Combine(Path.GetTempPath(), "pluginstore-restore-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await using (var fs = System.IO.File.Create(tmp))
                await archive.CopyToAsync(fs);

            var check = _backup.Inspect(tmp, admin!.Id);
            if (!check.Ok)
            {
                await _audit.LogAsync(admin.DisplayName, "backup.restore-refused", archive.FileName, check.Message);
                Fail(check.Message);
                return;
            }
            // Encrypted key ring: check the password BEFORE anything is replaced.
            byte[]? keys = null;
            var keysSkipped = false;
            if (BackupService.HasEncryptedKeys(tmp))
            {
                if (string.IsNullOrEmpty(keyPassword)) keysSkipped = true;
                else if ((keys = BackupService.DecryptKeys(tmp, keyPassword)) is null)
                {
                    await _audit.LogAsync(admin.DisplayName, "backup.restore-refused", archive.FileName, "wrong key ring password");
                    Fail("The password for the key ring is wrong.");
                    return;
                }
            }
            var safety = await _backup.RestoreAsync(tmp, admin.DisplayName, keys);
            // An older backup may lack newer columns, tables or role names:
            // upgrade it now instead of at the next app start.
            using (var scope = _scopes.CreateScope())
                await SchemaUpgrade.RunAsync(scope.ServiceProvider);
            // The audit table itself was just replaced; record the restore in the restored log.
            await _audit.LogAsync(admin.DisplayName, "backup.restored", archive.FileName,
                "safety backup: " + Path.GetFileName(safety));
            Notice = keysSkipped
                ? "Backup restored without its key ring (no password given). Enter the AI and Resend keys again in the settings."
                : "Backup restored. A safety backup of the previous state was kept on the server.";
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
            try { System.IO.File.Delete(tmp); } catch { }
        }
        Load();
    }

    private void Fail(string message)
    {
        Notice = message;
        NoticeKind = "error";
        Load();
    }

    private void Load()
    {
        SafetyBackups.Clear();
        if (!Directory.Exists(_backup.SafetyRoot)) return;
        foreach (var f in new DirectoryInfo(_backup.SafetyRoot).GetFiles("pre-restore-*.zip")
                     .OrderByDescending(f => f.LastWriteTimeUtc))
            SafetyBackups.Add((f.Name, f.Length, f.LastWriteTimeUtc));
    }
}
