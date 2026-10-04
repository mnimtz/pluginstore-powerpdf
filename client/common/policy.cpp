// policy.cpp — see policy.h.

#include "stdafx.h"
#include "policy.h"

static std::wstring PolicyPath(const wchar_t* section)
{
    std::wstring p =
        L"Software\\Kofax\\PDF\\Tungsten Power PDF\\PluginStore\\Policies\\";
    p += section;
    return p;
}

bool FPPolicyDword(const wchar_t* section, const wchar_t* name, DWORD& v)
{
    DWORD val = 0, sz = sizeof(val), ty = 0;
    if (RegGetValueW(HKEY_LOCAL_MACHINE, PolicyPath(section).c_str(), name,
                     RRF_RT_REG_DWORD, &ty, &val, &sz) == ERROR_SUCCESS)
    { v = val; return true; }
    // Admins sometimes deploy a policy as REG_SZ "1"; ignoring it would leave
    // the store open although IT locked it, so a decimal string counts too.
    wchar_t buf[32] = { 0 };
    sz = sizeof(buf);
    if (RegGetValueW(HKEY_LOCAL_MACHINE, PolicyPath(section).c_str(), name,
                     RRF_RT_REG_SZ, &ty, buf, &sz) == ERROR_SUCCESS)
    {
        wchar_t* end = nullptr;
        unsigned long n = wcstoul(buf, &end, 10);
        if (end && end != buf) { v = (DWORD)n; return true; }
    }
    return false;
}

bool FPPolicyString(const wchar_t* section, const wchar_t* name, std::wstring& v)
{
    wchar_t buf[1024] = { 0 };
    DWORD sz = sizeof(buf), ty = 0;
    if (RegGetValueW(HKEY_LOCAL_MACHINE, PolicyPath(section).c_str(), name,
                     RRF_RT_REG_SZ, &ty, buf, &sz) == ERROR_SUCCESS)
    { v = buf; return true; }
    return false;
}

bool FPPolicyLockPage(const wchar_t* section)
{
    DWORD v = 0;
    return FPPolicyDword(section, L"LockPage", v) && v != 0;
}

void FPDisableAllChildren(HWND page)
{
    if (!page) return;
    struct L {
        static BOOL CALLBACK Cb(HWND child, LPARAM)
        { EnableWindow(child, FALSE); return TRUE; }
    };
    EnumChildWindows(page, L::Cb, 0);
}
