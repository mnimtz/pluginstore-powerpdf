// settings.h — Add-on Store client settings.
//
// Read chain per value, first hit wins:
//   HKLM\Software\Kofax\PDF\Tungsten Power PDF\PluginStore\Policies\Store  (admin-enforced)
//   HKCU\Software\Kofax\PDF\Tungsten Power PDF\PluginStore                (user, Options page)
//   built-in default.

#pragma once
#include <string>
#include <vector>

void         PSSettingsLoad();            // resolve once (PluginInit)
std::wstring PSServerUrl();               // e.g. https://host (no trailing slash)
bool         PSBetaChannel();
bool         PSUrlLocked();               // true when a policy enforces the URL
void         PSSaveUserSettings(const std::wstring& url, bool beta);
bool         PSIsAllowedServerUrl(const std::wstring& url); // https://, or http:// for loopback; empty = default
bool         PSUseClassicUI();
// Power PDF update hint in the store window (C1.6.0; on by default since C1.6.1, the server decides): HKCU PowerPdfUpdateHint;
// policy Store\PowerPdfUpdates (0 = off, 1 = hint) beats it and locks the Options checkbox.
bool         PSPowerPdfHint();
bool         PSPowerPdfHintLocked();
void         PSSavePowerPdfHint(bool on);
// The update the user hid with "Hide" ("" = none); a newer one shows again.
std::wstring PSPowerPdfHiddenUpdate();
void         PSSetPowerPdfHiddenUpdate(const std::wstring& version);
// Customer codes of deliveries (C1.4.1: up to 10, before only one). Policy
// Store\CustomerCode (one code or several, separated by ';') beats HKCU CustomerCode
// ("CODE;CODE"; a single code of an older client reads as a list of one). The
// customer name of each code, as the store confirmed it, is kept in HKCU
// CustomerCodeNames (REG_MULTI_SZ "CODE<TAB>name").
const size_t kPSMaxCustomerCodes = 10;
std::vector<std::wstring> PSCustomerCodes();
// All codes joined with ';', sent as X-Customer-Code with every store request. "" = none.
std::wstring PSCustomerCode();
bool         PSCustomerCodeLocked();      // true when a policy sets them
bool         PSIsValidCustomerCode(const std::wstring& code);  // ONE code: letters, digits, '-', at most 64; "" is valid
bool         PSIsValidCustomerCodeList(const std::wstring& codes);   // codes separated by ';' or ','
// 0 = added, 1 = already there, 2 = list full (kPSMaxCustomerCodes).
int          PSAddCustomerCode(const std::wstring& code, const std::wstring& customer);
void         PSRemoveCustomerCode(const std::wstring& code);
std::wstring PSCustomerCodeName(const std::wstring& code);   // "" when unknown
// The codes as typed in the Options page ("A; B"); names of codes that stay are kept.
void         PSSaveCustomerCode(const std::wstring& codes);
bool         PSUpdateBadgeEnabled();      // policy Store\UpdateBadge = 0 switches the start-up check off            // ClassicUI = 1 (HKLM policy Store or HKCU): no WebView2 window
// Company policies (HKLM ...\PluginStore\Policies\Store only, never HKCU):
bool         PSPolicyNoInstall();         // DisableInstall = 1: browse only, no install/remove
bool         PSPolicyNoSelfUpdate();
// Random id of this installation (per Windows user), created on first use;
// the server stores only a per-package hash of it (ratings, problem reports).
std::wstring PSInstallId();
int          PSMyRating(const std::wstring& packageId);            // 0 = not rated
void         PSSetMyRating(const std::wstring& packageId, int stars);      // DisableSelfUpdate = 1: no client update from the store (IT deploys the MSI)

extern const wchar_t* kPSRegKey;          // HKCU key path
