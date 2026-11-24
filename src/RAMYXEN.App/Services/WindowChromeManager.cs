using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using RAMYXEN.Core.Models;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace RAMYXEN.App.Services;

public sealed record WindowDisplayMetrics(
    uint Dpi,
    double ScaleFactor,
    int WorkAreaWidthPx,
    int WorkAreaHeightPx,
    double CurrentWidthDip,
    double CurrentHeightDip)
{
    public string Summary => $"{(int)Math.Round(CurrentWidthDip)}x{(int)Math.Round(CurrentHeightDip)} DIP @ {Dpi} DPI ({ScaleFactor * 100:F0}%) [WorkArea {WorkAreaWidthPx}x{WorkAreaHeightPx}px]";
}

public static class WindowChromeManager
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;

    public static bool IsCustomTitleBarSupported => AppWindowTitleBar.IsCustomizationSupported();

    public static void ApplyIconAndChrome(Window window, AppThemeMode theme)
    {
        try
        {
            nint hwnd = WindowNative.GetWindowHandle(window);
            if (hwnd == nint.Zero) return;

            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);

            // 1. Official multi-resolution RAMYXEN.ico via AppWindow.SetIcon (zero leaked HICON handles - F-020)
            string icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "RAMYXEN.ico");
            if (File.Exists(icoPath))
            {
                appWindow?.SetIcon(icoPath);
            }

            // 2. Extend WinUI 3 content into title bar ONLY when supported by OS (Win10 19041 safe fallback - F-020)
            if (IsCustomTitleBarSupported)
            {
                window.ExtendsContentIntoTitleBar = true;
                if (appWindow?.TitleBar is { } tb)
                {
                    tb.ExtendsContentIntoTitleBar = true;
                    tb.PreferredHeightOption = TitleBarHeightOption.Tall;
                }
            }

            // 3. Apply DWM immersive dark mode + themed caption colors immediately (prevents white flash)
            ApplyThemeToWindowChrome(window, theme);
        }
        catch
        {
        }
    }

    public static void ApplyThemeToWindowChrome(Window window, AppThemeMode theme)
    {
        try
        {
            nint hwnd = WindowNative.GetWindowHandle(window);
            if (hwnd == nint.Zero) return;

            bool darkChrome = theme != AppThemeMode.Light;
            int darkFlag = darkChrome ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkFlag, sizeof(int));

            // COLORREF is 0x00BBGGRR (supported on Windows 11; harmlessly ignored on Windows 10)
            int captionColorRef = theme switch
            {
                AppThemeMode.Light => 0x00FCF9F7,       // #F7F9FC
                AppThemeMode.HighContrast => 0x00000000, // #000000
                _ => 0x0017110D                          // #0D1117
            };
            _ = DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColorRef, sizeof(int));

            if (!IsCustomTitleBarSupported)
                return;

            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            if (appWindow?.TitleBar is { } tb)
            {
                var transparent = Color.FromArgb(0, 0, 0, 0);
                tb.BackgroundColor = transparent;
                tb.InactiveBackgroundColor = transparent;
                tb.ButtonBackgroundColor = transparent;
                tb.ButtonInactiveBackgroundColor = transparent;

                if (theme == AppThemeMode.Light)
                {
                    tb.ForegroundColor = Color.FromArgb(255, 15, 23, 42);
                    tb.InactiveForegroundColor = Color.FromArgb(255, 100, 116, 139);
                    tb.ButtonForegroundColor = Color.FromArgb(255, 15, 23, 42);
                    tb.ButtonInactiveForegroundColor = Color.FromArgb(255, 100, 116, 139);
                    tb.ButtonHoverForegroundColor = Color.FromArgb(255, 15, 23, 42);
                    tb.ButtonHoverBackgroundColor = Color.FromArgb(255, 226, 232, 240);
                    tb.ButtonPressedForegroundColor = Color.FromArgb(255, 15, 23, 42);
                    tb.ButtonPressedBackgroundColor = Color.FromArgb(255, 203, 213, 225);
                }
                else if (theme == AppThemeMode.HighContrast)
                {
                    tb.ForegroundColor = Color.FromArgb(255, 255, 255, 255);
                    tb.InactiveForegroundColor = Color.FromArgb(255, 200, 200, 200);
                    tb.ButtonForegroundColor = Color.FromArgb(255, 255, 255, 255);
                    tb.ButtonInactiveForegroundColor = Color.FromArgb(255, 200, 200, 200);
                    tb.ButtonHoverForegroundColor = Color.FromArgb(255, 0, 0, 0);
                    tb.ButtonHoverBackgroundColor = Color.FromArgb(255, 0, 255, 255);
                    tb.ButtonPressedForegroundColor = Color.FromArgb(255, 0, 0, 0);
                    tb.ButtonPressedBackgroundColor = Color.FromArgb(255, 255, 255, 0);
                }
                else
                {
                    tb.ForegroundColor = Color.FromArgb(255, 240, 244, 248);
                    tb.InactiveForegroundColor = Color.FromArgb(255, 100, 116, 139);
                    tb.ButtonForegroundColor = Color.FromArgb(255, 226, 232, 240);
                    tb.ButtonInactiveForegroundColor = Color.FromArgb(255, 100, 116, 139);
                    tb.ButtonHoverForegroundColor = Color.FromArgb(255, 255, 255, 255);
                    tb.ButtonHoverBackgroundColor = Color.FromArgb(255, 29, 38, 53);
                    tb.ButtonPressedForegroundColor = Color.FromArgb(255, 255, 255, 255);
                    tb.ButtonPressedBackgroundColor = Color.FromArgb(255, 42, 54, 74);
                }
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// Returns physical screen-edge caption insets (LeftInsetDip = physical left, RightInsetDip = physical right).
    /// </summary>
    public static (double LeftInsetDip, double RightInsetDip) GetTitleBarCaptionInsetsDip(Window window)
    {
        if (!IsCustomTitleBarSupported)
            return (0, 0);

        try
        {
            nint hwnd = WindowNative.GetWindowHandle(window);
            if (hwnd == nint.Zero) return (0, 138);

            uint dpi = GetDpiForWindow(hwnd);
            double scale = dpi > 0 ? dpi / 96.0 : 1.0;

            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            if (appWindow?.TitleBar is { } tb)
            {
                double left = tb.LeftInset / scale;
                double right = tb.RightInset / scale;
                if (left > 0 || right > 0)
                    return (left, right);
            }
        }
        catch
        {
        }
        return (0, 138);
    }

    /// <summary>
    /// Computes logical XAML Thickness padding for the custom title bar container (F-004).
    /// AppWindowTitleBar LeftInset/RightInset are ALWAYS physical screen edges, whereas XAML
    /// Border.Padding mirrors logically when FlowDirection == RightToLeft:
    ///   - LTR: logical Left  = physical Left,  logical Right = physical Right
    ///   - RTL: logical Left  = physical Right, logical Right = physical Left
    /// Swapping in RTL ensures physical OS Minimize/Maximize/Close buttons NEVER collide with the RAMYXEN logo!
    /// </summary>
    public static Thickness ComputeLogicalTitleBarPadding(Window window, bool isRtl)
    {
        var (physLeft, physRight) = GetTitleBarCaptionInsetsDip(window);
        if (isRtl)
        {
            return new Thickness(physRight + 8, 0, physLeft + 14, 0);
        }
        return new Thickness(physLeft + 14, 0, physRight + 8, 0);
    }

    public static uint GetWindowDpi(Window window)
    {
        try
        {
            nint hwnd = WindowNative.GetWindowHandle(window);
            if (hwnd != nint.Zero)
            {
                uint dpi = GetDpiForWindow(hwnd);
                if (dpi > 0) return dpi;
            }
        }
        catch { }
        return 96;
    }

    public static WindowDisplayMetrics GetCurrentDisplayMetrics(Window window, double? overrideWidthDip = null, double? overrideHeightDip = null)
    {
        uint dpi = 96;
        double scale = 1.0;
        int workW = 1920;
        int workH = 1040;
        double curW = overrideWidthDip ?? 1440;
        double curH = overrideHeightDip ?? 900;

        try
        {
            nint hwnd = WindowNative.GetWindowHandle(window);
            if (hwnd != nint.Zero)
            {
                dpi = GetDpiForWindow(hwnd);
                if (dpi == 0) dpi = 96;
                scale = dpi / 96.0;

                var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = AppWindow.GetFromWindowId(windowId);
                var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
                if (displayArea is not null)
                {
                    workW = displayArea.WorkArea.Width;
                    workH = displayArea.WorkArea.Height;
                }
                if (appWindow is not null && overrideWidthDip is null && overrideHeightDip is null)
                {
                    curW = appWindow.Size.Width / scale;
                    curH = appWindow.Size.Height / scale;
                }
            }
        }
        catch { }

        return new WindowDisplayMetrics(dpi, scale, workW, workH, curW, curH);
    }

    /// <summary>
    /// Updates OverlappedPresenter minimum window dimensions dynamically on DPI / monitor change (F-009).
    /// Clamps minimums safely to the monitor WorkArea so small/high-scale screens are never forced off-screen.
    /// </summary>
    public static WindowDisplayMetrics RefreshMinimumWindowSize(Window window)
    {
        var metrics = GetCurrentDisplayMetrics(window);
        try
        {
            nint hwnd = WindowNative.GetWindowHandle(window);
            if (hwnd != nint.Zero)
            {
                var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = AppWindow.GetFromWindowId(windowId);
                if (appWindow?.Presenter is OverlappedPresenter presenter)
                {
                    int minW = Math.Min((int)Math.Round(1024 * metrics.ScaleFactor), Math.Max(640, (int)(metrics.WorkAreaWidthPx * 0.92)));
                    int minH = Math.Min((int)Math.Round(660 * metrics.ScaleFactor), Math.Max(480, (int)(metrics.WorkAreaHeightPx * 0.90)));
                    presenter.PreferredMinimumWidth = minW;
                    presenter.PreferredMinimumHeight = minH;
                }
            }
        }
        catch { }
        return metrics;
    }

    public static WindowDisplayMetrics ConfigureDpiAwareWindowSize(
        Window window,
        int requestedWidthDip,
        int requestedHeightDip,
        bool clampToWorkArea = true)
    {
        uint dpi = 96;
        double scale = 1.0;
        int workW = 1920;
        int workH = 1040;

        try
        {
            nint hwnd = WindowNative.GetWindowHandle(window);
            if (hwnd != nint.Zero)
            {
                dpi = GetDpiForWindow(hwnd);
                if (dpi == 0) dpi = 96;
                scale = dpi / 96.0;

                var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = AppWindow.GetFromWindowId(windowId);
                var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
                if (displayArea is not null)
                {
                    workW = displayArea.WorkArea.Width;
                    workH = displayArea.WorkArea.Height;
                }

                int minPxW = Math.Min((int)Math.Round(1024 * scale), Math.Max(640, (int)(workW * 0.92)));
                int minPxH = Math.Min((int)Math.Round(660 * scale), Math.Max(480, (int)(workH * 0.90)));

                int targetPxW = (int)Math.Round(requestedWidthDip * scale);
                int targetPxH = (int)Math.Round(requestedHeightDip * scale);

                if (clampToWorkArea)
                {
                    targetPxW = Math.Min(targetPxW, (int)(workW * 0.94));
                    targetPxH = Math.Min(targetPxH, (int)(workH * 0.94));
                }

                targetPxW = Math.Max(targetPxW, minPxW);
                targetPxH = Math.Max(targetPxH, minPxH);

                appWindow?.Resize(new SizeInt32(targetPxW, targetPxH));

                if (displayArea is not null && appWindow is not null && clampToWorkArea)
                {
                    int x = displayArea.WorkArea.X + Math.Max(0, (workW - targetPxW) / 2);
                    int y = displayArea.WorkArea.Y + Math.Max(0, (workH - targetPxH) / 2);
                    appWindow.Move(new PointInt32(x, y));
                }

                if (appWindow?.Presenter is OverlappedPresenter presenter)
                {
                    presenter.PreferredMinimumWidth = minPxW;
                    presenter.PreferredMinimumHeight = minPxH;
                }

                return new WindowDisplayMetrics(dpi, scale, workW, workH, targetPxW / scale, targetPxH / scale);
            }
        }
        catch
        {
        }

        return new WindowDisplayMetrics(dpi, scale, workW, workH, requestedWidthDip, requestedHeightDip);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
