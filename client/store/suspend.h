// suspend.h - add-ons out of service (C2.3.0, server S2.8.0, "Take out of service").
//
// GET /api/suspended lists add-ons the store admins took out of service. The client reads
// it at start and every few hours (like the blocklist). For an installed add-on at level
// "disabled" it asks the user to switch it off: Plug-Ins\<zxt>.zxt is renamed to
// <zxt>.zxt.suspended in one elevated step (the folder is under Program Files), so Power PDF
// does not load it from the next start on; manifest, data folder and settings stay. Once the
// add-on is no longer listed, it offers to switch it on again. "paused" only means no
// installations or updates; installed copies keep running, nothing is switched.
// An unreachable server changes nothing: only a list the server sent switches anything.

#pragma once
#include <windows.h>
#include <string>
#include <vector>

struct PSSuspended
{
    std::wstring id, name, version, zxtName, level, reason;   // level "" = no longer listed (back in service)
    bool off = false;                                          // switched off here (<zxt>.zxt.suspended)
    bool on = false;                                           // <zxt>.zxt is there
};

// Starts the check on a worker thread; the result arrives in the link window.
void PSSuspendCheckStart(HWND notify, UINT message);

// Takes the worker's result (UI thread, from the message's LPARAM).
void PSSuspendTakeResult(LPARAM lp);

// Installed add-ons that are out of service, or switched off by the store, from the last check, with the
// files as they are now (UI thread).
const std::vector<PSSuspended>& PSSuspendedInstalled();

// True when the last check found something to switch (off or on again); unaskedOnly: only what the
// user was not asked about yet in this session (so a retry timer does not poll for nothing).
bool PSSuspendHasWork(bool unaskedOnly = false);

// Asks once per add-on and session (or always when force) to switch each add-on off or on again,
// switches the confirmed ones in ONE elevated step and offers a restart.
void PSOfferSuspendChanges(HWND owner, bool force);
