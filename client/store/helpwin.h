// helpwin.h - the central help of the Add-on Store (C2.1.0).
//
// One window for the help of every installed add-on (docs\help of its data folder, format
// "tungsten-addon-help" version 1, see common/addonhelp.h) and of the store client itself:
// add-ons and chapters on the left, the chapter on the right, a search over all helps at the top.
// Add-ons open it through the HFT "TungstenAddonHelp" (selector 1 ShowHelp), the ribbon through
// the "Help" button on the shared "Enhanced Features" tab, the store window with its Help button/F1.
#pragma once
#include <string>

// Publishes the HFT (PluginExportHFTs, the earliest phase: add-ons may look it up in their init).
bool PSHelpExportHft();

// Opens the help window (or brings it to the front) at a topic of an add-on: a name of its
// help.json "topics" or a chapter id. Empty addonId = start page. UI thread only.
// Returns 0 when that add-on has no help here (the add-on then opens its own file).
int PSShowHelp(const std::wstring& addonId, const std::wstring& topic);

// True when the help window is open.
bool PSHelpWindowOpen();

// True for the help window itself: it is a visible top-level window WITHOUT owner, so code that looks for
// Power PDF's main window that way (link.cpp FindMain) must skip it (review C2.1.0). Include after <windows.h>.
bool PSIsHelpWindow(HWND h);

// PluginUnload: closes the window.
void PSHelpShutdown();

// The store client's own id: its help lives in Plug-Ins\PluginStore\docs\help.
#define PS_CLIENT_ID L"com.tungsten.pluginstore"
