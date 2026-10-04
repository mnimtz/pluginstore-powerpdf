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

// "AddonStore-PowerPDF/0.4.2 (PowerPDF 15.1.0.555; Windows 10.0.26200; arm64)":
// client, host and OS version plus the machine's native architecture, for the
// store's usage reports. Technical data only, nothing personal.
static const wchar_t* UserAgent()
{
    static std::wstring ua;
    if (!ua.empty()) return ua.c_str();

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

    ua = std::wstring(L"AddonStore-PowerPDF/") + FP_VERSION_W + L" (PowerPDF " + host + L"; Windows " + os + L"; " + arch + L")";
    return ua.c_str();
}

// Opens the request and receives the response; returns the request handle
// chain via out-params (caller keeps the session/connect handles alive).
bool Send(const Url& u, HINTERNET& session, HINTERNET& connect, HINTERNET& request, DWORD* status,
          const wchar_t* method = L"GET", const std::string* body = nullptr)
{
#ifndef WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY_CONFIG
#define WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY_CONFIG 4
#endif
    if (!Allowed(u)) return false;
    session = WinHttpOpen(UserAgent(),
                          WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY_CONFIG,
                          WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!session) return false;
    DWORD timeout = 20000;
    WinHttpSetTimeouts(session, timeout, timeout, timeout, timeout);

    connect = WinHttpConnect(session, u.host.c_str(), u.port, 0);
    if (!connect) return false;

    request = WinHttpOpenRequest(connect, method, u.path.c_str(), NULL,
                                 WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES,
                                 u.https ? WINHTTP_FLAG_SECURE : 0);
    if (!request) return false;

    if (body)
    {
        static const wchar_t* kJson = L"Content-Type: application/json; charset=utf-8\r\n";
        if (!WinHttpSendRequest(request, kJson, (DWORD)-1L, (LPVOID)body->data(), (DWORD)body->size(),
                                (DWORD)body->size(), 0)) return false;
    }
    else if (!WinHttpSendRequest(request, WINHTTP_NO_ADDITIONAL_HEADERS, 0,
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

bool PSHttpPostJson(const std::wstring& url, const std::string& bodyUtf8, std::string& responseUtf8, DWORD* status)
{
    Url u;
    if (!Crack(url, u)) return false;
    HINTERNET hs = NULL, hc = NULL, hr = NULL;
    DWORD code = 0;
    bool ok = Send(u, hs, hc, hr, &code, L"POST", &bodyUtf8);
    Handle s(hs), c(hc), r(hr);
    if (status) *status = code;
    responseUtf8.clear();
    if (!hr || code == 0) { FPLogW(L"[Store] POST %s failed", url.c_str()); return false; }
    for (;;)
    {
        DWORD avail = 0;
        if (!WinHttpQueryDataAvailable(r, &avail) || avail == 0) break;
        std::string chunk(avail, 0);
        DWORD got = 0;
        if (!WinHttpReadData(r, chunk.data(), avail, &got) || got == 0) break;
        responseUtf8.append(chunk.data(), got);
        if (responseUtf8.size() > 1024 * 1024) break;
    }
    if (!ok) FPLogW(L"[Store] POST %s -> %u", url.c_str(), code);
    return ok;
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
