// hostversion.h - version and edition of the running Power PDF (C1.1.1, C1.1.3).
//
// The Plugin SDK the store client is built with applies from Power PDF 2025.3
// hotfix 7 on ("2025.3.7"), in Power PDF 2025 for the Business edition only;
// from 2026.4 on every edition (Standard, Advanced, Business) loads plug-ins.
//
// Where the facts come from (C1.1.3, developer tip), for the installation that
// is RUNNING, so a machine with two releases side by side is judged right:
//   1. HKLM\SOFTWARE\Kofax\PDF\V1: InstallPath (must be the folder of the
//      running PowerPDF.exe), VersionLong "2025.3.8.0.26414" (third number =
//      hotfix), ProductName "Tungsten Power PDF Business" (edition).
//   2. The Windows programs list: the entry whose InstallLocation is that
//      folder, DisplayVersion "25.3.8.0.26414", DisplayName with the edition.
//   3. The exe file version "2025.3.0.72" (no hotfix level, no edition).
// The MSI refuses anything older than 2025.3 and non-Business editions before
// 2026.4 (Product.wxs); this check covers the hotfix level at run time: the
// store window explains instead of opening. Add-ons declare their own minimum
// (manifest minPowerPdfVersion); the store window offers no install for an
// add-on that needs a newer Power PDF.
// Keep kPSMinHost / kPSAllEditionsFrom in sync with installer/make_l10n.py.

#pragma once
#include <windows.h>
#include <string>
#include <vector>
#pragma comment(lib, "version.lib")

static const wchar_t kPSMinHost[] = L"2025.3.7";
static const wchar_t kPSAllEditionsFrom[] = L"2026.4";

namespace pshost {

// "25.3.8.0.26414" or "2025.3.8.0.26414" -> "2025.3.8" (two-digit year widened)
inline std::wstring FromDisplayVersion(const std::wstring& dv)
{
    unsigned a = 0, b = 0, c = 0;
    int n = swscanf_s(dv.c_str(), L"%u.%u.%u", &a, &b, &c);
    if (n < 2) return L"";
    if (a < 100) a += 2000;
    wchar_t buf[48];
    swprintf_s(buf, 48, L"%u.%u.%u", a, b, n >= 3 ? c : 0u);
    return buf;
}

// "Standard", "Advanced", "Business" from a product name, else empty (unknown).
inline std::wstring EditionOf(const std::wstring& name)
{
    for (const wchar_t* e : { L"Business", L"Advanced", L"Standard" })
        if (name.find(e) != std::wstring::npos) return e;
    return L"";
}

inline std::wstring Reg(HKEY root, const wchar_t* key, const wchar_t* value, DWORD view)
{
    wchar_t buf[1024] = { 0 };
    DWORD cb = sizeof(buf);
    if (RegGetValueW(root, key, value, RRF_RT_REG_SZ | view, NULL, buf, &cb) != ERROR_SUCCESS) return L"";
    return buf;
}

// Lower-case folder without trailing backslash, for comparing paths.
inline std::wstring NormDir(std::wstring p)
{
    while (!p.empty() && (p.back() == L'\\' || p.back() == L'/')) p.pop_back();
    if (!p.empty()) CharLowerBuffW(&p[0], (DWORD)p.size());
    return p;
}

// Folder of the running Power PDF: <root>\bin\PowerPDF.exe -> <root>; empty when
// we do not run inside PowerPDF.exe (tools, tests).
inline std::wstring HostRoot()
{
    wchar_t exe[MAX_PATH] = { 0 };
    if (!GetModuleFileNameW(NULL, exe, MAX_PATH)) return L"";
    std::wstring p = NormDir(exe);
    for (const std::wstring tail : { std::wstring(L"\\bin\\powerpdf.exe"), std::wstring(L"\\powerpdf.exe") })
        if (p.size() > tail.size() && p.compare(p.size() - tail.size(), tail.size(), tail) == 0)
            return p.substr(0, p.size() - tail.size());
    return L"";
}

struct Info
{
    std::wstring version;   // "2025.3.8" (with hotfix) or "2025.3.0.72" (exe only)
    std::wstring edition;   // "Business", "Advanced", "Standard" or empty
    bool hotfixKnown = false;
};

// 1. The product's own record, when it describes the running installation.
inline bool FromV1(Info& info, const std::wstring& host)
{
    for (DWORD view : { (DWORD)KEY_WOW64_64KEY, (DWORD)KEY_WOW64_32KEY })
    {
        const wchar_t* key = L"SOFTWARE\\Kofax\\PDF\\V1";
        std::wstring path = Reg(HKEY_LOCAL_MACHINE, key, L"InstallPath", view);
        if (path.empty() || (!host.empty() && NormDir(path) != host)) continue;
        std::wstring v = FromDisplayVersion(Reg(HKEY_LOCAL_MACHINE, key, L"VersionLong", view));
        if (v.empty()) continue;
        info.version = v;
        info.hotfixKnown = true;
        info.edition = EditionOf(Reg(HKEY_LOCAL_MACHINE, key, L"ProductName", view));
        return true;
    }
    return false;
}

// 2. The programs list: the entry of the running installation; without a known
//    host folder only when there is exactly one Power PDF entry.
inline bool FromProgramsList(Info& info, const std::wstring& host)
{
    HKEY root;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall", 0,
                      KEY_READ | KEY_WOW64_64KEY, &root) != ERROR_SUCCESS)
        return false;
    Info match, single;
    int count = 0;
    wchar_t name[256];
    for (DWORD i = 0;; ++i)
    {
        DWORD cch = 256;
        if (RegEnumKeyExW(root, i, name, &cch, NULL, NULL, NULL, NULL) != ERROR_SUCCESS) break;
        wchar_t disp[256] = { 0 }, ver[64] = { 0 }, loc[MAX_PATH * 2] = { 0 };
        DWORD cb = sizeof(disp);
        if (RegGetValueW(root, name, L"DisplayName", RRF_RT_REG_SZ, NULL, disp, &cb) != ERROR_SUCCESS) continue;
        std::wstring d = disp;
        if ((d.rfind(L"Tungsten Power PDF", 0) != 0 && d.rfind(L"Kofax Power PDF", 0) != 0) ||
            d.find(L"Add-on Store") != std::wstring::npos)
            continue;
        cb = sizeof(ver);
        if (RegGetValueW(root, name, L"DisplayVersion", RRF_RT_REG_SZ, NULL, ver, &cb) != ERROR_SUCCESS) continue;
        Info e;
        e.version = FromDisplayVersion(ver);
        if (e.version.empty()) continue;
        e.edition = EditionOf(d);
        e.hotfixKnown = true;
        ++count;
        single = e;
        cb = sizeof(loc);
        if (!host.empty() && RegGetValueW(root, name, L"InstallLocation", RRF_RT_REG_SZ, NULL, loc, &cb) == ERROR_SUCCESS
            && NormDir(loc) == host)
            match = e;
    }
    RegCloseKey(root);
    if (!match.version.empty()) { info = match; return true; }
    if (count == 1) { info = single; return true; }
    return false;
}

// 3. "2025.3.0.72" from the file version of the host executable (no hotfix level).
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

// a < b -> negative, equal -> 0 (numeric, missing parts count as 0)
inline int Compare(const std::wstring& a, const std::wstring& b)
{
    unsigned x[3] = { 0 }, y[3] = { 0 };
    swscanf_s(a.c_str(), L"%u.%u.%u", &x[0], &x[1], &x[2]);
    swscanf_s(b.c_str(), L"%u.%u.%u", &y[0], &y[1], &y[2]);
    for (int i = 0; i < 3; ++i) if (x[i] != y[i]) return x[i] < y[i] ? -1 : 1;
    return 0;
}

inline const Info& Host()
{
    // a function-local static is initialized once and thread-safely (C1.9.2: worker threads read it too)
    static const Info cached = [] {
        Info i;
        const std::wstring host = HostRoot();
        if (!FromV1(i, host) && !FromProgramsList(i, host))
            i.version = FromExe();
        return i;
    }();
    return cached;
}

} // namespace pshost

// "2025.3.8" (with the hotfix level) when the registry knows it, else the exe file
// version; empty when nothing is readable (then nothing is blocked).
inline std::wstring PSHostVersion() { return pshost::Host().version; }

// "Business", "Advanced", "Standard" or empty when unknown.
inline std::wstring PSHostEdition() { return pshost::Host().edition; }

// The hotfix level is only known from the registry; with the exe version alone
// ("2025.3.0.x") a 2025.3 host is not refused.
inline bool PSHostTooOld()
{
    const pshost::Info& h = pshost::Host();
    if (h.version.empty()) return false;
    if (!h.hotfixKnown)
    {
        unsigned a = 0, b = 0;
        swscanf_s(h.version.c_str(), L"%u.%u", &a, &b);
        return a < 2025 || (a == 2025 && b < 3);
    }
    return pshost::Compare(h.version, kPSMinHost) < 0;
}

// Standard and Advanced load plug-ins only from 2026.4 on; an unknown edition is
// never refused (the MSI checks the edition too).
inline bool PSHostEditionUnsupported()
{
    const pshost::Info& h = pshost::Host();
    if (h.version.empty() || h.edition.empty() || h.edition == L"Business") return false;
    return pshost::Compare(h.version, kPSAllEditionsFrom) < 0;
}
