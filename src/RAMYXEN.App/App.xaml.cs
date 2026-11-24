using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using RAMYXEN.App.Views;
using RAMYXEN.Diagnostics;

namespace RAMYXEN.App;

public partial class App : Microsoft.UI.Xaml.Application
{
    public static int AutoCloseMs { get; set; }
    public static bool VerifyAllViews { get; set; }
    public static string? ScreenshotOutputDir { get; set; }
    public static string? LocaleScreenshotOutputDir { get; set; }
    public static bool PrivacySafeCapture { get; set; }
    public static int InitialWidth { get; set; } = 1600;
    public static int InitialHeight { get; set; } = 960;

    private static CrashAndDiagnosticReportService? _activeCrashService;
    private static Action<DiagnosticReportRecord>? _uiErrorNotifier;
    private static int _inErrorHandler;
    private static int _handledErrorBurstCount;
    private static long _lastHandledErrorTickMs;
    private Window? _window;

    public static void AttachDiagnostics(
        CrashAndDiagnosticReportService crashService,
        Action<DiagnosticReportRecord>? uiErrorNotifier = null)
    {
        _activeCrashService = crashService;
        _uiErrorNotifier = uiErrorNotifier;
    }

    public static void DetachDiagnostics()
    {
        _activeCrashService = null;
        _uiErrorNotifier = null;
    }

    /// <summary>
    /// Classifies whether an exception (or any nested inner exception) indicates process corruption,
    /// missing native ABI/library, uninitialized state, or programming invariant breach that MUST NOT
    /// be silently swallowed via e.Handled = true (F-018).
    /// </summary>
    public static bool IsFatalOrCorrupting(Exception? ex)
    {
        if (ex is null)
            return true;

        for (Exception? cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is AggregateException agg)
            {
                foreach (var inner in agg.Flatten().InnerExceptions)
                {
                    if (IsFatalOrCorrupting(inner))
                        return true;
                }
                return false;
            }

            if (cur is OutOfMemoryException
                or StackOverflowException
                or AccessViolationException
                or SEHException
                or BadImageFormatException
                or DllNotFoundException
                or EntryPointNotFoundException
                or TypeInitializationException
                or NullReferenceException
                or ObjectDisposedException
                or COMException)
            {
                return true;
            }
        }
        return false;
    }

    public App()
    {
        InitializeComponent();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                RecordException(ex, "AppDomain.UnhandledException", isFatal: true);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            bool fatal = IsFatalOrCorrupting(e.Exception);
            RecordException(e.Exception, "TaskScheduler.UnobservedTaskException", isFatal: fatal);
            // Only observe non-fatal background task failures (F-018)
            if (!fatal)
            {
                e.SetObserved();
            }
        };

        UnhandledException += (_, e) =>
        {
            // Re-entrancy guard prevents infinite error loops if UI error notification itself faults
            if (Interlocked.Exchange(ref _inErrorHandler, 1) == 1)
                return;

            try
            {
                // Classify null e.Exception BEFORE wrapping so unknown null exceptions are NEVER silently swallowed (R2-NEW-010)
                bool isFatal = e.Exception is null || IsFatalOrCorrupting(e.Exception);

                Exception effectiveEx = e.Exception ?? new InvalidOperationException(
                    string.IsNullOrWhiteSpace(e.Message)
                        ? "WinUI unhandled exception reported with null Exception object."
                        : e.Message);

                // Rate-limit recoverable UI handling: if >3 errors occur within 5 seconds, fail fast instead of zombie UI
                long nowMs = Environment.TickCount64;
                long prevMs = Interlocked.Exchange(ref _lastHandledErrorTickMs, nowMs);
                int burst;
                if (prevMs != 0 && (nowMs - prevMs) < 5000)
                {
                    burst = Interlocked.Increment(ref _handledErrorBurstCount);
                }
                else
                {
                    Interlocked.Exchange(ref _handledErrorBurstCount, 1);
                    burst = 1;
                }

                bool effectiveFatal = isFatal || burst > 3;
                var record = RecordException(effectiveEx, "WinUI.UnhandledException", effectiveFatal);

                // Never swallow fatal/corrupting exceptions, error storms, or automated verification failures
                if (!effectiveFatal && !VerifyAllViews)
                {
                    e.Handled = true;
                    if (record is not null)
                    {
                        try { _uiErrorNotifier?.Invoke(record); } catch { }
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _inErrorHandler, 0);
            }
        };
    }

    private static DiagnosticReportRecord? RecordException(Exception ex, string source, bool isFatal)
    {
        try
        {
            var svc = _activeCrashService;
            if (svc is not null)
            {
                return svc.CaptureManagedException(ex, source, isFatal);
            }

            using var fallbackLogger = new StructuredDiagnosticLogger();
            var fallbackService = new CrashAndDiagnosticReportService(fallbackLogger, new DiagnosticRuntimeState());
            return fallbackService.CaptureManagedException(ex, source, isFatal);
        }
        catch
        {
            return null;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new RamyxenMainWindow(AutoCloseMs, VerifyAllViews, ScreenshotOutputDir, InitialWidth, InitialHeight);
        _window.Activate();
    }
}
