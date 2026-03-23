# Arquitectura

Spyder es una aplicación por capas: un pipeline managed (WPF + .NET) y un subsistema opcional nativo para introspección VCL dentro del proceso objetivo. El diseño es best-effort: si un backend falla, la captura continúa con los restantes.

## Capas

## UI Layer (Spy.UI)
- WPF UI y orquestación de captura (hover/drag/hotkey).
- Overlay para resaltado.
- Presentación de metadatos por backend y selectores.
- Persistencia de `locator.json` y controles de logging.

Archivos clave:
- `Spy.UI/MainWindow.xaml(.cs)`
- `Spy.UI/OverlayWindow.xaml(.cs)`
- `Spy.UI/GlobalMouseHook.cs`
- `Spy.UI/LocatorStorage.cs`

## Core Layer (Spy.Core)
- Pipeline de captura y modelos.
- Builder de selector y locator estructurado.
- Timeouts y fallbacks best-effort.
- Gestión del hook VCL (lifecycle y compatibilidad de bitness).

Archivos clave:
- `Spy.Core/SpyCapture.cs`
- `Spy.Core/SelectorBuilder.cs`
- `Spy.Core/Hierarchy.cs`
- `Spy.Core/Vcl/VclHookManager.cs`
- `Spy.Core/Vcl/VclLocatorEngine.cs`

## Hook Layer (VclHook)
- Corre dentro del proceso objetivo.
- Servidor Named Pipe (RPC).
- Dispatch al hilo UI del objetivo para ejecutar VCL de forma segura.
- Hit-test, árbol y propiedades.
- Integración con helper Delphi.

Archivos clave:
- `VclHook/VclHook.c`
- `VclHook/VclBridge.c/.h`
- `VclHook/SpyderVclHelper32.dpr`

## Flujo de datos

## Hover (rápido)
1. UI lee la posición del cursor.
2. Win32 entrega HWND y rectángulo.
3. Overlay dibuja el resaltado.
4. Con VCL conectado: un hit-test ligero puede refinar bounds.

## Captura (completa)
1. Disparo por hotkey o drag.
2. `SpyCapture` recoge Win32 + UIA/MSAA con timeouts.
3. Si VCL está habilitado: resuelve cadena VCL y propiedades (best-effort).
4. UI muestra selector, propiedades y jerarquía.
5. Persistencia opcional a `locator.json`.

## Modelo de hilos
- UI thread de Spyder: WPF/render.
- Tareas en background: UIA/MSAA con timeouts.
- UI thread del objetivo: ejecución VCL vía dispatch.
