// servermode_tests.cpp - tests of reading the server's operating mode (store/servermode.h, C2.2.0).
// Build and run: client\tests\run_tests.cmd (console program, no Power PDF needed).

#include "../store/servermode.h"
#include <cstdio>

using namespace psmode;

static int g_failed = 0, g_checks = 0;
#define CHECK(cond) do { ++g_checks; if (!(cond)) { ++g_failed; std::printf("FAIL %s:%d  %s\n", __FILE__, __LINE__, #cond); } } while (0)

int main()
{
    // the answers of /api/client/access and /api/ping
    CHECK(ModeOf("{\"ok\":true,\"data\":{\"allowed\":true,\"mode\":\"cloud\",\"environment\":\"production\",\"serverMode\":\"beta\"}}") == "beta");
    CHECK(ModeOf("{\"ok\":true,\"data\":{\"name\":\"pluginstore-powerpdf\",\"version\":\"2.6.0\",\"serverMode\":\"maintenance\"}}") == "maintenance");
    CHECK(ModeOf("{\"serverMode\": \"test\"}") == "test");
    CHECK(ModeOf("{\"serverMode\":\r\n\t\"production\"}") == "production");
    // the license "mode" is another field: never taken for the server mode
    CHECK(ModeOf("{\"ok\":true,\"data\":{\"allowed\":true,\"mode\":\"cloud\"}}") == "");
    // missing, broken or hostile values
    CHECK(ModeOf("") == "");
    CHECK(ModeOf("{\"serverMode\":null}") == "");
    CHECK(ModeOf("{\"serverMode\":42}") == "");
    CHECK(ModeOf("{\"serverMode\":\"beta") == "");
    CHECK(ModeOf("{\"serverMode\":\"a-very-long-value-beyond-twenty\"}") == "");
    CHECK(ModeOf("{\"serverMode\":\"be\\\"ta\"}") == "");
    CHECK(ModeOf("{\"serverMode\":") == "");

    CHECK(IndexOf("production") == 0);
    CHECK(IndexOf("beta") == 1);
    CHECK(IndexOf("maintenance") == 2);
    CHECK(IndexOf("test") == 3);
    CHECK(IndexOf("Beta") == -1);
    CHECK(IndexOf("offline") == -1);
    CHECK(IndexOf("") == -1);
    CHECK(IndexOf("<img src=x>") == -1);

    std::printf("servermode_tests: %d checks, %d failed\n", g_checks, g_failed);
    return g_failed ? 1 : 0;
}
