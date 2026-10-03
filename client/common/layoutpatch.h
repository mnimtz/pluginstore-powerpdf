// layoutpatch.h — our entries in Power PDF's UI layout.
//
// WHICH layout matters. There are two:
//
//   %APPDATA%\Kofax\PDF\PowerPDF\UILayout\Publish.xml     the live one
//   <PowerPDF root>\Resource\PowerPDF\UILayout\*.xml      the template
//
// Power PDF merges the template plus every plug-in's own layout into the user
// file ONCE and then runs from that file. Changing the template afterwards
// does nothing for an existing profile. The live file is in the user's own
// profile, so writing it needs no administrator rights at all.
//
// What v2 writes: IconMode="4" on our ribbon buttons — 4 is "large button,
// large icon" in the merged file (2 = row button, 1 = large button but SMALL
// icon, and no attribute at all is not the same as 4 once merged).
//
// v2 has no navigation panels (yet), so the whole panel machinery from v1 is
// gone. ApplyButtons() runs at every start and repairs installations that
// were merged before the buttons existed.
//
// Header-only and plain Win32 on purpose: no SDK dependencies.

#pragma once
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string>
#include <vector>

namespace fplayout {

// Every FP2 ribbon button that must render large. The toolbar itself is the
// SHARED "FeaturePack" tab (one tab for v1 and v2). Buttons are organised into
// their own groups; the group/button atoms are v2-only and never collide with
// v1's. Canonical order per group.
static const wchar_t* const kStoreButtons[] = {
    L"FeaturePack::PluginStore::Open",
};

struct GroupDef { const wchar_t* name; const wchar_t* const* buttons; int n; };
static const GroupDef kGroups[] = {
    { L"FeaturePack::PluginStore", kStoreButtons, (int)(sizeof(kStoreButtons) / sizeof(kStoreButtons[0])) },
};
static const int kGroupCount = (int)(sizeof(kGroups) / sizeof(kGroups[0]));

// --- file helpers ----------------------------------------------------------
// The layout files are UTF-16LE with a BOM; keep whatever we found so the file
// comes back byte-identical apart from our change.
inline bool ReadTextFile(const std::wstring& path, std::wstring& out, bool& isUtf16)
{
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, NULL,
                           OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return false;

    LARGE_INTEGER size = { 0 };
    if (!GetFileSizeEx(h, &size) || size.QuadPart > 8 * 1024 * 1024)
    { CloseHandle(h); return false; }

    std::vector<BYTE> raw((size_t)size.QuadPart);
    DWORD got = 0;
    BOOL ok = raw.empty() ? TRUE : ReadFile(h, &raw[0], (DWORD)raw.size(), &got, NULL);
    CloseHandle(h);
    if (!ok || got != raw.size()) return false;

    isUtf16 = (raw.size() >= 2 && raw[0] == 0xFF && raw[1] == 0xFE);
    if (isUtf16)
    {
        const wchar_t* p = (const wchar_t*)(raw.empty() ? NULL : &raw[0]);
        out.assign(p + 1, (raw.size() / sizeof(wchar_t)) - 1);   // skip the BOM
        return true;
    }

    size_t skip = (raw.size() >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF) ? 3 : 0;
    int need = MultiByteToWideChar(CP_UTF8, 0, (const char*)&raw[skip],
                                   (int)(raw.size() - skip), NULL, 0);
    if (need <= 0) return false;
    out.resize((size_t)need);
    MultiByteToWideChar(CP_UTF8, 0, (const char*)&raw[skip],
                        (int)(raw.size() - skip), &out[0], need);
    return true;
}

inline bool WriteTextFile(const std::wstring& path, const std::wstring& text,
                          bool isUtf16, DWORD* err)
{
    std::vector<BYTE> raw;
    if (isUtf16)
    {
        raw.push_back(0xFF); raw.push_back(0xFE);
        const BYTE* p = (const BYTE*)text.c_str();
        raw.insert(raw.end(), p, p + text.size() * sizeof(wchar_t));
    }
    else
    {
        int need = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), (int)text.size(),
                                       NULL, 0, NULL, NULL);
        raw.resize((size_t)(need > 0 ? need : 0));
        if (need > 0)
            WideCharToMultiByte(CP_UTF8, 0, text.c_str(), (int)text.size(),
                                (char*)&raw[0], need, NULL, NULL);
    }

    HANDLE h = CreateFileW(path.c_str(), GENERIC_WRITE, 0, NULL,
                           CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) { if (err) *err = GetLastError(); return false; }

    DWORD written = 0;
    BOOL ok = raw.empty() ? TRUE : WriteFile(h, &raw[0], (DWORD)raw.size(), &written, NULL);
    if (!ok || written != raw.size()) { if (err) *err = GetLastError(); CloseHandle(h); return false; }
    CloseHandle(h);
    return true;
}

// --- the patch itself ------------------------------------------------------
// The host merges a plug-in's own layout into the user file only when it does
// not already know the toolbar (or when the plug-in layout version is higher
// than what it cached). A profile that carried v1's "FeaturePack" toolbar
// therefore never receives our group from the merge — so we insert it
// ourselves, directly into the merged file. Returns true when text changed.
// Insert a group (with its buttons) into the merged toolbar if it is not there
// yet. Groups are appended in kGroups order, each before </toolbar>, so calling
// this for WordPro then Barcode yields WordPro first, Barcode second.
inline bool EnsureGroup(std::wstring& text, const GroupDef& g)
{
    std::wstring tag = std::wstring(L"\"") + g.name + L"\"";
    if (text.find(tag) != std::wstring::npos) return false;   // group already present

    size_t tb = text.find(L"<toolbar name=\"FeaturePack\"");
    if (tb == std::wstring::npos) return false;
    size_t end = text.find(L"</toolbar>", tb);
    if (end == std::wstring::npos) return false;

    std::wstring block = std::wstring(L"<PFFGroup name=\"") + g.name + L"\" GroupType=\"PFFTitleBlock\">\n";
    for (int i = 0; i < g.n; ++i)
        block += std::wstring(L"<PFFButton name=\"") + g.buttons[i] + L"\" IconMode=\"4\"/>\n";
    block += L"</PFFGroup>\n";
    text.insert(end, block);
    return true;
}

// Rewrite a group's button list into its canonical order. Each FP2 group is
// entirely ours, so re-emitting exactly our buttons is safe — this also drops a
// button that used to live here but has since moved to another group.
inline bool EnforceButtonOrder(std::wstring& text, const GroupDef& g)
{
    std::wstring grpTag = std::wstring(L"<PFFGroup name=\"") + g.name + L"\"";
    size_t grp = text.find(grpTag);
    if (grp == std::wstring::npos) return false;
    size_t open = text.find(L'>', grp);
    size_t close = text.find(L"</PFFGroup>", grp);
    if (open == std::wstring::npos || close == std::wstring::npos || open > close) return false;

    std::wstring want = L"\n";
    for (int i = 0; i < g.n; ++i)
        want += std::wstring(L"<PFFButton name=\"") + g.buttons[i] + L"\" IconMode=\"4\"/>\n";

    std::wstring cur = text.substr(open + 1, close - (open + 1));
    if (cur == want) return false;
    text.replace(open + 1, close - (open + 1), want);
    return true;
}

// Insert any button from the group that is missing, just before </PFFGroup>.
inline bool EnsureButtons(std::wstring& text, const GroupDef& g)
{
    std::wstring grpTag = std::wstring(L"<PFFGroup name=\"") + g.name + L"\"";
    size_t grp = text.find(grpTag);
    if (grp == std::wstring::npos) return false;
    size_t grpEnd = text.find(L"</PFFGroup>", grp);
    if (grpEnd == std::wstring::npos) return false;

    bool changed = false;
    for (int i = 0; i < g.n; ++i)
    {
        std::wstring needle = std::wstring(L"<PFFButton name=\"") + g.buttons[i] + L"\"";
        if (text.find(needle, grp) < grpEnd) continue;   // already present in group
        std::wstring ins = needle + L" IconMode=\"4\"/>\n";
        text.insert(grpEnd, ins);
        grpEnd += ins.size();
        changed = true;
    }
    return changed;
}

// Ribbon governance: the Plugin-Store group is always the LAST group on the
// shared tab. Other plug-ins insert their groups before </toolbar>, so after
// every install we move ours back to the end. Returns true when moved.
inline bool MoveGroupToEnd(std::wstring& text, const GroupDef& g)
{
    size_t tb = text.find(L"<toolbar name=\"FeaturePack\"");
    if (tb == std::wstring::npos) return false;
    size_t tbEnd = text.find(L"</toolbar>", tb);
    if (tbEnd == std::wstring::npos) return false;

    std::wstring grpTag = std::wstring(L"<PFFGroup name=\"") + g.name + L"\"";
    size_t grp = text.find(grpTag, tb);
    if (grp == std::wstring::npos || grp > tbEnd) return false;
    size_t grpEnd = text.find(L"</PFFGroup>", grp);
    if (grpEnd == std::wstring::npos || grpEnd > tbEnd) return false;
    grpEnd += wcslen(L"</PFFGroup>");

    // Already last? Only whitespace between our group and </toolbar>.
    bool last = true;
    for (size_t i = grpEnd; i < tbEnd; ++i)
        if (!iswspace(text[i])) { last = false; break; }
    if (last) return false;

    std::wstring block = text.substr(grp, grpEnd - grp);
    text.erase(grp, grpEnd - grp);
    tbEnd = text.find(L"</toolbar>", tb);
    text.insert(tbEnd, block + L"\n");
    return true;
}

// Navigation panel is PARKED (SDK 2025.3 can't surface a third-party panel yet,
// see reference-powerpdf-navigation-panel). This REMOVES the experimental panel
// entry we injected in 0.9.7-0.9.11 from the merged layout, so a profile that
// carried it comes back clean. Returns true when something was removed.
// Remove ANY navigation-panel entry we ever injected into a <Left> block,
// regardless of surrounding whitespace or attributes. We no longer add
// ourselves to the native panel bar at all (the panel opens via the ribbon
// "Side panel" toggle instead), so this cleans up old profiles. Returns true
// when something was removed.
inline bool CleanLeftPanel(std::wstring& text)
{
    bool changed = false;
    for (const wchar_t* atom : { L"TUNGSTEN:PluginStore:Left", L"panel::PluginStore" })
    {
        std::wstring needle = std::wstring(L"<panel name=\"") + atom + L"\"";
        for (;;)
        {
            size_t p = text.find(needle);
            if (p == std::wstring::npos) break;
            size_t end = text.find(L"/>", p);
            if (end == std::wstring::npos) break;
            end += 2;
            size_t start = p;   // swallow leading whitespace/newline too
            while (start > 0 && (text[start - 1] == L' ' || text[start - 1] == L'\t' ||
                                 text[start - 1] == L'\r' || text[start - 1] == L'\n')) --start;
            text.erase(start, end - start);
            changed = true;
        }
    }
    return changed;
}

// Left navigation panel (works in Power PDF 2025.3 FP8). List our panel in the
// viewer layout's <Left> block, right where the native panels (Bookmark,
// Stamps, ...) are listed - the same idea as PFFButton for the ribbon. Only the
// PDF VIEWER layout is touched (its <Left> carries the "Bookmark" panel); the
// Write-mode layout is left alone. Returns true when the entry was added.
inline bool EnsureLeftPanel(std::wstring& text)
{
    size_t l = text.find(L"<Left");
    if (l == std::wstring::npos) return false;
    size_t lend = text.find(L"</Left>", l);
    if (lend == std::wstring::npos) return false;
    std::wstring block = text.substr(l, lend - l);
    if (block.find(L"name=\"Bookmark\"") == std::wstring::npos) return false;         // not the viewer layout
    if (block.find(L"TUNGSTEN:PluginStore:Left") != std::wstring::npos) return false; // already present
    text.insert(lend, L"\r\n\t\t\t<panel name=\"TUNGSTEN:PluginStore:Left\" shortKey=\"XP\"/>");
    return true;
}

// Our ribbon buttons must carry IconMode="4" in the merged file, otherwise the
// host draws them with the small icon. Returns true when something changed.
inline bool PatchButtons(std::wstring& text)
{
    bool changed = false;
    for (int gi = 0; gi < kGroupCount; ++gi)
      for (int i = 0; i < kGroups[gi].n; ++i)
      {
        std::wstring open = std::wstring(L"<PFFButton name=\"") + kGroups[gi].buttons[i] + L"\"";
        size_t at = 0;
        for (;;)
        {
            at = text.find(open, at);
            if (at == std::wstring::npos) break;
            size_t end = text.find(L"/>", at);
            if (end == std::wstring::npos) break;

            std::wstring tg = text.substr(at, end + 2 - at);
            std::wstring want = open + L" IconMode=\"4\"/>";
            if (tg != want)
            {
                text.replace(at, end + 2 - at, want);
                changed = true;
                end = at + want.size() - 2;
            }
            at = end + 2;
        }
      }
    return changed;
}

// --- locating the layout files --------------------------------------------
// The live layout, in the user's own profile. Never resolve this from an
// elevated process — it would land in the administrator's profile instead.
inline std::wstring UserLayoutDir()
{
    wchar_t buf[MAX_PATH] = { 0 };
    if (!GetEnvironmentVariableW(L"APPDATA", buf, MAX_PATH)) return std::wstring();
    return std::wstring(buf) + L"\\Kofax\\PDF\\PowerPDF\\UILayout";
}

// Every layout file carrying a <Top> block (the ribbon lives there).
// Collected by content, not by name, so a future Power PDF that renames a
// mode still gets patched.
inline void CollectLayoutFiles(const std::wstring& dir, std::vector<std::wstring>& out,
                               int depth = 0)
{
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileW((dir + L"\\*").c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return;
    do
    {
        std::wstring name = fd.cFileName;
        if (name == L"." || name == L"..") continue;
        std::wstring full = dir + L"\\" + name;

        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)
        {
            if (depth < 1) CollectLayoutFiles(full, out, depth + 1);
            continue;
        }
        if (name.size() < 4 || _wcsicmp(name.c_str() + name.size() - 4, L".xml") != 0)
            continue;

        std::wstring text; bool utf16 = false;
        if (ReadTextFile(full, text, utf16) && text.find(L"<Top") != std::wstring::npos)
            out.push_back(full);
    } while (FindNextFileW(h, &fd));
    FindClose(h);
}

// Runs at every start: makes sure our group exists in the merged toolbar and
// that the buttons carry IconMode="4" — so an installation whose layout was
// merged before this pack existed repairs itself. Returns files written.
inline int ApplyButtons()
{
    int written = 0;
    std::vector<std::wstring> files;
    CollectLayoutFiles(UserLayoutDir(), files);

    for (size_t i = 0; i < files.size(); ++i)
    {
        std::wstring text; bool utf16 = false;
        if (!ReadTextFile(files[i], text, utf16)) continue;
        bool changed = false;
        for (int gi = 0; gi < kGroupCount; ++gi)
        {
            if (EnsureGroup(text, kGroups[gi]))        changed = true;
            if (EnsureButtons(text, kGroups[gi]))      changed = true;
            if (EnforceButtonOrder(text, kGroups[gi])) changed = true;
            if (MoveGroupToEnd(text, kGroups[gi]))     changed = true;
        }
        if (PatchButtons(text)) changed = true;
        if (CleanLeftPanel(text)) changed = true;   // never touch the native <Left> bar; strip old entries
        if (!changed) continue;

        // One pristine copy from before we ever touched it.
        std::wstring bak = files[i] + L".fp2-original";
        if (GetFileAttributesW(bak.c_str()) == INVALID_FILE_ATTRIBUTES)
            CopyFileW(files[i].c_str(), bak.c_str(), TRUE);

        DWORD err = 0;
        if (WriteTextFile(files[i], text, utf16, &err)) ++written;
    }
    return written;
}

} // namespace fplayout
