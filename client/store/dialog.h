// dialog.h — the Add-on Store dialog.
#pragma once

#include <string>

/// Opens the store dialog. With a package id (from a website link) that
/// package is selected and, when it is not installed or outdated, the user is
/// asked right away whether to install it. Ignored while the dialog is open.
void PSShowStoreDialog(const std::wstring& preselectId = std::wstring());
