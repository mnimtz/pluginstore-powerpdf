// logging.cpp — see logging.h.

#include "stdafx.h"
#include "logging.h"
#include <stdio.h>

static bool g_verbose = false;

bool FPVerboseLog() { return g_verbose; }
void FPLogSetVerbose(bool on) { g_verbose = on; }

static void WriteLine(const wchar_t* rawLine)
{
    // one log entry = one line: CR, LF and other control characters from
    // server or catalog values are replaced, so no entry can be forged
    wchar_t line[2048];
    size_t i = 0;
    for (; i < 2047 && rawLine[i]; ++i)   // bound first, then the character (CodeQL)
        line[i] = (rawLine[i] < 0x20 || rawLine[i] == 0x7F) ? L'?' : rawLine[i];
    line[i] = 0;
    // client review Nov 2026: a TEMP path near or over MAX_PATH made wcscat_s call the invalid-parameter
    // handler (Power PDF ended on the first log line) or appended to an unfilled buffer; such a
    // TEMP now gets no log line instead
    const wchar_t kName[] = L"PluginStore.log";
    wchar_t path[1024];
    DWORD n = GetTempPathW(_countof(path), path);
    if (n == 0 || n + _countof(kName) > _countof(path)) return;
    wcscat_s(path, _countof(path), kName);
    FILE* f = NULL;
    if (_wfopen_s(&f, path, L"a, ccs=UTF-8") == 0 && f)
    {
        SYSTEMTIME st; GetLocalTime(&st);
        fwprintf(f, L"%04u-%02u-%02u %02u:%02u:%02u.%03u  %s\n",
                 st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute,
                 st.wSecond, st.wMilliseconds, line);
        fclose(f);
    }
}

void FPLogW(const wchar_t* fmt, ...)
{
    wchar_t line[2048];
    va_list ap;
    va_start(ap, fmt);
    _vsnwprintf_s(line, 2048, _TRUNCATE, fmt, ap);
    va_end(ap);
    WriteLine(line);
}

void FPLogA(const char* fmt, ...)
{
    char lineA[2048];
    va_list ap;
    va_start(ap, fmt);
    _vsnprintf_s(lineA, 2048, _TRUNCATE, fmt, ap);
    va_end(ap);
    wchar_t line[2048];
    MultiByteToWideChar(CP_ACP, 0, lineA, -1, line, 2048);
    WriteLine(line);
}

void FPVLogW(const wchar_t* fmt, ...)
{
    if (!g_verbose) return;
    wchar_t line[2048];
    va_list ap;
    va_start(ap, fmt);
    _vsnwprintf_s(line, 2048, _TRUNCATE, fmt, ap);
    va_end(ap);
    WriteLine(line);
}

void FPVLogA(const char* fmt, ...)
{
    if (!g_verbose) return;
    char lineA[2048];
    va_list ap;
    va_start(ap, fmt);
    _vsnprintf_s(lineA, 2048, _TRUNCATE, fmt, ap);
    va_end(ap);
    wchar_t line[2048];
    MultiByteToWideChar(CP_ACP, 0, lineA, -1, line, 2048);
    WriteLine(line);
}
