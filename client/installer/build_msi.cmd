@echo off
rem Builds PluginStore-<version>.msi. Version comes from common\version.h - bump
rem it on every release, never ship an unchanged number.
rem Needs the WiX v3 binaries in C:\Claude\Tools\wix314 (candle.exe / light.exe).
setlocal
set WIX=C:\Claude\Tools\wix314
set PROJ=%~dp0..

if not exist "%WIX%\candle.exe" (
  echo WiX not found in %WIX%
  exit /b 2
)
if not exist "%PROJ%\Release\PluginStore.zxt" (
  echo Plug-in not built - run build.cmd first.
  exit /b 3
)

for /f "usebackq delims=" %%v in (`python "%~dp0get_version.py"`) do set FPVER=%%v
if "%FPVER%"=="" (
  echo Version not found - check common\version.h.
  exit /b 4
)
echo Version: %FPVER%

if not exist "%~dp0Release" mkdir "%~dp0Release"
"%WIX%\candle.exe" -nologo -arch x64 -ext WixUtilExtension -ext WixUIExtension ^
    -dProjectDir="%PROJ%" -dFPVersion=%FPVER% ^
    -out "%~dp0Product.wixobj" "%~dp0Product.wxs" || exit /b 1
"%WIX%\light.exe" -nologo -ext WixUtilExtension -ext WixUIExtension -sw1076 ^
    -out "%~dp0Release\PluginStore-%FPVER%.msi" "%~dp0Product.wixobj" || exit /b 1

echo OK. MSI: %~dp0Release\PluginStore-%FPVER%.msi
exit /b 0
