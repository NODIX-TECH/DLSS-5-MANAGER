@echo off
setlocal
cd /d "%~dp0"
call "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b 1
cl /nologo /O2 /EHsc /W3 /MD /std:c++17 /Iinclude verify_output.cpp /Fe:verify_output.exe /link SpoutDX.lib d3d11.lib
exit /b %errorlevel%
