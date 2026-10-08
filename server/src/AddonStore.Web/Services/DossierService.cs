using System.Text.Json;
using AddonStore.Web.Data;
using AddonStore.Web.Validation;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Services;

/// <summary>
/// The audit dossier of one version (S1.3.0): everything the store knows about how it was
/// checked and released, in one place. Package and binary hashes with the catalog signature,
/// the compliance declaration, the automatic checks and the rules in force at that time, the
/// source code check, the AI review aid, the review decision with the conditions in their
/// wording, blocks, problem reports and every audit entry. Shown on /Dossier/{id}/{version}
/// (print or save as PDF) and returned by GET /api/packages/{id}/{version}/dossier.
/// Visible to the owner, admins and reviewers, like the plug-in page.
/// </summary>
public class DossierService
{
    private readonly AppDbContext _db;
    private readonly PackageSigning _signing;
    public DossierService(AppDbContext db, PackageSigning signing) { _db = db; _signing = signing; }

    public record FileHash(string Arch, string Path, string Sha256);
    public record AuditLine(DateTime At, string Actor, string Action, string Subject, string Details);
    public record Dossier(
        string PackageId, string Name, string Owner, string Visibility, string Category,
        string Version, VersionStatus Status, string StatusName, string Channel, DateTime SubmittedAt, string SubmittedBy, string SubmittedVia,
        string PackageSha256, long SizeBytes, int Downloads, string MinHost, bool NoUi, string AtomNamespace,
        List<FileHash> Files, string? Signature, string SigningKeyId,
        JsonElement? Compliance, JsonElement? ThirdParty, JsonElement? ExternalServices,
        bool Passed, List<Finding> Findings, JsonElement? RulesSnapshot,
        string? SourceSha256, long SourceSizeBytes, DateTime? SourceUploadedAt, string? SourceUploadedBy, List<Finding>? SourceFindings,
        AiAssist.ReviewView? AiReview, string? AiModel, DateTime? AiAt,
        JsonElement? Approval, string? ReviewedBy, DateTime? ReviewedAt, string? ReviewComment, string? LegacyConditions,
        DateTime? BlockedAt, string? BlockReason, string? BlockedBy, DateTime? PackageBlockedAt, string? PackageBlockReason,
        Dictionary<string, int> Reports, List<AuditLine> VersionAudit, List<AuditLine> PackageAudit,
        DateTime GeneratedAt, string GeneratedBy, string ServerVersion);

    public async Task<Dossier?> BuildAsync(string id, string version, AppUser viewer, bool viewerMayReview = true)
    {
        var v = await _db.PackageVersions.AsNoTracking().Include(x => x.Package).ThenInclude(p => p!.Owner)
            .FirstOrDefaultAsync(x => x.PackageId == id && x.Version == version);
        if (v?.Package is null) return null;
        var pkg = v.Package;
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(v.ManifestJson) ? "{}" : v.ManifestJson);
        var root = doc.RootElement;
        JsonElement? Clone(string name) => root.TryGetProperty(name, out var e) ? e.Clone() : null;
        JsonElement? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { using var d = JsonDocument.Parse(json); return d.RootElement.Clone(); } catch (JsonException) { return null; }
        }

        var files = new List<FileHash>();
        if (root.TryGetProperty("files", out var fe) && fe.ValueKind == JsonValueKind.Object)
            foreach (var f in fe.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String))
            {
                var sha = root.TryGetProperty("sha256", out var se) && se.ValueKind == JsonValueKind.Object &&
                          se.TryGetProperty(f.Name, out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
                files.Add(new FileHash(f.Name, f.Value.GetString() ?? "", sha));
            }
        var zxt = files.FirstOrDefault(f => f.Arch == "x64")?.Path is { } p ? System.IO.Path.GetFileNameWithoutExtension(p) : "";
        await _signing.EnsureLoadedAsync();
        string? signature = null;
        try { signature = _signing.Problem is null && v.Status is VersionStatus.Live or VersionStatus.Beta ? _signing.Sign(v.PackageId, v.Version, v.Sha256, zxt) : null; }
        catch (InvalidOperationException) { }

        var report = ReportView.Parse(v.ValidationReportJson);
        var sourceReport = string.IsNullOrWhiteSpace(v.SourceReportJson) ? null : ReportView.Parse(v.SourceReportJson).Findings;

        // audit: entries of exactly this version, and package-wide ones (catalog entry, blocks, visibility, reports)
        // audit (audit S1.3.1): every entry of this version, and the newest 500 add-on-wide ones
        var subject = $"{v.PackageId} {v.Version}";
        AuditLine L(AuditEntry e) => new(e.At, e.Actor, e.Action, e.Subject, e.Details);
        var versionRows = await _db.AuditEntries.AsNoTracking().Where(e => e.Subject == subject).OrderBy(e => e.At).ToListAsync();
        var otherVersions = await _db.PackageVersions.AsNoTracking().Where(x => x.PackageId == v.PackageId && x.Version != v.Version)
            .Select(x => x.PackageId + " " + x.Version).ToListAsync();
        var packageRows = await _db.AuditEntries.AsNoTracking()
            .Where(e => e.Subject != subject && (e.Subject == v.PackageId || e.Subject.StartsWith(v.PackageId + " ")) && !otherVersions.Contains(e.Subject))
            .OrderByDescending(e => e.At).Take(500).ToListAsync();
        var versionAudit = versionRows.Select(L).ToList();
        var packageAudit = packageRows.OrderBy(e => e.At).Select(L).ToList();
        var legacy = v.ApprovalJson is null
            ? versionRows.Where(e => e.Action == "version.approved").Select(e => e.Details).LastOrDefault()
              ?? (v.PackageId == SubmissionService.ClientPackageId && v.ReviewedAt is not null ? "Store client: published by an admin, live at once (no review queue)." : null)
            : null;
        var known = await _db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Slug);
        // externalServices sits inside complianceAudit (older manifests: at the root)
        JsonElement? services = root.TryGetProperty("complianceAudit", out var ca) && ca.ValueKind == JsonValueKind.Object &&
                                ca.TryGetProperty("externalServices", out var es) ? es.Clone() : Clone("externalServices");
        var reviewer = v.ReviewedById is null ? null : await _db.Users.Where(u => u.Id == v.ReviewedById).Select(u => u.DisplayName).FirstOrDefaultAsync();
        var reports = await _db.Feedbacks.AsNoTracking().Where(f => f.PackageId == v.PackageId && f.Version == v.Version)
            .GroupBy(f => f.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.N);
        var culture = Lang.Current;

        return new Dossier(
            v.PackageId, CatalogUi.DisplayName(pkg, v, culture), pkg.Owner?.DisplayName ?? "", pkg.Visibility,
            CatalogUi.EffectiveCategory(pkg, root, known),
            v.Version, v.Status, v.Status.ToString().ToLowerInvariant(), v.Status == VersionStatus.Live ? "live" : v.Status == VersionStatus.Beta ? "beta" : v.Status == VersionStatus.Submitted ? "test code only" : "-",
            v.SubmittedAt, v.SubmittedBy, v.SubmittedVia, v.Sha256, v.SizeBytes, v.Downloads, v.MinPowerPdfVersion,
            CatalogUi.IsNoUi(root), v.AtomNamespace, files, signature, _signing.KeyId,
            Clone("complianceAudit"), Clone("thirdParty"), services,
            report.Passed, report.Findings, Parse(v.RulesSnapshotJson),
            v.SourceSha256, v.SourceSizeBytes, v.SourceUploadedAt, v.SourceUploadedBy, sourceReport,
            // the AI review aid is a reviewer tool (plug-in page and /ai-review): owners do not see it (audit S1.3.1)
            viewerMayReview ? AiAssist.ParseReview(v.AiReviewJson) : null, viewerMayReview ? v.AiReviewModel : null, viewerMayReview ? v.AiReviewAt : null,
            Parse(v.ApprovalJson), reviewer, v.ReviewedAt, v.ReviewComment, legacy,
            v.BlockedAt, v.BlockReason, v.BlockedBy, pkg.BlockedAt, pkg.BlockReason,
            reports, versionAudit, packageAudit,
            DateTime.UtcNow, viewer.DisplayName, RuleCatalog.ServerVersion);
    }
}
