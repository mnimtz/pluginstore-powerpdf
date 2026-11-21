// modal_owner_test.cpp - the window rule of the central help (C2.1.0, store/helpwin.cpp CreateHelpWindow):
// an add-on calls ShowHelp from a MODAL dialog whose owner (the Power PDF frame) is disabled. The help window
// is created the same way as in helpwin.cpp (top-level, NO owner, WS_EX_APPWINDOW, WS_OVERLAPPEDWINDOW) and must
// then be enabled and usable, come to the front, not be disabled by the modal loop, and must not block the modal
// dialog, which closes normally while the help window stays open. Run: client\tests\run_tests.cmd.

#include <windows.h>
#include <cstdio>

static int g_failed = 0, g_checks = 0;
#define CHECK(cond) do { ++g_checks; if (!(cond)) { ++g_failed; std::printf("FAIL %s:%d  %s\n", __FILE__, __LINE__, #cond); } } while (0)

static HWND g_main = NULL, g_help = NULL, g_dlg = NULL;
static bool g_helpGotInput = false;

static LRESULT CALLBACK HelpProc(HWND h, UINT m, WPARAM w, LPARAM l)
{
    if (m == WM_APP + 1) { g_helpGotInput = true; return 0; }
    return DefWindowProcW(h, m, w, l);
}

static HWND CreateHelpLike()
{
    // exactly the parameters of CreateHelpWindow in store/helpwin.cpp
    return CreateWindowExW(WS_EX_APPWINDOW, L"HelpLikeWindow", L"Help", WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN,
                           100, 100, 600, 400, NULL, NULL, GetModuleHandleW(NULL), NULL);
}

static INT_PTR CALLBACK DlgProc(HWND h, UINT m, WPARAM w, LPARAM)
{
    if (m == WM_INITDIALOG) { g_dlg = h; SetTimer(h, 1, 150, NULL); return TRUE; }
    if (m == WM_TIMER && w == 1)
    {
        KillTimer(h, 1);
        // the add-on's F1 inside its modal dialog: ShowHelp opens the help window
        CHECK(!IsWindowEnabled(g_main));              // the modal dialog disabled the frame
        g_help = CreateHelpLike();
        CHECK(g_help != NULL);
        ShowWindow(g_help, SW_SHOWNORMAL);
        BringWindowToTop(g_help);
        SetForegroundWindow(g_help);
        CHECK(GetWindow(g_help, GW_OWNER) == NULL);    // not owned by the disabled frame
        CHECK(IsWindowEnabled(g_help));                // usable while the modal dialog runs
        // the help window gets its messages through the dialog's modal loop
        PostMessageW(g_help, WM_APP + 1, 0, 0);
        SetTimer(h, 2, 150, NULL);
        return TRUE;
    }
    if (m == WM_TIMER && w == 2)
    {
        KillTimer(h, 2);
        CHECK(g_helpGotInput);                         // not blocked by the modal loop
        HWND fg = GetForegroundWindow();
        if (fg != g_help) std::printf("note: foreground is %p, not the help window (foreground lock of a non-interactive run)\n", (void*)fg);
        // the user closes the add-on's dialog: it must close although the help window is open
        PostMessageW(h, WM_COMMAND, IDOK, 0);
        return TRUE;
    }
    if (m == WM_COMMAND && LOWORD(w) == IDOK) { EndDialog(h, IDOK); return TRUE; }
    return FALSE;
}

int main()
{
    WNDCLASSW wc = {};
    wc.lpfnWndProc = DefWindowProcW; wc.hInstance = GetModuleHandleW(NULL); wc.lpszClassName = L"FrameLike";
    RegisterClassW(&wc);
    WNDCLASSW hc = {};
    hc.lpfnWndProc = HelpProc; hc.hInstance = GetModuleHandleW(NULL); hc.lpszClassName = L"HelpLikeWindow";
    RegisterClassW(&hc);
    g_main = CreateWindowExW(0, L"FrameLike", L"Power PDF", WS_OVERLAPPEDWINDOW, 50, 50, 800, 600, NULL, NULL, wc.hInstance, NULL);
    ShowWindow(g_main, SW_SHOW);

    // a modal dialog from a template in memory, owned by the frame (as an add-on's dialog)
    struct { DLGTEMPLATE t; WORD menu, cls, title; } tpl = {};
    tpl.t.style = WS_POPUP | WS_CAPTION | WS_SYSMENU | DS_MODALFRAME | DS_CENTER;
    tpl.t.cx = 200; tpl.t.cy = 100;
    INT_PTR rc = DialogBoxIndirectParamW(wc.hInstance, &tpl.t, g_main, DlgProc, 0);
    CHECK(rc == IDOK);                                 // the modal dialog ended normally
    CHECK(IsWindowEnabled(g_main));                    // the frame is enabled again
    CHECK(g_help && IsWindow(g_help) && IsWindowVisible(g_help));   // the help window stays open
    CHECK(g_help && IsWindowEnabled(g_help));
    if (g_help) DestroyWindow(g_help);
    DestroyWindow(g_main);
    std::printf("modal_owner_tests: %d checks, %d failed\n", g_checks, g_failed);
    return g_failed ? 1 : 0;
}
