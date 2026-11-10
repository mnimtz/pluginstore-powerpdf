// http.cpp — see http.h. Plain WinHTTP, no dependencies.

#include "stdafx.h"
#include "http.h"
#include "hostversion.h"
#include "logging.h"
#include "access.h"
#include "settings.h"
#include "version.h"
#include "failover.h"
#include <winhttp.h>

#pragma comment(lib, "winhttp.lib")

namespace {

struct Handle
{
    HINTERNET h = NULL;
    Handle(HINTERNET v) : h(v) {}
    ~Handle() { if (h) WinHttpCloseHandle(h); }
    operator HINTERNET() const { return h; }
};

struct Url
{
    std::wstring host;
    std::wstring path;
    INTERNET_PORT port = 0;
    bool https = false;
};

bool Crack(const std::wstring& url, Url& out)
{
    URL_COMPONENTS uc = { sizeof(uc) };
    wchar_t host[256] = { 0 }, path[2048] = { 0 };
    uc.lpszHostName = host; uc.dwHostNameLength = 256;
    uc.lpszUrlPath = path;  uc.dwUrlPathLength = 2048;
    if (!WinHttpCrackUrl(url.c_str(), 0, 0, &uc)) return false;
    out.host = host;
    out.path = path[0] ? path : L"/";
    out.port = uc.nPort;
    out.https = uc.nScheme == INTERNET_SCHEME_HTTPS;
    return true;
}

bool IsLoopback(const std::wstring& host)
{
    return _wcsicmp(host.c_str(), L"localhost") == 0 || host == L"127.0.0.1" || host == L"[::1]" || host == L"::1";
}

// Customers open exactly one firewall rule: HTTPS to the configured store
// host. Plain HTTP is accepted only for local development, and no request
// may leave for a different host or port, whatever the catalog says.
bool Allowed(const Url& u)
{
    if (!u.https && !IsLoopback(u.host))
    {
        FPLogW(L"[Store] refused non-HTTPS URL for host %s", u.host.c_str());
        return false;
    }
    // C1.9.17: with the built-in address also the store's second address (failover.h)
    if (PSServerUrlIsDefault() && u.https && u.port == INTERNET_DEFAULT_HTTPS_PORT &&
        _wcsicmp(u.host.c_str(), psfailover::kFallbackHost) == 0)
        return true;
    Url store;
    if (!Crack(PSServerUrl(), store)) return false;
    if (_wcsicmp(store.host.c_str(), u.host.c_str()) != 0 || store.port != u.port || store.https != u.https)
    {
        FPLogW(L"[Store] refused request to %s:%u, store host is %s:%u",
               u.host.c_str(), (unsigned)u.port, store.host.c_str(), (unsigned)store.port);
        return false;
    }
    return true;
}

// "AddonStore-PowerPDF/0.4.2 (PowerPDF 15.1.0.555; Windows 10.0.26200; arm64)":
// client, host and OS version plus the machine's native architecture, for the
// store's usage reports. Technical data only, nothing personal.
static std::wstring BuildUserAgent()
{
    std::wstring host = L"0";
    wchar_t exe[MAX_PATH] = { 0 };
    GetModuleFileNameW(NULL, exe, MAX_PATH);
    DWORD dummy = 0, size = GetFileVersionInfoSizeW(exe, &dummy);
    if (size > 0)
    {
        std::vector<BYTE> data(size);
        VS_FIXEDFILEINFO* fi = nullptr;
        UINT len = 0;
        if (GetFileVersionInfoW(exe, 0, size, data.data()) &&
            VerQueryValueW(data.data(), L"\\", reinterpret_cast<void**>(&fi), &len) && fi)
        {
            wchar_t v[64];
            swprintf_s(v, 64, L"%u.%u.%u.%u", HIWORD(fi->dwFileVersionMS), LOWORD(fi->dwFileVersionMS),
                       HIWORD(fi->dwFileVersionLS), LOWORD(fi->dwFileVersionLS));
            host = v;
        }
    }
    // With the hotfix level ("2025.3.8") when the programs list knows it, so the
    // reports show which hotfixes are in use.
    {
        std::wstring hv = PSHostVersion();
        if (!hv.empty()) host = hv;
    }

    // RtlGetVersion reports the real OS version (GetVersionEx is shimmed).
    std::wstring os = L"0";
    typedef LONG(WINAPI* RtlGetVersionFn)(OSVERSIONINFOW*);
    if (auto rtl = reinterpret_cast<RtlGetVersionFn>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "RtlGetVersion")))
    {
        OSVERSIONINFOW vi = { sizeof(vi) };
        if (rtl(&vi) == 0)
        {
            wchar_t v[48];
            swprintf_s(v, 48, L"%lu.%lu.%lu", vi.dwMajorVersion, vi.dwMinorVersion, vi.dwBuildNumber);
            os = v;
        }
    }

    // Native machine, so Power PDF in ARM64EC emulation still reports arm64.
    std::wstring arch = L"x64";
    typedef BOOL(WINAPI* IsWow64Process2Fn)(HANDLE, USHORT*, USHORT*);
    if (auto wow = reinterpret_cast<IsWow64Process2Fn>(GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "IsWow64Process2")))
    {
        USHORT proc = 0, native = 0;
        if (wow(GetCurrentProcess(), &proc, &native) && native == 0xAA64 /* IMAGE_FILE_MACHINE_ARM64 */)
            arch = L"arm64";
    }

    return std::wstring(L"AddonStore-PowerPDF/") + FP_VERSION_W + L" (PowerPDF " + host + L"; Windows " + os + L"; " + arch + L")";
}

// Built once; a function-local static is initialized thread-safely (workers
// and the update check may ask at the same time).
static const wchar_t* UserAgent()
{
    static const std::wstring ua = BuildUserAgent();
    return ua.c_str();
}

using psfailover::Answer;
using psfailover::Outcome;

// Opens the request and receives the response head; the handles go to the caller, who
// closes them. how: how far it got, for the store's second address (failover.h).
// bare: no customer code or install id (the ping that checks a host).
bool Send(const Url& u, HINTERNET& session, HINTERNET& connect, HINTERNET& request, DWORD* status, Outcome& how,
          const wchar_t* method = L"GET", const std::string* body = nullptr, const std::wstring* codeOverride = nullptr,
          DWORD timeout = 20000, bool bare = false)
{
#ifndef WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY_CONFIG
#define WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY_CONFIG 4
#endif
    how = Outcome::NotSent;
    if (!Allowed(u)) return false;
    session = WinHttpOpen(UserAgent(),
                          WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY_CONFIG,
                          WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!session) return false;
    WinHttpSetTimeouts(session, timeout, timeout, timeout, timeout);
#ifndef WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_3
#define WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_3 0x00002000
#endif
    // TLS 1.2 or newer only; Windows versions without TLS 1.3 refuse that flag.
    DWORD protocols = WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_2 | WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_3;
    if (!WinHttpSetOption(session, WINHTTP_OPTION_SECURE_PROTOCOLS, &protocols, sizeof(protocols)))
    {
        protocols = WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_2;
        if (!WinHttpSetOption(session, WINHTTP_OPTION_SECURE_PROTOCOLS, &protocols, sizeof(protocols)))
            return false;   // never fall back to the system default (could allow TLS 1.0/1.1)
    }

    connect = WinHttpConnect(session, u.host.c_str(), u.port, 0);
    if (!connect) return false;

    request = WinHttpOpenRequest(connect, method, u.path.c_str(), NULL,
                                 WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES,
                                 u.https ? WINHTTP_FLAG_SECURE : 0);
    if (!request) return false;

    // Never follow a redirect: Allowed() checked THIS host only, and a
    // redirect could lead the download to any other address.
    DWORD redirect = WINHTTP_OPTION_REDIRECT_POLICY_NEVER;
    if (!WinHttpSetOption(request, WINHTTP_OPTION_REDIRECT_POLICY, &redirect, sizeof(redirect)))
        return false;   // the default policy would follow https redirects to other hosts

    // Customer deliveries: the code goes to the store host only (Allowed above).
    std::wstring headers;
    // how this Power PDF is licensed (C1.5.0): the store admins choose which modes may use the store
    headers += L"X-License-Mode: " + PSLicenseMode() + L"\r\n";
    // (codeOverride: a code being checked in the store window before it is stored)
    std::wstring customer = bare ? std::wstring() : codeOverride ? *codeOverride : PSCustomerCode();
    if (!customer.empty())
    {
        headers += L"X-Customer-Code: " + customer + L"\r\n";
        // C1.4.1: deliveries may allow a number of installations; the random id of this
        // installation counts one (the server stores only a hash of it per delivery)
        headers += L"X-Install-Id: " + PSInstallId() + L"\r\n";
    }
    if (body) headers += L"Content-Type: application/json; charset=utf-8\r\n";
    const wchar_t* h = headers.empty() ? WINHTTP_NO_ADDITIONAL_HEADERS : headers.c_str();
    DWORD hlen = headers.empty() ? 0 : (DWORD)-1L;

    // name lookup, connection, TLS handshake and certificate check all happen here
    BOOL sent = body ? WinHttpSendRequest(request, h, hlen, (LPVOID)body->data(), (DWORD)body->size(), (DWORD)body->size(), 0)
                     : WinHttpSendRequest(request, h, hlen, WINHTTP_NO_REQUEST_DATA, 0, 0, 0);
    if (!sent)
    {
        FPLogW(L"[Store] no connection to %s (WinHTTP error %lu)", u.host.c_str(), GetLastError());
        return false;
    }
    how = Outcome::NoAnswer;
    if (!WinHttpReceiveResponse(request, NULL))
    {
        FPLogW(L"[Store] no answer from %s (WinHTTP error %lu)", u.host.c_str(), GetLastError());
        return false;
    }

    DWORD code = 0, sz = sizeof(code);
    WinHttpQueryHeaders(request, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                        WINHTTP_HEADER_NAME_BY_INDEX, &code, &sz, WINHTTP_NO_HEADER_INDEX);
    if (status) *status = code;
    if (code == 0) return false;
    bool ok = code >= 200 && code < 300;
    how = ok ? Outcome::Ok : Outcome::HttpError;
    return ok;
}

enum class Read { Complete, Dropped, TooLarge };

// The body into out; with cut, a body larger than maxBytes is cut there and counts as complete.
Read ReadBody(HINTERNET r, std::string& out, size_t maxBytes, bool cut = false)
{
    out.clear();
    for (;;)
    {
        DWORD avail = 0;
        if (!WinHttpQueryDataAvailable(r, &avail)) return Read::Dropped;   // dropped connection: not a complete answer
        if (avail == 0) break;
        std::string chunk(avail, 0);
        DWORD got = 0;
        if (!WinHttpReadData(r, chunk.data(), avail, &got)) return Read::Dropped;
        if (got == 0) break;
        out.append(chunk.data(), got);
        if (out.size() > maxBytes) return cut ? Read::Complete : Read::TooLarge;
    }
    return Read::Complete;
}

// Is this host the store? GET /api/ping, short timeout, without customer code.
bool Ping(psfailover::Host host)
{
    const std::wstring url = std::wstring(psfailover::Base(host)) + L"/api/ping";
    Url u;
    if (!Crack(url, u)) return false;
    HINTERNET hs = NULL, hc = NULL, hr = NULL;
    DWORD status = 0;
    Outcome how;
    bool ok = Send(u, hs, hc, hr, &status, how, L"GET", nullptr, nullptr, psfailover::kPingTimeoutMs, true);
    Handle s(hs), c(hc), r(hr);
    std::string body;
    bool store = ok && status == 200 && ReadBody(r, body, 4096) == Read::Complete && psfailover::IsStorePing(body);
    FPLogW(L"[Store] ping %s: %s (status %u)", url.c_str(), store ? L"the store" : L"not the store", (unsigned)status);
    return store;
}

psfailover::State g_failover;   // the host this Power PDF process uses

// One store request; with the built-in address a failed one goes once more to the
// store's second address (failover.h). attempt: Answer(const std::wstring& url).
template <class Attempt>
Answer WithFailover(const std::wstring& url, bool repeatable, Attempt attempt)
{
    return psfailover::Run(g_failover, PSServerUrlIsDefault(), url, repeatable, GetTickCount64(), attempt, Ping,
        [](psfailover::Host from, psfailover::Host to) {
            if (to == psfailover::Host::Primary)
                FPLogW(L"[Store] %s answers as the store again, requests go there", psfailover::Base(to));
            else
                FPLogW(L"[Store] %s does not answer as the store, requests go to %s for now",
                       psfailover::Base(from), psfailover::Base(to));
        });
}

// GET into a string (the stored customer code, or codeOverride).
bool GetText(const std::wstring& url, std::string& outUtf8, DWORD* status, size_t maxBytes, const std::wstring* codeOverride)
{
    bool good = false;
    DWORD code = 0;
    WithFailover(url, true, [&](const std::wstring& target) {
        good = false;
        code = 0;
        Answer a;
        Url u;
        if (!Crack(target, u)) return a;
        HINTERNET hs = NULL, hc = NULL, hr = NULL;
        bool ok = Send(u, hs, hc, hr, &code, a.outcome, L"GET", nullptr, codeOverride);
        Handle s(hs), c(hc), r(hr);
        a.status = code;
        if (!ok) return a;
        Read rd = ReadBody(r, outUtf8, maxBytes);
        if (rd == Read::Dropped) a.outcome = Outcome::NoAnswer;
        good = rd == Read::Complete;
        return a;
    });
    if (status) *status = code;
    if (!good) outUtf8.clear();
    return good;
}

} // namespace

bool PSHttpGetText(const std::wstring& url, std::string& outUtf8, DWORD* status)
{
    DWORD code = 0;
    bool ok = GetText(url, outUtf8, &code, 16 * 1024 * 1024, nullptr);
    if (status) *status = code;
    if (!ok) FPLogW(L"[Store] GET %s failed (status %u)", url.c_str(), (unsigned)code);
    return ok;
}

bool PSHttpCheckCustomerCode(const std::wstring& url, const std::wstring& code, std::string& outUtf8, DWORD* status, size_t maxBytes)
{
    return GetText(url, outUtf8, status, maxBytes, &code);
}

bool PSHttpPostJson(const std::wstring& url, const std::string& bodyUtf8, std::string& responseUtf8, DWORD* status)
{
    bool good = false;
    DWORD code = 0;
    // repeatable = false: a POST that may have reached the store is never sent twice
    WithFailover(url, false, [&](const std::wstring& target) {
        good = false;
        code = 0;
        responseUtf8.clear();
        Answer a;
        Url u;
        if (!Crack(target, u)) return a;
        HINTERNET hs = NULL, hc = NULL, hr = NULL;
        bool ok = Send(u, hs, hc, hr, &code, a.outcome, L"POST", &bodyUtf8);
        Handle s(hs), c(hc), r(hr);
        a.status = code;
        if (!hr || code == 0) return a;
        // the server explains errors in the body: read it for every status
        if (ReadBody(r, responseUtf8, 1024 * 1024, true) == Read::Dropped)
        {
            a.outcome = Outcome::NoAnswer;
            return a;
        }
        good = ok;
        return a;
    });
    if (status) *status = code;
    if (code == 0) FPLogW(L"[Store] POST %s failed", url.c_str());
    else if (!good) FPLogW(L"[Store] POST %s -> %u", url.c_str(), (unsigned)code);
    return good;
}

bool PSHttpGetFile(const std::wstring& url, const std::wstring& targetPath, DWORD* status, unsigned long long maxBytes)
{
    bool good = false;
    DWORD code = 0;
    WithFailover(url, true, [&](const std::wstring& target) {
        good = false;
        code = 0;
        Answer a;
        Url u;
        if (!Crack(target, u)) return a;
        HINTERNET hs = NULL, hc = NULL, hr = NULL;
        bool ok = Send(u, hs, hc, hr, &code, a.outcome, L"GET");
        Handle s(hs), c(hc), r(hr);
        a.status = code;
        if (!ok) return a;

        HANDLE f = CreateFileW(targetPath.c_str(), GENERIC_WRITE, 0, NULL,
                               CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
        if (f == INVALID_HANDLE_VALUE) return a;   // a local problem: the other host would not help

        bool result = true;
        unsigned long long total = 0;
        for (;;)
        {
            DWORD avail = 0;
            if (!WinHttpQueryDataAvailable(r, &avail)) { result = false; a.outcome = Outcome::NoAnswer; break; }
            if (avail == 0) break;
            std::string chunk(avail, 0);
            DWORD got = 0;
            if (!WinHttpReadData(r, chunk.data(), avail, &got)) { result = false; a.outcome = Outcome::NoAnswer; break; }
            if (got == 0) break;
            total += got;
            if (total > maxBytes)
            {
                FPLogW(L"[Store] download %s refused: larger than %llu bytes", target.c_str(), maxBytes);
                result = false; break;
            }
            DWORD written = 0;
            if (!WriteFile(f, chunk.data(), got, &written, NULL) || written != got)
            { result = false; break; }
        }
        CloseHandle(f);
        if (!result) DeleteFileW(targetPath.c_str());
        good = result;
        return a;
    });
    if (status) *status = code;
    if (!good) FPLogW(L"[Store] download %s failed (status %u)", url.c_str(), (unsigned)code);
    return good;
}
