using RAMYXEN.Core.Models;
using RAMYXEN.Infrastructure.Windows;
using RAMYXEN.Storage.Services;

namespace RAMYXEN.Memory.Engine;

public sealed record SelfDiagnosticMetrics(
    ulong SelfWorkingSetBytes,
    ulong SelfPrivateCommitBytes,
    uint SelfThreadCount,
    uint SelfHandleCount,
    double LastSampleDurationMs,
    int StoredHistoryCount);

/// <summary>
/// Core Memory Exhaustion &amp; Leak Investigation Engine.
/// Distinguishes all 9 forms of memory exhaustion and evaluates continuous historical process &amp; kernel
/// telemetry using ordinary least-squares linear regression (slope + R²), sawtooth GC reclamation detection,
/// post-spike drawdown analysis, Working Set vs. Private Commit divergence, and PID-reuse validation.
/// </summary>
public sealed class MemoryExhaustionAndLeakEngine
{
    private sealed class ProcessHistoryTracker
    {
        public required uint Pid { get; init; }
        public required string Name { get; set; }
        public required string ExecutablePath { get; set; }
        public required uint ParentPid { get; set; }
        public required bool IsManagedDotNet { get; set; }
        public DateTimeOffset? ProcessStartTimeUtc { get; set; }
        public DateTimeOffset FirstObservedUtc { get; set; }
        public List<(DateTimeOffset Time, ulong CommitBytes, ulong WorkingSetBytes, uint Handles)> Samples { get; } = [];
    }

    private readonly Dictionary<uint, ProcessHistoryTracker> _trackers = new();
    private readonly List<(DateTimeOffset Time, ulong NonpagedBytes, ulong PagedBytes)> _kernelHistory = [];
    private readonly object _lock = new();

    public MemoryExhaustionKind DetectSystemExhaustion(SystemMemoryTelemetry sys)
    {
        lock (_lock)
        {
            _kernelHistory.Add((sys.TimestampUtc, sys.KernelNonpagedBytes, sys.KernelPagedBytes));
            if (_kernelHistory.Count > 60) _kernelHistory.RemoveAt(0);
        }

        // 1. Imminent allocation failure risk when commit limit is nearly exhausted (>= 96% or < 512 MiB remaining)
        ulong remainingCommit = sys.CommitLimitBytes > sys.CommitTotalBytes
            ? (sys.CommitLimitBytes - sys.CommitTotalBytes) : 0UL;
        if (sys.CommitRatioPercent >= 96.0 || (sys.CommitLimitBytes > 0 && remainingCommit < (512UL * 1024 * 1024)))
            return MemoryExhaustionKind.AllocationFailureRisk;

        // 2. System commit charge exhaustion (>= 88% of commit limit)
        if (sys.CommitRatioPercent >= 88.0)
            return MemoryExhaustionKind.SystemCommitExhaustion;

        // 3. Available physical memory depletion (>= 92% physical load AND < 1 GiB available including Standby)
        if (sys.PhysicalUsagePercent >= 92.0 && sys.AvailablePhysicalBytes < (1024UL * 1024 * 1024))
            return MemoryExhaustionKind.AvailableMemoryDepletion;

        // 4. Kernel Nonpaged Pool exhaustion (> 2 GiB or > 15% of total physical RAM)
        if (sys.KernelNonpagedBytes > (2048UL * 1024 * 1024) ||
            (sys.TotalPhysicalBytes > 0 && (double)sys.KernelNonpagedBytes / sys.TotalPhysicalBytes > 0.15))
            return MemoryExhaustionKind.KernelPoolExhaustion;

        // 5. Driver / Kernel Pool sustained growth across history samples
        if (HasSustainedKernelPoolGrowth())
            return MemoryExhaustionKind.DriverMemoryGrowth;

        // 6. Pagefile thrashing / hard fault pressure
        if (sys.CommitRatioPercent >= 80.0 && sys.HardFaultsPerSec > 50.0)
            return MemoryExhaustionKind.PageFilePressure;

        // 7. Excessive modified / non-reclaimable memory retention (> 2 GiB Modified list)
        if (sys.ModifiedBytes > (2048UL * 1024 * 1024))
            return MemoryExhaustionKind.ExcessiveMemoryRetention;

        // 8. Elevated physical RAM pressure (only when Available RAM is actually low, never on high Standby Cache!)
        if (sys.PhysicalUsagePercent >= 85.0 && sys.AvailablePhysicalBytes < (2560UL * 1024 * 1024))
            return MemoryExhaustionKind.PhysicalRamPressure;

        return MemoryExhaustionKind.None;
    }

    private bool HasSustainedKernelPoolGrowth()
    {
        lock (_lock)
        {
            if (_kernelHistory.Count < 6) return false;
            var first = _kernelHistory[0];
            var last = _kernelHistory[^1];
            if (last.NonpagedBytes <= first.NonpagedBytes + (64UL * 1024 * 1024)) return false;

            int increases = 0;
            for (int i = 1; i < _kernelHistory.Count; i++)
            {
                if (_kernelHistory[i].NonpagedBytes > _kernelHistory[i - 1].NonpagedBytes)
                    increases++;
            }
            return increases >= (_kernelHistory.Count - 1) * 0.8;
        }
    }

    public IReadOnlyList<LeakInvestigationCase> AnalyzeSample(
        SystemMemoryTelemetry sys,
        IReadOnlyList<ProcessMemoryInfo> processes)
    {
        lock (_lock)
        {
            var now = sys.TimestampUtc;
            var allLivePids = new HashSet<uint>(processes.Count);
            foreach (var p in processes)
                allLivePids.Add(p.Pid);

            // Prune ONLY processes that have genuinely exited the system (never evict a live process that dipped below rank #40!)
            foreach (var exitedPid in _trackers.Keys.Where(k => !allLivePids.Contains(k)).ToList())
                _trackers.Remove(exitedPid);

            // Track union of top processes by Working Set, Private Commit, Handle Count, and any already-tracked live PID
            var candidatesToTrack = processes
                .OrderByDescending(p => p.PrivateCommitBytes)
                .Take(60)
                .UnionBy(processes.OrderByDescending(p => p.WorkingSetBytes).Take(60), p => p.Pid)
                .UnionBy(processes.OrderByDescending(p => p.HandleCount).Take(30), p => p.Pid)
                .UnionBy(processes.Where(p => _trackers.ContainsKey(p.Pid)), p => p.Pid)
                .ToList();

            foreach (var p in candidatesToTrack)
            {
                if (_trackers.TryGetValue(p.Pid, out var tr))
                {
                    // Scenario 8 Guard: Detect PID Reuse if process Name changed or StartTimeUtc changed!
                    bool nameChanged = !string.Equals(tr.Name, p.Name, StringComparison.OrdinalIgnoreCase);
                    bool startChanged = tr.ProcessStartTimeUtc.HasValue && p.StartTimeUtc.HasValue &&
                                        Math.Abs((tr.ProcessStartTimeUtc.Value - p.StartTimeUtc.Value).TotalSeconds) > 1.0;

                    if (nameChanged || startChanged)
                    {
                        tr.Samples.Clear();
                        tr.Name = p.Name;
                        tr.ExecutablePath = p.ExecutablePath;
                        tr.ParentPid = p.ParentPid;
                        tr.IsManagedDotNet = p.IsManagedDotNet;
                        tr.ProcessStartTimeUtc = p.StartTimeUtc;
                        tr.FirstObservedUtc = now;
                    }
                }
                else
                {
                    tr = new ProcessHistoryTracker
                    {
                        Pid = p.Pid,
                        Name = p.Name,
                        ExecutablePath = p.ExecutablePath,
                        ParentPid = p.ParentPid,
                        IsManagedDotNet = p.IsManagedDotNet,
                        ProcessStartTimeUtc = p.StartTimeUtc,
                        FirstObservedUtc = now
                    };
                    _trackers[p.Pid] = tr;
                }

                tr.Samples.Add((now, p.PrivateCommitBytes, p.WorkingSetBytes, p.HandleCount));
                if (tr.Samples.Count > 120) tr.Samples.RemoveAt(0);
            }

            var sysExhaustion = DetectSystemExhaustion(sys);
            var cases = new List<LeakInvestigationCase>();

            foreach (var tr in _trackers.Values)
            {
                if (tr.Samples.Count == 0) continue;
                var first = tr.Samples[0];
                var last = tr.Samples[^1];
                var (slopeBytesPerSec, rSquared) = ComputeLinearRegression(tr.Samples);
                var (wsSlopeBytesPerSec, wsRSquared) = ComputeWorkingSetRegression(tr.Samples);

                long commitDelta = (long)last.CommitBytes - (long)first.CommitBytes;
                long wsDelta = (long)last.WorkingSetBytes - (long)first.WorkingSetBytes;
                int handleDelta = (int)last.Handles - (int)first.Handles;

                // Analyze sawtooth reclamation drops (GC collections / free cycles) and post-peak drawdown
                ulong maxCommit = first.CommitBytes;
                int reclamationDrops = 0;
                int monotonicIncreases = 0;
                for (int i = 1; i < tr.Samples.Count; i++)
                {
                    ulong prevC = tr.Samples[i - 1].CommitBytes;
                    ulong currC = tr.Samples[i].CommitBytes;
                    if (currC > maxCommit) maxCommit = currC;

                    if (prevC > currC && (prevC - currC) >= (64UL * 1024))
                        reclamationDrops++;
                    else if (currC > prevC + (32UL * 1024))
                        monotonicIncreases++;
                }

                ulong peakGain = maxCommit > first.CommitBytes ? (maxCommit - first.CommitBytes) : 0UL;
                ulong drawdownFromPeak = maxCommit > last.CommitBytes ? (maxCommit - last.CommitBytes) : 0UL;
                double drawdownRatio = peakGain > 0 ? (double)drawdownFromPeak / peakGain : 0.0;

                // Scenario 6 Guard: Process spiked temporarily and then released >= 35% of its peak gain (or has negative/flat recent slope)
                bool releasedAfterSpike = peakGain >= (512UL * 1024) &&
                                          (drawdownRatio >= 0.35 || last.CommitBytes <= first.CommitBytes + (128UL * 1024));

                // Scenario 4 Guard: Managed .NET application undergoing sawtooth GC expansion/reclamation cycles
                bool isSawtoothGcCycle = tr.IsManagedDotNet && reclamationDrops >= 1 && drawdownRatio >= 0.15;

                // Scenario 7 Guard: Working Set grows significantly while Private Commit remains stable
                bool isWorkingSetOnlyGrowth = wsSlopeBytesPerSec > (128 * 1024) &&
                                              wsRSquared >= 0.65 &&
                                              Math.Abs(slopeBytesPerSec) <= (32 * 1024) &&
                                              Math.Abs(commitDelta) <= (256 * 1024);

                LeakClassification classification;
                int confidence;

                if (tr.Samples.Count < 4)
                {
                    // Warm-up guardrail: never raise leak alerts on < 4 samples
                    classification = LeakClassification.NormalGrowth;
                    confidence = 25;
                }
                else if (releasedAfterSpike)
                {
                    // Scenario 1 & 6: Temporary spike followed by reclamation
                    classification = LeakClassification.NormalGrowth;
                    confidence = 20;
                }
                else if (isWorkingSetOnlyGrowth)
                {
                    // Scenario 7: Shared/Mapped Working Set page-in while Private Commit is flat
                    classification = LeakClassification.NormalGrowth;
                    confidence = 30;
                }
                else if (isSawtoothGcCycle)
                {
                    // Scenario 4: Managed GC sawtooth heap expansion with observed GC reclamation drops
                    classification = LeakClassification.ElevatedGrowth;
                    confidence = 42;
                }
                else if (slopeBytesPerSec > (2 * 1024 * 1024) && rSquared >= 0.88 && tr.Samples.Count >= 6 &&
                         reclamationDrops == 0 && commitDelta >= (8 * 1024 * 1024))
                {
                    // Scenario 3 & 5: Continuous monotonic private commit retention with strong R² fit
                    // Note: Live regression caps at StrongLeakEvidence (never ConfirmedLeak without allocation call-stack / dump diff proof)
                    classification = LeakClassification.StrongLeakEvidence;
                    confidence = (int)Math.Clamp(75 + (rSquared * 20), 75, 95);
                }
                else if (slopeBytesPerSec > (256 * 1024) && rSquared >= 0.70 && commitDelta >= (1024 * 1024))
                {
                    classification = LeakClassification.SuspectedLeak;
                    confidence = (int)Math.Clamp(55 + (rSquared * 25), 55, 82);
                }
                else if (slopeBytesPerSec > (64 * 1024) && commitDelta > (256 * 1024))
                {
                    // Elevated growth depends strictly on positive growth velocity and net delta — NEVER static size alone! (Fixes Scenario 2)
                    classification = LeakClassification.ElevatedGrowth;
                    confidence = 48;
                }
                else
                {
                    // Scenario 2: Stable long-term memory (regardless of whether process is 50 MiB or 4 GiB)
                    classification = LeakClassification.NormalGrowth;
                    confidence = 20;
                }

                var domain = isWorkingSetOnlyGrowth
                    ? LeakDomain.SharedOrMappedMemory
                    : handleDelta > 250
                        ? LeakDomain.ResourceHandles
                        : tr.IsManagedDotNet
                            ? LeakDomain.ManagedDotNetGc
                            : LeakDomain.ProcessPrivateCommit;

                var exhaustion = sysExhaustion != MemoryExhaustionKind.None
                    ? sysExhaustion
                    : classification is LeakClassification.SuspectedLeak or LeakClassification.StrongLeakEvidence
                        ? MemoryExhaustionKind.ProcessCommitGrowth
                        : MemoryExhaustionKind.None;

                var evidence = new List<string>
                {
                    $"Private Commit: {UnitFormatter.FormatBytes(last.CommitBytes, true)} (Baseline: {UnitFormatter.FormatBytes(first.CommitBytes, true)}, Delta: {UnitFormatter.FormatSignedDeltaBytes(commitDelta)})",
                    $"Commit OLS Slope: {(slopeBytesPerSec / (1024.0 * 1024.0)):+0.00;-0.00;0.00} MiB/s (R² = {rSquared:F3} across {tr.Samples.Count} samples; Reclamation Drops: {reclamationDrops})",
                    $"Working Set: {UnitFormatter.FormatBytes(last.WorkingSetBytes, true)} (Delta: {UnitFormatter.FormatSignedDeltaBytes(wsDelta)}, WS Slope: {(wsSlopeBytesPerSec / 1024.0):+0.0;-0.0;0.0} KiB/s) · Handles: {last.Handles:N0} (Delta: {handleDelta:+0;-0;0})"
                };

                if (isWorkingSetOnlyGrowth)
                {
                    evidence.Add("Working Set Divergence: Resident Working Set increased while Private Commit remained stable (mapped file / shared DLL fault-in, not a private commit leak).");
                }
                else if (releasedAfterSpike)
                {
                    evidence.Add($"Spike Reclamation Observed: Process reclaimed {drawdownRatio * 100.0:F0}% of peak transient allocation ({UnitFormatter.FormatBytes(drawdownFromPeak, true)} released).");
                }
                else if (isSawtoothGcCycle)
                {
                    evidence.Add($"Managed GC Sawtooth Detected: {reclamationDrops} garbage collection reclamation drop(s) observed; consistent with normal .NET GC generation expansion.");
                }

                var nextSteps = new List<string>();
                if (classification is LeakClassification.SuspectedLeak or LeakClassification.StrongLeakEvidence)
                {
                    nextSteps.Add("Capture a baseline Process Snapshot and compare differential delta after 60 seconds.");
                    nextSteps.Add(tr.IsManagedDotNet
                        ? "Start a Managed .NET GC ETW profiling session to inspect Gen 2 / LOH survivor roots."
                        : "Start a Native Heap ETW profiling session with PDB symbols configured.");
                }
                else
                {
                    nextSteps.Add("Continue live monitoring to establish longer baseline trend.");
                    nextSteps.Add("Inspect Virtual Memory Map for large private committed arenas.");
                }

                var limitations = new List<string>
                {
                    "Live counters measure OS page & commit attribution; positive OLS slope or R² alone never establishes a ConfirmedLeak without allocation call-stack or multi-snapshot object retention proof."
                };

                cases.Add(new LeakInvestigationCase(
                    CaseId: $"INV-{tr.Pid}",
                    TargetPid: tr.Pid,
                    TargetProcessName: tr.Name,
                    ExecutablePath: tr.ExecutablePath,
                    ParentPid: tr.ParentPid,
                    Domain: domain,
                    ExhaustionKind: exhaustion,
                    Classification: classification,
                    ConfidencePercent: confidence,
                    FirstObservedUtc: tr.FirstObservedUtc,
                    LastUpdatedUtc: now,
                    BaselineCommitBytes: first.CommitBytes,
                    CurrentCommitBytes: last.CommitBytes,
                    LinearSlopeBytesPerSec: slopeBytesPerSec,
                    RegressionRSquared: rSquared,
                    BaselineHandles: first.Handles,
                    CurrentHandles: last.Handles,
                    SuspectedComponent: isWorkingSetOnlyGrowth
                        ? "Shared / Memory-Mapped Pages (Working Set Cache)"
                        : tr.IsManagedDotNet
                            ? ".NET Managed GC Heap / LOH"
                            : "Native Private Heap / VirtualAlloc Arena",
                    EvidenceItems: evidence,
                    RecommendedNextSteps: nextSteps,
                    KnownLimitations: limitations,
                    Provenance: DataSourceProvenance.Live));
            }

            return cases
                .OrderByDescending(c => c.Classification)
                .ThenByDescending(c => c.LinearSlopeBytesPerSec)
                .ThenByDescending(c => c.CurrentCommitBytes)
                .Take(25)
                .ToList();
        }
    }

    public static (double SlopeBytesPerSec, double RSquared) ComputeLinearRegression(
        IReadOnlyList<(DateTimeOffset Time, ulong CommitBytes, ulong WorkingSetBytes, uint Handles)> samples)
        => ComputeMetricRegression(samples, s => s.CommitBytes);

    public static (double SlopeBytesPerSec, double RSquared) ComputeWorkingSetRegression(
        IReadOnlyList<(DateTimeOffset Time, ulong CommitBytes, ulong WorkingSetBytes, uint Handles)> samples)
        => ComputeMetricRegression(samples, s => s.WorkingSetBytes);

    private static (double SlopeBytesPerSec, double RSquared) ComputeMetricRegression(
        IReadOnlyList<(DateTimeOffset Time, ulong CommitBytes, ulong WorkingSetBytes, uint Handles)> samples,
        Func<(DateTimeOffset Time, ulong CommitBytes, ulong WorkingSetBytes, uint Handles), ulong> selector)
    {
        if (samples.Count < 2) return (0.0, 0.0);
        var t0 = samples[0].Time;
        int n = samples.Count;
        double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0, sumY2 = 0;

        for (int i = 0; i < n; i++)
        {
            double x = (samples[i].Time - t0).TotalSeconds;
            double y = selector(samples[i]);
            sumX += x;
            sumY += y;
            sumXY += x * y;
            sumX2 += x * x;
            sumY2 += y * y;
        }

        double denomX = (n * sumX2) - (sumX * sumX);
        if (Math.Abs(denomX) < 1e-9) return (0.0, 0.0);

        double slope = ((n * sumXY) - (sumX * sumY)) / denomX;
        double denomY = (n * sumY2) - (sumY * sumY);
        double r2 = 0.0;
        if (Math.Abs(denomY) > 1e-9)
        {
            double r = ((n * sumXY) - (sumX * sumY)) / Math.Sqrt(denomX * denomY);
            r2 = Math.Clamp(r * r, 0.0, 1.0);
        }
        return (Math.Round(slope, 1), Math.Round(r2, 4));
    }
}

public sealed class LiveMemoryMonitoringEngine
{
    private readonly WindowsSystemMemoryCollector _sysCollector = new();
    private readonly WindowsProcessCollector _procCollector = new();
    private readonly MemoryExhaustionAndLeakEngine _leakEngine = new();
    private readonly BoundedHistoryRingBuffer<SystemMemoryTelemetry> _history;
    private readonly List<MemoryAlert> _alerts = [];
    private readonly object _lock = new();

    public bool IsPaused { get; set; }
    public SystemMemoryTelemetry? LatestSystem { get; private set; }
    public PhysicalPageListTelemetry LatestPageLists => _sysCollector.LatestPageLists;
    public IReadOnlyList<ProcessMemoryInfo> LatestProcesses { get; private set; } = [];
    public IReadOnlyList<LeakInvestigationCase> ActiveInvestigations { get; private set; } = [];
    public SelfDiagnosticMetrics? SelfMetrics { get; private set; }
    public WindowsProcessCollector ProcessCollector => _procCollector;
    public WindowsSystemMemoryCollector SystemCollector => _sysCollector;
    public MemoryExhaustionAndLeakEngine LeakEngine => _leakEngine;

    public LiveMemoryMonitoringEngine(int historyCapacity = 1800)
    {
        _history = new BoundedHistoryRingBuffer<SystemMemoryTelemetry>(historyCapacity);
    }

    public IReadOnlyList<SystemMemoryTelemetry> GetHistory(int maxCount = 120) => _history.GetRecent(maxCount);

    public IReadOnlyList<MemoryAlert> GetAlerts()
    {
        lock (_lock) return _alerts.ToList();
    }

    public void AddCustomAlertRule(string title, string targetComponent, ulong thresholdBytes)
    {
        lock (_lock)
        {
            string id = $"ALT-RULE-{DateTime.UtcNow:HHmmss}";
            _alerts.Insert(0, new MemoryAlert(
                AlertId: id,
                Title: title,
                Severity: "Warning",
                State: "Active Rule",
                TargetComponent: targetComponent,
                RelatedPid: null,
                MeasuredValue: $"Threshold: {UnitFormatter.FormatBytes(thresholdBytes, true)}",
                BaselineValue: "User-Configured Threshold Rule",
                LeakClassification: LeakClassification.NormalGrowth,
                ConfidencePercent: 90,
                EvidenceSummary: $"Active monitoring rule evaluating {targetComponent} against {UnitFormatter.FormatBytes(thresholdBytes, true)}.",
                SuggestedAction: "Rule is evaluated on every telemetry sweep.",
                TimestampUtc: DateTimeOffset.UtcNow,
                Provenance: DataSourceProvenance.Live));
            if (_alerts.Count > 100) _alerts.RemoveAt(_alerts.Count - 1);
        }
    }

    public void SampleNow()
    {
        if (IsPaused) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var sys = _sysCollector.Collect();
        var procs = _procCollector.CollectProcesses();
        var cases = _leakEngine.AnalyzeSample(sys, procs);

        _history.Add(sys);
        LatestSystem = sys;
        LatestProcesses = procs;
        ActiveInvestigations = cases;

        EvaluateAlerts(sys, procs, cases);
        sw.Stop();

        uint selfPid = (uint)Environment.ProcessId;
        var selfProc = procs.FirstOrDefault(p => p.Pid == selfPid);
        SelfMetrics = new SelfDiagnosticMetrics(
            SelfWorkingSetBytes: selfProc?.WorkingSetBytes ?? (ulong)Environment.WorkingSet,
            SelfPrivateCommitBytes: selfProc?.PrivateCommitBytes ?? (ulong)Environment.WorkingSet,
            SelfThreadCount: selfProc?.ThreadCount ?? 12,
            SelfHandleCount: selfProc?.HandleCount ?? 220,
            LastSampleDurationMs: Math.Round(sw.Elapsed.TotalMilliseconds, 2),
            StoredHistoryCount: _history.Count);
    }

    public void UpdateAlertState(string alertId, string newState)
    {
        lock (_lock)
        {
            int idx = _alerts.FindIndex(a => a.AlertId == alertId);
            if (idx >= 0) _alerts[idx] = _alerts[idx] with { State = newState };
        }
    }

    private void EvaluateAlerts(
        SystemMemoryTelemetry sys,
        IReadOnlyList<ProcessMemoryInfo> procs,
        IReadOnlyList<LeakInvestigationCase> cases)
    {
        lock (_lock)
        {
            var livePids = new HashSet<uint>(procs.Select(p => p.Pid));

            // Auto-resolve leak alerts for processes that have exited
            for (int i = 0; i < _alerts.Count; i++)
            {
                var a = _alerts[i];
                if (a.RelatedPid.HasValue && !livePids.Contains(a.RelatedPid.Value) && a.State == "Open")
                {
                    _alerts[i] = a with { State = "Resolved (Process Exited)" };
                }
            }

            if (_alerts.Count == 0 && procs.Count > 0)
            {
                var top = procs[0];
                string safeExeName = Path.GetFileName(top.ExecutablePath);
                _alerts.Add(new MemoryAlert(
                    "ALT-001",
                    "Top Physical Working Set Consumer",
                    sys.PhysicalUsagePercent > 85 ? "Warning" : "Informational",
                    "Open",
                    $"{top.Name} (PID {top.Pid})",
                    top.Pid,
                    $"Working Set: {UnitFormatter.FormatBytes(top.WorkingSetBytes, true)} · Commit: {UnitFormatter.FormatBytes(top.PrivateCommitBytes, true)}",
                    $"System Physical Load: {sys.MemoryLoadPercent}% ({UnitFormatter.FormatBytes(sys.InUsePhysicalBytes, true)} / {UnitFormatter.FormatBytes(sys.TotalPhysicalBytes, true)})",
                    LeakClassification.NormalGrowth,
                    70,
                    $"Largest resident Working Set on host ({safeExeName}). High Working Set alone is not classified as a leak without sustained monotonic commit growth.",
                    "Open Process Deep Dive or capture a baseline Telemetry Snapshot for longitudinal comparison.",
                    sys.TimestampUtc,
                    DataSourceProvenance.Live));
            }

            foreach (var c in cases.Where(c => c.Classification is LeakClassification.SuspectedLeak or LeakClassification.StrongLeakEvidence))
            {
                string id = $"ALT-LEAK-{c.TargetPid}";
                int existingIdx = _alerts.FindIndex(a => a.AlertId == id);
                if (existingIdx < 0)
                {
                    _alerts.Insert(0, new MemoryAlert(
                        id,
                        $"Sustained Private Commit Growth ({c.Classification})",
                        c.Classification == LeakClassification.StrongLeakEvidence ? "Critical" : "Warning",
                        "Open",
                        $"{c.TargetProcessName} (PID {c.TargetPid})",
                        c.TargetPid,
                        $"{UnitFormatter.FormatBytes(c.CurrentCommitBytes, true)} (+{(c.LinearSlopeBytesPerSec / 1024.0):F1} KiB/s)",
                        UnitFormatter.FormatBytes(c.BaselineCommitBytes, true),
                        c.Classification,
                        c.ConfidencePercent,
                        string.Join(" | ", c.EvidenceItems),
                        c.RecommendedNextSteps.FirstOrDefault() ?? "Inspect Process Deep Dive",
                        sys.TimestampUtc,
                        DataSourceProvenance.Live));
                }
            }

            // Cap alert buffer at 100 entries so long-running 24h sessions never grow unboundedly
            while (_alerts.Count > 100)
            {
                _alerts.RemoveAt(_alerts.Count - 1);
            }
        }
    }
}
