// link.h — "Install in Power PDF" links from the store website.
//
// The MSI registers the URL scheme addonstore:// with AddonStoreLink.exe.
// That helper stores the requested package id under HKCU and either pokes
// the running Power PDF (message-only window below) or starts Power PDF.
// The client then opens the store dialog with that package selected and
// asks the user before anything is installed.
#pragma once
#include <string>

#define PS_LINK_WINDOW_CLASS  L"TungstenAddonStoreLinkTarget"
#define PS_LINK_MESSAGE       L"TungstenAddonStore.OpenRequest"
#define PS_LINK_PENDING_VALUE L"PendingOpen"

void PSLinkInit();       // PluginInit: create the message-only window, pick up a pending request
void PSLinkShutdown();   // PluginUnload

/// Package ids from links must look like reverse-DNS ids, nothing else.
bool PSLinkIsValidId(const std::wstring& id);
