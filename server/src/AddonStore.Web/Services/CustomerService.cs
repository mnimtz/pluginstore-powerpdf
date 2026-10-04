using System.Security.Cryptography;
using System.Text;
using AddonStore.Web.Data;
using AddonStore.Web.Validation;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Services;

/// <summary>
/// Customer deliveries (S0.14.0): add-ons delivered to a customer by code,
/// with a beta and a live stage each. A code is valid either for all
/// deliveries of its customer or for one delivery. Codes are stored
/// encrypted (people who manage the customer can show them any time) and as
/// SHA-256 for the lookup. Clients send codes only in the X-Customer-Code
/// header, never in a URL.
/// </summary>
public class CustomerService
{
    public const string HeaderName = "X-Customer-Code";
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";   // no 0/O, 1/I: easy to read aloud
    private const int MaxFailedPerHour = 30;

    private readonly AppDbContext _db;
    private readonly IDataProtector _protector;
    private readonly IMemoryCache _cache;
    private readonly AuditService _audit;
    private readonly NotificationService _notify;
    private readonly ILogger<CustomerService> _log;

    public CustomerService(AppDbContext db, IDataProtectionProvider dp, IMemoryCache cache, AuditService audit,
                           NotificationService notify, ILogger<CustomerService> log)
    {
        _db = db; _protector = dp.CreateProtector("AddonStore.CustomerCode"); _cache = cache;
        _audit = audit; _notify = notify; _log = log;
    }

    public record Outcome(bool Ok, string Code, string Message, object? Data = null)
    {
        public static Outcome Fail(string code, string message) => new(false, code, message);
    }

    // ------------------------------------------------------------------ codes
    /// <summary>"K7QM-4XRT-9WPL-2HDN-6CVB": 20 characters of a 32-letter alphabet = 100 bits.</summary>
    public static string NewCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(20);
        var sb = new StringBuilder();
        for (int i = 0; i < 20; i++)
        {
            if (i > 0 && i % 4 == 0) sb.Append('-');
            sb.Append(Alphabet[bytes[i] & 31]);
        }
        return sb.ToString();
    }

    public static string Normalize(string code) =>
        new string(code.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    public static string Hash(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(code)))).ToLowerInvariant();

    public string Reveal(CustomerCode c)
    {
        try { return _protector.Unprotect(c.CodeProtected); }
        catch (CryptographicException) { return c.Prefix + "…"; }   // key ring lost: show the prefix only
    }

    private static bool CodeValid(CustomerCode c, DateTime now) =>
        c.RevokedAt is null && (c.ExpiresAt is null || c.ExpiresAt > now);

    /// <summary>
    /// Creates a code for the customer (deliveryId null) or for one delivery.
    /// An existing valid code of the same scope keeps working for
    /// <paramref name="transitionDays"/> days, then expires (0 = at once).
    /// </summary>
    public async Task<(CustomerCode Entity, string Plain)> CreateCodeAsync(int customerId, int? deliveryId, string actor, int transitionDays = 14)
    {
        var now = DateTime.UtcNow;
        foreach (var old in await _db.CustomerCodes.Where(c => c.CustomerId == customerId && c.DeliveryId == deliveryId &&
                                                               c.RevokedAt == null).ToListAsync())
            if (old.ExpiresAt is null || old.ExpiresAt > now)
                old.ExpiresAt = now.AddDays(Math.Clamp(transitionDays, 0, 365));
        var plain = NewCode();
        var entity = new CustomerCode
        {
            CustomerId = customerId, DeliveryId = deliveryId, CodeHash = Hash(plain), CodeProtected = _protector.Protect(plain),
            Prefix = plain[..4], CreatedBy = actor, CreatedAt = now,
        };
        _db.CustomerCodes.Add(entity);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(actor, "customer.code.created", $"customer {customerId}",
            (deliveryId is null ? "customer code" : $"code for delivery {deliveryId}") + $", prefix {entity.Prefix}, transition {transitionDays} days");
        return (entity, plain);
    }

    public async Task<bool> RevokeCodeAsync(int customerId, int codeId, string actor)
    {
        var c = await _db.CustomerCodes.FirstOrDefaultAsync(x => x.Id == codeId && x.CustomerId == customerId);
        if (c is null) return false;
        c.RevokedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(actor, "customer.code.revoked", $"customer {customerId}", $"prefix {c.Prefix}");
        return true;
    }

    // ----------------------------------------------------------- permissions
    public static bool IsAdmin(System.Security.Claims.ClaimsPrincipal u) => u.IsInRole("Admin");
    public static bool CanSee(System.Security.Claims.ClaimsPrincipal u, Customer c, string userId) =>
        u.IsInRole("Admin") || u.IsInRole("Reviewer") || c.OwnerId == userId;
    public static bool CanManage(System.Security.Claims.ClaimsPrincipal u, Customer c, string userId) =>
        u.IsInRole("Admin") || c.OwnerId == userId;

    public IQueryable<Customer> Visible(System.Security.Claims.ClaimsPrincipal u, string userId) =>
        u.IsInRole("Admin") || u.IsInRole("Reviewer") ? _db.Customers : _db.Customers.Where(c => c.OwnerId == userId);

    // ------------------------------------------------------ customers (CRUD)
    public static string? CheckCustomer(string? name, string? email, string? language)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 120) return "CUSTOMER_INVALID";
        if (!string.IsNullOrWhiteSpace(email) && !System.Net.Mail.MailAddress.TryCreate(email.Trim(), out _)) return "CUSTOMER_INVALID";
        if (!string.IsNullOrWhiteSpace(language) && !System.Text.RegularExpressions.Regex.IsMatch(language.Trim(), "^[a-z]{2}$")) return "CUSTOMER_INVALID";
        return null;
    }

    public async Task<Customer> CreateCustomerAsync(string name, string? contactName, string? email, string? language, string? note,
                                                    AppUser owner, bool withCode)
    {
        var c = new Customer
        {
            Name = name.Trim(), ContactName = Clean(contactName, 120), ContactEmail = Clean(email, 200),
            Language = string.IsNullOrWhiteSpace(language) ? "de" : language.Trim(), Note = Clean(note, 2000), OwnerId = owner.Id,
        };
        _db.Customers.Add(c);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(owner.DisplayName, "customer.created", $"customer {c.Id}", c.Name);
        if (withCode) await CreateCodeAsync(c.Id, null, owner.DisplayName, 0);
        return c;
    }

    private static string? Clean(string? v, int max)
    {
        v = v?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        return v.Length > max ? v[..max] : v;
    }

    public async Task UpdateCustomerAsync(Customer c, string? name, string? contactName, string? email, string? language, string? note,
                                          string? status, string actor)
    {
        if (!string.IsNullOrWhiteSpace(name)) c.Name = name.Trim();
        c.ContactName = Clean(contactName, 120);
        c.ContactEmail = Clean(email, 200);
        if (!string.IsNullOrWhiteSpace(language)) c.Language = language.Trim();
        c.Note = Clean(note, 2000);
        if (status is "active" or "paused") c.Status = status;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(actor, "customer.updated", $"customer {c.Id}", $"{c.Name}, status {c.Status}");
    }

    // ------------------------------------------------------------ deliveries
    public record StageInput(string? Mode, string? Version);

    /// <summary>Versions a delivery may hand out: passed the automatic checks (beta) or approved (live).</summary>
    public async Task<List<PackageVersion>> DeliverableVersionsAsync(string packageId) =>
        (await _db.PackageVersions.AsNoTracking()
            .Where(v => v.PackageId == packageId && (v.Status == VersionStatus.Live || v.Status == VersionStatus.Beta))
            .ToListAsync())
        .OrderByDescending(v => v.Version, new SemVerComparer()).ToList();

    /// <summary>
    /// The version a stage hands out. "latest": public packages take the newest
    /// approved version for live and the newest checked one for beta; private
    /// packages need no approval, so both take the newest checked version.
    /// </summary>
    public static PackageVersion? Resolve(List<PackageVersion> versions, bool isPrivate, string mode, string? fixedVersion, bool liveStage)
    {
        switch (mode)
        {
            case "off": return null;
            case "fixed": return versions.FirstOrDefault(v => v.Version == fixedVersion);
            default:
                return liveStage && !isPrivate
                    ? versions.FirstOrDefault(v => v.Status == VersionStatus.Live)
                    : versions.FirstOrDefault();
        }
    }

    private static string? CheckStage(StageInput s, List<PackageVersion> versions)
    {
        var mode = s.Mode ?? (s.Version is not null ? "fixed" : "latest");
        if (mode is not ("latest" or "fixed" or "off")) return "mode must be latest, fixed or off";
        if (mode == "fixed" && !versions.Any(v => v.Version == s.Version))
            return $"version '{s.Version}' is not a delivered version (it must have passed the automatic checks)";
        return null;
    }

    public async Task<Outcome> CreateDeliveryAsync(Customer customer, string packageId, StageInput beta, StageInput live,
                                                   DateTime? startsAt, DateTime? endsAt, bool ownCode, AppUser actor, bool actorIsAdmin)
    {
        var pkg = await _db.Packages.FirstOrDefaultAsync(p => p.Id == packageId);
        if (pkg is null || packageId == SubmissionService.ClientPackageId)
            return Outcome.Fail("PACKAGE_NOT_FOUND", $"No package with id '{packageId}'.");
        if (!actorIsAdmin && pkg.OwnerId != actor.Id)
            return Outcome.Fail("NOT_OWNER", "Developers deliver only their own add-ons.");
        if (await _db.Deliveries.AnyAsync(d => d.CustomerId == customer.Id && d.PackageId == packageId))
            return Outcome.Fail("DELIVERY_EXISTS", "This add-on is already delivered to this customer; change that delivery instead.");
        var versions = await DeliverableVersionsAsync(packageId);
        if (versions.Count == 0)
            return Outcome.Fail("DELIVERY_INVALID", "The add-on has no version that passed the automatic checks yet.");
        // Live default: the newest version now (for a public add-on the newest APPROVED one),
        // so later uploads reach beta workstations first.
        // A public add-on without an approved version has no live default: the live stage then
        // follows "latest", which hands out approved versions only (never an unreviewed beta).
        var liveDefault = pkg.Visibility == "private" ? versions[0] : versions.FirstOrDefault(v => v.Status == VersionStatus.Live);
        if (live.Mode is null && live.Version is null && liveDefault is null) live = live with { Mode = "latest" };
        else live = live with { Mode = live.Mode ?? "fixed", Version = live.Mode is null or "fixed" ? live.Version ?? liveDefault?.Version : live.Version };
        var err = CheckStage(beta, versions) ?? CheckStage(live, versions);
        if (err is not null) return Outcome.Fail("DELIVERY_INVALID", err);
        if (startsAt is not null && endsAt is not null && endsAt <= startsAt)
            return Outcome.Fail("DELIVERY_INVALID", "endsAt must be after startsAt.");
        var d = new Delivery
        {
            CustomerId = customer.Id, PackageId = packageId,
            BetaMode = beta.Mode ?? "latest", BetaVersion = beta.Mode == "fixed" ? beta.Version : null,
            LiveMode = live.Mode!, LiveVersion = live.Mode == "fixed" ? live.Version : null,
            StartsAt = startsAt, EndsAt = endsAt, CreatedBy = actor.DisplayName,
        };
        _db.Deliveries.Add(d);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(actor.DisplayName, "delivery.created", $"customer {customer.Id}",
            $"{packageId}: beta {d.BetaMode} {d.BetaVersion}, live {d.LiveMode} {d.LiveVersion}");
        if (ownCode) await CreateCodeAsync(customer.Id, d.Id, actor.DisplayName, 0);
        if (!actorIsAdmin)
            await _notify.NotifyStaffAsync("CustomerDelivery", $"[Add-on Store] Delivery: {packageId} to {customer.Name}",
                System.Net.WebUtility.HtmlEncode($"{actor.DisplayName} delivered {packageId} to the customer {customer.Name} (live: {d.LiveMode} {d.LiveVersion}, beta: {d.BetaMode} {d.BetaVersion})."));
        return new Outcome(true, "", "Delivery created.", d);
    }

    public async Task<Outcome> UpdateDeliveryAsync(Delivery d, StageInput? beta, StageInput? live, DateTime? startsAt, DateTime? endsAt,
                                                   bool datesGiven, string? status, AppUser actor, bool actorIsAdmin)
    {
        var versions = await DeliverableVersionsAsync(d.PackageId);
        if (beta is not null && CheckStage(beta, versions) is { } e1) return Outcome.Fail("DELIVERY_INVALID", e1);
        if (live is not null && CheckStage(live, versions) is { } e2) return Outcome.Fail("DELIVERY_INVALID", e2);
        if (status is not null and not ("active" or "paused" or "ended")) return Outcome.Fail("DELIVERY_INVALID", "status must be active, paused or ended");
        // A version without a mode means "fixed to that version"; neither keeps the current mode.
        if (beta is not null) { d.BetaMode = beta.Mode ?? (beta.Version is not null ? "fixed" : d.BetaMode); d.BetaVersion = d.BetaMode == "fixed" ? beta.Version ?? d.BetaVersion : null; }
        if (live is not null) { d.LiveMode = live.Mode ?? (live.Version is not null ? "fixed" : d.LiveMode); d.LiveVersion = d.LiveMode == "fixed" ? live.Version ?? d.LiveVersion : null; }
        if (datesGiven)
        {
            if (startsAt is not null && endsAt is not null && endsAt <= startsAt)
                return Outcome.Fail("DELIVERY_INVALID", "endsAt must be after startsAt.");
            d.StartsAt = startsAt; d.EndsAt = endsAt;
        }
        if (status is not null) d.Status = status;
        d.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(actor.DisplayName, "delivery.updated", $"delivery {d.Id}",
            $"{d.PackageId}: beta {d.BetaMode} {d.BetaVersion}, live {d.LiveMode} {d.LiveVersion}, status {d.Status}");
        if (!actorIsAdmin && live is not null)
            await _notify.NotifyStaffAsync("CustomerDelivery", $"[Add-on Store] Delivery changed: {d.PackageId}",
                System.Net.WebUtility.HtmlEncode($"{actor.DisplayName} changed the delivery of {d.PackageId} (live: {d.LiveMode} {d.LiveVersion}, status {d.Status})."));
        return new Outcome(true, "", "Delivery saved.", d);
    }

    /// <summary>"Nach Live übernehmen": the live stage gets the version the beta stage hands out now.</summary>
    public async Task<Outcome> PromoteAsync(Delivery d, AppUser actor, bool actorIsAdmin)
    {
        var pkg = await _db.Packages.AsNoTracking().FirstAsync(p => p.Id == d.PackageId);
        var versions = await DeliverableVersionsAsync(d.PackageId);
        var beta = Resolve(versions, pkg.Visibility == "private", d.BetaMode, d.BetaVersion, false);
        if (beta is null) return Outcome.Fail("PROMOTE_NOTHING", "The beta stage hands out no version that could go live.");
        return await UpdateDeliveryAsync(d, null, new StageInput("fixed", beta.Version), null, null, false, null, actor, actorIsAdmin);
    }

    // ------------------------------------------------------------ describe
    /// <summary>A customer with codes and deliveries for the API and the portal.</summary>
    public async Task<object> DescribeAsync(Customer c, bool showCodes, string culture)
    {
        var now = DateTime.UtcNow;
        var codes = await _db.CustomerCodes.AsNoTracking().Where(x => x.CustomerId == c.Id).OrderByDescending(x => x.Id).ToListAsync();
        var deliveries = await _db.Deliveries.AsNoTracking().Where(d => d.CustomerId == c.Id).OrderBy(d => d.PackageId).ToListAsync();
        var owner = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == c.OwnerId);
        var list = new List<object>();
        foreach (var d in deliveries)
        {
            var pkg = await _db.Packages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == d.PackageId);
            var versions = await DeliverableVersionsAsync(d.PackageId);
            var isPrivate = pkg?.Visibility == "private";
            var beta = Resolve(versions, isPrivate, d.BetaMode, d.BetaVersion, false);
            var live = Resolve(versions, isPrivate, d.LiveMode, d.LiveVersion, true);
            var shown = versions.FirstOrDefault();
            list.Add(new
            {
                id = d.Id, packageId = d.PackageId,
                name = pkg is null ? d.PackageId : CatalogUi.DisplayName(pkg, shown, culture),
                visibility = pkg?.Visibility ?? "public",
                beta = new { mode = d.BetaMode, version = d.BetaVersion, resolved = beta?.Version },
                live = new { mode = d.LiveMode, version = d.LiveVersion, resolved = live?.Version },
                versions = versions.Select(v => v.Version).ToList(),
                startsAt = d.StartsAt, endsAt = d.EndsAt, status = d.Status,
                effective = d.Status == "active" && (d.StartsAt is null || d.StartsAt <= now) && (d.EndsAt is null || d.EndsAt > now),
                lastSeenAt = d.LastSeenAt, createdBy = d.CreatedBy, createdAt = d.CreatedAt,
            });
        }
        return new
        {
            id = c.Id, name = c.Name, contactName = c.ContactName, contactEmail = c.ContactEmail, language = c.Language,
            note = c.Note, status = c.Status, owner = owner?.DisplayName, createdAt = c.CreatedAt, lastSeenAt = c.LastSeenAt,
            codes = codes.Select(x => new
            {
                id = x.Id, scope = x.DeliveryId is null ? "customer" : "delivery", deliveryId = x.DeliveryId,
                code = showCodes && CodeValid(x, now) ? Reveal(x) : null, prefix = x.Prefix,
                valid = CodeValid(x, now), createdAt = x.CreatedAt, createdBy = x.CreatedBy,
                expiresAt = x.ExpiresAt, revokedAt = x.RevokedAt, lastUsedAt = x.LastUsedAt,
            }),
            deliveries = list,
        };
    }

    // --------------------------------------------------- client side (codes)
    public record Granted(Delivery Delivery, Customer Customer, Package Package, PackageVersion? Beta, PackageVersion? Live);

    /// <summary>
    /// Deliveries the X-Customer-Code header of this request unlocks (several
    /// codes separated by ";" or ","). Unknown codes count against a per-address
    /// limit; above it, codes from that address are ignored for an hour.
    /// </summary>
    public async Task<List<Granted>> GrantsAsync(HttpContext ctx)
    {
        var raw = ctx.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(raw)) return new();
        var ip = GeoService.ClientIp(ctx)?.ToString() ?? "";
        var key = "custcode-fail:" + ip + ":" + DateTime.UtcNow.ToString("yyyyMMddHH");
        var fails = _cache.GetOrCreate(key, e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1); return new int[1]; })!;
        if (fails[0] >= MaxFailedPerHour) return new();

        var hashes = raw.Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize).Where(c => c.Length == 20).Distinct().Take(10).Select(Hash).ToList();
        if (hashes.Count == 0) return new();
        var now = DateTime.UtcNow;
        var rows = await _db.CustomerCodes.Where(c => hashes.Contains(c.CodeHash)).ToListAsync();
        var codes = rows.Where(c => CodeValid(c, now)).ToList();
        // expired or revoked codes are not guesses (an old code left on a workstation must not lock out the new one)
        var unknown = hashes.Count(h => !rows.Any(r => r.CodeHash == h));
        if (unknown > 0)
        {
            bool blockNow;
            lock (fails) { fails[0] += unknown; blockNow = fails[0] >= MaxFailedPerHour; }
            if (blockNow)
                await _audit.LogAsync("system", "customer.code.blocked", ip, $"{MaxFailedPerHour} unknown customer codes within an hour; codes from this address are ignored for the hour");
        }
        if (codes.Count == 0) return new();

        var customerIds = codes.Select(c => c.CustomerId).Distinct().ToList();
        var customers = await _db.Customers.Where(c => customerIds.Contains(c.Id) && c.Status == "active").ToDictionaryAsync(c => c.Id);
        var deliveries = await _db.Deliveries.Where(d => customerIds.Contains(d.CustomerId) && d.Status == "active").ToListAsync();
        var allowed = deliveries.Where(d => customers.ContainsKey(d.CustomerId) &&
                                            (d.StartsAt is null || d.StartsAt <= now) && (d.EndsAt is null || d.EndsAt > now) &&
                                            codes.Any(c => c.CustomerId == d.CustomerId && (c.DeliveryId is null || c.DeliveryId == d.Id)))
                                .ToList();
        // remember activity (at most every ten minutes per code)
        foreach (var c in codes.Where(c => c.LastUsedAt is null || c.LastUsedAt < now.AddMinutes(-10))) c.LastUsedAt = now;
        foreach (var cu in customers.Values.Where(c => c.LastSeenAt is null || c.LastSeenAt < now.AddMinutes(-10))) cu.LastSeenAt = now;
        foreach (var d in allowed.Where(d => d.LastSeenAt is null || d.LastSeenAt < now.AddMinutes(-10))) d.LastSeenAt = now;
        await _db.SaveChangesAsync();

        var pkgIds = allowed.Select(d => d.PackageId).Distinct().ToList();
        var pkgs = await _db.Packages.Where(p => pkgIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        var result = new List<Granted>();
        foreach (var d in allowed)
        {
            if (!pkgs.TryGetValue(d.PackageId, out var pkg)) continue;
            var versions = await DeliverableVersionsAsync(d.PackageId);
            var isPrivate = pkg.Visibility == "private";
            result.Add(new Granted(d, customers[d.CustomerId], pkg,
                Resolve(versions, isPrivate, d.BetaMode, d.BetaVersion, false),
                Resolve(versions, isPrivate, d.LiveMode, d.LiveVersion, true)));
        }
        return result;
    }

    /// <summary>The version a client in the given channel gets from a grant, and the channel label.</summary>
    public static (PackageVersion? Version, string Channel) Pick(Granted g, bool betaChannel)
    {
        if (betaChannel && g.Beta is not null &&
            (g.Live is null || new SemVerComparer().Compare(g.Beta.Version, g.Live.Version) > 0))
            return (g.Beta, "beta");
        return (g.Live ?? (betaChannel ? g.Beta : null), "live");
    }

    /// <summary>True when the request may see this package (public, or unlocked by a code).</summary>
    public async Task<bool> MaySeeAsync(HttpContext ctx, Package pkg)
    {
        if (pkg.Visibility != "private") return true;
        return (await GrantsAsync(ctx)).Any(g => g.Package.Id == pkg.Id);
    }

    /// <summary>True when the request may download this version of a private package.</summary>
    public async Task<bool> MayDownloadAsync(HttpContext ctx, Package pkg, string version)
    {
        if (pkg.Visibility != "private") return true;
        return (await GrantsAsync(ctx)).Any(g => g.Package.Id == pkg.Id && (g.Beta?.Version == version || g.Live?.Version == version));
    }
}
