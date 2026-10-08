// inventory.h - the daily state for the store's "Existing customers" evaluation (C1.8.0).
//
// Once a day the store client tells its store server: install id, the DOMAIN of the
// Cloud License Server sign-in (HKCU ...\Tungsten Power PDF\Identity\email; never the
// address itself, free-mail domains are not sent), license mode, Power PDF and client
// version, installed add-ons with versions. The server ignores it unless its admins
// switched the evaluation on (GDPR confirmation). No UI; policy Store\Inventory = 0
// switches the report off.

#pragma once
#include <string>

// Sends the report when one is due (worker threads only: it waits for the network).
void PSInventoryReportIfDue();

// The domain that would be reported ("" = none: no sign-in, invalid, or free mail).
std::wstring PSInventoryDomain();
