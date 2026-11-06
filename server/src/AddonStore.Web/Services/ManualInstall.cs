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

    /// <summary>Public, not blocked add-ons (never the store client) and their newest live version.</summary>
    public static async Task<Dictionary<string, PackageVersion>> AvailableAsync(AppDbContext db, string? onlyId = null)
    {
        var pkgs = await db.Packages.AsNoTracking()
            .Where(p => p.Visibility != "private" && p.BlockedAt == null && p.Id != SubmissionService.ClientPackageId && (onlyId == null || p.Id == onlyId))
            .Select(p => p.Id).ToListAsync();
        var lives = await db.PackageVersions.AsNoTracking()
            .Where(v => v.Status == VersionStatus.Live && v.BlockedAt == null && pkgs.Contains(v.PackageId))
            .Select(PackageVersion.Light).ToListAsync();
        var cmp = new SemVerComparer();
        return lives.GroupBy(v => v.PackageId).ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.Version, cmp).First());
    }

    /// <summary>File name of the zip, e.g. SmartBookmarks-1.5.2-manual-install.zip.</summary>
    public static string FileName(PackageVersion v) => $"{Zxt(v)}-{v.Version}-manual-install.zip";

    public static string Zxt(PackageVersion v)
    {
        using var doc = JsonDocument.Parse(v.ManifestJson);
        return AddonStore.Web.Api.ApiEndpoints.ZxtNameOf(doc.RootElement);
    }

    /// <summary>Writes the zip into <paramref name="target"/>; false when the package file is missing or has no x64 binary.</summary>
    public static bool Build(Stream target, PackageVersion v, string packageFile, string name, string author, string signature, string page)
    {
        var zxt = Zxt(v);
        if (zxt.Length == 0 || !File.Exists(packageFile)) return false;
        var root = $"{zxt}-{v.Version}/";
        var json = JsonSerializer.Serialize(new
        {
            format = Format,
            id = v.PackageId, version = v.Version, name, author, zxtName = zxt,
            sha256 = v.Sha256.ToLowerInvariant(), signature, page,
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
        // the licence texts of the package, readable before installing
        using (var ppak = ZipFile.OpenRead(packageFile))
        {
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
