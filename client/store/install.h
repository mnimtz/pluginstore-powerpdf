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

struct PSCatalogEntry;

// Version of an installed plugin, read from <bin>\Plug-Ins\<zxtName>\manifest.json
// ("" when not installed or no manifest).
std::wstring PSInstalledVersion(const std::wstring& zxtName);

// Full flow: download -> hash check -> elevated copy. Returns 0 on success,
// 1 = download failed, 2 = hash mismatch, 3 = elevation declined/failed,
// 4 = install script failed, 5 = Power PDF folder not found.
int PSInstallPackage(const PSCatalogEntry& e, HWND owner);

// Removes <bin>\Plug-Ins\<name>.zxt and the data folder (one elevated step)
// plus the plugin's HKCU key (user context). Same return codes as install.
int PSUninstallPackage(const std::wstring& zxtName, HWND owner);

// Self-update of the store client: download the client package (hash
// verified), extract its MSI in user context and launch it. The MSI closes
// Power PDF itself and elevates on its own. 0 = MSI started, 1 = download,
// 2 = hash, 4 = extract/launch failed.
int PSSelfUpdate(const PSCatalogEntry& e, HWND owner);

// "1.2.10" > "1.2.9": numeric per-segment compare.
int PSCompareVersions(const std::wstring& a, const std::wstring& b);
