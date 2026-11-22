// servermode.h - the store server's operating mode in a JSON answer (C2.2.0; server S2.4.0/S2.6.0).
// No dependencies, so client\tests\servermode_tests.cpp tests it on its own.
#pragma once
#include <string>

namespace psmode {

// The modes the client knows; the index is what HKCU ServerMode stores. Anything else counts as production.
static const char* const kNames[] = { "production", "beta", "maintenance", "test" };
static const int kCount = 4;

// The "serverMode" string of a JSON answer, or "" when there is none. The server writes it as a plain lower-case
// word; whitespace after the colon is allowed, escapes and long values are not (a value is at most 20 characters).
inline std::string ModeOf(const std::string& body)
{
    const std::string key = "\"serverMode\":";
    size_t i = body.find(key);
    if (i == std::string::npos) return "";
    size_t q = body.find_first_not_of(" \t\r\n", i + key.size());
    if (q == std::string::npos || body[q] != '"') return "";
    size_t e = body.find('"', q + 1);
    if (e == std::string::npos || e - q - 1 > 20) return "";
    std::string v = body.substr(q + 1, e - q - 1);
    return v.find('\\') == std::string::npos ? v : std::string();
}

// The index of a known mode, or -1.
inline int IndexOf(const std::string& mode)
{
    for (int i = 0; i < kCount; ++i)
        if (mode == kNames[i]) return i;
    return -1;
}

} // namespace psmode
