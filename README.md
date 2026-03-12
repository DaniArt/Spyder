# Spyder
Object Inspect for Windows desktop automation
<p align="center">
  <h1 align="center">Spyder</h1>
  <p align="center">
    Modern VCL ObjectSpy for Windows Desktop Automation
  </p>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/platform-Windows-blue">
  <img src="https://img.shields.io/badge/framework-VCL-orange">
  <img src="https://img.shields.io/badge/domain-Test%20Automation-green">
  <img src="https://img.shields.io/badge/status-Development-yellow">
</p>

---

## Overview

**Spyder** is a modern object inspection tool for **VCL desktop applications**.

It enables reliable UI automation by extracting the **real VCL component hierarchy** directly from application memory.

The project is designed for automation engineers working with legacy Windows systems where standard UI automation frameworks fail.

Spyder provides the missing tooling for desktop automation similar to what browser DevTools provide for web testing.

---

## Why Spyder

Many modern automation frameworks focus on:

- Web applications
- APIs
- Mobile platforms

However a huge amount of enterprise software still runs on **Windows desktop frameworks like VCL**.

Spyder brings modern automation capabilities to these systems.

---

## Features

- Deep **VCL component introspection**
- Automatic **32 / 64 bit process detection**
- Hover based **control inspection**
- Access to real **TComponent.Name**
- Extraction of **VCL component tree**
- Reliable selector generation
- JSON locators for automation frameworks

---

## Example Selector

When hovering over a control Spyder resolves the deepest VCL component.

Mouse position → TLabel lblUser


Generated selector:


Sys.Process("Project1")
.VCL("frmLoginDlg")
.VCL("pnlWindowless")
.VCL("lblUser")


---

## Generated Locator

Spyder exports locators as JSON:

```json
{
  "process": "Project1",
  "form": "frmLoginDlg",
  "control": "lblUser"
}

These locators can be used directly by automation frameworks.

Architecture
ObjectSpy (32bit UI Tool)
│
├── Process Selection
├── Hover Inspection
├── Selector Builder
│
└── Named Pipe Communication
        ↓
VCL Hook Layer
├── VclHook32.dll
└── VclHook64.dll
        ↓
Target Application (VCL)
How It Works

Spyder injects a lightweight hook into the target VCL application.

The hook extracts runtime information such as:

VMT pointer

TObject instance

Component name

Parent / child relations

Absolute screen bounds

This allows building selectors based on the actual VCL hierarchy.

Safe Memory Access

Cross-process memory reading is protected using:

VirtualQuery
Structured Exception Handling
Memory validation

This prevents crashes when accessing invalid memory regions.

Target Architecture Detection

Spyder automatically selects the correct hook DLL.

if target == 64bit
    inject VclHook64.dll
else
    inject VclHook32.dll

The ObjectSpy UI always runs as 32bit for compatibility.

Example Automation (Python)
login_label = vcl.find({
    "process": "Project1",
    "form": "frmLoginDlg",
    "control": "lblUser"
})

login_label.click()
Roadmap
Stage 1 — VCL Detection

VMT based detection

HWND → TObject resolving

ClassName extraction

Stage 2 — Component Introspection

Parent resolution

Controls[] traversal

TComponent.Name extraction

Stage 3 — Component Tree

Full VCL hierarchy

Tree viewer

Advanced selector builder

Stage 4 — Automation Integration

Python locator resolver

Automation SDK

Test framework integration

Repository Structure
spyder
│
├ objectspy
│
├ vcl_hook
│   ├ VclHook32
│   └ VclHook64
│
├ python
│   └ locator_resolver
│
├ docs
│
└ README.md
Vision

Spyder aims to become the standard inspection tool for desktop automation in VCL based systems.

Author

Daniyar Sagatov

Automation Engineer
Desktop Automation Toolmaker

Disclaimer

Spyder is intended for automation testing and UI inspection of software where you have legal access.


---

# Чтобы README выглядел **реально круто**

Добавь 2 вещи.

### 1️⃣ Баннер

В начало:

```markdown
<p align="center">
<img src="docs/spyder-banner.png">
</p>
2️⃣ GIF demo
docs/demo.gif
