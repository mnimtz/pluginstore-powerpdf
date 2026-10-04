using System.IO.Compression;
using System.Net;
using MaxMind.Db;

namespace AddonStore.Web.Services;

/// <summary>
/// IP address to location and network operator for the usage reports.
/// Data: "IP to City Lite" and "IP to ASN Lite" by DB-IP (https://db-ip.com),
/// CC BY 4.0, attribution on the reports page. Refreshed monthly into
/// data/geo (not part of backups, downloaded again); opened memory-mapped.
/// Without the files every lookup returns empty values.
/// </summary>
public sealed class GeoService : BackgroundService
{
    public record GeoInfo(string Country, string Region, string City, double? Latitude, double? Longitude,
                          long? Asn, string Org)
    {
        public static readonly GeoInfo Empty = new("", "", "", null, null, null, "");
    }

    private static readonly (string Key, string File)[] Databases =
    {
        ("city", "dbip-city-lite.mmdb"),
        ("asn", "dbip-asn-lite.mmdb"),
    };

    private readonly string _dir;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<GeoService> _log;
    private readonly Dictionary<string, Reader?> _readers = new() { ["city"] = null, ["asn"] = null };
    private readonly object _lock = new();

    public GeoService(IConfiguration config, IWebHostEnvironment env, IHttpClientFactory http, ILogger<GeoService> log)
    {
        var data = config["Storage:Data"];
        _dir = Path.Combine(string.IsNullOrWhiteSpace(data) ? Path.Combine(env.ContentRootPath, "data") : data, "geo");
        _http = http;
        _log = log;
    }

    private string PathOf(string file) => Path.Combine(_dir, file);

    /// <summary>Date of the older of both databases, null when one is missing.</summary>
    public DateTime? DatabaseDate
    {
        get
        {
            DateTime? oldest = null;
            foreach (var (_, file) in Databases)
            {
                if (!File.Exists(PathOf(file))) return null;
                var d = File.GetLastWriteTimeUtc(PathOf(file));
                if (oldest is null || d < oldest) oldest = d;
            }
            return oldest;
        }
    }

    /// <summary>The caller's IP: X-Client-IP / last X-Forwarded-For entry set by the App Service front end, else the socket.</summary>
    public static IPAddress? ClientIp(HttpContext ctx)
    {
        static IPAddress? Parse(string? s)
        {
            s = s?.Trim();
            if (string.IsNullOrEmpty(s)) return null;
            if (IPAddress.TryParse(s, out var ip)) return ip;
            // "1.2.3.4:5678" or "[::1]:5678"
            if (IPEndPoint.TryParse(s, out var ep)) return ep.Address;
            return null;
        }
        // X-Client-IP is not used: a client can send it itself. The App Service
        // front end APPENDS the address it saw to X-Forwarded-For, so only the
        // rightmost entry is trustworthy (values a client sent stand before it).
        IPAddress? ip = null;
        // Setting Network:TrustForwardedFor (default true for App Service); set it to false when
        // the container is reachable directly, because then a client can send the header itself.
        var trust = ctx.RequestServices.GetService<IConfiguration>()?["Network:TrustForwardedFor"] is not ("false" or "False" or "0");
        var xff = trust ? ctx.Request.Headers["X-Forwarded-For"].ToString() : "";
        if (!string.IsNullOrEmpty(xff)) ip = Parse(xff.Split(',').Last());
        ip ??= ctx.Connection.RemoteIpAddress;
        return ip is { IsIPv4MappedToIPv6: true } ? ip.MapToIPv4() : ip;
    }

    public GeoInfo Lookup(HttpContext ctx) => Lookup(ClientIp(ctx));

    public GeoInfo Lookup(IPAddress? ip)
    {
        if (ip is null || IPAddress.IsLoopback(ip)) return GeoInfo.Empty;
        Reader? city, asn;
        lock (_lock) { city = _readers["city"]; asn = _readers["asn"]; }
        string country = "", region = "", town = "", org = "";
        double? lat = null, lon = null;
        long? asNumber = null;
        try
        {
            if (city?.Find<Dictionary<string, object>>(ip) is { } c)
            {
                country = Str(Get(c, "country", "iso_code")).ToUpperInvariant();
                town = Str(Get(c, "city", "names", "en"));
                if (c.TryGetValue("subdivisions", out var subs) && subs is System.Collections.IList { Count: > 0 } list &&
                    list[0] is Dictionary<string, object> first)
                    region = Str(Get(first, "names", "en"));
                lat = Num(Get(c, "location", "latitude"));
                lon = Num(Get(c, "location", "longitude"));
            }
            if (asn?.Find<Dictionary<string, object>>(ip) is { } a)
            {
                org = Str(Get(a, "autonomous_system_organization"));
                asNumber = Num(Get(a, "autonomous_system_number")) is { } n ? (long)n : null;
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "geo lookup failed");
        }
        return new GeoInfo(country.Length == 2 ? country : "", Trim(region), Trim(town), lat, lon, asNumber, Trim(org));
    }

    private static object? Get(Dictionary<string, object> d, params string[] path)
    {
        object? cur = d;
        foreach (var key in path)
        {
            if (cur is not Dictionary<string, object> dict || !dict.TryGetValue(key, out cur)) return null;
        }
        return cur;
    }
    private static string Str(object? o) => o as string ?? "";
    private static double? Num(object? o) => o switch
    {
        double d => d, float f => f, int i => i, long l => l, uint u => u, ulong ul => ul,
        System.Numerics.BigInteger b => (double)b, _ => null
    };
    private static string Trim(string s) => s.Length > 120 ? s[..120] : s;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        Directory.CreateDirectory(_dir);
        foreach (var (key, file) in Databases) Load(key, file);
        while (!stop.IsCancellationRequested)
        {
            foreach (var (key, file) in Databases)
            {
                var path = PathOf(file);
                if (!File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(32))
                    await RefreshAsync(key, file, stop);
            }
            try { await Task.Delay(TimeSpan.FromHours(12), stop); }
            catch (TaskCanceledException) { break; }
        }
    }

    private void Load(string key, string file)
    {
        var path = PathOf(file);
        if (!File.Exists(path)) return;
        try
        {
            var reader = new Reader(path, FileAccessMode.MemoryMapped);
            Reader? old;
            lock (_lock) { old = _readers[key]; _readers[key] = reader; }
            old?.Dispose();
        }
        catch (Exception ex) { _log.LogWarning(ex, "geo database {File} could not be loaded", file); }
    }

    private async Task RefreshAsync(string key, string file, CancellationToken stop)
    {
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(10);
        // The current month's file appears at the start of the month; fall back to the previous one.
        foreach (var month in new[] { DateTime.UtcNow, DateTime.UtcNow.AddMonths(-1) })
        {
            var url = $"https://download.db-ip.com/free/dbip-{key}-lite-{month:yyyy-MM}.mmdb.gz";
            var tmp = PathOf(file) + ".download";
            try
            {
                using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stop);
                if (!resp.IsSuccessStatusCode) continue;
                if (resp.Content.Headers.ContentLength is > 256L * 1024 * 1024) continue;
                await using (var gz = new GZipStream(await resp.Content.ReadAsStreamAsync(stop), CompressionMode.Decompress))
                await using (var fs = File.Create(tmp))
                    await CopyLimitedAsync(gz, fs, 1024L * 1024 * 1024, stop);
                using (new Reader(tmp)) { }   // validate before replacing

                // Release the mapping before replacing the file (required on Windows).
                Reader? old;
                lock (_lock) { old = _readers[key]; _readers[key] = null; }
                old?.Dispose();
                File.Move(tmp, PathOf(file), overwrite: true);
                Load(key, file);
                _log.LogInformation("geo database updated from {Url}", url);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested)
            {
                _log.LogWarning(ex, "geo database download failed ({Url})", url);
                try { File.Delete(tmp); } catch { }
                Load(key, file);
            }
        }
    }

    private static async Task CopyLimitedAsync(Stream from, Stream to, long max, CancellationToken stop)
    {
        var buf = new byte[81920];
        long total = 0;
        int n;
        while ((n = await from.ReadAsync(buf, stop)) > 0)
        {
            total += n;
            if (total > max) throw new InvalidDataException("geo database larger than expected");
            await to.WriteAsync(buf.AsMemory(0, n), stop);
        }
    }
}
