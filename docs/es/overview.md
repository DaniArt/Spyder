# Spyder — Resumen

Spyder es una herramienta open-source para inspección de UI en Windows y creación de localizadores. Captura el elemento bajo el cursor, extrae metadatos desde varios backends, resalta el objetivo en pantalla y genera selectores/localizadores estructurados. De forma opcional incluye introspección VCL para aplicaciones Delphi/VCL, incluyendo controles sin HWND.

## Capacidades

- Captura por hotkey o modo drag-capture.
- Resaltado mediante overlay transparente (click-through).
- Inspección multi-backend:
  - Win32 (HWND, clase, texto, rectángulo, hit-test).
  - UI Automation (propiedades + cadena de padres, best-effort).
  - MSAA/IAccessible (best-effort).
  - VCL (cadena, propiedades, hit-test, non-HWND) cuando está habilitado.
- Generación de selectores:
  - selector legible para copiar.
  - locator estructurado para almacenamiento.
- Persistencia a `locator.json` con esquema versionado.
- Logging a un archivo local para diagnóstico.

## Casos típicos

- Derivar selectores estables para automatización.
- Inspeccionar composición de UI (paneles, barras, pestañas).
- Capturar controles non-HWND pintados por el contenedor.
- Compartir evidencia reproducible: locator + logs.

## Cómo navegar el repositorio

- UI y flujo principal:
  - `Spy.UI/MainWindow.xaml` y `Spy.UI/MainWindow.xaml.cs`
- Pipeline y builders:
  - `Spy.Core/SpyCapture.cs`
  - `Spy.Core/SelectorBuilder.cs`
- Introspección VCL:
  - `Spy.Core/Vcl/VclHookManager.cs`
  - `Spy.UI/VclHelperClient.cs`
  - `VclHook/VclHook.c` y `VclHook/VclBridge.c`
  - `VclHook/SpyderVclHelper32.dpr`

## Entry points

- UI: `Spy.UI/App.xaml(.cs)`, `Spy.UI/MainWindow.xaml(.cs)`
- Core: `Spy.Core/SpyCapture.cs`, `Spy.Core/SelectorBuilder.cs`
- Native: `VclHook/VclHook.c:GetMsgHookProc`, `VclHook/VclHook.c:PipeThread`

## Workflow típico

1. Ejecutar Spyder.
2. (Opcional) Activar VCL y seleccionar proceso.
3. Hover: resaltado y preview de metadatos.
4. Captura: hotkey o drag.
5. Revisar selector y propiedades.
6. Guardar/actualizar en `locator.json`.
