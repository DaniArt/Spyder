# Instalación y build

Spyder es un proyecto Windows con una parte managed (WPF + .NET) y un subsistema nativo opcional para introspección VCL.

## Requisitos

## Managed (requerido)
- Windows
- .NET SDK (target `net8.0-windows`)

## VCL nativo (opcional)
Si necesitas introspección VCL:
- toolchain C++ para `VclHook/`
- toolchain Delphi para `SpyderVclHelper32.dll`

## Build managed

Desde la raíz:

```powershell
dotnet build .\Spy.UI\Spyder.csproj -c Debug
```

## Build VCL (nativo)

Desde `VclHook/`:

```powershell
.\BuildSpyderVclHelper32.bat
.\BuildAll-x86.bat
```

Artefactos:
- `VclHook\SpyderVclHelper32.dll`
- `VclHook\VclHook32.dll`
- `VclHook\VclHookLoader32.dll`

## Publish (self-contained)

```powershell
dotnet publish .\Spy.UI\Spyder.csproj -c Release -r win-x86 --self-contained true -p:PublishSingleFile=true
```

Salida:
- `Spy.UI\bin\Release\net8.0-windows\win-x86\publish\Spyder.exe`

Coloca junto a `Spyder.exe`:
- `VclHook32.dll`
- `VclHookLoader32.dll`
- `SpyderVclHelper32.dll`

## Logs

`%LOCALAPPDATA%\Spyder\logs\spyder.log`

## Problemas comunes

## Bitness mismatch
- x86 para objetivos de 32-bit.
- x64 para objetivos de 64-bit (cuando esté disponible).

## Publish “Access denied”
Detén el proceso, limpia la carpeta y repite:

```powershell
Get-Process Spyder -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item -Recurse -Force .\Spy.UI\bin\Release\net8.0-windows\win-x86\publish\* -ErrorAction SilentlyContinue
dotnet publish .\Spy.UI\Spyder.csproj -c Release -r win-x86 --self-contained true -p:PublishSingleFile=true
```
