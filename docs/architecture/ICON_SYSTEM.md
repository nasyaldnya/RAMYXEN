# RAMYXEN Iconography & Official Windows Process Icon Architecture

## 1. Official Application Icon (`RAMYXEN.ico`)

- **Source Brand Asset:** Original RAMYXEN brand artwork derived from [ramyxen-app-icon.svg](../../assets/application/ramyxen-app-icon.svg) (deep obsidian container `#090C10`, cyan hexagon core `#38BDF8`, violet highlight `#A78BFA`).
- **Multi-Resolution Container:** 7 embedded PNG/ICO frames (`16×16`, `24×24`, `32×32`, `48×48`, `64×64`, `128×128`, `256×256` px) stored at:
  - `src/RAMYXEN.App/Assets/RAMYXEN.ico` (embedded into `RAMYXEN.exe` via `<ApplicationIcon>` and applied to Window/Taskbar/Alt+Tab via `AppWindow.SetIcon` with zero leaked Win32 `HICON` handles)
  - `installers/RAMYXEN.Installer/Assets/RAMYXEN.ico` (embedded into `RAMYXEN-Setup.exe`)
- **Windows Shell & Shortcut Integration:** `RAMYXEN-Setup.exe` creates Desktop and Start Menu `.lnk` shortcuts via native Win32 COM (`IShellLinkW` + `IPersistFile`, zero PowerShell invocation), assigns `IconLocation` with explicit `,0` resource index (`RAMYXEN.ico,0`), writes `DisplayIcon` / `UninstallString` / `QuietUninstallString` / `EstimatedSize` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\RAMYXEN`, and invokes `SHChangeNotify(SHCNE_CREATE / SHCNE_ASSOCCHANGED, SHCNF_FLUSH)` so Windows Explorer refreshes shortcut icons immediately.

## 2. Offline Product UI Icon System (Microsoft Fluent System Icons — MIT)

- **Location:** `src/RAMYXEN.App/Icons/FluentIconCatalog.cs`
- **Design Guarantee:** 100% locally bundled offline `24×24` SVG path geometries preserved inside a canonical `24×24` `Canvas` within a WinUI 3 `Viewbox` (`Stretch = Stretch.None` on `Path`) so optical margins and aspect ratios remain exact across 100%–200% DPI on 1080p, 2K, and 4K displays. Zero runtime network downloads, zero external font dependencies, and zero ASCII box-drawing or text-arrow fallbacks.
- **License Attribution:** Recorded in [THIRD_PARTY_NOTICES.md](../security/THIRD_PARTY_NOTICES.md) (`@fluentui/svg-icons` `1.1.x` reference, MIT License), explicitly distinguishing first-party `FluentGlyph.BrandMark` geometry.

## 3. Official Windows Process Icons (`chrome.exe`, `explorer.exe`, etc.)

- **Native Extraction (`RAMYXEN.Native.dll` ABI `v102`):** `Ramyxen_ExtractExecutableIconBgra` extracts the executable's official Windows icon via `PrivateExtractIconsW` / `ExtractIconExW` (`PE_RESOURCE`) first, falling back to `SHGetFileInfoW` wrapped in RAII `ScopedComInit` (`SHELL_FILE`) without `SHGFI_USEFILEATTRIBUTES` (preventing generic file-type icon spoofing). Rasterization uses a 32-bit top-down BGRA DIB section via `DrawIconEx` with `DI_MASK` alpha recovery for legacy 1-bit masked icons and RAII `ScopedIcon`/`ScopedGdiObject`/`ScopedMemoryDC` guards — verified at **strictly `0` GDI and `0` USER handle delta** across 40 checked iterations in `RAMYXEN.Native.Tests.exe`.
- **Async Managed Cache (`WindowsShellIconService` & `WinUiProcessIconCache`):**
  - Pure O(1) in-memory `TryGetCached` / `BuildCacheKey` with **zero UI-thread disk `stat()` calls**; all filesystem checks and native extractions execute exclusively on background threads (`SemaphoreSlim(3, 3)`).
  - Explicit `ExecutableIconProvenance` (`PeResource`, `ShellFile`, `GenericFileType`, `None`), 30-second negative-cache TTL, caller-cancellation decoupling, DPI-aware raster extraction (`32px` @ 100%, `48px` @ 150%, `64px` @ 200%), and true 256-entry LRU eviction in both `WindowsShellIconService` and `WinUiProcessIconCache`.
  - Graceful Fluent vector category fallback (`BrowserFallback`, `CodeFallback`, `SystemFallback`, `AppFallback`) for restricted, system-only, or terminated processes without throwing or blocking.
