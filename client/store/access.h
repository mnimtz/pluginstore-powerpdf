// access.h - may the store be used with this Power PDF license (C1.5.0)?
//
// The store admins choose under Settings, "Add-on Store" which license modes of
// Power PDF may use the store (server S1.7.0). The client finds the mode in the
// registry, sends it with every store request (header X-License-Mode) and asks
// GET /api/client/access at start and every few hours. When the answer is "no",
// the ribbon button of the store is hidden; the decision is kept in HKCU
// StoreAccess so the button stays hidden at the next start, also offline.
// Installed add-ons are not touched. It is a usage rule, not copy protection.

#pragma once
#include <windows.h>
#include <string>

// "cloud" (Cloud License Server), "serial" (legacy serial number) or "unknown"
// (on-premise License Server is not told apart yet). Read from
// HKLM\SOFTWARE\Kofax\PDF\V1: SerialNumber, and the CLS subkey with LicenseURL.
std::wstring PSLicenseMode();
// C1.9.6: setup refuses Power PDF licensed with a serial number (store for SaaS only), so no
// store update is offered there: it could not be installed.
bool PSSelfUpdateBlockedBySerial();

// The last answer of the server; true when it never answered (first start).
bool PSStoreAllowed();

// Starts the check on a worker thread; posts message(wParam = 1 allowed / 0 not) to notify.
// Nothing is posted when the server cannot be reached: the last answer stays.
void PSAccessCheckStart(HWND notify, UINT message);

// Stores the answer (UI thread).
void PSAccessTakeResult(bool allowed);
