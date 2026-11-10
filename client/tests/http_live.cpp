// http_live.cpp - the real store/http.cpp against the real hosts (C1.9.17), without Power PDF.
// Needs the internet: client\tests\run_tests.cmd live
//
// What it shows:
//   1. built-in address: every request reaches the store, whether addon.power-pdf.de works or
//      not (then through https://ppdf-store.azurewebsites.net, see the log lines)
//   2. an own address (user or policy): no second address, a broken host stays broken
//   3. hosts outside the store stay refused, also pluginstore.azurewebsites.net

#include "stdafx.h"
#include "../store/http.h"
#include "../store/failover.h"
#include <cstdio>
#include <cstdarg>

// --- what http.cpp needs from the rest of the plug-in ---------------------------------
static std::wstring g_url = psfailover::kPrimaryBase;
static bool g_default = true;

std::wstring PSServerUrl() { return g_url; }
bool PSServerUrlIsDefault() { return g_default; }
std::wstring PSCustomerCode() { return L""; }
std::wstring PSInstallId() { return L""; }
std::wstring PSLicenseMode() { return L"unknown"; }
void FPLogW(const wchar_t* fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    std::wprintf(L"    log: ");
    std::vwprintf(fmt, ap);
    std::wprintf(L"\n");
    va_end(ap);
}

static int g_failed = 0;
static void Expect(bool ok, const wchar_t* what)
{
    std::wprintf(L"%s  %s\n", ok ? L"ok  " : L"FAIL", what);
    if (!ok) ++g_failed;
}

int wmain()
{
    std::string body;
    DWORD status = 0;

    std::wprintf(L"-- built-in address %s\n", psfailover::kPrimaryBase);
    bool ok = PSHttpGetText(std::wstring(psfailover::kPrimaryBase) + L"/api/ping", body, &status);
    Expect(ok && status == 200 && psfailover::IsStorePing(body), L"GET /api/ping answers as the store");
    ok = PSHttpGetText(std::wstring(psfailover::kPrimaryBase) + L"/api/catalog?format=tsv&lang=en", body, &status);
    Expect(ok && status == 200 && body.find("com.tungsten.") != std::string::npos, L"GET /api/catalog delivers the catalog");
    // a download URL of either address, as a cached catalog may hold it
    size_t p = body.find("\thttps://");
    std::wstring dl;
    if (p != std::string::npos)
    {
        size_t e = body.find('\t', p + 1);
        std::string u = body.substr(p + 1, e == std::string::npos ? std::string::npos : e - p - 1);
        dl.assign(u.begin(), u.end());
        wchar_t tmp[MAX_PATH] = { 0 };
        GetTempPathW(MAX_PATH, tmp);
        std::wstring path = std::wstring(tmp) + L"failover_live_test.ppak";
        ok = PSHttpGetFile(dl, path, &status, 64ull * 1024 * 1024);
        Expect(ok && status == 200, (L"download " + dl).c_str());
        DeleteFileW(path.c_str());
    }
    ok = PSHttpGetText(std::wstring(psfailover::kPrimaryBase) + L"/api/packages/no.such.package", body, &status);
    Expect(!ok && status == 404, L"the store's own 404 stays a 404");

    std::wprintf(L"-- refused hosts\n");
    ok = PSHttpGetText(L"https://pluginstore.azurewebsites.net/api/ping", body, &status);
    Expect(!ok && status == 0, L"pluginstore.azurewebsites.net is refused (someone else's)");
    ok = PSHttpGetText(L"https://example.com/", body, &status);
    Expect(!ok && status == 0, L"example.com is refused");

    std::wprintf(L"-- own address (user or policy): no second address\n");
    g_default = false;
    status = 0;
    ok = PSHttpGetText(std::wstring(psfailover::kPrimaryBase) + L"/api/ping", body, &status);
    std::wprintf(L"    addon.power-pdf.de alone: %s (status %u)\n", ok ? L"reachable" : L"not reachable", (unsigned)status);
    ok = PSHttpGetText(std::wstring(psfailover::kFallbackBase) + L"/api/ping", body, &status);
    Expect(!ok && status == 0, L"the second address is refused when another address is set");

    std::wprintf(L"%s\n", g_failed ? L"LIVE_FAILED" : L"LIVE_OK");
    return g_failed ? 1 : 0;
}
