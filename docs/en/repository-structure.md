# Repository Structure

This document provides a top-level map of the repository and explains how modules are organized.

## Root

- `.gitignore`
  - Excludes build artifacts (`bin/`, `obj/`) and local logs from version control.
- `Spyder.slnx`
  - Solution descriptor listing managed projects.
- `build-dist.ps1`
  - Build and publish helper script for distribution outputs.
- `PROJECT_CONTEXT.md`
  - Developer notes and constraints.

## Managed Projects

### `Spy.UI/` (WPF application)

Purpose:
- User interface, capture orchestration, overlay highlighting, and locator persistence.

Highlights:
- `App.xaml(.cs)` — WPF entry point.
- `MainWindow.xaml(.cs)` — main interaction workflow, capture triggers, status/diagnostics.
- `OverlayWindow.xaml(.cs)` — click-through highlight rectangle.
- `VclHelperClient.cs` — Named Pipe client for VCL introspection.
- `LocatorStorage.cs` — `locator.json` schema and storage.

### `Spy.Core/` (core library)

Purpose:
- Capture pipeline, selectors, structured locators, and VCL hook lifecycle.

Highlights:
- `SpyCapture.cs` — builds a multi-backend `SpyElement` snapshot.
- `SelectorBuilder.cs` — selector output and structured locator definition.
- `Hierarchy.cs` — hierarchy model for UI presentation.
- `Vcl/VclHookManager.cs` — hook installation and lifecycle.
- `Vcl/VclLocatorEngine.cs` — resilient VCL chain resolution.

## Native Project

### `VclHook/` (native hook and VCL bridge)

Purpose:
- In-process server and VCL introspection code used when VCL support is enabled.

Highlights:
- `VclHook.c` — Named Pipe server + UI-thread dispatch.
- `VclBridge.c/.h` — hit-test, tree traversal, properties, safe reads.
- `VclHookLoader.c` — loader companion.
- `SpyderVclHelper32.dpr` — Delphi helper library for safe VCL API calls.
- `Build*.bat` — build entry points for x86/x64 native artifacts.

## `docs/`

Purpose:
- Multi-language engineering documentation.

Layout:

```text
docs/
  en/
  ru/
  es/
```

Each language folder contains the same set of documents.
