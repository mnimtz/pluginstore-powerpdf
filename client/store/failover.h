// failover.h - second address of the store for the built-in default (C1.9.17).
//
// Nov 10, 2026 the DNS name addon.power-pdf.de pointed at a foreign web server for a
// while, and every client lost the store although the store itself kept answering on
// its Azure address. With the built-in default (no policy, no own address of the user)
// the client now retries a failed request once on that Azure address and stays on the
// host that works for this Power PDF process; every kRecheckMs it asks the main
// address again.
//
// A host counts as the store only when GET /api/ping answers 200 with
// "name":"pluginstore-powerpdf". HTTPS with certificate checks applies to both hosts,
// the catalog signature (signature.cpp) is checked exactly as before.
//
// Header only and without WinHTTP, so client/tests can test the decisions on their own.

#pragma once
#include <windows.h>
#include <string>
#include <cwctype>

namespace psfailover {

// The built-in store address and its second address. The second one is our own Azure
// App Service; never "pluginstore.azurewebsites.net", that name belongs to someone else.
const wchar_t* const kPrimaryBase  = L"https://addon.power-pdf.de";
const wchar_t* const kFallbackBase = L"https://ppdf-store.azurewebsites.net";
const wchar_t* const kFallbackHost = L"ppdf-store.azurewebsites.net";

const unsigned long long kRecheckMs = 10ull * 60 * 1000;   // on the second address: ask the main one again after this
const unsigned long long kTrustMs   = 5ull * 60 * 1000;    // a good ping this recent: a 4xx answer stands without a new ping
const DWORD              kPingTimeoutMs = 5000;

enum class Host { Primary = 0, Fallback = 1 };
inline Host Other(Host h) { return h == Host::Primary ? Host::Fallback : Host::Primary; }
inline const wchar_t* Base(Host h) { return h == Host::Primary ? kPrimaryBase : kFallbackBase; }

// How one request ended.
enum class Outcome
{
    Ok,         // 2xx, body complete
    HttpError,  // an HTTP status outside 2xx (the body may explain it)
    NotSent,    // failed before the request left: name lookup, connection, TLS or certificate
    NoAnswer,   // sent, but no complete answer (timeout, connection dropped)
};

struct Answer
{
    Outcome outcome = Outcome::NotSent;
    DWORD status = 0;
};

// "https://addon.power-pdf.de/api/x?y" -> Primary and "/api/x?y". Scheme and host
// compare without case; the host must end right there (no port, user part or longer name).
inline bool SplitBase(const std::wstring& url, Host& host, std::wstring& rest)
{
    for (Host h : { Host::Primary, Host::Fallback })
    {
        const std::wstring base = Base(h);
        if (url.size() < base.size()) continue;
        bool same = true;
        for (size_t i = 0; i < base.size() && same; ++i)
            same = std::towlower(url[i]) == std::towlower(base[i]);
        if (!same) continue;
        if (url.size() > base.size())
        {
            wchar_t next = url[base.size()];
            if (next != L'/' && next != L'?' && next != L'#') continue;
        }
        host = h;
        rest = url.substr(base.size());
        return true;
    }
    return false;
}

inline std::wstring Join(Host h, const std::wstring& rest) { return std::wstring(Base(h)) + rest; }

// The answer of GET /api/ping: {"ok":true,"data":{"name":"pluginstore-powerpdf","version":"1.30.2"}}
inline bool IsStorePing(const std::string& body)
{
    if (body.empty() || body.size() > 4096) return false;
    std::string s;
    for (char c : body) if (c != ' ' && c != '\t' && c != '\r' && c != '\n') s += c;
    return !s.empty() && s[0] == '{' &&
           s.find("\"ok\":true") != std::string::npos &&
           s.find("\"name\":\"pluginstore-powerpdf\"") != std::string::npos;
}

// Which host this process uses, shared by all worker threads.
class State
{
public:
    Host Active()
    {
        AcquireSRWLockShared(&m_lock);
        Host h = m_fallback ? Host::Fallback : Host::Primary;
        ReleaseSRWLockShared(&m_lock);
        return h;
    }

    void Use(Host h, unsigned long long now)
    {
        AcquireSRWLockExclusive(&m_lock);
        m_fallback = h == Host::Fallback;
        m_since = now;
        ReleaseSRWLockExclusive(&m_lock);
    }

    // True for exactly one caller once the second address has been in use for kRecheckMs;
    // the window starts again right away, so other threads do not ask as well.
    bool ClaimRecheck(unsigned long long now)
    {
        AcquireSRWLockExclusive(&m_lock);
        bool due = m_fallback && now - m_since >= kRecheckMs;
        if (due) m_since = now;
        ReleaseSRWLockExclusive(&m_lock);
        return due;
    }

    void Verified(Host h, unsigned long long now)
    {
        AcquireSRWLockExclusive(&m_lock);
        m_verified[(int)h] = now + 1;   // 0 = never
        ReleaseSRWLockExclusive(&m_lock);
    }

    bool VerifiedWithin(Host h, unsigned long long now, unsigned long long ms)
    {
        AcquireSRWLockShared(&m_lock);
        unsigned long long v = m_verified[(int)h];
        ReleaseSRWLockShared(&m_lock);
        return v != 0 && now + 1 - v <= ms;
    }

private:
    SRWLOCK m_lock = SRWLOCK_INIT;
    bool m_fallback = false;
    unsigned long long m_since = 0;
    unsigned long long m_verified[2] = { 0, 0 };
};

// One store request with the second address.
//   enabled     the built-in default address is in use (no policy, no own address)
//   repeatable  false for POST: a request that may have reached the store is never sent twice
//   attempt     Answer(const std::wstring& url): sends the request to exactly this URL
//   ping        bool(Host): GET /api/ping on that host answered as the store
//   onSwitch    void(Host from, Host to): for the log
// A URL on neither address goes out unchanged, once.
template <class Attempt, class Ping, class OnSwitch>
Answer Run(State& st, bool enabled, const std::wstring& url, bool repeatable, unsigned long long now,
           Attempt attempt, Ping ping, OnSwitch onSwitch)
{
    Host given = Host::Primary;
    std::wstring rest;
    if (!enabled || !SplitBase(url, given, rest)) return attempt(url);

    if (st.ClaimRecheck(now) && ping(Host::Primary))
    {
        st.Verified(Host::Primary, now);
        st.Use(Host::Primary, now);
        onSwitch(Host::Fallback, Host::Primary);
    }

    // a URL of either address (a catalog of the other one may still be cached) goes to the active one
    const Host active = st.Active();
    Answer a = attempt(Join(active, rest));
    if (a.outcome == Outcome::Ok) return a;
    if (a.outcome == Outcome::NoAnswer && !repeatable) return a;
    if (a.outcome == Outcome::HttpError)
    {
        // 4xx right after a good ping: the store's own answer (unknown package, access refused ...)
        if (a.status < 500 && st.VerifiedWithin(active, now, kTrustMs)) return a;
        if (ping(active)) { st.Verified(active, now); return a; }
        // otherwise the host is not the store: the request never reached it, a POST may go again
    }

    const Host other = Other(active);
    if (!ping(other)) return a;   // the other address does not help either: keep the first answer
    st.Verified(other, now);
    st.Use(other, now);
    onSwitch(active, other);
    return attempt(Join(other, rest));
}

} // namespace psfailover
