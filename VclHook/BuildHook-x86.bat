@echo off
setlocal
cd /d "%~dp0"
cl /LD /MT /nologo "VclHook.c" "VclBridge.c" /link /DEF:"VclHook.def" /OUT:"VclHook32.dll" user32.lib
if errorlevel 1 exit /b 1
echo Built VclHook32.dll
endlocal
