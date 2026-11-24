# RAMYXEN — User Guide & Analytical Workspaces Manual

**Tagline:** *Understand Every Byte.*  
**Version:** `1.1.0`

---

## 1. Launching RAMYXEN

Run `RAMYXEN.exe` from the extracted Portable folder or via the Start Menu shortcut installed by `RAMYXEN-Setup.exe`.

- **Standard User Mode:** Monitors system-wide physical/commit memory, lists all processes, inspects virtual memory/heaps/modules/threads/strings of same-user processes, performs OLS leak regression analysis, captures snapshots, and exports reports.
- **Administrator Mode (Right-click → Run as administrator):** Unlocks cross-process `VirtualQueryEx`/Heap/MiniDump inspection for elevated services and enables live ETW tracing session control.

---

## 2. Navigating the 11 Primary Workspaces & All 98 Destinations

Use the **Left Collapsible Workspace Rail** for one-click access to the 11 core analytical workspaces, or use the **98-Destination Jump Selector** in the top command header to navigate directly to any of the `#01`–`#98` screens:

1. **Overview (`#14` Memory Atlas):**
   - Left 22% System Telemetry Ledger, Center 52% Physical-to-Virtual Memory Topology Bridge & Multi-Strata Physical Bar, Right 26% Live Exhaustion/Leak Triage & Top Consumers Spark-Bar Rail.
2. **Live Observatory (`#15` 5-Channel Synchronized Signal Observatory):**
   - Synchronized time-series channels for Physical In Use, Standby Cache, Commit Charge, Page Fault Velocity, and Kernel Nonpaged Pool, paired with the Correlated Process Growth Delta Matrix.
3. **Processes (`#18`–`#20` Process Memory Intelligence Ledger):**
   - Filterable live process table displaying PID, Parent PID, Category, Working Set, Private Commit, Peak Working Set, Growth Velocity ($\text{KiB/s}$), Handle/Thread counts, and Leak Classification.
4. **Process Deep Dive (`#21`–`#30` Process Anatomy):**
   - Inspects the selected process's Loaded Modules (`MODULEENTRY32W`), Native Heaps (`Heap32ListFirst`), Virtual Address Regions (`VirtualQueryEx`), creates real `.dmp` MiniDumps (`MiniDumpWriteDump`), and runs Consent-Gated Live ASCII/UTF-16 Memory String Searches.
5. **Virtual Memory (`#31`–`#33` Virtual Address Space & Live Hex Inspector):**
   - Color-coded VAD region table (`MEM_IMAGE`, `MEM_MAPPED`, `MEM_PRIVATE`, suspicious `PAGE_EXECUTE_READWRITE`). Click any committed region to read its live hexadecimal and ASCII payload via `ReadProcessMemory`.
6. **Physical RAM (`#34`–`#41` Physical Memory & Standby List Analytics):**
   - Inspects Installed vs. OS-Usable RAM, Active In-Use, Standby Cache (Available), Modified List, Free/Zeroed Pages, and Hardware Reserved memory, with explicit kernel PFN provenance disclosure.
7. **Allocation Profiler (`#42`–`#50` ETW & .NET 10 Runtime GC Profiler):**
   - Manages the 4 Windows ETW providers (`Kernel-Memory`, `DotNETRuntime`, `Kernel-Process`, `Heap-Snapshot`), displays live `.NET 10` Gen 0/1/2/LOH/POH and fragmentation metrics, and attributes committed memory hotspots.
8. **Snapshots (`#51`–`#58` Typed Snapshot Vault & Differential Comparison):**
   - Captures `TelemetrySnapshot` JSON states and real `ProcessDump` (`.dmp`) files with SHA-256 verification, computes differential delta comparisons between any two snapshots, and supports zero-overwrite secure deletion.
9. **Forensics (`#59`–`#75` Live Malfind & Offline Dump Forensics):**
   - Scans live process VADs for private `PAGE_EXECUTE_READWRITE` regions and in-memory `MZ` PE headers, and parses offline Windows MiniDump (`.dmp` `MDMP`) and `.raw` files.
10. **Alerts & Leaks (`#76`–`#79` Memory Exhaustion & Leak Investigation Center):**
    - Displays the 9 exhaustion classifications and detailed OLS linear regression evidence ($\text{Bytes/s}$ slope, $R^2$, confidence %, suspected subsystem, and non-destructive recommendations).
11. **Reports (`#80`–`#83`) & Settings (`#84`–`#98`):**
    - One-click export to **CSV**, **JSON**, **HTML** (LTR/RTL), and **PDF 1.4** with automatic PII path redaction, plus runtime language switching (`en-US` ↔ `ar-SA`), theme switching (`Dark` / `Light` / `HighContrast`), and IEC (`GiB`) vs. SI (`GB`) unit toggles.

---

## 3. Customizing or Adding External Language Packs

RAMYXEN loads translations from external UTF-8 JSON files:
1. Bundled defaults: `localization/locales/en-US.json` and `localization/locales/ar-SA.json`
2. User overrides: `%LOCALAPPDATA%\RAMYXEN\localization\locales\`

To edit translations or add a new language:
1. Copy `localization/locales/template.json` to `localization/locales/<locale>.json` (e.g., `de-DE.json`).
2. Register the locale in `localization/languages.json`.
3. Click **Reload External JSON Language Packs** in Settings (`#84`) or switch languages in the top command header—no recompilation or application restart required.
