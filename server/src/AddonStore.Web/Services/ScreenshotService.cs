using System.IO.Compression;
using System.Text.Json;
using AddonStore.Web.Data;
using AddonStore.Web.Validation;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Services;

/// <summary>
/// Screenshots declared in the manifest ("screenshots": [{"file": "assets/...png",
/// "caption": {"en": ..}}], S0.11.0). Served from the newest live (else beta)
/// version, like the icon; the files stay inside the package.
/// </summary>
public static class ScreenshotService
{
    public const int MaxCount = 6;
    public const long MaxBytes = 3 * 1024 * 1024;

    public record Shot(int Index, string File, string Caption);

    /// <summary>Newest live version, else newest beta: the one the catalog shows.</summary>
    public static async Task<PackageVersion?> DisplayVersionAsync(AppDbContext db, string packageId)
    {
        var cmp = new SemVerComparer();
        var versions = await db.PackageVersions.AsNoTracking()
            .Where(v => v.PackageId == packageId && (v.Status == VersionStatus.Live || v.Status == VersionStatus.Beta)).ToListAsync();
        return versions.Where(v => v.Status == VersionStatus.Live).OrderByDescending(v => v.Version, cmp).FirstOrDefault()
               ?? versions.OrderByDescending(v => v.Version, cmp).FirstOrDefault();
    }

    public static List<Shot> FromManifest(string manifestJson, string culture)
    {
        var list = new List<Shot>();
        try
        {
            using var doc = JsonDocument.Parse(manifestJson);
            if (!doc.RootElement.TryGetProperty("screenshots", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
            int i = 0;
            foreach (var s in arr.EnumerateArray())
            {
                if (i >= MaxCount) break;
                if (s.ValueKind != JsonValueKind.Object || !s.TryGetProperty("file", out var f) || f.ValueKind != JsonValueKind.String) continue;
                list.Add(new Shot(i, f.GetString() ?? "", CatalogUi.LangText(s, "caption", culture) ?? ""));
                i++;
            }
        }
        catch (JsonException) { }
        return list;
    }

    public static int Count(string manifestJson) => FromManifest(manifestJson, "en").Count;

    /// <summary>Image bytes and content type, or null.</summary>
    public static async Task<(byte[] Bytes, string Type)?> ReadAsync(string packagePath, string file)
    {
        if (!File.Exists(packagePath)) return null;
        try
        {
            using var zip = ZipFile.OpenRead(packagePath);
            var entry = zip.GetEntry(file);
            if (entry is null || entry.Length > MaxBytes) return null;
            using var s = entry.Open();
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms);
            var b = ms.ToArray();
            var type = ImageType(b);
            return type is null ? null : (b, type);
        }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>"image/png" or "image/jpeg" by magic bytes, else null.</summary>
    public static string? ImageType(byte[] b)
    {
        if (b.Length > 24 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G') return "image/png";
        if (b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        return null;
    }
}
