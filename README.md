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
