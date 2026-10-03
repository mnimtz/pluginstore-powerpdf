// AddonStoreLink.cpp — handler for addonstore:// links from the store website.
//
// Windows starts this helper with the clicked URL, e.g.
//   addonstore://install/com.tungsten.smartbookmarks
// It only passes a validated package id on to the Add-on Store client inside
// Power PDF (see client/store/link.h); it never downloads or installs
// anything itself. The client shows the package and asks the user first.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <string>
#include "../store/link.h"

#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "advapi32.lib")

static const wchar_t* kRegKey = L"Software\\Kofax\\PDF\\Tungsten Power PDF\\PluginStore";

bool PSLinkIsValidId(const std::wstring& id)
{
    if (id.size() < 3 || id.size() > 100) return false;
    bool dot = false;
    for (wchar_t c : id)
    {
        if (c == L'.') { dot = true; continue; }
        if (!((c >= L'a' && c <= L'z') || (c >= L'0' && c <= L'9') || c == L'-')) return false;
    }
    return dot && id.front() != L'.' && id.back() != L'.';
}

// "addonstore://install/<id>" or "addonstore:install/<id>", optional trailing
// slash or query; anything else yields an empty id.
static std::wstring ParseId(std::wstring url)
{
    for (auto& c : url) c = (wchar_t)towlower(c);
    for (const wchar_t* prefix : { L"addonstore://install/", L"addonstore:install/", L"addonstore://open/" })
    {
        size_t n = wcslen(prefix);
        if (url.compare(0, n, prefix) != 0) continue;
        std::wstring id = url.substr(n);
        size_t cut = id.find_first_of(L"/?#");
        if (cut != std::wstring::npos) id.resize(cut);
        return PSLinkIsValidId(id) ? id : std::wstring();
    }
    return std::wstring();
}

static std::wstring PowerPdfExe()
{
    wchar_t path[MAX_PATH] = { 0 };
    DWORD sz = sizeof(path);
    if (RegGetValueW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths\\PowerPDF.exe",
                     NULL, RRF_RT_REG_SZ | RRF_SUBKEY_WOW6464KEY, NULL, path, &sz) == ERROR_SUCCESS)
        return path;
    return std::wstring();
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, LPWSTR, int)
{
    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    std::wstring id = (argv && argc >= 2) ? ParseId(argv[1]) : std::wstring();
    if (argv) LocalFree(argv);
    if (id.empty()) return 1;

    HKEY k;
    if (RegCreateKeyExW(HKEY_CURRENT_USER, kRegKey, 0, NULL, 0, KEY_SET_VALUE, NULL, &k, NULL) != ERROR_SUCCESS) return 2;
    RegSetValueExW(k, PS_LINK_PENDING_VALUE, 0, REG_SZ, (const BYTE*)id.c_str(), (DWORD)((id.size() + 1) * sizeof(wchar_t)));
    RegCloseKey(k);

    // Power PDF with the store client already running: hand the request over.
    HWND target = FindWindowExW(HWND_MESSAGE, NULL, PS_LINK_WINDOW_CLASS, NULL);
    if (target)
    {
        DWORD pid = 0;
        GetWindowThreadProcessId(target, &pid);
        AllowSetForegroundWindow(pid);
        PostMessageW(target, RegisterWindowMessageW(PS_LINK_MESSAGE), 0, 0);
        return 0;
    }

    // Otherwise start Power PDF; the client picks the request up after start.
    std::wstring exe = PowerPdfExe();
    if (exe.empty()) return 3;
    return (INT_PTR)ShellExecuteW(NULL, L"open", exe.c_str(), NULL, NULL, SW_SHOWNORMAL) > 32 ? 0 : 4;
}
