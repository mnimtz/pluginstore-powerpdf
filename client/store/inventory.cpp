// inventory.cpp - see inventory.h.

#include "stdafx.h"
#include "inventory.h"
#include "access.h"
#include "http.h"
#include "install.h"
#include "settings.h"
#include "policy.h"
#include "version.h"
#include "logging.h"
#include "hostversion.h"
#include <algorithm>

namespace {

const wchar_t* kIdentityKey = L"Software\\Kofax\\PDF\\Tungsten Power PDF\\Identity";

// Domains of public mail services: they name no company, so they are not sent (the server refuses them too).
const wchar_t* kFreeMail[] = {
    L"gmail.com", L"googlemail.com", L"outlook.com", L"outlook.de", L"hotmail.com", L"hotmail.de", L"live.com", L"live.de",
    L"msn.com", L"yahoo.com", L"yahoo.de", L"icloud.com", L"me.com", L"mac.com", L"aol.com", L"aol.de", L"gmx.de", L"gmx.net",
    L"gmx.at", L"gmx.ch", L"web.de", L"t-online.de", L"freenet.de", L"posteo.de", L"mailbox.org", L"proton.me",
    L"protonmail.com", L"mail.ru", L"yandex.ru", L"yandex.com", L"zoho.com", L"qq.com", L"163.com", L"126.com",
    // C1.9.2: further providers in the 21 Power PDF languages
    L"gmx.com", L"ymail.com", L"yahoo.co.uk", L"yahoo.fr", L"yahoo.it", L"yahoo.es", L"yahoo.co.jp", L"hotmail.fr",
    L"hotmail.it", L"hotmail.es", L"hotmail.co.uk", L"outlook.fr", L"outlook.it", L"outlook.es", L"live.fr", L"live.it",
    L"orange.fr", L"free.fr", L"laposte.net", L"sfr.fr", L"wanadoo.fr", L"libero.it", L"virgilio.it", L"tiscali.it",
    L"seznam.cz", L"centrum.cz", L"email.cz", L"wp.pl", L"o2.pl", L"onet.pl", L"interia.pl", L"freemail.hu",
    L"citromail.hu", L"ziggo.nl", L"kpnmail.nl", L"telenet.be", L"skynet.be", L"bluewin.ch", L"terra.com.br",
    L"uol.com.br", L"bol.com.br", L"sapo.pt", L"naver.com", L"daum.net", L"hanmail.net", L"yandex.com.tr", L"rambler.ru",
    L"list.ru", L"bk.ru", L"inbox.ru", L"sina.com", L"sohu.com", L"foxmail.com", L"yeah.net", L"tutanota.com",
    L"icloud.de", L"arcor.de", L"online.de", L"vodafone.de", L"kabelmail.de", L"emailn.de",
};

bool IsAscii(const std::wstring& s, const wchar_t* extra)
{
    return std::all_of(s.begin(), s.end(), [extra](wchar_t c) {
        return (c >= L'a' && c <= L'z') || (c >= L'A' && c <= L'Z') || (c >= L'0' && c <= L'9') || (c && wcschr(extra, c));
    });
}

std::string Narrow(const std::wstring& s)   // ASCII only (checked)
{
    std::string r;
    r.reserve(s.size());
    for (wchar_t c : s) r.push_back(static_cast<char>(c));
    return r;
}

DWORD Today()
{
    SYSTEMTIME t;
    GetSystemTime(&t);
    return t.wYear * 10000u + t.wMonth * 100u + t.wDay;
}

} // namespace

std::wstring PSInventoryDomain()
{
    wchar_t buf[320] = { 0 };
    DWORD sz = sizeof(buf);
    if (RegGetValueW(HKEY_CURRENT_USER, kIdentityKey, L"email", RRF_RT_REG_SZ, NULL, buf, &sz) != ERROR_SUCCESS) return L"";
    std::wstring mail = buf;
    size_t at = mail.rfind(L'@');
    if (at == std::wstring::npos) return L"";
    std::wstring d = mail.substr(at + 1);
    while (!d.empty() && (d.back() == L' ' || d.back() == L'\t' || d.back() == L'.')) d.pop_back();
    std::transform(d.begin(), d.end(), d.begin(), [](wchar_t c) { return (wchar_t)towlower(c); });
    if (d.size() < 4 || d.size() > 100 || d.find(L'.') == std::wstring::npos || !IsAscii(d, L".-")) return L"";
    for (const wchar_t* f : kFreeMail) if (d == f) return L"";
    return d;
}

void PSInventoryReportIfDue()
{
    DWORD v = 1;
    if (FPPolicyDword(L"Store", L"Inventory", v) && v == 0) return;
    if (!PSStoreAllowed()) return;   // C1.9.2: nothing where this license mode may not use the store
    DWORD last = 0, sz = sizeof(last), today = Today();
    if (RegGetValueW(HKEY_CURRENT_USER, kPSRegKey, L"InventorySent", RRF_RT_REG_DWORD, NULL, &last, &sz) == ERROR_SUCCESS && last == today) return;
    std::wstring domain = PSInventoryDomain();
    std::wstring id = PSInstallId();
    if (domain.empty() || id.size() != 36 || !IsAscii(id, L"-")) return;

    std::wstring host = PSHostVersion();
    if (host.size() > 30 || !IsAscii(host, L".")) host.clear();
    std::string body = "{\"installId\":\"" + Narrow(id) + "\",\"domain\":\"" + Narrow(domain) +
                       "\",\"mode\":\"" + Narrow(PSLicenseMode()) + "\",\"hostVersion\":\"" + Narrow(host) +
                       "\",\"clientVersion\":\"" + Narrow(FP_VERSION_W) + "\",\"addons\":[";
    int n = 0;
    for (const auto& a : PSListInstalledAddons(L"en"))
    {
        if (n >= 200 || a.id.empty() || a.id.size() > 100 || !IsAscii(a.id, L"._-") || a.version.size() > 30 || !IsAscii(a.version, L".")) continue;
        body += (n++ ? ",{\"id\":\"" : "{\"id\":\"") + Narrow(a.id) + "\",\"version\":\"" + Narrow(a.version) + "\"}";
    }
    body += "]}";

    std::string answer;
    DWORD status = 0;
    if (PSHttpPostJson(PSServerUrl() + L"/api/client/inventory", body, answer, &status) && status == 200)
    {
        RegSetKeyValueW(HKEY_CURRENT_USER, kPSRegKey, L"InventorySent", REG_DWORD, &today, sizeof(today));
        FPLogW(L"[Store] daily state reported (%s)", answer.find("\"stored\":true") != std::string::npos ? L"stored" : L"not used by the server");
    }
    else
    {
        FPLogW(L"[Store] daily state not reported (HTTP %lu), next try at the next check", status);
    }
}
