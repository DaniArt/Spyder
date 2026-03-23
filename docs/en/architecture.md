# Architecture

Spyder is built as a layered Windows application with an optional in-process VCL introspection subsystem. The system is designed to degrade gracefully: if a backend is unavailable or fails, capture continues using remaining backends.

## Layers

### UI Layer (Spy.UI)

Responsibilities:
- WPF UI, capture modes, and UX orchestration.
- Rendering highlight overlay.
- Presenting per-backend metadata and selectors.
- Managing `locator.json` persistence and user workflows.
- Controlling local file logging.

Key files:
- `Spy.UI/MainWindow.xaml(.cs)`
- `Spy.UI/OverlayWindow.xaml(.cs)`
- `Spy.UI/GlobalMouseHook.cs`
- `Spy.UI/LocatorStorage.cs`

### Core Layer (Spy.Core)

Responsibilities:
- Capture pipeline and core models.
- Selector generation and structured locator building.
- Backend timeouts and best-effort fault handling.
- Hook installation and lifecycle management for VCL introspection.

Key files:
- `Spy.Core/SpyCapture.cs`
- `Spy.Core/SelectorBuilder.cs`
- `Spy.Core/Hierarchy.cs`
- `Spy.Core/Vcl/VclHookManager.cs`
- `Spy.Core/Vcl/VclLocatorEngine.cs`

### Hook Layer (VclHook)

Responsibilities:
- Runs inside the target process.
- Hosts a Named Pipe server for RPC.
- Dispatches VCL work onto the target UI thread.
- Implements hit-testing and component traversal.
- Integrates a Delphi helper DLL for safe VCL API access.

Key files:
- `VclHook/VclHook.c`
- `VclHook/VclBridge.c` and `VclHook/VclBridge.h`
- `VclHook/SpyderVclHelper32.dpr`

## Interaction Diagram (High Level)

```text
Spy.UI (WPF)
  ├─ calls Spy.Core capture pipeline
  ├─ optional: talks to VCL pipe client
  └─ shows overlay + selector + properties

Spy.Core
  ├─ Win32 / UIA / MSAA capture
  ├─ selector + locator building
  └─ optional: starts VCL hook manager

Target Process (injected)
  ├─ VclHook pipe server
  ├─ UI-thread dispatch via message-loop hook
  └─ VCL bridge + Delphi helper
```

## Data Flow

### Hover Highlight (fast path)
1. UI samples cursor location.
2. Win32 capture provides an HWND and rectangle.
3. Overlay draws bounds immediately.
4. If VCL is connected, a lightweight VCL hit-test may refine bounds.

### Capture (full path)
1. User triggers capture (hotkey or drag release).
2. Core pipeline builds a `SpyElement` snapshot:
   - Win32 snapshot (always).
   - UIA snapshot with a timeout.
   - MSAA snapshot with a timeout.
3. If VCL is enabled and connected:
   - Resolve VCL chain at screen point.
   - Optionally pull properties for the VCL leaf.
4. UI renders:
   - hierarchy view,
   - per-backend property panels,
   - selector preview.
5. UI persists a structured locator to `locator.json` when requested.

## Threading Model

- Spyder UI thread: WPF rendering and UI updates.
- Spyder background tasks: UIA/MSAA capture with timeouts.
- Target process UI thread: executes VCL work via dispatch to avoid cross-thread VCL access.

## Failure Containment

- UIA/MSAA timeouts prevent capture stalls.
- Native VCL code uses pointer validation and exception guards.
- Client treats VCL failures as best-effort and continues with other backends.
