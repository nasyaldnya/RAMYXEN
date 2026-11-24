using System;
using System.IO;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using RAMYXEN.Application.Services;
using RAMYXEN.Diagnostics;
using RAMYXEN.Infrastructure.Windows;

namespace RAMYXEN.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--verify-launch", StringComparer.OrdinalIgnoreCase))
        {
            return RunSelfVerification();
        }

        int autoCloseMs = 0;
        int idx = Array.FindIndex(args, a => a.Equals("--auto-close-ms", StringComparison.OrdinalIgnoreCase));
        if (idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], out int parsedMs))
        {
            autoCloseMs = parsedMs;
        }

        bool verifyAllViews = args.Contains("--verify-all-views", StringComparer.OrdinalIgnoreCase);

        string? screenshotDir = null;
        int sIdx = Array.FindIndex(args, a => a.Equals("--capture-screenshots", StringComparison.OrdinalIgnoreCase));
        if (sIdx >= 0 && sIdx + 1 < args.Length)
        {
            screenshotDir = args[sIdx + 1];
        }

        string? localeScreenshotDir = null;
        int lsIdx = Array.FindIndex(args, a => a.Equals("--capture-4locales", StringComparison.OrdinalIgnoreCase));
        if (lsIdx >= 0 && lsIdx + 1 < args.Length)
        {
            localeScreenshotDir = args[lsIdx + 1];
        }

        int width = 1600, height = 960;
        int wIdx = Array.FindIndex(args, a => a.Equals("--window-size", StringComparison.OrdinalIgnoreCase));
        if (wIdx >= 0 && wIdx + 1 < args.Length)
        {
            var parts = args[wIdx + 1].Split('x', 'X');
            if (parts.Length == 2 && int.TryParse(parts[0], out int pw) && int.TryParse(parts[1], out int ph))
            {
                width = pw;
                height = ph;
            }
        }

        try
        {
            App.AutoCloseMs = autoCloseMs;
            App.VerifyAllViews = verifyAllViews;
            App.ScreenshotOutputDir = screenshotDir;
            App.LocaleScreenshotOutputDir = localeScreenshotDir;
            App.PrivacySafeCapture = !string.IsNullOrWhiteSpace(localeScreenshotDir) || args.Contains("--privacy-safe-capture", StringComparer.OrdinalIgnoreCase);
            App.InitialWidth = width;
            App.InitialHeight = height;

            WinRT.ComWrappersSupport.InitializeComWrappers();
            Microsoft.UI.Xaml.Application.Start(_ =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                using var logger = new StructuredDiagnosticLogger();
                var svc = new CrashAndDiagnosticReportService(logger, new DiagnosticRuntimeState());
                svc.CaptureManagedException(ex, "Program.Main", isFatal: true);
            }
            catch { }
            return 1;
        }
    }

    private static int RunSelfVerification()
    {
        try
        {
            int abi = NativeMethods.Ramyxen_GetAbiVersion();
            using var host = new RamyxenPlatformHost();
            host.RefreshTelemetry();
            var sys = host.Monitor.LatestSystem ?? throw new InvalidOperationException("No system telemetry");
            var procs = host.Monitor.LatestProcesses;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("RAMYXEN_EXE_VERIFY=OK");
            sb.AppendLine($"NATIVE_ABI={abi}");
            sb.AppendLine($"PHYSICAL_TOTAL_BYTES={sys.TotalPhysicalBytes}");
            sb.AppendLine($"PHYSICAL_IN_USE_BYTES={sys.InUsePhysicalBytes}");
            sb.AppendLine($"COMMIT_TOTAL_BYTES={sys.CommitTotalBytes}");
            sb.AppendLine($"PROCESS_COUNT={procs.Count}");
            sb.AppendLine($"LOCALE_DEFAULT={host.Localization.CurrentLocale}");
            host.SetLanguage("ar-SA");
            sb.AppendLine($"LOCALE_AR_RTL={host.Localization.IsRightToLeft}");
            sb.AppendLine($"AR_PALETTE_TITLE={host.Localization.GetString("palette.title")}");
            host.SetLanguage("en-US");
            Console.Write(sb.ToString());
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RAMYXEN_EXE_VERIFY_FAILED: {ex}");
            return 1;
        }
    }
}
