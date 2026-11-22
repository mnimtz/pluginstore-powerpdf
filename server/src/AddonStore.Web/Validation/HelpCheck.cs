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
    private static readonly Regex TopicName = new("^[a-z0-9-]{1,64}$", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex LangCode = new("^[a-z]{2}(-[A-Za-z]{2,4})?$", RegexOptions.CultureInvariant, Timeout);

    /// <param name="Present">help.json exists (valid or not)</param>
    /// <param name="Problems">reasons the client would not show the help completely (HELP_INVALID)</param>
    /// <param name="Langs">the listed languages that have a page</param>
    /// <param name="UnknownTopics">topics whose chapter id does not occur in en.html (HELP_TOPIC_UNKNOWN)</param>
    /// <param name="Hash">SHA-256 over every file of docs/help/ (names and bytes), "" without files</param>
    /// <param name="EnglishText">the text of en.html (for the version history check)</param>
    public sealed record Result(bool Present, List<string> Problems, List<string> Langs, List<string> UnknownTopics, string Hash, string EnglishText);

    /// <summary>Inspects the files of docs/help/ (full entry names; a null value = larger than could be read).</summary>
    public static Result Inspect(IReadOnlyDictionary<string, byte[]?> files, string packageId)
    {
        var problems = new List<string>();
        var langs = new List<string>();
        var unknown = new List<string>();
        var map = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in files)
            if (kv.Key.StartsWith(Folder, StringComparison.OrdinalIgnoreCase)) map[kv.Key] = kv.Value;
        var hash = HashOf(map);
        if (!map.TryGetValue(Folder + "help.json", out var json))
        {
            if (map.Count > 0) problems.Add("docs/help/ has files but no help.json");
            return new Result(false, problems, langs, unknown, hash, "");
        }
        if (json is null || json.Length > MaxJsonBytes)
        {
            problems.Add($"help.json is larger than {MaxJsonBytes / 1024} KB");
            return new Result(true, problems, langs, unknown, hash, "");
        }
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            problems.Add("help.json is not valid JSON");
            return new Result(true, problems, langs, unknown, hash, "");
        }
        if (root.ValueKind != JsonValueKind.Object)
        {
            problems.Add("help.json is not a JSON object");
            return new Result(true, problems, langs, unknown, hash, "");
        }
        string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (Str("format") != "tungsten-addon-help") problems.Add("\"format\" must be \"tungsten-addon-help\"");
        if (!(root.TryGetProperty("version", out var ver) && ver.ValueKind == JsonValueKind.Number && ver.TryGetInt32(out var vn) && vn == 1))
            problems.Add("\"version\" must be 1");
        if (Str("addon") != packageId) problems.Add($"\"addon\" must be the package id \"{packageId}\"");

        var listed = new List<string>();
        if (root.TryGetProperty("langs", out var ls) && ls.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in ls.EnumerateArray())
            {
                var code = l.ValueKind == JsonValueKind.String ? l.GetString() ?? "" : "";
                if (!LangCode.IsMatch(code)) { problems.Add("\"langs\" contains an invalid language code"); continue; }
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
            var n = kv.Key.ToLowerInvariant();
            if (n.EndsWith(".html") || n.EndsWith(".json")) continue;
            if (kv.Value is null || kv.Value.Length > MaxImageBytes)
                problems.Add($"{kv.Key} is larger than {MaxImageBytes / 1048576} MB (the help window does not show it)");
        }

        var english = map.TryGetValue(Folder + "en.html", out var en) && en is not null && en.Length <= MaxHtmlBytes ? Encoding.UTF8.GetString(en) : "";
        if (root.TryGetProperty("topics", out var topics))
        {
            if (topics.ValueKind != JsonValueKind.Object) problems.Add("\"topics\" must be an object of name -> chapter id");
            else
                foreach (var t in topics.EnumerateObject())
                {
                    var target = t.Value.ValueKind == JsonValueKind.String ? t.Value.GetString() ?? "" : "";
                    if (!TopicName.IsMatch(t.Name) || !TopicName.IsMatch(target))
                    {
                        problems.Add("\"topics\" names and chapter ids use a to z, 0 to 9 and - (at most 64 characters)");
                        break;
                    }
                    if (english.Length > 0 && !HasId(english, target) && !unknown.Contains(target)) unknown.Add(target);
                }
        }
        return new Result(true, problems, langs, unknown, hash, english);
    }

    /// <summary>The HTML has an element with id="<paramref name="id"/>".</summary>
    public static bool HasId(string html, string id) =>
        html.Contains("id=\"" + id + "\"", StringComparison.Ordinal) || html.Contains("id='" + id + "'", StringComparison.Ordinal);

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
