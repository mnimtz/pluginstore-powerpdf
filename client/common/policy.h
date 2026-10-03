// policy.h — enterprise policies for every module's settings.
//
// HKLM\Software\Kofax\PDF\Tungsten Power PDF\FeaturePack\Policies\<section>
// holds ADMIN-ENFORCED values: anything present there beats every other
// source (HKCU, HKLM defaults, INI) — the user can change the field in the
// Options dialog all day, the policy value is what the module uses.
// LockPage=1 in the same key additionally renders the module's options
// page(s) read-only so the UI tells the truth.
//
// Section names match the INI sections: Sign, Bookmarks, Stamps, Invoice,
// Compliance.

#pragma once
#include <string>

bool FPPolicyDword(const wchar_t* section, const wchar_t* name, DWORD& v);
bool FPPolicyString(const wchar_t* section, const wchar_t* name, std::wstring& v);
bool FPPolicyLockPage(const wchar_t* section);   // LockPage != 0
void FPDisableAllChildren(HWND page);            // grey out a locked page
