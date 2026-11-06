@echo off
rem Installs this add-on into Tungsten Power PDF (asks once for administrator rights).
rem Without questions, e.g. for software distribution:  install.bat /quiet
setlocal
set "QUIET="
if /i "%~1"=="/quiet" set "QUIET=-Quiet"
if /i "%~1"=="-quiet" set "QUIET=-Quiet"
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0AddonSetup.ps1" -Action Install -Source "%~dp0." %QUIET%
exit /b %ERRORLEVEL%
