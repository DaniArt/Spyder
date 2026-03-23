# FAQ

## Spyder cannot find the element I hover over

Possible causes:
- The UI is a single composed surface and does not expose distinct children via UIA/MSAA.
- The cursor is over a container HWND (Win32 hit-test sees only the container).
- The element is transient and appears/disappears during hover.

Recommendations:
- Use capture (hotkey/drag) instead of relying on hover.
- Enable VCL introspection if the target uses VCL.
- Enable logging and attach logs when reporting issues.

## The selected control has no HWND

Some controls are non-windowed and are painted by a parent control. These controls:
- do not have a Win32 handle,
- may not appear as distinct UIA elements,
- require toolkit-specific introspection to identify.

Enable VCL introspection (when applicable) to capture non-HWND controls.

## VCL chain is incomplete

Typical reasons:
- The target UI thread is not pumping messages (dispatch cannot run).
- The control tree changes between hit-test and path lookup.
- Traversal is bounded by safety limits to protect the target process.

Try:
- Capture again while the target UI is idle.
- Reduce UI activity (animations, hover effects) during capture.
- Review logs to see which command failed.

## VCL introspection reports bitness mismatch

The inspector and target must match bitness for in-process introspection:
- Use x86 build for 32-bit targets.
- Use x64 build for 64-bit targets (when available).

## Where are logs stored?

When file logging is enabled:
- `%LOCALAPPDATA%\Spyder\logs\spyder.log`

Native-side logs (development builds) may also appear under:
- `VclHook/logs/`

## Why does publish fail with “Access denied”?

The output executable may still be running. Stop the process, clean the publish folder, and retry publish (see `docs/en/installation.md`).
