@echo off
rem Installs this add-on into Tungsten Power PDF (asks once for administrator rights).
rem Without questions, e.g. for software distribution:  install.bat /quiet
setlocal
set "QUIET="
if /i "%~1"=="/quiet" set "QUIET=-Quiet"
if /i "%~1"=="-quiet" set "QUIET=-Quiet"
if not exist "%~dp0AddonSetup.ps1" goto :notextracted
if not exist "%~dp0package.ppak" goto :notextracted
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0AddonSetup.ps1" -Action Install -Source "%~dp0." %QUIET%
exit /b %ERRORLEVEL%

:notextracted
rem started from inside the ZIP (Explorer copies only this file to a temporary folder)
echo Please extract the whole ZIP file first (right-click, "Extract All"), then start install.bat from the extracted folder.
echo Bitte entpacken Sie zuerst die ganze ZIP-Datei (Rechtsklick, "Alle extrahieren") und starten Sie dann install.bat im entpackten Ordner.
if not defined QUIET pause
exit /b 8
