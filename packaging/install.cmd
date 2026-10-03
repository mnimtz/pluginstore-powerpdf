@echo off
rem ---------------------------------------------------------------------------
rem  Plugin-Store for Tungsten Power PDF - first-time install.
rem  Right-click -> "Run as administrator" after extracting the downloaded ZIP.
rem  Afterwards the add-on updates itself and your plugins through the store.
rem ---------------------------------------------------------------------------
setlocal
set "SRC=%~dp0"

set "PPDF="
for /f "usebackq tokens=2,*" %%a in (`reg query "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\PowerPDF.exe" /v Path 2^>nul`) do set "PPDF=%%b"
if not defined PPDF (
  for /f "usebackq tokens=2,*" %%a in (`reg query "HKLM\SOFTWARE\Kofax\PDF" /v InstallDir 2^>nul`) do set "PPDF=%%bbin"
)
if not defined PPDF (
  echo Tungsten Power PDF was not found on this machine.
  pause
  exit /b 2
)
set "DST=%PPDF%\Plug-Ins"
if not exist "%DST%" set "DST=%PPDF%Plug-Ins"

echo Installing Plugin-Store to "%DST%" ...
copy /Y "%SRC%x64\PluginStore.zxt" "%DST%\" || goto :fail
if not exist "%DST%\PluginStore" mkdir "%DST%\PluginStore"
copy /Y "%SRC%manifest.json" "%DST%\PluginStore\" >nul
if exist "%SRC%UILayout" xcopy /E /I /Y "%SRC%UILayout" "%DST%\PluginStore\UILayout" >nul
if exist "%SRC%assets" xcopy /E /I /Y "%SRC%assets" "%DST%\PluginStore\assets" >nul

echo.
echo Done. Start Power PDF - the "Plugin-Store" group appears on the
echo "Enhanced Features" ribbon tab. Server URL and the beta channel can be
echo changed under File ^> Options ^> Plugin-Store.
pause
exit /b 0
:fail
echo FAILED - is Power PDF closed, and was this run as administrator?
pause
exit /b 1
