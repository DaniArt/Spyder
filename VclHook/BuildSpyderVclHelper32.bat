@echo off
setlocal
cd /d "%~dp0"

set "RSVARS="
if exist "%ProgramFiles(x86)%\Embarcadero\Studio\23.0\bin\rsvars.bat" set "RSVARS=%ProgramFiles(x86)%\Embarcadero\Studio\23.0\bin\rsvars.bat"
if exist "%ProgramFiles(x86)%\Embarcadero\Studio\22.0\bin\rsvars.bat" set "RSVARS=%ProgramFiles(x86)%\Embarcadero\Studio\22.0\bin\rsvars.bat"
if exist "%ProgramFiles(x86)%\Embarcadero\Studio\21.0\bin\rsvars.bat" set "RSVARS=%ProgramFiles(x86)%\Embarcadero\Studio\21.0\bin\rsvars.bat"

if not "%RSVARS%"=="" (
  call "%RSVARS%"
)

where dcc32 >nul 2>nul
if errorlevel 1 (
  echo dcc32 not found. Install Delphi and run again.
  exit /b 1
)

dcc32 -B -Q -E"%~dp0" "SpyderVclHelper32.dpr"
if errorlevel 1 exit /b 1

if not exist "%~dp0SpyderVclHelper32.dll" (
  echo SpyderVclHelper32.dll not produced.
  exit /b 1
)

echo Built SpyderVclHelper32.dll
endlocal
