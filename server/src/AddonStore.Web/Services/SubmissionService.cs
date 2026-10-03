using System.Security.Cryptography;
using AddonStore.Web.Data;
using AddonStore.Web.Validation;
using Microsoft.AspNetCore.Identity;
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
    /// <summary>The store client's own package: admin-only, no review queue.</summary>
    public const string ClientPackageId = "com.tungsten.pluginstore";

    private readonly AppDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly AuditService _audit;
    private readonly NotificationService _notify;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;

    public SubmissionService(AppDbContext db, UserManager<AppUser> users, AuditService audit,
        NotificationService notify, IConfiguration config, IWebHostEnvironment env)
    {
        _db = db; _users = users; _audit = audit; _notify = notify; _config = config; _env = env;
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

        // The store client has its own lane: only admins publish it, and a new
        // version goes live immediately (it is our own tooling, not a third-
        // party plugin waiting for review).
        var isClient = manifest.Id == ClientPackageId;
        if (isClient && !await _users.IsInRoleAsync(user, "Admin"))
            return new SubmissionResult(report, null, "CLIENT_ADMIN_ONLY",
                "Only administrators can publish new versions of the Add-on Store client.");

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
            Status = isClient ? VersionStatus.Live : VersionStatus.Beta,
            ReviewedById = isClient ? user.Id : null,
            ReviewedAt = isClient ? DateTime.UtcNow : null,
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

        if (isClient)
        {
            // Older client versions still waiting in beta are superseded.
            var stale = await _db.PackageVersions
                .Where(v => v.PackageId == ClientPackageId && v.Status == VersionStatus.Beta)
                .ToListAsync();
            foreach (var v in stale) v.Status = VersionStatus.Withdrawn;
            await _db.SaveChangesAsync();
            await _audit.LogAsync(user.DisplayName, "client.released", manifest.Version,
                $"via {via}; superseded beta versions: {stale.Count}");
            await _notify.NotifyStaffAsync("ClientRelease",
                $"[Add-on Store] Client {manifest.Version} released",
                $"<p><b>{System.Net.WebUtility.HtmlEncode(user.DisplayName)}</b> released Add-on Store client <b>{manifest.Version}</b>. " +
                "Installed clients offer the update now; the landing page serves the new installer.</p>");
            return new SubmissionResult(report, version);
        }

        await _db.SaveChangesAsync();

        await _audit.LogAsync(user.DisplayName, "package.submitted",
            $"{manifest.Id} {manifest.Version}",
            $"via {via}; changelog: {Truncate(manifest.Changelog, 300)}; warnings: {report.Findings.Count(f => f.Severity == "warning")}");

        await _notify.NotifyStaffAsync("Submission",
            $"[Add-on Store] New submission: {manifest.Id} {manifest.Version}",
            $"<p><b>{System.Net.WebUtility.HtmlEncode(user.DisplayName)}</b> submitted <b>{manifest.Id} {manifest.Version}</b> (via {System.Net.WebUtility.HtmlEncode(via)}).</p>" +
            $"<p>Changelog: {System.Net.WebUtility.HtmlEncode(manifest.Changelog)}</p>" +
            "<p>The version passed all automatic checks and is now in the beta channel, awaiting review.</p>",
            includeReviewers: true);

        return new SubmissionResult(report, version);
    }

    public static async Task<string> HashFileAsync(string path)
    {
        await using var fs = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(fs)).ToLowerInvariant();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
