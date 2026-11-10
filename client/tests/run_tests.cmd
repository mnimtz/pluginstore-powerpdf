@echo off
rem Client tests (C1.9.17): console programs without Power PDF.
rem   client\tests\run_tests.cmd        unit tests (no network)
rem   client\tests\run_tests.cmd live   also the real store\http.cpp against the real hosts
setlocal
set "HERE=%~dp0"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -property installationPath`) do set "VS=%%i"
if not defined VS ( echo Visual Studio not found & exit /b 1 )
rem vcvars64 changes the current directory: everything below uses absolute paths
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul 2>nul || exit /b 1
if not exist "%HERE%out" mkdir "%HERE%out"
cl /nologo /EHsc /std:c++17 /W4 /WX /permissive- /Fo"%HERE%out\\" /Fe"%HERE%out\failover_tests.exe" "%HERE%failover_tests.cpp" || exit /b 1
"%HERE%out\failover_tests.exe" || exit /b 1
if /i not "%~1"=="live" goto :done
rem stubs\stdafx.h replaces MFC and the Power PDF SDK; the other headers are the plug-in's own
cl /nologo /EHsc /std:c++17 /W4 /permissive- /DUNICODE /D_UNICODE /I"%HERE%stubs" /I"%HERE%..\common" /Fo"%HERE%out\\" /Fe"%HERE%out\http_live.exe" "%HERE%http_live.cpp" "%HERE%..\store\http.cpp" winhttp.lib version.lib advapi32.lib user32.lib || exit /b 1
"%HERE%out\http_live.exe" || exit /b 1
:done
echo TESTS_OK
