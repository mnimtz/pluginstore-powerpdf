@echo off
setlocal
for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do set MSB=%%i
"%MSB%" /nologo /v:m /p:Configuration=Release /p:Platform=x64 "%~dp0PluginStore.vcxproj"
set RC1=%ERRORLEVEL%
"%MSB%" /nologo /v:m /p:Configuration=Release /p:Platform=x64 "%~dp0link\AddonStoreLink.vcxproj"
set RC2=%ERRORLEVEL%
set /a MSB_RC=RC1+RC2
echo MSB_EXIT=%MSB_RC%
exit /b %MSB_RC%
