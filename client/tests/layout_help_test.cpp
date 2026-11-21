// layout_help_test.cpp - the "Help" group of the central help in the user's ribbon layout (C2.1.0,
// common/layoutpatch.h): added to the shared "FeaturePack" tab when the host never merged it, kept as the
// last group of that tab, never duplicated, never written into another tab. Run: client\tests\run_tests.cmd.

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

int main()
{
    const std::wstring head = L"<GaaihoLayout name=\"Publish\" type=\"ZeonUILayout\"><Frisbee><Top panelcount=\"0\" cursel=\"0\">\n";
    const std::wstring tail = L"</Top></Frisbee></GaaihoLayout>\n";
    const std::wstring grp = L"<PFFGroup name=\"FeaturePack::AddonStoreHelp\"";
    std::vector<AddonGroup> groups;
    groups.push_back(HelpCenterGroup());

    // 1. a profile with the shared tab and one add-on: the group comes, after the add-on's
    std::wstring t = head + L"<toolbar name=\"FeaturePack\" shortKey=\"U\">\n<PFFGroup name=\"FeaturePack::MailMerge\" GroupType=\"PFFTitleBlock\">\n"
                            L"<PFFButton name=\"FeaturePack::MailMerge::Open\" IconMode=\"4\"/>\n</PFFGroup>\n</toolbar>\n"
                            L"<toolbar name=\"AddonStore\" shortKey=\"A\">\n</toolbar>\n" + tail;
    CHECK(EnsureAddonGroups(t, groups));
    CHECK(Count(t, grp) == 1);
    CHECK(t.find(grp) > t.find(L"FeaturePack::MailMerge\"") && t.find(grp) < t.find(L"<toolbar name=\"AddonStore\""));
    CHECK(t.find(L"<PFFButton name=\"FeaturePack::AddonStoreHelp::Open\" IconMode=\"4\"/>") != std::wstring::npos);
    CHECK(!EnsureAddonGroups(t, groups));            // second run: nothing to do
    CHECK(!MoveGroupToEnd(t, kHelpCenterDef));       // already last

    // 2. an add-on group the host appended after ours: ours moves back to the end of the tab
    size_t end = t.find(L"</toolbar>");
    t.insert(end, L"<PFFGroup name=\"FeaturePack::Late\" GroupType=\"PFFTitleBlock\">\n<PFFButton name=\"FeaturePack::Late::Go\" IconMode=\"4\"/>\n</PFFGroup>\n");
    CHECK(MoveGroupToEnd(t, kHelpCenterDef));
    CHECK(t.find(grp) > t.find(L"FeaturePack::Late\"") && Count(t, grp) == 1);
    CHECK(t.find(grp) < t.find(L"<toolbar name=\"AddonStore\""));

    // 3. a profile without the shared tab: the tab is created in front of the store's tab, with the group
    std::wstring u = head + L"<toolbar name=\"home\">\n</toolbar>\n<toolbar name=\"AddonStore\" shortKey=\"A\">\n</toolbar>\n" + tail;
    CHECK(EnsureAddonGroups(u, groups));
    CHECK(u.find(L"<toolbar name=\"FeaturePack\"") != std::wstring::npos && u.find(L"<toolbar name=\"FeaturePack\"") < u.find(L"<toolbar name=\"AddonStore\""));
    CHECK(Count(u, grp) == 1 && u.find(grp) < u.find(L"<toolbar name=\"AddonStore\""));

    // 4. a self-closing shared tab is opened, not duplicated
    std::wstring v = head + L"<toolbar name=\"FeaturePack\" shortKey=\"U\"/>\n<toolbar name=\"AddonStore\">\n</toolbar>\n" + tail;
    CHECK(EnsureAddonGroups(v, groups));
    CHECK(Count(v, L"<toolbar name=\"FeaturePack\"") == 1 && Count(v, grp) == 1);

    // 5. another layout than "Publish" is left alone
    std::wstring w = L"<GaaihoLayout name=\"Edit\"><Frisbee><Top><toolbar name=\"FeaturePack\">\n</toolbar></Top></Frisbee></GaaihoLayout>";
    std::wstring w0 = w;
    CHECK(!EnsureAddonGroups(w, groups) && w == w0);

    // 6. the atom names pass the layout's own atom rule
    CHECK(IsAtomName(kHelpCenterGroup) && IsAtomName(kHelpCenterButtons[0]));

    std::printf("layout_help_tests: %d checks, %d failed\n", g_checks, g_failed);
    return g_failed ? 1 : 0;
}
