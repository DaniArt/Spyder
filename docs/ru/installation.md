# Сборка и установка

Spyder — Windows-проект: managed (WPF + .NET) и опциональный нативный слой для VCL интроспекции.

## Требования

## Managed (обязательно)
- Windows
- .NET SDK (таргет `net8.0-windows`)

## Native VCL (опционально)
Если нужна VCL интроспекция:
- C++ toolchain для сборки DLL в `VclHook/`
- Delphi toolchain для `SpyderVclHelper32.dll`

## Сборка managed

Из корня репозитория:

```powershell
dotnet build .\Spy.UI\Spyder.csproj -c Debug
```

## Сборка VCL слоя

Из `VclHook/`:

```powershell
.\BuildSpyderVclHelper32.bat
.\BuildAll-x86.bat
```

Артефакты:
- `VclHook\SpyderVclHelper32.dll`
- `VclHook\VclHook32.dll`
- `VclHook\VclHookLoader32.dll`

## Публикация (self-contained)

Из корня репозитория:

```powershell
dotnet publish .\Spy.UI\Spyder.csproj -c Release -r win-x86 --self-contained true -p:PublishSingleFile=true
```

Папка publish:
- `Spy.UI\bin\Release\net8.0-windows\win-x86\publish\Spyder.exe`

Рядом с `Spyder.exe` должны лежать:
- `VclHook32.dll`
- `VclHookLoader32.dll`
- `SpyderVclHelper32.dll`

## Логи

Файл лога (при включении):

`%LOCALAPPDATA%\Spyder\logs\spyder.log`

## Типовые проблемы

## Несовпадение битности
- Для 32-bit цели используй x86 сборку.
- Для 64-bit цели используй x64 (если доступно).

## Publish “Access denied”
Если `Spyder.exe` открыт, publish может упасть:

```powershell
Get-Process Spyder -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item -Recurse -Force .\Spy.UI\bin\Release\net8.0-windows\win-x86\publish\* -ErrorAction SilentlyContinue
dotnet publish .\Spy.UI\Spyder.csproj -c Release -r win-x86 --self-contained true -p:PublishSingleFile=true
```
