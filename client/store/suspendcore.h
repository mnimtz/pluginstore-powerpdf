// suspendcore.h - add-ons out of service (C2.3.0, server S2.8.0), the parts without Windows calls
// (tested by client\tests\suspend_tests.cpp).
//
// GET /api/suspended?format=tsv lists add-ons the store admins took out of service:
//   # hash<TAB>level<TAB>reason
//   <sha256 of the lowercase id><TAB>paused|disabled<TAB><reason>
// "paused": no installations or updates, installed copies keep running (nothing to do here).
// "disabled": the client switches the installed add-on off (Plug-Ins\<zxt>.zxt -> <zxt>.zxt.suspended),
// without removing it, and on again once the add-on is no longer listed.

#pragma once
#include <map>
#include <string>
#include <vector>

namespace pssusp {

struct Entry { std::string level; std::string reason; };   // reason as UTF-8

const size_t kMaxEntries = 2000;

// The list, or false when the answer is not the store's list (review C2.3.0: a proxy or captive-portal page
// with status 200 must never read as "nothing listed", which would switch add-ons on again). The first line
// must be the header; a list beyond kMaxEntries is refused as a whole. Broken lines are skipped.
inline bool Parse(const std::string& tsv, std::map<std::string, Entry>& out)
{
    out.clear();
    static const std::string header = "# hash\tlevel\treason";
    size_t first = tsv.find('\n');
    std::string head = tsv.substr(0, first);
    if (!head.empty() && head.back() == '\r') head.pop_back();
    if (head != header) return false;
    size_t pos = first == std::string::npos ? tsv.size() : first + 1;
    while (pos < tsv.size())
    {
        size_t end = tsv.find('\n', pos);
        std::string line = tsv.substr(pos, end == std::string::npos ? std::string::npos : end - pos);
        pos = end == std::string::npos ? tsv.size() : end + 1;
        if (!line.empty() && line.back() == '\r') line.pop_back();
        if (line.empty() || line[0] == '#') continue;
        size_t t1 = line.find('\t');
        if (t1 != 64) continue;
        size_t t2 = line.find('\t', t1 + 1);
        std::string hash = line.substr(0, 64);
        bool hex = true;
        for (char c : hash) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) { hex = false; break; }
        if (!hex) continue;
        std::string level = line.substr(t1 + 1, t2 == std::string::npos ? std::string::npos : t2 - t1 - 1);
        if (level != "paused" && level != "disabled") continue;   // an unknown level of a newer server: nothing to do
        std::string reason = t2 == std::string::npos ? std::string() : line.substr(t2 + 1, 600);
        for (auto& c : reason) if ((unsigned char)c < 0x20) c = ' ';
        if (out.size() >= kMaxEntries) { out.clear(); return false; }
        out[hash] = { level, reason };
    }
    return true;
}

// What to do with one installed add-on.
enum class Action { None, SwitchOff, SwitchOn };

// level: from the list ("" = not listed); on: <zxt>.zxt exists; off: <zxt>.zxt.suspended exists.
inline Action Decide(const std::string& level, bool on, bool off)
{
    if (level == "disabled") return on ? Action::SwitchOff : Action::None;
    // not listed, or only paused: an add-on the store switched off comes back
    if (off && !on) return Action::SwitchOn;
    return Action::None;
}

} // namespace pssusp
