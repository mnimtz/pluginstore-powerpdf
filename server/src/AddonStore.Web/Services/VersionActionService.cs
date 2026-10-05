using AddonStore.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Services;

/// <summary>
/// Every status change of a version in one place, shared by the review queue
/// and the plug-in detail page, so permissions, audit entries and mails are
/// the same wherever an admin clicks.
/// </summary>
public class VersionActionService
{
    private readonly AppDbContext _db;
    private readonly AuditService _audit;
    private readonly NotificationService _notify;
    private readonly SourceService _sources;
    private readonly SettingsService _settings;

    public VersionActionService(AppDbContext db, AuditService audit, NotificationService notify, SourceService sources,
                                SettingsService settings)
    {
        _db = db; _audit = audit; _notify = notify; _sources = sources; _settings = settings;
    }

    private static string Enc(string s) => System.Net.WebUtility.HtmlEncode(s);

    /// <summary>Mails the package owner about a change someone else made (event StatusChange).</summary>
    private async Task TellOwnerAsync(AppUser? owner, AppUser actor, string packageId, string subject, string html)
    {
        if (owner is null || owner.Id == actor.Id) return;
        await _notify.NotifyUserAsync("StatusChange", owner, subject, html + await _notify.PluginLinkAsync(packageId));
    }

    public static bool CanReview(System.Security.Claims.ClaimsPrincipal u) => u.IsInRole("Admin") || u.IsInRole("Reviewer");

    /// <summary>Owner may withdraw beta versions; admins may withdraw any beta or live version.</summary>
    public static bool CanWithdraw(PackageVersion v, AppUser user, bool isAdmin) =>
        (v.Status == VersionStatus.Beta && (isAdmin || v.Package?.OwnerId == user.Id)) ||
        (v.Status == VersionStatus.Live && isAdmin);

    /// <summary>Admins can set a live version back to beta (not the store client, which has no beta stage).</summary>
    public static bool CanDemote(PackageVersion v, bool isAdmin) =>
        isAdmin && v.Status == VersionStatus.Live && v.PackageId != SubmissionService.ClientPackageId;

    /// <summary>Only admins bring a withdrawn version back.</summary>
    public static bool CanRestore(PackageVersion v, bool isAdmin) => isAdmin && v.Status == VersionStatus.Withdrawn;

    /// <summary>Status a withdrawn version returns to: live when it had been approved (or is the client, which never queues), otherwise beta.</summary>
    public static VersionStatus RestoreTarget(PackageVersion v) =>
        v.ReviewedAt is not null || v.PackageId == SubmissionService.ClientPackageId ? VersionStatus.Live : VersionStatus.Beta;

    public async Task<string?> DecideAsync(int versionId, AppUser actor, bool approve, string? comment,
                                           IReadOnlyCollection<string>? confirmed = null)
    {
        var v = await _db.PackageVersions.Include(x => x.Package).ThenInclude(p => p!.Owner)
            .FirstOrDefaultAsync(x => x.Id == versionId);
        if (v is null || v.Status != VersionStatus.Beta) return null;
        // Approval conditions (S1.0.10): the reviewer confirms each one; the ids are recorded.
        var conditions = approve ? Validation.RuleCatalog.ApprovalConditions().Select(c => c.Id).ToList() : new List<string>();
        if (approve && conditions.Any(id => confirmed is null || !confirmed.Contains(id)))
            return "Confirm every approval condition before approving.";
        if (approve && await _sources.BlocksApprovalAsync(v))
            return "The source code of this version is missing. It can be approved once the author (or an admin) has uploaded it.";
        // Four-eyes rule (setting Review.FourEyes): nobody approves a version they uploaded or whose package they own.
        if (approve && v.PackageId != SubmissionService.ClientPackageId && await FourEyesAsync() &&
            (v.SubmittedById == actor.Id || v.Package?.OwnerId == actor.Id))
            return "Four-eyes rule: another admin or reviewer has to approve a version you uploaded or own.";
        v.Status = approve ? VersionStatus.Live : VersionStatus.Rejected;
        v.ReviewedById = actor.Id;
        v.ReviewedAt = DateTime.UtcNow;
        v.ReviewComment = comment;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(actor.DisplayName, approve ? "version.approved" : "version.rejected",
            $"{v.PackageId} {v.Version}", approve ? "conditions confirmed: " + string.Join(", ", conditions) : comment ?? "");
        if (v.Package?.Owner is { } owner)
            await _notify.NotifyUserAsync("ReviewResult", owner,
                $"[Add-on Store] {v.PackageId} {v.Version} {(approve ? "approved" : "rejected")}",
                (approve
                    ? $"<p>Your version <b>{v.PackageId} {v.Version}</b> was approved and is live for all users.</p>"
                    : $"<p>Your version <b>{v.PackageId} {v.Version}</b> was rejected.</p><p>Reason: {System.Net.WebUtility.HtmlEncode(comment ?? "-")}</p>")
                + await _notify.PluginLinkAsync(v.PackageId));
        return approve ? "Version approved and live." : "Version rejected.";
    }

    private async Task<bool> FourEyesAsync() => await _settings.GetAsync("Review.FourEyes") == "on";

    public async Task<string?> WithdrawAsync(int versionId, AppUser actor, bool isAdmin)
    {
        var v = await _db.PackageVersions.Include(x => x.Package).ThenInclude(p => p!.Owner).FirstOrDefaultAsync(x => x.Id == versionId);
        if (v is null || !CanWithdraw(v, actor, isAdmin)) return null;
        var was = v.Status;
        v.Status = VersionStatus.Withdrawn;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(actor.DisplayName, "version.withdrawn", $"{v.PackageId} {v.Version}",
            $"was {was.ToString().ToLowerInvariant()}; by {(isAdmin ? "admin" : "owner")}");
        await TellOwnerAsync(v.Package?.Owner, actor, v.PackageId, $"[Add-on Store] {v.PackageId} {v.Version} withdrawn",
            $"<p><b>{Enc(actor.DisplayName)}</b> withdrew version <b>{v.PackageId} {v.Version}</b> (previously {was.ToString().ToLowerInvariant()}). " +
            "It is no longer offered in the store; installed copies keep working.</p>");
        return "Version withdrawn.";
    }

    /// <summary>
    /// Live back to beta: from now on only beta workstations are offered this version,
    /// the live channel falls back to the previous live version, and it needs approval
    /// again (it is back in the review queue). Installed copies stay.
    /// </summary>
    public async Task<string?> DemoteAsync(int versionId, AppUser actor, bool isAdmin)
    {
        var v = await _db.PackageVersions.Include(x => x.Package).ThenInclude(p => p!.Owner).FirstOrDefaultAsync(x => x.Id == versionId);
        if (v is null || !CanDemote(v, isAdmin)) return null;
        // Approval needs the source code: without it the version would be stuck in beta.
        if (await _sources.BlocksApprovalAsync(v))
            return "The source code of this version is missing: back in beta, it could not be approved again. Upload the source code first.";
        v.Status = VersionStatus.Beta;
        v.ReviewedAt = null;
        v.ReviewedById = null;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(actor.DisplayName, "version.demoted", $"{v.PackageId} {v.Version}", "live -> beta, needs approval again");
        await TellOwnerAsync(v.Package?.Owner, actor, v.PackageId, $"[Add-on Store] {v.PackageId} {v.Version} back to beta",
            $"<p><b>{Enc(actor.DisplayName)}</b> set version <b>{v.PackageId} {v.Version}</b> back to beta. " +
            "Only beta workstations are offered it now; it needs approval again. Installed copies keep working.</p>");
        return "Version set back to beta; it needs approval again.";
    }

    public async Task<string?> RestoreAsync(int versionId, AppUser actor, bool isAdmin)
    {
        var v = await _db.PackageVersions.Include(x => x.Package).ThenInclude(p => p!.Owner).FirstOrDefaultAsync(x => x.Id == versionId);
        if (v is null || !CanRestore(v, isAdmin)) return null;
        v.Status = RestoreTarget(v);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(actor.DisplayName, "version.restored", $"{v.PackageId} {v.Version}",
            $"now {v.Status.ToString().ToLowerInvariant()}");
        await TellOwnerAsync(v.Package?.Owner, actor, v.PackageId, $"[Add-on Store] {v.PackageId} {v.Version} restored",
            $"<p><b>{Enc(actor.DisplayName)}</b> restored version <b>{v.PackageId} {v.Version}</b>. " +
            (v.Status == VersionStatus.Live ? "It is live again.</p>" : "It is back in the beta channel and needs approval again.</p>"));
        return v.Status == VersionStatus.Live ? "Version restored and live again." : "Version restored to the beta channel; it needs approval again.";
    }

    /// <summary>Takes the whole plug-in out of the store: every live and beta version is withdrawn.</summary>
    public async Task<string?> WithdrawPackageAsync(string packageId, AppUser actor, bool isAdmin)
    {
        if (!isAdmin) return null;
        var versions = await _db.PackageVersions
            .Where(x => x.PackageId == packageId && (x.Status == VersionStatus.Live || x.Status == VersionStatus.Beta))
            .ToListAsync();
        if (versions.Count == 0) return null;
        foreach (var v in versions) v.Status = VersionStatus.Withdrawn;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(actor.DisplayName, "package.withdrawn", packageId,
            $"withdrawn versions: {string.Join(", ", versions.Select(v => v.Version))}");
        var owner = (await _db.Packages.Include(p => p.Owner).FirstOrDefaultAsync(p => p.Id == packageId))?.Owner;
        await TellOwnerAsync(owner, actor, packageId, $"[Add-on Store] {packageId} taken out of the store",
            $"<p><b>{Enc(actor.DisplayName)}</b> took <b>{packageId}</b> out of the store (versions {string.Join(", ", versions.Select(v => v.Version))}). " +
            "Installed copies keep working; an admin can restore single versions.</p>");
        return "The plug-in was taken out of the store. Installed copies keep working; restore single versions to publish it again.";
    }
}
