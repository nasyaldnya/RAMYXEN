using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using RAMYXEN.App.Icons;
using RAMYXEN.App.Services;
using RAMYXEN.Application.Services;
using RAMYXEN.Core.Models;
using RAMYXEN.Diagnostics;
using RAMYXEN.ETW;
using RAMYXEN.Forensics;
using RAMYXEN.Infrastructure.Windows;
using RAMYXEN.Localization.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using WinRT.Interop;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using Path = System.IO.Path;

namespace RAMYXEN.App.Views;

public sealed class RamyxenMainWindow : Window
{
    private readonly RamyxenPlatformHost _host;
    private readonly WinUiProcessIconCache _processIconCache;
    private readonly UiHangWatchdog _hangWatchdog;
    private readonly DispatcherQueueTimer _timer;
    private readonly DispatcherQueueTimer? _autoCloseTimer;
    private readonly bool _isAdministrator;
    private int _isSamplingBackground;

    private string _activeRoute = "overview";
    private string _processSearchQuery = "";
    private string _stringSearchQuery = "";
    private ulong _selectedRegionBase;
    private string _lastStatusMessage = "";
    private string? _pendingDeleteSnapshotId;
    private string? _activeOfflineDumpPath;
    private bool _recoveryBannerDismissed;

    // Responsive layout & dynamic DPI tracking (DIPs & RasterizationScale)
    private double _currentWidthDip = 1600;
    private double _currentHeightDip = 960;
    private double _currentScaleFactor = 1.0;
    private int _currentBreakpoint = 1; // 0=Compact (<1180), 1=Standard (1180..1699), 2=Wide 2K/4K (>=1700)
    private XamlRoot? _hookedXamlRoot;
    private volatile bool _isClosed;
    private bool _isConsentDialogOpen;
    private readonly List<ScrollViewer> _activeWorkspaceScrollViewers = new();

    // Async deep-dive, profiler, hex, strings & forensics state (zero UI-thread stalls - F-002)
    private ProcessDeepDivePayload? _activeInspection;
    private long _lastRequestedInspectionPid;
    private ProfilerSnapshotPayload? _activeProfilerPayload;
    private uint _loadingProfilerPid;
    private byte[]? _activeHexBytes;
    private ulong _activeHexAddress;
    private uint _activeHexPid;
    private bool _isLoadingHex;
    private IReadOnlyList<ExtractedMemoryString>? _activeStringsResults;
    private uint _activeStringsPid;
    private string _activeStringsQuery = "";
    private bool _isSearchingStrings;
    private CancellationTokenSource? _stringsSearchCts;
    private ForensicLivePayload? _activeForensicsLive;
    private uint _loadingForensicsLivePid;
    private ForensicDumpPayload? _activeForensicsDump;
    private string? _loadingForensicsDumpPath;

    // Local filter search box reference for distinct Ctrl+F focus (F-045)
    private TextBox? _activeLocalSearchBox;
    private ScrollViewer? _activeWorkspaceScrollViewer;

    // Command Palette overlay state (F-012, F-041)
    private Grid? _commandPaletteOverlay;
    private TextBox? _paletteSearchBox;
    private ScrollViewer? _paletteResultsScroll;
    private StackPanel? _paletteResultsPanel;
    private TextBlock? _paletteCountText;
    private List<PaletteEntry> _currentPaletteMatches = [];
    private int _paletteTotalMatchCount;
    private int _paletteSelectedIndex;
    private bool _suppressPaletteTextChanged;
    private object? _previousFocusBeforePalette;

    // Theme Palette Brushes (initialized in ApplyThemePalette)
    private SolidColorBrush _bgCanvas = null!;
    private SolidColorBrush _bgZone = null!;
    private SolidColorBrush _bgSurface1 = null!;
    private SolidColorBrush _bgSurface2 = null!;
    private SolidColorBrush _seamBrush = null!;
    private SolidColorBrush _textPrimary = null!;
    private SolidColorBrush _textSecondary = null!;
    private SolidColorBrush _textMuted = null!;
    private SolidColorBrush _accentCyan = null!;
    private SolidColorBrush _accentViolet = null!;
    private SolidColorBrush _semEmerald = null!;
    private SolidColorBrush _semAmber = null!;
    private SolidColorBrush _semCrimson = null!;
    private SolidColorBrush _semOrange = null!;

    private FontFamily _fontUi = new("Segoe UI Variable, Segoe UI");
    private readonly FontFamily _fontMono = new("Cascadia Code, Consolas");

    private Grid _rootGrid = null!;
    private Border _topBar = null!;
    private Grid _titleDragArea = null!;
    private Button? _paletteTriggerBtn;
    private ColumnDefinition _navColumnDef = null!;
    private StackPanel _navStack = null!;
    private Grid _workspaceHost = null!;
    private Border _recoveryBannerHost = null!;
    private TextBlock _breadcrumbText = null!;
    private TextBlock _statusLeftText = null!;
    private TextBlock _statusRightText = null!;

    private sealed record PaletteEntry(
        string Title,
        string Description,
        string CategoryLabel,
        string ShortcutBadge,
        FluentGlyph Icon,
        string SearchHaystackNormalized,
        Action Execute);

    public RamyxenMainWindow(int autoCloseMs = 0, bool verifyAllViews = false, string? screenshotOutputDir = null, int initialWidth = 1600, int initialHeight = 960)
    {
        _host = new RamyxenPlatformHost();
        _isAdministrator = WindowsProcessCollector.IsRunningAsAdministrator();
        _host.RefreshTelemetry();
        _processIconCache = new WinUiProcessIconCache(_host.ProcessIcons, DispatcherQueue);

        // Wire global managed crash handler to this host's report service
        App.AttachDiagnostics(_host.CrashReports, report =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _recoveryBannerDismissed = false;
                _lastStatusMessage = report.Summary;
                UpdateRecoveryBanner();
                UpdateStatusBarOnly();
            });
        });

        // Start dedicated background UI Hang Watchdog (bounded, power-aware, debugger-aware)
        _hangWatchdog = new UiHangWatchdog(
            action => DispatcherQueue.TryEnqueue(() => action()),
            _host.CrashReports,
            _host.DiagnosticsLogger,
            heartbeatIntervalMs: 1500,
            hangThresholdMs: 5000);
        if (!verifyAllViews && string.IsNullOrWhiteSpace(screenshotOutputDir))
        {
            _hangWatchdog.Start();
        }

        var loc = _host.Localization;
        Title = $"{loc.GetString("app.name")} — {loc.GetString("app.tagline")}";
        _lastStatusMessage = loc.GetString("state.ready");

        // Configure official RAMYXEN.ico, integrated dark/light title bar, and DPI-aware initial window size BEFORE Activate()
        var metrics = WindowChromeManager.ConfigureDpiAwareWindowSize(this, initialWidth, initialHeight, clampToWorkArea: string.IsNullOrWhiteSpace(screenshotOutputDir));
        _currentWidthDip = metrics.CurrentWidthDip;
        _currentHeightDip = metrics.CurrentHeightDip;
        _currentScaleFactor = metrics.ScaleFactor;
        _currentBreakpoint = ComputeBreakpoint(_currentWidthDip);
        _host.DiagnosticState.DisplayMetrics = metrics.Summary;

        ApplyThemePalette(_host.Settings.Current.Theme);
        WindowChromeManager.ApplyIconAndChrome(this, _host.Settings.Current.Theme);

        BuildShell();
        RenderActiveWorkspace();

        SizeChanged += (_, args) =>
        {
            _currentWidthDip = args.Size.Width;
            _currentHeightDip = args.Size.Height;
            var curMetrics = WindowChromeManager.GetCurrentDisplayMetrics(this, _currentWidthDip, _currentHeightDip);
            _currentScaleFactor = curMetrics.ScaleFactor;
            _host.DiagnosticState.DisplayMetrics = curMetrics.Summary;

            if (_topBar is not null)
            {
                _topBar.Padding = WindowChromeManager.ComputeLogicalTitleBarPadding(this, _host.Localization.IsRightToLeft);
            }

            int nextBp = ComputeBreakpoint(_currentWidthDip);
            if (_navColumnDef is not null)
            {
                _navColumnDef.Width = new GridLength(GetNavRailWidth(nextBp));
            }
            if (nextBp != _currentBreakpoint)
            {
                _currentBreakpoint = nextBp;
                BuildShell();
                RenderActiveWorkspace();
            }
        };

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_host.Settings.Current.SamplingIntervalMs, 500, 10000));
        _timer.Tick += async (_, _) =>
        {
            if (_isClosed || _host.Monitor.IsPaused) return;
            if (Interlocked.CompareExchange(ref _isSamplingBackground, 1, 0) != 0) return;
            try
            {
                await Task.Run(() => _host.RefreshTelemetry());
                if (_isClosed) return;
                UpdateStatusBarOnly();
                // Non-destructive live refresh: never nuke Command Palette, active keyboard focus inside workspace, or scroll offsets (F-025)
                bool paletteOpen = _commandPaletteOverlay?.Visibility == Visibility.Visible;
                var focused = Content?.XamlRoot is { } xr ? FocusManager.GetFocusedElement(xr) : null;
                bool typingOrFocusedControl = focused is TextBox or Button or HyperlinkButton;
                if (!paletteOpen && !typingOrFocusedControl && _activeRoute is "overview" or "live-monitor" or "profiler-recording")
                {
                    RenderActiveWorkspace(preserveScroll: true);
                }
            }
            catch (Exception ex)
            {
                if (!_isClosed)
                    _host.DiagnosticsLogger.Error("TelemetryLoop", "Background telemetry sampling exception.", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _isSamplingBackground, 0);
            }
        };
        _timer.Start();

        if (verifyAllViews || !string.IsNullOrWhiteSpace(screenshotOutputDir) || !string.IsNullOrWhiteSpace(App.LocaleScreenshotOutputDir))
        {
            _timer.Stop();
            DispatcherQueue.TryEnqueue(async () =>
            {
                await RunAutomatedVerificationAndScreenshotsAsync(verifyAllViews, screenshotOutputDir);
            });
        }
        else if (autoCloseMs > 0)
        {
            _autoCloseTimer = DispatcherQueue.CreateTimer();
            _autoCloseTimer.Interval = TimeSpan.FromMilliseconds(autoCloseMs);
            _autoCloseTimer.Tick += (_, _) =>
            {
                _autoCloseTimer.Stop();
                Close();
            };
            _autoCloseTimer.Start();
        }

        Closed += (_, _) =>
        {
            _isClosed = true;
            _timer.Stop();
            if (_hookedXamlRoot is not null)
            {
                try { _hookedXamlRoot.Changed -= OnXamlRootChanged; } catch { }
                _hookedXamlRoot = null;
            }
            _stringsSearchCts?.Cancel();
            _stringsSearchCts?.Dispose();
            App.DetachDiagnostics();
            _hangWatchdog.Dispose();
            _processIconCache.Clear();
            _host.Dispose();
        };
    }

    private void HookDynamicDpiTracking()
    {
        var currentRoot = _rootGrid?.XamlRoot;
        if (_isClosed || currentRoot is null)
            return;
        if (ReferenceEquals(_hookedXamlRoot, currentRoot))
            return;

        if (_hookedXamlRoot is not null)
        {
            try { _hookedXamlRoot.Changed -= OnXamlRootChanged; } catch { }
        }
        _hookedXamlRoot = currentRoot;
        _currentScaleFactor = currentRoot.RasterizationScale;
        currentRoot.Changed += OnXamlRootChanged;
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (_isClosed || _rootGrid?.XamlRoot is null) return;
        double newScale = sender.RasterizationScale;
        if (Math.Abs(newScale - _currentScaleFactor) > 0.01)
        {
            _currentScaleFactor = newScale;
            var refreshed = WindowChromeManager.RefreshMinimumWindowSize(this);
            _host.DiagnosticState.DisplayMetrics = refreshed.Summary;
            if (_topBar is not null)
            {
                _topBar.Padding = WindowChromeManager.ComputeLogicalTitleBarPadding(this, _host.Localization.IsRightToLeft);
            }
            _host.DiagnosticsLogger.Info("DisplayDpi", $"Dynamic monitor DPI changed: {refreshed.Summary}");
            RenderActiveWorkspace(preserveScroll: true);
        }
    }

    private static int ComputeBreakpoint(double widthDip) =>
        widthDip < 1180 ? 0 : widthDip >= 1700 ? 2 : 1;

    private static double GetNavRailWidth(int bp) => bp switch
    {
        0 => 188,
        2 => 242,
        _ => 222
    };

    private string FmtBytes(ulong bytes) =>
        UnitFormatter.FormatBytes(bytes, _host.Settings.Current.UseBinaryUnits, _host.Localization.CurrentCulture);

    private string FmtDelta(long deltaBytes) =>
        UnitFormatter.FormatSignedDeltaBytes(deltaBytes, _host.Localization.CurrentCulture);

    private string FmtNum(long value) => value.ToString("N0", _host.Localization.CurrentCulture);
    private string FmtNum(ulong value) => value.ToString("N0", _host.Localization.CurrentCulture);
    private string FmtDec(double value) => value.ToString("N1", _host.Localization.CurrentCulture);

    private string GetLocalizedThemeName(AppThemeMode mode) => mode switch
    {
        AppThemeMode.Light => _host.Localization.GetString("toolbar.themeLight"),
        AppThemeMode.HighContrast => _host.Localization.GetString("toolbar.themeHighContrast"),
        _ => _host.Localization.GetString("toolbar.themeDark")
    };

    private static FluentGlyph GetThemeGlyph(AppThemeMode mode) => mode switch
    {
        AppThemeMode.Light => FluentGlyph.ThemeLight,
        AppThemeMode.HighContrast => FluentGlyph.ThemeContrast,
        _ => FluentGlyph.ThemeDark
    };

    private string GetLocalizedWatchdogState(WatchdogState state) => state switch
    {
        WatchdogState.Running => _host.Localization.GetString("diagnostics.stateRunning"),
        WatchdogState.SuspendedPower => _host.Localization.GetString("diagnostics.stateSuspendedPower"),
        WatchdogState.SuspendedDebugger => _host.Localization.GetString("diagnostics.stateSuspendedDebugger"),
        WatchdogState.Degraded => _host.Localization.GetString("diagnostics.stateDegraded"),
        WatchdogState.HangDetected => _host.Localization.GetString("diagnostics.stateHangDetected"),
        _ => _host.Localization.GetString("diagnostics.stateStopped")
    };

    // =========================================================================
    // AUTOMATED VERIFICATION (98/98 REAL LAYOUT & ROUTE IDENTITY) & SCREENSHOTS
    // =========================================================================
    private async Task RunAutomatedVerificationAndScreenshotsAsync(bool verifyAllViews, string? screenshotOutputDir)
    {
        try
        {
            if (verifyAllViews)
            {
                var loc = _host.Localization;
                int verifiedRoutes = 0;
                var failures = new List<string>();

                foreach (var d in DestinationRegistry.All98)
                {
                    Navigate(d.RouteKey);
                    await Task.Delay(15);
                    _rootGrid.UpdateLayout();

                    if (d.RouteKey == "command-palette")
                    {
                        bool opened = _commandPaletteOverlay?.Visibility == Visibility.Visible;
                        if (!opened) failures.Add($"#{d.Id:D2} {d.RouteKey}: Command Palette did not become visible");
                        CloseCommandPalette();
                        await Task.Delay(10);
                        _rootGrid.UpdateLayout();
                        if (_commandPaletteOverlay?.Visibility != Visibility.Collapsed)
                            failures.Add($"#{d.Id:D2} {d.RouteKey}: Command Palette failed to close");
                        verifiedRoutes++;
                        continue;
                    }

                    bool overlayClear = _commandPaletteOverlay?.Visibility == Visibility.Collapsed;
                    bool hasChildren = _workspaceHost.Children.Count > 0;
                    bool hasBounds = _rootGrid.ActualWidth > 0 && _rootGrid.ActualHeight > 0;
                    string expectedTitle = DestinationRegistry.GetLocalizedTitle(loc, d.RouteKey);
                    bool titleMatches = string.Equals(_breadcrumbText.Text, expectedTitle, StringComparison.Ordinal);

                    if (!overlayClear || !hasChildren || !hasBounds || !titleMatches)
                    {
                        failures.Add($"#{d.Id:D2} {d.RouteKey}: overlayClear={overlayClear}, hasChildren={hasChildren}, bounds={_rootGrid.ActualWidth:F0}x{_rootGrid.ActualHeight:F0}, titleMatch={titleMatches}");
                    }
                    else
                    {
                        verifiedRoutes++;
                    }
                }

                // Test rapid async process switching across real running processes (including Chrome if present)
                var chromeProc = _host.Monitor.LatestProcesses.FirstOrDefault(p => p.Name.Contains("chrome", StringComparison.OrdinalIgnoreCase));
                var sampleProcs = _host.Monitor.LatestProcesses.Take(5).ToList();
                if (chromeProc is not null && !sampleProcs.Any(p => p.Pid == chromeProc.Pid))
                    sampleProcs.Insert(0, chromeProc);

                foreach (var p in sampleProcs)
                {
                    SelectProcessAndNavigate(p.Pid, "process-deep-dive");
                    await Task.Delay(40);
                }

                // Verify Arabic Command Palette filtering (including normalized Alef/Teh Marbuta) and RTL layout
                _host.SetLanguage("ar-SA");
                BuildShell();
                OpenCommandPalette("الذاكرة");
                await Task.Delay(80);
                int arMatches = _paletteTotalMatchCount;
                CloseCommandPalette();
                if (arMatches == 0)
                    failures.Add("Arabic Command Palette returned 0 matches for 'الذاكرة'");

                foreach (var key in new[] { "overview", "live-monitor", "process-explorer", "process-tree", "process-deep-dive", "virtual-overview", "physical-overview", "profiler-home", "snapshot-comparison", "forensics-home", "alerts-center", "report-center", "settings-general", "settings-advanced", "welcome", "about" })
                {
                    Navigate(key);
                    await Task.Delay(15);
                    _rootGrid.UpdateLayout();
                }

                foreach (var tm in new[] { AppThemeMode.Light, AppThemeMode.HighContrast, AppThemeMode.Dark })
                {
                    ApplyThemeAndRebuild(tm);
                    Navigate("overview");
                    await Task.Delay(20);
                    _rootGrid.UpdateLayout();
                }

                _host.SetLanguage("en-US");
                BuildShell();
                Navigate("overview");
                await Task.Delay(20);

                string summary = failures.Count == 0
                    ? $"VERIFY_ALL_98_VIEWS=OK ({verifiedRoutes}/98 routes layout+identity verified, arMatches={arMatches})"
                    : $"VERIFY_ALL_98_VIEWS=FAIL ({verifiedRoutes}/98)\n" + string.Join("\n", failures);
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "verify_views_result.txt"), summary);
            }

            if (!string.IsNullOrWhiteSpace(screenshotOutputDir))
            {
                Directory.CreateDirectory(screenshotOutputDir);

                if (_host.Snapshots.GetSnapshots().Count < 2 && _host.Monitor.LatestSystem != null)
                {
                    _host.Snapshots.CreateTelemetrySnapshot("Baseline System Capture", _host.Monitor.LatestSystem, _host.Monitor.LatestProcesses);
                    _host.RefreshTelemetry();
                    _host.Snapshots.CreateTelemetrySnapshot("Target System Capture", _host.Monitor.LatestSystem, _host.Monitor.LatestProcesses);
                }

                int extractPx = WinUiProcessIconCache.SelectExtractionSizePx(_currentScaleFactor);
                var warmTasks = _host.Monitor.LatestProcesses
                    .Where(p => !string.IsNullOrWhiteSpace(p.ExecutablePath) && Path.IsPathRooted(p.ExecutablePath))
                    .Take(32)
                    .Select(p => _host.ProcessIcons.ResolveAsync(p.ExecutablePath, extractPx))
                    .ToList();
                await Task.WhenAll(warmTasks);

                // 1. Standard 1080p-class DIP window (1440x860 DIP, Dark, en-US)
                WindowChromeManager.ConfigureDpiAwareWindowSize(this, 1440, 860, clampToWorkArea: true);
                _host.SetLanguage("en-US");
                _lastStatusMessage = _host.Localization.GetString("state.ready");
                ApplyThemeAndRebuild(AppThemeMode.Dark);
                Navigate("overview");
                await Task.Delay(340);
                await CaptureRootGridToPngAsync(Path.Combine(screenshotOutputDir, "01_main_overview_dark_en_1080p_class.png"));
                CaptureDesktopWindowPng(Path.Combine(screenshotOutputDir, "01b_desktop_composited_titlebar_ltr_dark.png"));

                // 2. Compact 1024x700 DIP responsive test (proving zero header overflow & clean >=220 DIP drag space - F-010)
                WindowChromeManager.ConfigureDpiAwareWindowSize(this, 1024, 700, clampToWorkArea: true);
                await Task.Delay(300);
                await CaptureRootGridToPngAsync(Path.Combine(screenshotOutputDir, "02_compact_1024dip_responsive_dark_en.png"));

                // Restore 1440x860 for remaining feature screenshots
                WindowChromeManager.ConfigureDpiAwareWindowSize(this, 1440, 860, clampToWorkArea: true);
                await Task.Delay(250);

                // 3. Process Explorer showing official Windows app icons (chrome.exe, explorer.exe, etc.)
                Navigate("process-explorer");
                await Task.Delay(350);
                await CaptureRootGridToPngAsync(Path.Combine(screenshotOutputDir, "03_process_explorer_official_icons_dark_en.png"));

                // 4. Process Deep Dive on chrome.exe (or top real app) — proving instant non-blocking load + official icon + real DLL versions
                var targetProc = _host.Monitor.LatestProcesses.FirstOrDefault(p => p.Name.Contains("chrome", StringComparison.OrdinalIgnoreCase))
                              ?? _host.Monitor.LatestProcesses.FirstOrDefault(p => !p.IsProtectedOrRestricted && Path.IsPathRooted(p.ExecutablePath))
                              ?? _host.Monitor.LatestProcesses.First();
                await _host.ProcessIcons.ResolveAsync(targetProc.ExecutablePath, extractPx);
                SelectProcessAndNavigate(targetProc.Pid, "process-deep-dive");
                await Task.Delay(650);
                await CaptureRootGridToPngAsync(Path.Combine(screenshotOutputDir, "04_process_deep_dive_async_dark_en.png"));

                // 5. English Command Palette Modal (Ctrl+K)
                Navigate("overview");
                await Task.Delay(200);
                OpenCommandPalette("");
                await Task.Delay(250);
                await CaptureRootGridToPngAsync(Path.Combine(screenshotOutputDir, "05_command_palette_english_dark.png"));
                CloseCommandPalette();

                // 6. Arabic RTL (ar-SA) Main Overview + Desktop-Composited Real Title Bar Proof (F-004, F-008, F-026)
                _host.SetLanguage("ar-SA");
                _lastStatusMessage = _host.Localization.GetString("state.ready");
                BuildShell();
                Navigate("overview");
                await Task.Delay(380);
                await CaptureRootGridToPngAsync(Path.Combine(screenshotOutputDir, "06_main_overview_arabic_rtl_dark.png"));
                CaptureDesktopWindowPng(Path.Combine(screenshotOutputDir, "06b_desktop_composited_titlebar_arabic_rtl_dark.png"));

                // 7. Arabic RTL Command Palette Modal (100% localized Arabic, CLDR plurals, zero English fallback!)
                OpenCommandPalette("");
                await Task.Delay(260);
                await CaptureRootGridToPngAsync(Path.Combine(screenshotOutputDir, "07_command_palette_arabic_rtl_dark.png"));
                CloseCommandPalette();

                // 8. Daylight Light Theme (proving WCAG AA white stratum contrast & crisp light title bar - F-024)
                _host.SetLanguage("en-US");
                _lastStatusMessage = _host.Localization.GetString("state.ready");
                ApplyThemeAndRebuild(AppThemeMode.Light);
                Navigate("overview");
                await Task.Delay(320);
                await CaptureRootGridToPngAsync(Path.Combine(screenshotOutputDir, "08_main_overview_light_en.png"));

                // 9. High Contrast Theme
                ApplyThemeAndRebuild(AppThemeMode.HighContrast);
                Navigate("overview");
                await Task.Delay(300);
                await CaptureRootGridToPngAsync(Path.Combine(screenshotOutputDir, "09_main_overview_high_contrast_en.png"));

                // 10. Diagnostics, Crash Logs & Watchdog Workspace (#93) in Obsidian Dark
                ApplyThemeAndRebuild(AppThemeMode.Dark);
                Navigate("settings-advanced");
                await Task.Delay(300);
                await CaptureRootGridToPngAsync(Path.Combine(screenshotOutputDir, "10_diagnostics_watchdog_dark_en.png"));

                // 11. Live Signal Observatory, Snapshot Comparison & Leaks
                foreach (var (route, fileName) in new (string, string)[]
                {
                    ("live-monitor", "11_live_signal_observatory_dark_en.png"),
                    ("snapshot-comparison", "12_snapshot_comparison_dark_en.png"),
                    ("alerts-center", "13_leak_investigation_dark_en.png")
                })
                {
                    Navigate(route);
                    await Task.Delay(260);
                    await CaptureRootGridToPngAsync(Path.Combine(screenshotOutputDir, fileName));
                }

                // 12. Physical 2K QHD host maximized-equivalent viewport (~1660x920 DIP @ 150%)
                WindowChromeManager.ConfigureDpiAwareWindowSize(this, 1660, 920, clampToWorkArea: true);
                Navigate("overview");
                await Task.Delay(320);
                await CaptureRootGridToPngAsync(Path.Combine(screenshotOutputDir, "14_overview_2k_qhd_150pct_physical.png"));

                // 13. Simulated 4K UHD @ 200% scaling viewport (1920x1080 logical DIPs; explicitly documented as simulated)
                WindowChromeManager.ConfigureDpiAwareWindowSize(this, 1920, 1080, clampToWorkArea: false);
                Navigate("overview");
                await Task.Delay(320);
                await CaptureRootGridToPngAsync(Path.Combine(screenshotOutputDir, "15_overview_4k_200pct_simulated_viewport.png"));

                // Restore defaults
                WindowChromeManager.ConfigureDpiAwareWindowSize(this, 1600, 960, clampToWorkArea: true);
                _host.SetLanguage("en-US");
                ApplyThemeAndRebuild(AppThemeMode.Dark);
                Navigate("overview");
            }

            if (!string.IsNullOrWhiteSpace(App.LocaleScreenshotOutputDir))
            {
                await CaptureFourLocaleScreenshotsAsync(App.LocaleScreenshotOutputDir);
            }
        }
        finally
        {
            Close();
        }
    }

    private async Task CaptureFourLocaleScreenshotsAsync(string rootDir)
    {
        int extractPx = WinUiProcessIconCache.SelectExtractionSizePx(_currentScaleFactor);
        var warmTasks = _host.Monitor.LatestProcesses
            .Where(p => !string.IsNullOrWhiteSpace(p.ExecutablePath) && Path.IsPathRooted(p.ExecutablePath))
            .Take(24)
            .Select(p => _host.ProcessIcons.ResolveAsync(p.ExecutablePath, extractPx))
            .ToList();
        await Task.WhenAll(warmTasks);

        WindowChromeManager.ConfigureDpiAwareWindowSize(this, 1440, 860, clampToWorkArea: true);
        var localeMap = new (string Locale, string Folder)[]
        {
            ("en-US", "en"),
            ("ar-SA", "ar"),
            ("zh-CN", "zh-CN"),
            ("ja-JP", "ja")
        };

        var views = new (string Route, string FileName)[]
        {
            ("overview", "01_overview.png"),
            ("process-explorer", "02_process_explorer.png"),
            ("virtual-overview", "03_virtual_memory.png"),
            ("physical-overview", "04_physical_memory.png"),
            ("profiler-home", "05_allocation_profiler.png"),
            ("forensics-home", "06_memory_forensics.png"),
            ("settings-advanced", "07_diagnostics.png")
        };

        foreach (var (locale, folder) in localeMap)
        {
            string outDir = Path.Combine(rootDir, folder);
            Directory.CreateDirectory(outDir);
            _host.SetLanguage(locale);
            _lastStatusMessage = _host.Localization.GetString("state.ready");
            ApplyThemeAndRebuild(AppThemeMode.Dark);

            foreach (var (route, fileName) in views)
            {
                Navigate(route);
                await Task.Delay(320);
                await CaptureRootGridToPngAsync(Path.Combine(outDir, fileName));
            }

            Navigate("overview");
            await Task.Delay(200);
            OpenCommandPalette("");
            await Task.Delay(260);
            await CaptureRootGridToPngAsync(Path.Combine(outDir, "08_command_palette.png"));
            CloseCommandPalette();
        }

        _host.SetLanguage("en-US");
        ApplyThemeAndRebuild(AppThemeMode.Dark);
    }

    private async Task CaptureRootGridToPngAsync(string outputPath)
    {
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(_rootGrid);
        IBuffer pixelBuffer = await rtb.GetPixelsAsync();
        uint width = (uint)Math.Max(1, rtb.PixelWidth);
        uint height = (uint)Math.Max(1, rtb.PixelHeight);

        double dpi = WindowChromeManager.GetWindowDpi(this);
        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var ras = fs.AsRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ras);

        byte[] bytes = new byte[pixelBuffer.Length];
        using (var reader = DataReader.FromBuffer(pixelBuffer))
        {
            reader.ReadBytes(bytes);
        }

        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            width,
            height,
            dpi,
            dpi,
            bytes);
        await encoder.FlushAsync();
    }

    /// <summary>
    /// Captures the real OS-composited window (including Windows caption Minimize/Maximize/Close buttons)
    /// using Win32 PrintWindow(PW_RENDERFULLCONTENT) to provide verifiable visual evidence for F-004!
    /// </summary>
    private void CaptureDesktopWindowPng(string outputPath)
    {
        try
        {
            nint hwnd = WindowNative.GetWindowHandle(this);
            if (hwnd == nint.Zero || !GetWindowRect(hwnd, out var rect))
                return;

            int w = rect.Right - rect.Left;
            int h = rect.Bottom - rect.Top;
            if (w <= 0 || h <= 0) return;

            nint screenDc = GetDC(nint.Zero);
            if (screenDc == nint.Zero) return;
            nint memDc = CreateCompatibleDC(screenDc);
            nint hBmp = nint.Zero;
            nint oldObj = nint.Zero;
            try
            {
                var bi = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h, // top-down DIB
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0
                };
                hBmp = CreateDIBSection(memDc, ref bi, 0, out nint bitsPtr, nint.Zero, 0);
                if (hBmp == nint.Zero || bitsPtr == nint.Zero) return;

                oldObj = SelectObject(memDc, hBmp);
                bool ok = PrintWindow(hwnd, memDc, 0x00000002); // PW_RENDERFULLCONTENT
                if (!ok)
                    ok = BitBlt(memDc, 0, 0, w, h, screenDc, rect.Left, rect.Top, 0x00CC0020); // SRCCOPY

                if (!ok) return;

                byte[] bgra = new byte[w * h * 4];
                Marshal.Copy(bitsPtr, bgra, 0, bgra.Length);
                // Force opaque alpha on DWM window capture
                for (int i = 3; i < bgra.Length; i += 4) bgra[i] = 255;

                double dpi = WindowChromeManager.GetWindowDpi(this);
                using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                using var ras = fs.AsRandomAccessStream();
                var encoder = BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ras).GetAwaiter().GetResult();
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)w, (uint)h, dpi, dpi, bgra);
                encoder.FlushAsync().GetAwaiter().GetResult();
            }
            finally
            {
                if (oldObj != nint.Zero) SelectObject(memDc, oldObj);
                if (hBmp != nint.Zero) DeleteObject(hBmp);
                if (memDc != nint.Zero) DeleteDC(memDc);
                ReleaseDC(nint.Zero, screenDc);
            }
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint hWnd, nint hdcBlt, uint nFlags);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hWnd, nint hDC);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint hdc, ref BITMAPINFOHEADER pbmi, uint iUsage, out nint ppvBits, nint hSection, uint dwOffset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint ho);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint hdcDest, int x, int y, int cx, int cy, nint hdcSrc, int x1, int y1, uint rop);

    // =========================================================================
    // THEME PALETTE & WINDOW CHROME
    // =========================================================================
    private void ApplyThemeAndRebuild(AppThemeMode mode)
    {
        _host.SetTheme(mode);
        ApplyThemePalette(mode);
        WindowChromeManager.ApplyThemeToWindowChrome(this, mode);
        BuildShell();
        RenderActiveWorkspace();
    }

    private void ApplyThemePalette(AppThemeMode mode)
    {
        if (mode == AppThemeMode.Light)
        {
            _bgCanvas = new(Color.FromArgb(255, 241, 245, 249));
            _bgZone = new(Color.FromArgb(255, 248, 250, 252));
            _bgSurface1 = new(Color.FromArgb(255, 255, 255, 255));
            _bgSurface2 = new(Color.FromArgb(255, 226, 232, 240));
            _seamBrush = new(Color.FromArgb(255, 203, 213, 225));
            _textPrimary = new(Color.FromArgb(255, 15, 23, 42));
            _textSecondary = new(Color.FromArgb(255, 51, 65, 85));
            _textMuted = new(Color.FromArgb(255, 71, 85, 105));     // #475569 (6.73:1 on #F1F5F9 - WCAG AA PASS)
            _accentCyan = new(Color.FromArgb(255, 3, 105, 161));    // #0369A1 (5.93:1 white-on-cyan, 4.81:1 on #E2E8F0 - WCAG AA PASS)
            _accentViolet = new(Color.FromArgb(255, 109, 40, 217));
            _semEmerald = new(Color.FromArgb(255, 4, 120, 87));
            _semAmber = new(Color.FromArgb(255, 180, 83, 9));
            _semCrimson = new(Color.FromArgb(255, 185, 28, 28));
            _semOrange = new(Color.FromArgb(255, 194, 65, 12));
        }
        else if (mode == AppThemeMode.HighContrast)
        {
            _bgCanvas = new(Color.FromArgb(255, 0, 0, 0));
            _bgZone = new(Color.FromArgb(255, 0, 0, 0));
            _bgSurface1 = new(Color.FromArgb(255, 12, 12, 12));
            _bgSurface2 = new(Color.FromArgb(255, 28, 28, 28));
            _seamBrush = new(Color.FromArgb(255, 255, 255, 255));
            _textPrimary = new(Color.FromArgb(255, 255, 255, 255));
            _textSecondary = new(Color.FromArgb(255, 240, 240, 240));
            _textMuted = new(Color.FromArgb(255, 215, 215, 215));
            _accentCyan = new(Color.FromArgb(255, 0, 255, 255));
            _accentViolet = new(Color.FromArgb(255, 255, 119, 255));
            _semEmerald = new(Color.FromArgb(255, 0, 255, 136));
            _semAmber = new(Color.FromArgb(255, 255, 255, 0));
            _semCrimson = new(Color.FromArgb(255, 255, 85, 85));
            _semOrange = new(Color.FromArgb(255, 255, 170, 0));
        }
        else
        {
            _bgCanvas = new(Color.FromArgb(255, 9, 12, 16));
            _bgZone = new(Color.FromArgb(255, 13, 17, 23));
            _bgSurface1 = new(Color.FromArgb(255, 17, 23, 32));
            _bgSurface2 = new(Color.FromArgb(255, 24, 33, 47));
            _seamBrush = new(Color.FromArgb(255, 31, 42, 60));
            _textPrimary = new(Color.FromArgb(255, 240, 244, 248));
            _textSecondary = new(Color.FromArgb(255, 148, 163, 184));
            _textMuted = new(Color.FromArgb(255, 148, 163, 184));   // #94A3B8 (7.11:1 on #111720, 7.49:1 on #0D1117 - WCAG AAA PASS)
            _accentCyan = new(Color.FromArgb(255, 56, 189, 248));
            _accentViolet = new(Color.FromArgb(255, 167, 139, 250));
            _semEmerald = new(Color.FromArgb(255, 52, 211, 153));
            _semAmber = new(Color.FromArgb(255, 251, 191, 36));
            _semCrimson = new(Color.FromArgb(255, 248, 113, 113));
            _semOrange = new(Color.FromArgb(255, 251, 146, 60));
        }
    }

    // =========================================================================
    // INTEGRATED PRECISION SHELL, REDESIGNED TOOLBAR & COMMAND PALETTE OVERLAY
    // =========================================================================
    private void BuildShell()
    {
        var loc = _host.Localization;
        var currentTheme = _host.Settings.Current.Theme;
        _fontUi = loc.CurrentLocale.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? new FontFamily("Microsoft YaHei UI, Segoe UI Variable, Segoe UI")
            : loc.CurrentLocale.StartsWith("ja", StringComparison.OrdinalIgnoreCase)
                ? new FontFamily("Yu Gothic UI, Meiryo UI, Segoe UI Variable, Segoe UI")
                : new FontFamily("Segoe UI Variable, Segoe UI");

        _rootGrid = new Grid
        {
            Background = _bgCanvas,
            FlowDirection = loc.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            RequestedTheme = currentTheme == AppThemeMode.Light ? ElementTheme.Light : ElementTheme.Dark
        };
        AutomationProperties.SetName(_rootGrid, $"{loc.GetString("app.name")} — {loc.GetString("app.descriptor")}");

        // Row 0: Integrated Title Bar + Precision Command Header (48 DIP)
        // Row 1: Optional Crash/Hang Recovery Banner (Auto)
        // Row 2: Main Body (Nav Rail + Workspace Host)
        // Row 3: Calibrated Status Strip (30 DIP)
        _rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        _rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });

        // 1. INTEGRATED CUSTOM TITLE BAR + REDESIGNED PRECISION COMMAND HEADER (F-004, F-010)
        _topBar = new Border
        {
            Background = _bgZone,
            BorderBrush = _seamBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = WindowChromeManager.ComputeLogicalTitleBarPadding(this, loc.IsRightToLeft)
        };

        var topGrid = new Grid { ColumnSpacing = 10 };
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 180 }); // Guaranteed >=180 DIP draggable brand + breadcrumb (F-010)
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Precision controls cluster

        // Draggable Title Region (Brand Mark + Breadcrumb + empty drag canvas)
        _titleDragArea = new Grid
        {
            Background = new SolidColorBrush(Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Stretch
        };
        var brandRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center
        };
        brandRow.Children.Add(FluentIconCatalog.Create(FluentGlyph.BrandMark, 20, _accentCyan));
        brandRow.Children.Add(new TextBlock
        {
            Text = loc.GetString("app.name"),
            FontFamily = _fontUi,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            CharacterSpacing = 140,
            FlowDirection = FlowDirection.LeftToRight,
            Foreground = _accentCyan,
            VerticalAlignment = VerticalAlignment.Center
        });
        brandRow.Children.Add(new Border
        {
            Width = 1,
            Height = 16,
            Background = _seamBrush,
            VerticalAlignment = VerticalAlignment.Center
        });
        _breadcrumbText = new TextBlock
        {
            Text = DestinationRegistry.GetLocalizedTitle(loc, _activeRoute),
            FontFamily = _fontUi,
            FontSize = 12.5,
            FontWeight = FontWeights.Medium,
            Foreground = _textSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        brandRow.Children.Add(_breadcrumbText);
        _titleDragArea.Children.Add(brandRow);
        Grid.SetColumn(_titleDragArea, 0);
        topGrid.Children.Add(_titleDragArea);

        // Redesigned Precision Command Header Controls (Cleanly Grouped, Responsive at Compact 1024 DIP)
        bool isCompact = _currentBreakpoint == 0;
        var controlsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = isCompact ? 4 : 6,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Group A: Unified Search & Command Palette Trigger Pill (Ctrl+K / Ctrl+F)
        var searchPillContent = new Grid { ColumnSpacing = 8 };
        searchPillContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchPillContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchPillContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        searchPillContent.Children.Add(FluentIconCatalog.Create(FluentGlyph.Search, 14, _textSecondary));

        var searchHintText = new TextBlock
        {
            Text = loc.GetString("toolbar.searchPlaceholder"),
            FontFamily = _fontUi,
            FontSize = 12,
            Foreground = _textSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(searchHintText, 1);
        searchPillContent.Children.Add(searchHintText);

        var keycap = new Border
        {
            Background = _bgCanvas,
            BorderBrush = _seamBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            Child = new TextBlock
            {
                Text = loc.GetString("toolbar.commandPaletteHint"),
                FontFamily = _fontMono,
                FontSize = 10.5,
                Foreground = _textMuted,
                FlowDirection = FlowDirection.LeftToRight
            }
        };
        Grid.SetColumn(keycap, 2);
        searchPillContent.Children.Add(keycap);

        _paletteTriggerBtn = new Button
        {
            Width = isCompact ? 176 : (_currentBreakpoint == 2 ? 300 : 264),
            Height = 32,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = _bgSurface1,
            BorderBrush = _seamBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 0, 8, 0),
            Content = searchPillContent
        };
        AutomationProperties.SetName(_paletteTriggerBtn, loc.GetString("palette.title"));
        ToolTipService.SetToolTip(_paletteTriggerBtn, $"{loc.GetString("shortcuts.commandPalette")} (Ctrl+K)");
        _paletteTriggerBtn.Click += (_, _) =>
        {
            if (_commandPaletteOverlay?.Visibility == Visibility.Visible)
                CloseCommandPalette();
            else
                OpenCommandPalette("");
        };
        AddShortcut(_paletteTriggerBtn, VirtualKey.K, VirtualKeyModifiers.Control);
        controlsRow.Children.Add(_paletteTriggerBtn);

        // Distinct Ctrl+F accelerator: focuses local filter box on filterable views, or opens Command Palette (F-045)
        var ctrlFAccel = new KeyboardAccelerator { Key = VirtualKey.F, Modifiers = VirtualKeyModifiers.Control };
        ctrlFAccel.Invoked += (_, e) =>
        {
            e.Handled = true;
            if (_commandPaletteOverlay?.Visibility == Visibility.Visible) return;
            if (_activeLocalSearchBox is not null && _activeLocalSearchBox.IsLoaded)
            {
                _activeLocalSearchBox.Focus(FocusState.Programmatic);
                _activeLocalSearchBox.SelectAll();
            }
            else
            {
                OpenCommandPalette("");
            }
        };
        _rootGrid.KeyboardAccelerators.Add(ctrlFAccel);

        controlsRow.Children.Add(MakeToolbarDivider());

        // Group B: Monitoring Action Cluster (Pause/Resume Status Pill + Primary Snapshot)
        var liveDot = new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = _host.Monitor.IsPaused ? _semAmber : _semEmerald,
            VerticalAlignment = VerticalAlignment.Center
        };
        string pauseLabel = _host.Monitor.IsPaused ? loc.GetString("liveMonitor.resume") : loc.GetString("liveMonitor.pause");
        var pauseBtn = MakeIconToolbarButton(
            _host.Monitor.IsPaused ? FluentGlyph.Play : FluentGlyph.Pause,
            isCompact ? "" : pauseLabel,
            primary: false,
            leadingAccentDot: liveDot,
            accessibleName: pauseLabel,
            onClick: () =>
            {
                if (_commandPaletteOverlay?.Visibility == Visibility.Visible) return;
                _host.Monitor.IsPaused = !_host.Monitor.IsPaused;
                _host.DiagnosticsLogger.Breadcrumb("Monitor", _host.Monitor.IsPaused ? "Paused telemetry" : "Resumed telemetry");
                BuildShell();
                RenderActiveWorkspace();
            });
        ToolTipService.SetToolTip(pauseBtn, $"{loc.GetString("shortcuts.togglePause")} (Ctrl+P)");
        AddShortcut(pauseBtn, VirtualKey.P, VirtualKeyModifiers.Control);
        controlsRow.Children.Add(pauseBtn);

        string snapLabel = loc.GetString("toolbar.createSnapshot");
        var snapBtn = MakeIconToolbarButton(
            FluentGlyph.CameraAdd,
            snapLabel,
            primary: true,
            leadingAccentDot: null,
            accessibleName: snapLabel,
            onClick: () =>
            {
                if (_commandPaletteOverlay?.Visibility == Visibility.Visible) return;
                CaptureSnapshotFromToolbar();
            });
        ToolTipService.SetToolTip(snapBtn, $"{loc.GetString("snapshots.create")} (Ctrl+S)");
        AddShortcut(snapBtn, VirtualKey.S, VirtualKeyModifiers.Control);
        controlsRow.Children.Add(snapBtn);

        controlsRow.Children.Add(MakeToolbarDivider());

        // Group C: Preferences & Diagnostics Cluster (Compact icon-only mode preserves >=220 DIP drag area at 1024 DIP)
        string themeLabel = GetLocalizedThemeName(currentTheme);
        var themeFlyoutBtn = MakeToolbarFlyoutButton(
            GetThemeGlyph(currentTheme),
            isCompact ? "" : themeLabel,
            themeLabel,
            BuildThemeFlyout());
        ToolTipService.SetToolTip(themeFlyoutBtn, $"{loc.GetString("settings.appearance")}: {themeLabel}");
        controlsRow.Children.Add(themeFlyoutBtn);

        var activeLangMeta = loc.AvailableLanguages.FirstOrDefault(l => l.Locale.Equals(loc.CurrentLocale, StringComparison.OrdinalIgnoreCase));
        string langLabel = activeLangMeta?.NativeName ?? loc.CurrentLocale;
        var langFlyoutBtn = MakeToolbarFlyoutButton(
            FluentGlyph.Globe,
            isCompact ? "" : langLabel,
            langLabel,
            BuildLanguageFlyout());
        ToolTipService.SetToolTip(langFlyoutBtn, $"{loc.GetString("settings.language")}: {langLabel}");
        controlsRow.Children.Add(langFlyoutBtn);

        int pendingReports = _host.CrashReports.GetUnacknowledgedReports().Count;
        string diagLabel = loc.GetString("toolbar.diagnostics");
        var diagBtn = MakeIconToolbarButton(
            FluentGlyph.Diagnostics,
            isCompact ? "" : diagLabel,
            primary: false,
            leadingAccentDot: pendingReports > 0 ? new Ellipse { Width = 7, Height = 7, Fill = _semAmber, VerticalAlignment = VerticalAlignment.Center } : null,
            accessibleName: loc.GetString("diagnostics.title"),
            onClick: () => Navigate("settings-advanced"));
        ToolTipService.SetToolTip(diagBtn, loc.GetString("diagnostics.title"));
        controlsRow.Children.Add(diagBtn);

        Grid.SetColumn(controlsRow, 1);
        topGrid.Children.Add(controlsRow);
        _topBar.Child = topGrid;
        Grid.SetRow(_topBar, 0);
        _rootGrid.Children.Add(_topBar);

        // 2. OPTIONAL CRASH/HANG RECOVERY BANNER (Row 1)
        _recoveryBannerHost = new Border { Visibility = Visibility.Collapsed };
        Grid.SetRow(_recoveryBannerHost, 1);
        _rootGrid.Children.Add(_recoveryBannerHost);
        UpdateRecoveryBanner();

        // 3. BODY (Navigation Rail + Main Workspace) (Row 2)
        var bodyGrid = new Grid();
        _navColumnDef = new ColumnDefinition { Width = new GridLength(GetNavRailWidth(_currentBreakpoint)) };
        bodyGrid.ColumnDefinitions.Add(_navColumnDef);
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var navBorder = new Border
        {
            Background = _bgZone,
            BorderBrush = _seamBrush,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(8, 10, 8, 10)
        };
        AutomationProperties.SetName(navBorder, loc.GetString("a11y.primaryNavigation"));
        _navStack = new StackPanel { Spacing = 3 };
        PopulateNavRail();
        navBorder.Child = new ScrollViewer { Content = _navStack };
        Grid.SetColumn(navBorder, 0);
        bodyGrid.Children.Add(navBorder);

        _workspaceHost = new Grid { Background = _bgCanvas };
        AutomationProperties.SetName(_workspaceHost, loc.GetString("a11y.inspectorPanel"));
        Grid.SetColumn(_workspaceHost, 1);
        bodyGrid.Children.Add(_workspaceHost);

        Grid.SetRow(bodyGrid, 2);
        _rootGrid.Children.Add(bodyGrid);

        // 4. BOTTOM CALIBRATED STATUS STRIP (Row 3) — Full RTL Bidi-safe layout (F-008)
        var statusBar = new Border
        {
            Background = _bgZone,
            BorderBrush = _seamBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(14, 0, 14, 0)
        };
        var statusGrid = new Grid { ColumnSpacing = 12 };
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _statusLeftText = new TextBlock
        {
            FontFamily = _fontUi,
            FontSize = 11.5,
            Foreground = _textSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _statusRightText = new TextBlock
        {
            FontFamily = _fontUi,
            FontSize = 11.5,
            Foreground = _accentCyan,
            VerticalAlignment = VerticalAlignment.Center
            // Inherits _rootGrid.FlowDirection; individual numeric/unit tokens are LRI-isolated via IsolateTechnicalToken! (F-008)
        };
        statusGrid.Children.Add(_statusLeftText);
        Grid.SetColumn(_statusRightText, 1);
        statusGrid.Children.Add(_statusRightText);
        statusBar.Child = statusGrid;

        Grid.SetRow(statusBar, 3);
        _rootGrid.Children.Add(statusBar);

        // 5. REAL KEYBOARD-DRIVEN LOCALIZED COMMAND PALETTE MODAL OVERLAY
        _commandPaletteOverlay = BuildCommandPaletteOverlay();
        Grid.SetRowSpan(_commandPaletteOverlay, 4);
        _rootGrid.Children.Add(_commandPaletteOverlay);

        UpdateStatusBarOnly();

        // Assign Content BEFORE calling SetTitleBar (WinUI 3 ordering requirement - F-024)
        _rootGrid.Loaded += (_, _) => HookDynamicDpiTracking();
        Content = _rootGrid;

        if (WindowChromeManager.IsCustomTitleBarSupported)
        {
            try { SetTitleBar(_titleDragArea); } catch { }
        }
    }

    private MenuFlyout BuildThemeFlyout()
    {
        var flyout = new MenuFlyout();
        foreach (var mode in new[] { AppThemeMode.Dark, AppThemeMode.Light, AppThemeMode.HighContrast })
        {
            var m = mode;
            var item = new MenuFlyoutItem { Text = GetLocalizedThemeName(m) };
            item.Click += (_, _) =>
            {
                if (_host.Settings.Current.Theme != m)
                    ApplyThemeAndRebuild(m);
            };
            flyout.Items.Add(item);
        }
        return flyout;
    }

    private MenuFlyout BuildLanguageFlyout()
    {
        var flyout = new MenuFlyout();
        foreach (var lang in _host.Localization.AvailableLanguages)
        {
            string code = lang.Locale;
            var item = new MenuFlyoutItem { Text = $"{lang.NativeName} ({lang.CompletenessPercent:F0}%)" };
            item.Click += (_, _) => SwitchLanguageAndRebuild(code);
            flyout.Items.Add(item);
        }
        return flyout;
    }

    private void SwitchLanguageAndRebuild(string localeCode)
    {
        if (localeCode.Equals(_host.Localization.CurrentLocale, StringComparison.OrdinalIgnoreCase))
            return;

        if (_host.SetLanguage(localeCode))
        {
            var loc = _host.Localization;
            Title = $"{loc.GetString("app.name")} — {loc.GetString("app.tagline")}";
            _lastStatusMessage = loc.GetString("state.ready");
            BuildShell();
            RenderActiveWorkspace();
        }
    }

    private void CaptureSnapshotFromToolbar()
    {
        var loc = _host.Localization;
        if (_host.Monitor.LatestSystem != null)
        {
            var rec = _host.Snapshots.CreateTelemetrySnapshot(
                $"Live Capture {DateTime.Now:HH:mm:ss}",
                _host.Monitor.LatestSystem,
                _host.Monitor.LatestProcesses);
            _lastStatusMessage = $"{loc.GetString("toolbar.snapshotCreated")}: {rec.SnapshotId} (SHA-256: {rec.Sha256Hash[..12]}…)";
            _host.DiagnosticsLogger.Breadcrumb("Snapshot", $"Created snapshot {rec.SnapshotId}");
            Navigate("snapshot-comparison");
        }
    }

    private Border MakeToolbarDivider() => new()
    {
        Width = 1,
        Height = 20,
        Background = _seamBrush,
        Margin = new Thickness(2, 0, 2, 0),
        VerticalAlignment = VerticalAlignment.Center
    };

    private void ApplyPrimaryButtonThemeResources(Button btn, SolidColorBrush fg)
    {
        bool isLight = _host.Settings.Current.Theme == AppThemeMode.Light;
        var hoverBg = isLight
            ? new SolidColorBrush(Color.FromArgb(255, 7, 89, 133))     // Distinct darker sky-blue (#075985) on hover in Light theme (REL-NEW-014)
            : new SolidColorBrush(Color.FromArgb(255, 125, 211, 252)); // Brighter cyan on hover in Dark/HC
        var pressBg = isLight
            ? new SolidColorBrush(Color.FromArgb(255, 12, 74, 110))    // Deep sky-blue (#0C4A6E) on press
            : new SolidColorBrush(Color.FromArgb(255, 14, 165, 233));

        btn.Resources["ButtonBackgroundPointerOver"] = hoverBg;
        btn.Resources["ButtonForegroundPointerOver"] = fg;
        btn.Resources["ButtonBorderBrushPointerOver"] = hoverBg;
        btn.Resources["ButtonBackgroundPressed"] = pressBg;
        btn.Resources["ButtonForegroundPressed"] = fg;
        btn.Resources["ButtonBorderBrushPressed"] = pressBg;
    }

    private Button MakeIconToolbarButton(
        FluentGlyph glyph,
        string label,
        bool primary,
        UIElement? leadingAccentDot,
        Action onClick,
        string? accessibleName = null)
    {
        bool isLight = _host.Settings.Current.Theme == AppThemeMode.Light;
        var fg = primary
            ? (isLight ? new SolidColorBrush(Colors.White) : new SolidColorBrush(Color.FromArgb(255, 6, 16, 26)))
            : _textPrimary;

        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (leadingAccentDot is not null)
            sp.Children.Add(leadingAccentDot);
        sp.Children.Add(FluentIconCatalog.Create(glyph, 14, fg));
        if (!string.IsNullOrEmpty(label))
        {
            sp.Children.Add(new TextBlock
            {
                Text = label,
                FontFamily = _fontUi,
                FontSize = 12,
                FontWeight = primary ? FontWeights.SemiBold : FontWeights.Medium,
                Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        var btn = new Button
        {
            Height = 32,
            Background = primary ? _accentCyan : _bgSurface1,
            Foreground = fg,
            BorderBrush = primary ? _accentCyan : _seamBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(string.IsNullOrEmpty(label) ? 8 : 10, 0, string.IsNullOrEmpty(label) ? 8 : 10, 0),
            Content = sp
        };
        if (primary)
            ApplyPrimaryButtonThemeResources(btn, fg);

        AutomationProperties.SetName(btn, !string.IsNullOrEmpty(accessibleName) ? accessibleName : (string.IsNullOrEmpty(label) ? glyph.ToString() : label));
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private Button MakeToolbarFlyoutButton(FluentGlyph glyph, string label, string accessibleName, MenuFlyout flyout)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(FluentIconCatalog.Create(glyph, 14, _accentCyan));
        if (!string.IsNullOrEmpty(label))
        {
            sp.Children.Add(new TextBlock
            {
                Text = label,
                FontFamily = _fontUi,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Foreground = _textPrimary,
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        sp.Children.Add(FluentIconCatalog.Create(FluentGlyph.ChevronDown, 11, _textMuted));

        var btn = new Button
        {
            Height = 32,
            Background = _bgSurface1,
            BorderBrush = _seamBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(string.IsNullOrEmpty(label) ? 8 : 10, 0, 8, 0),
            Content = sp,
            Flyout = flyout
        };
        AutomationProperties.SetName(btn, accessibleName);
        return btn;
    }

    // =========================================================================
    // CRASH & HANG RECOVERY BANNER (Next-Launch & Live Error Surfacing)
    // =========================================================================
    private void UpdateRecoveryBanner()
    {
        if (_recoveryBannerHost is null) return;
        var unack = _host.CrashReports.GetUnacknowledgedReports();
        if (_recoveryBannerDismissed || unack.Count == 0)
        {
            _recoveryBannerHost.Visibility = Visibility.Collapsed;
            _recoveryBannerHost.Child = null;
            return;
        }

        var loc = _host.Localization;
        var latest = unack[0];
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(FluentIconCatalog.Create(FluentGlyph.Warning, 18, _semAmber));

        var textStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(new TextBlock
        {
            Text = loc.FormatPlural("diagnostics.crashBannerTitle", unack.Count),
            FontFamily = _fontUi,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textPrimary
        });
        textStack.Children.Add(new TextBlock
        {
            Text = $"{latest.Summary} — {loc.GetString("diagnostics.crashBannerBody")}",
            FontFamily = _fontUi,
            FontSize = 11.5,
            Foreground = _textSecondary,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(MakeButton(loc.GetString("diagnostics.viewDetails"), true, () => Navigate("settings-advanced")));
        actions.Children.Add(MakeButton(loc.GetString("diagnostics.copyDetails"), false, () => CopyDiagnosticReportToClipboard(latest)));
        actions.Children.Add(MakeButton(loc.GetString("diagnostics.saveReport"), false, () => _ = SaveDiagnosticBundleLocallyAsync(includeDump: false, latest)));
        actions.Children.Add(MakeButton(loc.GetString("diagnostics.dismissBanner"), false, () =>
        {
            _recoveryBannerDismissed = true;
            _host.CrashReports.AcknowledgeAllReports();
            UpdateRecoveryBanner();
        }));
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        _recoveryBannerHost.Background = _bgSurface1;
        _recoveryBannerHost.BorderBrush = _semAmber;
        _recoveryBannerHost.BorderThickness = new Thickness(0, 0, 0, 1);
        _recoveryBannerHost.Padding = new Thickness(16, 8, 16, 8);
        _recoveryBannerHost.Child = grid;
        _recoveryBannerHost.Visibility = Visibility.Visible;
    }

    // =========================================================================
    // REAL ARABIC & ENGLISH COMMAND PALETTE MODAL OVERLAY (Ctrl+K) (F-012, F-041)
    // =========================================================================
    private Grid BuildCommandPaletteOverlay()
    {
        var loc = _host.Localization;

        var overlay = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(175, 4, 7, 12)),
            Visibility = Visibility.Collapsed,
            XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled
        };
        AutomationProperties.SetName(overlay, loc.GetString("palette.title"));

        overlay.PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, overlay))
                CloseCommandPalette();
        };

        // Overlay-level PreviewKeyDown so Escape & arrow navigation work from ANY focused child inside the modal (F-012)
        overlay.PreviewKeyDown += (_, e) =>
        {
            if (overlay.Visibility != Visibility.Visible)
                return;

            if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                CloseCommandPalette();
            }
            else if (e.Key == VirtualKey.Down)
            {
                e.Handled = true;
                MovePaletteSelection(+1);
            }
            else if (e.Key == VirtualKey.Up)
            {
                e.Handled = true;
                MovePaletteSelection(-1);
            }
            else if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                ExecuteSelectedPaletteItem();
            }
            else if (e.Key == VirtualKey.Tab)
            {
                // Keep keyboard Tab trapped inside Command Palette search box while open (F-012)
                e.Handled = true;
                _paletteSearchBox?.Focus(FocusState.Programmatic);
            }
        };

        var modalCard = new Border
        {
            Width = Math.Min(680, Math.Max(480, _currentWidthDip - 80)),
            MaxHeight = 560,
            Background = _bgZone,
            BorderBrush = _accentCyan,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(24, 68, 24, 24),
            Padding = new Thickness(0)
        };

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header + Search box
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Results list
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Footer hints

        // Header + Search Input
        var headerBorder = new Border
        {
            BorderBrush = _seamBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16, 14, 16, 12)
        };
        var headerStack = new StackPanel { Spacing = 8 };

        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        titleRow.Children.Add(FluentIconCatalog.Create(FluentGlyph.CommandPalette, 18, _accentCyan));

        var titleTxt = new TextBlock
        {
            Text = loc.GetString("palette.title"),
            FontFamily = _fontUi,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textPrimary,
            Margin = new Thickness(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleTxt, 1);
        titleRow.Children.Add(titleTxt);

        var closeBtn = new Button
        {
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6),
            Content = FluentIconCatalog.Create(FluentGlyph.Dismiss, 14, _textSecondary)
        };
        AutomationProperties.SetName(closeBtn, loc.GetString("a11y.closeDialog"));
        closeBtn.Click += (_, _) => CloseCommandPalette();
        Grid.SetColumn(closeBtn, 2);
        titleRow.Children.Add(closeBtn);
        headerStack.Children.Add(titleRow);

        _paletteSearchBox = new TextBox
        {
            PlaceholderText = loc.GetString("palette.placeholder"),
            PlaceholderForeground = _textSecondary,
            RequestedTheme = _host.Settings.Current.Theme == AppThemeMode.Light ? ElementTheme.Light : ElementTheme.Dark,
            FontFamily = _fontUi,
            FontSize = 13.5,
            Background = _bgSurface1,
            Foreground = _textPrimary,
            BorderBrush = _seamBrush,
            Padding = new Thickness(12, 8, 12, 8)
        };
        _paletteSearchBox.Resources["TextControlBackgroundFocused"] = _bgSurface1;
        _paletteSearchBox.Resources["TextControlBackgroundPointerOver"] = _bgSurface2;
        _paletteSearchBox.Resources["TextControlForegroundFocused"] = _textPrimary;
        _paletteSearchBox.Resources["TextControlPlaceholderForeground"] = _textSecondary;
        _paletteSearchBox.Resources["TextControlPlaceholderForegroundFocused"] = _textSecondary;
        _paletteSearchBox.Resources["TextControlPlaceholderForegroundPointerOver"] = _textSecondary;
        _paletteSearchBox.Resources["TextControlBorderBrushFocused"] = _accentCyan;
        AutomationProperties.SetName(_paletteSearchBox, loc.GetString("palette.placeholder"));
        _paletteSearchBox.TextChanged += (_, _) =>
        {
            if (!_suppressPaletteTextChanged)
                RefreshPaletteMatches(_paletteSearchBox.Text);
        };
        headerStack.Children.Add(_paletteSearchBox);

        headerBorder.Child = headerStack;
        Grid.SetRow(headerBorder, 0);
        layout.Children.Add(headerBorder);

        // Results ScrollViewer (tracked so keyboard Up/Down calls StartBringIntoView - F-012)
        _paletteResultsPanel = new StackPanel { Spacing = 2, Padding = new Thickness(10, 8, 10, 8) };
        _paletteResultsScroll = new ScrollViewer { Content = _paletteResultsPanel, MaxHeight = 390 };
        Grid.SetRow(_paletteResultsScroll, 1);
        layout.Children.Add(_paletteResultsScroll);

        // Footer Bar
        var footer = new Border
        {
            Background = _bgSurface1,
            BorderBrush = _seamBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16, 8, 16, 8),
            CornerRadius = new CornerRadius(0, 0, 10, 10)
        };
        var footerGrid = new Grid();
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        footerGrid.Children.Add(new TextBlock
        {
            Text = loc.GetString("palette.footerHint"),
            FontFamily = _fontUi,
            FontSize = 11.5,
            Foreground = _textSecondary
        });
        _paletteCountText = new TextBlock
        {
            FontFamily = _fontUi,
            FontSize = 11.5,
            Foreground = _accentCyan
        };
        AutomationProperties.SetLiveSetting(_paletteCountText, AutomationLiveSetting.Polite);
        Grid.SetColumn(_paletteCountText, 1);
        footerGrid.Children.Add(_paletteCountText);
        footer.Child = footerGrid;
        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);

        modalCard.Child = layout;
        overlay.Children.Add(modalCard);
        return overlay;
    }

    private void MovePaletteSelection(int delta)
    {
        if (_currentPaletteMatches.Count == 0) return;
        _paletteSelectedIndex = (_paletteSelectedIndex + delta + _currentPaletteMatches.Count) % _currentPaletteMatches.Count;
        RenderPaletteRows(scrollSelectedIntoView: true);
    }

    private List<PaletteEntry> BuildAllPaletteEntries()
    {
        var loc = _host.Localization;
        string catQuick = loc.GetString("palette.cat.quickActions");
        var entries = new List<PaletteEntry>(112)
        {
            new(loc.GetString("palette.action.createSnapshot"), loc.GetString("palette.action.createSnapshotDesc"), catQuick, "Ctrl+S", FluentGlyph.CameraAdd,
                LocalizationService.NormalizeForSearch($"snapshot capture {loc.GetString("palette.action.createSnapshot")} {loc.GetString("palette.action.createSnapshotDesc")}"),
                () => { CloseCommandPalette(); CaptureSnapshotFromToolbar(); }),

            new(loc.GetString("palette.action.togglePause"), loc.GetString("palette.action.togglePauseDesc"), catQuick, "Ctrl+P",
                _host.Monitor.IsPaused ? FluentGlyph.Play : FluentGlyph.Pause,
                LocalizationService.NormalizeForSearch($"pause resume live monitor {loc.GetString("palette.action.togglePause")} {loc.GetString("palette.action.togglePauseDesc")}"),
                () => { CloseCommandPalette(); _host.Monitor.IsPaused = !_host.Monitor.IsPaused; BuildShell(); RenderActiveWorkspace(); }),

            new(loc.GetString("palette.action.openDiagnostics"), loc.GetString("palette.action.openDiagnosticsDesc"), catQuick, "#93", FluentGlyph.Diagnostics,
                LocalizationService.NormalizeForSearch($"diagnostics crash log watchdog hang error {loc.GetString("palette.action.openDiagnostics")} {loc.GetString("palette.action.openDiagnosticsDesc")}"),
                () => { CloseCommandPalette(); Navigate("settings-advanced"); }),

            new(loc.GetString("palette.action.exportDiagnosticBundle"), loc.GetString("palette.action.exportDiagnosticBundleDesc"), catQuick, "ZIP", FluentGlyph.Save,
                LocalizationService.NormalizeForSearch($"export diagnostic bundle zip report {loc.GetString("palette.action.exportDiagnosticBundle")}"),
                () => { CloseCommandPalette(); _ = SaveDiagnosticBundleLocallyAsync(includeDump: false); }),

            new(loc.GetString("palette.action.switchArabic"), loc.GetString("palette.action.switchArabicDesc"), catQuick, "AR", FluentGlyph.Globe,
                LocalizationService.NormalizeForSearch($"arabic rtl ar-sa العربية {loc.GetString("palette.action.switchArabic")}"),
                () => { CloseCommandPalette(); SwitchLanguageAndRebuild("ar-SA"); }),

            new(loc.GetString("palette.action.switchEnglish"), loc.GetString("palette.action.switchEnglishDesc"), catQuick, "EN", FluentGlyph.Globe,
                LocalizationService.NormalizeForSearch($"english ltr en-us الإنجليزية {loc.GetString("palette.action.switchEnglish")}"),
                () => { CloseCommandPalette(); SwitchLanguageAndRebuild("en-US"); }),

            new(loc.GetString("palette.action.themeDark"), loc.GetString("toolbar.themeDark"), catQuick, "", FluentGlyph.ThemeDark,
                LocalizationService.NormalizeForSearch($"theme dark obsidian داكن {loc.GetString("palette.action.themeDark")}"),
                () => { CloseCommandPalette(); ApplyThemeAndRebuild(AppThemeMode.Dark); }),

            new(loc.GetString("palette.action.themeLight"), loc.GetString("toolbar.themeLight"), catQuick, "", FluentGlyph.ThemeLight,
                LocalizationService.NormalizeForSearch($"theme light daylight فاتح {loc.GetString("palette.action.themeLight")}"),
                () => { CloseCommandPalette(); ApplyThemeAndRebuild(AppThemeMode.Light); }),

            new(loc.GetString("palette.action.themeHighContrast"), loc.GetString("toolbar.themeHighContrast"), catQuick, "", FluentGlyph.ThemeContrast,
                LocalizationService.NormalizeForSearch($"theme high contrast تباين {loc.GetString("palette.action.themeHighContrast")}"),
                () => { CloseCommandPalette(); ApplyThemeAndRebuild(AppThemeMode.HighContrast); })
        };

        foreach (var dest in DestinationRegistry.All98)
        {
            string title = DestinationRegistry.GetLocalizedTitle(loc, dest.RouteKey);
            string desc = loc.GetString(dest.DescLocKey);
            string cat = loc.GetString(dest.CategoryLocKey);
            string key = dest.RouteKey;
            string badge = $"#{dest.Id:D2}";
            entries.Add(new PaletteEntry(
                title,
                desc,
                cat,
                badge,
                dest.Icon,
                LocalizationService.NormalizeForSearch($"{badge} {dest.Id} {key} {title} {desc} {cat}"),
                () => { CloseCommandPalette(); Navigate(key); }));
        }

        return entries;
    }

    private void OpenCommandPalette(string initialQuery)
    {
        if (_commandPaletteOverlay is null || _paletteSearchBox is null) return;
        try
        {
            if (Content?.XamlRoot is { } xr)
                _previousFocusBeforePalette = FocusManager.GetFocusedElement(xr);
        }
        catch { }

        _commandPaletteOverlay.Visibility = Visibility.Visible;
        _suppressPaletteTextChanged = true;
        _paletteSearchBox.Text = initialQuery ?? "";
        _suppressPaletteTextChanged = false;
        RefreshPaletteMatches(initialQuery ?? "");
        _paletteSearchBox.Focus(FocusState.Programmatic);
    }

    private void CloseCommandPalette()
    {
        if (_commandPaletteOverlay is not null)
            _commandPaletteOverlay.Visibility = Visibility.Collapsed;

        // Restore previous focus cleanly when modal closes (F-012)
        if (_previousFocusBeforePalette is Control prevCtrl && prevCtrl.IsLoaded)
        {
            try { prevCtrl.Focus(FocusState.Programmatic); } catch { }
        }
        else if (_paletteTriggerBtn is not null && _paletteTriggerBtn.IsLoaded)
        {
            try { _paletteTriggerBtn.Focus(FocusState.Programmatic); } catch { }
        }
        _previousFocusBeforePalette = null;
    }

    private void RefreshPaletteMatches(string query)
    {
        var all = BuildAllPaletteEntries();
        string normQ = LocalizationService.NormalizeForSearch(query);

        if (string.IsNullOrEmpty(normQ))
        {
            _paletteTotalMatchCount = all.Count;
            _currentPaletteMatches = all; // All 107 entries scrollable and searchable (F-041)
        }
        else
        {
            var terms = normQ.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var matched = all
                .Where(e => terms.All(t => e.SearchHaystackNormalized.Contains(t, StringComparison.Ordinal)))
                .ToList();
            _paletteTotalMatchCount = matched.Count;
            _currentPaletteMatches = matched;
        }

        _paletteSelectedIndex = 0;
        RenderPaletteRows(scrollSelectedIntoView: false);
        _paletteResultsScroll?.ChangeView(null, 0, null, disableAnimation: true);
    }

    private void RenderPaletteRows(bool scrollSelectedIntoView = false)
    {
        if (_paletteResultsPanel is null || _paletteCountText is null) return;
        var loc = _host.Localization;
        _paletteResultsPanel.Children.Clear();

        string countStr = loc.FormatPlural("palette.resultCount", _paletteTotalMatchCount);
        _paletteCountText.Text = _currentPaletteMatches.Count < _paletteTotalMatchCount
            ? $"{countStr} ({loc.Format("palette.showingOfTotal", new Dictionary<string, object?> { ["shown"] = _currentPaletteMatches.Count, ["total"] = _paletteTotalMatchCount })})"
            : countStr;

        if (_currentPaletteMatches.Count == 0)
        {
            var emptyStack = new StackPanel { Spacing = 6, Padding = new Thickness(20, 28, 20, 28), HorizontalAlignment = HorizontalAlignment.Center };
            emptyStack.Children.Add(FluentIconCatalog.Create(FluentGlyph.Search, 28, _textMuted));
            emptyStack.Children.Add(new TextBlock
            {
                Text = loc.GetString("palette.noResults"),
                FontFamily = _fontUi,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = _textPrimary,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            emptyStack.Children.Add(new TextBlock
            {
                Text = loc.GetString("palette.noResultsHint"),
                FontFamily = _fontUi,
                FontSize = 12,
                Foreground = _textSecondary,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            _paletteResultsPanel.Children.Add(emptyStack);
            return;
        }

        Button? selectedButton = null;
        string? lastCat = null;
        for (int i = 0; i < _currentPaletteMatches.Count; i++)
        {
            var entry = _currentPaletteMatches[i];
            if (!string.Equals(lastCat, entry.CategoryLabel, StringComparison.Ordinal))
            {
                lastCat = entry.CategoryLabel;
                _paletteResultsPanel.Children.Add(new TextBlock
                {
                    Text = entry.CategoryLabel,
                    FontFamily = _fontUi,
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = _textMuted,
                    Margin = new Thickness(10, i == 0 ? 2 : 10, 10, 4)
                });
            }

            bool isSelected = i == _paletteSelectedIndex;
            var rowGrid = new Grid { ColumnSpacing = 10 };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            rowGrid.Children.Add(FluentIconCatalog.Create(entry.Icon, 16, isSelected ? _accentCyan : _textSecondary));

            var textStack = new StackPanel { Spacing = 1 };
            textStack.Children.Add(new TextBlock
            {
                Text = entry.Title,
                FontFamily = _fontUi,
                FontSize = 13,
                FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = isSelected ? _accentCyan : _textPrimary
            });
            if (!string.IsNullOrWhiteSpace(entry.Description))
            {
                textStack.Children.Add(new TextBlock
                {
                    Text = entry.Description,
                    FontFamily = _fontUi,
                    FontSize = 11.5,
                    Foreground = _textSecondary,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
            }
            Grid.SetColumn(textStack, 1);
            rowGrid.Children.Add(textStack);

            if (!string.IsNullOrEmpty(entry.ShortcutBadge))
            {
                var chip = new Border
                {
                    Background = _bgCanvas,
                    BorderBrush = _seamBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = entry.ShortcutBadge,
                        FontFamily = _fontMono,
                        FontSize = 10.5,
                        Foreground = _textMuted,
                        FlowDirection = FlowDirection.LeftToRight
                    }
                };
                Grid.SetColumn(chip, 2);
                rowGrid.Children.Add(chip);
            }

            int captureIdx = i;
            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = isSelected ? _bgSurface2 : new SolidColorBrush(Colors.Transparent),
                BorderBrush = isSelected ? _accentCyan : new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(isSelected ? 1 : 0),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 7, 10, 7),
                Content = rowGrid
            };
            AutomationProperties.SetName(btn, $"{entry.Title} {entry.Description}");
            btn.Click += (_, _) =>
            {
                _paletteSelectedIndex = captureIdx;
                ExecuteSelectedPaletteItem();
            };
            if (isSelected)
                selectedButton = btn;
            _paletteResultsPanel.Children.Add(btn);
        }

        if (scrollSelectedIntoView && selectedButton is not null)
        {
            var target = selectedButton;
            DispatcherQueue.TryEnqueue(() =>
            {
                try { target.StartBringIntoView(); } catch { }
            });
        }
    }

    private void ExecuteSelectedPaletteItem()
    {
        if (_paletteSelectedIndex >= 0 && _paletteSelectedIndex < _currentPaletteMatches.Count)
        {
            _currentPaletteMatches[_paletteSelectedIndex].Execute();
        }
    }

    private static void AddShortcut(UIElement element, VirtualKey key, VirtualKeyModifiers modifiers)
    {
        element.KeyboardAccelerators.Add(new KeyboardAccelerator { Key = key, Modifiers = modifiers });
    }

    // =========================================================================
    // NAVIGATION RAIL (100% VENDORED FLUENT VECTOR ICONS — ZERO UNICODE HACKS)
    // =========================================================================
    private void PopulateNavRail()
    {
        _navStack.Children.Clear();
        var loc = _host.Localization;
        var items = new (string Key, FluentGlyph Glyph, string Label)[]
        {
            ("overview", FluentGlyph.Overview, loc.GetString("navigation.overview")),
            ("live-monitor", FluentGlyph.LivePulse, loc.GetString("navigation.liveMonitor")),
            ("process-explorer", FluentGlyph.Processes, loc.GetString("navigation.processes")),
            ("process-deep-dive", FluentGlyph.ProcessDeepDive, loc.GetString("process.deepDive")),
            ("virtual-overview", FluentGlyph.VirtualMemory, loc.GetString("navigation.virtualMemory")),
            ("physical-overview", FluentGlyph.PhysicalMemory, loc.GetString("navigation.physicalMemory")),
            ("alerts-center", FluentGlyph.Alerts, $"{loc.GetString("navigation.alerts")} ({_host.Monitor.ActiveInvestigations.Count})"),
            ("profiler-home", FluentGlyph.Profiler, loc.GetString("navigation.profiler")),
            ("snapshot-comparison", FluentGlyph.Snapshots, loc.GetString("navigation.snapshots")),
            ("forensics-home", FluentGlyph.Forensics, loc.GetString("navigation.forensics")),
            ("report-center", FluentGlyph.Reports, loc.GetString("navigation.reports")),
            ("settings-general", FluentGlyph.Settings, loc.GetString("navigation.settings")),
            ("settings-advanced", FluentGlyph.Diagnostics, loc.GetString("toolbar.diagnostics")),
            ("glossary", FluentGlyph.HelpGlossary, loc.GetString("navigation.help"))
        };

        foreach (var it in items)
        {
            bool active = _activeRoute == it.Key;
            var contentRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                VerticalAlignment = VerticalAlignment.Center
            };
            contentRow.Children.Add(FluentIconCatalog.Create(it.Glyph, 16, active ? _accentCyan : _textSecondary));
            contentRow.Children.Add(new TextBlock
            {
                Text = it.Label,
                FontFamily = _fontUi,
                FontSize = 12.5,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = active ? _accentCyan : _textPrimary,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = active ? _bgSurface2 : new SolidColorBrush(Colors.Transparent),
                BorderBrush = active ? _accentCyan : new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(active ? 2.5 : 0, 0, 0, 0),
                Padding = new Thickness(10, 8, 10, 8),
                CornerRadius = new CornerRadius(5),
                Content = contentRow
            };
            AutomationProperties.SetName(btn, it.Label);
            string routeKey = it.Key;
            btn.Click += (_, _) => Navigate(routeKey);
            _navStack.Children.Add(btn);
        }
    }

    public void Navigate(string routeKey)
    {
        if (routeKey == "command-palette")
        {
            OpenCommandPalette("");
            return;
        }

        _activeRoute = routeKey;
        _host.DiagnosticState.ActiveViewId = routeKey;
        _host.DiagnosticsLogger.Breadcrumb("Navigation", $"Navigated to {routeKey}");

        // If entering deep dive views for a process that hasn't been inspected yet, kick off async inspection!
        if (IsProcessDeepDiveRoute(routeKey))
        {
            var proc = _host.GetSelectedProcess();
            if (proc is not null && (_activeInspection is null || _activeInspection.Pid != proc.Pid))
            {
                TriggerAsyncProcessInspection(proc, alsoLoadHeaps: routeKey == "heap-explorer");
            }
            else if (proc is not null && routeKey == "heap-explorer" && _activeInspection is not null && !_activeInspection.HeapsLoaded && !_activeInspection.HeapsLoading)
            {
                TriggerAsyncHeapWalk(proc.Pid, _activeInspection.SelectionVersion);
            }
        }

        PopulateNavRail();
        RenderActiveWorkspace();
    }

    private static bool IsProcessDeepDiveRoute(string routeKey) =>
        routeKey is "process-deep-dive" or "process-overview" or "process-memory" or "process-memory-map"
            or "heap-explorer" or "stack-explorer" or "process-modules" or "process-threads" or "process-handles" or "memory-strings";

    /// <summary>
    /// Non-blocking process selection: immediately updates selection &amp; renders UI (&lt;5ms),
    /// then loads Regions, Fast Modules, and Threads on a background worker with stale-result rejection.
    /// Never walks NT process heaps synchronously on the UI thread!
    /// </summary>
    private void SelectProcessAndNavigate(uint pid, string targetRoute)
    {
        _host.SelectedPid = pid;
        _host.DiagnosticState.SelectedPid = (int)pid;
        _activeRoute = targetRoute;
        _host.DiagnosticState.ActiveViewId = targetRoute;

        var proc = _host.GetSelectedProcess();
        if (proc is not null)
        {
            TriggerAsyncProcessInspection(proc, alsoLoadHeaps: targetRoute == "heap-explorer");
        }

        PopulateNavRail();
        RenderActiveWorkspace();
    }

    private void TriggerAsyncProcessInspection(ProcessMemoryInfo proc, bool alsoLoadHeaps)
    {
        _lastRequestedInspectionPid = proc.Pid;
        // Single authoritative selection increment (eliminates double BeginSelection race - F-013)
        var (placeholder, task) = _host.ProcessInspector.StartLightweightInspection(proc.Pid, proc.IsProtectedOrRestricted);
        _activeInspection = placeholder;

        _ = task.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _ = t.Exception;
                return;
            }
            if (!t.IsCompletedSuccessfully || t.Result is null || _isClosed)
                return;

            var payload = t.Result;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isClosed || _host.ProcessInspector.IsStale(payload.SelectionVersion))
                    return;

                _activeInspection = payload;
                if (IsProcessDeepDiveRoute(_activeRoute) || _activeRoute is "virtual-overview" or "virtual-map" or "virtual-region-details")
                {
                    RenderActiveWorkspace(preserveScroll: true);
                }

                if (alsoLoadHeaps && !payload.IsRestricted && !payload.HeapsLoaded)
                {
                    TriggerAsyncHeapWalk(payload.Pid, payload.SelectionVersion);
                }
            });
        }, TaskScheduler.Default);
    }

    private void TriggerAsyncHeapWalk(uint pid, long expectedVersion)
    {
        if (_activeInspection is not null && _activeInspection.SelectionVersion == expectedVersion)
        {
            _activeInspection = _activeInspection with { HeapsLoading = true };
            RenderActiveWorkspace(preserveScroll: true);
        }

        _ = _host.ProcessInspector.LoadHeapsOnDemandAsync(pid, expectedVersion).ContinueWith(t =>
        {
            if (t.IsFaulted) _ = t.Exception;
            if (_isClosed) return;

            var updated = t.IsCompletedSuccessfully ? t.Result : null;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isClosed) return;
                if (updated is null)
                {
                    // Never leave UI stuck on HeapsLoading = true if cancelled/errored (F-014, R2-NEW-009)
                    if (_activeInspection is not null && _activeInspection.SelectionVersion == expectedVersion && _activeInspection.HeapsLoading)
                    {
                        _activeInspection = _activeInspection with { HeapsLoading = false };
                        if (IsProcessDeepDiveRoute(_activeRoute))
                            RenderActiveWorkspace(preserveScroll: true);
                    }
                    return;
                }

                if (_host.ProcessInspector.IsStale(updated.SelectionVersion))
                    return;

                _activeInspection = updated;
                if (IsProcessDeepDiveRoute(_activeRoute))
                {
                    RenderActiveWorkspace(preserveScroll: true);
                }
            });
        }, TaskScheduler.Default);
    }

    private void TriggerAsyncProfilerLoad(uint pid, string procName)
    {
        if (_loadingProfilerPid == pid) return;
        _loadingProfilerPid = pid;
        var existingHeaps = _activeInspection?.Pid == pid && _activeInspection.HeapsLoaded ? _activeInspection.Heaps : null;

        _ = _host.ProcessInspector.AnalyzeProfilerAsync(_host.Profiler, pid, procName, existingHeaps).ContinueWith(t =>
        {
            if (t.IsFaulted) _ = t.Exception;
            if (_isClosed) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isClosed) return;
                if (_loadingProfilerPid == pid) _loadingProfilerPid = 0;
                if (!t.IsCompletedSuccessfully || t.Result is null) return;
                if (_host.SelectedPid != pid) return;
                _activeProfilerPayload = t.Result;
                if (_activeRoute.StartsWith("profiler-", StringComparison.Ordinal))
                    RenderActiveWorkspace(preserveScroll: true);
            });
        }, TaskScheduler.Default);
    }

    private void TriggerAsyncHexRead(uint pid, ulong baseAddr)
    {
        if (_isLoadingHex && _activeHexPid == pid && _activeHexAddress == baseAddr) return;
        _isLoadingHex = true;
        _activeHexPid = pid;
        _activeHexAddress = baseAddr;
        _activeHexBytes = null;

        _ = _host.ProcessInspector.ReadMemoryHexAsync(pid, baseAddr, 192).ContinueWith(t =>
        {
            if (t.IsFaulted) _ = t.Exception;
            if (_isClosed) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isClosed || _activeHexPid != pid || _activeHexAddress != baseAddr) return;
                _isLoadingHex = false;
                _activeHexBytes = t.IsCompletedSuccessfully ? t.Result : [];
                if (_activeRoute is "virtual-overview" or "virtual-map" or "virtual-region-details")
                    RenderActiveWorkspace(preserveScroll: true);
            });
        }, TaskScheduler.Default);
    }

    private void TriggerAsyncStringsSearch(uint pid, string query)
    {
        try { _stringsSearchCts?.Cancel(); } catch { }
        try { _stringsSearchCts?.Dispose(); } catch { }
        var cts = new CancellationTokenSource();
        _stringsSearchCts = cts;
        _isSearchingStrings = true;
        _activeStringsPid = pid;
        _activeStringsQuery = query;

        _ = _host.ProcessInspector.SearchStringsAsync(pid, query, 5, 80, cts.Token).ContinueWith(t =>
        {
            if (t.IsFaulted) _ = t.Exception;
            if (_isClosed) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isClosed || cts.IsCancellationRequested) return;
                _isSearchingStrings = false;
                _activeStringsResults = t.IsCompletedSuccessfully ? t.Result : [];
                if (_activeRoute == "memory-strings")
                    RenderActiveWorkspace(preserveScroll: true);
            });
        }, TaskScheduler.Default);
    }

    private void TriggerAsyncForensicsLive(uint pid, string procName)
    {
        if (_loadingForensicsLivePid == pid) return;
        _loadingForensicsLivePid = pid;
        _ = _host.ProcessInspector.AnalyzeForensicsLiveAsync(_host.Forensics, pid, procName).ContinueWith(t =>
        {
            if (t.IsFaulted) _ = t.Exception;
            if (_isClosed) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isClosed) return;
                if (_loadingForensicsLivePid == pid) _loadingForensicsLivePid = 0;
                if (!t.IsCompletedSuccessfully || _host.SelectedPid != pid) return;
                _activeForensicsLive = t.Result;
                if (_activeRoute.StartsWith("forensics-", StringComparison.Ordinal))
                    RenderActiveWorkspace(preserveScroll: true);
            });
        }, TaskScheduler.Default);
    }

    private void TriggerAsyncForensicsDump(string dumpPath)
    {
        if (_loadingForensicsDumpPath == dumpPath) return;
        _loadingForensicsDumpPath = dumpPath;
        _ = _host.ProcessInspector.AnalyzeForensicsDumpAsync(_host.Forensics, dumpPath).ContinueWith(t =>
        {
            Exception? fault = t.IsFaulted ? t.Exception?.GetBaseException() : null;
            if (_isClosed) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isClosed) return;
                if (_loadingForensicsDumpPath == dumpPath) _loadingForensicsDumpPath = null;
                if (!t.IsCompletedSuccessfully)
                {
                    // Prevent infinite retry loop on corrupt/locked dump files (R2-NEW-007)
                    if (_activeOfflineDumpPath == dumpPath) _activeOfflineDumpPath = null;
                    _lastStatusMessage = _host.Localization.Format("dialogs.consentDumpFailed", new Dictionary<string, object?> { ["error"] = fault?.Message ?? "Dump parse failed" });
                    UpdateStatusBarOnly();
                    return;
                }
                if (_activeOfflineDumpPath != dumpPath) return;
                _activeForensicsDump = t.Result;
                if (_activeRoute.StartsWith("forensics-", StringComparison.Ordinal))
                    RenderActiveWorkspace(preserveScroll: true);
            });
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Displays an explicit consent dialog before capturing any MiniDump (.dmp) file (F-006),
    /// guarded against concurrent ContentDialog re-entrancy (R2-NEW-008).
    /// </summary>
    private async Task<bool> ConfirmMiniDumpConsentAsync(string targetFolder, string? targetProcessLabel = null)
    {
        if (_isClosed || _isConsentDialogOpen || Content?.XamlRoot is null)
            return false;

        _isConsentDialogOpen = true;
        try
        {
            var loc = _host.Localization;
            string body = loc.Format("dialogs.consentDumpBody", new Dictionary<string, object?> { ["path"] = loc.IsolateTechnicalToken(targetFolder) });
            if (!string.IsNullOrWhiteSpace(targetProcessLabel))
            {
                body = $"{loc.IsolateTechnicalToken(targetProcessLabel)}\n\n{body}";
            }

            var dlg = new ContentDialog
            {
                Title = loc.GetString("dialogs.consentDumpTitle"),
                Content = new TextBlock
                {
                    Text = body,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = _fontUi,
                    FontSize = 13
                },
                PrimaryButtonText = loc.GetString("dialogs.consentDumpConfirm"),
                CloseButtonText = loc.GetString("common.cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
                FlowDirection = loc.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
                RequestedTheme = _host.Settings.Current.Theme == AppThemeMode.Light ? ElementTheme.Light : ElementTheme.Dark
            };

            var result = await dlg.ShowAsync();
            return result == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            _host.DiagnosticsLogger.Warning("Diagnostics", $"Consent dialog closed/unavailable: {ex.Message}");
            return false;
        }
        finally
        {
            _isConsentDialogOpen = false;
        }
    }

    private async Task CaptureProcessMiniDumpWithConsentAsync(uint pid, string procName)
    {
        var loc = _host.Localization;
        try
        {
            string dumpDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RAMYXEN", "Dumps");
            Directory.CreateDirectory(dumpDir);

            if (!await ConfirmMiniDumpConsentAsync(dumpDir, $"{procName} (PID {pid})"))
                return;

            string dumpPath = Path.Combine(dumpDir, $"{procName}_{pid}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.dmp");
            _lastStatusMessage = loc.GetString("state.loading");
            UpdateStatusBarOnly();

            var (ok, err) = await Task.Run(() =>
            {
                bool created = _host.Monitor.ProcessCollector.TryCreateProcessMiniDump(pid, dumpPath, out string errorMsg);
                return (created, errorMsg);
            });

            if (_isClosed) return;

            if (ok)
            {
                _activeOfflineDumpPath = dumpPath;
                _activeForensicsDump = null;
                _lastStatusMessage = loc.Format("dialogs.consentDumpCaptured", new Dictionary<string, object?> { ["path"] = loc.IsolateTechnicalToken(Path.GetFileName(dumpPath)) });
                if (_activeRoute.StartsWith("forensics-", StringComparison.Ordinal))
                {
                    TriggerAsyncForensicsDump(dumpPath);
                    RenderActiveWorkspace(preserveScroll: true);
                }
            }
            else
            {
                _lastStatusMessage = loc.Format("dialogs.consentDumpFailed", new Dictionary<string, object?> { ["error"] = err });
            }
            UpdateStatusBarOnly();
        }
        catch (Exception ex)
        {
            _host.DiagnosticsLogger.Warning("Diagnostics", $"Process MiniDump capture failed: {ex.Message}");
            if (!_isClosed)
            {
                _lastStatusMessage = loc.Format("dialogs.consentDumpFailed", new Dictionary<string, object?> { ["error"] = ex.Message });
                UpdateStatusBarOnly();
            }
        }
    }

    private void UpdateStatusBarOnly()
    {
        var loc = _host.Localization;
        var sys = _host.Monitor.LatestSystem;
        var self = _host.Monitor.SelfMetrics;
        string privLabel = _isAdministrator ? loc.GetString("status.adminMode") : loc.GetString("status.standardUser");
        string priv = $"{loc.GetString("badges.live")} ({privLabel})";
        string stateBadge = _host.Monitor.IsPaused
            ? loc.GetString("state.paused")
            : $"{loc.GetString("badges.live")} {loc.IsolateTechnicalToken($"{_host.Settings.Current.SamplingIntervalMs}ms")}";
        _statusLeftText.Text = $"{stateBadge}  ·  {priv}  ·  {_lastStatusMessage}";
        if (sys != null)
        {
            string selfWs = self != null ? loc.IsolateTechnicalToken(FmtBytes(self.SelfWorkingSetBytes)) : "—";
            string inUsePair = loc.IsolateTechnicalToken($"{FmtBytes(sys.InUsePhysicalBytes)} / {FmtBytes(sys.TotalPhysicalBytes)} ({sys.MemoryLoadPercent}%)");
            string commitPair = loc.IsolateTechnicalToken($"{FmtBytes(sys.CommitTotalBytes)} / {FmtBytes(sys.CommitLimitBytes)}");
            string sampleMs = loc.IsolateTechnicalToken($"{self?.LastSampleDurationMs ?? 0}ms");
            _statusRightText.Text = $"{loc.GetString("system.memory.inUse")}: {inUsePair}  |  {loc.GetString("system.memory.committed")}: {commitPair}  |  {loc.GetString("status.selfWs")}: {selfWs} ({sampleMs})";
        }
    }

    private void RenderActiveWorkspace(bool preserveScroll = false)
    {
        var savedOffsets = preserveScroll
            ? _activeWorkspaceScrollViewers.Select(sv => sv.VerticalOffset).ToList()
            : new List<double>();

        _activeLocalSearchBox = null;
        _activeWorkspaceScrollViewer = null;
        _activeWorkspaceScrollViewers.Clear();
        _workspaceHost.Children.Clear();
        var sys = _host.Monitor.LatestSystem;
        if (sys == null) return;

        var loc = _host.Localization;
        _breadcrumbText.Text = DestinationRegistry.GetLocalizedTitle(loc, _activeRoute);

        FrameworkElement view = _activeRoute switch
        {
            "splash" or "first-launch" or "welcome" or "compat-check" or "permissions-setup" or "init-monitor"
                or "shell-main" or "shell-nav-expanded" or "shell-nav-collapsed" or "global-search"
                or "notifications" or "status-bar-detail" => BuildOnboardingAndShellWorkspace(sys),
            "overview" or "memory-usage-details" or "memory-composition" => BuildArchitecturalOverview(sys),
            "live-monitor" => BuildLiveSignalObservatory(sys),
            "process-explorer" or "process-tree" or "process-comparison" => BuildProcessExplorer(sys),
            "process-deep-dive" or "process-overview" or "process-memory" or "process-memory-map" or "heap-explorer" or "stack-explorer" or "process-modules" or "process-threads" or "process-handles" or "memory-strings" => BuildProcessDeepDive(sys),
            "virtual-overview" or "virtual-map" or "virtual-region-details" => BuildVirtualMemoryExplorer(),
            "physical-overview" or "page-distribution" or "physical-pages" or "physical-ranges" or "file-summary" or "file-details" or "priority-summary" or "page-inspector" => BuildPhysicalMemoryExplorer(sys),
            "profiler-home" or "profiler-new" or "profiler-providers" or "profiler-recording" or "profiler-timeline" or "profiler-hotspots" or "profiler-callstacks" or "profiler-gc" or "profiler-comparison" => BuildProfilerWorkspace(),
            "snapshot-library" or "snapshot-create" or "snapshot-details" or "snapshot-comparison" or "snapshot-comparison-viz" or "snapshot-import" or "snapshot-export" or "snapshot-management" => BuildSnapshotsWorkspace(sys),
            "forensics-home" or "forensics-source" or "forensics-import" or "forensics-acq-config" or "forensics-acq-progress" or "forensics-analysis" or "forensics-pstree" or "forensics-vad" or "forensics-pte" or "forensics-kernel" or "forensics-drivers" or "forensics-strings" or "forensics-yara" or "forensics-suspicious" or "forensics-artifacts" or "forensics-timeline" or "forensics-report" => BuildForensicsWorkspace(),
            "alerts-center" or "alert-details" or "alert-rules" or "alert-rule-create" => BuildLeakAndAlertsWorkspace(sys),
            "report-center" or "report-preview" or "export-config" or "export-progress" => BuildReportsWorkspace(sys),
            "settings-advanced" => BuildDiagnosticsWorkspace(),
            "help-center" or "glossary" or "about" => BuildHelpAndAboutWorkspace(),
            _ => BuildSettingsAndGlossaryWorkspace(sys)
        };

        // Track both single top-level ScrollViewer AND multi-pane Grid ScrollViewers (e.g. Overview 3 panes - R2-NEW-020)
        if (view is ScrollViewer topSv)
        {
            _activeWorkspaceScrollViewer = topSv;
            _activeWorkspaceScrollViewers.Add(topSv);
        }
        else if (view is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is ScrollViewer childSv)
                    _activeWorkspaceScrollViewers.Add(childSv);
            }
            if (_activeWorkspaceScrollViewers.Count > 0)
                _activeWorkspaceScrollViewer = _activeWorkspaceScrollViewers[0];
        }

        if (preserveScroll && savedOffsets.Count > 0)
        {
            for (int i = 0; i < Math.Min(savedOffsets.Count, _activeWorkspaceScrollViewers.Count); i++)
            {
                double offset = savedOffsets[i];
                if (offset > 0)
                {
                    var targetSv = _activeWorkspaceScrollViewers[i];
                    targetSv.Loaded += (_, _) => targetSv.ChangeView(null, offset, null, disableAnimation: true);
                }
            }
        }

        _workspaceHost.Children.Add(view);
        UpdateStatusBarOnly();
    }

    // =========================================================================
    // 1. ARCHITECTURAL MAIN OVERVIEW (Responsive 1080p / 2K / 4K Layout)
    // =========================================================================
    private FrameworkElement BuildArchitecturalOverview(SystemMemoryTelemetry sys)
    {
        var loc = _host.Localization;
        var grid = new Grid { ColumnSpacing = 1, Background = _seamBrush };
        bool isCompact = _currentBreakpoint == 0;
        if (isCompact)
        {
            // Responsive 2-column Compact layout (<1180 DIP) prevents crushing the center Topology Bridge to 199 DIP (F-025)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(285) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        else
        {
            double leftColW = _currentBreakpoint == 2 ? 364 : 338;
            double rightColW = _currentBreakpoint == 2 ? 410 : 365;
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(leftColW) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(rightColW) });
        }

        // LEFT: System Memory Telemetry Ledger
        var leftZone = MakeSeamZone();
        leftZone.Children.Add(MakeZoneHeader($"{loc.GetString("navigation.overview")} · {loc.GetString("overview.telemetryLedger")}", loc.GetString("badges.live")));

        leftZone.Children.Add(new TextBlock
        {
            Text = loc.IsolateTechnicalToken(FmtBytes(sys.InUsePhysicalBytes)),
            FontFamily = _fontMono,
            FontSize = _currentBreakpoint == 2 ? 34 : 30,
            FontWeight = FontWeights.Bold,
            Foreground = _accentCyan
        });
        leftZone.Children.Add(new TextBlock
        {
            Text = $"{loc.GetString("system.memory.inUse")} / {loc.IsolateTechnicalToken($"{FmtBytes(sys.TotalPhysicalBytes)} ({sys.MemoryLoadPercent}%)")}",
            FontFamily = _fontUi,
            FontSize = 12,
            Foreground = _textSecondary,
            Margin = new Thickness(0, 0, 0, 8)
        });
        leftZone.Children.Add(MakeProportionBar(sys.MemoryLoadPercent / 100.0, _accentCyan, 6));

        leftZone.Children.Add(MakeLedgerRow(loc.GetString("system.memory.installed"), loc.IsolateTechnicalToken(FmtBytes(sys.InstalledPhysicalBytes)), _textPrimary));
        leftZone.Children.Add(MakeLedgerRow(loc.GetString("system.memory.available"), loc.IsolateTechnicalToken(FmtBytes(sys.AvailablePhysicalBytes)), _semEmerald));
        leftZone.Children.Add(MakeLedgerRow(loc.GetString("physical.list.standby"), loc.IsolateTechnicalToken(FmtBytes(sys.StandbyCacheBytes)), _semEmerald));
        leftZone.Children.Add(MakeLedgerRow(loc.GetString("physical.list.modified"), loc.IsolateTechnicalToken(FmtBytes(sys.ModifiedBytes)), _semAmber));
        leftZone.Children.Add(MakeLedgerRow(loc.GetString("system.memory.committed"), loc.IsolateTechnicalToken($"{FmtBytes(sys.CommitTotalBytes)} / {FmtBytes(sys.CommitLimitBytes)} ({sys.CommitRatioPercent:0.0}%)"), _accentViolet));
        leftZone.Children.Add(MakeLedgerRow(loc.GetString("system.memory.nonpagedPool"), loc.IsolateTechnicalToken(FmtBytes(sys.KernelNonpagedBytes)), _semOrange));
        leftZone.Children.Add(MakeLedgerRow(loc.GetString("system.memory.pagedPool"), loc.IsolateTechnicalToken(FmtBytes(sys.KernelPagedBytes)), _textPrimary));
        leftZone.Children.Add(MakeLedgerRow(loc.GetString("system.memory.hardwareReserved"), loc.IsolateTechnicalToken(FmtBytes(sys.HardwareReservedBytes)), _textMuted));
        leftZone.Children.Add(MakeLedgerRow(loc.GetString("system.memory.pageFaultsPerSec"), loc.IsolateTechnicalToken($"{FmtDec(sys.PageFaultsPerSec)} /s"), _textPrimary));
        leftZone.Children.Add(MakeLedgerRow(loc.GetString("system.memory.hardFaultsPerSec"), loc.IsolateTechnicalToken($"{FmtDec(sys.HardFaultsPerSec)} /s"), sys.HardFaultsPerSec > 25 ? _semCrimson : _textPrimary));
        leftZone.Children.Add(MakeLedgerRow(loc.GetString("system.memory.processesThreadsHandles"), loc.IsolateTechnicalToken($"{FmtNum(sys.ProcessCount)} / {FmtNum(sys.ThreadCount)} / {FmtNum(sys.HandleCount)}"), _textSecondary));

        var leftScroll = new ScrollViewer { Content = leftZone, Background = _bgZone };
        Grid.SetColumn(leftScroll, 0);
        grid.Children.Add(leftScroll);

        // CENTER: Physical-to-Virtual Memory Bridge & Tectonic Strata Composition
        var centerZone = MakeSeamZone(useCanvasBg: true);
        string hostDisplay = App.PrivacySafeCapture ? "WORKSTATION-01" : Environment.MachineName;
        centerZone.Children.Add(MakeZoneHeader(loc.GetString("overview.topologyBridge"), $"{loc.GetString("status.host")}: {loc.IsolateTechnicalToken(hostDisplay)}"));

        var strataGrid = new Grid { Height = 38, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 4, 0, 12) };
        double total = Math.Max(1.0, sys.TotalPhysicalBytes);
        double actW = Math.Max(0.05, (double)sys.InUsePhysicalBytes / total);
        double stbW = Math.Max(0.02, (double)sys.StandbyCacheBytes / total);
        double freW = Math.Max(0.02, (double)sys.FreeOrZeroedBytes / total);
        strataGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(actW, GridUnitType.Star) });
        strataGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(stbW, GridUnitType.Star) });
        strataGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(freW, GridUnitType.Star) });

        // WCAG AA stratum text contrast: crisp white ink on saturated Light-theme cyan/emerald, obsidian dark ink on luminous Dark/HC (F-024)
        var stratumInk = _host.Settings.Current.Theme == AppThemeMode.Light
            ? new SolidColorBrush(Colors.White)
            : new SolidColorBrush(Color.FromArgb(255, 9, 12, 16));
        strataGrid.Children.Add(MakeStratumBlock($"{loc.GetString("physical.list.active").ToUpperInvariant()} ({loc.IsolateTechnicalToken(FmtBytes(sys.InUsePhysicalBytes))})", _accentCyan, stratumInk, 0));
        strataGrid.Children.Add(MakeStratumBlock($"{loc.GetString("physical.list.standby").ToUpperInvariant()} ({loc.IsolateTechnicalToken(FmtBytes(sys.StandbyCacheBytes))})", _semEmerald, stratumInk, 1));
        strataGrid.Children.Add(MakeStratumBlock($"{loc.GetString("physical.list.free").ToUpperInvariant()} ({loc.IsolateTechnicalToken(FmtBytes(sys.FreeOrZeroedBytes))})", _bgSurface2, _textPrimary, 2));
        centerZone.Children.Add(strataGrid);

        // Responsive inner bridge: 1-column stack at Compact (<1180 DIP), 2-column side-by-side at Standard/Wide (F-025)
        var colPhysical = new StackPanel { Spacing = 8 };
        colPhysical.Children.Add(MakeTopologyCard(loc.GetString("overview.activeWorkingSets"), FmtBytes(sys.InUsePhysicalBytes), $"{sys.MemoryLoadPercent}% {loc.GetString("system.memory.inUse")}", _accentCyan, () => Navigate("physical-overview")));
        colPhysical.Children.Add(MakeTopologyCard(loc.GetString("overview.standbyReclaimable"), FmtBytes(sys.StandbyCacheBytes), loc.GetString("physical.prioritySummary"), _semEmerald, () => Navigate("priority-summary")));
        colPhysical.Children.Add(MakeTopologyCard(loc.GetString("overview.modifiedAndPools"), $"{FmtBytes(sys.ModifiedBytes)} · NP {FmtBytes(sys.KernelNonpagedBytes)}", $"{loc.GetString("system.memory.pagedPool")}: {loc.IsolateTechnicalToken(FmtBytes(sys.KernelPagedBytes))}", _semAmber, () => Navigate("physical-overview")));
        colPhysical.Children.Add(MakeTopologyCard(loc.GetString("overview.committedCharge"), $"{FmtBytes(sys.CommitTotalBytes)} / {FmtBytes(sys.CommitLimitBytes)}", $"{sys.CommitRatioPercent}% {loc.GetString("system.memory.commitLimit")}", _accentViolet, () => Navigate("virtual-overview")));

        var colProcs = new StackPanel { Spacing = 8 };
        foreach (var proc in _host.Monitor.LatestProcesses.Take(5))
        {
            uint pid = proc.Pid;
            var iconEl = _processIconCache.CreateProcessIconElement(proc, 20, _accentCyan, _currentScaleFactor);
            colProcs.Children.Add(MakeTopologyCard(
                $"{proc.Name} (PID {proc.Pid})",
                $"{loc.GetString("process.column.workingSet")}: {loc.IsolateTechnicalToken(FmtBytes(proc.WorkingSetBytes))}  |  {loc.GetString("process.column.privateBytes")}: {loc.IsolateTechnicalToken(FmtBytes(proc.PrivateCommitBytes))}",
                $"{proc.Category} · {loc.GetString("process.column.handles")}: {FmtNum(proc.HandleCount)} · {loc.GetString("process.column.threads")}: {FmtNum(proc.ThreadCount)}",
                proc.Pid == _host.SelectedPid ? _accentCyan : _accentViolet,
                () => SelectProcessAndNavigate(pid, "process-deep-dive"),
                leadingIcon: iconEl,
                isTechnicalTitle: true));
        }

        if (_currentBreakpoint == 0)
        {
            var stackedBridge = new StackPanel { Spacing = 10, Margin = new Thickness(0, 4, 0, 12) };
            stackedBridge.Children.Add(colPhysical);
            stackedBridge.Children.Add(colProcs);
            centerZone.Children.Add(stackedBridge);
        }
        else
        {
            var bridgeGrid = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 4, 0, 12) };
            bridgeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bridgeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(colPhysical, 0);
            bridgeGrid.Children.Add(colPhysical);
            Grid.SetColumn(colProcs, 1);
            bridgeGrid.Children.Add(colProcs);
            centerZone.Children.Add(bridgeGrid);
        }

        centerZone.Children.Add(MakeInfoBanner(loc.GetString("overview.semanticGuardrail"), _semEmerald));

        var centerScroll = new ScrollViewer { Content = centerZone, Background = _bgCanvas };
        Grid.SetColumn(centerScroll, 1);
        grid.Children.Add(centerScroll);

        // RIGHT: Live Exhaustion/Leak Triage & Top Consumers with Official Windows Process Icons
        var rightZone = MakeSeamZone();
        var exhaustion = _host.Monitor.LeakEngine.DetectSystemExhaustion(sys);
        string exhaustionLabel = exhaustion == MemoryExhaustionKind.None
            ? loc.GetString("leak.exhaustion.none")
            : exhaustion.ToString().ToUpperInvariant();
        rightZone.Children.Add(MakeZoneHeader(loc.GetString("overview.exhaustionTriage"), exhaustionLabel));

        foreach (var inv in _host.Monitor.ActiveInvestigations.Take(3))
        {
            uint pid = inv.TargetPid;
            var col = inv.Classification is LeakClassification.StrongLeakEvidence or LeakClassification.ConfirmedLeak ? _semCrimson
                    : inv.Classification == LeakClassification.SuspectedLeak ? _semAmber : _accentCyan;
            string clsLabel = inv.Classification == LeakClassification.NormalGrowth
                ? loc.GetString("leak.classification.normalGrowth")
                : inv.Classification.ToString();
            string subEvidence = inv.EvidenceItems.ElementAtOrDefault(1) ?? inv.SuspectedComponent;
            if (loc.IsRightToLeft && subEvidence.StartsWith("OLS ", StringComparison.OrdinalIgnoreCase))
                subEvidence = loc.IsolateTechnicalToken(subEvidence);
            rightZone.Children.Add(MakeTopologyCard(
                $"{inv.TargetProcessName} (PID {inv.TargetPid}) · {clsLabel}",
                $"{loc.GetString("process.column.privateBytes")}: {loc.IsolateTechnicalToken($"{FmtBytes(inv.CurrentCommitBytes)} ({inv.ConfidencePercent}%)")}",
                subEvidence,
                col,
                () => SelectProcessAndNavigate(pid, "alerts-center"),
                isTechnicalTitle: true));
        }

        rightZone.Children.Add(MakeZoneHeader(loc.GetString("overview.topConsumers"), loc.FormatPlural("process.count", _host.Monitor.LatestProcesses.Count)));
        ulong maxWs = Math.Max(1UL, _host.Monitor.LatestProcesses.FirstOrDefault()?.WorkingSetBytes ?? 1UL);
        foreach (var p in _host.Monitor.LatestProcesses.Take(10))
        {
            uint pid = p.Pid;
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3), ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });

            row.Children.Add(_processIconCache.CreateProcessIconElement(p, 18, _textSecondary, _currentScaleFactor));

            var nameBtn = new HyperlinkButton
            {
                Content = new TextBlock
                {
                    Text = p.Name,
                    FlowDirection = FlowDirection.LeftToRight,
                    TextTrimming = TextTrimming.CharacterEllipsis
                },
                FontSize = 12,
                Foreground = _textPrimary,
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = loc.IsRightToLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(nameBtn, $"{p.Name} PID {p.Pid}");
            nameBtn.Click += (_, _) => SelectProcessAndNavigate(pid, "process-deep-dive");
            Grid.SetColumn(nameBtn, 1);
            row.Children.Add(nameBtn);

            var wsTxt = new TextBlock
            {
                Text = loc.IsolateTechnicalToken(FmtBytes(p.WorkingSetBytes)),
                FontFamily = _fontMono,
                FontSize = 11.5,
                Foreground = _accentCyan,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                FlowDirection = FlowDirection.LeftToRight
            };
            Grid.SetColumn(wsTxt, 2);
            row.Children.Add(wsTxt);

            var bar = MakeProportionBar((double)p.WorkingSetBytes / maxWs, _accentCyan, 5);
            bar.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(bar, 3);
            row.Children.Add(bar);

            rightZone.Children.Add(row);
        }

        if (isCompact)
        {
            // In Compact (<1180 DIP) mode, stack Triage & Top Consumers cleanly inside the center scroll column
            // so the 2-column grid never clips a 3rd column (F-025).
            rightZone.Margin = new Thickness(0, 12, 0, 0);
            centerZone.Children.Add(rightZone);
        }
        else
        {
            var rightScroll = new ScrollViewer { Content = rightZone, Background = _bgZone };
            Grid.SetColumn(rightScroll, 2);
            grid.Children.Add(rightScroll);
        }

        return grid;
    }

    // =========================================================================
    // 2. LIVE SIGNAL OBSERVATORY (#15)
    // =========================================================================
    private FrameworkElement BuildLiveSignalObservatory(SystemMemoryTelemetry sys)
    {
        var loc = _host.Localization;
        var history = _host.Monitor.GetHistory(60);
        var stack = new StackPanel { Spacing = 1, Background = _seamBrush };

        var oscZone = MakeSeamZone(useCanvasBg: true);
        oscZone.Children.Add(MakeZoneHeader(loc.GetString("liveMonitor.title").ToUpperInvariant(), $"{loc.Format("status.samples", new Dictionary<string, object?> { ["count"] = history.Count })} · {loc.IsolateTechnicalToken($"{_host.Settings.Current.SamplingIntervalMs}ms")}"));

        oscZone.Children.Add(MakeWaveformChannel(loc.GetString("liveMonitor.ch1PhysicalInUse"), FmtBytes(sys.InUsePhysicalBytes),
            history.Select(h => (double)h.InUsePhysicalBytes / Math.Max(1, h.TotalPhysicalBytes)).ToList(), _accentCyan));
        oscZone.Children.Add(MakeWaveformChannel(loc.GetString("liveMonitor.ch2StandbyCache"), FmtBytes(sys.StandbyCacheBytes),
            history.Select(h => (double)h.StandbyCacheBytes / Math.Max(1, h.TotalPhysicalBytes)).ToList(), _semEmerald));
        oscZone.Children.Add(MakeWaveformChannel(loc.GetString("liveMonitor.ch3CommitCharge"), $"{FmtBytes(sys.CommitTotalBytes)} ({sys.CommitRatioPercent}%)",
            history.Select(h => h.CommitRatioPercent / 100.0).ToList(), _accentViolet));
        oscZone.Children.Add(MakeWaveformChannel(loc.GetString("liveMonitor.ch4PageFaultVelocity"), $"{FmtDec(sys.PageFaultsPerSec)} /s ({loc.GetString("system.memory.hardFaultsPerSec")}: {FmtDec(sys.HardFaultsPerSec)}/s)",
            history.Select(h => Math.Min(1.0, h.PageFaultsPerSec / 5000.0)).ToList(), _semAmber));
        oscZone.Children.Add(MakeWaveformChannel(loc.GetString("liveMonitor.ch5KernelNonpaged"), FmtBytes(sys.KernelNonpagedBytes),
            history.Select(h => Math.Min(1.0, (double)h.KernelNonpagedBytes / (2048.0 * 1024 * 1024))).ToList(), _semOrange));
        stack.Children.Add(oscZone);

        var bottomZone = MakeSeamZone();
        bottomZone.Children.Add(MakeZoneHeader(loc.GetString("liveMonitor.deltaMatrix"), loc.GetString("process.deepDive").ToUpperInvariant()));
        foreach (var p in _host.Monitor.LatestProcesses.Take(10))
        {
            uint pid = p.Pid;
            string rate = p.GrowthRateBytesPerSec != 0
                ? $"{(p.GrowthRateBytesPerSec / 1024.0):+0.0;-0.0;0.0} KiB/s"
                : "0.0 KiB/s";
            bottomZone.Children.Add(MakeTableRow(
                [$"{p.Name} (PID {p.Pid})", p.Category, $"{loc.GetString("process.column.workingSet")}: {loc.IsolateTechnicalToken(FmtBytes(p.WorkingSetBytes))}", $"{loc.GetString("process.column.privateBytes")}: {loc.IsolateTechnicalToken(FmtBytes(p.PrivateCommitBytes))}", rate, $"{loc.GetString("process.column.handles")}: {FmtNum(p.HandleCount)}"],
                p.GrowthRateBytesPerSec > 256 * 1024 ? _semCrimson : _textPrimary,
                () => SelectProcessAndNavigate(pid, "process-deep-dive"),
                leadingIcon: _processIconCache.CreateProcessIconElement(p, 18, _textSecondary, _currentScaleFactor)));
        }
        stack.Children.Add(bottomZone);

        return WrapConstrainedScrollViewer(stack, _bgCanvas);
    }

    // =========================================================================
    // 3. PROCESS EXPLORER (#18), PROCESS TREE (#19) & COMPARISON (#20)
    // =========================================================================
    private FrameworkElement BuildProcessExplorer(SystemMemoryTelemetry sys)
    {
        var loc = _host.Localization;
        var zone = MakeSeamZone();
        zone.Children.Add(MakeZoneHeader(
            $"{loc.GetString("process.title").ToUpperInvariant()} ({loc.FormatPlural("process.count", _host.Monitor.LatestProcesses.Count)})",
            DestinationRegistry.GetLocalizedTitle(loc, _activeRoute)));

        var filterRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 10) };
        var searchBox = new TextBox
        {
            Width = _currentBreakpoint == 0 ? 220 : 320,
            PlaceholderText = loc.GetString("process.filterPlaceholder"),
            Text = _processSearchQuery
        };
        _activeLocalSearchBox = searchBox;
        AutomationProperties.SetName(searchBox, loc.GetString("process.filterPlaceholder"));
        filterRow.Children.Add(searchBox);

        foreach (var (rKey, rLbl) in new[]
        {
            ("process-explorer", $"{loc.IsolateTechnicalToken("#18")} {loc.GetString("process.title")}"),
            ("process-tree", $"{loc.IsolateTechnicalToken("#19")} {loc.GetString("process.tree")}"),
            ("process-comparison", $"{loc.IsolateTechnicalToken("#20")} {loc.GetString("process.compare")}")
        })
        {
            string k = rKey;
            filterRow.Children.Add(MakeButton(rLbl, _activeRoute == k, () => Navigate(k)));
        }

        filterRow.Children.Add(MakeButtonWithTrailingChevron(loc.GetString("process.openDeepDive"), true, () => SelectProcessAndNavigate(_host.SelectedPid, "process-deep-dive")));
        zone.Children.Add(filterRow);

        var rowsContainer = new StackPanel { Spacing = 0 };
        zone.Children.Add(rowsContainer);

        void PopulateProcessRows()
        {
            rowsContainer.Children.Clear();
            string normQ = LocalizationService.NormalizeForSearch(_processSearchQuery);
            var filtered = _host.Monitor.LatestProcesses
                .Where(p => string.IsNullOrEmpty(normQ)
                         || LocalizationService.NormalizeForSearch(p.Name).Contains(normQ, StringComparison.Ordinal)
                         || p.Pid.ToString().Contains(normQ, StringComparison.Ordinal)
                         || LocalizationService.NormalizeForSearch(p.Category).Contains(normQ, StringComparison.Ordinal))
                .ToList();

            if (_activeRoute == "process-tree")
            {
                rowsContainer.Children.Add(MakeTableRow(
                    [
                        $"{loc.GetString("process.tree").ToUpperInvariant()} ({loc.GetString("process.column.name")})",
                        $"{loc.GetString("process.column.pid")} / {loc.GetString("process.column.ppid")}",
                        loc.GetString("process.column.workingSet").ToUpperInvariant(),
                        loc.GetString("process.column.privateBytes").ToUpperInvariant(),
                        $"{loc.GetString("process.column.handles").ToUpperInvariant()} / {loc.GetString("process.column.threads").ToUpperInvariant()}",
                        loc.GetString("process.column.provenance").ToUpperInvariant()
                    ],
                    _textMuted));

                var byParent = filtered.ToLookup(p => p.ParentPid);
                var pidSet = new HashSet<uint>(filtered.Select(p => p.Pid));
                var roots = filtered.Where(p => !pidSet.Contains(p.ParentPid) || p.ParentPid == p.Pid)
                    .OrderByDescending(p => p.WorkingSetBytes)
                    .Take(40)
                    .ToList();

                int rendered = 0;
                void RenderTreeNode(ProcessMemoryInfo node, int depth)
                {
                    if (rendered >= 85) return;
                    rendered++;
                    uint pid = node.Pid;
                    var children = byParent[node.Pid].Where(c => c.Pid != node.Pid).OrderByDescending(c => c.WorkingSetBytes).ToList();
                    string label = children.Count > 0 ? $"{node.Name} ({children.Count})" : node.Name;

                    // Clean logical indentation + RTL-mirrored Fluent chevron instead of ASCII box characters (F-047)
                    var treeLead = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(depth * 14, 0, 0, 0)
                    };
                    if (depth > 0)
                    {
                        treeLead.Children.Add(FluentIconCatalog.Create(FluentGlyph.ChevronRight, 11, _textMuted, mirrorInRtl: true));
                    }
                    treeLead.Children.Add(_processIconCache.CreateProcessIconElement(node, 18, _textSecondary, _currentScaleFactor));

                    rowsContainer.Children.Add(MakeTableRow(
                        [
                            label,
                            $"PID {node.Pid} (PPID {node.ParentPid})",
                            FmtBytes(node.WorkingSetBytes),
                            FmtBytes(node.PrivateCommitBytes),
                            $"{FmtNum(node.HandleCount)} / {FmtNum(node.ThreadCount)}",
                            node.IsProtectedOrRestricted ? loc.GetString("badges.adminRequired") : loc.GetString("badges.live")
                        ],
                        pid == _host.SelectedPid ? _accentCyan : _textPrimary,
                        () => SelectProcessAndNavigate(pid, "process-deep-dive"),
                        leadingIcon: treeLead));

                    foreach (var child in children.Take(6))
                        RenderTreeNode(child, Math.Min(depth + 1, 5));
                }

                foreach (var root in roots)
                    RenderTreeNode(root, 0);
            }
            else if (_activeRoute == "process-comparison")
            {
                var primary = _host.GetSelectedProcess() ?? filtered.FirstOrDefault();
                var secondary = filtered.FirstOrDefault(p => p.Pid != primary?.Pid) ?? primary;
                if (primary != null && secondary != null)
                {
                    rowsContainer.Children.Add(MakeInfoBanner(
                        $"{loc.GetString("process.compare")}: [A] {primary.Name} (PID {primary.Pid})  vs.  [B] {secondary.Name} (PID {secondary.Pid})",
                        _accentViolet));

                    rowsContainer.Children.Add(MakeTableRow(
                        [loc.GetString("process.metric").ToUpperInvariant(), $"[A] {primary.Name} (PID {primary.Pid})", $"[B] {secondary.Name} (PID {secondary.Pid})", loc.GetString("process.differentialDelta").ToUpperInvariant()],
                        _textMuted));

                    rowsContainer.Children.Add(MakeTableRow(
                        [loc.GetString("process.column.workingSet"), FmtBytes(primary.WorkingSetBytes), FmtBytes(secondary.WorkingSetBytes), FmtDelta((long)primary.WorkingSetBytes - (long)secondary.WorkingSetBytes)],
                        _accentCyan));
                    rowsContainer.Children.Add(MakeTableRow(
                        [loc.GetString("process.column.privateWorkingSet"), FmtBytes(primary.PrivateWorkingSetBytes), FmtBytes(secondary.PrivateWorkingSetBytes), FmtDelta((long)primary.PrivateWorkingSetBytes - (long)secondary.PrivateWorkingSetBytes)],
                        _textPrimary));
                    rowsContainer.Children.Add(MakeTableRow(
                        [loc.GetString("process.column.privateBytes"), FmtBytes(primary.PrivateCommitBytes), FmtBytes(secondary.PrivateCommitBytes), FmtDelta((long)primary.PrivateCommitBytes - (long)secondary.PrivateCommitBytes)],
                        _accentViolet));
                    rowsContainer.Children.Add(MakeTableRow(
                        [loc.GetString("process.column.virtualSize"), FmtBytes(primary.VirtualSizeBytes), FmtBytes(secondary.VirtualSizeBytes), FmtDelta((long)primary.VirtualSizeBytes - (long)secondary.VirtualSizeBytes)],
                        _textPrimary));
                    rowsContainer.Children.Add(MakeTableRow(
                        [loc.GetString("process.column.handles"), FmtNum(primary.HandleCount), FmtNum(secondary.HandleCount), $"{(int)primary.HandleCount - (int)secondary.HandleCount:+0;-0;0}"],
                        _semAmber));
                    rowsContainer.Children.Add(MakeTableRow(
                        [loc.GetString("process.column.threads"), FmtNum(primary.ThreadCount), FmtNum(secondary.ThreadCount), $"{(int)primary.ThreadCount - (int)secondary.ThreadCount:+0;-0;0}"],
                        _semEmerald));
                }
            }
            else
            {
                rowsContainer.Children.Add(MakeTableRow(
                    [
                        loc.GetString("process.column.name").ToUpperInvariant(),
                        $"{loc.GetString("process.column.pid")} / {loc.GetString("process.column.ppid")}",
                        loc.GetString("process.column.workingSet").ToUpperInvariant(),
                        loc.GetString("process.column.privateWorkingSet").ToUpperInvariant(),
                        loc.GetString("process.column.privateBytes").ToUpperInvariant(),
                        $"{loc.GetString("process.column.handles").ToUpperInvariant()} / {loc.GetString("process.column.threads").ToUpperInvariant()}",
                        loc.GetString("process.column.provenance").ToUpperInvariant()
                    ],
                    _textMuted));

                foreach (var p in filtered.Take(80))
                {
                    uint pid = p.Pid;
                    bool selected = pid == _host.SelectedPid;
                    rowsContainer.Children.Add(MakeTableRow(
                        [
                            p.Name,
                            $"PID {p.Pid} (PPID {p.ParentPid})",
                            FmtBytes(p.WorkingSetBytes),
                            FmtBytes(p.PrivateWorkingSetBytes),
                            FmtBytes(p.PrivateCommitBytes),
                            $"{FmtNum(p.HandleCount)} / {FmtNum(p.ThreadCount)}",
                            p.IsProtectedOrRestricted ? loc.GetString("badges.adminRequired") : loc.GetString("badges.live")
                        ],
                        selected ? _accentCyan : _textPrimary,
                        () => SelectProcessAndNavigate(pid, "process-deep-dive"),
                        leadingIcon: _processIconCache.CreateProcessIconElement(p, 18, selected ? _accentCyan : _textSecondary, _currentScaleFactor)));
                }
            }
        }

        searchBox.TextChanged += (_, _) =>
        {
            _processSearchQuery = searchBox.Text;
            PopulateProcessRows();
        };
        PopulateProcessRows();

        return WrapConstrainedScrollViewer(zone, _bgZone);
    }

    // =========================================================================
    // 4. NON-BLOCKING ASYNC PROCESS DEEP DIVE (#21-#30)
    // =========================================================================
    private FrameworkElement BuildProcessDeepDive(SystemMemoryTelemetry sys)
    {
        var loc = _host.Localization;
        var proc = _host.GetSelectedProcess();
        if (proc == null) return MakeSeamZone();

        if (_activeInspection is null || _activeInspection.Pid != proc.Pid)
        {
            if (_lastRequestedInspectionPid != proc.Pid)
                TriggerAsyncProcessInspection(proc, alsoLoadHeaps: _activeRoute == "heap-explorer");
        }

        var payload = _activeInspection is not null && _activeInspection.Pid == proc.Pid ? _activeInspection : null;
        var regions = payload?.Regions ?? [];
        var modules = payload?.Modules ?? [];
        var threads = payload?.Threads ?? [];
        var heaps = payload?.Heaps ?? [];
        bool isLoading = payload?.IsLoading ?? true;

        var stack = new StackPanel { Spacing = 1, Background = _seamBrush };

        // Header with DPI-aware Official Windows Executable Icon
        var topZone = MakeSeamZone();
        var procTitleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 0, 0, 8) };
        procTitleRow.Children.Add(_processIconCache.CreateProcessIconElement(proc, 30, _accentCyan, _currentScaleFactor));

        var procTitleStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        procTitleStack.Children.Add(new TextBlock
        {
            Text = $"{proc.Name}  (PID {proc.Pid} · PPID {proc.ParentPid})",
            FontFamily = _fontUi,
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = _textPrimary,
            FlowDirection = FlowDirection.LeftToRight
        });
        procTitleStack.Children.Add(new TextBlock
        {
            Text = $"{loc.IsolateTechnicalToken(proc.ExecutablePath)}  |  {loc.IsolateTechnicalToken(proc.Architecture)}  |  {loc.GetString("process.column.workingSet")}: {loc.IsolateTechnicalToken(FmtBytes(proc.WorkingSetBytes))}  |  {loc.GetString("process.column.privateBytes")}: {loc.IsolateTechnicalToken(FmtBytes(proc.PrivateCommitBytes))}  |  {loc.GetString("process.column.virtualSize")}: {loc.IsolateTechnicalToken(FmtBytes(proc.VirtualSizeBytes))}",
            FontFamily = _fontUi,
            FontSize = 11.5,
            Foreground = _textSecondary,
            TextWrapping = TextWrapping.Wrap
        });
        procTitleRow.Children.Add(procTitleStack);
        topZone.Children.Add(procTitleRow);

        if (isLoading)
        {
            topZone.Children.Add(MakeInfoBanner(loc.GetString("process.loadingDetails"), _accentCyan));
        }
        else if (proc.IsProtectedOrRestricted || payload?.RegionsAccessDenied == true)
        {
            topZone.Children.Add(MakeInfoBanner(loc.GetString("process.restrictedDetailsNote"), _semAmber));
        }
        else if (payload is not null)
        {
            topZone.Children.Add(MakeInfoBanner(
                loc.Format("process.loadedInMs", new Dictionary<string, object?> { ["ms"] = payload.LightweightElapsedMs }),
                _semEmerald));
        }

        var subBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var (key, lbl) in new[]
        {
            ("process-deep-dive", $"{loc.IsolateTechnicalToken("#21")} {loc.GetString("process.tab.overview")}"),
            ("process-memory-map", $"{loc.IsolateTechnicalToken("#24")} {loc.GetString("process.tab.memoryMap")}"),
            ("heap-explorer", $"{loc.IsolateTechnicalToken("#25")} {loc.GetString("process.tab.heap")}"),
            ("stack-explorer", $"{loc.IsolateTechnicalToken("#26")} {loc.GetString("process.tab.stack")}"),
            ("process-modules", $"{loc.IsolateTechnicalToken("#27")} {loc.GetString("process.tab.modules")}"),
            ("process-handles", $"{loc.IsolateTechnicalToken("#29")} {loc.GetString("process.tab.handles")}"),
            ("memory-strings", $"{loc.IsolateTechnicalToken("#30")} {loc.GetString("process.tab.strings")}")
        })
        {
            string k = key;
            subBar.Children.Add(MakeButton(lbl, _activeRoute == k, () => Navigate(k)));
        }
        subBar.Children.Add(MakeButton(loc.GetString("process.captureMiniDump"), false, () => _ = CaptureProcessMiniDumpWithConsentAsync(proc.Pid, proc.Name)));
        topZone.Children.Add(new ScrollViewer { Content = subBar, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        stack.Children.Add(topZone);

        // Split-Plane Process Anatomy Summary (Regions | On-Demand Heaps | Fast Modules & Threads)
        var splitGrid = new Grid { ColumnSpacing = 1, Background = _seamBrush };
        splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var regCol = MakeSeamZone(useCanvasBg: true);
        regCol.Children.Add(MakeZoneHeader($"{loc.GetString("virtual.addressSpaceMap").ToUpperInvariant()} ({regions.Count})", payload?.RegionsTruncated == true ? loc.GetString("badges.partial") : "VirtualQueryEx"));
        if (isLoading)
            regCol.Children.Add(new TextBlock { Text = loc.GetString("state.loading"), Foreground = _textMuted, FontSize = 11.5 });
        foreach (var g in regions.GroupBy(r => r.RegionType))
        {
            ulong bytes = (ulong)g.Sum(x => (long)x.SizeBytes);
            regCol.Children.Add(MakeLedgerRow($"{g.Key} ({g.Count()})", FmtBytes(bytes), _accentCyan));
        }
        Grid.SetColumn(regCol, 0);
        splitGrid.Children.Add(regCol);

        var heapCol = MakeSeamZone(useCanvasBg: true);
        string heapBadge = payload?.HeapsTimedOut == true ? loc.GetString("badges.partial")
                         : payload?.HeapsLoaded == true ? $"{payload.HeapElapsedMs}ms"
                         : "ON-DEMAND";
        heapCol.Children.Add(MakeZoneHeader($"{loc.GetString("process.tab.heap").ToUpperInvariant()} ({heaps.Count})", heapBadge));
        if (payload?.HeapsLoading == true)
        {
            heapCol.Children.Add(new TextBlock { Text = loc.GetString("process.heapWalking"), Foreground = _accentViolet, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
        }
        else if (payload?.HeapsTimedOut == true)
        {
            heapCol.Children.Add(new TextBlock { Text = loc.GetString("process.heapTimedOut"), Foreground = _semAmber, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
        }
        else if (payload?.HeapsLoaded != true)
        {
            heapCol.Children.Add(new TextBlock { Text = loc.GetString("process.heapNotLoaded"), Foreground = _textSecondary, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
            if (!proc.IsProtectedOrRestricted && payload is not null)
            {
                long ver = payload.SelectionVersion;
                heapCol.Children.Add(MakeButton(loc.GetString("process.runHeapWalk"), false, () => TriggerAsyncHeapWalk(proc.Pid, ver)));
            }
        }
        else if (heaps.Count == 0)
        {
            heapCol.Children.Add(new TextBlock { Text = loc.GetString("state.accessDenied"), Foreground = _textMuted, FontSize = 11.5 });
        }
        else
        {
            foreach (var h in heaps.Take(5))
            {
                heapCol.Children.Add(MakeLedgerRow($"{h.HeapKind} ({h.HeapIdHex})", $"{FmtBytes(h.CommittedBytes)} ({FmtNum(h.BlockCount)} blk)", _accentViolet));
            }
        }
        Grid.SetColumn(heapCol, 1);
        splitGrid.Children.Add(heapCol);

        var modCol = MakeSeamZone(useCanvasBg: true);
        modCol.Children.Add(MakeZoneHeader($"{loc.GetString("process.tab.modules").ToUpperInvariant()} ({modules.Count}) & {loc.GetString("process.tab.threads").ToUpperInvariant()} ({threads.Count})", payload?.ModulesTruncated == true ? loc.GetString("badges.partial") : "Toolhelp32"));
        if (isLoading)
            modCol.Children.Add(new TextBlock { Text = loc.GetString("state.loading"), Foreground = _textMuted, FontSize = 11.5 });
        foreach (var m in modules.Take(5))
        {
            modCol.Children.Add(MakeLedgerRow(m.ModuleName, $"{m.BaseAddressHex} · {FmtBytes(m.SizeBytes)}", _semEmerald));
        }
        Grid.SetColumn(modCol, 2);
        splitGrid.Children.Add(modCol);
        stack.Children.Add(splitGrid);

        // Lower Focused Lens (#21-#30)
        var lowerZone = MakeSeamZone();
        if (_activeRoute == "memory-strings")
        {
            lowerZone.Children.Add(MakeZoneHeader($"{loc.GetString("dialogs.consentStringsTitle").ToUpperInvariant()} ({loc.IsolateTechnicalToken("#30")})", "ASCII/UTF-8 + UTF-16LE"));
            if (!_host.StringsInspectionConsentGranted)
            {
                lowerZone.Children.Add(MakeInfoBanner(
                    loc.Format("dialogs.consentStringsBody", new Dictionary<string, object?> { ["processName"] = proc.Name, ["pid"] = proc.Pid }),
                    _semAmber));
                lowerZone.Children.Add(MakeButton(loc.GetString("dialogs.grantConsentButton"), true, () =>
                {
                    _host.StringsInspectionConsentGranted = true;
                    TriggerAsyncStringsSearch(proc.Pid, _stringSearchQuery);
                    RenderActiveWorkspace();
                }));
            }
            else
            {
                if (_activeStringsPid != proc.Pid || (_activeStringsResults is null && !_isSearchingStrings))
                {
                    TriggerAsyncStringsSearch(proc.Pid, _stringSearchQuery);
                }

                var strSearchRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
                var strBox = new TextBox
                {
                    Width = 340,
                    PlaceholderText = $"{loc.GetString("common.search")} ASCII / UTF-16LE…",
                    Text = _stringSearchQuery
                };
                _activeLocalSearchBox = strBox;
                AutomationProperties.SetName(strBox, loc.GetString("process.tab.strings"));
                strSearchRow.Children.Add(strBox);
                strSearchRow.Children.Add(MakeButton(loc.GetString("common.search"), true, () =>
                {
                    _stringSearchQuery = strBox.Text;
                    TriggerAsyncStringsSearch(proc.Pid, _stringSearchQuery);
                    RenderActiveWorkspace(preserveScroll: true);
                }));
                lowerZone.Children.Add(strSearchRow);

                strBox.KeyDown += (_, e) =>
                {
                    if (e.Key == VirtualKey.Enter)
                    {
                        _stringSearchQuery = strBox.Text;
                        TriggerAsyncStringsSearch(proc.Pid, _stringSearchQuery);
                        RenderActiveWorkspace(preserveScroll: true);
                    }
                };

                if (_isSearchingStrings)
                {
                    lowerZone.Children.Add(MakeInfoBanner(loc.GetString("process.searchingStrings"), _accentCyan));
                }
                else
                {
                    var strings = _activeStringsResults ?? [];
                    lowerZone.Children.Add(MakeZoneHeader($"{loc.GetString("process.tab.strings").ToUpperInvariant()} ({strings.Count})", "ReadProcessMemory (Async)"));
                    foreach (var s in strings)
                    {
                        lowerZone.Children.Add(MakeTableRow([s.AddressHex, s.EncodingName, s.RegionContext, s.Value], _textPrimary));
                    }
                }
            }
        }
        else if (_activeRoute == "heap-explorer")
        {
            lowerZone.Children.Add(MakeZoneHeader($"{loc.GetString("process.tab.heap").ToUpperInvariant()} ({heaps.Count})", "ISOLATED HEAP WORKER (3.0s)"));
            if (payload?.HeapsLoading == true)
            {
                lowerZone.Children.Add(MakeInfoBanner(loc.GetString("process.heapWalking"), _accentViolet));
            }
            else if (payload?.HeapsTimedOut == true)
            {
                lowerZone.Children.Add(MakeInfoBanner(loc.GetString("process.heapTimedOut"), _semAmber));
            }
            else if (payload?.HeapsLoaded != true && payload is not null && !proc.IsProtectedOrRestricted)
            {
                long ver = payload.SelectionVersion;
                lowerZone.Children.Add(MakeInfoBanner(loc.GetString("process.heapNotLoaded"), _accentViolet));
                lowerZone.Children.Add(MakeButton(loc.GetString("process.runHeapWalk"), true, () => TriggerAsyncHeapWalk(proc.Pid, ver)));
            }
            else
            {
                lowerZone.Children.Add(MakeTableRow([
                    loc.GetString("process.heapHandleId").ToUpperInvariant(),
                    loc.GetString("process.heapKind").ToUpperInvariant(),
                    loc.GetString("process.heapBlocks").ToUpperInvariant(),
                    loc.GetString("process.heapCommitted").ToUpperInvariant(),
                    loc.GetString("process.heapAllocated").ToUpperInvariant(),
                    loc.GetString("process.heapFree").ToUpperInvariant(),
                    loc.GetString("process.column.provenance").ToUpperInvariant()
                ], _textMuted));
                foreach (var h in heaps)
                {
                    lowerZone.Children.Add(MakeTableRow(
                        [h.HeapIdHex, h.HeapKind, FmtNum(h.BlockCount), FmtBytes(h.CommittedBytes), FmtBytes(h.AllocatedBytes), FmtBytes(h.FreeBytes), h.Provenance.ToString()],
                        _accentViolet));
                }
            }
        }
        else if (_activeRoute == "process-handles")
        {
            lowerZone.Children.Add(MakeZoneHeader($"{loc.GetString("process.handlesAndKernelPool").ToUpperInvariant()} ({loc.IsolateTechnicalToken("#29")})", loc.Format("process.totalHandles", new Dictionary<string, object?> { ["count"] = proc.HandleCount })));
            lowerZone.Children.Add(MakeLedgerRow(loc.GetString("process.column.handles"), FmtNum(proc.HandleCount), _accentCyan));
            lowerZone.Children.Add(MakeLedgerRow(loc.GetString("system.memory.nonpagedPool"), FmtBytes(proc.NonpagedPoolBytes), _semOrange));
            lowerZone.Children.Add(MakeLedgerRow(loc.GetString("system.memory.pagedPool"), FmtBytes(proc.PagedPoolBytes), _accentViolet));
            lowerZone.Children.Add(MakeLedgerRow(loc.GetString("process.column.pageFaults"), FmtNum(proc.PageFaultCount), _semAmber));
            lowerZone.Children.Add(MakeLedgerRow(loc.GetString("process.column.peakWorkingSet"), FmtBytes(proc.PeakWorkingSetBytes), _semEmerald));
        }
        else if (_activeRoute == "process-modules")
        {
            lowerZone.Children.Add(MakeZoneHeader($"{loc.GetString("process.tab.modules").ToUpperInvariant()} ({modules.Count})", $"{loc.GetString("process.fileVersion").ToUpperInvariant()} · {loc.GetString("process.filePath").ToUpperInvariant()}"));
            if (payload?.ModulesTruncated == true)
            {
                lowerZone.Children.Add(MakeInfoBanner(
                    loc.Format("process.modulesTruncated", new Dictionary<string, object?> { ["shown"] = modules.Count, ["total"] = payload.TotalModulesCount }),
                    _semAmber));
            }
            foreach (var m in modules.Take(80))
                lowerZone.Children.Add(MakeTableRow([m.ModuleName, m.BaseAddressHex, FmtBytes(m.SizeBytes), m.Version, m.FilePath], _textPrimary));
        }
        else if (_activeRoute is "stack-explorer" or "process-threads")
        {
            string tabHdr = _activeRoute == "stack-explorer" ? loc.GetString("process.tab.stack") : loc.GetString("process.tab.threads");
            lowerZone.Children.Add(MakeZoneHeader($"{tabHdr.ToUpperInvariant()} ({threads.Count})", $"{loc.GetString("process.threadState").ToUpperInvariant()} · {loc.GetString("process.waitReason").ToUpperInvariant()} · {loc.GetString("process.cpuTime").ToUpperInvariant()}"));
            foreach (var t in threads.Take(60))
                lowerZone.Children.Add(MakeTableRow([$"TID {t.ThreadId}", t.State, t.WaitReason, t.StartAddressHex, $"{FmtNum((long)t.CpuTime.TotalMilliseconds)} ms"], _textPrimary));
        }
        else
        {
            lowerZone.Children.Add(MakeZoneHeader($"{loc.GetString("virtual.addressSpaceMap").ToUpperInvariant()} ({regions.Count})", "VirtualQueryEx"));
            if (payload?.RegionsTruncated == true)
            {
                lowerZone.Children.Add(MakeInfoBanner(
                    loc.Format("process.regionsTruncated", new Dictionary<string, object?> { ["shown"] = regions.Count, ["total"] = payload.TotalRegionsCount }),
                    _semAmber));
            }
            foreach (var r in regions.Take(45))
            {
                ulong addr = r.BaseAddress;
                lowerZone.Children.Add(MakeTableRow(
                    [r.BaseAddressHex, r.EndAddressHex, FmtBytes(r.SizeBytes), r.State, r.RegionType, r.Protection, r.BackingModuleOrFile],
                    r.IsSuspiciousRwx ? _semCrimson : _textPrimary,
                    () => { _selectedRegionBase = addr; Navigate("virtual-region-details"); }));
            }
        }
        stack.Children.Add(lowerZone);

        return WrapConstrainedScrollViewer(stack, _bgCanvas);
    }

    // =========================================================================
    // 5. DIAGNOSTICS, CRASH LOGS & UI HANG WATCHDOG WORKSPACE (#93)
    // =========================================================================
    private FrameworkElement BuildDiagnosticsWorkspace()
    {
        var loc = _host.Localization;
        var zone = MakeSeamZone();
        zone.Children.Add(MakeZoneHeader(
            loc.GetString("diagnostics.title").ToUpperInvariant(),
            loc.Format("diagnostics.nativeAbi", new Dictionary<string, object?> { ["ver"] = _host.DiagnosticState.NativeAbiVersion })));
        zone.Children.Add(new TextBlock
        {
            Text = loc.GetString("diagnostics.subtitle"),
            FontFamily = _fontUi,
            FontSize = 12.5,
            Foreground = _textSecondary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });

        zone.Children.Add(MakeInfoBanner(loc.GetString("diagnostics.privacyNotice"), _semEmerald));

        // Watchdog & Diagnostics Runtime Telemetry (100% localized & truthful WatchdogState - F-019, F-027, R2-NEW-017)
        var wdColor = _hangWatchdog.State switch
        {
            WatchdogState.Running => _semEmerald,
            WatchdogState.HangDetected => _semCrimson,
            WatchdogState.Degraded or WatchdogState.SuspendedPower or WatchdogState.SuspendedDebugger => _semAmber,
            _ => _textMuted
        };
        zone.Children.Add(MakeLedgerRow(
            loc.GetString("diagnostics.watchdogStateLabel"),
            $"{GetLocalizedWatchdogState(_hangWatchdog.State)} · {loc.Format("diagnostics.hangsDetected", new Dictionary<string, object?> { ["count"] = _hangWatchdog.HangCount })}",
            wdColor));
        string diagDirDisplay = App.PrivacySafeCapture ? @"%LOCALAPPDATA%\RAMYXEN" : _host.DiagnosticsLogger.RootDirectory;
        string diagLogDisplay = App.PrivacySafeCapture ? @"%LOCALAPPDATA%\RAMYXEN\logs\ramyxen.jsonl" : _host.DiagnosticsLogger.GetCurrentLogFilePath();
        string diagReportsDisplay = App.PrivacySafeCapture ? @"%LOCALAPPDATA%\RAMYXEN\crash-reports" : _host.CrashReports.CrashReportsDirectory;
        zone.Children.Add(MakeLedgerRow(loc.GetString("diagnostics.localDir"), diagDirDisplay, _accentCyan));
        zone.Children.Add(MakeLedgerRow(loc.GetString("diagnostics.currentLog"), diagLogDisplay, _textSecondary));
        zone.Children.Add(MakeLedgerRow(loc.GetString("diagnostics.displayMetrics"), _host.DiagnosticState.DisplayMetrics, _accentViolet));
        zone.Children.Add(MakeLedgerRow(
            loc.GetString("diagnostics.iconCache"),
            loc.IsolateTechnicalToken($"{loc.GetString("diagnostics.iconCacheCached")}: {_host.ProcessIcons.CachedEntryCount} · {loc.GetString("diagnostics.iconCacheHits")}: {_host.ProcessIcons.CacheHits} · {loc.GetString("diagnostics.iconCacheExtracted")}: {_host.ProcessIcons.ExtractedCount}"),
            _accentCyan));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 12) };
        actions.Children.Add(MakeButton(loc.GetString("diagnostics.copyDetails"), true, () => CopyDiagnosticReportToClipboard()));
        actions.Children.Add(MakeButton(loc.GetString("diagnostics.saveReport"), false, () => _ = SaveDiagnosticBundleLocallyAsync(includeDump: false)));
        actions.Children.Add(MakeButton(loc.GetString("diagnostics.saveBundleWithDump"), false, () => _ = SaveDiagnosticBundleLocallyAsync(includeDump: true)));
        zone.Children.Add(new ScrollViewer { Content = actions, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });

        // Recorded Crash & Hang Reports — NEVER claim watchdog is active when WatchdogState != Running (R2-NEW-017)!
        var reports = _host.CrashReports.GetAllReports(15);
        zone.Children.Add(MakeZoneHeader($"{loc.GetString("diagnostics.recordedReports").ToUpperInvariant()} ({reports.Count})", diagReportsDisplay));
        if (reports.Count == 0)
        {
            string emptyMsg = _hangWatchdog.State == WatchdogState.Running
                ? loc.GetString("diagnostics.noReports")
                : loc.Format("diagnostics.noReportsState", new Dictionary<string, object?> { ["state"] = GetLocalizedWatchdogState(_hangWatchdog.State) });
            zone.Children.Add(MakeInfoBanner(emptyMsg, _hangWatchdog.State == WatchdogState.Running ? _accentCyan : _semAmber));
        }
        else
        {
            foreach (var r in reports)
            {
                var rep = r;
                zone.Children.Add(MakeTopologyCard(
                    $"[{rep.Kind}] {rep.ReportId} ({rep.TimestampUtc:yyyy-MM-dd HH:mm:ss} UTC)",
                    $"{rep.Source} · View #{rep.ActiveViewId} · {rep.DisplayMetrics}",
                    rep.Summary,
                    rep.IsFatal ? _semCrimson : _semAmber,
                    () => CopyDiagnosticReportToClipboard(rep),
                    isTechnicalTitle: true));
            }
        }

        // Structured JSONL Logs Ring Buffer
        var recentLogs = _host.DiagnosticsLogger.GetRecentEntries(40).Reverse().ToList();
        zone.Children.Add(MakeZoneHeader($"{loc.GetString("diagnostics.recentLogs").ToUpperInvariant()} ({recentLogs.Count})", loc.GetString("diagnostics.redactedJsonl")));
        foreach (var log in recentLogs)
        {
            var fg = log.Level switch
            {
                DiagnosticSeverity.Fatal or DiagnosticSeverity.Error => _semCrimson,
                DiagnosticSeverity.Warning => _semAmber,
                _ => _textSecondary
            };
            zone.Children.Add(MakeTableRow(
                [$"{log.TimestampUtc:HH:mm:ss.fff}", log.Level.ToString().ToUpperInvariant(), log.Category, log.Message],
                fg));
        }

        return WrapConstrainedScrollViewer(zone, _bgZone);
    }

    private void CopyDiagnosticReportToClipboard(DiagnosticReportRecord? specific = null)
    {
        var loc = _host.Localization;
        try
        {
            string text = _host.CrashReports.BuildCopyableReportText(specific);
            var pkg = new DataPackage();
            pkg.SetText(text);
            Clipboard.SetContent(pkg);
            _lastStatusMessage = loc.GetString("diagnostics.copiedToast");
            UpdateStatusBarOnly();
        }
        catch (Exception ex)
        {
            _host.DiagnosticsLogger.Warning("Diagnostics", $"Clipboard copy failed: {ex.Message}");
            _lastStatusMessage = loc.Format("dialogs.clipboardError", new Dictionary<string, object?> { ["error"] = ex.Message });
            UpdateStatusBarOnly();
        }
    }

    private async Task SaveDiagnosticBundleLocallyAsync(bool includeDump, DiagnosticReportRecord? specific = null)
    {
        var loc = _host.Localization;
        try
        {
            string exportDir = Path.Combine(_host.DiagnosticsLogger.RootDirectory, "exports");
            Directory.CreateDirectory(exportDir);

            // Explicit user consent dialog BEFORE capturing optional MiniDump (F-006)
            if (includeDump)
            {
                bool consented = await ConfirmMiniDumpConsentAsync(_host.CrashReports.DumpsDirectory);
                if (!consented) return;
            }

            string fileName = $"RAMYXEN_Diagnostics_{DateTime.Now:yyyyMMdd_HHmmss_fff}.zip";
            string destPath = Path.Combine(exportDir, fileName);

            _lastStatusMessage = loc.GetString("diagnostics.exporting");
            UpdateStatusBarOnly();

            await _host.CrashReports.ExportDiagnosticBundleAsync(destPath, includeConsentGatedMinidump: includeDump, specificReport: specific);
            _lastStatusMessage = loc.Format("diagnostics.savedToast", new Dictionary<string, object?> { ["path"] = destPath });
            UpdateStatusBarOnly();
        }
        catch (Exception ex)
        {
            _host.DiagnosticsLogger.Error("Diagnostics", "Failed to save diagnostic bundle.", ex);
            _lastStatusMessage = loc.Format("dialogs.exportError", new Dictionary<string, object?> { ["error"] = ex.Message });
            UpdateStatusBarOnly();
        }
    }

    // =========================================================================
    // 6. VIRTUAL MEMORY EXPLORER & CALIBRATED HEX INSPECTOR (#31-#33) (ASYNC - F-002)
    // =========================================================================
    private FrameworkElement BuildVirtualMemoryExplorer()
    {
        var loc = _host.Localization;
        var proc = _host.GetSelectedProcess();
        if (proc == null) return MakeSeamZone();

        if (_activeInspection is null || _activeInspection.Pid != proc.Pid)
        {
            if (_lastRequestedInspectionPid != proc.Pid)
                TriggerAsyncProcessInspection(proc, alsoLoadHeaps: false);
        }

        var payload = _activeInspection is not null && _activeInspection.Pid == proc.Pid ? _activeInspection : null;
        var regions = payload?.Regions ?? [];
        bool isLoading = payload?.IsLoading ?? true;
        var activeReg = regions.FirstOrDefault(r => r.BaseAddress == _selectedRegionBase)
                     ?? regions.FirstOrDefault(r => r.State == "MEM_COMMIT")
                     ?? regions.FirstOrDefault();

        if (activeReg is not null && (_activeHexPid != proc.Pid || _activeHexAddress != activeReg.BaseAddress) && !_isLoadingHex)
        {
            TriggerAsyncHexRead(proc.Pid, activeReg.BaseAddress);
        }

        var split = new Grid { ColumnSpacing = 1, Background = _seamBrush };
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = MakeSeamZone();
        left.Children.Add(MakeZoneHeader($"{loc.GetString("virtual.addressSpaceMap").ToUpperInvariant()} — {proc.Name} (PID {proc.Pid})", $"{regions.Count}"));
        if (isLoading)
            left.Children.Add(MakeInfoBanner(loc.GetString("process.loadingDetails"), _accentCyan));
        foreach (var r in regions.Take(60))
        {
            ulong addr = r.BaseAddress;
            bool sel = activeReg?.BaseAddress == addr;
            left.Children.Add(MakeTableRow(
                [r.BaseAddressHex, FmtBytes(r.SizeBytes), r.State, r.RegionType, r.Protection],
                r.IsSuspiciousRwx ? _semCrimson : sel ? _accentCyan : _textPrimary,
                () => { _selectedRegionBase = addr; TriggerAsyncHexRead(proc.Pid, addr); RenderActiveWorkspace(preserveScroll: true); }));
        }

        var leftScroll = new ScrollViewer { Content = left, Background = _bgZone };
        Grid.SetColumn(leftScroll, 0);
        split.Children.Add(leftScroll);

        var right = MakeSeamZone(useCanvasBg: true);
        if (activeReg != null)
        {
            right.Children.Add(MakeZoneHeader($"{loc.GetString("virtual.hexInspector")} ({activeReg.BaseAddressHex})", activeReg.Protection));
            right.Children.Add(MakeLedgerRow(loc.GetString("virtual.baseEndAddress"), $"{activeReg.BaseAddressHex} — {activeReg.EndAddressHex}", _accentCyan));
            right.Children.Add(MakeLedgerRow(loc.GetString("virtual.sizeState"), $"{FmtBytes(activeReg.SizeBytes)} · {activeReg.State}", _textPrimary));
            right.Children.Add(MakeLedgerRow(loc.GetString("virtual.backingFile"), $"{activeReg.RegionType} · {activeReg.BackingModuleOrFile}", _textSecondary));

            string hexText = _isLoadingHex || _activeHexPid != proc.Pid || _activeHexAddress != activeReg.BaseAddress
                ? loc.GetString("virtual.loadingHex")
                : FormatHexDump(activeReg.BaseAddress, _activeHexBytes ?? [], loc.GetString("virtual.hexUnreadable"));
            right.Children.Add(new TextBlock
            {
                Text = hexText,
                FontFamily = _fontMono,
                FontSize = 11.5,
                Foreground = _semEmerald,
                FlowDirection = FlowDirection.LeftToRight,
                Margin = new Thickness(0, 10, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
        }

        var rightScroll = new ScrollViewer { Content = right, Background = _bgCanvas };
        Grid.SetColumn(rightScroll, 1);
        split.Children.Add(rightScroll);

        return split;
    }

    // =========================================================================
    // 7. PHYSICAL MEMORY EXPLORER & STANDBY PRIORITY P0-P7 (#34-#41)
    // =========================================================================
    private FrameworkElement BuildPhysicalMemoryExplorer(SystemMemoryTelemetry sys)
    {
        var loc = _host.Localization;
        var pageLists = _host.Monitor.LatestPageLists;
        var zone = MakeSeamZone();
        string provBadge = pageLists.HasExactKernelLists ? loc.GetString("badges.live") : loc.GetString("badges.partial");
        zone.Children.Add(MakeZoneHeader($"{loc.GetString("physical.title").ToUpperInvariant()} ({loc.IsolateTechnicalToken("#34–#41")})", provBadge));

        zone.Children.Add(MakeLedgerRow(loc.GetString("system.memory.installed"), FmtBytes(sys.InstalledPhysicalBytes), _textPrimary));
        zone.Children.Add(MakeLedgerRow(loc.GetString("system.memory.inUse"), FmtBytes(sys.InUsePhysicalBytes), _accentCyan));
        zone.Children.Add(MakeLedgerRow(loc.GetString("system.memory.available"), FmtBytes(sys.AvailablePhysicalBytes), _semEmerald));
        zone.Children.Add(MakeLedgerRow(loc.GetString("physical.list.standby"), FmtBytes(sys.StandbyCacheBytes), _semEmerald));
        zone.Children.Add(MakeLedgerRow(loc.GetString("physical.list.modified"), FmtBytes(sys.ModifiedBytes), _semAmber));
        zone.Children.Add(MakeLedgerRow(loc.GetString("physical.list.modifiedNoWrite"), FmtBytes(pageLists.ModifiedNoWritePageBytes), _semAmber));
        zone.Children.Add(MakeLedgerRow(loc.GetString("physical.list.zeroed"), FmtBytes(pageLists.ZeroPageBytes), _semEmerald));
        zone.Children.Add(MakeLedgerRow(loc.GetString("physical.list.free"), FmtBytes(pageLists.FreePageBytes), _semEmerald));
        zone.Children.Add(MakeLedgerRow(loc.GetString("system.memory.hardwareReserved"), FmtBytes(sys.HardwareReservedBytes), _textMuted));
        zone.Children.Add(MakeLedgerRow(loc.GetString("system.memory.nonpagedPool"), FmtBytes(sys.KernelNonpagedBytes), _semOrange));
        zone.Children.Add(MakeLedgerRow(loc.GetString("system.memory.pagedPool"), FmtBytes(sys.KernelPagedBytes), _textPrimary));
        zone.Children.Add(MakeLedgerRow(loc.GetString("physical.pfnPageSizeFrames"), $"{sys.PageSizeBytes} B ({loc.FormatPlural("physical.pageCount", (int)Math.Min(int.MaxValue, sys.TotalPhysicalBytes / Math.Max(1, sys.PageSizeBytes)))})", _accentViolet));

        zone.Children.Add(MakeZoneHeader(loc.GetString("physical.prioritySummary").ToUpperInvariant(), pageLists.HasExactKernelLists ? "NtQuerySystemInformation" : loc.GetString("badges.partial")));
        for (int p = 0; p < pageLists.StandbyPriorityBytes.Count; p++)
        {
            ulong prioBytes = pageLists.StandbyPriorityBytes[p];
            ulong pages = prioBytes / Math.Max(1UL, sys.PageSizeBytes);
            zone.Children.Add(MakeLedgerRow(
                loc.Format("physical.standbyPriorityLabel", new Dictionary<string, object?> { ["p"] = p }),
                $"{FmtBytes(prioBytes)} ({loc.FormatPlural("physical.pageCount", (int)Math.Min(int.MaxValue, pages))})",
                p >= 5 ? _accentCyan : _semEmerald));
        }

        zone.Children.Add(MakeInfoBanner(loc.GetString("physical.provenanceNotice"), _semAmber));

        return WrapConstrainedScrollViewer(zone, _bgZone);
    }

    // =========================================================================
    // 8. MEMORY LEAK & EXHAUSTION INVESTIGATION + ALERTS WORKSPACE (#76-#79)
    // =========================================================================
    private FrameworkElement BuildLeakAndAlertsWorkspace(SystemMemoryTelemetry sys)
    {
        var loc = _host.Localization;
        var split = new Grid { ColumnSpacing = 1, Background = _seamBrush };
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });

        var left = MakeSeamZone();
        var exhaustion = _host.Monitor.LeakEngine.DetectSystemExhaustion(sys);
        left.Children.Add(MakeZoneHeader(loc.GetString("overview.exhaustionTriage"), exhaustion.ToString().ToUpperInvariant()));

        foreach (var c in _host.Monitor.ActiveInvestigations)
        {
            uint pid = c.TargetPid;
            var col = c.Classification is LeakClassification.StrongLeakEvidence or LeakClassification.ConfirmedLeak ? _semCrimson
                    : c.Classification == LeakClassification.SuspectedLeak ? _semAmber : _accentCyan;
            left.Children.Add(MakeTopologyCard(
                $"[{c.Classification}] {c.TargetProcessName} (PID {c.TargetPid}) — {c.ConfidencePercent}%",
                $"{c.Domain}  |  {c.SuspectedComponent}",
                string.Join("\n", c.EvidenceItems),
                col,
                () => SelectProcessAndNavigate(pid, "process-deep-dive"),
                isTechnicalTitle: true));
        }

        var leftScroll = new ScrollViewer { Content = left, Background = _bgZone };
        Grid.SetColumn(leftScroll, 0);
        split.Children.Add(leftScroll);

        var right = MakeSeamZone(useCanvasBg: true);
        var alerts = _host.Monitor.GetAlerts();
        int openCount = alerts.Count(a => a.State == "Open");
        right.Children.Add(MakeZoneHeader(loc.GetString("alerts.title").ToUpperInvariant(), loc.FormatPlural("alerts.openCount", openCount)));

        var ruleBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 10) };
        ruleBar.Children.Add(MakeButton($"+ {loc.GetString("alerts.createRule")} (Commit > 2 GiB)", true, () =>
        {
            var proc = _host.GetSelectedProcess();
            string target = proc != null ? $"{proc.Name} (PID {proc.Pid})" : "System Commit Charge";
            _host.Monitor.AddCustomAlertRule("Custom Private Commit Threshold Rule", target, 2UL * 1024 * 1024 * 1024);
            RenderActiveWorkspace();
        }));
        right.Children.Add(ruleBar);

        foreach (var a in alerts)
        {
            string alertId = a.AlertId;
            right.Children.Add(MakeTopologyCard(
                $"{a.AlertId}: {a.Title} ({a.Severity} · {a.State})",
                $"{a.TargetComponent}  |  {a.MeasuredValue}",
                $"{a.EvidenceSummary}\n{a.SuggestedAction}",
                a.Severity == "Critical" ? _semCrimson : _semAmber,
                () =>
                {
                    _host.Monitor.UpdateAlertState(alertId, "Acknowledged");
                    RenderActiveWorkspace();
                }));

            var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 8) };
            actionRow.Children.Add(MakeButton(loc.GetString("alerts.acknowledge"), false, () => { _host.Monitor.UpdateAlertState(alertId, "Acknowledged"); RenderActiveWorkspace(); }));
            actionRow.Children.Add(MakeButton(loc.GetString("alerts.resolve"), false, () => { _host.Monitor.UpdateAlertState(alertId, "Resolved"); RenderActiveWorkspace(); }));
            actionRow.Children.Add(MakeButton(loc.GetString("alerts.falsePositive"), false, () => { _host.Monitor.UpdateAlertState(alertId, "False Positive"); RenderActiveWorkspace(); }));
            right.Children.Add(actionRow);
        }

        var rightScroll = new ScrollViewer { Content = right, Background = _bgCanvas };
        Grid.SetColumn(rightScroll, 1);
        split.Children.Add(rightScroll);

        return split;
    }

    // =========================================================================
    // 9. ALLOCATION & RUNTIME PROFILER (#42-#50) (ASYNC — ZERO UI BLOCKING - F-002)
    // =========================================================================
    private FrameworkElement BuildProfilerWorkspace()
    {
        var loc = _host.Localization;
        var proc = _host.GetSelectedProcess();
        uint pid = proc?.Pid ?? (uint)Environment.ProcessId;
        string pName = proc?.Name ?? "RAMYXEN.exe";

        if (_activeProfilerPayload is null || _activeProfilerPayload.Pid != pid)
        {
            TriggerAsyncProfilerLoad(pid, pName);
        }

        var profPayload = _activeProfilerPayload?.Pid == pid ? _activeProfilerPayload : null;
        var gc = profPayload?.GcTelemetry;
        var hotspots = profPayload?.Hotspots ?? [];

        var zone = MakeSeamZone();
        zone.Children.Add(MakeZoneHeader($"{loc.GetString("profiler.title").ToUpperInvariant()} — {pName} (PID {pid})", gc?.Provenance.ToString().ToUpperInvariant() ?? loc.GetString("state.loading")));

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 10) };
        btnRow.Children.Add(MakeButton(loc.GetString("profiler.startRecording"), true, () =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var s = await _host.Etw.StartSessionAsync(pid, pName, EtwSessionManager.SupportedProviders.Select(p => p.Name).ToList());
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_isClosed) return;
                        _lastStatusMessage = s.StatusMessage;
                        RenderActiveWorkspace(preserveScroll: true);
                    });
                }
                catch (Exception ex)
                {
                    _host.DiagnosticsLogger.Error("ETW", "Async StartSessionAsync failed.", ex);
                }
            });
        }));
        btnRow.Children.Add(MakeButton(loc.GetString("profiler.stopRecording"), false, () =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var s = await _host.Etw.StopSessionAsync();
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_isClosed) return;
                        if (s != null) _lastStatusMessage = s.StatusMessage;
                        RenderActiveWorkspace(preserveScroll: true);
                    });
                }
                catch (Exception ex)
                {
                    _host.DiagnosticsLogger.Error("ETW", "Async StopSessionAsync failed.", ex);
                }
            });
        }));
        btnRow.Children.Add(MakeButton(loc.GetString("common.refresh"), false, () =>
        {
            _activeProfilerPayload = null;
            TriggerAsyncProfilerLoad(pid, pName);
            RenderActiveWorkspace();
        }));
        zone.Children.Add(btnRow);

        if (_host.Etw.CurrentSession != null)
        {
            var s = _host.Etw.CurrentSession;
            zone.Children.Add(MakeInfoBanner(
                $"Session {s.SessionId} ({(s.IsRecording ? "RECORDING" : "STOPPED")}) | {s.TargetProcessName} | {FmtNum(s.EventsCaptured)} | Lost: {s.LostEvents} | {s.BufferUsagePercent}% | {s.StatusMessage}",
                s.IsRecording ? _accentViolet : _semAmber));
        }

        zone.Children.Add(MakeZoneHeader(loc.GetString("profiler.providers").ToUpperInvariant(), loc.GetString("profiler.etwWin32Badge")));
        foreach (var ep in EtwSessionManager.SupportedProviders)
        {
            zone.Children.Add(MakeTableRow(
                [ep.Name, $"{{{ep.ProviderGuid}}}", ep.EventTypes, ep.RequiresAdministrator ? loc.GetString("badges.adminRequired") : loc.GetString("badges.runtimeSpecific")],
                _textSecondary));
        }

        if (gc is null)
        {
            zone.Children.Add(MakeInfoBanner(loc.GetString("profiler.loadingNotice"), _accentCyan));
        }
        else
        {
            zone.Children.Add(MakeZoneHeader(loc.GetString("profiler.gcAnalysis").ToUpperInvariant(), gc.Provenance.ToString().ToUpperInvariant()));
            if (!string.IsNullOrWhiteSpace(gc.RuntimeStatusNote))
            {
                zone.Children.Add(MakeInfoBanner(gc.RuntimeStatusNote, gc.Provenance == DataSourceProvenance.RuntimeSpecific ? _semEmerald : _semAmber));
            }
            zone.Children.Add(MakeLedgerRow(loc.GetString("profiler.gcGenerations"), $"{FmtBytes(gc.Gen0SizeBytes)} / {FmtBytes(gc.Gen1SizeBytes)} / {FmtBytes(gc.Gen2SizeBytes)}", _accentCyan));
            zone.Children.Add(MakeLedgerRow(loc.GetString("profiler.gcLohPoh"), $"{FmtBytes(gc.LohSizeBytes)} / {FmtBytes(gc.PohSizeBytes)}", _accentViolet));
            zone.Children.Add(MakeLedgerRow(loc.GetString("profiler.gcCommittedCumulative"), $"{FmtBytes(gc.TotalCommittedGcBytes)} / {FmtBytes(gc.TotalAllocatedBytesCumulative)}", _semEmerald));
            zone.Children.Add(MakeLedgerRow(loc.GetString("profiler.gcCollections"), $"{gc.Gen0Collections} / {gc.Gen1Collections} / {gc.Gen2Collections}  ({gc.PauseTimePercentage}% · {gc.FragmentationPercent}%)", _textPrimary));

            zone.Children.Add(MakeZoneHeader($"{loc.GetString("profiler.hotspots").ToUpperInvariant()} ({hotspots.Count})", "VIRTUALQUERYEX + ON-DEMAND HEAP"));
            foreach (var h in hotspots)
            {
                zone.Children.Add(MakeTableRow([h.AllocationSiteOrCategory, h.SourceProvider, FmtBytes(h.SurvivingBytes), $"{h.SharePercent}%", h.SymbolStatus], _textPrimary));
            }
        }

        return WrapConstrainedScrollViewer(zone, _bgZone);
    }

    // =========================================================================
    // 10. SNAPSHOTS & DIFFERENTIAL COMPARISON (#51-#58)
    // =========================================================================
    private FrameworkElement BuildSnapshotsWorkspace(SystemMemoryTelemetry sys)
    {
        var loc = _host.Localization;
        var snaps = _host.Snapshots.GetSnapshots();
        var zone = MakeSeamZone();
        zone.Children.Add(MakeZoneHeader($"{loc.GetString("snapshots.title").ToUpperInvariant()} ({loc.FormatPlural("snapshots.count", snaps.Count)})", _host.Snapshots.VaultDirectory));

        var actionBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 10) };
        actionBar.Children.Add(MakeButton($"+ {loc.GetString("snapshots.create")}", true, () =>
        {
            var rec = _host.Snapshots.CreateTelemetrySnapshot($"Snapshot {DateTime.Now:HH:mm:ss}", sys, _host.Monitor.LatestProcesses);
            _lastStatusMessage = $"{loc.GetString("snapshots.create")}: {rec.SnapshotId}";
            RenderActiveWorkspace();
        }));

        if (_pendingDeleteSnapshotId != null)
        {
            string delId = _pendingDeleteSnapshotId;
            actionBar.Children.Add(MakeButton($"{loc.GetString("dialogs.confirmDelete")} ({delId})", true, () =>
            {
                _host.Snapshots.DeleteSnapshotPermanently(delId);
                _pendingDeleteSnapshotId = null;
                _lastStatusMessage = $"{loc.GetString("dialogs.confirmDelete")}: {delId}";
                RenderActiveWorkspace();
            }));
            actionBar.Children.Add(MakeButton(loc.GetString("common.cancel"), false, () =>
            {
                _pendingDeleteSnapshotId = null;
                RenderActiveWorkspace();
            }));
        }
        zone.Children.Add(actionBar);

        if (snaps.Count >= 2)
        {
            var diff = _host.Snapshots.CompareSnapshots(snaps[^1], snaps[0]);

            zone.Children.Add(MakeInfoBanner(
                $"{loc.GetString("snapshots.compare")}: {loc.IsolateTechnicalToken($"{diff.Baseline.SnapshotId} ⇄ {diff.Target.SnapshotId}")}  |  Δ {loc.GetString("system.memory.inUse")}: {loc.IsolateTechnicalToken(FmtDelta(diff.PhysicalInUseDeltaBytes))}  |  Δ {loc.GetString("system.memory.committed")}: {loc.IsolateTechnicalToken(FmtDelta(diff.CommitTotalDeltaBytes))}  |  Δ {loc.GetString("system.memory.nonpagedPool")}: {loc.IsolateTechnicalToken(FmtDelta(diff.NonpagedPoolDeltaBytes))}",
                _accentViolet));

            foreach (var pd in diff.ProcessDiffs.Take(8))
            {
                zone.Children.Add(MakeTableRow(
                    [$"{pd.ProcessName} (PID {pd.Pid})", $"WS Δ: {FmtDelta(pd.WorkingSetDeltaBytes)}", $"Commit Δ: {FmtDelta(pd.CommitDeltaBytes)}", $"Handles Δ: {pd.HandleDelta:+0;-0;0}", pd.Status],
                    pd.CommitDeltaBytes > 0 ? _semAmber : _semEmerald));
            }
        }

        foreach (var s in snaps)
        {
            string id = s.SnapshotId;
            zone.Children.Add(MakeTableRow(
                [s.SnapshotId, s.Name, s.Kind.ToString(), $"{s.CreatedUtc:yyyy-MM-dd HH:mm:ss}", $"SHA-256: {s.Sha256Hash[..12]}…", $"{FmtNum(s.FileSizeBytes)} B"],
                _pendingDeleteSnapshotId == id ? _semCrimson : _accentCyan,
                () => { _pendingDeleteSnapshotId = id; RenderActiveWorkspace(); }));
        }

        return WrapConstrainedScrollViewer(zone, _bgZone);
    }

    // =========================================================================
    // 11. MEMORY FORENSICS WORKBENCH (#59-#75) (ASYNC LIVE & DUMP - F-002, F-006)
    // =========================================================================
    private FrameworkElement BuildForensicsWorkspace()
    {
        var loc = _host.Localization;
        var proc = _host.GetSelectedProcess();
        uint pid = proc?.Pid ?? (uint)Environment.ProcessId;
        string pName = proc?.Name ?? "RAMYXEN.exe";

        if (_activeForensicsLive is null || _activeForensicsLive.Pid != pid)
        {
            TriggerAsyncForensicsLive(pid, pName);
        }

        var zone = MakeSeamZone();
        zone.Children.Add(MakeZoneHeader($"{loc.GetString("forensics.title").ToUpperInvariant()} — {pName} (PID {pid})", loc.GetString("badges.live")));
        zone.Children.Add(MakeInfoBanner(loc.GetString("forensics.yaraDisclaimer"), _semAmber));

        string dumpDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RAMYXEN", "Dumps");
        Directory.CreateDirectory(dumpDir);

        var actRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 10) };
        actRow.Children.Add(MakeButton($"{loc.GetString("forensics.importDump")} / {loc.GetString("process.captureMiniDump")}", true, () =>
            _ = CaptureProcessMiniDumpWithConsentAsync(pid, pName)));

        foreach (string existingDump in Directory.GetFiles(dumpDir, "*.dmp").OrderByDescending(File.GetLastWriteTimeUtc).Take(3))
        {
            string dPath = existingDump;
            actRow.Children.Add(MakeButton(Path.GetFileName(dPath), _activeOfflineDumpPath == dPath, () =>
            {
                _activeOfflineDumpPath = dPath;
                _activeForensicsDump = null;
                TriggerAsyncForensicsDump(dPath);
                RenderActiveWorkspace();
            }));
        }
        zone.Children.Add(new ScrollViewer { Content = actRow, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });

        if (!string.IsNullOrWhiteSpace(_activeOfflineDumpPath) && File.Exists(_activeOfflineDumpPath))
        {
            if (_activeForensicsDump is null || !string.Equals(_activeForensicsDump.Source.FilePathOrPid, _activeOfflineDumpPath, StringComparison.OrdinalIgnoreCase))
            {
                TriggerAsyncForensicsDump(_activeOfflineDumpPath);
                zone.Children.Add(MakeInfoBanner(loc.GetString("forensics.loadingDump"), _accentViolet));
            }
            else
            {
                var dumpSrc = _activeForensicsDump.Source;
                zone.Children.Add(MakeZoneHeader($"{loc.GetString("forensics.importDump").ToUpperInvariant()} — {dumpSrc.SourceName}", dumpSrc.SourceKind));
                zone.Children.Add(MakeLedgerRow(loc.GetString("forensics.archSize"), $"{dumpSrc.DetectedArchitecture} · {FmtBytes((ulong)dumpSrc.SizeBytes)}", _accentCyan));
                zone.Children.Add(MakeLedgerRow(loc.GetString("forensics.sha256Digest"), dumpSrc.IntegritySha256, _semEmerald));
                foreach (var df in _activeForensicsDump.Findings)
                {
                    zone.Children.Add(MakeTopologyCard(
                        $"{df.FindingId}: {df.FindingType} [{df.Category} · {df.ConfidencePercent}%]",
                        $"{df.TargetProcessOrModule} @ {df.AddressHex}",
                        $"{df.EvidenceDetail}\n{df.LimitationNote}",
                        df.Severity == "Critical" ? _semCrimson : _accentViolet,
                        () => { }));
                }
                zone.Children.Add(new TextBlock
                {
                    Text = FormatHexDump(0, _activeForensicsDump.PreviewBytes, loc.GetString("virtual.hexUnreadable")),
                    FontFamily = _fontMono,
                    FontSize = 11,
                    Foreground = _semEmerald,
                    FlowDirection = FlowDirection.LeftToRight,
                    Margin = new Thickness(0, 4, 0, 10)
                });
            }
        }

        zone.Children.Add(MakeZoneHeader($"{loc.GetString("forensics.vadAnalysis").ToUpperInvariant()} — {pName} (PID {pid})", "VirtualQueryEx + PE\\0\\0 (Async)"));
        if (_activeForensicsLive is null || _activeForensicsLive.Pid != pid)
        {
            zone.Children.Add(MakeInfoBanner(loc.GetString("forensics.loadingLive"), _accentCyan));
        }
        else
        {
            foreach (var f in _activeForensicsLive.Findings)
            {
                zone.Children.Add(MakeTopologyCard(
                    $"{f.FindingId}: {f.FindingType} [{f.Category} · {f.ConfidencePercent}%]",
                    $"{f.TargetProcessOrModule} @ {f.AddressHex} ({FmtBytes(f.SizeBytes)})",
                    $"{f.EvidenceDetail}\n{f.LimitationNote}",
                    f.Severity == "Critical" ? _semCrimson : f.Severity == "Warning" ? _semAmber : _semEmerald,
                    () => { _selectedRegionBase = f.Address; Navigate("virtual-region-details"); }));
            }
        }

        return WrapConstrainedScrollViewer(zone, _bgZone);
    }

    // =========================================================================
    // 12. REPORTS & SANITIZED EXPORT (#80-#83)
    // =========================================================================
    private FrameworkElement BuildReportsWorkspace(SystemMemoryTelemetry sys)
    {
        var loc = _host.Localization;
        var zone = MakeSeamZone();
        zone.Children.Add(MakeZoneHeader(loc.GetString("reports.title").ToUpperInvariant(), "CSV / JSON / HTML / PDF 1.4"));
        zone.Children.Add(MakeInfoBanner(loc.GetString("reports.redactNotice"), _semEmerald));

        string outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RAMYXEN", "Reports");
        Directory.CreateDirectory(outDir);
        bool redact = _host.Settings.Current.RedactPathsOnExport;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 8, 0, 14) };
        void RunAsyncReportExport(Func<string> exportAction)
        {
            _lastStatusMessage = loc.GetString("state.loading");
            UpdateStatusBarOnly();
            _ = Task.Run(() =>
            {
                try
                {
                    string savedPath = exportAction();
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_isClosed) return;
                        _lastStatusMessage = loc.Format("reports.savedToast", new Dictionary<string, object?> { ["path"] = loc.IsolateTechnicalToken(savedPath) });
                        UpdateStatusBarOnly();
                    });
                }
                catch (Exception ex)
                {
                    _host.DiagnosticsLogger.Error("Reports", "Report export failed.", ex);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_isClosed) return;
                        _lastStatusMessage = loc.Format("dialogs.exportError", new Dictionary<string, object?> { ["error"] = ex.Message });
                        UpdateStatusBarOnly();
                    });
                }
            });
        }

        row.Children.Add(MakeButton(loc.GetString("reports.exportCsv"), true, () =>
            RunAsyncReportExport(() => _host.Reporter.ExportCsv(Path.Combine(outDir, $"RAMYXEN_Report_{DateTime.Now:yyyyMMdd_HHmmss_fff}.csv"), sys, _host.Monitor.LatestProcesses, redact))));
        row.Children.Add(MakeButton(loc.GetString("reports.exportJson"), true, () =>
            RunAsyncReportExport(() => _host.Reporter.ExportJson(Path.Combine(outDir, $"RAMYXEN_Report_{DateTime.Now:yyyyMMdd_HHmmss_fff}.json"), sys, _host.Monitor.LatestProcesses, _host.Monitor.ActiveInvestigations, redact))));
        row.Children.Add(MakeButton(loc.GetString("reports.exportHtml"), true, () =>
            RunAsyncReportExport(() => _host.Reporter.ExportHtml(Path.Combine(outDir, $"RAMYXEN_Report_{DateTime.Now:yyyyMMdd_HHmmss_fff}.html"), sys, _host.Monitor.LatestProcesses, _host.Monitor.ActiveInvestigations, redact))));
        row.Children.Add(MakeButton(loc.GetString("reports.exportPdf"), true, () =>
            RunAsyncReportExport(() => _host.Reporter.ExportPdf(Path.Combine(outDir, $"RAMYXEN_Report_{DateTime.Now:yyyyMMdd_HHmmss_fff}.pdf"), sys, _host.Monitor.LatestProcesses, _host.Monitor.ActiveInvestigations, redact))));
        zone.Children.Add(new ScrollViewer { Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        zone.Children.Add(MakeLedgerRow(loc.GetString("reports.outputDir"), outDir, _accentCyan));
        zone.Children.Add(MakeLedgerRow(loc.GetString("reports.redactionStatus"), redact ? loc.GetString("reports.enabled") : loc.GetString("reports.disabled"), redact ? _semEmerald : _semAmber));

        return WrapConstrainedScrollViewer(zone, _bgZone);
    }

    // =========================================================================
    // 13. DEDICATED ONBOARDING, COMPATIBILITY & SHELL ROUTES (#01-#13) (F-028)
    // =========================================================================
    private FrameworkElement BuildOnboardingAndShellWorkspace(SystemMemoryTelemetry sys)
    {
        var loc = _host.Localization;
        var zone = MakeSeamZone();
        zone.Children.Add(MakeZoneHeader(loc.GetString("onboarding.shellTitle").ToUpperInvariant(), DestinationRegistry.GetLocalizedTitle(loc, _activeRoute)));

        string routeDesc = DestinationRegistry.GetLocalizedDescription(loc, _activeRoute);
        if (!string.IsNullOrWhiteSpace(routeDesc))
            zone.Children.Add(MakeInfoBanner(routeDesc, _accentCyan));

        zone.Children.Add(MakeLedgerRow(loc.GetString("onboarding.runtimeStatus"), loc.Format("onboarding.abiVerified", new Dictionary<string, object?> { ["ver"] = _host.DiagnosticState.NativeAbiVersion }), _semEmerald));
        zone.Children.Add(MakeLedgerRow(loc.GetString("onboarding.elevationPosture"), _isAdministrator ? loc.GetString("status.adminMode") : loc.GetString("status.standardUser"), _isAdministrator ? _semEmerald : _semAmber));
        zone.Children.Add(MakeLedgerRow(loc.GetString("diagnostics.displayMetrics"), _host.DiagnosticState.DisplayMetrics, _accentCyan));
        zone.Children.Add(MakeLedgerRow(loc.GetString("system.memory.installed"), FmtBytes(sys.InstalledPhysicalBytes), _textPrimary));

        var navRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 10, 0, 0) };
        navRow.Children.Add(MakeButtonWithTrailingChevron(loc.GetString("onboarding.proceedToOverview"), true, () => Navigate("overview")));
        navRow.Children.Add(MakeButton(loc.GetString("toolbar.diagnostics"), false, () => Navigate("settings-advanced")));
        zone.Children.Add(navRow);

        return WrapConstrainedScrollViewer(zone, _bgZone);
    }

    // =========================================================================
    // 14. DEDICATED HELP CENTER, GLOSSARY & ABOUT ROUTES (#96-#98) (F-028)
    // =========================================================================
    private FrameworkElement BuildHelpAndAboutWorkspace()
    {
        var loc = _host.Localization;
        var zone = MakeSeamZone();
        zone.Children.Add(MakeZoneHeader(loc.GetString("help.guideTitle").ToUpperInvariant(), DestinationRegistry.GetLocalizedTitle(loc, _activeRoute)));
        zone.Children.Add(MakeInfoBanner(loc.GetString("help.aboutSummary"), _accentCyan));

        zone.Children.Add(MakeLedgerRow(loc.GetString("help.aboutTitle"), $"{loc.GetString("app.name")} v{_host.DiagnosticState.AppVersion}", _accentCyan));
        zone.Children.Add(MakeLedgerRow(loc.GetString("onboarding.runtimeStatus"), loc.Format("onboarding.abiVerified", new Dictionary<string, object?> { ["ver"] = _host.DiagnosticState.NativeAbiVersion }), _semEmerald));
        zone.Children.Add(MakeLedgerRow(loc.GetString("diagnostics.displayMetrics"), _host.DiagnosticState.DisplayMetrics, _textSecondary));

        zone.Children.Add(MakeZoneHeader(loc.GetString("settings.glossary").ToUpperInvariant(), "WINDOWS NT"));
        zone.Children.Add(MakeLedgerRow(loc.GetString("process.column.workingSet"), loc.GetString("settings.glossaryWorkingSet"), _textSecondary));
        zone.Children.Add(MakeLedgerRow(loc.GetString("process.column.privateBytes"), loc.GetString("settings.glossaryPrivateBytes"), _textSecondary));
        zone.Children.Add(MakeLedgerRow(loc.GetString("physical.list.standby"), loc.GetString("settings.glossaryStandby"), _textSecondary));
        zone.Children.Add(MakeLedgerRow(loc.GetString("physical.list.modified"), loc.GetString("settings.glossaryModified"), _textSecondary));
        zone.Children.Add(MakeLedgerRow(loc.GetString("system.memory.nonpagedPool"), loc.GetString("settings.glossaryNonpagedPool"), _textSecondary));

        return WrapConstrainedScrollViewer(zone, _bgZone);
    }

    // =========================================================================
    // 15. SETTINGS, PRIVACY CENTER & SHORTCUTS (#84-#95) (100% LOCALIZED - F-027)
    // =========================================================================
    private FrameworkElement BuildSettingsAndGlossaryWorkspace(SystemMemoryTelemetry sys)
    {
        var loc = _host.Localization;
        var self = _host.Monitor.SelfMetrics;
        var cur = _host.Settings.Current;
        var zone = MakeSeamZone();
        zone.Children.Add(MakeZoneHeader(loc.GetString("navigation.settings").ToUpperInvariant(), DestinationRegistry.GetLocalizedTitle(loc, _activeRoute)));

        zone.Children.Add(MakeLedgerRow(loc.GetString("settings.language"), $"{loc.CurrentLocale} ({(loc.IsRightToLeft ? "RTL" : "LTR")})", _accentCyan));
        zone.Children.Add(MakeLedgerRow(loc.GetString("settings.appearance"), GetLocalizedThemeName(cur.Theme), _accentViolet));
        zone.Children.Add(MakeLedgerRow(loc.GetString("settings.unitStandard"), cur.UseBinaryUnits ? loc.GetString("settings.unitBinaryIec") : loc.GetString("settings.unitDecimalSi"), _semEmerald));
        zone.Children.Add(MakeLedgerRow(loc.GetString("liveMonitor.samplingInterval"), $"{cur.SamplingIntervalMs} ms ({loc.Format("status.samples", new Dictionary<string, object?> { ["count"] = cur.HistoryRetentionSamples })})", _textPrimary));
        zone.Children.Add(MakeLedgerRow(loc.GetString("settings.redactionOnExport"), cur.RedactPathsOnExport ? loc.GetString("reports.enabled") : loc.GetString("reports.disabled"), cur.RedactPathsOnExport ? _semEmerald : _semAmber));
        zone.Children.Add(MakeLedgerRow(loc.GetString("settings.selfWsCommit"), $"{FmtBytes(self?.SelfWorkingSetBytes ?? 0)} / {FmtBytes(self?.SelfPrivateCommitBytes ?? 0)}", _semEmerald));
        zone.Children.Add(MakeLedgerRow(loc.GetString("settings.selfOverhead"), $"{self?.SelfThreadCount ?? 0} / {self?.SelfHandleCount ?? 0} / {self?.LastSampleDurationMs ?? 0} ms", _textPrimary));
        zone.Children.Add(MakeLedgerRow(loc.GetString("diagnostics.displayMetrics"), _host.DiagnosticState.DisplayMetrics, _accentCyan));

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 10) };
        btnRow.Children.Add(MakeButton(loc.GetString("settings.toggleBinaryUnits"), true, () =>
        {
            _host.Settings.Save(cur with { UseBinaryUnits = !cur.UseBinaryUnits });
            RenderActiveWorkspace();
        }));
        btnRow.Children.Add(MakeButton(loc.GetString("settings.togglePathRedaction"), false, () =>
        {
            _host.Settings.Save(cur with { RedactPathsOnExport = !cur.RedactPathsOnExport });
            RenderActiveWorkspace();
        }));
        btnRow.Children.Add(MakeButton($"{loc.GetString("liveMonitor.samplingInterval")}: {(cur.SamplingIntervalMs == 1000 ? "2000ms" : "1000ms")}", false, () =>
        {
            int nextMs = cur.SamplingIntervalMs == 1000 ? 2000 : 1000;
            _host.Settings.Save(cur with { SamplingIntervalMs = nextMs });
            _timer.Interval = TimeSpan.FromMilliseconds(nextMs);
            RenderActiveWorkspace();
        }));
        btnRow.Children.Add(MakeButton(loc.GetString("settings.reloadLanguages"), false, () =>
        {
            _host.Localization.ReloadLanguagePacks();
            _lastStatusMessage = loc.GetString("settings.reloadLanguages");
            BuildShell();
            RenderActiveWorkspace();
        }));
        btnRow.Children.Add(MakeButton(loc.GetString("toolbar.diagnostics"), false, () => Navigate("settings-advanced")));
        zone.Children.Add(new ScrollViewer { Content = btnRow, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });

        zone.Children.Add(MakeZoneHeader(loc.GetString("settings.shortcuts").ToUpperInvariant(), loc.GetString("settings.winuiAccelerators")));
        zone.Children.Add(MakeLedgerRow(loc.GetString("shortcuts.commandPalette"), "Ctrl + K", _accentCyan));
        zone.Children.Add(MakeLedgerRow(loc.GetString("shortcuts.globalSearch"), "Ctrl + F", _accentCyan));
        zone.Children.Add(MakeLedgerRow(loc.GetString("shortcuts.togglePause"), "Ctrl + P", _accentCyan));
        zone.Children.Add(MakeLedgerRow(loc.GetString("snapshots.create"), "Ctrl + S", _accentCyan));

        return WrapConstrainedScrollViewer(zone, _bgZone);
    }

    // =========================================================================
    // UI COMPONENT HELPERS (Calibrated for 1024 DIP, 1080p, 2K QHD, 4K UHD & RTL)
    // =========================================================================
    private ScrollViewer WrapConstrainedScrollViewer(FrameworkElement content, Brush background)
    {
        // Enforce readable line-length discipline on ultra-wide 4K monitors while keeping compact/standard full-bleed (F-025)
        if (_currentBreakpoint == 2)
        {
            content.MaxWidth = 1760;
            content.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        return new ScrollViewer { Content = content, Background = background };
    }

    private StackPanel MakeSeamZone(bool useCanvasBg = false) => new()
    {
        Background = useCanvasBg ? _bgCanvas : _bgZone,
        Padding = new Thickness(_currentBreakpoint == 2 ? 22 : 16, 14, _currentBreakpoint == 2 ? 22 : 16, 16),
        Spacing = 6
    };

    private Grid MakeZoneHeader(string title, string badge)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 6), ColumnSpacing = 8 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = _fontUi,
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            // Never apply positive CharacterSpacing to cursive Arabic script (F-042)
            CharacterSpacing = _host.Localization.IsRightToLeft ? 0 : 60,
            Foreground = _textSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var b = new Border
        {
            Background = _bgSurface2,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 2, 7, 2),
            Child = new TextBlock { Text = badge, FontFamily = _fontMono, FontSize = 10.5, Foreground = _accentCyan }
        };
        Grid.SetColumn(b, 1);
        g.Children.Add(b);
        return g;
    }

    private Border MakeLedgerRow(string key, string val, SolidColorBrush valBrush)
    {
        // Use ColumnSpacing instead of LTR-only Margin so RTL spacing mirrors symmetrically (F-042)
        var g = new Grid { Padding = new Thickness(0, 6, 0, 6), ColumnSpacing = _currentBreakpoint == 0 ? 8 : 12 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 76 });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new TextBlock { Text = key, FontFamily = _fontUi, FontSize = _currentBreakpoint == 0 ? 11.8 : 12.5, Foreground = _textSecondary, TextWrapping = TextWrapping.Wrap });
        var v = new TextBlock
        {
            Text = _host.Localization.IsolateTechnicalToken(val),
            FontFamily = _fontMono,
            FontSize = _currentBreakpoint == 0 ? 11.0 : 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = valBrush,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = _currentBreakpoint == 0 ? 185 : 520
        };
        Grid.SetColumn(v, 1);
        g.Children.Add(v);
        return new Border { BorderBrush = _seamBrush, BorderThickness = new Thickness(0, 0, 0, 1), Child = g };
    }

    private Grid MakeProportionBar(double ratio, SolidColorBrush fill, double height = 6)
    {
        double clamped = Math.Clamp(ratio, 0.02, 1.0);
        var g = new Grid { Height = height, Background = _bgSurface2, CornerRadius = new CornerRadius(3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(clamped, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, 1.0 - clamped), GridUnitType.Star) });
        g.Children.Add(new Border { Background = fill, CornerRadius = new CornerRadius(3) });
        return g;
    }

    private Border MakeStratumBlock(string label, SolidColorBrush bg, SolidColorBrush fg, int col)
    {
        var b = new Border
        {
            Background = bg,
            Padding = new Thickness(10, 0, 10, 0),
            Child = new TextBlock
            {
                Text = label,
                FontFamily = _fontUi,
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        };
        Grid.SetColumn(b, col);
        return b;
    }

    private Button MakeTopologyCard(
        string title,
        string metric,
        string sub,
        SolidColorBrush accent,
        Action onClick,
        FrameworkElement? leadingIcon = null,
        bool isTechnicalTitle = false)
    {
        // Use a 2-column Grid(Auto, *) instead of horizontal StackPanel so long process names NEVER clip their leading letters in RTL (F-026)
        var headerGrid = new Grid { ColumnSpacing = 8 };
        if (leadingIcon is not null)
        {
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.Children.Add(leadingIcon);
        }
        else
        {
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var titleTb = new TextBlock
        {
            Text = title,
            FontFamily = _fontUi,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textPrimary,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FlowDirection = isTechnicalTitle ? FlowDirection.LeftToRight : (_host.Localization.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight),
            HorizontalAlignment = isTechnicalTitle && _host.Localization.IsRightToLeft ? HorizontalAlignment.Right : HorizontalAlignment.Stretch
        };
        Grid.SetColumn(titleTb, leadingIcon is not null ? 1 : 0);
        headerGrid.Children.Add(titleTb);

        var sp = new StackPanel { Spacing = 3 };
        sp.Children.Add(headerGrid);
        sp.Children.Add(new TextBlock
        {
            Text = metric,
            FontFamily = _fontUi,
            FontSize = 11.5,
            Foreground = accent,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        sp.Children.Add(new TextBlock { Text = sub, FontFamily = _fontUi, FontSize = 11.5, Foreground = _textSecondary, TextWrapping = TextWrapping.Wrap });

        var btn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = _bgSurface1,
            BorderBrush = accent,
            BorderThickness = new Thickness(3, 1, 1, 1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(12, 8, 12, 8),
            Content = sp
        };
        AutomationProperties.SetName(btn, $"{title} {metric}");
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private Border MakeWaveformChannel(string channelTitle, string currentVal, IReadOnlyList<double> ratios, SolidColorBrush color)
    {
        var sp = new StackPanel { Spacing = 4 };
        sp.Children.Add(MakeZoneHeader(channelTitle, currentVal));
        var barRow = new Grid { Height = 36, ColumnSpacing = 2 };
        int count = Math.Max(1, ratios.Count);
        for (int i = 0; i < count; i++)
        {
            barRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            double r = Math.Clamp(ratios[i], 0.06, 1.0);
            var bar = new Border
            {
                Background = color,
                Height = 36 * r,
                VerticalAlignment = VerticalAlignment.Bottom,
                CornerRadius = new CornerRadius(1.5)
            };
            Grid.SetColumn(bar, i);
            barRow.Children.Add(bar);
        }
        sp.Children.Add(barRow);
        return new Border
        {
            Background = _bgZone,
            BorderBrush = _seamBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(12, 8, 12, 10),
            Margin = new Thickness(0, 0, 0, 6),
            Child = sp
        };
    }

    private FrameworkElement MakeTableRow(string[] cells, SolidColorBrush fg, Action? onClick = null, FrameworkElement? leadingIcon = null)
    {
        var g = new Grid { Padding = new Thickness(8, 6, 8, 6), ColumnSpacing = 10 };
        for (int i = 0; i < cells.Length; i++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(i == 0 ? 1.65 : 1, GridUnitType.Star) });
            // Only wrap non-Arabic technical cells in LRI so Arabic headers & labels preserve natural RTL flow (F-042)
            string cellText = cells[i] ?? string.Empty;
            bool hasArabic = cellText.Any(c => c >= '\u0600' && c <= '\u06FF');
            var tb = new TextBlock
            {
                Text = hasArabic ? cellText : _host.Localization.IsolateTechnicalToken(cellText),
                FontFamily = i == 0 ? _fontUi : _fontMono,
                FontSize = 12,
                Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            if (i == 0 && leadingIcon is not null)
            {
                var cellGrid = new Grid { ColumnSpacing = 8 };
                cellGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                cellGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                cellGrid.Children.Add(leadingIcon);
                Grid.SetColumn(tb, 1);
                cellGrid.Children.Add(tb);
                Grid.SetColumn(cellGrid, 0);
                g.Children.Add(cellGrid);
            }
            else
            {
                Grid.SetColumn(tb, i);
                g.Children.Add(tb);
            }
        }

        // WCAG 2.1.1 keyboard accessibility: interactive rows MUST be real focusable Buttons (Tab/Enter/Space), never non-focusable Borders (F-011)
        if (onClick != null)
        {
            var rowBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = _bgSurface1,
                BorderBrush = _seamBrush,
                BorderThickness = new Thickness(0, 0, 0, 1),
                CornerRadius = new CornerRadius(0),
                Padding = new Thickness(0),
                Content = g
            };
            AutomationProperties.SetName(rowBtn, string.Join(" ", cells));
            rowBtn.Click += (_, _) => onClick();
            return rowBtn;
        }

        var b = new Border
        {
            Background = _bgSurface1,
            BorderBrush = _seamBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = g
        };
        AutomationProperties.SetName(b, string.Join(" ", cells));
        return b;
    }

    private Border MakeInfoBanner(string text, SolidColorBrush accent) => new()
    {
        Background = _bgSurface1,
        BorderBrush = accent,
        BorderThickness = new Thickness(3, 1, 1, 1),
        Padding = new Thickness(12, 9, 12, 9),
        CornerRadius = new CornerRadius(5),
        Child = new TextBlock { Text = text, FontFamily = _fontUi, FontSize = 12, Foreground = _textPrimary, TextWrapping = TextWrapping.Wrap }
    };

    private Button MakeButton(string text, bool primary, Action onClick)
    {
        bool isLight = _host.Settings.Current.Theme == AppThemeMode.Light;
        var fg = primary
            ? (isLight ? new SolidColorBrush(Colors.White) : new SolidColorBrush(Color.FromArgb(255, 6, 16, 26)))
            : _textPrimary;
        var btn = new Button
        {
            Content = text,
            FontFamily = _fontUi,
            FontSize = 12,
            FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
            Background = primary ? _accentCyan : _bgSurface2,
            Foreground = fg,
            BorderBrush = primary ? _accentCyan : _seamBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(11, 5, 11, 5),
            CornerRadius = new CornerRadius(5)
        };
        if (primary)
            ApplyPrimaryButtonThemeResources(btn, fg);
        AutomationProperties.SetName(btn, text);
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private Button MakeButtonWithTrailingChevron(string text, bool primary, Action onClick)
    {
        bool isLight = _host.Settings.Current.Theme == AppThemeMode.Light;
        var fg = primary
            ? (isLight ? new SolidColorBrush(Colors.White) : new SolidColorBrush(Color.FromArgb(255, 6, 16, 26)))
            : _textPrimary;

        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new TextBlock
        {
            Text = text,
            FontFamily = _fontUi,
            FontSize = 12,
            FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = fg,
            VerticalAlignment = VerticalAlignment.Center
        });
        sp.Children.Add(FluentIconCatalog.Create(FluentGlyph.ChevronRight, 12, fg, mirrorInRtl: true));

        var btn = new Button
        {
            Content = sp,
            Background = primary ? _accentCyan : _bgSurface2,
            Foreground = fg,
            BorderBrush = primary ? _accentCyan : _seamBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(11, 5, 11, 5),
            CornerRadius = new CornerRadius(5)
        };
        if (primary)
            ApplyPrimaryButtonThemeResources(btn, fg);
        AutomationProperties.SetName(btn, text);
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private static string FormatHexDump(ulong baseAddr, byte[] bytes, string unreadableMsg)
    {
        if (bytes.Length == 0) return unreadableMsg;
        var sb = new StringBuilder();
        sb.AppendLine("OFFSET              00 01 02 03 04 05 06 07  08 09 0A 0B 0C 0D 0E 0F   DECODED ASCII");
        for (int i = 0; i < bytes.Length; i += 16)
        {
            sb.Append($"0x{baseAddr + (ulong)i:X16}  ");
            for (int j = 0; j < 16; j++)
            {
                if (i + j < bytes.Length) sb.Append($"{bytes[i + j]:X2} ");
                else sb.Append("   ");
                if (j == 7) sb.Append(' ');
            }
            sb.Append(" |");
            for (int j = 0; j < 16 && (i + j) < bytes.Length; j++)
            {
                byte b = bytes[i + j];
                sb.Append(b >= 32 && b <= 126 ? (char)b : '.');
            }
            sb.AppendLine("|");
        }
        return sb.ToString();
    }
}
