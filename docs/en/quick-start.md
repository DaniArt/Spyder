# Quick Start

This is a minimal end-to-end workflow: launch → capture → copy selector → save locator.

## 1) Launch Spyder

Run `Spyder.exe`.

## 2) (Optional) Enable VCL Introspection

If your target application uses VCL and you need component-level inspection:
1. Enable VCL introspection in the UI.
2. Select the target process.
3. Wait for the VCL status to indicate an active connection.

If VCL is not enabled, Spyder still works via Win32/UIA/MSAA backends.

## 3) Hover Highlight

Move the cursor over an element:
- Spyder renders a highlight rectangle.
- The panels update with metadata snapshots.

## 4) Capture

Use one of:
- Hotkey capture (if configured).
- Drag capture mode.

After capture, Spyder updates:
- Hierarchy view
- Properties
- Selector preview

## 5) Copy Selector

Use the copy action near the selector field.

## 6) Save to `locator.json`

1. Open locator management UI.
2. Select a file path for `locator.json`.
3. Add or update an entry with the current capture.

## Logs

Enable file logging to write diagnostics to:

`%LOCALAPPDATA%\Spyder\logs\spyder.log`
