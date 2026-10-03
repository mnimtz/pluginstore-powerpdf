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

    public async Task<IActionResult> OnGetDownloadAsync()
    {
        var admin = await _users.GetUserAsync(User);
        var tmp = Path.Combine(Path.GetTempPath(), "pluginstore-backup-" + Guid.NewGuid().ToString("N") + ".zip");
        await _backup.CreateAsync(tmp, admin!.DisplayName);
        await _audit.LogAsync(admin.DisplayName, "backup.created", Path.GetFileName(tmp), $"{new FileInfo(tmp).Length / 1024} KB");
        var stream = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.None, 81920, FileOptions.DeleteOnClose);
        return File(stream, "application/zip", $"pluginstore-backup-{DateTime.UtcNow:yyyyMMdd-HHmm}.zip");
    }

    public async Task<IActionResult> OnGetSafetyAsync(string name)
    {
        if (!Regex.IsMatch(name ?? "", @"^pre-restore-\d{8}-\d{6}\.zip$")) return NotFound();
        var path = Path.Combine(_backup.SafetyRoot, name!);
        if (!System.IO.File.Exists(path)) return NotFound();
        var admin = await _users.GetUserAsync(User);
        await _audit.LogAsync(admin!.DisplayName, "backup.downloaded", name!);
        return PhysicalFile(path, "application/zip", name);
    }

    public async Task OnPostRestoreAsync(IFormFile? archive, string? confirm)
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
            var safety = await _backup.RestoreAsync(tmp, admin.DisplayName);
            // An older backup may lack newer columns, tables or role names:
            // upgrade it now instead of at the next app start.
            using (var scope = _scopes.CreateScope())
                await SchemaUpgrade.RunAsync(scope.ServiceProvider);
            // The audit table itself was just replaced; record the restore in the restored log.
            await _audit.LogAsync(admin.DisplayName, "backup.restored", archive.FileName,
                "safety backup: " + Path.GetFileName(safety));
            Notice = "Backup restored. A safety backup of the previous state was kept on the server.";
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
