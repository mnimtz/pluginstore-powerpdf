// access.cpp - see access.h.

#include "stdafx.h"
#include "access.h"
#include "http.h"
#include "settings.h"
#include "logging.h"
#include "inventory.h"

extern "C" HINSTANCE gHINSTANCE;

namespace {

volatile LONG g_running = 0;
volatile LONG g_allowed = -1;   // -1 = not read yet, then 0 / 1 (HKCU StoreAccess)

std::wstring ReadMachine(const wchar_t* sub, const wchar_t* name)
{
    wchar_t buf[512] = { 0 };
    DWORD sz = sizeof(buf);
    // the 64-bit view: Power PDF writes its keys there (also under ARM64 emulation)
    HKEY k = NULL;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, sub, 0, KEY_QUERY_VALUE | KEY_WOW64_64KEY, &k) != ERROR_SUCCESS) return L"";
    LSTATUS rc = RegGetValueW(k, NULL, name, RRF_RT_REG_SZ, NULL, buf, &sz);
    RegCloseKey(k);
    if (rc != ERROR_SUCCESS) return L"";
    std::wstring v = buf;
    while (!v.empty() && (v.back() == L' ' || v.back() == L'\t')) v.pop_back();
    while (!v.empty() && (v.front() == L' ' || v.front() == L'\t')) v.erase(v.begin());
    return v;
}

struct Job { HWND notify; UINT message; std::wstring url; };

DWORD WINAPI Worker(LPVOID p)
{
    Job* job = static_cast<Job*>(p);
    std::string body;
    DWORD status = 0;
    if (PSHttpGetText(job->url, body, &status) && status == 200)
    {
        // {"ok":true,"data":{"allowed":true|false,...}}
        size_t i = body.find("\"allowed\":");
        size_t v = i == std::string::npos ? i : body.find_first_not_of(" \t\r\n", i + 10);   // C1.9.2: whitespace allowed
        // only a literal true or false counts; anything else keeps the last answer
        if (v != std::string::npos && (body.compare(v, 4, "true") == 0 || body.compare(v, 5, "false") == 0))
        {
            bool allowed = body.compare(v, 4, "true") == 0;
            FPLogW(L"[Store] access for license mode %s: %s", PSLicenseMode().c_str(), allowed ? L"allowed" : L"not allowed");
            if (job->notify) PostMessageW(job->notify, job->message, allowed ? 1 : 0, 0);
        }
    }
    else
    {
        FPLogW(L"[Store] access check not reachable (HTTP %lu), keeping the last answer", status);
    }
    PSInventoryReportIfDue();   // C1.8.0: once a day, on this worker (at start, then every 4 h)
    delete job;
    InterlockedExchange(&g_running, 0);
    FreeLibraryAndExitThread(gHINSTANCE, 0);
}

} // namespace

std::wstring PSLicenseMode()
{
    const wchar_t* v1 = L"SOFTWARE\\Kofax\\PDF\\V1";
    if (!ReadMachine(v1, L"SerialNumber").empty()) return L"serial";
    if (!ReadMachine(L"SOFTWARE\\Kofax\\PDF\\V1\\CLS", L"LicenseURL").empty()) return L"cloud";
    return L"unknown";
}

bool PSStoreAllowed()
{
    LONG a = g_allowed;
    if (a < 0)
    {
        DWORD v = 1, sz = sizeof(v);
        if (RegGetValueW(HKEY_CURRENT_USER, kPSRegKey, L"StoreAccess", RRF_RT_REG_DWORD, NULL, &v, &sz) != ERROR_SUCCESS) v = 1;
        a = v ? 1 : 0;
        InterlockedExchange(&g_allowed, a);
    }
    return a != 0;
}

void PSAccessTakeResult(bool allowed)
{
    if (PSStoreAllowed() == allowed) return;
    InterlockedExchange(&g_allowed, allowed ? 1 : 0);
    DWORD v = allowed ? 1 : 0;
    RegSetKeyValueW(HKEY_CURRENT_USER, kPSRegKey, L"StoreAccess", REG_DWORD, &v, sizeof(v));
}

void PSAccessCheckStart(HWND notify, UINT message)
{
    if (InterlockedCompareExchange(&g_running, 1, 0) != 0) return;
    HMODULE self = NULL;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, reinterpret_cast<LPCWSTR>(&Worker), &self))
    { InterlockedExchange(&g_running, 0); return; }
    Job* job = new Job{ notify, message, PSServerUrl() + L"/api/client/access" };
    HANDLE t = CreateThread(NULL, 0, Worker, job, 0, NULL);
    if (t) CloseHandle(t);
    else { delete job; FreeLibrary(self); InterlockedExchange(&g_running, 0); }
}
