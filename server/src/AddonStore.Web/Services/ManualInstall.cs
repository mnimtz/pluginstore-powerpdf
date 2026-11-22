using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AddonStore.Web.Data;
using AddonStore.Web.Validation;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Services;

/// <summary>
/// Manual installation without the store client (S1.30.0): a zip with install.bat,
/// uninstall.bat, the generic AddonSetup.ps1, the unchanged signed package and its
/// package.json. Built on request from the newest live version, so every public
/// add-on has it without any step of its developer.
/// </summary>
public static class ManualInstall
{
    public const string Format = "addonstore-manual-v1";

    /// <summary>Public, not blocked add-ons (never the store client) and their newest live version (row id, version).
    /// Only these columns: the catalog page asks on every view, and the database lies on a network share.</summary>
    public static async Task<Dictionary<string, (int Id, string Version)>> AvailableAsync(AppDbContext db, string? onlyId = null)
    {
        // S2.0.3: kept with the catalog generation (a write to packages or versions, at most a minute)
        var key = "manual|" + (onlyId ?? "*");
        if (Services.CatalogUi.CachedObject(key) is Dictionary<string, (int Id, string Version)> hit) return new(hit);
        var gen = Services.CatalogUi.Generation;
        var result = await LoadAvailableAsync(db, onlyId);
        Services.CatalogUi.CacheObject(key, gen, result);
        return new(result);
    }

    private static async Task<Dictionary<string, (int Id, string Version)>> LoadAvailableAsync(AppDbContext db, string? onlyId)
    {
        var lives = await db.PackageVersions.AsNoTracking()
            .Where(v => v.Status == VersionStatus.Live && v.BlockedAt == null && v.PackageId != SubmissionService.ClientPackageId &&
                        (onlyId == null || v.PackageId == onlyId) &&
                        db.Packages.Any(p => p.Id == v.PackageId && p.Visibility != "private" && p.BlockedAt == null))
            .Select(v => new { v.Id, v.PackageId, v.Version }).ToListAsync();
        var cmp = new SemVerComparer();
        return lives.GroupBy(v => v.PackageId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.Version, cmp).Select(v => (v.Id, v.Version)).First());
    }

    /// <summary>File name of the zip, e.g. SmartBookmarks-1.5.2-manual-install.zip.</summary>
    public static string FileName(PackageVersion v) => $"{Zxt(v)}-{v.Version}-manual-install.zip";

    public static string Zxt(PackageVersion v)
    {
        using var doc = JsonDocument.Parse(v.ManifestJson);
        return AddonStore.Web.Api.ApiEndpoints.ZxtNameOf(doc.RootElement);
    }

    /// <summary>
    /// S2.0.3: the finished zip from the container's local disk (built at the first request for this version, name,
    /// author, signature and address), opened for reading; null when the package file is missing or has no x64 binary.
    /// </summary>
    public static FileStream? CachedZip(PackageVersion v, string packageFile, string name, string author, string signature, string page,
                                        string minHost = "")
    {
        var dir = Path.Combine(Path.GetTempPath(), "addonstore-manual");
        Directory.CreateDirectory(dir);
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join("\n", Format, v.PackageId, v.Version, v.Sha256, name, author, signature, page, minHost))))[..32].ToLowerInvariant();
        var file = Path.Combine(dir, key + ".zip");
        if (!File.Exists(file))
        {
            if (!File.Exists(packageFile)) return null;
            Prune(dir);
            var tmp = Path.Combine(dir, key + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                    if (!Build(fs, v, packageFile, name, author, signature, page, minHost)) return null;
                File.Move(tmp, file, overwrite: true);
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } }
        }
        return new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, useAsync: true);
    }

    /// <summary>At most 300 cached zips: the oldest go first.</summary>
    private static void Prune(string dir)
    {
        try
        {
            var files = new DirectoryInfo(dir).GetFiles("*.zip");
            if (files.Length < 300) return;
            foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc).Take(files.Length - 250))
                try { f.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (IOException) { }
    }

    /// <summary>Writes the zip into <paramref name="target"/>; false when the package file is missing or has no x64 binary.</summary>
    public static bool Build(Stream target, PackageVersion v, string packageFile, string name, string author, string signature, string page,
                             string minHost = "")
    {
        var zxt = Zxt(v);
        if (zxt.Length == 0 || !File.Exists(packageFile)) return false;
        var root = $"{zxt}-{v.Version}/";
        var json = JsonSerializer.Serialize(new
        {
            format = Format,
            id = v.PackageId, version = v.Version, name, author, zxtName = zxt,
            sha256 = v.Sha256.ToLowerInvariant(), signature, page,
            // S2.5.0: the store's minimum Power PDF version (MinHost.Effective: an admin's value or the package's own);
            // S2.7.2: setup uses it instead of the manifest's, like the store window
            minPowerPdfVersion = MinHost.IsValid(minHost) ? minHost : "",
        }, new JsonSerializerOptions { WriteIndented = true });
        using var zip = new ZipArchive(target, ZipArchiveMode.Create, leaveOpen: true);
        void Text(string entry, string text, bool bom)
        {
            var e = zip.CreateEntry(root + entry, CompressionLevel.Optimal);
            using var s = e.Open();
            var crlf = text.Replace("\r\n", "\n").Replace("\n", "\r\n");   // Windows files, whatever the checkout did
            var bytes = new UTF8Encoding(bom).GetBytes(crlf);
            if (bom) s.Write(Encoding.UTF8.GetPreamble());
            s.Write(bytes);
        }
        // the .ps1 with a BOM: Windows PowerShell 5.1 reads a file without one in the ANSI code page
        Text("AddonSetup.ps1", Resource("AddonSetup.ps1"), true);
        Text("install.bat", Resource("install.bat"), false);
        Text("uninstall.bat", Resource("uninstall.bat"), false);
        Text("README.txt", Resource("README.txt").Replace("{name}", name).Replace("{version}", v.Version)
                                                 .Replace("{id}", v.PackageId).Replace("{page}", page), true);
        Text("package.json", json, false);
        zip.CreateEntryFromFile(packageFile, root + "package.ppak", CompressionLevel.NoCompression);   // unchanged: its hash is signed
        // the licence texts of the package, readable before installing; a package that cannot be read is not offered
        try
        {
            using var ppak = ZipFile.OpenRead(packageFile);
            if (ppak.GetEntry($"x64/{zxt}.zxt") is null) return false;
            foreach (var n in new[] { "LICENSES.md", "THIRD-PARTY-NOTICES.txt" })
            {
                var e = ppak.GetEntry(n);
                if (e is null || e.Length > 4 * 1024 * 1024) continue;
                var copy = zip.CreateEntry(root + n, CompressionLevel.Optimal);
                using var from = e.Open();
                using var to = copy.Open();
                from.CopyTo(to);
            }
        }
        catch (InvalidDataException) { return false; }
        return true;
    }

    private static string Resource(string file)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ManualInstall." + file)
                      ?? throw new InvalidOperationException("Embedded resource ManualInstall." + file + " is missing.");
        using var r = new StreamReader(s, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return r.ReadToEnd();
    }
}
