@echo off
setlocal
for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do set MSB=%%i
rem C1.9.11: the store window uses the Tungsten Plug-in UI (third_party\tpui): check ui\store.src.html,
rem then inline it into ui\store.html, the RCDATA page
python "%~dp0third_party\tpui\tools\check_ui.py" "%~dp0ui\store.src.html" >nul || ( python "%~dp0third_party\tpui\tools\check_ui.py" "%~dp0ui\store.src.html" & echo MSB_EXIT=1 & exit /b 1 )
python "%~dp0third_party\tpui\tools\inline.py" "%~dp0ui\store.src.html" "%~dp0ui\store.html" >nul || ( echo MSB_EXIT=1 & exit /b 1 )
rem C2.1.0: the central help page, same checks, inlined into ui\help.html (RCDATA IDR_HELP_HTML)
python "%~dp0third_party\tpui\tools\check_ui.py" "%~dp0ui\help.src.html" >nul || ( python "%~dp0third_party\tpui\tools\check_ui.py" "%~dp0ui\help.src.html" & echo MSB_EXIT=1 & exit /b 1 )
python "%~dp0third_party\tpui\tools\inline.py" "%~dp0ui\help.src.html" "%~dp0ui\help.html" >nul || ( echo MSB_EXIT=1 & exit /b 1 )
"%MSB%" /nologo /v:m /p:Configuration=Release /p:Platform=x64 "%~dp0PluginStore.vcxproj"
set RC1=%ERRORLEVEL%
"%MSB%" /nologo /v:m /p:Configuration=Release /p:Platform=x64 "%~dp0link\AddonStoreLink.vcxproj"
set RC2=%ERRORLEVEL%
set /a MSB_RC=RC1+RC2
if not "%MSB_RC%"=="0" ( echo MSB_EXIT=%MSB_RC% & exit /b %MSB_RC% )
rem Optional Authenticode signing (docs\code-signing.md): off unless a signing configuration exists
rem outside the repository; then it signs both binaries BEFORE the MSI packs them, and fails loudly.
python "%~dp0signing\sign.py" "%~dp0Release\PluginStore.zxt" "%~dp0link\Release\AddonStoreLink.exe"
if errorlevel 1 ( echo SIGN_EXIT=1 & echo MSB_EXIT=1 & exit /b 1 )
echo MSB_EXIT=%MSB_RC%
exit /b %MSB_RC%
