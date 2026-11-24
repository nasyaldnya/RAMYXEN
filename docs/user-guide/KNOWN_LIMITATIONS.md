# RAMYXEN — Known Limitations, Platform Boundaries & Provenance Guide

**Version:** `1.1.0`  
**Target Platform:** Windows 11 (`win-x64`, Build 22000+)

---

## 1. Engineering Honesty Principle

RAMYXEN never fabricates kernel or hardware telemetry when a Windows API boundary restricts access. Every data view carries an explicit `DataSourceProvenance` badge covering all 9 enum classifications:

- **`LIVE`**: Direct, unfiltered OS counter or virtual memory structure (`GlobalMemoryStatusEx`, `GetPerformanceInfo`, `GetProcessMemoryInfo`, `VirtualQueryEx`).
- **`ADMIN_REQUIRED`**: Cross-process inspection or handle/module access restricted by Windows elevation/integrity boundaries.
- **`ETW_REQUIRED`**: Cross-process allocation call-stack or external `.NET` GC generation tracing requiring an active elevated ETW session.
- **`DUMP_REQUIRED`**: Forensic artifact or offline heap/memory inspection requiring a captured `.dmp` / `.raw` memory image.
- **`DRIVER_REQUIRED`**: Capability requires a WHQL-signed Ring-0 kernel driver (`\\.\PhysicalMemory` / hardware PFN database walk), which RAMYXEN does not install.
- **`SYMBOLS_REQUIRED`**: Native/managed call-stack function resolution requiring local PDB symbol files.
- **`RUNTIME_SPECIFIC`**: Managed `.NET CLR` generation/heap metrics (`GC.GetGCMemoryInfo()`) applicable to the self-hosted `.NET 10` process.
- **`PARTIAL`**: Sampled, bounded, or truncated collection (e.g., virtual region count exceeding `512` display cap, module count exceeding `512`, or bounded/timed-out NT heap walk).
- **`UNAVAILABLE`**: Capability cannot be queried in the current environment or target state.

---

## 2. Specific Technical Boundaries

### 2.1 Protected Process Light (PPL) & Antimalware Processes
Even when running `RAMYXEN.exe` elevated as Administrator, Windows Kernel Patch Protection and Protected Process Light (`PPL`, e.g., `MsMpEng.exe`, `csrss.exe`, `lsass.exe` with RunAsPPL) restrict `PROCESS_VM_READ` and `VirtualQueryEx`. RAMYXEN reports summary counters safely and returns `RAMYXEN_ERR_ACCESS_DENIED` honestly on virtual-region enumeration without fabricating data.

### 2.2 Physical Page Frame Number (PFN) Hardware RAM Acquisition
Direct raw physical RAM acquisition and hardware PFN database inspection require a WHQL-signed Ring-0 kernel driver on Windows 11 with Secure Boot/HVCI. RAMYXEN never installs unsigned drivers or weakens Secure Boot; offline `.dmp` (`MDMP`) and `.raw` images are inspected in read-only mode.

### 2.3 Cross-Process Managed `.NET` GC Heap Introspection
`GC.GetGCMemoryInfo()` provides exact live Gen 0/1/2/LOH/POH metrics for `RAMYXEN.exe` itself; cross-process managed GC generation stats for external `.NET` processes require an elevated `Microsoft-Windows-DotNETRuntime` ETW trace (`ETW_REQUIRED`). RAMYXEN never misattributes its own GC metrics to external PIDs.

### 2.4 Isolated Out-of-Process Heap Walking (`RAMYXEN.HeapWorker.exe`)
Windows `CreateToolhelp32Snapshot(TH32CS_SNAPHEAPLIST)` is a synchronous kernel/user call that can stall on massive or suspended processes.
- Routine process selection never walks NT heaps.
- Explicit on-demand heap walks for external processes execute inside a dedicated killable child process (`RAMYXEN.HeapWorker.exe`) enforced by an external `3,000 ms` timeout (`Process.Kill(entireProcessTree: true)` on timeout, with a 60-second cooldown keyed by `(PID, ProcessStartTimeUtc)` and verified via `GetProcessTimes` inside the worker to prevent PID-reuse aliasing) and isolated from the lightweight inspection semaphore lane.

### 2.5 .NET 10 Unmanaged / Corrupted-State Crash Boundary
On `.NET 10`, `StackOverflowException` and native corrupted-state exceptions (`AccessViolationException`) terminate the process directly at the runtime/OS level without executing managed `catch` or `UnhandledException` handlers. Post-mortem capture for those conditions relies on Windows Error Reporting (WER) local dumps rather than managed exception hooks.

### 2.6 High-DPI & Multi-Resolution Validation Scope
- **Physically Verified Display:** Dual physical `2560×1440` (QHD / 2K) monitors running at **150% Windows Display Scaling** (`144 DPI`) on Windows 11 with Per-Monitor V2 DPI awareness (`app.manifest`).
- **Simulated Viewport Validation:** `1024×700` DIP (Compact), `1440×860` DIP (1080p-class), and `1920×1080` DIP (`4K UHD @ 200%` equivalent) layouts are validated via DPI-aware window sizing on the physical QHD host and explicitly labeled as simulated viewports rather than physical 4K hardware tests.

### 2.7 Uninstallation of Locked Files & Standard-User Privilege Boundary
`Uninstall-RAMYXEN.exe` executes a two-stage self-uninstallation (Stage 1 closes running `RAMYXEN.exe` and `RAMYXEN.HeapWorker.exe`, removes verified shortcuts and HKCU Uninstall registration, deletes unlocked payload files in-place, and hands off final `Uninstall-RAMYXEN.exe` / install-directory cleanup to a short-lived temp helper in `%TEMP%` that self-deletes via detached `cmd.exe` cleanup).
- If a third-party process holds a file lock after retries, `MoveFileExW(..., MOVEFILE_DELAY_UNTIL_REBOOT)` is attempted and its boolean return value is checked honestly:
  - When **elevated (Administrator)** and `MoveFileExW` succeeds, the uninstaller returns `3010` (`ERROR_SUCCESS_REBOOT_REQUIRED`).
  - When running as a **non-elevated Standard User**, Windows denies `MOVEFILE_DELAY_UNTIL_REBOOT` (which requires `HKLM` write access); the uninstaller does **not** fabricate `3010` or `0`, and honestly returns `1603` (`ERROR_INSTALL_FAILURE`) with a message instructing the user to close the locking application and retry.
