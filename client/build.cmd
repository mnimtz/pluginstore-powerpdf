@echo off
setlocal
for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do set MSB=%%i
rem C1.9.11: the store window uses the Tungsten Plug-in UI (third_party\tpui): check ui\store.src.html,
rem then inline it into ui\store.html, the RCDATA page
python "%~dp0third_party\tpui\tools\check_ui.py" "%~dp0ui\store.src.html" >nul || ( python "%~dp0third_party\tpui\tools\check_ui.py" "%~dp0ui\store.src.html" & echo MSB_EXIT=1 & exit /b 1 )
python "%~dp0third_party\tpui\tools\inline.py" "%~dp0ui\store.src.html" "%~dp0ui\store.html" >nul || ( echo MSB_EXIT=1 & exit /b 1 )
"%MSB%" /nologo /v:m /p:Configuration=Release /p:Platform=x64 "%~dp0PluginStore.vcxproj"
set RC1=%ERRORLEVEL%
"%MSB%" /nologo /v:m /p:Configuration=Release /p:Platform=x64 "%~dp0link\AddonStoreLink.vcxproj"
set RC2=%ERRORLEVEL%
set /a MSB_RC=RC1+RC2
echo MSB_EXIT=%MSB_RC%
exit /b %MSB_RC%
