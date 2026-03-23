$ErrorActionPreference = "Stop"

Write-Host "== Spyder dist build =="
$root = Split-Path -Parent $PSCommandPath
Set-Location $root

function Run-Cmd($cmd) {
  Write-Host ">> $cmd"
  $p = Start-Process -FilePath "cmd.exe" -ArgumentList "/c $cmd" -NoNewWindow -PassThru -Wait
  if ($p.ExitCode -ne 0) { throw "Command failed: $cmd" }
}

# Build native hooks (require Developer Command Prompt environment with cl)
if (Test-Path "$root\VclHook\BuildHook-x86.bat") {
  & "$root\VclHook\BuildHook-x86.bat"
  if ($LASTEXITCODE -ne 0) { throw "VclHook-x86 build failed. Run from 'Developer Command Prompt for VS'." }
} else {
  Write-Warning "BuildHook-x86.bat not found"
}
if (Test-Path "$root\VclHook\BuildHook-x64.bat") {
  & "$root\VclHook\BuildHook-x64.bat"
  if ($LASTEXITCODE -ne 0) { throw "VclHook-x64 build failed. Run from 'x64 Native Tools Command Prompt for VS'." }
} else {
  Write-Warning "BuildHook-x64.bat not found"
}

# Clean dist
if (Test-Path "$root\dist") { Remove-Item -Recurse -Force "$root\dist" }
New-Item -ItemType Directory "$root\dist\x86" | Out-Null
New-Item -ItemType Directory "$root\dist\x64" | Out-Null

# Publish x86
dotnet publish "$root\Spy.UI\Spyder.csproj" -c Release -r win-x86 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o "$root\dist\x86"

# Publish x64
dotnet publish "$root\Spy.UI\Spyder.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o "$root\dist\x64"

# Ensure correct hook DLL placed
if (Test-Path "$root\VclHook\VclHook32.dll") {
  Copy-Item "$root\VclHook\VclHook32.dll" "$root\dist\x86\VclHook32.dll" -Force
}
if (Test-Path "$root\VclHook\VclHook64.dll") {
  Copy-Item "$root\VclHook\VclHook64.dll" "$root\dist\x64\VclHook64.dll" -Force
}
# Remove wrong-arch DLLs if they slipped in
if (Test-Path "$root\dist\x86\VclHook64.dll") { Remove-Item "$root\dist\x86\VclHook64.dll" -Force }
if (Test-Path "$root\dist\x64\VclHook32.dll") { Remove-Item "$root\dist\x64\VclHook32.dll" -Force }

Write-Host "== Dist ready =="
Write-Host " - $root\dist\x86\Spy.UI.exe + VclHook32.dll"
Write-Host " - $root\dist\x64\Spy.UI.exe + VclHook64.dll"
