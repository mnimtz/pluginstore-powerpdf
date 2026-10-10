// webui.cpp — see webui.h.

#include "stdafx.h"
#include "webui.h"
#include "dialog.h"
#include "catalog.h"
#include "access.h"
#include "install.h"
#include "http.h"
#include "settings.h"
#include "policy.h"
#include "loc.h"
#include "logging.h"
#include "clocale.h"
#include "hostversion.h"
#include "blocklist.h"
#include "Resource.h"
#include "version.h"
#include <wrl.h>
#include <wrl/event.h>
#include <shlobj.h>
#include <wincrypt.h>
#include <vector>
#include <functional>
#include <memory>
#include <algorithm>
#include <atomic>
#include "WebView2.h"

#pragma comment(lib, "WebView2LoaderStatic.lib")
#pragma comment(lib, "version.lib")
#pragma comment(lib, "crypt32.lib")

extern "C" HINSTANCE gHINSTANCE;

using Microsoft::WRL::Callback;
using Microsoft::WRL::ComPtr;

namespace webui {

const wchar_t* kClientId = L"com.tungsten.pluginstore";

// --- small helpers ----------------------------------------------------------

std::wstring Json(const std::wstring& s)
{
    std::wstring o = L"\"";
    for (wchar_t c : s)
    {
        switch (c)
        {
        case L'"':  o += L"\\\""; break;
        case L'\\': o += L"\\\\"; break;
        case L'\n': o += L"\\n"; break;
        case L'\r': o += L"\\r"; break;
        case L'\t': o += L"\\t"; break;
        default:
            if (c < 0x20 || c == 0x2028 || c == 0x2029) { wchar_t b[8]; swprintf_s(b, 8, L"\\u%04x", (unsigned)c); o += b; }
            else o += c;
        }
    }
    return o + L"\"";
}

// Reads a string field from a flat JSON object sent by the page ({"cmd":"install","id":"..."}).
std::wstring Field(const std::wstring& json, const wchar_t* key, size_t maxLen = 512)
{
    std::wstring k = std::wstring(L"\"") + key + L"\"";
    size_t p = json.find(k);
    if (p == std::wstring::npos) return std::wstring();
    p = json.find(L':', p + k.size());
    if (p == std::wstring::npos) return std::wstring();
    p = json.find(L'"', p);
    if (p == std::wstring::npos) return std::wstring();
    std::wstring v;
    for (size_t i = p + 1; i < json.size() && v.size() <= maxLen; ++i)
    {
        wchar_t c = json[i];
        if (c == L'"') return v;
        if (c == L'\\' && i + 1 < json.size())
        {
            wchar_t n = json[++i];
            if (n == L'n') v += L'\n'; else if (n == L't') v += L'\t'; else if (n == L'u' && i + 4 < json.size())
            { v += (wchar_t)wcstoul(json.substr(i + 1, 4).c_str(), NULL, 16); i += 4; }
            else v += n;
        }
        else v += c;
    }
    return std::wstring();
}

// A number or true/false of a flat JSON answer ("" when missing).
std::wstring RawField(const std::wstring& json, const wchar_t* key)
{
    std::wstring k = std::wstring(L"\"") + key + L"\"";
    size_t p = json.find(k);
    if (p == std::wstring::npos) return std::wstring();
    p = json.find(L':', p + k.size());
    if (p == std::wstring::npos) return std::wstring();
    size_t s = json.find_first_not_of(L" \t\r\n", p + 1), e = json.find_first_of(L",}", s);
    return s == std::wstring::npos || e == std::wstring::npos ? std::wstring() : json.substr(s, e - s);
}

std::wstring Fmt(UINT id, const std::wstring& a, const std::wstring& b = std::wstring())
{
    wchar_t buf[1200];
    _snwprintf_s(buf, 1200, _TRUNCATE, FPLoc(id).c_str(), a.c_str(), b.c_str());
    return buf;
}

std::wstring FmtInt(UINT id, int n)
{
    wchar_t buf[600];
    _snwprintf_s(buf, 600, _TRUNCATE, FPLoc(id).c_str(), n);
    return buf;
}

std::wstring Base64(const std::vector<BYTE>& data)
{
    DWORD len = 0;
    if (!CryptBinaryToStringW(data.data(), (DWORD)data.size(), CRYPT_STRING_BASE64 | CRYPT_STRING_NOCRLF, NULL, &len)) return std::wstring();
    std::wstring out(len, L'\0');
    if (!CryptBinaryToStringW(data.data(), (DWORD)data.size(), CRYPT_STRING_BASE64 | CRYPT_STRING_NOCRLF, &out[0], &len)) return std::wstring();
    out.resize(len);
    return out;
}

std::string U8(const std::wstring& w)
{
    if (w.empty()) return std::string();
    int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), NULL, 0, NULL, NULL);
    std::string s(n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), &s[0], n, NULL, NULL);
    return s;
}

std::wstring W16(const std::string& s)
{
    if (s.empty()) return std::wstring();
    int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), NULL, 0);
    std::wstring w(n, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), &w[0], n);
    return w;
}

// Percent-encoding (UTF-8) for a query value.
std::wstring UrlEncode(const std::wstring& v)
{
    std::string u = U8(v);
    std::wstring o;
    for (unsigned char c : u)
    {
        if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.' || c == '~')
            o += (wchar_t)c;
        else { wchar_t b[4]; swprintf_s(b, 4, L"%%%02X", c); o += b; }
    }
    return o;
}

// Number after "key": in a server JSON response (flat search is enough here).
double JsonNumber(const std::string& json, const char* key)
{
    std::string k = std::string("\"") + key + "\":";
    size_t p = json.find(k);
    return p == std::string::npos ? 0 : _strtod_l(json.c_str() + p + k.size(), NULL, FPCLocale());
}

// "4.3" without locale influence (JSON numbers for the page).
std::wstring Tenths(double v)
{
    if (!(v > 0)) v = 0;   // also NaN
    if (v > 5) v = 5;
    int t = (int)(v * 10 + 0.5);
    wchar_t b[32];
    swprintf_s(b, 32, L"%d.%d", t / 10, t % 10);
    return b;
}

// Last lines of the store log for a problem report (only when the user ticks it).
std::wstring LogTail(size_t maxLines)
{
    wchar_t tmp[MAX_PATH];
    GetTempPathW(MAX_PATH, tmp);
    HANDLE f = CreateFileW((std::wstring(tmp) + L"PluginStore.log").c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                           NULL, OPEN_EXISTING, 0, NULL);
    if (f == INVALID_HANDLE_VALUE) return std::wstring();
    LARGE_INTEGER sz = { 0 };
    GetFileSizeEx(f, &sz);
    LONGLONG start = sz.QuadPart > 64 * 1024 ? sz.QuadPart - 64 * 1024 : 0;
    LARGE_INTEGER off; off.QuadPart = start;
    SetFilePointerEx(f, off, NULL, FILE_BEGIN);
    std::string data((size_t)(sz.QuadPart - start), '\0');
    DWORD got = 0;
    ReadFile(f, data.data(), (DWORD)data.size(), &got, NULL);
    CloseHandle(f);
    data.resize(got);
    std::wstring text = W16(data);
    size_t pos = text.size(), lines = 0;
    while (pos > 0 && lines <= maxLines) { pos = text.rfind(L'\n', pos - 1); if (pos == std::wstring::npos) { pos = 0; break; } ++lines; }
    text = text.substr(pos);
    return text.size() > 18000 ? text.substr(text.size() - 18000) : text;
}

std::wstring HostLang()
{
    char code[64] = { 0 };
    DURING DVAppGetLanguage(code); HANDLER END_HANDLER
    wchar_t w[64] = { 0 };
    MultiByteToWideChar(CP_ACP, 0, code, -1, w, 64);
    return w;
}

std::wstring LocalDir(const wchar_t* sub)
{
    PWSTR base = nullptr;
    std::wstring dir;
    if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, NULL, &base)))
    {
        dir = std::wstring(base) + L"\\Tungsten\\AddonStore\\" + sub;
        SHCreateDirectoryExW(NULL, dir.c_str(), NULL);
    }
    if (base) CoTaskMemFree(base);
    return dir;
}

UINT DpiOf(HWND h)
{
    HDC dc = ::GetDC(h);
    UINT dpi = dc ? (UINT)GetDeviceCaps(dc, LOGPIXELSY) : 96;
    if (dc) ::ReleaseDC(h, dc);
    return dpi ? dpi : 96;
}

bool SafeFileName(const std::wstring& s)
{
    for (wchar_t c : s)
        if (!((c >= L'a' && c <= L'z') || (c >= L'0' && c <= L'9') || c == L'.' || c == L'-')) return false;
    return !s.empty();
}

std::wstring PageHtml()
{
    HRSRC r = FindResourceW(gHINSTANCE, MAKEINTRESOURCEW(IDR_STORE_HTML), RT_RCDATA);
    if (!r) return std::wstring();
    HGLOBAL g = LoadResource(gHINSTANCE, r);
    const char* p = g ? (const char*)LockResource(g) : nullptr;
    DWORD n = SizeofResource(gHINSTANCE, r);
    if (!p || !n) return std::wstring();
    int need = MultiByteToWideChar(CP_UTF8, 0, p, (int)n, NULL, 0);
    std::wstring w((size_t)need, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, p, (int)n, &w[0], need);
    return w;
}

// Reads a small file completely (icons, screenshots); empty when missing or too large.
std::vector<BYTE> ReadSmallFile(const std::wstring& path, LONGLONG maxBytes)
{
    std::vector<BYTE> data;
    HANDLE f = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    if (f == INVALID_HANDLE_VALUE) return data;
    LARGE_INTEGER sz = { 0 };
    if (GetFileSizeEx(f, &sz) && sz.QuadPart > 8 && sz.QuadPart <= maxBytes)
    {
        data.resize((size_t)sz.QuadPart);
        DWORD got = 0;
        if (!ReadFile(f, data.data(), (DWORD)data.size(), &got, NULL) || got != data.size()) data.clear();
    }
    CloseHandle(f);
    return data;
}

// --- worker threads -----------------------------------------------------------

const UINT WM_ASYNC = WM_APP + 64;
enum AsyncKind { KCatalog = 1, KRated, KFeedback, KSend, KJob, KCustomer, KCodeInfo, KCodePreview };

// Manifest language code of the UI language (names of installed add-ons, C1.4.1).
std::wstring ManifestLang()
{
    LANGID l = FPLocLangId();
    switch (PRIMARYLANGID(l))
    {
    case LANG_GERMAN: return L"de";     case LANG_FRENCH: return L"fr";     case LANG_ITALIAN: return L"it";
    case LANG_SPANISH: return L"es";    case LANG_DUTCH: return L"nl";      case LANG_PORTUGUESE: return L"pt";
    case LANG_DANISH: return L"da";     case LANG_FINNISH: return L"fi";    case LANG_NORWEGIAN: return L"nb";
    case LANG_SWEDISH: return L"sv";    case LANG_POLISH: return L"pl";     case LANG_CZECH: return L"cs";
    case LANG_HUNGARIAN: return L"hu";  case LANG_RUSSIAN: return L"ru";    case LANG_TURKISH: return L"tr";
    case LANG_CHINESE: return SUBLANGID(l) == SUBLANG_CHINESE_TRADITIONAL ? L"zh-Hant" : L"zh-Hans";
    case LANG_JAPANESE: return L"ja";   case LANG_KOREAN: return L"ko";     case LANG_ARABIC: return L"ar";
    default: return L"en";
    }
}

// The objects of a JSON array field, e.g. "installs":[{...},{...}] (flat objects only).
std::vector<std::wstring> JsonObjectArray(const std::wstring& json, const wchar_t* key)
{
    std::vector<std::wstring> out;
    std::wstring k = std::wstring(L"\"") + key + L"\":[";
    size_t p = json.find(k);
    if (p == std::wstring::npos) return out;
    p += k.size();
    while (out.size() < 200)
    {
        size_t a = json.find_first_of(L"{]", p);
        if (a == std::wstring::npos || json[a] == L']') break;
        size_t b = json.find(L'}', a);
        if (b == std::wstring::npos) break;
        out.push_back(json.substr(a, b - a + 1));
        p = b + 1;
    }
    return out;
}

// The strings of a JSON array field, e.g. "packages":["a","b"] (ids only, no escapes needed).
std::vector<std::wstring> JsonStringArray(const std::wstring& json, const wchar_t* key)
{
    std::vector<std::wstring> out;
    std::wstring k = std::wstring(L"\"") + key + L"\":[";
    size_t p = json.find(k);
    if (p == std::wstring::npos) return out;
    p += k.size();
    size_t end = json.find(L']', p);
    if (end == std::wstring::npos) return out;
    while (p < end && out.size() < 200)
    {
        size_t a = json.find(L'"', p);
        if (a == std::wstring::npos || a >= end) break;
        size_t b = json.find(L'"', a + 1);
        if (b == std::wstring::npos || b > end) break;
        out.push_back(json.substr(a + 1, std::min<size_t>(b - a - 1, 200)));
        p = b + 1;
    }
    return out;
}

// Result of a worker, posted to the window (which deletes it).
struct AsyncMsg
{
    int kind = 0;
    int gen = 0;                          // catalog generation (stale results are dropped)
    bool flag = false;                    // success / "with preselect"
    int number = 0, count = 0;            // job or stars / rc or rating count
    double rating = 0;
    std::wstring id, text, error;
    std::vector<PSCatalogEntry> entries;
    bool cached = false;                  // C1.9.7: entries from the stored catalog, not from the server
};

// The store window that accepts results. Posting happens under the lock, so
// once OnDestroy has cleared it and drained the queue nothing new arrives
// (a reused HWND never receives a stale result either).
SRWLOCK g_asyncLock = SRWLOCK_INIT;
HWND g_asyncTarget = NULL;
// Bumped for every icon load and when the window closes: older icon workers stop.
std::atomic<int> g_iconGen{ 0 };

// False when the window is gone (the message is then deleted here).
bool PostAsync(HWND h, AsyncMsg* m)
{
    bool posted = false;
    AcquireSRWLockShared(&g_asyncLock);
    if (h && h == g_asyncTarget)
        posted = ::PostMessageW(h, WM_ASYNC, 0, reinterpret_cast<LPARAM>(m)) != FALSE;
    ReleaseSRWLockShared(&g_asyncLock);
    if (!posted) delete m;
    return posted;
}

DWORD WINAPI WorkerMain(LPVOID p)
{
    std::unique_ptr<std::function<void()>> fn(static_cast<std::function<void()>*>(p));
    // COM for ShellExecuteEx in the elevated install step
    HRESULT co = CoInitializeEx(NULL, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE);
    try { (*fn)(); } catch (...) { FPLogW(L"[Store] worker failed"); }
    if (SUCCEEDED(co)) CoUninitialize();
    fn.reset();
    // the thread holds its own reference on this DLL (see Spawn)
    FreeLibraryAndExitThread(gHINSTANCE, 0);
}

// Runs work on a new thread that keeps this DLL loaded until it ends.
void Spawn(std::function<void()> work)
{
    auto* p = new std::function<void()>(std::move(work));
    HMODULE self = NULL;
    if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, reinterpret_cast<LPCWSTR>(&WorkerMain), &self))
    {
        HANDLE t = CreateThread(NULL, 0, WorkerMain, p, 0, NULL);
        if (t) { CloseHandle(t); return; }
        FreeLibrary(self);
    }
    std::unique_ptr<std::function<void()>> fn(p);   // no thread: run it here
    (*fn)();
}

// --- the window -------------------------------------------------------------

class CWebStore : public CDialog
{
public:
    explicit CWebStore(const std::wstring& preselect) : CDialog(IDD_PS_WEB), m_preselect(preselect) {}

protected:
    enum { WM_FAIL = WM_APP + 63 };
    enum Job { JobInstall = 1, JobUninstall, JobSelfUpdate, JobCodeRemove };
    // removal of a customer code (C1.4.1): the add-ons that came with this code only,
    // found by the preview, removed together with the code
    std::wstring m_pendingCode;
    std::vector<PSCatalogEntry> m_pendingRemoval;

    ComPtr<ICoreWebView2Environment> m_env;
    ComPtr<ICoreWebView2Controller> m_ctrl;
    ComPtr<ICoreWebView2> m_web;
    std::vector<PSCatalogEntry> m_entries;
    PSCatalogEntry m_self;
    bool m_hasSelfUpdate = false;
    std::wstring m_preselect;
    int m_catalogGen = 0;
    bool m_jobRunning = false;
    std::vector<std::wstring> m_migrateKeys;   // "<old root>|<id>" of the add-ons offered (C1.1.4)
    struct Attachment { std::wstring name, data; };   // data: base64 from the page (C1.2.0)
    std::vector<Attachment> m_attach;
    std::vector<std::pair<std::wstring, std::wstring>> m_news;   // id, version shown under "What's new" (C1.2.0)
    // WebView2 completes its creation callbacks through the message loop, possibly
    // after the dialog (a stack object) is gone: the callbacks check this flag first.
    std::shared_ptr<bool> m_alive = std::make_shared<bool>(true);

    BOOL OnInitDialog() override
    {
        CDialog::OnInitDialog();
        AcquireSRWLockExclusive(&g_asyncLock);
        g_asyncTarget = m_hWnd;
        ReleaseSRWLockExclusive(&g_asyncLock);
        SetWindowTextW(FPLoc(IDS_PSD_TITLE).c_str());
        HICON ico = (HICON)LoadImageW(gHINSTANCE, MAKEINTRESOURCEW(IDI_PS_STORE), IMAGE_ICON, 0, 0, LR_DEFAULTSIZE);
        if (ico) { SetIcon(ico, TRUE); SetIcon(ico, FALSE); }

        // About 1000 x 680 px at 100 %, scaled with the monitor DPI, never larger than the work area.
        UINT dpi = DpiOf(m_hWnd);
        int w = MulDiv(1000, dpi, 96), h = MulDiv(680, dpi, 96);
        MONITORINFO mi = { sizeof(mi) };
        GetMonitorInfoW(MonitorFromWindow(m_hWnd, MONITOR_DEFAULTTONEAREST), &mi);
        w = min(w, (int)(mi.rcWork.right - mi.rcWork.left) - 40);
        h = min(h, (int)(mi.rcWork.bottom - mi.rcWork.top) - 40);
        SetWindowPos(NULL, 0, 0, w, h, SWP_NOMOVE | SWP_NOZORDER);
        CenterWindow();

        std::wstring udf = LocalDir(L"WebView2");
        HRESULT hr = CreateCoreWebView2EnvironmentWithOptions(nullptr, udf.empty() ? nullptr : udf.c_str(), nullptr,
            Callback<ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler>(
                [this, alive = m_alive](HRESULT res, ICoreWebView2Environment* env) -> HRESULT { return *alive ? OnEnvironment(res, env) : S_OK; }).Get());
        if (FAILED(hr)) { FPLogW(L"[Store] WebView2 environment failed (0x%08x)", (unsigned)hr); PostMessage(WM_FAIL); }
        return TRUE;
    }

    HRESULT OnEnvironment(HRESULT res, ICoreWebView2Environment* env)
    {
        if (FAILED(res) || !env || !::IsWindow(m_hWnd)) { FPLogW(L"[Store] WebView2 environment callback failed (0x%08x)", (unsigned)res); PostMessage(WM_FAIL); return S_OK; }
        m_env = env;
        return env->CreateCoreWebView2Controller(m_hWnd, Callback<ICoreWebView2CreateCoreWebView2ControllerCompletedHandler>(
            [this, alive = m_alive](HRESULT r, ICoreWebView2Controller* c) -> HRESULT {
                if (*alive) return OnController(r, c);
                if (c) c->Close();   // window closed while WebView2 was starting
                return S_OK; }).Get());
    }

    HRESULT OnController(HRESULT res, ICoreWebView2Controller* ctrl)
    {
        if (FAILED(res) || !ctrl || !::IsWindow(m_hWnd)) { FPLogW(L"[Store] WebView2 controller failed (0x%08x)", (unsigned)res); PostMessage(WM_FAIL); return S_OK; }
        m_ctrl = ctrl;
        m_ctrl->get_CoreWebView2(&m_web);

        ComPtr<ICoreWebView2Settings> s;
        if (SUCCEEDED(m_web->get_Settings(&s)) && s)
        {
            s->put_AreDevToolsEnabled(FALSE);
            s->put_AreDefaultContextMenusEnabled(FALSE);
            s->put_IsStatusBarEnabled(FALSE);
            s->put_IsZoomControlEnabled(FALSE);
            s->put_IsBuiltInErrorPageEnabled(FALSE);
            s->put_AreHostObjectsAllowed(FALSE);          // C1.3.1: the page talks only through messages
            ComPtr<ICoreWebView2Settings4> s4;
            if (SUCCEEDED(s.As(&s4)) && s4)
            {
                s4->put_IsPasswordAutosaveEnabled(FALSE);
                s4->put_IsGeneralAutofillEnabled(FALSE);
            }
        }

        EventRegistrationToken t;
        // The page is local; it must never navigate anywhere or open windows.
        m_web->add_NavigationStarting(Callback<ICoreWebView2NavigationStartingEventHandler>(
            [](ICoreWebView2*, ICoreWebView2NavigationStartingEventArgs* a) -> HRESULT {
                LPWSTR uri = nullptr;
                a->get_Uri(&uri);
                std::wstring u = uri ? uri : L"";
                if (uri) CoTaskMemFree(uri);
                if (u.rfind(L"about:blank", 0) != 0 && u.rfind(L"data:", 0) != 0) a->put_Cancel(TRUE);
                return S_OK;
            }).Get(), &t);
        m_web->add_NewWindowRequested(Callback<ICoreWebView2NewWindowRequestedEventHandler>(
            [](ICoreWebView2*, ICoreWebView2NewWindowRequestedEventArgs* a) -> HRESULT { a->put_Handled(TRUE); return S_OK; }).Get(), &t);
        // C1.3.1: no camera, microphone, location, clipboard or notification rights, and no downloads
        m_web->add_PermissionRequested(Callback<ICoreWebView2PermissionRequestedEventHandler>(
            [](ICoreWebView2*, ICoreWebView2PermissionRequestedEventArgs* a) -> HRESULT {
                a->put_State(COREWEBVIEW2_PERMISSION_STATE_DENY); return S_OK; }).Get(), &t);
        ComPtr<ICoreWebView2_4> web4;
        if (SUCCEEDED(m_web.As(&web4)) && web4)
            web4->add_DownloadStarting(Callback<ICoreWebView2DownloadStartingEventHandler>(
                [](ICoreWebView2*, ICoreWebView2DownloadStartingEventArgs* a) -> HRESULT { a->put_Cancel(TRUE); return S_OK; }).Get(), &t);
        m_web->add_WebMessageReceived(Callback<ICoreWebView2WebMessageReceivedEventHandler>(
            [this](ICoreWebView2*, ICoreWebView2WebMessageReceivedEventArgs* a) -> HRESULT {
                // Messages count only from our own page (loaded with NavigateToString).
                LPWSTR src = nullptr;
                a->get_Source(&src);
                std::wstring from = src ? src : L"";
                if (src) CoTaskMemFree(src);
                if (from.rfind(L"about:blank", 0) != 0 && from.rfind(L"data:", 0) != 0)
                {
                    FPLogW(L"[Store] page message from %.200s ignored", from.c_str());
                    return S_OK;
                }
                LPWSTR json = nullptr;
                if (SUCCEEDED(a->get_WebMessageAsJson(&json)) && json) { OnPageMessage(json); CoTaskMemFree(json); }
                return S_OK;
            }).Get(), &t);

        Resize();
        m_ctrl->put_IsVisible(TRUE);
        m_ctrl->MoveFocus(COREWEBVIEW2_MOVE_FOCUS_REASON_PROGRAMMATIC);
        std::wstring html = PageHtml();
        if (html.empty()) { PostMessage(WM_FAIL); return S_OK; }
        m_web->NavigateToString(html.c_str());
        return S_OK;
    }

    void Resize()
    {
        if (!m_ctrl) return;
        RECT rc; GetClientRect(&rc);
        m_ctrl->put_Bounds(rc);
    }

    void Send(const std::wstring& json)
    {
        if (m_web) m_web->PostWebMessageAsJson(json.c_str());
    }

    void SendMessageToPage(const std::wstring& title, const std::wstring& text)
    {
        Send(L"{\"type\":\"message\",\"title\":" + Json(title) + L",\"message\":" + Json(text) + L"}");
    }

    // A Power PDF update for this release line (C1.6.0): only when the hint is switched on;
    // the server answers only when its admins switched the update hints on.
    static bool IsTrustedReadme(const std::wstring& u)
    {
        const std::wstring pre = L"https://";
        if (u.size() < 12 || u.size() > 500 || u.compare(0, pre.size(), pre) != 0) return false;
        size_t end = u.find_first_of(L"/?#", pre.size());   // C1.9.2: "?" and "#" end the host too
        std::wstring host = u.substr(pre.size(), end == std::wstring::npos ? std::wstring::npos : end - pre.size());
        if (host.empty() || host.find_first_of(L":%") != std::wstring::npos) return false;
        for (auto& c : host) c = (wchar_t)towlower(c);
        const std::wstring dom = L"tungstenautomation.com";
        for (wchar_t c : u) if (c <= L' ' || c == L'"' || c == L'<' || c == L'>' || c == L'\\' || c == L'@' || c > 0x7E) return false;
        return host == dom || (host.size() > dom.size() && host.compare(host.size() - dom.size() - 1, std::wstring::npos, L"." + dom) == 0);
    }

    void PowerPdfHint()
    {
        if (!PSPowerPdfHint()) return;
        std::wstring host = PSHostVersion();
        if (host.empty() || host.size() > 30) return;
        for (wchar_t c : host) if (!iswdigit(c) && c != L'.') return;
        std::wstring url = PSServerUrl() + L"/api/powerpdf/update?version=" + host + L"&lang=" + HostLang();
        std::wstring hidden = PSPowerPdfHiddenUpdate();
        HWND h = m_hWnd;
        std::wstring installed = host;   // "2025.3.8.0.26414" -> "2025.3.8" (C1.9.0, shown in the updates view)
        for (int dots = 0, i = 0; i < (int)installed.size(); ++i)
            if (installed[i] == L'.' && ++dots == 3) { installed.resize(i); break; }
        Spawn([h, url, hidden, installed]() {
            std::string body;
            DWORD status = 0;
            std::wstring out;
            try
            {
                if (PSHttpGetText(url, body, &status) && status == 200)
                {
                    std::wstring j = W16(body);
                    auto str = [&j](const wchar_t* k, size_t max) { return RawField(j, k) == L"null" ? std::wstring() : Field(j, k, max); };
                    std::wstring latest = str(L"latest", 40);
                    if (RawField(j, L"newer") == L"true" && !latest.empty() && latest != hidden)
                    {
                        std::wstring readme = str(L"readmeUrl", 500);
                        out = L"{\"type\":\"ppupdate\",\"latest\":" + Json(latest) + L",\"title\":" + Json(str(L"title", 200)) +
                              L",\"buildDate\":" + Json(str(L"buildDate", 40)) + L",\"summary\":" + Json(str(L"summary", 1200)) +
                              L",\"readme\":" + Json(IsTrustedReadme(readme) ? readme : std::wstring()) +
                              L",\"installed\":" + Json(installed) + L"}";
                    }
                }
            }
            catch (...) { out.clear(); }
            if (out.empty()) return;
            auto* m = new AsyncMsg;
            m->kind = KSend; m->gen = 0; m->text = out;
            PostAsync(h, m);
        });
    }

    void SendInit()
    {
        struct S { const wchar_t* key; UINT id; };
        static const S strings[] = {
            { L"title", IDS_PSD_TITLE }, { L"search", IDS_PSW_SEARCH }, { L"all", IDS_PSW_ALL }, { L"none", IDS_PSW_NONE },
            { L"empty", IDS_PSD_EMPTY }, { L"stInstalled", IDS_PSD_ST_INSTALLED }, { L"stUpdate", IDS_PSD_ST_UPDATE },
            { L"beta", IDS_PSW_BETA }, { L"install", IDS_PSD_BTN_INSTALL }, { L"update", IDS_PSW_UPDATE },
            { L"remove", IDS_PSW_REMOVE }, { L"cancel", IDS_PSW_CANCEL }, { L"version", IDS_PSW_VERSION },
            { L"colInstalled", IDS_PSD_C_INSTALLED }, { L"whatsNew", IDS_PSW_WHATSNEW }, { L"author", IDS_PSD_C_AUTHOR },
            { L"adminNote", IDS_PSD_ADMIN_NOTE }, { L"confirmInstall", IDS_PSD_CONFIRM }, { L"needsHost", IDS_PSW_NEEDS_HOST }, { L"confirmUninstall", IDS_PSD_CONFIRM_UNINST },
            { L"installing", IDS_PSW_INSTALLING }, { L"removing", IDS_PSW_REMOVING }, { L"later", IDS_PSW_LATER },
            { L"restartNow", IDS_PSW_RESTARTNOW }, { L"disclaimer", IDS_PSD_DISCLAIMER }, { L"disclaimerBtn", IDS_PSD_BTN_DISCLAIMER },
            { L"disclaimerFull", IDS_PSD_DISCLAIMER_FULL }, { L"selfUpdate", IDS_PSD_SELF_UPDATE }, { L"selfUpdateBtn", IDS_PSD_BTN_SELFUPD },
            { L"installLocked", IDS_PSD_POLICY_INSTALL },
            { L"ratings", IDS_PSW_RATINGS }, { L"yourRating", IDS_PSW_YOUR_RATING }, { L"rateHint", IDS_PSW_RATE_HINT },
            { L"rated", IDS_PSW_RATED }, { L"report", IDS_PSW_REPORT }, { L"reportProblem", IDS_PSW_REPORT_PROBLEM },
            { L"reportComment", IDS_PSW_REPORT_COMMENT }, { L"reportText", IDS_PSW_REPORT_TEXT }, { L"reportEmail", IDS_PSW_REPORT_EMAIL },
            { L"reportLog", IDS_PSW_REPORT_LOG }, { L"reportPrivacy", IDS_PSW_REPORT_PRIVACY }, { L"send", IDS_PSW_SEND },
            { L"screenshots", IDS_PSW_SCREENSHOTS }, { L"close", IDS_PSW_CLOSE },
            { L"forCustomer", IDS_PSW_FOR }, { L"needHint", IDS_PSW_NEED_HINT }, { L"needHits", IDS_PSW_NEED_HITS },
            { L"needAll", IDS_PSW_NEED_ALL }, { L"needNone", IDS_PSW_NEED_NONE },
            { L"codeBtn", IDS_PSW_CODE_BTN }, { L"codeIntro", IDS_PSW_CODE_INTRO }, { L"codeApply", IDS_PSW_CODE_APPLY },
            { L"codeRemove", IDS_PSW_CODE_REMOVE }, { L"codeLocked", IDS_PSW_CODE_LOCKED }, { L"codeActive", IDS_PSW_CODE_ACTIVE },
            { L"codeBad", IDS_PSO_CODE_BAD },
            { L"migrateText", IDS_PSW_MIGRATE_TEXT }, { L"migrateBtn", IDS_PSW_MIGRATE_BTN }, { L"migrateDone", IDS_PSW_MIGRATE_DONE },
            { L"blockedText", IDS_PSW_BLOCKED_BANNER }, { L"blockedBtn", IDS_PSW_BLOCKED_BTN },
            { L"attach", IDS_PSW_ATTACH }, { L"attachHint", IDS_PSW_ATTACH_HINT }, { L"attachRefused", IDS_PSW_ATTACH_REFUSED },
            { L"reportStore", IDS_PSW_REPORT_STORE }, { L"noUi", IDS_PSW_NO_UI },
              { L"connects", IDS_PSW_CONNECTS }, { L"connProvider", IDS_PSW_CONN_PROVIDER },
              { L"connNote", IDS_PSW_CONN_NOTE }, { L"connData", IDS_PSW_CONN_DATA }, { L"newsTitle", IDS_PSW_NEWS_TITLE }, { L"newsOk", IDS_PSW_NEWS_OK },
            { L"codeList", IDS_PSW_CODE_LIST }, { L"codeAdd", IDS_PSW_CODE_ADD }, { L"codeNone", IDS_PSW_CODE_NONE },
            { L"codeAsk", IDS_PSW_CODE_ASK }, { L"codeAskAddons", IDS_PSW_CODE_ASK_ADDONS }, { L"codeInvalid", IDS_PSW_CODE_INVALID },
            { L"orphan", IDS_PSW_ORPHAN }, { L"orphanPill", IDS_PSW_ORPHAN_PILL }, { L"seats", IDS_PSW_SEATS },
            { L"secInstalled", IDS_PSW_SEC_INSTALLED }, { L"secUpdates", IDS_PSW_SEC_UPDATES }, { L"secMore", IDS_PSW_SEC_MORE },
            { L"ppUpdate", IDS_PSW_PP_UPDATE }, { L"ppNews", IDS_PSW_PP_NEWS }, { L"ppHide", IDS_PSW_PP_HIDE },
            { L"ppInstallHint", IDS_PSW_PP_INSTALL_HINT },
            { L"updTitle", IDS_PSW_UPD_TITLE }, { L"updAll", IDS_PSW_UPD_ALL }, { L"updNone", IDS_PSW_UPD_NONE },
            { L"updStore", IDS_PSW_UPD_STORE }, { L"updFromTo", IDS_PSW_UPD_FROMTO },
        };
        std::wstring j = L"{\"type\":\"init\",\"version\":" + Json(FP_VERSION_W) +
                         (FPLocIsRtl() ? L",\"dir\":\"rtl\"" : L"") +
                         L",\"installLocked\":" + (PSPolicyNoInstall() ? L"true" : L"false") +
                         (m_preselect == kPSUpdatesView ? L",\"mode\":\"updates\"" : L"") +
                         L",\"code\":" + CodesJson() + L",\"strings\":{";
        for (size_t i = 0; i < _countof(strings); ++i)
            j += (i ? L"," : L"") + Json(strings[i].key) + L":" + Json(FPLoc(strings[i].id));
        Send(j + L"}}");
    }

    // --- network work runs on worker threads; results come back as WM_ASYNC ---
    // Workers never touch the window object or host APIs: they get copies of
    // what they need (entries, language) and post an AsyncMsg back.

    void LoadCatalog(bool withPreselect)
    {
        int gen = ++m_catalogGen;
        std::wstring lang = HostLang();   // host call: UI thread only
        HWND h = m_hWnd;
        // C1.9.3: the window opens with the catalog of the startup check (up to 30 min old) and the
        // fresh one replaces it; a selection waits for the fresh one (flag false).
        if (withPreselect)
        {
            AsyncMsg cached;
            cached.kind = KCatalog; cached.gen = gen; cached.flag = false; cached.cached = true;
            if (PSCachedCatalog(lang, 30 * 60 * 1000, cached.entries) && !cached.entries.empty()) ApplyCatalog(cached);
        }
        Spawn([h, gen, lang, withPreselect]() {
            auto* m = new AsyncMsg;
            m->kind = KCatalog; m->gen = gen; m->flag = withPreselect;
            // C1.9.3: offline, the last catalog stays in the window (with the error above it)
            // a deep link waits for the server's catalog: against the stored one it could say "not found" (C1.9.7)
            try { if (!PSFetchCatalogFor(lang, m->entries, m->error) && PSCachedCatalog(lang, MAXDWORD, m->entries)) { m->cached = true; m->flag = false; } }
            catch (...) { m->entries.clear(); m->error = FPLoc(IDS_PSD_MSG_FAIL); }
            PostAsync(h, m);
        });
    }

    void ApplyCatalog(AsyncMsg& r)
    {
        if (r.gen != m_catalogGen) return;   // a newer load is on its way
        m_entries.clear();
        m_hasSelfUpdate = false;
        for (auto& e : r.entries)
        {
            if (e.id == kClientId)
            {
                m_self = e;
                m_hasSelfUpdate = !PSPolicyNoSelfUpdate() && !PSSelfUpdateBlockedBySerial() && PSCompareVersions(e.version, FP_VERSION_W) > 0;
                continue;
            }
            if (_wcsicmp(e.zxtName.c_str(), L"PluginStore") == 0) continue;   // would replace the store client
            m_entries.push_back(e);
        }
        // Add-ons the store installed here that the catalog no longer offers (C1.4.1: e.g. after
        // their customer code was removed): listed as installed so they can still be removed.
        // Only with a complete catalog: offline, everything would look "no longer offered".
        // (not from the stored catalog: an add-on installed since then would look "no longer offered", C1.9.7)
        if (r.error.empty() && !r.entries.empty() && !r.cached)
            for (const auto& a : PSListInstalledAddons(ManifestLang()))
            {
                bool known = _wcsicmp(a.id.c_str(), kClientId) == 0;
                for (const auto& e : m_entries) if (_wcsicmp(e.id.c_str(), a.id.c_str()) == 0 || _wcsicmp(e.zxtName.c_str(), a.zxtName.c_str()) == 0) known = true;
                if (known) continue;
                PSCatalogEntry o;
                o.id = a.id; o.name = a.name; o.version = a.version; o.installedVersion = a.version.empty() ? L"?" : a.version;
                o.zxtName = a.zxtName; o.orphan = true;
                m_entries.push_back(o);
            }

        std::wstring j = L"{\"type\":\"catalog\",\"error\":" + Json(r.error) + L",\"items\":[";
        for (size_t i = 0; i < m_entries.size(); ++i)
        {
            const PSCatalogEntry& e = m_entries[i];
            wchar_t size[32]; swprintf_s(size, 32, L"%llu", e.sizeBytes);
            j += (i ? L"," : L"") + std::wstring(L"{\"id\":") + Json(e.id) + L",\"name\":" + Json(e.name) +
                 L",\"description\":" + Json(e.description) + L",\"changelog\":" + Json(e.changelog) +
                 L",\"version\":" + Json(e.version) + L",\"installed\":" + Json(e.installedVersion) +
                 L",\"update\":" + (!e.orphan && PSIsUpdate(e.version, e.installedVersion) ? L"true" : L"false") +
                 L",\"channel\":" + Json(e.channel) + L",\"category\":" + Json(e.category) +
                 L",\"categoryName\":" + Json(e.categoryName.empty() ? e.category : e.categoryName) +
                 L",\"size\":" + size + L",\"author\":" + Json(e.author) + L",\"contact\":" + Json(e.contactEmail) +
                 L",\"rating\":" + Tenths(e.rating) + L",\"ratingCount\":" + std::to_wstring(e.ratingCount) +
                 L",\"shots\":" + std::to_wstring(e.screenshots) + L",\"mine\":" + std::to_wstring(PSMyRating(e.id)) +
                 L",\"customer\":" + Json(e.customer) + L",\"connections\":" + Json(e.connections) + (e.noUi ? L",\"noUi\":true" : L"") + (e.orphan ? L",\"orphan\":true" : L"") +
                 // needs a newer Power PDF than this one: shown, but not installable
                 L",\"needsHost\":" + Json(!e.minHost.empty() && !PSHostVersion().empty() &&
                                            PSCompareVersions(PSHostVersion(), e.minHost) < 0 ? e.minHost : std::wstring()) + L"}";
        }
        j += L"]";
        j += MigrateJson();
        j += NewsJson();
        // installed add-ons the store has blocked for a security reason (C1.1.5)
        {
            std::wstring b;
            for (const auto& x : PSBlockedInstalled())
                b += (b.empty() ? L"" : L",") + std::wstring(L"{\"name\":") + Json(x.name) + L",\"version\":" + Json(x.version) +
                     L",\"reason\":" + Json(x.reason) + L"}";
            if (!b.empty()) j += L",\"blocked\":[" + b + L"]";
        }
        if (m_hasSelfUpdate)
            j += L",\"self\":{\"version\":" + Json(m_self.version) + L",\"installed\":" + Json(FP_VERSION_W) +
                 L",\"changelog\":" + Json(m_self.changelog) + L"}";
        if (r.flag && !m_preselect.empty() && m_preselect != kClientId && m_preselect != kPSUpdatesView)
        {
            j += L",\"select\":" + Json(m_preselect) + L",\"selectMissing\":" + Json(Fmt(IDS_PSD_LINK_NOTFOUND, m_preselect));
            m_preselect.clear();
        }
        Send(j + L"}");
        LoadIcons();
    }

    // Add-ons the store installed into ANOTHER Power PDF folder (an older release
    // after an update) that this installation lacks and the catalog offers
    // (C1.1.4). "Later" remembers them per folder and id in HKCU.
    static std::wstring Lower(std::wstring s) { if (!s.empty()) CharLowerBuffW(&s[0], (DWORD)s.size()); return s; }

    std::wstring MigrateDismissed()
    {
        wchar_t buf[8192] = { 0 };
        DWORD cb = sizeof(buf);
        if (RegGetValueW(HKEY_CURRENT_USER, kPSRegKey, L"MigrateDismissed", RRF_RT_REG_SZ, NULL, buf, &cb) != ERROR_SUCCESS) return L"";
        return buf;
    }

    std::wstring MigrateJson()
    {
        m_migrateKeys.clear();
        if (PSPolicyNoInstall()) return L"";
        std::vector<PSOldInstall> olds;
        try { olds = PSFindOldInstalls(); } catch (...) { return L""; }
        const std::wstring dismissed = L";" + MigrateDismissed() + L";";
        std::wstring folder, items;
        for (const auto& o : olds)
        {
            const PSCatalogEntry* e = Find(o.id, nullptr);
            if (!e || !e->installedVersion.empty()) continue;   // unknown here, or installed already
            if (!e->minHost.empty() && !PSHostVersion().empty() && PSCompareVersions(PSHostVersion(), e->minHost) < 0) continue;
            std::wstring key = Lower(o.root) + L"|" + o.id;
            if (dismissed.find(L";" + key + L";") != std::wstring::npos) continue;
            bool dup = false;
            for (auto& k : m_migrateKeys) if (k.substr(k.find(L'|')) == key.substr(key.find(L'|'))) dup = true;
            if (dup) continue;   // the same add-on in two old folders: offer it once
            m_migrateKeys.push_back(key);
            if (folder.empty()) folder = o.folder;
            items += (items.empty() ? L"" : L",") + std::wstring(L"{\"id\":") + Json(o.id) + L",\"name\":" + Json(e->name) + L"}";
        }
        if (items.empty()) return L"";
        FPLogW(L"[Store] offering %u add-on(s) from %s", (unsigned)m_migrateKeys.size(), folder.c_str());
        return L",\"migrate\":{\"folder\":" + Json(folder) + L",\"items\":[" + items + L"]}";
    }

    // "What's new" (C1.2.0): the last version per add-on whose changes the user has seen
    // (HKCU ...\PluginStore\Seen). A first sight records silently: a fresh install or the
    // first start of this client has nothing to tell.
    static std::wstring SeenVersion(const std::wstring& id)
    {
        wchar_t buf[64] = { 0 };
        DWORD cb = sizeof(buf);
        std::wstring key = std::wstring(kPSRegKey) + L"\\Seen";
        if (RegGetValueW(HKEY_CURRENT_USER, key.c_str(), id.c_str(), RRF_RT_REG_SZ, NULL, buf, &cb) != ERROR_SUCCESS) return L"";
        return buf;
    }

    static void SetSeen(const std::wstring& id, const std::wstring& version)
    {
        std::wstring key = std::wstring(kPSRegKey) + L"\\Seen";
        RegSetKeyValueW(HKEY_CURRENT_USER, key.c_str(), id.c_str(), REG_SZ, version.c_str(), (DWORD)((version.size() + 1) * sizeof(wchar_t)));
    }

    std::wstring NewsJson()
    {
        m_news.clear();
        std::wstring items;
        auto add = [&](const std::wstring& id, const std::wstring& name, const std::wstring& installed,
                       const std::wstring& latest, const std::wstring& changelog) {
            if (id.empty() || installed.empty() || id.size() > 200) return;
            std::wstring seen = SeenVersion(id);
            if (seen.empty()) { SetSeen(id, installed); return; }
            if (seen == installed || installed != latest) return;   // nothing new, or the changelog is of another version
            if (changelog.empty()) { SetSeen(id, installed); return; }
            m_news.push_back({ id, installed });
            items += (items.empty() ? L"" : L",") + std::wstring(L"{\"name\":") + Json(name) + L",\"version\":" + Json(installed) +
                     L",\"changelog\":" + Json(changelog) + L"}";
        };
        if (!m_self.id.empty()) add(kClientId, m_self.name, FP_VERSION_W, m_self.version, m_self.changelog);
        for (const auto& e : m_entries) add(e.id, e.name, e.installedVersion, e.version, e.changelog);
        return items.empty() ? L"" : L",\"news\":[" + items + L"]";
    }

    void NewsSeen()
    {
        for (const auto& n : m_news) SetSeen(n.first, n.second);
        m_news.clear();
    }

    void MigrateDismiss()
    {
        std::wstring v = MigrateDismissed();
        for (const auto& k : m_migrateKeys)
            if ((L";" + v + L";").find(L";" + k + L";") == std::wstring::npos) v += (v.empty() ? L"" : L";") + k;
        if (v.size() > 4000) v = v.substr(v.size() - 4000);   // keeps the newest
        RegSetKeyValueW(HKEY_CURRENT_USER, kPSRegKey, L"MigrateDismissed", REG_SZ, v.c_str(), (DWORD)((v.size() + 1) * sizeof(wchar_t)));
        m_migrateKeys.clear();
    }

    std::wstring PackageUrl(const PSCatalogEntry& e, const wchar_t* tail)
    {
        return PSServerUrl() + L"/api/packages/" + e.id + tail;
    }

    // Star rating (1-5) of an installed add-on; one per installation.
    void Rate(size_t i, int stars)
    {
        if (stars < 1 || stars > 5 || i >= m_entries.size()) return;
        const PSCatalogEntry& e = m_entries[i];
        std::string body = "{\"installId\":" + U8(Json(PSInstallId())) + ",\"stars\":" + std::to_string(stars) +
                           ",\"version\":" + U8(Json(e.installedVersion.empty() ? e.version : e.installedVersion)) + "}";
        std::wstring url = PackageUrl(e, L"/rating"), id = e.id;
        HWND h = m_hWnd;
        Spawn([h, url, body, id, stars]() {
            auto* m = new AsyncMsg;
            m->kind = KRated; m->id = id; m->number = stars;
            std::string resp;
            DWORD status = 0;
            m->flag = PSHttpPostJson(url, body, resp, &status);
            if (m->flag)
            {
                PSSetMyRating(id, stars);
                m->rating = JsonNumber(resp, "average");
                m->count = (int)JsonNumber(resp, "count");
            }
            PostAsync(h, m);
        });
    }

    void ApplyRated(AsyncMsg& r)
    {
        if (!r.flag) { SendMessageToPage(FPLoc(IDS_PSD_TITLE), FPLoc(IDS_PSW_RATE_FAIL)); return; }
        size_t i = 0;
        if (Find(r.id, &i)) { m_entries[i].rating = r.rating; m_entries[i].ratingCount = r.count; }
        Send(L"{\"type\":\"rated\",\"id\":" + Json(r.id) + L",\"rating\":" + Tenths(r.rating) +
             L",\"ratingCount\":" + std::to_wstring(r.count) + L",\"mine\":" + std::to_wstring(r.number) + L"}");
    }

    // Problem report or comment to the add-on's developer.
    // Files the page attached to the report being written (C1.2.0): base64, checked again by the server.
    void Attach(const std::wstring& name, const std::wstring& data)
    {
        if (m_attach.size() >= 3 || data.empty() || data.size() > 7 * 1024 * 1024) return;
        for (wchar_t c : data)
            if (!((c >= L'A' && c <= L'Z') || (c >= L'a' && c <= L'z') || (c >= L'0' && c <= L'9') ||
                  c == L'+' || c == L'/' || c == L'=')) return;   // base64 only, ASCII (C1.3.1)
        m_attach.push_back({ name.substr(0, 200), data });
    }

    void SendFeedback(size_t i, std::wstring kind, const std::wstring& message, const std::wstring& email, bool withLog)
    {
        if (i >= m_entries.size()) return;
        SendFeedbackFor(m_entries[i], kind, message, email, withLog);
    }

    void SendFeedbackFor(const PSCatalogEntry& e, std::wstring kind, const std::wstring& message, const std::wstring& email, bool withLog)
    {
        if (kind != L"comment") kind = L"problem";
        std::string files;
        for (const auto& a : m_attach)
        {
            std::string data;   // base64, checked in Attach(): ASCII only
            data.reserve(a.data.size());
            for (wchar_t ch : a.data) data += static_cast<char>(ch);
            files += (files.empty() ? "" : ",") + std::string("{\"name\":") + U8(Json(a.name)) + ",\"data\":\"" + data + "\"}";
        }
        m_attach.clear();
        std::string body = "{\"installId\":" + U8(Json(PSInstallId())) + ",\"kind\":" + U8(Json(kind)) +
                           ",\"message\":" + U8(Json(message)) + ",\"email\":" + U8(Json(email)) +
                           ",\"version\":" + U8(Json(e.id == kClientId ? std::wstring(FP_VERSION_W)
                                                     : e.installedVersion.empty() ? e.version : e.installedVersion)) +
                           ",\"log\":" + U8(Json(withLog ? LogTail(80) : std::wstring())) +
                           (files.empty() ? "" : ",\"attachments\":[" + files + "]") + "}";
        std::wstring url = PackageUrl(e, L"/feedback");
        HWND h = m_hWnd;
        Spawn([h, url, body]() {
            auto* m = new AsyncMsg;
            m->kind = KFeedback;
            std::string resp;
            DWORD status = 0;
            m->flag = PSHttpPostJson(url, body, resp, &status);
            // The server explains a refusal (too short, invalid address, limit) in "message".
            if (!m->flag)
            {
                std::wstring msg = Field(W16(resp), L"message", 600);
                if (msg.size() > 600) msg.resize(600);
                m->text = msg;
            }
            PostAsync(h, m);
        });
    }

    void ApplyFeedback(AsyncMsg& r)
    {
        if (r.flag) SendMessageToPage(FPLoc(IDS_PSW_REPORT), FPLoc(IDS_PSW_REPORT_SENT));
        else SendMessageToPage(FPLoc(IDS_PSW_REPORT), FPLoc(IDS_PSW_REPORT_FAIL) + (r.text.empty() ? L"" : L"\n\n" + r.text));
    }

    // Screenshots of the selected add-on: list (TSV url<TAB>caption), images cached per version.
    void LoadShots(size_t i)
    {
        if (i >= m_entries.size()) return;
        PSCatalogEntry e = m_entries[i];
        std::wstring url = PackageUrl(e, L"/screenshots?format=tsv&lang=") + HostLang();
        HWND h = m_hWnd;
        Spawn([h, e, url]() {
            std::string tsv;
            if (!PSHttpGetText(url, tsv)) return;
            std::wstring dir = LocalDir(L"shots");
            std::wstring items;
            std::wstring text = W16(tsv);
            size_t pos = 0;
            int n = 0;
            while (pos < text.size() && n < 6)
            {
                size_t eol = text.find(L'\n', pos);
                if (eol == std::wstring::npos) eol = text.size();
                std::wstring line = text.substr(pos, eol - pos);
                pos = eol + 1;
                size_t tab = line.find(L'\t');
                if (tab == std::wstring::npos) continue;
                std::wstring shotUrl = line.substr(0, tab), caption = line.substr(tab + 1);
                std::wstring name = e.id + L"-" + e.version + L"-" + std::to_wstring(n) + L".img";
                ++n;
                if (dir.empty() || !SafeFileName(name)) continue;
                std::wstring path = dir + L"\\" + name;
                if (GetFileAttributesW(path.c_str()) == INVALID_FILE_ATTRIBUTES)
                {
                    DWORD status = 0;
                    if (!PSHttpGetFile(shotUrl, path, &status, 3 * 1024 * 1024)) continue;
                }
                std::vector<BYTE> data = ReadSmallFile(path, 3 * 1024 * 1024);
                const wchar_t* type = data.size() > 4 && data[0] == 0x89 && data[1] == 'P' ? L"image/png"
                                    : data.size() > 4 && data[0] == 0xFF && data[1] == 0xD8 ? L"image/jpeg" : nullptr;
                if (!type) continue;
                items += (items.empty() ? L"" : L",") + std::wstring(L"{\"src\":") + Json(std::wstring(L"data:") + type + L";base64," + Base64(data)) +
                         L",\"caption\":" + Json(caption) + L"}";
            }
            auto* m = new AsyncMsg;
            m->kind = KSend;
            m->text = L"{\"type\":\"shots\",\"id\":" + Json(e.id) + L",\"items\":[" + items + L"]}";
            PostAsync(h, m);
        });
    }

    // Search by need on the server (AI ranking with reasons when the store has
    // it switched on, else its word search). Only ids of listed add-ons count.
    // Customer code entered in the store window (C0.8.0): checked with the server
    // BEFORE it is stored; a valid one is stored and the catalog reloaded, so the
    // delivered add-ons appear in their own section.
    void CheckCustomerCode(const std::wstring& raw)
    {
        std::wstring code;
        for (wchar_t c : raw) if (c != L' ' && c != L'\t') code += (wchar_t)towupper(c);
        if (code.empty() || !PSIsValidCustomerCode(code))
        {
            Send(L"{\"type\":\"code\",\"ok\":false,\"message\":" + Json(FPLoc(IDS_PSO_CODE_BAD)) + L"}");
            return;
        }
        std::wstring url = PSServerUrl() + L"/api/customer-code";
        HWND h = m_hWnd;
        Spawn([h, url, code]() {
            auto* m = new AsyncMsg;
            m->kind = KCustomer; m->id = code;
            std::string body;
            DWORD status = 0;
            try
            {
                if (PSHttpCheckCustomerCode(url, code, body, &status))
                {
                    std::wstring j = W16(body);
                    m->flag = RawField(j, L"valid") == L"true";
                    m->text = Field(j, L"customer", 200);
                    m->count = _wtoi(RawField(j, L"addons").c_str());
                    m->number = 1;   // answered
                }
            }
            catch (...) { m->number = 0; }
            PostAsync(h, m);
        });
    }

    void ApplyCustomer(AsyncMsg& r)
    {
        if (r.number == 0)
        {
            Send(L"{\"type\":\"code\",\"ok\":false,\"message\":" + Json(FPLoc(IDS_PSD_MSG_FAIL)) + L"}");
            return;
        }
        if (!r.flag)
        {
            Send(L"{\"type\":\"code\",\"ok\":false,\"message\":" + Json(FPLoc(IDS_PSW_CODE_INVALID)) + L"}");
            return;
        }
        int added = PSAddCustomerCode(r.id, r.text);   // C1.4.1: one more code, the others stay
        if (added != 0)
        {
            Send(L"{\"type\":\"code\",\"ok\":false,\"list\":" + CodesJson() + L",\"message\":" +
                 Json(FPLoc(added == 1 ? IDS_PSW_CODE_DUP : IDS_PSW_CODE_FULL)) + L"}");
            return;
        }
        std::wstring msg = r.count > 0 ? Fmt(IDS_PSW_CODE_VALID, r.text, std::to_wstring(r.count))
                                       : Fmt(IDS_PSW_CODE_VALID_NONE, r.text);
        Send(L"{\"type\":\"code\",\"ok\":true,\"list\":" + CodesJson() + L",\"customer\":" + Json(r.text) + L",\"message\":" + Json(msg) + L"}");
        LoadCatalog(false);
        CodeInfo();
    }

    // {"has","locked","list":[{"code","name"}]} for the page (C1.4.1)
    static std::wstring CodesJson()
    {
        std::wstring l;
        for (const auto& c : PSCustomerCodes())
            l += (l.empty() ? L"" : L",") + std::wstring(L"{\"code\":") + Json(c) + L",\"name\":" + Json(PSCustomerCodeName(c)) + L"}";
        return std::wstring(L"{\"has\":") + (l.empty() ? L"false" : L"true") +
               L",\"locked\":" + (PSCustomerCodeLocked() ? L"true" : L"false") + L",\"list\":[" + l + L"]}";
    }

    // Asks the store about every stored code: valid?, customer, which add-ons (C1.4.1).
    void CodeInfo()
    {
        std::vector<std::wstring> codes = PSCustomerCodes();
        if (codes.empty()) return;
        std::wstring url = PSServerUrl() + L"/api/customer-code";
        HWND h = m_hWnd;
        Spawn([h, url, codes]() {
            auto* m = new AsyncMsg;
            m->kind = KCodeInfo;
            std::wstring items;
            for (const auto& c : codes)
            {
                std::string body;
                DWORD status = 0;
                bool answered = false, valid = false;
                std::wstring customer, pk, seats;
                try
                {
                    if (PSHttpCheckCustomerCode(url, c, body, &status))
                    {
                        std::wstring j = W16(body);
                        answered = true;
                        valid = RawField(j, L"valid") == L"true";
                        customer = Field(j, L"customer", 200);
                        for (const auto& id : JsonStringArray(j, L"packages")) pk += (pk.empty() ? L"" : L",") + Json(id);
                        // installations per add-on (server S1.4.2): {"package","used","max"}, max null = unlimited
                        for (const auto& o : JsonObjectArray(j, L"installs"))
                        {
                            std::wstring max = RawField(o, L"max");
                            seats += (seats.empty() ? L"" : L",") + std::wstring(L"{\"package\":") + Json(Field(o, L"package", 200)) +
                                     L",\"used\":" + std::to_wstring(_wtoi(RawField(o, L"used").c_str())) +
                                     L",\"max\":" + (max.empty() || max == L"null" ? std::wstring(L"null") : std::to_wstring(_wtoi(max.c_str()))) + L"}";
                        }
                    }
                }
                catch (...) {}
                items += (items.empty() ? L"" : L",") + std::wstring(L"{\"code\":") + Json(c) +
                         L",\"answered\":" + (answered ? L"true" : L"false") + L",\"valid\":" + (valid ? L"true" : L"false") +
                         L",\"customer\":" + Json(customer) + L",\"packages\":[" + pk + L"],\"installs\":[" + seats + L"]}";
            }
            m->text = L"{\"type\":\"codeInfo\",\"items\":[" + items + L"]}";
            PostAsync(h, m);
        });
    }

    // Before a code is removed: which installed add-ons would no longer be offered?
    // The catalog is fetched with the remaining codes; an installed add-on missing
    // from it came with this code only (C1.4.1).
    void CodePreview(const std::wstring& code)
    {
        std::wstring rest;
        for (const auto& c : PSCustomerCodes()) if (c != code) rest += (rest.empty() ? L"" : L";") + c;
        std::vector<PSCatalogEntry> installed;
        for (const auto& e : m_entries) if (!e.installedVersion.empty() && !e.orphan) installed.push_back(e);
        std::wstring lang = HostLang();
        HWND h = m_hWnd;
        Spawn([h, code, rest, installed, lang]() {
            auto* m = new AsyncMsg;
            m->kind = KCodePreview; m->id = code;
            std::vector<PSCatalogEntry> after;
            std::wstring err;
            try { m->flag = PSFetchCatalogFor(lang, after, err, &rest); }
            catch (...) { m->flag = false; }
            if (m->flag)
                for (const auto& e : installed)
                    if (std::none_of(after.begin(), after.end(), [&](const PSCatalogEntry& a) { return _wcsicmp(a.id.c_str(), e.id.c_str()) == 0; }))
                        m->entries.push_back(e);
            PostAsync(h, m);
        });
    }

    void ApplyCodePreview(AsyncMsg& r)
    {
        if (!r.flag)
        {
            Send(L"{\"type\":\"code\",\"ok\":false,\"list\":" + CodesJson() + L",\"message\":" + Json(FPLoc(IDS_PSD_MSG_FAIL)) + L"}");
            return;
        }
        m_pendingCode = r.id;
        m_pendingRemoval = r.entries;
        std::wstring names;
        for (const auto& e : r.entries) names += (names.empty() ? L"" : L",") + Json(e.name);
        Send(L"{\"type\":\"codeAsk\",\"code\":" + Json(r.id) + L",\"customer\":" + Json(PSCustomerCodeName(r.id)) +
             L",\"affected\":[" + names + L"]}");
    }

    // The confirmed removal: first the add-ons (one administrator confirmation), then the code.
    void CodeRemove(const std::wstring& code)
    {
        if (code.empty() || code != m_pendingCode) return;   // only after the preview of this code
        std::vector<PSCatalogEntry> remove = m_pendingRemoval;
        m_pendingCode.clear();
        m_pendingRemoval.clear();
        if (remove.empty() || PSPolicyNoInstall())
        {
            PSRemoveCustomerCode(code);
            Send(L"{\"type\":\"code\",\"ok\":true,\"list\":" + CodesJson() + L",\"message\":" + Json(FPLoc(IDS_PSW_CODE_REMOVED)) + L"}");
            LoadCatalog(false);
            return;
        }
        m_jobRunning = true;
        HWND h = m_hWnd;
        Spawn([h, code, remove]() {
            auto* m = new AsyncMsg;
            m->kind = KJob; m->number = JobCodeRemove; m->id = code; m->entries = remove;
            m->count = 4;
            std::vector<std::wstring> zxts;
            for (const auto& e : remove) zxts.push_back(e.zxtName);
            try
            {
                std::vector<bool> gone;
                m->count = PSUninstallPackages(zxts, h, &gone);
                // while the code is still stored: free the installations of what was removed (all, or some)
                std::vector<std::wstring> ids;
                for (size_t i = 0; i < remove.size() && i < gone.size(); ++i) if (gone[i]) ids.push_back(remove[i].id);
                PSReleaseInstallations(ids);
            }
            catch (...) { FPLogW(L"[Store] removing the add-ons of a code failed"); }
            PostAsync(h, m);
        });
    }

    void NeedSearch(const std::wstring& q)
    {
        if (q.size() < 2 || q.size() > 300) return;
        std::wstring url = PSServerUrl() + L"/api/search?format=tsv&q=" + UrlEncode(q) + L"&lang=" + UrlEncode(HostLang());
        if (PSBetaChannel()) url += L"&channel=beta";
        std::vector<std::wstring> known;
        for (const auto& e : m_entries) known.push_back(e.id);
        HWND h = m_hWnd;
        Spawn([h, url, q, known]() {
            std::string tsv;
            DWORD status = 0;
            std::wstring hits;
            bool ai = false;
            if (PSHttpGetText(url, tsv, &status))
            {
                std::wstring text = W16(tsv);
                size_t pos = 0;
                int n = 0;
                while (pos < text.size() && n < 20)
                {
                    size_t eol = text.find(L'\n', pos);
                    if (eol == std::wstring::npos) eol = text.size();
                    std::wstring line = text.substr(pos, eol - pos);
                    pos = eol + 1;
                    while (!line.empty() && line.back() == L'\r') line.pop_back();
                    if (line == L"#ai") { ai = true; continue; }
                    if (line.empty() || line[0] == L'#') continue;
                    size_t tab = line.find(L'\t');
                    std::wstring id = line.substr(0, tab), reason = tab == std::wstring::npos ? L"" : line.substr(tab + 1, 400);
                    if (std::find(known.begin(), known.end(), id) == known.end()) continue;
                    hits += (n++ ? L"," : L"") + std::wstring(L"{\"id\":") + Json(id) + L",\"reason\":" + Json(reason) + L"}";
                }
            }
            auto* m = new AsyncMsg;
            m->kind = KSend;
            m->text = L"{\"type\":\"need\",\"q\":" + Json(q) + L",\"ai\":" + (ai ? L"true" : L"false") + L",\"hits\":[" + hits + L"]}";
            PostAsync(h, m);
        });
    }

    // Icons, cached per version: one worker fetches them in turn and hands
    // each one over as soon as it is there.
    void LoadIcons()
    {
        struct Want { std::wstring id, version, url; };
        std::vector<Want> want;
        for (const auto& e : m_entries) want.push_back({ e.id, e.version, e.iconUrl });
        int gen = m_catalogGen;
        int iconGen = ++g_iconGen;
        HWND h = m_hWnd;
        Spawn([h, want, gen, iconGen]() {
            std::wstring dir = LocalDir(L"icons");
            for (const auto& w : want)
            {
                if (iconGen != g_iconGen.load()) return;   // a newer load (or the window closed)
                std::wstring name = w.id + L"-" + w.version + L".png";
                if (dir.empty() || !SafeFileName(name) || w.url.empty()) continue;
                std::wstring path = dir + L"\\" + name;
                std::wstring none = path + L".none";   // the server has no icon for this version
                DWORD status = 0;
                if (GetFileAttributesW(path.c_str()) == INVALID_FILE_ATTRIBUTES &&
                    GetFileAttributesW(none.c_str()) == INVALID_FILE_ATTRIBUTES)
                {
                    PSHttpGetFile(w.url, path, &status, 4 * 1024 * 1024);
                    if (status == 404)
                    {
                        HANDLE f = CreateFileW(none.c_str(), GENERIC_WRITE, 0, NULL, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, NULL);
                        if (f != INVALID_HANDLE_VALUE) CloseHandle(f);
                    }
                }
                std::vector<BYTE> data = ReadSmallFile(path, 4 * 1024 * 1024);
                if (data.size() < 8 || data[0] != 0x89 || data[1] != 'P' || data[2] != 'N' || data[3] != 'G') continue;
                auto* m = new AsyncMsg;
                m->kind = KSend; m->gen = gen;
                m->text = L"{\"type\":\"icon\",\"id\":" + Json(w.id) + L",\"data\":" + Json(L"data:image/png;base64," + Base64(data)) + L"}";
                if (!PostAsync(h, m)) return;   // window gone
            }
        });
    }

    const PSCatalogEntry* Find(const std::wstring& id, size_t* index)
    {
        for (size_t i = 0; i < m_entries.size(); ++i)
            if (m_entries[i].id == id) { if (index) *index = i; return &m_entries[i]; }
        return nullptr;
    }

    void OnPageMessage(const std::wstring& json)
    {
        std::wstring cmd = Field(json, L"cmd");
        if (cmd == L"ready") { SendInit(); LoadCatalog(true); PowerPdfHint(); }
        else if (cmd == L"ppHide")
        {
            std::wstring v = Field(json, L"version", 40);
            bool ok = !v.empty();
            for (wchar_t c : v) if (!iswdigit(c) && c != L'.') ok = false;
            if (ok) PSSetPowerPdfHiddenUpdate(v);
        }
        else if (cmd == L"ppReadme")
        {
            // only the official documentation, over HTTPS (C1.6.0)
            std::wstring u = Field(json, L"url", 500);
            if (IsTrustedReadme(u)) ShellExecuteW(m_hWnd, L"open", u.c_str(), NULL, NULL, SW_SHOWNORMAL);
        }
        else if (cmd == L"refresh") LoadCatalog(false);
        else if (cmd == L"migrateDismiss") MigrateDismiss();
        else if (cmd == L"removeBlocked" && !m_jobRunning) { PSOfferBlockedRemoval(m_hWnd, true); LoadCatalog(false); }
        else if (cmd == L"install" || cmd == L"uninstall")
        {
            // The page waits behind a progress dialog until a "result" arrives: every
            // path answers with one.
            size_t idx = 0;
            const PSCatalogEntry* e = Find(Field(json, L"id"), &idx);
            std::wstring refusal = PSPolicyNoInstall() ? FPLoc(IDS_PSD_POLICY_INSTALL)
                                 : m_jobRunning ? FPLoc(IDS_PSD_MSG_FAIL)
                                 : !e ? FPLoc(IDS_PSD_EMPTY) : std::wstring();
            if (refusal.empty() && cmd == L"install" && !e->minHost.empty() && !PSHostVersion().empty() &&
                PSCompareVersions(PSHostVersion(), e->minHost) < 0)
                refusal = Fmt(IDS_PSW_NEEDS_HOST, e->minHost);   // needs a newer Power PDF
            if (refusal.empty()) RunJob(cmd == L"install" ? JobInstall : JobUninstall, m_entries[idx]);
            else Send(L"{\"type\":\"result\",\"ok\":false,\"title\":" + Json(e ? e->name : std::wstring()) + L",\"message\":" + Json(refusal) + L"}");
        }
        else if (cmd == L"selfUpdate" && (!m_hasSelfUpdate || m_jobRunning))
        {
            Send(L"{\"type\":\"result\",\"ok\":false}");   // nothing to do now: close the progress dialog (C1.9.2)
        }
        else if (cmd == L"selfUpdate" && m_hasSelfUpdate && !m_jobRunning)
        {
            // Download on a worker; the question and the helper start come back to this thread.
            m_jobRunning = true;
            HWND h = m_hWnd;
            PSCatalogEntry e = m_self;
            Spawn([h, e]() {
                auto* m = new AsyncMsg;
                m->kind = KJob; m->number = JobSelfUpdate; m->entries.push_back(e);
                m->count = 1;
                try { m->count = PSSelfUpdateDownload(e, m->text); }
                catch (...) { FPLogW(L"[Store] self-update download failed"); }
                PostAsync(h, m);
            });
        }
        else if (cmd == L"codeCheck" && !PSCustomerCodeLocked()) CheckCustomerCode(Field(json, L"code", 80));
        else if (cmd == L"codeInfo") CodeInfo();
        else if (cmd == L"codePreview" && !PSCustomerCodeLocked() && !m_jobRunning) CodePreview(Field(json, L"code", 80));
        else if (cmd == L"codeRemove" && !PSCustomerCodeLocked() && !m_jobRunning) CodeRemove(Field(json, L"code", 80));
        else if (cmd == L"rate")
        {
            size_t idx = 0;
            if (Find(Field(json, L"id"), &idx)) Rate(idx, _wtoi(Field(json, L"stars").c_str()));
        }
        else if (cmd == L"attachClear") m_attach.clear();
        else if (cmd == L"attach") Attach(Field(json, L"name", 200), Field(json, L"data", 7 * 1024 * 1024));
        else if (cmd == L"newsSeen") NewsSeen();
        else if (cmd == L"feedback")
        {
            size_t idx = 0;
            std::wstring id = Field(json, L"id");
            if (id == kClientId && !m_self.id.empty())
                SendFeedbackFor(m_self, Field(json, L"kind"), Field(json, L"message", 4000), Field(json, L"email", 200), Field(json, L"log") == L"1");
            else if (Find(id, &idx))
                SendFeedback(idx, Field(json, L"kind"), Field(json, L"message", 4000), Field(json, L"email", 200), Field(json, L"log") == L"1");
        }
        else if (cmd == L"need") NeedSearch(Field(json, L"q", 300));
        else if (cmd == L"shots")
        {
            size_t idx = 0;
            if (Find(Field(json, L"id"), &idx)) LoadShots(idx);
        }
        else if (cmd == L"restart")
        {
            HWND mainWnd = ::GetAncestor(m_hWnd, GA_ROOTOWNER);
            if (!PSScheduleRestart())
            {
                FPMessageBox(m_hWnd, FPLoc(IDS_PSD_RESTART_FAIL).c_str(), FPLoc(IDS_PSD_TITLE).c_str(), MB_OK | MB_ICONINFORMATION);
                return;
            }
            EndDialog(IDOK);
            if (mainWnd && mainWnd != m_hWnd) ::PostMessageW(mainWnd, WM_CLOSE, 0, 0);
        }
        else if (cmd == L"mail")
        {
            std::wstring to = Field(json, L"to");
            bool ok = to.find(L'@') != std::wstring::npos && to.size() < 200;
            // one plain address: no header fields, encoded characters or further recipients (C1.3.1: also % , ; : \\)
            for (wchar_t c : to) if (c <= L' ' || c == L'"' || c == L'<' || c == L'>' || c == L'&' || c == L'?' ||
                                     c == L'%' || c == L',' || c == L';' || c == L':' || c == L'\\' || c > 0x7E) ok = false;
            if (ok) ShellExecuteW(m_hWnd, L"open", (L"mailto:" + to).c_str(), NULL, NULL, SW_SHOWNORMAL);
        }
        else if (cmd == L"site")
        {
            // C1.9.9: a provider's website from "connects to": only an https address that the server's
            // catalog names for an add-on, never what the page makes up
            std::wstring u = Field(json, L"url", 300);
            bool ok = u.size() > 12 && u.compare(0, 8, L"https://") == 0;
            for (wchar_t c : u) if (c <= L' ' || c == L'"' || c == L'<' || c == L'>' || c == L'\\' || c == L'@' || c > 0x7E) ok = false;
            bool listed = false;
            if (ok)
            {
                const std::wstring needle = L"\"w\":\"" + u + L"\"";
                for (const auto& e : m_entries) if (e.connections.find(needle) != std::wstring::npos) { listed = true; break; }
            }
            if (ok && listed) ShellExecuteW(m_hWnd, L"open", u.c_str(), NULL, NULL, SW_SHOWNORMAL);
        }
    }

    // Install / remove on a worker: download, checks and the elevated step
    // can take minutes; the window keeps painting its progress meanwhile.
    void RunJob(int job, const PSCatalogEntry& e)
    {
        m_jobRunning = true;
        HWND h = m_hWnd;
        Spawn([h, job, e]() {
            auto* m = new AsyncMsg;
            m->kind = KJob; m->number = job; m->entries.push_back(e);
            m->count = 4;   // reported as a failure if the job throws
            try
            {
                m->count = job == JobInstall ? PSInstallPackage(e, h) : PSUninstallPackage(e.zxtName, h);
                // a delivered add-on removed: its installation is free again (C1.4.1)
                if (job == JobUninstall && m->count == 0 && !e.customer.empty()) PSReleaseInstallations({ e.id });
            }
            catch (...) { FPLogW(L"[Store] install job failed"); }
            PostAsync(h, m);
        });
    }

    void ApplyJob(AsyncMsg& r)
    {
        m_jobRunning = false;
        if (r.number == JobSelfUpdate)
        {
            int rc = r.count == 0 ? PSSelfUpdateFinish(r.entries.front(), r.text, m_hWnd) : r.count;
            FinishSelfUpdate(rc);
            return;
        }
        if (r.number == JobCodeRemove)
        {
            std::wstring names;
            for (const auto& x : r.entries) names += (names.empty() ? L"" : L", ") + x.name;
            if (r.count == 0)
            {
                PSRemoveCustomerCode(r.id);
                LoadCatalog(false);
                Send(L"{\"type\":\"code\",\"ok\":true,\"list\":" + CodesJson() + L"}");
                Send(L"{\"type\":\"result\",\"ok\":true,\"restart\":true,\"title\":" + Json(FPLoc(IDS_PSW_CODE_BTN)) +
                     L",\"message\":" + Json(Fmt(IDS_PSW_CODE_REMOVED_ADDONS, names)) + L"}");
            }
            else
            {
                LoadCatalog(false);   // some may be gone (C1.4.2)
                Send(L"{\"type\":\"result\",\"ok\":false,\"title\":" + Json(FPLoc(IDS_PSW_CODE_BTN)) +
                     L",\"message\":" + Json(FPLoc(IDS_PSW_CODE_KEPT)) + L"}");
            }
            FPLogW(L"[Store] code removal with %u add-on(s) -> %d", (unsigned)r.entries.size(), r.count);
            return;
        }
        const PSCatalogEntry& e = r.entries.front();
        int rc = r.count;
        if (rc == 0)
        {
            if (r.number == JobInstall) SetSeen(e.id, e.version);
            LoadCatalog(false);
            Send(L"{\"type\":\"result\",\"ok\":true,\"restart\":true,\"title\":" + Json(e.name) + L",\"message\":" +
                 Json(Fmt(r.number == JobInstall ? IDS_PSD_ASK_RESTART : IDS_PSD_ASK_RESTART_UN, e.name)) + L"}");
        }
        else
        {
            std::wstring msg = rc == 2 ? FPLoc(IDS_PSD_MSG_HASH) : rc == 6 ? FPLoc(IDS_PSD_MSG_SIG) : rc == 13 ? FPLoc(IDS_PSD_MSG_SEATS)
                             : FmtInt(IDS_PSD_MSG_INSTFAIL, rc);
            Send(L"{\"type\":\"result\",\"ok\":false,\"title\":" + Json(e.name) + L",\"message\":" + Json(msg) + L"}");
        }
    }

    LRESULT OnAsync(WPARAM, LPARAM lp)
    {
        std::unique_ptr<AsyncMsg> r(reinterpret_cast<AsyncMsg*>(lp));
        if (!r || !m_web) return 0;
        switch (r->kind)
        {
        case KCatalog:  ApplyCatalog(*r); break;
        case KRated:    ApplyRated(*r); break;
        case KFeedback: ApplyFeedback(*r); break;
        case KJob:      ApplyJob(*r); break;
        case KCustomer: ApplyCustomer(*r); break;
        case KCodeInfo: Send(r->text); break;
        case KCodePreview: ApplyCodePreview(*r); break;
        case KSend:     if (r->gen == 0 || r->gen == m_catalogGen) Send(r->text); break;
        }
        return 0;
    }

    // End of the store client's own update (UI thread): rc of PSSelfUpdateFinish
    // or of the download. 0 closes Power PDF so the helper can install.
    void FinishSelfUpdate(int rc)
    {
        if (rc == 0)
        {
            // The helper installs once Power PDF is gone: close it now.
            HWND mainWnd = ::GetAncestor(m_hWnd, GA_ROOTOWNER);
            EndDialog(IDOK);
            if (mainWnd && mainWnd != m_hWnd) ::PostMessageW(mainWnd, WM_CLOSE, 0, 0);
        }
        else if (rc == 5)
            SendMessageToPage(L"", L"");
        else
            SendMessageToPage(FPLoc(IDS_PSD_TITLE), rc == 2 ? FPLoc(IDS_PSD_MSG_HASH) : rc == 6 ? FPLoc(IDS_PSD_MSG_SIG) : FmtInt(IDS_PSD_MSG_INSTFAIL, rc));
    }

    LRESULT OnFail(WPARAM, LPARAM) { EndDialog(IDABORT); return 0; }

    afx_msg void OnSize(UINT type, int cx, int cy) { CDialog::OnSize(type, cx, cy); Resize(); }
    afx_msg void OnMove(int x, int y) { CDialog::OnMove(x, y); if (m_ctrl) m_ctrl->NotifyParentWindowPositionChanged(); }
    afx_msg void OnGetMinMaxInfo(MINMAXINFO* mmi)
    {
        UINT dpi = m_hWnd ? DpiOf(m_hWnd) : 96;
        mmi->ptMinTrackSize.x = MulDiv(760, dpi, 96);
        mmi->ptMinTrackSize.y = MulDiv(480, dpi, 96);
    }
    afx_msg void OnDestroy()
    {
        // No result can arrive any more; free the ones still queued.
        AcquireSRWLockExclusive(&g_asyncLock);
        g_asyncTarget = NULL;
        ReleaseSRWLockExclusive(&g_asyncLock);
        ++g_iconGen;
        MSG msg;
        while (::PeekMessageW(&msg, m_hWnd, WM_ASYNC, WM_ASYNC, PM_REMOVE))
            delete reinterpret_cast<AsyncMsg*>(msg.lParam);
        *m_alive = false;
        if (m_ctrl) { m_ctrl->Close(); m_ctrl.Reset(); }
        m_web.Reset(); m_env.Reset();
        CDialog::OnDestroy();
    }
    void OnOK() override {}   // Enter belongs to the page
    // While an install or removal runs, the window stays open: its result
    // (restart prompt, error) must reach the user, and a second run of the
    // same package must not start on top of it.
    void OnCancel() override { if (!m_jobRunning) CDialog::OnCancel(); }

    DECLARE_MESSAGE_MAP()
};

BEGIN_MESSAGE_MAP(CWebStore, CDialog)
    ON_WM_SIZE()
    ON_WM_MOVE()
    ON_WM_GETMINMAXINFO()
    ON_WM_DESTROY()
    ON_MESSAGE(WM_ASYNC, &CWebStore::OnAsync)
    ON_MESSAGE(CWebStore::WM_FAIL, &CWebStore::OnFail)
END_MESSAGE_MAP()

} // namespace webui

bool PSWebUiAvailable()
{
    if (PSUseClassicUI()) return false;
    LPWSTR ver = nullptr;
    HRESULT hr = GetAvailableCoreWebView2BrowserVersionString(nullptr, &ver);
    bool ok = SUCCEEDED(hr) && ver && *ver;
    if (ver) CoTaskMemFree(ver);
    if (!ok) FPLogW(L"[Store] WebView2 runtime not available (0x%08x), using the classic dialog", (unsigned)hr);
    return ok;
}

INT_PTR PSShowWebStore(const std::wstring& preselectId)
{
    AFX_MANAGE_MODULE_STATE;
    webui::CWebStore dlg(preselectId);
    return dlg.DoModal();
}
