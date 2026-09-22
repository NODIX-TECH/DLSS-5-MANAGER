@echo off
setlocal
cd /d "%~dp0"
call "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b 1
cl /nologo /O2 /EHsc /W3 /MD /utf-8 /std:c++17 /Iinclude /LD ngx_forwarder.cpp /Fe:nvngx.dll_ns-forwarder.dll /link kernel32.lib d3d12.lib
if errorlevel 1 exit /b 1
cl /nologo /O2 /EHsc /W3 /MD /utf-8 /std:c++17 /Iinclude /Isrc screen_worker.cpp spout_bridge.cpp /Fe:nvngx.dll /link lib\Windows_x86_64\x64\nvsdk_ngx_d.lib SpoutDX.lib version.lib kernel32.lib user32.lib gdi32.lib advapi32.lib ole32.lib d3d11.lib d3d12.lib dxgi.lib d3dcompiler.lib WindowsApp.lib dwmapi.lib
exit /b %errorlevel%
