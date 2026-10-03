@echo off
setlocal
for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do set MSB=%%i
"%MSB%" /nologo /v:m /p:Configuration=Release /p:Platform=x64 "%~dp0PluginStore.vcxproj"
echo MSB_EXIT=%ERRORLEVEL%
