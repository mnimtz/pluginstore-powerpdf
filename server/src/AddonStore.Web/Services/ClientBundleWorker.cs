using System.IO.Compression;
using System.Text.Json;
using AddonStore.Web.Data;
using AddonStore.Web.Validation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Services;

/// <summary>
/// The store client comes with the server (S1.15.0). Every client release puts its package (.ppak plus the source
/// ZIP of the same version) into packaging/client-bundle/ in the repository, and the container image carries that
/// folder (ClientBundle__Path=/app/client-bundle). When this server knows no version of the client yet, or an older
/// one, it takes the bundled one in through the normal upload path: the same checks, the client lane (live at once,
/// older betas withdrawn), the source stored, an audit entry and the staff email. A fresh installation therefore
/// offers its installer as soon as the first administrator exists.
/// Off when ClientBundle:Path is not set (local development and tests).
/// </summary>
public sealed class ClientBundleWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ClientBundleWorker> _log;
    private readonly string? _path;
    private string? _failed;   // a bundled version that did not pass: not tried again until the next start

    public ClientBundleWorker(IServiceScopeFactory scopes, ILogger<ClientBundleWorker> log, IConfiguration config)
    {
        _scopes = scopes; _log = log;
        var p = config["ClientBundle:Path"];
        _path = string.IsNullOrWhiteSpace(p) ? null : p;
    }

    /// <summary>The bundled package: the one .ppak in the folder, its version and the source ZIP next to it.</summary>
    public static (string Ppak, string Version, string? Source)? Find(string? dir)
    {
        if (dir is null || !Directory.Exists(dir)) return null;
        var ppaks = Directory.GetFiles(dir, SubmissionService.ClientPackageId + "-*.ppak");
        if (ppaks.Length != 1) return null;   // none, or more than one: nothing is taken (and logged)
        try
        {
            using var zip = ZipFile.OpenRead(ppaks[0]);
            var entry = zip.GetEntry("manifest.json");
            if (entry is null || entry.Length > 1024 * 1024) return null;
            using var s = entry.Open();
            using var doc = JsonDocument.Parse(s);
            var root = doc.RootElement;
            if (root.GetProperty("id").GetString() != SubmissionService.ClientPackageId) return null;
            var version = root.GetProperty("version").GetString() ?? "";
            var source = Path.Combine(dir, $"{SubmissionService.ClientPackageId}-{version}.source.zip");
            return (ppaks[0], version, File.Exists(source) ? source : null);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (_path is null) return;
        try { await Task.Delay(TimeSpan.FromSeconds(20), stop); } catch (TaskCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            var wait = TimeSpan.FromMinutes(10);
            try
            {
                var r = await RunOnceAsync();
                if (r == "no-admin") wait = TimeSpan.FromMinutes(1);   // set-up not finished yet: look again soon
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested)
            {
                _log.LogWarning(ex, "client bundle check failed");
            }
            try { await Task.Delay(wait, stop); } catch (TaskCanceledException) { break; }
        }
    }

    /// <summary>One check. Returns what happened: none, current, failed, no-admin, imported.</summary>
    public async Task<string> RunOnceAsync()
    {
        var b = Find(_path);
        if (b is null)
        {
            if (_path is not null && Directory.Exists(_path) && Directory.GetFiles(_path, "*.ppak").Length > 1)
                _log.LogWarning("client bundle: more than one .ppak in {Path}, none taken", _path);
            return "none";
        }
        var (ppak, version, source) = b.Value;
        if (version == _failed) return "failed";
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var known = await db.PackageVersions.AsNoTracking()
            .Where(v => v.PackageId == SubmissionService.ClientPackageId).Select(v => v.Version).ToListAsync();
        // a version that exists in any status (also withdrawn on purpose) or a newer one: nothing to do
        var cmp = new SemVerComparer();
        if (known.Any(k => cmp.Compare(k, version) >= 0)) return "current";

        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var admin = (await users.GetUsersInRoleAsync("Admin"))
            .Where(u => u.Status == UserStatus.Active).OrderBy(u => u.CreatedAt).FirstOrDefault();
        if (admin is null) return "no-admin";

        var result = await scope.ServiceProvider.GetRequiredService<SubmissionService>().SubmitAsync(ppak, admin, "bundle");
        var audit = scope.ServiceProvider.GetRequiredService<AuditService>();
        if (result.ErrorCode == "VERSION_EXISTS") return "current";   // a manual upload of the same version came first (S1.17.3)
        if (result.Version is null)
        {
            _failed = version;
            var errors = string.Join(", ", result.Report.Findings.Where(f => f.Severity == "error").Select(f => f.Code).Take(10));
            _log.LogWarning("client bundle {Version} not taken: {Code} {Errors}", version, result.ErrorCode, errors);
            await audit.LogAsync("system", "client.bundle.failed", version, $"{result.ErrorCode} {errors}".Trim());
            return "failed";
        }
        var stored = source is not null
            && (await scope.ServiceProvider.GetRequiredService<SourceService>().UploadAsync(result.Version, source, admin, true)).Passed;
        await audit.LogAsync("system", "client.bundle.imported", version,
            $"from the server image, released under {admin.DisplayName}; source {(source is null ? "missing" : stored ? "stored" : "refused by the source check")}");
        _log.LogInformation("client bundle {Version} released", version);
        return "imported";
    }
}
