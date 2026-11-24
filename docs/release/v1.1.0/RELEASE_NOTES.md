# RAMYXEN v1.1.0 — Release Notes

**Version:** `1.1.0`  
**Target Platform:** Windows 11 (`x64`, Build 22000+)  
**Runtime Stack:** Self-Contained `.NET 10 LTS` (`.NET 10.0.15` runtime via pinned SDK `10.0.404`) + Self-Contained `Windows App SDK 2.7.0` + C++20 Static-CRT (`/MT`) Native Engine

---

## Overview

RAMYXEN `v1.1.0` is the initial public release baseline of the native Windows 11 Memory Intelligence, Inspection, Analysis, Diagnostics, and Forensics platform.

## Key Capabilities

- **Live System & Process Memory Intelligence:** Real-time working set, private commit, standby/modified page list metrics, virtual memory region inspection, module/thread analysis, and out-of-process heap sampling via `RAMYXEN.HeapWorker.exe`.
- **Forensics, Snapshots & Reporting:** MiniDump (`.dmp`) header/stream parsing, PE header validation, snapshot diffing, and redacted CSV/JSON/HTML/PDF export with CSV formula-injection and XSS protection.
- **Four Complete Languages (`567 / 567` keys, `100%`):** English (`en-US`), Arabic (`ar-SA`, full RTL + Unicode Bidi isolation), Simplified Chinese (`zh-CN`), and Japanese (`ja-JP`), powered by external editable JSON language packs.
- **Zero-Prerequisite Self-Contained Packaging:**
  - `RAMYXEN-Portable-win-x64.zip`: Extract anywhere on Windows 11 x64 and run `RAMYXEN.exe` directly — no preinstalled `.NET` runtime, `Windows App SDK`, or Visual C++ Redistributable required.
  - `RAMYXEN-Setup.exe`: Single-file installer embedding the exact self-contained Portable payload, with silent install (`--silent`), custom install path (`--install-dir`), and clean uninstall (`--uninstall`).
- **Local-First Security & Privacy:** Zero external telemetry; automatic redaction of user paths, UNC shares, SIDs, and secret patterns in diagnostic bundles and reports.

## System Requirements

- **Operating System:** Windows 11 (`x64`, Version 21H2 / Build 22000 or later; built against Windows 11 22H2 SDK `10.0.22621.0`).
- **Privileges:** Standard user for live system/own-process inspection; Administrator elevation only when inspecting protected processes or starting kernel ETW sessions.

## Verification Summary

- Native C++20 test suite: `31 / 31` passed
- `.NET 10` Release unit & integration test suite: `40 / 40` passed
- Localization validator (`en-US`, `ar-SA`, `zh-CN`, `ja-JP`): `4 / 4` locales valid (`567 / 567` keys, `0` errors)
- Localization infrastructure test suite: `7 / 7` passed
- Clean-clone reproducible build verified from the `v1.1.0` public baseline
