@echo off
rem Builds PluginStore-<version>.msi with all 16 languages (build_msi.py).
rem Version comes from common\version.h - bump it on every release, never
rem ship an unchanged number. Needs the WiX v3 binaries in C:\Claude\Tools\wix314
rem and Python 3.12 or older (msilib).
python "%~dp0build_msi.py"
exit /b %ERRORLEVEL%
