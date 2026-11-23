// suspend_tests.cpp - tests of the out-of-service list (store/suspendcore.h, C2.3.0).
// Build and run: client\tests\run_tests.cmd (console program, no Power PDF needed).

#include "../store/suspendcore.h"
#include <cstdio>

using namespace pssusp;

static int g_failed = 0, g_checks = 0;
#define CHECK(cond) do { ++g_checks; if (!(cond)) { ++g_failed; std::printf("FAIL %s:%d  %s\n", __FILE__, __LINE__, #cond); } } while (0)

static const std::string H = "# hash\tlevel\treason\n";

int main()
{
    const std::string a(64, 'a'), b(64, 'b'), c(64, 'c');
    std::map<std::string, Entry> m;
    // the server's answer (S2.8.0): header, CRLF or LF, reasons with spaces
    CHECK(Parse(H + a + "\tdisabled\tLeaks a token\r\n" + b + "\tpaused\tChecking\n", m));
    CHECK(m.size() == 2);
    CHECK(m[a].level == "disabled" && m[a].reason == "Leaks a token");
    CHECK(m[b].level == "paused" && m[b].reason == "Checking");
    CHECK(Parse("# hash\tlevel\treason\r\n" + a + "\tdisabled\tx\r\n", m) && m.size() == 1);
    // the empty list: valid, nothing suspended
    CHECK(Parse(H, m) && m.empty());
    CHECK(Parse("# hash\tlevel\treason", m) && m.empty());
    // NOT the list (review C2.3.0): never "nothing listed"
    CHECK(!Parse("", m));
    CHECK(!Parse("<html><body>Please sign in to the proxy</body></html>", m));
    CHECK(!Parse("{\"ok\":true}", m));
    CHECK(!Parse(a + "\tdisabled\tx\n", m));                  // no header
    CHECK(!Parse("# hash\tversion\treason\n", m));            // the blocklist, not this list
    // broken lines are skipped: short hash, upper case, not hex, unknown level, no tab
    CHECK(Parse(H + std::string(63, 'a') + "\tdisabled\tx\n", m) && m.empty());
    CHECK(Parse(H + std::string(64, 'A') + "\tdisabled\tx\n", m) && m.empty());
    CHECK(Parse(H + std::string(64, 'g') + "\tdisabled\tx\n", m) && m.empty());
    CHECK(Parse(H + a + "\tremoved\tx\n", m) && m.empty());
    CHECK(Parse(H + a + "disabled x\n", m) && m.empty());
    // a line without reason still counts
    CHECK(Parse(H + a + "\tdisabled\n" + c + "\tpaused", m) && m.size() == 2 && m[a].reason.empty() && m[c].level == "paused");
    // control characters in a reason become spaces
    CHECK(Parse(H + a + "\tdisabled\tone\x01two\n", m) && m[a].reason == "one two");
    // more than kMaxEntries: refused as a whole (never a cut list)
    {
        std::string big = H;
        for (size_t i = 0; i <= kMaxEntries; ++i)
        {
            char hex[65];
            std::snprintf(hex, sizeof hex, "%064zx", i);
            big += std::string(hex) + "\tpaused\tx\n";
        }
        CHECK(!Parse(big, m) && m.empty());
    }

    // what to do with an installed add-on
    CHECK(Decide("disabled", true, false) == Action::SwitchOff);   // listed, still on: switch off
    CHECK(Decide("disabled", false, true) == Action::None);        // already off
    CHECK(Decide("disabled", true, true) == Action::SwitchOff);    // new copy next to an old switched-off one
    CHECK(Decide("paused", true, false) == Action::None);          // paused: keeps running
    CHECK(Decide("paused", false, true) == Action::SwitchOn);      // lowered to paused: comes back
    CHECK(Decide("", false, true) == Action::SwitchOn);            // no longer listed: comes back
    CHECK(Decide("", true, true) == Action::None);                 // a newer copy is on already
    CHECK(Decide("", true, false) == Action::None);
    CHECK(Decide("", false, false) == Action::None);               // neither file: nothing to switch

    std::printf("suspend_tests: %d checks, %d failed\n", g_checks, g_failed);
    return g_failed == 0 ? 0 : 1;
}
