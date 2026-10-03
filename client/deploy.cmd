@echo off
rem ---------------------------------------------------------------------------
rem  Plugin-Store client - developer deploy (run as Administrator).
rem  Copies the .zxt and the Plug-ins\PluginStore folder into the local
rem  Power PDF installation. Later this client ships once via MSI and then
rem  updates itself through the store.
rem ---------------------------------------------------------------------------
setlocal
set "SRC=%~dp0"

rem Resolve the Power PDF root from the App Paths entry (never hardcode - the
rem folder carries the version and moves between machines).
set "PPDF="
for /f "usebackq tokens=2,*" %%a in (`reg query "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\PowerPDF.exe" /v Path 2^>nul`) do set "PPDF=%%b"
if not defined PPDF (
  for /f "usebackq tokens=2,*" %%a in (`reg query "HKLM\SOFTWARE\Kofax\PDF" /v InstallDir 2^>nul`) do set "PPDF=%%bbin"
)
if not defined PPDF (
  echo Power PDF nicht gefunden.
  exit /b 2
)
set "DST=%PPDF%\Plug-Ins"
if not exist "%DST%" set "DST=%PPDF%Plug-Ins"

echo  Deploying Plugin-Store client to %DST% ...
copy /Y "%SRC%Release\PluginStore.zxt" "%DST%\" || goto :fail
xcopy /E /I /Y "%SRC%Plug-ins\PluginStore" "%DST%\PluginStore" || goto :fail

echo.
echo Fertig. Power PDF starten - Gruppe "Plugin-Store" erscheint im Tab
echo "Erweiterte Funktionen". Server-URL unter Datei ^> Optionen ^> Plugin-Store
echo (Standard: https://pluginstore.azurewebsites.net).
echo    %DST%\PluginStore.zxt
echo    %DST%\PluginStore\...
exit /b 0
:fail
echo FEHLER - Power PDF geschlossen? Als Administrator gestartet?
exit /b 1
