// settings.h — Add-on Store client settings.
//
// Read chain per value, first hit wins:
//   HKLM\Software\Kofax\PDF\Tungsten Power PDF\PluginStore\Policies\Store  (admin-enforced)
//   HKCU\Software\Kofax\PDF\Tungsten Power PDF\PluginStore                (user, Options page)
//   built-in default.

#pragma once
#include <string>

void         PSSettingsLoad();            // resolve once (PluginInit)
std::wstring PSServerUrl();               // e.g. https://host (no trailing slash)
bool         PSBetaChannel();
bool         PSUrlLocked();               // true when a policy enforces the URL
void         PSSaveUserSettings(const std::wstring& url, bool beta);
bool         PSIsAllowedServerUrl(const std::wstring& url); // https://, or http:// for loopback; empty = default
bool         PSUseClassicUI();
// Customer code of a delivery (policy Store\CustomerCode beats HKCU CustomerCode);
// sent as X-Customer-Code with every store request. "" = none.
std::wstring PSCustomerCode();
bool         PSCustomerCodeLocked();      // true when a policy sets it
bool         PSIsValidCustomerCode(const std::wstring& code);  // letters, digits, '-', at most 64; "" is valid
void         PSSaveCustomerCode(const std::wstring& code);
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
