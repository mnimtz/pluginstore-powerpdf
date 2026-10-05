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
#include "Resource.h"

extern "C" HINSTANCE gHINSTANCE;

static RVToolButton g_openBtn = NULL;

static DUText MakeDUText(const std::wstring& s)
{
    return DUTextFromUnicode(reinterpret_cast<const DUUTF16Val*>(s.c_str()), kUTF16HostEndian);
}

static DCCB1 void DCCB2 OnOpenStore(void* /*data*/)
{
    AFX_MANAGE_MODULE_STATE;
    // Older Power PDF than the client is built for (an MSI from another source,
    // or Power PDF downgraded afterwards): explain instead of failing half-way.
    const std::wstring host = PSHostVersion();
    if (PSHostTooOld())
    {
        wchar_t msg[600];
        _snwprintf_s(msg, _countof(msg), _TRUNCATE, FPLoc(IDS_PS_HOST_TOO_OLD).c_str(), kPSMinHost, host.c_str());
        FPLogW(L"[Store] host %s is older than %s - store not opened", host.c_str(), kPSMinHost);
        MessageBoxW(NULL, msg, FPLoc(IDS_PSD_TITLE).c_str(), MB_OK | MB_ICONINFORMATION);
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
        MessageBoxW(NULL, msg, FPLoc(IDS_PSD_TITLE).c_str(), MB_OK | MB_ICONINFORMATION);
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
    DVIcon big = RVToolGetIconFromBitmap(gHINSTANCE, MAKEINTRESOURCEW(IDB_STORE));
    if (big) RVToolButtonSetIcon(b, big, true);
    DVIcon sm = RVToolGetIconFromBitmap(gHINSTANCE, MAKEINTRESOURCEW(IDB_STORE16));
    if (sm) RVToolButtonSetIcon(b, sm, false);
    RVToolGroupButtonAddButton(group, b, false, NULL);
    g_openBtn = b;
}
