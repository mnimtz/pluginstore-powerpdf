// linkcode.h — code words from the store website (C2.4.0). Pure functions, no Windows API:
// used by AddonStoreLink.exe, the client and tests\linkcode_tests.cpp.
//
// Two ways a code word such as FINANCE reaches the client:
//   1. the link addonstore://code/FINANCE on a customer page (www.power-pdf.de/finance);
//   2. the installer downloaded from that page as "AddonStore-Code-FINANCE.msi": the MSI writes its own path
//      (OriginalDatabase) to HKLM, the client reads the code word from the file name once per user.
// Either way the client only fills in the code dialog; the user adds the code with "Add" (nothing is added silently).
#pragma once
#include <string>

namespace pslink {

/// A code word as it may come from a link or a file name: letters A-Z, digits and hyphens, 5 to 32 letters and
/// digits, at least one letter. Returns it upper case without hyphens, or "" when it does not look like a code.
inline std::wstring NormalizeCode(const std::wstring& in)
{
    if (in.size() > 64) return std::wstring();
    std::wstring c;
    bool letter = false;
    for (wchar_t ch : in)
    {
        if (ch == L'-') continue;
        if (ch >= L'a' && ch <= L'z') ch = (wchar_t)(ch - L'a' + L'A');
        if (ch >= L'A' && ch <= L'Z') letter = true;
        else if (!(ch >= L'0' && ch <= L'9')) return std::wstring();
        c += ch;
    }
    return letter && c.size() >= 5 && c.size() <= 32 ? c : std::wstring();
}

inline bool StartsWithNoCase(const std::wstring& s, const wchar_t* prefix)
{
    size_t i = 0;
    for (; prefix[i]; ++i)
    {
        if (i >= s.size()) return false;
        wchar_t a = s[i], b = prefix[i];
        if (a >= L'A' && a <= L'Z') a = (wchar_t)(a - L'A' + L'a');
        if (b >= L'A' && b <= L'Z') b = (wchar_t)(b - L'A' + L'a');
        if (a != b) return false;
    }
    return true;
}

/// "addonstore://code/FINANCE" (or "addonstore:code/FINANCE", optional trailing slash, query or fragment) -> "FINANCE".
inline std::wstring CodeFromUrl(const std::wstring& url)
{
    for (const wchar_t* prefix : { L"addonstore://code/", L"addonstore:code/" })
    {
        if (!StartsWithNoCase(url, prefix)) continue;
        std::wstring rest = url.substr(std::char_traits<wchar_t>::length(prefix));
        size_t cut = rest.find_first_of(L"/?#");
        if (cut != std::wstring::npos) rest.resize(cut);
        return NormalizeCode(rest);
    }
    return std::wstring();
}

/// Path of the MSI the store client was installed from -> the code word in its name, or "".
/// "C:\Users\a\Downloads\AddonStore-Code-FINANCE.msi" -> "FINANCE"; the browser's copy suffix " (1)" is fine.
/// Any other name (AddonStore-2.4.0.msi, a renamed file, a deployment share) gives "".
inline std::wstring CodeFromInstallerPath(const std::wstring& path)
{
    size_t slash = path.find_last_of(L"\\/");
    std::wstring name = slash == std::wstring::npos ? path : path.substr(slash + 1);
    const wchar_t* prefix = L"addonstore-code-";
    if (!StartsWithNoCase(name, prefix)) return std::wstring();
    size_t i = std::char_traits<wchar_t>::length(prefix), start = i;
    while (i < name.size() && ((name[i] >= L'A' && name[i] <= L'Z') || (name[i] >= L'a' && name[i] <= L'z') ||
                               (name[i] >= L'0' && name[i] <= L'9') || name[i] == L'-'))
        ++i;
    std::wstring code = name.substr(start, i - start), rest = name.substr(i);
    // " (1)" or "(12)" from the browser, then ".msi" and nothing else
    size_t j = 0;
    if (j < rest.size() && rest[j] == L' ') ++j;
    if (j < rest.size() && rest[j] == L'(')
    {
        size_t k = j + 1;
        while (k < rest.size() && rest[k] >= L'0' && rest[k] <= L'9') ++k;
        if (k == j + 1 || k >= rest.size() || rest[k] != L')') return std::wstring();
        j = k + 1;
    }
    else if (j > 0) return std::wstring();
    std::wstring ext = rest.substr(j);
    if (!(ext.size() == 4 && StartsWithNoCase(ext, L".msi"))) return std::wstring();
    return NormalizeCode(code);
}

/// The value AddonStoreLink.exe / the startup check leave under HKCU PendingOpen for a code word:
/// "code:FINANCE" (from a link) or "dlcode:FINANCE" (from the installer's name).
inline std::wstring PendingForCode(const std::wstring& code, bool fromDownload)
{
    std::wstring c = NormalizeCode(code);
    return c.empty() ? std::wstring() : (fromDownload ? L"dlcode:" : L"code:") + c;
}

/// PendingOpen value -> preselect of the store window ("::code:FINANCE", "::dlcode:FINANCE"), or "".
inline std::wstring PreselectFromPending(const std::wstring& v)
{
    for (const wchar_t* prefix : { L"code:", L"dlcode:" })
    {
        if (v.compare(0, std::char_traits<wchar_t>::length(prefix), prefix) != 0) continue;
        std::wstring c = NormalizeCode(v.substr(std::char_traits<wchar_t>::length(prefix)));
        return c.empty() ? std::wstring() : L"::" + std::wstring(prefix) + c;
    }
    return std::wstring();
}

/// Preselect of the store window -> the offered code word and where it came from.
inline bool OfferFromPreselect(const std::wstring& p, std::wstring& code, bool& fromDownload)
{
    for (const wchar_t* prefix : { L"::code:", L"::dlcode:" })
    {
        size_t n = std::char_traits<wchar_t>::length(prefix);
        if (p.compare(0, n, prefix) != 0) continue;
        code = NormalizeCode(p.substr(n));
        fromDownload = prefix[2] == L'd';
        return !code.empty();
    }
    return false;
}

/// Same code word, ignoring case and hyphens (the client keeps generated codes with their hyphens).
inline bool SameCode(const std::wstring& a, const std::wstring& b)
{
    std::wstring x, y;
    for (wchar_t ch : a) if (ch != L'-' && ch != L' ') x += (ch >= L'a' && ch <= L'z') ? (wchar_t)(ch - L'a' + L'A') : ch;
    for (wchar_t ch : b) if (ch != L'-' && ch != L' ') y += (ch >= L'a' && ch <= L'z') ? (wchar_t)(ch - L'a' + L'A') : ch;
    return !x.empty() && x == y;
}

} // namespace pslink
