<h1 align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset=".github/images/spyder-inverse.svg">
    <source media="(prefers-color-scheme: light)" srcset=".github/images/spyder.svg">
    <img width="360" src=".github/images/spyder.svg" alt="Spyder">
  </picture>
</h1>

<p align="center">

<a href="https://github.com/YOUR_USERNAME/Spyder/actions">
<img src="https://img.shields.io/github/actions/workflow/status/YOUR_USERNAME/Spyder/ci.yml?label=build&logo=github">
</a>

<a href="LICENSE">
<img src="https://img.shields.io/badge/license-Apache%202.0-blue">
</a>

<a href="https://github.com/YOUR_USERNAME/Spyder">
<img src="https://img.shields.io/github/stars/YOUR_USERNAME/Spyder?style=social">
</a>

<a href="https://github.com/YOUR_USERNAME/Spyder/issues">
<img src="https://img.shields.io/github/issues/YOUR_USERNAME/Spyder">
</a>

</p>


<p align="center">
  <h1 align="center">Spyder</h1>
  <p align="center">
    Modern VCL object inspect for Windows Desktop Automation
  </p>
</p>

![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg)
![Python](https://img.shields.io/badge/python-3.10%2B-blue)
![Platform](https://img.shields.io/badge/platform-Windows-0078D6)
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
## Spyder Inspector

![Spyder UI](docs/images/spyder-ui.png)

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

## License

Spyder is licensed under the Apache 2.0 License.
