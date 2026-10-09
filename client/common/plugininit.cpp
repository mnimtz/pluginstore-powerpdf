// plugininit.cpp — Add-on Store client lifecycle.
//
// ONE plug-in, ONE handshake, the SHARED ribbon tab (atom "FeaturePack",
// title "Erweiterte Funktionen"/"Enhanced Features"): all store plug-ins put
// their group on this one tab; our group is the Add-on Store button. Everything else is
// PluginStore-own and collision-free: extension atom (TUNGSTEN:PluginStore),
// registry key (...\PluginStore), data folder (Plug-Ins\PluginStore),
// log (%TEMP%\PluginStore.log).

#include "stdafx.h"
#include "pluginapp.h"
#include "loc.h"
#include "version.h"
#include "layoutpatch.h"
#include "powerpdfpath.h"
#include "../store/settings.h"
#include "../store/link.h"
#include "logging.h"

void PSRegisterUI(RVToolBar bar);           // store/ribbon.cpp
void PSRegisterHelpUI();                     // store/ribbon.cpp: "Updates available" on the Help tab (C1.7.0)
void PSSettingsLoad();                      // store/settings.cpp
void PSRegisterOptionsPage(const char*);    // common/optionspage.cpp
extern ExtensionID gExtensionID;            // common/pimain.cpp

DCCB1 DUBool DCCB2 PluginInit();
DCCB1 DUBool DCCB2 PluginUnload();
DCCB1 DUBool DCCB2 PluginExportHFTs();
DCCB1 DUBool DCCB2 PluginImportReplaceAndRegister();
DCCB1 DUBool DCCB2 PIHandshake(Uns32 handshakeVersion, void* handshakeData);
DUAtom        GetExtensionName();

extern "C" HINSTANCE gHINSTANCE;

static DUText MakeDUText(const wchar_t* s)
{
    if (!s) return NULL;
    return DUTextFromUnicode(reinterpret_cast<const DUUTF16Val*>(s), kUTF16HostEndian);
}

// The tab title IS localized and IDENTICAL to the Feature Pack's, so all
// store plug-ins land on ONE shared "Enhanced Features" tab.
static std::wstring TabTitle() { return FPLoc(IDS_PS_TAB_STORE); }

// Power PDF's Plug-Ins folder (C1.9.4: the layout repair adds the groups of installed add-ons)
static std::wstring PluginsFolder()
{
    std::wstring bin = cspath::FindBin((HMODULE)gHINSTANCE);
    return bin.empty() ? std::wstring() : bin + L"\\Plug-Ins";
}

DCCB1 DUBool DCCB2 PluginInit()
{
    AFX_MANAGE_MODULE_STATE;

    FPLocInit();
    PSSettingsLoad();

    // ---- our OWN ribbon tab "Store" (atom "AddonStore", C1.1.0) ------------
    // Add-ons keep the shared "FeaturePack" tab; the store no longer sits there.
    DUAtom toolbarAtom = DUAtomFromString("AddonStore");
    RVToolBar bar = RVFrisbeeGetToolBar(toolbarAtom);
    if (!bar)
    {
        DUText title = MakeDUText(TabTitle().c_str());
        bar = RVToolBarNew(toolbarAtom, title);
        DUTextDestroy(title);
        RVFrisbeeAddToolBar(bar, kDVToolBarDockTop, false, NULL);
    }
    if (!bar) return false;

    DURING PSRegisterUI(bar); HANDLER END_HANDLER
    DURING PSRegisterHelpUI(); HANDLER END_HANDLER

    // ---- Options category + page -------------------------------------------
    // A prefs TYPE with ZERO pages CRASHES Power PDF when the category opens,
    // so register the type AND the page together.
    DURING
        DUText optTitle = MakeDUText(FPLoc(IDS_PS_GROUP).c_str());   // "Add-on Store"
        RVAppRegisterPrefsType("PluginStore", optTitle);
    HANDLER END_HANDLER
    DURING PSRegisterOptionsPage("PluginStore"); HANDLER END_HANDLER

    int reset = -1;
    DURING reset = fplayout::ResetSharedTabOnFreshInstall(kPSRegKey); HANDLER END_HANDLER
    if (reset >= 0) FPLogW(L"[Store] fresh install: shared tab cleared in %d layout file(s)", reset);

    DURING PSLinkInit(); HANDLER END_HANDLER

    int fixed = fplayout::ApplyButtons(PluginsFolder());
    FPLogW(L"[Store] v%s ready, layout repaired: %d file(s)", FP_VERSION_W, fixed);
    return true;
}

// Plug-ins initialised after us may have appended their groups behind ours;
// re-applying at shutdown keeps the Add-on Store group last for the next start.
DCCB1 DUBool DCCB2 PluginUnload()
{
    DURING fplayout::ApplyButtons(PluginsFolder()); HANDLER END_HANDLER
    DURING PSLinkShutdown(); HANDLER END_HANDLER
    return true;
}
DCCB1 DUBool DCCB2 PluginExportHFTs()  { return true; }

DCCB1 DUBool DCCB2 PluginImportReplaceAndRegister()
{
    AFX_MANAGE_MODULE_STATE;
    return true;
}

DUAtom GetExtensionName()
{
    return DUAtomFromString("TUNGSTEN:PluginStore");
}

DCCB1 DUBool DCCB2 PIHandshake(Uns32 handshakeVersion, void* handshakeData)
{
    if (handshakeVersion == HANDSHAKE_V0200)
    {
        PIHandshakeData_V0200* hsData = (PIHandshakeData_V0200*)handshakeData;

        hsData->extensionName = GetExtensionName();
        hsData->exportHFTsCallback =
            DUCallbackCreateProto(PIExportHFTsProcType, PluginExportHFTs);
        hsData->importReplaceAndRegisterCallback =
            DUCallbackCreateProto(PIImportReplaceAndRegisterProcType, PluginImportReplaceAndRegister);
        hsData->initCallback =
            DUCallbackCreateProto(PIInitProcType, PluginInit);
        hsData->unloadCallback =
            DUCallbackCreateProto(PIUnloadProcType, PluginUnload);

        return true;
    }
    return false;
}
