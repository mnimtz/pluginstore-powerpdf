using System.IO.Compression;
using System.Security.Cryptography;
using AddonStore.Web.Data;
using AddonStore.Web.Validation;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Services;

/// <summary>
/// Source code of each version, stored next to the package and visible to
/// admins only (backup, central fixes such as new languages, better license
/// checks). The Power PDF client never receives it.
/// </summary>
public class SourceService
{
    /// <summary>Ways to switch off HTTPS certificate validation in C, C++ and C# (S1.0.11).</summary>
    internal static readonly (string Label, System.Text.RegularExpressions.Regex Rx)[] TlsBypassPatterns =
    {
        // NonBacktracking: linear time on crafted sources (audit S1.3.1)
        ("WinHTTP/WinINet ignore flags", new(@"\b(SECURITY_FLAG_IGNORE_(UNKNOWN_CA|CERT_CN_INVALID|CERT_DATE_INVALID|CERT_WRONG_USAGE|ALL_CERT_ERRORS)|INTERNET_FLAG_IGNORE_CERT_(CN|DATE)_INVALID)\b", System.Text.RegularExpressions.RegexOptions.NonBacktracking)),
        ("curl verification off", new(@"CURLOPT_SSL_VERIFY(PEER|HOST)\s*,\s*0", System.Text.RegularExpressions.RegexOptions.NonBacktracking)),
        ("OpenSSL verification off", new(@"SSL_(CTX_)?set_verify\s*\([^;]{0,400}SSL_VERIFY_NONE", System.Text.RegularExpressions.RegexOptions.NonBacktracking)),
        (".NET accept-all certificate callback", new(@"ServerCertificate(Validation|CustomValidation)Callback\s*=\s*[^;]{0,400}=>\s*true", System.Text.RegularExpressions.RegexOptions.NonBacktracking)),
    };

    public const long MaxZipBytes = 100L * 1024 * 1024;
    public const long MaxInflatedBytes = 600L * 1024 * 1024;
    public const int MaxEntries = 20000;
    private const long MaxScannedFileBytes = 2L * 1024 * 1024;

    private static readonly string[] TextExtensions =
    {
        ".c", ".cc", ".cpp", ".cxx", ".h", ".hh", ".hpp", ".hxx", ".inl", ".cs", ".rc", ".rc2", ".def", ".idl",
        ".txt", ".md", ".json", ".xml", ".ini", ".cfg", ".config", ".props", ".targets", ".vcxproj", ".csproj",
        ".sln", ".filters", ".ps1", ".psm1", ".py", ".cmd", ".bat", ".js", ".ts", ".yml", ".yaml", ".wxs", ".wxi", ".reg"
    };
    private static readonly string[] BuildOutputDirs = { "/bin/", "/obj/", "/release/", "/debug/", "/x64/", "/arm64/", "/.vs/", "/ipch/" };
    private static readonly string[] BuildOutputExtensions = { ".pdb", ".obj", ".ilk", ".iobj", ".ipdb", ".pch", ".tlog", ".zxt", ".exe", ".dll", ".lib", ".exp", ".res", ".msi" };
    private static readonly HashSet<string> SdkHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "rvcalls.h", "ducalls.h", "dvcalls.h", "ddcalls.h", "dsfcalls.h", "pgcntcalls.h", "duextracalls.h", "dercalls.h",
        "dewcalls.h", "ddswritecalls.h", "ddsreadcalls.h", "ddmetadatacalls.h", "rvpanelpagepref.h", "picommon.h",
    };

    private readonly AppDbContext _db;
    private readonly SettingsService _settings;
    private readonly AuditService _audit;
    private readonly SubmissionService _submissions;

    public SourceService(AppDbContext db, SettingsService settings, AuditService audit, SubmissionService submissions)
    {
        _db = db; _settings = settings; _audit = audit; _submissions = submissions;
    }

    /// <summary>"off", "recommended" or "required" (default).</summary>
    public async Task<string> PolicyAsync()
    {
        var p = await _settings.GetAsync("Source.Policy");
        return p is "off" or "recommended" or "required" ? p : "required";
    }

    /// <summary>
    /// Policy "required" (S1.20.0): the source must come WITH the submission, in the
    /// upload package (.ppak + source ZIP); a bare .ppak is refused (SOURCE_REQUIRED)
    /// and nothing is stored, so no version waits for review without its source.
    /// The Add-on Store client is exempt, as for the approval rule below.
    /// </summary>
    public async Task<bool> RequiredAtSubmitAsync(string ppakPath) =>
        await PolicyAsync() == "required" && PeekPackageId(ppakPath) != SubmissionService.ClientPackageId;

    /// <summary>The id in manifest.json of a .ppak; null when it cannot be read (the validator then says why).</summary>
    public static string? PeekPackageId(string ppakPath)
    {
        try
        {
            // the central directory is only loaded when its declared size is sane (review S1.20.2)
            if (UploadLimits.DeclaredEntries(ppakPath) > MaxEntries) return null;
            using var zip = ZipFile.OpenRead(ppakPath);
            var entry = zip.GetEntry("manifest.json");
            if (entry is null || entry.Length > 256 * 1024) return null;
            using var s = entry.Open();
            using var doc = System.Text.Json.JsonDocument.Parse(s);
            return doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String
                ? id.GetString() : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// The source check of a submission under the policy "required" (S1.20.2): as <see cref="Check"/>,
    /// but a ZIP without any C, C++ or C# file (SOURCE_NO_CODE) is an error, so a readme or the built
    /// package alone does not satisfy SOURCE_REQUIRED.
    /// </summary>
    public static ValidationReport CheckForSubmit(string zipPath)
    {
        var r = Check(zipPath);
        var strict = new ValidationReport();
        foreach (var f in r.Findings)
            strict.Findings.Add(f.Code == "SOURCE_NO_CODE" && f.Severity == "warning" ? f with { Severity = "error" } : f);
        return strict;
    }

    /// <summary>True when the version may not be approved yet because its source is missing.</summary>
    public async Task<bool> BlocksApprovalAsync(PackageVersion v) =>
        v.SourcePath is null && v.PackageId != SubmissionService.ClientPackageId && await PolicyAsync() == "required";

    public string FullPath(PackageVersion v) => Path.Combine(_submissions.StorageRoot, v.SourcePath!);

    /// <summary>Checks a source ZIP; stores it for the version when no finding has severity "error".</summary>
    public async Task<ValidationReport> UploadAsync(PackageVersion v, string zipPath, AppUser user, bool actorIsAdmin)
    {
        // Once a version was reviewed, its stored source documents what was approved.
        if (v.SourcePath is not null && v.ReviewedAt is not null && !actorIsAdmin)
        {
            var locked = new ValidationReport();
            locked.Error("SOURCE_LOCKED", $"The source code of {v.PackageId} {v.Version} was reviewed and cannot be replaced.",
                "Upload a new version with its source code, or ask an admin.");
            return locked;
        }
        var report = Check(zipPath);
        if (!report.Passed) return report;

        var rel = Path.Combine(v.PackageId, $"{v.PackageId}-{v.Version}.source.zip");
        var target = Path.Combine(_submissions.StorageRoot, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(zipPath, target, overwrite: true);

        await using (var fs = File.OpenRead(target))
            v.SourceSha256 = Convert.ToHexString(await SHA256.HashDataAsync(fs)).ToLowerInvariant();
        var replaced = v.SourcePath is not null;
        v.SourcePath = rel;
        v.SourceSizeBytes = new FileInfo(target).Length;
        v.SourceUploadedAt = DateTime.UtcNow;
        v.SourceUploadedBy = user.DisplayName;
        v.SourceReportJson = report.ToJson();
        await _db.SaveChangesAsync();
        await _audit.LogAsync(user.DisplayName, replaced ? "source.replaced" : "source.uploaded", $"{v.PackageId} {v.Version}",
            $"{v.SourceSizeBytes / 1024} KB; warnings: {report.Findings.Count(f => f.Severity == "warning")}");
        return report;
    }

    public static ValidationReport Check(string zipPath)
    {
        var report = new ValidationReport();
        var size = new FileInfo(zipPath).Length;
        if (size > MaxZipBytes)
        {
            report.Error("SOURCE_TOO_LARGE", $"The source ZIP has {size / (1024 * 1024)} MB (limit {MaxZipBytes / (1024 * 1024)} MB).",
                "Leave out build output (bin, obj, Release, Debug, .vs), packages and large test files; reference big third-party sources by version instead.");
            return report;
        }

        if (UploadLimits.DeclaredEntries(zipPath) > MaxEntries)   // before the entries are loaded (audit S1.3.1)
        {
            report.Error("SOURCE_INVALID", $"The source ZIP has more than {MaxEntries} files.", "Leave out build output and dependencies that can be restored.");
            return report;
        }
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(zipPath); }
        catch (Exception)
        {
            report.Error("SOURCE_INVALID", "The source upload is not a readable ZIP file.", "Upload the source tree as one ZIP file.");
            return report;
        }

        using (zip)
        {
            var entries = zip.Entries.Where(e => !e.FullName.EndsWith('/')).ToList();
            if (entries.Count == 0 || entries.Count > MaxEntries || entries.Sum(e => e.Length) > MaxInflatedBytes)
            {
                report.Error("SOURCE_INVALID", $"The source ZIP is empty or too big when unpacked ({entries.Count} files).",
                    $"At most {MaxEntries} files and {MaxInflatedBytes / (1024 * 1024)} MB unpacked; leave out build output.");
                return report;
            }

            var buildOutput = new List<string>();
            var sdk = new List<string>();
            var secrets = new SortedSet<string>();
            var copyleft = new SortedSet<string>();
            var weak = new SortedSet<string>();
            var thirdParty = new SortedSet<string>();
            var tlsOff = new SortedSet<string>();
            var foreignDocs = new SortedSet<string>();   // S1.25.0: standards, schemas, data with a copyright notice
            var hasCode = false;
            // S1.27.0 code security: only the developer's own files; folders of third-party code
            // (a license file of their own, or a usual vendor folder name) are left out
            var security = new AddonStore.Web.Validation.SourceSecurity();
            var foreignDirs = entries
                .Select(x => x.FullName.Replace('\\', '/'))
                .Where(f => { var n = Path.GetFileName(f).ToLowerInvariant(); return (n.StartsWith("license") && !n.StartsWith("licenses.")) || n.StartsWith("copying"); })
                .Select(f => Path.GetDirectoryName(f)?.Replace('\\', '/') ?? "")
                .Where(d => d.Length > 0)
                .Select(d => "/" + d.ToLowerInvariant() + "/")
                .ToHashSet();
            bool OwnCode(string lowerPath) =>
                !foreignDirs.Any(lowerPath.StartsWith) &&
                !new[] { "/third_party/", "/thirdparty/", "/3rdparty/", "/vendor/", "/external/", "/extern/", "/deps/", "/node_modules/", "/packages/" }.Any(lowerPath.Contains);

            foreach (var e in entries)
            {
                var path = "/" + e.FullName.Replace('\\', '/');
                var lower = path.ToLowerInvariant();
                var ext = Path.GetExtension(lower);
                var name = Path.GetFileName(lower);
                if (ext is ".cpp" or ".c" or ".h" or ".hpp" or ".cs") hasCode = true;

                if (BuildOutputDirs.Any(lower.Contains) || BuildOutputExtensions.Contains(ext)) buildOutput.Add(e.FullName);
                if (SdkHeaders.Contains(name)) sdk.Add(e.FullName);
                if (ext is ".pfx" or ".p12" or ".key" or ".snk") { secrets.Add($"{e.FullName} (key or certificate container)"); continue; }

                // S1.25.0 (license audit): documents of others (standards, specifications, schemas, sample files,
                // data sets) may only be included when their license allows passing them on
                if (ext is ".xsd" or ".sch" or ".xsl" or ".xslt" or ".yaml" or ".yml" or ".pdf" or ".icc" or ".icm" && e.Length <= MaxScannedFileBytes)
                {
                    try
                    {
                        using var ds = e.Open();
                        using var ms = new MemoryStream();
                        ds.CopyTo(ms);
                        var db = ms.ToArray();
                        if (db.AsSpan().IndexOf("Copyright"u8) >= 0 || db.AsSpan().IndexOf("©"u8) >= 0) foreignDocs.Add(e.FullName);
                    }
                    catch { }
                }

                // LICENSES.md is the package's own component list, not a license text.
                var isLicense = (name.StartsWith("license") && !name.StartsWith("licenses.")) || name.StartsWith("copying") || name.StartsWith("notice");
                var isWeb = AddonStore.Web.Validation.SourceSecurity.IsWeb(ext);
                if (!isLicense && !TextExtensions.Contains(ext) && !isWeb) continue;
                if (e.Length > MaxScannedFileBytes) continue;

                string text;
                try
                {
                    using var s = e.Open();
                    using var r = new StreamReader(s);
                    text = r.ReadToEnd();
                }
                catch { continue; }

                foreach (var (label, rx) in PackageValidator.SecretPatterns)
                    if (rx.IsMatch(text)) secrets.Add($"{label} in {e.FullName}");
                if (!isLicense && !SdkHeaders.Contains(name) && OwnCode(lower)) security.Scan(e.FullName, ext, text);
                // certificate checks switched off (S1.0.11): documents and credentials would be open to interception
                if (ext is ".c" or ".cc" or ".cpp" or ".cxx" or ".h" or ".hpp" or ".hxx" or ".inl" or ".cs")
                    foreach (var (label, rx) in TlsBypassPatterns)
                        if (rx.IsMatch(text)) tlsOff.Add($"{label} in {e.FullName}");

                var dir = Path.GetDirectoryName(e.FullName.Replace('\\', '/'))?.Replace('\\', '/') ?? "";
                if (isLicense)
                {
                    var t = text.ToUpperInvariant();
                    if (t.Contains("GNU AFFERO GENERAL PUBLIC LICENSE") || (t.Contains("GNU GENERAL PUBLIC LICENSE") && !t.Contains("GNU LESSER")))
                        copyleft.Add($"{e.FullName} (GPL/AGPL)");
                    else if (t.Contains("GNU LESSER GENERAL PUBLIC LICENSE") || t.Contains("GNU LIBRARY GENERAL PUBLIC LICENSE"))
                        weak.Add($"{e.FullName} (LGPL)");
                    else if (t.Contains("MOZILLA PUBLIC LICENSE") || t.Contains("ECLIPSE PUBLIC LICENSE"))
                        weak.Add($"{e.FullName} (MPL/EPL)");
                    else if (dir.Length > 0)
                    {
                        var kind = t.Contains("APACHE LICENSE") ? "Apache-2.0" : t.Contains("PERMISSION IS HEREBY GRANTED, FREE OF CHARGE") ? "MIT"
                                 : t.Contains("REDISTRIBUTION AND USE IN SOURCE AND BINARY FORMS") ? "BSD" : "license file";
                        thirdParty.Add($"{dir} ({kind})");
                    }
                }
                else if (text.Contains("SPDX-License-Identifier:"))
                {
                    foreach (var line in text.Split('\n').Where(l => l.Contains("SPDX-License-Identifier:")).Take(3))
                    {
                        var id = line[(line.IndexOf("SPDX-License-Identifier:") + 24)..].Trim().ToUpperInvariant();
                        if (id.StartsWith("GPL") || id.StartsWith("AGPL")) copyleft.Add($"{e.FullName} ({id})");
                        else if (id.StartsWith("LGPL") || id.StartsWith("MPL") || id.StartsWith("EPL")) weak.Add($"{e.FullName} ({id})");
                    }
                }
            }

            if (!hasCode)
                report.Warn("SOURCE_NO_CODE", "The ZIP contains no C, C++ or C# source files.",
                    "Upload the plugin's source tree (project file, .cpp/.h files, resources, UILayout), not the built package.");
            if (secrets.Count > 0)
                report.Error("SOURCE_SECRET", $"Possible credentials in the source: {string.Join("; ", secrets.Take(10))}.",
                    "Remove keys, tokens, passwords and certificates from the source, rotate any exposed secret, and load credentials at runtime.");
            if (tlsOff.Count > 0)
                report.Error("TLS_CHECK_DISABLED", $"The source switches off certificate checks: {string.Join("; ", tlsOff.Take(10))}.",
                    "Keep HTTPS certificate validation on (no ignore flags, no accept-all callbacks). Fix the certificate or the host name instead; a disabled check lets anyone on the network read documents and credentials.");
            if (copyleft.Count > 0)
                report.Error("LICENSE_COPYLEFT_SOURCE", $"GPL/AGPL code in the source: {string.Join("; ", copyleft.Take(10))}.",
                    "Shipped plugins may contain only MIT, BSD or Apache-2.0 third-party code. Remove or replace the component.");
            if (weak.Count > 0)
                report.Warn("LICENSE_WEAK_COPYLEFT_SOURCE", $"LGPL/MPL/EPL code in the source: {string.Join("; ", weak.Take(10))}.",
                    "These licenses need a legal review and usually cannot ship. Prefer an MIT/BSD/Apache-2.0 alternative.");
            if (foreignDocs.Count > 0)
                report.Warn("SOURCE_FOREIGN_DOCUMENT", $"The source ZIP contains documents with a copyright notice: {string.Join("; ", foreignDocs.Take(10))}.",
                    "Include standards, specifications, schemas, sample files and data sets of others only when their license allows passing them on; otherwise leave them out and say in a README where to get them. Your own documents are fine; the reviewer checks.");
            ReportSecurity(report, security);
            if (thirdParty.Count > 0)
                report.Info("THIRDPARTY_SOURCE_DETECTED", $"Third-party code folders: {string.Join("; ", thirdParty.Take(15))}.");
            if (buildOutput.Count > 0)
                report.Warn("SOURCE_BUILD_OUTPUT", $"The ZIP contains build output, e.g. {string.Join(", ", buildOutput.Take(5))} ({buildOutput.Count} files).",
                    "Leave out bin, obj, Release, Debug, .vs and binaries; the source should rebuild them.");
            if (sdk.Count > 0)
                report.Warn("SOURCE_SDK_INCLUDED", $"The ZIP contains Power PDF Plugin SDK headers, e.g. {string.Join(", ", sdk.Take(3))}.",
                    "Do not bundle the SDK; reference its location (e.g. in the project file or a README).");
        }
        // S1.27.0: the store's stricter rules apply to the source checks too (code security
        // warnings an admin made mandatory become errors and refuse the source)
        AddonStore.Web.Validation.RuleCatalog.Escalate(report);
        return report;
    }

    /// <summary>Code security findings of the source (S1.27.0), warnings with the places (file:line).</summary>
    private static void ReportSecurity(ValidationReport report, AddonStore.Web.Validation.SourceSecurity s)
    {
        string Places(string code) => string.Join("; ", s.Findings[code].Take(12)) + (s.Findings[code].Count > 12 ? $" (+{s.Findings[code].Count - 12} more)" : "");
        void Warn(string code, string what, string hint)
        {
            if (s.Findings.ContainsKey(code)) report.Warn(code, $"{what}: {Places(code)}.", hint);
        }
        Warn(AddonStore.Web.Validation.SourceSecurity.UnsafeFunction, "Copy or format functions without a buffer size (CWE-120)",
            "Use the bounded versions: strcpy_s/wcscpy_s, strcat_s/wcscat_s, sprintf_s/swprintf_s, StringCchCopy/StringCchPrintf, or std::string/std::wstring. A value longer than the buffer would overwrite memory.");
        Warn(AddonStore.Web.Validation.SourceSecurity.FormatString, "Format strings that are not a constant (CWE-134)",
            "Pass a literal format (L\"%s\") and the text as an argument, or fill placeholders by plain replacement. A format from resources, files or the network with a wrong placeholder reads past the arguments and can crash Power PDF.");
        Warn(AddonStore.Web.Validation.SourceSecurity.CommandExec, "Commands started through the shell or without a program path (CWE-78)",
            "Start the program directly with CreateProcessW and its full path as the first argument (from GetSystemDirectoryW or the install folder), quote every argument, and never put document, user or network text into a command line.");
        Warn(AddonStore.Web.Validation.SourceSecurity.DllSearchPath, "DLLs loaded by name only (CWE-427)",
            "Load system DLLs with LoadLibraryExW(name, nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32) and your own DLLs with their full path (bin/ through pluginstore_bin.h); a bare name lets a DLL of the same name in another folder load into Power PDF.");
        Warn(AddonStore.Web.Validation.SourceSecurity.WeakCrypto, "Weak cryptography or random numbers (CWE-327/328/338)",
            "For security use SHA-256 or better, AES-GCM, and BCryptGenRandom (RandomNumberGenerator in .NET). MD5, SHA-1 or rand() are fine only where nothing depends on them for security (a cache key, a checksum a standard demands); say so to the reviewer.");
        Warn(AddonStore.Web.Validation.SourceSecurity.UnsafeHtml, "HTML or script built from strings in the add-on's web pages (CWE-79)",
            "Set text with textContent and build elements with createElement; no innerHTML/outerHTML/insertAdjacentHTML/srcdoc/jQuery .html() with data, no eval, new Function or string timers, and postMessage only to a named origin. Document or server text in HTML runs as script in the add-on's window.");
        Warn(AddonStore.Web.Validation.SourceSecurity.XmlEntities, "XML parsing that may resolve external entities or DTDs (CWE-611)",
            "Use MSXML 6 (DTDs are refused by default) or XmlLite with DtdProcessing_Prohibit; never switch ProhibitDTD off or resolveExternals on. A crafted XML (XFA, XMP, e-invoice) could otherwise read local files or call out to the network.");
        Warn(AddonStore.Web.Validation.SourceSecurity.TempFile, "Temporary files with predictable names (CWE-377)",
            "Create temporary files with a random name (a GUID) in GetTempPath2W/GetTempPathW and open them with CreateFileW(CREATE_NEW); tmpnam, _mktemp and tmpfile names can be guessed and taken over by another process.");
        Warn(AddonStore.Web.Validation.SourceSecurity.WeakPermissions, "Objects created with open access rights (CWE-732)",
            "Pass NULL security attributes (the default DACL of the user) or a DACL limited to the current user; a NULL DACL or SDDL that grants Everyone/Users full access lets any process on the machine change files, keys or events of the add-on.");
        Warn(AddonStore.Web.Validation.SourceSecurity.HardcodedKey, "Fixed key, IV or salt bytes in the code (CWE-321/329)",
            "Create keys and IVs at run time (BCryptGenRandom), keep secrets with DPAPI (CryptProtectData) and never ship a fixed key: it is the same in every installation and can be read from the DLL. Public verification keys are fine; say so to the reviewer.");
        Warn(AddonStore.Web.Validation.SourceSecurity.WebViewExposure, "WebView2 settings that open the window to page scripts (CWE-749/346)",
            "Talk to the page through PostWebMessageAsJson/WebMessageReceived and check the message source; keep host objects and developer tools off in release builds, and block navigation away from your own page (NavigationStarting).");
    }
}
