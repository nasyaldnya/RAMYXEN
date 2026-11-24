# RAMYXEN — Build & Deployment Guide

**Target Platform:** Windows 11 (`x64`, Build 22000+)  
**Runtime Baseline:** Self-Contained `.NET 10 LTS` (`.NET 10.0.15` runtime via pinned SDK `10.0.404`) + Self-Contained `Windows App SDK 2.7.0` + Static CRT (`/MT`) C++20 Native Engine

---

## 1. Distribution Packages

Running `.\scripts\build-release.ps1` produces the release deliverables in `artifacts/release-v1.1.0-candidate/` (local staging, git-ignored):

1. **Portable ZIP Archive (`RAMYXEN-Portable-win-x64.zip`)**
   - Contains `RAMYXEN.exe`, `RAMYXEN.Native.dll`, `RAMYXEN.HeapWorker.exe`, self-contained `.NET 10.0.15` runtime binaries (`coreclr.dll`, `hostfxr.dll`, `hostpolicy.dll`), self-contained `Windows App SDK 2.7.0` binaries, and external editable `localization/` JSON language packs (`en-US`, `ar-SA`, `zh-CN`, `ja-JP`).
   - Extract anywhere on Windows 11 x64 and run `RAMYXEN.exe` directly — no preinstalled `.NET` runtime, `Windows App SDK`, or Visual C++ Redistributable required.
2. **Single-File Windows Setup Installer (`RAMYXEN-Setup.exe`)**
   - Embeds the exact self-contained `RAMYXEN-Portable-win-x64.zip` payload.
   - Installs to `%LOCALAPPDATA%\Programs\RAMYXEN` (or a custom directory via `--install-dir <path>`), creates Start Menu and Desktop shortcuts, and installs `Uninstall-RAMYXEN.exe`.
   - Command-line options:
     - `RAMYXEN-Setup.exe` — Standard installation with Start Menu & Desktop shortcuts
     - `RAMYXEN-Setup.exe --launch` — Install and immediately launch `RAMYXEN.exe`
     - `RAMYXEN-Setup.exe --silent --install-dir "C:\Tools\RAMYXEN"` — Silent automated deployment
     - `RAMYXEN-Setup.exe --uninstall --silent` — Cleanly removes RAMYXEN, shortcuts, and registry entries
3. **Checksums & Build Notes (`SHA256SUMS.txt`, `RELEASE_NOTES.md`)**
   - Independently computed SHA-256 hashes for both deliverables and full build provenance metadata.

---

## 2. Building From a Clean Clone

### Prerequisites
- Windows 11 (`x64`, Build 22000+)
- **`.NET 10 SDK 10.0.404`** (pinned explicitly in `global.json` with `rollForward: "latestPatch"` and `allowPrerelease: false`)
- Visual Studio 2022 / Visual Studio 18 Build Tools with the C++ Desktop Development workload (`cl.exe`, `link.exe`, Windows 11 SDK)
- Node.js 20+ (for localization validation scripts in `tools/localization/` and `tests/localization/`)

### One-Command Reproducible Release Build & Verification
```powershell
.\scripts\build-release.ps1 -RunTests
```

This script executes in order:
1. Builds the C++20 native engine (`RAMYXEN.Native.dll`, `RAMYXEN.HeapWorker.exe`, `RAMYXEN.Native.Tests.exe`) with `/MT`, `/guard:cf`, and `/Brepro`.
2. Publishes `src/RAMYXEN.App/RAMYXEN.App.csproj` for `win-x64` with `--self-contained true`, `WindowsAppSDKSelfContained=true`, and zero PDBs, then packages `RAMYXEN-Portable-win-x64.zip`.
3. Publishes single-file `installers/RAMYXEN.Installer/RAMYXEN.Installer.csproj` embedding the freshly built `RAMYXEN-Portable-win-x64.zip`.
4. Builds and runs the Native C++20 test suite, `.NET 10` Release test suite, and 4-locale localization validation suite.
5. Generates `SHA256SUMS.txt` and `RELEASE_NOTES.md` in `artifacts/release-v1.1.0-candidate/`.
