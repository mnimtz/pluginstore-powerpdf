// loc.h - EFIGS localization for the whole pack (shared infrastructure).
//
// The plug-in follows Power PDF's own UI language (DVAppGetLanguage), so a
// German Power PDF shows German labels, tooltips and messages. Strings live
// as RT_STRING resources in per-language blocks of res\FeaturePack.rc; every
// string id exists in all five blocks, English is the fallback.
//
// v1 lesson: this used to live inside one module and every other module
// reached over. In v2 it is common infrastructure from day one.

#pragma once
#include <string>

void         FPLocInit();
std::wstring FPLoc(UINT id);
// The LANGID of the strings in use (Power PDF's UI language, else English).
LANGID       FPLocLangId();
// Arabic (C1.3.0): right-to-left; message boxes then read and align right to left.
bool         FPLocIsRtl();
int          FPMessageBox(HWND owner, LPCWSTR text, LPCWSTR caption, UINT type);

void FPNote(UINT idMsg);
void FPNoteText(const std::wstring& msg);
bool FPConfirmText(const std::wstring& msg);
