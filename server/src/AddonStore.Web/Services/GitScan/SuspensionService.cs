using AddonStore.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Services.GitScan;

/// <summary>
/// "Take out of service" (S2.8.0), the emergency brake for any add-on, linked to a repository or not:
/// paused = no new installations or updates, installed copies keep running; disabled = in addition, store clients
/// from 2.3.0 switch installed copies off at the next Power PDF start without removing them (settings stay).
/// The security block (S1.0.11) stays the strongest step. Admins do everything; emergency users (a list kept on
/// /Admin/GitScan) may only take an add-on out of service or raise the level, never lift it.
/// </summary>
public class SuspensionService
{
    public const int MaxReason = 300;
    public static readonly string[] Levels = { "paused", "disabled" };

    private readonly AppDbContext _db;
    private readonly AuditService _audit;
    private readonly NotificationService _notify;
    private readonly GitScanConfig _config;
    private readonly VersionActionService _actions;
    private readonly UserManager<AppUser> _users;

    public SuspensionService(AppDbContext db, AuditService audit, NotificationService notify, GitScanConfig config,
                             VersionActionService actions, UserManager<AppUser> users)
    {
        _db = db; _audit = audit; _notify = notify; _config = config; _actions = actions; _users = users;
    }

    // ---- which add-ons are out of service: kept in memory, read on every catalog request ------------------

    private static volatile Dictionary<string, (string Level, string Reason, DateTime Since)>? s_cache;
    /// <summary>The last list read successfully; used when the database cannot be read for a moment (never "nothing suspended").</summary>
    private static volatile Dictionary<string, (string Level, string Reason, DateTime Since)>? s_last;
    private static DateTime s_cacheAt;
    private static long s_version;
    private static readonly SemaphoreSlim s_load = new(1, 1);

    /// <summary>Forgets the cached list (after a change or a restore) and starts a new catalog generation.</summary>
    public static void Invalidate()
    {
        Interlocked.Increment(ref s_version);   // a load running now must not store its older result (review S2.8.0)
        s_cache = null;
        CatalogUi.Invalidate();
    }

    /// <summary>Package id -> level, reason, since; at most a minute old (a change clears it at once).</summary>
    public static async Task<IReadOnlyDictionary<string, (string Level, string Reason, DateTime Since)>> AllAsync(AppDbContext db)
    {
        var c = s_cache;
        if (c is not null && DateTime.UtcNow - s_cacheAt < TimeSpan.FromMinutes(1)) return c;
        await s_load.WaitAsync();
        try
        {
            c = s_cache;
            if (c is not null && DateTime.UtcNow - s_cacheAt < TimeSpan.FromMinutes(1)) return c;
            var version = Interlocked.Read(ref s_version);
            Dictionary<string, (string, string, DateTime)> fresh;
            try
            {
                fresh = (await db.AddonSuspensions.AsNoTracking().ToListAsync())
                    .ToDictionary(s => s.PackageId, s => (s.Level, s.Reason, s.ChangedAt ?? s.CreatedAt), StringComparer.OrdinalIgnoreCase);
            }
            catch (Microsoft.Data.Sqlite.SqliteException e) when (e.SqliteErrorCode == 1 && e.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
            {
                fresh = new(StringComparer.OrdinalIgnoreCase);   // table not there yet (restore of an older backup): nothing suspended
            }
            catch (Microsoft.Data.Sqlite.SqliteException) when (s_last is { } last)
            {
                return last;   // locked or busy for a moment: the last known list, not cached, never an empty one (fail closed)
            }
            s_last = fresh;
            if (Interlocked.Read(ref s_version) == version)
            {
                s_cacheAt = DateTime.UtcNow;
                s_cache = fresh;
            }
            return fresh;
        }
        finally { s_load.Release(); }
    }

    public static async Task<HashSet<string>> IdsAsync(AppDbContext db) =>
        (await AllAsync(db)).Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

    // ---- who may do what ------------------------------------------------------------------------------------

    public async Task<bool> IsEmergencyUserAsync(AppUser? user) =>
        user is not null && user.Status == UserStatus.Active && (await _config.EmergencyUsersAsync()).Contains(user.Id);

    public async Task<bool> IsAdminAsync(AppUser? user) =>
        user is not null && (await _users.GetRolesAsync(user)).Contains("Admin");

    // ---- actions --------------------------------------------------------------------------------------------

    public record Outcome(bool Ok, string Message, object[]? Args = null);

    /// <summary>Takes an add-on out of service, or changes the level, reason or review date of one that is.</summary>
    public async Task<Outcome> SuspendAsync(string packageId, string level, string? reason, DateTime? reviewAt, bool notifyCustomers,
                                            AppUser actor, bool isAdmin)
    {
        packageId = (packageId ?? "").Trim();
        reason = (reason ?? "").Trim();
        var emergency = !isAdmin && await IsEmergencyUserAsync(actor);
        if (!isAdmin && !emergency) return new(false, "Only admins and emergency users take add-ons out of service.");
        if (!Levels.Contains(level)) return new(false, "Choose paused or out of service.");
        if (reason.Length is < 3 or > MaxReason) return new(false, "Give a reason (3 to 300 characters); users see it.");
        if (reason.Any(ch => char.IsControl(ch))) reason = new string(reason.Select(ch => char.IsControl(ch) ? ' ' : ch).ToArray());
        if (reviewAt is { } ra && (ra < DateTime.UtcNow.Date.AddDays(-1) || ra > DateTime.UtcNow.AddYears(1).AddDays(1)))   // a date: today counts in every time zone
            return new(false, "The review date lies between today and one year from now.");
        var pkg = await _db.Packages.Include(p => p.Owner).FirstOrDefaultAsync(p => p.Id == packageId);
        if (pkg is null) return new(false, "Unknown add-on.");
        if (pkg.Id == SubmissionService.ClientPackageId) return new(false, "The store client itself cannot be taken out of service.");
        if (pkg.BlockedAt is not null) return new(false, "This add-on has a security block; that already takes it out of the store.");

        var row = await _db.AddonSuspensions.FirstOrDefaultAsync(s => s.PackageId == pkg.Id);
        var isNew = row is null;
        // emergency users only take out or raise the level; reason, review date and lower levels stay with the admins
        if (row is not null && emergency && Array.IndexOf(Levels, level) <= Array.IndexOf(Levels, row.Level))
            return new(false, "Emergency users may raise the level, not lower it; ask an admin.");
        var wasLevel = row?.Level;
        if (row is null)
        {
            row = new AddonSuspension { PackageId = pkg.Id, CreatedAt = DateTime.UtcNow, CreatedBy = actor.DisplayName };
            _db.AddonSuspensions.Add(row);
        }
        else { row.ChangedAt = DateTime.UtcNow; row.ChangedBy = actor.DisplayName; }
        row.Level = level;
        if (isNew || !emergency) row.Reason = reason;                       // an emergency user raising the level keeps the admin's reason
        if (isNew || !emergency || reviewAt is not null) row.ReviewAt = reviewAt;
        row.LastReminderAt = null;
        try { await _db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();   // the same add-on taken out at the same moment by someone else (review S2.8.0)
            return new(false, "Someone else changed this add-on at the same moment. Look at the list and try again.");
        }
        Invalidate();
        await _audit.LogAsync(actor.DisplayName, isNew ? "addon.suspended" : "addon.suspension.changed", pkg.Id,
            $"{(wasLevel is null ? "" : wasLevel + " -> ")}{level}; review {(reviewAt?.ToString("yyyy-MM-dd") ?? "-")}; {(emergency ? "emergency user; " : "")}{reason}");

        var levelText = level == "disabled" ? "taken out of service" : "paused";
        var effect = level == "disabled"
            ? "No new installations or updates. Store clients from version 2.3.0 switch installed copies off at the next Power PDF start, without removing them."
            : "No new installations or updates. Installed copies keep running.";
        var html = $"<p><b>{Enc(actor.DisplayName)}</b> {levelText} <b>{Enc(pkg.Id)}</b>.</p><p>Reason: {Enc(reason)}</p><p>{effect}</p>";
        if (pkg.Owner is { } owner && owner.Id != actor.Id)
            await _notify.NotifyUserAsync("StatusChange", owner, $"[Add-on Store] {pkg.Id} {levelText}", html + await _notify.PluginLinkAsync(pkg.Id));
        await _notify.NotifyStaffAsync("AddonSuspended", $"[Add-on Store] {pkg.Id} {levelText}", html);
        var told = 0;
        if (notifyCustomers && (isNew || wasLevel != level)) told = await TellCustomersAsync(pkg.Id, level, reason);
        if (told > 0)
        {
            row.CustomersNotified = true;
            await _db.SaveChangesAsync();
        }
        return isNew
            ? new(true, level == "disabled" ? "Add-on taken out of service." : "Add-on paused.")
            : new(true, "Changes saved.");
    }

    /// <summary>Puts an add-on back into service (admins only).</summary>
    public async Task<Outcome> LiftAsync(string packageId, AppUser actor, bool isAdmin)
    {
        if (!isAdmin) return new(false, "Only admins put an add-on back into service.");
        var row = await _db.AddonSuspensions.FirstOrDefaultAsync(s => s.PackageId == packageId);
        if (row is null) return new(false, "This add-on is not out of service.");
        _db.AddonSuspensions.Remove(row);
        await _db.SaveChangesAsync();
        Invalidate();
        await _audit.LogAsync(actor.DisplayName, "addon.resumed", packageId, $"was {row.Level} since {row.CreatedAt:yyyy-MM-dd}");
        var pkg = await _db.Packages.Include(p => p.Owner).FirstOrDefaultAsync(p => p.Id == packageId);
        var html = $"<p><b>{Enc(actor.DisplayName)}</b> put <b>{Enc(packageId)}</b> back into service. It is offered again" +
                   (row.Level == "disabled" ? "; store clients switch installed copies on again at the next Power PDF start." : ".") + "</p>";
        if (pkg?.Owner is { } owner && owner.Id != actor.Id)
            await _notify.NotifyUserAsync("StatusChange", owner, $"[Add-on Store] {packageId} back in service", html + await _notify.PluginLinkAsync(packageId));
        await _notify.NotifyStaffAsync("AddonSuspended", $"[Add-on Store] {packageId} back in service", html);
        if (row.CustomersNotified) await TellCustomersAsync(packageId, "resumed", "");
        return new(true, "Add-on back in service.");
    }

    /// <summary>
    /// Back to the last good version (admins only): the newest live version is withdrawn; an older live version then
    /// takes its place, or, when there is none, the newest older version that was approved for live is restored first.
    /// </summary>
    public async Task<Outcome> RollbackAsync(string packageId, AppUser actor, bool isAdmin)
    {
        if (!isAdmin) return new(false, "Only admins go back to an earlier version.");
        if (packageId == SubmissionService.ClientPackageId) return new(false, "Not for the store client.");
        var cmp = new Validation.SemVerComparer();
        var versions = await _db.PackageVersions.Where(v => v.PackageId == packageId).ToListAsync();
        var newest = versions.Where(v => v.Status == VersionStatus.Live).OrderByDescending(v => v.Version, cmp).FirstOrDefault();
        if (newest is null) return new(false, "This add-on has no live version.");
        var olderLive = versions.Where(v => v.Status == VersionStatus.Live && v.Id != newest.Id).OrderByDescending(v => v.Version, cmp).FirstOrDefault();
        var target = olderLive;
        if (target is null)
        {
            target = versions.Where(v => v.Status == VersionStatus.Withdrawn && v.BlockedAt is null && cmp.Compare(v.Version, newest.Version) < 0
                                         && VersionActionService.RestoreTarget(v) == VersionStatus.Live)
                .OrderByDescending(v => v.Version, cmp).FirstOrDefault();
            if (target is null) return new(false, "There is no earlier version approved for live to go back to.");
            if (await _actions.RestoreAsync(target.Id, actor, true) is null) return new(false, "The earlier version cannot be restored.");
        }
        if (await _actions.WithdrawAsync(newest.Id, actor, true) is null)
        {
            if (olderLive is null) await _actions.WithdrawAsync(target.Id, actor, true);   // never leave two live versions behind
            return new(false, "The newest version cannot be withdrawn.");
        }
        await _audit.LogAsync(actor.DisplayName, "addon.rollback", packageId, $"{newest.Version} -> {target.Version}");
        CatalogUi.Invalidate();
        return new(true, "Back to version {0}: it is live again, {1} is withdrawn.", new object[] { target.Version, newest.Version });
    }

    /// <summary>Daily reminder for each add-on whose review date has come (background worker).</summary>
    public async Task<int> RemindAsync()
    {
        var now = DateTime.UtcNow;
        var due = await _db.AddonSuspensions
            .Where(s => s.ReviewAt != null && s.ReviewAt <= now && (s.LastReminderAt == null || s.LastReminderAt < now.AddHours(-20)))
            .ToListAsync();
        foreach (var s in due)
        {
            var html = $"<p><b>{Enc(s.PackageId)}</b> has been {(s.Level == "disabled" ? "out of service" : "paused")} since {s.CreatedAt:yyyy-MM-dd}; " +
                       $"its review date has come.</p><p>Reason: {Enc(s.Reason)}</p><p>Put it back into service, or set a new review date.</p>" +
                       $"<p><a href=\"{await _notify.BaseUrlAsync()}/Admin/GitScan?view=suspend\">Git &amp; Veracode, Take out of service</a></p>";
            await _notify.NotifyStaffAsync("AddonSuspended", $"[Add-on Store] Review due: {s.PackageId}", html);
            s.LastReminderAt = now;
        }
        if (due.Count > 0) await _db.SaveChangesAsync();
        return due.Count;
    }

    /// <summary>Customers with a delivery of the add-on, to their contact address, in their language (German or English).</summary>
    private async Task<int> TellCustomersAsync(string packageId, string level, string reason)
    {
        var customers = await _db.Deliveries.Where(d => d.PackageId == packageId && d.Status == "active")
            .Join(_db.Customers.Where(c => c.Status == "active" && c.ContactEmail != null && c.ContactEmail != ""), d => d.CustomerId, c => c.Id, (d, c) => c)
            .Distinct().ToListAsync();
        var sent = 0;
        foreach (var c in customers.GroupBy(c => c.ContactEmail!.Trim().ToLowerInvariant()).Select(g => g.First()))
        {
            var de = c.Language.StartsWith("de", StringComparison.OrdinalIgnoreCase);
            var (subject, body) = (level, de) switch
            {
                ("resumed", true) => ($"[Add-on Store] {packageId} wieder verfügbar", $"<p>Das Add-on <b>{Enc(packageId)}</b> ist wieder verfügbar.</p>"),
                ("resumed", false) => ($"[Add-on Store] {packageId} available again", $"<p>The add-on <b>{Enc(packageId)}</b> is available again.</p>"),
                ("disabled", true) => ($"[Add-on Store] {packageId} vorübergehend außer Betrieb",
                    $"<p>Das Add-on <b>{Enc(packageId)}</b> ist vorübergehend außer Betrieb. Der Add-on Store schaltet installierte Kopien beim nächsten Start von Power PDF ab, ohne sie zu entfernen.</p><p>Grund: {Enc(reason)}</p>"),
                ("disabled", false) => ($"[Add-on Store] {packageId} temporarily out of service",
                    $"<p>The add-on <b>{Enc(packageId)}</b> is temporarily out of service. The Add-on Store switches installed copies off at the next Power PDF start, without removing them.</p><p>Reason: {Enc(reason)}</p>"),
                (_, true) => ($"[Add-on Store] {packageId} vorübergehend pausiert",
                    $"<p>Das Add-on <b>{Enc(packageId)}</b> ist vorübergehend pausiert: keine neuen Installationen und Updates. Installierte Kopien laufen weiter.</p><p>Grund: {Enc(reason)}</p>"),
                _ => ($"[Add-on Store] {packageId} temporarily paused",
                    $"<p>The add-on <b>{Enc(packageId)}</b> is temporarily paused: no new installations or updates. Installed copies keep running.</p><p>Reason: {Enc(reason)}</p>"),
            };
            await _notify.SendDirectAsync(c.ContactEmail!.Trim(), subject, body, "AddonSuspended");
            sent++;
        }
        if (sent > 0) await _audit.LogAsync("system", "addon.suspension.customers", packageId, $"{level}; {sent} customer contacts told");
        return sent;
    }

    private static string Enc(string s) => System.Net.WebUtility.HtmlEncode(s);
}
