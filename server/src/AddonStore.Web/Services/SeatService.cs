using System.Security.Cryptography;
using AddonStore.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AddonStore.Web.Services;

/// <summary>
/// Installations of delivered add-ons (S1.4.2). A delivery may allow a number of
/// installations (Delivery.MaxInstalls, null = unlimited). The store client (1.4.1+)
/// sends its random installation id (X-Install-Id) together with customer codes; a
/// download of a delivered private add-on takes a seat for that installation, an update
/// keeps it, a removal (POST /api/deliveries/release) frees it. Only a hash of the id
/// per delivery is stored. Seats count for unlimited deliveries too, so managers see
/// how many installations there are.
/// </summary>
public class SeatService
{
    public const string HeaderName = "X-Install-Id";
    /// <summary>New seats per network address and hour; a rollout behind one NAT address stays well below (S1.4.3).</summary>
    public const int NewSeatsPerHour = 200;
    public const int ReleasesPerHour = 300;
    /// <summary>Freed seats are deleted after this many days (S1.4.3).</summary>
    public const int KeepReleasedDays = 180;
    // one claim at a time: count and insert must not interleave (two downloads with one seat left)
    private static readonly SemaphoreSlim s_claim = new(1, 1);
    private readonly AppDbContext _db;
    private readonly AuditService _audit;
    private readonly IMemoryCache _cache;

    public SeatService(AppDbContext db, AuditService audit, IMemoryCache cache) { _db = db; _audit = audit; _cache = cache; }

    private bool Allow(HttpContext ctx, string kind, int max)
    {
        var ip = GeoService.ClientIp(ctx)?.ToString() ?? "?";
        var key = $"seats:{kind}:{ip}:{DateTime.UtcNow:yyyyMMddHH}";
        var n = _cache.GetOrCreate(key, e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(61); return new int[1]; })!;
        lock (n) return ++n[0] <= max;
    }

    /// <summary>The client's installation id (a GUID), or null.</summary>
    public static string? InstallIdOf(HttpContext ctx)
    {
        var v = ctx.Request.Headers[HeaderName].ToString().Trim();
        return Guid.TryParse(v, out var g) ? g.ToString("D") : null;
    }

    public static string HashOf(int deliveryId, string installId) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"seat|{deliveryId}|{installId.ToLowerInvariant()}"))).ToLowerInvariant();

    public record Usage(int Used, int? Max);

    /// <summary>Active seats per delivery.</summary>
    public async Task<Dictionary<int, int>> UsedAsync(IEnumerable<int> deliveryIds)
    {
        var ids = deliveryIds.Distinct().ToList();
        return await _db.DeliverySeats.AsNoTracking().Where(s => ids.Contains(s.DeliveryId) && s.ReleasedAt == null)
            .GroupBy(s => s.DeliveryId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
    }

    public record Claim(bool Ok, string Code, string Message, int Used = 0, int? Max = null);

    /// <summary>
    /// Download gate for a delivered private add-on: the deliveries that hand out this
    /// version to the request. An installation that holds a seat keeps it (updates);
    /// a new one takes a free seat, or is refused when every delivery is full.
    /// </summary>
    public async Task<Claim> ClaimAsync(HttpContext ctx, IReadOnlyList<Delivery> deliveries, string version)
    {
        if (deliveries.Count == 0) return new(true, "", "");
        var installId = InstallIdOf(ctx);
        if (installId is null)
            return deliveries.Any(d => d.MaxInstalls is null)
                ? new(true, "", "")
                : new(false, "INSTALL_ID_MISSING", "This delivery allows a limited number of installations; update the Add-on Store client (1.4.1 or later) to install it.");
        var hashes = deliveries.ToDictionary(d => d.Id, d => HashOf(d.Id, installId));
        var ids = hashes.Keys.ToList();
        var hashList = hashes.Values.ToList();
        await s_claim.WaitAsync();
        try
        {
            var now = DateTime.UtcNow;
            // this installation's seat (an update keeps it): looked up by its hashes, not by loading every seat
            var held = (await _db.DeliverySeats.Where(s => ids.Contains(s.DeliveryId) && hashList.Contains(s.InstallHash) && s.ReleasedAt == null).ToListAsync())
                .FirstOrDefault(s => hashes.TryGetValue(s.DeliveryId, out var h) && h == s.InstallHash);
            if (held is not null)
            {
                held.LastSeenAt = now; held.Version = version;
                await _db.SaveChangesAsync();
                return new(true, "", "");
            }
            if (!Allow(ctx, "claim", NewSeatsPerHour))
                return new(false, "SEATS_RATE_LIMITED", "Too many new installations from this network in the last hour; try again later.");
            var used = await _db.DeliverySeats.AsNoTracking().Where(s => ids.Contains(s.DeliveryId) && s.ReleasedAt == null)
                .GroupBy(s => s.DeliveryId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
            // unlimited first, then the delivery with the most free seats
            var pick = deliveries.OrderBy(d => d.MaxInstalls is null ? 0 : 1)
                                 .ThenByDescending(d => (d.MaxInstalls ?? int.MaxValue) - used.GetValueOrDefault(d.Id))
                                 .First();
            var u = used.GetValueOrDefault(pick.Id);
            if (pick.MaxInstalls is int max && u >= max)
                return new(false, "SEATS_EXHAUSTED", $"All {max} installations of this delivery are in use. Remove the add-on on a computer that no longer needs it, or ask the provider for more installations.", u, max);
            var seat = new DeliverySeat { DeliveryId = pick.Id, InstallHash = hashes[pick.Id], Version = version, FirstAt = now, LastSeenAt = now };
            _db.DeliverySeats.Add(seat);
            try
            {
                // a database briefly locked by a parallel write: wait and try again (SQLite)
                for (var attempt = 1; ; attempt++)
                {
                    try { await _db.SaveChangesAsync(); break; }
                    catch (DbUpdateException ex) when (attempt < 3 && ex.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 5 or 6 })
                    { await Task.Delay(100 * attempt); }
                }
            }
            catch (DbUpdateException)
            {
                // the same installation in a parallel request (unique index) holds its seat now; any other
                // failure (e.g. a busy database) must not hand out the add-on without a seat
                _db.Entry(seat).State = EntityState.Detached;
                var heldNow = await _db.DeliverySeats.AsNoTracking()
                    .AnyAsync(s => s.DeliveryId == pick.Id && s.InstallHash == seat.InstallHash && s.ReleasedAt == null);
                if (!heldNow) return new(false, "SEATS_BUSY", "The store is busy right now; try again in a moment.");
            }
            return new(true, "", "", u + 1, pick.MaxInstalls);
        }
        finally { s_claim.Release(); }
    }

    /// <summary>The client removed the add-on: free its seats in these deliveries.</summary>
    public async Task<int> ReleaseAsync(HttpContext ctx, IReadOnlyList<Delivery> deliveries)
    {
        var installId = InstallIdOf(ctx);
        if (installId is null || deliveries.Count == 0) return 0;
        if (!Allow(ctx, "release", ReleasesPerHour)) return 0;
        var now = DateTime.UtcNow;
        var hashes = deliveries.Select(d => HashOf(d.Id, installId)).ToList();
        var ids = deliveries.Select(d => d.Id).ToList();
        return await _db.DeliverySeats.Where(s => ids.Contains(s.DeliveryId) && hashes.Contains(s.InstallHash) && s.ReleasedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ReleasedAt, now).SetProperty(x => x.ReleasedBy, "client"));
    }

    /// <summary>A manager frees a seat (a computer that was reset without removing the add-on).</summary>
    public async Task<bool> ReleaseSeatAsync(int deliveryId, int seatId, AppUser actor)
    {
        var s = await _db.DeliverySeats.FirstOrDefaultAsync(x => x.Id == seatId && x.DeliveryId == deliveryId && x.ReleasedAt == null);
        if (s is null) return false;
        s.ReleasedAt = DateTime.UtcNow; s.ReleasedBy = actor.DisplayName;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(actor.DisplayName, "delivery.seat-released", $"delivery {deliveryId}", $"seat {seatId}");
        return true;
    }

    /// <summary>A manager frees every installation of a delivery not seen for the given days (S1.4.3).</summary>
    public async Task<int> ReleaseStaleAsync(int deliveryId, int days, AppUser actor)
    {
        var before = DateTime.UtcNow.AddDays(-days);
        var now = DateTime.UtcNow;
        var n = await _db.DeliverySeats.Where(s => s.DeliveryId == deliveryId && s.ReleasedAt == null && s.LastSeenAt < before)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ReleasedAt, now).SetProperty(x => x.ReleasedBy, actor.DisplayName));
        if (n > 0) await _audit.LogAsync(actor.DisplayName, "delivery.seats-released", $"delivery {deliveryId}", $"{n} installation(s) not seen for {days} days");
        return n;
    }

    /// <summary>Freed seats older than KeepReleasedDays are deleted (maintenance, S1.4.3).</summary>
    public async Task<int> PurgeAsync()
    {
        var before = DateTime.UtcNow.AddDays(-KeepReleasedDays);
        return await _db.DeliverySeats.Where(s => s.ReleasedAt != null && s.ReleasedAt < before).ExecuteDeleteAsync();
    }

    /// <summary>A delivered add-on still in use: refresh "last seen" of this installation (at most hourly).</summary>
    public async Task TouchAsync(HttpContext ctx, IEnumerable<Delivery> deliveries)
    {
        var installId = InstallIdOf(ctx);
        if (installId is null) return;
        var list = deliveries.ToList();
        if (list.Count == 0) return;
        var now = DateTime.UtcNow; var before = now.AddHours(-1);
        var hashes = list.Select(d => HashOf(d.Id, installId)).ToList();
        var ids = list.Select(d => d.Id).ToList();
        await _db.DeliverySeats.Where(s => ids.Contains(s.DeliveryId) && hashes.Contains(s.InstallHash) && s.ReleasedAt == null && s.LastSeenAt < before)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSeenAt, now));
    }
}
