using System.Security.Claims;
using AddonStore.Web.Data;
using AddonStore.Web.Validation;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Services;

/// <summary>
/// Delivery templates (S1.13.0): a named set of add-ons, each with "newest approved version" or a fixed one.
/// Templates are linked: a customer can have several, the server keeps the customer's deliveries in step with
/// them (<see cref="SyncCustomerAsync"/>), and a change of a template reaches every customer that has it.
/// Deliveries made from a template carry its id (<see cref="Delivery.TemplateId"/>). A manual delivery of the
/// same add-on wins; changing a template delivery by hand detaches it from the template (it becomes manual).
/// </summary>
public class TemplateService
{
    public const int MaxItems = 50;
    private readonly AppDbContext _db;
    private readonly AuditService _audit;
    private readonly CustomerService _customers;
    private readonly NotificationService _notify;

    public TemplateService(AppDbContext db, AuditService audit, CustomerService customers, NotificationService notify)
    {
        _db = db; _audit = audit; _customers = customers; _notify = notify;
    }

    public record ItemInput(string? PackageId, string? Version);
    public record View(DeliveryTemplate Template, List<DeliveryTemplateItem> Items, int Customers, string Owner);

    /// <summary>Templates are shared: every developer and admin may assign them; the creator and admins edit them.</summary>
    public static bool CanEdit(ClaimsPrincipal user, DeliveryTemplate t, string userId) =>
        t.OwnerId == userId || user.IsInRole("Admin");

    public async Task<List<View>> ListAsync()
    {
        var ts = await _db.DeliveryTemplates.AsNoTracking().OrderBy(t => t.Name).ToListAsync();
        var items = await _db.DeliveryTemplateItems.AsNoTracking().OrderBy(i => i.PackageId).ToListAsync();
        var uses = await _db.CustomerTemplates.AsNoTracking().GroupBy(a => a.TemplateId).Select(g => new { g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N);
        var ownerIds = ts.Select(t => t.OwnerId).Distinct().ToList();
        var owners = await _db.Users.AsNoTracking().Where(u => ownerIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName);
        return ts.Select(t => new View(t, items.Where(i => i.TemplateId == t.Id).ToList(), uses.GetValueOrDefault(t.Id),
                                      owners.GetValueOrDefault(t.OwnerId, "?"))).ToList();
    }

    // text-direction overrides and control characters would let a name pose as another one (hardening S1.13.1)
    private static bool Unsafe(string s, bool lines) => s.Any(ch =>
        (char.IsControl(ch) && !(lines && ch is '\n' or '\r')) || ch is '\u202A' or '\u202B' or '\u202C' or '\u202D' or '\u202E' or '\u2066' or '\u2067' or '\u2068' or '\u2069');

    public static string? CheckName(string? name, string? description) =>
        string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80 || Unsafe(name, false) ? "TEMPLATE_INVALID"
        : description is { Length: > 1000 } || (description is not null && Unsafe(description, true)) ? "TEMPLATE_INVALID" : null;

    // template writes one after the other (hardening S1.13.1: parallel assignments raced on the unique indexes and on
    // SQLite's write lock); templates change rarely, so one lock for all of them costs nothing
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static async Task<IDisposable> LockAsync(int customerId)
    {
        await Gate.WaitAsync();
        return new Release(Gate);
    }
    /// <summary>Runs a database write again when SQLite is briefly locked by a parallel request (S1.13.1).</summary>
    private static async Task<T> Busy<T>(Func<Task<T>> write)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await write(); }
            catch (Exception ex) when (attempt < 5 && (ex as Microsoft.Data.Sqlite.SqliteException ?? ex.InnerException as Microsoft.Data.Sqlite.SqliteException)
                                                       is { SqliteErrorCode: 5 or 6 })
            {
                await Task.Delay(50 * attempt * attempt);
            }
        }
    }
    private Task<int> Save() => Busy(() => _db.SaveChangesAsync());

    private sealed class Release : IDisposable
    {
        private SemaphoreSlim? _s;
        public Release(SemaphoreSlim s) => _s = s;
        public void Dispose() { _s?.Release(); _s = null; }
    }

    /// <summary>Checks the items (every add-on once, an existing one, the version approved); null when fine.
    /// <paramref name="onlyVersionOf"/>: check the version of that add-on only (adding one item).</summary>
    public async Task<string?> CheckItemsAsync(IReadOnlyList<ItemInput> items, string? onlyVersionOf = null)
    {
        if (items.Count > MaxItems) return $"at most {MaxItems} add-ons per template";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in items)
        {
            var pid = (i.PackageId ?? "").Trim();
            if (pid.Length == 0 || pid == SubmissionService.ClientPackageId || !seen.Add(pid)) return $"invalid or repeated add-on '{pid}'";
            if (!await _db.Packages.AnyAsync(p => p.Id == pid)) return $"no add-on '{pid}'";
            var v = string.IsNullOrWhiteSpace(i.Version) ? null : i.Version.Trim();
            if (onlyVersionOf is not null && !string.Equals(pid, onlyVersionOf, StringComparison.OrdinalIgnoreCase)) continue;
            if (v is not null && !(await _customers.DeliverableVersionsAsync(pid)).Any(x => x.Version == v))
                return $"version {v} of '{pid}' is not approved (leave the version empty for the newest approved one)";
        }
        return null;
    }

    public async Task<DeliveryTemplate> CreateAsync(string name, string? description, bool restrict, IReadOnlyList<ItemInput> items, AppUser actor)
    {
        using var gate = await LockAsync(0);
        var t = new DeliveryTemplate { Name = name.Trim(), Description = Clean(description), RestrictCatalog = restrict, OwnerId = actor.Id };
        _db.DeliveryTemplates.Add(t);
        await Save();
        await ReplaceItemsAsync(t, items);
        await _audit.LogAsync(actor.DisplayName, "template.created", $"template {t.Id}", $"{t.Name}; {items.Count} add-on(s)" + (restrict ? "; catalog limited" : ""));
        return t;
    }

    /// <summary>Changes name, description, the catalog option and (when given) the items, then updates every customer that has it.</summary>
    public async Task UpdateAsync(DeliveryTemplate t, string? name, string? description, bool? restrict, IReadOnlyList<ItemInput>? items, AppUser actor)
    {
        using var gate = await LockAsync(0);
        if (!string.IsNullOrWhiteSpace(name)) t.Name = name.Trim();
        if (description is not null) t.Description = Clean(description);
        if (restrict is { } r) t.RestrictCatalog = r;
        t.UpdatedAt = DateTime.UtcNow;
        await Save();
        if (items is not null) await ReplaceItemsAsync(t, items);
        await _audit.LogAsync(actor.DisplayName, "template.updated", $"template {t.Id}",
            $"{t.Name}" + (items is null ? "" : $"; {items.Count} add-on(s)") + (t.RestrictCatalog ? "; catalog limited" : ""));
        await NotifyUsersOfAsync(t, actor, "changed");
        foreach (var cid in await _db.CustomerTemplates.Where(a => a.TemplateId == t.Id).Select(a => a.CustomerId).ToListAsync())
            await SyncCoreAsync(cid, actor);   // under the lock already
    }

    public async Task<string?> AddItemAsync(DeliveryTemplate t, string? packageId, string? version, AppUser actor)
    {
        var current = await _db.DeliveryTemplateItems.Where(i => i.TemplateId == t.Id).Select(i => new ItemInput(i.PackageId, i.Version)).ToListAsync();
        var pid = (packageId ?? "").Trim();
        current.RemoveAll(i => string.Equals(i.PackageId, pid, StringComparison.OrdinalIgnoreCase));   // adding again changes its version
        current.Add(new ItemInput(pid, version));
        if (await CheckItemsAsync(current, pid) is { } err) return err;
        await UpdateAsync(t, null, null, null, current, actor);
        return null;
    }

    public async Task RemoveItemAsync(DeliveryTemplate t, int itemId, AppUser actor)
    {
        var current = await _db.DeliveryTemplateItems.Where(i => i.TemplateId == t.Id && i.Id != itemId)
            .Select(i => new ItemInput(i.PackageId, i.Version)).ToListAsync();
        await UpdateAsync(t, null, null, null, current, actor);
    }

    /// <summary>Deletes the template; the deliveries made from it end (customers keep their manual ones).</summary>
    public async Task DeleteAsync(DeliveryTemplate t, AppUser actor)
    {
        using var gate = await LockAsync(0);
        await NotifyUsersOfAsync(t, actor, "deleted");
        var customers = await _db.CustomerTemplates.Where(a => a.TemplateId == t.Id).Select(a => a.CustomerId).ToListAsync();
        await Busy(() => _db.CustomerTemplates.Where(a => a.TemplateId == t.Id).ExecuteDeleteAsync());
        await Busy(() => _db.DeliveryTemplateItems.Where(i => i.TemplateId == t.Id).ExecuteDeleteAsync());
        _db.DeliveryTemplates.Remove(t);
        await Save();
        foreach (var cid in customers) await SyncCoreAsync(cid, actor);   // under the lock already
        await _audit.LogAsync(actor.DisplayName, "template.deleted", $"template {t.Id}", $"{t.Name}; {customers.Count} customer(s)");
    }

    public async Task<string?> AssignAsync(Customer c, int templateId, AppUser actor)
    {
        var t = await _db.DeliveryTemplates.FirstOrDefaultAsync(x => x.Id == templateId);
        if (t is null) return "TEMPLATE_NOT_FOUND";
        using (await LockAsync(c.Id))
        {
            if (!await _db.CustomerTemplates.AnyAsync(a => a.CustomerId == c.Id && a.TemplateId == templateId))
            {
                _db.CustomerTemplates.Add(new CustomerTemplate { CustomerId = c.Id, TemplateId = templateId, AssignedBy = actor.DisplayName });
                await Save();
                await _audit.LogAsync(actor.DisplayName, "template.assigned", $"customer {c.Id}", t.Name);
            }
            await SyncCoreAsync(c.Id, actor);
        }
        return null;
    }

    public async Task UnassignAsync(Customer c, int templateId, AppUser actor)
    {
        using (await LockAsync(c.Id))
        {
            var n = await Busy(() => _db.CustomerTemplates.Where(a => a.CustomerId == c.Id && a.TemplateId == templateId).ExecuteDeleteAsync());
            if (n > 0) await _audit.LogAsync(actor.DisplayName, "template.unassigned", $"customer {c.Id}", $"template {templateId}");
            await SyncCoreAsync(c.Id, actor);
        }
    }

    public async Task SyncTemplateAsync(int templateId, AppUser actor)
    {
        var customers = await _db.CustomerTemplates.Where(a => a.TemplateId == templateId).Select(a => a.CustomerId).ToListAsync();
        foreach (var cid in customers) await SyncCustomerAsync(cid, actor);
    }

    public async Task SyncCustomerAsync(int customerId, AppUser actor)
    {
        using (await LockAsync(customerId)) await SyncCoreAsync(customerId, actor);
    }

    /// <summary>
    /// Brings the customer's template deliveries in line with its templates: an add-on of a template is delivered
    /// with the template's version rule (the first assigned template wins when two contain it); a manual delivery
    /// of the same add-on is left alone, also an ended one (audit S1.13.1: a template must not revive what someone
    /// ended by hand); template deliveries whose add-on left every template end and keep their template mark, so a
    /// template may bring them back. Call it under <see cref="LockAsync"/>.
    /// </summary>
    private async Task SyncCoreAsync(int customerId, AppUser actorUser)
    {
        var actor = actorUser.DisplayName;
        var added = new List<Delivery>();
        var actorIsAdmin = await _db.UserRoles.AnyAsync(r => r.UserId == actorUser.Id && _db.Roles.Any(x => x.Id == r.RoleId && x.Name == "Admin"));
        var now = DateTime.UtcNow;
        var assigned = await _db.CustomerTemplates.Where(a => a.CustomerId == customerId).OrderBy(a => a.Id).Select(a => a.TemplateId).ToListAsync();
        var items = await _db.DeliveryTemplateItems.Where(i => assigned.Contains(i.TemplateId)).ToListAsync();
        var want = new Dictionary<string, DeliveryTemplateItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var tid in assigned)
            foreach (var i in items.Where(x => x.TemplateId == tid).OrderBy(x => x.Id))
                want.TryAdd(i.PackageId, i);
        var dels = await _db.Deliveries.Where(d => d.CustomerId == customerId).ToListAsync();
        var wantIds = want.Keys.ToList();
        var owners = await _db.Packages.AsNoTracking().Where(p => wantIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.OwnerId, StringComparer.OrdinalIgnoreCase);
        int changed = 0, ended = 0;
        foreach (var (pid, item) in want)
        {
            var d = dels.FirstOrDefault(x => string.Equals(x.PackageId, pid, StringComparison.OrdinalIgnoreCase));
            if (d is not null && d.TemplateId is null)
            {
                // a running manual delivery wins; an ended or expired one goes to the template only when the actor
                // may change that delivery by hand too (admin or the add-on's owner), never by someone else's template edit
                var over = d.Status == "ended" || (d.EndsAt is not null && d.EndsAt <= now);
                if (!over || !(actorIsAdmin || owners.GetValueOrDefault(d.PackageId) == actorUser.Id)) continue;
            }
            var mode = item.Version is null ? "latest" : "fixed";
            if (d is null)
            {
                d = new Delivery { CustomerId = customerId, PackageId = item.PackageId, CreatedBy = actor, CreatedAt = now };
                _db.Deliveries.Add(d);
                added.Add(d);
            }
            else if (d.TemplateId != item.TemplateId || d.Status != "active" || d.BetaMode != mode || d.BetaVersion != item.Version ||
                     d.LiveMode != mode || d.LiveVersion != item.Version) changed++;
            else continue;
            d.TemplateId = item.TemplateId; d.Status = "active";
            d.BetaMode = mode; d.BetaVersion = item.Version; d.LiveMode = mode; d.LiveVersion = item.Version;
            d.StartsAt = null; d.EndsAt = null; d.UpdatedAt = now;
        }
        foreach (var d in dels.Where(x => x.TemplateId is not null && x.Status != "ended" && !want.ContainsKey(x.PackageId)))
        {
            d.Status = "ended"; d.UpdatedAt = now;   // keeps TemplateId: ended by a template, a template may revive it
            ended++;
        }
        if (added.Count + changed + ended == 0) return;
        try { await Save(); }
        catch (DbUpdateException)
        {
            // a manual delivery of the same add-on was created at this moment: it wins; the next sync is clean
            foreach (var e in _db.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified).ToList()) e.State = EntityState.Detached;
            return;
        }
        await _audit.LogAsync(actor, "template.synced", $"customer {customerId}", $"{added.Count} added, {changed} changed, {ended} ended");
        // the same notices as a manual delivery (audit S1.13.1): the add-on's owner, and the staff for non-admins
        if (added.Count == 0) return;
        var customer = await _db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == customerId);
        if (customer is null) return;
        var isAdmin = actorIsAdmin;
        foreach (var d in added)
        {
            var pkg = await _db.Packages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == d.PackageId);
            if (pkg is null) continue;
            var text = System.Net.WebUtility.HtmlEncode($"{actor} delivered {d.PackageId} to the customer {customer.Name} with a delivery template.");
            if (!isAdmin) await _notify.NotifyStaffAsync("CustomerDelivery", $"[Add-on Store] Delivery: {d.PackageId} to {customer.Name}", text);
            if (pkg.OwnerId != actorUser.Id && await _db.Users.FirstOrDefaultAsync(u => u.Id == pkg.OwnerId) is { } owner)
                await _notify.NotifyUserAsync("CustomerDelivery", owner, $"[Add-on Store] Your add-on {d.PackageId} was delivered to {customer.Name}", text);
        }
    }

    /// <summary>Customers whose catalog a template limits to their deliveries.</summary>
    public static async Task<HashSet<int>> RestrictedByTemplateAsync(AppDbContext db, ICollection<int> customerIds) =>
        (await db.CustomerTemplates.AsNoTracking().Where(a => customerIds.Contains(a.CustomerId))
            .Join(db.DeliveryTemplates.AsNoTracking().Where(t => t.RestrictCatalog), a => a.TemplateId, t => t.Id, (a, t) => a.CustomerId)
            .ToListAsync()).ToHashSet();

    private async Task ReplaceItemsAsync(DeliveryTemplate t, IReadOnlyList<ItemInput> items)
    {
        await using var tx = await _db.Database.BeginTransactionAsync();
        await Busy(() => _db.DeliveryTemplateItems.Where(i => i.TemplateId == t.Id).ExecuteDeleteAsync());
        foreach (var i in items)
            _db.DeliveryTemplateItems.Add(new DeliveryTemplateItem
            {
                TemplateId = t.Id, PackageId = i.PackageId!.Trim(),
                Version = string.IsNullOrWhiteSpace(i.Version) ? null : i.Version.Trim(),
            });
        await Save();
        await tx.CommitAsync();
    }

    /// <summary>Tells the people who manage customers with this template (other than the actor) what changed (audit S1.13.1).</summary>
    private async Task NotifyUsersOfAsync(DeliveryTemplate t, AppUser actor, string what)
    {
        var ownerIds = await _db.CustomerTemplates.AsNoTracking().Where(a => a.TemplateId == t.Id)
            .Join(_db.Customers.AsNoTracking(), a => a.CustomerId, c => c.Id, (a, c) => c.OwnerId).Distinct().ToListAsync();
        foreach (var uid in ownerIds.Where(u => u != actor.Id))
            if (await _db.Users.FirstOrDefaultAsync(u => u.Id == uid) is { } user)
                await _notify.NotifyUserAsync("CustomerDelivery", user, $"[Add-on Store] Delivery template {t.Name} changed",
                    System.Net.WebUtility.HtmlEncode($"{actor.DisplayName} {what} the delivery template {t.Name}. Your customers with this template follow the change."));
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(s.Trim().Length, 1000)];
}
