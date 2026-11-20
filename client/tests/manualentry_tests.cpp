// manualentry_tests.cpp - key names of a manual installation (store/manualentry.h, C2.0.1).
// Build and run: client\tests\run_tests.cmd (console program, no Power PDF needed).

#include "../store/manualentry.h"
#include <cstdio>

using namespace psmanual;

static int g_failed = 0, g_checks = 0;
#define CHECK(cond) do { ++g_checks; if (!(cond)) { ++g_failed; std::printf("FAIL %s:%d  %s\n", __FILE__, __LINE__, #cond); } } while (0)

int main()
{
    // ids as AddonSetup.ps1 accepts them ('^[a-z0-9][a-z0-9._-]{2,100}$')
    CHECK(IsValidId(L"com.tungsten.smartbookmarks"));
    CHECK(IsValidId(L"de.example.my-addon_2"));
    CHECK(IsValidId(L"abc"));
    CHECK(IsValidId(L"0ab"));
    CHECK(IsValidId(std::wstring(101, L'a')));
    CHECK(!IsValidId(std::wstring(102, L'a')));
    CHECK(!IsValidId(L""));
    CHECK(!IsValidId(L"ab"));
    CHECK(!IsValidId(L".abc"));
    CHECK(!IsValidId(L"-abc"));
    CHECK(!IsValidId(L"_abc"));
    CHECK(!IsValidId(L"Com.Tungsten.X"));          // upper case: never an id
    CHECK(!IsValidId(L"com.x\\..\\AddonStore_y"));  // no path parts
    CHECK(!IsValidId(L"com.x*"));                  // no wildcards
    CHECK(!IsValidId(L"com.x?"));
    CHECK(!IsValidId(L"com.x'y"));                 // no quote characters
    CHECK(!IsValidId(L"com.x y"));
    CHECK(!IsValidId(L"com.xä"));

    // exactly the key names of AddonSetup.ps1 (Get-EntryKey, Register-Entries)
    CHECK(UninstallKey(L"com.tungsten.x") == L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\AddonStore_com.tungsten.x");
    CHECK(ActiveSetupKey(L"com.tungsten.x") == L"SOFTWARE\\Microsoft\\Active Setup\\Installed Components\\AddonStore_com.tungsten.x");
    // an invalid or missing id gives no key at all (the script then touches nothing)
    CHECK(UninstallKey(L"").empty());
    CHECK(ActiveSetupKey(L"").empty());
    CHECK(UninstallKey(L"com.x\\y").empty());
    CHECK(ActiveSetupKey(L"*").empty());

    // DisplayVersion
    CHECK(IsValidVersion(L"1.2.3"));
    CHECK(IsValidVersion(L"10"));
    CHECK(!IsValidVersion(L""));
    CHECK(!IsValidVersion(L"1..2"));
    CHECK(!IsValidVersion(L".1"));
    CHECK(!IsValidVersion(L"1."));
    CHECK(!IsValidVersion(L"1.2-beta"));
    CHECK(!IsValidVersion(L"1.2'3"));

    // only valid ids reach the elevated step
    CHECK(SafeId(L"com.tungsten.x") == L"com.tungsten.x");
    CHECK(SafeId(L"com.x\\y").empty());
    CHECK(SafeId(L"").empty());

    // the PowerShell part: 64-bit view of HKLM, exactly the two key names, id checked again
    // with the same rule, no wildcards
    std::wstring ps = RegistryFunctions();
    CHECK(ps.find(L"OpenBaseKey('LocalMachine','Registry64')") != std::wstring::npos);
    CHECK(ps.find(L"DeleteSubKeyTree($key, $false)") != std::wstring::npos);
    CHECK(ps.find(L"('SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\AddonStore_'+$id) 'UninstallString'") != std::wstring::npos);
    CHECK(ps.find(L"('SOFTWARE\\Microsoft\\Active Setup\\Installed Components\\AddonStore_'+$id) 'StubPath'") != std::wstring::npos);
    CHECK(ps.find(L"OpenSubKey('SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\AddonStore_'+$id, $true)") != std::wstring::npos);
    CHECK(ps.find(L"$id -cmatch '^[a-z0-9][a-z0-9._-]{2,100}$'") != std::wstring::npos);
    CHECK(ps.find(L"*") == std::wstring::npos);
    // small enough that removing 29 add-ons with the longest names and ids stays well within
    // the command line limit of -EncodedCommand (32767 characters, base64 of UTF-16)
    CHECK(ps.size() < 2600);

    std::printf("%d checks, %d failed\n", g_checks, g_failed);
    return g_failed ? 1 : 0;
}
