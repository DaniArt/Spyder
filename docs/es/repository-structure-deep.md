# Estructura del repositorio (profunda)

Este documento describe archivos clave y su rol. Los helpers se agrupan; el foco está en entry points e integraciones principales.

## Root

## `Spyder.slnx`
- Propósito: descriptor de solución para proyectos managed.

## `build-dist.ps1`
- Propósito: helper de publish/distribución.
- Interactúa con: `Spy.UI/Spyder.csproj`.

## `PROJECT_CONTEXT.md`
- Propósito: notas y restricciones.

## Spy.Core

## `Spy.Core/SpyCapture.cs`
- Propósito: pipeline de captura que produce `SpyElement`.
- Usado por: `Spy.UI/MainWindow.xaml.cs`.
- Interacciones: Win32 (siempre), UIA/MSAA con timeouts, VCL opcional.

## `Spy.Core/SelectorBuilder.cs`
- Propósito: construir selector en texto y definir `StructuredLocator` (schema de `locator.json`).
- Usado por: flujo de captura en UI.

## `Spy.Core/Hierarchy.cs`
- Propósito: modelo de jerarquía para presentación en UI.

## `Spy.Core/Vcl/VclHookManager.cs`
- Propósito: instalar/gestionar el hook VCL y validar bitness.
- Interacciones: `LoadLibrary`, `SetWindowsHookEx(WH_GETMESSAGE)`.

## `Spy.Core/Vcl/VclLocatorEngine.cs`
- Propósito: resolución resiliente de cadenas VCL y caché del árbol.

## Spy.UI

## `Spy.UI/MainWindow.xaml(.cs)`
- Propósito: flujo principal de captura, overlay, selector y persistencia.
- Interactúa con: `SpyCapture`, `SelectorBuilder`, `VclHookManager`, `VclHelperClient`.

## `Spy.UI/VclHelperClient.cs`
- Propósito: cliente Named Pipe para consultas VCL.

## `Spy.UI/LocatorStorage.cs`
- Propósito: schema + I/O de `locator.json`.

## `Spy.UI/OverlayWindow.xaml(.cs)`
- Propósito: overlay de resaltado (click-through).

## VclHook

## `VclHook/VclHook.c`
- Propósito: servidor pipe + dispatch a UI thread del objetivo; hook procedure export.

## `VclHook/VclBridge.c/.h`
- Propósito: hit-test VCL, children/path/properties, safe reads y logging.

## `VclHook/SpyderVclHelper32.dpr`
- Propósito: helper Delphi in-process para llamadas VCL seguras.
