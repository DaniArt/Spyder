# Spyder — Overview

Spyder is an open-source Windows UI inspection and locator authoring tool. It captures the element under the cursor, extracts metadata across multiple backends, highlights the target on screen, and produces selectors and structured locators suitable for automation and reverse engineering workflows. An optional VCL introspection subsystem supports Delphi/VCL applications, including controls without a dedicated HWND.

## Capabilities

- Capture UI elements via hotkey or drag capture mode.
- Hover highlight with a transparent click-through overlay.
- Multi-backend inspection:
  - Win32 (HWND, class, text, rect, hit-test).
  - UI Automation (properties + parent chain, best-effort).
  - MSAA/IAccessible (best-effort snapshot).
  - VCL (chain, properties, hit-test, non-HWND controls) when enabled.
- Selector output:
  - Human-readable selector string for quick copying.
  - Structured locator object for durable storage.
- Locator persistence to a versioned JSON file (`locator.json`).
- Local file logging for troubleshooting.

## Typical Scenarios

- Generate stable selectors and locators for automation code.
- Inspect complex nested UI composition (containers, toolbars, tabs).
- Investigate non-HWND controls that are painted by a parent surface.
- Capture and share reproducible evidence: selector + locator entry + logs.

## How To Navigate The Repository

- Start from the UI orchestration:
  - `Spy.UI/MainWindow.xaml` and `Spy.UI/MainWindow.xaml.cs`
- Then review the capture pipeline and selector builders:
  - `Spy.Core/SpyCapture.cs`
  - `Spy.Core/SelectorBuilder.cs`
- For VCL introspection, follow the call chain:
  - `Spy.Core/Vcl/VclHookManager.cs` (hook install)
  - `Spy.UI/VclHelperClient.cs` (pipe client + VCL operations)
  - `VclHook/VclHook.c` + `VclHook/VclBridge.c` (in-process server + VCL logic)
  - `VclHook/SpyderVclHelper32.dpr` (Delphi helper used in-process)

## Entry Points

- UI entry: `Spy.UI/App.xaml(.cs)`, `Spy.UI/MainWindow.xaml(.cs)`
- Core entry: `Spy.Core/SpyCapture.cs`, `Spy.Core/SelectorBuilder.cs`
- Native entry: `VclHook/VclHook.c:GetMsgHookProc`, `VclHook/VclHook.c:PipeThread`

## Developer Onboarding

- Build managed code: `dotnet build .\Spy.UI\Spyder.csproj`
- If you need VCL introspection, build native x86 artifacts in `VclHook/` and place them next to the published `Spyder.exe`.
- Enable “Log to file”, reproduce one capture scenario, and attach logs to issues/PRs.

## Typical Workflow

1. Launch Spyder.
2. (Optional) Enable VCL introspection and select the target process.
3. Hover to see highlight and preview metadata.
4. Capture an element (hotkey or drag).
5. Review selector and properties.
6. Save/update an entry in `locator.json`.
