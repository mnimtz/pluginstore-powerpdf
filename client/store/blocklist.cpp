// blocklist.cpp - see blocklist.h.

#include "stdafx.h"
#include "blocklist.h"
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
#include <algorithm>
#pragma comment(lib, "bcrypt.lib")

extern "C" HINSTANCE gHINSTANCE;

namespace {

std::vector<PSBlocked> g_blocked;          // UI thread only
std::set<std::wstring> g_asked;            // "<id>|<version>" asked this session
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

std::string Utf8Lower(const std::wstring& s)
{
    std::wstring l = s;
    if (!l.empty()) CharLowerBuffW(&l[0], (DWORD)l.size());
    int n = WideCharToMultiByte(CP_UTF8, 0, l.c_str(), (int)l.size(), NULL, 0, NULL, NULL);
    std::string u(n > 0 ? n : 0, 0);
    if (n > 0) WideCharToMultiByte(CP_UTF8, 0, l.c_str(), (int)l.size(), &u[0], n, NULL, NULL);
    return u;
}

std::wstring FromUtf8(const std::string& s)
{
    int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), NULL, 0);
    std::wstring w(n > 0 ? n : 0, 0);
    if (n > 0) MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), &w[0], n);
    return w;
}

// English catalog name from our own manifest.json ("name": {"en": "..."}), else the id.
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

DWORD WINAPI Worker(LPVOID p)
{
    Job* job = static_cast<Job*>(p);
    auto* found = new std::vector<PSBlocked>();
    std::string tsv;
    DWORD status = 0;
    if (PSHttpGetText(job->url, tsv, &status) && status == 200)
    {
        // installed add-ons of THIS Power PDF: <bin>\Plug-Ins\<zxt>\manifest.json
        struct Inst { std::wstring id, version, zxt, name, hash; };
        std::vector<Inst> installed;
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
                Inst i;
                i.id = psold::Value(json, "id");
                i.version = psold::Value(json, "version");
                i.zxt = zxt;
                i.name = NameOf(json, i.id);
                if (i.id.empty()) continue;
                i.hash = Sha256Hex(Utf8Lower(i.id));
                installed.push_back(i);
            } while (FindNextFileW(h, &fd));
            FindClose(h);
        }
        size_t pos = 0;
        while (pos < tsv.size())
        {
            size_t end = tsv.find('\n', pos);
            std::string line = tsv.substr(pos, end == std::string::npos ? std::string::npos : end - pos);
            pos = end == std::string::npos ? tsv.size() : end + 1;
            if (line.empty() || line[0] == '#') continue;
            if (!line.empty() && line.back() == '\r') line.pop_back();
            size_t t1 = line.find('\t'), t2 = t1 == std::string::npos ? t1 : line.find('\t', t1 + 1);
            if (t2 == std::string::npos) continue;
            std::wstring hash = FromUtf8(line.substr(0, t1));
            std::wstring ver = FromUtf8(line.substr(t1 + 1, t2 - t1 - 1));
            std::wstring reason = FromUtf8(line.substr(t2 + 1));
            for (const auto& i : installed)
                if (i.hash == hash && (ver == L"*" || ver == i.version))
                    found->push_back({ i.id, i.name, i.version, i.zxt, reason });
        }
        FPLogW(L"[Store] blocklist checked: %u blocked add-on(s) installed", (unsigned)found->size());
    }
    else
        FPLogW(L"[Store] blocklist not reachable (HTTP %lu)", status);
    if (!job->notify || !PostMessageW(job->notify, job->message, 0, (LPARAM)found)) delete found;
    delete job;
    InterlockedExchange(&g_running, 0);
    FreeLibraryAndExitThread(gHINSTANCE, 0);
}

} // namespace

void PSBlockCheckStart(HWND notify, UINT message)
{
    if (InterlockedCompareExchange(&g_running, 1, 0) != 0) return;
    std::wstring bin = cspath::FindBin((HMODULE)gHINSTANCE);
    HMODULE self = NULL;
    if (bin.empty() || !GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, reinterpret_cast<LPCWSTR>(&Worker), &self))
    { InterlockedExchange(&g_running, 0); return; }
    Job* job = new Job{ notify, message, PSServerUrl() + L"/api/blocked?format=tsv", bin + L"\\Plug-Ins" };
    HANDLE t = CreateThread(NULL, 0, Worker, job, 0, NULL);
    if (t) CloseHandle(t);
    else { delete job; FreeLibrary(self); InterlockedExchange(&g_running, 0); }
}

void PSBlockTakeResult(LPARAM lp)
{
    std::unique_ptr<std::vector<PSBlocked>> r(reinterpret_cast<std::vector<PSBlocked>*>(lp));
    if (r) g_blocked = *r;
}

const std::vector<PSBlocked>& PSBlockedInstalled() { return g_blocked; }

static std::wstring FormatW(const std::wstring& fmt, const std::wstring& a, const std::wstring& b, const std::wstring& c)
{
    wchar_t buf[2048];
    _snwprintf_s(buf, _countof(buf), _TRUNCATE, fmt.c_str(), a.c_str(), b.c_str(), c.c_str());
    return buf;
}

void PSOfferBlockedRemoval(HWND owner, bool force)
{
    static bool busy = false;
    if (busy || g_blocked.empty()) return;
    busy = true;
    int removed = 0;
    std::vector<PSBlocked> list = g_blocked;
    for (const auto& b : list)
    {
        std::wstring key = b.id + L"|" + b.version;
        if (!force && g_asked.count(key)) continue;
        g_asked.insert(key);
        std::wstring msg = FormatW(FPLoc(IDS_PS_BLOCKED_ASK), b.name, b.version, b.reason.empty() ? L"-" : b.reason);
        if (FPMessageBox(owner, msg.c_str(), FPLoc(IDS_PSD_TITLE).c_str(), MB_YESNO | MB_ICONWARNING | MB_DEFBUTTON1) != IDYES)
        {
            FPLogW(L"[Store] blocked %s %s: removal postponed by the user", b.id.c_str(), b.version.c_str());
            continue;
        }
        int rc = PSUninstallPackage(b.zxtName, owner);
        FPLogW(L"[Store] blocked %s %s: removal -> %d", b.id.c_str(), b.version.c_str(), rc);
        if (rc == 0) ++removed;
        else
        {
            wchar_t err[1024];
            _snwprintf_s(err, _countof(err), _TRUNCATE, FPLoc(IDS_PS_BLOCKED_FAILED).c_str(), b.name.c_str(), rc);
            FPMessageBox(owner, err, FPLoc(IDS_PSD_TITLE).c_str(), MB_OK | MB_ICONERROR);
        }
    }
    if (removed > 0)
    {
        g_blocked.erase(std::remove_if(g_blocked.begin(), g_blocked.end(), [&](const PSBlocked& b) {
            return !cspath::FileExists(cspath::FindBin((HMODULE)gHINSTANCE) + L"\\Plug-Ins\\" + b.zxtName + L".zxt"); }), g_blocked.end());
        if (FPMessageBox(owner, FPLoc(IDS_PS_BLOCKED_REMOVED).c_str(), FPLoc(IDS_PSD_TITLE).c_str(), MB_YESNO | MB_ICONINFORMATION) == IDYES
            && PSScheduleRestart())
        {
            HWND main = owner ? GetAncestor(owner, GA_ROOTOWNER) : NULL;
            if (main) PostMessageW(main, WM_CLOSE, 0, 0);
        }
    }
    busy = false;
}
