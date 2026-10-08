// catalog.cpp — see catalog.h.

#include "stdafx.h"
#include "catalog.h"
#include "http.h"
#include "settings.h"
#include "install.h"
#include "loc.h"
#include "logging.h"
#include "clocale.h"
#include "link.h"
#include <vector>
#include <bcrypt.h>
#include <shlobj.h>

static std::wstring Utf8ToWide(const std::string& s)
{
    if (s.empty()) return L"";
    int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), NULL, 0);
    std::wstring w(n, 0);
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), w.data(), n);
    return w;
}

static std::vector<std::wstring> SplitTabs(const std::wstring& line)
{
    std::vector<std::wstring> parts;
    size_t start = 0;
    for (;;)
    {
        size_t tab = line.find(L'\t', start);
        if (tab == std::wstring::npos) { parts.push_back(line.substr(start)); break; }
        parts.push_back(line.substr(start, tab - start));
        start = tab + 1;
    }
    return parts;
}

// Fields that end up in file paths, script values or hash comparisons are
// checked here, whatever the server sent: a line that does not fit is skipped.
static bool IsVersion(const std::wstring& v)
{
    if (v.empty() || v.size() > 32 || v.front() == L'.' || v.back() == L'.') return false;
    for (wchar_t c : v) if (!((c >= L'0' && c <= L'9') || c == L'.')) return false;
    return v.find(L"..") == std::wstring::npos;
}

static bool IsSha256(const std::wstring& s)
{
    if (s.size() != 64) return false;
    for (wchar_t c : s)
        if (!((c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f') || (c >= L'A' && c <= L'F'))) return false;
    return true;
}

bool PSIsValidZxtName(const std::wstring& n)
{
    if (n.empty() || n.size() > 64) return false;
    for (wchar_t c : n)
        if (!((c >= L'a' && c <= L'z') || (c >= L'A' && c <= L'Z') || (c >= L'0' && c <= L'9') || c == L'-' || c == L'_')) return false;
    return true;
}

static bool IsHttpUrl(const std::wstring& u)
{
    return u.size() < 2048 && (_wcsnicmp(u.c_str(), L"https://", 8) == 0 || _wcsnicmp(u.c_str(), L"http://", 7) == 0);
}

// The server escapes embedded tabs/newlines in text fields as spaces, so a
// plain split per line is safe.
bool PSFetchCatalog(std::vector<PSCatalogEntry>& out, std::wstring& error)
{
    // UI language for localized names/descriptions
    char code[64] = { 0 };
    DURING DVAppGetLanguage(code); HANDLER END_HANDLER
    wchar_t wcode[64] = { 0 };
    MultiByteToWideChar(CP_ACP, 0, code, -1, wcode, 63);
    return PSFetchCatalogFor(wcode, out, error);
}

namespace {
// C1.9.3: the last catalog fetched with the stored codes, in memory and in
// %LOCALAPPDATA%\Tungsten\AddonStore\cache\catalog.tsv, so the store window shows it at
// once (also right after a Power PDF start) and a fresh one replaces it. The file holds
// the server's TSV as it came, behind one line "#addonstore-catalog-v1\t<sha256 of key>"
// (the key holds the customer codes: only their hash is written). It is parsed with the
// same checks as an answer of the server, and installing still needs the server's
// signature (signature.h), so a changed file can show other texts at most.
SRWLOCK g_cacheLock = SRWLOCK_INIT;
std::wstring g_cacheKey;
ULONGLONG g_cacheTick = 0;
std::vector<PSCatalogEntry> g_cache;
const char kCacheMagic[] = "#addonstore-catalog-v1\t";
const size_t kCacheMax = 16 * 1024 * 1024;

std::wstring CatalogUrl(const std::wstring& lang)
{
    std::wstring wcode;
    for (wchar_t c : lang)
        if ((c >= L'a' && c <= L'z') || (c >= L'A' && c <= L'Z') || c == L'-' || c == L'_') wcode += c;
    std::wstring url = PSServerUrl() + L"/api/catalog?format=tsv&lang=" + wcode;
    if (PSBetaChannel()) url += L"&channel=beta";
    return url;
}

std::string KeyHash(const std::wstring& key)
{
    std::string utf8;
    int n = WideCharToMultiByte(CP_UTF8, 0, key.c_str(), (int)key.size(), NULL, 0, NULL, NULL);
    if (n > 0) { utf8.resize(n); WideCharToMultiByte(CP_UTF8, 0, key.c_str(), (int)key.size(), &utf8[0], n, NULL, NULL); }
    BCRYPT_ALG_HANDLE alg = NULL;
    BCRYPT_HASH_HANDLE h = NULL;
    UCHAR digest[32] = { 0 };
    std::string out;
    if (BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, NULL, 0) == 0)
    {
        if (BCryptCreateHash(alg, &h, NULL, 0, NULL, 0, 0) == 0 &&
            BCryptHashData(h, (PUCHAR)utf8.data(), (ULONG)utf8.size(), 0) == 0 &&
            BCryptFinishHash(h, digest, sizeof(digest), 0) == 0)
        {
            static const char hex[] = "0123456789abcdef";
            for (UCHAR b : digest) { out += hex[b >> 4]; out += hex[b & 15]; }
        }
        if (h) BCryptDestroyHash(h);
        BCryptCloseAlgorithmProvider(alg, 0);
    }
    return out;
}

std::wstring CacheFile()
{
    PWSTR base = nullptr;
    std::wstring path;
    if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, NULL, &base)))
    {
        std::wstring dir = std::wstring(base) + L"\\Tungsten\\AddonStore\\cache";
        SHCreateDirectoryExW(NULL, dir.c_str(), NULL);
        path = dir + L"\\catalog.tsv";
    }
    if (base) CoTaskMemFree(base);
    return path;
}

void WriteCacheFile(const std::wstring& key, const std::string& body)
{
    std::wstring path = CacheFile();
    if (path.empty() || body.size() > kCacheMax) return;
    std::string data = kCacheMagic + KeyHash(key) + "\n" + body;
    std::wstring tmp = path + L".tmp";
    HANDLE f = CreateFileW(tmp.c_str(), GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f == INVALID_HANDLE_VALUE) return;
    DWORD w = 0;
    BOOL ok = WriteFile(f, data.data(), (DWORD)data.size(), &w, NULL) && w == data.size();
    CloseHandle(f);
    if (!ok || !MoveFileExW(tmp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING)) DeleteFileW(tmp.c_str());
}

// The body of the cache file when it belongs to this key, else empty.
std::string ReadCacheFile(const std::wstring& key)
{
    std::wstring path = CacheFile();
    if (path.empty()) return std::string();
    HANDLE f = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f == INVALID_HANDLE_VALUE) return std::string();
    LARGE_INTEGER size = { 0 };
    std::string data;
    if (GetFileSizeEx(f, &size) && size.QuadPart > 0 && size.QuadPart <= (LONGLONG)(kCacheMax + 200))
    {
        data.resize((size_t)size.QuadPart);
        DWORD r = 0;
        if (!ReadFile(f, &data[0], (DWORD)data.size(), &r, NULL) || r != data.size()) data.clear();
    }
    CloseHandle(f);
    std::string head = kCacheMagic + KeyHash(key) + "\n";
    if (data.size() < head.size() || data.compare(0, head.size(), head) != 0) return std::string();
    return data.substr(head.size());
}

void Parse(const std::string& body, std::vector<PSCatalogEntry>& out)
{
    std::wstring text = Utf8ToWide(body);
    size_t pos = 0;
    while (pos < text.size())
    {
        size_t eol = text.find(L'\n', pos);
        if (eol == std::wstring::npos) eol = text.size();
        std::wstring line = text.substr(pos, eol - pos);
        pos = eol + 1;
        while (!line.empty() && (line.back() == L'\r')) line.pop_back();
        if (line.empty()) continue;

        auto f = SplitTabs(line);
        if (f.size() < 11) continue;
        PSCatalogEntry e;
        e.id = f[0]; e.version = f[1]; e.channel = f[2]; e.name = f[3];
        e.description = f[4]; e.changelog = f[5]; e.minHost = f[6];
        e.sizeBytes = _wcstoui64(f[7].c_str(), NULL, 10);
        e.sha256 = f[8]; e.downloadUrl = f[9]; e.zxtName = f[10];
        if (f.size() > 11) e.category = f[11];
        if (f.size() > 12) e.author = f[12];
        if (f.size() > 13) e.contactEmail = f[13];
        if (f.size() > 14) e.categoryName = f[14];
        if (f.size() > 15) e.iconUrl = f[15];
        if (f.size() > 16) e.rating = _wcstod_l(f[16].c_str(), NULL, FPCLocale());   // "4.3", invariant format
        if (f.size() > 17) e.ratingCount = _wtoi(f[17].c_str());
        if (f.size() > 18) e.screenshots = _wtoi(f[18].c_str());
        if (f.size() > 19) e.customer = f[19];
        if (f.size() > 20) e.signature = f[20];
        if (f.size() > 21) e.noUi = f[21] == L"none";
        if (!PSLinkIsValidId(e.id) || !IsVersion(e.version) || !IsSha256(e.sha256) ||
            !PSIsValidZxtName(e.zxtName) || !IsHttpUrl(e.downloadUrl) || (!e.iconUrl.empty() && !IsHttpUrl(e.iconUrl)))
        {
            FPLogW(L"[Store] catalog line skipped, invalid fields (id %.100s)", e.id.c_str());
            continue;
        }
        e.installedVersion = PSInstalledVersion(e.zxtName);
        out.push_back(std::move(e));
    }
}
} // namespace

bool PSCachedCatalog(const std::wstring& lang, unsigned long maxAgeMs, std::vector<PSCatalogEntry>& out)
{
    out.clear();
    std::wstring key = CatalogUrl(lang) + L"|" + PSCustomerCode();
    AcquireSRWLockShared(&g_cacheLock);
    bool hit = g_cacheTick && key == g_cacheKey && GetTickCount64() - g_cacheTick <= maxAgeMs;
    if (hit) out = g_cache;
    ReleaseSRWLockShared(&g_cacheLock);
    if (hit)
    {
        for (auto& e : out) e.installedVersion = PSInstalledVersion(e.zxtName);   // may have changed since
        return true;
    }
    // nothing in memory yet (first window after a Power PDF start): the file of the last run
    std::string body = ReadCacheFile(key);
    if (body.empty()) return false;
    Parse(body, out);
    FPLogW(L"[Store] catalog: %u entries from the local copy", (unsigned)out.size());
    return !out.empty();
}

bool PSFetchCatalogFor(const std::wstring& lang, std::vector<PSCatalogEntry>& out, std::wstring& error,
                       const std::wstring* codes)
{
    out.clear();
    error.clear();
    const std::wstring url = CatalogUrl(lang);
    const std::wstring key = url + L"|" + PSCustomerCode();

    std::string body;
    DWORD status = 0;
    // "what would the catalog be with these codes" (removing a code, C1.4.1) or the stored ones
    if (!(codes ? PSHttpCheckCustomerCode(url, *codes, body, &status, 16 * 1024 * 1024) : PSHttpGetText(url, body, &status)))
    {
        error = FPLoc(IDS_PSD_MSG_FAIL);
        return false;
    }

    Parse(body, out);
    FPLogW(L"[Store] catalog: %u entries (%s)", (unsigned)out.size(), url.c_str());
    if (!codes && status == 200)
    {
        AcquireSRWLockExclusive(&g_cacheLock);
        g_cacheKey = key; g_cache = out; g_cacheTick = GetTickCount64();
        ReleaseSRWLockExclusive(&g_cacheLock);
        WriteCacheFile(key, body);
    }
    return true;
}
