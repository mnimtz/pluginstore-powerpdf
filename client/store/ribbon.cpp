// ribbon.cpp — the Plugin-Store group on the shared "Enhanced Features" tab.
// One button that opens the store dialog. Per the ribbon governance this group
// is meant to be the LAST one on the tab.

#include "stdafx.h"
#include "dialog.h"
#include "loc.h"
#include "logging.h"
#include "Resource.h"

extern "C" HINSTANCE gHINSTANCE;

static DUText MakeDUText(const std::wstring& s)
{
    return DUTextFromUnicode(reinterpret_cast<const DUUTF16Val*>(s.c_str()), kUTF16HostEndian);
}

static DCCB1 void DCCB2 OnOpenStore(void* /*data*/)
{
    AFX_MANAGE_MODULE_STATE;
    DURING PSShowStoreDialog(); HANDLER END_HANDLER
}

void PSRegisterUI(RVToolBar bar)
{
    AFX_MANAGE_MODULE_STATE;

    DUAtom groupAtom = DUAtomFromString("FeaturePack::PluginStore");
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

    RVToolButton b = RVToolButtonNew(DUAtomFromString("FeaturePack::PluginStore::Open"), kBtnNormal);
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
}
