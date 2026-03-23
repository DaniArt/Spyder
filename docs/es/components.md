# Componentes

## Spy.UI (WPF UI)

Responsabilidades:
- Flujo de captura (hover/drag/hotkey).
- Overlay de resaltado.
- Paneles de metadatos por backend (Win32/UIA/MSAA/VCL).
- Copia de selector y persistencia de `locator.json`.
- Control de logging a archivo.

Integración:
- `Spy.Core.SpyCapture` para capturas.
- `Spy.Core.Vcl.VclHookManager` para lifecycle del hook VCL.
- `Spy.UI.VclHelperClient` para RPC por Named Pipe.

Archivos clave:
- `Spy.UI/MainWindow.xaml(.cs)`
- `Spy.UI/OverlayWindow.xaml(.cs)`
- `Spy.UI/GlobalMouseHook.cs`
- `Spy.UI/LocatorStorage.cs`
- `Spy.UI/VclHelperClient.cs`

## Spy.Core (Core)

Responsabilidades:
- Pipeline de captura y modelo `SpyElement`.
- Builder de selector y locator estructurado.
- Timeouts y fallbacks best-effort.
- Gestión del hook VCL y compatibilidad de bitness.

Archivos clave:
- `Spy.Core/SpyCapture.cs`
- `Spy.Core/SelectorBuilder.cs`
- `Spy.Core/Hierarchy.cs`
- `Spy.Core/Vcl/VclHookManager.cs`
- `Spy.Core/Vcl/VclLocatorEngine.cs`

## VclHook (Hook Layer)

Responsabilidades:
- Ejecutar dentro del proceso objetivo.
- Servidor Named Pipe (JSON RPC).
- Dispatch al hilo UI del objetivo.
- Hit-test, enumeración de hijos, path y propiedades.

Archivos clave:
- `VclHook/VclHook.c`
- `VclHook/VclBridge.c/.h`
- `VclHook/SpyderVclHelper32.dpr`

## IPC (Named Pipe)

- Cliente: `Spy.UI/VclHelperClient.cs`
- Servidor: `VclHook/VclHook.c`
- Formato: JSON con prefijo de longitud

Ver:
- `docs/es/pipe-protocol.md`

## Sistema de locators

- Locator estructurado: `Spy.Core/SelectorBuilder.cs`
- Persistencia: `Spy.UI/LocatorStorage.cs`

Ver:
- `docs/es/locator-format.md`
