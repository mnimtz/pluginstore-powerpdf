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
//
// C1.1.0: the Add-on Store has its OWN ribbon tab "Store" (toolbar atom
// "AddonStore", shortKey A) instead of a group on the shared "FeaturePack" tab,
// so add-ons named before "PluginStore" no longer push it around. The group it
// had on the shared tab ("FeaturePack::PluginStore") is removed from profiles
// merged by an older client.
static const wchar_t kStoreToolbar[] = L"AddonStore";
static const wchar_t kStoreShortKey[] = L"A";
static const wchar_t kLegacyGroup[] = L"FeaturePack::PluginStore";

static const wchar_t* const kStoreButtons[] = {
    L"AddonStore::Store::Open",
};

struct GroupDef { const wchar_t* name; const wchar_t* const* buttons; int n; };
static const GroupDef kGroups[] = {
    { L"AddonStore::Store", kStoreButtons, (int)(sizeof(kStoreButtons) / sizeof(kStoreButtons[0])) },
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
// Our own tab: appended as the last tab of the ribbon (<Top> block) when the
// merged file does not know it yet. Returns true when text changed.
inline bool EnsureStoreToolbar(std::wstring& text)
{
    if (text.find(std::wstring(L"<toolbar name=\"") + kStoreToolbar + L"\"") != std::wstring::npos) return false;
    size_t top = text.find(L"<Top");
    if (top == std::wstring::npos) return false;
    size_t topEnd = text.find(L"</Top>", top);
    if (topEnd == std::wstring::npos) return false;
    text.insert(topEnd, std::wstring(L"<toolbar name=\"") + kStoreToolbar + L"\" shortKey=\"" + kStoreShortKey +
                        L"\">\n</toolbar>\n");
    return true;
}

// Power PDF's own "Help" tab (toolbar atom "help").
static const wchar_t kHelpToolbar[] = L"help";

// Cuts a whole <toolbar name="<name>">...</toolbar> block (with its indentation)
// out of the <Top> block and returns it; empty when not found.
inline std::wstring CutToolbar(std::wstring& text, const wchar_t* name)
{
    size_t tb = text.find(std::wstring(L"<toolbar name=\"") + name + L"\"");
    if (tb == std::wstring::npos) return std::wstring();
    size_t topEnd = text.find(L"</Top>", tb);
    size_t gt = text.find(L'>', tb);
    if (topEnd == std::wstring::npos || gt == std::wstring::npos || gt > topEnd) return std::wstring();
    size_t end;
    if (text[gt - 1] == L'/') end = gt + 1;                        // <toolbar .../>
    else
    {
        end = text.find(L"</toolbar>", tb);
        if (end == std::wstring::npos || end > topEnd) return std::wstring();
        end += wcslen(L"</toolbar>");
    }
    size_t start = tb;
    while (start > 0 && (text[start - 1] == L' ' || text[start - 1] == L'\t')) --start;
    if (end < text.size() && text[end] == L'\r') ++end;
    if (end < text.size() && text[end] == L'\n') ++end;
    std::wstring block = text.substr(start, end - start);
    text.erase(start, end - start);
    if (block.empty() || block[block.size() - 1] != L'\n') block += L"\n";
    return block;
}

// The ribbon ends with "... Enhanced Features, <own tabs of private add-ons>,
// Store, Help": the "Store" tab comes last of all add-on tabs (an add-on that
// creates the shared tab later would otherwise put "Enhanced Features" behind
// it), and Power PDF's "Help" stays the very last tab (C1.1.2, Marcus).
// Tabs that add-ons create at run time are appended by the host after every tab
// of the file; the next start moves "Store" and "Help" behind them again.
// Returns true when the order changed.
inline bool EnsureTailOrder(std::wstring& text)
{
    const std::wstring storeTag = std::wstring(L"<toolbar name=\"") + kStoreToolbar + L"\"";
    const std::wstring helpTag = std::wstring(L"<toolbar name=\"") + kHelpToolbar + L"\"";
    size_t store = text.find(storeTag);
    if (store == std::wstring::npos) return false;
    size_t topEnd = text.find(L"</Top>", store);
    if (topEnd == std::wstring::npos) return false;
    size_t help = text.find(helpTag);
    if (help != std::wstring::npos && help > topEnd) help = std::wstring::npos;

    // Already in order: nothing but whitespace after "Store" up to "Help" (if
    // any), and nothing but whitespace after "Help" (or "Store") up to </Top>.
    auto onlySpaceBetween = [&](size_t from, size_t to) {
        for (size_t i = from; i < to; ++i) if (!iswspace(text[i])) return false;
        return true;
    };
    auto blockEnd = [&](size_t at) {
        size_t gt = text.find(L'>', at);
        if (gt != std::wstring::npos && text[gt - 1] == L'/') return gt + 1;
        size_t e = text.find(L"</toolbar>", at);
        return e == std::wstring::npos ? std::wstring::npos : e + wcslen(L"</toolbar>");
    };
    size_t storeEnd = blockEnd(store);
    if (storeEnd == std::wstring::npos || storeEnd > topEnd) return false;
    if (help == std::wstring::npos)
    {
        if (onlySpaceBetween(storeEnd, topEnd)) return false;
    }
    else if (help > store)
    {
        size_t helpEnd = blockEnd(help);
        if (helpEnd == std::wstring::npos || helpEnd > topEnd) return false;
        if (onlySpaceBetween(storeEnd, help) && onlySpaceBetween(helpEnd, topEnd)) return false;
    }

    std::wstring storeBlock = CutToolbar(text, kStoreToolbar);
    std::wstring helpBlock = help != std::wstring::npos ? CutToolbar(text, kHelpToolbar) : std::wstring();
    if (storeBlock.empty()) return false;
    size_t top = text.find(L"<Top");
    topEnd = top == std::wstring::npos ? std::wstring::npos : text.find(L"</Top>", top);
    if (topEnd == std::wstring::npos) return false;
    // </Top> starts at its own (indented) line: insert in front of that indentation
    size_t at = topEnd;
    while (at > 0 && (text[at - 1] == L' ' || text[at - 1] == L'\t')) --at;
    text.insert(at, storeBlock + helpBlock);
    return true;
}

// The shared "FeaturePack" tab must stand in the file BEFORE our tab. Add-ons
// create it at run time when it is missing, and the host appends such tabs after
// every tab of the file, so "Store" would land in front of "Enhanced Features".
// An empty placeholder is hidden by the host (no empty tab without add-ons); an
// add-on fills it, the host saves that, and from the next start on "Store" is the
// last tab. Returns true when inserted.
inline bool EnsureSharedToolbarBeforeStore(std::wstring& text)
{
    if (text.find(L"<toolbar name=\"FeaturePack\"") != std::wstring::npos) return false;
    size_t ours = text.find(std::wstring(L"<toolbar name=\"") + kStoreToolbar + L"\"");
    if (ours == std::wstring::npos) return false;
    text.insert(ours, L"<toolbar name=\"FeaturePack\" shortKey=\"U\">\n</toolbar>\n");
    return true;
}

// Removes a whole group (and the line break in front of it). Returns true when removed.
inline bool RemoveGroup(std::wstring& text, const wchar_t* name)
{
    bool changed = false;
    std::wstring tag = std::wstring(L"<PFFGroup name=\"") + name + L"\"";
    for (;;)
    {
        size_t g = text.find(tag);
        if (g == std::wstring::npos) break;
        size_t open = text.find(L'>', g);
        if (open == std::wstring::npos) break;
        size_t end;
        if (text[open - 1] == L'/') end = open + 1;                      // <PFFGroup .../>
        else
        {
            end = text.find(L"</PFFGroup>", g);
            if (end == std::wstring::npos) break;
            end += wcslen(L"</PFFGroup>");
        }
        size_t start = g;
        while (start > 0 && (text[start - 1] == L' ' || text[start - 1] == L'\t' ||
                             text[start - 1] == L'\r' || text[start - 1] == L'\n')) --start;
        text.erase(start, end - start);
        changed = true;
    }
    return changed;
}

// "Updates available" (C1.7.0): our own group at the end of Power PDF's Help tab ("help").
// C1.9.1: one "Updates" button that is always there (new atoms again: the host caches a group's button
// list by name); the groups of C1.7.0 (AddonStore::Updates) and C1.9.0 (AddonStore::Help) are removed.
static const wchar_t kHelpGroup[] = L"AddonStore::HelpTab";
static const wchar_t kHelpButtons[1][36] = { L"AddonStore::HelpTab::Updates" };
static const wchar_t* const kOldHelpGroups[] = { L"AddonStore::Updates", L"AddonStore::Help" };
inline bool EnsureHelpGroup(std::wstring& text)
{
    bool changed = false;
    // the earlier groups go (their buttons moved into the new one)
    for (const wchar_t* oldName : kOldHelpGroups)
        if (RemoveGroup(text, oldName)) changed = true;   // C1.9.2: every copy, also <PFFGroup .../>
    if (text.find(std::wstring(L"\"") + kHelpGroup + L"\"") != std::wstring::npos) return changed;
    size_t tb = text.find(L"<toolbar name=\"help\"");
    if (tb == std::wstring::npos) return changed;
    size_t tagEnd = text.find(L'>', tb);
    if (tagEnd == std::wstring::npos || text[tagEnd - 1] == L'/') return changed;   // <toolbar name="help"/>: leave it
    size_t end = text.find(L"</toolbar>", tb);
    if (end == std::wstring::npos) return changed;
    size_t nextTb = text.find(L"<toolbar ", tagEnd);
    if (nextTb != std::wstring::npos && nextTb < end) return changed;               // malformed: never write into another tab
    std::wstring block = std::wstring(L"<PFFGroup name=\"") + kHelpGroup + L"\" GroupType=\"PFFTitleBlock\">\n";
    for (const auto& b : kHelpButtons) block += std::wstring(L"<PFFButton name=\"") + b + L"\" IconMode=\"4\"/>\n";
    text.insert(end, block + L"</PFFGroup>\n");
    return true;
}

inline bool EnsureGroup(std::wstring& text, const GroupDef& g)
{
    std::wstring tag = std::wstring(L"\"") + g.name + L"\"";
    if (text.find(tag) != std::wstring::npos) return false;   // group already present

    size_t tb = text.find(std::wstring(L"<toolbar name=\"") + kStoreToolbar + L"\"");
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

// Ribbon governance: the Add-on Store group is always the LAST group on the
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

// First start after a FRESH client install (never after an update): empties the
// shared "FeaturePack" tab so groups of plug-ins that are no longer installed
// disappear. Installed plug-ins add their groups again on their own start, and
// ApplyButtons() re-adds ours right after. Returns true when text changed.
inline bool ClearSharedTab(std::wstring& text)
{
    size_t tb = text.find(L"<toolbar name=\"FeaturePack\"");
    if (tb == std::wstring::npos) return false;
    size_t open = text.find(L'>', tb);
    size_t end = text.find(L"</toolbar>", tb);
    if (open == std::wstring::npos || end == std::wstring::npos || open > end || text[open - 1] == L'/') return false;

    bool empty = true;
    for (size_t i = open + 1; i < end; ++i)
        if (!iswspace(text[i])) { empty = false; break; }
    if (empty) return false;
    text.replace(open + 1, end - (open + 1), L"\n");
    return true;
}

// Marker protocol: the MSI writes HKLM\...\PluginStore\FreshInstall only when it
// did not upgrade an existing client; each user profile remembers in HKCU which
// fresh install it has already handled. Returns files written (or -1 = nothing to do).
inline int ResetSharedTabOnFreshInstall(const wchar_t* regKey)
{
    wchar_t machine[256] = { 0 }, user[256] = { 0 };
    DWORD sz = sizeof(machine);
    if (RegGetValueW(HKEY_LOCAL_MACHINE, regKey, L"FreshInstall", RRF_RT_REG_SZ,
                     NULL, machine, &sz) != ERROR_SUCCESS || !machine[0])
        return -1;
    sz = sizeof(user);
    if (RegGetValueW(HKEY_CURRENT_USER, regKey, L"TabResetFor", RRF_RT_REG_SZ, NULL, user, &sz) == ERROR_SUCCESS
        && wcscmp(machine, user) == 0)
        return -1;

    int written = 0;
    std::vector<std::wstring> files;
    CollectLayoutFiles(UserLayoutDir(), files);
    for (size_t i = 0; i < files.size(); ++i)
    {
        std::wstring text; bool utf16 = false;
        if (!ReadTextFile(files[i], text, utf16) || !ClearSharedTab(text)) continue;
        std::wstring bak = files[i] + L".addonstore-before-reset";
        CopyFileW(files[i].c_str(), bak.c_str(), FALSE);
        DWORD err = 0;
        if (WriteTextFile(files[i], text, utf16, &err)) ++written;
    }

    HKEY k;
    if (RegCreateKeyExW(HKEY_CURRENT_USER, regKey, 0, NULL, 0, KEY_WRITE, NULL, &k, NULL) == ERROR_SUCCESS)
    {
        RegSetValueExW(k, L"TabResetFor", 0, REG_SZ, (const BYTE*)machine, (DWORD)((wcslen(machine) + 1) * sizeof(wchar_t)));
        RegCloseKey(k);
    }
    return written;
}

// --- groups of the add-ons the store installed (C1.9.4) ----------------------
// The host merges a plug-in's own "UILayout\Publish Mode.xml" into the user file
// only when it does not know the toolbar yet. Every profile that ever carried
// the shared "FeaturePack" tab therefore never receives the group of an add-on
// installed later: the add-on loads, but its buttons and (when no other group
// on the tab has a loaded plug-in) the whole "Enhanced Features" tab stay
// hidden. So the store adds each installed add-on's group itself.
// The group is rebuilt from checked atom names, never copied as text, so a
// layout file cannot place anything else into the user's ribbon.
struct AddonGroup { std::wstring name; std::vector<std::wstring> buttons; std::vector<std::wstring> modes; };

inline bool IsAtomName(const std::wstring& s)
{
    if (s.size() < 14 || s.size() > 200 || s.compare(0, 13, L"FeaturePack::") != 0) return false;
    for (wchar_t c : s)
        if (!((c >= L'a' && c <= L'z') || (c >= L'A' && c <= L'Z') || (c >= L'0' && c <= L'9') ||
              c == L':' || c == L'_' || c == L'-' || c == L'.')) return false;
    return true;
}

// Value of attr="..." inside the tag text [from, to); empty when absent.
inline std::wstring AttrIn(const std::wstring& text, size_t from, size_t to, const wchar_t* attr)
{
    std::wstring key = std::wstring(L" ") + attr + L"=\"";
    size_t p = text.find(key, from);
    if (p == std::wstring::npos || p >= to) return std::wstring();
    p += key.size();
    size_t e = text.find(L'"', p);
    if (e == std::wstring::npos || e > to) return std::wstring();
    return text.substr(p, e - p);
}

// Value of name="..." of the root <GaaihoLayout> ("Publish" for the viewer layout).
inline std::wstring LayoutName(const std::wstring& text)
{
    size_t p = text.find(L"<GaaihoLayout");
    if (p == std::wstring::npos) return std::wstring();
    size_t gt = text.find(L'>', p);
    return gt == std::wstring::npos ? std::wstring() : AttrIn(text, p, gt, L"name");
}

// The groups an add-on's own layout puts on the shared tab.
inline void AddonGroupsFrom(const std::wstring& text, std::vector<AddonGroup>& out)
{
    size_t tb = text.find(L"<toolbar name=\"FeaturePack\"");
    if (tb == std::wstring::npos) return;
    size_t tbEnd = text.find(L"</toolbar>", tb);
    if (tbEnd == std::wstring::npos) return;
    for (size_t g = text.find(L"<PFFGroup", tb); g != std::wstring::npos && g < tbEnd; g = text.find(L"<PFFGroup", g + 1))
    {
        size_t gt = text.find(L'>', g);
        if (gt == std::wstring::npos || gt > tbEnd || text[gt - 1] == L'/') continue;
        size_t gEnd = text.find(L"</PFFGroup>", gt);
        if (gEnd == std::wstring::npos || gEnd > tbEnd) break;
        AddonGroup ag;
        ag.name = AttrIn(text, g, gt, L"name");
        if (!IsAtomName(ag.name)) continue;
        for (size_t b = text.find(L"<PFFButton", gt); b != std::wstring::npos && b < gEnd; b = text.find(L"<PFFButton", b + 1))
        {
            size_t bt = text.find(L'>', b);
            if (bt == std::wstring::npos || bt > gEnd) break;
            std::wstring n = AttrIn(text, b, bt, L"name"), m = AttrIn(text, b, bt, L"IconMode");
            if (!IsAtomName(n) || ag.buttons.size() >= 40) continue;
            if (m.size() != 1 || m[0] < L'1' || m[0] > L'9') m = L"4";
            ag.buttons.push_back(n);
            ag.modes.push_back(m);
        }
        if (!ag.buttons.empty() && out.size() < 200) out.push_back(ag);
    }
}

// Every add-on in Plug-Ins that the store installed: <Name>.zxt next to a folder
// <Name> with manifest.json and UILayout\Publish Mode.xml.
inline std::vector<AddonGroup> InstalledAddonGroups(const std::wstring& pluginsDir)
{
    std::vector<AddonGroup> out;
    if (pluginsDir.empty()) return out;
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileW((pluginsDir + L"\\*").c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return out;
    do
    {
        std::wstring name = fd.cFileName;
        if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) || name.empty() || name[0] == L'.') continue;
        std::wstring dir = pluginsDir + L"\\" + name;
        if (GetFileAttributesW((pluginsDir + L"\\" + name + L".zxt").c_str()) == INVALID_FILE_ATTRIBUTES ||
            GetFileAttributesW((dir + L"\\manifest.json").c_str()) == INVALID_FILE_ATTRIBUTES) continue;
        std::wstring text; bool utf16 = false;
        if (ReadTextFile(dir + L"\\UILayout\\Publish Mode.xml", text, utf16)) AddonGroupsFrom(text, out);
    } while (FindNextFileW(h, &fd));
    FindClose(h);
    return out;
}

// Adds the groups (and buttons missing from a group) to the shared tab of the
// viewer layout. Returns true when text changed.
inline bool EnsureAddonGroups(std::wstring& text, const std::vector<AddonGroup>& groups)
{
    if (groups.empty() || LayoutName(text) != L"Publish") return false;
    bool changed = false;
    if (text.find(L"<toolbar name=\"FeaturePack\"") == std::wstring::npos)
    {
        size_t at = text.find(std::wstring(L"<toolbar name=\"") + kStoreToolbar + L"\"");
        if (at == std::wstring::npos)
        {
            size_t top = text.find(L"<Top");
            at = top == std::wstring::npos ? std::wstring::npos : text.find(L"</Top>", top);
        }
        if (at == std::wstring::npos) return false;
        text.insert(at, L"<toolbar name=\"FeaturePack\" shortKey=\"U\">\n</toolbar>\n");
        changed = true;
    }
    size_t tb = text.find(L"<toolbar name=\"FeaturePack\"");
    size_t gt = text.find(L'>', tb);
    if (gt == std::wstring::npos) return changed;
    if (text[gt - 1] == L'/')   // <toolbar name="FeaturePack" .../>
    {
        size_t slash = gt - 1;
        while (slash > tb && text[slash - 1] == L' ') --slash;
        text.replace(slash, gt + 1 - slash, L">\n</toolbar>");
        changed = true;
    }
    for (const auto& g : groups)
    {
        size_t tbEnd = text.find(L"</toolbar>", tb);
        if (tbEnd == std::wstring::npos) break;
        std::wstring grpTag = std::wstring(L"<PFFGroup name=\"") + g.name + L"\"";
        size_t grp = text.find(grpTag);
        if (grp == std::wstring::npos)
        {
            std::wstring block = grpTag + L" GroupType=\"PFFTitleBlock\">\n";
            for (size_t i = 0; i < g.buttons.size(); ++i)
                block += L"<PFFButton name=\"" + g.buttons[i] + L"\" IconMode=\"" + g.modes[i] + L"\"/>\n";
            block += L"</PFFGroup>\n";
            text.insert(tbEnd, block);
            changed = true;
            continue;
        }
        size_t open = text.find(L'>', grp);
        if (open == std::wstring::npos || text[open - 1] == L'/') continue;   // a self-closing group is left alone
        size_t grpEnd = text.find(L"</PFFGroup>", open);
        if (grpEnd == std::wstring::npos) continue;
        for (size_t i = 0; i < g.buttons.size(); ++i)
        {
            std::wstring needle = L"<PFFButton name=\"" + g.buttons[i] + L"\"";
            size_t f = text.find(needle, open);
            if (f != std::wstring::npos && f < grpEnd) continue;
            std::wstring ins = needle + L" IconMode=\"" + g.modes[i] + L"\"/>\n";
            text.insert(grpEnd, ins);
            grpEnd += ins.size();
            changed = true;
        }
    }
    return changed;
}

// Runs at every start: makes sure our group exists in the merged toolbar and
// that the buttons carry IconMode="4" — so an installation whose layout was
// merged before this pack existed repairs itself. Returns files written.
// pluginsDir (C1.9.4): Power PDF's Plug-Ins folder, for the groups of installed add-ons.
inline int ApplyButtons(const std::wstring& pluginsDir = std::wstring())
{
    int written = 0;
    std::vector<std::wstring> files;
    CollectLayoutFiles(UserLayoutDir(), files);
    const std::vector<AddonGroup> addons = InstalledAddonGroups(pluginsDir);

    for (size_t i = 0; i < files.size(); ++i)
    {
        std::wstring text; bool utf16 = false;
        if (!ReadTextFile(files[i], text, utf16)) continue;
        bool changed = false;
        if (RemoveGroup(text, kLegacyGroup)) changed = true;      // C1.0.0 and older: group on the shared tab
        if (EnsureStoreToolbar(text)) changed = true;
        if (EnsureTailOrder(text)) changed = true;      // ... Store, Help (C1.1.2)
        if (EnsureSharedToolbarBeforeStore(text)) changed = true;
        for (int gi = 0; gi < kGroupCount; ++gi)
        {
            if (EnsureGroup(text, kGroups[gi]))        changed = true;
            if (EnsureButtons(text, kGroups[gi]))      changed = true;
            if (EnforceButtonOrder(text, kGroups[gi])) changed = true;
        }
        if (PatchButtons(text)) changed = true;
        if (EnsureHelpGroup(text)) changed = true;   // C1.7.0
        if (CleanLeftPanel(text)) changed = true;   // never touch the native <Left> bar; strip old entries
        if (EnsureAddonGroups(text, addons)) changed = true;   // C1.9.4: groups the host never merged
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
