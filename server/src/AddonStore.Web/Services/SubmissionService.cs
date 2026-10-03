using System.Security.Cryptography;
using AddonStore.Web.Data;
using AddonStore.Web.Validation;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Services;

public record SubmissionResult(ValidationReport Report, PackageVersion? Version, string? ErrorCode = null, string? ErrorHint = null);

/// <summary>
/// Shared submission flow for the web upload and the API: validate, store,
/// enter the beta channel, audit, notify. Dry-run validation uses the same path
/// without persisting anything.
/// </summary>
public class SubmissionService
{
    private readonly AppDbContext _db;
    private readonly AuditService _audit;
    private readonly NotificationService _notify;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;

    public SubmissionService(AppDbContext db, AuditService audit, NotificationService notify,
        IConfiguration config, IWebHostEnvironment env)
    {
        _db = db; _audit = audit; _notify = notify; _config = config; _env = env;
    }

    public string StorageRoot
    {
        get
        {
            var root = _config["Storage:Root"];
            if (string.IsNullOrWhiteSpace(root))
                root = Path.Combine(_env.ContentRootPath, "data", "packages");
            Directory.CreateDirectory(root);
            return root;
        }
    }

    public async Task<ValidationReport> ValidateOnlyAsync(string zipPath, AppUser user)
    {
        var validator = new PackageValidator(_db);
        var (report, _) = await validator.ValidateAsync(zipPath, user.Id);
        return report;
    }

    public async Task<SubmissionResult> SubmitAsync(string zipPath, AppUser user, string via)
    {
        var validator = new PackageValidator(_db);
        var (report, manifest) = await validator.ValidateAsync(zipPath, user.Id);
        if (!report.Passed || manifest is null)
            return new SubmissionResult(report, null);

        // Idempotency: the same version again is a clear, named condition.
        var existing = await _db.PackageVersions
            .FirstOrDefaultAsync(v => v.PackageId == manifest.Id && v.Version == manifest.Version);
        if (existing is not null)
        {
            var zipHash = await HashFileAsync(zipPath);
            var identical = string.Equals(existing.Sha256, zipHash, StringComparison.OrdinalIgnoreCase);
            return new SubmissionResult(report, null, "VERSION_EXISTS",
                identical
                    ? $"Version {manifest.Version} with this exact content is already on the server (status: {existing.Status}). Nothing to do."
                    : $"Version {manifest.Version} already exists with different content. Bump 'version' in the manifest and upload again.");
        }

        var package = await _db.Packages.FirstOrDefaultAsync(p => p.Id == manifest.Id);
        if (package is null)
        {
            package = new Package { Id = manifest.Id, OwnerId = user.Id };
            _db.Packages.Add(package);
        }

        var dir = Path.Combine(StorageRoot, manifest.Id);
        Directory.CreateDirectory(dir);
        var fileName = $"{manifest.Id}-{manifest.Version}.ppak";
        var target = Path.Combine(dir, fileName);
        File.Copy(zipPath, target, overwrite: true);

        var version = new PackageVersion
        {
            PackageId = manifest.Id,
            Version = manifest.Version,
            Status = VersionStatus.Beta,
            Changelog = manifest.Changelog,
            ManifestJson = manifest.RawJson,
            AtomNamespace = manifest.AtomNamespace,
            MinPowerPdfVersion = manifest.MinPowerPdfVersion,
            FilePath = Path.Combine(manifest.Id, fileName),
            Sha256 = await HashFileAsync(target),
            SizeBytes = new FileInfo(target).Length,
            SubmittedBy = user.DisplayName,
            SubmittedVia = via,
            ValidationReportJson = report.ToJson()
        };
        _db.PackageVersions.Add(version);
        await _db.SaveChangesAsync();

        await _audit.LogAsync(user.DisplayName, "package.submitted",
            $"{manifest.Id} {manifest.Version}",
            $"via {via}; changelog: {Truncate(manifest.Changelog, 300)}; warnings: {report.Findings.Count(f => f.Severity == "warning")}");

        await _notify.NotifyAdminsAsync(
            $"[Add-on Store] New submission: {manifest.Id} {manifest.Version}",
            $"<p><b>{user.DisplayName}</b> submitted <b>{manifest.Id} {manifest.Version}</b> (via {via}).</p>" +
            $"<p>Changelog: {System.Net.WebUtility.HtmlEncode(manifest.Changelog)}</p>" +
            "<p>The version passed all automatic checks and is now in the beta channel, awaiting review.</p>");

        return new SubmissionResult(report, version);
    }

    public static async Task<string> HashFileAsync(string path)
    {
        await using var fs = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(fs)).ToLowerInvariant();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
