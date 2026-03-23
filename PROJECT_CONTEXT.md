# Project Context

Spyder is an open-source UI inspection and locator authoring tool for Windows.

Architecture:
- Spy.UI (WPF, net8.0-windows)
- Spy.Core (Win32 + UIA inspection logic)

Features implemented:
- Global mouse hook (WH_MOUSE_LL)
- Overlay highlight window (transparent, click-through)
- Drag-and-drop capture mode
- UIA + Win32 element capture
- Parent chain capture planned
- MSAA fallback planned

Purpose:
Inspect Windows UI, including optional Delphi/VCL introspection, and extract element metadata.

Important:
Do NOT suggest creating a new project.
Modify existing files only.
Preserve current architecture.
