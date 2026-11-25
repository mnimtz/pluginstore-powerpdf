// linkcode_tests.cpp - tests of the code words from the store website (store/linkcode.h, C2.4.0).
// Build and run: client\tests\run_tests.cmd (console program, no Power PDF needed).

#include "../store/linkcode.h"
#include <cstdio>

using namespace pslink;

static int g_failed = 0, g_checks = 0;
#define CHECK(cond) do { ++g_checks; if (!(cond)) { ++g_failed; std::printf("FAIL %s:%d  %s\n", __FILE__, __LINE__, #cond); } } while (0)

int main()
{
    // the code word itself: 5 to 32 letters and digits, at least one letter, hyphens ignored, upper case
    CHECK(NormalizeCode(L"FINANCE") == L"FINANCE");
    CHECK(NormalizeCode(L"finance") == L"FINANCE");
    CHECK(NormalizeCode(L"Finance-2026") == L"FINANCE2026");
    CHECK(NormalizeCode(L"ABCD-EFGH-JKLM-NPQR-STUV") == L"ABCDEFGHJKLMNPQRSTUV");   // a generated code passes too
    CHECK(NormalizeCode(L"FIN").empty());
    CHECK(NormalizeCode(L"12345").empty());
    CHECK(NormalizeCode(L"FIN ANCE").empty());
    CHECK(NormalizeCode(L"FINANZ\u00c4MTER").empty());
    CHECK(NormalizeCode(L"FINANCE<script>").empty());
    CHECK(NormalizeCode(L"ABCDEFGHIJKLMNOPQRSTUVWXYZ1234567").empty());   // 33
    CHECK(NormalizeCode(std::wstring(70, L'A')).empty());

    // links from a customer page
    CHECK(CodeFromUrl(L"addonstore://code/FINANCE") == L"FINANCE");
    CHECK(CodeFromUrl(L"addonstore://code/finance/") == L"FINANCE");
    CHECK(CodeFromUrl(L"ADDONSTORE://CODE/Finance?x=1") == L"FINANCE");
    CHECK(CodeFromUrl(L"addonstore:code/FINANCE#top") == L"FINANCE");
    CHECK(CodeFromUrl(L"addonstore://code/").empty());
    CHECK(CodeFromUrl(L"addonstore://code/FIN").empty());
    CHECK(CodeFromUrl(L"addonstore://code/FIN%41NCE").empty());
    CHECK(CodeFromUrl(L"addonstore://install/com.tungsten.smartbookmarks").empty());
    CHECK(CodeFromUrl(L"https://evil.example/code/FINANCE").empty());
    CHECK(CodeFromUrl(L"").empty());

    // the installer's name
    CHECK(CodeFromInstallerPath(L"C:\\Users\\a\\Downloads\\AddonStore-Code-FINANCE.msi") == L"FINANCE");
    CHECK(CodeFromInstallerPath(L"C:\\Users\\a\\Downloads\\AddonStore-Code-FINANCE (1).msi") == L"FINANCE");
    CHECK(CodeFromInstallerPath(L"C:\\Users\\a\\Downloads\\AddonStore-Code-FINANCE(12).msi") == L"FINANCE");
    CHECK(CodeFromInstallerPath(L"\\\\server\\share\\addonstore-code-finance2026.MSI") == L"FINANCE2026");
    CHECK(CodeFromInstallerPath(L"D:/x/AddonStore-Code-Finance-2026.msi") == L"FINANCE2026");
    CHECK(CodeFromInstallerPath(L"AddonStore-Code-FINANCE.msi") == L"FINANCE");
    CHECK(CodeFromInstallerPath(L"C:\\Users\\a\\Downloads\\AddonStore-2.4.0.msi").empty());
    CHECK(CodeFromInstallerPath(L"C:\\Users\\a\\Downloads\\pluginstore.msi").empty());
    CHECK(CodeFromInstallerPath(L"C:\\Users\\a\\Downloads\\AddonStore-Code-FIN.msi").empty());
    CHECK(CodeFromInstallerPath(L"C:\\Users\\a\\Downloads\\AddonStore-Code-FINANCE.msi.exe").empty());
    CHECK(CodeFromInstallerPath(L"C:\\Users\\a\\Downloads\\AddonStore-Code-FINANCE copy.msi").empty());
    CHECK(CodeFromInstallerPath(L"C:\\Users\\a\\Downloads\\AddonStore-Code-FINANCE ().msi").empty());
    CHECK(CodeFromInstallerPath(L"C:\\Users\\a\\Downloads\\AddonStore-Code-FINANCE (1.msi").empty());
    CHECK(CodeFromInstallerPath(L"C:\\AddonStore-Code-FINANCE.msi\\pluginstore.msi").empty());   // only the file name counts
    CHECK(CodeFromInstallerPath(L"C:\\Windows\\Temp\\{A1B2}\\AddonStore-2.4.1.msi").empty());     // self-update
    CHECK(CodeFromInstallerPath(L"").empty());

    // the HKCU request and the store window's preselect
    CHECK(PendingForCode(L"finance", false) == L"code:FINANCE");
    CHECK(PendingForCode(L"FINANCE", true) == L"dlcode:FINANCE");
    CHECK(PendingForCode(L"FIN", true).empty());
    CHECK(PreselectFromPending(L"code:FINANCE") == L"::code:FINANCE");
    CHECK(PreselectFromPending(L"dlcode:FINANCE") == L"::dlcode:FINANCE");
    CHECK(PreselectFromPending(L"code:FIN").empty());
    CHECK(PreselectFromPending(L"code:FINANCE;DROP").empty());
    CHECK(PreselectFromPending(L"com.tungsten.smartbookmarks").empty());
    CHECK(PreselectFromPending(L"::updates").empty());
    std::wstring code;
    bool dl = true;
    CHECK(OfferFromPreselect(L"::code:FINANCE", code, dl) && code == L"FINANCE" && !dl);
    CHECK(OfferFromPreselect(L"::dlcode:FINANCE", code, dl) && code == L"FINANCE" && dl);
    CHECK(!OfferFromPreselect(L"::updates", code, dl));
    CHECK(!OfferFromPreselect(L"com.tungsten.smartbookmarks", code, dl));
    CHECK(!OfferFromPreselect(L"::code:F", code, dl));

    // the same code, whatever the case and hyphens
    CHECK(SameCode(L"FINANCE", L"finance"));
    CHECK(SameCode(L"ABCD-EFGH-JKLM-NPQR-STUV", L"ABCDEFGHJKLMNPQRSTUV"));
    CHECK(!SameCode(L"FINANCE", L"FINANCES"));
    CHECK(!SameCode(L"", L""));

    std::printf("linkcode_tests: %d checks, %d failed\n", g_checks, g_failed);
    return g_failed == 0 ? 0 : 1;
}
