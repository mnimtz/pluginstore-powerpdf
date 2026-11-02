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
    private readonly Microsoft.AspNetCore.Identity.UserManager<AppUser> _users;
    private readonly SubmissionService _submissions;

    public VersionActionService(AppDbContext db, AuditService audit, NotificationService notify, SourceService sources,
                                SettingsService settings, Microsoft.AspNetCore.Identity.UserManager<AppUser> users,
                                SubmissionService submissions)
    {
        _db = db; _audit = audit; _notify = notify; _sources = sources; _settings = settings; _users = users; _submissions = submissions;
    }

    private static string Enc(string s) => System.Net.WebUtility.HtmlEncode(s);

    /// <summary>Mails the package owner about a change someone else made (event StatusChange).</summary>
    private async Task TellOwnerAsync(AppUser? owner, AppUser actor, string packageId, string subject, string html)
    {
        if (owner is null || owner.Id == actor.Id) return;
        await _notify.NotifyUserAsync("StatusChange", owner, subject, html + await _notify.PluginLinkAsync(packageId));
    }

    public static bool CanReview(System.Security.Claims.ClaimsPrincipal u) => u.IsInRole("Admin") || u.IsInRole("Reviewer");

    /// <summary>Owner may withdraw versions waiting for review or in beta; admins may withdraw any of them and live versions.</summary>
    public static bool CanWithdraw(PackageVersion v, AppUser user, bool isAdmin) =>
        (v.Status is VersionStatus.Submitted or VersionStatus.Beta && (isAdmin || v.Package?.OwnerId == user.Id)) ||
        (v.Status == VersionStatus.Live && isAdmin);

    /// <summary>A reviewer decides on it (S1.6.0): waiting for review (beta, live or reject), or approved for beta (live or reject).</summary>
    public static bool CanDecide(PackageVersion v) =>
        v.Status is VersionStatus.Submitted or VersionStatus.Beta && v.PackageId != SubmissionService.ClientPackageId;

    /// <summary>"beta" or "live": the stage an approval was for (approvals before S1.6.0 were for live).</summary>
    public static string ApprovedStage(PackageVersion v)
    {
        if (string.IsNullOrEmpty(v.ApprovalJson)) return "live";
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(v.ApprovalJson);
            return d.RootElement.TryGetProperty("stage", out var s) && s.GetString() == "beta" ? "beta" : "live";
        }
        catch (System.Text.Json.JsonException) { return "live"; }
    }

    /// <summary>Admins can set a live version back to beta (not the store client, which has no beta stage).</summary>
    public static bool CanDemote(PackageVersion v, bool isAdmin) =>
        isAdmin && v.Status == VersionStatus.Live && v.PackageId != SubmissionService.ClientPackageId;

    /// <summary>Only admins bring a withdrawn version back.</summary>
    public static bool CanRestore(PackageVersion v, bool isAdmin) =>
        isAdmin && v.Status == VersionStatus.Withdrawn && v.BlockedAt is null && v.Package?.BlockedAt is null;

    // ---- security block (S1.0.11) -------------------------------------------
    public const int MaxBlockReason = 300;

    /// <summary>
    /// Blocks a version after a security finding: it is withdrawn (out of the catalog,
    /// deliveries and downloads) and listed in GET /api/blocked, so every client that
    /// has it installed asks the user to remove it. Admins only; the owner and the
    /// staff are told.
    /// </summary>
    public async Task<string?> BlockVersionAsync(int versionId, AppUser actor, bool isAdmin, string? reason)
    {
        reason = (reason ?? "").Trim();
        if (!isAdmin) return null;
        if (reason.Length is < 3 or > MaxBlockReason) return "Give a reason for the block (3 to 300 characters); users see it.";
        var v = await _db.PackageVersions.Include(x => x.Package).ThenInclude(p => p!.Owner).FirstOrDefaultAsync(x => x.Id == versionId);
        if (v is null || v.PackageId == SubmissionService.ClientPackageId || v.BlockedAt is not null) return null;
        var was = v.Status;
        if (v.Status is VersionStatus.Live or VersionStatus.Beta or VersionStatus.Submitted) v.Status = VersionStatus.Withdrawn;
        v.BlockedAt = DateTime.UtcNow; v.BlockReason = reason; v.BlockedBy = actor.DisplayName;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(actor.DisplayName, "version.blocked", $"{v.PackageId} {v.Version}", $"was {was.ToString().ToLowerInvariant()}; {reason}");
        await TellBlockAsync(v.Package, actor, $"version {v.Version}", reason);
        return "Version blocked. Clients that have it installed ask the user to remove it.";
    }

    public async Task<string?> UnblockVersionAsync(int versionId, AppUser actor, bool isAdmin)
    {
        if (!isAdmin) return null;
        var v = await _db.PackageVersions.FirstOrDefaultAsync(x => x.Id == versionId);
        if (v is null || v.BlockedAt is null) return null;
        v.BlockedAt = null; v.BlockReason = null; v.BlockedBy = null;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(actor.DisplayName, "version.unblocked", $"{v.PackageId} {v.Version}", "stays withdrawn until restored");
        return "Block lifted. The version stays withdrawn; restore it if it should be offered again.";
    }

    /// <summary>Blocks the whole add-on: every version withdrawn, uploads refused, all installed versions to be removed.</summary>
    public async Task<string?> BlockPackageAsync(string packageId, AppUser actor, bool isAdmin, string? reason)
    {
        reason = (reason ?? "").Trim();
        if (!isAdmin) return null;
        if (reason.Length is < 3 or > MaxBlockReason) return "Give a reason for the block (3 to 300 characters); users see it.";
        var pkg = await _db.Packages.Include(p => p.Owner).FirstOrDefaultAsync(p => p.Id == packageId);
        if (pkg is null || pkg.Id == SubmissionService.ClientPackageId || pkg.BlockedAt is not null) return null;
        pkg.BlockedAt = DateTime.UtcNow; pkg.BlockReason = reason; pkg.BlockedBy = actor.DisplayName;
        var versions = await _db.PackageVersions
            .Where(x => x.PackageId == packageId && (x.Status == VersionStatus.Live || x.Status == VersionStatus.Beta || x.Status == VersionStatus.Submitted))
            .ToListAsync();
        foreach (var v in versions) v.Status = VersionStatus.Withdrawn;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(actor.DisplayName, "package.blocked", packageId,
            $"{reason}; withdrawn versions: {string.Join(", ", versions.Select(v => v.Version))}");
        await TellBlockAsync(pkg, actor, "all versions", reason);
        return "Add-on blocked. Every version is withdrawn, uploads are refused, and clients ask the user to remove it.";
    }

    public async Task<string?> UnblockPackageAsync(string packageId, AppUser actor, bool isAdmin)
    {
        if (!isAdmin) return null;
        var pkg = await _db.Packages.FirstOrDefaultAsync(p => p.Id == packageId);
        if (pkg is null || pkg.BlockedAt is null) return null;
        pkg.BlockedAt = null; pkg.BlockReason = null; pkg.BlockedBy = null;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(actor.DisplayName, "package.unblocked", packageId, "versions stay withdrawn until restored");
        return "Block lifted. The versions stay withdrawn; restore the ones that should be offered again.";
    }

    private async Task TellBlockAsync(Package? pkg, AppUser actor, string what, string reason)
    {
        if (pkg is null) return;
        var html = $"<p><b>{Enc(actor.DisplayName)}</b> blocked <b>{pkg.Id}</b> ({Enc(what)}) for a security reason: {Enc(reason)}</p>" +
                   "<p>The store has withdrawn it, and Power PDF clients that have it installed ask their users to remove it.</p>";
        if (pkg.Owner is { } owner && owner.Id != actor.Id)
            await _notify.NotifyUserAsync("StatusChange", owner, $"[Add-on Store] {pkg.Id} blocked", html + await _notify.PluginLinkAsync(pkg.Id));
        await _notify.NotifyStaffAsync("StatusChange", $"[Add-on Store] Security block: {pkg.Id}", html);
    }

    /// <summary>Status a withdrawn version returns to: the stage it was approved for (the client never queues), otherwise waiting for review.</summary>
    public static VersionStatus RestoreTarget(PackageVersion v) =>
        v.PackageId == SubmissionService.ClientPackageId ? VersionStatus.Live
        : v.ReviewedAt is null ? VersionStatus.Submitted
        : ApprovedStage(v) == "beta" ? VersionStatus.Beta : VersionStatus.Live;

    /// <summary>
    /// The review decision (S1.6.0: approval for beta or for live). A version waiting for review can be
    /// approved for beta, approved for live or rejected; one approved for beta can go live or be rejected.
    /// Private add-ons are approved in one step (stage live): from then on their customer deliveries hand it out (S1.11.0).
    /// </summary>
    public async Task<string?> DecideAsync(int versionId, AppUser actor, bool approve, string? comment,
                                           IReadOnlyCollection<string>? confirmed = null, string stage = "live")
    {
        var v = await _db.PackageVersions.Include(x => x.Package).ThenInclude(p => p!.Owner)
            .FirstOrDefaultAsync(x => x.Id == versionId);
        if (v is null || !CanDecide(v)) return null;
        var isPrivate = v.Package?.Visibility == "private";
        stage = isPrivate || stage != "beta" ? "live" : "beta";
        if (approve && v.Status == VersionStatus.Beta && stage == "beta") return "This version is already approved for beta.";
        // Approval conditions (S1.0.10): the reviewer confirms each one; the ids are recorded.
        var conditions = approve ? Validation.RuleCatalog.ApprovalConditions().Select(c => c.Id).ToList() : new List<string>();
        if (approve && conditions.Any(id => confirmed is null || !confirmed.Contains(id)))
            return "Confirm every approval condition before approving.";
        if (approve && await _sources.BlocksApprovalAsync(v))
            return "The source code of this version is missing. It can be approved once the author (or an admin) has uploaded it.";
        // Four-eyes rule (setting Review.FourEyes): nobody approves a version they uploaded or whose package they own.
        // Reviewers who are not admins never do, whatever the setting (audit S1.3.1).
        var own = v.SubmittedById == actor.Id || v.Package?.OwnerId == actor.Id;
        var actorIsAdmin = (await _users.GetRolesAsync(actor)).Contains("Admin");
        if (approve && v.PackageId != SubmissionService.ClientPackageId && own && (!actorIsAdmin || await FourEyesAsync()))
            return "Four-eyes rule: another admin or reviewer has to approve a version you uploaded or own.";
        // UAT handover (S1.22.0): whoever uploaded the version in the UAT never approves it here, whatever the setting
        if (approve && v.OriginUploaderEmail is { Length: > 0 } uatUploader && string.Equals(uatUploader, actor.Email, StringComparison.OrdinalIgnoreCase))
            return "Four-eyes rule: you uploaded this version in the UAT; another admin or reviewer of production approves it.";
        v.Status = !approve ? VersionStatus.Rejected : stage == "beta" ? VersionStatus.Beta : VersionStatus.Live;
        v.ReviewedById = actor.Id;
        v.ReviewedAt = DateTime.UtcNow;
        v.ReviewComment = comment;
        // audit dossier (S1.3.0): the decision with the conditions in the wording the reviewer confirmed
        var fourEyes = await FourEyesAsync();
        v.ApprovalJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            decision = approve ? "approved" : "rejected",
            stage = approve ? stage : null,   // S1.6.0
            by = actor.DisplayName,
            byId = actor.Id,
            at = v.ReviewedAt,
            comment,
            fourEyesRule = fourEyes,
            conditions = approve ? Validation.RuleCatalog.ApprovalConditions().Select(c => new { id = c.Id, text = c.Text }).ToList() : null,
            rules = Validation.RuleCatalog.Snapshot(),
        });
        await _db.SaveChangesAsync();
        await _audit.LogAsync(actor.DisplayName, !approve ? "version.rejected" : stage == "beta" ? "version.approved.beta" : "version.approved",
            $"{v.PackageId} {v.Version}", approve ? $"stage {stage}; conditions confirmed: " + string.Join(", ", conditions) : comment ?? "");
        if (v.Package?.Owner is { } owner)
            await _notify.NotifyUserAsync("ReviewResult", owner,
                $"[Add-on Store] {v.PackageId} {v.Version} {(!approve ? "rejected" : stage == "beta" ? "approved for beta" : "approved")}",
                (!approve
                    ? $"<p>Your version <b>{v.PackageId} {v.Version}</b> was rejected.</p><p>Reason: {System.Net.WebUtility.HtmlEncode(comment ?? "-")}</p>"
                    : isPrivate ? $"<p>Your private version <b>{v.PackageId} {v.Version}</b> was approved: its customer deliveries hand it out now.</p>"
                    : stage == "beta" ? $"<p>Your version <b>{v.PackageId} {v.Version}</b> was approved for beta: workstations with the beta option get it now.</p>"
                    : $"<p>Your version <b>{v.PackageId} {v.Version}</b> was approved and is live for all users.</p>")
                + await _notify.PluginLinkAsync(v.PackageId));
        return !approve ? "Version rejected." : isPrivate ? "Version approved for customer deliveries." : stage == "beta" ? "Version approved for beta." : "Version approved and live.";
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
    /// Live back to beta: from now on only beta workstations are offered this version, and
    /// the live channel falls back to the previous live version. It stays approved for beta
    /// (S1.6.0; before, it went back to the review queue). Installed copies stay.
    /// </summary>
    public async Task<string?> DemoteAsync(int versionId, AppUser actor, bool isAdmin)
    {
        var v = await _db.PackageVersions.Include(x => x.Package).ThenInclude(p => p!.Owner).FirstOrDefaultAsync(x => x.Id == versionId);
        if (v is null || !CanDemote(v, isAdmin)) return null;
        v.Status = VersionStatus.Beta;   // stays approved for beta: no new approval, so a missing source code does not matter here
        // the approval now covers beta only; the earlier live decision stays in the audit log
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(v.ApprovalJson ?? "{}") as System.Text.Json.Nodes.JsonObject ?? new();
            node["stage"] = "beta";
            node["demotedBy"] = actor.DisplayName;
            node["demotedAt"] = DateTime.UtcNow;
            v.ApprovalJson = node.ToJsonString();
        }
        catch (System.Text.Json.JsonException) { v.ApprovalJson = System.Text.Json.JsonSerializer.Serialize(new { stage = "beta", demotedBy = actor.DisplayName, demotedAt = DateTime.UtcNow }); }
        await _db.SaveChangesAsync();
        await _audit.LogAsync(actor.DisplayName, "version.demoted", $"{v.PackageId} {v.Version}", "live -> beta, stays approved for beta");
        await TellOwnerAsync(v.Package?.Owner, actor, v.PackageId, $"[Add-on Store] {v.PackageId} {v.Version} back to beta",
            $"<p><b>{Enc(actor.DisplayName)}</b> set version <b>{v.PackageId} {v.Version}</b> back to beta. " +
            "Only beta workstations are offered it now. Installed copies keep working.</p>");
        return "Version set back to beta.";
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
            (v.Status == VersionStatus.Live ? "It is live again.</p>" : v.Status == VersionStatus.Beta ? "It is back in the beta channel.</p>" : "It waits for review again.</p>"));
        return v.Status == VersionStatus.Live ? "Version restored and live again."
             : v.Status == VersionStatus.Beta ? "Version restored to the beta channel." : "Version restored; it waits for review.";
    }

    /// <summary>Takes the whole plug-in out of the store: every live and beta version is withdrawn.</summary>
    public async Task<string?> WithdrawPackageAsync(string packageId, AppUser actor, bool isAdmin)
    {
        if (!isAdmin) return null;
        var versions = await _db.PackageVersions
            .Where(x => x.PackageId == packageId && (x.Status == VersionStatus.Live || x.Status == VersionStatus.Beta || x.Status == VersionStatus.Submitted))
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

    /// <summary>S1.29.0: what keeps an admin from deleting an add-on for good: customer deliveries and template entries.</summary>
    public async Task<(int Deliveries, int TemplateItems)> DeleteBlockersAsync(string packageId) =>
        (await _db.Deliveries.CountAsync(d => d.PackageId == packageId),
         await _db.DeliveryTemplateItems.CountAsync(t => t.PackageId == packageId));

    /// <summary>
    /// S1.29.0: deletes an add-on for good (admins only, the id typed again to confirm): every version with its package
    /// and source files, ratings, problem reports with notes and attachments, usage and share figures and UAT handover
    /// records. Not while a customer delivery or a delivery template names it. The audit log keeps "package.deleted",
    /// the id stays taken (RetiredPackages), the owner gets an e-mail. Installed copies keep working; the store client
    /// shows them as no longer offered.
    /// </summary>
    public async Task<(bool Ok, string Message)> DeletePackageAsync(string packageId, string? typedId, AppUser actor, bool isAdmin)
    {
        if (!isAdmin || packageId == SubmissionService.ClientPackageId) return (false, "This action is not allowed for this version.");
        if (!string.Equals((typedId ?? "").Trim(), packageId, StringComparison.Ordinal))
            return (false, "Type the package id exactly to confirm the deletion.");
        var pkg = await _db.Packages.Include(p => p.Owner).FirstOrDefaultAsync(p => p.Id == packageId);
        if (pkg is null) return (false, "This action is not allowed for this version.");
        var (deliveries, items) = await DeleteBlockersAsync(packageId);
        if (deliveries > 0 || items > 0)
            return (false, "The add-on is still delivered to customers or part of a delivery template. Remove those deliveries and template entries first.");

        var versions = await _db.PackageVersions.Where(v => v.PackageId == packageId).Select(v => v.Version).ToListAsync();
        var cmp = new Validation.SemVerComparer();
        var versionList = string.Join(", ", versions.OrderBy(v => v, cmp));
        var owner = pkg.Owner;
        var feedbackIds = _db.Feedbacks.Where(f => f.PackageId == packageId).Select(f => f.Id);
        int ratings, reports;
        await using (var tx = await _db.Database.BeginTransactionAsync())
        {
            await _db.FeedbackAttachments.Where(a => feedbackIds.Contains(a.FeedbackId)).ExecuteDeleteAsync();
            await _db.FeedbackNotes.Where(n => feedbackIds.Contains(n.FeedbackId)).ExecuteDeleteAsync();
            reports = await _db.Feedbacks.Where(f => f.PackageId == packageId).ExecuteDeleteAsync();
            ratings = await _db.Ratings.Where(r => r.PackageId == packageId).ExecuteDeleteAsync();
            await _db.UsageStats.Where(s => s.PackageId == packageId).ExecuteDeleteAsync();
            await _db.UsageEvents.Where(e => e.PackageId == packageId).ExecuteDeleteAsync();
            await _db.ShareStats.Where(s => s.PackageId == packageId).ExecuteDeleteAsync();
            await _db.FederationIntakes.Where(i => i.PackageId == packageId).ExecuteDeleteAsync();
            await _db.FederationHandovers.Where(h => h.PackageId == packageId).ExecuteDeleteAsync();
            await _db.PackageVersions.Where(v => v.PackageId == packageId).ExecuteDeleteAsync();
            await _db.Packages.Where(p => p.Id == packageId).ExecuteDeleteAsync();
            if (!await _db.RetiredPackages.AnyAsync(r => r.Id == packageId))
                _db.RetiredPackages.Add(new RetiredPackage
                {
                    Id = packageId, RetiredBy = actor.DisplayName, OwnerName = owner?.DisplayName ?? "", Versions = versionList,
                });
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        _db.ChangeTracker.Clear();
        CatalogUi.Invalidate();

        // package and source files: the add-on's own folder in the package storage, nothing outside it
        var removedFiles = "files removed";
        try
        {
            var root = Path.GetFullPath(_submissions.StorageRoot);
            var dir = Path.GetFullPath(Path.Combine(root, packageId));
            if (dir.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            removedFiles = "files could not all be removed: " + ex.GetType().Name;
        }

        await _audit.LogAsync(actor.DisplayName, "package.deleted", packageId,
            $"owner {owner?.DisplayName ?? "-"}; versions {versionList}; ratings {ratings}; problem reports {reports}; {removedFiles}; the id stays taken");
        if (owner is not null && owner.Id != actor.Id)   // no link: the plug-in page is gone
            await _notify.NotifyUserAsync("StatusChange", owner, $"[Add-on Store] {packageId} deleted",
                $"<p><b>{Enc(actor.DisplayName)}</b> deleted the add-on <b>{Enc(packageId)}</b> from the store for good " +
                $"(versions {Enc(versionList)}). Its id cannot be used again. Installed copies keep working, but get no updates.</p>");
        return (true, "deleted");
    }
}
