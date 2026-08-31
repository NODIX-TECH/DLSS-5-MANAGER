# ⚡ DLSS 5 MANAGER

<p align="center">
  <strong>The First Smart Manager & One-Click Installer for DLSS 5, ReShade, and Streamline Mods.</strong>
</p>

<p align="center">
  <a href="https://github.com/NODIX-TECH/DLSS-5-MANAGER/releases/latest">
    <img src="https://img.shields.io/badge/Download-Latest%20Release%20(v1.0.0)-00E575?style=for-the-badge&logo=windows" alt="Download Release" />
  </a>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Platform-Windows%20x64-141A16?style=flat-square&logo=windows" alt="Platform" />
  <img src="https://img.shields.io/badge/Framework-.NET%208%20%7C%20Avalonia%20UI-141A16?style=flat-square" alt="Framework" />
  <img src="https://img.shields.io/badge/Language-F%23-141A16?style=flat-square&logo=fsharp" alt="Language" />
  <img src="https://img.shields.io/badge/License-NODIX%20TECH-141A16?style=flat-square" alt="License" />
</p>

---

## 📖 Overview

**DLSS 5 MANAGER** is a standalone, high-performance desktop utility designed to scan your PC game libraries and effortlessly deploy next-generation DLSS 5 modifications, ReShade shaders, and Streamline binaries with a single click.

Engineered with **F#** and **Avalonia UI**, it brings a modern Apple-inspired glassmorphic interface with translucent layers, real-time background blur, and vibrant Neon Emerald styling.

---

## ✨ Features

* **🔍 Smart Executable Detection:** Identifies the real game `.exe` across engines (Unreal Engine, REDengine, Unity, Source 2) and bypasses launcher binaries automatically.
* **🕹️ Multi-Platform Scanning:** Auto-detects installed libraries from **Steam**, **Epic Games Store**, **GOG**, and custom/repack directories.
* **⚡ One-Click Pipeline:**
  * Silent ReShade deployment directly alongside the game binary.
  * Injects `renodx-dlss5.addon64` and core dependencies.
  * Smart Streamline upgrade ($\ge 2.4$) and DLSS DLL version alignment.
  * Automatic file backups before mutation, guaranteeing clean rollbacks on removal.
* **🚀 Instant Library Caching:** Lightning-fast launch times with local caching for game lists, posters, and analysis data.
* **🎨 Customization:** Drag-and-drop game grid reordering, navbar layout toggles (Top / Sidebar), and instant update checking.

---

## 📥 Installation & Usage

1. Download the latest self-contained build from [**Releases**](https://github.com/NODIX-TECH/DLSS-5-MANAGER/releases/latest).
2. Extract the downloaded `.zip` archive to any directory.
3. Launch `DLSS 5 MANAGER.exe`.
4. Select a detected game (or click `+` to add manually) and hit **Install DLSS 5**.

---

## 📂 Storage & Cache Locations

All cached metadata, custom layouts, and safety backups are preserved locally under:
```text
%LOCALAPPDATA%\DLSS5Manager\
