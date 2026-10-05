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

    private static readonly Regex IdPattern = new(@"^[a-z0-9][a-z0-9-]*(\.[a-z0-9][a-z0-9-]*)+$", RegexOptions.Compiled);
    // [0-9] and \z: no non-ASCII digits, no trailing newline ($ would allow one); 9 digits fit an int
    private static readonly Regex SemVerPattern = new(@"^[0-9]{1,9}\.[0-9]{1,9}\.[0-9]{1,9}\z", RegexOptions.Compiled);
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
        { "x64", "arm64", "assets", "docs", "UILayout", "installer" };
    /// <summary>Id prefixes of the store operators; only admins create packages there.</summary>
    private static readonly string[] ReservedIdPrefixes = { "com.tungsten.", "com.kofax.", "com.nuance." };
    private static string Printable(string s) => new(s.Select(c => c < 0x20 || c == 0x7F ? '?' : c).ToArray());

    /// <summary>All 16 European Power PDF UI language folder codes.</summary>
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
                report.Error("MANIFEST_MISSING", "manifest.json was not found at the package root.",
                    "Add manifest.json at the ZIP root. Fetch /api/schema/manifest for the expected structure.");
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

                // Every user-facing text ships in ALL European Power PDF languages
                // (standing team rule); the name may stay a single product name.
                foreach (var field in new[] { "description", "changelog" })
                {
                    var missing = MissingLanguages(root, field);
                    if (missing.Count > 0)
                        report.Error("LANG_TEXT_INCOMPLETE",
                            $"'{field}' is missing languages: {string.Join(", ", missing)}.",
                            $"Provide '{field}' as an object with all 16 languages: {string.Join(", ", RequiredLanguages)}. " +
                            "Translate the text yourself; the store shows it in the user's language.");
                }

                manifest.AtomNamespace = GetString(root, "ribbonAtomNamespace") ?? "";
                if (manifest.AtomNamespace.Length == 0)
                    report.Warn("ATOM_NAMESPACE_MISSING", "Manifest field 'ribbonAtomNamespace' is not set.",
                        "Declare the ribbon atom namespace your plugin uses; the host caches ribbons by atom name and the store checks for collisions.");

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
                    report.Warn("AUTHOR_MISSING", "Manifest field 'author' is not set.",
                        "Name the person or team behind the add-on, e.g. \"author\": \"Team Signing\"; it is shown in the catalog and in Power PDF.");
                else if (author.Length > 100)
                    report.Error("AUTHOR_INVALID", "Manifest field 'author' is longer than 100 characters.", "Use a person's or team's name.");
                var contact = GetString(root, "contactEmail")?.Trim();
                if (string.IsNullOrEmpty(contact))
                    report.Warn("CONTACT_MISSING", "Manifest field 'contactEmail' is not set.",
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

                    CheckPe(report, arch, file, bytes, manifest.Version);
                }

                CheckIcon(report, zip);
                CheckScreenshots(report, zip, root);
                CheckLicenses(report, zip);
                CheckThirdParty(report, zip, root);
                CheckUiLayout(report, zip, manifest.AtomNamespace, manifest.Id, manifest.OwnTab);
                CheckDocs(report, zip);
            }
        }

        if (manifest is not null && manifest.Id.Length > 0)
            await CheckAgainstCatalogAsync(report, manifest, callerUserId, callerIsAdmin);

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

    private static void CheckPe(ValidationReport report, string arch, string file, byte[] bytes, string manifestVersion)
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

        CheckImports(report, file, bytes, peOffset);
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
            var (a, b, c, _) = fv.Value;
            if ($"{a}.{b}.{c}" != manifestVersion)
                report.Warn("VERSIONINFO_MISMATCH", $"'{file}' has FILEVERSION {a}.{b}.{c}, the manifest says {manifestVersion}.",
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
                else if (!KnownImportPrefixes.Any(p => lower.StartsWith(p)))
                    foreign.Add(dll);
            }

            if (debugCrt)
                report.Error("PE_DEBUG_RUNTIME", $"'{file}' imports a DEBUG C/C++ runtime.",
                    "Package the Release build; debug runtimes are not present on user machines (the .zxt must run with the same runtime as PowerPDF.exe).");
            if (foreign.Count > 0)
                report.Error("FOREIGN_DEPENDENCY",
                    $"'{file}' imports DLLs that are not part of Windows or Power PDF: {string.Join(", ", foreign.Distinct())}.",
                    "Power PDF loads plug-ins from its own program folder, so extra DLLs are never found there (the store installs only the .zxt). Link these libraries statically (MIT/BSD/Apache-2.0 only), or load them yourself from the plug-in's data folder with LoadLibraryEx and a full path and delay-load the import.");
        }
        catch
        {
            // import walking is best effort; a malformed table was already caught by the PE checks
        }
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
                Regex.IsMatch(html, @"\b(?:src|href)\s*=\s*[""']?\s*(?:https?:)?//", RegexOptions.IgnoreCase) && Regex.IsMatch(html, @"<(?:img|script|link|iframe)\b[^>]*\b(?:src|href)\s*=\s*[""']?\s*(?:https?:)?//", RegexOptions.IgnoreCase))
                bad.Add(e.FullName);
        }
        if (bad.Count > 0)
            report.Warn("DOCS_ACTIVE_CONTENT", $"Help pages contain scripts or load external resources: {string.Join(", ", bad.Take(5))}.",
                "Help under docs/ is installed on user machines and opened locally: plain HTML with local images only, no scripts, frames or external sources.");
    }

    private void CheckUiLayout(ValidationReport report, ZipArchive zip, string atomNamespace, string packageId = "", bool ownTab = false)
    {
        var layoutEntries = zip.Entries
            .Where(e => e.FullName.Replace('\\', '/').StartsWith("UILayout/", StringComparison.OrdinalIgnoreCase) && !e.FullName.EndsWith('/'))
            .ToList();
        if (layoutEntries.Count == 0)
        {
            report.Warn("UILAYOUT_MISSING", "The package has no UILayout folder.",
                "Ship UILayout/Publish Mode.xml and NameAndTitle.xml (root and the 16 language folders) so the ribbon group appears in every language.");
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
            foreach (Match tb in Regex.Matches(xml, @"<toolbar\b[^>]*?\bname\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase))
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
        }
    }

    /// <summary>The 16 European Power PDF UI languages (manifest language codes).</summary>
    public static readonly string[] RequiredLanguages =
        { "en", "de", "fr", "it", "es", "nl", "pt", "da", "fi", "nb", "sv", "pl", "cs", "hu", "ru", "tr" };

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
