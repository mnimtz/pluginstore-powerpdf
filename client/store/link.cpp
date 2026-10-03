// link.cpp — see link.h.

#include "stdafx.h"
#include "link.h"
#include "dialog.h"
#include "settings.h"
#include "logging.h"

extern "C" HINSTANCE gHINSTANCE;

namespace {

HWND g_linkWnd = NULL;
UINT g_openMsg = 0;
int  g_startupTries = 0;
const UINT_PTR kStartupTimer = 1;

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
    std::wstring id = TakePending();
    if (id.empty()) return;
    FPLogW(L"[Store] link request for %s", id.c_str());

    HWND main = NULL;
    EnumWindows(FindMain, (LPARAM)&main);
    if (main)
    {
        if (IsIconic(main)) ShowWindow(main, SW_RESTORE);
        SetForegroundWindow(main);
    }
    DURING PSShowStoreDialog(id); HANDLER END_HANDLER
}

LRESULT CALLBACK LinkWndProc(HWND h, UINT msg, WPARAM wp, LPARAM lp)
{
    if (msg == g_openMsg && g_openMsg != 0)
    {
        OpenPending();
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
}

void PSLinkShutdown()
{
    if (g_linkWnd) { DestroyWindow(g_linkWnd); g_linkWnd = NULL; }
    UnregisterClassW(PS_LINK_WINDOW_CLASS, gHINSTANCE);
}
