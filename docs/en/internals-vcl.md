# VCL Internals

This document describes the optional VCL introspection subsystem: how Spyder performs hit-testing and chain/property extraction for VCL controls, including non-HWND controls.

## Why VCL Requires a Specialized Path

VCL applications can contain controls that:
- are painted by a parent and do not have a dedicated HWND,
- are not exposed as distinct UI Automation elements,
- require access to VCL runtime structures for reliable chain resolution.

Spyder’s VCL subsystem runs inside the target process and uses VCL-native APIs in the correct execution context.

## Execution Context: UI Thread Only

VCL access is required to run on the target UI thread. Spyder enforces this by:
- installing a WH_GETMESSAGE hook into the target GUI thread,
- posting a thread message used as a dispatch trigger,
- executing request handling in the hook procedure when the target pumps its message loop.

## Subsystems

### Native Hook Server (`VclHook`)
- Hosts a Named Pipe server inside the target process.
- Parses JSON commands and executes them via a UI-thread dispatch.
- Uses pointer validation and exception guards around risky areas.

### VCL Bridge (`VclBridge`)
- Implements core primitives:
  - validate VCL objects and class names,
  - extract component name and other metadata,
  - hit-test at a screen point,
  - enumerate children and build parent chains,
  - serialize results as JSON.

### Delphi Helper (`SpyderVclHelper32`)
- Provides safe, in-process access to VCL APIs that are difficult to call reliably from C.
- Used for:
  - control hit-testing at a point (`ControlAtPos`, `FindDragTarget`),
  - control rectangle calculations via coordinate transforms,
  - pulling selected control fields (caption/name/tab order, best-effort).

## Hit-test Design

High-level flow:
1. Resolve a VCL root control from an HWND (best effort).
2. Hit-test at screen coordinates to get a leaf control candidate.
3. If the leaf is a container class, apply bounded refinement:
   - enumerate children safely,
   - check bounds and visibility,
   - stop early when a deeper match is found.

Two modes are commonly used:
- Overlay hit-test: optimized for frequent hover calls.
- Capture hit-test: may run deeper refinement when necessary.

## Non-HWND Controls

Non-HWND controls are discovered through VCL’s component/control lists rather than Win32 enumeration. Bounds are computed using:
- `ClientToScreen` for windowed controls,
- parent-relative offsets when reliable for non-windowed controls,
- helper-assisted calculation when needed.

## Safety Rules

The VCL subsystem is designed to avoid destabilizing the target process:
- Validate pointers before dereferencing.
- Wrap risky operations in structured exception handling.
- Apply strict limits on recursion depth and visited node count.
- Treat failures as best-effort and return partial results.

## Debugging

- Enable file logging in the UI.
- Check native-side logs if enabled by the native build.
- Prefer capture logs over hover logs for root-cause analysis because capture produces a consistent snapshot.
