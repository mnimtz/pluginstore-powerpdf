// pluginstore_bin.h - loads an add-on's own DLLs from Plug-Ins\<Name>\bin\ (Add-on Store S1.4.0).
//
// MIT License. Copyright (c) 2026 Tungsten Automation.
// Permission is hereby granted, free of charge, to any person obtaining a copy of this file,
// to deal in it without restriction, including the rights to use, copy, modify, merge,
// publish, distribute, sublicense and/or sell copies, subject to this notice being kept.
// THE FILE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND.
//
// Why: Windows looks for the DLLs a plug-in imports next to PowerPDF.exe, never in the
// Plug-Ins folder. The store installs your DLLs (bin/ in the .ppak, listed in files.bin)
// to Plug-Ins\<Name>\bin\; this header loads them from there by full path.
//
// Use:
//   1. Put your DLLs into bin/ of the package and list them in the manifest:
//        "files": { "x64": "x64/MyPlugin.zxt", "bin": ["bin/MyPlugin_core.dll"] }
//      Give every DLL a name of its own (e.g. your plug-in as prefix): all add-ons share
//      one Power PDF process, and the store refuses names another add-on already uses.
//   2. Link the .zxt with /DELAYLOAD:MyPlugin_core.dll (one per DLL) and delayimp.lib.
//      A direct import would make Power PDF fail to load the plug-in (BIN_IMPORT_NOT_DELAYED).
//   3. In exactly ONE .cpp file of the .zxt:
//        #define PLUGINSTORE_BIN_IMPLEMENT
//        #include "pluginstore_bin.h"
//      Every other file may include the header without the define.
//
// A delay-loaded DLL that exists in bin\ is then loaded from there; its own dependencies
// are found in bin\ as well (LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR). Every other delay-loaded
// DLL loads as usual. Never call SetDllDirectory or SetDefaultDllDirectories: they change
// the search for the whole Power PDF process and break other plug-ins.
//
// Explicit loading:  HMODULE h = PluginStoreBinLoad(L"MyPlugin_core.dll");
// Own delay-load hook: define PLUGINSTORE_BIN_OWN_HOOK before the include and call
// PluginStoreBinDelayHook(notify, info) from your hook for dliNotePreLoadLibrary.

#pragma once
#include <windows.h>
#include <wchar.h>

// Full path Plug-Ins\<Name>\bin\<file> for the .zxt that contains this code.
inline bool PluginStoreBinPath(const wchar_t* file, wchar_t* out, size_t cch)
{
    if (!file || !*file || wcschr(file, L'\\') || wcschr(file, L'/') || wcschr(file, L':') || !out || cch == 0) return false;
    HMODULE self = NULL;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            reinterpret_cast<LPCWSTR>(&PluginStoreBinPath), &self))
        return false;
    wchar_t mod[MAX_PATH * 2] = { 0 };
    DWORD n = GetModuleFileNameW(self, mod, MAX_PATH * 2);
    if (n == 0 || n >= MAX_PATH * 2) return false;
    // ...\Plug-Ins\<Name>.zxt  ->  ...\Plug-Ins\<Name>\bin\<file>
    wchar_t* dot = wcsrchr(mod, L'.');
    wchar_t* slash = wcsrchr(mod, L'\\');
    if (!dot || (slash && dot < slash)) return false;
    *dot = 0;
    return wcscpy_s(out, cch, mod) == 0 && wcscat_s(out, cch, L"\\bin\\") == 0 && wcscat_s(out, cch, file) == 0;
}

// Loads <file> from Plug-Ins\<Name>\bin\ (NULL when it is not there or does not load).
inline HMODULE PluginStoreBinLoad(const wchar_t* file)
{
    wchar_t path[MAX_PATH * 2];
    if (!PluginStoreBinPath(file, path, MAX_PATH * 2)) return NULL;
    if (GetFileAttributesW(path) == INVALID_FILE_ATTRIBUTES) return NULL;
    return LoadLibraryExW(path, NULL, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
}

#ifdef PLUGINSTORE_BIN_IMPLEMENT
#include <delayimp.h>

// Delay-load hook: DLLs in bin\ by full path, everything else as usual (NULL).
FARPROC WINAPI PluginStoreBinDelayHook(unsigned notify, PDelayLoadInfo info)
{
    if (notify != dliNotePreLoadLibrary || !info || !info->szDll) return NULL;
    wchar_t name[MAX_PATH] = { 0 };
    if (MultiByteToWideChar(CP_ACP, 0, info->szDll, -1, name, MAX_PATH) == 0) return NULL;
    return reinterpret_cast<FARPROC>(PluginStoreBinLoad(name));
}

#ifndef PLUGINSTORE_BIN_OWN_HOOK
extern "C" const PfnDliHook __pfnDliNotifyHook2 = PluginStoreBinDelayHook;
#endif
#endif
