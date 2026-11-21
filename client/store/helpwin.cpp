// helpwin.cpp - see helpwin.h. The central help: HFT server, window, page bridge (C2.1.0).
//
// The page (client/ui/help.html, RCDATA) shows what this file sends: the CLEANED help of every
// installed add-on (helpcore.h allow list; the page cleans it a second time and builds the DOM itself).
// The page can ask for exactly two things: open an https:// address that one of the helps contains
// (in the default browser, never in the window), and nothing else; it never names a file.

#include "stdafx.h"
#include "helpwin.h"
#include "helpcore.h"
#include "addonhelp.h"
#include "loc.h"
#include "logging.h"
#include "version.h"
#include "powerpdfpath.h"
#include "Resource.h"
#include <wrl.h>
#include <wrl/event.h>
#include <memory>
#include <shlobj.h>
#include <set>
#include "WebView2.h"

extern "C" HINSTANCE gHINSTANCE;

using Microsoft::WRL::Callback;
using Microsoft::WRL::ComPtr;

int ShowHelpImpl(const std::wstring& addonId, const std::wstring& topic, bool fromHft);

namespace {

const wchar_t kWndClass[] = L"TungstenAddonStoreHelpWindow";
const UINT WM_HELP_FAIL = WM_APP + 71;

HFTServer g_server = NULL;
HFT g_hftV1 = NULL;

struct HelpWindow
{
    HWND hwnd = NULL;
    ComPtr<ICoreWebView2Environment> env;
    ComPtr<ICoreWebView2Controller> ctrl;
    ComPtr<ICoreWebView2> web;
    bool ready = false;               // the page said "ready"
    bool docsSent = false;
    std::wstring wantAddon, wantId;   // where to go once the page is ready
    std::set<std::string> links;      // the https:// addresses the helps contain
    std::shared_ptr<bool> alive = std::make_shared<bool>(true);
    bool ownUi = false;               // opened by the ribbon or the store's own windows (may show a message box)
};
HelpWindow* g_win = nullptr;
bool g_pendingAgain = false;          // a re-entrant ShowHelp arrived while the window was being made

std::wstring Json(const std::wstring& s)
{
    std::wstring o = L"\"";
    o.reserve(s.size() + 16);
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

std::wstring UiLang()
{
    LANGID l = FPLocLangId();
    wchar_t name[LOCALE_NAME_MAX_LENGTH] = {};
    if (!LCIDToLocaleName(MAKELCID(l, SORT_DEFAULT), name, LOCALE_NAME_MAX_LENGTH, 0)) return L"en";
    return name;   // "de-DE", "zh-CN" ... (the help files use the first two letters)
}

std::wstring PluginsDir()
{
    std::wstring bin = cspath::FindBin((HMODULE)gHINSTANCE);
    return bin.empty() ? std::wstring() : bin + L"\\Plug-Ins";
}

std::vector<pshelpcore::HelpSource> Sources()
{
    try { return pshelpcore::FindHelps(PluginsDir(), UiLang(), PS_CLIENT_ID, FPLoc(IDS_PSD_TITLE)); }
    catch (...) { FPLogW(L"[Help] reading the installed helps failed"); return {}; }
}

std::wstring PageHtml()
{
    HRSRC r = FindResourceW(gHINSTANCE, MAKEINTRESOURCEW(IDR_HELP_HTML), RT_RCDATA);
    if (!r) return std::wstring();
    HGLOBAL g = LoadResource(gHINSTANCE, r);
    const char* p = g ? (const char*)LockResource(g) : nullptr;
    DWORD n = SizeofResource(gHINSTANCE, r);
    if (!p || !n) return std::wstring();
    return pshelpcore::W(std::string(p, n));
}

void Send(const std::wstring& json)
{
    if (g_win && g_win->web) g_win->web->PostWebMessageAsJson(json.c_str());
}

void SendInit()
{
    static const struct { const wchar_t* key; UINT id; } strings[] = {
        { L"title", IDS_PSH_TITLE }, { L"search", IDS_PSH_SEARCH }, { L"hits", IDS_PSH_HITS },
        { L"noHits", IDS_PSH_NOHITS }, { L"empty", IDS_PSH_EMPTY }, { L"contents", IDS_PSH_CONTENTS },
        { L"print", IDS_PSH_PRINT }, { L"link", IDS_PSH_LINK }, { L"hint", IDS_PSH_HINT },
        { L"prev", IDS_PSH_PREV }, { L"next", IDS_PSH_NEXT }, { L"help", IDS_PSW_HELP }, { L"clear", IDS_PSH_CLEAR },
    };
    std::wstring j = L"{\"type\":\"init\",\"version\":" + Json(FP_VERSION_W) + L",\"lang\":" + Json(UiLang()) +
                     (FPLocIsRtl() ? L",\"dir\":\"rtl\"" : L"") + L",\"self\":" + Json(PS_CLIENT_ID) + L",\"strings\":{";
    for (size_t i = 0; i < _countof(strings); ++i)
        j += (i ? L"," : L"") + Json(strings[i].key) + L":" + Json(FPLoc(strings[i].id));
    Send(j + L"}}");
}

// Every installed help, cleaned, in the UI language (else English).
void SendDocs()
{
    if (!g_win) return;
    std::vector<pshelpcore::HelpSource> src = Sources();
    std::wstring j = L"{\"type\":\"docs\",\"items\":[";
    g_win->links.clear();
    size_t n = 0, bytes = 0;
    for (const auto& s : src)
    {
        pshelpcore::HelpDoc d;
        try { d = pshelpcore::LoadDoc(s); } catch (...) { continue; }
        if (d.html.empty()) continue;
        if (bytes + d.html.size() > 48u * 1024 * 1024) break;   // all helps together: a bound for the message
        bytes += d.html.size();
        for (const auto& l : d.links) g_win->links.insert(l);
        std::wstring topics = L"{";
        for (size_t t = 0; t < s.meta.topics.size(); ++t)
            topics += (t ? L"," : L"") + Json(s.meta.topics[t].first) + L":" + Json(s.meta.topics[t].second);
        j += std::wstring(n++ ? L"," : L"") + L"{\"id\":" + Json(s.id) + L",\"name\":" + Json(s.name) + L",\"lang\":" + Json(s.lang) +
             L",\"topics\":" + topics + L"},\"html\":" + Json(pshelpcore::W(d.html)) + L"}";
    }
    Send(j + L"]}");
    g_win->docsSent = true;
    FPLogW(L"[Help] %u help(s) shown", (unsigned)n);
}

void SendShow()
{
    if (!g_win || !g_win->ready) return;
    Send(L"{\"type\":\"show\",\"addon\":" + Json(g_win->wantAddon) + L",\"id\":" + Json(g_win->wantId) + L"}");
}

// Field of a flat JSON message from the page (strings only, no escapes needed for what we read).
std::wstring Field(const std::wstring& json, const wchar_t* key, size_t maxLen)
{
    std::string text = pshelpcore::U8(json);
    pshelpcore::JVal v;
    if (!pshelpcore::ParseJson(text, v) || v.type != pshelpcore::JVal::TObj) return std::wstring();
    std::wstring s = v.Str(key);
    return s.size() > maxLen ? std::wstring() : s;
}

void OnPageMessage(const std::wstring& json)
{
    std::wstring cmd = Field(json, L"cmd", 32);
    if (cmd == L"ready")
    {
        g_win->ready = true;
        SendInit();
        SendDocs();
        SendShow();
    }
    else if (cmd == L"reload")   // F5
    {
        SendDocs();
    }
    else if (cmd == L"open")
    {
        // only an address that a cleaned help contains, only https, in the default browser
        std::string u = pshelpcore::U8(Field(json, L"url", 2000));
        if (pshelpcore::IsHttpsUrl(u) && g_win->links.count(u))
            ShellExecuteW(g_win->hwnd, L"open", pshelpcore::W(u).c_str(), NULL, NULL, SW_SHOWNORMAL);
        else FPLogW(L"[Help] link refused");
    }
}

void Resize()
{
    if (!g_win || !g_win->ctrl) return;
    RECT rc; GetClientRect(g_win->hwnd, &rc);
    g_win->ctrl->put_Bounds(rc);
}

void StartWebView(HelpWindow* w)
{
    PWSTR base = nullptr;
    std::wstring udf;
    if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, NULL, &base)))
    {
        udf = std::wstring(base) + L"\\Tungsten\\AddonStore\\WebView2";
        SHCreateDirectoryExW(NULL, udf.c_str(), NULL);
    }
    if (base) CoTaskMemFree(base);
    std::weak_ptr<bool> alive = w->alive;
    HWND hwnd = w->hwnd;
    HRESULT hr = CreateCoreWebView2EnvironmentWithOptions(nullptr, udf.empty() ? nullptr : udf.c_str(), nullptr,
        Callback<ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler>(
            [alive, hwnd](HRESULT res, ICoreWebView2Environment* env) -> HRESULT {
                if (alive.expired() || !g_win || g_win->hwnd != hwnd) return S_OK;
                if (FAILED(res) || !env) { PostMessageW(hwnd, WM_HELP_FAIL, 0, 0); return S_OK; }
                g_win->env = env;
                return env->CreateCoreWebView2Controller(hwnd,
                    Callback<ICoreWebView2CreateCoreWebView2ControllerCompletedHandler>(
                        [alive, hwnd](HRESULT r2, ICoreWebView2Controller* c) -> HRESULT {
                            try
                            {
                                if (alive.expired() || !g_win || g_win->hwnd != hwnd) return S_OK;
                                if (FAILED(r2) || !c) { PostMessageW(hwnd, WM_HELP_FAIL, 0, 0); return S_OK; }
                                g_win->ctrl = c;
                                c->get_CoreWebView2(&g_win->web);
                                if (!g_win->web) { PostMessageW(hwnd, WM_HELP_FAIL, 0, 0); return S_OK; }
                                ComPtr<ICoreWebView2Settings> s;
                                if (SUCCEEDED(g_win->web->get_Settings(&s)) && s)
                                {
                                    s->put_AreDevToolsEnabled(FALSE);
                                    s->put_AreDefaultContextMenusEnabled(FALSE);
                                    s->put_IsStatusBarEnabled(FALSE);
                                    s->put_IsBuiltInErrorPageEnabled(FALSE);
                                    s->put_AreHostObjectsAllowed(FALSE);
                                    ComPtr<ICoreWebView2Settings4> s4;
                                    if (SUCCEEDED(s.As(&s4)) && s4) { s4->put_IsPasswordAutosaveEnabled(FALSE); s4->put_IsGeneralAutofillEnabled(FALSE); }
                                }
                                EventRegistrationToken t;
                                // the page never navigates: help links go to the browser through "open"
                                g_win->web->add_NavigationStarting(Callback<ICoreWebView2NavigationStartingEventHandler>(
                                    [](ICoreWebView2*, ICoreWebView2NavigationStartingEventArgs* a) -> HRESULT {
                                        LPWSTR uri = nullptr; a->get_Uri(&uri);
                                        std::wstring u = uri ? uri : L""; if (uri) CoTaskMemFree(uri);
                                        if (u.rfind(L"about:blank", 0) != 0 && u.rfind(L"data:", 0) != 0) a->put_Cancel(TRUE);
                                        return S_OK; }).Get(), &t);
                                g_win->web->add_NewWindowRequested(Callback<ICoreWebView2NewWindowRequestedEventHandler>(
                                    [](ICoreWebView2*, ICoreWebView2NewWindowRequestedEventArgs* a) -> HRESULT { a->put_Handled(TRUE); return S_OK; }).Get(), &t);
                                g_win->web->add_PermissionRequested(Callback<ICoreWebView2PermissionRequestedEventHandler>(
                                    [](ICoreWebView2*, ICoreWebView2PermissionRequestedEventArgs* a) -> HRESULT {
                                        a->put_State(COREWEBVIEW2_PERMISSION_STATE_DENY); return S_OK; }).Get(), &t);
                                ComPtr<ICoreWebView2_4> web4;
                                if (SUCCEEDED(g_win->web.As(&web4)) && web4)
                                    web4->add_DownloadStarting(Callback<ICoreWebView2DownloadStartingEventHandler>(
                                        [](ICoreWebView2*, ICoreWebView2DownloadStartingEventArgs* a) -> HRESULT { a->put_Cancel(TRUE); return S_OK; }).Get(), &t);
                                g_win->web->add_WebMessageReceived(Callback<ICoreWebView2WebMessageReceivedEventHandler>(
                                    [](ICoreWebView2*, ICoreWebView2WebMessageReceivedEventArgs* a) -> HRESULT {
                                        LPWSTR src = nullptr; a->get_Source(&src);
                                        std::wstring from = src ? src : L""; if (src) CoTaskMemFree(src);
                                        if (from.rfind(L"about:blank", 0) != 0 && from.rfind(L"data:", 0) != 0) return S_OK;
                                        LPWSTR json = nullptr;
                                        if (SUCCEEDED(a->get_WebMessageAsJson(&json)) && json)
                                        {
                                            // no C++ exception may cross WebView2's COM callback (Power PDF would end)
                                            try { if (g_win) OnPageMessage(json); } catch (...) { FPLogW(L"[Help] page message failed"); }
                                            CoTaskMemFree(json);
                                        }
                                        return S_OK; }).Get(), &t);
                                Resize();
                                c->put_IsVisible(TRUE);
                                c->MoveFocus(COREWEBVIEW2_MOVE_FOCUS_REASON_PROGRAMMATIC);
                                std::wstring html = PageHtml();
                                if (html.empty()) { PostMessageW(hwnd, WM_HELP_FAIL, 0, 0); return S_OK; }
                                g_win->web->NavigateToString(html.c_str());
                            }
                            catch (...) { FPLogW(L"[Help] WebView2 start failed"); }
                            return S_OK;
                        }).Get());
            }).Get());
    if (FAILED(hr)) { FPLogW(L"[Help] WebView2 environment failed (0x%08x)", (unsigned)hr); PostMessageW(hwnd, WM_HELP_FAIL, 0, 0); }
}

void Destroy()
{
    HelpWindow* w = g_win;
    if (!w) return;
    g_win = nullptr;
    *w->alive = false;
    if (w->ctrl) { w->ctrl->Close(); }
    w->web.Reset(); w->ctrl.Reset(); w->env.Reset();
    if (w->hwnd && IsWindow(w->hwnd)) { HWND h = w->hwnd; w->hwnd = NULL; DestroyWindow(h); }
    delete w;
}

LRESULT CALLBACK WndProc(HWND h, UINT msg, WPARAM wp, LPARAM lp)
{
    try
    {
        switch (msg)
        {
        case WM_SIZE: Resize(); return 0;
        case WM_SETFOCUS: if (g_win && g_win->ctrl) g_win->ctrl->MoveFocus(COREWEBVIEW2_MOVE_FOCUS_REASON_PROGRAMMATIC); return 0;
        case WM_MOVE: case WM_MOVING: if (g_win && g_win->ctrl) g_win->ctrl->NotifyParentWindowPositionChanged(); break;
        case WM_HELP_FAIL:
        {
            FPLogW(L"[Help] the help window could not start WebView2");
            const bool own = g_win && g_win->ownUi;
            if (g_win && g_win->hwnd == h) Destroy();
            // only when the ribbon or the store's own windows asked (posted, never inside a ShowHelp call)
            if (own) FPMessageBox(NULL, FPLoc(IDS_PSH_NOWEBVIEW).c_str(), FPLoc(IDS_PSH_TITLE).c_str(), MB_OK | MB_ICONINFORMATION);
            return 0;
        }
        case WM_CLOSE:
            if (g_win && g_win->hwnd == h) Destroy(); else DestroyWindow(h);
            return 0;
        case WM_GETMINMAXINFO:
        {
            MINMAXINFO* mm = (MINMAXINFO*)lp;
            UINT dpi = 96;
            HDC dc = GetDC(h); if (dc) { dpi = (UINT)GetDeviceCaps(dc, LOGPIXELSY); ReleaseDC(h, dc); }
            mm->ptMinTrackSize.x = MulDiv(640, dpi, 96); mm->ptMinTrackSize.y = MulDiv(420, dpi, 96);
            return 0;
        }
        }
    }
    catch (...) { FPLogW(L"[Help] window message failed"); }
    return DefWindowProcW(h, msg, wp, lp);
}

HWND MainWindow()
{
    struct F { static BOOL CALLBACK Cb(HWND w, LPARAM p) {
        DWORD pid = 0; GetWindowThreadProcessId(w, &pid);
        if (pid == GetCurrentProcessId() && IsWindowVisible(w) && !GetWindow(w, GW_OWNER) && GetWindowTextLengthW(w) > 0 &&
            w != (g_win ? g_win->hwnd : NULL)) { *(HWND*)p = w; return FALSE; }
        return TRUE; } };
    HWND m = NULL;
    EnumWindows(F::Cb, (LPARAM)&m);
    return m;
}

bool CreateHelpWindow(bool ownUi)
{
    LPWSTR ver = nullptr;
    HRESULT hr = GetAvailableCoreWebView2BrowserVersionString(nullptr, &ver);
    bool runtime = SUCCEEDED(hr) && ver && *ver;
    if (ver) CoTaskMemFree(ver);
    if (!runtime) { FPLogW(L"[Help] WebView2 runtime not available (0x%08x)", (unsigned)hr); return false; }

    static bool registered = false;
    if (!registered)
    {
        WNDCLASSEXW wc = { sizeof(wc) };
        wc.lpfnWndProc = WndProc;
        wc.hInstance = gHINSTANCE;
        wc.hCursor = LoadCursorW(NULL, IDC_ARROW);
        wc.hbrBackground = (HBRUSH)(COLOR_WINDOW + 1);
        wc.lpszClassName = kWndClass;
        wc.hIcon = (HICON)LoadImageW(gHINSTANCE, MAKEINTRESOURCEW(IDI_PS_STORE), IMAGE_ICON, 0, 0, LR_DEFAULTSIZE);
        wc.hIconSm = (HICON)LoadImageW(gHINSTANCE, MAKEINTRESOURCEW(IDI_PS_STORE), IMAGE_ICON, 16, 16, 0);
        if (!RegisterClassExW(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return false;
        registered = true;
    }
    // about 1120 x 760 at 100 %, never larger than the work area of Power PDF's monitor, centered there
    HWND main = MainWindow();
    MONITORINFO mi = { sizeof(mi) };
    GetMonitorInfoW(MonitorFromWindow(main ? main : GetDesktopWindow(), MONITOR_DEFAULTTONEAREST), &mi);
    UINT dpi = 96;
    HDC dc = GetDC(NULL); if (dc) { dpi = (UINT)GetDeviceCaps(dc, LOGPIXELSY); ReleaseDC(NULL, dc); }
    int ww = MulDiv(1120, dpi, 96), wh = MulDiv(760, dpi, 96);
    int aw = mi.rcWork.right - mi.rcWork.left - 40, ah = mi.rcWork.bottom - mi.rcWork.top - 40;
    if (ww > aw) ww = aw;
    if (wh > ah) wh = ah;
    int x = mi.rcWork.left + (mi.rcWork.right - mi.rcWork.left - ww) / 2, y = mi.rcWork.top + (mi.rcWork.bottom - mi.rcWork.top - wh) / 2;
    DWORD ex = 0;   // the page itself sets dir="rtl" (a mirrored parent would mirror WebView2 too)
    HWND h = CreateWindowExW(ex | WS_EX_APPWINDOW, kWndClass, FPLoc(IDS_PSH_TITLE).c_str(), WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN,
                             x, y, ww, wh, NULL, NULL, gHINSTANCE, NULL);
    if (!h) return false;
    g_win = new HelpWindow();
    g_win->hwnd = h;
    g_win->ownUi = ownUi;
    ShowWindow(h, SW_SHOWNORMAL);
    UpdateWindow(h);
    StartWebView(g_win);
    return g_win != nullptr;
}

// Is there a help for this add-on, and where does the topic go?
bool Resolve(const std::wstring& addonId, const std::wstring& topic, std::wstring& id)
{
    id.clear();
    if (addonId.empty()) return true;   // the start page
    for (const auto& s : Sources())
        if (s.id == addonId) { id = pshelpcore::ResolveTopic(s.meta, topic); return true; }
    return false;
}

// ---- HFT -----------------------------------------------------------------------------------------
// The C++ part: no exception leaves it (callers use addonhelp_client.h, which has no DURING/HANDLER).
int ShowGuarded(const wchar_t* addonId, const wchar_t* topic) noexcept
{
    AFX_MANAGE_MODULE_STATE;
    try
    {
        std::wstring id = addonId ? std::wstring(addonId, wcsnlen(addonId, 120)) : std::wstring();
        std::wstring tp = topic ? std::wstring(topic, wcsnlen(topic, 80)) : std::wstring();
        if (id.size() > 101 || (!id.empty() && !pshelpcore::IsAddonId(id))) return 0;
        if (!pshelpcore::IsTopicName(tp)) tp.clear();
        return ShowHelpImpl(id, tp, true);   // through the HFT: never a message box or other modal loop
    }
    catch (...) { FPLogW(L"[Help] ShowHelp failed"); return 0; }
}

// The HFT entry: also an exception of the Power PDF SDK (DURaise inside a host call) stays here.
// No C++ object lives in this frame, so the SDK's longjmp skips no destructor.
int TAHAPI HftShowHelp(const wchar_t* addonId, const wchar_t* topic)
{
    int rc = 0;
    DURING rc = ShowGuarded(addonId, topic); HANDLER rc = 0; END_HANDLER
    return rc;
}

DCCB1 HFT DCCB2 ProvideHft(HFTServer server, DUVersion version, void* /*rock*/)
{
    if (version > TAH_HFT_VERSION_1) return NULL;   // a newer interface than we have
    if (!g_hftV1)
    {
        HFTDataRec d = { sizeof(HFTDataRec), TAHSel_CountV1, TAH_HFT_VERSION_1 };
        g_hftV1 = HFTNewEx(server, &d);
        if (!g_hftV1) return NULL;
        HFTReplaceEntry(g_hftV1, TAHSel_ShowHelp, (HFTEntry)&HftShowHelp, 0);
    }
    return g_hftV1;
}

}   // namespace

bool PSHelpExportHft()
{
    if (g_server) return true;
    DURING g_server = HFTServerNew(TAH_HFT_NAME, &ProvideHft, NULL, NULL); HANDLER g_server = NULL; END_HANDLER
    return g_server != NULL;
}

int PSShowHelp(const std::wstring& addonId, const std::wstring& topic)
{
    try { return ShowHelpImpl(addonId, topic, false); }
    catch (...) { FPLogW(L"[Help] showing the help failed"); return 0; }
}

int ShowHelpImpl(const std::wstring& addonId, const std::wstring& topic, bool fromHft)
{
    AFX_MANAGE_MODULE_STATE;
    // re-entry (a second call while this one creates the window, e.g. a double click in an add-on):
    // remember the newest target, the running call shows it; never a second window
    static bool inShow = false;
    static std::wstring pendingAddon, pendingTopic;
    if (inShow) { pendingAddon = addonId; pendingTopic = topic; g_pendingAgain = true; return 1; }
    struct Guard { Guard() { inShow = true; } ~Guard() { inShow = false; } } guard;
    std::wstring id;
    if (!Resolve(addonId, topic, id)) { FPLogW(L"[Help] no help for %.120s", addonId.c_str()); return 0; }
    if (!g_win && !CreateHelpWindow(!fromHft))
    {
        // an add-on then opens its own file in the browser: no box for it (a call through the HFT never starts a
        // modal loop); the ribbon and the store's own windows say why, at most once in 5 s
        const bool own = !fromHft && (addonId.empty() || addonId == PS_CLIENT_ID);
        static ULONGLONG lastBox = 0;
        if (own && GetTickCount64() - lastBox > 5000)
        {
            lastBox = GetTickCount64();
            FPMessageBox(NULL, FPLoc(IDS_PSH_NOWEBVIEW).c_str(), FPLoc(IDS_PSH_TITLE).c_str(), MB_OK | MB_ICONINFORMATION);
        }
        return own ? 1 : 0;
    }
    if (g_pendingAgain)   // a re-entrant call came while the window was being made: it is the newer wish
    {
        g_pendingAgain = false;
        std::wstring pid;
        if (Resolve(pendingAddon, pendingTopic, pid)) { g_win->wantAddon = pendingAddon; g_win->wantId = pid; }
        if (g_win->ready) SendShow();
        return 1;
    }
    // the same request again within a moment (held F1): only to the front, no new jump, no flicker
    static ULONGLONG lastShow = 0;
    static std::wstring lastKey;
    const std::wstring key = addonId + L"|" + id;
    const bool repeat = key == lastKey && GetTickCount64() - lastShow < 800;
    lastShow = GetTickCount64();
    lastKey = key;
    g_win->wantAddon = addonId;
    g_win->wantId = id;
    HWND h = g_win->hwnd;
    if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
    if (GetForegroundWindow() != h) { BringWindowToTop(h); SetForegroundWindow(h); }
    if (g_win->ready && !repeat) SendShow();
    return 1;
}

bool PSHelpWindowOpen() { return g_win != nullptr; }

void PSHelpShutdown()
{
    AFX_MANAGE_MODULE_STATE;
    try { Destroy(); } catch (...) {}
}
