using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using RAMYXEN.Core.Models;
using RAMYXEN.Diagnostics;
using RAMYXEN.Forensics;
using RAMYXEN.Infrastructure.Windows;
using RAMYXEN.Memory.Engine;
using RAMYXEN.Profiler;

namespace RAMYXEN.Application.Services;

public sealed record ProcessDeepDivePayload(
    uint Pid,
    long SelectionVersion,
    bool IsLoading,
    bool IsRestricted,
    string? ErrorMessage,
    IReadOnlyList<VirtualMemoryRegion> Regions,
    IReadOnlyList<ProcessModuleInfo> Modules,
    IReadOnlyList<ProcessThreadInfo> Threads,
    IReadOnlyList<ProcessHeapInfo>? Heaps,
    bool HeapsLoaded,
    bool HeapsLoading,
    long LightweightElapsedMs,
    long HeapElapsedMs,
    bool HeapsTimedOut = false,
    int TotalRegionsCount = 0,
    bool RegionsTruncated = false,
    int TotalModulesCount = 0,
    bool ModulesTruncated = false,
    bool RegionsAccessDenied = false);

public sealed record ProfilerSnapshotPayload(
    uint Pid,
    long Version,
    ManagedGcTelemetry GcTelemetry,
    IReadOnlyList<AllocationHotspotItem> Hotspots,
    long ElapsedMs);

public sealed record ForensicLivePayload(
    uint Pid,
    long Version,
    IReadOnlyList<ForensicFinding> Findings,
    long ElapsedMs);

public sealed record ForensicDumpPayload(
    ForensicSourceDescriptor Source,
    IReadOnlyList<ForensicFinding> Findings,
    byte[] PreviewBytes,
    long ElapsedMs);

/// <summary>
/// Non-blocking, cancellable, version-guarded asynchronous process inspection coordinator.
/// Separates lightweight process inspection from expensive native heap walks via dedicated
/// concurrency lanes and an isolated killable out-of-process worker (RAMYXEN.HeapWorker.exe)
/// so a stalled or huge external process can never freeze the UI or starve lightweight selection (F-003, F-013, F-014).
/// </summary>
public sealed class ProcessInspectionCoordinator : IDisposable
{
    private readonly LiveMemoryMonitoringEngine _monitor;
    private readonly StructuredDiagnosticLogger _logger;
    private readonly SemaphoreSlim _lightweightGate = new(3, 3);
    private readonly SemaphoreSlim _heapGate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly ConcurrentDictionary<(uint Pid, long Version), Task<ProcessDeepDivePayload?>> _inFlightHeaps = new();
    private readonly ConcurrentDictionary<(uint Pid, long StartTicksUtc), DateTimeOffset> _timedOutHeapCooldown = new();

    private CancellationTokenSource? _activeCts;
    private long _selectionVersion;
    private ProcessDeepDivePayload? _latestPayload;
    private volatile bool _disposed;

    public int HeapWorkerTimeoutMs { get; set; } = 3000;
    public long CurrentSelectionVersion => Interlocked.Read(ref _selectionVersion);

    public ProcessDeepDivePayload? LatestPayload
    {
        get { lock (_stateLock) return _latestPayload; }
    }

    public ProcessInspectionCoordinator(LiveMemoryMonitoringEngine monitor, StructuredDiagnosticLogger logger)
    {
        _monitor = monitor;
        _logger = logger;
    }

    /// <summary>
    /// Immediately advances selection version, cancels any in-flight inspection for a prior process,
    /// and returns a lightweight placeholder payload for instant (&lt;5ms) UI rendering.
    /// </summary>
    public (long Version, CancellationToken Token, ProcessDeepDivePayload Placeholder) BeginSelection(uint pid, bool isRestricted)
    {
        if (_disposed)
        {
            var empty = new ProcessDeepDivePayload(pid, Interlocked.Read(ref _selectionVersion), false, isRestricted, "Coordinator disposed", [], [], [], null, false, false, 0, 0);
            return (empty.SelectionVersion, new CancellationToken(true), empty);
        }

        long version = Interlocked.Increment(ref _selectionVersion);
        CancellationTokenSource nextCts = new();
        CancellationToken nextToken = nextCts.Token;
        CancellationTokenSource? previousCts;

        var placeholder = new ProcessDeepDivePayload(
            Pid: pid,
            SelectionVersion: version,
            IsLoading: !isRestricted,
            IsRestricted: isRestricted,
            ErrorMessage: null,
            Regions: [],
            Modules: [],
            Threads: [],
            Heaps: null,
            HeapsLoaded: false,
            HeapsLoading: false,
            LightweightElapsedMs: 0,
            HeapElapsedMs: 0);

        lock (_stateLock)
        {
            if (_disposed)
            {
                nextCts.Dispose();
                return (version, new CancellationToken(true), placeholder with { IsLoading = false });
            }
            previousCts = _activeCts;
            _activeCts = nextCts;
            _latestPayload = placeholder;
        }

        if (previousCts is not null)
        {
            // Cancel only; do not eagerly Dispose while background workers may still hold/link its Token (F-014)
            try { previousCts.Cancel(); } catch { }
        }

        return (version, nextToken, placeholder);
    }

    /// <summary>
    /// Single authoritative selection entry point (F-013): advances _selectionVersion exactly once,
    /// returns the instant placeholder immediately for synchronous UI paint, and launches the background work.
    /// </summary>
    public (ProcessDeepDivePayload Placeholder, Task<ProcessDeepDivePayload?> Completion) StartLightweightInspection(
        uint pid,
        bool isRestricted,
        CancellationToken externalCt = default)
    {
        var (version, selectionToken, placeholder) = BeginSelection(pid, isRestricted);
        Task<ProcessDeepDivePayload?> task = RunLightweightCoreAsync(pid, isRestricted, version, selectionToken, externalCt);
        return (placeholder, task);
    }

    /// <summary>
    /// Convenience async wrapper calling StartLightweightInspection (single version increment).
    /// </summary>
    public Task<ProcessDeepDivePayload?> InspectLightweightAsync(
        uint pid,
        bool isRestricted,
        CancellationToken externalCt = default)
        => StartLightweightInspection(pid, isRestricted, externalCt).Completion;

    private async Task<ProcessDeepDivePayload?> RunLightweightCoreAsync(
        uint pid,
        bool isRestricted,
        long version,
        CancellationToken selectionToken,
        CancellationToken externalCt)
    {
        if (_disposed)
            return null;

        if (isRestricted)
        {
            var restrictedPayload = new ProcessDeepDivePayload(
                Pid: pid,
                SelectionVersion: version,
                IsLoading: false,
                IsRestricted: true,
                ErrorMessage: null,
                Regions: [],
                Modules: [],
                Threads: [],
                Heaps: null,
                HeapsLoaded: false,
                HeapsLoading: false,
                LightweightElapsedMs: 0,
                HeapElapsedMs: 0);
            TryStoreIfCurrent(restrictedPayload);
            return restrictedPayload;
        }

        CancellationTokenSource? linked;
        try
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(selectionToken, externalCt);
        }
        catch (ObjectDisposedException)
        {
            return null;
        }

        using (linked)
        {
            var ct = linked.Token;
            bool acquired = false;
            try
            {
                await _lightweightGate.WaitAsync(ct).ConfigureAwait(false);
                acquired = true;

                if (_disposed || IsStale(version) || ct.IsCancellationRequested)
                    return null;

                var sw = Stopwatch.StartNew();
                var result = await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    var (regions, totalRegions, regionsTrunc, regionsDenied) = _monitor.ProcessCollector.GetProcessVirtualRegionsWithTotal(pid, 512);

                    ct.ThrowIfCancellationRequested();
                    var (modules, totalMods, modsTrunc) = _monitor.ProcessCollector.GetProcessModulesWithTotal(pid);

                    ct.ThrowIfCancellationRequested();
                    var threads = _monitor.ProcessCollector.GetProcessThreads(pid);

                    sw.Stop();
                    return new ProcessDeepDivePayload(
                        Pid: pid,
                        SelectionVersion: version,
                        IsLoading: false,
                        IsRestricted: regionsDenied && regions.Count == 0 && modules.Count == 0,
                        ErrorMessage: null,
                        Regions: regions,
                        Modules: modules,
                        Threads: threads,
                        Heaps: null,
                        HeapsLoaded: false,
                        HeapsLoading: false,
                        LightweightElapsedMs: sw.ElapsedMilliseconds,
                        HeapElapsedMs: 0,
                        HeapsTimedOut: false,
                        TotalRegionsCount: totalRegions,
                        RegionsTruncated: regionsTrunc,
                        TotalModulesCount: totalMods,
                        ModulesTruncated: modsTrunc,
                        RegionsAccessDenied: regionsDenied);
                }, ct).ConfigureAwait(false);

                if (IsStale(version))
                    return null;

                TryStoreIfCurrent(result);
                if (result.LightweightElapsedMs > 500)
                {
                    _logger.Warning("ProcessInspector", $"Lightweight inspection for PID {pid} took {result.LightweightElapsedMs} ms.");
                }
                return result;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.Warning("ProcessInspector", $"Inspection error for PID {pid}: {ex.Message}");
                if (IsStale(version))
                    return null;

                var errPayload = new ProcessDeepDivePayload(
                    Pid: pid,
                    SelectionVersion: version,
                    IsLoading: false,
                    IsRestricted: false,
                    ErrorMessage: ex.Message,
                    Regions: [],
                    Modules: [],
                    Threads: [],
                    Heaps: null,
                    HeapsLoaded: false,
                    HeapsLoading: false,
                    LightweightElapsedMs: 0,
                    HeapElapsedMs: 0);
                TryStoreIfCurrent(errPayload);
                return errPayload;
            }
            finally
            {
                if (acquired)
                {
                    try { _lightweightGate.Release(); } catch { }
                }
            }
        }
    }

    /// <summary>
    /// On-demand Native Heap walk isolated onto a dedicated semaphore lane and executed via the
    /// out-of-process killable RAMYXEN.HeapWorker.exe for external PIDs with a real external timeout (F-003).
    /// </summary>
    public Task<ProcessDeepDivePayload?> LoadHeapsOnDemandAsync(
        uint pid,
        long expectedVersion,
        CancellationToken externalCt = default)
    {
        if (_disposed || IsStale(expectedVersion))
            return Task.FromResult<ProcessDeepDivePayload?>(null);

        var key = (pid, expectedVersion);
        return _inFlightHeaps.GetOrAdd(key, _ => LoadHeapsCoreAsync(pid, expectedVersion, externalCt));
    }

    private static long TryGetProcessStartTicksUtc(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.StartTime.ToUniversalTime().Ticks;
        }
        catch
        {
            return 0;
        }
    }

    private void PruneExpiredCooldowns(DateTimeOffset now)
    {
        foreach (var kv in _timedOutHeapCooldown)
        {
            if (kv.Value <= now)
                _timedOutHeapCooldown.TryRemove(kv.Key, out _);
        }
    }

    private async Task<ProcessDeepDivePayload?> LoadHeapsCoreAsync(
        uint pid,
        long expectedVersion,
        CancellationToken externalCt)
    {
        try
        {
            CancellationToken selectionToken;
            lock (_stateLock)
            {
                if (_disposed || IsStale(expectedVersion))
                    return null;

                if (_latestPayload is not null && _latestPayload.SelectionVersion == expectedVersion)
                {
                    if (_latestPayload.HeapsLoaded && !_latestPayload.HeapsTimedOut)
                        return _latestPayload;
                    _latestPayload = _latestPayload with { HeapsLoading = true };
                }
                selectionToken = _activeCts?.Token ?? CancellationToken.None;
            }

            long startTicksUtc = TryGetProcessStartTicksUtc(pid);
            var cooldownKey = (pid, startTicksUtc);
            var nowUtc = DateTimeOffset.UtcNow;
            PruneExpiredCooldowns(nowUtc);

            // Circuit breaker keyed on (Pid, StartTimeUtc) so recycled PIDs are never falsely suppressed (R2-NEW-008)
            if (_timedOutHeapCooldown.TryGetValue(cooldownKey, out var cooldownUntil) && nowUtc < cooldownUntil)
            {
                return StoreHeapOutcome(expectedVersion, [], true, 0,
                    "Heap walk skipped (recent timeout cooldown active for this process instance). Click Retry after cooldown.");
            }

            CancellationTokenSource? linked;
            try
            {
                linked = CancellationTokenSource.CreateLinkedTokenSource(selectionToken, externalCt);
            }
            catch (ObjectDisposedException)
            {
                return null;
            }

            using (linked)
            {
                var ct = linked.Token;
                bool acquired = false;
                try
                {
                    await _heapGate.WaitAsync(ct).ConfigureAwait(false);
                    acquired = true;

                    if (_disposed || IsStale(expectedVersion) || ct.IsCancellationRequested)
                        return null;

                    var sw = Stopwatch.StartNew();
                    bool timedOut = false;
                    string? heapErr = null;
                    IReadOnlyList<ProcessHeapInfo> heaps;

                    if (pid == (uint)Environment.ProcessId)
                    {
                        heaps = await Task.Run(() =>
                        {
                            ct.ThrowIfCancellationRequested();
                            return _monitor.ProcessCollector.GetProcessHeaps(pid);
                        }, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        long expectedStartFt = startTicksUtc > 0 ? new DateTime(startTicksUtc, DateTimeKind.Utc).ToFileTimeUtc() : 0;
                        var workerResult = await RunOutOfProcessHeapWorkerAsync(pid, expectedStartFt, HeapWorkerTimeoutMs, ct).ConfigureAwait(false);
                        heaps = workerResult.Heaps;
                        timedOut = workerResult.TimedOut;
                        heapErr = workerResult.ErrorMessage;
                    }

                    sw.Stop();

                    if (IsStale(expectedVersion))
                        return null;

                    if (timedOut)
                    {
                        _timedOutHeapCooldown[cooldownKey] = DateTimeOffset.UtcNow.AddSeconds(60);
                        _logger.Warning("ProcessInspector", $"Out-of-process heap worker for PID {pid} timed out after {sw.ElapsedMilliseconds} ms and was terminated.");
                    }
                    else
                    {
                        _timedOutHeapCooldown.TryRemove(cooldownKey, out _);
                        _logger.Info("ProcessInspector", $"On-demand bounded heap walk for PID {pid} returned {heaps.Count} heaps in {sw.ElapsedMilliseconds} ms.");
                    }

                    return StoreHeapOutcome(expectedVersion, heaps, timedOut, sw.ElapsedMilliseconds, heapErr);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
                catch (ObjectDisposedException)
                {
                    return null;
                }
                catch (Exception ex)
                {
                    _logger.Warning("ProcessInspector", $"Heap walk failed for PID {pid}: {ex.Message}");
                    return StoreHeapOutcome(expectedVersion, [], false, 0, $"Heap walk error: {ex.Message}");
                }
                finally
                {
                    if (acquired)
                    {
                        try { _heapGate.Release(); } catch { }
                    }
                }
            }
        }
        finally
        {
            ResetHeapsLoadingIfCurrent(expectedVersion);
            _inFlightHeaps.TryRemove((pid, expectedVersion), out _);
        }
    }

    private ProcessDeepDivePayload? StoreHeapOutcome(
        long expectedVersion,
        IReadOnlyList<ProcessHeapInfo> heaps,
        bool timedOut,
        long elapsedMs,
        string? errorMessage)
    {
        lock (_stateLock)
        {
            var current = _latestPayload;
            if (current is null || current.SelectionVersion != expectedVersion)
                return null;

            var updated = current with
            {
                Heaps = heaps,
                HeapsLoaded = true,
                HeapsLoading = false,
                HeapsTimedOut = timedOut,
                HeapElapsedMs = elapsedMs,
                ErrorMessage = errorMessage ?? current.ErrorMessage
            };
            _latestPayload = updated;
            return updated;
        }
    }

    private void ResetHeapsLoadingIfCurrent(long expectedVersion)
    {
        lock (_stateLock)
        {
            if (_latestPayload is not null && _latestPayload.SelectionVersion == expectedVersion && _latestPayload.HeapsLoading)
            {
                _latestPayload = _latestPayload with { HeapsLoading = false };
            }
        }
    }

    private async Task<(IReadOnlyList<ProcessHeapInfo> Heaps, bool TimedOut, string? ErrorMessage)> RunOutOfProcessHeapWorkerAsync(
        uint pid,
        long expectedStartFileTimeUtc,
        int timeoutMs,
        CancellationToken ct)
    {
        string workerPath = Path.Combine(AppContext.BaseDirectory, "RAMYXEN.HeapWorker.exe");
        if (!File.Exists(workerPath))
        {
            _logger.Warning("ProcessInspector", $"RAMYXEN.HeapWorker.exe missing at {workerPath}; refusing unkillable in-process fallback for external PID {pid}.");
            return ([], false, "Isolated heap worker (RAMYXEN.HeapWorker.exe) not found.");
        }

        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName = workerPath,
            WorkingDirectory = AppContext.BaseDirectory,
            Arguments = expectedStartFileTimeUtc > 0 ? $"{pid} 64 {expectedStartFileTimeUtc}" : $"{pid} 64",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            if (!proc.Start())
                return ([], false, "Unable to launch RAMYXEN.HeapWorker.exe.");
        }
        catch (Exception ex)
        {
            return ([], false, $"Heap worker launch error: {ex.Message}");
        }

        using var timeoutCts = new CancellationTokenSource(Math.Clamp(timeoutMs, 250, 15_000));
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        // Drain BOTH stdout and stderr concurrently to eliminate pipe deadlocks (R2-NEW-006)
        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync();
        try
        {
            await proc.WaitForExitAsync(combined.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            bool wasTimeout = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested;
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(500);
                }
            }
            catch { }

            // Always observe stream tasks before disposing proc so zero unobserved exceptions can fire (R2-NEW-006)
            try { await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromMilliseconds(300)).ConfigureAwait(false); } catch { }

            if (wasTimeout)
            {
                return ([], true, $"Heap walk exceeded {timeoutMs} ms safety budget and isolated worker was terminated.");
            }
            throw;
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        int exitCode = proc.ExitCode;

        var (parsed, metaSeen, metaAbi, metaRc, errDetail) = ParseHeapWorkerOutput(stdout);

        if (exitCode == 3 || (metaSeen && metaAbi != 102))
        {
            string msg = $"Heap worker ABI mismatch (expected v102, got v{metaAbi}).";
            _logger.Warning("ProcessInspector", msg);
            return ([], false, msg);
        }
        if (exitCode == 4)
        {
            return ([], false, "Target process exited or PID was reused before heap walk began.");
        }
        if (exitCode != 0 || !metaSeen || metaRc != 0)
        {
            string reason = !string.IsNullOrWhiteSpace(errDetail)
                ? errDetail
                : (!string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : $"exitCode={exitCode}, rc={metaRc}");
            string msg = metaRc == -2
                ? $"Access denied inspecting heaps for PID {pid} ({reason})."
                : $"Isolated heap worker failed for PID {pid}: {reason}";
            _logger.Warning("ProcessInspector", msg);
            return ([], false, msg);
        }

        return (parsed, false, null);
    }

    private static (IReadOnlyList<ProcessHeapInfo> Heaps, bool MetaSeen, int MetaAbi, int MetaRc, string? ErrDetail) ParseHeapWorkerOutput(string stdout)
    {
        var list = new List<ProcessHeapInfo>();
        bool metaSeen = false;
        int metaAbi = 0;
        int metaRc = -6;
        string? errDetail = null;

        if (string.IsNullOrWhiteSpace(stdout))
            return (list, false, 0, -6, "Empty worker output");

        int index = 0;
        using var reader = new StringReader(stdout);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.StartsWith("META|", StringComparison.Ordinal))
            {
                var mp = line.Split('|');
                if (mp.Length >= 4 &&
                    int.TryParse(mp[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int abi) &&
                    int.TryParse(mp[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rc))
                {
                    metaSeen = true;
                    metaAbi = abi;
                    metaRc = rc;
                }
                continue;
            }

            if (line.StartsWith("ERR|", StringComparison.Ordinal))
            {
                var ep = line.Split('|');
                if (ep.Length >= 4)
                {
                    errDetail = $"{ep[3]} (status={ep[1]}, win32={ep[2]})";
                }
                continue;
            }

            if (!line.StartsWith("HEAP|", StringComparison.Ordinal))
                continue;

            var parts = line.Split('|');
            if (parts.Length < 7) continue;

            if (!ulong.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong heapId)) continue;
            if (!uint.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint flags)) continue;
            if (!uint.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint blocks)) continue;
            if (!ulong.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong committed)) continue;
            if (!ulong.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong allocated)) continue;
            if (!ulong.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong free)) continue;

            bool bounded = (flags & 0x80000000U) != 0;
            var prov = bounded ? DataSourceProvenance.Partial : DataSourceProvenance.Live;
            string name = (flags & 1U) != 0 || index == 0
                ? (bounded ? "Default Process Heap (Bounded Sample)" : "Default Process Heap (NT Segment/LFH)")
                : (bounded ? $"Private Heap #{index + 1} (Bounded Sample)" : $"Private Heap #{index + 1}");

            list.Add(new ProcessHeapInfo(
                heapId,
                name,
                flags & ~0x80000000U,
                blocks,
                committed,
                allocated,
                free,
                prov));
            index++;
        }
        return (list, metaSeen, metaAbi, metaRc, errDetail);
    }

    // ------------------------------------------------------------------------
    // Off-UI-Thread Async Inspection Helpers for Profiler, Hex, Strings & Forensics (F-002, F-007)
    // ------------------------------------------------------------------------

    public async Task<ProfilerSnapshotPayload?> AnalyzeProfilerAsync(
        AllocationAndRuntimeProfiler profiler,
        uint pid,
        string processName,
        IReadOnlyList<ProcessHeapInfo>? existingHeaps,
        CancellationToken ct = default)
    {
        if (_disposed) return null;
        long ver = CurrentSelectionVersion;
        var sw = Stopwatch.StartNew();
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var gc = profiler.CollectManagedGcTelemetry(pid, processName);
            ct.ThrowIfCancellationRequested();
            var (regions, _, _, _) = _monitor.ProcessCollector.GetProcessVirtualRegionsWithTotal(pid, 1024);
            var (modules, _, _) = _monitor.ProcessCollector.GetProcessModulesWithTotal(pid);
            var hotspots = profiler.AnalyzeProcessAllocationHotspots(pid, regions, modules, existingHeaps);
            sw.Stop();
            return new ProfilerSnapshotPayload(pid, ver, gc, hotspots, sw.ElapsedMilliseconds);
        }, ct).ConfigureAwait(false);
    }

    public Task<byte[]> ReadMemoryHexAsync(uint pid, ulong address, int length = 256, CancellationToken ct = default)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return _monitor.ProcessCollector.ReadMemoryRegionHex(pid, address, length);
        }, ct);

    public Task<IReadOnlyList<ExtractedMemoryString>> SearchStringsAsync(
        uint pid,
        string query,
        int minLength = 5,
        int maxResults = 120,
        CancellationToken ct = default)
        => Task.Run(() => _monitor.ProcessCollector.SearchProcessStrings(pid, query, minLength, maxResults, ct), ct);

    public async Task<ForensicLivePayload> AnalyzeForensicsLiveAsync(
        ForensicAnalysisEngine forensics,
        uint pid,
        string processName,
        CancellationToken ct = default)
    {
        long ver = CurrentSelectionVersion;
        var sw = Stopwatch.StartNew();
        var findings = await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return forensics.AnalyzeLiveProcess(pid, processName);
        }, ct).ConfigureAwait(false);
        sw.Stop();
        return new ForensicLivePayload(pid, ver, findings, sw.ElapsedMilliseconds);
    }

    public async Task<ForensicDumpPayload> AnalyzeForensicsDumpAsync(
        ForensicAnalysisEngine forensics,
        string dumpPath,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var (src, findings, preview) = await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return forensics.AnalyzeMemoryDumpFile(dumpPath);
        }, ct).ConfigureAwait(false);
        sw.Stop();
        return new ForensicDumpPayload(src, findings, preview, sw.ElapsedMilliseconds);
    }

    public bool IsStale(long version) => Interlocked.Read(ref _selectionVersion) != version;

    private void TryStoreIfCurrent(ProcessDeepDivePayload payload)
    {
        lock (_stateLock)
        {
            if (Interlocked.Read(ref _selectionVersion) == payload.SelectionVersion)
            {
                // Preserve concurrent Heap Walk loading/loaded state for the same SelectionVersion (R2-NEW-009)
                var existing = _latestPayload;
                if (existing is not null &&
                    existing.SelectionVersion == payload.SelectionVersion &&
                    (existing.HeapsLoading || existing.HeapsLoaded))
                {
                    payload = payload with
                    {
                        Heaps = existing.Heaps,
                        HeapsLoaded = existing.HeapsLoaded,
                        HeapsLoading = existing.HeapsLoading,
                        HeapsTimedOut = existing.HeapsTimedOut,
                        HeapElapsedMs = existing.HeapElapsedMs
                    };
                }
                _latestPayload = payload;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_stateLock)
        {
            try { _activeCts?.Cancel(); } catch { }
            _activeCts = null;
        }
        _lightweightGate.Dispose();
        _heapGate.Dispose();
    }
}
