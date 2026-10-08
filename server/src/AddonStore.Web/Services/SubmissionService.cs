using System.Security.Cryptography;
using AddonStore.Web.Data;
using AddonStore.Web.Validation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Services;

public record SubmissionResult(ValidationReport Report, PackageVersion? Version, string? ErrorCode = null, string? ErrorHint = null);

/// <summary>
/// Shared submission flow for the web upload and the API: validate, store,
/// wait for review (S1.6.0), audit, notify. Dry-run validation uses the same path
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
    private readonly CategoryService _categories;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;

    public SubmissionService(AppDbContext db, UserManager<AppUser> users, AuditService audit,
        NotificationService notify, IConfiguration config, IWebHostEnvironment env, CategoryService categories)
    {
        _db = db; _users = users; _audit = audit; _notify = notify; _categories = categories; _config = config; _env = env;
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
        var validator = new PackageValidator(_db, _categories);
        var (report, _) = await validator.ValidateAsync(zipPath, user.Id, await _users.IsInRoleAsync(user, "Admin"));
        return report;
    }

    // One submission per package at a time (two uploads of the same new version would race).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

    public async Task<SubmissionResult> SubmitAsync(string zipPath, AppUser user, string via)
    {
        var validator = new PackageValidator(_db, _categories);
        var (report, manifest) = await validator.ValidateAsync(zipPath, user.Id, await _users.IsInRoleAsync(user, "Admin"));
        if (manifest is not null && manifest.Id.Length > 0 && report.Findings.Any(f => f.Code == "VERSION_EXISTS"))
            return await ExistingVersionAsync(zipPath, manifest, report);
        if (!report.Passed || manifest is null)
            return new SubmissionResult(report, null);

        var gate = Locks.GetOrAdd(manifest.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try { return await SubmitCheckedAsync(zipPath, user, via, report, manifest); }
        finally { gate.Release(); }
    }

    /// <summary>The same version again is a clear, named condition (409), with or without identical content.</summary>
    private async Task<SubmissionResult> ExistingVersionAsync(string zipPath, ParsedManifest manifest, ValidationReport report)
    {
        var existing = await _db.PackageVersions.AsNoTracking()
            .FirstOrDefaultAsync(v => v.PackageId == manifest.Id && v.Version == manifest.Version);
        var identical = existing is not null && string.Equals(existing.Sha256, await HashFileAsync(zipPath), StringComparison.OrdinalIgnoreCase);
        return new SubmissionResult(report, null, "VERSION_EXISTS",
            identical
                ? $"Version {manifest.Version} with this exact content is already on the server (status: {existing!.Status}). Nothing to do."
                : $"Version {manifest.Version} already exists with different content. Bump 'version' in the manifest and upload again.");
    }

    private async Task<SubmissionResult> SubmitCheckedAsync(string zipPath, AppUser user, string via, ValidationReport report, ParsedManifest manifest)
    {

        // The store client has its own lane: only admins publish it, and a new
        // version goes live immediately (it is our own tooling, not a third-
        // party plugin waiting for review).
        var isClient = manifest.Id == ClientPackageId;
        if (isClient && !await _users.IsInRoleAsync(user, "Admin"))
            return new SubmissionResult(report, null, "CLIENT_ADMIN_ONLY",
                "Only administrators can publish new versions of the Add-on Store client.");

        // checked again under the lock: another upload of the same version may have won
        if (await _db.PackageVersions.AnyAsync(v => v.PackageId == manifest.Id && v.Version == manifest.Version))
            return await ExistingVersionAsync(zipPath, manifest, report);

        var package = await _db.Packages.FirstOrDefaultAsync(p => p.Id == manifest.Id);
        if (package is null)
        {
            // visibility comes from the first upload; later it is changed in the
            // portal or with PATCH /api/packages/{id}, never by a new version
            package = new Package { Id = manifest.Id, OwnerId = user.Id, Visibility = isClient ? "public" : manifest.Visibility };
            _db.Packages.Add(package);
        }
        else if (manifest.Visibility != package.Visibility && !isClient)
            report.Info("VISIBILITY_KEPT", $"The add-on stays {package.Visibility}; the manifest says {manifest.Visibility}.",
                "Visibility is set by the first upload. Change it on the plug-in page or with PATCH /api/packages/{id} {\"visibility\": \"private\"}.");

        var dir = Path.Combine(StorageRoot, manifest.Id);
        Directory.CreateDirectory(dir);
        var fileName = $"{manifest.Id}-{manifest.Version}.ppak";
        var target = Path.Combine(dir, fileName);
        File.Copy(zipPath, target, overwrite: true);

        var version = new PackageVersion
        {
            PackageId = manifest.Id,
            Version = manifest.Version,
            // waits for review (S1.6.0): no client gets it until a reviewer approves it for beta or live
            // (private add-ons too since S1.11.0; only the developer's test code shows it before the approval)
            Status = isClient ? VersionStatus.Live : VersionStatus.Submitted,
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
            SubmittedById = user.Id,
            SubmittedVia = via,
            ValidationReportJson = report.ToJson(),
            RulesSnapshotJson = Validation.RuleCatalog.SnapshotJson(),   // the rules it was checked against (S1.3.0)
        };
        _db.PackageVersions.Add(version);
        if (manifest.NewCategoryNames is not null)
            await _categories.CreateAsync(manifest.Category, manifest.NewCategoryNames, user, manifest.Id);

        // The uploader's compliance statement is kept under their name; the
        // server-side scans do not rely on it.
        var attestation = report.Findings.FirstOrDefault(f => f.Code == "COMPLIANCE_AUDIT_CONFIRMED");
        await _audit.LogAsync(user.DisplayName, "compliance.attested", $"{manifest.Id} {manifest.Version}",
            $"via {via}; {Truncate(attestation?.Message ?? "", 600)}");

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
            "<p>The version passed all automatic checks and waits for review: approve it for beta or for the live store.</p>",
            includeReviewers: true);

        var warnings = report.Findings.Count(f => f.Severity == "warning");
        await _notify.NotifyUserAsync("SubmissionReceipt", user,
            $"[Add-on Store] Received: {manifest.Id} {manifest.Version}",
            $"<p>Your version <b>{manifest.Id} {manifest.Version}</b> was received (via {System.Net.WebUtility.HtmlEncode(via)}) " +
            $"and passed all automatic checks{(warnings > 0 ? $" with {warnings} warning(s)" : "")}.</p>" +
            "<p>It waits for review now; no workstation gets it yet. Try it in Power PDF with your personal test code (profile page). " +
            "You get another email when it is approved for beta or the live store, or rejected.</p>" +
            await _notify.PluginLinkAsync(manifest.Id));

        return new SubmissionResult(report, version);
    }

    public static async Task<string> HashFileAsync(string path)
    {
        await using var fs = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(fs)).ToLowerInvariant();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
