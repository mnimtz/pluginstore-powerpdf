using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;

namespace AddonStore.Web.Services;

/// <summary>
/// Limits shared by every upload path (audit S1.3.1): at most 60 package checks, submissions and
/// source uploads per account and hour (API, web upload, source upload), and the number of ZIP
/// entries read from the archive's end record before the entries are loaded into memory.
/// </summary>
public static class UploadLimits
{
    public const int PerHour = 60;

    public static bool TooMany(HttpContext ctx, IMemoryCache cache)
    {
        var who = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? GeoService.ClientIp(ctx)?.ToString() ?? "?";
        var counter = cache.GetOrCreate("uploads:" + who + ":" + DateTime.UtcNow.ToString("yyyyMMddHH", System.Globalization.CultureInfo.InvariantCulture),
            e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1); return new int[1]; })!;
        lock (counter) { return ++counter[0] > PerHour; }
    }

    public static bool TooMany(HttpContext ctx) => TooMany(ctx, ctx.RequestServices.GetRequiredService<IMemoryCache>());

    /// <summary>
    /// Entries a ZIP declares in its end-of-central-directory record, without reading the
    /// directory; int.MaxValue for ZIP64 archives (not needed by any package), -1 when no record
    /// is found (the ZIP reader then reports the broken file).
    /// </summary>
    public static int DeclaredEntries(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var len = (int)Math.Min(fs.Length, 65557);   // the record: 22 bytes + up to 65535 bytes comment
            if (len < 22) return -1;
            var buf = new byte[len];
            fs.Seek(-len, SeekOrigin.End);
            fs.ReadExactly(buf);
            for (var i = len - 22; i >= 0; i--)
                if (buf[i] == 0x50 && buf[i + 1] == 0x4B && buf[i + 2] == 0x05 && buf[i + 3] == 0x06)
                {
                    int total = BitConverter.ToUInt16(buf, i + 10);
                    if (total == 0xFFFF) return int.MaxValue;
                    // a directory far larger than the declared entries need hides more entries than
                    // it declares (the reader would load them all before it notices): never trust it
                    long dirSize = BitConverter.ToUInt32(buf, i + 12);
                    return dirSize > (long)total * 1100 + 65536 ? int.MaxValue : total;
                }
            return -1;
        }
        catch (IOException) { return -1; }
    }
}
