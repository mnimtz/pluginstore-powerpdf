using System.Collections.Concurrent;

namespace AddonStore.Web.Services;

/// <summary>
/// S2.0.3: the catalog icon (assets/icon.png) of a package version, read once from the package on the data share and
/// then kept in memory. A version never changes, so its SHA-256 is the key; "no icon" is remembered as well.
/// </summary>
public static class IconCache
{
    private const int MaxEntries = 1000;
    private const long MaxIconBytes = 4 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, byte[]> Icons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The PNG bytes, or null when the package has no valid icon or cannot be read.</summary>
    public static async Task<byte[]?> GetAsync(string sha256, string packagePath)
    {
        if (Icons.TryGetValue(sha256, out var hit)) return hit.Length == 0 ? null : hit;
        var (bytes, final) = await ReadAsync(packagePath);
        if (!final) return bytes;   // a missing file or a read error is not remembered (restore, network share hiccup)
        if (Icons.Count >= MaxEntries) Icons.Clear();
        Icons[sha256] = bytes ?? Array.Empty<byte>();
        return bytes;
    }

    private static async Task<(byte[]? Bytes, bool Final)> ReadAsync(string path)
    {
        if (!File.Exists(path)) return (null, false);
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            var entry = zip.GetEntry("assets/icon.png");
            if (entry is null || entry.Length > MaxIconBytes) return (null, true);
            await using var s = entry.Open();
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms);
            var bytes = ms.ToArray();
            return (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == (byte)'P' ? bytes : null, true);
        }
        catch (InvalidDataException) { return (null, true); }   // not a zip: stays so
        catch (IOException) { return (null, false); }
    }
}
