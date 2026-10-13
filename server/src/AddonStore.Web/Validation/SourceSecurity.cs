using System.Text.RegularExpressions;

namespace AddonStore.Web.Validation;

/// <summary>
/// Code security checks on the deposited source (S1.27.0): the weakness classes (CWE) a
/// static analysis of the release scan reports most often in C/C++ plug-ins and their web
/// windows. Pattern checks, not a data-flow analysis: they find typical places early, the
/// reviewer (and the AI review aid) judge them. Third-party folders are left out (the
/// caller passes only the developer's own files). All findings are warnings; admins can
/// make them mandatory as house rules.
/// </summary>
public sealed class SourceSecurity
{
    public const string UnsafeFunction = "SOURCE_UNSAFE_FUNCTION";   // CWE-120/676
    public const string FormatString = "SOURCE_FORMAT_STRING";       // CWE-134
    public const string CommandExec = "SOURCE_COMMAND_EXEC";         // CWE-78
    public const string DllSearchPath = "SOURCE_DLL_SEARCH_PATH";    // CWE-427
    public const string WeakCrypto = "SOURCE_WEAK_CRYPTO";           // CWE-327/328/338
    public const string UnsafeHtml = "SOURCE_UNSAFE_HTML";           // CWE-79
    public const string XmlEntities = "SOURCE_XML_EXTERNAL_ENTITIES"; // CWE-611
    public const string TempFile = "SOURCE_TEMP_FILE";               // CWE-377
    public const string WeakPermissions = "SOURCE_WEAK_PERMISSIONS"; // CWE-732
    public const string HardcodedKey = "SOURCE_HARDCODED_KEY";       // CWE-321/329
    public const string WebViewExposure = "SOURCE_WEBVIEW_EXPOSURE"; // CWE-749/346

    /// <summary>Every code this class can report (the "Code security" report area).</summary>
    public static readonly string[] Codes =
        { UnsafeFunction, FormatString, CommandExec, DllSearchPath, WeakCrypto, UnsafeHtml,
          XmlEntities, TempFile, WeakPermissions, HardcodedKey, WebViewExposure };

    private const RegexOptions Rx = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;

    private static readonly Regex Unsafe = new(
        @"\b(strcpy|wcscpy|_tcscpy|_mbscpy|strcat|wcscat|_tcscat|_mbscat|sprintf|vsprintf|_stprintf|gets|_getws|" +
        @"lstrcpy[AW]?|lstrcpyn[AW]?|lstrcat[AW]?|StrCpy[AW]?|StrCat[AW]?|StrCatBuff[AW]?|wsprintf[AW]?|wvsprintf[AW]?|strncpy|wcsncpy|strncat|wcsncat)\s*\(", Rx);

    // function name -> index of the format argument
    private static readonly Dictionary<string, int> FormatArg = new(StringComparer.Ordinal)
    {
        ["printf"] = 0, ["wprintf"] = 0, ["_tprintf"] = 0,
        ["fprintf"] = 1, ["fwprintf"] = 1, ["_ftprintf"] = 1,
        ["sprintf"] = 1, ["wsprintfA"] = 1, ["wsprintfW"] = 1, ["wsprintf"] = 1,
        ["sprintf_s"] = 2, ["swprintf_s"] = 2, ["_stprintf_s"] = 2, ["swprintf"] = 2, ["snprintf"] = 2, ["_snprintf"] = 2, ["_snwprintf"] = 2,
        ["StringCchPrintfA"] = 2, ["StringCchPrintfW"] = 2, ["StringCchPrintf"] = 2,
        ["StringCbPrintfA"] = 2, ["StringCbPrintfW"] = 2, ["StringCbPrintf"] = 2,
        ["_snprintf_s"] = 3, ["_snwprintf_s"] = 3, ["_sntprintf_s"] = 3,
        ["Format"] = 0, ["AppendFormat"] = 0,   // CString / CStringT members
    };
    private static readonly Regex FormatCall = new(
        @"\b(printf|wprintf|_tprintf|fprintf|fwprintf|_ftprintf|sprintf|wsprintf[AW]?|sprintf_s|swprintf_s|_stprintf_s|swprintf|snprintf|_snprintf|_snwprintf|" +
        @"StringCchPrintf[AW]?|StringCbPrintf[AW]?|_snprintf_s|_snwprintf_s|_sntprintf_s|Format|AppendFormat)\s*\(", Rx);

    private static readonly Regex NamedConstant = new(@"^(k[A-Z]\w*|[A-Z][A-Z0-9_]{2,})$", Rx);
    private static readonly Regex Command = new(@"\b(system|_wsystem|_tsystem|_popen|_wpopen|WinExec)\s*\(", Rx);
    private static readonly Regex CreateProc = new(@"\bCreateProcess[AW]?\s*\(", Rx);
    private static readonly Regex LoadLib = new(@"\bLoadLibrary(Ex)?[AW]?\s*\(", Rx);

    private static readonly (string Label, Regex Rx)[] Crypto =
    {
        ("MD5/MD4", new(@"\b(BCRYPT_MD5_ALGORITHM|BCRYPT_MD4_ALGORITHM|CALG_MD5|CALG_MD4|EVP_md5|MD5_Init)\b|\bMD5\.Create\s*\(|\bnew\s+MD5CryptoServiceProvider\b", Rx)),
        ("SHA-1", new(@"\b(BCRYPT_SHA1_ALGORITHM|CALG_SHA1|CALG_SHA|EVP_sha1|SHA1_Init)\b|\bSHA1\.Create\s*\(|\bnew\s+SHA1(Managed|CryptoServiceProvider)\b", Rx)),
        ("DES/3DES/RC2/RC4", new(@"\b(BCRYPT_(DES|3DES|3DES_112|DESX|RC2|RC4)_ALGORITHM|CALG_(DES|3DES|3DES_112|RC2|RC4)|EVP_(des|rc4|rc2)_\w+)\b|\b(DES|TripleDES|RC2)\.Create\s*\(", Rx)),
        ("ECB mode", new(@"\bBCRYPT_CHAIN_MODE_ECB\b|CipherMode\.ECB\b", Rx)),
        ("rand()", new(@"(?:^|[^\w.>:])(rand|srand)\s*\(", Rx)),
    };

    // S1.27.0, second set (from the release scan's CWE list); searched in the masked code
    // (function and macro names) or in the real code (values inside string literals)
    private static readonly Regex DllDirectory = new(@"\b(SetDllDirectory[AW]?|SetDefaultDllDirectories|AddDllDirectory|SetSearchPathMode)\s*\(", Rx);
    private static readonly (string Label, Regex Rx, bool InLiterals)[] Xml =
    {
        ("MSXML 3 (DTDs allowed)", new(@"\b(CLSID_DOMDocument30|CLSID_FreeThreadedDOMDocument30|DOMDocument30)\b", Rx), false),
        ("MSXML 3 by ProgID", new(@"Msxml2\.(FreeThreaded)?DOMDocument\.3\.0", Rx), true),
        ("ProhibitDTD switched off", new(@"ProhibitDTD""\s*\)?\s*,\s*(_variant_t\s*\(\s*)?(false|VARIANT_FALSE|0)\b", Rx), true),
        ("external resolution on", new(@"\bput_resolveExternals\s*\(\s*VARIANT_TRUE|\bresolveExternals\s*=\s*(true|VARIANT_TRUE)", Rx), false),
        ("XmlLite DTD processing", new(@"\bDtdProcessing_Parse\b", Rx), false),
        ("libxml2 entities/DTD loading", new(@"\bXML_PARSE_(NOENT|DTDLOAD|DTDATTR)\b", Rx), false),
    };
    private static readonly Regex TempName = new(@"\b(tmpnam|_tmpnam|_wtmpnam|_ttmpnam|tmpnam_s|_wtmpnam_s|_tempnam|_wtempnam|_mktemp|_wmktemp|_mktemp_s|_wmktemp_s|mktemp|tmpfile)\s*\(", Rx);
    private static readonly (string Label, Regex Rx, bool InLiterals)[] Permissions =
    {
        ("NULL DACL", new(@"\bSetSecurityDescriptorDacl\s*\([^;,]*,\s*TRUE\s*,\s*(NULL|nullptr|0)\s*,", Rx), false),
        ("SDDL granting Everyone/Users full access", new(@"\(A;[^;)]*;(GA|FA|KA|GRGW[^;]*);;;(WD|AU|BU|AN|IU)\)", Rx), true),
    };
    private static readonly Regex KeyArray = new(
        @"\b(static\s+)?(const\s+)?(constexpr\s+)?(BYTE|UCHAR|unsigned\s+char|uint8_t|char)\s+(\w*(?:key|Key|KEY|iv|Iv|IV|salt|Salt|SALT|secret|Secret|SECRET)\w*)\s*\[[^\]]*\]\s*=\s*\{\s*(0x[0-9A-Fa-f]{1,2}|[1-9][0-9]*|'.')\s*,", Rx);   // values, not {0}
    private static readonly (string Label, Regex Rx, bool InLiterals)[] WebView =
    {
        ("AddHostObjectToScript", new(@"\bAddHostObjectToScript\s*\(", Rx), false),
        ("developer tools on", new(@"\bput_AreDevToolsEnabled\s*\(\s*(TRUE|true|1)\s*\)", Rx), false),
        ("script access to host objects", new(@"\bput_AreHostObjectsAllowed\s*\(\s*(TRUE|true|1)\s*\)", Rx), false),
    };

    private static readonly (string Label, Regex Rx)[] Html =
    {
        ("srcdoc", new(@"\.srcdoc\s*=|\bsrcdoc\s*=\s*[""'][^""']*\$\{", Rx)),
        ("jQuery .html()", new(@"[\w)\]$]\.html\(\s*[^)\s]", Rx)),   // "file.html (x)" in a comment is no call
        ("postMessage to any origin", new(@"\bpostMessage\s*\([^;]*,\s*['""]\*['""]\s*\)", Rx)),
        ("innerHTML/outerHTML", new(@"\.(innerHTML|outerHTML)\s*\+?=\s*[^=\s'""`]", Rx)),
        ("insertAdjacentHTML", new(@"\binsertAdjacentHTML\s*\(", Rx)),
        ("document.write", new(@"\bdocument\.write(ln)?\s*\(", Rx)),
        ("eval", new(@"(?:^|[^\w.])eval\s*\(", Rx)),
        ("new Function", new(@"\bnew\s+Function\s*\(", Rx)),
        ("setTimeout/setInterval with a string", new(@"\bset(Timeout|Interval)\s*\(\s*['""`]", Rx)),
    };

    private const int MaxPerCode = 200;
    private readonly Dictionary<string, SortedSet<string>> _found = new();

    /// <summary>Codes with their places ("file:line function"), only codes that were found.</summary>
    public IReadOnlyDictionary<string, SortedSet<string>> Findings => _found;

    public static bool IsNative(string ext) => ext is ".c" or ".cc" or ".cpp" or ".cxx" or ".h" or ".hh" or ".hpp" or ".hxx" or ".inl";
    public static bool IsWeb(string ext) => ext is ".html" or ".htm" or ".js" or ".mjs" or ".ts";

    /// <summary>Scans one of the developer's own files.</summary>
    public void Scan(string file, string ext, string text)
    {
        if (IsNative(ext) || ext == ".cs")
        {
            var code = StripComments(text);
            // names are searched in a copy whose string literals are blanked (same length, so the
            // positions match); arguments are read from the real code
            var masked = MaskLiterals(code);
            if (IsNative(ext))
            {
                ScanNative(file, code, masked);
                ScanPatterns(file, code, masked);
            }
            foreach (var (label, rx) in Crypto)
                foreach (Match m in rx.Matches(masked))
                    Add(WeakCrypto, file, code, m.Index, label);
        }
        else if (IsWeb(ext))
        {
            // comments are not stripped: "//" also starts URLs in HTML and JS strings
            foreach (var (label, rx) in Html)
                foreach (Match m in rx.Matches(text))
                    Add(UnsafeHtml, file, text, m.Index, label);
        }
    }

    private void ScanNative(string file, string code, string masked)
    {
        foreach (Match m in Unsafe.Matches(masked))
            if (!IsMember(masked, m.Index)) Add(UnsafeFunction, file, code, m.Index, m.Groups[1].Value);

        foreach (Match m in FormatCall.Matches(masked))
        {
            var name = m.Groups[1].Value;
            var member = IsMember(masked, m.Index);
            if (name is "Format" or "AppendFormat" ? !member : member) continue;   // Format only as a member, the rest only as free functions
            var args = Arguments(code, m.Index + m.Length);
            var at = FormatArg[name];
            if (args is null || args.Count <= at) continue;
            // the secure CRT has template forms without the size (sprintf_s(buf, "%s", x)): the
            // format may then sit one place earlier; a named constant (kFormat, FORMAT_X) is fine
            // (an IDS_ string id is a format from the resources: CString::Format(IDS_X, ...))
            bool Constant(int i) => i >= 0 && i < args.Count &&
                (IsLiteral(args[i]) || (NamedConstant.IsMatch(args[i]) && !args[i].StartsWith("IDS_", StringComparison.Ordinal)));
            // (one place earlier only a literal counts: _TRUNCATE or a size macro is no format)
            if (!Constant(at) && !(at > 0 && IsLiteral(args[at - 1]))) Add(FormatString, file, code, m.Index, name);
        }

        foreach (Match m in Command.Matches(masked))
            if (!IsMember(masked, m.Index) || masked.AsSpan(Math.Max(0, m.Index - 5), Math.Min(5, m.Index)).EndsWith("std::"))
                Add(CommandExec, file, code, m.Index, m.Groups[1].Value);
        foreach (Match m in CreateProc.Matches(masked))
        {
            var args = Arguments(code, m.Index + m.Length);
            if (args is { Count: > 1 } && args[0].Trim() is "NULL" or "nullptr" or "0")
                Add(CommandExec, file, code, m.Index, "CreateProcess without a program path");
        }

        foreach (Match m in LoadLib.Matches(masked))
        {
            var args = Arguments(code, m.Index + m.Length);
            if (args is null || args.Count == 0) continue;
            var ex = m.Groups[1].Success;
            if (ex && args.Count >= 3 && args[2].Contains("LOAD_LIBRARY_SEARCH", StringComparison.Ordinal)) continue;
            var lit = LiteralText(args[0]);
            // a literal name without a folder is searched along the DLL search path
            if (lit is not null && lit.IndexOfAny(new[] { '\\', '/', ':' }) < 0)
                Add(DllSearchPath, file, code, m.Index, (ex ? "LoadLibraryEx" : "LoadLibrary") + " \"" + Short(lit) + "\"");
        }
    }

    /// <summary>The second set (S1.27.0): process-wide DLL folders, XML entities, temp names, permissions, keys, WebView2.</summary>
    private void ScanPatterns(string file, string code, string masked)
    {
        foreach (Match m in DllDirectory.Matches(masked))
            if (!IsMember(masked, m.Index))
                Add(DllSearchPath, file, code, m.Index, m.Groups[1].Value + " (changes the DLL search of the whole Power PDF process)");
        foreach (var (label, rx, lit) in Xml)
            foreach (Match m in rx.Matches(lit ? code : masked))
                Add(XmlEntities, file, code, m.Index, label);
        foreach (Match m in TempName.Matches(masked))
            if (!IsMember(masked, m.Index)) Add(TempFile, file, code, m.Index, m.Groups[1].Value);
        foreach (var (label, rx, lit) in Permissions)
            foreach (Match m in rx.Matches(lit ? code : masked))
                Add(WeakPermissions, file, code, m.Index, label);
        foreach (Match m in KeyArray.Matches(masked))
            Add(HardcodedKey, file, code, m.Index, "byte array " + m.Groups[5].Value);
        foreach (var (label, rx, lit) in WebView)
            foreach (Match m in rx.Matches(lit ? code : masked))
                Add(WebViewExposure, file, code, m.Index, label);
    }

    private void Add(string code, string file, string text, int index, string what)
    {
        if (!_found.TryGetValue(code, out var set)) _found[code] = set = new SortedSet<string>(StringComparer.Ordinal);
        if (set.Count >= MaxPerCode) return;
        set.Add($"{file}:{Line(text, index)} {what}");
    }

    private static int Line(string text, int index)
    {
        var n = 1;
        for (int i = 0; i < index && i < text.Length; i++) if (text[i] == '\n') n++;
        return n;
    }

    /// <summary>Comments replaced by spaces of the same length (line numbers stay right); string literals are kept.</summary>
    private static string StripComments(string text)
    {
        if (!text.Contains("//") && !text.Contains("/*")) return text;
        // a "//" or "/*" inside a string literal must survive: blank out comments only outside strings
        var chars = text.ToCharArray();
        int i = 0;
        while (i < chars.Length)
        {
            var c = chars[i];
            if (c is '"' or '\'')
            {
                var q = c; i++;
                while (i < chars.Length && chars[i] != q && chars[i] != '\n') { if (chars[i] == '\\') i++; i++; }
                i++;
            }
            else if (c == '/' && i + 1 < chars.Length && chars[i + 1] == '/')
            {
                while (i < chars.Length && chars[i] != '\n') chars[i++] = ' ';
            }
            else if (c == '/' && i + 1 < chars.Length && chars[i + 1] == '*')
            {
                chars[i++] = ' '; chars[i++] = ' ';
                while (i < chars.Length && !(chars[i] == '*' && i + 1 < chars.Length && chars[i + 1] == '/'))
                { if (chars[i] != '\n') chars[i] = ' '; i++; }
                if (i < chars.Length) { chars[i++] = ' '; if (i < chars.Length) chars[i++] = ' '; }
            }
            else i++;
        }
        return new string(chars);
    }

    /// <summary>The contents of string and character literals replaced by spaces; quotes and line breaks stay.</summary>
    private static string MaskLiterals(string code)
    {
        var chars = code.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (chars[i] is not ('"' or '\'')) continue;
            var q = chars[i++];
            while (i < chars.Length && chars[i] != q && chars[i] != '\n')
            {
                if (chars[i] == '\\' && i + 1 < chars.Length && chars[i + 1] != '\n') { chars[i++] = ' '; }
                chars[i++] = ' ';
            }
        }
        return new string(chars);
    }

    /// <summary>True for "obj.name(", "p->name(" or "ns::name(" (a member or another library's function).</summary>
    private static bool IsMember(string code, int index)
    {
        int i = index - 1;
        while (i >= 0 && code[i] is ' ' or '\t') i--;
        if (i < 0) return false;
        return code[i] == '.' || (code[i] == '>' && i > 0 && code[i - 1] == '-') || (code[i] == ':' && i > 0 && code[i - 1] == ':');
    }

    /// <summary>The top-level arguments of a call whose "(" ends at <paramref name="start"/>; null when unbalanced.</summary>
    private static List<string>? Arguments(string code, int start)
    {
        var args = new List<string>();
        int depth = 0, from = start;
        for (int i = start; i < code.Length && i < start + 4000; i++)
        {
            var c = code[i];
            if (c is '"' or '\'')
            {
                var q = c; i++;
                while (i < code.Length && code[i] != q && code[i] != '\n') { if (code[i] == '\\') i++; i++; }
                continue;
            }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0) { args.Add(code[from..i].Trim()); return args; }
                depth--;
            }
            else if (c == ',' && depth == 0) { args.Add(code[from..i].Trim()); from = i + 1; }
        }
        return null;
    }

    /// <summary>A string literal ("...", L"...", u8"...", _T("..."), TEXT("...")), optionally several in a row.</summary>
    private static bool IsLiteral(string arg) => LiteralText(arg) is not null;

    private static string? LiteralText(string arg)
    {
        var a = arg.Trim();
        foreach (var wrap in new[] { "_T(", "TEXT(", "_TEXT(" })
            if (a.StartsWith(wrap, StringComparison.Ordinal) && a.EndsWith(')')) a = a[wrap.Length..^1].Trim();
        foreach (var prefix in new[] { "u8", "L", "u", "U" })
            if (a.StartsWith(prefix + "\"", StringComparison.Ordinal)) { a = a[prefix.Length..]; break; }
        if (a.Length < 2 || a[0] != '"' || a[^1] != '"') return null;
        // adjacent literals ("a" "b") count as one
        return a[1..^1];
    }

    private static string Short(string s) => s.Length > 40 ? s[..40] + "..." : s;
}
