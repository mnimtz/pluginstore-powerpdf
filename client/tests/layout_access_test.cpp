// layout_access_test.cpp - groups of add-ons with "ribbonTab": "accessibility" (C2.3.1, common/layoutpatch.h):
// into Power PDF's own Accessibility tab (toolbar tool::accessibility) after the "Fonts" group, out of the shared
// tab, out of the Accessibility tab again on uninstall; Power PDF's own groups are never touched.
// Run: client\tests\run_tests.cmd.

#include "../common/layoutpatch.h"
#include <cstdio>

using namespace fplayout;

static int g_failed = 0, g_checks = 0;
#define CHECK(cond) do { ++g_checks; if (!(cond)) { ++g_failed; std::printf("FAIL %s:%d  %s\n", __FILE__, __LINE__, #cond); } } while (0)

static size_t Count(const std::wstring& t, const std::wstring& x)
{
    size_t n = 0;
    for (size_t p = t.find(x); p != std::wstring::npos; p = t.find(x, p + 1)) ++n;
    return n;
}

static const wchar_t kGrp[] = L"<PFFGroup name=\"FeaturePack::AccessibilityReview\"";

static AddonGroup Access()
{
    AddonGroup g;
    g.name = L"FeaturePack::AccessibilityReview";
    for (const wchar_t* b : { L"Panel", L"Window", L"Review", L"Folder" })
    {
        g.buttons.push_back(std::wstring(L"FeaturePack::AccessibilityReview::") + b);
        g.modes.push_back(L"4");
    }
    g.addon = L"AccessibilityReview";
    g.accessTab = true;
    return g;
}

// position of x inside the content of a tab, npos when it is not there
static size_t InTab(const std::wstring& t, const wchar_t* tab, const std::wstring& x)
{
    size_t open, end;
    if (!ToolbarSpan(t, tab, open, end)) return std::wstring::npos;
    size_t p = t.find(x, open);
    return p < end ? p : std::wstring::npos;
}

// the real shape of the user layout (Power PDF 2025.3, shortened): CRLF, tabs, built-in groups
static const std::wstring kHead = L"<GaaihoLayout name=\"Publish\" type=\"ZeonUILayout\" mode=\"17\" version=\"512\">\r\n<Frisbee>\r\n\t<Top panelcount=\"0\" cursel=\"0\">\r\n";
static const std::wstring kAccess =
    L"\t\t<toolbar name=\"tool::accessibility\" shortKey=\"V\">\r\n"
    L"\t\t\t<PFFGroup name=\"group::accessibility::check\" GroupType=\"PFFTitleBlock\">\r\n\t\t\t\t<PFFButton name=\"tool::accessibility::check\"/>\r\n\t\t\t</PFFGroup>\r\n"
    L"\t\t\t<PFFGroup name=\"group::accessibility::fonts\" GroupType=\"PFFTitleBlock\">\r\n\t\t\t\t<PFFButton name=\"tool::accessibility::fonts\"/>\r\n\t\t\t</PFFGroup>\r\n"
    L"\t\t\t<PFFGroup name=\"group::accessibility::readout\" GroupType=\"PFFTitleBlock\">\r\n\t\t\t\t<PFFButton name=\"tool::accessibility::readout\"/>\r\n\t\t\t</PFFGroup>\r\n"
    L"\t\t</toolbar>\r\n";
static const std::wstring kShared0 =
    L"\t\t<toolbar name=\"FeaturePack\" shortKey=\"U\">\r\n"
    L"\t\t\t<PFFGroup name=\"FeaturePack::MailMerge\" GroupType=\"PFFTitleBlock\">\r\n\t\t\t\t<PFFButton name=\"FeaturePack::MailMerge::Open\" IconMode=\"4\"/>\r\n\t\t\t</PFFGroup>\r\n";
static const std::wstring kSharedOld =   // Smart Accessibility 0.1.x on the shared tab
    L"\t\t\t<PFFGroup name=\"FeaturePack::AccessibilityReview\" GroupType=\"PFFTitleBlock\">\r\n"
    L"\t\t\t\t<PFFButton name=\"FeaturePack::AccessibilityReview::Panel\" IconMode=\"4\"/>\r\n"
    L"\t\t\t\t<PFFButton name=\"FeaturePack::AccessibilityReview::Window\" IconMode=\"4\"/>\r\n"
    L"\t\t\t</PFFGroup>\r\n";
static const std::wstring kSharedEnd = L"\t\t</toolbar>\r\n\t\t<toolbar name=\"AddonStore\" shortKey=\"A\">\r\n\t\t</toolbar>\r\n";
static const std::wstring kTail = L"\t</Top>\r\n</Frisbee>\r\n</GaaihoLayout>\r\n";

static bool WriteFileW(const std::wstring& path, const std::string& bytes)
{
    HANDLE f = CreateFileW(path.c_str(), GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, 0, NULL);
    if (f == INVALID_HANDLE_VALUE) return false;
    DWORD w = 0;
    BOOL ok = WriteFile(f, bytes.data(), (DWORD)bytes.size(), &w, NULL);
    CloseHandle(f);
    return ok && w == bytes.size();
}

static void RemoveTree(const std::wstring& dir)
{
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileW((dir + L"\\*").c_str(), &fd);
    if (h != INVALID_HANDLE_VALUE)
    {
        do
        {
            std::wstring n = fd.cFileName;
            if (n == L"." || n == L"..") continue;
            if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) RemoveTree(dir + L"\\" + n);
            else DeleteFileW((dir + L"\\" + n).c_str());
        } while (FindNextFileW(h, &fd));
        FindClose(h);
    }
    RemoveDirectoryW(dir.c_str());
}

int wmain(int, wchar_t** argv)
{
    std::vector<AddonGroup> groups;
    groups.push_back(Access());
    groups.push_back(HelpCenterGroup());

    // 1. update from 0.1.x: out of the shared tab, into the Accessibility tab right after "Fonts", all four buttons
    std::wstring t = kHead + kAccess + kShared0 + kSharedOld + kSharedEnd + kTail;
    const std::wstring accessBefore = kAccess;
    CHECK(EnsureAddonGroups(t, groups));
    CHECK(Count(t, kGrp) == 1);
    CHECK(InTab(t, L"FeaturePack", kGrp) == std::wstring::npos);
    size_t at = InTab(t, kAccessToolbar, kGrp);
    CHECK(at != std::wstring::npos);
    CHECK(at > t.find(L"group::accessibility::fonts") && at < t.find(L"group::accessibility::readout"));
    CHECK(Count(t, L"<PFFButton name=\"FeaturePack::AccessibilityReview::") == 4);
    CHECK(t.find(L"<PFFButton name=\"FeaturePack::AccessibilityReview::Folder\" IconMode=\"4\"/>") != std::wstring::npos);
    // Power PDF's own groups are unchanged and in their order
    CHECK(t.find(L"group::accessibility::check") < t.find(L"group::accessibility::fonts"));
    CHECK(Count(t, L"<PFFGroup name=\"group::accessibility::") == 3);
    CHECK(t.find(L"<PFFGroup name=\"group::accessibility::readout\" GroupType=\"PFFTitleBlock\">\r\n\t\t\t\t<PFFButton name=\"tool::accessibility::readout\"/>") != std::wstring::npos);
    // the other add-on and the store's help stay on the shared tab
    CHECK(InTab(t, L"FeaturePack", L"FeaturePack::MailMerge\"") != std::wstring::npos);
    CHECK(InTab(t, L"FeaturePack", L"FeaturePack::AddonStoreHelp\"") != std::wstring::npos);
    // the shared tab is still one well-formed block in front of the store's tab
    CHECK(Count(t, L"<toolbar name=\"FeaturePack\"") == 1 && Count(t, L"</toolbar>") == 3);
    // 2. second run: nothing to do
    const std::wstring t1 = t;
    CHECK(!EnsureAddonGroups(t, groups) && t == t1);

    // 3. a fresh profile the host merged the add-on into: the shared tab only had our group, now it is empty
    std::wstring u = kHead + kAccess + L"\t\t<toolbar name=\"FeaturePack\" shortKey=\"U\">\r\n" + kSharedOld + kSharedEnd + kTail;
    std::vector<AddonGroup> only(1, Access());
    CHECK(EnsureAddonGroups(u, only));
    CHECK(InTab(u, L"FeaturePack", L"<PFFGroup") == std::wstring::npos);
    CHECK(InTab(u, kAccessToolbar, kGrp) != std::wstring::npos && Count(u, kGrp) == 1);

    // 4. a profile without the shared tab: only the Accessibility group, no shared tab is created for it
    std::wstring v = kHead + kAccess + L"\t\t<toolbar name=\"AddonStore\" shortKey=\"A\">\r\n\t\t</toolbar>\r\n" + kTail;
    CHECK(EnsureAddonGroups(v, only));
    CHECK(v.find(L"<toolbar name=\"FeaturePack\"") == std::wstring::npos);
    CHECK(InTab(v, kAccessToolbar, kGrp) != std::wstring::npos);

    // 5. the "Fonts" group is missing (another Power PDF version): at the end of the Accessibility tab
    std::wstring w = kHead + L"\t\t<toolbar name=\"tool::accessibility\" shortKey=\"V\">\r\n"
                     L"\t\t\t<PFFGroup name=\"group::accessibility::check\" GroupType=\"PFFTitleBlock\">\r\n\t\t\t</PFFGroup>\r\n\t\t</toolbar>\r\n" + kTail;
    CHECK(EnsureAddonGroups(w, only));
    at = InTab(w, kAccessToolbar, kGrp);
    CHECK(at != std::wstring::npos && at > w.find(L"group::accessibility::check"));

    // 6. no Accessibility tab at all: the group goes to the shared tab like every other add-on's
    std::wstring x = kHead + kShared0 + kSharedEnd + kTail;
    CHECK(EnsureAddonGroups(x, only));
    CHECK(InTab(x, L"FeaturePack", kGrp) != std::wstring::npos && Count(x, kGrp) == 1);

    // 7. a self-closing or broken Accessibility tab is never written into
    std::wstring y = kHead + L"\t\t<toolbar name=\"tool::accessibility\" shortKey=\"V\"/>\r\n" + kShared0 + kSharedEnd + kTail;
    CHECK(EnsureAddonGroups(y, only));
    CHECK(y.find(L"<toolbar name=\"tool::accessibility\" shortKey=\"V\"/>") != std::wstring::npos);
    CHECK(InTab(y, L"FeaturePack", kGrp) != std::wstring::npos);

    // 8. the user moved the group to another tab: it stays there, missing buttons are added
    std::wstring z = kHead + kAccess + L"\t\t<toolbar name=\"home\">\r\n" + kSharedOld + L"\t\t</toolbar>\r\n" + kTail;
    CHECK(EnsureAddonGroups(z, only));
    CHECK(InTab(z, L"home", kGrp) != std::wstring::npos && Count(z, kGrp) == 1);
    CHECK(Count(z, L"<PFFButton name=\"FeaturePack::AccessibilityReview::") == 4);

    // 9. uninstall: out of the Accessibility tab; Power PDF's own groups and the shared tab stay as they were
    std::vector<std::wstring> gone;
    gone.push_back(L"FeaturePack::AccessibilityReview");
    gone.push_back(L"group::accessibility::fonts");     // never a Power PDF group, even when listed
    std::wstring r = t;
    CHECK(RemoveGoneFromAccessTab(r, gone));
    CHECK(r.find(kGrp) == std::wstring::npos);
    CHECK(Count(r, L"<PFFGroup name=\"group::accessibility::") == 3);
    {
        size_t open, end;
        CHECK(ToolbarSpan(r, kAccessToolbar, open, end));
        CHECK(r.substr(r.find(L"\t\t<toolbar name=\"tool::accessibility\""), end + wcslen(L"</toolbar>\r\n") - r.find(L"\t\t<toolbar name=\"tool::accessibility\"")) == accessBefore);
    }
    CHECK(!RemoveGoneFromAccessTab(r, gone));            // nothing left to remove
    // a gone group on the shared tab keeps today's behaviour (left alone, the host hides it)
    std::wstring s = kHead + kAccess + kShared0 + kSharedOld + kSharedEnd + kTail;
    CHECK(!RemoveGoneFromAccessTab(s, gone) && Count(s, kGrp) == 1);
    // another layout than "Publish" is left alone
    std::wstring e = L"<GaaihoLayout name=\"Edit\"><Frisbee><Top>" + kAccess + L"</Top></Frisbee></GaaihoLayout>";
    const std::wstring e0 = e;
    CHECK(!EnsureAddonGroups(e, only) && !RemoveGoneFromAccessTab(e, gone) && e == e0);

    // 10. the manifest field
    CHECK(ManifestString(L"{\"id\":\"x\",\"ribbonTab\": \"accessibility\"}", L"ribbonTab") == L"accessibility");
    CHECK(ManifestString(L"{\r\n  \"ribbonTab\"\t:\r\n \"shared\"\r\n}", L"ribbonTab") == L"shared");
    CHECK(ManifestString(L"{\"id\":\"x\"}", L"ribbonTab").empty());
    CHECK(ManifestString(L"{\"changelog\":{\"en\":\"set \\\"ribbonTab\\\": \\\"accessibility\\\"\"}}", L"ribbonTab").empty());
    CHECK(ManifestString(L"{\"note\":\"ribbonTab\",\"ribbonTab\":\"accessibility\"}", L"ribbonTab") == L"accessibility");
    CHECK(ManifestString(L"{\"ribbonTab\":true}", L"ribbonTab").empty());
    CHECK(ManifestString(L"{\"ribbonTab\":\"acc\\u0065ssibility\"}", L"ribbonTab").empty());

    // 11. end to end: ApplyButtons with a Plug-Ins folder and a user layout folder (APPDATA) of our own
    std::wstring base = argv[0];
    base = base.substr(0, base.find_last_of(L"\\/")) + L"\\accesstmp";
    RemoveTree(base);
    const std::wstring plugins = base + L"\\Plug-Ins", appdata = base + L"\\AppData";
    const std::wstring layoutDir = appdata + L"\\Kofax\\PDF\\PowerPDF\\UILayout";
    for (const std::wstring& d : { base, plugins, plugins + L"\\AccessibilityReview", plugins + L"\\AccessibilityReview\\UILayout",
                                   plugins + L"\\MailMerge", plugins + L"\\MailMerge\\UILayout", appdata, appdata + L"\\Kofax",
                                   appdata + L"\\Kofax\\PDF", appdata + L"\\Kofax\\PDF\\PowerPDF", layoutDir })
        CreateDirectoryW(d.c_str(), NULL);
    const std::string ownLayout =
        "<?xml version=\"1.0\" ?>\r\n<GaaihoLayout name=\"Publish\" type=\"ZeonUILayout\" mode=\"17\" version=\"19\">\r\n<Frisbee>\r\n<Top panelcount=\"0\" cursel=\"0\">\r\n"
        "<toolbar name=\"FeaturePack\" shortKey=\"U\">\r\n<PFFGroup name=\"FeaturePack::AccessibilityReview\" GroupType=\"PFFTitleBlock\">\r\n"
        "<PFFButton name=\"FeaturePack::AccessibilityReview::Panel\" IconMode=\"4\"/>\r\n<PFFButton name=\"FeaturePack::AccessibilityReview::Window\" IconMode=\"4\"/>\r\n"
        "<PFFButton name=\"FeaturePack::AccessibilityReview::Review\" IconMode=\"4\"/>\r\n<PFFButton name=\"FeaturePack::AccessibilityReview::Folder\" IconMode=\"4\"/>\r\n"
        "</PFFGroup>\r\n</toolbar>\r\n</Top>\r\n</Frisbee>\r\n</GaaihoLayout>\r\n";
    const std::string mailLayout =
        "<GaaihoLayout name=\"Publish\"><Frisbee><Top><toolbar name=\"FeaturePack\"><PFFGroup name=\"FeaturePack::MailMerge\" GroupType=\"PFFTitleBlock\">"
        "<PFFButton name=\"FeaturePack::MailMerge::Open\" IconMode=\"4\"/></PFFGroup></toolbar></Top></Frisbee></GaaihoLayout>";
    CHECK(WriteFileW(plugins + L"\\AccessibilityReview.zxt", "MZ"));
    CHECK(WriteFileW(plugins + L"\\AccessibilityReview\\manifest.json", "{\"id\":\"com.tungsten.accessibilityreview\",\"version\":\"0.2.0\",\"ribbonTab\":\"accessibility\"}"));
    CHECK(WriteFileW(plugins + L"\\AccessibilityReview\\UILayout\\Publish Mode.xml", ownLayout));
    CHECK(WriteFileW(plugins + L"\\MailMerge.zxt", "MZ"));
    CHECK(WriteFileW(plugins + L"\\MailMerge\\manifest.json", "{\"id\":\"com.tungsten.mailmerge\",\"version\":\"2.1.1\"}"));
    CHECK(WriteFileW(plugins + L"\\MailMerge\\UILayout\\Publish Mode.xml", mailLayout));
    // the user file is UTF-16LE with a BOM, like Power PDF writes it
    const std::wstring userLayout = kHead + kAccess + kShared0 + kSharedOld + kSharedEnd + kTail;
    std::string utf16("\xFF\xFE", 2);
    utf16.append((const char*)userLayout.c_str(), userLayout.size() * sizeof(wchar_t));
    CHECK(WriteFileW(layoutDir + L"\\Publish.xml", utf16));
    SetEnvironmentVariableW(L"APPDATA", appdata.c_str());
    const wchar_t regKey[] = L"Software\\TungstenAddonStoreTests\\LayoutAccess";
    RegDeleteTreeW(HKEY_CURRENT_USER, regKey);

    auto ReadLayout = [&]() { std::wstring text; bool u16 = false; ReadTextFile(layoutDir + L"\\Publish.xml", text, u16); return text; };
    CHECK(ApplyButtons(plugins, regKey) == 1);
    std::wstring live = ReadLayout();
    CHECK(InTab(live, kAccessToolbar, kGrp) != std::wstring::npos && Count(live, kGrp) == 1);
    CHECK(InTab(live, L"FeaturePack", L"FeaturePack::MailMerge\"") != std::wstring::npos);
    CHECK(Count(live, L"<PFFButton name=\"FeaturePack::AccessibilityReview::") == 4);
    { std::wstring raw; bool u16 = false; CHECK(ReadTextFile(layoutDir + L"\\Publish.xml", raw, u16) && u16); }
    CHECK(ApplyButtons(plugins, regKey) == 0);            // nothing to do the next time

    // switched off by the store (out of service): the group stays where it is
    CHECK(MoveFileW((plugins + L"\\AccessibilityReview.zxt").c_str(), (plugins + L"\\AccessibilityReview.zxt.suspended").c_str()));
    CHECK(ApplyButtons(plugins, regKey) == 0);
    CHECK(Count(ReadLayout(), kGrp) == 1);
    CHECK(MoveFileW((plugins + L"\\AccessibilityReview.zxt.suspended").c_str(), (plugins + L"\\AccessibilityReview.zxt").c_str()));

    // uninstalled: out of the Accessibility tab; Mail Merge and Power PDF's groups stay
    DeleteFileW((plugins + L"\\AccessibilityReview.zxt").c_str());
    RemoveTree(plugins + L"\\AccessibilityReview");
    CHECK(ApplyButtons(plugins, regKey) == 1);
    live = ReadLayout();
    CHECK(live.find(kGrp) == std::wstring::npos);
    CHECK(Count(live, L"<PFFGroup name=\"group::accessibility::") == 3);
    CHECK(InTab(live, L"FeaturePack", L"FeaturePack::MailMerge\"") != std::wstring::npos);

    // a Plug-Ins folder we cannot see never counts as "everything uninstalled"
    std::wstring back = kHead + kAccess + kTail;
    back.insert(back.find(L"\t\t</toolbar>"), GroupBlock(Access()));
    std::string b16("\xFF\xFE", 2);
    b16.append((const char*)back.c_str(), back.size() * sizeof(wchar_t));
    CHECK(WriteFileW(layoutDir + L"\\Publish.xml", b16));
    ApplyButtons(base + L"\\missing", regKey);
    CHECK(Count(ReadLayout(), kGrp) == 1);

    RegDeleteTreeW(HKEY_CURRENT_USER, regKey);
    RemoveTree(base);

    std::printf("layout_access_tests: %d checks, %d failed\n", g_checks, g_failed);
    return g_failed ? 1 : 0;
}
