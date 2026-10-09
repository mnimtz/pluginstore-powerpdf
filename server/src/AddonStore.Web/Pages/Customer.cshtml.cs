using AddonStore.Web.Data;
using AddonStore.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Pages;

/// <summary>One customer (S0.14.0): master data, codes, deliveries with beta and live stage.</summary>
public class CustomerModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly CustomerService _customers;

    public record CodeRow(CustomerCode Code, string? Plain, bool Valid, string Scope);
    public record DeliveryRow(Delivery Delivery, string Name, string Visibility, List<string> Versions,
                              string? BetaResolved, string? LiveResolved, bool Effective, List<DeliverySeat> Seats, int SeatCount);
    /// <summary>Installations listed per delivery on the page (the newest); the API pages through all (S1.4.3).</summary>
    public const int SeatsShown = 50;

    public Customer? Cust { get; private set; }
    public string Owner { get; private set; } = "";
    public bool CanManage { get; private set; }
    public List<CodeRow> Codes { get; private set; } = new();
    public List<DeliveryRow> Deliveries { get; private set; } = new();
    /// <summary>Add-ons for the add-on choice (S1.14.0); Present = delivered to this customer already.</summary>
    public List<AddonPicker.Item> PickItems { get; private set; } = new();
    public string? Notice { get; private set; }
    public string NoticeKind { get; private set; } = "ok";

    public CustomerModel(AppDbContext db, UserManager<AppUser> users, CustomerService customers, TemplateService templates)
    {
        _db = db; _users = users; _customers = customers; _templates = templates;
    }
    private readonly TemplateService _templates;
    /// <summary>Delivery templates (S1.13.0): assigned ones in order, the others to choose from, names by id.</summary>
    public List<DeliveryTemplate> AssignedTemplates { get; private set; } = new();
    public List<DeliveryTemplate> OtherTemplates { get; private set; } = new();
    public Dictionary<int, string> TemplateNames { get; private set; } = new();
    public bool RestrictedByTemplate { get; private set; }

    public Task<IActionResult> OnPostAssignTemplateAsync(int id, int templateId) =>
        ActAsync(id, async me => await _templates.AssignAsync(Cust!, templateId, me) is null
            ? (true, "Template assigned. Its add-ons are delivered now.") : (false, "This action is not allowed for this version."));

    public Task<IActionResult> OnPostUnassignTemplateAsync(int id, int templateId) =>
        ActAsync(id, async me => { await _templates.UnassignAsync(Cust!, templateId, me); return (true, "Template removed. Its deliveries ended."); });

    public Task<IActionResult> OnPostDetachAsync(int id, int did) =>
        ActAsync(id, async me =>
        {
            var d = await OwnDeliveryAsync(id, did);   // the same rule as every other change of a delivery
            if (d is null) return (false, "This action is not allowed for this version.");
            d.TemplateId = null; d.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            await HttpContext.RequestServices.GetRequiredService<AuditService>().LogAsync(me.DisplayName, "delivery.detached", $"delivery {d.Id}", d.PackageId);
            return (true, "The delivery is manual now; the template no longer changes it.");
        });

    private async Task<bool> LoadAsync(int id)
    {
        _db.ChangeTracker.Clear();
        var me = await _users.GetUserAsync(User);
        Cust = await _db.Customers.FirstOrDefaultAsync(c => c.Id == id);
        if (me is null || Cust is null || !CustomerService.CanSee(User, Cust, me.Id)) return false;
        CanManage = CustomerService.CanManage(User, Cust, me.Id);
        Owner = (await _db.Users.FirstOrDefaultAsync(u => u.Id == Cust.OwnerId))?.DisplayName ?? "?";
        var lang = AddonStore.Web.Services.Lang.Current;
        var now = DateTime.UtcNow;

        var deliveries = await _db.Deliveries.Where(d => d.CustomerId == id).OrderBy(d => d.PackageId).ToListAsync();
        Deliveries.Clear();
        foreach (var d in deliveries)
        {
            var pkg = await _db.Packages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == d.PackageId);
            var versions = await _customers.DeliverableVersionsAsync(d.PackageId);
            var priv = pkg?.Visibility == "private";
            var seatQuery = _db.DeliverySeats.AsNoTracking().Where(s => s.DeliveryId == d.Id && s.ReleasedAt == null);
            var seatCount = await seatQuery.CountAsync();   // installations (S1.4.2); the newest are listed (S1.4.3)
            var seats = seatCount == 0 ? new List<DeliverySeat>() : await seatQuery.OrderByDescending(s => s.LastSeenAt).Take(SeatsShown).ToListAsync();
            Deliveries.Add(new DeliveryRow(d, pkg is null ? d.PackageId : CatalogUi.DisplayName(pkg, versions.FirstOrDefault(), lang),
                pkg?.Visibility ?? "public", versions.Select(v => v.Version).ToList(),
                CustomerService.Resolve(versions, priv, d.BetaMode, d.BetaVersion, false)?.Version,
                CustomerService.Resolve(versions, priv, d.LiveMode, d.LiveVersion, true)?.Version,
                d.Status == "active" && (d.StartsAt is null || d.StartsAt <= now) && (d.EndsAt is null || d.EndsAt > now), seats, seatCount));
        }

        var allTemplates = await _db.DeliveryTemplates.AsNoTracking().OrderBy(t => t.Name).ToListAsync();
        TemplateNames = allTemplates.ToDictionary(t => t.Id, t => t.Name);
        var assignedIds = await _db.CustomerTemplates.AsNoTracking().Where(a => a.CustomerId == id).OrderBy(a => a.Id).Select(a => a.TemplateId).ToListAsync();
        AssignedTemplates = assignedIds.Select(i => allTemplates.FirstOrDefault(t => t.Id == i)).Where(t => t is not null).Select(t => t!).ToList();
        OtherTemplates = allTemplates.Where(t => !assignedIds.Contains(t.Id)).ToList();
        RestrictedByTemplate = AssignedTemplates.Any(t => t.RestrictCatalog);

        var codes = await _db.CustomerCodes.Where(c => c.CustomerId == id).OrderByDescending(c => c.Id).ToListAsync();
        Codes = codes.Select(c =>
        {
            var valid = c.RevokedAt is null && (c.ExpiresAt is null || c.ExpiresAt > now);
            var scope = c.DeliveryId is null ? "" : Deliveries.FirstOrDefault(d => d.Delivery.Id == c.DeliveryId)?.Name ?? $"#{c.DeliveryId}";
            return new CodeRow(c, CanManage && valid ? _customers.Reveal(c) : null, valid, scope);
        }).ToList();

        // add-ons to deliver (S1.12.0: public ones too, e.g. for a customer whose catalog is limited to its deliveries;
        // S1.11.0: one waiting for its first approval too, the delivery then follows "newest")
        PickItems = CanManage
            ? await AddonPicker.BuildAsync(_db, lang, me.Id, deliveries.Select(d => d.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase), withWaiting: true)
            : new();
        return true;
    }

    public async Task<IActionResult> OnGetAsync(int id) => await LoadAsync(id) ? Page() : NotFound();

    private async Task<IActionResult> ActAsync(int id, Func<AppUser, Task<(bool Ok, string Message)>> action)
    {
        if (!await LoadAsync(id)) return NotFound();
        if (!CanManage) return Forbid();
        var me = (await _users.GetUserAsync(User))!;
        var (ok, msg) = await action(me);
        await LoadAsync(id);
        Notice = msg; NoticeKind = ok ? "ok" : "error";
        return Page();
    }

    public Task<IActionResult> OnPostSaveAsync(int id, string? name, string? contactName, string? contactEmail, string? language,
                                               string? note, string? status, bool restrictCatalog) =>
        ActAsync(id, async me =>
        {
            if (CustomerService.CheckCustomer(name, contactEmail, language) is not null) return (false, "Please check name and email address.");
            await _customers.UpdateCustomerAsync(Cust!, name, contactName, contactEmail, language, note, status, me.DisplayName, restrictCatalog);
            return (true, "Settings saved.");
        });

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        if (!await LoadAsync(id)) return NotFound();
        if (!CanManage) return Forbid();
        var me = (await _users.GetUserAsync(User))!;
        await _customers.DeleteCustomerAsync(Cust!, me.DisplayName);
        return RedirectToPage("/Customers", new { deleted = true });
    }

    public Task<IActionResult> OnPostCodeAsync(int id, int? deliveryId, int transitionDays) =>
        ActAsync(id, async me =>
        {
            if (deliveryId is int did && !await _db.Deliveries.AnyAsync(d => d.Id == did && d.CustomerId == id))
                return (false, "This action is not allowed for this version.");
            await _customers.CreateCodeAsync(id, deliveryId, me.DisplayName, transitionDays);
            return (true, "New code created.");
        });

    public Task<IActionResult> OnPostRevokeAsync(int id, int codeId) =>
        ActAsync(id, async me => await _customers.RevokeCodeAsync(id, codeId, me.DisplayName)
            ? (true, "Code revoked.") : (false, "This action is not allowed for this version."));

    private static DateTime? Day(string? s) =>
        DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal |
                          System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? d : null;

    public Task<IActionResult> OnPostDeliverAsync(int id, string? packageId, string betaMode, string? betaVersion, string liveMode,
                                                  string? liveVersion, string? startsAt, string? endsAt, bool ownCode) =>
        ActAsync(id, async me =>
        {
            // S1.14.0: several add-ons from the add-on choice, the stages and the period for all of them,
            // the version of a "fixed" stage per add-on (empty = the newest approved one)
            var picked = AddonPicker.Selection(Request.Form);
            if (picked.Count == 0 && !string.IsNullOrWhiteSpace(packageId)) picked.Add((packageId.Trim(), null));
            else if (picked.Count == 0) return (false, "Choose at least one add-on.");
            else { betaVersion = null; liveVersion = null; }
            if (picked.Count == 1 && !string.IsNullOrWhiteSpace(packageId))
            {
                var one = await _customers.CreateDeliveryAsync(Cust!, picked[0].Id, new(betaMode, betaVersion), new(liveMode, liveVersion),
                    Day(startsAt), Day(endsAt)?.AddDays(1), ownCode, me, User.IsInRole("Admin"));
                return (one.Ok, one.Ok ? "Delivery created." : one.Message);
            }
            int done = 0;
            var failed = new List<string>();
            foreach (var (pid, ver) in picked)
            {
                var versions = await _customers.DeliverableVersionsAsync(pid);
                var priv = PickItems.FirstOrDefault(i => i.Id == pid)?.Visibility == "private";
                // "fixed" without a chosen version: the newest approved one (for live: the newest live one of a public
                // add-on); none yet (waits for its first approval): the stage follows "newest" instead
                var bv = betaMode == "fixed" ? ver ?? versions.FirstOrDefault()?.Version : null;
                var lv = liveMode == "fixed" ? ver ?? (priv ? versions.FirstOrDefault() : versions.FirstOrDefault(v => v.Status == VersionStatus.Live))?.Version : null;
                var bm = betaMode == "fixed" && bv is null ? "latest" : betaMode;
                var lm = liveMode == "fixed" && lv is null ? "latest" : liveMode;
                var r = await _customers.CreateDeliveryAsync(Cust!, pid, new(bm, bv), new(lm, lv),
                    Day(startsAt), Day(endsAt)?.AddDays(1), ownCode, me, User.IsInRole("Admin"));
                if (r.Ok) done++;
                else failed.Add($"{PickItems.FirstOrDefault(i => i.Id == pid)?.Name ?? pid} ({L(r.Message)})");
            }
            if (failed.Count == 0) return (true, done == 1 ? "Delivery created." : Fmt("{0} deliveries created.", done));
            var not = Fmt("Not delivered: {0}", string.Join("; ", failed));
            return (false, done == 0 ? not : Fmt("{0} deliveries created.", done) + " " + not);
        });

    private string L(string key) => HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<SharedResource>>()[key];
    private string Fmt(string key, params object[] args) =>
        HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<SharedResource>>()[key, args];

    /// <summary>The delivery, if it belongs to this customer and the user may change it (admin or the add-on's owner).</summary>
    private async Task<Delivery?> OwnDeliveryAsync(int id, int did)
    {
        var d = await _db.Deliveries.FirstOrDefaultAsync(x => x.Id == did && x.CustomerId == id);
        if (d is null || User.IsInRole("Admin")) return d;
        var me = await _users.GetUserAsync(User);
        return await _db.Packages.AnyAsync(p => p.Id == d.PackageId && p.OwnerId == me!.Id) ? d : null;
    }

    public Task<IActionResult> OnPostDeliveryAsync(int id, int did, string betaMode, string? betaVersion, string liveMode, string? liveVersion,
                                                   string? startsAt, string? endsAt, string? status, string? maxInstalls, bool maxInstallsShown) =>
        ActAsync(id, async me =>
        {
            var d = await OwnDeliveryAsync(id, did);
            if (d is null) return (false, "This action is not allowed for this version.");
            // installations (S1.4.2): empty = unlimited (an empty field binds as null, hence the marker);
            // checked before anything is saved (S1.4.3)
            int? max = null;
            if (maxInstallsShown)
            {
                var raw = (maxInstalls ?? "").Trim();
                if (raw.Length > 0 && !int.TryParse(raw, out _)) return (false, "Enter a number of installations, or leave the field empty for unlimited.");
                max = raw.Length == 0 ? 0 : int.Parse(raw);
                if (await _customers.CheckMaxInstallsAsync(d.PackageId, max) is not null)
                    return (false, "Enter a number of installations, or leave the field empty for unlimited.");
            }
            var r = await _customers.UpdateDeliveryAsync(d, new(betaMode, betaVersion), new(liveMode, liveVersion),
                Day(startsAt), Day(endsAt)?.AddDays(1), true, status, me, User.IsInRole("Admin"));
            if (!r.Ok) return (false, r.Message);
            if (max is not null) await _customers.SetMaxInstallsAsync(d, max, me);
            return (true, "Delivery saved.");
        });

    public Task<IActionResult> OnPostReleaseStaleAsync(int id, int did) =>
        ActAsync(id, async me =>
        {
            var d = await OwnDeliveryAsync(id, did);
            if (d is null) return (false, "This action is not allowed for this version.");
            var n = await HttpContext.RequestServices.GetRequiredService<SeatService>().ReleaseStaleAsync(d.Id, 90, me);
            return (true, n == 0 ? "No installation was older than 90 days." : "Installations not seen for 90 days were freed.");
        });

    public Task<IActionResult> OnPostReleaseSeatAsync(int id, int did, int sid) =>
        ActAsync(id, async me =>
        {
            var d = await OwnDeliveryAsync(id, did);
            if (d is null) return (false, "This action is not allowed for this version.");
            var ok = await HttpContext.RequestServices.GetRequiredService<SeatService>().ReleaseSeatAsync(d.Id, sid, me);
            return (ok, ok ? "Installation released." : "This installation is no longer in use.");
        });

    public Task<IActionResult> OnPostPromoteAsync(int id, int did) =>
        ActAsync(id, async me =>
        {
            var d = await OwnDeliveryAsync(id, did);
            if (d is null) return (false, "This action is not allowed for this version.");
            var r = await _customers.PromoteAsync(d, me, User.IsInRole("Admin"));
            return (r.Ok, r.Ok ? "The beta version is now live for this customer." : r.Message);
        });

    public Task<IActionResult> OnPostStatusAsync(int id, int did, string status) =>
        ActAsync(id, async me =>
        {
            var d = await OwnDeliveryAsync(id, did);
            if (d is null) return (false, "This action is not allowed for this version.");
            var r = await _customers.UpdateDeliveryAsync(d, null, null, null, null, false, status, me, User.IsInRole("Admin"));
            return (r.Ok, r.Ok ? "Delivery saved." : r.Message);
        });
}
