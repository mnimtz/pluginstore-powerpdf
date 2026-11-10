// failover_tests.cpp - tests of the store's second address (store/failover.h, C1.9.17).
// Build and run: client\tests\run_tests.cmd (console program, no Power PDF needed).

#include "../store/failover.h"
#include <cstdio>
#include <vector>

using namespace psfailover;

static int g_failed = 0, g_checks = 0;
#define CHECK(cond) do { ++g_checks; if (!(cond)) { ++g_failed; std::printf("FAIL %s:%d  %s\n", __FILE__, __LINE__, #cond); } } while (0)

// A scripted network: what each host answers and what the client sent.
struct Net
{
    Answer primary{ Outcome::Ok, 200 };
    Answer fallback{ Outcome::Ok, 200 };
    bool primaryIsStore = true;
    bool fallbackIsStore = true;
    std::vector<std::wstring> sent;     // URLs of the requests
    std::vector<Host> pinged;
    std::vector<std::pair<Host, Host>> switches;

    Answer Call(State& st, const std::wstring& url, bool repeatable, unsigned long long now, bool enabled = true)
    {
        return Run(st, enabled, url, repeatable, now,
            [&](const std::wstring& u) {
                sent.push_back(u);
                Host h; std::wstring rest;
                if (!SplitBase(u, h, rest)) return Answer{ Outcome::Ok, 200 };   // some other server
                return h == Host::Primary ? primary : fallback;
            },
            [&](Host h) { pinged.push_back(h); return h == Host::Primary ? primaryIsStore : fallbackIsStore; },
            [&](Host from, Host to) { switches.push_back({ from, to }); });
    }
    void Clear() { sent.clear(); pinged.clear(); switches.clear(); }
};

static const std::wstring P = L"https://addon.power-pdf.de";
static const std::wstring F = L"https://ppdf-store.azurewebsites.net";

static void TestSplitBase()
{
    Host h; std::wstring rest;
    CHECK(SplitBase(P + L"/api/catalog?format=tsv", h, rest) && h == Host::Primary && rest == L"/api/catalog?format=tsv");
    CHECK(SplitBase(P, h, rest) && h == Host::Primary && rest.empty());
    CHECK(SplitBase(L"HTTPS://Addon.Power-PDF.de/api/ping", h, rest) && h == Host::Primary && rest == L"/api/ping");
    CHECK(SplitBase(F + L"/api/packages/a/1.0.0/download", h, rest) && h == Host::Fallback && rest == L"/api/packages/a/1.0.0/download");
    CHECK(SplitBase(P + L"?x=1", h, rest) && rest == L"?x=1");
    // lookalikes and other hosts are never rewritten
    CHECK(!SplitBase(L"https://addon.power-pdf.de.example.com/api/ping", h, rest));
    CHECK(!SplitBase(L"https://addon.power-pdf.de@example.com/api/ping", h, rest));
    CHECK(!SplitBase(L"https://addon.power-pdf.de:8443/api/ping", h, rest));
    CHECK(!SplitBase(L"http://addon.power-pdf.de/api/ping", h, rest));
    CHECK(!SplitBase(L"https://pluginstore.azurewebsites.net/api/ping", h, rest));   // belongs to someone else
    CHECK(!SplitBase(L"https://ppdf-store.azurewebsites.net.example.com/", h, rest));
    CHECK(!SplitBase(L"http://localhost:5190/api/ping", h, rest));
    CHECK(!SplitBase(L"", h, rest));
    CHECK(Join(Host::Fallback, L"/api/ping") == F + L"/api/ping");
    CHECK(std::wstring(kFallbackHost) == L"ppdf-store.azurewebsites.net");
}

static void TestIsStorePing()
{
    CHECK(IsStorePing("{\"ok\":true,\"data\":{\"name\":\"pluginstore-powerpdf\",\"version\":\"1.30.2\"}}"));
    CHECK(IsStorePing("{ \"ok\": true, \"data\": { \"name\": \"pluginstore-powerpdf\" } }\n"));
    CHECK(!IsStorePing(""));
    CHECK(!IsStorePing("<html><body><h1>It works!</h1></body></html>"));
    CHECK(!IsStorePing("{\"ok\":true,\"data\":{\"name\":\"other-store\"}}"));
    CHECK(!IsStorePing("{\"ok\":false,\"data\":{\"name\":\"pluginstore-powerpdf\"}}"));
    CHECK(!IsStorePing(std::string(5000, ' ') + "{\"ok\":true,\"data\":{\"name\":\"pluginstore-powerpdf\"}}"));
}

// Policy or own address of the user: exactly one request, unchanged, no ping.
static void TestDisabled()
{
    State st; Net n;
    n.primary = { Outcome::NotSent, 0 };
    Answer a = n.Call(st, P + L"/api/catalog", true, 1000, false);
    CHECK(a.outcome == Outcome::NotSent);
    CHECK(n.sent.size() == 1 && n.sent[0] == P + L"/api/catalog");
    CHECK(n.pinged.empty() && n.switches.empty());
    CHECK(st.Active() == Host::Primary);
}

static void TestOtherHostUntouched()
{
    State st; Net n;
    Answer a = n.Call(st, L"http://localhost:5190/api/catalog", true, 1000);
    CHECK(a.outcome == Outcome::Ok && n.sent.size() == 1 && n.sent[0] == L"http://localhost:5190/api/catalog" && n.pinged.empty());
}

static void TestPrimaryWorks()
{
    State st; Net n;
    Answer a = n.Call(st, P + L"/api/catalog", true, 1000);
    CHECK(a.outcome == Outcome::Ok && n.sent.size() == 1 && n.sent[0] == P + L"/api/catalog");
    CHECK(n.pinged.empty() && st.Active() == Host::Primary);
}

// The incident of Nov 10, 2026: the main name leads to a server without the store's HTTPS.
static void TestPrimaryUnreachable()
{
    State st; Net n;
    n.primary = { Outcome::NotSent, 0 };
    Answer a = n.Call(st, P + L"/api/catalog?lang=de", true, 1000);
    CHECK(a.outcome == Outcome::Ok);
    CHECK(n.sent.size() == 2 && n.sent[1] == F + L"/api/catalog?lang=de");
    CHECK(n.pinged.size() == 1 && n.pinged[0] == Host::Fallback);   // no ping of a host that did not connect
    CHECK(n.switches.size() == 1 && n.switches[0].first == Host::Primary && n.switches[0].second == Host::Fallback);
    CHECK(st.Active() == Host::Fallback);

    // remembered: the next requests go straight to the second address, also with a main-address URL
    n.Clear();
    a = n.Call(st, P + L"/api/blocked?format=tsv", true, 2000);
    CHECK(a.outcome == Outcome::Ok && n.sent.size() == 1 && n.sent[0] == F + L"/api/blocked?format=tsv" && n.pinged.empty());
    // a download URL of the second address (catalog fetched there) works the same
    n.Clear();
    a = n.Call(st, F + L"/api/packages/x/1.0.0/download", true, 3000);
    CHECK(a.outcome == Outcome::Ok && n.sent.size() == 1 && n.sent[0] == F + L"/api/packages/x/1.0.0/download");
}

// Recheck: after kRecheckMs the main address gets asked again (once), and is used when it is the store.
static void TestRecheck()
{
    State st; Net n;
    n.primary = { Outcome::NotSent, 0 };
    n.Call(st, P + L"/api/catalog", true, 1000);
    CHECK(st.Active() == Host::Fallback);

    // not yet due
    n.Clear();
    n.Call(st, P + L"/api/catalog", true, 1000 + kRecheckMs - 1);
    CHECK(n.pinged.empty() && n.sent.size() == 1 && n.sent[0] == F + L"/api/catalog");

    // due, but the main address is still broken: stays, and the next check waits a full window again
    n.Clear();
    n.primaryIsStore = false;
    n.Call(st, P + L"/api/catalog", true, 1000 + kRecheckMs);
    CHECK(n.pinged.size() == 1 && n.pinged[0] == Host::Primary && st.Active() == Host::Fallback);
    CHECK(n.sent.size() == 1 && n.sent[0] == F + L"/api/catalog");
    n.Clear();
    n.Call(st, P + L"/api/catalog", true, 1000 + kRecheckMs + 10);
    CHECK(n.pinged.empty());

    // repaired: back to the main address
    n.Clear();
    n.primary = { Outcome::Ok, 200 };
    n.primaryIsStore = true;
    n.Call(st, P + L"/api/catalog", true, 1000 + 2 * kRecheckMs + 10);
    CHECK(n.pinged.size() == 1 && n.pinged[0] == Host::Primary);
    CHECK(st.Active() == Host::Primary && n.sent.size() == 1 && n.sent[0] == P + L"/api/catalog");
    CHECK(n.switches.size() == 1 && n.switches[0].second == Host::Primary);
}

// ClaimRecheck hands the recheck to one caller only.
static void TestClaimOnce()
{
    State st;
    CHECK(!st.ClaimRecheck(kRecheckMs * 5));   // on the main address there is nothing to check
    st.Use(Host::Fallback, 100);
    CHECK(!st.ClaimRecheck(100 + kRecheckMs - 1));
    CHECK(st.ClaimRecheck(100 + kRecheckMs));
    CHECK(!st.ClaimRecheck(100 + kRecheckMs));
}

// A foreign server behind the name answers with HTTP (404 page, proxy error): ping says it is not the store.
static void TestForeignAnswer()
{
    State st; Net n;
    n.primary = { Outcome::HttpError, 404 };
    n.primaryIsStore = false;
    Answer a = n.Call(st, P + L"/api/catalog", true, 1000);
    CHECK(a.outcome == Outcome::Ok);
    CHECK(n.pinged.size() == 2 && n.pinged[0] == Host::Primary && n.pinged[1] == Host::Fallback);
    CHECK(n.sent.size() == 2 && n.sent[1] == F + L"/api/catalog" && st.Active() == Host::Fallback);
}

// The store's own 4xx (unknown package, access refused) stands; no switch.
static void TestStoreOwnError()
{
    State st; Net n;
    n.primary = { Outcome::HttpError, 404 };
    Answer a = n.Call(st, P + L"/api/packages/x/icon", true, 1000);
    CHECK(a.outcome == Outcome::HttpError && a.status == 404);
    CHECK(n.pinged.size() == 1 && n.pinged[0] == Host::Primary && n.sent.size() == 1 && st.Active() == Host::Primary);

    // within kTrustMs a further 4xx needs no new ping
    n.Clear();
    a = n.Call(st, P + L"/api/packages/y/icon", true, 1000 + kTrustMs);
    CHECK(a.status == 404 && n.pinged.empty() && n.sent.size() == 1);
    // later it does
    n.Clear();
    a = n.Call(st, P + L"/api/packages/z/icon", true, 1000 + kTrustMs + 1);
    CHECK(n.pinged.size() == 1);
    // a 5xx is always checked (a proxy in front of a foreign server answers 52x)
    n.Clear();
    n.primary = { Outcome::HttpError, 503 };
    a = n.Call(st, P + L"/api/catalog", true, 1000 + kTrustMs + 2);
    CHECK(n.pinged.size() == 1 && n.pinged[0] == Host::Primary && a.status == 503 && st.Active() == Host::Primary);
}

// The second address is no store (app gone, name taken over): never switch to it.
static void TestFallbackNotStore()
{
    State st; Net n;
    n.primary = { Outcome::NotSent, 0 };
    n.fallbackIsStore = false;
    Answer a = n.Call(st, P + L"/api/catalog", true, 1000);
    CHECK(a.outcome == Outcome::NotSent && n.sent.size() == 1 && st.Active() == Host::Primary && n.switches.empty());
}

// Offline: both fail, the first answer is reported, nothing remembered.
static void TestOffline()
{
    State st; Net n;
    n.primary = { Outcome::NotSent, 0 };
    n.fallback = { Outcome::NotSent, 0 };
    n.fallbackIsStore = false;   // the ping fails as well
    Answer a = n.Call(st, P + L"/api/catalog", true, 1000);
    CHECK(a.outcome == Outcome::NotSent && st.Active() == Host::Primary);
}

// POST: a request that may have reached the store is not sent again; one that never left is.
static void TestPost()
{
    {
        State st; Net n;
        n.primary = { Outcome::NoAnswer, 0 };
        Answer a = n.Call(st, P + L"/api/client/inventory", false, 1000);
        CHECK(a.outcome == Outcome::NoAnswer && n.sent.size() == 1 && n.pinged.empty() && st.Active() == Host::Primary);
    }
    {
        State st; Net n;
        n.primary = { Outcome::NotSent, 0 };
        Answer a = n.Call(st, P + L"/api/client/inventory", false, 1000);
        CHECK(a.outcome == Outcome::Ok && n.sent.size() == 2 && n.sent[1] == F + L"/api/client/inventory");
    }
    {
        // a foreign server answered: our store never saw the POST
        State st; Net n;
        n.primary = { Outcome::HttpError, 405 };
        n.primaryIsStore = false;
        Answer a = n.Call(st, P + L"/api/deliveries/release", false, 1000);
        CHECK(a.outcome == Outcome::Ok && n.sent.size() == 2);
    }
    {
        // GET without answer is repeated
        State st; Net n;
        n.primary = { Outcome::NoAnswer, 0 };
        Answer a = n.Call(st, P + L"/api/catalog", true, 1000);
        CHECK(a.outcome == Outcome::Ok && n.sent.size() == 2);
    }
}

// On the second address and it fails while the main one is back: one retry there.
static void TestFallbackFailsPrimaryBack()
{
    State st; Net n;
    st.Use(Host::Fallback, 1000);
    n.fallback = { Outcome::NotSent, 0 };
    Answer a = n.Call(st, P + L"/api/catalog", true, 2000);
    CHECK(a.outcome == Outcome::Ok && n.sent.size() == 2 && n.sent[0] == F + L"/api/catalog" && n.sent[1] == P + L"/api/catalog");
    CHECK(st.Active() == Host::Primary);
}

int main()
{
    TestSplitBase();
    TestIsStorePing();
    TestDisabled();
    TestOtherHostUntouched();
    TestPrimaryWorks();
    TestPrimaryUnreachable();
    TestRecheck();
    TestClaimOnce();
    TestForeignAnswer();
    TestStoreOwnError();
    TestFallbackNotStore();
    TestOffline();
    TestPost();
    TestFallbackFailsPrimaryBack();
    std::printf("%d checks, %d failed\n", g_checks, g_failed);
    return g_failed == 0 ? 0 : 1;
}
