// signature.cpp - see signature.h.

#include "stdafx.h"
#include "signature.h"
#include "catalog.h"
#include "policy.h"
#include "logging.h"
#include <bcrypt.h>
#include <wincrypt.h>
#include <vector>

#pragma comment(lib, "bcrypt.lib")
#pragma comment(lib, "crypt32.lib")

namespace {

struct PinnedKey { const wchar_t* id; const wchar_t* raw; };

// Public keys only (X||Y, base64). The private keys never leave the server
// and the offline store respectively.
const PinnedKey kBuiltIn[] = {
    // offline recovery key (used by the server only if its own key is lost)
    { L"f729dd9553a86cd1", L"H2GP5IfEAXH1x8RafnXP2bb3ZgrvNA8NpsMHVKytweKjTLTgBgUpf72MbzQgOLC//5gG7Wl+937JR+f8iqkP5g==" },
    // store instance https://addon.power-pdf.de (GET /api/signing-key, Oct 4, 2026)
    { L"6d2e832af60f35aa", L"Si6zOG8/Us2ZtBgEOWpTkkvdcbrL33xowWFepHvJAFJiKbuDB+uwhVzA1fP/gGfaoTAp83VvO9RyrXWZSH+8VQ==" },
};

std::vector<BYTE> Unbase64(const std::wstring& s)
{
    DWORD n = 0;
    if (!CryptStringToBinaryW(s.c_str(), (DWORD)s.size(), CRYPT_STRING_BASE64, NULL, &n, NULL, NULL)) return {};
    std::vector<BYTE> out(n);
    if (!CryptStringToBinaryW(s.c_str(), (DWORD)s.size(), CRYPT_STRING_BASE64, out.data(), &n, NULL, NULL)) return {};
    out.resize(n);
    return out;
}

std::string Utf8(const std::wstring& w)
{
    if (w.empty()) return std::string();
    int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), NULL, 0, NULL, NULL);
    std::string s(n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), &s[0], n, NULL, NULL);
    return s;
}

// Raw key (X||Y) for a key id: built in first, then the HKLM policy.
std::vector<BYTE> KeyFor(const std::wstring& id)
{
    for (const auto& k : kBuiltIn)
        if (id == k.id) return Unbase64(k.raw);
    // REG_SZ or REG_MULTI_SZ of any length (a company may trust many keys)
    std::wstring list;
    const wchar_t* key = L"Software\\Kofax\\PDF\\Tungsten Power PDF\\PluginStore\\Policies\\Store";
    const DWORD types = RRF_RT_REG_SZ | RRF_RT_REG_MULTI_SZ;
    DWORD sz = 0;
    LSTATUS st = RegGetValueW(HKEY_LOCAL_MACHINE, key, L"TrustedSigningKeys", types, NULL, NULL, &sz);
    if (st == ERROR_SUCCESS && sz > 0 && sz < 1024 * 1024)
    {
        std::vector<wchar_t> buf(sz / sizeof(wchar_t) + 2, L'\0');
        sz = (DWORD)(buf.size() * sizeof(wchar_t));
        st = RegGetValueW(HKEY_LOCAL_MACHINE, key, L"TrustedSigningKeys", types, NULL, buf.data(), &sz);
        if (st == ERROR_SUCCESS)
            for (const wchar_t* p = buf.data(); *p; p += wcslen(p) + 1) { list += p; list += L";"; }
    }
    if (st != ERROR_SUCCESS && st != ERROR_FILE_NOT_FOUND)
        FPLogW(L"[Store] policy TrustedSigningKeys could not be read (%ld)", (long)st);
    size_t pos = 0;
    while (pos < list.size())
    {
        size_t end = list.find_first_of(L";\r\n", pos);
        if (end == std::wstring::npos) end = list.size();
        std::wstring item = list.substr(pos, end - pos);
        pos = end + 1;
        size_t colon = item.find(L':');
        if (colon != std::wstring::npos && item.substr(0, colon) == id) return Unbase64(item.substr(colon + 1));
    }
    return {};
}

bool Verify(const std::vector<BYTE>& raw, const std::string& msg, const std::vector<BYTE>& sig)
{
    if (raw.size() != 64 || sig.size() != 64) return false;
    BCRYPT_ALG_HANDLE alg = NULL, sha = NULL;
    BCRYPT_KEY_HANDLE key = NULL;
    bool ok = false;
    do
    {
        if (BCryptOpenAlgorithmProvider(&alg, BCRYPT_ECDSA_P256_ALGORITHM, NULL, 0) != 0) break;
        std::vector<BYTE> blob(sizeof(BCRYPT_ECCKEY_BLOB) + 64);
        auto* hdr = reinterpret_cast<BCRYPT_ECCKEY_BLOB*>(blob.data());
        hdr->dwMagic = BCRYPT_ECDSA_PUBLIC_P256_MAGIC;
        hdr->cbKey = 32;
        memcpy(blob.data() + sizeof(BCRYPT_ECCKEY_BLOB), raw.data(), 64);
        if (BCryptImportKeyPair(alg, NULL, BCRYPT_ECCPUBLIC_BLOB, &key, blob.data(), (ULONG)blob.size(), 0) != 0) break;
        BYTE digest[32] = { 0 };
        if (BCryptOpenAlgorithmProvider(&sha, BCRYPT_SHA256_ALGORITHM, NULL, 0) != 0) break;
        BCRYPT_HASH_HANDLE hh = NULL;
        if (BCryptCreateHash(sha, &hh, NULL, 0, NULL, 0, 0) != 0) break;
        bool hashed = BCryptHashData(hh, (PUCHAR)msg.data(), (ULONG)msg.size(), 0) == 0 &&
                      BCryptFinishHash(hh, digest, sizeof(digest), 0) == 0;
        BCryptDestroyHash(hh);
        if (!hashed) break;
        ok = BCryptVerifySignature(key, NULL, digest, sizeof(digest), (PUCHAR)sig.data(), (ULONG)sig.size(), 0) == 0;
    } while (false);
    if (key) BCryptDestroyKey(key);
    if (sha) BCryptCloseAlgorithmProvider(sha, 0);
    if (alg) BCryptCloseAlgorithmProvider(alg, 0);
    return ok;
}

} // namespace

bool PSSignatureValid(const PSCatalogEntry& e)
{
    DWORD allow = 0;
    if (FPPolicyDword(L"Store", L"AllowUnsigned", allow) && allow != 0) return true;

    size_t colon = e.signature.find(L':');
    if (colon == std::wstring::npos)
    {
        FPLogW(L"[Store] %s %s is not signed", e.id.c_str(), e.version.c_str());
        return false;
    }
    std::wstring id = e.signature.substr(0, colon);
    std::vector<BYTE> raw = KeyFor(id);
    if (raw.empty())
    {
        FPLogW(L"[Store] %s %s is signed with an unknown key %.32s", e.id.c_str(), e.version.c_str(), id.c_str());
        return false;
    }
    std::wstring sha = e.sha256;
    for (auto& c : sha) c = (wchar_t)towlower(c);
    std::string msg = Utf8(L"addonstore-pkg-v2\n" + e.id + L"\n" + e.version + L"\n" + sha + L"\n" + e.zxtName);
    bool ok = Verify(raw, msg, Unbase64(e.signature.substr(colon + 1)));
    if (!ok) FPLogW(L"[Store] %s %s: signature does not match", e.id.c_str(), e.version.c_str());
    return ok;
}
