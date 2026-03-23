call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvarsall.bat" x86
call BuildHook-x86.bat
if errorlevel 1 exit /b 1
call BuildHookLoader-x86.bat
