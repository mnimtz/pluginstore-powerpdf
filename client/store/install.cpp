// install.cpp â€” see install.h.

#include "stdafx.h"
#include "install.h"
#include "catalog.h"
#include "http.h"
#include "powerpdfpath.h"
#include "logging.h"
#include "loc.h"
#include "Resource.h"
#include <bcrypt.h>
#include <shellapi.h>
#include <vector>

#pragma comment(lib, "bcrypt.lib")

extern "C" HINSTANCE gHINSTANCE;

// ---------------------------------------------------------------------------
// helpers
// ---------------------------------------------------------------------------

static std::wstring PluginsDir()
{
    std::wstring bin = cspath::FindBin((HMODULE)gHINSTANCE);
    if (bin.empty()) return L"";
    return bin + L"\\Plug-Ins";
}

static std::wstring Sha256File(const std::wstring& path)
{
    std::wstring result;
    BCRYPT_ALG_HANDLE alg = NULL;
    BCRYPT_HASH_HANDLE hash = NULL;
    if (BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, NULL, 0) != 0)
        return result;

    do
    {
        if (BCryptCreateHash(alg, &hash, NULL, 0, NULL, 0, 0) != 0) break;

        HANDLE f = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, NULL,
                               OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, NULL);
        if (f == INVALID_HANDLE_VALUE) break;

        std::vector<BYTE> buf(1 << 20);
        DWORD got = 0;
        BOOL ok = TRUE;
        while ((ok = ReadFile(f, buf.data(), (DWORD)buf.size(), &got, NULL)) && got > 0)
            if (BCryptHashData(hash, buf.data(), got, 0) != 0) { ok = FALSE; break; }
        CloseHandle(f);
        if (!ok) break;

        BYTE digest[32] = { 0 };
        if (BCryptFinishHash(hash, digest, sizeof(digest), 0) != 0) break;

        wchar_t hex[65] = { 0 };
        for (int i = 0; i < 32; ++i) swprintf_s(hex + i * 2, 3, L"%02x", digest[i]);
        result = hex;
    } while (false);

    if (hash) BCryptDestroyHash(hash);
    if (alg) BCryptCloseAlgorithmProvider(alg, 0);
    return result;
}

// Minimal value scraper for OUR OWN manifest.json files ("key": "value").
static std::wstring JsonValue(const std::string& json, const char* key)
{
    std::string needle = std::string("\"") + key + "\"";
    size_t k = json.find(needle);
    if (k == std::string::npos) return L"";
    size_t colon = json.find(':', k + needle.size());
    if (colon == std::string::npos) return L"";
    size_t q1 = json.find('"', colon + 1);
    if (q1 == std::string::npos) return L"";
    size_t q2 = json.find('"', q1 + 1);
    if (q2 == std::string::npos) return L"";
    std::string v = json.substr(q1 + 1, q2 - q1 - 1);
    wchar_t w[512] = { 0 };
    MultiByteToWideChar(CP_UTF8, 0, v.c_str(), -1, w, 512);
    return w;
}

std::wstring PSInstalledVersion(const std::wstring& zxtName)
{
    std::wstring dir = PluginsDir();
    if (dir.empty()) return L"";
    std::wstring manifest = dir + L"\\" + zxtName + L"\\manifest.json";
    HANDLE f = CreateFileW(manifest.c_str(), GENERIC_READ, FILE_SHARE_READ, NULL,
                           OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f == INVALID_HANDLE_VALUE)
    {
        // No manifest: treat a present .zxt as installed with unknown version.
        if (cspath::FileExists(dir + L"\\" + zxtName + L".zxt")) return L"?";
        return L"";
    }
    DWORD size = GetFileSize(f, NULL);
    std::string json(size > 0 && size < 1 << 20 ? size : 0, 0);
    DWORD got = 0;
    if (!json.empty()) ReadFile(f, json.data(), size, &got, NULL);
    CloseHandle(f);
    return JsonValue(json, "version");
}

// ---------------------------------------------------------------------------
// the elevated step: ONE PowerShell child does the Program-Files work
// ---------------------------------------------------------------------------

static bool WriteTextFile(const std::wstring& path, const std::wstring& text)
{
    HANDLE f = CreateFileW(path.c_str(), GENERIC_WRITE, 0, NULL,
                           CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f == INVALID_HANDLE_VALUE) return false;
    // UTF-8 with BOM so PowerShell 5.1 reads umlauts correctly
    const BYTE bom[] = { 0xEF, 0xBB, 0xBF };
    DWORD w = 0;
    WriteFile(f, bom, 3, &w, NULL);
    int n = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), -1, NULL, 0, NULL, NULL);
    std::string utf8(n > 0 ? n - 1 : 0, 0);
    if (n > 1) WideCharToMultiByte(CP_UTF8, 0, text.c_str(), -1, utf8.data(), n, NULL, NULL);
    WriteFile(f, utf8.data(), (DWORD)utf8.size(), &w, NULL);
    CloseHandle(f);
    return true;
}

int PSInstallPackage(const PSCatalogEntry& e, HWND owner)
{
    std::wstring pluginsDir = PluginsDir();
    if (pluginsDir.empty()) return 5;

    wchar_t tempDir[MAX_PATH];
    GetTempPathW(MAX_PATH, tempDir);
    std::wstring ppak = std::wstring(tempDir) + e.id + L"-" + e.version + L".ppak";

    // 1) download (user context)
    DWORD status = 0;
    if (!PSHttpGetFile(e.downloadUrl, ppak, &status)) return 1;

    // 2) verify (user context) â€” the catalog hash is authoritative
    std::wstring actual = Sha256File(ppak);
    if (_wcsicmp(actual.c_str(), e.sha256.c_str()) != 0)
    {
        FPLogW(L"[Store] hash mismatch for %s: %s != %s", e.id.c_str(), actual.c_str(), e.sha256.c_str());
        DeleteFileW(ppak.c_str());
        return 2;
    }

    // 3) ONE elevated PowerShell: extract + copy the Program-Files part.
    //    x64 binary on every machine for now; Power PDF on Windows-on-ARM runs
    //    ARM64EC and loads x64 plug-ins (verified on this ARM64 dev machine).
    std::wstring script = std::wstring() +
        L"$ErrorActionPreference='Stop'\r\n" +
        L"$pkg='" + ppak + L"'\r\n" +
        L"$plugins='" + pluginsDir + L"'\r\n" +
        L"$name='" + e.zxtName + L"'\r\n" +
        L"$tmp=Join-Path $env:TEMP ('psinst-'+[guid]::NewGuid().ToString('N'))\r\n" +
        L"Add-Type -AssemblyName System.IO.Compression.FileSystem\r\n" +
        L"[System.IO.Compression.ZipFile]::ExtractToDirectory($pkg,$tmp)\r\n" +
        // A plug-in that is loaded in the running Power PDF cannot be
        // overwritten, but a loaded DLL CAN be renamed; the stale copy is
        // swept on the next store operation.
        L"Get-ChildItem $plugins -Filter '*.zxt.old-*' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue\r\n" +
        L"$target=Join-Path $plugins ($name + '.zxt')\r\n" +
        L"if(Test-Path $target){ try { Remove-Item $target -Force } catch { Rename-Item $target ($name + '.zxt.old-' + [guid]::NewGuid().ToString('N')) } }\r\n" +
        L"Copy-Item (Join-Path $tmp ('x64\\' + $name + '.zxt')) $target -Force\r\n" +
        L"$data=Join-Path $plugins $name\r\n" +
        L"New-Item -ItemType Directory -Force $data | Out-Null\r\n" +
        L"Copy-Item (Join-Path $tmp 'manifest.json') (Join-Path $data 'manifest.json') -Force\r\n" +
        L"foreach($extra in @('assets','docs','UILayout')){ $src=Join-Path $tmp $extra; if(Test-Path $src){ Copy-Item $src (Join-Path $data $extra) -Recurse -Force } }\r\n" +
        L"Remove-Item $tmp -Recurse -Force\r\n" +
        L"exit 0\r\n";

    std::wstring scriptPath = std::wstring(tempDir) + L"psinstall-" + e.zxtName + L".ps1";
    if (!WriteTextFile(scriptPath, script)) { DeleteFileW(ppak.c_str()); return 4; }

    std::wstring args = L"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + scriptPath + L"\"";

    SHELLEXECUTEINFOW sei = { sizeof(sei) };
    sei.fMask = SEE_MASK_NOCLOSEPROCESS;
    sei.hwnd = owner;
    sei.lpVerb = L"runas";                 // the ONE UAC prompt
    sei.lpFile = L"powershell.exe";
    sei.lpParameters = args.c_str();
    sei.nShow = SW_HIDE;

    int result;
    if (!ShellExecuteExW(&sei) || !sei.hProcess)
    {
        result = 3;                        // user declined elevation
    }
    else
    {
        WaitForSingleObject(sei.hProcess, 120000);
        DWORD exitCode = 1;
        GetExitCodeProcess(sei.hProcess, &exitCode);
        CloseHandle(sei.hProcess);
        result = exitCode == 0 ? 0 : 4;
    }

    DeleteFileW(scriptPath.c_str());
    DeleteFileW(ppak.c_str());
    FPLogW(L"[Store] install %s %s -> %d", e.id.c_str(), e.version.c_str(), result);
    return result;
}

// PowerShell single-quoted literal: ' doubles (a user folder like O'Brien).
static std::wstring PsQuote(const std::wstring& v)
{
    std::wstring r = L"'";
    for (wchar_t c : v)
    {
        r += c;
        if (c == L'\'' || c == 0x2018 || c == 0x2019) r += c;
    }
    return r + L"'";
}

// Starts a PowerShell helper script that must outlive this process.
static bool LaunchHelper(const std::wstring& scriptPath, const wchar_t* what)
{
    std::wstring cmd = L"powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + scriptPath + L"\"";
    std::vector<wchar_t> buf(cmd.begin(), cmd.end());
    buf.push_back(0);
    STARTUPINFOW si = { sizeof(si) };
    si.dwFlags = STARTF_USESHOWWINDOW;
    si.wShowWindow = SW_HIDE;
    PROCESS_INFORMATION pi = { 0 };
    // Break away from a job the host may run in, so the helper survives the
    // host's exit; fall back to a plain start where that is denied.
    // Never DETACHED_PROCESS: powershell.exe 5.1 started without any console
    // exits at once without running the script (C0.3.3 to C0.4.0 restart bug).
    // CREATE_NO_WINDOW gives it a hidden console of its own instead.
    BOOL ok = CreateProcessW(NULL, buf.data(), NULL, NULL, FALSE,
                             CREATE_NO_WINDOW | CREATE_BREAKAWAY_FROM_JOB,
                             NULL, NULL, &si, &pi);
    if (!ok)
        ok = CreateProcessW(NULL, buf.data(), NULL, NULL, FALSE, CREATE_NO_WINDOW,
                            NULL, NULL, &si, &pi);
    if (!ok)
    {
        FPLogW(L"[Store] %s helper could not start (%lu)", what, GetLastError());
        return false;
    }
    CloseHandle(pi.hThread);
    // The helper waits for this process, so an exit within the first second
    // means it did not run; report that instead of closing Power PDF for good.
    if (WaitForSingleObject(pi.hProcess, 1000) == WAIT_OBJECT_0)
    {
        DWORD code = 0;
        GetExitCodeProcess(pi.hProcess, &code);
        CloseHandle(pi.hProcess);
        FPLogW(L"[Store] %s helper exited at once (code %lu)", what, code);
        return false;
    }
    CloseHandle(pi.hProcess);
    FPLogW(L"[Store] %s requested, helper started", what);
    return true;
}

// Script lines shared by the restart and the self-update helper: log
// function, wait for this process, then for every PowerPDF.exe (a second
// instance still shutting down would swallow the new start via the
// single-instance hand-over). Generous timeout: the user may take a while
// with "save changes?" prompts.
static std::wstring HelperPrologue(const wchar_t* tag, const wchar_t* giveUp)
{
    wchar_t exe[MAX_PATH] = { 0 };
    GetModuleFileNameW(NULL, exe, MAX_PATH);
    wchar_t pid[16];
    swprintf_s(pid, 16, L"%lu", GetCurrentProcessId());
    return std::wstring() +
        L"$log = Join-Path $env:TEMP 'PluginStore.log'\r\n" +
        L"function L($m) { Add-Content -Path $log -Encoding UTF8 -Value ((Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff') + '  [" + tag + L"] ' + $m) }\r\n" +
        L"$p0 = " + pid + L"\r\n" +
        L"$exe = " + PsQuote(exe) + L"\r\n" +
        L"L ('helper waiting for pid ' + $p0)\r\n" +
        L"Wait-Process -Id $p0 -Timeout 900 -ErrorAction SilentlyContinue\r\n" +
        L"if (Get-Process -Id $p0 -ErrorAction SilentlyContinue) { L '" + giveUp + L"'; exit 1 }\r\n" +
        L"$deadline = (Get-Date).AddSeconds(60)\r\n" +
        L"while ((Get-Process -Name PowerPDF -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }\r\n";
}

bool PSScheduleRestart()
{
    wchar_t tempDir[MAX_PATH];
    GetTempPathW(MAX_PATH, tempDir);
    std::wstring script = HelperPrologue(L"Restart", L"Power PDF still running after 15 min, giving up") +
        L"if (Get-Process -Name PowerPDF -ErrorAction SilentlyContinue) { L 'another PowerPDF.exe is still running, not starting a second one'; exit 2 }\r\n" +
        L"Start-Sleep -Seconds 2\r\n" +
        L"try { Start-Process -FilePath $exe; L 'Power PDF restarted' } catch { L ('start failed: ' + $_.Exception.Message) }\r\n";

    std::wstring scriptPath = std::wstring(tempDir) + L"psrestart.ps1";
    if (!WriteTextFile(scriptPath, script)) return false;
    return LaunchHelper(scriptPath, L"restart");
}

int PSCompareVersions(const std::wstring& a, const std::wstring& b)
{
    size_t ia = 0, ib = 0;
    for (int seg = 0; seg < 4; ++seg)
    {
        unsigned long va = 0, vb = 0;
        if (ia < a.size()) va = wcstoul(a.c_str() + ia, nullptr, 10);
        if (ib < b.size()) vb = wcstoul(b.c_str() + ib, nullptr, 10);
        if (va != vb) return va < vb ? -1 : 1;
        ia = a.find(L'.', ia); ia = ia == std::wstring::npos ? a.size() : ia + 1;
        ib = b.find(L'.', ib); ib = ib == std::wstring::npos ? b.size() : ib + 1;
    }
    return 0;
}

// Helper script of the self-update: wait until Power PDF has exited, extract
// the MSI in user context, install with a progress bar (Windows asks for
// elevation), clean up and start Power PDF again, also when the installation
// was cancelled.
std::wstring PSSelfUpdateScript(const std::wstring& ppak, const std::wstring& outDir)
{
    return HelperPrologue(L"SelfUpdate", L"Power PDF still running after 15 min, update not installed") +
        L"$ppak = " + PsQuote(ppak) + L"\r\n" +
        L"$out = " + PsQuote(outDir) + L"\r\n" +
        L"try {\r\n" +
        L"  if (Test-Path $out) { Remove-Item $out -Recurse -Force }\r\n" +
        L"  Add-Type -AssemblyName System.IO.Compression.FileSystem\r\n" +
        L"  [System.IO.Compression.ZipFile]::ExtractToDirectory($ppak, $out)\r\n" +
        L"  $msi = Get-ChildItem (Join-Path $out 'installer') -Filter '*.msi' | Select-Object -First 1\r\n" +
        L"  if (-not $msi) { throw 'no MSI in the package' }\r\n" +
        L"  $mlog = Join-Path $env:TEMP 'AddonStoreUpdate.log'\r\n" +
        L"  L ('installing ' + $msi.Name)\r\n" +
        L"  $p = Start-Process msiexec.exe -ArgumentList @('/i', ('\"' + $msi.FullName + '\"'), '/passive', '/norestart', '/l*v', ('\"' + $mlog + '\"')) -Wait -PassThru\r\n" +
        L"  L ('msiexec exit code ' + $p.ExitCode + ' (0 = ok, 3010 = ok, 1602 = cancelled)')\r\n" +
        L"} catch { L ('update failed: ' + $_.Exception.Message) }\r\n" +
        L"Remove-Item $ppak -Force -ErrorAction SilentlyContinue\r\n" +
        L"Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue\r\n" +
        L"if (-not (Get-Process -Name PowerPDF -ErrorAction SilentlyContinue)) {\r\n" +
        L"  Start-Sleep -Seconds 2\r\n" +
        L"  try { Start-Process -FilePath $exe; L 'Power PDF restarted' } catch { L ('start failed: ' + $_.Exception.Message) }\r\n" +
        L"}\r\n";
}

int PSSelfUpdate(const PSCatalogEntry& e, HWND owner)
{
    wchar_t tempDir[MAX_PATH];
    GetTempPathW(MAX_PATH, tempDir);
    std::wstring ppak = std::wstring(tempDir) + e.id + L"-" + e.version + L".ppak";

    DWORD status = 0;
    if (!PSHttpGetFile(e.downloadUrl, ppak, &status)) return 1;
    if (_wcsicmp(Sha256File(ppak).c_str(), e.sha256.c_str()) != 0)
    {
        DeleteFileW(ppak.c_str());
        return 2;
    }

    // The MSI cannot replace PluginStore.zxt while Power PDF has it loaded, so
    // Power PDF closes first; ask before that happens.
    if (MessageBoxW(owner, FPLoc(IDS_PSD_ASK_SELFUPD).c_str(), FPLoc(IDS_PSD_TITLE).c_str(),
                    MB_YESNO | MB_ICONQUESTION) != IDYES)
    {
        DeleteFileW(ppak.c_str());
        return 5;
    }

    std::wstring outDir = std::wstring(tempDir) + L"PluginStoreUpdate-" + e.version;
    std::wstring script = PSSelfUpdateScript(ppak, outDir);
    std::wstring scriptPath = std::wstring(tempDir) + L"psselfupdate.ps1";
    int result = 4;
    if (WriteTextFile(scriptPath, script) && LaunchHelper(scriptPath, L"self-update"))
        result = 0;
    else
        DeleteFileW(ppak.c_str());
    FPLogW(L"[Store] self-update to %s -> %d", e.version.c_str(), result);
    return result;
}

int PSUninstallPackage(const std::wstring& zxtName, HWND owner)
{
    std::wstring pluginsDir = PluginsDir();
    if (pluginsDir.empty()) return 5;

    // One elevated step removes the Program-Files part; the HKCU settings key
    // is removed afterwards in USER context (elevated processes can resolve the
    // wrong profile, a lesson learned the hard way).
    std::wstring script = std::wstring() +
        L"$ErrorActionPreference='Stop'\r\n" +
        L"$plugins='" + pluginsDir + L"'\r\n" +
        L"$name='" + zxtName + L"'\r\n" +
        L"Get-ChildItem $plugins -Filter '*.zxt.old-*' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue\r\n" +
        L"$zxt=Join-Path $plugins ($name + '.zxt')\r\n" +
        L"if(Test-Path $zxt){ try { Remove-Item $zxt -Force } catch { Rename-Item $zxt ($name + '.zxt.old-' + [guid]::NewGuid().ToString('N')) } }\r\n" +
        L"$data=Join-Path $plugins $name\r\n" +
        L"if(Test-Path $data){ Remove-Item $data -Recurse -Force }\r\n" +
        L"exit 0\r\n";

    wchar_t tempDir[MAX_PATH];
    GetTempPathW(MAX_PATH, tempDir);
    std::wstring scriptPath = std::wstring(tempDir) + L"psuninstall-" + zxtName + L".ps1";
    if (!WriteTextFile(scriptPath, script)) return 4;

    std::wstring args = L"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + scriptPath + L"\"";

    SHELLEXECUTEINFOW sei = { sizeof(sei) };
    sei.fMask = SEE_MASK_NOCLOSEPROCESS;
    sei.hwnd = owner;
    sei.lpVerb = L"runas";
    sei.lpFile = L"powershell.exe";
    sei.lpParameters = args.c_str();
    sei.nShow = SW_HIDE;

    int result;
    if (!ShellExecuteExW(&sei) || !sei.hProcess)
    {
        result = 3;
    }
    else
    {
        WaitForSingleObject(sei.hProcess, 60000);
        DWORD exitCode = 1;
        GetExitCodeProcess(sei.hProcess, &exitCode);
        CloseHandle(sei.hProcess);
        result = exitCode == 0 ? 0 : 4;
    }
    DeleteFileW(scriptPath.c_str());

    if (result == 0)
    {
        std::wstring key = L"Software\\Kofax\\PDF\\Tungsten Power PDF\\" + zxtName;
        RegDeleteTreeW(HKEY_CURRENT_USER, key.c_str());
    }

    FPLogW(L"[Store] uninstall %s -> %d", zxtName.c_str(), result);
    return result;
}
