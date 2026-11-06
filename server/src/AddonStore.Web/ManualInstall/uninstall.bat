@echo off
rem Removes this add-on from Tungsten Power PDF (asks once for administrator rights).
rem Without questions, e.g. for software distribution:  uninstall.bat /quiet
setlocal
set "QUIET="
if /i "%~1"=="/quiet" set "QUIET=-Quiet"
if /i "%~1"=="-quiet" set "QUIET=-Quiet"
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0AddonSetup.ps1" -Action Uninstall -Source "%~dp0." %QUIET%
exit /b %ERRORLEVEL%
