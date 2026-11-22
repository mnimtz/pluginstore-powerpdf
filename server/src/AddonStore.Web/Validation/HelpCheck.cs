using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AddonStore.Web.Validation;

/// <summary>
/// S2.7.0: the add-on's help for the central help window of the store client (client 2.1.0+). An add-on brings
/// <c>docs/help/help.json</c> (format "tungsten-addon-help", version 1, "addon" = its package id, "langs" with "en",
/// optional "topics" = name -> chapter id) and one <c>docs/help/&lt;lang&gt;.html</c> per listed language. The limits
/// are the client's (store/helpcore.h): help.json 256 KB, one page 4 MB, one picture 2 MB. Pure functions over the
/// files of that folder, so they are tested without a package.
/// </summary>
public static class HelpCheck
{
    public const string Folder = "docs/help/";
    public const long MaxJsonBytes = 256 * 1024;
    public const long MaxHtmlBytes = 4 * 1024 * 1024;
    public const long MaxImageBytes = 2 * 1024 * 1024;
    /// <summary>The store's manual languages (help in all six is recommended).</summary>
    public static readonly string[] ManualLanguages = { "en", "de", "fr", "it", "es", "nl" };

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(500);
    private static readonly Regex TopicName = new("^[a-z0-9-]{1,64}\\z", RegexOptions.CultureInvariant, Timeout);
    // the client takes two lower-case letters only ("de", "zh"); it picks the page by the first two letters of the UI language
    private static readonly Regex LangCode = new("^[a-z]{2}\\z", RegexOptions.CultureInvariant, Timeout);

    /// <param name="Present">help.json exists (valid or not)</param>
    /// <param name="Problems">reasons the client would not show the help completely (HELP_INVALID)</param>
    /// <param name="Langs">the listed languages that have a page</param>
    /// <param name="UnknownTopics">topics whose chapter id does not occur in en.html (HELP_TOPIC_UNKNOWN)</param>
    /// <param name="Hash">SHA-256 over every file of docs/help/ (names and bytes), "" without files</param>
    /// <param name="EnglishText">the text of en.html (for the version history check)</param>
    /// <param name="Ignored">entries the help window skips without a message: other language codes, invalid topics (HELP_IGNORED)</param>
    public sealed record Result(bool Present, List<string> Problems, List<string> Langs, List<string> UnknownTopics, string Hash, string EnglishText,
                                List<string> Ignored);

    /// <summary>Inspects the files of docs/help/ (full entry names; a null value = larger than could be read).</summary>
    public static Result Inspect(IReadOnlyDictionary<string, byte[]?> files, string packageId)
    {
        var problems = new List<string>();
        var langs = new List<string>();
        var unknown = new List<string>();
        var ignored = new List<string>();
        var map = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in files)
            if (kv.Key.StartsWith(Folder, StringComparison.OrdinalIgnoreCase)) map[kv.Key] = kv.Value;
        var hash = HashOf(map);
        if (!map.TryGetValue(Folder + "help.json", out var json))
        {
            if (map.Count > 0) problems.Add("docs/help/ has files but no help.json");
            return new Result(false, problems, langs, unknown, hash, "", ignored);
        }
        if (json is null || json.Length > MaxJsonBytes)
        {
            problems.Add($"help.json is larger than {MaxJsonBytes / 1024} KB");
            return new Result(true, problems, langs, unknown, hash, "", ignored);
        }
        JsonElement root;
        try
        {
            // a UTF-8 byte order mark (PowerShell 5.1 writes one) is skipped, as by the client's parser (store/helpcore.h)
            var body = json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF ? json.AsMemory(3) : json.AsMemory();
            using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            problems.Add("help.json is not valid JSON");
            return new Result(true, problems, langs, unknown, hash, "", ignored);
        }
        if (root.ValueKind != JsonValueKind.Object)
        {
            problems.Add("help.json is not a JSON object");
            return new Result(true, problems, langs, unknown, hash, "", ignored);
        }
        string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (Str("format") != "tungsten-addon-help") problems.Add("\"format\" must be \"tungsten-addon-help\"");
        if (!(root.TryGetProperty("version", out var ver) && ver.ValueKind == JsonValueKind.Number && ver.TryGetDouble(out var vn) && vn == 1))   // 1 or 1.0, as the client reads it
            problems.Add("\"version\" must be 1");
        if (Str("addon") != packageId) problems.Add($"\"addon\" must be the package id \"{packageId}\"");
        else if (packageId.Length > 101) problems.Add("the package id is longer than the help window accepts (101 characters)");

        var listed = new List<string>();
        if (root.TryGetProperty("langs", out var ls) && ls.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in ls.EnumerateArray())
            {
                var code = l.ValueKind == JsonValueKind.String ? l.GetString() ?? "" : "";
                if (!LangCode.IsMatch(code))
                {
                    if (ignored.Count < 20) ignored.Add($"language \"{(code.Length > 20 ? code[..20] : code)}\" (use two-letter codes such as de or zh)");
                    continue;
                }
                if (!listed.Contains(code)) listed.Add(code);
            }
        }
        else problems.Add("\"langs\" must be an array of language codes");
        if (!listed.Contains("en")) problems.Add("\"langs\" must contain \"en\"");
        foreach (var code in listed)
        {
            if (!map.TryGetValue(Folder + code + ".html", out var page)) { problems.Add($"docs/help/{code}.html is missing"); continue; }
            if (page is null || page.Length > MaxHtmlBytes) { problems.Add($"docs/help/{code}.html is larger than {MaxHtmlBytes / 1048576} MB"); continue; }
            if (page.Length == 0) { problems.Add($"docs/help/{code}.html is empty"); continue; }
            langs.Add(code);
        }
        foreach (var kv in map)
        {
            // the help window shows PNG and JPEG pictures only (store/helpcore.h); other files are never read
            var n = kv.Key.ToLowerInvariant();
            if (n.EndsWith(".png") || n.EndsWith(".jpg") || n.EndsWith(".jpeg"))
            {
                if (kv.Value is null || kv.Value.Length > MaxImageBytes)
                    problems.Add($"{kv.Key} is larger than {MaxImageBytes / 1048576} MB (the help window does not show it)");
            }
            else if ((n.EndsWith(".gif") || n.EndsWith(".webp") || n.EndsWith(".svg") || n.EndsWith(".bmp")) && ignored.Count < 20)
                ignored.Add($"picture {kv.Key} (only PNG and JPEG are shown)");
        }

        var english = map.TryGetValue(Folder + "en.html", out var en) && en is not null && en.Length <= MaxHtmlBytes ? Encoding.UTF8.GetString(en) : "";
        if (root.TryGetProperty("topics", out var topics))
        {
            if (topics.ValueKind != JsonValueKind.Object) ignored.Add("\"topics\" (it must be an object of name -> chapter id)");
            else
                foreach (var t in topics.EnumerateObject())
                {
                    var target = t.Value.ValueKind == JsonValueKind.String ? t.Value.GetString() ?? "" : "";
                    if (!TopicName.IsMatch(t.Name) || !TopicName.IsMatch(target))
                    {
                        if (ignored.Count < 20) ignored.Add("topic \"" + (t.Name.Length > 40 ? t.Name[..40] : t.Name) + "\" (names and chapter ids use a to z, 0 to 9 and -, at most 64 characters)");
                        continue;
                    }
                    if (english.Length > 0 && !HasId(english, target) && !unknown.Contains(target)) unknown.Add(target);
                }
        }
        return new Result(true, problems, langs, unknown, hash, english, ignored);
    }

    /// <summary>The HTML has an element with id="<paramref name="id"/>".</summary>
    public static bool HasId(string html, string id)
    {
        // id="x", id='x' or id=x, any case of the attribute name (review: ID=x gave a false warning)
        try { return Regex.IsMatch(html, @"\bid\s*=\s*[""']?" + Regex.Escape(id) + @"(?=[""'\s>/])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout); }
        catch (RegexMatchTimeoutException) { return true; }
    }

    /// <summary>The help names this version (e.g. "1.3.0", also written "1.3" for a version ending in ".0").</summary>
    public static bool MentionsVersion(string text, string version)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(version)) return false;
        var forms = new List<string> { version };
        var trimmed = version;
        while (trimmed.EndsWith(".0", StringComparison.Ordinal) && trimmed.Count(c => c == '.') > 1) { trimmed = trimmed[..^2]; forms.Add(trimmed); }
        foreach (var f in forms)
            if (Regex.IsMatch(text, @"(?<![0-9.])" + Regex.Escape(f) + @"(?![0-9]|\.[0-9])", RegexOptions.CultureInvariant, Timeout)) return true;
        return false;
    }

    /// <summary>SHA-256 over the files of docs/help/ in name order (name, NUL, bytes); "" when there are none.</summary>
    public static string HashOf(IReadOnlyDictionary<string, byte[]?> files)
    {
        var names = files.Keys.Where(k => k.StartsWith(Folder, StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => k.ToLowerInvariant(), StringComparer.Ordinal).ToList();
        if (names.Count == 0) return "";
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var n in names)
        {
            sha.AppendData(Encoding.UTF8.GetBytes(n.ToLowerInvariant()));
            sha.AppendData(new byte[] { 0 });
            sha.AppendData(files[n] ?? Encoding.UTF8.GetBytes("<too large>"));
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>One file of a stored package (e.g. docs/help/en.html), at most <see cref="MaxHtmlBytes"/>; null when missing or unreadable.</summary>
    public static byte[]? ReadEntry(string packagePath, string name)
    {
        try
        {
            if (!File.Exists(packagePath)) return null;
            using var zip = System.IO.Compression.ZipFile.OpenRead(packagePath);
            var e = zip.Entries.FirstOrDefault(x => string.Equals(x.FullName, name, StringComparison.OrdinalIgnoreCase));
            if (e is null || e.Length > MaxHtmlBytes) return null;
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The files of docs/help/ of a stored package (for the comparison with the previous version); empty when unreadable.</summary>
    public static Dictionary<string, byte[]?> ReadFromPackage(string packagePath)
    {
        var files = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(packagePath)) return files;
            using var zip = System.IO.Compression.ZipFile.OpenRead(packagePath);
            foreach (var e in zip.Entries)
            {
                if (!e.FullName.StartsWith(Folder, StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith('/') || files.Count >= 500) continue;
                if (e.Length > MaxHtmlBytes) { files[e.FullName] = null; continue; }
                using var s = e.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                files[e.FullName] = ms.ToArray();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { files.Clear(); }
        return files;
    }
}
