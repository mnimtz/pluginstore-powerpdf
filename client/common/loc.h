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

void FPNote(UINT idMsg);
void FPNoteText(const std::wstring& msg);
bool FPConfirmText(const std::wstring& msg);
