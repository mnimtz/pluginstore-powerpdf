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
cl /nologo /EHsc /std:c++17 /W4 /WX /permissive- /Fo"%HERE%out\\" /Fe"%HERE%out\manualentry_tests.exe" "%HERE%manualentry_tests.cpp" || exit /b 1
"%HERE%out\manualentry_tests.exe" || exit /b 1
cl /nologo /EHsc /std:c++17 /W4 /WX /permissive- /Fo"%HERE%out\\" /Fe"%HERE%out\servermode_tests.exe" "%HERE%servermode_tests.cpp" || exit /b 1
"%HERE%out\servermode_tests.exe" || exit /b 1
rem the log file with a long TEMP: the plug-in's own logging.cpp, copied next to the test output so
rem that its "stdafx.h" resolves to stubs\stdafx.h (no MFC, no Power PDF SDK)
copy /Y "%HERE%..\common\logging.cpp" "%HERE%out\logging_under_test.cpp" >nul || exit /b 1
cl /nologo /EHsc /std:c++17 /W4 /WX /permissive- /DUNICODE /D_UNICODE /I"%HERE%stubs" /I"%HERE%..\common" /Fo"%HERE%out\\" /Fe"%HERE%out\logging_tests.exe" "%HERE%logging_tests.cpp" "%HERE%out\logging_under_test.cpp" || exit /b 1
"%HERE%out\logging_tests.exe" || exit /b 1
cl /nologo /EHsc /std:c++17 /W4 /WX /permissive- /DUNICODE /D_UNICODE /Fo"%HERE%out\\" /Fe"%HERE%out\layoutdir_tests.exe" "%HERE%layoutdir_tests.cpp" || exit /b 1
"%HERE%out\layoutdir_tests.exe" || exit /b 1
rem C2.1.0: the central help (JSON, help.json, HTML allow list, pictures, file limits, junctions, installed helps)
cl /nologo /EHsc /std:c++17 /W4 /WX /permissive- /DUNICODE /D_UNICODE /Fo"%HERE%out\\" /Fe"%HERE%out\helpcore_tests.exe" "%HERE%helpcore_tests.cpp" crypt32.lib || exit /b 1
"%HERE%out\helpcore_tests.exe" || exit /b 1
rem C2.1.0: the help window from a modal dialog of an add-on (no owner, usable, does not block the dialog)
cl /nologo /EHsc /std:c++17 /W4 /WX /permissive- /DUNICODE /D_UNICODE /Fo"%HERE%out\\" /Fe"%HERE%out\modal_owner_test.exe" "%HERE%modal_owner_test.cpp" user32.lib || exit /b 1
"%HERE%out\modal_owner_test.exe" || exit /b 1
rem C2.1.0: the "Help" group on the shared tab of the user layout
cl /nologo /EHsc /std:c++17 /W4 /WX /permissive- /DUNICODE /D_UNICODE /Fo"%HERE%out\\" /Fe"%HERE%out\layout_help_test.exe" "%HERE%layout_help_test.cpp" advapi32.lib || exit /b 1
"%HERE%out\layout_help_test.exe" || exit /b 1
rem C2.1.0: the help page with a mock host in Edge headless (needs ui\help.html from build.cmd)
if exist "%HERE%..\ui\help.html" ( python -I "%HERE%help_page_test.py" || exit /b 1 )
rem optional code signing step (signing\sign.py): configuration checks and command lines, signs nothing
python "%HERE%signing_tests.py" "%HERE%out\failover_tests.exe" || exit /b 1
if /i not "%~1"=="live" goto :done
rem stubs\stdafx.h replaces MFC and the Power PDF SDK; the other headers are the plug-in's own
cl /nologo /EHsc /std:c++17 /W4 /permissive- /DUNICODE /D_UNICODE /I"%HERE%stubs" /I"%HERE%..\common" /Fo"%HERE%out\\" /Fe"%HERE%out\http_live.exe" "%HERE%http_live.cpp" "%HERE%..\store\http.cpp" winhttp.lib version.lib advapi32.lib user32.lib || exit /b 1
"%HERE%out\http_live.exe" || exit /b 1
:done
echo TESTS_OK
