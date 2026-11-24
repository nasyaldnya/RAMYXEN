# Changelog

All notable changes to RAMYXEN are documented in this file.

## [1.1.0] — Initial Public Baseline

### Highlights
- **Native Windows 11 x64 Memory Intelligence Platform:** Built with WinUI 3, Windows App SDK 2.7 (self-contained), `.NET 10 LTS` (`.NET 10.0.15` self-contained runtime, pinned SDK `10.0.404`), and a C++20 static-CRT (`/MT`) native engine (`RAMYXEN.Native.dll` + isolated `RAMYXEN.HeapWorker.exe`).
- **Four Complete Locales (`567 / 567` keys, `100%` coverage):** English (`en-US`), Arabic (`ar-SA` with full RTL mirroring and Unicode Bidi isolation), Simplified Chinese (`zh-CN`), and Japanese (`ja-JP`), loaded from external editable UTF-8 JSON language packs.
- **Self-Contained Distribution:** Ships as both a zero-prerequisite Portable ZIP (`RAMYXEN-Portable-win-x64.zip`) and a single-file Windows Setup Installer (`RAMYXEN-Setup.exe`) embedding the exact same self-contained payload.
- **Local-First Privacy & Diagnostics:** Zero cloud telemetry; consent-gated diagnostics, structured redaction of sensitive paths/tokens/SIDs, and read-only memory inspection by default.
- **Reproducible Clean-Clone Build & Verification Pipeline:** Single-command `scripts/build-release.ps1 -RunTests` covering native C++20 tests, `.NET 10` Release tests, and 4-locale validation.
