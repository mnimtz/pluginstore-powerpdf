// powerpdfpath.h — finding the Power PDF installation at run time.
//
// The folder name carries the version ("Power PDF 2025") and differs between
// releases, editions and customers — a 32-bit-style "Program Files (x86)" tree,
// a non-default install drive or a localized path are all normal. So nothing
// here is ever hard-coded: every candidate comes from the machine itself and is
// only accepted once PowerPDF.exe is actually found in it.
//
// Order (first verified hit wins):
//   1. our own module path        — only inside the plug-in, and unbeatable:
//                                   the .zxt sits in <root>\bin\Plug-Ins
//   2. App Paths\PowerPDF.exe     — the standard Windows mechanism
//   3. HKLM\SOFTWARE\Kofax\PDF    — InstallDir / InstallPath (product's own)
//   4. Uninstall entries          — DisplayName "…Power PDF…" → InstallLocation
//
// Shared by the plug-in and the installer, so both land on the same folder.

#pragma once
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string>

namespace cspath {

inline std::wstring TrimSlash(std::wstring s)
{
    while (!s.empty() && (s.back() == L'\\' || s.back() == L'/')) s.pop_back();
    return s;
}

inline bool FileExists(const std::wstring& p)
{
    DWORD a = GetFileAttributesW(p.c_str());
    return a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_DIRECTORY);
}

inline bool DirExists(const std::wstring& p)
{
    DWORD a = GetFileAttributesW(p.c_str());
    return a != INVALID_FILE_ATTRIBUTES && (a & FILE_ATTRIBUTE_DIRECTORY);
}

// A root is only a root if the executable and the UI layout are really there.
inline bool LooksLikeRoot(const std::wstring& root)
{
    if (root.empty()) return false;
    return FileExists(root + L"\\bin\\PowerPDF.exe");
}

inline std::wstring RegStr(HKEY hive, const wchar_t* subkey, const wchar_t* value, DWORD view)
{
    wchar_t buf[MAX_PATH * 2] = { 0 };
    DWORD sz = sizeof(buf), type = 0;
    if (RegGetValueW(hive, subkey, value, RRF_RT_REG_SZ | view, &type, buf, &sz) == ERROR_SUCCESS)
        return std::wstring(buf);
    return std::wstring();
}

// Walks the uninstall lists for the product entry. Last resort, but it survives
// a machine where the App Paths entry was cleaned up by some deployment tool.
inline std::wstring RootFromUninstallList(HKEY hive, DWORD view)
{
    const wchar_t* kUninst = L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall";
    HKEY h = NULL;
    if (RegOpenKeyExW(hive, kUninst, 0, KEY_READ | view, &h) != ERROR_SUCCESS)
        return std::wstring();

    std::wstring found;
    wchar_t name[512];
    for (DWORD i = 0; ; ++i)
    {
        DWORD n = 512;
        if (RegEnumKeyExW(h, i, name, &n, NULL, NULL, NULL, NULL) != ERROR_SUCCESS) break;

        std::wstring sub = std::wstring(kUninst) + L"\\" + name;
        std::wstring disp = RegStr(hive, sub.c_str(), L"DisplayName", view);
        if (disp.find(L"Power PDF") == std::wstring::npos) continue;

        std::wstring loc = TrimSlash(RegStr(hive, sub.c_str(), L"InstallLocation", view));
        if (LooksLikeRoot(loc)) { found = loc; break; }
    }
    RegCloseKey(h);
    return found;
}

// hModule: pass the plug-in's own instance handle to use the module path first;
// NULL (the installer) skips straight to the registry.
inline std::wstring FindRoot(HMODULE hModule)
{
    // 1. Where we are loaded from — <root>\bin\Plug-Ins\SigningTools.zxt.
    if (hModule)
    {
        wchar_t mod[MAX_PATH] = { 0 };
        if (GetModuleFileNameW(hModule, mod, MAX_PATH))
        {
            std::wstring p(mod);
            for (int i = 0; i < 3 && !p.empty(); ++i)
            {
                size_t sl = p.find_last_of(L'\\');
                p = (sl == std::wstring::npos) ? std::wstring() : p.substr(0, sl);
            }
            if (LooksLikeRoot(p)) return p;
        }
    }

    static const DWORD views[] = { 0, KEY_WOW64_64KEY, KEY_WOW64_32KEY };
    for (int v = 0; v < 3; ++v)
    {
        DWORD view = views[v];

        // 2. App Paths: "Path" is the bin folder, the default value the exe.
        std::wstring bin = TrimSlash(RegStr(HKEY_LOCAL_MACHINE,
            L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths\\PowerPDF.exe",
            L"Path", view));
        if (bin.empty())
        {
            std::wstring exe = RegStr(HKEY_LOCAL_MACHINE,
                L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths\\PowerPDF.exe",
                NULL, view);
            size_t sl = exe.find_last_of(L'\\');
            if (sl != std::wstring::npos) bin = exe.substr(0, sl);
        }
        if (!bin.empty())
        {
            size_t sl = bin.find_last_of(L'\\');
            std::wstring root = (sl == std::wstring::npos) ? std::wstring() : bin.substr(0, sl);
            if (LooksLikeRoot(root)) return root;
        }

        // 3. The product's own key — both spellings have been seen.
        const wchar_t* kNames[] = { L"InstallDir", L"InstallPath" };
        for (int i = 0; i < 2; ++i)
        {
            std::wstring root = TrimSlash(RegStr(HKEY_LOCAL_MACHINE, L"SOFTWARE\\Kofax\\PDF",
                                                 kNames[i], view));
            if (LooksLikeRoot(root)) return root;
        }

        // 4. The uninstall list.
        std::wstring root = RootFromUninstallList(HKEY_LOCAL_MACHINE, view);
        if (LooksLikeRoot(root)) return root;
    }
    return std::wstring();
}

inline std::wstring FindBin(HMODULE hModule)
{
    std::wstring root = FindRoot(hModule);
    return root.empty() ? root : root + L"\\bin";
}

} // namespace cspath
