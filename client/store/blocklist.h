// blocklist.h - security blocks of the store (C1.1.5).
//
// When the store admins block an add-on version (or a whole add-on) for a
// security reason, GET /api/blocked lists it: SHA-256 of the lowercase package
// id, the version or "*", and the reason. The client reads that list at start
// and every few hours, independent of the update badge setting, compares it
// with the add-ons installed in this Power PDF and asks the user to remove a
// blocked one (the removal needs administrator rights, as every uninstall).

#pragma once
#include <windows.h>
#include <string>
#include <vector>

struct PSBlocked
{
    std::wstring id, name, version, zxtName, reason;
};

// Starts the check on a worker thread; the result arrives in the link window.
void PSBlockCheckStart(HWND notify, UINT message);

// Takes the worker's result (UI thread, from the message's LPARAM).
void PSBlockTakeResult(LPARAM lp);

// The blocked add-ons installed here, from the last check (UI thread).
const std::vector<PSBlocked>& PSBlockedInstalled();

// Asks the user once per add-on and session (or always when force) to remove each
// blocked add-on, removes the ones confirmed (UAC) and offers a restart.
void PSOfferBlockedRemoval(HWND owner, bool force);
