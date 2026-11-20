// layoutdir_tests.cpp - tests of the user ribbon layout folder (common/layoutpatch.h).
// Build and run: client\tests\run_tests.cmd (console program, no Power PDF needed).
// Client review Nov 2026: with APPDATA missing or longer than MAX_PATH the folder came out as ""
// or "\Kofax\...", and listing "" + "\*" walks the root of the current drive; now there is no
// folder and nothing is listed.

#include "../common/layoutpatch.h"
#include <cstdio>

static int g_failed = 0, g_checks = 0;
#define CHECK(cond) do { ++g_checks; if (!(cond)) { ++g_failed; std::printf("FAIL %s:%d  %s\n", __FILE__, __LINE__, #cond); } } while (0)

static bool EndsWith(const std::wstring& s, const std::wstring& tail)
{
    return s.size() >= tail.size() && s.compare(s.size() - tail.size(), tail.size(), tail) == 0;
}

int wmain(int, wchar_t** argv)
{
    // the normal case
    SetEnvironmentVariableW(L"APPDATA", L"C:\\Users\\Test\\AppData\\Roaming");
    CHECK(fplayout::UserLayoutDir() == L"C:\\Users\\Test\\AppData\\Roaming\\Kofax\\PDF\\PowerPDF\\UILayout");

    // a non-ASCII profile name stays intact
    SetEnvironmentVariableW(L"APPDATA", L"C:\\Users\\J\u00fcrgen \u0141\u00f3d\u017a\\AppData\\Roaming");
    CHECK(EndsWith(fplayout::UserLayoutDir(), L"J\u00fcrgen \u0141\u00f3d\u017a\\AppData\\Roaming\\Kofax\\PDF\\PowerPDF\\UILayout"));

    // APPDATA longer than MAX_PATH: no folder (used to be "\Kofax\PDF\PowerPDF\UILayout")
    std::wstring longer = L"C:\\" + std::wstring(300, L'r');
    SetEnvironmentVariableW(L"APPDATA", longer.c_str());
    CHECK(fplayout::UserLayoutDir().empty());

    // APPDATA missing: no folder
    SetEnvironmentVariableW(L"APPDATA", NULL);
    CHECK(fplayout::UserLayoutDir().empty());

    // no folder: nothing is listed (never the root of the current drive)
    std::vector<std::wstring> files;
    fplayout::CollectLayoutFiles(std::wstring(), files);
    CHECK(files.empty());

    // a real folder with one layout file and one other XML: only the layout file is collected
    std::wstring dir = argv[0];
    dir = dir.substr(0, dir.find_last_of(L"\\/")) + L"\\layouttmp";
    CreateDirectoryW(dir.c_str(), NULL);
    const char layout[] = "<?xml version=\"1.0\"?><Layout><Top></Top></Layout>";
    const char other[] = "<?xml version=\"1.0\"?><Other/>";
    for (int i = 0; i < 2; ++i)
    {
        HANDLE f = CreateFileW((dir + (i ? L"\\other.xml" : L"\\Publish.xml")).c_str(), GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, 0, NULL);
        DWORD w = 0;
        if (f != INVALID_HANDLE_VALUE) { WriteFile(f, i ? other : layout, (DWORD)(i ? sizeof(other) : sizeof(layout)) - 1, &w, NULL); CloseHandle(f); }
    }
    files.clear();
    fplayout::CollectLayoutFiles(dir, files);
    CHECK(files.size() == 1 && EndsWith(files[0], L"\\Publish.xml"));
    DeleteFileW((dir + L"\\Publish.xml").c_str());
    DeleteFileW((dir + L"\\other.xml").c_str());
    RemoveDirectoryW(dir.c_str());

    std::printf("%d checks, %d failed\n", g_checks, g_failed);
    return g_failed ? 1 : 0;
}
