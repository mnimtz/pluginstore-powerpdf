// settings.cpp — see settings.h.

#include "stdafx.h"
#include "settings.h"
#include <algorithm>
#include "policy.h"
#include "logging.h"

const wchar_t* kPSRegKey = L"Software\\Kofax\\PDF\\Tungsten Power PDF\\PluginStore";

// Our team instance; the Options page and the HKLM policy can override it
// any time (e.g. http://localhost:5190 during development).
static const wchar_t* kDefaultUrl = L"https://addon.power-pdf.de";

static std::wstring g_url = kDefaultUrl;
// g_url is read by worker threads while Options may rewrite it.
static SRWLOCK g_urlLock = SRWLOCK_INIT;
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

bool PSPowerPdfHintLocked()
{
    DWORD v = 0;
    return FPPolicyDword(L"Store", L"PowerPdfUpdates", v);
}

bool PSPowerPdfHint()
{
    DWORD v = 0;
    if (FPPolicyDword(L"Store", L"PowerPdfUpdates", v)) return v != 0;
    return ReadUserDword(L"PowerPdfUpdateHint", v) && v != 0;
}

void PSSavePowerPdfHint(bool on)
{
    DWORD v = on ? 1 : 0;
    RegSetKeyValueW(HKEY_CURRENT_USER, kPSRegKey, L"PowerPdfUpdateHint", REG_DWORD, &v, sizeof(v));
}

std::wstring PSPowerPdfHiddenUpdate()
{
    std::wstring v;
    return ReadUserString(L"PowerPdfHiddenUpdate", v) && v.size() < 40 ? v : std::wstring();
}

void PSSetPowerPdfHiddenUpdate(const std::wstring& version)
{
    RegSetKeyValueW(HKEY_CURRENT_USER, kPSRegKey, L"PowerPdfHiddenUpdate", REG_SZ, version.c_str(), (DWORD)((version.size() + 1) * sizeof(wchar_t)));
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

static std::wstring NormalizeCode(const std::wstring& c)
{
    std::wstring o;
    for (wchar_t ch : c)
    {
        if (ch == L' ' || ch == L'\t') continue;
        o += (wchar_t)towupper(ch);
    }
    return o;
}

bool PSIsValidCustomerCode(const std::wstring& code)
{
    std::wstring c = NormalizeCode(code);
    if (c.size() > 64) return false;
    for (wchar_t ch : c)
        if (!((ch >= L'A' && ch <= L'Z') || (ch >= L'0' && ch <= L'9') || ch == L'-')) return false;
    return true;
}

bool PSCustomerCodeLocked()
{
    std::wstring s;
    return FPPolicyString(L"Store", L"CustomerCode", s) && !s.empty();
}

// "A; b,C" -> {"A", "B", "C"}: normalized, valid, no duplicates, at most kPSMaxCustomerCodes
static std::vector<std::wstring> SplitCodes(const std::wstring& s)
{
    std::vector<std::wstring> out;
    std::wstring cur;
    auto flush = [&]() {
        std::wstring c = NormalizeCode(cur);
        cur.clear();
        if (c.empty() || !PSIsValidCustomerCode(c) || out.size() >= kPSMaxCustomerCodes) return;
        for (const auto& x : out) if (x == c) return;
        out.push_back(c);
    };
    for (wchar_t ch : s)
    {
        if (ch == L';' || ch == L',' || ch == L'\n' || ch == L'\r') flush();
        else cur += ch;
    }
    flush();
    return out;
}

static std::wstring JoinCodes(const std::vector<std::wstring>& codes)
{
    std::wstring s;
    for (const auto& c : codes) s += (s.empty() ? L"" : L";") + c;
    return s;
}

bool PSIsValidCustomerCodeList(const std::wstring& codes)
{
    std::wstring cur;
    size_t n = 0;
    for (size_t i = 0; i <= codes.size(); ++i)
    {
        wchar_t ch = i < codes.size() ? codes[i] : L';';
        if (ch == L';' || ch == L',')
        {
            std::wstring c = NormalizeCode(cur);
            cur.clear();
            if (c.empty()) continue;
            if (!PSIsValidCustomerCode(c) || ++n > kPSMaxCustomerCodes) return false;
        }
        else cur += ch;
    }
    return true;
}

std::vector<std::wstring> PSCustomerCodes()
{
    std::wstring s;
    if (!FPPolicyString(L"Store", L"CustomerCode", s) || s.empty()) ReadUserString(L"CustomerCode", s);
    return SplitCodes(s);
}

std::wstring PSCustomerCode() { return JoinCodes(PSCustomerCodes()); }

// names: "CODE<TAB>name" lines of the REG_MULTI_SZ value CustomerCodeNames
static std::vector<std::pair<std::wstring, std::wstring>> ReadCodeNames()
{
    std::vector<std::pair<std::wstring, std::wstring>> out;
    DWORD type = 0, size = 0;
    if (RegGetValueW(HKEY_CURRENT_USER, kPSRegKey, L"CustomerCodeNames", RRF_RT_REG_MULTI_SZ, &type, NULL, &size) != ERROR_SUCCESS ||
        size == 0 || size > 64 * 1024) return out;
    std::vector<wchar_t> buf(size / sizeof(wchar_t) + 2, 0);
    if (RegGetValueW(HKEY_CURRENT_USER, kPSRegKey, L"CustomerCodeNames", RRF_RT_REG_MULTI_SZ, &type, buf.data(), &size) != ERROR_SUCCESS)
        return out;
    for (const wchar_t* p = buf.data(); *p; p += wcslen(p) + 1)
    {
        std::wstring line = p;
        size_t tab = line.find(L'\t');
        if (tab != std::wstring::npos && tab > 0) out.push_back({ line.substr(0, tab), line.substr(tab + 1, 200) });
    }
    return out;
}

static void WriteCodes(const std::vector<std::wstring>& codes, const std::vector<std::pair<std::wstring, std::wstring>>& names)
{
    std::wstring joined = JoinCodes(codes);
    if (joined.empty()) RegDeleteKeyValueW(HKEY_CURRENT_USER, kPSRegKey, L"CustomerCode");
    else RegSetKeyValueW(HKEY_CURRENT_USER, kPSRegKey, L"CustomerCode", REG_SZ, joined.c_str(), (DWORD)((joined.size() + 1) * sizeof(wchar_t)));
    std::wstring multi;
    for (const auto& c : codes)
        for (const auto& n : names)
            if (n.first == c && !n.second.empty()) { multi += c + L"\t" + n.second; multi.push_back(0); break; }
    if (multi.empty()) RegDeleteKeyValueW(HKEY_CURRENT_USER, kPSRegKey, L"CustomerCodeNames");
    else
    {
        multi.push_back(0);
        RegSetKeyValueW(HKEY_CURRENT_USER, kPSRegKey, L"CustomerCodeNames", REG_MULTI_SZ, multi.data(), (DWORD)(multi.size() * sizeof(wchar_t)));
    }
}

int PSAddCustomerCode(const std::wstring& code, const std::wstring& customer)
{
    std::wstring c = NormalizeCode(code);
    if (c.empty() || !PSIsValidCustomerCode(c)) return 2;
    std::wstring s;
    ReadUserString(L"CustomerCode", s);
    auto codes = SplitCodes(s);
    auto names = ReadCodeNames();
    names.erase(std::remove_if(names.begin(), names.end(), [&](const auto& n) { return n.first == c; }), names.end());
    std::wstring label;   // the server's customer name: no control characters (REG_MULTI_SZ lines, TAB separator)
    for (wchar_t ch : customer) if (ch >= 0x20 && ch != 0x7F) label += ch;
    names.push_back({ c, label.substr(0, 200) });
    for (const auto& x : codes)
        if (x == c) { WriteCodes(codes, names); return 1; }   // known: the name may have changed
    if (codes.size() >= kPSMaxCustomerCodes) return 2;
    codes.push_back(c);
    WriteCodes(codes, names);
    return 0;
}

void PSRemoveCustomerCode(const std::wstring& code)
{
    std::wstring c = NormalizeCode(code), s;
    ReadUserString(L"CustomerCode", s);
    auto codes = SplitCodes(s);
    codes.erase(std::remove(codes.begin(), codes.end(), c), codes.end());
    WriteCodes(codes, ReadCodeNames());
}

std::wstring PSCustomerCodeName(const std::wstring& code)
{
    std::wstring c = NormalizeCode(code);
    for (const auto& n : ReadCodeNames()) if (n.first == c) return n.second;
    return L"";
}

void PSSaveCustomerCode(const std::wstring& codes)
{
    if (!PSIsValidCustomerCodeList(codes)) return;
    WriteCodes(SplitCodes(codes), ReadCodeNames());
}

bool PSUpdateBadgeEnabled()
{
    DWORD v = 1;
    return !FPPolicyDword(L"Store", L"UpdateBadge", v) || v != 0;
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

    std::wstring url = kDefaultUrl;
    bool locked = false;
    if (FPPolicyString(L"Store", L"ServerUrl", s) && !s.empty())
    { url = TrimUrl(s); locked = true; }
    else if (ReadUserString(L"ServerUrl", s) && !s.empty())
        url = TrimUrl(s);
    AcquireSRWLockExclusive(&g_urlLock);
    g_url = url;
    g_urlLocked = locked;
    ReleaseSRWLockExclusive(&g_urlLock);

    if (FPPolicyDword(L"Store", L"BetaChannel", d))
        g_beta = d != 0;
    else if (ReadUserDword(L"BetaChannel", d))
        g_beta = d != 0;

    DWORD verbose = 0;
    if (ReadUserDword(L"VerboseLog", verbose))
        FPLogSetVerbose(verbose != 0);
}

std::wstring PSServerUrl()
{
    AcquireSRWLockShared(&g_urlLock);
    std::wstring u = g_url;
    ReleaseSRWLockShared(&g_urlLock);
    return u;
}
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
