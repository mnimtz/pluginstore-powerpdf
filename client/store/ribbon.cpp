// ribbon.cpp — the Add-on Store group on the shared "Enhanced Features" tab.
// One button that opens the store dialog, on the store's own tab "Store"
// (toolbar atom AddonStore, C1.1.0); add-ons keep the shared tab.

#include "stdafx.h"
#include "dialog.h"
#include "loc.h"
#include "logging.h"
#include "link.h"
#include "install.h"      // PSCompareVersions
#include "hostversion.h"
#include "access.h"
#include "settings.h"
#include "Resource.h"

extern "C" HINSTANCE gHINSTANCE;

static RVToolButton g_openBtn = NULL;
static RVToolButton g_helpBtn = NULL;   // "Updates" in the Help tab (C1.9.1)
static volatile LONG g_pending = -1;   // updates of the last check (-1 = take HKCU PendingUpdates)

static DUText MakeDUText(const std::wstring& s)
{
    return DUTextFromUnicode(reinterpret_cast<const DUUTF16Val*>(s.c_str()), kUTF16HostEndian);
}

// Hidden when the store admins do not allow this Power PDF's license mode (C1.5.0).
static DCCB1 DUBool DCCB2 IsStoreVisible(void* /*data*/)
{
    return PSStoreAllowed() ? true : false;
}

// Updates known so far: from the last check, else the count kept from the previous session (HKCU PendingUpdates).
static int PendingNow()
{
    LONG n = g_pending;
    if (n < 0) { n = PSPendingUpdates(); InterlockedExchange(&g_pending, n); }
    return n;
}

static void OpenStoreView(const std::wstring& view)
{
    AFX_MANAGE_MODULE_STATE;
    if (!PSStoreAllowed()) { FPLogW(L"[Store] not allowed for license mode %s - store not opened", PSLicenseMode().c_str()); return; }
    // Older Power PDF than the client is built for (an MSI from another source,
    // or Power PDF downgraded afterwards): explain instead of failing half-way.
    const std::wstring host = PSHostVersion();
    if (PSHostTooOld())
    {
        wchar_t msg[600];
        _snwprintf_s(msg, _countof(msg), _TRUNCATE, FPLoc(IDS_PS_HOST_TOO_OLD).c_str(), kPSMinHost, host.c_str());
        FPLogW(L"[Store] host %s is older than %s - store not opened", host.c_str(), kPSMinHost);
        FPMessageBox(NULL, msg, FPLoc(IDS_PSD_TITLE).c_str(), MB_OK | MB_ICONINFORMATION);
        return;
    }
    // Standard/Advanced before 2026.4: the SDK is for Business only there (C1.1.3).
    if (PSHostEditionUnsupported())
    {
        const std::wstring edition = PSHostEdition();
        wchar_t msg[700];
        _snwprintf_s(msg, _countof(msg), _TRUNCATE, FPLoc(IDS_PS_HOST_EDITION).c_str(),
                     kPSMinHost, kPSAllEditionsFrom, edition.c_str(), host.c_str());
        FPLogW(L"[Store] host %s %s does not load add-ons before %s - store not opened",
               edition.c_str(), host.c_str(), kPSAllEditionsFrom);
        FPMessageBox(NULL, msg, FPLoc(IDS_PSD_TITLE).c_str(), MB_OK | MB_ICONINFORMATION);
        return;
    }
    DURING PSShowStoreDialog(view); HANDLER END_HANDLER
    PSUpdateCheckSoon();   // an install or update may have cleared the badge
}

static DCCB1 void DCCB2 OnOpenStore(void* /*data*/) { OpenStoreView(std::wstring()); }
// "Updates available" (C1.9.0): the store window in its updates view
static DCCB1 void DCCB2 OnOpenUpdates(void* /*data*/) { OpenStoreView(kPSUpdatesView); }

// Amber dot on a button's icon and a tooltip with the number of updates; count 0 restores the plain button.
static void SetBadge(RVToolButton b, int count, UINT tipNone, UINT tipSome)
{
    if (!b) return;
    DURING
        DVIcon big = RVToolGetIconFromBitmap(gHINSTANCE, MAKEINTRESOURCEW(count ? IDB_STORE_UPD : IDB_STORE));
        if (big) RVToolButtonSetIcon(b, big, true);
        DVIcon sm = RVToolGetIconFromBitmap(gHINSTANCE, MAKEINTRESOURCEW(count ? IDB_STORE16_UPD : IDB_STORE16));
        if (sm) RVToolButtonSetIcon(b, sm, false);
        std::wstring tip = FPLoc(tipNone);
        if (count)
        {
            wchar_t buf[400];
            _snwprintf_s(buf, _countof(buf), _TRUNCATE, FPLoc(tipSome).c_str(), count);
            tip = buf;
        }
        DUText h = MakeDUText(tip);
        RVToolButtonSetHelpText(b, h); DUTextDestroy(h);
    HANDLER END_HANDLER
}

// The store button and the Help tab's "Updates" (store client, installed add-ons, Power PDF).
void PSRibbonSetUpdateBadge(int count)
{
    AFX_MANAGE_MODULE_STATE;
    static int shown = -1;   // -1: the first result always draws (the Help button may start with the kept count)
    if (count >= 0) InterlockedExchange(&g_pending, count);
    if (count < 0 || count == shown) return;
    shown = count;
    SetBadge(g_openBtn, count, IDS_PS_TIP_OPEN, IDS_PS_TIP_UPDATES);
    SetBadge(g_helpBtn, count, IDS_PS_TIP_UPD_NONE, IDS_PS_TIP_UPD_SOME);
    FPLogW(L"[Store] update badge: %d update(s)", count);
}

// The same checks as the ribbon button (used by the one-time update notice, C1.7.0: since C1.9.0 its updates view).
void PSRibbonOpenStore() { OnOpenStore(NULL); }
void PSRibbonOpenUpdates() { OnOpenUpdates(NULL); }

// One button of the Help tab group (C1.9.0); the callback types come from the SDK's own declarations.
template <class Run, class Visible>
static RVToolButton AddHelpButton(RVToolButton group, const char* atom, UINT label, UINT tip, UINT icon, UINT icon16, Run run, Visible visible)
{
    RVToolButton b = RVToolButtonNew(DUAtomFromString(atom), kBtnNormal);
    if (!b) return NULL;
    DUText l = MakeDUText(FPLoc(label));
    RVToolButtonSetLabelText(b, l, kLabelBottom); DUTextDestroy(l);
    DUText h = MakeDUText(FPLoc(tip));
    RVToolButtonSetHelpText(b, h); DUTextDestroy(h);
    RVToolButtonSetExecuteProc(b, run, NULL);
    RVToolButtonSetComputeVisibleProc(b, visible, NULL);
    DVIcon big = RVToolGetIconFromBitmap(gHINSTANCE, MAKEINTRESOURCEW(icon));
    if (big) RVToolButtonSetIcon(b, big, true);
    DVIcon sm = RVToolGetIconFromBitmap(gHINSTANCE, MAKEINTRESOURCEW(icon16));
    if (sm) RVToolButtonSetIcon(b, sm, false);
    RVToolGroupButtonAddButton(group, b, false, NULL);
    return b;
}

// "Updates available" in Power PDF's own Help tab (C1.7.0): visible only while updates are pending.
// New atoms (AddonStore::Updates...), so the host's ribbon cache never knew a shorter version of it.
void PSRegisterHelpUI()
{
    AFX_MANAGE_MODULE_STATE;
    RVToolBar help = RVFrisbeeGetToolBar(DUAtomFromString("help"));
    if (!help) { FPLogW(L"[Store] Help tab not found - no updates button"); return; }
    // C1.9.1: one "Updates" button that is always there (Power PDF leaves out a group without a visible
    // button when it builds the ribbon and never adds it later). It opens the "Available updates" view;
    // its icon gets the amber dot and its tooltip the number while updates are pending.
    DUAtom groupAtom = DUAtomFromString("AddonStore::HelpTab");
    RVToolButton group = RVToolBarGetButtonByName(help, groupAtom);
    if (!group)
    {
        group = RVToolButtonNew(groupAtom, kBtnGroup);
        RVToolBarAddButton(help, group, false, NULL);
    }
    if (!group) return;
    RVToolButtonSetComputeVisibleProc(group, IsStoreVisible, NULL);
    {
        DUText gl = MakeDUText(FPLoc(IDS_PS_GROUP));
        RVToolButtonSetLabelText(group, gl, kLabelBottom); DUTextDestroy(gl);
    }
    g_helpBtn = AddHelpButton(group, "AddonStore::HelpTab::Updates", IDS_PS_BTN_UPD_SHORT, IDS_PS_TIP_UPD_NONE, IDB_STORE, IDB_STORE16,
                              OnOpenUpdates, IsStoreVisible);
    if (int n = PendingNow()) SetBadge(g_helpBtn, n, IDS_PS_TIP_UPD_NONE, IDS_PS_TIP_UPD_SOME);   // right at the start
}

void PSRegisterUI(RVToolBar bar)
{
    AFX_MANAGE_MODULE_STATE;

    DUAtom groupAtom = DUAtomFromString("AddonStore::Store");
    RVToolButton group = RVToolBarGetButtonByName(bar, groupAtom);
    if (!group)
    {
        group = RVToolButtonNew(groupAtom, kBtnGroup);
        RVToolBarAddButton(bar, group, false, NULL);
    }
    if (!group) return;
    RVToolButtonSetComputeVisibleProc(group, IsStoreVisible, NULL);
    {
        DUText gl = MakeDUText(FPLoc(IDS_PS_GROUP));
        RVToolButtonSetLabelText(group, gl, kLabelBottom); DUTextDestroy(gl);
    }

    RVToolButton b = RVToolButtonNew(DUAtomFromString("AddonStore::Store::Open"), kBtnNormal);
    if (!b) return;
    DUText l = MakeDUText(FPLoc(IDS_PS_BTN_OPEN));
    RVToolButtonSetLabelText(b, l, kLabelBottom); DUTextDestroy(l);
    DUText h = MakeDUText(FPLoc(IDS_PS_TIP_OPEN));
    RVToolButtonSetHelpText(b, h); DUTextDestroy(h);
    RVToolButtonSetExecuteProc(b, OnOpenStore, NULL);
    RVToolButtonSetComputeVisibleProc(b, IsStoreVisible, NULL);
    DVIcon big = RVToolGetIconFromBitmap(gHINSTANCE, MAKEINTRESOURCEW(IDB_STORE));
    if (big) RVToolButtonSetIcon(b, big, true);
    DVIcon sm = RVToolGetIconFromBitmap(gHINSTANCE, MAKEINTRESOURCEW(IDB_STORE16));
    if (sm) RVToolButtonSetIcon(b, sm, false);
    RVToolGroupButtonAddButton(group, b, false, NULL);
    g_openBtn = b;
}
