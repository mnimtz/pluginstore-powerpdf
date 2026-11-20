// manualentry.h - the machine registry entries of a MANUAL installation (C2.0.1).
//
// The manual installation (server feature, ManualInstall\AddonSetup.ps1) registers two
// HKLM keys per add-on, named after the package id, in the 64-bit registry view:
//   SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AddonStore_<id>   ("Apps and features")
//   SOFTWARE\Microsoft\Active Setup\Installed Components\AddonStore_<id> (ribbon group at sign-in)
// Both start Plug-Ins\<Name>\setup\AddonSetup.ps1. When the store client removes such an
// add-on, that folder goes too: the client then deletes these two keys of exactly that id.
// When the client updates such an add-on, it sets DisplayVersion of the Uninstall key.
//
// Header only and without Power PDF or MFC, so client/tests can test the key names.

#pragma once
#include <string>

namespace psmanual {

// Key paths below HKLM up to the id (AddonSetup.ps1: Get-EntryKey, Register-Entries).
#define PSMANUAL_UNINSTALL_PREFIX   L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\AddonStore_"
#define PSMANUAL_ACTIVESETUP_PREFIX L"SOFTWARE\\Microsoft\\Active Setup\\Installed Components\\AddonStore_"
// The id rule of AddonSetup.ps1, also checked again in the elevated step.
#define PSMANUAL_ID_REGEX           L"^[a-z0-9][a-z0-9._-]{2,100}$"

// The id rule of AddonSetup.ps1 ('^[a-z0-9][a-z0-9._-]{2,100}$'): the only ids for which
// the manual installation creates keys. No backslash, no wildcard, no quote character.
inline bool IsValidId(const std::wstring& id)
{
    if (id.size() < 3 || id.size() > 101) return false;
    for (size_t i = 0; i < id.size(); ++i)
    {
        wchar_t c = id[i];
        bool alnum = (c >= L'a' && c <= L'z') || (c >= L'0' && c <= L'9');
        if (!alnum && (i == 0 || (c != L'.' && c != L'_' && c != L'-'))) return false;
    }
    return true;
}

// The id handed to the elevated step: "" for an invalid id (the step then touches nothing).
inline std::wstring SafeId(const std::wstring& id) { return IsValidId(id) ? id : std::wstring(); }

// DisplayVersion: digits and dots only, as the catalog checks it.
inline bool IsValidVersion(const std::wstring& v)
{
    if (v.empty() || v.size() > 32 || v.front() == L'.' || v.back() == L'.') return false;
    for (wchar_t c : v) if (!((c >= L'0' && c <= L'9') || c == L'.')) return false;
    return v.find(L"..") == std::wstring::npos;
}

// Full key paths below HKLM (64-bit view); "" for an invalid id.
inline std::wstring UninstallKey(const std::wstring& id)
{
    return IsValidId(id) ? PSMANUAL_UNINSTALL_PREFIX + id : std::wstring();
}

inline std::wstring ActiveSetupKey(const std::wstring& id)
{
    return IsValidId(id) ? PSMANUAL_ACTIVESETUP_PREFIX + id : std::wstring();
}

// PowerShell functions for the elevated step (only the id goes into the script, which keeps
// the command line of a removal of many add-ons short).
// Remove-ManualEntries $id deletes the two keys of that id, each only when its command
// (UninstallString or StubPath) starts a script that no longer exists, i.e. the setup folder
// of that installation is gone; an entry of the same add-on in another Power PDF folder whose
// script is still there stays. Set-ManualVersion $id $dir $ver writes DisplayVersion when the
// Uninstall key belongs to the installation in $dir. Missing keys and every error are silent:
// neither changes the result of the install or removal.
inline std::wstring RegistryFunctions()
{
    return std::wstring() +
        // MANUAL-PS-BEGIN
        L"function Remove-ManualEntry($key, $value) {\r\n" +
        L" try {\r\n" +
        L"  $h=[Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine','Registry64')\r\n" +
        L"  try {\r\n" +
        L"   $k=$h.OpenSubKey($key)\r\n" +
        L"   if (-not $k) { return }\r\n" +
        L"   try { $cmd=[string]$k.GetValue($value) } finally { $k.Close() }\r\n" +
        L"   if ($cmd -match '-File \"([^\"]+)\"' -and (Test-Path -LiteralPath $Matches[1])) { return }\r\n" +
        L"   $h.DeleteSubKeyTree($key, $false)\r\n" +
        L"  } finally { $h.Close() }\r\n" +
        L" } catch { }\r\n" +
        L"}\r\n" +
        L"function Remove-ManualEntries($id) {\r\n" +
        L" if (-not ($id -cmatch '" PSMANUAL_ID_REGEX L"')) { return }\r\n" +
        L" Remove-ManualEntry ('" PSMANUAL_UNINSTALL_PREFIX L"'+$id) 'UninstallString'\r\n" +
        L" Remove-ManualEntry ('" PSMANUAL_ACTIVESETUP_PREFIX L"'+$id) 'StubPath'\r\n" +
        L"}\r\n" +
        L"function Set-ManualVersion($id, $dir, $ver) {\r\n" +
        L" if (-not ($id -cmatch '" PSMANUAL_ID_REGEX L"') -or -not $ver) { return }\r\n" +
        L" try {\r\n" +
        L"  $h=[Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine','Registry64')\r\n" +
        L"  try {\r\n" +
        L"   $k=$h.OpenSubKey('" PSMANUAL_UNINSTALL_PREFIX L"'+$id, $true)\r\n" +
        L"   if (-not $k) { return }\r\n" +
        L"   try {\r\n" +
        L"    $loc=[string]$k.GetValue('InstallLocation')\r\n" +
        L"    if ($loc -and [string]::Equals([IO.Path]::GetFullPath($loc).TrimEnd('\\'), [IO.Path]::GetFullPath($dir).TrimEnd('\\'), [StringComparison]::OrdinalIgnoreCase)) { $k.SetValue('DisplayVersion', [string]$ver) }\r\n" +
        L"   } finally { $k.Close() }\r\n" +
        L"  } finally { $h.Close() }\r\n" +
        L" } catch { }\r\n" +
        L"}\r\n";
        // MANUAL-PS-END
}

} // namespace psmanual
