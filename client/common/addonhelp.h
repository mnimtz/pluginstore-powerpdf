/* addonhelp.h - the central help of the Add-on Store for every add-on (format: docs/concepts/addon-help-format.md).
 *
 * The Add-on Store client publishes a host function table (HFT) named TAH_HFT_NAME. An add-on opens the
 * central help at one of its topics (a name of its help.json "topics" or a chapter id). When the HFT is
 * not there (no store client), the add-on opens its own docs/help/<lang>.html in the browser instead.
 * Keep this file identical in the store client (client/common/addonhelp.h) and in the add-ons.
 */
#pragma once

#define TAH_HFT_NAME       "TungstenAddonHelp"
#define TAH_HFT_VERSION_1  0x00010000u
#define TAHAPI             __cdecl

enum
{
    TAHSel_ShowHelp = 1,     /* selector 0 is unused (SDK convention) */
    TAHSel_CountV1
};

/* Opens (or brings to the front) the help window at `topic` of the add-on `addonId` ("com.tungsten.mailmerge").
 * topic may be NULL or empty: the start page of the add-on. Returns 0 when the add-on has no help. */
typedef int (TAHAPI *TAHShowHelpProc)(const wchar_t* addonId, const wchar_t* topic);
