// oldinstalls.h - add-ons the store installed into ANOTHER Power PDF folder (C1.1.4).
//
// After an update to a new release the installation folder changes ("Power PDF
// 2025" -> "Power PDF 2026") and the add-ons stay behind in the old one. They
// are found by their store manifest, <root>\bin\Plug-Ins\<zxtName>\manifest.json,
// in the Power PDF folders next to the running installation and under
// Program Files\Tungsten and Program Files\Kofax (both views). Header-only and
// plain Win32 so it can be tested outside Power PDF.

#pragma once
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string>
#include <vector>
#include "powerpdfpath.h"

struct PSOldInstall
{
    std::wstring root;      // e.g. C:\Program Files\Tungsten\Power PDF 2025
    std::wstring folder;    // "Power PDF 2025", for messages
    std::wstring id, zxtName, version;
    bool leftover = false;  // no PowerPDF.exe there any more: left over from an update
};

namespace psold {

inline std::wstring Lower(std::wstring p)
{
    while (!p.empty() && (p.back() == L'\\' || p.back() == L'/')) p.pop_back();
    if (!p.empty()) CharLowerBuffW(&p[0], (DWORD)p.size());
    return p;
}

inline std::string ReadSmall(const std::wstring& path)
{
    HANDLE f = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f == INVALID_HANDLE_VALUE) return std::string();
    DWORD size = GetFileSize(f, NULL);
    std::string data(size > 0 && size < (1u << 20) ? size : 0, 0);
    DWORD got = 0;
    if (!data.empty() && (!ReadFile(f, &data[0], size, &got, NULL) || got != size)) data.clear();
    CloseHandle(f);
    return data;
}

// "key": "value" from OUR OWN manifest.json (first occurrence; id and version
// come first in every store manifest).
inline std::wstring Value(const std::string& json, const char* key)
{
    std::string needle = std::string("\"") + key + "\"";
    size_t k = json.find(needle);
    if (k == std::string::npos) return L"";
    size_t colon = json.find(':', k + needle.size());
    size_t q1 = colon == std::string::npos ? colon : json.find('"', colon + 1);
    size_t q2 = q1 == std::string::npos ? q1 : json.find('"', q1 + 1);
    if (q2 == std::string::npos) return L"";
    std::string v = json.substr(q1 + 1, q2 - q1 - 1);
    wchar_t w[512] = { 0 };
    MultiByteToWideChar(CP_UTF8, 0, v.c_str(), -1, w, 511);
    return w;
}

// Plug-in folder names the store itself writes: letters, digits, '-', '_'.
inline bool SafeName(const std::wstring& n)
{
    if (n.empty() || n.size() > 64) return false;
    for (wchar_t c : n)
        if (!((c >= L'a' && c <= L'z') || (c >= L'A' && c <= L'Z') || (c >= L'0' && c <= L'9') || c == L'-' || c == L'_'))
            return false;
    return true;
}

} // namespace psold

// currentRoot: the running installation (never reported).
inline std::vector<PSOldInstall> PSFindOldInstallsFrom(const std::wstring& currentRoot)
{
    std::vector<PSOldInstall> out;
    if (currentRoot.empty()) return out;
    const std::wstring currentKey = psold::Lower(currentRoot);

    std::vector<std::wstring> parents;
    auto addParent = [&parents](std::wstring p) {
        p = cspath::TrimSlash(p);
        if (p.empty() || !cspath::DirExists(p)) return;
        for (auto& q : parents) if (psold::Lower(q) == psold::Lower(p)) return;
        parents.push_back(p);
    };
    size_t sl = currentRoot.find_last_of(L'\\');
    if (sl != std::wstring::npos) addParent(currentRoot.substr(0, sl));
    for (const wchar_t* env : { L"ProgramW6432", L"ProgramFiles", L"ProgramFiles(x86)" })
    {
        wchar_t pf[MAX_PATH] = { 0 };
        if (!GetEnvironmentVariableW(env, pf, MAX_PATH)) continue;
        addParent(std::wstring(pf) + L"\\Tungsten");
        addParent(std::wstring(pf) + L"\\Kofax");
    }

    for (const auto& parent : parents)
    {
        WIN32_FIND_DATAW fd;
        HANDLE h = FindFirstFileW((parent + L"\\*").c_str(), &fd);
        if (h == INVALID_HANDLE_VALUE) continue;
        do
        {
            if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) || fd.cFileName[0] == L'.') continue;
            const std::wstring name = fd.cFileName;
            if (psold::Lower(name).find(L"power pdf") == std::wstring::npos) continue;
            const std::wstring root = parent + L"\\" + name;
            if (psold::Lower(root) == currentKey) continue;
            const std::wstring plugins = root + L"\\bin\\Plug-Ins";
            const bool leftover = !cspath::FileExists(root + L"\\bin\\PowerPDF.exe");

            WIN32_FIND_DATAW pd;
            HANDLE ph = FindFirstFileW((plugins + L"\\*").c_str(), &pd);
            if (ph == INVALID_HANDLE_VALUE) continue;
            do
            {
                if (!(pd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) || pd.cFileName[0] == L'.') continue;
                const std::wstring zxt = pd.cFileName;
                if (!psold::SafeName(zxt) || _wcsicmp(zxt.c_str(), L"PluginStore") == 0) continue;   // the client itself
                const std::string json = psold::ReadSmall(plugins + L"\\" + zxt + L"\\manifest.json");
                if (json.empty()) continue;   // not installed by the store
                PSOldInstall o;
                o.root = root; o.folder = name; o.zxtName = zxt; o.leftover = leftover;
                o.id = psold::Value(json, "id");
                o.version = psold::Value(json, "version");
                if (!o.id.empty()) out.push_back(o);
            } while (FindNextFileW(ph, &pd));
            FindClose(ph);
        } while (FindNextFileW(h, &fd));
        FindClose(h);
    }
    return out;
}
