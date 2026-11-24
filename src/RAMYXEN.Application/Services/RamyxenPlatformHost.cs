using RAMYXEN.Core.Models;
using RAMYXEN.Diagnostics;
using RAMYXEN.ETW;
using RAMYXEN.Forensics;
using RAMYXEN.Infrastructure.Windows;
using RAMYXEN.Localization.Services;
using RAMYXEN.Memory.Engine;
using RAMYXEN.Profiler;
using RAMYXEN.Reporting;
using RAMYXEN.Storage.Services;

namespace RAMYXEN.Application.Services;

/// <summary>
/// Central Application Orchestrator connecting the WinUI 3 presentation layer to all
/// underlying Windows memory, process, leak investigation, ETW, profiler, forensic,
/// snapshot, reporting, localization, official shell icon, async inspection, and crash diagnostics engines.
/// </summary>
public sealed class RamyxenPlatformHost : IDisposable
{
    public ILocalizationService Localization { get; }
    public SettingsStore Settings { get; }
    public SnapshotVaultService Snapshots { get; }
    public LiveMemoryMonitoringEngine Monitor { get; }
    public EtwSessionManager Etw { get; }
    public AllocationAndRuntimeProfiler Profiler { get; }
    public ForensicAnalysisEngine Forensics { get; }
    public ReportExporterService Reporter { get; }

    public DiagnosticRuntimeState DiagnosticState { get; }
    public StructuredDiagnosticLogger DiagnosticsLogger { get; }
    public CrashAndDiagnosticReportService CrashReports { get; }
    public WindowsShellIconService ProcessIcons { get; }
    public ProcessInspectionCoordinator ProcessInspector { get; }

    public uint SelectedPid { get; set; }
    public bool StringsInspectionConsentGranted { get; set; }

    public RamyxenPlatformHost(string? storageRoot = null, string? bundledLocalizationRoot = null)
    {
        DiagnosticState = new DiagnosticRuntimeState();
        DiagnosticsLogger = new StructuredDiagnosticLogger(storageRoot);
        CrashReports = new CrashAndDiagnosticReportService(DiagnosticsLogger, DiagnosticState, storageRoot);

        try
        {
            DiagnosticState.NativeAbiVersion = NativeMethods.EnsureNativeAbiCompatibility();
        }
        catch (Exception ex)
        {
            DiagnosticsLogger.Error("Startup", "Native ABI / struct-layout compatibility check failed.", ex);
            throw;
        }

        Settings = new SettingsStore(storageRoot);
        Localization = new LocalizationService(bundledLocalizationRoot);
        Localization.TrySetLanguage(Settings.Current.Language);

        DiagnosticState.ActiveLocale = Localization.CurrentLocale;
        DiagnosticState.ActiveTheme = Settings.Current.Theme.ToString();

        Snapshots = new SnapshotVaultService(storageRoot != null ? Path.Combine(storageRoot, "Snapshots") : null);
        Monitor = new LiveMemoryMonitoringEngine(Settings.Current.HistoryRetentionSamples);
        Monitor.SystemCollector.DiagnosticsLogger = DiagnosticsLogger;
        Monitor.SystemCollector.CrashReportService = CrashReports;
        Monitor.ProcessCollector.DiagnosticsLogger = DiagnosticsLogger;
        Etw = new EtwSessionManager();
        Profiler = new AllocationAndRuntimeProfiler();
        Forensics = new ForensicAnalysisEngine();
        Reporter = new ReportExporterService(Localization);

        ProcessIcons = new WindowsShellIconService(DiagnosticsLogger);
        ProcessInspector = new ProcessInspectionCoordinator(Monitor, DiagnosticsLogger);

        SelectedPid = (uint)Environment.ProcessId;
        DiagnosticState.SelectedPid = (int)SelectedPid;

        DiagnosticsLogger.Info("Startup", $"RAMYXEN Platform Host initialized (ABI v{DiagnosticState.NativeAbiVersion}, Locale={Localization.CurrentLocale}, Theme={Settings.Current.Theme}).");
    }

    public void RefreshTelemetry()
    {
        Monitor.SampleNow();
        Etw.PollSessionUpdate();
        if (Monitor.LatestProcesses.Count > 0 && !Monitor.LatestProcesses.Any(p => p.Pid == SelectedPid))
        {
            SelectedPid = Monitor.LatestProcesses[0].Pid;
            DiagnosticState.SelectedPid = (int)SelectedPid;
        }
    }

    public ProcessMemoryInfo? GetSelectedProcess()
    {
        return Monitor.LatestProcesses.FirstOrDefault(p => p.Pid == SelectedPid)
            ?? Monitor.LatestProcesses.FirstOrDefault();
    }

    public void SetTheme(AppThemeMode theme)
    {
        Settings.Save(Settings.Current with { Theme = theme });
        DiagnosticState.ActiveTheme = theme.ToString();
        DiagnosticsLogger.Breadcrumb("Theme", $"Switched theme to {theme}");
    }

    public bool SetLanguage(string locale)
    {
        bool ok = Localization.TrySetLanguage(locale);
        if (ok)
        {
            Settings.Save(Settings.Current with { Language = Localization.CurrentLocale });
            DiagnosticState.ActiveLocale = Localization.CurrentLocale;
            DiagnosticsLogger.Breadcrumb("Locale", $"Switched language to {Localization.CurrentLocale}");
        }
        return ok;
    }

    public void Dispose()
    {
        ProcessInspector.Dispose();
        ProcessIcons.Dispose();
        Etw.Dispose();
        DiagnosticsLogger.Dispose();
    }
}
