// install.h — download, verify and install a .ppak from the store.
//
// Install mechanics (spike S1): the plug-in folder is under Program Files, so
// the file copy runs in ONE elevated PowerShell child (single UAC prompt).
// Everything else (download, SHA-256 verification) stays in user context.
// The installer also drops the package's manifest.json into the plug-in data
// folder — the basis for update detection across ALL plug-ins, including
// MSI-deployed ones.

#pragma once
#include <string>
#include <vector>
#include "oldinstalls.h"

struct PSCatalogEntry;

// Version of an installed plugin, read from <bin>\Plug-Ins\<zxtName>\manifest.json
// ("" when not installed or no manifest).
std::wstring PSInstalledVersion(const std::wstring& zxtName);

// Add-ons the store installed into ANOTHER Power PDF folder (C1.1.4), see
// oldinstalls.h; the running installation is never reported.
std::vector<PSOldInstall> PSFindOldInstalls();

// Full flow: download -> hash check -> elevated copy. Returns 0 on success,
// 1 = download failed, 2 = hash mismatch, 3 = elevation declined/failed,
// 4 = install script failed, 5 = Power PDF folder not found,
// 6 = not signed by a trusted store key (signature.h),
// 10 = blocked by a security block of the store (blocklist.h),
// 11 = older than the installed version (no downgrades),
// 12 = the name belongs to another add-on or to the store client itself,
// 13 = every installation the delivery allows is in use (server S1.4.2).
// The elevated step checks the hash once more in an admin-only staging folder
// before it extracts anything (2 when it changed in between).
int PSInstallPackage(const PSCatalogEntry& e, HWND owner);

// Add-ons the store installed into THIS Power PDF (Plug-Ins\<zxt>\manifest.json with an id),
// the store client itself excluded. Name in the given manifest language code, else English.
struct PSInstalledAddon { std::wstring id, version, zxtName, name; };
std::vector<PSInstalledAddon> PSListInstalledAddons(const std::wstring& lang);

// Removes several add-ons with ONE elevated step (C1.4.1, e.g. the add-ons of a removed
// customer code). Same return codes as PSUninstallPackage.
int PSUninstallPackages(const std::vector<std::wstring>& zxtNames, HWND owner, std::vector<bool>* removed = nullptr);
// (removed, C1.4.2: one flag per name, also when only some could be removed; 4 = not all)

// Tells the store that these delivered add-ons were removed here, so their installations
// are free again (C1.4.1). Sent with the stored customer codes; best effort.
void PSReleaseInstallations(const std::vector<std::wstring>& packageIds);

// Removes <bin>\Plug-Ins\<name>.zxt and the data folder (one elevated step)
// plus the plugin's HKCU key (user context). Same return codes as install;
// 4 also when the plug-in has no store manifest (never installed by the store).
int PSUninstallPackage(const std::wstring& zxtName, HWND owner);

// Self-update of the store client: downloads the client package (hash
// verified), asks the user, then starts a helper that waits for Power PDF to
// exit, installs the MSI (/passive, elevates itself) and starts Power PDF
// again. The CALLER closes Power PDF when this returns 0.
// 0 = helper started, 1 = download, 2 = hash, 4 = helper failed, 5 = cancelled,
// 6 = not signed by a trusted key, 8 = Power PDF folder not found,
// 7 = blocked by policy DisableSelfUpdate (install/remove: 7 = DisableInstall).
int PSSelfUpdate(const PSCatalogEntry& e, HWND owner);
// The two halves of PSSelfUpdate: the download (no UI, fine on a worker thread;
// 0 and the verified package path, or 1, 2, 6, 7, 8) and the question plus the
// helper start (UI thread; 0, 4, 5).
int PSSelfUpdateDownload(const PSCatalogEntry& e, std::wstring& ppak);
int PSSelfUpdateFinish(const PSCatalogEntry& e, const std::wstring& ppak, HWND owner);

// The self-update helper script (PowerShell) for a downloaded package; used by
// PSSelfUpdate, separate so it can be tested without a download.
std::wstring PSSelfUpdateScript(const std::wstring& ppak, const std::wstring& sha256);

// Starts a detached helper that waits until Power PDF (this process and any
// other PowerPDF.exe) has exited and then starts it again. Logs its steps to
// %TEMP%\PluginStore.log. Returns false when the helper could not start.
bool PSScheduleRestart();

// "1.2.10" > "1.2.9": numeric per-segment compare.
int PSCompareVersions(const std::wstring& a, const std::wstring& b);

// The catalog offers an update: installed with unknown version ("?", no
// manifest) or the catalog version is NEWER. An older catalog version (e.g.
// a newer build installed from an MSI) is no update, never a downgrade.
inline bool PSIsUpdate(const std::wstring& catalogVersion, const std::wstring& installed)
{
    return !installed.empty() && (installed == L"?" || PSCompareVersions(catalogVersion, installed) > 0);
}
