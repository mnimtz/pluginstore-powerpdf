// helpcore.h - the parts of the central help that touch foreign data (C2.1.0):
// reading files of add-ons safely, a small JSON reader for help.json/manifest.json, the HTML
// allow list of the help format (docs/concepts/addon-help-format.md, version 1) and the list of
// installed helps. Everything an add-on ships is untrusted: size limits, no reparse points, no path
// from the data, no exception, no recursion without a depth limit.
//
// Header only, plain Win32 and the C++ library (no MFC, no Power PDF SDK), so client/tests can test it.

#pragma once
#include <windows.h>
#include <wincrypt.h>
#include <string>
#include <vector>
#include <functional>
#include <cstring>
#include <clocale>
#include <cstdlib>
#include <cwctype>
#include <cctype>

#pragma comment(lib, "crypt32.lib")

namespace pshelpcore {

// ------------------------------------------------------------------ limits
const LONGLONG kMaxHtmlBytes     = 4 * 1024 * 1024;   // one <lang>.html
const LONGLONG kMaxJsonBytes     = 256 * 1024;         // help.json
const LONGLONG kMaxManifestBytes = 1024 * 1024;        // manifest.json of the package
const LONGLONG kMaxImageBytes    = 2 * 1024 * 1024;    // one picture of a help
const size_t   kMaxImagesPerDoc  = 64;
const size_t   kMaxAddons        = 200;
const size_t   kMaxTopics        = 200;
const int      kMaxJsonDepth     = 32;

// ------------------------------------------------------------------ text helpers
// strict: invalid UTF-8 gives "" (JSON strings); otherwise it becomes U+FFFD
inline std::wstring W(const std::string& s, bool strict = false)
{
    if (s.empty()) return std::wstring();
    const DWORD fl = strict ? MB_ERR_INVALID_CHARS : 0;
    int n = MultiByteToWideChar(CP_UTF8, fl, s.data(), (int)s.size(), NULL, 0);
    if (n <= 0) return std::wstring();
    std::wstring w((size_t)n, L'\0');
    MultiByteToWideChar(CP_UTF8, fl, s.data(), (int)s.size(), &w[0], n);
    return w;
}

inline std::string U8(const std::wstring& w)
{
    if (w.empty()) return std::string();
    int n = WideCharToMultiByte(CP_UTF8, 0, w.data(), (int)w.size(), NULL, 0, NULL, NULL);
    if (n <= 0) return std::string();
    std::string s((size_t)n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.data(), (int)w.size(), &s[0], n, NULL, NULL);
    return s;
}

inline char Lower(char c) { return (c >= 'A' && c <= 'Z') ? (char)(c - 'A' + 'a') : c; }

inline bool IEquals(const std::string& a, const char* b)
{
    size_t n = strlen(b);
    if (a.size() != n) return false;
    for (size_t i = 0; i < n; ++i) if (Lower(a[i]) != Lower(b[i])) return false;
    return true;
}

// Names and ids: [a-z0-9-], 1 to 64 characters (topic names, chapter ids, help.json "topics").
inline bool IsTopicName(const std::wstring& s)
{
    if (s.empty() || s.size() > 64) return false;
    for (wchar_t c : s) if (!((c >= L'a' && c <= L'z') || (c >= L'0' && c <= L'9') || c == L'-')) return false;
    return true;
}

// Package ids ("com.tungsten.mailmerge"): the store's rule [a-z0-9][a-z0-9._-]{2,100}.
inline bool IsAddonId(const std::wstring& s)
{
    if (s.size() < 3 || s.size() > 101) return false;
    for (size_t i = 0; i < s.size(); ++i)
    {
        wchar_t c = s[i];
        bool ok = (c >= L'a' && c <= L'z') || (c >= L'0' && c <= L'9') || (i > 0 && (c == L'.' || c == L'_' || c == L'-'));
        if (!ok) return false;
    }
    return true;
}

// ------------------------------------------------------------------ safe file access
inline bool IsReparseOrMissing(const std::wstring& path, bool wantDir)
{
    DWORD a = GetFileAttributesW(path.c_str());
    if (a == INVALID_FILE_ATTRIBUTES) return true;
    if (a & FILE_ATTRIBUTE_REPARSE_POINT) return true;
    return wantDir != ((a & FILE_ATTRIBUTE_DIRECTORY) != 0);
}

// Reads a whole regular file of at most maxBytes. Refuses links (reparse points) at the file
// itself; the caller checks the folders on the way (SafeDir).
inline bool ReadFileLimited(const std::wstring& path, LONGLONG maxBytes, std::string& out)
{
    out.clear();
    if (path.empty() || path.size() > 32000) return false;
    HANDLE f = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING,
                           FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_SEQUENTIAL_SCAN, NULL);
    if (f == INVALID_HANDLE_VALUE) return false;
    bool ok = false;
    BY_HANDLE_FILE_INFORMATION fi = {};
    if (GetFileInformationByHandle(f, &fi) && !(fi.dwFileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY)) &&
        GetFileType(f) == FILE_TYPE_DISK)
    {
        ULONGLONG size = ((ULONGLONG)fi.nFileSizeHigh << 32) | fi.nFileSizeLow;
        if (size <= (ULONGLONG)maxBytes)
        {
            out.resize((size_t)size);
            DWORD got = 0;
            ok = size == 0 || (ReadFile(f, &out[0], (DWORD)size, &got, NULL) && got == size);
        }
    }
    CloseHandle(f);
    if (!ok) out.clear();
    return ok;
}

// A folder below base, each step a real folder (no junction, no symbolic link).
inline bool SafeDir(const std::wstring& base, const std::vector<std::wstring>& steps, std::wstring& out)
{
    std::wstring p = base;
    if (IsReparseOrMissing(p, true)) return false;
    for (const auto& s : steps)
    {
        p += L"\\" + s;
        if (IsReparseOrMissing(p, true)) return false;
    }
    out = p;
    return true;
}

// ------------------------------------------------------------------ JSON (small, total, no exceptions)
struct JVal
{
    enum Type { TNull, TBool, TNum, TStr, TArr, TObj } type = TNull;
    bool b = false;
    double n = 0;
    std::wstring s;
    std::vector<JVal> a;
    std::vector<std::pair<std::wstring, JVal>> o;

    const JVal* Get(const wchar_t* key) const
    {
        if (type != TObj) return nullptr;
        for (const auto& kv : o) if (kv.first == key) return &kv.second;
        return nullptr;
    }
    std::wstring Str(const wchar_t* key) const
    {
        const JVal* v = Get(key);
        return v && v->type == JVal::TStr ? v->s : std::wstring();
    }
};

class JsonReader
{
public:
    explicit JsonReader(const std::string& t) : m_t(t) {}
    bool Parse(JVal& out)
    {
        m_p = 0;
        if (m_t.size() >= 3 && (unsigned char)m_t[0] == 0xEF && (unsigned char)m_t[1] == 0xBB && (unsigned char)m_t[2] == 0xBF) m_p = 3;
        if (!Value(out, 0)) return false;
        Ws();
        return m_p == m_t.size();
    }

private:
    const std::string& m_t;
    size_t m_p = 0;

    void Ws() { while (m_p < m_t.size() && (m_t[m_p] == ' ' || m_t[m_p] == '\t' || m_t[m_p] == '\r' || m_t[m_p] == '\n')) ++m_p; }
    bool Lit(const char* w)
    {
        size_t n = strlen(w);
        if (m_t.compare(m_p, n, w) != 0) return false;
        m_p += n;
        return true;
    }
    static int Hex(char c) { return c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1; }
    bool Hex4(unsigned& v)
    {
        if (m_p + 4 > m_t.size()) return false;
        v = 0;
        for (int i = 0; i < 4; ++i) { int h = Hex(m_t[m_p + i]); if (h < 0) return false; v = v * 16 + (unsigned)h; }
        m_p += 4;
        return true;
    }
    bool String(std::wstring& out)
    {
        if (m_p >= m_t.size() || m_t[m_p] != '"') return false;
        ++m_p;
        std::string raw;
        std::wstring w;
        auto flush = [&]() { if (!raw.empty()) { std::wstring x = W(raw, true); if (x.empty()) return false; w += x; raw.clear(); } return true; };
        while (m_p < m_t.size())
        {
            char c = m_t[m_p++];
            if (c == '"') { if (!flush()) return false; out.swap(w); return true; }
            if ((unsigned char)c < 0x20) return false;
            if (c != '\\') { raw += c; if (raw.size() > 1024 * 1024) return false; continue; }
            if (!flush() || m_p >= m_t.size()) return false;
            char e = m_t[m_p++];
            switch (e)
            {
            case '"': w += L'"'; break;   case '\\': w += L'\\'; break; case '/': w += L'/'; break;
            case 'b': w += L'\b'; break;  case 'f': w += L'\f'; break;  case 'n': w += L'\n'; break;
            case 'r': w += L'\r'; break;  case 't': w += L'\t'; break;
            case 'u':
            {
                unsigned u = 0;
                if (!Hex4(u)) return false;
                if (u >= 0xD800 && u <= 0xDBFF)
                {
                    unsigned lo = 0;
                    if (m_p + 2 > m_t.size() || m_t[m_p] != '\\' || m_t[m_p + 1] != 'u') return false;
                    m_p += 2;
                    if (!Hex4(lo) || lo < 0xDC00 || lo > 0xDFFF) return false;
                    w += (wchar_t)u; w += (wchar_t)lo;
                }
                else if (u >= 0xDC00 && u <= 0xDFFF) return false;
                else w += (wchar_t)u;
                break;
            }
            default: return false;
            }
        }
        return false;
    }
    bool Number(double& v)
    {
        size_t s = m_p;
        if (m_p < m_t.size() && m_t[m_p] == '-') ++m_p;
        size_t d = m_p;
        while (m_p < m_t.size() && m_t[m_p] >= '0' && m_t[m_p] <= '9') ++m_p;
        if (m_p == d) return false;
        if (m_p < m_t.size() && m_t[m_p] == '.') { ++m_p; size_t f = m_p; while (m_p < m_t.size() && m_t[m_p] >= '0' && m_t[m_p] <= '9') ++m_p; if (m_p == f) return false; }
        if (m_p < m_t.size() && (m_t[m_p] == 'e' || m_t[m_p] == 'E'))
        {
            ++m_p;
            if (m_p < m_t.size() && (m_t[m_p] == '+' || m_t[m_p] == '-')) ++m_p;
            size_t f = m_p; while (m_p < m_t.size() && m_t[m_p] >= '0' && m_t[m_p] <= '9') ++m_p;
            if (m_p == f) return false;
        }
        if (m_p - s > 40) return false;
        std::string num = m_t.substr(s, m_p - s);
        _locale_t c = _create_locale(LC_NUMERIC, "C");
        v = c ? _strtod_l(num.c_str(), NULL, c) : strtod(num.c_str(), NULL);
        if (c) _free_locale(c);
        return true;
    }
    bool Value(JVal& v, int depth)
    {
        if (depth > kMaxJsonDepth) return false;
        Ws();
        if (m_p >= m_t.size()) return false;
        char c = m_t[m_p];
        if (c == '{')
        {
            v.type = JVal::TObj; ++m_p; Ws();
            if (m_p < m_t.size() && m_t[m_p] == '}') { ++m_p; return true; }
            for (;;)
            {
                Ws();
                std::wstring k;
                if (!String(k)) return false;
                Ws();
                if (m_p >= m_t.size() || m_t[m_p] != ':') return false;
                ++m_p;
                JVal x;
                if (!Value(x, depth + 1)) return false;
                if (v.o.size() >= 10000) return false;
                v.o.emplace_back(std::move(k), std::move(x));
                Ws();
                if (m_p < m_t.size() && m_t[m_p] == ',') { ++m_p; continue; }
                if (m_p < m_t.size() && m_t[m_p] == '}') { ++m_p; return true; }
                return false;
            }
        }
        if (c == '[')
        {
            v.type = JVal::TArr; ++m_p; Ws();
            if (m_p < m_t.size() && m_t[m_p] == ']') { ++m_p; return true; }
            for (;;)
            {
                JVal x;
                if (!Value(x, depth + 1)) return false;
                if (v.a.size() >= 10000) return false;
                v.a.push_back(std::move(x));
                Ws();
                if (m_p < m_t.size() && m_t[m_p] == ',') { ++m_p; continue; }
                if (m_p < m_t.size() && m_t[m_p] == ']') { ++m_p; return true; }
                return false;
            }
        }
        if (c == '"') { v.type = JVal::TStr; return String(v.s); }
        if (c == 't') { v.type = JVal::TBool; v.b = true; return Lit("true"); }
        if (c == 'f') { v.type = JVal::TBool; v.b = false; return Lit("false"); }
        if (c == 'n') { v.type = JVal::TNull; return Lit("null"); }
        v.type = JVal::TNum;
        return Number(v.n);
    }
};

inline bool ParseJson(const std::string& text, JVal& out)
{
    out = JVal();
    try { JsonReader r(text); return r.Parse(out); }
    catch (...) { out = JVal(); return false; }   // bad_alloc on hostile data: no help, no crash
}

// ------------------------------------------------------------------ help.json
struct HelpMeta
{
    std::wstring addon;
    std::vector<std::wstring> langs;
    std::vector<std::pair<std::wstring, std::wstring>> topics;   // name -> chapter id
};

// format "tungsten-addon-help", version 1, addon id, langs with "en"; topics optional.
inline bool ParseHelpMeta(const std::string& text, HelpMeta& m)
{
    m = HelpMeta();
    JVal j;
    if (!ParseJson(text, j) || j.type != JVal::TObj) return false;
    if (j.Str(L"format") != L"tungsten-addon-help") return false;
    const JVal* v = j.Get(L"version");
    if (!v || v->type != JVal::TNum || v->n != 1) return false;
    m.addon = j.Str(L"addon");
    if (!IsAddonId(m.addon)) return false;
    const JVal* langs = j.Get(L"langs");
    if (langs && langs->type == JVal::TArr)
        for (const auto& l : langs->a)
        {
            if (l.type != JVal::TStr || l.s.size() != 2 || !(l.s[0] >= L'a' && l.s[0] <= L'z' && l.s[1] >= L'a' && l.s[1] <= L'z')) continue;
            bool dup = false;
            for (const auto& x : m.langs) if (x == l.s) dup = true;
            if (!dup && m.langs.size() < 50) m.langs.push_back(l.s);
        }
    bool en = false;
    for (const auto& x : m.langs) if (x == L"en") en = true;
    if (!en) return false;
    const JVal* t = j.Get(L"topics");
    if (t && t->type == JVal::TObj)
        for (const auto& kv : t->o)
            if (IsTopicName(kv.first) && kv.second.type == JVal::TStr && IsTopicName(kv.second.s) && m.topics.size() < kMaxTopics)
                m.topics.emplace_back(kv.first, kv.second.s);
    return true;
}

// A topic of the add-on: a name of "topics" -> its chapter id; a valid id stays itself; else "".
inline std::wstring ResolveTopic(const HelpMeta& m, const std::wstring& topic)
{
    if (!IsTopicName(topic)) return std::wstring();
    for (const auto& kv : m.topics) if (kv.first == topic) return kv.second;
    return topic;
}

// The language file to show: the UI language when the help has it, else English.
inline std::wstring PickLang(const HelpMeta& m, const std::wstring& uiLang)
{
    std::wstring l = uiLang.size() >= 2 ? uiLang.substr(0, 2) : std::wstring();
    for (auto& c : l) c = (wchar_t)towlower(c);
    for (const auto& x : m.langs) if (x == l) return l;
    return L"en";
}

// Display name of a package from its manifest.json ("name": text or {lang: text}).
inline std::wstring ManifestName(const JVal& man, const std::wstring& lang)
{
    const JVal* n = man.Get(L"name");
    std::wstring out;
    if (n && n->type == JVal::TStr) out = n->s;
    else if (n && n->type == JVal::TObj)
    {
        out = n->Str(lang.c_str());
        if (out.empty()) out = n->Str(L"en");
        if (out.empty()) for (const auto& kv : n->o) if (kv.second.type == JVal::TStr && !kv.second.s.empty()) { out = kv.second.s; break; }
    }
    std::wstring clean;
    for (wchar_t c : out) if (c >= 0x20 && c != 0x7F && clean.size() < 80) clean += c;
    return clean;
}

// ------------------------------------------------------------------ HTML allow list
// Tags of the format. Everything else goes; the content of these goes with it.
inline bool AllowedTag(const std::string& t)
{
    static const char* k[] = { "h1", "h2", "h3", "p", "ul", "ol", "li", "table", "thead", "tbody", "tr", "th", "td", "b",
                               "strong", "i", "em", "code", "pre", "br", "div", "span", "a", "img" };
    for (const char* x : k) if (t == x) return true;
    return false;
}
inline bool DropWithContent(const std::string& t)
{
    static const char* k[] = { "script", "style", "head", "title", "template", "noscript", "iframe", "frame", "frameset",
                               "object", "embed", "applet", "svg", "math", "textarea", "select", "option", "button",
                               "form", "canvas", "audio", "video", "xmp", "plaintext", "noembed", "noframes", "dialog" };
    for (const char* x : k) if (t == x) return true;
    return false;
}
inline bool VoidTag(const std::string& t)
{
    static const char* k[] = { "br", "img", "hr", "meta", "link", "input", "wbr", "col", "area", "base", "source", "track", "param", "keygen" };
    for (const char* x : k) if (t == x) return true;
    return false;
}

inline std::string EscText(const std::string& s)
{
    std::string o;
    o.reserve(s.size());
    for (char c : s)
    {
        if (c == '<') o += "&lt;";
        else if (c == '>') o += "&gt;";
        else if (c == '"') o += "&quot;";
        else o += c;
    }
    return o;
}

// Keeps a valid character reference, turns a lone "&" into "&amp;".
inline size_t CopyAmp(const std::string& s, size_t i, std::string& o)
{
    size_t j = i + 1;
    if (j < s.size() && s[j] == '#')
    {
        ++j;
        bool hex = j < s.size() && (s[j] == 'x' || s[j] == 'X');
        if (hex) ++j;
        size_t d = j;
        while (j < s.size() && j - d < 7 && (hex ? isxdigit((unsigned char)s[j]) != 0 : (s[j] >= '0' && s[j] <= '9'))) ++j;
        if (j > d && j < s.size() && s[j] == ';') { o.append(s, i, j + 1 - i); return j + 1; }
    }
    else
    {
        size_t d = j;
        while (j < s.size() && j - d < 12 && ((s[j] >= 'a' && s[j] <= 'z') || (s[j] >= 'A' && s[j] <= 'Z') || (s[j] >= '0' && s[j] <= '9'))) ++j;
        if (j > d && j < s.size() && s[j] == ';') { o.append(s, i, j + 1 - i); return j + 1; }
    }
    o += "&amp;";
    return i + 1;
}

inline std::string CleanText(const std::string& s)
{
    std::string o;
    o.reserve(s.size());
    for (size_t i = 0; i < s.size();)
    {
        char c = s[i];
        if (c == '&') { i = CopyAmp(s, i, o); continue; }
        if (c == '<') o += "&lt;";
        else if (c == '>') o += "&gt;";
        else if (c == '\0') { /* drop */ }
        else o += c;
        ++i;
    }
    return o;
}

// Attribute values: only the few entities a URL or id may carry are decoded; any other "&" makes the value invalid.
inline bool DecodeAttr(const std::string& v, std::string& out)
{
    out.clear();
    for (size_t i = 0; i < v.size(); ++i)
    {
        if (v[i] != '&') { out += v[i]; continue; }
        static const struct { const char* e; char c; } k[] = { { "&amp;", '&' }, { "&quot;", '"' }, { "&#39;", '\'' }, { "&lt;", '<' }, { "&gt;", '>' } };
        bool ok = false;
        for (const auto& x : k)
            if (v.compare(i, strlen(x.e), x.e) == 0) { out += x.c; i += strlen(x.e) - 1; ok = true; break; }
        if (!ok) return false;
    }
    return true;
}

inline bool IsIdValue(const std::string& v)
{
    if (v.empty() || v.size() > 100) return false;
    for (char c : v) if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_')) return false;
    return true;
}

// https:// with a host and only plain URL characters (no space, quote, backslash, angle bracket, "@" user info).
inline bool IsHttpsUrl(const std::string& v)
{
    if (v.size() < 12 || v.size() > 2000 || v.compare(0, 8, "https://") != 0) return false;
    size_t hostEnd = v.find_first_of("/?#", 8);
    std::string host = v.substr(8, hostEnd == std::string::npos ? std::string::npos : hostEnd - 8);
    if (host.empty() || host.find_first_of("@:%") != std::string::npos || host[0] == '.' || host[0] == '-') return false;
    for (char c : host) if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '-')) return false;
    for (char c : v)
    {
        unsigned char u = (unsigned char)c;
        if (u <= 0x20 || u >= 0x7F || c == '"' || c == '\'' || c == '<' || c == '>' || c == '\\' || c == '`' || c == '{' || c == '}' || c == '|' || c == '^') return false;
    }
    return true;
}

// A picture of the help: a relative name inside docs\help (sub folders allowed), .png/.jpg/.jpeg.
inline bool IsHelpImagePath(const std::string& v)
{
    if (v.empty() || v.size() > 200 || v[0] == '/' || v[0] == '.' || v.find("..") != std::string::npos || v.find("//") != std::string::npos) return false;
    for (char c : v) if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.' || c == '/')) return false;
    std::string l;
    for (char c : v) l += Lower(c);
    auto ends = [&](const char* e) { size_t n = strlen(e); return l.size() > n && l.compare(l.size() - n, n, e) == 0; };
    return ends(".png") || ends(".jpg") || ends(".jpeg");
}

struct CleanResult
{
    std::string html;                      // allowed markup only
    std::vector<std::string> links;        // https:// addresses in the help (the only ones the window opens)
    size_t dropped = 0;                    // tags or attributes removed (for the log)
};

// imageData(rel) returns a data: URI for an allowed picture, or "" (the picture is dropped).
inline CleanResult CleanHtml(const std::string& doc, const std::function<std::string(const std::string&)>& imageData = nullptr)
{
    CleanResult r;
    // the content of <body> (the whole text when there is none)
    std::string low;
    low.reserve(doc.size());
    for (char c : doc) low += Lower(c);
    size_t from = 0, to = doc.size();
    size_t b = low.find("<body");
    if (b != std::string::npos) { size_t gt = low.find('>', b); if (gt != std::string::npos) from = gt + 1; }
    size_t e = low.rfind("</body");
    if (e != std::string::npos && e >= from) to = e;
    const std::string& s = doc;
    std::string& o = r.html;
    o.reserve(to - from);
    std::vector<std::string> open;   // written start tags
    size_t images = 0;
    size_t i = from;
    while (i < to)
    {
        char c = s[i];
        if (c != '<')
        {
            size_t n = s.find('<', i);
            if (n == std::string::npos || n > to) n = to;
            o += CleanText(s.substr(i, n - i));
            i = n;
            continue;
        }
        // comment, doctype, processing instruction, CDATA
        if (s.compare(i, 4, "<!--") == 0) { size_t x = s.find("-->", i + 4); i = x == std::string::npos || x + 3 > to ? to : x + 3; ++r.dropped; continue; }
        if (i + 1 < to && (s[i + 1] == '!' || s[i + 1] == '?')) { size_t x = s.find('>', i); i = x == std::string::npos || x >= to ? to : x + 1; ++r.dropped; continue; }
        bool closing = i + 1 < to && s[i + 1] == '/';
        size_t p = i + (closing ? 2 : 1);
        size_t ns = p;
        while (p < to && ((s[p] >= 'a' && s[p] <= 'z') || (s[p] >= 'A' && s[p] <= 'Z') || (s[p] >= '0' && s[p] <= '9'))) ++p;
        if (p == ns) { o += "&lt;"; ++i; continue; }   // a lone "<"
        std::string name;
        for (size_t k = ns; k < p; ++k) name += Lower(s[k]);
        // attributes up to ">" (quotes respected)
        std::vector<std::pair<std::string, std::string>> attrs;
        bool selfClose = false;
        for (;;)
        {
            while (p < to && (s[p] == ' ' || s[p] == '\t' || s[p] == '\r' || s[p] == '\n' || s[p] == '\f')) ++p;
            if (p >= to) break;
            if (s[p] == '>') { ++p; break; }
            if (s[p] == '/') { selfClose = true; ++p; continue; }
            size_t as = p;
            while (p < to && s[p] != '=' && s[p] != '>' && s[p] != '/' && s[p] != ' ' && s[p] != '\t' && s[p] != '\r' && s[p] != '\n' && s[p] != '\f') ++p;
            std::string an;
            for (size_t k = as; k < p; ++k) an += Lower(s[k]);
            if (an.empty()) { ++p; continue; }
            while (p < to && (s[p] == ' ' || s[p] == '\t' || s[p] == '\r' || s[p] == '\n')) ++p;
            std::string av;
            if (p < to && s[p] == '=')
            {
                ++p;
                while (p < to && (s[p] == ' ' || s[p] == '\t' || s[p] == '\r' || s[p] == '\n')) ++p;
                if (p < to && (s[p] == '"' || s[p] == '\''))
                {
                    char q = s[p++];
                    size_t ve = s.find(q, p);
                    if (ve == std::string::npos || ve > to) ve = to;
                    av = s.substr(p, ve - p);
                    p = ve < to ? ve + 1 : to;
                }
                else
                {
                    size_t vs = p;
                    while (p < to && s[p] != '>' && s[p] != ' ' && s[p] != '\t' && s[p] != '\r' && s[p] != '\n') ++p;
                    av = s.substr(vs, p - vs);
                }
            }
            if (attrs.size() < 32) attrs.emplace_back(an, av);
        }
        i = p;
        if (closing)
        {
            if (!AllowedTag(name) || VoidTag(name)) continue;
            // close up to the matching open tag; a stray end tag is ignored
            size_t k = open.size();
            while (k > 0 && open[k - 1] != name) --k;
            if (k == 0) continue;
            while (open.size() >= k) { o += "</" + open.back() + ">"; open.pop_back(); }
            continue;
        }
        if (DropWithContent(name))
        {
            ++r.dropped;
            if (VoidTag(name) || selfClose) continue;
            // skip to the matching end tag of the same name (nesting counted)
            int depth = 1;
            size_t q = i;
            while (q < to && depth > 0)
            {
                size_t lt = low.find('<', q);
                if (lt == std::string::npos || lt >= to) { q = to; break; }
                bool cl = lt + 1 < to && low[lt + 1] == '/';
                size_t nb = lt + (cl ? 2 : 1);
                if (low.compare(nb, name.size(), name) == 0)
                {
                    char after = nb + name.size() < to ? low[nb + name.size()] : '>';
                    if (!((after >= 'a' && after <= 'z') || (after >= '0' && after <= '9')))
                        depth += cl ? -1 : 1;
                }
                size_t gt = low.find('>', lt);
                q = gt == std::string::npos || gt >= to ? to : gt + 1;
            }
            i = q;
            continue;
        }
        if (!AllowedTag(name)) { ++r.dropped; continue; }   // the tag goes, its text stays
        std::string out = "<" + name;
        bool keep = true;
        for (const auto& a : attrs)
        {
            std::string v;
            if (!DecodeAttr(a.second, v)) { ++r.dropped; continue; }
            if (a.first == "id" && IsIdValue(v)) out += " id=\"" + v + "\"";
            else if (a.first == "class")
            {
                std::string cls;
                size_t q = 0;
                while (q < v.size())
                {
                    size_t sp = v.find_first_of(" \t\r\n", q);
                    std::string w = v.substr(q, sp == std::string::npos ? std::string::npos : sp - q);
                    if ((w == "callout" || w == "steps") && cls.find(w) == std::string::npos) cls += (cls.empty() ? "" : " ") + w;
                    if (sp == std::string::npos) break;
                    q = sp + 1;
                }
                if (!cls.empty()) out += " class=\"" + cls + "\"";
            }
            else if (a.first == "href" && name == "a")
            {
                if (v.size() > 1 && v[0] == '#' && IsIdValue(v.substr(1))) out += " href=\"" + v + "\"";
                else if (IsHttpsUrl(v))
                {
                    out += " href=\"" + EscText(CleanText(v)) + "\"";
                    if (r.links.size() < 2000) r.links.push_back(v);
                }
                else ++r.dropped;
            }
            else if ((a.first == "colspan" || a.first == "rowspan") && (name == "td" || name == "th") &&
                     !v.empty() && v.size() <= 2 && isdigit((unsigned char)v[0]) && (v.size() == 1 || isdigit((unsigned char)v[1])))
                out += " " + a.first + "=\"" + v + "\"";
            else if (a.first == "alt" && name == "img")
                out += " alt=\"" + EscText(CleanText(v.substr(0, 300))) + "\"";
            else if (a.first == "src" && name == "img")
            {
                std::string data;
                if (IsHelpImagePath(v) && imageData && images < kMaxImagesPerDoc) data = imageData(v);
                if (data.empty()) keep = false;
                else { out += " src=\"" + data + "\""; ++images; }
            }
            else ++r.dropped;
        }
        if (name == "img")
        {
            if (!keep || out.find(" src=\"") == std::string::npos) { ++r.dropped; continue; }
            o += out + ">";
            continue;
        }
        if (name == "br") { o += "<br>"; continue; }
        if (open.size() >= 200) { ++r.dropped; continue; }   // nesting limit: deeper tags become text flow
        o += out + ">";
        open.push_back(name);
    }
    while (!open.empty()) { o += "</" + open.back() + ">"; open.pop_back(); }
    return r;
}

// ------------------------------------------------------------------ pictures
inline std::string Base64(const std::string& data)
{
    DWORD len = 0;
    if (data.empty() || !CryptBinaryToStringA((const BYTE*)data.data(), (DWORD)data.size(), CRYPT_STRING_BASE64 | CRYPT_STRING_NOCRLF, NULL, &len)) return std::string();
    std::string out(len, '\0');
    if (!CryptBinaryToStringA((const BYTE*)data.data(), (DWORD)data.size(), CRYPT_STRING_BASE64 | CRYPT_STRING_NOCRLF, &out[0], &len)) return std::string();
    out.resize(len);
    return out;
}

// A picture below helpDir as data: URI after the checks of the format (relative name, PNG/JPEG by its bytes).
inline std::string ImageDataUri(const std::wstring& helpDir, const std::string& rel)
{
    if (!IsHelpImagePath(rel)) return std::string();
    std::wstring w = W(rel);
    std::wstring dir = helpDir;
    size_t q = 0;
    for (;;)   // every folder on the way is a real folder
    {
        size_t sl = w.find(L'/', q);
        if (sl == std::wstring::npos) break;
        dir += L"\\" + w.substr(q, sl - q);
        if (IsReparseOrMissing(dir, true)) return std::string();
        q = sl + 1;
    }
    std::string data;
    if (!ReadFileLimited(dir + L"\\" + w.substr(q), kMaxImageBytes, data) || data.size() < 8) return std::string();
    const unsigned char* d = (const unsigned char*)data.data();
    const char* mime = nullptr;
    if (d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G') mime = "image/png";
    else if (d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) mime = "image/jpeg";
    if (!mime) return std::string();
    std::string b = Base64(data);
    return b.empty() ? std::string() : std::string("data:") + mime + ";base64," + b;
}

// ------------------------------------------------------------------ installed helps
struct HelpSource
{
    std::wstring id, name, lang, helpDir;
    HelpMeta meta;
};

struct HelpDoc
{
    HelpSource src;
    std::string html;                  // cleaned
    std::vector<std::string> links;
};

// The help of one data folder: <dataDir>\docs\help with help.json whose addon is expectedId.
inline bool SourceIn(const std::wstring& dataDir, const std::wstring& expectedId, const std::wstring& uiLang, HelpSource& out)
{
    std::wstring helpDir;
    if (!SafeDir(dataDir, { L"docs", L"help" }, helpDir)) return false;
    std::string text;
    if (!ReadFileLimited(helpDir + L"\\help.json", kMaxJsonBytes, text)) return false;
    HelpMeta m;
    if (!ParseHelpMeta(text, m) || m.addon != expectedId) return false;
    out.id = expectedId;
    out.meta = m;
    out.helpDir = helpDir;
    out.lang = PickLang(m, uiLang);
    return true;
}

// Every help of this Power PDF: the store client's own (Plug-Ins\PluginStore) first, then each
// add-on the store installed (<Name>.zxt next to a <Name> folder with manifest.json) whose
// help.json names the package id of its manifest. clientName: the display name of the client.
inline std::vector<HelpSource> FindHelps(const std::wstring& pluginsDir, const std::wstring& uiLang, const std::wstring& clientId,
                                         const std::wstring& clientName)
{
    std::vector<HelpSource> out;
    if (pluginsDir.empty() || IsReparseOrMissing(pluginsDir, true)) return out;
    HelpSource own;
    if (SourceIn(pluginsDir + L"\\PluginStore", clientId, uiLang, own)) { own.name = clientName; out.push_back(own); }
    std::wstring lang2 = uiLang.size() >= 2 ? uiLang.substr(0, 2) : L"en";
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileExW((pluginsDir + L"\\*").c_str(), FindExInfoBasic, &fd, FindExSearchLimitToDirectories, NULL, 0);
    if (h == INVALID_HANDLE_VALUE) return out;
    std::vector<HelpSource> addons;
    do
    {
        std::wstring name = fd.cFileName;
        if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) || (fd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)) continue;
        if (name.empty() || name[0] == L'.' || name.size() > 100 || _wcsicmp(name.c_str(), L"PluginStore") == 0) continue;
        if (addons.size() >= kMaxAddons) break;
        std::wstring dir = pluginsDir + L"\\" + name;
        DWORD za = GetFileAttributesW((dir + L".zxt").c_str());
        if (za == INVALID_FILE_ATTRIBUTES || (za & FILE_ATTRIBUTE_DIRECTORY)) continue;   // not loaded by Power PDF
        std::string mtext;
        if (!ReadFileLimited(dir + L"\\manifest.json", kMaxManifestBytes, mtext)) continue;   // not installed by the store
        JVal man;
        if (!ParseJson(mtext, man) || man.type != JVal::TObj) continue;
        std::wstring id = man.Str(L"id");
        if (!IsAddonId(id) || id == clientId) continue;
        HelpSource s;
        if (!SourceIn(dir, id, uiLang, s)) continue;
        s.name = ManifestName(man, lang2);
        if (s.name.empty()) s.name = name;
        addons.push_back(s);
    } while (FindNextFileW(h, &fd));
    FindClose(h);
    // by name, as the user reads them
    for (size_t i = 1; i < addons.size(); ++i)
        for (size_t k = i; k > 0 && CompareStringEx(LOCALE_NAME_USER_DEFAULT, NORM_IGNORECASE, addons[k].name.c_str(), -1,
                                                     addons[k - 1].name.c_str(), -1, NULL, NULL, 0) == CSTR_LESS_THAN; --k)
            std::swap(addons[k], addons[k - 1]);
    out.insert(out.end(), addons.begin(), addons.end());
    return out;
}

// Reads and cleans the language file of a source ("" html when it cannot be read).
inline HelpDoc LoadDoc(const HelpSource& s)
{
    HelpDoc d;
    d.src = s;
    std::string raw;
    if (!ReadFileLimited(s.helpDir + L"\\" + s.lang + L".html", kMaxHtmlBytes, raw) &&
        (s.lang == L"en" || !ReadFileLimited(s.helpDir + L"\\en.html", kMaxHtmlBytes, raw)))
        return d;
    if (raw.size() >= 3 && (unsigned char)raw[0] == 0xEF && (unsigned char)raw[1] == 0xBB && (unsigned char)raw[2] == 0xBF) raw.erase(0, 3);
    std::wstring dir = s.helpDir;
    CleanResult r = CleanHtml(raw, [dir](const std::string& rel) { return ImageDataUri(dir, rel); });
    d.html.swap(r.html);
    d.links.swap(r.links);
    return d;
}

}   // namespace pshelpcore
