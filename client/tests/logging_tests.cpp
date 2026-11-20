// logging_tests.cpp - tests of common/logging.cpp (the one log file %TEMP%\PluginStore.log).
// Build and run: client\tests\run_tests.cmd (console program, no Power PDF needed).
// Client review Nov 2026: a TEMP path near or over MAX_PATH used to end the process on the
// first log line (wcscat_s on a full buffer); such a TEMP now simply gets no log line.

#include "stdafx.h"
#include "logging.h"
#include <cstdio>
#include <fstream>
#include <sstream>

static int g_failed = 0, g_checks = 0;
#define CHECK(cond) do { ++g_checks; if (!(cond)) { ++g_failed; std::printf("FAIL %s:%d  %s\n", __FILE__, __LINE__, #cond); } } while (0)

static std::wstring Dir(const wchar_t* argv0)
{
    std::wstring p = argv0;
    size_t s = p.find_last_of(L"\\/");
    return s == std::wstring::npos ? L"." : p.substr(0, s);
}

static std::string ReadAll(const std::wstring& path)
{
    std::ifstream f(path, std::ios::binary);
    std::stringstream ss;
    ss << f.rdbuf();
    return ss.str();
}

int wmain(int, wchar_t** argv)
{
    // 1) normal TEMP: the line arrives, control characters are neutralized (no forged entries)
    std::wstring dir = Dir(argv[0]) + L"\\logtmp";
    CreateDirectoryW(dir.c_str(), NULL);
    std::wstring log = dir + L"\\PluginStore.log";
    DeleteFileW(log.c_str());
    SetEnvironmentVariableW(L"TMP", dir.c_str());
    SetEnvironmentVariableW(L"TEMP", dir.c_str());
    FPLogW(L"[Test] first\r\n2026-01-01 00:00:00.000  [Store] forged %d", 7);
    std::string text = ReadAll(log);
    CHECK(text.find("[Test] first??2026-01-01") != std::string::npos);
    CHECK(text.find("forged 7") != std::string::npos);
    CHECK(text.find('\r') == std::string::npos || text.find("first\r") == std::string::npos);

    // 2) TEMP of 250 characters: GetTempPath succeeds, but "PluginStore.log" no longer fits in MAX_PATH
    std::wstring longDir = L"C:\\" + std::wstring(246, L'a');
    SetEnvironmentVariableW(L"TMP", longDir.c_str());
    SetEnvironmentVariableW(L"TEMP", longDir.c_str());
    FPLogW(L"[Test] long temp %s", L"x");   // used to end the process here
    CHECK(true);

    // 3) TEMP longer than MAX_PATH: GetTempPath reports the needed size and fills nothing
    std::wstring longerDir = L"C:\\" + std::wstring(400, L'b');
    SetEnvironmentVariableW(L"TMP", longerDir.c_str());
    SetEnvironmentVariableW(L"TEMP", longerDir.c_str());
    FPLogA("[Test] longer temp %d", 3);
    CHECK(true);

    // 4) back to the normal folder: logging still works
    SetEnvironmentVariableW(L"TMP", dir.c_str());
    SetEnvironmentVariableW(L"TEMP", dir.c_str());
    FPLogW(L"[Test] after");
    CHECK(ReadAll(log).find("[Test] after") != std::string::npos);
    DeleteFileW(log.c_str());
    RemoveDirectoryW(dir.c_str());

    std::printf("%d checks, %d failed\n", g_checks, g_failed);
    return g_failed ? 1 : 0;
}
