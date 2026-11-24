using System.Globalization;

namespace RAMYXEN.Core.Models;

public enum DataSourceProvenance
{
    Live,
    AdminRequired,
    EtwRequired,
    DumpRequired,
    DriverRequired,
    SymbolsRequired,
    RuntimeSpecific,
    Partial,
    Unavailable
}

public enum FeatureStatus
{
    Implemented,
    Tested,
    VerifiedOnWindows,
    LimitedBySource,
    BlockedByPlatform
}

public enum MemoryExhaustionKind
{
    None,
    PhysicalRamPressure,
    AvailableMemoryDepletion,
    SystemCommitExhaustion,
    PageFilePressure,
    ProcessCommitGrowth,
    KernelPoolExhaustion,
    DriverMemoryGrowth,
    AllocationFailureRisk,
    ExcessiveMemoryRetention
}

public enum LeakClassification
{
    NormalGrowth,
    ElevatedGrowth,
    SuspectedLeak,
    StrongLeakEvidence,
    ConfirmedLeak,
    Inconclusive
}

public enum LeakDomain
{
    ProcessNativeHeap,
    ProcessPrivateCommit,
    ManagedDotNetGc,
    SystemKernelPool,
    ResourceHandles,
    SharedOrMappedMemory
}

public sealed record PhysicalPageListTelemetry(
    ulong ZeroPageBytes,
    ulong FreePageBytes,
    ulong ModifiedPageBytes,
    ulong ModifiedNoWritePageBytes,
    ulong BadPageBytes,
    IReadOnlyList<ulong> StandbyPriorityBytes,
    ulong TotalStandbyBytes,
    bool HasExactKernelLists,
    DataSourceProvenance Provenance);

public enum AppThemeMode
{
    Dark,
    Light,
    HighContrast
}

public enum SnapshotKind
{
    TelemetrySnapshot,
    ProcessDump,
    FullPhysicalMemoryImage
}

public sealed record SystemMemoryTelemetry(
    ulong InstalledPhysicalBytes,
    ulong TotalPhysicalBytes,
    ulong AvailablePhysicalBytes,
    ulong InUsePhysicalBytes,
    ulong HardwareReservedBytes,
    ulong StandbyCacheBytes,
    ulong ModifiedBytes,
    ulong FreeOrZeroedBytes,
    ulong CommitTotalBytes,
    ulong CommitLimitBytes,
    ulong CommitPeakBytes,
    ulong SystemCacheBytes,
    ulong KernelTotalBytes,
    ulong KernelPagedBytes,
    ulong KernelNonpagedBytes,
    ulong PageSizeBytes,
    uint MemoryLoadPercent,
    uint HandleCount,
    uint ProcessCount,
    uint ThreadCount,
    double PageFaultsPerSec,
    double HardFaultsPerSec,
    DateTimeOffset TimestampUtc,
    DataSourceProvenance Provenance)
{
    public double CommitRatioPercent => CommitLimitBytes > 0
        ? Math.Round((double)CommitTotalBytes / CommitLimitBytes * 100.0, 1) : 0.0;

    public double PhysicalUsagePercent => TotalPhysicalBytes > 0
        ? Math.Round((double)InUsePhysicalBytes / TotalPhysicalBytes * 100.0, 1) : 0.0;
}

public sealed record ProcessMemoryInfo(
    uint Pid,
    uint ParentPid,
    string Name,
    string ExecutablePath,
    string UserName,
    string Category,
    string Architecture,
    ulong WorkingSetBytes,
    ulong PrivateWorkingSetBytes,
    ulong SharedWorkingSetBytes,
    ulong PrivateCommitBytes,
    ulong PeakWorkingSetBytes,
    ulong VirtualSizeBytes,
    ulong PagedPoolBytes,
    ulong NonpagedPoolBytes,
    uint PageFaultCount,
    uint HandleCount,
    uint ThreadCount,
    bool IsProtectedOrRestricted,
    bool IsManagedDotNet,
    DateTimeOffset? StartTimeUtc,
    double GrowthRateBytesPerSec,
    DataSourceProvenance Provenance);

public sealed record VirtualMemoryRegion(
    ulong BaseAddress,
    ulong AllocationBase,
    ulong SizeBytes,
    string State,
    string Protection,
    string AllocationProtection,
    string RegionType,
    string BackingModuleOrFile,
    bool IsSuspiciousRwx,
    DataSourceProvenance Provenance)
{
    public ulong EndAddress => BaseAddress + SizeBytes;
    public string BaseAddressHex => $"0x{BaseAddress:X16}";
    public string EndAddressHex => $"0x{EndAddress:X16}";
}

public sealed record ProcessHeapInfo(
    ulong HeapId,
    string HeapKind,
    uint Flags,
    uint BlockCount,
    ulong CommittedBytes,
    ulong AllocatedBytes,
    ulong FreeBytes,
    DataSourceProvenance Provenance)
{
    public string HeapIdHex => $"0x{HeapId:X16}";
}

public sealed record ProcessModuleInfo(
    string ModuleName,
    string FilePath,
    ulong BaseAddress,
    ulong SizeBytes,
    string Version,
    DataSourceProvenance Provenance)
{
    public string BaseAddressHex => $"0x{BaseAddress:X16}";
}

public sealed record ProcessThreadInfo(
    uint ThreadId,
    string State,
    string WaitReason,
    ulong StartAddress,
    TimeSpan CpuTime,
    DataSourceProvenance Provenance)
{
    public string StartAddressHex => $"0x{StartAddress:X16}";
}

public sealed record ExtractedMemoryString(
    ulong Address,
    string EncodingName,
    string Value,
    string RegionContext)
{
    public string AddressHex => $"0x{Address:X16}";
}

public sealed record LeakInvestigationCase(
    string CaseId,
    uint TargetPid,
    string TargetProcessName,
    string ExecutablePath,
    uint ParentPid,
    LeakDomain Domain,
    MemoryExhaustionKind ExhaustionKind,
    LeakClassification Classification,
    int ConfidencePercent,
    DateTimeOffset FirstObservedUtc,
    DateTimeOffset LastUpdatedUtc,
    ulong BaselineCommitBytes,
    ulong CurrentCommitBytes,
    double LinearSlopeBytesPerSec,
    double RegressionRSquared,
    uint BaselineHandles,
    uint CurrentHandles,
    string SuspectedComponent,
    IReadOnlyList<string> EvidenceItems,
    IReadOnlyList<string> RecommendedNextSteps,
    IReadOnlyList<string> KnownLimitations,
    DataSourceProvenance Provenance)
{
    public long CommitDeltaBytes => (long)CurrentCommitBytes - (long)BaselineCommitBytes;
}

public sealed record MemoryAlert(
    string AlertId,
    string Title,
    string Severity,
    string State,
    string TargetComponent,
    uint? RelatedPid,
    string MeasuredValue,
    string BaselineValue,
    LeakClassification LeakClassification,
    int ConfidencePercent,
    string EvidenceSummary,
    string SuggestedAction,
    DateTimeOffset TimestampUtc,
    DataSourceProvenance Provenance);

public sealed record MemorySnapshotRecord(
    string SnapshotId,
    string Name,
    SnapshotKind Kind,
    DateTimeOffset CreatedUtc,
    uint? TargetPid,
    string TargetDescription,
    string FilePath,
    long FileSizeBytes,
    string Sha256Hash,
    SystemMemoryTelemetry SystemState,
    IReadOnlyList<ProcessMemoryInfo> TopProcesses,
    IReadOnlyList<string> Tags,
    string Notes);

public sealed record SnapshotComparisonResult(
    MemorySnapshotRecord Baseline,
    MemorySnapshotRecord Target,
    long PhysicalInUseDeltaBytes,
    long CommitTotalDeltaBytes,
    long AvailableDeltaBytes,
    long NonpagedPoolDeltaBytes,
    IReadOnlyList<ProcessSnapshotDiff> ProcessDiffs);

public sealed record ProcessSnapshotDiff(
    uint Pid,
    string ProcessName,
    ulong BaselineWorkingSetBytes,
    ulong TargetWorkingSetBytes,
    long WorkingSetDeltaBytes,
    ulong BaselineCommitBytes,
    ulong TargetCommitBytes,
    long CommitDeltaBytes,
    int HandleDelta,
    string Status);

public sealed record ForensicFinding(
    string FindingId,
    string FindingType,
    string Category, // Observed Data, Heuristic Finding, Potential Suspicion, Confirmed Evidence
    string Severity,
    int ConfidencePercent,
    string TargetProcessOrModule,
    ulong Address,
    ulong SizeBytes,
    string EvidenceDetail,
    string DetectionMethod,
    string LimitationNote,
    DataSourceProvenance Provenance)
{
    public string AddressHex => $"0x{Address:X16}";
}

public static class UnitFormatter
{
    public static string FormatBytes(ulong bytes, bool useBinaryIec = false, CultureInfo? culture = null)
    {
        var ci = culture ?? CultureInfo.InvariantCulture;
        double step = useBinaryIec ? 1024.0 : 1000.0;
        string[] units = useBinaryIec
            ? ["B", "KiB", "MiB", "GiB", "TiB"]
            : ["B", "KB", "MB", "GB", "TB"];

        if (bytes < (ulong)step)
            return $"{bytes.ToString("N0", ci)} B";

        double val = bytes;
        int idx = 0;
        while (val >= step && idx < units.Length - 1)
        {
            val /= step;
            idx++;
        }
        return $"{val.ToString(idx >= 3 ? "F2" : "F1", ci)} {units[idx]}";
    }

    public static string FormatSignedDeltaBytes(long deltaBytes, CultureInfo? culture = null)
    {
        string sign = deltaBytes > 0 ? "+" : deltaBytes < 0 ? "-" : "";
        ulong abs = deltaBytes == long.MinValue
            ? (ulong)long.MaxValue + 1UL
            : (ulong)Math.Abs(deltaBytes);
        return $"{sign}{FormatBytes(abs, useBinaryIec: true, culture)}";
    }
}
