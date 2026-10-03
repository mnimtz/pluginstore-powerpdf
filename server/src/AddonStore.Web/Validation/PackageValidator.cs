using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AddonStore.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Validation;

/// <summary>
/// The automatic validation pipeline. The same code runs for the dry-run
/// endpoint, the real submission, the web upload and (later) the ppak CLI.
/// Findings carry stable codes and a concrete fix hint so that an AI agent
/// can iterate until the report passes.
/// </summary>
public class PackageValidator
{
    public const long MaxPackageBytes = 200 * 1024L * 1024L;

    private static readonly Regex IdPattern = new(@"^[a-z0-9][a-z0-9-]*(\.[a-z0-9][a-z0-9-]*)+$", RegexOptions.Compiled);
    private static readonly Regex SemVerPattern = new(@"^\d+\.\d+\.\d+$", RegexOptions.Compiled);

    /// <summary>Plugin base names Power PDF ships itself; a store plugin must not shadow them.</summary>
    private static readonly HashSet<string> ReservedZxtNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "AIAssistant","Annot","AzureOpenAI","AzureRMS","BatesStamp","BuildContents","CaseMap","Catalog",
        "ClipArt","CompareDoc","Compliance","ConvertFile","DMSConnector","EditText","email","ExamineDoc",
        "Fax","FeaturePack","FileOpen","FileSplit","FileStorage","FormTyper","Help","Inbox","jump","Layer",
        "Link","Movie","OpenAI","Optimize","PDF2Image","PDF2PS","PPDesktop","PPKLite","ReduceFile","Retag",
        "RMSDRM","SaveAs","SaveAsText","ScanToPDF","Search","SignDoc","TTS","TypeWriter","ViewFolder",
        "Watermark","Z3D2","ZAccessibility","ZAutoSave","ZDigSig","ZeonForm","ZFavorite","ZFillSign",
        "ZGraphic","ZImposition","ZJavaScript","ZSaveRevision","ZSpellCheck","ZTouchup","ZWebPDF","PluginStore"
    };

    /// <summary>All 16 European Power PDF UI language folder codes.</summary>
    private static readonly string[] EuroLangFolders =
        { "ENU","DEU","FRA","ITA","ESP","NLD","PTB","DAN","FIN","NOR","SVE","PLK","CSY","HUN","RUS","TRK" };

    private readonly AppDbContext _db;

    public PackageValidator(AppDbContext db) => _db = db;

    public async Task<(ValidationReport Report, ParsedManifest? Manifest)> ValidateAsync(
        string zipPath, string callerUserId)
    {
        var report = new ValidationReport();
        ParsedManifest? manifest = null;

        var size = new FileInfo(zipPath).Length;
        if (size > MaxPackageBytes)
        {
            report.Error("SIZE_LIMIT", $"Package is {size / 1048576} MB, the limit is {MaxPackageBytes / 1048576} MB.",
                "Reduce the package size; large third-party payloads should be downloaded at install time instead of being bundled.");
            return (report, null);
        }

        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(zipPath);
        }
        catch (Exception ex)
        {
            report.Error("ZIP_UNREADABLE", $"The file is not a readable ZIP archive ({ex.Message}).",
                "Create the package as a standard ZIP container with manifest.json at the root.");
            return (report, null);
        }

        using (zip)
        {
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (name.Contains("..") || name.StartsWith('/') || name.Contains(':'))
                {
                    report.Error("ZIP_SLIP", $"Entry '{entry.FullName}' uses an unsafe path.",
                        "All ZIP entries must use relative paths without '..', drive letters or leading slashes.");
                    return (report, null);
                }
            }

            var manifestEntry = zip.GetEntry("manifest.json");
            if (manifestEntry is null)
            {
                report.Error("MANIFEST_MISSING", "manifest.json was not found at the package root.",
                    "Add manifest.json at the ZIP root. Fetch /api/schema/manifest for the expected structure.");
                return (report, null);
            }

            JsonDocument doc;
            try
            {
                using var ms = new MemoryStream();
                await using (var es = manifestEntry.Open()) await es.CopyToAsync(ms);
                doc = JsonDocument.Parse(ms.ToArray());
            }
            catch (JsonException ex)
            {
                report.Error("MANIFEST_INVALID_JSON", $"manifest.json is not valid JSON ({ex.Message}).",
                    "Fix the JSON syntax; validate it locally before uploading.");
                return (report, null);
            }

            using (doc)
            {
                var root = doc.RootElement;
                manifest = new ParsedManifest { RawJson = root.GetRawText() };

                // id
                var id = GetString(root, "id");
                if (id is null || !IdPattern.IsMatch(id))
                    report.Error("ID_INVALID", $"Manifest field 'id' is missing or invalid ('{id}').",
                        "Use a lowercase reverse-DNS id such as 'com.tungsten.myplugin' (letters, digits, '-', at least one dot).");
                else manifest.Id = id;

                // version
                var version = GetString(root, "version");
                if (version is null || !SemVerPattern.IsMatch(version))
                    report.Error("VERSION_INVALID", $"Manifest field 'version' is missing or not SemVer ('{version}').",
                        "Set 'version' to MAJOR.MINOR.PATCH, e.g. '0.1.0'. Every upload must carry a new, higher version.");
                else manifest.Version = version;

                // name
                if (!root.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.Object ||
                    !nameEl.EnumerateObject().Any())
                    report.Error("NAME_MISSING", "Manifest field 'name' must be an object with at least one language.",
                        "Provide 'name' as an object keyed by language code, e.g. {\"en\": \"My Plugin\", \"de\": \"Mein Plug-in\"}.");

                // changelog (mandatory, per team rule: every version documents its changes)
                var changelog = ReadTextOrFirstLanguage(root, "changelog");
                if (string.IsNullOrWhiteSpace(changelog))
                    report.Error("CHANGELOG_EMPTY", "Manifest field 'changelog' is missing or empty.",
                        "Describe briefly what changed in this version, e.g. {\"en\": \"Fixes crash when ...\", \"de\": \"...\"}. Admins review this text.");
                else manifest.Changelog = changelog.Trim();

                manifest.AtomNamespace = GetString(root, "ribbonAtomNamespace") ?? "";
                if (manifest.AtomNamespace.Length == 0)
                    report.Warn("ATOM_NAMESPACE_MISSING", "Manifest field 'ribbonAtomNamespace' is not set.",
                        "Declare the ribbon atom namespace your plugin uses; the host caches ribbons by atom name and the store checks for collisions.");

                manifest.MinPowerPdfVersion = GetString(root, "minPowerPdfVersion") ?? "";
                if (manifest.MinPowerPdfVersion.Length == 0)
                    report.Warn("MIN_HOST_VERSION_MISSING", "Manifest field 'minPowerPdfVersion' is not set.",
                        "State the lowest Power PDF version the plugin was tested with, e.g. \"5.0\".");

                // architectures + files + hashes + PE checks
                var architectures = new[] { "x64", "arm64" };
                var declared = new HashSet<string>();
                if (root.TryGetProperty("architectures", out var archEl) && archEl.ValueKind == JsonValueKind.Array)
                    foreach (var a in archEl.EnumerateArray())
                        if (a.ValueKind == JsonValueKind.String) declared.Add(a.GetString()!);

                // Both architectures must carry the SAME base name: the host loads
                // <name>.zxt and the store client derives folders from it.
                {
                    string? fx = root.TryGetProperty("files", out var fEl) && fEl.ValueKind == JsonValueKind.Object ? GetString(fEl, "x64") : null;
                    string? fa = root.TryGetProperty("files", out var fEl2) && fEl2.ValueKind == JsonValueKind.Object ? GetString(fEl2, "arm64") : null;
                    if (fx is not null && fa is not null)
                    {
                        var bx = Path.GetFileName(fx);
                        var ba = Path.GetFileName(fa);
                        if (!string.Equals(bx, ba, StringComparison.OrdinalIgnoreCase))
                            report.Error("FILENAME_MISMATCH", $"x64 file '{bx}' and arm64 file '{ba}' have different names.",
                                "Both architectures must ship the same .zxt base name, e.g. x64/MyPlugin.zxt and arm64/MyPlugin.zxt.");
                        var baseName = Path.GetFileNameWithoutExtension(bx ?? "");
                        if (baseName.Length > 0 && ReservedZxtNames.Contains(baseName))
                            report.Error("RESERVED_NAME", $"'{baseName}.zxt' collides with a plugin Power PDF ships itself.",
                                "Rename the plugin binary; it must not shadow a built-in Power PDF plugin.");
                    }
                }

                if (manifest.AtomNamespace.Length > 0 && !manifest.AtomNamespace.StartsWith("FeaturePack::", StringComparison.Ordinal)
                    && manifest.AtomNamespace != "FeaturePack")
                    report.Warn("ATOM_NOT_SHARED_TAB",
                        $"ribbonAtomNamespace '{manifest.AtomNamespace}' does not live on the shared tab.",
                        "Store plugins share ONE ribbon tab (toolbar atom 'FeaturePack', title 'Enhanced Features'). Use a group atom like 'FeaturePack::MyPlugin' instead of creating an own tab.");

                foreach (var arch in architectures)
                {
                    if (!declared.Contains(arch))
                        report.Error("ARCH_MISSING", $"Architecture '{arch}' is not declared in 'architectures'.",
                            "Both \"x64\" and \"arm64\" are mandatory for every package (Windows-on-ARM support is a standing requirement).");

                    string? file = root.TryGetProperty("files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Object
                        ? GetString(filesEl, arch) : null;
                    if (file is null)
                    {
                        report.Error("FILE_DECLARATION_MISSING", $"'files.{arch}' is not declared in the manifest.",
                            $"Declare 'files.{arch}' with the ZIP path of the {arch} .zxt, e.g. \"{arch}/MyPlugin.zxt\".");
                        continue;
                    }

                    var zxtEntry = zip.GetEntry(file.Replace('\\', '/'));
                    if (zxtEntry is null)
                    {
                        report.Error("FILE_MISSING", $"Declared file '{file}' ({arch}) does not exist in the ZIP.",
                            $"Add the file at exactly that path, or correct 'files.{arch}'.");
                        continue;
                    }

                    using var fileMs = new MemoryStream();
                    await using (var es = zxtEntry.Open()) await es.CopyToAsync(fileMs);
                    var bytes = fileMs.ToArray();

                    var expected = root.TryGetProperty("sha256", out var shaEl) && shaEl.ValueKind == JsonValueKind.Object
                        ? GetString(shaEl, arch) : null;
                    var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                    if (expected is null)
                        report.Error("HASH_MISSING", $"'sha256.{arch}' is not declared in the manifest.",
                            $"Add 'sha256.{arch}': \"{actual}\" (lowercase hex of the file's SHA-256).");
                    else if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                        report.Error("HASH_MISMATCH", $"'sha256.{arch}' does not match the file in the ZIP.",
                            $"Recompute the hash after the final build; the actual value is \"{actual}\".");

                    CheckPe(report, arch, file, bytes);
                }

                CheckIcon(report, zip);
                CheckLicenses(report, zip);
                CheckUiLayout(report, zip);
            }
        }

        report.Warn("MALWARE_SCAN_SKIPPED", "Server-side malware scanning is not configured on this instance yet.",
            "No action needed by the submitter; admins review packages manually until scanning is enabled.");

        if (manifest is not null && manifest.Id.Length > 0)
            await CheckAgainstCatalogAsync(report, manifest, callerUserId);

        return (report, manifest);
    }

    private async Task CheckAgainstCatalogAsync(ValidationReport report, ParsedManifest manifest, string callerUserId)
    {
        var package = await _db.Packages.Include(p => p.Owner)
            .FirstOrDefaultAsync(p => p.Id == manifest.Id);

        if (package is not null && package.OwnerId != callerUserId)
        {
            report.Error("PACKAGE_OWNED_BY_OTHER",
                $"Package id '{manifest.Id}' belongs to another user ({package.Owner?.DisplayName ?? "unknown"}).",
                "Choose a different package id, or ask an admin to transfer ownership.");
            return;
        }

        if (package is not null && manifest.Version.Length > 0)
        {
            var versions = await _db.PackageVersions
                .Where(v => v.PackageId == manifest.Id && v.Status != VersionStatus.Rejected)
                .Select(v => v.Version).ToListAsync();
            var highest = versions.OrderByDescending(v => v, new SemVerComparer()).FirstOrDefault();
            if (highest is not null && new SemVerComparer().Compare(manifest.Version, highest) <= 0)
                report.Error("VERSION_NOT_INCREMENTED",
                    $"Version '{manifest.Version}' is not higher than the latest submitted version '{highest}'.",
                    $"Bump 'version' above {highest}; every release must carry a new, higher version.");
        }

        if (manifest.AtomNamespace.Length > 0)
        {
            var collision = await _db.PackageVersions
                .Where(v => v.AtomNamespace == manifest.AtomNamespace && v.PackageId != manifest.Id)
                .Select(v => v.PackageId).FirstOrDefaultAsync();
            if (collision is not null)
                report.Error("ATOM_COLLISION",
                    $"Ribbon atom namespace '{manifest.AtomNamespace}' is already used by package '{collision}'.",
                    "Choose a unique ribbonAtomNamespace; the host caches ribbon layouts by atom name, collisions break both plugins.");
        }
    }

    private static void CheckPe(ValidationReport report, string arch, string file, byte[] bytes)
    {
        const ushort MachineX64 = 0x8664, MachineArm64 = 0xAA64, DllFlag = 0x2000;
        var expectedMachine = arch == "x64" ? MachineX64 : MachineArm64;

        if (bytes.Length < 0x40 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
        {
            report.Error("PE_INVALID", $"'{file}' is not a Windows PE binary.",
                "A .zxt must be a native Windows DLL built from the Power PDF Plugin SDK.");
            return;
        }
        var peOffset = BitConverter.ToInt32(bytes, 0x3C);
        if (peOffset <= 0 || peOffset + 24 > bytes.Length ||
            bytes[peOffset] != 'P' || bytes[peOffset + 1] != 'E' || bytes[peOffset + 2] != 0 || bytes[peOffset + 3] != 0)
        {
            report.Error("PE_INVALID", $"'{file}' has no valid PE header.",
                "Rebuild the plugin; the file seems truncated or corrupted.");
            return;
        }
        var machine = BitConverter.ToUInt16(bytes, peOffset + 4);
        if (machine != expectedMachine)
            report.Error("PE_WRONG_MACHINE",
                $"'{file}' is built for machine 0x{machine:X4}, expected 0x{expectedMachine:X4} for {arch}.",
                $"Build the {arch} configuration and package that binary under the {arch}/ folder.");
        var characteristics = BitConverter.ToUInt16(bytes, peOffset + 22);
        if ((characteristics & DllFlag) == 0)
            report.Error("PE_NOT_DLL", $"'{file}' is not a DLL.",
                "A .zxt is a renamed DLL; check the project type (dynamic library).");

        CheckImports(report, file, bytes, peOffset);
    }

    /// <summary>System and runtime DLLs a .zxt may import without a finding.</summary>
    private static readonly string[] KnownImportPrefixes =
    {
        "kernel32","user32","gdi32","advapi32","shell32","ole32","oleaut32","comctl32","comdlg32",
        "shlwapi","winhttp","wininet","ws2_32","crypt32","bcrypt","ncrypt","winspool","version",
        "uxtheme","dwmapi","gdiplus","msimg32","imm32","oledlg","winmm","wintrust","secur32",
        "rpcrt4","userenv","netapi32","iphlpapi","dbghelp","psapi","d2d1","dwrite","windowscodecs",
        "urlmon","propsys","uiautomationcore","mscoree",
        "mfc","vcruntime","msvcp","msvcr","ucrtbase","concrt","api-ms-","ext-ms-","ntdll","kernelbase"
    };

    /// <summary>
    /// Walks the PE import directory. Two findings come out of this:
    /// a Debug-CRT import is a hard error (debug builds do not run on customer
    /// machines, learned with the plugin runtime rule), and imports outside the
    /// known system/runtime set are flagged so the reviewer checks whether the
    /// dependency is bundled and license-clean.
    /// </summary>
    private static void CheckImports(ValidationReport report, string file, byte[] bytes, int peOffset)
    {
        try
        {
            var optOffset = peOffset + 24;
            var magic = BitConverter.ToUInt16(bytes, optOffset);
            if (magic != 0x20B) return;                       // PE32+ only; .zxt are 64-bit
            var numSections = BitConverter.ToUInt16(bytes, peOffset + 6);
            var optSize = BitConverter.ToUInt16(bytes, peOffset + 20);
            var importDirRva = BitConverter.ToUInt32(bytes, optOffset + 120);
            var importDirSize = BitConverter.ToUInt32(bytes, optOffset + 124);
            if (importDirRva == 0 || importDirSize == 0) return;

            var sections = new List<(uint va, uint vsize, uint raw, uint rawSize)>();
            var secOffset = optOffset + optSize;
            for (int i = 0; i < numSections; i++)
            {
                var s = secOffset + i * 40;
                if (s + 40 > bytes.Length) return;
                sections.Add((BitConverter.ToUInt32(bytes, s + 12), BitConverter.ToUInt32(bytes, s + 8),
                              BitConverter.ToUInt32(bytes, s + 20), BitConverter.ToUInt32(bytes, s + 16)));
            }
            long Rva(uint rva)
            {
                foreach (var s in sections)
                    if (rva >= s.va && rva < s.va + Math.Max(s.vsize, s.rawSize))
                        return s.raw + (rva - s.va);
                return -1;
            }

            var foreign = new List<string>();
            bool debugCrt = false;
            for (int i = 0; ; i++)
            {
                var desc = Rva(importDirRva) + i * 20;
                if (desc < 0 || desc + 20 > bytes.Length) break;
                var nameRva = BitConverter.ToUInt32(bytes, (int)desc + 12);
                if (nameRva == 0) break;
                var nameOff = Rva(nameRva);
                if (nameOff < 0) break;
                var end = Array.IndexOf(bytes, (byte)0, (int)nameOff);
                if (end < 0) break;
                var dll = System.Text.Encoding.ASCII.GetString(bytes, (int)nameOff, end - (int)nameOff);
                var lower = dll.ToLowerInvariant();

                if (lower.Contains("d.dll") &&
                    (lower.StartsWith("vcruntime") || lower.StartsWith("msvcp") || lower.StartsWith("ucrtbased") || lower.StartsWith("mfc")))
                    debugCrt = true;
                else if (!KnownImportPrefixes.Any(p => lower.StartsWith(p)))
                    foreign.Add(dll);
            }

            if (debugCrt)
                report.Error("PE_DEBUG_RUNTIME", $"'{file}' imports a DEBUG C/C++ runtime.",
                    "Package the Release build; debug runtimes are not present on user machines (the .zxt must run with the same runtime as PowerPDF.exe).");
            if (foreign.Count > 0)
                report.Warn("FOREIGN_DEPENDENCY",
                    $"'{file}' imports non-system DLLs: {string.Join(", ", foreign.Distinct())}.",
                    "Make sure these DLLs ship inside the package (same folder as the .zxt), work without an extra redistributable, and are MIT/BSD/Apache-2.0 licensed.");
        }
        catch
        {
            // import walking is best effort; a malformed table was already caught by the PE checks
        }
    }

    private static void CheckIcon(ValidationReport report, ZipArchive zip)
    {
        var icon = zip.GetEntry("assets/icon.png");
        if (icon is null)
        {
            report.Warn("ICON_MISSING", "assets/icon.png was not found.",
                "Add a square PNG icon at assets/icon.png; the store catalog displays it.");
            return;
        }
        try
        {
            using var ms = new MemoryStream();
            using (var es = icon.Open()) es.CopyTo(ms);
            var b = ms.ToArray();
            bool png = b.Length > 24 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G';
            if (!png) { report.Warn("ICON_INVALID", "assets/icon.png is not a PNG file.", "Export the icon as PNG."); return; }
            int w = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
            int h = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
            if (w != h)
                report.Warn("ICON_NOT_SQUARE", $"assets/icon.png is {w}x{h}.", "Use a square icon (e.g. 128x128).");
            if (w > 1024)
                report.Warn("ICON_TOO_LARGE", $"assets/icon.png is {w}px wide.", "Keep the icon at 512px or less.");
        }
        catch
        {
            report.Warn("ICON_INVALID", "assets/icon.png could not be read.", "Re-export the PNG.");
        }
    }

    private static void CheckLicenses(ValidationReport report, ZipArchive zip)
    {
        var lic = zip.GetEntry("LICENSES.md");
        if (lic is null)
        {
            report.Warn("LICENSES_MISSING", "LICENSES.md was not found.",
                "List all third-party components and their licenses. Only MIT/BSD/Apache-2.0 dependencies are allowed in shipped plugins.");
            return;
        }
        try
        {
            using var reader = new StreamReader(lic.Open());
            var text = reader.ReadToEnd();
            if (Regex.IsMatch(text, @"\b(A?GPL|GNU (Affero )?General Public License|LGPL)\b", RegexOptions.IgnoreCase))
                report.Warn("LICENSE_GPL_MARKER", "LICENSES.md mentions a GPL-family license.",
                    "Only MIT/BSD/Apache-2.0 dependencies are allowed in shipped plugins (standing team policy). Replace the dependency or clarify the mention.");
        }
        catch { /* unreadable file was caught by the ZIP checks */ }
    }

    /// <summary>
    /// Layout checks distilled from the collected SDK pitfalls: the shared tab
    /// rule, the host-owned panel:: namespace, the IconMode=1 trap, and
    /// NameAndTitle language folders that drift apart after a fork/rename.
    /// </summary>
    private static void CheckUiLayout(ValidationReport report, ZipArchive zip)
    {
        var layoutEntries = zip.Entries
            .Where(e => e.FullName.Replace('\\', '/').Contains("UILayout/"))
            .ToList();
        if (layoutEntries.Count == 0) return;

        string ReadEntry(ZipArchiveEntry e)
        {
            using var r = new StreamReader(e.Open());
            return r.ReadToEnd();
        }

        var publish = layoutEntries.FirstOrDefault(e => e.FullName.EndsWith("Publish Mode.xml", StringComparison.OrdinalIgnoreCase));
        if (publish is not null)
        {
            var xml = ReadEntry(publish);
            if (Regex.IsMatch(xml, "name=\"panel::", RegexOptions.IgnoreCase))
                report.Error("RESERVED_PANEL_NS", "Publish Mode.xml declares atoms in the host-owned 'panel::' namespace.",
                    "'panel::' belongs to Power PDF itself; use your own atom namespace for panels.");
            if (Regex.IsMatch(xml, "IconMode=\"1\""))
                report.Warn("ICONMODE_SMALL", "Publish Mode.xml uses IconMode=\"1\" (large button with a SMALL icon).",
                    "Use IconMode=\"4\" for product-sized buttons; 1 renders a large button with a small icon once merged.");
            var tb = Regex.Match(xml, "<toolbar name=\"([^\"]+)\"");
            if (tb.Success && tb.Groups[1].Value != "FeaturePack")
                report.Warn("ATOM_NOT_SHARED_TAB", $"Publish Mode.xml creates its own ribbon tab '{tb.Groups[1].Value}'.",
                    "Store plugins share ONE tab: toolbar atom 'FeaturePack' with the localized title 'Enhanced Features'/'Erweiterte Funktionen'.");
        }

        // NameAndTitle consistency across language folders (classic fork/rename trap)
        var baseNat = layoutEntries.FirstOrDefault(e =>
            Regex.IsMatch(e.FullName.Replace('\\', '/'), @"UILayout/NameAndTitle\.xml$", RegexOptions.IgnoreCase));
        if (baseNat is not null)
        {
            var atoms = new Regex("name=\"([^\"]+)\"");
            var baseAtoms = atoms.Matches(ReadEntry(baseNat)).Select(m => m.Groups[1].Value).OrderBy(x => x).ToList();
            var langFolders = new List<string>();
            foreach (var e in layoutEntries)
            {
                var m = Regex.Match(e.FullName.Replace('\\', '/'), @"UILayout/([A-Z]{3})/NameAndTitle\.xml$", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                var lang = m.Groups[1].Value.ToUpperInvariant();
                langFolders.Add(lang);
                var langAtoms = atoms.Matches(ReadEntry(e)).Select(x => x.Groups[1].Value).OrderBy(x => x).ToList();
                if (!baseAtoms.SequenceEqual(langAtoms))
                    report.Warn("LANG_ATOMS_INCONSISTENT",
                        $"UILayout/{lang}/NameAndTitle.xml declares different atoms than the base NameAndTitle.xml.",
                        "All language folders must carry exactly the same atom set as the base file; this drifts apart easily after a fork or rename.");
            }
            var missing = EuroLangFolders.Where(l => !langFolders.Contains(l)).ToList();
            if (langFolders.Count > 0 && missing.Count > 0)
                report.Warn("LANGS_INCOMPLETE",
                    $"UILayout language folders missing: {string.Join(", ", missing)}.",
                    "Ship all 16 European Power PDF languages (ENU DEU FRA ITA ESP NLD PTB DAN FIN NOR SVE PLK CSY HUN RUS TRK); the ribbon follows the host language.");
        }
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Accepts either a plain string or a {lang: text} object; returns a combined text.</summary>
    private static string ReadTextOrFirstLanguage(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el)) return "";
        if (el.ValueKind == JsonValueKind.String) return el.GetString() ?? "";
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var lang in new[] { "en", "de" })
                if (el.TryGetProperty(lang, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                    return v.GetString()!;
            var first = el.EnumerateObject().FirstOrDefault(p => p.Value.ValueKind == JsonValueKind.String);
            return first.Value.ValueKind == JsonValueKind.String ? first.Value.GetString() ?? "" : "";
        }
        return "";
    }
}

public class SemVerComparer : IComparer<string>
{
    public int Compare(string? x, string? y)
    {
        var a = Parse(x); var b = Parse(y);
        var c = a.Item1.CompareTo(b.Item1); if (c != 0) return c;
        c = a.Item2.CompareTo(b.Item2); if (c != 0) return c;
        return a.Item3.CompareTo(b.Item3);
    }

    private static (int, int, int) Parse(string? v)
    {
        if (v is null) return (0, 0, 0);
        var parts = v.Split('.');
        int P(int i) => parts.Length > i && int.TryParse(parts[i], out var n) ? n : 0;
        return (P(0), P(1), P(2));
    }
}
