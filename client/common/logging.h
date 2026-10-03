// logging.h — ONE log for the whole pack: %TEMP%\PluginStore.log.
//
// Two levels:
//   FPLogA/FPLogW   — always written (sparse: one line per operation/error).
//   FPVLogA/FPVLogW — only with "extended logging" enabled
//                     (PluginStore\VerboseLog or Policies\Pack\VerboseLog)
//                     — the support switch: a user enables it, reproduces,
//                     sends ONE file.
//
// Every line carries a timestamp and the module prefix passed by the
// caller ("[WordPro] ...", "[CSDK] ...").

#pragma once

bool FPVerboseLog();
void FPLogSetVerbose(bool on);

void FPLogA(const char* fmt, ...);      // narrow printf (%S = wide arg)
void FPLogW(const wchar_t* fmt, ...);   // wide printf
void FPVLogA(const char* fmt, ...);     // verbose-gated
void FPVLogW(const wchar_t* fmt, ...);
