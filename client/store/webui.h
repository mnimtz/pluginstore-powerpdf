// webui.h — the Add-on Store window as an embedded web page (client/ui/store.html).
//
// The page is compiled into the plug-in (RCDATA) and shown in Microsoft
// WebView2 (the Edge runtime that ships with Windows 10/11). It only renders:
// catalog download, hash checks, installation, the HTTPS rule and the admin
// prompt stay in the C++ code the classic dialog uses. Without the WebView2
// runtime, or with the ClassicUI setting, the classic dialog opens instead.
#pragma once
#include <string>

/// True when the WebView2 runtime is present and the classic UI is not enforced.
bool PSWebUiAvailable();

/// Shows the web store window modally. Returns IDABORT when WebView2 could not
/// start, so the caller can fall back to the classic dialog.
INT_PTR PSShowWebStore(const std::wstring& preselectId);
