// optionspage.cpp - the store's page in File > Options > "Add-on Store".
//
// A prefs TYPE must never be registered without at least one page, or Power PDF
// crashes when the category is opened (see reference: prefs-page-crash). This
// page edits the server URL and the beta-channel switch; an HKLM policy can
// enforce both (the page then renders read-only).

#include "stdafx.h"
#include "loc.h"
#include "logging.h"
#include "version.h"
#include "policy.h"
#include "Resource.h"
#include "RVPanelPagePref.h"
#include "../store/settings.h"

extern "C" HINSTANCE gHINSTANCE;

static bool ReadVerbose()
{
    DWORD v = 0, cb = sizeof(v);
    return RegGetValueW(HKEY_CURRENT_USER, kPSRegKey, L"VerboseLog", RRF_RT_REG_DWORD, NULL, &v, &cb) == ERROR_SUCCESS && v != 0;
}
static void WriteVerbose(bool on)
{
    HKEY k;
    if (RegCreateKeyExW(HKEY_CURRENT_USER, kPSRegKey, 0, NULL, 0, KEY_WRITE, NULL, &k, NULL) == ERROR_SUCCESS)
    { DWORD d = on ? 1 : 0; RegSetValueExW(k, L"VerboseLog", 0, REG_DWORD, (const BYTE*)&d, sizeof(d)); RegCloseKey(k); }
}

static INT_PTR CALLBACK PsoDlgProc(HWND h, UINT msg, WPARAM /*wp*/, LPARAM /*lp*/)
{
    if (msg == WM_INITDIALOG)
    {
        SetDlgItemTextW(h, IDC_PSO_GRP,      FPLoc(IDS_PSO_GRP).c_str());
        SetDlgItemTextW(h, IDC_PSO_URL_LBL,  FPLoc(IDS_PSO_URL_LBL).c_str());
        SetDlgItemTextW(h, IDC_PSO_URL,      PSServerUrl().c_str());
        SetDlgItemTextW(h, IDC_PSO_BETA,     FPLoc(IDS_PSO_BETA).c_str());
        SetDlgItemTextW(h, IDC_PSO_HINT,     FPLoc(IDS_PSO_HINT).c_str());
        SetDlgItemTextW(h, IDC_PSO_GRP_DIAG, FPLoc(IDS_PSO_GRP_DIAG).c_str());
        SetDlgItemTextW(h, IDC_PSO_VERBOSE,  FPLoc(IDS_PSO_VERBOSE).c_str());
        wchar_t v[160]; swprintf_s(v, 160, FPLoc(IDS_PSO_VERSIONLBL).c_str(), FP_VERSION_W);
        SetDlgItemTextW(h, IDC_PSO_VERSION, v);
        CheckDlgButton(h, IDC_PSO_BETA, PSBetaChannel() ? BST_CHECKED : BST_UNCHECKED);
        CheckDlgButton(h, IDC_PSO_VERBOSE, ReadVerbose() ? BST_CHECKED : BST_UNCHECKED);

        if (PSUrlLocked())
            EnableWindow(GetDlgItem(h, IDC_PSO_URL), FALSE);
        if (FPPolicyLockPage(L"Store"))
            FPDisableAllChildren(h);
        return TRUE;
    }
    return FALSE;
}

static void*  PsoCreate(void* parent)
{
    AFX_MANAGE_MODULE_STATE;
    return (void*)CreateDialogParamW(gHINSTANCE, MAKEINTRESOURCEW(IDD_PS_OPTIONS), (HWND)parent, PsoDlgProc, 0);
}
static DUBool PsoCheck(void*) { return true; }
static DUBool PsoUpdate(void* hWnd)
{
    AFX_MANAGE_MODULE_STATE;
    HWND h = (HWND)hWnd;
    wchar_t url[1024] = { 0 };
    GetDlgItemTextW(h, IDC_PSO_URL, url, 1024);
    bool beta = IsDlgButtonChecked(h, IDC_PSO_BETA) == BST_CHECKED;
    PSSaveUserSettings(url, beta);
    bool verbose = IsDlgButtonChecked(h, IDC_PSO_VERBOSE) == BST_CHECKED;
    WriteVerbose(verbose);
    FPLogSetVerbose(verbose);
    return true;
}
static void PsoHelp(void*) {}

static RVPrefPageHandlerRec g_psoHandler = { PsoCreate, PsoCheck, PsoUpdate, PsoHelp };

// Registers the store's options page under the given prefs type.
void PSRegisterOptionsPage(const char* prefsType)
{
    AFX_MANAGE_MODULE_STATE;
    DURING
        std::wstring w = FPLoc(IDS_PSO_PAGE);
        DUText t = DUTextFromUnicode((const DUUTF16Val*)w.c_str(), kUTF16HostEndian);
        RVAppRegisterPrefsPage(prefsType, "PsGeneral", t, &g_psoHandler);
    HANDLER END_HANDLER
}
