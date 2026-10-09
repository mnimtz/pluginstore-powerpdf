// link.cpp — see link.h.

#include "stdafx.h"
#include "link.h"
#include "dialog.h"
#include "settings.h"
#include "catalog.h"
#include "install.h"
#include "version.h"
#include "logging.h"
#include "blocklist.h"
#include "access.h"
#include "http.h"
#include "hostversion.h"
#include "loc.h"
#include "Resource.h"
#include <algorithm>
#include <memory>

extern "C" HINSTANCE gHINSTANCE;

namespace {

HWND g_linkWnd = NULL;
}
void PSRibbonSetUpdateBadge(int count);   // ribbon.cpp
void PSRibbonOpenStore();                  // ribbon.cpp: the same checks as the ribbon button
void PSRibbonOpenUpdates();                // ribbon.cpp: the same, in the "Available updates" view (C1.9.0)
namespace {
UINT g_openMsg = 0;
int  g_startupTries = 0;
const UINT_PTR kStartupTimer = 1;
const UINT_PTR kUpdateTimer = 2;
const UINT WM_PS_UPDATES = WM_APP + 77;   // lParam = UpdateResult*
const UINT_PTR kNoticeTimer = 6;           // one-time notice (C1.7.0): retried while Power PDF is busy
const UINT_PTR kBlockTimer = 3;            // security blocklist (C1.1.5): 20 s after start, then every 4 h
const UINT WM_PS_BLOCKED = WM_APP + 78;    // lParam = std::vector<PSBlocked>*
const UINT kBlockEveryMs = 4 * 60 * 60 * 1000;
const UINT_PTR kAccessTimer = 5;           // allowed license mode (C1.5.0): 3 s after start, then every 4 h
const UINT WM_PS_ACCESS = WM_APP + 79;     // wParam = 1 allowed, 0 not
volatile LONG g_checkRunning = 0;

// What the update check found (C1.7.0): every pending update as "id@version" and its display name.
struct UpdateResult { int count = 0; std::vector<std::wstring> keys, names; };
std::unique_ptr<UpdateResult> g_notice;    // a notice waiting for Power PDF to be idle (UI thread only)

// Tiny JSON helper for the flat Power PDF update answer: a string field, "" when missing or null.
std::wstring JsonStr(const std::wstring& j, const wchar_t* key)
{
    std::wstring k = std::wstring(L"\"") + key + L"\":";
    size_t p = j.find(k);
    if (p == std::wstring::npos) return std::wstring();
    p += k.size();
    while (p < j.size() && j[p] == L' ') ++p;
    if (p >= j.size() || j[p] != L'"') return std::wstring();
    size_t e = j.find(L'"', p + 1);
    return e == std::wstring::npos || e - p > 200 ? std::wstring() : j.substr(p + 1, e - p - 1);
}

// Worker: fetch the catalog, count newer versions, report to the window.
// It holds its own reference on this DLL, so an unload while it waits for
// the network cannot pull the code from under it.
DWORD WINAPI UpdateCheckThread(LPVOID p)
{
    std::wstring* lang = static_cast<std::wstring*>(p);
    std::vector<PSCatalogEntry> all;
    std::wstring err;
    UpdateResult* r = nullptr;
    if (PSFetchCatalogFor(*lang, all, err))
    {
        r = new UpdateResult();
        for (const auto& e : all)
        {
            if (e.id == L"com.tungsten.pluginstore")
            {
                if (!PSPolicyNoSelfUpdate() && !PSSelfUpdateBlockedBySerial() && PSCompareVersions(e.version, FP_VERSION_W) > 0)
                { r->keys.push_back(e.id + L"@" + e.version); r->names.push_back(L"Add-on Store " + e.version); }
            }
            else if (!PSPolicyNoInstall() && !e.installedVersion.empty() && e.installedVersion != L"?" &&
                     PSCompareVersions(e.version, e.installedVersion) > 0 &&
                     (e.minHost.empty() || PSHostVersion().empty() || PSCompareVersions(PSHostVersion(), e.minHost) >= 0))
            { r->keys.push_back(e.id + L"@" + e.version); r->names.push_back(e.name + L" " + e.version); }
        }
        // the Power PDF update of this release line (C1.7.0), when the hint is on and the server offers one
        std::wstring host = PSHostVersion();
        bool plain = !host.empty() && host.size() < 30 && std::all_of(host.begin(), host.end(), [](wchar_t c) { return iswdigit(c) || c == L'.'; });
        if (PSPowerPdfHint() && plain)
        {
            std::string body;
            DWORD status = 0;
            if (PSHttpGetText(PSServerUrl() + L"/api/powerpdf/update?version=" + host + L"&lang=" + *lang, body, &status) && status == 200)
            {
                std::wstring j(body.begin(), body.end());   // the fields used here are ASCII
                std::wstring latest = JsonStr(j, L"latest");
                if (j.find(L"\"newer\":true") != std::wstring::npos && !latest.empty() && latest != PSPowerPdfHiddenUpdate() &&
                    std::all_of(latest.begin(), latest.end(), [](wchar_t c) { return iswdigit(c) || c == L'.'; }))
                { r->keys.push_back(L"powerpdf@" + latest); r->names.push_back(L"Power PDF " + latest); }
            }
        }
        r->count = (int)r->keys.size();
    }
    delete lang;
    if (!r || !g_linkWnd || !PostMessageW(g_linkWnd, WM_PS_UPDATES, 0, (LPARAM)r)) delete r;
    InterlockedExchange(&g_checkRunning, 0);
    FreeLibraryAndExitThread(gHINSTANCE, 0);
}

void StartUpdateCheck()
{
    if (InterlockedCompareExchange(&g_checkRunning, 1, 0) != 0) return;
    char code[64] = { 0 };
    DURING DVAppGetLanguage(code); HANDLER END_HANDLER   // host call: UI thread only
    wchar_t w[64] = { 0 };
    MultiByteToWideChar(CP_ACP, 0, code, -1, w, 63);
    HMODULE self = NULL;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, reinterpret_cast<LPCWSTR>(&UpdateCheckThread), &self))
    { InterlockedExchange(&g_checkRunning, 0); return; }
    std::wstring* lang = new std::wstring(w);
    HANDLE t = CreateThread(NULL, 0, UpdateCheckThread, lang, 0, NULL);
    if (t) CloseHandle(t);
    else { delete lang; FreeLibrary(self); InterlockedExchange(&g_checkRunning, 0); }
}

std::wstring TakePending()
{
    wchar_t buf[256] = { 0 };
    DWORD sz = sizeof(buf);
    if (RegGetValueW(HKEY_CURRENT_USER, kPSRegKey, PS_LINK_PENDING_VALUE, RRF_RT_REG_SZ, NULL, buf, &sz) != ERROR_SUCCESS)
        return std::wstring();
    RegDeleteKeyValueW(HKEY_CURRENT_USER, kPSRegKey, PS_LINK_PENDING_VALUE);
    std::wstring id = buf;
    return PSLinkIsValidId(id) ? id : std::wstring();
}

// The Power PDF main window of this process, to bring it to the front.
BOOL CALLBACK FindMain(HWND h, LPARAM lp)
{
    DWORD pid = 0;
    GetWindowThreadProcessId(h, &pid);
    if (pid == GetCurrentProcessId() && IsWindowVisible(h) && GetWindow(h, GW_OWNER) == NULL)
    {
        *(HWND*)lp = h;
        return FALSE;
    }
    return TRUE;
}

void OpenPending()
{
    // A store window is already open: keep the request, it is replayed when
    // that window closes (PSLinkReplayPending).
    if (PSStoreDialogOpen()) return;
    std::wstring id = TakePending();
    if (id.empty()) return;
    if (!PSStoreAllowed()) { FPLogW(L"[Store] link request ignored: store not allowed for this license"); return; }
    FPLogW(L"[Store] link request for %s", id.c_str());

    HWND main = NULL;
    EnumWindows(FindMain, (LPARAM)&main);
    if (main)
    {
        if (IsIconic(main)) ShowWindow(main, SW_RESTORE);
        SetForegroundWindow(main);
    }
    DURING PSShowStoreDialog(id); HANDLER END_HANDLER
    PSUpdateCheckSoon();
}

LRESULT CALLBACK LinkWndProc(HWND h, UINT msg, WPARAM wp, LPARAM lp)
{
    if (msg == g_openMsg && g_openMsg != 0)
    {
        OpenPending();
        return 0;
    }
    if (msg == WM_TIMER && wp == kUpdateTimer)
    {
        KillTimer(h, kUpdateTimer);
        StartUpdateCheck();
        return 0;
    }
    if (msg == WM_PS_UPDATES)
    {
        std::unique_ptr<UpdateResult> r(reinterpret_cast<UpdateResult*>(lp));
        if (!r) return 0;
        // a notice still waiting is out of date now (C1.9.2: it popped up after the update was installed)
        g_notice.reset();
        KillTimer(h, kNoticeTimer);
        PSRibbonSetUpdateBadge(r->count);
        PSSetPendingUpdates(r->count);   // the Help ribbon button reads it (also at the next start)
        // one notice per new update (C1.7.0): only what the user was not told about yet
        std::wstring told = L";" + PSNotifiedUpdates() + L";";
        bool fresh = false;
        for (const auto& k : r->keys) if (told.find(L";" + k + L";") == std::wstring::npos) fresh = true;
        if (fresh && PSUpdateNotice() && PSStoreAllowed())
        {
            g_notice = std::move(r);
            SetTimer(h, kNoticeTimer, 500, NULL);
        }
        return 0;
    }
    if (msg == WM_TIMER && wp == kNoticeTimer)
    {
        KillTimer(h, kNoticeTimer);
        if (!g_notice) return 0;
        // ask over the Power PDF main window, never over a modal dialog of Power PDF or the store window
        HWND main = NULL;
        EnumWindows(FindMain, (LPARAM)&main);
        if (!main || !IsWindowEnabled(main) || PSStoreDialogOpen()) { SetTimer(h, kNoticeTimer, 60 * 1000, NULL); return 0; }
        std::unique_ptr<UpdateResult> r = std::move(g_notice);
        std::wstring list, keys;
        for (size_t i = 0; i < r->names.size() && i < 8; ++i) list += L"\n- " + r->names[i];
        if (r->names.size() > 8) list += L"\n- ...";
        for (const auto& k : r->keys) keys += (keys.empty() ? L"" : L";") + k;
        PSSetNotifiedUpdates(keys);   // told once, whatever the answer
        wchar_t text[2400];
        _snwprintf_s(text, _countof(text), _TRUNCATE, FPLoc(IDS_PS_NOTICE_TEXT).c_str(), list.c_str());
        FPLogW(L"[Store] notice about %d new update(s)", r->count);
        if (FPMessageBox(main, text, FPLoc(IDS_PSD_TITLE).c_str(), MB_YESNO | MB_ICONINFORMATION) == IDYES)
            PSRibbonOpenUpdates();
        return 0;
    }
    if (msg == WM_TIMER && wp == kAccessTimer)
    {
        SetTimer(h, kAccessTimer, kBlockEveryMs, NULL);
        PSAccessCheckStart(h, WM_PS_ACCESS);
        return 0;
    }
    if (msg == WM_PS_ACCESS)
    {
        PSAccessTakeResult(wp != 0);   // the ribbon button asks PSStoreAllowed() whenever Power PDF redraws it
        return 0;
    }
    if (msg == WM_TIMER && wp == kBlockTimer)
    {
        SetTimer(h, kBlockTimer, kBlockEveryMs, NULL);
        PSBlockCheckStart(h, WM_PS_BLOCKED);
        return 0;
    }
    if (msg == WM_PS_BLOCKED)
    {
        PSBlockTakeResult(lp);
        if (!PSBlockedInstalled().empty())
        {
            // ask over the Power PDF main window, not while a modal dialog of Power PDF is open
            HWND main = NULL;
            EnumWindows(FindMain, (LPARAM)&main);
            if (main && IsWindowEnabled(main) && !PSStoreDialogOpen()) PSOfferBlockedRemoval(main, false);
            else SetTimer(h, kBlockTimer, 60 * 1000, NULL);
        }
        return 0;
    }
    if (msg == WM_TIMER && wp == kStartupTimer)
    {
        // Power PDF was started by the link helper: wait until its main
        // window is up, then open the dialog (give up after ~1 minute).
        HWND main = NULL;
        EnumWindows(FindMain, (LPARAM)&main);
        if ((main && IsWindowEnabled(main)) || ++g_startupTries > 30)
        {
            KillTimer(h, kStartupTimer);
            if (main) OpenPending();
        }
        return 0;
    }
    return DefWindowProcW(h, msg, wp, lp);
}

} // namespace

bool PSLinkIsValidId(const std::wstring& id)
{
    if (id.size() < 3 || id.size() > 100) return false;
    bool dot = false;
    for (wchar_t c : id)
    {
        if (c == L'.') { dot = true; continue; }
        if (!((c >= L'a' && c <= L'z') || (c >= L'0' && c <= L'9') || c == L'-')) return false;
    }
    return dot && id.front() != L'.' && id.back() != L'.';
}

void PSLinkInit()
{
    g_openMsg = RegisterWindowMessageW(PS_LINK_MESSAGE);
    WNDCLASSW wc = { 0 };
    wc.lpfnWndProc = LinkWndProc;
    wc.hInstance = gHINSTANCE;
    wc.lpszClassName = PS_LINK_WINDOW_CLASS;
    RegisterClassW(&wc);
    g_linkWnd = CreateWindowExW(0, PS_LINK_WINDOW_CLASS, L"", 0, 0, 0, 0, 0, HWND_MESSAGE, NULL, wc.hInstance, NULL);
    if (!g_linkWnd) { FPLogW(L"[Store] link window not created (%lu)", GetLastError()); return; }
    // The helper uses PostMessage from a normal user process into ours.
    ChangeWindowMessageFilterEx(g_linkWnd, g_openMsg, MSGFLT_ALLOW, NULL);

    wchar_t buf[8];
    DWORD sz = sizeof(buf);
    LSTATUS rc = RegGetValueW(HKEY_CURRENT_USER, kPSRegKey, PS_LINK_PENDING_VALUE, RRF_RT_REG_SZ, NULL, buf, &sz);
    if (rc == ERROR_SUCCESS || rc == ERROR_MORE_DATA)
        SetTimer(g_linkWnd, kStartupTimer, 2000, NULL);
    if (PSUpdateBadgeEnabled())
        SetTimer(g_linkWnd, kUpdateTimer, 15000, NULL);
    // security blocks are checked regardless of the update badge setting
    SetTimer(g_linkWnd, kBlockTimer, 20000, NULL);
    SetTimer(g_linkWnd, kAccessTimer, 3000, NULL);
}

void PSUpdateCheckSoon()
{
    if (g_linkWnd && PSUpdateBadgeEnabled())
        SetTimer(g_linkWnd, kUpdateTimer, 1500, NULL);
}

void PSLinkShutdown()
{
    if (g_linkWnd) { DestroyWindow(g_linkWnd); g_linkWnd = NULL; }
    UnregisterClassW(PS_LINK_WINDOW_CLASS, gHINSTANCE);
}

void PSLinkReplayPending()
{
    if (g_linkWnd && g_openMsg) PostMessageW(g_linkWnd, g_openMsg, 0, 0);
}
