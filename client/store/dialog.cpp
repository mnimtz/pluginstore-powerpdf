// dialog.cpp — the Add-on Store dialog: catalog list, description, install.

#include "stdafx.h"
#include "dialog.h"
#include "link.h"
#include "catalog.h"
#include "access.h"
#include "install.h"
#include "settings.h"
#include "loc.h"
#include "logging.h"
#include "Resource.h"
#include "version.h"
#include "webui.h"
#include <afxcmn.h>
#include <vector>

namespace storedlg {

class CStoreDialog : public CDialog
{
public:
    explicit CStoreDialog(const std::wstring& preselect) : CDialog(IDD_PS_DIALOG), m_preselect(preselect) {}

protected:
    CListCtrl m_list;
    std::vector<PSCatalogEntry> m_entries;
    PSCatalogEntry m_self;          // the store client's own catalog entry
    bool m_selfUpdate = false;
    std::wstring m_preselect;       // package id from a website link
    static const UINT WM_ASK_INSTALL = WM_APP + 41;

    BOOL OnInitDialog() override
    {
        CDialog::OnInitDialog();

        SetWindowTextW(FPLoc(IDS_PSD_TITLE).c_str());
        SetDlgItemTextW(IDC_PS_REFRESH, FPLoc(IDS_PSD_BTN_REFRESH).c_str());
        SetDlgItemTextW(IDC_PS_UNINSTALL, FPLoc(IDS_PSD_BTN_UNINSTALL).c_str());
        SetDlgItemTextW(IDC_PS_SELFUPDATE, FPLoc(IDS_PSD_BTN_SELFUPD).c_str());
        SetDlgItemTextW(IDC_PS_INSTALL, FPLoc(IDS_PSD_BTN_INSTALL).c_str());
        SetDlgItemTextW(IDCANCEL,       FPLoc(IDS_PSD_BTN_CLOSE).c_str());
        SetDlgItemTextW(IDC_PS_ADMIN_NOTE, FPLoc(IDS_PSD_ADMIN_NOTE).c_str());
        SetDlgItemTextW(IDC_PS_DISCLAIMER, FPLoc(IDS_PSD_DISCLAIMER).c_str());
        SetDlgItemTextW(IDC_PS_DISCLAIMER_BTN, FPLoc(IDS_PSD_BTN_DISCLAIMER).c_str());

        m_list.SubclassDlgItem(IDC_PS_LIST, this);
        m_list.SetExtendedStyle(LVS_EX_FULLROWSELECT | LVS_EX_GRIDLINES);
        m_list.InsertColumn(0, FPLoc(IDS_PSD_C_NAME).c_str(),      LVCFMT_LEFT, 160);
        m_list.InsertColumn(1, FPLoc(IDS_PSD_C_AVAIL).c_str(),     LVCFMT_LEFT, 65);
        m_list.InsertColumn(2, FPLoc(IDS_PSD_C_INSTALLED).c_str(), LVCFMT_LEFT, 65);
        m_list.InsertColumn(3, FPLoc(IDS_PSD_C_STATUS).c_str(),    LVCFMT_LEFT, 110);
        m_list.InsertColumn(4, FPLoc(IDS_PSD_C_SIZE).c_str(),      LVCFMT_RIGHT, 60);
        m_list.InsertColumn(5, FPLoc(IDS_PSD_C_AUTHOR).c_str(),    LVCFMT_LEFT, 130);
        m_list.InsertColumn(6, FPLoc(IDS_PSD_C_CONTACT).c_str(),   LVCFMT_LEFT, 210);

        Reload();
        if (!m_preselect.empty()) ApplyPreselect();
        return TRUE;
    }

    void ApplyPreselect()
    {
        for (int row = 0; row < m_list.GetItemCount(); ++row)
        {
            size_t idx = (size_t)m_list.GetItemData(row);
            if (idx >= m_entries.size() || m_entries[idx].id != m_preselect) continue;
            m_list.SetItemState(-1, 0, LVIS_SELECTED | LVIS_FOCUSED);
            m_list.SetItemState(row, LVIS_SELECTED | LVIS_FOCUSED, LVIS_SELECTED | LVIS_FOCUSED);
            m_list.EnsureVisible(row, FALSE);
            UpdateDescription();
            const PSCatalogEntry& e = m_entries[idx];
            if (e.installedVersion.empty() || PSIsUpdate(e.version, e.installedVersion))
                PostMessage(WM_ASK_INSTALL);
            return;
        }
        if (m_preselect != L"com.tungsten.pluginstore")
        {
            wchar_t msg[400];
            _snwprintf_s(msg, 400, _TRUNCATE, FPLoc(IDS_PSD_LINK_NOTFOUND).c_str(), m_preselect.c_str());
            SetDlgItemTextW(IDC_PS_STATUS, msg);
        }
    }

    afx_msg LRESULT OnAskInstall(WPARAM, LPARAM) { OnInstall(); return 0; }

    void Reload()
    {
        m_list.DeleteAllItems();
        m_entries.clear();
        SetDlgItemTextW(IDC_PS_STATUS, L"");
        SetDlgItemTextW(IDC_PS_DESC, L"");

        std::wstring error;
        if (!PSFetchCatalog(m_entries, error))
        {
            SetDlgItemTextW(IDC_PS_STATUS, error.c_str());
            GetDlgItem(IDC_PS_SELFUPDATE)->ShowWindow(SW_HIDE);
            return;
        }

        // The client has its own lane: it is not listed as a plugin; a newer
        // catalog version shows up as an update hint instead.
        m_selfUpdate = false;
        for (auto it = m_entries.begin(); it != m_entries.end(); ++it)
        {
            if (it->id == L"com.tungsten.pluginstore")
            {
                m_self = *it;
                m_selfUpdate = !PSPolicyNoSelfUpdate() && !PSSelfUpdateBlockedBySerial() && PSCompareVersions(it->version, FP_VERSION_W) > 0;
                m_entries.erase(it);
                break;
            }
        }
        GetDlgItem(IDC_PS_SELFUPDATE)->ShowWindow(m_selfUpdate ? SW_SHOW : SW_HIDE);
        if (m_selfUpdate)
        {
            wchar_t hint[256];
            _snwprintf_s(hint, 256, _TRUNCATE, FPLoc(IDS_PSD_SELF_UPDATE).c_str(), m_self.version.c_str(), FP_VERSION_W);
            SetDlgItemTextW(IDC_PS_STATUS, hint);
        }
        if (m_entries.empty())
        {
            SetDlgItemTextW(IDC_PS_STATUS, FPLoc(IDS_PSD_EMPTY).c_str());
            return;
        }

        for (int i = 0; i < (int)m_entries.size(); ++i)
        {
            const PSCatalogEntry& e = m_entries[i];
            std::wstring name = e.name;
            if (e.channel == L"beta") name += FPLoc(IDS_PSD_BETA_TAG);
            int row = m_list.InsertItem(i, name.c_str());
            m_list.SetItemText(row, 1, e.version.c_str());
            m_list.SetItemText(row, 2, e.installedVersion.c_str());
            m_list.SetItemText(row, 3, StatusText(e).c_str());
            wchar_t size[32];
            swprintf_s(size, 32, L"%llu KB", e.sizeBytes / 1024);
            m_list.SetItemText(row, 4, size);
            m_list.SetItemText(row, 5, e.author.c_str());
            m_list.SetItemText(row, 6, e.contactEmail.c_str());
            m_list.SetItemData(row, (DWORD_PTR)i);
        }
        m_list.SetItemState(0, LVIS_SELECTED | LVIS_FOCUSED, LVIS_SELECTED | LVIS_FOCUSED);
        UpdateDescription();
    }

    std::wstring StatusText(const PSCatalogEntry& e) const
    {
        if (e.installedVersion.empty()) return FPLoc(IDS_PSD_ST_NOTINST);
        if (PSIsUpdate(e.version, e.installedVersion)) return FPLoc(IDS_PSD_ST_UPDATE);
        return FPLoc(IDS_PSD_ST_INSTALLED);
    }

    const PSCatalogEntry* Selected() const
    {
        POSITION pos = m_list.GetFirstSelectedItemPosition();
        if (!pos) return nullptr;
        int row = m_list.GetNextSelectedItem(pos);
        size_t idx = (size_t)m_list.GetItemData(row);
        return idx < m_entries.size() ? &m_entries[idx] : nullptr;
    }

    void UpdateDescription()
    {
        const PSCatalogEntry* e = Selected();
        if (!e) { SetDlgItemTextW(IDC_PS_DESC, L""); return; }
        std::wstring text = e->description;
        if (!e->author.empty())
        {
            text += L"\r\n\r\n" + FPLoc(IDS_PSD_AUTHOR) + L" " + e->author;
            if (!e->contactEmail.empty()) text += L" <" + e->contactEmail + L">";
        }
        if (!e->changelog.empty())
            text += L"\r\n\r\n" + e->version + L": " + e->changelog;
        SetDlgItemTextW(IDC_PS_DESC, text.c_str());

        // Uninstall applies to installed plugins; the store client itself is
        // not removable from its own dialog.
        BOOL canUninstall = !e->installedVersion.empty() && _wcsicmp(e->zxtName.c_str(), L"PluginStore") != 0;
        // Company policy DisableInstall: browse only.
        const BOOL allowed = !PSPolicyNoInstall();
        GetDlgItem(IDC_PS_UNINSTALL)->EnableWindow(canUninstall && allowed);
        GetDlgItem(IDC_PS_INSTALL)->EnableWindow(allowed);
        if (!allowed) SetDlgItemTextW(IDC_PS_STATUS, FPLoc(IDS_PSD_POLICY_INSTALL).c_str());
    }

    void OnUninstall()
    {
        const PSCatalogEntry* e = Selected();
        if (!e || e->installedVersion.empty() || _wcsicmp(e->zxtName.c_str(), L"PluginStore") == 0) return;

        wchar_t ask[512];
        _snwprintf_s(ask, 512, _TRUNCATE, FPLoc(IDS_PSD_CONFIRM_UNINST).c_str(), e->name.c_str());
        if (MessageBoxW(ask, FPLoc(IDS_PSD_TITLE).c_str(), MB_YESNO | MB_ICONQUESTION) != IDYES)
            return;

        std::wstring name = e->name;
        CWaitCursor wait;
        int rc = PSUninstallPackage(e->zxtName, GetSafeHwnd());
        if (rc == 0)
        {
            SetDlgItemTextW(IDC_PS_STATUS, FPLoc(IDS_PSD_MSG_UNINSTOK).c_str());
            Reload();
            OfferRestart(IDS_PSD_ASK_RESTART_UN, name);
        }
        else
        {
            wchar_t msg[256];
            _snwprintf_s(msg, 256, _TRUNCATE, FPLoc(IDS_PSD_MSG_INSTFAIL).c_str(), rc);
            SetDlgItemTextW(IDC_PS_STATUS, msg);
        }
    }

    // Power PDF loads plug-ins only at start. On "yes" a detached helper
    // (PSScheduleRestart) waits until Power PDF has fully exited and starts it
    // again; then the main window gets a normal close request, so unsaved
    // documents are offered for saving and the user can still cancel.
    void OfferRestart(UINT idsQuestion, const std::wstring& name)
    {
        wchar_t ask[600];
        _snwprintf_s(ask, 600, _TRUNCATE, FPLoc(idsQuestion).c_str(), name.c_str());
        if (MessageBoxW(ask, FPLoc(IDS_PSD_TITLE).c_str(), MB_YESNO | MB_ICONQUESTION) != IDYES)
            return;

        HWND mainWnd = ::GetAncestor(GetSafeHwnd(), GA_ROOTOWNER);
        if (!PSScheduleRestart())
        {
            MessageBoxW(FPLoc(IDS_PSD_RESTART_FAIL).c_str(), FPLoc(IDS_PSD_TITLE).c_str(), MB_OK | MB_ICONINFORMATION);
            return;
        }

        EndDialog(IDCANCEL);
        if (mainWnd && mainWnd != GetSafeHwnd())
            ::PostMessageW(mainWnd, WM_CLOSE, 0, 0);
    }

    void OnInstall()
    {
        const PSCatalogEntry* e = Selected();
        if (!e) return;

        wchar_t ask[512];
        _snwprintf_s(ask, 512, _TRUNCATE, FPLoc(IDS_PSD_CONFIRM).c_str(), e->name.c_str(), e->version.c_str());
        if (MessageBoxW(ask, FPLoc(IDS_PSD_TITLE).c_str(), MB_YESNO | MB_ICONQUESTION) != IDYES)
            return;

        std::wstring name = e->name;
        CWaitCursor wait;
        int rc = PSInstallPackage(*e, GetSafeHwnd());
        if (rc == 0)
        {
            SetDlgItemTextW(IDC_PS_STATUS, FPLoc(IDS_PSD_MSG_INSTOK).c_str());
            Reload();
            OfferRestart(IDS_PSD_ASK_RESTART, name);
        }
        else if (rc == 13)   // every installation of the delivery in use (C1.4.1)
            SetDlgItemTextW(IDC_PS_STATUS, FPLoc(IDS_PSD_MSG_SEATS).c_str());
        else if (rc == 2 || rc == 6)
        {
            SetDlgItemTextW(IDC_PS_STATUS, FPLoc(rc == 2 ? IDS_PSD_MSG_HASH : IDS_PSD_MSG_SIG).c_str());
        }
        else
        {
            wchar_t msg[256];
            _snwprintf_s(msg, 256, _TRUNCATE, FPLoc(IDS_PSD_MSG_INSTFAIL).c_str(), rc);
            SetDlgItemTextW(IDC_PS_STATUS, msg);
        }
    }

    afx_msg void OnRefresh() { Reload(); }
    afx_msg void OnInstallClicked() { OnInstall(); }
    afx_msg void OnUninstallClicked() { OnUninstall(); }
    afx_msg void OnDisclaimerClicked()
    {
        MessageBoxW(FPLoc(IDS_PSD_DISCLAIMER_FULL).c_str(), FPLoc(IDS_PSD_BTN_DISCLAIMER).c_str(),
                    MB_OK | MB_ICONINFORMATION);
    }
    afx_msg void OnSelfUpdateClicked()
    {
        if (!m_selfUpdate) return;
        CWaitCursor wait;
        int rc = PSSelfUpdate(m_self, GetSafeHwnd());
        if (rc == 0)
        {
            // The helper installs once Power PDF is gone: close it now.
            HWND mainWnd = ::GetAncestor(GetSafeHwnd(), GA_ROOTOWNER);
            EndDialog(IDCANCEL);
            if (mainWnd && mainWnd != GetSafeHwnd())
                ::PostMessageW(mainWnd, WM_CLOSE, 0, 0);
        }
        else if (rc == 5)
            return;
        else if (rc == 2 || rc == 6)
            SetDlgItemTextW(IDC_PS_STATUS, FPLoc(rc == 2 ? IDS_PSD_MSG_HASH : IDS_PSD_MSG_SIG).c_str());
        else
        {
            wchar_t msg[256];
            _snwprintf_s(msg, 256, _TRUNCATE, FPLoc(IDS_PSD_MSG_INSTFAIL).c_str(), rc);
            SetDlgItemTextW(IDC_PS_STATUS, msg);
        }
    }
    afx_msg void OnListChanged(NMHDR*, LRESULT* result) { UpdateDescription(); *result = 0; }

    DECLARE_MESSAGE_MAP()
};

BEGIN_MESSAGE_MAP(CStoreDialog, CDialog)
    ON_BN_CLICKED(IDC_PS_REFRESH, &CStoreDialog::OnRefresh)
    ON_BN_CLICKED(IDC_PS_INSTALL, &CStoreDialog::OnInstallClicked)
    ON_BN_CLICKED(IDC_PS_UNINSTALL, &CStoreDialog::OnUninstallClicked)
    ON_BN_CLICKED(IDC_PS_SELFUPDATE, &CStoreDialog::OnSelfUpdateClicked)
    ON_BN_CLICKED(IDC_PS_DISCLAIMER_BTN, &CStoreDialog::OnDisclaimerClicked)
    ON_NOTIFY(LVN_ITEMCHANGED, IDC_PS_LIST, &CStoreDialog::OnListChanged)
    ON_MESSAGE(CStoreDialog::WM_ASK_INSTALL, &CStoreDialog::OnAskInstall)
END_MESSAGE_MAP()

} // namespace storedlg

static bool s_storeOpen = false;

void PSShowStoreDialog(const std::wstring& preselectId)
{
    AFX_MANAGE_MODULE_STATE;
    if (s_storeOpen) return;
    s_storeOpen = true;
    if (!PSWebUiAvailable() || PSShowWebStore(preselectId) == IDABORT)
    {
        storedlg::CStoreDialog dlg(preselectId == kPSUpdatesView ? std::wstring() : preselectId);   // C1.9.2
        dlg.DoModal();
    }
    s_storeOpen = false;
    PSLinkReplayPending();
}

bool PSStoreDialogOpen() { return s_storeOpen; }
