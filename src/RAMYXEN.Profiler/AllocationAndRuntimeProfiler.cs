using RAMYXEN.Core.Models;
using RAMYXEN.Infrastructure.Windows;

namespace RAMYXEN.Profiler;

public sealed record ManagedGcTelemetry(
    uint TargetPid,
    string ProcessName,
    ulong Gen0SizeBytes,
    ulong Gen1SizeBytes,
    ulong Gen2SizeBytes,
    ulong LohSizeBytes,
    ulong PohSizeBytes,
    ulong TotalCommittedGcBytes,
    ulong TotalAllocatedBytesCumulative,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    double FragmentationPercent,
    double PauseTimePercentage,
    DataSourceProvenance Provenance,
    string RuntimeStatusNote = "");

public sealed record AllocationHotspotItem(
    string AllocationSiteOrCategory,
    string SourceProvider,
    ulong TotalCommittedBytes,
    ulong SurvivingBytes,
    double SharePercent,
    string SymbolStatus,
    DataSourceProvenance Provenance);

public sealed class AllocationAndRuntimeProfiler
{
    private readonly WindowsProcessCollector _procCollector = new();

    /// <summary>
    /// Collects exact in-process .NET 10 CLR GC heap metrics via GC.GetGCMemoryInfo() when inspecting
    /// the current host process (RAMYXEN.exe), and honestly reports runtime presence &amp; ETW requirements
    /// for external processes without EVER misattributing RAMYXEN.exe's own GC counters to an external PID.
    /// </summary>
    public ManagedGcTelemetry CollectManagedGcTelemetry(uint targetPid, string processName)
    {
        bool isSelf = targetPid == (uint)Environment.ProcessId;
        if (isSelf)
        {
            var gcInfo = GC.GetGCMemoryInfo();
            ulong g0 = gcInfo.GenerationInfo.Length > 0 ? (ulong)Math.Max(0, gcInfo.GenerationInfo[0].SizeAfterBytes) : 0;
            ulong g1 = gcInfo.GenerationInfo.Length > 1 ? (ulong)Math.Max(0, gcInfo.GenerationInfo[1].SizeAfterBytes) : 0;
            ulong g2 = gcInfo.GenerationInfo.Length > 2 ? (ulong)Math.Max(0, gcInfo.GenerationInfo[2].SizeAfterBytes) : 0;
            ulong loh = gcInfo.GenerationInfo.Length > 3 ? (ulong)Math.Max(0, gcInfo.GenerationInfo[3].SizeAfterBytes) : 0;
            ulong poh = gcInfo.GenerationInfo.Length > 4 ? (ulong)Math.Max(0, gcInfo.GenerationInfo[4].SizeAfterBytes) : 0;

            ulong totalHeap = (ulong)Math.Max(0, gcInfo.HeapSizeBytes);
            ulong liveMemory = (ulong)Math.Max(0, GC.GetTotalMemory(false));
            ulong totalCommitted = gcInfo.TotalCommittedBytes > 0
                ? (ulong)gcInfo.TotalCommittedBytes
                : (totalHeap > 0 ? totalHeap : liveMemory);
            ulong fragBytes = (ulong)Math.Max(0, gcInfo.FragmentedBytes);
            double fragPct = totalHeap > 0 ? Math.Round((double)fragBytes / totalHeap * 100.0, 1) : 0.0;

            return new ManagedGcTelemetry(
                TargetPid: targetPid,
                ProcessName: "RAMYXEN.exe (.NET 10 Self-Host)",
                Gen0SizeBytes: g0,
                Gen1SizeBytes: g1,
                Gen2SizeBytes: g2 > 0 ? g2 : totalHeap,
                LohSizeBytes: loh,
                PohSizeBytes: poh,
                TotalCommittedGcBytes: totalCommitted,
                TotalAllocatedBytesCumulative: (ulong)Math.Max(0, GC.GetTotalAllocatedBytes(false)),
                Gen0Collections: GC.CollectionCount(0),
                Gen1Collections: GC.CollectionCount(1),
                Gen2Collections: GC.CollectionCount(2),
                FragmentationPercent: fragPct,
                PauseTimePercentage: Math.Round(gcInfo.PauseTimePercentage, 2),
                Provenance: DataSourceProvenance.RuntimeSpecific,
                RuntimeStatusNote: "Exact in-process .NET 10 CLR GC introspection via GC.GetGCMemoryInfo().");
        }

        // For external processes: check loaded modules to determine if a .NET CLR is actually loaded
        var modules = _procCollector.GetProcessModules(targetPid);
        bool hasClrModule = modules.Any(m =>
            m.ModuleName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase) ||
            m.ModuleName.Equals("clr.dll", StringComparison.OrdinalIgnoreCase) ||
            m.ModuleName.Equals("mscoree.dll", StringComparison.OrdinalIgnoreCase) ||
            m.ModuleName.Equals("hostfxr.dll", StringComparison.OrdinalIgnoreCase)) ||
            processName.Contains("dotnet", StringComparison.OrdinalIgnoreCase);

        if (!hasClrModule)
        {
            return new ManagedGcTelemetry(
                TargetPid: targetPid,
                ProcessName: processName,
                Gen0SizeBytes: 0,
                Gen1SizeBytes: 0,
                Gen2SizeBytes: 0,
                LohSizeBytes: 0,
                PohSizeBytes: 0,
                TotalCommittedGcBytes: 0,
                TotalAllocatedBytesCumulative: 0,
                Gen0Collections: 0,
                Gen1Collections: 0,
                Gen2Collections: 0,
                FragmentationPercent: 0.0,
                PauseTimePercentage: 0.0,
                Provenance: DataSourceProvenance.Unavailable,
                RuntimeStatusNote: "Native Process — No .NET CLR runtime module (coreclr.dll / clr.dll) loaded in target address space.");
        }

        return new ManagedGcTelemetry(
            TargetPid: targetPid,
            ProcessName: processName,
            Gen0SizeBytes: 0,
            Gen1SizeBytes: 0,
            Gen2SizeBytes: 0,
            LohSizeBytes: 0,
            PohSizeBytes: 0,
            TotalCommittedGcBytes: 0,
            TotalAllocatedBytesCumulative: 0,
            Gen0Collections: 0,
            Gen1Collections: 0,
            Gen2Collections: 0,
            FragmentationPercent: 0.0,
            PauseTimePercentage: 0.0,
            Provenance: DataSourceProvenance.EtwRequired,
            RuntimeStatusNote: "External .NET CLR Detected — Cross-process GC generation sizes require an elevated ETW Microsoft-Windows-DotNETRuntime GCHeapStats trace.");
    }

    public IReadOnlyList<AllocationHotspotItem> AnalyzeProcessAllocationHotspots(uint pid)
    {
        var regions = _procCollector.GetProcessVirtualRegions(pid, 2048);
        var modules = _procCollector.GetProcessModules(pid);
        // Never walk external process heaps synchronously here; only walk self-process heap when not pre-supplied (F-002)
        IReadOnlyList<ProcessHeapInfo>? heaps = pid == (uint)Environment.ProcessId
            ? _procCollector.GetProcessHeaps(pid)
            : null;
        return AnalyzeProcessAllocationHotspots(pid, regions, modules, heaps);
    }

    public IReadOnlyList<AllocationHotspotItem> AnalyzeProcessAllocationHotspots(
        uint pid,
        IReadOnlyList<VirtualMemoryRegion> regions,
        IReadOnlyList<ProcessModuleInfo> modules,
        IReadOnlyList<ProcessHeapInfo>? heaps)
    {
        var moduleByBase = modules
            .GroupBy(m => m.BaseAddress)
            .ToDictionary(g => g.Key, g => g.First().ModuleName);

        var committed = regions.Where(r => r.State == "MEM_COMMIT").ToList();
        ulong totalCommit = (ulong)Math.Max(1, committed.Sum(r => (long)r.SizeBytes));

        var items = new List<AllocationHotspotItem>();

        // 1. Include Native Heaps if already loaded on demand (honestly marking bounded samples - F-034)
        if (heaps is not null)
        {
            foreach (var h in heaps.Where(h => h.CommittedBytes > 0).Take(4))
            {
                double pct = Math.Round((double)h.CommittedBytes / totalCommit * 100.0, 1);
                bool isPartial = h.Provenance == DataSourceProvenance.Partial;
                items.Add(new AllocationHotspotItem(
                    AllocationSiteOrCategory: $"{h.HeapKind} ({h.HeapIdHex}) — {h.BlockCount:N0} blocks",
                    SourceProvider: "Win32 HeapWalk / Toolhelp32",
                    TotalCommittedBytes: h.CommittedBytes,
                    SurvivingBytes: h.AllocatedBytes > 0 ? h.AllocatedBytes : h.CommittedBytes,
                    SharePercent: pct,
                    SymbolStatus: isPartial ? "Partial Heap Sample (Bounded)" : "Heap Segment Walk Verified",
                    Provenance: h.Provenance));
            }
        }

        // 2. Group committed Virtual Address Space regions and map Image regions to loaded DLL/EXE names
        var grouped = committed
            .GroupBy(r =>
            {
                if (r.RegionType == "Image" && moduleByBase.TryGetValue(r.AllocationBase, out var modName))
                    return $"Image [{r.Protection}] — {modName}";
                return $"{r.RegionType} [{r.Protection}] — {r.BackingModuleOrFile}";
            })
            .Select(g =>
            {
                ulong bytes = (ulong)g.Sum(x => (long)x.SizeBytes);
                double pct = Math.Round((double)bytes / totalCommit * 100.0, 1);
                bool hasSym = g.Key.StartsWith("Image", StringComparison.OrdinalIgnoreCase) && modules.Count > 0;
                return new AllocationHotspotItem(
                    AllocationSiteOrCategory: $"{g.Key} ({g.Count()} regions)",
                    SourceProvider: "VirtualQueryEx",
                    TotalCommittedBytes: bytes,
                    SurvivingBytes: bytes,
                    SharePercent: pct,
                    SymbolStatus: hasSym ? "PE Module Mapped" : "Symbols Required for Call Stack",
                    Provenance: hasSym ? DataSourceProvenance.Live : DataSourceProvenance.SymbolsRequired);
            });

        items.AddRange(grouped);

        return items
            .OrderByDescending(x => x.SurvivingBytes)
            .Take(12)
            .ToList();
    }
}
