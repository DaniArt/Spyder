# Estructura del repositorio

Mapa de alto nivel del repositorio y propósito de los módulos.

## Raíz

- `.gitignore` — excluye `bin/`, `obj/` y logs locales del control de versiones.
- `Spyder.slnx` — descriptor de solución para proyectos managed.
- `build-dist.ps1` — helper de build/publish.
- `PROJECT_CONTEXT.md` — notas y restricciones de desarrollo.

## Proyectos managed

## `Spy.UI/` (aplicación WPF)

Propósito:
- UX, modos de captura, overlay, paneles de propiedades, y persistencia de `locator.json`.

Archivos destacados:
- `App.xaml(.cs)`
- `MainWindow.xaml(.cs)`
- `OverlayWindow.xaml(.cs)`
- `VclHelperClient.cs`
- `LocatorStorage.cs`

## `Spy.Core/` (librería core)

Propósito:
- pipeline de captura, modelos, selectores/locators, lifecycle del hook VCL.

Archivos destacados:
- `SpyCapture.cs`
- `SelectorBuilder.cs`
- `Hierarchy.cs`
- `Vcl/VclHookManager.cs`
- `Vcl/VclLocatorEngine.cs`

## Native

## `VclHook/`

Propósito:
- hook nativo y bridge VCL dentro del proceso objetivo.

Archivos destacados:
- `VclHook.c`
- `VclBridge.c/.h`
- `VclHookLoader.c`
- `SpyderVclHelper32.dpr`
- `Build*.bat`

## `docs/`

Documentación multi-idioma:

```text
docs/
  en/
  ru/
  es/
```
