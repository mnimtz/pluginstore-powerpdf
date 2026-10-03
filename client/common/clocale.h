// clocale.h — the ONE C-locale for every number that lands in PDF syntax.
//
// PDF requires '.' as the decimal separator. printf follows the CRT locale,
// and the host process may call setlocale(LC_ALL, "German") on the shared
// CRT at any time - on such machines every plain sprintf("%f") writes
// "3,00", which breaks the content stream SILENTLY and completely (a signed
// stamp rendered empty on a colleague's machine, Aug 31, 2026). Every
// number formatted into PDF operators MUST therefore go through the _l
// printf variants with this locale.

#pragma once
#include <locale.h>
#include <stdarg.h>
#include <stdio.h>

inline _locale_t FPCLocale()
{
    static _locale_t loc = _create_locale(LC_ALL, "C");
    return loc;
}

// sprintf for PDF syntax: pinned C locale, and a belt-and-suspenders pass
// that turns any decimal COMMA the CRT still produced (locale handle NULL,
// exotic CRT states) back into a point. Safe because PDF operator templates
// never contain a comma of their own - only numbers and names do.
inline int FPFmtA(char* buf, size_t size, const char* fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    int n = _vsprintf_s_l(buf, size, fmt, FPCLocale(), ap);
    va_end(ap);
    for (char* p = buf; *p; ++p)
        if (*p == ',') *p = '.';
    return n;
}
