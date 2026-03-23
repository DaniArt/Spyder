call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvarsall.bat" x64
call BuildHook-x64.bat
if errorlevel 1 exit /b 1
call BuildHookLoader-x64.bat
