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
bool         PSUseClassicUI();            // ClassicUI = 1 (HKLM policy Store or HKCU): no WebView2 window

extern const wchar_t* kPSRegKey;          // HKCU key path
