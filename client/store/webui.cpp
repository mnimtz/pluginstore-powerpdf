// webui.cpp — see webui.h.

#include "stdafx.h"
#include "webui.h"
#include "catalog.h"
#include "install.h"
#include "http.h"
#include "settings.h"
#include "policy.h"
#include "loc.h"
#include "logging.h"
#include "Resource.h"
#include "version.h"
#include <wrl.h>
#include <wrl/event.h>
#include <shlobj.h>
#include <wincrypt.h>
#include <vector>
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
std::wstring Field(const std::wstring& json, const wchar_t* key)
{
    std::wstring k = std::wstring(L"\"") + key + L"\"";
    size_t p = json.find(k);
    if (p == std::wstring::npos) return std::wstring();
    p = json.find(L':', p + k.size());
    if (p == std::wstring::npos) return std::wstring();
    p = json.find(L'"', p);
    if (p == std::wstring::npos) return std::wstring();
    std::wstring v;
    for (size_t i = p + 1; i < json.size() && v.size() < 512; ++i)
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

std::wstring Fmt(UINT id, const std::wstring& a, const std::wstring& b = std::wstring())
{
    wchar_t buf[1200];
    swprintf_s(buf, 1200, FPLoc(id).c_str(), a.c_str(), b.c_str());
    return buf;
}

std::wstring FmtInt(UINT id, int n)
{
    wchar_t buf[600];
    swprintf_s(buf, 600, FPLoc(id).c_str(), n);
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

// --- the window -------------------------------------------------------------

class CWebStore : public CDialog
{
public:
    explicit CWebStore(const std::wstring& preselect) : CDialog(IDD_PS_WEB), m_preselect(preselect) {}

protected:
    enum { WM_RUN = WM_APP + 61, WM_ICONS, WM_FAIL };
    enum Job { JobInstall = 1, JobUninstall, JobSelfUpdate };

    ComPtr<ICoreWebView2Environment> m_env;
    ComPtr<ICoreWebView2Controller> m_ctrl;
    ComPtr<ICoreWebView2> m_web;
    std::vector<PSCatalogEntry> m_entries;
    PSCatalogEntry m_self;
    bool m_hasSelfUpdate = false;
    std::wstring m_preselect;
    std::vector<size_t> m_iconQueue;
    size_t m_jobIndex = 0;

    BOOL OnInitDialog() override
    {
        CDialog::OnInitDialog();
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
                [this](HRESULT res, ICoreWebView2Environment* env) -> HRESULT { return OnEnvironment(res, env); }).Get());
        if (FAILED(hr)) { FPLogW(L"[Store] WebView2 environment failed (0x%08x)", (unsigned)hr); PostMessage(WM_FAIL); }
        return TRUE;
    }

    HRESULT OnEnvironment(HRESULT res, ICoreWebView2Environment* env)
    {
        if (FAILED(res) || !env || !::IsWindow(m_hWnd)) { FPLogW(L"[Store] WebView2 environment callback failed (0x%08x)", (unsigned)res); PostMessage(WM_FAIL); return S_OK; }
        m_env = env;
        return env->CreateCoreWebView2Controller(m_hWnd, Callback<ICoreWebView2CreateCoreWebView2ControllerCompletedHandler>(
            [this](HRESULT r, ICoreWebView2Controller* c) -> HRESULT { return OnController(r, c); }).Get());
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
        m_web->add_WebMessageReceived(Callback<ICoreWebView2WebMessageReceivedEventHandler>(
            [this](ICoreWebView2*, ICoreWebView2WebMessageReceivedEventArgs* a) -> HRESULT {
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

    void SendInit()
    {
        struct S { const wchar_t* key; UINT id; };
        static const S strings[] = {
            { L"title", IDS_PSD_TITLE }, { L"search", IDS_PSW_SEARCH }, { L"all", IDS_PSW_ALL }, { L"none", IDS_PSW_NONE },
            { L"empty", IDS_PSD_EMPTY }, { L"stInstalled", IDS_PSD_ST_INSTALLED }, { L"stUpdate", IDS_PSD_ST_UPDATE },
            { L"beta", IDS_PSW_BETA }, { L"install", IDS_PSD_BTN_INSTALL }, { L"update", IDS_PSW_UPDATE },
            { L"remove", IDS_PSW_REMOVE }, { L"cancel", IDS_PSW_CANCEL }, { L"version", IDS_PSW_VERSION },
            { L"colInstalled", IDS_PSD_C_INSTALLED }, { L"whatsNew", IDS_PSW_WHATSNEW }, { L"author", IDS_PSD_C_AUTHOR },
            { L"adminNote", IDS_PSD_ADMIN_NOTE }, { L"confirmInstall", IDS_PSD_CONFIRM }, { L"confirmUninstall", IDS_PSD_CONFIRM_UNINST },
            { L"installing", IDS_PSW_INSTALLING }, { L"removing", IDS_PSW_REMOVING }, { L"later", IDS_PSW_LATER },
            { L"restartNow", IDS_PSW_RESTARTNOW }, { L"disclaimer", IDS_PSD_DISCLAIMER }, { L"disclaimerBtn", IDS_PSD_BTN_DISCLAIMER },
            { L"disclaimerFull", IDS_PSD_DISCLAIMER_FULL }, { L"selfUpdate", IDS_PSD_SELF_UPDATE }, { L"selfUpdateBtn", IDS_PSD_BTN_SELFUPD },
        };
        std::wstring j = L"{\"type\":\"init\",\"version\":" + Json(FP_VERSION_W) + L",\"strings\":{";
        for (size_t i = 0; i < _countof(strings); ++i)
            j += (i ? L"," : L"") + Json(strings[i].key) + L":" + Json(FPLoc(strings[i].id));
        Send(j + L"}}");
    }

    void LoadCatalog(bool withPreselect)
    {
        std::vector<PSCatalogEntry> all;
        std::wstring error;
        if (!PSFetchCatalog(all, error)) all.clear();
        m_entries.clear();
        m_hasSelfUpdate = false;
        for (auto& e : all)
        {
            if (e.id == kClientId)
            {
                m_self = e;
                m_hasSelfUpdate = PSCompareVersions(e.version, FP_VERSION_W) > 0;
                continue;
            }
            m_entries.push_back(e);
        }

        std::wstring j = L"{\"type\":\"catalog\",\"error\":" + Json(error) + L",\"items\":[";
        for (size_t i = 0; i < m_entries.size(); ++i)
        {
            const PSCatalogEntry& e = m_entries[i];
            wchar_t size[32]; swprintf_s(size, 32, L"%llu", e.sizeBytes);
            j += (i ? L"," : L"") + std::wstring(L"{\"id\":") + Json(e.id) + L",\"name\":" + Json(e.name) +
                 L",\"description\":" + Json(e.description) + L",\"changelog\":" + Json(e.changelog) +
                 L",\"version\":" + Json(e.version) + L",\"installed\":" + Json(e.installedVersion) +
                 L",\"channel\":" + Json(e.channel) + L",\"category\":" + Json(e.category) +
                 L",\"categoryName\":" + Json(e.categoryName.empty() ? e.category : e.categoryName) +
                 L",\"size\":" + size + L",\"author\":" + Json(e.author) + L",\"contact\":" + Json(e.contactEmail) + L"}";
        }
        j += L"]";
        if (m_hasSelfUpdate)
            j += L",\"self\":{\"version\":" + Json(m_self.version) + L",\"installed\":" + Json(FP_VERSION_W) + L"}";
        if (withPreselect && !m_preselect.empty() && m_preselect != kClientId)
        {
            j += L",\"select\":" + Json(m_preselect) + L",\"selectMissing\":" + Json(Fmt(IDS_PSD_LINK_NOTFOUND, m_preselect));
            m_preselect.clear();
        }
        Send(j + L"}");

        m_iconQueue.clear();
        for (size_t i = 0; i < m_entries.size(); ++i) m_iconQueue.push_back(i);
        PostMessage(WM_ICONS);
    }

    // One icon per message so the window stays responsive; cached per version.
    void NextIcon()
    {
        if (m_iconQueue.empty() || !m_web) return;
        size_t i = m_iconQueue.front();
        m_iconQueue.erase(m_iconQueue.begin());
        if (i < m_entries.size())
        {
            const PSCatalogEntry& e = m_entries[i];
            std::wstring dir = LocalDir(L"icons");
            std::wstring name = e.id + L"-" + e.version + L".png";
            if (!dir.empty() && SafeFileName(name) && !e.iconUrl.empty())
            {
                std::wstring path = dir + L"\\" + name;
                DWORD status = 0;
                if (GetFileAttributesW(path.c_str()) == INVALID_FILE_ATTRIBUTES)
                    PSHttpGetFile(e.iconUrl, path, &status);
                HANDLE f = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
                if (f != INVALID_HANDLE_VALUE)
                {
                    LARGE_INTEGER sz = { 0 };
                    if (GetFileSizeEx(f, &sz) && sz.QuadPart > 8 && sz.QuadPart < 4 * 1024 * 1024)
                    {
                        std::vector<BYTE> data((size_t)sz.QuadPart);
                        DWORD got = 0;
                        if (ReadFile(f, data.data(), (DWORD)data.size(), &got, NULL) && got == data.size() &&
                            data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G')
                            Send(L"{\"type\":\"icon\",\"id\":" + Json(e.id) + L",\"data\":" + Json(L"data:image/png;base64," + Base64(data)) + L"}");
                    }
                    CloseHandle(f);
                }
            }
        }
        if (!m_iconQueue.empty()) PostMessage(WM_ICONS);
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
        if (cmd == L"ready") { SendInit(); LoadCatalog(true); }
        else if (cmd == L"refresh") LoadCatalog(false);
        else if (cmd == L"install" || cmd == L"uninstall")
        {
            size_t idx = 0;
            if (Find(Field(json, L"id"), &idx)) { m_jobIndex = idx; PostMessage(WM_RUN, cmd == L"install" ? JobInstall : JobUninstall); }
            else SendMessageToPage(L"", FPLoc(IDS_PSD_EMPTY));
        }
        else if (cmd == L"selfUpdate" && m_hasSelfUpdate) PostMessage(WM_RUN, JobSelfUpdate);
        else if (cmd == L"restart")
        {
            HWND mainWnd = ::GetAncestor(m_hWnd, GA_ROOTOWNER);
            if (!PSScheduleRestart())
            {
                ::MessageBoxW(m_hWnd, FPLoc(IDS_PSD_RESTART_FAIL).c_str(), FPLoc(IDS_PSD_TITLE).c_str(), MB_OK | MB_ICONINFORMATION);
                return;
            }
            EndDialog(IDOK);
            if (mainWnd && mainWnd != m_hWnd) ::PostMessageW(mainWnd, WM_CLOSE, 0, 0);
        }
        else if (cmd == L"mail")
        {
            std::wstring to = Field(json, L"to");
            bool ok = to.find(L'@') != std::wstring::npos && to.size() < 200;
            for (wchar_t c : to) if (c <= L' ' || c == L'"' || c == L'<' || c == L'>' || c == L'&' || c == L'?') ok = false;
            if (ok) ShellExecuteW(m_hWnd, L"open", (L"mailto:" + to).c_str(), NULL, NULL, SW_SHOWNORMAL);
        }
    }

    // Long-running work runs here, after the page has shown its progress state.
    LRESULT OnRun(WPARAM job, LPARAM)
    {
        if (job == JobSelfUpdate)
        {
            int rc = PSSelfUpdate(m_self, m_hWnd);
            SendMessageToPage(FPLoc(IDS_PSD_TITLE), rc == 0 ? FPLoc(IDS_PSD_MSG_SELFUPD)
                : rc == 2 ? FPLoc(IDS_PSD_MSG_HASH) : FmtInt(IDS_PSD_MSG_INSTFAIL, rc));
            return 0;
        }
        if (m_jobIndex >= m_entries.size()) return 0;
        PSCatalogEntry e = m_entries[m_jobIndex];
        int rc = job == JobInstall ? PSInstallPackage(e, m_hWnd) : PSUninstallPackage(e.zxtName, m_hWnd);
        if (rc == 0)
        {
            LoadCatalog(false);
            Send(L"{\"type\":\"result\",\"ok\":true,\"restart\":true,\"title\":" + Json(e.name) + L",\"message\":" +
                 Json(Fmt(job == JobInstall ? IDS_PSD_ASK_RESTART : IDS_PSD_ASK_RESTART_UN, e.name)) + L"}");
        }
        else
        {
            std::wstring msg = rc == 2 ? FPLoc(IDS_PSD_MSG_HASH) : FmtInt(IDS_PSD_MSG_INSTFAIL, rc);
            Send(L"{\"type\":\"result\",\"ok\":false,\"title\":" + Json(e.name) + L",\"message\":" + Json(msg) + L"}");
        }
        return 0;
    }

    LRESULT OnIcons(WPARAM, LPARAM) { NextIcon(); return 0; }
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
        if (m_ctrl) { m_ctrl->Close(); m_ctrl.Reset(); }
        m_web.Reset(); m_env.Reset();
        CDialog::OnDestroy();
    }
    void OnOK() override {}   // Enter belongs to the page

    DECLARE_MESSAGE_MAP()
};

BEGIN_MESSAGE_MAP(CWebStore, CDialog)
    ON_WM_SIZE()
    ON_WM_MOVE()
    ON_WM_GETMINMAXINFO()
    ON_WM_DESTROY()
    ON_MESSAGE(CWebStore::WM_RUN, &CWebStore::OnRun)
    ON_MESSAGE(CWebStore::WM_ICONS, &CWebStore::OnIcons)
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
