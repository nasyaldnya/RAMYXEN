using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using RAMYXEN.Core.Models;
using RAMYXEN.Diagnostics;

namespace RAMYXEN.Infrastructure.Windows;

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct NativeSystemMemorySnapshot
{
    public ulong InstalledPhysicalBytes;
    public ulong TotalPhysicalBytes;
    public ulong AvailablePhysicalBytes;
    public ulong InUsePhysicalBytes;
    public ulong HardwareReservedBytes;
    public ulong CommitTotalBytes;
    public ulong CommitLimitBytes;
    public ulong CommitPeakBytes;
    public ulong SystemCacheBytes;
    public ulong KernelTotalBytes;
    public ulong KernelPagedBytes;
    public ulong KernelNonpagedBytes;
    public ulong PageSizeBytes;
    public uint MemoryLoadPercent;
    public uint HandleCount;
    public uint ProcessCount;
    public uint ThreadCount;
    public ulong TimestampUtcMs;
    public int StatusCode;
    public uint ReservedPadding;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct NativeKernelPageListMetrics
{
    public ulong ZeroPageBytes;
    public ulong FreePageBytes;
    public ulong ModifiedPageBytes;
    public ulong ModifiedNoWritePageBytes;
    public ulong BadPageBytes;
    public fixed ulong StandbyPriorityBytes[8];
    public ulong TotalStandbyBytes;
    public uint PageFaultCountCumulative;
    public uint PageReadHardFaultCountCumulative;
    public uint HasExactMemoryLists;
    public uint HasExactFaultCounters;
}

[StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Unicode)]
public unsafe struct NativeProcessMemoryMetrics
{
    public uint Pid;
    public uint ParentPid;
    public ulong WorkingSetBytes;
    public ulong PrivateWorkingSetBytes;
    public ulong SharedWorkingSetBytes;
    public ulong PrivateCommitBytes;
    public ulong PeakWorkingSetBytes;
    public ulong PagefileUsageBytes;
    public ulong VirtualSizeBytes;
    public ulong PagedPoolBytes;
    public ulong NonpagedPoolBytes;
    public uint PageFaultCount;
    public uint HandleCount;
    public uint ThreadCount;
    public uint IsProtectedOrRestricted;
    public ulong StartTimeFileTimeUtc;
    public int StatusCode;
    public uint ArchitectureCode;
    public fixed char ExecutablePath[260];
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct NativeVirtualRegionInfo
{
    public ulong BaseAddress;
    public ulong AllocationBase;
    public ulong RegionSizeBytes;
    public uint State;
    public uint Protect;
    public uint AllocationProtect;
    public uint Type;
    public uint IsSuspiciousRwx;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct NativeHeapSummary
{
    public ulong HeapId;
    public uint Flags;
    public uint BlockCount;
    public ulong CommittedBytes;
    public ulong AllocatedBytes;
    public ulong FreeBytes;
}

[StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Unicode)]
public unsafe struct NativeModuleInfo
{
    public ulong BaseAddress;
    public ulong SizeBytes;
    public fixed char ModuleName[128];
    public fixed char FilePath[260];
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct NativeErrorInfo
{
    public int StatusCode;
    public uint Win32ErrorCode;
    public int HResult;
    public uint TargetPid;
    public ulong TimestampUtcMs;
    public fixed byte FunctionName[64];
}

public static class NativeMethods
{
    private const string DllName = "RAMYXEN.Native.dll";
    public const int ExpectedAbiVersion = 102;

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int Ramyxen_GetAbiVersion();

    public static int EnsureNativeAbiCompatibility()
    {
        int abi = Ramyxen_GetAbiVersion();
        if (abi != ExpectedAbiVersion)
        {
            throw new InvalidOperationException(
                $"RAMYXEN.Native.dll ABI mismatch: expected v{ExpectedAbiVersion}, found v{abi}.");
        }

        static void AssertSize<T>(int expected) where T : struct
        {
            int actual = Marshal.SizeOf<T>();
            if (actual != expected)
            {
                throw new InvalidOperationException(
                    $"Managed/Native struct layout mismatch for {typeof(T).Name}: expected {expected} bytes, got {actual} bytes.");
            }
        }

        AssertSize<NativeSystemMemorySnapshot>(136);
        AssertSize<NativeKernelPageListMetrics>(128);
        AssertSize<NativeProcessMemoryMetrics>(632);
        AssertSize<NativeVirtualRegionInfo>(48);
        AssertSize<NativeHeapSummary>(40);
        AssertSize<NativeModuleInfo>(792);
        AssertSize<NativeErrorInfo>(88);
        return abi;
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int Ramyxen_GetLastNativeError(out NativeErrorInfo outError);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int Ramyxen_GetSystemMemorySnapshot(out NativeSystemMemorySnapshot snapshot);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int Ramyxen_GetKernelPageListMetrics(out NativeKernelPageListMetrics metrics);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int Ramyxen_GetProcessMemoryMetrics(uint pid, out NativeProcessMemoryMetrics metrics);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int Ramyxen_EnumerateVirtualRegions(
        uint pid,
        [Out] NativeVirtualRegionInfo[]? outRegions,
        int maxRegions,
        out int outTotalRegions);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int Ramyxen_EnumerateProcessHeaps(
        uint pid,
        [Out] NativeHeapSummary[]? outHeaps,
        int maxHeaps,
        out int outTotalHeaps);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int Ramyxen_EnumerateProcessModulesFast(
        uint pid,
        [Out] NativeModuleInfo[]? outModules,
        int maxModules,
        out int outTotalModules);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int Ramyxen_ReadProcessMemorySafe(
        uint pid,
        ulong baseAddress,
        [Out] byte[] outBuffer,
        int bufferSize,
        out int outBytesRead);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    public static extern int Ramyxen_ExtractExecutableIconBgra(
        [MarshalAs(UnmanagedType.LPWStr)] string executablePath,
        int desiredSize,
        [Out] byte[] outBgraPixels,
        int bufferBytes,
        out int outWidth,
        out int outHeight,
        out int outProvenance);

    public static unsafe string FormatLastNativeError()
    {
        try
        {
            if (Ramyxen_GetLastNativeError(out var err) != 0)
                return "NativeError(unavailable)";
            byte* p = err.FunctionName;
            int len = 0;
            while (len < 64 && p[len] != 0) len++;
            string fn = Encoding.ASCII.GetString(p, len);
            return $"fn={fn}, status={err.StatusCode}, win32={err.Win32ErrorCode}, pid={err.TargetPid}";
        }
        catch
        {
            return "NativeError(unavailable)";
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool Process32FirstW(nint hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool Process32NextW(nint hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(nint hObject);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("dbghelp.dll", SetLastError = true)]
    public static extern bool MiniDumpWriteDump(
        nint hProcess,
        uint processId,
        SafeHandle hFile,
        uint dumpType,
        nint exceptionParam,
        nint userStreamParam,
        nint callbackParam);
}

public sealed class WindowsSystemMemoryCollector
{
    private readonly object _sync = new();
    private uint _prevPageFaultCumulative;
    private uint _prevHardFaultCumulative;
    private DateTimeOffset _prevSampleTime = DateTimeOffset.MinValue;

    public StructuredDiagnosticLogger? DiagnosticsLogger { get; set; }
    public CrashAndDiagnosticReportService? CrashReportService { get; set; }

    public PhysicalPageListTelemetry LatestPageLists { get; private set; } = new(
        0, 0, 0, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0], 0, false, DataSourceProvenance.Partial);

    public unsafe SystemMemoryTelemetry Collect()
    {
        int rc = NativeMethods.Ramyxen_GetSystemMemorySnapshot(out var snap);
        if (rc != 0)
        {
            string nativeErr = NativeMethods.FormatLastNativeError();
            if (NativeMethods.Ramyxen_GetLastNativeError(out var errInfo) == 0)
                CrashReportService?.CaptureNativeBoundaryError("Ramyxen_GetSystemMemorySnapshot", errInfo.StatusCode, errInfo.Win32ErrorCode, nativeErr);
            else
                DiagnosticsLogger?.Warning("NativeError", $"Ramyxen_GetSystemMemorySnapshot failed: {nativeErr}");
            throw new InvalidOperationException($"Ramyxen_GetSystemMemorySnapshot failed with code {rc} ({nativeErr})");
        }

        int krc = NativeMethods.Ramyxen_GetKernelPageListMetrics(out var kList);
        if (krc != 0)
        {
            DiagnosticsLogger?.Warning("NativeError", $"Ramyxen_GetKernelPageListMetrics failed: {NativeMethods.FormatLastNativeError()}");
        }
        var now = DateTimeOffset.UtcNow;

        double faultsPerSec = 0;
        double hardFaultsPerSec = 0;
        lock (_sync)
        {
            if (kList.HasExactFaultCounters != 0)
            {
                if (_prevSampleTime != DateTimeOffset.MinValue)
                {
                    double dt = Math.Max(0.2, (now - _prevSampleTime).TotalSeconds);
                    uint faultDelta = unchecked(kList.PageFaultCountCumulative - _prevPageFaultCumulative);
                    uint hardDelta = unchecked(kList.PageReadHardFaultCountCumulative - _prevHardFaultCumulative);
                    if (faultDelta < 50_000_000)
                        faultsPerSec = Math.Round(faultDelta / dt, 1);
                    if (hardDelta < 10_000_000)
                        hardFaultsPerSec = Math.Round(hardDelta / dt, 1);
                }
                _prevPageFaultCumulative = kList.PageFaultCountCumulative;
                _prevHardFaultCumulative = kList.PageReadHardFaultCountCumulative;
            }
            _prevSampleTime = now;
        }

        ulong standby;
        ulong freeOrZeroed;
        ulong modified;
        var prios = new ulong[8];

        if (kList.HasExactMemoryLists != 0)
        {
            standby = kList.TotalStandbyBytes;
            freeOrZeroed = kList.ZeroPageBytes + kList.FreePageBytes;
            modified = kList.ModifiedPageBytes + kList.ModifiedNoWritePageBytes;
            for (int i = 0; i < 8; i++)
                prios[i] = kList.StandbyPriorityBytes[i];

            LatestPageLists = new PhysicalPageListTelemetry(
                ZeroPageBytes: kList.ZeroPageBytes,
                FreePageBytes: kList.FreePageBytes,
                ModifiedPageBytes: kList.ModifiedPageBytes,
                ModifiedNoWritePageBytes: kList.ModifiedNoWritePageBytes,
                BadPageBytes: kList.BadPageBytes,
                StandbyPriorityBytes: prios,
                TotalStandbyBytes: standby,
                HasExactKernelLists: true,
                Provenance: DataSourceProvenance.Live);
        }
        else
        {
            // Unprivileged fallback when SeProfileSingleProcessPrivilege is not held:
            // Standby Cache is bounded by AvailablePhysicalBytes and SystemCacheBytes; marked as Partial in LatestPageLists.
            standby = Math.Min(snap.AvailablePhysicalBytes, snap.SystemCacheBytes);
            freeOrZeroed = snap.AvailablePhysicalBytes > standby ? (snap.AvailablePhysicalBytes - standby) : 0UL;
            modified = snap.SystemCacheBytes > standby ? (snap.SystemCacheBytes - standby) : 0UL;
            for (int i = 0; i < 8; i++)
                prios[i] = standby / 8;

            LatestPageLists = new PhysicalPageListTelemetry(
                ZeroPageBytes: freeOrZeroed / 2,
                FreePageBytes: freeOrZeroed - (freeOrZeroed / 2),
                ModifiedPageBytes: modified,
                ModifiedNoWritePageBytes: 0,
                BadPageBytes: 0,
                StandbyPriorityBytes: prios,
                TotalStandbyBytes: standby,
                HasExactKernelLists: false,
                Provenance: DataSourceProvenance.Partial);
        }

        return new SystemMemoryTelemetry(
            InstalledPhysicalBytes: snap.InstalledPhysicalBytes,
            TotalPhysicalBytes: snap.TotalPhysicalBytes,
            AvailablePhysicalBytes: snap.AvailablePhysicalBytes,
            InUsePhysicalBytes: snap.InUsePhysicalBytes,
            HardwareReservedBytes: snap.HardwareReservedBytes,
            StandbyCacheBytes: standby,
            ModifiedBytes: modified,
            FreeOrZeroedBytes: freeOrZeroed,
            CommitTotalBytes: snap.CommitTotalBytes,
            CommitLimitBytes: snap.CommitLimitBytes,
            CommitPeakBytes: snap.CommitPeakBytes,
            SystemCacheBytes: snap.SystemCacheBytes,
            KernelTotalBytes: snap.KernelTotalBytes,
            KernelPagedBytes: snap.KernelPagedBytes,
            KernelNonpagedBytes: snap.KernelNonpagedBytes,
            PageSizeBytes: snap.PageSizeBytes,
            MemoryLoadPercent: snap.MemoryLoadPercent,
            HandleCount: snap.HandleCount,
            ProcessCount: snap.ProcessCount,
            ThreadCount: snap.ThreadCount,
            PageFaultsPerSec: faultsPerSec,
            HardFaultsPerSec: hardFaultsPerSec,
            TimestampUtc: now,
            Provenance: DataSourceProvenance.Live);
    }
}

public sealed class WindowsProcessCollector
{
    private readonly object _sync = new();
    private readonly Dictionary<uint, (ulong CommitBytes, DateTimeOffset Time, ulong StartFileTime)> _prevCommit = new();
    private readonly Dictionary<uint, (bool IsDotNet, ulong StartFileTime)> _dotNetCache = new();
    private readonly ConcurrentDictionary<string, string> _moduleVersionCache = new(StringComparer.OrdinalIgnoreCase);

    public StructuredDiagnosticLogger? DiagnosticsLogger { get; set; }

    public IReadOnlyList<ProcessMemoryInfo> CollectProcesses()
    {
        var toolhelpEntries = GetToolhelpProcessEntries();
        var results = new List<ProcessMemoryInfo>(toolhelpEntries.Count);
        var activePids = new HashSet<uint>(toolhelpEntries.Count);
        var now = DateTimeOffset.UtcNow;

        foreach (var entry in toolhelpEntries)
        {
            uint pid = entry.Pid;
            if (pid == 0) continue;
            activePids.Add(pid);

            int rc = NativeMethods.Ramyxen_GetProcessMemoryMetrics(pid, out var m);

            uint ppid = entry.ParentPid;
            uint threads = entry.Threads;
            string name = !string.IsNullOrWhiteSpace(entry.ExeFile) ? entry.ExeFile : $"PID-{pid}";
            string exePath = ExtractFixedPath(ref m);
            if (string.IsNullOrWhiteSpace(exePath)) exePath = name;

            ulong ws = m.WorkingSetBytes;
            ulong pws = m.PrivateWorkingSetBytes > 0 ? m.PrivateWorkingSetBytes : ws;
            ulong sws = ws > pws ? (ws - pws) : 0UL;
            ulong commit = m.PrivateCommitBytes;
            ulong virt = m.VirtualSizeBytes > 0 ? m.VirtualSizeBytes : (ws + commit);

            double growthRate = 0;
            lock (_sync)
            {
                if (_prevCommit.TryGetValue(pid, out var prev) &&
                    (prev.StartFileTime == m.StartTimeFileTimeUtc || m.StartTimeFileTimeUtc == 0))
                {
                    double dt = Math.Max(0.25, (now - prev.Time).TotalSeconds);
                    growthRate = Math.Round(((long)commit - (long)prev.CommitBytes) / dt, 1);
                }
                if (commit > 0)
                {
                    _prevCommit[pid] = (commit, now, m.StartTimeFileTimeUtc);
                }
            }

            DateTimeOffset? startUtc = null;
            if (m.StartTimeFileTimeUtc > 0)
            {
                try { startUtc = DateTimeOffset.FromFileTime((long)m.StartTimeFileTimeUtc); } catch { }
            }

            string arch = m.ArchitectureCode switch { 1 => "x64", 2 => "x86 (WOW64)", 3 => "ARM64", _ => "x64" };
            string cat = ClassifyProcessCategory(name, exePath);
            bool isDotNet = DetectManagedDotNetProcess(pid, name, exePath, m.StartTimeFileTimeUtc);

            var prov = (m.IsProtectedOrRestricted != 0 || rc != 0)
                ? DataSourceProvenance.AdminRequired
                : DataSourceProvenance.Live;

            results.Add(new ProcessMemoryInfo(
                Pid: pid,
                ParentPid: ppid,
                Name: name,
                ExecutablePath: exePath,
                UserName: (m.IsProtectedOrRestricted != 0 || rc != 0) ? "SYSTEM / Protected" : Environment.UserName,
                Category: cat,
                Architecture: arch,
                WorkingSetBytes: ws,
                PrivateWorkingSetBytes: pws,
                SharedWorkingSetBytes: sws,
                PrivateCommitBytes: commit,
                PeakWorkingSetBytes: m.PeakWorkingSetBytes,
                VirtualSizeBytes: virt,
                PagedPoolBytes: m.PagedPoolBytes,
                NonpagedPoolBytes: m.NonpagedPoolBytes,
                PageFaultCount: m.PageFaultCount,
                HandleCount: m.HandleCount,
                ThreadCount: threads,
                IsProtectedOrRestricted: m.IsProtectedOrRestricted != 0 || rc != 0,
                IsManagedDotNet: isDotNet,
                StartTimeUtc: startUtc,
                GrowthRateBytesPerSec: growthRate,
                Provenance: prov));
        }

        // Prune exited PIDs so _prevCommit and _dotNetCache never grow unboundedly over 24-hour runs
        lock (_sync)
        {
            foreach (var stalePid in _prevCommit.Keys.Where(k => !activePids.Contains(k)).ToList())
                _prevCommit.Remove(stalePid);
            foreach (var stalePid in _dotNetCache.Keys.Where(k => !activePids.Contains(k)).ToList())
                _dotNetCache.Remove(stalePid);
        }

        return results.OrderByDescending(p => p.WorkingSetBytes).ToList();
    }

    private bool DetectManagedDotNetProcess(uint pid, string name, string exePath, ulong startFileTime)
    {
        if (pid == (uint)Environment.ProcessId) return true;
        if (name.Contains("RAMYXEN", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("dotnet", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("devenv", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("pwsh", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("msbuild", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("VBCSCompiler", StringComparison.OrdinalIgnoreCase) ||
            exePath.Contains("\\dotnet\\", StringComparison.OrdinalIgnoreCase) ||
            exePath.Contains("Microsoft.NET", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        lock (_sync)
        {
            if (_dotNetCache.TryGetValue(pid, out var cached) && cached.StartFileTime == startFileTime)
                return cached.IsDotNet;
        }

        // Check if companion .runtimeconfig.json or .deps.json exists adjacent to executable path
        bool isManaged = false;
        try
        {
            if (exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && Path.IsPathRooted(exePath))
            {
                string baseNoExt = exePath[..^4];
                if (File.Exists(baseNoExt + ".runtimeconfig.json") || File.Exists(baseNoExt + ".deps.json") || File.Exists(exePath + ".config"))
                {
                    isManaged = true;
                }
            }
        }
        catch { }

        lock (_sync)
        {
            _dotNetCache[pid] = (isManaged, startFileTime);
        }
        return isManaged;
    }

    public (IReadOnlyList<VirtualMemoryRegion> Regions, int TotalCount, bool IsTruncated, bool IsAccessDenied) GetProcessVirtualRegionsWithTotal(uint pid, int maxRegions = 512)
    {
        int cap = Math.Clamp(maxRegions, 1, 8192);
        var buf = new NativeVirtualRegionInfo[cap];
        int rc = NativeMethods.Ramyxen_EnumerateVirtualRegions(pid, buf, cap, out int total);
        if (rc == -2)
        {
            DiagnosticsLogger?.Warning("NativeError", $"Ramyxen_EnumerateVirtualRegions access denied for PID {pid}: {NativeMethods.FormatLastNativeError()}");
            return ([], 0, false, true);
        }
        if (rc != 0 || total <= 0)
        {
            if (rc != 0 && rc != -3)
                DiagnosticsLogger?.Warning("NativeError", $"Ramyxen_EnumerateVirtualRegions rc={rc} for PID {pid}: {NativeMethods.FormatLastNativeError()}");
            return ([], 0, false, false);
        }

        int count = Math.Min(total, cap);
        bool truncated = total > cap;
        var prov = truncated ? DataSourceProvenance.Partial : DataSourceProvenance.Live;
        var list = new List<VirtualMemoryRegion>(count);
        for (int i = 0; i < count; i++)
        {
            var r = buf[i];
            if (r.BaseAddress == 0 && r.RegionSizeBytes == 0) continue;
            string state = r.State switch { 0x1000 => "MEM_COMMIT", 0x2000 => "MEM_RESERVE", _ => "MEM_FREE" };
            string type = r.Type switch { 0x1000000 => "Image", 0x40000 => "Mapped File", 0x20000 => "Private", _ => "Private" };
            string prot = FormatProtection(r.Protect);
            string allocProt = FormatProtection(r.AllocationProtect);
            list.Add(new VirtualMemoryRegion(
                r.BaseAddress, r.AllocationBase, r.RegionSizeBytes, state, prot, allocProt, type,
                type == "Image" ? "Executable / DLL Image" : type == "Mapped File" ? "Memory-Mapped Section" : "Private Heap / Stack / Arena",
                r.IsSuspiciousRwx != 0, prov));
        }
        return (list, total, truncated, false);
    }

    public IReadOnlyList<VirtualMemoryRegion> GetProcessVirtualRegions(uint pid, int maxRegions = 512)
        => GetProcessVirtualRegionsWithTotal(pid, maxRegions).Regions;

    public IReadOnlyList<ProcessHeapInfo> GetProcessHeaps(uint pid)
    {
        var buf = new NativeHeapSummary[64];
        int rc = NativeMethods.Ramyxen_EnumerateProcessHeaps(pid, buf, buf.Length, out int total);
        if (rc != 0 || total <= 0)
        {
            if (rc != 0 && rc != -2 && rc != -3)
                DiagnosticsLogger?.Warning("NativeError", $"Ramyxen_EnumerateProcessHeaps rc={rc} for PID {pid}: {NativeMethods.FormatLastNativeError()}");
            return [];
        }

        int take = Math.Min(total, buf.Length);
        var list = new List<ProcessHeapInfo>(take);
        for (int i = 0; i < take; i++)
        {
            var h = buf[i];
            // Strictly check explicit native truncation flag bit (0x80000000) rather than block count heuristic (F-034)
            bool bounded = (h.Flags & 0x80000000U) != 0;
            var prov = bounded ? DataSourceProvenance.Partial : DataSourceProvenance.Live;
            string name = (h.Flags & 1U) != 0 || i == 0
                ? (bounded ? "Default Process Heap (Bounded Sample)" : "Default Process Heap (NT Segment/LFH)")
                : (bounded ? $"Private Heap #{i + 1} (Bounded Sample)" : $"Private Heap #{i + 1}");
            list.Add(new ProcessHeapInfo(
                h.HeapId,
                name,
                h.Flags & ~0x80000000U,
                h.BlockCount,
                h.CommittedBytes,
                h.AllocatedBytes,
                h.FreeBytes,
                prov));
        }
        return list;
    }

    public unsafe (IReadOnlyList<ProcessModuleInfo> Modules, int TotalModules, bool IsTruncated) GetProcessModulesWithTotal(uint pid)
    {
        var buf = new NativeModuleInfo[512];
        int rc = NativeMethods.Ramyxen_EnumerateProcessModulesFast(pid, buf, buf.Length, out int total);
        if (rc == 0 && total > 0)
        {
            int count = Math.Min(total, buf.Length);
            bool truncated = total > buf.Length;
            var prov = truncated ? DataSourceProvenance.Partial : DataSourceProvenance.Live;
            var fastList = new List<ProcessModuleInfo>(count);
            for (int i = 0; i < count; i++)
            {
                var m = buf[i];
                string modName = ExtractFixedWideString(m.ModuleName, 128);
                string modPath = ExtractFixedWideString(m.FilePath, 260);
                string version = ResolveCachedFileVersion(modPath);
                fastList.Add(new ProcessModuleInfo(
                    string.IsNullOrWhiteSpace(modName) ? "Module" : modName,
                    modPath,
                    m.BaseAddress,
                    m.SizeBytes,
                    version,
                    prov));
            }
            return (fastList, total, truncated);
        }

        if (rc != 0 && rc != -2 && rc != -3)
        {
            DiagnosticsLogger?.Warning("NativeError", $"Ramyxen_EnumerateProcessModulesFast rc={rc} for PID {pid}: {NativeMethods.FormatLastNativeError()}");
        }

        // Do NOT fall through to Process.Modules when native returned ACCESS_DENIED (-2) or PROTECTED (-3) (R2-NEW-018)
        if (rc == -2 || rc == -3)
        {
            return (Array.Empty<ProcessModuleInfo>(), 0, false);
        }

        // Fallback if Toolhelp32 returned empty/transient error
        var list = new List<ProcessModuleInfo>();
        try
        {
            using var p = Process.GetProcessById((int)pid);
            foreach (ProcessModule m in p.Modules)
            {
                using (m)
                {
                    string modPath = m.FileName ?? "";
                    list.Add(new ProcessModuleInfo(
                        m.ModuleName ?? "Module",
                        modPath,
                        (ulong)m.BaseAddress.ToInt64(),
                        (ulong)m.ModuleMemorySize,
                        ResolveCachedFileVersion(modPath),
                        DataSourceProvenance.Live));
                }
            }
        }
        catch { }
        return (list, list.Count, false);
    }

    public IReadOnlyList<ProcessModuleInfo> GetProcessModules(uint pid)
        => GetProcessModulesWithTotal(pid).Modules;

    private string ResolveCachedFileVersion(string modulePath)
    {
        if (string.IsNullOrWhiteSpace(modulePath) || !Path.IsPathRooted(modulePath))
            return "—";

        long lastWriteTicks = 0;
        try { lastWriteTicks = File.GetLastWriteTimeUtc(modulePath).Ticks; } catch { }
        string cacheKey = $"{modulePath}|{lastWriteTicks}";

        if (_moduleVersionCache.TryGetValue(cacheKey, out var cached))
            return cached;

        // Prevent unbounded cache growth across long sessions
        if (_moduleVersionCache.Count >= 2048)
            _moduleVersionCache.Clear();

        string resolved;
        bool cacheable = true;
        try
        {
            var fvi = FileVersionInfo.GetVersionInfo(modulePath);
            string? ver = !string.IsNullOrWhiteSpace(fvi.FileVersion)
                ? fvi.FileVersion.Trim()
                : (!string.IsNullOrWhiteSpace(fvi.ProductVersion) ? fvi.ProductVersion.Trim() : null);
            resolved = string.IsNullOrWhiteSpace(ver) ? "—" : ver;
        }
        catch (FileNotFoundException)
        {
            resolved = "—";
            cacheable = false;
        }
        catch
        {
            resolved = "Unavailable";
            cacheable = false;
        }

        if (cacheable)
        {
            _moduleVersionCache[cacheKey] = resolved;
        }
        return resolved;
    }

    private static unsafe string ExtractFixedWideString(char* ptr, int maxChars)
    {
        ReadOnlySpan<char> span = new(ptr, maxChars);
        int nul = span.IndexOf('\0');
        if (nul >= 0) span = span[..nul];
        return new string(span);
    }

    public IReadOnlyList<ProcessThreadInfo> GetProcessThreads(uint pid)
    {
        var list = new List<ProcessThreadInfo>();
        try
        {
            using var p = Process.GetProcessById((int)pid);
            foreach (ProcessThread t in p.Threads)
            {
                using (t)
                {
                    TimeSpan cpu = TimeSpan.Zero;
                    try { cpu = t.TotalProcessorTime; } catch { }
                    string wait = t.ThreadState == System.Diagnostics.ThreadState.Wait ? t.WaitReason.ToString() : "—";
                    list.Add(new ProcessThreadInfo(
                        (uint)t.Id,
                        t.ThreadState.ToString(),
                        wait,
                        (ulong)t.StartAddress.ToInt64(),
                        cpu,
                        DataSourceProvenance.Live));
                }
            }
        }
        catch { }
        return list;
    }

    public byte[] ReadMemoryRegionHex(uint pid, ulong address, int length = 256)
    {
        int cap = Math.Clamp(length, 16, 65536);
        byte[] buf = new byte[cap];
        NativeMethods.Ramyxen_ReadProcessMemorySafe(pid, address, buf, cap, out int read);
        if (read <= 0) return [];
        return read == cap ? buf : buf[..read];
    }

    public IReadOnlyList<ExtractedMemoryString> SearchProcessStrings(
        uint pid,
        string query = "",
        int minLength = 5,
        int maxResults = 200,
        CancellationToken ct = default)
    {
        var regions = GetProcessVirtualRegions(pid, 512)
            .Where(r => r.State == "MEM_COMMIT" && !r.Protection.Contains("NOACCESS") && !r.Protection.Contains("GUARD"))
            .OrderByDescending(r => r.RegionType == "Private")
            .Take(64);

        var results = new List<ExtractedMemoryString>();
        byte[] chunk = new byte[65536];

        foreach (var reg in regions)
        {
            if (ct.IsCancellationRequested || results.Count >= maxResults) break;
            ulong maxRegionScan = Math.Min(reg.SizeBytes, 262144UL);
            for (ulong offset = 0; offset < maxRegionScan && results.Count < maxResults; offset += (ulong)chunk.Length)
            {
                int toRead = (int)Math.Min(maxRegionScan - offset, (ulong)chunk.Length);
                if (toRead <= minLength) break;

                NativeMethods.Ramyxen_ReadProcessMemorySafe(pid, reg.BaseAddress + offset, chunk, toRead, out int read);
                if (read <= minLength) break;

                // 1. Extract printable ASCII/UTF-8 runs (including flushing trailing string at buffer end!)
                int start = -1;
                for (int i = 0; i <= read; i++)
                {
                    bool printable = i < read && chunk[i] >= 32 && chunk[i] <= 126;
                    if (printable)
                    {
                        if (start < 0) start = i;
                    }
                    else if (start >= 0)
                    {
                        int len = i - start;
                        if (len >= minLength)
                        {
                            string s = Encoding.ASCII.GetString(chunk, start, Math.Min(160, len));
                            if (string.IsNullOrEmpty(query) || s.Contains(query, StringComparison.OrdinalIgnoreCase))
                            {
                                results.Add(new ExtractedMemoryString(reg.BaseAddress + offset + (ulong)start, "ASCII/UTF-8", s, reg.RegionType));
                                if (results.Count >= maxResults) break;
                            }
                        }
                        start = -1;
                    }
                }

                if (results.Count >= maxResults) break;

                // 2. Extract printable UTF-16LE (Windows Unicode) runs
                int uStart = -1;
                int uChars = 0;
                for (int i = 0; i <= read - 2; i += 2)
                {
                    bool uPrintable = chunk[i] >= 32 && chunk[i] <= 126 && chunk[i + 1] == 0;
                    if (uPrintable)
                    {
                        if (uStart < 0) { uStart = i; uChars = 0; }
                        uChars++;
                    }
                    else if (uStart >= 0)
                    {
                        if (uChars >= minLength)
                        {
                            string s = Encoding.Unicode.GetString(chunk, uStart, Math.Min(160, uChars) * 2);
                            if (string.IsNullOrEmpty(query) || s.Contains(query, StringComparison.OrdinalIgnoreCase))
                            {
                                results.Add(new ExtractedMemoryString(reg.BaseAddress + offset + (ulong)uStart, "UTF-16LE", s, reg.RegionType));
                                if (results.Count >= maxResults) break;
                            }
                        }
                        uStart = -1;
                        uChars = 0;
                    }
                }
                if (uStart >= 0 && uChars >= minLength && results.Count < maxResults)
                {
                    string s = Encoding.Unicode.GetString(chunk, uStart, Math.Min(160, uChars) * 2);
                    if (string.IsNullOrEmpty(query) || s.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(new ExtractedMemoryString(reg.BaseAddress + offset + (ulong)uStart, "UTF-16LE", s, reg.RegionType));
                    }
                }
            }
        }
        return results;
    }

    public bool TryCreateProcessMiniDump(uint pid, string outputPath, out string errorMessage)
    {
        errorMessage = string.Empty;
        bool ok = false;
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                // MiniDumpNormal (0x00000000) | MiniDumpWithDataSegs (0x00000001) | MiniDumpWithHandleData (0x00000004)
                ok = NativeMethods.MiniDumpWriteDump(proc.Handle, pid, fs.SafeFileHandle, 0x00000005, nint.Zero, nint.Zero, nint.Zero);
                if (!ok)
                {
                    errorMessage = $"MiniDumpWriteDump error {Marshal.GetLastWin32Error()}";
                }
            }

            if (!ok)
            {
                try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
            return false;
        }
    }

    public static bool IsRunningAsAdministrator()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static List<(uint Pid, uint ParentPid, uint Threads, string ExeFile)> GetToolhelpProcessEntries()
    {
        var list = new List<(uint, uint, uint, string)>(384);
        nint snap = NativeMethods.CreateToolhelp32Snapshot(0x00000002, 0); // TH32CS_SNAPPROCESS
        if (snap == nint.Zero || snap == -1) return list;
        try
        {
            var pe = new NativeMethods.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<NativeMethods.PROCESSENTRY32W>() };
            if (NativeMethods.Process32FirstW(snap, ref pe))
            {
                do
                {
                    list.Add((pe.th32ProcessID, pe.th32ParentProcessID, pe.cntThreads, pe.szExeFile ?? ""));
                } while (NativeMethods.Process32NextW(snap, ref pe));
            }
        }
        finally
        {
            NativeMethods.CloseHandle(snap);
        }
        return list;
    }

    private static unsafe string ExtractFixedPath(ref NativeProcessMemoryMetrics m)
    {
        fixed (char* ptr = m.ExecutablePath)
        {
            ReadOnlySpan<char> span = new(ptr, 260);
            int nul = span.IndexOf('\0');
            if (nul >= 0) span = span[..nul];
            return new string(span);
        }
    }

    private static string ClassifyProcessCategory(string name, string path)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("chrome") || n.Contains("msedge") || n.Contains("firefox")) return "Browser";
        if (n.Contains("devenv") || n.Contains("code") || n.Contains("rider") || n.Contains("msbuild")) return "IDE / Toolchain";
        if (n.Contains("vmmem") || n.Contains("docker") || n.Contains("vmware")) return "VM / Container";
        if (n.Contains("svchost") || n.Contains("lsass") || n.Contains("dwm") || n.Contains("csrss") || n.Contains("system")) return "Windows OS / Service";
        if (n.Contains("ramyxen")) return "RAMYXEN Diagnostic Host";
        return "Application";
    }

    private static string FormatProtection(uint p)
    {
        string baseP = (p & 0xFF) switch
        {
            0x01 => "PAGE_NOACCESS",
            0x02 => "PAGE_READONLY",
            0x04 => "PAGE_READWRITE",
            0x08 => "PAGE_WRITECOPY",
            0x10 => "PAGE_EXECUTE",
            0x20 => "PAGE_EXECUTE_READ",
            0x40 => "PAGE_EXECUTE_READWRITE",
            0x80 => "PAGE_EXECUTE_WRITECOPY",
            _ => $"0x{p:X2}"
        };
        if ((p & 0x100) != 0) baseP += " | PAGE_GUARD";
        if ((p & 0x200) != 0) baseP += " | PAGE_NOCACHE";
        return baseP;
    }
}
