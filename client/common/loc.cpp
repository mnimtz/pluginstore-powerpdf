// loc.cpp - pack-wide EFIGS localization, see loc.h.
//
// Strings are stored as RT_STRING resources in per-language blocks inside
// this module. Windows stores string resources in bundles of 16 entries
// (bundle id = stringId/16 + 1, each entry: WORD length + WCHARs), which is
// what the parser below walks. FindResourceExW selects the language block
// explicitly, so the plugin follows Power PDF's UI language - independent of
// the OS locale.

#include "stdafx.h"
#include "loc.h"

extern "C" HINSTANCE gHINSTANCE;

static LANGID g_lang = MAKELANGID(LANG_ENGLISH, SUBLANG_ENGLISH_US);
static const LANGID kFallback = MAKELANGID(LANG_ENGLISH, SUBLANG_ENGLISH_US);

// ---------------------------------------------------------------------------
// FPLocInit - map Power PDF's language code to the resource LANGID.
// Handles 3-letter (DEU/GER/FRA/ITA/ESP/ENU), 2-letter (de/fr/it/es/en),
// BCP-47 (de-DE), English names, and decimal/hex Windows LCIDs.
// ---------------------------------------------------------------------------
static LANGID MapLangCode(const char* raw)
{
    char c[64] = { 0 };
    strncpy_s(c, raw ? raw : "", _TRUNCATE);
    for (char* p = c; *p; ++p) *p = (char)toupper((unsigned char)*p);

    // numeric LCID? (e.g. "1031", "0x0407")
    if (c[0] >= '0' && c[0] <= '9')
    {
        long v = strtol(c, NULL, (c[1] == 'X') ? 16 : 10);
        switch (PRIMARYLANGID((LANGID)v))
        {
        case LANG_GERMAN:     return MAKELANGID(LANG_GERMAN,     SUBLANG_GERMAN);
        case LANG_FRENCH:     return MAKELANGID(LANG_FRENCH,     SUBLANG_FRENCH);
        case LANG_ITALIAN:    return MAKELANGID(LANG_ITALIAN,    SUBLANG_ITALIAN);
        case LANG_SPANISH:    return MAKELANGID(LANG_SPANISH,    SUBLANG_SPANISH_MODERN);
        case LANG_DUTCH:      return MAKELANGID(LANG_DUTCH,      SUBLANG_DUTCH);
        case LANG_PORTUGUESE: return MAKELANGID(LANG_PORTUGUESE, SUBLANG_PORTUGUESE_BRAZILIAN);
        case LANG_DANISH:     return MAKELANGID(LANG_DANISH,     SUBLANG_DANISH_DENMARK);
        case LANG_FINNISH:    return MAKELANGID(LANG_FINNISH,    SUBLANG_FINNISH_FINLAND);
        case LANG_NORWEGIAN:  return MAKELANGID(LANG_NORWEGIAN,  SUBLANG_NORWEGIAN_BOKMAL);
        case LANG_SWEDISH:    return MAKELANGID(LANG_SWEDISH,    SUBLANG_SWEDISH);
        case LANG_POLISH:     return MAKELANGID(LANG_POLISH,     SUBLANG_POLISH_POLAND);
        case LANG_CZECH:      return MAKELANGID(LANG_CZECH,      SUBLANG_CZECH_CZECH_REPUBLIC);
        case LANG_HUNGARIAN:  return MAKELANGID(LANG_HUNGARIAN,  SUBLANG_HUNGARIAN_HUNGARY);
        case LANG_RUSSIAN:    return MAKELANGID(LANG_RUSSIAN,    SUBLANG_RUSSIAN_RUSSIA);
        case LANG_TURKISH:    return MAKELANGID(LANG_TURKISH,    SUBLANG_TURKISH_TURKEY);
        // the five further Power PDF languages (C1.3.0)
        case LANG_CHINESE:
        {
            WORD sub = SUBLANGID((LANGID)v);
            return sub == SUBLANG_CHINESE_TRADITIONAL || sub == SUBLANG_CHINESE_HONGKONG || sub == SUBLANG_CHINESE_MACAU
                ? MAKELANGID(LANG_CHINESE, SUBLANG_CHINESE_TRADITIONAL) : MAKELANGID(LANG_CHINESE, SUBLANG_CHINESE_SIMPLIFIED);
        }
        case LANG_JAPANESE:   return MAKELANGID(LANG_JAPANESE,   SUBLANG_JAPANESE_JAPAN);
        case LANG_KOREAN:     return MAKELANGID(LANG_KOREAN,     SUBLANG_KOREAN);
        case LANG_ARABIC:     return MAKELANGID(LANG_ARABIC,     SUBLANG_ARABIC_SAUDI_ARABIA);
        default:              return kFallback;
        }
    }

    struct { const char* key; LANGID id; } table[] = {
        // the five further Power PDF languages (C1.3.0); Traditional before the bare "ZH"
        { "CHT", MAKELANGID(LANG_CHINESE, SUBLANG_CHINESE_TRADITIONAL) },
        { "ZH-TW", MAKELANGID(LANG_CHINESE, SUBLANG_CHINESE_TRADITIONAL) },
        { "ZH-HK", MAKELANGID(LANG_CHINESE, SUBLANG_CHINESE_TRADITIONAL) },
        { "ZH-MO", MAKELANGID(LANG_CHINESE, SUBLANG_CHINESE_TRADITIONAL) },
        { "ZH-HANT", MAKELANGID(LANG_CHINESE, SUBLANG_CHINESE_TRADITIONAL) },
        { "CHS", MAKELANGID(LANG_CHINESE, SUBLANG_CHINESE_SIMPLIFIED) },
        { "ZH", MAKELANGID(LANG_CHINESE, SUBLANG_CHINESE_SIMPLIFIED) },
        { "CHINESE", MAKELANGID(LANG_CHINESE, SUBLANG_CHINESE_SIMPLIFIED) },
        { "JPN", MAKELANGID(LANG_JAPANESE, SUBLANG_JAPANESE_JAPAN) },
        { "JA", MAKELANGID(LANG_JAPANESE, SUBLANG_JAPANESE_JAPAN) },
        { "JAPANESE", MAKELANGID(LANG_JAPANESE, SUBLANG_JAPANESE_JAPAN) },
        { "KOR", MAKELANGID(LANG_KOREAN, SUBLANG_KOREAN) },
        { "KO", MAKELANGID(LANG_KOREAN, SUBLANG_KOREAN) },
        { "KOREAN", MAKELANGID(LANG_KOREAN, SUBLANG_KOREAN) },
        { "ARA", MAKELANGID(LANG_ARABIC, SUBLANG_ARABIC_SAUDI_ARABIA) },
        { "AR", MAKELANGID(LANG_ARABIC, SUBLANG_ARABIC_SAUDI_ARABIA) },
        { "ARABIC", MAKELANGID(LANG_ARABIC, SUBLANG_ARABIC_SAUDI_ARABIA) },
        { "DEU", MAKELANGID(LANG_GERMAN,  SUBLANG_GERMAN) },
        { "GER", MAKELANGID(LANG_GERMAN,  SUBLANG_GERMAN) },
        { "DE",  MAKELANGID(LANG_GERMAN,  SUBLANG_GERMAN) },
        { "GERMAN", MAKELANGID(LANG_GERMAN, SUBLANG_GERMAN) },
        { "FRA", MAKELANGID(LANG_FRENCH,  SUBLANG_FRENCH) },
        { "FRE", MAKELANGID(LANG_FRENCH,  SUBLANG_FRENCH) },
        { "FR",  MAKELANGID(LANG_FRENCH,  SUBLANG_FRENCH) },
        { "FRENCH", MAKELANGID(LANG_FRENCH, SUBLANG_FRENCH) },
        { "ITA", MAKELANGID(LANG_ITALIAN, SUBLANG_ITALIAN) },
        { "IT",  MAKELANGID(LANG_ITALIAN, SUBLANG_ITALIAN) },
        { "ITALIAN", MAKELANGID(LANG_ITALIAN, SUBLANG_ITALIAN) },
        { "ESP", MAKELANGID(LANG_SPANISH, SUBLANG_SPANISH_MODERN) },
        { "SPA", MAKELANGID(LANG_SPANISH, SUBLANG_SPANISH_MODERN) },
        { "ES",  MAKELANGID(LANG_SPANISH, SUBLANG_SPANISH_MODERN) },
        { "SPANISH", MAKELANGID(LANG_SPANISH, SUBLANG_SPANISH_MODERN) },
        { "NLD", MAKELANGID(LANG_DUTCH, SUBLANG_DUTCH) },
        { "DUT", MAKELANGID(LANG_DUTCH, SUBLANG_DUTCH) },
        { "NL",  MAKELANGID(LANG_DUTCH, SUBLANG_DUTCH) },
        { "DUTCH", MAKELANGID(LANG_DUTCH, SUBLANG_DUTCH) },
        { "PTB", MAKELANGID(LANG_PORTUGUESE, SUBLANG_PORTUGUESE_BRAZILIAN) },
        { "POR", MAKELANGID(LANG_PORTUGUESE, SUBLANG_PORTUGUESE_BRAZILIAN) },
        { "PT",  MAKELANGID(LANG_PORTUGUESE, SUBLANG_PORTUGUESE_BRAZILIAN) },
        { "PORTUGUESE", MAKELANGID(LANG_PORTUGUESE, SUBLANG_PORTUGUESE_BRAZILIAN) },
        { "DAN", MAKELANGID(LANG_DANISH, SUBLANG_DANISH_DENMARK) },
        { "DA",  MAKELANGID(LANG_DANISH, SUBLANG_DANISH_DENMARK) },
        { "DANISH", MAKELANGID(LANG_DANISH, SUBLANG_DANISH_DENMARK) },
        { "FIN", MAKELANGID(LANG_FINNISH, SUBLANG_FINNISH_FINLAND) },
        { "FI",  MAKELANGID(LANG_FINNISH, SUBLANG_FINNISH_FINLAND) },
        { "FINNISH", MAKELANGID(LANG_FINNISH, SUBLANG_FINNISH_FINLAND) },
        { "NOR", MAKELANGID(LANG_NORWEGIAN, SUBLANG_NORWEGIAN_BOKMAL) },
        { "NB",  MAKELANGID(LANG_NORWEGIAN, SUBLANG_NORWEGIAN_BOKMAL) },
        { "NO",  MAKELANGID(LANG_NORWEGIAN, SUBLANG_NORWEGIAN_BOKMAL) },
        { "NORWEGIAN", MAKELANGID(LANG_NORWEGIAN, SUBLANG_NORWEGIAN_BOKMAL) },
        { "SVE", MAKELANGID(LANG_SWEDISH, SUBLANG_SWEDISH) },
        { "SWE", MAKELANGID(LANG_SWEDISH, SUBLANG_SWEDISH) },
        { "SV",  MAKELANGID(LANG_SWEDISH, SUBLANG_SWEDISH) },
        { "SWEDISH", MAKELANGID(LANG_SWEDISH, SUBLANG_SWEDISH) },
        { "PLK", MAKELANGID(LANG_POLISH, SUBLANG_POLISH_POLAND) },
        { "POL", MAKELANGID(LANG_POLISH, SUBLANG_POLISH_POLAND) },
        { "PL",  MAKELANGID(LANG_POLISH, SUBLANG_POLISH_POLAND) },
        { "POLISH", MAKELANGID(LANG_POLISH, SUBLANG_POLISH_POLAND) },
        { "CSY", MAKELANGID(LANG_CZECH, SUBLANG_CZECH_CZECH_REPUBLIC) },
        { "CZE", MAKELANGID(LANG_CZECH, SUBLANG_CZECH_CZECH_REPUBLIC) },
        { "CS",  MAKELANGID(LANG_CZECH, SUBLANG_CZECH_CZECH_REPUBLIC) },
        { "CZECH", MAKELANGID(LANG_CZECH, SUBLANG_CZECH_CZECH_REPUBLIC) },
        { "HUN", MAKELANGID(LANG_HUNGARIAN, SUBLANG_HUNGARIAN_HUNGARY) },
        { "HU",  MAKELANGID(LANG_HUNGARIAN, SUBLANG_HUNGARIAN_HUNGARY) },
        { "HUNGARIAN", MAKELANGID(LANG_HUNGARIAN, SUBLANG_HUNGARIAN_HUNGARY) },
        { "RUS", MAKELANGID(LANG_RUSSIAN, SUBLANG_RUSSIAN_RUSSIA) },
        { "RU",  MAKELANGID(LANG_RUSSIAN, SUBLANG_RUSSIAN_RUSSIA) },
        { "RUSSIAN", MAKELANGID(LANG_RUSSIAN, SUBLANG_RUSSIAN_RUSSIA) },
        { "TRK", MAKELANGID(LANG_TURKISH, SUBLANG_TURKISH_TURKEY) },
        { "TUR", MAKELANGID(LANG_TURKISH, SUBLANG_TURKISH_TURKEY) },
        { "TR",  MAKELANGID(LANG_TURKISH, SUBLANG_TURKISH_TURKEY) },
        { "TURKISH", MAKELANGID(LANG_TURKISH, SUBLANG_TURKISH_TURKEY) },
    };
    for (auto& e : table)
    {
        size_t n = strlen(e.key);
        if (_strnicmp(c, e.key, n) == 0 &&
            (c[n] == 0 || c[n] == '-' || c[n] == '_'))
            return e.id;
    }
    return kFallback;   // ENU / EN / English / anything else
}

void FPLocInit()
{
    char code[64] = { 0 };
    bool gotCode = false;
    DURING
        DVAppGetLanguage(code);
        gotCode = true;
    HANDLER END_HANDLER

    // If Power PDF gave us nothing (can happen very early at startup), fall
    // back to the Windows UI language so we still localize sensibly.
    if (!gotCode || code[0] == 0)
    {
        LANGID win = GetThreadUILanguage();
        if (!win) win = GetUserDefaultUILanguage();
        _snprintf_s(code, sizeof(code), _TRUNCATE, "0x%04X", win);
    }

    g_lang = MapLangCode(code);

    // Diagnostic breadcrumb: record what Power PDF reported and what we chose,
    // so a wrong mapping can be diagnosed from the registry without a debugger.
    HKEY hk;
    if (RegCreateKeyExW(HKEY_CURRENT_USER,
            L"Software\\Kofax\\PDF\\Tungsten Power PDF\\PluginStore",
            0, NULL, 0, KEY_SET_VALUE, NULL, &hk, NULL) == ERROR_SUCCESS)
    {
        wchar_t wcode[64] = { 0 };
        MultiByteToWideChar(CP_ACP, 0, code, -1, wcode, 64);
        RegSetValueExW(hk, L"_DetectedLangRaw", 0, REG_SZ,
                       (const BYTE*)wcode, (DWORD)((wcslen(wcode) + 1) * sizeof(wchar_t)));
        DWORD resolved = g_lang;
        RegSetValueExW(hk, L"_ResolvedLangId", 0, REG_DWORD,
                       (const BYTE*)&resolved, sizeof(resolved));
        RegCloseKey(hk);
    }
}

// ---------------------------------------------------------------------------
// String lookup with explicit language + English fallback
// ---------------------------------------------------------------------------
static std::wstring LoadFromBundle(LANGID lang, UINT id)
{
    HRSRC hRes = FindResourceExW(gHINSTANCE, RT_STRING,
                                 MAKEINTRESOURCEW(id / 16 + 1), lang);
    if (!hRes) return L"";
    HGLOBAL hMem = LoadResource(gHINSTANCE, hRes);
    if (!hMem) return L"";
    const wchar_t* p = (const wchar_t*)LockResource(hMem);
    if (!p) return L"";
    // walk the 16 length-prefixed entries up to our slot
    for (UINT slot = 0; slot < (id & 15); ++slot)
        p += 1 + *p;
    UINT len = *p;
    return std::wstring(p + 1, len);
}

LANGID FPLocLangId() { return g_lang; }

bool FPLocIsRtl() { return PRIMARYLANGID(g_lang) == LANG_ARABIC; }

int FPMessageBox(HWND owner, LPCWSTR text, LPCWSTR caption, UINT type)
{
    return ::MessageBoxW(owner, text, caption, type | (FPLocIsRtl() ? MB_RTLREADING | MB_RIGHT : 0));
}

std::wstring FPLoc(UINT id)
{
    std::wstring s = LoadFromBundle(g_lang, id);
    if (s.empty() && g_lang != kFallback)
        s = LoadFromBundle(kFallback, id);
    return s;
}

// ---------------------------------------------------------------------------
// Alert helpers (Unicode-safe via DUText)
// ---------------------------------------------------------------------------
void FPNote(UINT idMsg)
{
    std::wstring msg = FPLoc(idMsg);
    RVAlertNoteWithUnicode(reinterpret_cast<const DUUTF16Val*>(msg.c_str()));
}

void FPNoteText(const std::wstring& msg)
{
    RVAlertNoteWithUnicode(reinterpret_cast<const DUUTF16Val*>(msg.c_str()));
}

bool FPConfirmText(const std::wstring& msg)
{
    DUText msgTxt = DUTextFromUnicode(
        (const DUUTF16Val*)msg.c_str(), kUTF16HostEndian);

    DVAlertParamsRec ap;
    memset(&ap, 0, sizeof(ap));
    ap.size      = sizeof(DVAlertParamsRec);
    ap.iconType  = ALERT_NOTE;
    ap.message   = msgTxt;
    ap.alertType = kDVAlertTypeYesNo;
    DUInt32 btn = DVAlertWithParams(&ap);
    DUTextDestroy(msgTxt);
    return btn == 1;    // button 1 = Yes
}
