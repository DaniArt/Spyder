# Components

This document explains major components and how they interact.

## Spy.UI (WPF UI)

Responsibilities:
- User-facing capture workflow (hover, drag, hotkey).
- Overlay highlight rendering.
- Display of metadata by backend (Win32 / UIA / MSAA / VCL).
- Locator persistence and selector copying.
- File logging control.

Main integration points:
- Calls `Spy.Core.SpyCapture` to capture elements.
- Optionally starts VCL hook via `Spy.Core.Vcl.VclHookManager`.
- Uses `Spy.UI.VclHelperClient` to query VCL chain and properties over Named Pipe.

Key files:
- `Spy.UI/MainWindow.xaml(.cs)`
- `Spy.UI/OverlayWindow.xaml(.cs)`
- `Spy.UI/GlobalMouseHook.cs`
- `Spy.UI/LocatorStorage.cs`
- `Spy.UI/VclHelperClient.cs`

## Spy.Core (Core Layer)

Responsibilities:
- Core capture pipeline and models (`SpyElement`).
- Selector and structured locator building.
- Backend resilience (timeouts, best-effort fallbacks).
- VCL hook lifecycle and target process compatibility checks.

Key files:
- `Spy.Core/SpyCapture.cs`
- `Spy.Core/SelectorBuilder.cs`
- `Spy.Core/Hierarchy.cs`
- `Spy.Core/Vcl/VclHookManager.cs`
- `Spy.Core/Vcl/VclLocatorEngine.cs`

## VclHook (Hook Layer)

Responsibilities:
- Run inside the target process.
- Provide a JSON RPC surface over a Named Pipe.
- Dispatch VCL work onto the UI thread to avoid unsafe cross-thread access.
- Implement VCL hit-test, child traversal, and property extraction.

Key files:
- `VclHook/VclHook.c`
- `VclHook/VclBridge.c/.h`
- `VclHook/VclHookLoader.c`
- `VclHook/SpyderVclHelper32.dpr`

## IPC (Named Pipe)

Responsibilities:
- Reliable message framing for small JSON requests and responses.
- Failure handling: timeouts and graceful degradation.

Implementation:
- Client: `Spy.UI/VclHelperClient.cs`
- Server: `VclHook/VclHook.c`

See also:
- `docs/en/pipe-protocol.md`

## Locator System

Responsibilities:
- A durable, versioned representation of a captured element.
- Supports human selector strings and structured locator objects.

Implementation:
- Structured locator model: `Spy.Core/SelectorBuilder.cs`
- File storage schema: `Spy.UI/LocatorStorage.cs`

See also:
- `docs/en/locator-format.md`
