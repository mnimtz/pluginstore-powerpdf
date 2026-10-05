// hostversion.h - version of the running Power PDF (C1.1.1).
//
// The Plugin SDK the store client is built with applies from Power PDF 2025.3
// hotfix 7 on ("2025.3.7"). The hotfix level is visible only in the Windows
// programs list (DisplayVersion "25.3.8.0.26414" = 2025.3 hotfix 8): the exe
// file version stays 2025.3.0.x and the MSI product version stays 25.3.0.
// The MSI refuses anything older than 2025.3 (Product.wxs); this check covers
// the hotfix level at run time: the store window explains instead of opening.
// Add-ons declare their own minimum (manifest minPowerPdfVersion); the store
// window offers no install for an add-on that needs a newer Power PDF.
// Keep kPSMinHost in sync with installer/make_l10n.py (MIN_PPDF).

#pragma once
#include <windows.h>
#include <string>
#include <vector>
#pragma comment(lib, "version.lib")

static const wchar_t kPSMinHost[] = L"2025.3.7";

namespace pshost {

// "25.3.8.0.26414" -> "2025.3.8" (first three numbers, two-digit year widened)
inline std::wstring FromDisplayVersion(const std::wstring& dv)
{
    unsigned a = 0, b = 0, c = 0;
    if (swscanf_s(dv.c_str(), L"%u.%u.%u", &a, &b, &c) < 2) return L"";
    if (a < 100) a += 2000;
    wchar_t buf[48];
    swprintf_s(buf, 48, L"%u.%u.%u", a, b, c);
    return buf;
}

// DisplayVersion of the installed Power PDF from the programs list (64-bit view).
inline std::wstring FromProgramsList()
{
    HKEY root;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall", 0,
                      KEY_READ | KEY_WOW64_64KEY, &root) != ERROR_SUCCESS)
        return L"";
    std::wstring best;
    wchar_t name[256];
    for (DWORD i = 0;; ++i)
    {
        DWORD cch = 256;
        if (RegEnumKeyExW(root, i, name, &cch, NULL, NULL, NULL, NULL) != ERROR_SUCCESS) break;
        wchar_t disp[256] = { 0 }, ver[64] = { 0 };
        DWORD cb = sizeof(disp);
        if (RegGetValueW(root, name, L"DisplayName", RRF_RT_REG_SZ, NULL, disp, &cb) != ERROR_SUCCESS) continue;
        std::wstring d = disp;
        if ((d.rfind(L"Tungsten Power PDF", 0) != 0 && d.rfind(L"Kofax Power PDF", 0) != 0) ||
            d.find(L"Add-on Store") != std::wstring::npos)
            continue;
        cb = sizeof(ver);
        if (RegGetValueW(root, name, L"DisplayVersion", RRF_RT_REG_SZ, NULL, ver, &cb) != ERROR_SUCCESS) continue;
        std::wstring v = FromDisplayVersion(ver);
        if (!v.empty()) { best = v; break; }
    }
    RegCloseKey(root);
    return best;
}

// "2025.3.0.72" from the file version of the host executable (no hotfix level).
inline std::wstring FromExe()
{
    wchar_t exe[MAX_PATH] = { 0 };
    if (!GetModuleFileNameW(NULL, exe, MAX_PATH)) return L"";
    DWORD handle = 0;
    DWORD size = GetFileVersionInfoSizeW(exe, &handle);
    if (!size) return L"";
    std::vector<BYTE> data(size);
    if (!GetFileVersionInfoW(exe, 0, size, data.data())) return L"";
    VS_FIXEDFILEINFO* fi = NULL; UINT len = 0;
    if (!VerQueryValueW(data.data(), L"\\", (void**)&fi, &len) || !fi) return L"";
    wchar_t buf[64];
    swprintf_s(buf, 64, L"%u.%u.%u.%u", HIWORD(fi->dwFileVersionMS), LOWORD(fi->dwFileVersionMS),
               HIWORD(fi->dwFileVersionLS), LOWORD(fi->dwFileVersionLS));
    return buf;
}

} // namespace pshost

// "2025.3.8" (with the hotfix level) when the programs list knows it, else the exe
// file version; empty when neither is readable (then nothing is blocked).
inline std::wstring PSHostVersion()
{
    static std::wstring cached;
    static bool done = false;
    if (done) return cached;
    done = true;
    cached = pshost::FromProgramsList();
    if (cached.empty()) cached = pshost::FromExe();
    return cached;
}

// The hotfix level is only known from the programs list; with the exe version alone
// ("2025.3.0.x") a 2025.3 host is not refused.
inline bool PSHostTooOld()
{
    const std::wstring v = PSHostVersion();
    if (v.empty()) return false;
    if (pshost::FromProgramsList().empty())
    {
        unsigned a = 0, b = 0;
        swscanf_s(v.c_str(), L"%u.%u", &a, &b);
        return a < 2025 || (a == 2025 && b < 3);
    }
    unsigned a = 0, b = 0, c = 0, ma = 0, mb = 0, mc = 0;
    swscanf_s(v.c_str(), L"%u.%u.%u", &a, &b, &c);
    swscanf_s(kPSMinHost, L"%u.%u.%u", &ma, &mb, &mc);
    if (a != ma) return a < ma;
    if (b != mb) return b < mb;
    return c < mc;
}
