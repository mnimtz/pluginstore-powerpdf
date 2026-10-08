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

    // Decompression caps (zip-bomb defense): per entry type and total inflated.
    private const long MaxManifestBytes = 256 * 1024;
    private const long MaxTextEntryBytes = 1 * 1024 * 1024;
    private const long MaxIconBytes = 4 * 1024 * 1024;
    private const long MaxZxtBytes = 120 * 1024L * 1024L;
    private const long MaxTotalInflatedBytes = 400 * 1024L * 1024L;
    private long _inflated;
    private bool _inflateCapHit;

    /// <summary>Reads an entry with a hard decompressed-size cap; null when exceeded.</summary>
    private readonly Dictionary<string, byte[]> _readCache = new();

    private byte[]? ReadCapped(ZipArchiveEntry entry, long cap)
    {
        if (_readCache.TryGetValue(entry.FullName, out var hit)) return hit.Length <= cap ? hit : null;
        var data = ReadCappedCore(entry, cap);
        // binaries are read by the PE checks and again by the license/secret scan
        if (data is not null && data.Length > 1024 * 1024) _readCache[entry.FullName] = data;
        return data;
    }

    private byte[]? ReadCappedCore(ZipArchiveEntry entry, long cap)
    {
        using var ms = new MemoryStream();
        using var es = entry.Open();
        var buf = new byte[81920];
        int got;
        while ((got = es.Read(buf, 0, buf.Length)) > 0)
        {
            _inflated += got;
            if (_inflated > MaxTotalInflatedBytes) { _inflateCapHit = true; return null; }
            if (ms.Length + got > cap) return null;
            ms.Write(buf, 0, got);
        }
        return ms.ToArray();
    }

    // \z, not $: $ also matches before a trailing newline (audit S1.3.1)
    private static readonly Regex IdPattern = new(@"^[a-z0-9][a-z0-9-]*(\.[a-z0-9][a-z0-9-]*)+\z", RegexOptions.Compiled);
    // [0-9] and \z: no non-ASCII digits, no trailing newline ($ would allow one); 9 digits fit an int
    // three numbers, an optional fourth (S1.4.1): e.g. Power PDF's Year.Quarter.Update(.Fix)
    private static readonly Regex SemVerPattern = new(@"^[0-9]{1,9}\.[0-9]{1,9}\.[0-9]{1,9}(\.[0-9]{1,9})?\z", RegexOptions.Compiled);
    private static readonly Regex HostVersionPattern = new(@"^[0-9]{1,4}(\.[0-9]{1,6}){0,3}\z", RegexOptions.Compiled);

    /// <summary>Ribbon tab atoms of Power PDF and the store; a private add-on's own tab must not reuse them.</summary>
    private static readonly HashSet<string> ReservedToolbars = new(StringComparer.OrdinalIgnoreCase)
    {
        "FeaturePack","AddonStore","PluginStore","tool","help","connectors","panel","ZEON:Add-ins","ZEON",
        "TouchupObjectBar","ReadingOrderToolBar","DrawingTool","HighlightAreaToolBar","UnderlineToolBar",
        "CrossoutToolBar","HighlightToolBar","FreetextTool","CaretToolBar","StampToolBar","FillSignToolBar",
        "NoteToolBar","AttachFileToolBar","AttachSoundToolBar","AuthSelectArrange","EditTextFormatBar",
        "TypeWriter","TextField","AccessmblyToolBar"
    };

    /// <summary>True when the namespace is not on the shared tab, i.e. the add-on brings its own tab.</summary>
    public static bool IsOwnTabNamespace(string ns) =>
        ns.Length > 0 && ns != "FeaturePack" && !ns.StartsWith("FeaturePack::", StringComparison.Ordinal)
        && !ns.StartsWith("AddonStore::", StringComparison.Ordinal);

    /// <summary>Tab atom of an own-tab namespace ("CustomerSign::Main" -> "CustomerSign").</summary>
    public static string TabOf(string ns)
    {
        int i = ns.IndexOf("::", StringComparison.Ordinal);
        return i < 0 ? ns : ns[..i];
    }

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

    private const int MaxEntries = 5000;
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON","PRN","AUX","NUL","COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
        "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"
    };
    private static readonly HashSet<string> AllowedTopFolders = new(StringComparer.OrdinalIgnoreCase)
        { "x64", "arm64", "assets", "docs", "UILayout", "installer", "bin" };
    /// <summary>bin/ (S1.4.0): the add-on's own DLLs, installed to Plug-Ins\&lt;Name&gt;\bin\. DLLs only.</summary>
    public const int MaxBinFiles = 64;
    private static readonly System.Text.RegularExpressions.Regex BinPath =
        new(@"^bin/[A-Za-z0-9][A-Za-z0-9_.-]{0,63}\.dll\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    /// <summary>Id prefixes of the store operators; only admins create packages there.</summary>
    private static readonly string[] ReservedIdPrefixes = { "com.tungsten.", "com.kofax.", "com.nuance." };
    private static string Printable(string s) => new(s.Select(c => c < 0x20 || c == 0x7F ? '?' : c).ToArray());

    /// <summary>All 16 European Power PDF UI language folder codes.</summary>
    /// <summary>UILayout folders of the five further Power PDF languages (S1.2.0), recommended.</summary>
    private static readonly string[] ExtendedLangFolders = { "CHS", "CHT", "JPN", "KOR", "ARA" };

    private static readonly string[] EuroLangFolders =
        { "ENU","DEU","FRA","ITA","ESP","NLD","PTB","DAN","FIN","NOR","SVE","PLK","CSY","HUN","RUS","TRK" };

    private readonly AppDbContext _db;

    private readonly Services.CategoryService? _categories;

    public PackageValidator(AppDbContext db, Services.CategoryService? categories = null)
    {
        _db = db; _categories = categories;
    }

    public async Task<(ValidationReport Report, ParsedManifest? Manifest)> ValidateAsync(
        string zipPath, string callerUserId, bool callerIsAdmin = false)
    {
        // A corrupt deflate stream or a malformed binary must give a report, not a 500.
        _inflated = 0; _inflateCapHit = false; _readCache.Clear();
        try
        {
            var (report, manifest) = await ValidateCoreAsync(zipPath, callerUserId, callerIsAdmin);
            if (_inflateCapHit)
                report.Error("INFLATE_LIMIT", $"The package unpacks to more than {MaxTotalInflatedBytes / 1048576} MB.",
                    "Reduce the package content; the checks need to read every file.");
            return (report, manifest);
        }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException or IOException or OverflowException)
        {
            var report = new ValidationReport();
            report.Error("ZIP_UNREADABLE", $"The package could not be read completely ({ex.GetType().Name}).",
                "Create the package as a standard ZIP container (deflate or stored) with intact files.");
            return (report, null);
        }
    }

    private async Task<(ValidationReport Report, ParsedManifest? Manifest)> ValidateCoreAsync(
        string zipPath, string callerUserId, bool callerIsAdmin)
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

        // the number of entries from the end record, before ZipArchive loads them all (audit S1.3.1)
        if (Services.UploadLimits.DeclaredEntries(zipPath) is var declaredEntries && declaredEntries > MaxEntries)
        {
            report.Error("ZIP_TOO_MANY_ENTRIES", $"The package declares {(declaredEntries == int.MaxValue ? "a ZIP64 or inconsistent directory" : declaredEntries + " entries")} (at most {MaxEntries}).",
                "Pack only manifest.json, the binaries, UILayout, assets and docs; a package never needs that many files.");
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

            // Names that collide on Windows (case, '\' vs '/', trailing dots/spaces) would make the
            // client's extraction fail or show reviewers another file than the one checked.
            if (zip.Entries.Count > MaxEntries)
            {
                report.Error("ZIP_TOO_MANY_ENTRIES", $"The package has {zip.Entries.Count} entries (at most {MaxEntries}).",
                    "Ship only what the plug-in needs at run time.");
                return (report, null);
            }
            var seenNames = new Dictionary<string, string>(StringComparer.Ordinal);
            var unexpected = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                var raw = entry.FullName.Replace('\\', '/');
                if (raw.Any(c => c < 0x20 || c == 0x7F))
                {
                    report.Error("ZIP_SLIP", $"Entry '{Printable(entry.FullName)}' contains control characters.",
                        "Use plain file names.");
                    return (report, null);
                }
                var segs = raw.TrimEnd('/').Split('/');
                foreach (var seg in segs)
                {
                    var stem = seg.Split('.')[0].TrimEnd(' ');
                    if (ReservedDeviceNames.Contains(stem))
                    {
                        report.Error("ZIP_RESERVED_NAME", $"Entry '{entry.FullName}' uses the reserved Windows name '{seg}'.",
                            "Windows cannot create files or folders named CON, PRN, AUX, NUL, COM1-9 or LPT1-9; rename it.");
                        return (report, null);
                    }
                }
                var key = string.Join("/", segs.Select(s => s.TrimEnd('.', ' ').Normalize(System.Text.NormalizationForm.FormC)))
                          .ToUpperInvariant() + (raw.EndsWith('/') ? "/" : "");
                if (seenNames.TryGetValue(key, out var first))
                {
                    report.Error("ZIP_DUPLICATE_ENTRY", $"Entries '{first}' and '{entry.FullName}' are the same file on Windows.",
                        "Each path may appear only once (case, '\\' and '/', trailing dots and spaces do not make names different on Windows).");
                    return (report, null);
                }
                seenNames[key] = entry.FullName;
                var top = segs[0];
                if (!(segs.Length == 1 && (top.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ||
                                           top.Equals("LICENSES.md", StringComparison.OrdinalIgnoreCase))) &&
                    !(segs.Length > 1 && AllowedTopFolders.Contains(top)))
                    unexpected.Add(segs.Length > 1 ? top + "/" : top);
            }
            if (unexpected.Count > 0)
                report.Warn("UNEXPECTED_ENTRY", $"The package contains entries the client never installs: {string.Join(", ", unexpected.Take(8))}.",
                    "A package holds manifest.json, LICENSES.md and the folders x64/, arm64/, assets/, docs/, UILayout/ (installer/ for the store client); remove anything else.");

            var manifestEntry = zip.GetEntry("manifest.json");
            if (manifestEntry is null)
            {
                // the most common cause (S1.0.7): the package tree was zipped together with its folder
                var nested = zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').Count(c => c == '/') == 1
                                                             && e.FullName.Replace('\\', '/').EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase));
                if (nested is not null)
                {
                    var folder = nested.FullName.Replace('\\', '/').Split('/')[0];
                    report.Error("MANIFEST_MISSING", $"manifest.json is inside the folder '{folder}/' instead of at the package root.",
                        $"Zip the CONTENTS of '{folder}' (manifest.json, x64/, UILayout/, assets/, LICENSES.md), not the folder itself. " +
                        "For one upload with the source code, put the finished .ppak and the source ZIP into an upload package (see 'Manual upload package' in /api/agent-guide).");
                }
                else
                    report.Error("MANIFEST_MISSING", "manifest.json was not found at the package root.",
                        "Add manifest.json at the ZIP root. Fetch /api/schema/manifest for the expected structure. " +
                        "An upload package instead holds exactly one .ppak and the source ZIP (see 'Manual upload package' in /api/agent-guide).");
                return (report, null);
            }

            var manifestBytes = ReadCapped(manifestEntry, MaxManifestBytes);
            if (manifestBytes is null)
            {
                report.Error("MANIFEST_TOO_LARGE", "manifest.json exceeds 256 KB.",
                    "Keep the manifest small; long documentation belongs under docs/.");
                return (report, null);
            }
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(manifestBytes);
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
                    report.Error("VERSION_INVALID", $"Manifest field 'version' is missing or not three or four numbers ('{version}').",
                        "Set 'version' to three numbers, optionally a fourth, e.g. '1.2.0' or '2026.4.0.3' (Year.Quarter.Update.Fix). Every upload must carry a new, higher version.");
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

                // the catalog name in every language too (S1.4.0): a warning, which a store can make
                // mandatory (Settings, Rules); a product name may read the same in every language
                var nameMissing = MissingLanguages(root, "name");
                if (nameMissing.Count > 0)
                    report.Error("NAME_NOT_LOCALIZED",
                        $"'name' is missing languages: {string.Join(", ", nameMissing)}; the catalog shows the English name there.",
                        $"Give 'name' an entry for each of {string.Join(", ", RequiredLanguages)} (and zh-Hans, zh-Hant, ja, ko, ar). " +
                        "Translate descriptive names; a product name may read the same in every language, but every language needs its entry.");
                if (nameMissing.Count == 0)
                {
                    var nameExtended = MissingExtended(root, "name");
                    if (nameExtended.Count > 0)
                        report.Error("LANG_TEXT_EXTENDED",
                            $"'name' lacks the further Power PDF languages: {string.Join(", ", nameExtended)}.",
                            "Add \"zh-Hans\", \"zh-Hant\", \"ja\", \"ko\" and \"ar\" to the name (a product name may stay the same).");
                }

                // Every user-facing text ships in ALL European Power PDF languages
                // (standing team rule).
                foreach (var field in new[] { "description", "changelog" })
                {
                    var missing = MissingLanguages(root, field);
                    if (missing.Count > 0)
                        report.Error("LANG_TEXT_INCOMPLETE",
                            $"'{field}' is missing languages: {string.Join(", ", missing)}.",
                            $"Provide '{field}' as an object with all 16 languages: {string.Join(", ", RequiredLanguages)}. " +
                            "Translate the text yourself; the store shows it in the user's language.");
                    // the five further Power PDF languages: recommended since S1.2.0, required since S1.11.0
                    var extended = MissingExtended(root, field);
                    if (extended.Count > 0)
                        report.Error("LANG_TEXT_EXTENDED",
                            $"'{field}' lacks the further Power PDF languages: {string.Join(", ", extended)}.",
                            "Power PDF also runs in Simplified and Traditional Chinese, Japanese, Korean and Arabic: add " +
                            "\"zh-Hans\", \"zh-Hant\", \"ja\", \"ko\" and \"ar\" (translate the text yourself). All 21 Power PDF languages are required.");
                }

                // Add-ons without ribbon buttons (S1.1.1) declare "ui": "none"; the ribbon rules
                // do not apply to them, every other check does.
                var ui = GetString(root, "ui");
                if (ui is not null && ui != "ribbon" && ui != "none")
                    report.Error("UI_INVALID", $"Manifest field 'ui' is '{ui.Replace("\n", " ")}'.",
                        "Use \"ribbon\" (default, buttons on the shared tab) or \"none\" (no ribbon buttons, e.g. an engine for a Power PDF feature).");
                manifest.NoUi = ui == "none";
                manifest.AtomNamespace = GetString(root, "ribbonAtomNamespace") ?? "";
                if (manifest.NoUi)
                {
                    if (manifest.AtomNamespace.Length > 0)
                        report.Warn("UI_NONE_ATOM", "The add-on declares \"ui\": \"none\" and a ribbonAtomNamespace; the namespace is ignored.",
                            "Remove ribbonAtomNamespace, or set \"ui\": \"ribbon\" and ship the UILayout files.");
                    manifest.AtomNamespace = "";
                    report.Info("UI_NONE", "Add-on without ribbon buttons (\"ui\": \"none\").",
                        "Reviewers check that the description says where the function appears in Power PDF and how to switch it off (approval condition R5).");
                    var en = root.TryGetProperty("description", out var dEl) && dEl.ValueKind == JsonValueKind.Object ? GetString(dEl, "en") ?? "" : "";
                    if (en.Trim().Length < 80)
                        report.Warn("UI_NONE_DESCRIPTION", "The description of an add-on without ribbon buttons is short.",
                            "Users see no button: describe in 'description' (all 16 languages) where the function appears in Power PDF (e.g. an engine in a feature's settings) and how to switch it off.");
                }
                else if (manifest.AtomNamespace.Length == 0)
                    report.Warn("ATOM_NAMESPACE_MISSING", "Manifest field 'ribbonAtomNamespace' is not set.",
                        "Declare the ribbon atom namespace your plugin uses; the host caches ribbons by atom name and the store checks for collisions. Add-ons without ribbon buttons declare \"ui\": \"none\" instead.");

                manifest.MinPowerPdfVersion = GetString(root, "minPowerPdfVersion") ?? "";
                if (manifest.MinPowerPdfVersion.Length > 0 && !HostVersionPattern.IsMatch(manifest.MinPowerPdfVersion))
                    report.Error("MIN_HOST_VERSION_INVALID", $"Manifest field 'minPowerPdfVersion' is '{manifest.MinPowerPdfVersion.Replace("\n", " ")}'.",
                        "Use a plain version such as \"5.0\" or \"2025.3\" (digits and dots only).");
                if (manifest.MinPowerPdfVersion.Length == 0)
                    report.Warn("MIN_HOST_VERSION_MISSING", "Manifest field 'minPowerPdfVersion' is not set.",
                        "State the lowest Power PDF version the plugin was tested with, e.g. \"5.0\".");

                // author/contactEmail: shown in the catalog and to customers (S1.0.6); without
                // them the catalog falls back to the account, which may not show its contact.
                var author = GetString(root, "author")?.Trim();
                if (string.IsNullOrEmpty(author))
                    report.Error("AUTHOR_MISSING", "Manifest field 'author' is not set.",
                        "Name the person or team behind the add-on, e.g. \"author\": \"Team Signing\"; it is shown in the catalog and in Power PDF.");
                else if (author.Length > 100)
                    report.Error("AUTHOR_INVALID", "Manifest field 'author' is longer than 100 characters.", "Use a person's or team's name.");
                var contact = GetString(root, "contactEmail")?.Trim();
                if (string.IsNullOrEmpty(contact))
                    report.Error("CONTACT_MISSING", "Manifest field 'contactEmail' is not set.",
                        "Give a reachable support address, e.g. \"contactEmail\": \"team@example.com\"; users and customers see it in the catalog.");
                else if (contact.Length > 200 || !System.Net.Mail.MailAddress.TryCreate(contact, out _))
                    report.Error("CONTACT_INVALID", $"Manifest field 'contactEmail' is not a valid email address.",
                        "Use a reachable address such as team@example.com.");

                // visibility: "private" = only for customers with a delivery and code (S0.14.0)
                var vis = GetString(root, "visibility")?.Trim().ToLowerInvariant();
                if (vis is not null and not ("public" or "private"))
                    report.Error("VISIBILITY_INVALID", $"Manifest field 'visibility' is '{vis}'.",
                        "Use \"public\" (catalog, default) or \"private\" (only customers with a delivery and code see it).");
                else manifest.Visibility = vis ?? "public";

                manifest.Category = GetString(root, "category")?.Trim() ?? "";
                if (_categories is not null)
                    manifest.NewCategoryNames = await _categories.CheckAsync(report, root, manifest.Id);

                // architectures + files + hashes + PE checks. x64 is mandatory and
                // alone covers every machine (ARM64EC hosts load x64 plugins); a
                // native arm64 build is optional and validated when present.
                var declared = new HashSet<string>();
                if (root.TryGetProperty("architectures", out var archEl) && archEl.ValueKind == JsonValueKind.Array)
                    foreach (var a in archEl.EnumerateArray())
                        if (a.ValueKind == JsonValueKind.String) declared.Add(a.GetString()!);

                if (!declared.Contains("x64"))
                    report.Error("ARCH_MISSING", "Architecture 'x64' is not declared in 'architectures'.",
                        "\"x64\" is mandatory; it also serves Windows-on-ARM, where Power PDF runs as ARM64EC and loads x64 plugins.");
                if (!declared.Contains("arm64"))
                    report.Info("ARCH_ARM64_ABSENT", "No native arm64 build is included (fine: ARM64EC hosts run the x64 binary).",
                        "Optionally add a native arm64 build under arm64/ once the toolchain supports it.");

                var architectures = declared.Contains("arm64") ? new[] { "x64", "arm64" } : new[] { "x64" };
                foreach (var a in declared.Where(a => a is not ("x64" or "arm64")))
                    report.Error("ARCH_UNKNOWN", $"Architecture '{a}' is not supported.", "Use \"x64\" and optionally \"arm64\".");
                if (!declared.Contains("arm64") && root.TryGetProperty("files", out var fCheck) && fCheck.ValueKind == JsonValueKind.Object
                    && fCheck.TryGetProperty("arm64", out _))
                    report.Error("ARCH_UNDECLARED", "'files.arm64' is set, but \"arm64\" is not listed in 'architectures'.",
                        "Add \"arm64\" to 'architectures' (the binary is then checked like the x64 one) or remove 'files.arm64'.");

                // Both architectures, when present, must share the same base name:
                // the host loads <name>.zxt and the client derives folders from it.
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
                    }
                    // The base name becomes a file and folder name on every client and is
                    // used in the installer: letters, digits, '-' and '_' only, directly
                    // below x64/ (or arm64/).
                    foreach (var (arch, f) in new[] { ("x64", fx), ("arm64", fa) })
                        if (f is not null && !System.Text.RegularExpressions.Regex.IsMatch(f, "^" + arch + "/[A-Za-z0-9_-]{1,64}\\.zxt$"))
                            report.Error("ZXT_NAME_INVALID", $"files.{arch} is '{f}'.",
                                $"Use \"{arch}/<Name>.zxt\" with a name of 1 to 64 letters, digits, '-' or '_' (no spaces, dots or other characters).");
                    var baseName = Path.GetFileNameWithoutExtension(fx ?? "");
                    manifest.ZxtName = baseName;
                    if (baseName.Length > 0 && ReservedZxtNames.Contains(baseName) && manifest.Id != "com.tungsten.pluginstore")
                        report.Error("RESERVED_NAME", $"'{baseName}.zxt' collides with a plugin Power PDF ships itself.",
                            "Rename the plugin binary; it must not shadow a built-in Power PDF plugin.");
                }

                // The store client itself has its own tab "Store" (toolbar atom "AddonStore", C1.1.0).
                // Public add-ons live on the shared tab; a PRIVATE customer add-on may have its own
                // tab (S1.0.6). Whether the package is private is known only against the catalog,
                // so the decision is made in CheckAgainstCatalogAsync.
                bool clientOwnTab = manifest.Id == "com.tungsten.pluginstore" &&
                                    manifest.AtomNamespace.StartsWith("AddonStore::", StringComparison.Ordinal);
                manifest.OwnTab = manifest.AtomNamespace.Length > 0 && !clientOwnTab && IsOwnTabNamespace(manifest.AtomNamespace);
                if (manifest.OwnTab && ReservedToolbars.Contains(TabOf(manifest.AtomNamespace)))
                    report.Error("OWN_TAB_RESERVED", $"The ribbon tab '{TabOf(manifest.AtomNamespace)}' belongs to Power PDF or the store.",
                        "Choose your own tab atom (e.g. 'CustomerSign'), or use a group on the shared tab ('FeaturePack::MyPlugin').");

                var bundled = new HashSet<string>(
                    zip.Entries.Where(e => e.Length > 0 && e.FullName.Replace('\\', '/').StartsWith("bin/", StringComparison.OrdinalIgnoreCase))
                               .Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
                foreach (var arch in architectures)
                {
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

                    var bytes = ReadCapped(zxtEntry, MaxZxtBytes);
                    if (bytes is null)
                    {
                        report.Error("ENTRY_TOO_LARGE", $"'{file}' inflates beyond the allowed size.",
                            "A single .zxt may be at most 120 MB uncompressed; large payloads must be fetched at install time.");
                        continue;
                    }

                    var expected = root.TryGetProperty("sha256", out var shaEl) && shaEl.ValueKind == JsonValueKind.Object
                        ? GetString(shaEl, arch) : null;
                    var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                    if (expected is null)
                        report.Error("HASH_MISSING", $"'sha256.{arch}' is not declared in the manifest.",
                            $"Add 'sha256.{arch}': \"{actual}\" (lowercase hex of the file's SHA-256).");
                    else if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                        report.Error("HASH_MISMATCH", $"'sha256.{arch}' does not match the file in the ZIP.",
                            $"Recompute the hash after the final build; the actual value is \"{actual}\".");

                    CheckPe(report, arch, file, bytes, manifest.Version, bundled);
                    CheckCapabilities(report, file, bytes, root);   // network, injection, downloads, processes, persistence (S1.0.11)
                }
                CheckBin(report, zip, root, manifest, bundled);

                CheckIcon(report, zip);
                CheckScreenshots(report, zip, root);
                CheckLicenses(report, zip);
                CheckThirdParty(report, zip, root);
                if (manifest.NoUi) CheckNoUiLayout(report, zip);
                else CheckUiLayout(report, zip, manifest.AtomNamespace, manifest.Id, manifest.OwnTab);
                CheckDocs(report, zip);
            }
        }

        if (manifest is not null && manifest.Id.Length > 0)
            await CheckAgainstCatalogAsync(report, manifest, callerUserId, callerIsAdmin);

        RuleCatalog.Escalate(report);   // the store's stricter rules (S1.0.9)
        return (report, manifest);
    }

    private async Task CheckAgainstCatalogAsync(ValidationReport report, ParsedManifest manifest, string callerUserId, bool callerIsAdmin)
    {
        var package = await _db.Packages.Include(p => p.Owner)
            .FirstOrDefaultAsync(p => p.Id == manifest.Id);

        if (package is not null && package.OwnerId != callerUserId && callerIsAdmin)
            report.Info("ADMIN_UPLOAD_FOR_OWNER",
                $"Admin upload for '{manifest.Id}', owned by {package.Owner?.DisplayName ?? "unknown"}; the owner stays unchanged and is notified.");
        else if (package is not null && package.OwnerId != callerUserId)
        {
            report.Error("PACKAGE_OWNED_BY_OTHER",
                package.Visibility == "private"
                    ? $"Package id '{manifest.Id}' is taken."
                    : $"Package id '{manifest.Id}' belongs to another user ({Services.CatalogUi.PublicName(package.Owner)}).",
                "Choose a different package id, or ask an admin to transfer ownership.");
            return;
        }

        // A blocked add-on takes no new versions (S1.0.11).
        if (package?.BlockedAt is not null)
        {
            report.Error("PACKAGE_BLOCKED", $"Package '{manifest.Id}' is blocked by the store admins.",
                "A blocked add-on takes no uploads. Contact the store admins; they lift the block once the security problem is solved.");
            return;
        }

        // Ids of the store operators' namespaces are reserved for admins.
        if (package is null && !callerIsAdmin && ReservedIdPrefixes.Any(p => manifest.Id.StartsWith(p, StringComparison.Ordinal)))
            report.Error("ID_RESERVED", $"Ids starting with '{manifest.Id.Split('.')[0]}.{manifest.Id.Split('.')[1]}.' are reserved.",
                "Use your own reverse-DNS prefix (your company or domain), e.g. 'com.example.myplugin'.");

        if (package is not null && manifest.Version.Length > 0)
        {
            var all = await _db.PackageVersions.Where(v => v.PackageId == manifest.Id)
                .Select(v => new { v.Version, v.Status }).ToListAsync();
            var same = all.FirstOrDefault(v => v.Version == manifest.Version);
            if (same is not null)
            {
                report.Error("VERSION_EXISTS", $"Version {manifest.Version} already exists (status: {same.Status}).",
                    "Every upload carries a new, higher version; bump 'version' in the manifest.");
                return;
            }
            var versions = all.Where(v => v.Status != VersionStatus.Rejected).Select(v => v.Version).ToList();
            var highest = versions.OrderByDescending(v => v, new SemVerComparer()).FirstOrDefault();
            if (highest is not null && new SemVerComparer().Compare(manifest.Version, highest) <= 0)
                report.Error("VERSION_NOT_INCREMENTED",
                    $"Version '{manifest.Version}' is not higher than the latest submitted version '{highest}'.",
                    $"Bump 'version' above {highest}; every release must carry a new, higher version.");
        }

        // Two packages with the same binary name would overwrite and uninstall each other.
        if (manifest.ZxtName.Length > 0)
        {
            var others = await _db.PackageVersions.AsNoTracking()
                .Where(v => v.PackageId != manifest.Id && v.Status != VersionStatus.Rejected)
                .Select(v => new { v.PackageId, v.ManifestJson, Private = _db.Packages.Any(p => p.Id == v.PackageId && p.Visibility == "private") })
                .ToListAsync();
            foreach (var o in others)
            {
                try
                {
                    using var d = JsonDocument.Parse(o.ManifestJson);
                    if (d.RootElement.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Object &&
                        f.TryGetProperty("x64", out var x) && x.ValueKind == JsonValueKind.String &&
                        string.Equals(Path.GetFileNameWithoutExtension(x.GetString()), manifest.ZxtName, StringComparison.OrdinalIgnoreCase))
                    {
                        report.Error("ZXT_NAME_TAKEN", o.Private
                                ? $"The plug-in file name '{manifest.ZxtName}.zxt' is already used by another package."
                                : $"The plug-in file name '{manifest.ZxtName}.zxt' is already used by package '{o.PackageId}'.",
                            "Choose another binary name; two add-ons with the same file name would overwrite each other.");
                        break;
                    }
                }
                catch (JsonException) { }
            }
        }

        // DLLs of different add-ons share the Power PDF process: one name, one module (S1.4.0)
        if (manifest.BinFiles.Count > 0)
        {
            var mine = new HashSet<string>(manifest.BinFiles, StringComparer.OrdinalIgnoreCase);
            var others = await _db.PackageVersions.AsNoTracking()
                // withdrawn versions count: they may still be installed somewhere (S1.4.3)
                .Where(v => v.PackageId != manifest.Id && v.Status != VersionStatus.Rejected)
                .Select(v => new { v.PackageId, v.ManifestJson, Private = _db.Packages.Any(p => p.Id == v.PackageId && p.Visibility == "private") })
                .ToListAsync();
            var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in others)
                foreach (var dll in BinFilesOf(o.ManifestJson).Where(mine.Contains))
                    if (reported.Add(dll))
                        report.Error("BIN_NAME_TAKEN", o.Private
                                ? $"bin/{dll} has the same name as a DLL of another add-on."
                                : $"bin/{dll} has the same name as a DLL of the add-on '{o.PackageId}'.",
                            "All add-ons run in one Power PDF process, where one DLL name means one module. Rename your DLL with your own prefix (e.g. MyPlugin_core.dll).");
        }

        // Own ribbon tab: only for private customer add-ons (S1.0.6). The package's current
        // visibility counts; on the first upload the manifest's.
        if (manifest.OwnTab)
        {
            var effective = package?.Visibility ?? manifest.Visibility;
            if (effective == "private")
                report.Info("OWN_TAB_PRIVATE", $"The add-on brings its own ribbon tab '{TabOf(manifest.AtomNamespace)}'; allowed because it is private.",
                    "It cannot be switched to public while a version has its own tab; a public version must move to the shared tab ('FeaturePack::...').");
            else
                report.Error("ATOM_NOT_SHARED_TAB",
                    $"ribbonAtomNamespace '{manifest.AtomNamespace}' creates its own ribbon tab, but the add-on is public.",
                    "Public store plugins share ONE ribbon tab (toolbar atom 'FeaturePack', title 'Enhanced Features'): use a group atom like 'FeaturePack::MyPlugin'. Only private customer add-ons (\"visibility\": \"private\") may have their own tab.");
        }

        if (manifest.AtomNamespace == "FeaturePack")
            report.Error("ATOM_NOT_SHARED_TAB", "ribbonAtomNamespace is the shared tab itself.",
                "Declare your own group atom on the shared tab, e.g. 'FeaturePack::MyPlugin'.");
        else if (manifest.AtomNamespace.Length > 0)
        {
            var collision = await _db.PackageVersions
                .Where(v => v.AtomNamespace == manifest.AtomNamespace && v.PackageId != manifest.Id
                            && v.Status != VersionStatus.Rejected && v.Status != VersionStatus.Withdrawn)
                .Select(v => new { v.PackageId, Private = _db.Packages.Any(p => p.Id == v.PackageId && p.Visibility == "private") })
                .FirstOrDefaultAsync();
            if (collision is not null)
                report.Error("ATOM_COLLISION",
                    collision.Private
                        ? $"Ribbon atom namespace '{manifest.AtomNamespace}' is already used by another package."
                        : $"Ribbon atom namespace '{manifest.AtomNamespace}' is already used by package '{collision.PackageId}'.",
                    "Choose a unique ribbonAtomNamespace; the host caches ribbon layouts by atom name, collisions break both plugins.");
        }
    }

    private static void CheckPe(ValidationReport report, string arch, string file, byte[] bytes, string manifestVersion,
                                ISet<string>? bundled = null)
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
        if (peOffset <= 0 || (long)peOffset + 24 > bytes.Length ||
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

        CheckImports(report, file, bytes, peOffset, bundled, isZxt: true);
        var pe = PeResources.Open(bytes);
        if (!pe.Valid) return;
        if (pe.IsManaged)
            report.Error("PE_MANAGED", $"'{file}' is a .NET assembly.",
                "Power PDF loads native plug-ins only; build the .zxt as a native C++ DLL with the Plugin SDK.");
        if (!pe.Exports().Contains("PlugInMain"))
            report.Error("PE_NO_ENTRY", $"'{file}' does not export PlugInMain.",
                "Power PDF loads a plug-in through its PlugInMain export (Plugin SDK, linker option /EXPORT:PlugInMain).");
        const ushort DynamicBase = 0x40, NxCompat = 0x100;
        if ((pe.DllCharacteristics & DynamicBase) == 0 || (pe.DllCharacteristics & NxCompat) == 0)
            report.Warn("PE_HARDENING", $"'{file}' is built without ASLR (/DYNAMICBASE) or DEP (/NXCOMPAT).",
                "Keep the default linker options /DYNAMICBASE and /NXCOMPAT (and /HIGHENTROPYVA); they make exploits much harder.");
        var fv = pe.FileVersion();
        if (fv is null)
            report.Warn("VERSIONINFO_MISSING", $"'{file}' has no version resource.",
                "Add a VERSIONINFO resource whose FILEVERSION matches the manifest version; support and the store use it to tell builds apart.");
        else if (manifestVersion.Length > 0)
        {
            // a four-part version is compared in full; with three parts the binary's fourth field (build) stays free
            var (a, b, c, d4) = fv.Value;
            var four = manifestVersion.Count(ch => ch == '.') == 3;
            var binary = four ? $"{a}.{b}.{c}.{d4}" : $"{a}.{b}.{c}";
            if (binary != manifestVersion)
                report.Warn("VERSIONINFO_MISMATCH", $"'{file}' has FILEVERSION {binary}, the manifest says {manifestVersion}.",
                    "Build the binary with the same version as the manifest (FILEVERSION major,minor,patch,0); a mismatch usually means an old build was packaged (fine only for releases without a new binary, e.g. documentation).");
        }
        if (arch == "x64") CheckUiLanguages(report, file, pe);
    }

    /// <summary>
    /// The add-on's own UI follows the Power PDF UI language: its string tables
    /// (or, without any, its dialogs and menus) must exist in all 16 Power PDF
    /// languages as LANGUAGE blocks in the .zxt resources.
    /// </summary>
    private static void CheckUiLanguages(ValidationReport report, string file, PeResources pe)
    {
        var res = pe.Languages();
        if (res is null)
        {
            report.Warn("UI_LANGS_UNREADABLE", $"The resources of '{file}' could not be read.",
                "Build the plug-in with standard resource scripts (.rc); the store reads the string tables to check the 16 languages.");
            return;
        }
        // string tables decide; without language-specific ones, dialogs and menus do
        res.TryGetValue(PeResources.RtString, out var strings);
        bool HasLanguages(Dictionary<int, HashSet<int>>? d) => d is not null && d.Keys.Any(l => l != 0);
        var basis = HasLanguages(strings) ? strings!
            : res.Values.SelectMany(d => d).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.SelectMany(kv => kv.Value).ToHashSet());
        var present = basis.Keys.Where(l => l != 0).ToHashSet();
        if (present.Count == 0)
        {
            report.Warn("UI_LANGS_UNKNOWN", $"'{file}' has no localized string tables, dialogs or menus.",
                "Texts hard-coded in the source cannot follow the Power PDF language. Put every visible text into the .rc string table with one LANGUAGE block per Power PDF language (16).");
            return;
        }
        var missing = PeResources.PowerPdfLanguages.Where(l => !present.Contains(l.Primary)).Select(l => l.Code).ToList();
        if (missing.Count > 0)
            report.Error("UI_LANGS_MISSING",
                $"'{file}' has its UI texts in {16 - missing.Count} of the 16 Power PDF languages; missing: {string.Join(", ", missing)}.",
                "Every add-on follows the Power PDF UI language. Add a STRINGTABLE (and translated dialogs/menus, if any) with a LANGUAGE block for each of: " +
                "en de fr it es nl pt da fi nb sv pl cs hu ru tr, and pick the block that matches the host language at run time.");
        var furtherMissing = PeResources.ExtendedLanguages.Where(l => !present.Contains(l.Primary)).Select(l => l.Code).ToList();
        if (missing.Count == 0 && furtherMissing.Count > 0)
            report.Error("UI_LANGS_EXTENDED",
                $"'{file}' has no UI texts in the further Power PDF languages: {string.Join(", ", furtherMissing)}.",
                "Power PDF also runs in Simplified and Traditional Chinese, Japanese, Korean and Arabic: add LANGUAGE blocks " +
                "LANG_CHINESE/SUBLANG_CHINESE_SIMPLIFIED, LANG_CHINESE/SUBLANG_CHINESE_TRADITIONAL, LANG_JAPANESE, LANG_KOREAN and LANG_ARABIC (right to left).");
        if (HasLanguages(strings))
        {
            int Count(int primary) => strings!.TryGetValue(primary, out var set) ? set.Count : 0;
            var en = Count(0x09);
            var partial = PeResources.PowerPdfLanguages.Where(l => l.Primary != 0x09 && Count(l.Primary) > 0 && Count(l.Primary) < en)
                .Select(l => l.Code).ToList();
            if (en > 0 && partial.Count > 0)
                report.Warn("UI_STRINGS_PARTIAL",
                    $"'{file}': the string tables of {string.Join(", ", partial)} have fewer blocks than English; some texts appear in English.",
                    "Translate every string of the English table into each language.");
        }
    }

    // ---- capabilities of the binary (S1.0.11) --------------------------------
    // An add-on runs inside Power PDF with the user's rights and sees confidential
    // documents. What it can do is read from its imports (normal and delay-load)
    // and from function or DLL names in its strings (GetProcAddress, LoadLibrary).
    private static readonly string[] NetworkDlls =
        { "winhttp.dll", "wininet.dll", "ws2_32.dll", "wsock32.dll", "urlmon.dll", "websocket.dll", "httpapi.dll", "mswsock.dll" };
    private static readonly string[] NetworkFunctions =
        { "WinHttpOpen", "InternetOpenA", "InternetOpenW", "WSAStartup", "HttpOpenRequestA", "HttpOpenRequestW", "URLOpenBlockingStreamA", "URLOpenBlockingStreamW" };
    private static readonly string[] InjectionFunctions =
        { "WriteProcessMemory", "CreateRemoteThread", "CreateRemoteThreadEx", "VirtualAllocEx", "NtWriteVirtualMemory", "ZwWriteVirtualMemory",
          "RtlCreateUserThread", "NtCreateThreadEx", "ZwCreateThreadEx", "NtQueueApcThread", "SetThreadContext" };
    private static readonly string[] DownloadFunctions =
        { "URLDownloadToFileA", "URLDownloadToFileW", "URLDownloadToCacheFileA", "URLDownloadToCacheFileW" };
    private static readonly string[] ProcessFunctions =
        { "CreateProcessA", "CreateProcessW", "CreateProcessAsUserA", "CreateProcessAsUserW", "CreateProcessWithLogonW",
          "CreateProcessWithTokenW", "WinExec", "ShellExecuteA", "ShellExecuteW", "ShellExecuteExA", "ShellExecuteExW" };
    private static readonly string[] ServiceFunctions =
        { "CreateServiceA", "CreateServiceW", "ChangeServiceConfigA", "ChangeServiceConfigW", "ChangeServiceConfig2A", "ChangeServiceConfig2W" };
    private static readonly string[] PersistenceStrings =
        { @"\currentversion\run", @"\currentversion\runonce", "schedule.service", "schtasks", @"\start menu\programs\startup",
          "image file execution options", "appinit_dlls", @"\currentversion\winlogon" };
    private static readonly Regex PlainHttpUrl = new(@"http://([A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,})", RegexOptions.Compiled);

    private static void CheckCapabilities(ValidationReport report, string file, byte[] bytes, JsonElement root)
    {
        var imp = PeImports.Read(bytes);
        var strings = new HashSet<string>(StringComparer.Ordinal);
        var lowerStrings = new List<string>();
        foreach (var s in ExtractStrings(bytes))
        {
            if (strings.Add(s)) lowerStrings.Add(s.ToLowerInvariant());
            if (strings.Count > 400_000) break;
        }
        // a function counts when it is imported or its exact name is a string (GetProcAddress)
        List<string> Uses(IEnumerable<string> names) =>
            names.Where(n => imp.Functions.Contains(n) || strings.Contains(n)).Distinct().ToList();

        var net = NetworkDlls.Where(d => imp.Dlls.Contains(d) || lowerStrings.Any(s => s == d || s.EndsWith("\\" + d))).ToList();
        net.AddRange(Uses(NetworkFunctions));
        int declaredServices = root.TryGetProperty("complianceAudit", out var audit) && audit.ValueKind == JsonValueKind.Object
                               && audit.TryGetProperty("externalServices", out var svc) && svc.ValueKind == JsonValueKind.Array
            ? svc.GetArrayLength() : 0;
        if (net.Count > 0 && declaredServices == 0)
            report.Error("NETWORK_UNDECLARED",
                $"'{file}' can use the network ({string.Join(", ", net.Distinct().Take(6))}), but complianceAudit.externalServices is empty.",
                "Declare every service the plug-in contacts (name, url, data sent) so admins can assess what may leave the machine; documents are confidential. If the network code is not needed, remove it (and the library that brings it).");

        var injection = Uses(InjectionFunctions);
        if (injection.Count > 0)
            report.Error("PROCESS_INJECTION", $"'{file}' can write into or start threads in other processes ({string.Join(", ", injection)}).",
                "A Power PDF add-on has no reason to touch other processes. Remove this code; the store does not accept it.");

        var download = Uses(DownloadFunctions);
        if (download.Count > 0)
            report.Error("RUNTIME_DOWNLOAD", $"'{file}' downloads files to disk with {string.Join(", ", download)}.",
                "Add-ons must not fetch and run code at run time: updates come only through the store, where they are checked. Fetch data from a declared service with WinHTTP and never execute or load what you download.");

        var processes = Uses(ProcessFunctions);
        if (processes.Count > 0)
            report.Warn("PROCESS_START", $"'{file}' can start programs or open files and links ({string.Join(", ", processes)}).",
                "Fine for opening a document, a mail or a web page the user asked for. Say in the compliance method text what is started and why; the reviewer checks it. Never start downloaded or temporary programs.");

        // Administrator rights (S1.11.0): ShellExecute with the "runas" verb starts a program elevated after a
        // UAC prompt. Allowed only when the manifest says why ("elevation": {"reason": ...}); the reviewer checks it.
        var shellExec = processes.Where(p => p.StartsWith("ShellExecute", StringComparison.Ordinal)).ToList();
        if (shellExec.Count > 0 && HasToken(bytes, "runas"))
        {
            var reason = root.TryGetProperty("elevation", out var el) && el.ValueKind == JsonValueKind.Object ? GetString(el, "reason") : null;
            if (reason is null || reason.Trim().Length < 20)
                report.Error("ELEVATION_UNDECLARED", $"'{file}' can start programs with administrator rights (ShellExecute with \"runas\"), but the manifest does not declare it.",
                    "Add \"elevation\": {\"reason\": \"what runs elevated, when and why\"} (20 to 500 characters) to the manifest, or remove the code. Add-ons normally need no administrator rights.");
            else
                report.Warn("ELEVATION_DECLARED", $"'{file}' can start programs with administrator rights: {reason.Trim()[..Math.Min(reason.Trim().Length, 500)]}",
                    "The reviewer checks what runs elevated and why; the user always sees the UAC prompt.");
        }
        var shells = new[] { "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe", "rundll32.exe", "regsvr32.exe" }
            .Where(sh => HasToken(bytes, sh)).ToList();
        if (processes.Count > 0 && shells.Count > 0)
            report.Warn("COMMAND_SHELL", $"'{file}' can start a command interpreter or script host ({string.Join(", ", shells)}).",
                "Start the program you need directly instead of a shell, never pass user or document text into a command line, and say in the compliance method text what is run and why.");

        var persistence = Uses(ServiceFunctions);
        persistence.AddRange(PersistenceStrings.Where(p => lowerStrings.Any(s => s.Contains(p))));
        if (persistence.Count > 0)
            report.Warn("PERSISTENCE", $"'{file}' may register itself to run outside Power PDF ({string.Join(", ", persistence.Distinct().Take(6))}).",
                "Add-ons run only while Power PDF runs: no autostart entries, services, scheduled tasks or system hooks. Remove this code or explain the finding to the reviewer.");

        var plainHttp = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in strings)
            foreach (Match m in PlainHttpUrl.Matches(s))
            {
                var host = m.Groups[1].Value.ToLowerInvariant();
                if (!IgnoredHostSuffixes.Any(i => host == i || host.EndsWith("." + i))) plainHttp.Add(host);
            }
        if (plainHttp.Count > 0)
            report.Warn("INSECURE_HTTP", $"'{file}' contains plain http:// addresses: {string.Join(", ", plainHttp.Take(10))}.",
                "Talk to services over HTTPS only, with certificate checks on. Plain HTTP exposes documents and credentials on the network. Documentation links can stay, say so in the compliance method text.");
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
    private static void CheckImports(ValidationReport report, string file, byte[] bytes, int peOffset,
                                     ISet<string>? bundled = null, bool isZxt = true)
    {
        var notDelayed = new List<string>();
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
            var maxDescriptors = (int)Math.Min(importDirSize / 20, 1024);
            for (int i = 0; i < maxDescriptors; i++)
            {
                var desc = Rva(importDirRva) + i * 20;
                if (desc < 0 || desc + 20 > bytes.Length) break;
                var nameRva = BitConverter.ToUInt32(bytes, (int)desc + 12);
                if (nameRva == 0) break;
                var nameOff = Rva(nameRva);
                if (nameOff < 0) break;
                var end = Array.IndexOf(bytes, (byte)0, (int)nameOff, (int)Math.Min(260, bytes.Length - nameOff));
                if (end < 0) break;
                var dll = System.Text.Encoding.ASCII.GetString(bytes, (int)nameOff, end - (int)nameOff);
                var lower = dll.ToLowerInvariant();

                if (lower.Contains("d.dll") &&
                    (lower.StartsWith("vcruntime") || lower.StartsWith("msvcp") || lower.StartsWith("ucrtbased") || lower.StartsWith("mfc")))
                    debugCrt = true;
                else if (bundled is not null && bundled.Contains(dll))
                {
                    // a DLL of the add-on's own bin/ (S1.4.0): the .zxt must delay-load it (Windows
                    // would look for it next to PowerPDF.exe at start); DLLs in bin/ find each other
                    if (isZxt) notDelayed.Add(dll);
                }
                else if (!KnownImportPrefixes.Any(p => lower.StartsWith(p)))
                    foreign.Add(dll);
            }

            if (debugCrt)
                report.Error("PE_DEBUG_RUNTIME", $"'{file}' imports a DEBUG C/C++ runtime.",
                    "Package the Release build; debug runtimes are not present on user machines (the .zxt must run with the same runtime as PowerPDF.exe).");
            if (foreign.Count > 0)
                report.Error("FOREIGN_DEPENDENCY",
                    $"'{file}' imports DLLs that are neither part of Windows or Power PDF nor in the package's bin/: {string.Join(", ", foreign.Distinct())}.",
                    "Ship your own DLLs in bin/ (declared in files.bin) and delay-load them with the store's loader header (see \"Additional DLLs\" in the guide), or link the libraries statically (MIT/BSD/Apache-2.0 only).");
            if (notDelayed.Count > 0)
                report.Error("BIN_IMPORT_NOT_DELAYED",
                    $"'{file}' imports DLLs of bin/ directly: {string.Join(", ", notDelayed.Distinct())}.",
                    "Windows looks for directly imported DLLs next to PowerPDF.exe, so Power PDF would fail to load the plug-in. Delay-load them (linker /DELAYLOAD:<name>.dll and delayimp.lib) and include pluginstore_bin.h from the developer kit, which loads them from Plug-Ins\\<Name>\\bin\\.");
        }
        catch
        {
            // import walking is best effort; a malformed table was already caught by the PE checks
        }
    }

    /// <summary>
    /// bin/ (S1.4.0): the add-on's own DLLs, installed to Plug-Ins\&lt;Name&gt;\bin\ and
    /// delay-loaded by the .zxt. DLLs only (no programs, no subfolders), each declared in
    /// files.bin, native x64 (Windows-on-ARM loads them through ARM64EC) and checked like
    /// the .zxt itself: PE format, runtime, imports, network and system access, hardening.
    /// </summary>
    private void CheckBin(ValidationReport report, ZipArchive zip, JsonElement root, ParsedManifest manifest, ISet<string> bundled)
    {
        var entries = zip.Entries.Where(e => e.FullName.Replace('\\', '/').StartsWith("bin/", StringComparison.OrdinalIgnoreCase) && e.Length > 0).ToList();
        var declared = new List<string>();
        var declaredOk = true;
        if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Object && files.TryGetProperty("bin", out var bin))
        {
            if (bin.ValueKind != JsonValueKind.Array || bin.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))
            {
                report.Error("BIN_INVALID", "'files.bin' must be an array of ZIP paths, e.g. [\"bin/MyPlugin_core.dll\"].",
                    "List every DLL of bin/ in files.bin, or leave files.bin out when the add-on has no DLLs of its own.");
                declaredOk = false;
            }
            else declared = bin.EnumerateArray().Select(x => (x.GetString() ?? "").Replace('\\', '/')).ToList();
        }
        if (entries.Count == 0 && declared.Count == 0) return;

        if (entries.Count > MaxBinFiles)
            report.Error("BIN_TOO_MANY", $"bin/ holds {entries.Count} files (at most {MaxBinFiles}).",
                "Combine libraries or link them statically; an add-on rarely needs more than a few DLLs.");
        foreach (var e in entries)
        {
            var path = e.FullName.Replace('\\', '/');
            if (!BinPath.IsMatch(path))
            {
                report.Error("BIN_FILE_INVALID", $"'{Printable(path)}' is not allowed in bin/.",
                    "bin/ holds DLLs only, directly in the folder, named with letters, digits, '.', '-' or '_' and ending in .dll. Programs (.exe), scripts and subfolders are not allowed.");
                continue;
            }
            if (declaredOk && !declared.Contains(path, StringComparer.OrdinalIgnoreCase))
                report.Error("BIN_UNDECLARED", $"'{path}' is not listed in files.bin.",
                    $"Add \"{path}\" to files.bin in the manifest.");
            var stem = Path.GetFileNameWithoutExtension(e.Name).ToLowerInvariant();
            if (IsReservedDllName(stem) || ReservedZxtNames.Contains(stem))
                report.Error("BIN_NAME_RESERVED", $"'{path}' has the name of a Windows, runtime or Power PDF library.",
                    "Give your DLL its own name, e.g. with your add-on as prefix (MyPlugin_core.dll); a second DLL with a system name in the same process breaks Power PDF or other add-ons.");
            var bytes = ReadCapped(e, MaxZxtBytes);
            if (bytes is null)
            {
                report.Error("ENTRY_TOO_LARGE", $"'{path}' inflates beyond the allowed size.",
                    "A single DLL may be at most 120 MB uncompressed.");
                continue;
            }
            // a name of the add-on's own (S1.4.3): <ZxtName>... never collides with other software in the process
            if (manifest.ZxtName.Length > 0 && !stem.StartsWith(manifest.ZxtName.ToLowerInvariant(), StringComparison.Ordinal))
                report.Warn("BIN_NAME_GENERIC", $"'{path}' does not start with the plug-in name '{manifest.ZxtName}'.",
                    $"Name your DLLs after the plug-in (e.g. bin/{manifest.ZxtName}_core.dll): all add-ons and Power PDF share one process, where one DLL name is one module.");
            CheckBinPe(report, path, bytes, bundled);
            CheckCapabilities(report, path, bytes, root);
            manifest.BinFiles.Add(e.Name);
        }
        foreach (var d in declared.Where(d => !entries.Any(e => string.Equals(e.FullName.Replace('\\', '/'), d, StringComparison.OrdinalIgnoreCase))))
            report.Error("BIN_MISSING", $"files.bin lists '{Printable(d)}', but the package does not contain it.",
                "Add the DLL at exactly that path, or remove it from files.bin.");
        if (manifest.BinFiles.Count > 0)
            report.Info("BIN_INCLUDED", $"The add-on brings {manifest.BinFiles.Count} DLL(s) of its own: {string.Join(", ", manifest.BinFiles.Take(10))}.",
                "Installed to Plug-Ins\\<Name>\\bin\\; clients from 1.4.0 on install it (older clients are offered the previous version).");
    }

    /// <summary>Names a DLL in bin/ must not use: Windows, the C/C++ runtimes, MFC and other system libraries.</summary>
    private static bool IsReservedDllName(string stem) =>
        KnownImportPrefixes.Any(p => !p.EndsWith('-') && stem == p) ||
        new[] { "api-ms-", "ext-ms-", "vcruntime", "msvcp", "msvcr", "ucrtbase", "concrt", "vccorlib", "mfcm", "vcomp",
                // widespread libraries (S1.4.3): another component of the process may load the same name
                "libcrypto", "libssl", "zlib", "sqlite3", "libcurl", "icu", "qt5", "qt6", "libxml2", "libiconv", "libpng", "libjpeg",
                "freetype", "pdfium", "libgcc", "libstdc", "libwinpthread", "onnxruntime", "opencv" }.Any(p => stem.StartsWith(p, StringComparison.Ordinal)) ||
        System.Text.RegularExpressions.Regex.IsMatch(stem, @"^mfc\d") ||
        stem is "advapi32" or "kernel32" or "user32" or "ntdll" or "comctl32" or "dbghelp" or "msvcrt" or "zlib1" or "libcrypto" or "libssl";

    private static void CheckBinPe(ValidationReport report, string file, byte[] bytes, ISet<string> bundled)
    {
        const ushort MachineX64 = 0x8664, DllFlag = 0x2000;
        if (bytes.Length < 0x40 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
        {
            report.Error("PE_INVALID", $"'{file}' is not a Windows PE binary.", "bin/ holds native Windows DLLs only.");
            return;
        }
        var peOffset = BitConverter.ToInt32(bytes, 0x3C);
        if (peOffset <= 0 || (long)peOffset + 24 > bytes.Length ||
            bytes[peOffset] != 'P' || bytes[peOffset + 1] != 'E' || bytes[peOffset + 2] != 0 || bytes[peOffset + 3] != 0)
        {
            report.Error("PE_INVALID", $"'{file}' has no valid PE header.", "Rebuild the DLL; the file seems truncated or corrupted.");
            return;
        }
        var machine = BitConverter.ToUInt16(bytes, peOffset + 4);
        if (machine != MachineX64)
            report.Error("PE_WRONG_MACHINE", $"'{file}' is built for machine 0x{machine:X4}, expected 0x8664 (x64).",
                "Ship x64 DLLs in bin/; Power PDF on Windows-on-ARM loads them through ARM64EC like the x64 .zxt.");
        if ((BitConverter.ToUInt16(bytes, peOffset + 22) & DllFlag) == 0)
            report.Error("BIN_FILE_INVALID", $"'{file}' is not a DLL.", "bin/ holds DLLs only; programs (.exe) are not allowed.");
        CheckImports(report, file, bytes, peOffset, bundled, isZxt: false);
        var pe = PeResources.Open(bytes);
        if (!pe.Valid) return;
        if (pe.IsManaged)
            report.Error("PE_MANAGED", $"'{file}' is a .NET assembly.", "bin/ holds native DLLs only.");
        const ushort DynamicBase = 0x40, NxCompat = 0x100;
        if ((pe.DllCharacteristics & DynamicBase) == 0 || (pe.DllCharacteristics & NxCompat) == 0)
            report.Warn("PE_HARDENING", $"'{file}' is built without ASLR (/DYNAMICBASE) or DEP (/NXCOMPAT).",
                "Keep the default linker options /DYNAMICBASE and /NXCOMPAT (and /HIGHENTROPYVA).");
    }

    private void CheckIcon(ValidationReport report, ZipArchive zip)
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
            var b = ReadCapped(icon, MaxIconBytes);
            if (b is null)
            {
                report.Warn("ICON_TOO_LARGE", "assets/icon.png inflates beyond 4 MB.", "Use a small square PNG (e.g. 128x128).");
                return;
            }
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

    /// <summary>Optional "screenshots": [{"file": "assets/x.png", "caption": {"en": ..}}], max 6 (S0.11.0).</summary>
    private void CheckScreenshots(ValidationReport report, ZipArchive zip, JsonElement root)
    {
        if (!root.TryGetProperty("screenshots", out var arr) || arr.ValueKind == JsonValueKind.Null)
        {
            report.Info("SCREENSHOTS_NONE", "The package has no screenshots.",
                "Add up to 6 screenshots (PNG or JPEG, 1280x800 recommended) under assets/ and list them in \"screenshots\"; the catalog shows them.");
            return;
        }
        if (arr.ValueKind != JsonValueKind.Array)
        {
            report.Error("SCREENSHOTS_INVALID", "\"screenshots\" must be an array.",
                "Use \"screenshots\": [{\"file\": \"assets/screenshot-1.png\", \"caption\": {\"en\": \"...\"}}].");
            return;
        }
        if (arr.GetArrayLength() > ScreenshotMax)
            report.Error("SCREENSHOTS_TOO_MANY", $"\"screenshots\" lists {arr.GetArrayLength()} entries.", $"List at most {ScreenshotMax} screenshots.");
        int n = 0;
        foreach (var s in arr.EnumerateArray())
        {
            n++;
            if (s.ValueKind != JsonValueKind.Object || GetString(s, "file") is not { Length: > 0 } file)
            {
                report.Error("SCREENSHOTS_INVALID", $"Screenshot {n} has no \"file\".",
                    "Each entry needs \"file\": the path of the image inside the package, e.g. assets/screenshot-1.png.");
                continue;
            }
            if (!file.StartsWith("assets/", StringComparison.Ordinal) || file.Contains(".."))
            {
                report.Error("SCREENSHOT_FORMAT", $"Screenshot '{file}' is not under assets/.", "Put screenshots into the package's assets/ folder.");
                continue;
            }
            var entry = zip.GetEntry(file);
            if (entry is null)
            {
                report.Error("SCREENSHOT_MISSING", $"Screenshot '{file}' is not in the package.", "Add the file to the ZIP or remove it from \"screenshots\".");
                continue;
            }
            var b = ReadCapped(entry, ScreenshotMaxBytes);
            if (b is null)
            {
                report.Error("SCREENSHOT_TOO_LARGE", $"Screenshot '{file}' is larger than 3 MB.", "Export it smaller (1280x800 PNG or JPEG quality 85).");
                continue;
            }
            var type = Services.ScreenshotService.ImageType(b);
            if (type is null)
            {
                report.Error("SCREENSHOT_FORMAT", $"Screenshot '{file}' is neither PNG nor JPEG.", "Export the screenshot as PNG or JPEG.");
                continue;
            }
            if (type == "image/png")
            {
                int w = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
                if (w < 640 || w > 3840)
                    report.Warn("SCREENSHOT_SIZE", $"Screenshot '{file}' is {w}px wide.", "Use 640 to 3840 px width; 1280x800 is recommended.");
            }
            if (s.TryGetProperty("caption", out var cap) && cap.ValueKind == JsonValueKind.Object)
            {
                var missing = MissingLanguages(s, "caption");
                if (missing.Count > 0)
                    report.Error("SCREENSHOT_CAPTION_LANGS", $"The caption of '{file}' lacks: {string.Join(", ", missing)}.",
                        "Give each caption in all 16 languages (en de fr it es nl pt da fi nb sv pl cs hu ru tr).");
                var extended = MissingExtended(s, "caption");
                if (extended.Count > 0)
                    report.Error("LANG_TEXT_EXTENDED", $"The caption of '{file}' lacks the further Power PDF languages: {string.Join(", ", extended)}.",
                        "Add the captions in zh-Hans, zh-Hant, ja, ko and ar as well.");
            }
        }
    }

    private const int ScreenshotMax = 6;
    private const long ScreenshotMaxBytes = 3 * 1024 * 1024;

    private void CheckLicenses(ValidationReport report, ZipArchive zip)
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
            var raw = ReadCapped(lic, MaxTextEntryBytes);
            if (raw is null) return;
            var text = System.Text.Encoding.UTF8.GetString(raw);
            if (Regex.IsMatch(text, @"\b(A?GPL|GNU (Affero )?General Public License|LGPL)\b", RegexOptions.IgnoreCase))
                report.Warn("LICENSE_GPL_MARKER", "LICENSES.md mentions a GPL-family license.",
                    "Only MIT/BSD/Apache-2.0 dependencies are allowed in shipped plugins (standing team policy). Replace the dependency or clarify the mention.");
        }
        catch { /* unreadable file was caught by the ZIP checks */ }
    }

    // Standing team policy: shipped plugins contain only MIT / BSD / Apache-2.0
    // third-party code. Other permissive licenses go to the reviewer; copyleft fails.
    private static readonly HashSet<string> AllowedLicenses = new(StringComparer.OrdinalIgnoreCase)
    {
        "MIT", "BSD-2-Clause", "BSD-3-Clause", "0BSD", "Apache-2.0"
    };
    private static readonly Regex CopyleftLicense = new(
        @"(^|[^A-Za-z])(A?GPL|LGPL|GPL|MPL|EPL|CDDL|EUPL|OSL|SSPL|CC-BY-SA|CC-BY-NC)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private enum LibKind { Copyleft, WeakCopyleft, Permissive }

    /// <summary>
    /// Signatures of well-known libraries as they appear in compiled binaries
    /// (version banners, copyright strings, MSVC RTTI names). Aliases are the
    /// names accepted in the manifest's thirdParty list or in LICENSES.md.
    /// </summary>
    private static readonly (string Name, LibKind Kind, string[] Signatures, string[] Aliases)[] KnownLibraries =
    {
        ("MuPDF / Ghostscript (AGPL)", LibKind.Copyleft, new[] { "Artifex Software", "MuPDF", "Ghostscript" }, new[] { "mupdf", "ghostscript" }),
        ("Poppler / Xpdf (GPL)",       LibKind.Copyleft, new[] { "Poppler", "Glyph & Cog" }, new[] { "poppler", "xpdf" }),
        ("FFmpeg (LGPL/GPL)",          LibKind.WeakCopyleft, new[] { "FFmpeg", "libavcodec" }, new[] { "ffmpeg" }),
        ("UnRAR (restricted license)", LibKind.WeakCopyleft, new[] { "UnRAR" }, new[] { "unrar" }),
        ("zlib",          LibKind.Permissive, new[] { "Jean-loup Gailly", "Mark Adler" }, new[] { "zlib" }),
        ("libpng",        LibKind.Permissive, new[] { "libpng version" }, new[] { "libpng" }),
        ("libjpeg",       LibKind.Permissive, new[] { "Independent JPEG Group", "libjpeg-turbo" }, new[] { "libjpeg", "ijg" }),
        ("OpenSSL",       LibKind.Permissive, new[] { "OpenSSL 1.", "OpenSSL 3." }, new[] { "openssl" }),
        ("curl",          LibKind.Permissive, new[] { "libcurl/" }, new[] { "curl" }),
        ("SQLite",        LibKind.Permissive, new[] { "SQLite format 3" }, new[] { "sqlite" }),
        ("nlohmann/json", LibKind.Permissive, new[] { "[json.exception." }, new[] { "nlohmann", "json for modern c++" }),
        ("FreeType",      LibKind.Permissive, new[] { "FreeType" }, new[] { "freetype" }),
        ("libtiff",       LibKind.Permissive, new[] { "LIBTIFF, Version" }, new[] { "libtiff" }),
        ("OpenJPEG",      LibKind.Permissive, new[] { "OpenJPEG" }, new[] { "openjpeg" }),
        ("Leptonica",     LibKind.Permissive, new[] { "leptonica-" }, new[] { "leptonica" }),
        ("Tesseract",     LibKind.Permissive, new[] { "@tesseract@@", "Tesseract Open Source OCR Engine" }, new[] { "tesseract" }),
        ("PDFium",        LibKind.Permissive, new[] { "PDFium" }, new[] { "pdfium" }),
        ("HarfBuzz",      LibKind.Permissive, new[] { "HarfBuzz" }, new[] { "harfbuzz" }),
        ("Expat",         LibKind.Permissive, new[] { "expat_2." }, new[] { "expat" }),
        ("Lua",           LibKind.Permissive, new[] { "$LuaVersion: " }, new[] { "lua" }),
        ("protobuf",      LibKind.Permissive, new[] { "google/protobuf/" }, new[] { "protobuf" }),
    };

    private static bool Contains(byte[] haystack, string needle) =>
        haystack.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(needle)) >= 0 ||
        haystack.AsSpan().IndexOf(System.Text.Encoding.Unicode.GetBytes(needle)) >= 0;

    /// <summary>
    /// Third-party code: the publisher (usually their Claude session) declares
    /// every component in manifest.thirdParty after a license audit, and the
    /// server cross-checks binaries and bundled files against that declaration.
    /// </summary>
    private void CheckThirdParty(ValidationReport report, ZipArchive zip, JsonElement root)
    {
        var declared = new List<string>();
        var declaredHosts = new List<string>();
        if (!root.TryGetProperty("complianceAudit", out var audit) || audit.ValueKind != JsonValueKind.Object
            || !audit.TryGetProperty("confirmed", out var conf) || conf.ValueKind != JsonValueKind.True
            || string.IsNullOrWhiteSpace(GetString(audit, "method")))
            report.Error("COMPLIANCE_AUDIT_MISSING", "The manifest has no confirmed compliance audit.",
                "Before every upload, check the plugin as described in the agent guide (third-party code and licenses, trademarks, assets, secrets, personal data, external services) and confirm truthfully: \"complianceAudit\": {\"confirmed\": true, \"method\": \"what you checked\", \"externalServices\": []}. Never confirm without checking; if you find a problem you cannot fix, declare it and let the check fail.");
        else
            report.Info("COMPLIANCE_AUDIT_CONFIRMED", $"Compliance audit confirmed by the uploader: {GetString(audit, "method")}");

        if (audit.ValueKind == JsonValueKind.Object)
        {
            if (!audit.TryGetProperty("externalServices", out var svc) || svc.ValueKind != JsonValueKind.Array)
                report.Error("EXTERNAL_SERVICES_MISSING", "'complianceAudit.externalServices' is missing.",
                    "List every server the plugin contacts at runtime with the data it sends, e.g. [{\"name\": \"Printix Cloud Print API\", \"url\": \"https://api.printix.net\", \"data\": \"print job, user email\"}]. Use [] when the plugin works fully offline.");
            else
                foreach (var s in svc.EnumerateArray())
                {
                    var url = s.ValueKind == JsonValueKind.Object ? GetString(s, "url") : null;
                    if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out var u)) declaredHosts.Add(u.Host);
                    else if (url is not null) declaredHosts.Add(url);
                }
        }

        if (!root.TryGetProperty("thirdParty", out var tp))
        {
            report.Error("THIRDPARTY_DECLARATION_MISSING", "The manifest has no 'thirdParty' list.",
                "Audit the plugin for third-party code (libraries, header-only code, bundled DLLs, copied snippets, fonts, icons) and declare it: \"thirdParty\": [{\"name\": \"nlohmann/json\", \"version\": \"3.12.0\", \"license\": \"MIT\"}]. Use [] when the plugin contains none.");
        }
        else if (tp.ValueKind != JsonValueKind.Array)
        {
            report.Error("THIRDPARTY_INVALID", "'thirdParty' must be an array.",
                "Use \"thirdParty\": [] or a list of {name, version, license} objects.");
        }
        else
        {
            foreach (var item in tp.EnumerateArray())
            {
                var name = item.ValueKind == JsonValueKind.Object ? GetString(item, "name") : null;
                var license = item.ValueKind == JsonValueKind.Object ? GetString(item, "license") : null;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(license))
                {
                    report.Error("THIRDPARTY_INVALID", "Every 'thirdParty' entry needs 'name' and 'license'.",
                        "Example: {\"name\": \"zlib\", \"version\": \"1.3.1\", \"license\": \"Zlib\"}; 'license' is an SPDX identifier.");
                    continue;
                }
                declared.Add(name);
                var parts = license.Split(new[] { " OR ", " or " }, StringSplitOptions.TrimEntries);
                if (parts.Any(p => AllowedLicenses.Contains(p)))
                    continue;
                if (CopyleftLicense.IsMatch(license))
                    report.Error("LICENSE_NOT_ALLOWED", $"'{name}' is licensed under {license}.",
                        "Shipped plugins may contain only MIT, BSD or Apache-2.0 third-party code. Replace or remove the component; copyleft licenses (GPL, AGPL, LGPL, MPL, EPL, CC-BY-SA ...) are not accepted.");
                else
                    report.Warn("LICENSE_NEEDS_REVIEW", $"'{name}' is licensed under {license}, outside the standard list (MIT, BSD, Apache-2.0).",
                        "Permissive licenses such as Zlib, ISC or BSL-1.0 can be acceptable after review; prefer an MIT/BSD/Apache-2.0 alternative, otherwise explain the choice in LICENSES.md.");
            }
        }

        string licensesText = "";
        var licEntry = zip.GetEntry("LICENSES.md");
        if (licEntry is not null)
        {
            var raw = ReadCapped(licEntry, MaxTextEntryBytes);
            if (raw is not null) licensesText = System.Text.Encoding.UTF8.GetString(raw);
        }
        bool IsDeclared(string[] aliases) =>
            aliases.Any(a => declared.Any(d => d.Contains(a, StringComparison.OrdinalIgnoreCase))
                          || licensesText.Contains(a, StringComparison.OrdinalIgnoreCase));

        var hits = new Dictionary<string, (LibKind Kind, string[] Aliases, SortedSet<string> Files)>();
        void Hit(string name, LibKind kind, string[] aliases, string file)
        {
            if (!hits.TryGetValue(name, out var h)) hits[name] = h = (kind, aliases, new SortedSet<string>());
            h.Files.Add(file);
        }

        var secrets = new SortedSet<string>();
        var hosts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
            if (ext is ".pfx" or ".p12" or ".key" or ".snk")
            {
                secrets.Add($"{entry.FullName} (key or certificate container)");
                continue;
            }
            // classify by content as well: an executable or an archive with a harmless extension
            // must not slip past the scans
            var magic = Head(entry);
            var isOfficeDoc = ext is ".docx" or ".xlsx" or ".pptx" or ".odt" or ".ods" or ".odp" or ".epub";
            if (IsArchiveMagic(magic) && !isOfficeDoc && !entry.FullName.StartsWith("installer/", StringComparison.OrdinalIgnoreCase))
            {
                report.Error("NESTED_ARCHIVE", $"'{entry.FullName}' is an archive inside the package.",
                    "Ship files unpacked; archives inside the package cannot be checked and are never installed.");
                continue;
            }
            var isBinary = ext is ".zxt" or ".dll" or ".exe" or ".ocx" or ".sys" || (magic.Length >= 2 && magic[0] == 'M' && magic[1] == 'Z');
            var isText = !isBinary && (ext is ".txt" or ".md" or ".rtf" or ".htm" or ".html" or ".json" or ".xml" or ".ini" or ".cfg"
                                        or ".config" or ".reg" or ".ps1" or ".cmd" or ".bat" or ".js" or ".pem" or ".cer" or ".crt"
                                        or ".env" or ".yml" or ".yaml" or ".toml" or ".properties" or ".csv" or ".vbs" or ".py"
                                        || ext.Length == 0
                                        || entry.Name.StartsWith("COPYING", StringComparison.OrdinalIgnoreCase)
                                        || entry.Name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase));
            if (!isBinary && !isText) continue;

            var bytes = ReadCapped(entry, isBinary ? MaxZxtBytes : MaxTextEntryBytes);
            if (bytes is null)
            {
                if (isBinary)
                    report.Error("ENTRY_NOT_SCANNED", $"'{entry.FullName}' is too large to be checked.",
                        "Binaries may be at most 120 MB uncompressed; large payloads must be fetched at install time.");
                else
                    report.Warn("ENTRY_NOT_SCANNED", $"'{entry.FullName}' is larger than 1 MB and was not scanned for secrets.",
                        "Keep text and configuration files small, or make sure they hold no keys or passwords.");
                continue;
            }

            foreach (var s in ExtractStrings(bytes))
            {
                foreach (var (label, rx) in SecretPatterns)
                    if (rx.IsMatch(s)) secrets.Add($"{label} in {entry.FullName}");
                if (!isBinary) continue;
                foreach (Match m in UrlHost.Matches(s))
                {
                    var host = m.Groups[1].Value.TrimEnd('.').ToLowerInvariant();
                    if (!IgnoredHostSuffixes.Any(i => host == i || host.EndsWith("." + i))) hosts.Add(host);
                }
                foreach (var known in KnownServiceHosts)
                    if (s.Contains(known, StringComparison.OrdinalIgnoreCase)) hosts.Add(known);
            }

            if (entry.FullName.Equals("LICENSES.md", StringComparison.OrdinalIgnoreCase)) continue;
            if (isText && !(ext is ".txt" or ".md" or ".rtf" or ".htm" or ".html"
                            || entry.Name.StartsWith("COPYING", StringComparison.OrdinalIgnoreCase)
                            || entry.Name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase)))
                continue;

            if (isText)
            {
                if (Contains(bytes, "GNU LESSER GENERAL PUBLIC LICENSE") || Contains(bytes, "GNU LIBRARY GENERAL PUBLIC LICENSE"))
                    Hit("LGPL license text", LibKind.WeakCopyleft, Array.Empty<string>(), entry.FullName);
                else if (Contains(bytes, "GNU GENERAL PUBLIC LICENSE") || Contains(bytes, "GNU AFFERO GENERAL PUBLIC LICENSE"))
                    Hit("GPL/AGPL license text", LibKind.Copyleft, Array.Empty<string>(), entry.FullName);
                continue;
            }

            if (Contains(bytes, "GNU Lesser General Public License") || Contains(bytes, "GNU Library General Public License")
                || Contains(bytes, "SPDX-License-Identifier: LGPL"))
                Hit("LGPL-licensed code", LibKind.WeakCopyleft, Array.Empty<string>(), entry.FullName);
            else if (Contains(bytes, "GNU General Public License") || Contains(bytes, "GNU Affero General Public License")
                || Contains(bytes, "SPDX-License-Identifier: GPL") || Contains(bytes, "SPDX-License-Identifier: AGPL"))
                Hit("GPL/AGPL-licensed code", LibKind.Copyleft, Array.Empty<string>(), entry.FullName);

            foreach (var lib in KnownLibraries)
                if (lib.Signatures.Any(s => Contains(bytes, s)))
                    Hit(lib.Name, lib.Kind, lib.Aliases, entry.FullName);
        }

        foreach (var (name, h) in hits)
        {
            var where = string.Join(", ", h.Files);
            switch (h.Kind)
            {
                case LibKind.Copyleft:
                    report.Error("LICENSE_COPYLEFT_BINARY", $"{name} found in {where}.",
                        "GPL/AGPL code must not ship in a store plugin. Remove the component or replace it with an MIT/BSD/Apache-2.0 alternative. If this is a false positive (e.g. the string only appears in a comment or a compatibility check), explain it to the reviewer.");
                    break;
                case LibKind.WeakCopyleft:
                    report.Warn("LICENSE_WEAK_COPYLEFT_BINARY", $"{name} found in {where}.",
                        "LGPL or restricted-license code needs a legal review and usually cannot ship. Replace it with an MIT/BSD/Apache-2.0 alternative where possible.");
                    break;
                default:
                    if (IsDeclared(h.Aliases))
                        report.Info("THIRDPARTY_DETECTED", $"{name} detected in {where} (declared).");
                    else
                        report.Warn("THIRDPARTY_UNDECLARED", $"{name} appears to be compiled into {where} but is not declared.",
                            $"Add {name} to manifest.thirdParty with its SPDX license and include its license text in LICENSES.md. If the plugin does not contain it, explain the false positive to the reviewer.");
                    break;
            }
        }

        if (secrets.Count > 0)
            report.Error("SECRET_DETECTED", $"Possible credentials in the package: {string.Join("; ", secrets.Take(10))}.",
                "Never ship keys, tokens, passwords or certificates with private keys. Remove them, rotate the exposed secret, and load credentials at runtime (e.g. per-user, DPAPI-protected).");

        var undeclared = hosts.Where(h => !declaredHosts.Any(d => h.Equals(d, StringComparison.OrdinalIgnoreCase)
                                                                || h.EndsWith("." + d, StringComparison.OrdinalIgnoreCase))).ToList();
        if (undeclared.Count > 0)
            report.Warn("EXTERNAL_SERVICE_UNDECLARED", $"The binaries reference hosts that are not declared: {string.Join(", ", undeclared.Take(15))}.",
                "Declare every service the plugin contacts in complianceAudit.externalServices (name, url, data sent), so admins can assess data protection. If a host is only a documentation link or an XML namespace, say so in the method text.");

        var texts = new List<string>();
        foreach (var key in new[] { "name", "description" })
            if (root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Object)
                foreach (var p in el.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String) texts.Add(p.Value.GetString() ?? "");
        var brands = ForeignBrands.Where(b => texts.Any(t => Regex.IsMatch(t, $@"\b{Regex.Escape(b)}\b", RegexOptions.IgnoreCase))).ToList();
        if (brands.Count > 0)
            report.Warn("THIRDPARTY_TRADEMARK", $"Name or description mentions third-party brands: {string.Join(", ", brands)}.",
                "Do not use other companies' product names or trademarks in plugin names and catalog texts; describe the function instead.");
    }

    internal static readonly (string Label, Regex Rx)[] SecretPatterns =
    {
        ("private key", new Regex(@"-----BEGIN (RSA |EC |DSA |OPENSSH |ENCRYPTED )?PRIVATE KEY-----", RegexOptions.Compiled)),
        ("store API token", new Regex(@"ppak_[A-Za-z0-9_\-]{20,}", RegexOptions.Compiled)),
        ("AWS access key", new Regex(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.Compiled)),
        ("Google API key", new Regex(@"\bAIza[0-9A-Za-z_\-]{35}\b", RegexOptions.Compiled)),
        ("GitHub token", new Regex(@"\bgh[pousr]_[A-Za-z0-9]{36,}\b", RegexOptions.Compiled)),
        ("Slack token", new Regex(@"\bxox[abprs]-[A-Za-z0-9\-]{10,}", RegexOptions.Compiled)),
        ("Azure storage key", new Regex(@"AccountKey=[A-Za-z0-9+/=]{40,}", RegexOptions.Compiled)),
        ("AI API key", new Regex(@"\bsk-(ant-|proj-)?[A-Za-z0-9_\-]{32,}", RegexOptions.Compiled)),
        ("Stripe key", new Regex(@"\b[sr]k_live_[A-Za-z0-9]{20,}", RegexOptions.Compiled)),
    };

    private static readonly Regex UrlHost = new(@"https?://([A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,})", RegexOptions.Compiled);

    // XML namespaces, code-signing CRL/OCSP endpoints, license links and our own
    // product domains appear in many binaries without any network traffic.
    private static readonly string[] IgnoredHostSuffixes =
    {
        "microsoft.com", "windows.com", "w3.org", "openxmlformats.org", "purl.org", "xml.org", "json-schema.org",
        "ns.adobe.com", "iptc.org", "digicert.com", "verisign.com", "symantec.com", "thawte.com", "globalsign.com",
        "globalsign.net", "sectigo.com", "comodoca.com", "usertrust.com", "entrust.net", "letsencrypt.org",
        "apache.org", "opensource.org", "gnu.org", "spdx.org", "aiim.org", "npes.org", "etsi.org", "oasis-open.org", "xmlsoap.org", "unece.org", "localhost", "example.com", "example.org", "example.invalid",
        "tungstenautomation.com", "kofax.com", "nuance.com",
    };

    // Cloud and AI APIs that code often addresses by bare host name (WinHttpConnect),
    // so they never show up as a URL. Found anywhere in a binary, they count as used.
    private static readonly string[] KnownServiceHosts =
    {
        "api.openai.com", "openai.azure.com", "generativelanguage.googleapis.com", "aiplatform.googleapis.com",
        "api.anthropic.com", "api.mistral.ai", "api.cohere.ai", "api.deepl.com", "api-free.deepl.com",
        "graph.microsoft.com", "login.microsoftonline.com", "api.printix.net", "auth.printix.net",
        "api.dropboxapi.com", "www.googleapis.com", "api.box.com",
    };

    internal static readonly string[] ForeignBrands =
    {
        "Adobe", "Acrobat", "Foxit", "Nitro", "ABBYY", "Bluebeam", "PDF-XChange", "Smallpdf", "iLovePDF", "Wondershare", "PDFelement",
    };

    /// <summary>
    /// Is a short word (shorter than the 8 characters <see cref="ExtractStrings"/> needs, e.g. "runas", "cmd.exe")
    /// in the binary as ASCII or UTF-16LE, at any offset, ignoring ASCII case? Not preceded or followed by a letter
    /// or digit, so "runas" does not match inside "rerunasync". (S1.11.0)
    /// </summary>
    internal static bool HasToken(byte[] b, string token)
    {
        static byte Low(byte c) => c >= (byte)'A' && c <= (byte)'Z' ? (byte)(c + 32) : c;
        static bool Word(byte c) => (c >= (byte)'a' && c <= (byte)'z') || (c >= (byte)'A' && c <= (byte)'Z') || (c >= (byte)'0' && c <= (byte)'9');
        var t = System.Text.Encoding.ASCII.GetBytes(token.ToLowerInvariant());
        for (int step = 1; step <= 2; step++)          // 1 = ASCII, 2 = UTF-16LE (high bytes zero)
        {
            int len = t.Length * step;
            for (int i = 0; i + len <= b.Length; i++)
            {
                if (Low(b[i]) != t[0]) continue;
                bool ok = true;
                for (int k = 0; k < t.Length && ok; k++)
                    ok = Low(b[i + k * step]) == t[k] && (step == 1 || b[i + k * step + 1] == 0);
                if (!ok) continue;
                bool before = i >= step && Word(b[i - step]) && (step == 1 || b[i - 1] == 0);
                bool after = i + len + step <= b.Length && Word(b[i + len]) && (step == 1 || b[i + len + 1] == 0);
                if (!before && !after) return true;
            }
        }
        return false;
    }

    /// <summary>Printable ASCII and UTF-16LE runs of at least 8 characters, like `strings`.</summary>
    internal static IEnumerable<string> ExtractStrings(byte[] b)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < b.Length; i++)
        {
            var c = b[i];
            if (c >= 0x20 && c < 0x7F) { sb.Append((char)c); continue; }
            if (sb.Length >= 8) yield return sb.ToString();
            sb.Clear();
        }
        if (sb.Length >= 8) yield return sb.ToString();
        sb.Clear();
        for (int i = 0; i + 1 < b.Length; i += 2)
        {
            var c = b[i];
            if (b[i + 1] == 0 && c >= 0x20 && c < 0x7F) { sb.Append((char)c); continue; }
            if (sb.Length >= 8) yield return sb.ToString();
            sb.Clear();
        }
        if (sb.Length >= 8) yield return sb.ToString();
        sb.Clear();
        for (int i = 1; i + 1 < b.Length; i += 2)
        {
            var c = b[i];
            if (b[i + 1] == 0 && c >= 0x20 && c < 0x7F) { sb.Append((char)c); continue; }
            if (sb.Length >= 8) yield return sb.ToString();
            sb.Clear();
        }
        if (sb.Length >= 8) yield return sb.ToString();
    }

    /// <summary>
    /// Layout checks distilled from the collected SDK pitfalls: the shared tab
    /// rule, the host-owned panel:: namespace, the IconMode=1 trap, and
    /// NameAndTitle language folders that drift apart after a fork/rename.
    /// </summary>
    /// <summary>First bytes of an entry (content sniffing).</summary>
    private static byte[] Head(ZipArchiveEntry entry)
    {
        if (entry.Length == 0) return Array.Empty<byte>();
        try
        {
            using var s = entry.Open();
            var buf = new byte[8];
            int n = 0, got;
            while (n < buf.Length && (got = s.Read(buf, n, buf.Length - n)) > 0) n += got;
            return buf[..n];
        }
        catch (InvalidDataException) { return Array.Empty<byte>(); }
    }

    private static bool IsArchiveMagic(byte[] m) =>
        (m.Length >= 4 && m[0] == 'P' && m[1] == 'K' && (m[2] == 3 || m[2] == 5 || m[2] == 7)) ||        // ZIP
        (m.Length >= 6 && m[0] == '7' && m[1] == 'z' && m[2] == 0xBC && m[3] == 0xAF) ||                   // 7-Zip
        (m.Length >= 4 && m[0] == 'R' && m[1] == 'a' && m[2] == 'r' && m[3] == '!') ||                     // RAR
        (m.Length >= 3 && m[0] == 0x1F && m[1] == 0x8B) ||                                                  // gzip
        (m.Length >= 4 && m[0] == 'M' && m[1] == 'S' && m[2] == 'C' && m[3] == 'F');                        // CAB

    /// <summary>HTML help is installed on user machines: no scripts, no external resources.</summary>
    private void CheckDocs(ValidationReport report, ZipArchive zip)
    {
        var bad = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("docs/", StringComparison.OrdinalIgnoreCase)
                                                && (e.FullName.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".html", StringComparison.OrdinalIgnoreCase))))
        {
            var raw = ReadCapped(e, MaxTextEntryBytes);
            if (raw is null) continue;
            var html = System.Text.Encoding.UTF8.GetString(raw);
            if (Regex.IsMatch(html, @"<script\b|\bon[a-z]+\s*=|javascript:", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(html, @"<(?:iframe|object|embed)\b", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(html, @"\b(?:src|href)\s*=\s*[""']?\s*(?:https?:)?//", RegexOptions.IgnoreCase) && Regex.IsMatch(html, @"<(?:img|script|link|iframe)\b[^>]{0,2000}\b(?:src|href)\s*=\s*[""']?\s*(?:https?:)?//", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking))
                bad.Add(e.FullName);
        }
        if (bad.Count > 0)
            report.Warn("DOCS_ACTIVE_CONTENT", $"Help pages contain scripts or load external resources: {string.Join(", ", bad.Take(5))}.",
                "Help under docs/ is installed on user machines and opened locally: plain HTML with local images only, no scripts, frames or external sources.");
    }

    /// <summary>"ui": "none" (S1.1.1): no ribbon layout may be shipped, it would contradict the declaration.</summary>
    private static void CheckNoUiLayout(ValidationReport report, ZipArchive zip)
    {
        var layout = zip.Entries.Where(e => e.FullName.Replace('\\', '/').StartsWith("UILayout/", StringComparison.OrdinalIgnoreCase) && !e.FullName.EndsWith('/'))
                                .Select(e => e.FullName).Take(3).ToList();
        if (layout.Count > 0)
            report.Error("UI_NONE_HAS_LAYOUT", $"The add-on declares \"ui\": \"none\" but ships a ribbon layout ({string.Join(", ", layout)}).",
                "Remove the UILayout folder, or set \"ui\": \"ribbon\" when the add-on has buttons.");
    }

    private void CheckUiLayout(ValidationReport report, ZipArchive zip, string atomNamespace, string packageId = "", bool ownTab = false)
    {
        var layoutEntries = zip.Entries
            .Where(e => e.FullName.Replace('\\', '/').StartsWith("UILayout/", StringComparison.OrdinalIgnoreCase) && !e.FullName.EndsWith('/'))
            .ToList();
        if (layoutEntries.Count == 0)
        {
            // A missing layout is almost always a packaging mistake (S1.2.1): an add-on without
            // ribbon buttons confirms it explicitly with "ui": "none".
            report.Error("UILAYOUT_MISSING", "The package has no UILayout folder.",
                "Ship UILayout/Publish Mode.xml and NameAndTitle.xml (root and the 16 language folders) so the ribbon group appears in every language. " +
                "If the add-on intentionally has no ribbon buttons, confirm it with \"ui\": \"none\" in manifest.json.");
            return;
        }

        string ReadEntry(ZipArchiveEntry e)
        {
            var raw = ReadCapped(e, MaxTextEntryBytes);
            return raw is null ? "" : System.Text.Encoding.UTF8.GetString(raw);
        }

        foreach (var publish in layoutEntries.Where(e => e.FullName.EndsWith("Publish Mode.xml", StringComparison.OrdinalIgnoreCase)))
        {
            var xml = ReadEntry(publish);
            if (Regex.IsMatch(xml, "name=\"panel::", RegexOptions.IgnoreCase))
                report.Error("RESERVED_PANEL_NS", "Publish Mode.xml declares atoms in the host-owned 'panel::' namespace.",
                    "'panel::' belongs to Power PDF itself; use your own atom namespace for panels.");
            if (Regex.IsMatch(xml, "IconMode=\"1\""))
                report.Warn("ICONMODE_SMALL", "Publish Mode.xml uses IconMode=\"1\" (large button with a SMALL icon).",
                    "Use IconMode=\"4\" for product-sized buttons; 1 renders a large button with a small icon once merged.");
            foreach (Match tb in Regex.Matches(xml, @"<toolbar\b[^>]{0,2000}?\bname\s*=\s*[""']([^""']{1,200})[""']", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)))
            {
                // the store client has its own tab ("AddonStore", C1.1.0); a private add-on may have
                // its own tab, named like its namespace (S1.0.6; public/private is decided against the catalog)
                var name = tb.Groups[1].Value;
                if (name == "FeaturePack" || (packageId == "com.tungsten.pluginstore" && name == "AddonStore")) continue;
                if (ownTab && name == TabOf(atomNamespace)) continue;
                report.Error(ownTab ? "OWN_TAB_NAME" : "ATOM_NOT_SHARED_TAB",
                    ownTab ? $"{publish.FullName} declares the ribbon tab '{name}', but ribbonAtomNamespace is '{atomNamespace}'."
                           : $"{publish.FullName} creates its own ribbon tab '{name}', but ribbonAtomNamespace '{atomNamespace}' is on the shared tab.",
                    ownTab ? $"An own tab must carry the atom of the namespace: <toolbar name=\"{TabOf(atomNamespace)}\">, groups and buttons below '{atomNamespace}::'."
                           : "Public store plugins share ONE tab: toolbar atom 'FeaturePack' with the localized title 'Enhanced Features'/'Erweiterte Funktionen'. Only private customer add-ons may have their own tab.");
                break;
            }
        }

        // every atom the layout declares lives in the plug-in's own namespace (or is the shared tab)
        if (atomNamespace.Length > 0 && atomNamespace != "FeaturePack")
        {
            var foreign = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var e in layoutEntries.Where(e => e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
                foreach (Match m in Regex.Matches(ReadEntry(e), @"\bname\s*=\s*[""']([^""']+)[""']"))
                {
                    var atom = m.Groups[1].Value;
                    if (atom == "FeaturePack" || atom == atomNamespace || atom.StartsWith(atomNamespace + "::", StringComparison.Ordinal)) continue;
                    if (!atom.Contains("::")) continue;   // element names such as layout modes, not atoms
                    foreign.Add(atom);
                }
            if (foreign.Count > 0)
                report.Error("ATOM_OUTSIDE_NAMESPACE", $"The layout declares atoms outside '{atomNamespace}': {string.Join(", ", foreign.Take(6))}.",
                    "Every group and button atom must start with the declared ribbonAtomNamespace; atoms of other plug-ins would collide.");
        }

        // NameAndTitle consistency across language folders (classic fork/rename trap)
        var baseNat = layoutEntries.FirstOrDefault(e =>
            Regex.IsMatch(e.FullName.Replace('\\', '/'), @"^UILayout/NameAndTitle\.xml$", RegexOptions.IgnoreCase));
        if (baseNat is null)
            report.Error("LANGS_INCOMPLETE", "UILayout/NameAndTitle.xml is missing.",
                "Ship NameAndTitle.xml at UILayout/ and in all 16 language folders (ENU DEU FRA ITA ESP NLD PTB DAN FIN NOR SVE PLK CSY HUN RUS TRK).");
        {
            var atoms = new Regex("name=\"([^\"]+)\"");
            var baseAtoms = baseNat is null ? new List<string>() : atoms.Matches(ReadEntry(baseNat)).Select(m => m.Groups[1].Value).OrderBy(x => x).ToList();
            var langFolders = new List<string>();
            foreach (var e in layoutEntries)
            {
                var m = Regex.Match(e.FullName.Replace('\\', '/'), @"^UILayout/([A-Z]{3})/NameAndTitle\.xml$", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                var lang = m.Groups[1].Value.ToUpperInvariant();
                langFolders.Add(lang);
                var langAtoms = atoms.Matches(ReadEntry(e)).Select(x => x.Groups[1].Value).OrderBy(x => x).ToList();
                if (baseNat is not null && !baseAtoms.SequenceEqual(langAtoms))
                    report.Warn("LANG_ATOMS_INCONSISTENT",
                        $"UILayout/{lang}/NameAndTitle.xml declares different atoms than the base NameAndTitle.xml.",
                        "All language folders must carry exactly the same atom set as the base file; this drifts apart easily after a fork or rename.");
            }
            var missing = EuroLangFolders.Where(l => !langFolders.Contains(l)).ToList();
            if (missing.Count > 0)
                report.Error("LANGS_INCOMPLETE",
                    $"UILayout language folders missing: {string.Join(", ", missing)}.",
                    "Ship all 16 European Power PDF languages (ENU DEU FRA ITA ESP NLD PTB DAN FIN NOR SVE PLK CSY HUN RUS TRK); the ribbon follows the host language.");
            var further = ExtendedLangFolders.Where(l => !langFolders.Contains(l)).ToList();
            if (further.Count > 0)
                report.Error("LANGS_EXTENDED_MISSING",
                    $"UILayout folders of the further Power PDF languages missing: {string.Join(", ", further)}.",
                    "Power PDF also runs in CHS (Simplified Chinese), CHT (Traditional Chinese), JPN, KOR and ARA: add a translated NameAndTitle.xml in each, so the ribbon follows these languages too.");
        }
    }

    /// <summary>DLL file names a stored manifest declares in files.bin (S1.4.0).</summary>
    public static IEnumerable<string> BinFilesOf(string? manifestJson)
    {
        if (string.IsNullOrEmpty(manifestJson)) return Array.Empty<string>();
        try
        {
            using var d = JsonDocument.Parse(manifestJson);
            if (d.RootElement.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Object &&
                f.TryGetProperty("bin", out var b) && b.ValueKind == JsonValueKind.Array)
                return b.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                        .Select(x => Path.GetFileName((x.GetString() ?? "").Replace('\\', '/'))).Where(n => n.Length > 0).ToList();
        }
        catch (JsonException) { }
        return Array.Empty<string>();
    }

    /// <summary>The 16 European Power PDF UI languages (manifest language codes); required.</summary>
    public static readonly string[] RequiredLanguages =
        { "en", "de", "fr", "it", "es", "nl", "pt", "da", "fi", "nb", "sv", "pl", "cs", "hu", "ru", "tr" };

    /// <summary>
    /// The five further Power PDF UI languages (S1.2.0): Simplified and Traditional Chinese,
    /// Japanese, Korean, Arabic. Recommended: missing ones are warnings, which admins can
    /// make mandatory on the rules page.
    /// </summary>
    public static readonly string[] ExtendedLanguages = { "zh-Hans", "zh-Hant", "ja", "ko", "ar" };

    /// <summary>All 21 Power PDF UI languages.</summary>
    public static readonly string[] AllLanguages = RequiredLanguages.Concat(ExtendedLanguages).ToArray();

    private static List<string> MissingExtended(JsonElement root, string field)
    {
        if (!root.TryGetProperty(field, out var el) || el.ValueKind != JsonValueKind.Object) return new();
        bool Has(string lang) =>
            el.TryGetProperty(lang, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString());
        return ExtendedLanguages.Where(l => !Has(l)).ToList();
    }

    private static List<string> MissingLanguages(JsonElement root, string field)
    {
        if (!root.TryGetProperty(field, out var el) || el.ValueKind != JsonValueKind.Object)
            return RequiredLanguages.ToList();
        bool Has(string lang) =>
            el.TryGetProperty(lang, out var v) && v.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(v.GetString());
        return RequiredLanguages.Where(l => !Has(l) && !(l == "nb" && Has("no"))).ToList();
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

/// <summary>
/// Orders store versions number by number: three parts and an optional fourth (S1.4.1),
/// a missing fourth part counts as 0 (1.2.3 equals 1.2.3.0, 1.2.3.1 is higher).
/// </summary>
public class SemVerComparer : IComparer<string>
{
    public int Compare(string? x, string? y)
    {
        var a = Parse(x); var b = Parse(y);
        for (var i = 0; i < 4; i++)
        {
            var c = a[i].CompareTo(b[i]);
            if (c != 0) return c;
        }
        return 0;
    }

    private static long[] Parse(string? v)
    {
        var r = new long[4];
        if (v is null) return r;
        var parts = v.Split('.');
        for (var i = 0; i < 4 && i < parts.Length; i++) r[i] = long.TryParse(parts[i], out var n) ? n : 0;
        return r;
    }
}
