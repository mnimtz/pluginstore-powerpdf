using System.Text.RegularExpressions;
using AddonStore.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Services;

/// <summary>
/// Counts downloads and catalog fetches for the admin reports.
/// Always: daily counters (UsageStats) with the derived source ("client",
/// "web", "api"), client/Power PDF/Windows version, architecture, catalog
/// language and country, region, city and network operator looked up from the
/// IP (the IP itself is not stored there). Only while an admin switched on IP logging with the GDPR
/// confirmation: one UsageEvent per request including IP and user agent,
/// deleted after the retention period. Counting never breaks a download.
/// </summary>
public class UsageService
{
    public const string IpLoggingKey = "Usage.IpLogging";              // "on" / "off"
    public const string IpRetentionKey = "Usage.IpRetentionDays";      // 1..730, default 90; 0 = keep permanently
    public const string IpConfirmedByKey = "Usage.IpConfirmedBy";
    public const string IpConfirmedAtKey = "Usage.IpConfirmedAt";
    public const int DefaultRetentionDays = 90;

    // AddonStore-PowerPDF/0.4.2 (PowerPDF 15.1.0.1234; Windows 10.0.26200; arm64)
    private static readonly Regex ClientUa = new(
        @"AddonStore-PowerPDF/(\d+(?:\.\d+){1,3})(?:\s*\(PowerPDF ([\d.]+); Windows ([\d.]+); (x64|arm64|x86)\))?",
        RegexOptions.Compiled);
    private static readonly Regex Safe = new(@"^[A-Za-z0-9][A-Za-z0-9._\-]{0,63}$", RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly GeoService _geo;
    private readonly SettingsService _settings;
    private readonly ILogger<UsageService> _log;

    public UsageService(AppDbContext db, GeoService geo, SettingsService settings, ILogger<UsageService> log)
    {
        _db = db; _geo = geo; _settings = settings; _log = log;
    }

    public record ClientInfo(string Source, string ClientVersion, string HostVersion, string OsVersion, string Arch);

    /// <summary>Source and client details derived from the request's user agent.</summary>
    public static ClientInfo Classify(HttpContext ctx)
    {
        var ua = ctx.Request.Headers.UserAgent.ToString();
        var m = ClientUa.Match(ua);
        if (m.Success)
            return new("client", m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value);
        return new(ua.Contains("Mozilla/", StringComparison.Ordinal) ? "web" : "api", "", "", "", "");
    }

    private static string Clean(string? v) => v is not null && Safe.IsMatch(v) ? v : "";

    /// <summary>Free text from the geo database: no control characters, max 120 chars.</summary>
    private static string Text(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        var t = new string(v.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return t.Length > 120 ? t[..120] : t;
    }

    public async Task<bool> IpLoggingOnAsync() =>
        string.Equals(await _settings.GetAsync(IpLoggingKey), "on", StringComparison.OrdinalIgnoreCase);

    public async Task CountAsync(HttpContext ctx, string kind, string packageId = "", string version = "", string? lang = null)
    {
        try
        {
            var c = Classify(ctx);
            var geo = _geo.Lookup(ctx);
            var day = DateTime.UtcNow.ToString("yyyy-MM-dd");
            string k = Clean(kind), p = Clean(packageId), v = Clean(version), l = Clean(lang).ToUpperInvariant();
            await _db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO UsageStats (Day, Kind, PackageId, Version, Source, Lang, ClientVersion,
                                        Country, Region, City, Org, Arch, HostVersion, OsVersion, Count)
                VALUES ({day}, {k}, {p}, {v}, {c.Source}, {l}, {Clean(c.ClientVersion)},
                        {Clean(geo.Country)}, {Text(geo.Region)}, {Text(geo.City)}, {Text(geo.Org)},
                        {Clean(c.Arch)}, {Clean(c.HostVersion)}, {Clean(c.OsVersion)}, 1)
                ON CONFLICT (Day, Kind, PackageId, Version, Source, Lang, ClientVersion,
                             Country, Region, City, Org, Arch, HostVersion, OsVersion)
                DO UPDATE SET Count = Count + 1");

            if (await IpLoggingOnAsync())
            {
                var ua = ctx.Request.Headers.UserAgent.ToString();
                _db.UsageEvents.Add(new UsageEvent
                {
                    Kind = k, PackageId = p, Version = v, Source = c.Source, Lang = l,
                    ClientVersion = Clean(c.ClientVersion), HostVersion = Clean(c.HostVersion),
                    OsVersion = Clean(c.OsVersion), Arch = Clean(c.Arch), Country = Clean(geo.Country),
                    Region = Text(geo.Region), City = Text(geo.City), Latitude = geo.Latitude, Longitude = geo.Longitude,
                    Asn = geo.Asn, Org = Text(geo.Org),
                    Ip = GeoService.ClientIp(ctx)?.ToString() ?? "",
                    UserAgent = ua.Length > 300 ? ua[..300] : ua
                });
                await _db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "usage counter failed for {Kind} {Package}", kind, packageId);
        }
    }

    public async Task<int> RetentionDaysAsync()
    {
        var s = await _settings.GetAsync(IpRetentionKey);
        return int.TryParse(s, out var d) && d is >= 0 and <= 730 ? d : DefaultRetentionDays;
    }

    /// <summary>0 = permanent storage, 1..730 days; anything else falls back to the default.</summary>
    public static int NormalizeRetention(int days, bool permanent) =>
        permanent ? 0 : days is >= 1 and <= 730 ? days : DefaultRetentionDays;

    public Task<int> EventCountAsync() => _db.UsageEvents.CountAsync();
    public Task<int> DeleteAllEventsAsync() => _db.UsageEvents.ExecuteDeleteAsync();

    /// <summary>Deletes IP events older than the retention period; returns the number removed.</summary>
    public async Task<int> PurgeAsync()
    {
        var days = await RetentionDaysAsync();
        if (days == 0) return 0;   // permanent storage chosen by an admin
        var cutoff = DateTime.UtcNow.AddDays(-days);
        return await _db.UsageEvents.Where(e => e.At < cutoff).ExecuteDeleteAsync();
    }
}

/// <summary>
/// Background work for IP logging: resolves host names of new events (reverse
/// DNS, so downloads never wait for it) every minute and applies the retention
/// period twice a day.
/// </summary>
public sealed class UsageMaintenance : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<UsageMaintenance> _log;

    public UsageMaintenance(IServiceScopeFactory scopes, ILogger<UsageMaintenance> log) { _scopes = scopes; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        // Start after the web server: SQLite's async calls run synchronously, and
        // the first purge must not hold up the start (S0.17.1).
        try { await Task.Delay(TimeSpan.FromSeconds(30), stop); } catch (TaskCanceledException) { return; }
        var nextPurge = DateTime.MinValue;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                if (DateTime.UtcNow >= nextPurge)
                {
                    var n = await scope.ServiceProvider.GetRequiredService<UsageService>().PurgeAsync();
                    if (n > 0) _log.LogInformation("usage: {Count} IP events past the retention period deleted", n);
                    nextPurge = DateTime.UtcNow.AddHours(12);
                }
                await ResolveHostnamesAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), stop);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested) { _log.LogWarning(ex, "usage maintenance run failed"); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stop); }
            catch (TaskCanceledException) { break; }
        }
    }

    private static async Task ResolveHostnamesAsync(AppDbContext db, CancellationToken stop)
    {
        var pending = await db.UsageEvents.Where(e => e.Hostname == null).OrderBy(e => e.Id).Take(50).ToListAsync(stop);
        if (pending.Count == 0) return;
        var cache = new Dictionary<string, string>();
        foreach (var e in pending)
        {
            if (!cache.TryGetValue(e.Ip, out var name))
            {
                name = "";
                if (System.Net.IPAddress.TryParse(e.Ip, out var ip))
                {
                    try
                    {
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
                        cts.CancelAfter(TimeSpan.FromSeconds(3));
                        var entry = await System.Net.Dns.GetHostEntryAsync(ip.ToString(), cts.Token);
                        if (!string.Equals(entry.HostName, e.Ip, StringComparison.OrdinalIgnoreCase)) name = entry.HostName;
                    }
                    catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException or ArgumentException) { }
                }
                name = name.Length > 200 ? name[..200] : name;
                cache[e.Ip] = name;
            }
            e.Hostname = name;
        }
        await db.SaveChangesAsync(stop);
    }
}
