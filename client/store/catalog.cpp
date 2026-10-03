// catalog.cpp — see catalog.h.

#include "stdafx.h"
#include "catalog.h"
#include "http.h"
#include "settings.h"
#include "install.h"
#include "loc.h"
#include "logging.h"
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

// The server escapes embedded tabs/newlines in text fields as spaces, so a
// plain split per line is safe.
bool PSFetchCatalog(std::vector<PSCatalogEntry>& out, std::wstring& error)
{
    out.clear();
    error.clear();

    // UI language for localized names/descriptions
    char code[64] = { 0 };
    DURING DVAppGetLanguage(code); HANDLER END_HANDLER
    wchar_t wcode[64] = { 0 };
    MultiByteToWideChar(CP_ACP, 0, code, -1, wcode, 64);

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
        e.installedVersion = PSInstalledVersion(e.zxtName);
        out.push_back(std::move(e));
    }
    FPLogW(L"[Store] catalog: %u entries (%s)", (unsigned)out.size(), url.c_str());
    return true;
}
