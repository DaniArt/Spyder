# Repository Structure (Deep)

This document describes key files and how they are used. Helper utilities are grouped; the focus is on entry points and core integration surfaces.

## Root

## `Spyder.slnx`
- Purpose: solution descriptor for the managed projects.
- Used by: developers opening the repository in an IDE.

## `build-dist.ps1`
- Purpose: build/publish helper script for distribution.
- Used by: packaging workflows.
- Interacts with: `Spy.UI/Spyder.csproj` publish output.

## `PROJECT_CONTEXT.md`
- Purpose: developer notes and non-functional constraints.

## Spy.Core

## `Spy.Core/SpyCapture.cs`
- Purpose: capture pipeline producing a `SpyElement` snapshot from cursor or screen point.
- Used by: `Spy.UI/MainWindow.xaml.cs`.
- Interacts with:
  - Win32 capture (always).
  - UIA capture (timeout-protected).
  - MSAA capture (timeout-protected).
  - Optional VCL backend (when enabled).

## `Spy.Core/SelectorBuilder.cs`
- Purpose:
  - Builds the displayed selector string.
  - Defines the structured locator schema (`StructuredLocator`) used by `locator.json`.
- Used by: `Spy.UI` capture workflow.
- Interacts with: `SpyElement` models and hierarchy data.

## `Spy.Core/Hierarchy.cs`
- Purpose: hierarchy model used to render element chains in the UI.
- Used by: `Spy.UI` hierarchy panel.

## `Spy.Core/Locator.cs`
- Purpose: locator model and formatting utilities.
- Used by: selector/locator building and UI integration.

## `Spy.Core/Vcl/VclHookManager.cs`
- Purpose: manages VCL hook lifecycle and target compatibility.
- Used by: `Spy.UI` when VCL introspection is enabled.
- Interacts with:
  - `LoadLibrary` of the native hook DLL.
  - `SetWindowsHookEx(WH_GETMESSAGE)` into the target GUI thread.
  - Bitness checks and error reporting.

## `Spy.Core/Vcl/VclLocatorEngine.cs`
- Purpose: resilient VCL chain resolution and tree cache management.
- Used by: VCL resolution workflows in the managed client.
- Interacts with: pipe client results (`get_path`, `get_children`) and best-effort recovery.

## `Spy.Core/Logging/*` (group)
- Purpose: common logging helpers for managed code.
- Used by: capture pipeline and VCL manager.

## Spy.UI

## `Spy.UI/App.xaml` and `Spy.UI/App.xaml.cs`
- Purpose: WPF application entry point and lifecycle integration.

## `Spy.UI/MainWindow.xaml` and `Spy.UI/MainWindow.xaml.cs`
- Purpose: main capture UX:
  - starts and stops spy mode,
  - triggers capture (hotkey/drag),
  - updates hierarchy/properties/selector panels,
  - coordinates optional VCL introspection,
  - writes to `locator.json` via `LocatorStorage`.
- Interacts with:
  - `Spy.Core.SpyCapture`
  - `Spy.Core.SelectorBuilder`
  - `Spy.Core.Vcl.VclHookManager`
  - `Spy.UI.VclHelperClient`
  - `Spy.UI.OverlayWindow`

## `Spy.UI/VclHelperClient.cs`
- Purpose: Named Pipe client and higher-level VCL operations.
- Used by: `MainWindow` and VCL-related UI flows.
- Interacts with:
  - `resolve_by_hwnd`
  - `hit_test` / `hit_test_overlay`
  - `get_children`
  - `get_path`
  - `get_properties` (and optional extended queries)

## `Spy.UI/LocatorStorage.cs`
- Purpose: locator file schema and persistence logic.
- Used by: UI to load/save `locator.json`.

## `Spy.UI/OverlayWindow.xaml(.cs)`
- Purpose: click-through highlight overlay used by hover and capture.
- Used by: `MainWindow`.

## `Spy.UI/*Window.xaml(.cs)` (group)
- Purpose: auxiliary windows (process selection, locator management, selector entry, splash).
- Used by: user workflows and setup steps.

## VclHook

## `VclHook/VclHook.c`
- Purpose:
  - WH_GETMESSAGE hook procedure export (`GetMsgHookProc`),
  - Named Pipe server thread,
  - request dispatch onto the target UI thread.
- Used by: `Spy.Core/Vcl/VclHookManager.cs`.
- Interacts with:
  - `VclBridge` to implement commands.
  - UI-thread dispatch message to execute `HandleCommandOnCurrentThread`.

## `VclHook/VclBridge.c` and `VclHook/VclBridge.h`
- Purpose:
  - safe reads and pointer validation,
  - VCL hit test logic,
  - children/path/property extraction,
  - logging.
- Used by: `VclHook.c`.

## `VclHook/SpyderVclHelper32.dpr`
- Purpose: Delphi helper library used in-process for safe VCL API access.
- Used by: VCL bridge functions that need VCL-native operations.
- Interacts with VCL APIs such as control hit-testing and coordinate transforms.

## `VclHook/Build*.bat` (group)
- Purpose: native build entry points for x86/x64 artifacts.
