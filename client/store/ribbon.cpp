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

// The Help tab button (C1.7.0): only while updates are pending, and only where the store may be used.
static DCCB1 DUBool DCCB2 IsUpdatesVisible(void* /*data*/)
{
    LONG n = g_pending;
    if (n < 0) { n = PSPendingUpdates(); InterlockedExchange(&g_pending, n); }
    return n > 0 && PSStoreAllowed() ? true : false;
}

static DCCB1 void DCCB2 OnOpenStore(void* /*data*/)
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
    DURING PSShowStoreDialog(std::wstring()); HANDLER END_HANDLER
    PSUpdateCheckSoon();   // an install or update may have cleared the badge
}

// Amber dot on the store icon and a tooltip with the number of updates
// (store client and installed add-ons); count 0 restores the plain button.
void PSRibbonSetUpdateBadge(int count)
{
    AFX_MANAGE_MODULE_STATE;
    static int shown = 0;
    if (count >= 0) InterlockedExchange(&g_pending, count);   // the Help tab button follows
    if (!g_openBtn || count < 0 || count == shown) return;
    shown = count;
    DURING
        DVIcon big = RVToolGetIconFromBitmap(gHINSTANCE, MAKEINTRESOURCEW(count ? IDB_STORE_UPD : IDB_STORE));
        if (big) RVToolButtonSetIcon(g_openBtn, big, true);
        DVIcon sm = RVToolGetIconFromBitmap(gHINSTANCE, MAKEINTRESOURCEW(count ? IDB_STORE16_UPD : IDB_STORE16));
        if (sm) RVToolButtonSetIcon(g_openBtn, sm, false);
        std::wstring tip = FPLoc(IDS_PS_TIP_OPEN);
        if (count)
        {
            wchar_t buf[300];
            _snwprintf_s(buf, _countof(buf), _TRUNCATE, FPLoc(IDS_PS_TIP_UPDATES).c_str(), count);
            tip = buf;
        }
        DUText h = MakeDUText(tip);
        RVToolButtonSetHelpText(g_openBtn, h); DUTextDestroy(h);
    HANDLER END_HANDLER
    FPLogW(L"[Store] update badge: %d update(s)", count);
}

// The same checks as the ribbon button (used by the one-time update notice, C1.7.0).
void PSRibbonOpenStore() { OnOpenStore(NULL); }

// "Updates available" in Power PDF's own Help tab (C1.7.0): visible only while updates are pending.
// New atoms (AddonStore::Updates...), so the host's ribbon cache never knew a shorter version of it.
void PSRegisterHelpUI()
{
    AFX_MANAGE_MODULE_STATE;
    RVToolBar help = RVFrisbeeGetToolBar(DUAtomFromString("help"));
    if (!help) { FPLogW(L"[Store] Help tab not found - no updates button"); return; }
    DUAtom groupAtom = DUAtomFromString("AddonStore::Updates");
    RVToolButton group = RVToolBarGetButtonByName(help, groupAtom);
    if (!group)
    {
        group = RVToolButtonNew(groupAtom, kBtnGroup);
        RVToolBarAddButton(help, group, false, NULL);
    }
    if (!group) return;
    RVToolButtonSetComputeVisibleProc(group, IsUpdatesVisible, NULL);
    {
        DUText gl = MakeDUText(FPLoc(IDS_PS_GROUP));
        RVToolButtonSetLabelText(group, gl, kLabelBottom); DUTextDestroy(gl);
    }
    RVToolButton b = RVToolButtonNew(DUAtomFromString("AddonStore::Updates::Open"), kBtnNormal);
    if (!b) return;
    DUText l = MakeDUText(FPLoc(IDS_PS_BTN_UPDATES));
    RVToolButtonSetLabelText(b, l, kLabelBottom); DUTextDestroy(l);
    DUText h = MakeDUText(FPLoc(IDS_PS_TIP_UPDATES_HELP));
    RVToolButtonSetHelpText(b, h); DUTextDestroy(h);
    RVToolButtonSetExecuteProc(b, OnOpenStore, NULL);
    RVToolButtonSetComputeVisibleProc(b, IsUpdatesVisible, NULL);
    DVIcon big = RVToolGetIconFromBitmap(gHINSTANCE, MAKEINTRESOURCEW(IDB_STORE_UPD));
    if (big) RVToolButtonSetIcon(b, big, true);
    DVIcon sm = RVToolGetIconFromBitmap(gHINSTANCE, MAKEINTRESOURCEW(IDB_STORE16_UPD));
    if (sm) RVToolButtonSetIcon(b, sm, false);
    RVToolGroupButtonAddButton(group, b, false, NULL);
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
