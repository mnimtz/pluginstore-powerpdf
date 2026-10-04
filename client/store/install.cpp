// install.cpp - see install.h.

#include "stdafx.h"
#include "install.h"
#include "catalog.h"
#include "http.h"
#include "powerpdfpath.h"
#include "logging.h"
#include "settings.h"
#include "signature.h"
#include "loc.h"
#include "Resource.h"
#include <bcrypt.h>
#include <shellapi.h>
#include <wincrypt.h>
#include <vector>

#pragma comment(lib, "bcrypt.lib")
#pragma comment(lib, "crypt32.lib")

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
    MultiByteToWideChar(CP_UTF8, 0, v.c_str(), -1, w, 511);
    return w;
}

std::wstring PSInstalledVersion(const std::wstring& zxtName)
{
    std::wstring dir = PluginsDir();
    if (dir.empty() || !PSIsValidZxtName(zxtName)) return L"";
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
    if (!json.empty() && (!ReadFile(f, json.data(), size, &got, NULL) || got != size)) json.clear();
    CloseHandle(f);
    return JsonValue(json, "version");
}

// ---------------------------------------------------------------------------
// PowerShell children: full path, script passed in memory (-EncodedCommand),
// so no script file exists that another process could change in between.
// ---------------------------------------------------------------------------

// PowerShell single-quoted literal: every single-quote character doubles
// (PowerShell also treats U+2018, U+2019, U+201A and U+201B as quotes).
// Download target in the user's TEMP, unique per run: two runs for the same
// package (a store window reopened while the first one still works) never
// share or delete each other's file.
static std::wstring TempPackagePath(const PSCatalogEntry& e)
{
    wchar_t tempDir[MAX_PATH] = { 0 };
    GetTempPathW(MAX_PATH, tempDir);
    GUID g = { 0 };
    wchar_t tag[40] = L"0";
    if (SUCCEEDED(CoCreateGuid(&g)))
        swprintf_s(tag, 40, L"%08lx%04x%04x", g.Data1, g.Data2, g.Data3);
    return std::wstring(tempDir) + e.id + L"-" + e.version + L"-" + tag + L".ppak";
}

static std::wstring PsQuote(const std::wstring& v)
{
    std::wstring r = L"'";
    for (wchar_t c : v)
    {
        r += c;
        if (c == L'\'' || c == 0x2018 || c == 0x2019 || c == 0x201A || c == 0x201B) r += c;
    }
    return r + L"'";
}

static std::wstring SystemPath(const wchar_t* rel)
{
    wchar_t sys[MAX_PATH] = { 0 };
    UINT n = GetSystemDirectoryW(sys, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) return std::wstring(L"C:\\Windows\\System32\\") + rel;
    return std::wstring(sys) + L"\\" + rel;
}

static std::wstring PowerShellExe() { return SystemPath(L"WindowsPowerShell\\v1.0\\powershell.exe"); }

// "-NoProfile ... -EncodedCommand <base64 of the UTF-16LE script>"
static std::wstring EncodedArgs(const std::wstring& script)
{
    const BYTE* p = reinterpret_cast<const BYTE*>(script.data());
    DWORD n = (DWORD)(script.size() * sizeof(wchar_t)), len = 0;
    std::wstring b64;
    if (CryptBinaryToStringW(p, n, CRYPT_STRING_BASE64 | CRYPT_STRING_NOCRLF, NULL, &len) && len > 0)
    {
        b64.assign(len, L'\0');
        if (!CryptBinaryToStringW(p, n, CRYPT_STRING_BASE64 | CRYPT_STRING_NOCRLF, &b64[0], &len)) b64.clear();
        else b64.resize(len);
    }
    return L"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + b64;
}

// The ONE UAC prompt. Returns false when the user declined; exit code in *code.
static bool RunElevated(const std::wstring& script, HWND owner, DWORD timeoutMs, DWORD* code)
{
    std::wstring exe = PowerShellExe(), args = EncodedArgs(script);
    SHELLEXECUTEINFOW sei = { sizeof(sei) };
    sei.fMask = SEE_MASK_NOCLOSEPROCESS;
    sei.hwnd = owner;
    sei.lpVerb = L"runas";
    sei.lpFile = exe.c_str();
    sei.lpParameters = args.c_str();
    sei.nShow = SW_HIDE;
    if (!ShellExecuteExW(&sei) || !sei.hProcess) return false;
    *code = 1;
    if (WaitForSingleObject(sei.hProcess, timeoutMs) == WAIT_OBJECT_0)
        GetExitCodeProcess(sei.hProcess, code);
    else
        FPLogW(L"[Store] elevated step did not finish within %lu s", timeoutMs / 1000);
    CloseHandle(sei.hProcess);
    return true;
}

int PSInstallPackage(const PSCatalogEntry& e, HWND owner)
{
    if (PSPolicyNoInstall())
    {
        FPLogW(L"[Store] install/remove blocked by policy DisableInstall");
        return 7;
    }
    if (!PSIsValidZxtName(e.zxtName)) return 4;
    if (!PSSignatureValid(e)) return 6;   // not signed by a trusted store key
    std::wstring pluginsDir = PluginsDir();
    if (pluginsDir.empty()) return 5;

    std::wstring ppak = TempPackagePath(e);

    // 1) download (user context); never more than the catalog announced
    DWORD status = 0;
    unsigned long long cap = e.sizeBytes > 0 ? e.sizeBytes + 65536 : 300ull * 1024 * 1024;
    if (!PSHttpGetFile(e.downloadUrl, ppak, &status, cap)) return 1;

    // 2) verify (user context): the catalog hash is authoritative
    std::wstring actual = Sha256File(ppak);
    if (_wcsicmp(actual.c_str(), e.sha256.c_str()) != 0)
    {
        FPLogW(L"[Store] hash mismatch for %s: %s != %s", e.id.c_str(), actual.c_str(), e.sha256.c_str());
        DeleteFileW(ppak.c_str());
        return 2;
    }

    // 3) ONE elevated PowerShell. It copies the package into a staging folder
    //    under Plug-Ins (writable for administrators only), checks the hash
    //    AGAIN there and only then extracts it, so nothing can swap the file
    //    in the user's TEMP folder between the check above and the install.
    //    x64 binary on every machine for now; Power PDF on Windows-on-ARM runs
    //    ARM64EC and loads x64 plug-ins (verified on an ARM64 machine).
    //    A plug-in that is loaded in the running Power PDF cannot be
    //    overwritten, but a loaded DLL CAN be renamed; the stale copy is swept
    //    on the next store operation.
    std::wstring sha = e.sha256;
    for (auto& c : sha) c = (wchar_t)towupper(c);
    std::wstring script = std::wstring() +
        L"$ErrorActionPreference='Stop'\r\n" +
        L"$src=" + PsQuote(ppak) + L"\r\n" +
        L"$plugins=" + PsQuote(pluginsDir) + L"\r\n" +
        L"$name=" + PsQuote(e.zxtName) + L"\r\n" +
        L"$sha=" + PsQuote(sha) + L"\r\n" +
        L"$rc=0\r\n" +
        L"$stage=Join-Path $plugins ('.psstage-'+[guid]::NewGuid().ToString('N'))\r\n" +
        L"try {\r\n" +
        L"  Add-Type -AssemblyName System.IO.Compression.FileSystem\r\n" +
        L"  New-Item -ItemType Directory -Path $stage | Out-Null\r\n" +
        L"  $pkg=Join-Path $stage 'package.ppak'\r\n" +
        L"  Copy-Item -LiteralPath $src -Destination $pkg -Force\r\n" +
        L"  if ((Get-FileHash -LiteralPath $pkg -Algorithm SHA256).Hash -ne $sha) { $rc=9 }\r\n" +
        L"  else {\r\n" +
        L"    $tmp=Join-Path $stage 'x'\r\n" +
        L"    [System.IO.Compression.ZipFile]::ExtractToDirectory($pkg,$tmp)\r\n" +
        L"    $bin=Join-Path $tmp ('x64\\'+$name+'.zxt')\r\n" +
        L"    if (-not (Test-Path -LiteralPath $bin)) { $rc=8 }\r\n" +
        L"    else {\r\n" +
        L"      Get-ChildItem -LiteralPath $plugins -Filter '*.zxt.old-*' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue\r\n" +
        L"      $target=Join-Path $plugins ($name+'.zxt')\r\n" +
        L"      if (Test-Path -LiteralPath $target) { try { Remove-Item -LiteralPath $target -Force } catch { Rename-Item -LiteralPath $target ($name+'.zxt.old-'+[guid]::NewGuid().ToString('N')) } }\r\n" +
        L"      Copy-Item -LiteralPath $bin -Destination $target -Force\r\n" +
        L"      $data=Join-Path $plugins $name\r\n" +
        L"      New-Item -ItemType Directory -Force -Path $data | Out-Null\r\n" +
        L"      Copy-Item -LiteralPath (Join-Path $tmp 'manifest.json') -Destination (Join-Path $data 'manifest.json') -Force\r\n" +
        // replace each extra folder as a whole (copying onto an existing one nests it)
        L"      foreach ($extra in @('assets','docs','UILayout')) {\r\n" +
        L"        $s=Join-Path $tmp $extra; $d=Join-Path $data $extra\r\n" +
        L"        if (Test-Path -LiteralPath $s) { if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force }; Copy-Item -LiteralPath $s -Destination $d -Recurse -Force }\r\n" +
        L"      }\r\n" +
        L"    }\r\n" +
        L"  }\r\n" +
        L"} catch { $rc=4 }\r\n" +
        L"finally { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }\r\n" +
        L"exit $rc\r\n";

    int result;
    DWORD code = 1;
    if (!RunElevated(script, owner, 180000, &code))
        result = 3;                        // user declined elevation
    else
        result = code == 0 ? 0 : code == 9 ? 2 : 4;
    if (code == 8) FPLogW(L"[Store] package %s has no x64\\%s.zxt", e.id.c_str(), e.zxtName.c_str());

    DeleteFileW(ppak.c_str());
    FPLogW(L"[Store] install %s %s -> %d", e.id.c_str(), e.version.c_str(), result);
    return result;
}

// Starts a PowerShell helper (user context) that must outlive this process.
static bool LaunchHelper(const std::wstring& script, const wchar_t* what)
{
    std::wstring cmd = L"\"" + PowerShellExe() + L"\" " + EncodedArgs(script);
    std::vector<wchar_t> buf(cmd.begin(), cmd.end());
    buf.push_back(0);
    std::wstring exe = PowerShellExe();
    STARTUPINFOW si = { sizeof(si) };
    si.dwFlags = STARTF_USESHOWWINDOW;
    si.wShowWindow = SW_HIDE;
    PROCESS_INFORMATION pi = { 0 };
    // Break away from a job the host may run in, so the helper survives the
    // host's exit; fall back to a plain start where that is denied.
    // Never DETACHED_PROCESS: powershell.exe 5.1 started without any console
    // exits at once without running the script (C0.3.3 to C0.4.0 restart bug).
    // CREATE_NO_WINDOW gives it a hidden console of its own instead.
    BOOL ok = CreateProcessW(exe.c_str(), buf.data(), NULL, NULL, FALSE,
                             CREATE_NO_WINDOW | CREATE_BREAKAWAY_FROM_JOB,
                             NULL, NULL, &si, &pi);
    if (!ok)
        ok = CreateProcessW(exe.c_str(), buf.data(), NULL, NULL, FALSE, CREATE_NO_WINDOW,
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
        L"if (Get-Process -Id $p0 -ErrorAction SilentlyContinue) { L " + PsQuote(giveUp) + L"; exit 1 }\r\n" +
        L"$deadline = (Get-Date).AddSeconds(60)\r\n" +
        L"while ((Get-Process -Name PowerPDF -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }\r\n";
}

bool PSScheduleRestart()
{
    std::wstring script = HelperPrologue(L"Restart", L"Power PDF still running after 15 min, giving up") +
        L"if (Get-Process -Name PowerPDF -ErrorAction SilentlyContinue) { L 'another PowerPDF.exe is still running, not starting a second one'; exit 2 }\r\n" +
        L"Start-Sleep -Seconds 2\r\n" +
        L"try { Start-Process -FilePath $exe; L 'Power PDF restarted' } catch { L ('start failed: ' + $_.Exception.Message) }\r\n";
    return LaunchHelper(script, L"restart");
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

// Helper script of the self-update (user context): wait until Power PDF has
// exited, then ONE elevated step copies the package into an admin-only
// staging folder under Plug-Ins, checks the hash there, unpacks it and runs
// the MSI from there with a progress bar (so no file the user can write is
// ever executed elevated); afterwards clean up and start Power PDF again,
// also when the installation was cancelled.
std::wstring PSSelfUpdateScript(const std::wstring& ppak, const std::wstring& sha256)
{
    // The installer shows its dialogs in Power PDF's language: the MSI carries all 16
    // languages as embedded transforms named by their LCID (C0.8.0).
    // Only names that exist: an unknown transform makes msiexec fail (1624).
    static const unsigned kEmbedded[] = { 1031, 1036, 1040, 3082, 1043, 1046, 1030, 1035, 1044, 1053, 1045, 1029, 1038, 1049, 1055 };
    unsigned lcid = 1033;
    for (unsigned l : kEmbedded) if (l == FPLocLangId()) lcid = l;
    std::wstring sha = sha256;
    for (auto& c : sha) c = (wchar_t)towupper(c);
    std::wstring elevated = std::wstring() +
        L"$ErrorActionPreference='Stop'\r\n" +
        L"$src=" + PsQuote(ppak) + L"\r\n" +
        L"$sha=" + PsQuote(sha) + L"\r\n" +
        L"$plugins=" + PsQuote(PluginsDir()) + L"\r\n" +
        // The MSI log goes to the Windows TEMP under a fresh name, never into a
        // folder the user can prepare (a link there would redirect the write
        // of an administrator).
        L"$mlog=Join-Path $env:windir ('Temp\\AddonStoreUpdate-'+[guid]::NewGuid().ToString('N')+'.log')\r\n" +
        L"$msiexec=Join-Path ([Environment]::GetFolderPath('System')) 'msiexec.exe'\r\n" +
        L"$lang=@(" + (lcid == 1033 ? std::wstring() : L"'TRANSFORMS=:" + std::to_wstring(lcid) + L"'") + L")\r\n" +
        L"$rc=1\r\n" +
        L"$stage=Join-Path $plugins ('.psupdate-'+[guid]::NewGuid().ToString('N'))\r\n" +
        L"try {\r\n" +
        L"  Add-Type -AssemblyName System.IO.Compression.FileSystem\r\n" +
        L"  New-Item -ItemType Directory -Path $stage | Out-Null\r\n" +
        L"  $pkg=Join-Path $stage 'package.ppak'\r\n" +
        L"  Copy-Item -LiteralPath $src -Destination $pkg -Force\r\n" +
        L"  if ((Get-FileHash -LiteralPath $pkg -Algorithm SHA256).Hash -ne $sha) { $rc=9 }\r\n" +
        L"  else {\r\n" +
        L"    $x=Join-Path $stage 'x'\r\n" +
        L"    [System.IO.Compression.ZipFile]::ExtractToDirectory($pkg,$x)\r\n" +
        L"    $msi=Get-ChildItem -LiteralPath (Join-Path $x 'installer') -Filter '*.msi' | Select-Object -First 1\r\n" +
        L"    if (-not $msi) { $rc=8 }\r\n" +
        L"    else { $p=Start-Process -FilePath $msiexec -ArgumentList (@('/i', ('\"'+$msi.FullName+'\"'), '/passive', '/norestart', '/l*v', ('\"'+$mlog+'\"')) + $lang) -Wait -PassThru; $rc=$p.ExitCode }\r\n" +
        L"  }\r\n" +
        L"} catch { $rc=4 }\r\n" +
        L"finally { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }\r\n" +
        L"exit $rc\r\n";
    return HelperPrologue(L"SelfUpdate", L"Power PDF still running after 15 min, update not installed") +
        L"$ppak = " + PsQuote(ppak) + L"\r\n" +
        L"$ps = Join-Path ([Environment]::GetFolderPath('System')) 'WindowsPowerShell\\v1.0\\powershell.exe'\r\n" +
        L"try {\r\n" +
        L"  L 'installing the update (one administrator confirmation)'\r\n" +
        L"  $p = Start-Process -FilePath $ps -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ArgumentList " +
            PsQuote(EncodedArgs(elevated)) + L"\r\n" +
        L"  L ('update step exit code ' + $p.ExitCode + ' (0 = ok, 3010 = ok, 1602 = cancelled, 9 = hash changed, 8 = no MSI)')\r\n" +
        L"} catch { L ('update not installed: ' + $_.Exception.Message) }\r\n" +
        L"Remove-Item -LiteralPath $ppak -Force -ErrorAction SilentlyContinue\r\n" +
        L"if (-not (Get-Process -Name PowerPDF -ErrorAction SilentlyContinue)) {\r\n" +
        L"  Start-Sleep -Seconds 2\r\n" +
        L"  try { Start-Process -FilePath $exe; L 'Power PDF restarted' } catch { L ('start failed: ' + $_.Exception.Message) }\r\n" +
        L"}\r\n";
}

int PSSelfUpdate(const PSCatalogEntry& e, HWND owner)
{
    if (PSPolicyNoSelfUpdate())
    {
        FPLogW(L"[Store] self-update blocked by policy DisableSelfUpdate");
        return 7;
    }
    if (!PSSignatureValid(e)) return 6;
    if (PluginsDir().empty()) return 8;
    std::wstring ppak = TempPackagePath(e);

    DWORD status = 0;
    unsigned long long cap = e.sizeBytes > 0 ? e.sizeBytes + 65536 : 300ull * 1024 * 1024;
    if (!PSHttpGetFile(e.downloadUrl, ppak, &status, cap)) return 1;
    if (_wcsicmp(Sha256File(ppak).c_str(), e.sha256.c_str()) != 0)
    {
        DeleteFileW(ppak.c_str());
        return 2;
    }

    // The MSI cannot replace PluginStore.zxt while Power PDF has it loaded, so
    // Power PDF closes first; ask before that happens ("No" is the default).
    if (MessageBoxW(owner, FPLoc(IDS_PSD_ASK_SELFUPD).c_str(), FPLoc(IDS_PSD_TITLE).c_str(),
                    MB_YESNO | MB_ICONQUESTION | MB_DEFBUTTON2) != IDYES)
    {
        DeleteFileW(ppak.c_str());
        return 5;
    }

    int result = 4;
    if (LaunchHelper(PSSelfUpdateScript(ppak, e.sha256), L"self-update"))
        result = 0;
    else
        DeleteFileW(ppak.c_str());
    FPLogW(L"[Store] self-update to %s -> %d", e.version.c_str(), result);
    return result;
}

int PSUninstallPackage(const std::wstring& zxtName, HWND owner)
{
    if (PSPolicyNoInstall())
    {
        FPLogW(L"[Store] install/remove blocked by policy DisableInstall");
        return 7;
    }
    if (!PSIsValidZxtName(zxtName)) return 4;
    // Only plug-ins with a store manifest (installed by the store or by one of
    // our MSIs) can be removed here: a catalog entry naming one of Power PDF's
    // own plug-ins must never offer to delete it.
    if (PSInstalledVersion(zxtName).empty())
    {
        FPLogW(L"[Store] %s has no store manifest, not removed", zxtName.c_str());
        return 4;
    }
    std::wstring pluginsDir = PluginsDir();
    if (pluginsDir.empty()) return 5;

    // One elevated step removes the Program-Files part; the HKCU settings key
    // is removed afterwards in USER context (elevated processes can resolve the
    // wrong profile, a lesson learned the hard way).
    std::wstring script = std::wstring() +
        L"$ErrorActionPreference='Stop'\r\n" +
        L"$plugins=" + PsQuote(pluginsDir) + L"\r\n" +
        L"$name=" + PsQuote(zxtName) + L"\r\n" +
        L"Get-ChildItem -LiteralPath $plugins -Filter '*.zxt.old-*' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue\r\n" +
        L"$zxt=Join-Path $plugins ($name + '.zxt')\r\n" +
        L"if (Test-Path -LiteralPath $zxt) { try { Remove-Item -LiteralPath $zxt -Force } catch { Rename-Item -LiteralPath $zxt ($name + '.zxt.old-' + [guid]::NewGuid().ToString('N')) } }\r\n" +
        L"$data=Join-Path $plugins $name\r\n" +
        L"if (Test-Path -LiteralPath $data) { Remove-Item -LiteralPath $data -Recurse -Force }\r\n" +
        L"exit 0\r\n";

    int result;
    DWORD code = 1;
    if (!RunElevated(script, owner, 60000, &code))
        result = 3;
    else
        result = code == 0 ? 0 : 4;

    if (result == 0)
    {
        std::wstring key = L"Software\\Kofax\\PDF\\Tungsten Power PDF\\" + zxtName;
        RegDeleteTreeW(HKEY_CURRENT_USER, key.c_str());
    }

    FPLogW(L"[Store] uninstall %s -> %d", zxtName.c_str(), result);
    return result;
}
