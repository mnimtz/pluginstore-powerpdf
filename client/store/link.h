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

// Update badge: checks the catalog in the background (15 s after start, and
// again shortly after the store dialog closed) and marks the ribbon button
// when the store client or an installed add-on has a newer version.
// Policy Store\UpdateBadge = 0 switches it off.
void PSUpdateCheckSoon();

// After a store window closed: opens a link request that arrived meanwhile.
void PSLinkReplayPending();

/// Package ids from links must look like reverse-DNS ids, nothing else.
bool PSLinkIsValidId(const std::wstring& id);
