# Installation and Build

Spyder is a Windows-focused project composed of managed code (WPF + .NET) and an optional native subsystem for VCL introspection.

## Prerequisites

### Managed (required)
- Windows
- .NET SDK (project targets `net8.0-windows`)

### Native VCL introspection (optional)
If you need VCL introspection support, you also need:
- C++ toolchain compatible with the native projects in `VclHook/`
- Delphi toolchain for building `SpyderVclHelper32.dll`

## Build (Managed)

From repository root:

```powershell
dotnet build .\Spy.UI\Spyder.csproj -c Debug
```

## Build (Native VCL)

From `VclHook/`:

```powershell
.\BuildSpyderVclHelper32.bat
.\BuildAll-x86.bat
```

Artifacts (x86):
- `VclHook\SpyderVclHelper32.dll`
- `VclHook\VclHook32.dll`
- `VclHook\VclHookLoader32.dll`

## Publish (Self-contained)

From repository root:

```powershell
dotnet publish .\Spy.UI\Spyder.csproj -c Release -r win-x86 --self-contained true -p:PublishSingleFile=true
```

Publish output:
- `Spy.UI\bin\Release\net8.0-windows\win-x86\publish\Spyder.exe`

Place native DLLs next to `Spyder.exe` (same directory):
- `VclHook32.dll`
- `VclHookLoader32.dll`
- `SpyderVclHelper32.dll`

## Logging

When enabled, logs are written to:

`%LOCALAPPDATA%\Spyder\logs\spyder.log`

## Troubleshooting

### Bitness mismatch
Spyder must match the target process bitness for VCL introspection:
- Use x86 build for 32-bit targets.
- Use x64 build for 64-bit targets (when x64 artifacts are available).

### Publish fails with “Access denied”
The publish step may fail if `Spyder.exe` is still running:

```powershell
Get-Process Spyder -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item -Recurse -Force .\Spy.UI\bin\Release\net8.0-windows\win-x86\publish\* -ErrorAction SilentlyContinue
dotnet publish .\Spy.UI\Spyder.csproj -c Release -r win-x86 --self-contained true -p:PublishSingleFile=true
```
