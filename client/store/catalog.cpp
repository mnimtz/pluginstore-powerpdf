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

bool PSFetchCatalogFor(const std::wstring& lang, std::vector<PSCatalogEntry>& out, std::wstring& error)
{
    out.clear();
    error.clear();
    std::wstring wcode;
    for (wchar_t c : lang)
        if ((c >= L'a' && c <= L'z') || (c >= L'A' && c <= L'Z') || c == L'-' || c == L'_') wcode += c;

    std::wstring url = PSServerUrl() + L"/api/catalog?format=tsv&lang=" + wcode;
    if (PSBetaChannel()) url += L"&channel=beta";

    std::string body;
    DWORD status = 0;
    if (!PSHttpGetText(url, body, &status))
    {
        error = FPLoc(IDS_PSD_MSG_FAIL);
        return false;
    }

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
        if (!PSLinkIsValidId(e.id) || !IsVersion(e.version) || !IsSha256(e.sha256) ||
            !PSIsValidZxtName(e.zxtName) || !IsHttpUrl(e.downloadUrl) || (!e.iconUrl.empty() && !IsHttpUrl(e.iconUrl)))
        {
            FPLogW(L"[Store] catalog line skipped, invalid fields (id %.100s)", e.id.c_str());
            continue;
        }
        e.installedVersion = PSInstalledVersion(e.zxtName);
        out.push_back(std::move(e));
    }
    FPLogW(L"[Store] catalog: %u entries (%s)", (unsigned)out.size(), url.c_str());
    return true;
}
