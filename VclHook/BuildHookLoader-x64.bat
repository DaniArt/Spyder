@echo off
setlocal
cd /d "%~dp0"
cl /LD /MT /nologo "VclHookLoader.c" /link /DEF:"VclHookLoader.def" /OUT:"VclHookLoader64.dll" user32.lib
if errorlevel 1 exit /b 1
echo Built VclHookLoader64.dll
endlocal
