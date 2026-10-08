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

    public TemplateService(AppDbContext db, AuditService audit, CustomerService customers)
    {
        _db = db; _audit = audit; _customers = customers;
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

    public static string? CheckName(string? name, string? description) =>
        string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80 || name.Any(char.IsControl) ? "TEMPLATE_INVALID"
        : description is { Length: > 1000 } ? "TEMPLATE_INVALID" : null;

    /// <summary>Checks the items (every add-on once, an existing one, the version approved); null when fine.</summary>
    public async Task<string?> CheckItemsAsync(IReadOnlyList<ItemInput> items)
    {
        if (items.Count > MaxItems) return $"at most {MaxItems} add-ons per template";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in items)
        {
            var pid = (i.PackageId ?? "").Trim();
            if (pid.Length == 0 || pid == SubmissionService.ClientPackageId || !seen.Add(pid)) return $"invalid or repeated add-on '{pid}'";
            if (!await _db.Packages.AnyAsync(p => p.Id == pid)) return $"no add-on '{pid}'";
            var v = string.IsNullOrWhiteSpace(i.Version) ? null : i.Version.Trim();
            if (v is not null && !(await _customers.DeliverableVersionsAsync(pid)).Any(x => x.Version == v))
                return $"version {v} of '{pid}' is not approved (leave the version empty for the newest approved one)";
        }
        return null;
    }

    public async Task<DeliveryTemplate> CreateAsync(string name, string? description, bool restrict, IReadOnlyList<ItemInput> items, AppUser actor)
    {
        var t = new DeliveryTemplate { Name = name.Trim(), Description = Clean(description), RestrictCatalog = restrict, OwnerId = actor.Id };
        _db.DeliveryTemplates.Add(t);
        await _db.SaveChangesAsync();
        await ReplaceItemsAsync(t, items);
        await _audit.LogAsync(actor.DisplayName, "template.created", $"template {t.Id}", $"{t.Name}; {items.Count} add-on(s)" + (restrict ? "; catalog limited" : ""));
        return t;
    }

    /// <summary>Changes name, description, the catalog option and (when given) the items, then updates every customer that has it.</summary>
    public async Task UpdateAsync(DeliveryTemplate t, string? name, string? description, bool? restrict, IReadOnlyList<ItemInput>? items, AppUser actor)
    {
        if (!string.IsNullOrWhiteSpace(name)) t.Name = name.Trim();
        if (description is not null) t.Description = Clean(description);
        if (restrict is { } r) t.RestrictCatalog = r;
        t.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        if (items is not null) await ReplaceItemsAsync(t, items);
        await _audit.LogAsync(actor.DisplayName, "template.updated", $"template {t.Id}",
            $"{t.Name}" + (items is null ? "" : $"; {items.Count} add-on(s)") + (t.RestrictCatalog ? "; catalog limited" : ""));
        await SyncTemplateAsync(t.Id, actor.DisplayName);
    }

    public async Task<string?> AddItemAsync(DeliveryTemplate t, string? packageId, string? version, AppUser actor)
    {
        var current = await _db.DeliveryTemplateItems.Where(i => i.TemplateId == t.Id).Select(i => new ItemInput(i.PackageId, i.Version)).ToListAsync();
        var pid = (packageId ?? "").Trim();
        current.RemoveAll(i => string.Equals(i.PackageId, pid, StringComparison.OrdinalIgnoreCase));   // adding again changes its version
        current.Add(new ItemInput(pid, version));
        if (await CheckItemsAsync(current) is { } err) return err;
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
        var customers = await _db.CustomerTemplates.Where(a => a.TemplateId == t.Id).Select(a => a.CustomerId).ToListAsync();
        await _db.CustomerTemplates.Where(a => a.TemplateId == t.Id).ExecuteDeleteAsync();
        await _db.DeliveryTemplateItems.Where(i => i.TemplateId == t.Id).ExecuteDeleteAsync();
        _db.DeliveryTemplates.Remove(t);
        await _db.SaveChangesAsync();
        foreach (var cid in customers) await SyncCustomerAsync(cid, actor.DisplayName);
        await _audit.LogAsync(actor.DisplayName, "template.deleted", $"template {t.Id}", $"{t.Name}; {customers.Count} customer(s)");
    }

    public async Task<string?> AssignAsync(Customer c, int templateId, AppUser actor)
    {
        var t = await _db.DeliveryTemplates.FirstOrDefaultAsync(x => x.Id == templateId);
        if (t is null) return "TEMPLATE_NOT_FOUND";
        if (!await _db.CustomerTemplates.AnyAsync(a => a.CustomerId == c.Id && a.TemplateId == templateId))
        {
            _db.CustomerTemplates.Add(new CustomerTemplate { CustomerId = c.Id, TemplateId = templateId, AssignedBy = actor.DisplayName });
            await _db.SaveChangesAsync();
            await _audit.LogAsync(actor.DisplayName, "template.assigned", $"customer {c.Id}", t.Name);
        }
        await SyncCustomerAsync(c.Id, actor.DisplayName);
        return null;
    }

    public async Task UnassignAsync(Customer c, int templateId, AppUser actor)
    {
        var n = await _db.CustomerTemplates.Where(a => a.CustomerId == c.Id && a.TemplateId == templateId).ExecuteDeleteAsync();
        if (n > 0) await _audit.LogAsync(actor.DisplayName, "template.unassigned", $"customer {c.Id}", $"template {templateId}");
        await SyncCustomerAsync(c.Id, actor.DisplayName);
    }

    public async Task SyncTemplateAsync(int templateId, string actor)
    {
        var customers = await _db.CustomerTemplates.Where(a => a.TemplateId == templateId).Select(a => a.CustomerId).ToListAsync();
        foreach (var cid in customers) await SyncCustomerAsync(cid, actor);
    }

    /// <summary>
    /// Brings the customer's template deliveries in line with its templates: an add-on of a template is delivered
    /// with the template's version rule (the first assigned template wins when two contain it); a manual delivery
    /// of the same add-on is left alone; template deliveries whose add-on left every template end.
    /// </summary>
    public async Task SyncCustomerAsync(int customerId, string actor)
    {
        var now = DateTime.UtcNow;
        var assigned = await _db.CustomerTemplates.Where(a => a.CustomerId == customerId).OrderBy(a => a.Id).Select(a => a.TemplateId).ToListAsync();
        var items = await _db.DeliveryTemplateItems.Where(i => assigned.Contains(i.TemplateId)).ToListAsync();
        var want = new Dictionary<string, DeliveryTemplateItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var tid in assigned)
            foreach (var i in items.Where(x => x.TemplateId == tid).OrderBy(x => x.Id))
                want.TryAdd(i.PackageId, i);
        var dels = await _db.Deliveries.Where(d => d.CustomerId == customerId).ToListAsync();
        int added = 0, changed = 0, ended = 0;
        foreach (var (pid, item) in want)
        {
            var d = dels.FirstOrDefault(x => string.Equals(x.PackageId, pid, StringComparison.OrdinalIgnoreCase));
            if (d is not null && d.TemplateId is null && d.Status != "ended") continue;   // a manual delivery wins
            var mode = item.Version is null ? "latest" : "fixed";
            if (d is null)
            {
                d = new Delivery { CustomerId = customerId, PackageId = item.PackageId, CreatedBy = actor, CreatedAt = now };
                _db.Deliveries.Add(d);
                added++;
            }
            else if (d.TemplateId != item.TemplateId || d.Status != "active" || d.BetaMode != mode || d.BetaVersion != item.Version ||
                     d.LiveMode != mode || d.LiveVersion != item.Version) changed++;
            else continue;
            d.TemplateId = item.TemplateId; d.Status = "active";
            d.BetaMode = mode; d.BetaVersion = item.Version; d.LiveMode = mode; d.LiveVersion = item.Version;
            d.StartsAt = null; d.EndsAt = null; d.UpdatedAt = now;
        }
        foreach (var d in dels.Where(x => x.TemplateId is not null && !want.ContainsKey(x.PackageId)))
        {
            d.Status = "ended"; d.TemplateId = null; d.UpdatedAt = now;
            ended++;
        }
        if (added + changed + ended == 0) return;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(actor, "template.synced", $"customer {customerId}", $"{added} added, {changed} changed, {ended} ended");
    }

    /// <summary>Customers whose catalog a template limits to their deliveries.</summary>
    public static async Task<HashSet<int>> RestrictedByTemplateAsync(AppDbContext db, ICollection<int> customerIds) =>
        (await db.CustomerTemplates.AsNoTracking().Where(a => customerIds.Contains(a.CustomerId))
            .Join(db.DeliveryTemplates.AsNoTracking().Where(t => t.RestrictCatalog), a => a.TemplateId, t => t.Id, (a, t) => a.CustomerId)
            .ToListAsync()).ToHashSet();

    private async Task ReplaceItemsAsync(DeliveryTemplate t, IReadOnlyList<ItemInput> items)
    {
        await _db.DeliveryTemplateItems.Where(i => i.TemplateId == t.Id).ExecuteDeleteAsync();
        foreach (var i in items)
            _db.DeliveryTemplateItems.Add(new DeliveryTemplateItem
            {
                TemplateId = t.Id, PackageId = i.PackageId!.Trim(),
                Version = string.IsNullOrWhiteSpace(i.Version) ? null : i.Version.Trim(),
            });
        await _db.SaveChangesAsync();
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(s.Trim().Length, 1000)];
}
