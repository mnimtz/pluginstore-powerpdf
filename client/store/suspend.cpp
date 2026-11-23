// suspend.cpp - see suspend.h.

#include "stdafx.h"
#include "suspend.h"
#include "suspendcore.h"
#include "http.h"
#include "install.h"
#include "settings.h"
#include "oldinstalls.h"
#include "powerpdfpath.h"
#include "loc.h"
#include "logging.h"
#include "Resource.h"
#include <bcrypt.h>
#include <set>
#include <memory>
#pragma comment(lib, "bcrypt.lib")

extern "C" HINSTANCE gHINSTANCE;

namespace {

std::vector<PSSuspended> g_found;          // UI thread only
std::set<std::wstring> g_asked;            // "<id>|off" / "<id>|on" asked this session (UI thread only)
volatile LONG g_running = 0;

struct Job { HWND notify; UINT message; std::wstring url, plugins; };

std::wstring Sha256Hex(const std::string& utf8)
{
    BCRYPT_ALG_HANDLE alg = NULL;
    BCRYPT_HASH_HANDLE h = NULL;
    UCHAR digest[32] = { 0 };
    std::wstring out;
    if (BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, NULL, 0) == 0)
    {
        if (BCryptCreateHash(alg, &h, NULL, 0, NULL, 0, 0) == 0 &&
            BCryptHashData(h, (PUCHAR)utf8.data(), (ULONG)utf8.size(), 0) == 0 &&
            BCryptFinishHash(h, digest, sizeof(digest), 0) == 0)
        {
            static const wchar_t hex[] = L"0123456789abcdef";
            for (UCHAR b : digest) { out += hex[b >> 4]; out += hex[b & 15]; }
        }
        if (h) BCryptDestroyHash(h);
        BCryptCloseAlgorithmProvider(alg, 0);
    }
    return out;
}

std::string Utf8(const std::wstring& s)
{
    int n = WideCharToMultiByte(CP_UTF8, 0, s.c_str(), (int)s.size(), NULL, 0, NULL, NULL);
    std::string u(n > 0 ? n : 0, 0);
    if (n > 0) WideCharToMultiByte(CP_UTF8, 0, s.c_str(), (int)s.size(), &u[0], n, NULL, NULL);
    return u;
}

std::wstring FromUtf8(const std::string& s)
{
    int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), NULL, 0);
    std::wstring w(n > 0 ? n : 0, 0);
    if (n > 0) MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), &w[0], n);
    return w;
}

std::wstring NameOf(const std::string& json, const std::wstring& id)
{
    size_t n = json.find("\"name\"");
    if (n != std::string::npos)
    {
        std::wstring v = psold::Value(json.substr(n), "en");
        if (!v.empty()) return v;
    }
    return id;
}

bool Exists(const std::wstring& path)
{
    DWORD a = GetFileAttributesW(path.c_str());
    return a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_DIRECTORY);
}

// The files as they are now (review C2.3.0): the list may be hours old; an add-on removed meanwhile drops out.
void Refresh(std::vector<PSSuspended>& list)
{
    std::wstring plugins = cspath::FindBin((HMODULE)gHINSTANCE);
    if (plugins.empty()) return;
    plugins += L"\\Plug-Ins\\";
    std::vector<PSSuspended> keep;
    for (auto s : list)
    {
        if (!Exists(plugins + s.zxtName + L"\\manifest.json")) continue;
        s.on = Exists(plugins + s.zxtName + L".zxt");
        s.off = Exists(plugins + s.zxtName + L".zxt.suspended");
        if (s.level.empty() && !(s.off && !s.on)) continue;   // nothing left to show
        keep.push_back(s);
    }
    list.swap(keep);
}

DWORD WINAPI Worker(LPVOID p)
{
    Job* job = static_cast<Job*>(p);
    std::vector<PSSuspended>* found = nullptr;
    std::string tsv;
    DWORD status = 0;
    // an exception leaving a thread ends Power PDF (std::terminate): none gets out
    try {
    std::map<std::string, pssusp::Entry> list;
    if (PSHttpGetText(job->url, tsv, &status) && status == 200 && pssusp::Parse(tsv, list))
    {
        found = new std::vector<PSSuspended>();
        bool hashOk = true;
        WIN32_FIND_DATAW fd;
        HANDLE h = FindFirstFileW((job->plugins + L"\\*").c_str(), &fd);
        if (h != INVALID_HANDLE_VALUE)
        {
            do
            {
                if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) || fd.cFileName[0] == L'.') continue;
                std::wstring zxt = fd.cFileName;
                if (!psold::SafeName(zxt) || _wcsicmp(zxt.c_str(), L"PluginStore") == 0) continue;
                std::string json = psold::ReadSmall(job->plugins + L"\\" + zxt + L"\\manifest.json");
                if (json.empty()) continue;
                PSSuspended s;
                s.id = psold::Value(json, "id");
                if (s.id.empty()) continue;
                std::wstring lower = s.id;
                CharLowerBuffW(&lower[0], (DWORD)lower.size());
                std::wstring hash = Sha256Hex(Utf8(lower));
                if (hash.size() != 64) { hashOk = false; break; }   // no hash: no decision at all, never "not listed"
                auto it = list.find(Utf8(hash));
                s.on = Exists(job->plugins + L"\\" + zxt + L".zxt");
                s.off = Exists(job->plugins + L"\\" + zxt + L".zxt.suspended");
                // not listed: only a copy the store switched off (and nothing else is on) is worth showing
                if (it == list.end() && !(s.off && !s.on)) continue;
                s.version = psold::Value(json, "version");
                s.zxtName = zxt;
                s.name = NameOf(json, s.id);
                if (it != list.end()) { s.level = FromUtf8(it->second.level); s.reason = FromUtf8(it->second.reason); }
                found->push_back(s);
            } while (FindNextFileW(h, &fd));
            FindClose(h);
        }
        if (!hashOk) { delete found; found = nullptr; FPLogW(L"[Store] out-of-service check: hashing failed, nothing decided"); }
        else FPLogW(L"[Store] out-of-service list checked: %u entr%s for installed add-ons (%u listed in all)",
                    (unsigned)found->size(), found->size() == 1 ? L"y" : L"ies", (unsigned)list.size());
    }
    else
    {
        // unreachable, or an answer that is not the list (proxy page): neither switches off nor on
        FPLogW(L"[Store] out-of-service list not available (HTTP %lu), keeping the last result", status);
    }
    } catch (...) { delete found; found = nullptr; FPLogW(L"[Store] out-of-service check failed"); }
    if (found && (!job->notify || !PostMessageW(job->notify, job->message, 0, (LPARAM)found))) delete found;
    delete job;
    InterlockedExchange(&g_running, 0);
    FreeLibraryAndExitThread(gHINSTANCE, 0);
}

pssusp::Action ActionOf(const PSSuspended& s) { return pssusp::Decide(Utf8(s.level), s.on, s.off); }

std::wstring KeyOf(const PSSuspended& s, pssusp::Action a) { return s.id + (a == pssusp::Action::SwitchOff ? L"|off" : L"|on"); }

} // namespace

void PSSuspendCheckStart(HWND notify, UINT message)
{
    if (InterlockedCompareExchange(&g_running, 1, 0) != 0) return;
    std::wstring bin = cspath::FindBin((HMODULE)gHINSTANCE);
    HMODULE self = NULL;
    if (bin.empty() || !GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, reinterpret_cast<LPCWSTR>(&Worker), &self))
    { InterlockedExchange(&g_running, 0); return; }
    Job* job = nullptr;
    try { job = new Job{ notify, message, PSServerUrl() + L"/api/suspended?format=tsv", bin + L"\\Plug-Ins" }; }
    catch (...) { FreeLibrary(self); InterlockedExchange(&g_running, 0); return; }
    HANDLE t = CreateThread(NULL, 0, Worker, job, 0, NULL);
    if (t) CloseHandle(t);
    else { delete job; FreeLibrary(self); InterlockedExchange(&g_running, 0); }
}

void PSSuspendTakeResult(LPARAM lp)
{
    std::unique_ptr<std::vector<PSSuspended>> r(reinterpret_cast<std::vector<PSSuspended>*>(lp));
    if (r) g_found = *r;
}

const std::vector<PSSuspended>& PSSuspendedInstalled()
{
    Refresh(g_found);
    return g_found;
}

bool PSSuspendHasWork(bool unaskedOnly)
{
    for (const auto& s : g_found)
    {
        pssusp::Action a = ActionOf(s);
        if (a != pssusp::Action::None && !(unaskedOnly && g_asked.count(KeyOf(s, a)))) return true;
    }
    return false;
}

void PSOfferSuspendChanges(HWND owner, bool force)
{
    static bool busy = false;
    if (busy) return;
    Refresh(g_found);
    if (!PSSuspendHasWork(!force)) return;
    busy = true;
    std::vector<std::pair<std::wstring, bool>> changes;   // zxt name, true = off
    std::vector<PSSuspended> list = g_found;
    for (const auto& s : list)
    {
        pssusp::Action a = ActionOf(s);
        if (a == pssusp::Action::None || changes.size() >= 29) continue;   // the exit code carries 29 add-ons; the rest next time
        std::wstring key = KeyOf(s, a);
        if (!force && g_asked.count(key)) continue;
        g_asked.insert(key);
        if (PSPolicyNoInstall())
        {
            // installing and removing is turned off on this PC: tell, switch nothing
            if (a == pssusp::Action::SwitchOff)
            {
                std::wstring msg = FPLocFormat(IDS_PS_SUSPEND_POLICY, { s.name, s.reason.empty() ? L"-" : s.reason });
                FPMessageBox(owner, msg.c_str(), FPLoc(IDS_PSD_TITLE).c_str(), MB_OK | MB_ICONWARNING);
            }
            FPLogW(L"[Store] out of service %s: not switched (policy DisableInstall)", s.id.c_str());
            continue;
        }
        std::wstring msg = a == pssusp::Action::SwitchOff
            ? FPLocFormat(IDS_PS_SUSPEND_ASK, { s.name, s.reason.empty() ? L"-" : s.reason })
            : FPLocFormat(IDS_PS_RESUME_ASK, { s.name });
        if (FPMessageBox(owner, msg.c_str(), FPLoc(IDS_PSD_TITLE).c_str(),
                         MB_YESNO | (a == pssusp::Action::SwitchOff ? MB_ICONWARNING : MB_ICONINFORMATION) | MB_DEFBUTTON1) != IDYES)
        {
            FPLogW(L"[Store] out of service %s: switching %s postponed by the user", s.id.c_str(), a == pssusp::Action::SwitchOff ? L"off" : L"on");
            continue;
        }
        changes.push_back({ s.zxtName, a == pssusp::Action::SwitchOff });
    }
    int changed = 0;
    if (!changes.empty())
    {
        std::vector<bool> done;
        int rc = PSSetAddonsSuspended(changes, owner, &done);   // ONE elevated step for all of them
        FPLogW(L"[Store] switched %u add-on(s) -> %d", (unsigned)changes.size(), rc);
        std::wstring failedNames;
        for (size_t i = 0; i < changes.size(); ++i)
        {
            if (i < done.size() && done[i]) { ++changed; continue; }
            std::wstring name = changes[i].first;
            for (const auto& s : list) if (s.zxtName == changes[i].first) name = s.name;
            failedNames += (failedNames.empty() ? L"" : L", ") + name;
        }
        // UAC declined (3): the user decided, no error; asked again in the next session
        if (rc != 3 && !failedNames.empty())
        {
            std::wstring err = FPLocFormat(IDS_PS_SUSPEND_FAILED, { failedNames, std::to_wstring(rc) });
            FPMessageBox(owner, err.c_str(), FPLoc(IDS_PSD_TITLE).c_str(), MB_OK | MB_ICONERROR);
        }
    }
    if (changed > 0)
    {
        Refresh(g_found);
        if (FPMessageBox(owner, FPLoc(IDS_PS_SUSPEND_RESTART).c_str(), FPLoc(IDS_PSD_TITLE).c_str(), MB_YESNO | MB_ICONINFORMATION) == IDYES
            && PSScheduleRestart())
        {
            HWND main = owner ? GetAncestor(owner, GA_ROOTOWNER) : NULL;
            if (main) PostMessageW(main, WM_CLOSE, 0, 0);
        }
    }
    busy = false;
}
