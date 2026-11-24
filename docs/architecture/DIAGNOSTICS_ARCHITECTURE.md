# RAMYXEN Diagnostics, Crash Reporting & UI Hang Watchdog Architecture

## 1. Local-First Privacy Guarantee

All diagnostics produced by `RAMYXEN.Diagnostics` are stored **strictly locally** under `%LOCALAPPDATA%\RAMYXEN\` (`logs\`, `crash-reports\`, `diagnostic-dumps\`, `exports\`; intentionally separated from user forensic `Dumps\`). RAMYXEN **never uploads crash reports, logs, or dumps automatically**. Every log entry, crash report, and ZIP export entry passes through `DiagnosticPrivacyRedactor` (all regexes bounded with 250ms ReDoS timeouts), which redacts:
- Exact `Environment.SpecialFolder.UserProfile` paths (raw, forward-slash, and JSON-escaped variants) regardless of username length, plus contextual 1–2 character username references (`for/by/as user <name>`, `username="<name>"`) and contextual short hostnames
- General `C:\Users\<name>`, `/Users/<name>`, `\\?\`, `\Device\HarddiskVolumeN\Users\<name>`, JSON-escaped and raw UNC network shares (`\\server\share`), `DOMAIN\User`, hostnames (`Environment.MachineName`), and domains
- `Authorization: Basic/Bearer/Token/ApiKey`, URL-embedded credentials (`https://user:pass@host`), quoted/unquoted/JSON secret key-value pairs (`api_key`, `access_token`, `db_password`, `secret`, etc.), quoted and unquoted connection string credentials, Windows SIDs (`S-1-...`), non-loopback IPv4 addresses (preserving `.NET`/SDK version quads such as `10.0.0.0` and `2.7.0.0`), and email addresses
- Local file paths inside exported JSON (`JsonFilePath` / `TextFilePath` replaced with `<LOCAL_REPORTS_DIR>/<filename>`)

## 2. Components

1. **`StructuredDiagnosticLogger` (`src/RAMYXEN.Diagnostics/DiagnosticServices.cs`):**
   - Thread-safe rolling JSONL files (`ramyxen-YYYYMMDD.jsonl`, 5 MiB max size, up to 5 retained files pruned on startup and rotation).
   - Export reading uses `FileShare.ReadWrite` under lock across active and rotated logs.
   - 250-entry in-memory ring buffer and 40-entry breadcrumb trail surfaced live in `#93 Diagnostics & Advanced`.
2. **`CrashAndDiagnosticReportService`:**
   - Wired to `AppDomain.CurrentDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`, `Application.UnhandledException`, and native ABI `Ramyxen_GetLastNativeError`.
   - `App.IsFatalOrCorrupting(ex)` walks inner/aggregate exceptions and refuses to swallow fatal or state-corrupting errors (`OutOfMemoryException`, `StackOverflowException`, `AccessViolationException`, `SEHException`, `DllNotFoundException`, `EntryPointNotFoundException`, `BadImageFormatException`, `TypeInitializationException`, `NullReferenceException`, `ObjectDisposedException`, `COMException`), with a burst rate guard and `App.DetachDiagnostics()` on window close.
   - **Honest .NET 10 Runtime Boundary:** On .NET 10, `StackOverflowException` and native corrupted-state exceptions (`AccessViolationException`) terminate the process directly at the runtime/OS level without executing managed catch handlers; post-mortem capture for those conditions relies on Windows Error Reporting (WER) local dumps.
   - Writes paired `.json` and `.txt` reports atomically (`Flush(flushToDisk: true)` + `File.Move(..., overwrite: true)`), prunes `crash-reports/` (max 30), `diagnostic-dumps/` (max 5 `ramyxen-consent-*.dmp` files only), and `exports/` (max 10 diagnostic ZIP bundles), and requires an **explicit WinUI `ContentDialog` consent prompt** before capturing any optional `MiniDumpNormal` (`.dmp`) off the UI thread.
3. **`UiHangWatchdog`:**
   - Dedicated background thread (`RAMYXEN.UiHangWatchdog`) with truthful `WatchdogState` (`Stopped`, `Running`, `SuspendedPower`, `SuspendedDebugger`, `Degraded`, `HangDetected`).
   - Enforces at most **one** pending heartbeat in `DispatcherQueue` (zero queue flooding), evaluates recovery (`_pendingHeartbeat == 0`) **before** dispatching the next heartbeat (preventing `HangDetected` recovery deadlock), registers real Win32 `PowerRegisterSuspendResumeNotification` callbacks (`powrprof.dll`) plus monotonic S0ix jump detection, and never fabricates synthetic stack traces in `StackTrace` (`StackTrace = null`; contextual metadata recorded in `NativeContext` and `RecentBreadcrumbs`).
4. **Non-Blocking Async Process Inspection & Isolated Heap Worker (`ProcessInspectionCoordinator`):**
   - Separates lightweight inspection (`_lightweightGate = SemaphoreSlim(3, 3)`) from on-demand heap walking (`_heapGate = SemaphoreSlim(1, 1)`) using a single authoritative `StartLightweightInspection` selection version increment and race-free CTS cancellation.
   - Executes external-process NT heap walks inside a dedicated short-lived child process (`RAMYXEN.HeapWorker.exe`) under a hard external `3,000 ms` timeout enforced via `Process.Kill(entireProcessTree: true)` with a 60-second `(PID, StartTicksUtc)` cooldown, concurrent stdout+stderr draining, ABI v102 handshake, and `GetProcessTimes` PID-reuse verification — guaranteeing that slow or suspended targets can never freeze the UI or starve lightweight process switching.
   - Also offloads Profiler GC/Hotspots, Virtual Hex reads, Memory Strings scanning (cancellable), and Forensics live/offline dump SHA-256 + MDMP parsing off the UI thread.
