// http.cpp — see http.h. Plain WinHTTP, no dependencies.

#include "stdafx.h"
#include "http.h"
#include "logging.h"
#include "settings.h"
#include "version.h"
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

// Opens the request and receives the response; returns the request handle
// chain via out-params (caller keeps the session/connect handles alive).
bool Send(const Url& u, HINTERNET& session, HINTERNET& connect, HINTERNET& request, DWORD* status)
{
#ifndef WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY_CONFIG
#define WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY_CONFIG 4
#endif
    if (!Allowed(u)) return false;
    session = WinHttpOpen(L"AddonStore-PowerPDF/" FP_VERSION_W,
                          WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY_CONFIG,
                          WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!session) return false;
    DWORD timeout = 20000;
    WinHttpSetTimeouts(session, timeout, timeout, timeout, timeout);

    connect = WinHttpConnect(session, u.host.c_str(), u.port, 0);
    if (!connect) return false;

    request = WinHttpOpenRequest(connect, L"GET", u.path.c_str(), NULL,
                                 WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES,
                                 u.https ? WINHTTP_FLAG_SECURE : 0);
    if (!request) return false;

    if (!WinHttpSendRequest(request, WINHTTP_NO_ADDITIONAL_HEADERS, 0,
                            WINHTTP_NO_REQUEST_DATA, 0, 0, 0)) return false;
    if (!WinHttpReceiveResponse(request, NULL)) return false;

    DWORD code = 0, sz = sizeof(code);
    WinHttpQueryHeaders(request, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                        WINHTTP_HEADER_NAME_BY_INDEX, &code, &sz, WINHTTP_NO_HEADER_INDEX);
    if (status) *status = code;
    return code >= 200 && code < 300;
}

} // namespace

bool PSHttpGetText(const std::wstring& url, std::string& outUtf8, DWORD* status)
{
    Url u;
    if (!Crack(url, u)) return false;

    HINTERNET hs = NULL, hc = NULL, hr = NULL;
    bool ok = Send(u, hs, hc, hr, status);
    Handle s(hs), c(hc), r(hr);
    if (!ok) { FPLogW(L"[Store] GET %s failed (status %u)", url.c_str(), status ? *status : 0); return false; }

    outUtf8.clear();
    for (;;)
    {
        DWORD avail = 0;
        if (!WinHttpQueryDataAvailable(r, &avail) || avail == 0) break;
        std::string chunk(avail, 0);
        DWORD got = 0;
        if (!WinHttpReadData(r, chunk.data(), avail, &got) || got == 0) break;
        outUtf8.append(chunk.data(), got);
        if (outUtf8.size() > 16 * 1024 * 1024) return false;
    }
    return true;
}

bool PSHttpGetFile(const std::wstring& url, const std::wstring& targetPath, DWORD* status)
{
    Url u;
    if (!Crack(url, u)) return false;

    HINTERNET hs = NULL, hc = NULL, hr = NULL;
    bool ok = Send(u, hs, hc, hr, status);
    Handle s(hs), c(hc), r(hr);
    if (!ok) { FPLogW(L"[Store] download %s failed (status %u)", url.c_str(), status ? *status : 0); return false; }

    HANDLE f = CreateFileW(targetPath.c_str(), GENERIC_WRITE, 0, NULL,
                           CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f == INVALID_HANDLE_VALUE) return false;

    bool result = true;
    for (;;)
    {
        DWORD avail = 0;
        if (!WinHttpQueryDataAvailable(r, &avail)) { result = false; break; }
        if (avail == 0) break;
        std::string chunk(avail, 0);
        DWORD got = 0;
        if (!WinHttpReadData(r, chunk.data(), avail, &got)) { result = false; break; }
        if (got == 0) break;
        DWORD written = 0;
        if (!WriteFile(f, chunk.data(), got, &written, NULL) || written != got)
        { result = false; break; }
    }
    CloseHandle(f);
    if (!result) DeleteFileW(targetPath.c_str());
    return result;
}
