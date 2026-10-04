// settings.cpp — see settings.h.

#include "stdafx.h"
#include "settings.h"
#include "policy.h"
#include "logging.h"

const wchar_t* kPSRegKey = L"Software\\Kofax\\PDF\\Tungsten Power PDF\\PluginStore";

// Our team instance; the Options page and the HKLM policy can override it
// any time (e.g. http://localhost:5190 during development).
static const wchar_t* kDefaultUrl = L"https://addon.power-pdf.de";

static std::wstring g_url = kDefaultUrl;
static bool g_beta = false;
static bool g_urlLocked = false;

static bool ReadUserString(const wchar_t* name, std::wstring& v)
{
    wchar_t buf[1024] = { 0 };
    DWORD sz = sizeof(buf);
    if (RegGetValueW(HKEY_CURRENT_USER, kPSRegKey, name, RRF_RT_REG_SZ, NULL, buf, &sz) == ERROR_SUCCESS)
    { v = buf; return true; }
    return false;
}

static bool ReadUserDword(const wchar_t* name, DWORD& v)
{
    DWORD sz = sizeof(v);
    return RegGetValueW(HKEY_CURRENT_USER, kPSRegKey, name, RRF_RT_REG_DWORD, NULL, &v, &sz) == ERROR_SUCCESS;
}

static std::wstring TrimUrl(std::wstring u)
{
    while (!u.empty() && (u.back() == L'/' || u.back() == L' ')) u.pop_back();
    return u;
}

bool PSUseClassicUI()
{
    DWORD v = 0;
    if (FPPolicyDword(L"Store", L"ClassicUI", v)) return v != 0;
    return ReadUserDword(L"ClassicUI", v) && v != 0;
}

std::wstring PSInstallId()
{
    std::wstring id;
    if (ReadUserString(L"InstallId", id) && id.size() == 36) return id;
    GUID g;
    if (FAILED(CoCreateGuid(&g))) return L"";
    wchar_t buf[64] = { 0 };
    StringFromGUID2(g, buf, 64);                        // {xxxxxxxx-...}
    id = std::wstring(buf + 1, 36);
    RegSetKeyValueW(HKEY_CURRENT_USER, kPSRegKey, L"InstallId", REG_SZ, id.c_str(), (DWORD)((id.size() + 1) * sizeof(wchar_t)));
    return id;
}

int PSMyRating(const std::wstring& packageId)
{
    DWORD v = 0, sz = sizeof(v);
    std::wstring key = std::wstring(kPSRegKey) + L"\\Ratings";
    if (RegGetValueW(HKEY_CURRENT_USER, key.c_str(), packageId.c_str(), RRF_RT_REG_DWORD, NULL, &v, &sz) != ERROR_SUCCESS) return 0;
    return v >= 1 && v <= 5 ? (int)v : 0;
}

void PSSetMyRating(const std::wstring& packageId, int stars)
{
    DWORD v = (DWORD)stars;
    std::wstring key = std::wstring(kPSRegKey) + L"\\Ratings";
    RegSetKeyValueW(HKEY_CURRENT_USER, key.c_str(), packageId.c_str(), REG_DWORD, &v, sizeof(v));
}

bool PSPolicyNoInstall()
{
    DWORD v = 0;
    return FPPolicyDword(L"Store", L"DisableInstall", v) && v != 0;
}

bool PSPolicyNoSelfUpdate()
{
    DWORD v = 0;
    return FPPolicyDword(L"Store", L"DisableSelfUpdate", v) && v != 0;
}

bool PSIsAllowedServerUrl(const std::wstring& url)
{
    std::wstring u = TrimUrl(url);
    while (!u.empty() && u.front() == L' ') u.erase(u.begin());
    if (u.empty()) return true;
    for (auto& c : u) c = (wchar_t)towlower(c);
    if (u.rfind(L"https://", 0) == 0) return u.size() > 8;
    for (const wchar_t* host : { L"http://localhost", L"http://127.0.0.1", L"http://[::1]" })
    {
        size_t n = wcslen(host);
        if (u.rfind(host, 0) == 0 && (u.size() == n || u[n] == L':' || u[n] == L'/')) return true;
    }
    return false;
}

void PSSettingsLoad()
{
    std::wstring s;
    DWORD d = 0;

    if (FPPolicyString(L"Store", L"ServerUrl", s) && !s.empty())
    { g_url = TrimUrl(s); g_urlLocked = true; }
    else if (ReadUserString(L"ServerUrl", s) && !s.empty())
        g_url = TrimUrl(s);

    if (FPPolicyDword(L"Store", L"BetaChannel", d))
        g_beta = d != 0;
    else if (ReadUserDword(L"BetaChannel", d))
        g_beta = d != 0;

    DWORD verbose = 0;
    if (ReadUserDword(L"VerboseLog", verbose))
        FPLogSetVerbose(verbose != 0);
}

std::wstring PSServerUrl() { return g_url; }
bool PSBetaChannel()       { return g_beta; }
bool PSUrlLocked()         { return g_urlLocked; }

void PSSaveUserSettings(const std::wstring& url, bool beta)
{
    HKEY k;
    if (RegCreateKeyExW(HKEY_CURRENT_USER, kPSRegKey, 0, NULL, 0, KEY_WRITE, NULL, &k, NULL) == ERROR_SUCCESS)
    {
        std::wstring u = TrimUrl(url);
        RegSetValueExW(k, L"ServerUrl", 0, REG_SZ, (const BYTE*)u.c_str(),
                       (DWORD)((u.size() + 1) * sizeof(wchar_t)));
        DWORD d = beta ? 1 : 0;
        RegSetValueExW(k, L"BetaChannel", 0, REG_DWORD, (const BYTE*)&d, sizeof(d));
        RegCloseKey(k);
    }
    PSSettingsLoad();
}
