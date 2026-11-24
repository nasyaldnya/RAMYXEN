using System.Runtime.InteropServices;
using RAMYXEN.Core.Models;
using RAMYXEN.Infrastructure.Windows;

namespace RAMYXEN.ETW;

public sealed record EtwProviderDescriptor(
    string Name,
    Guid ProviderGuid,
    string EventTypes,
    bool RequiresAdministrator,
    string MinWindowsVersion,
    string OutputMetrics,
    string KnownLimitations,
    DataSourceProvenance Provenance);

public sealed record EtwRecordingSessionState(
    string SessionId,
    uint TargetPid,
    string TargetProcessName,
    bool IsRecording,
    bool IsAdminElevated,
    DateTimeOffset StartedUtc,
    TimeSpan Elapsed,
    long EventsCaptured,
    long LostEvents,
    int BufferUsagePercent,
    string StatusMessage,
    IReadOnlyList<string> ActiveProviders,
    DataSourceProvenance Provenance);

public sealed class EtwSessionManager : IDisposable
{
    public static IReadOnlyList<EtwProviderDescriptor> SupportedProviders { get; } =
    [
        new("Microsoft-Windows-Kernel-Memory", new Guid("D1D93EF7-E1F2-4F45-9943-03D245FE6C00"),
            "VirtualAlloc, VirtualFree, PageFault, WorkingSetTrim", true, "Windows 11",
            "VirtualAlloc Commit Lifetimes, Hard Fault Latency, Page Table Activity",
            "Requires Administrator privileges to start kernel memory trace session.", DataSourceProvenance.EtwRequired),
        new("Microsoft-Windows-DotNETRuntime", new Guid("E13C0D23-CCBC-4E12-931B-D9CC2EEE27E4"),
            "GCAllocationTick, GCStart, GCEnd, GCHeapStats, PinObjectAtGCTime", false, "Windows 11 (.NET CLR)",
            "Gen 0/1/2/LOH/POH Sizes, Allocation Velocity, GC Pause Duration, Survivor Roots",
            "Applies only to processes hosting the .NET CLR runtime.", DataSourceProvenance.RuntimeSpecific),
        new("Microsoft-Windows-Heap-Snapshot", new Guid("901D2AFA-4FF6-46D7-8D0E-53645E1A47F5"),
            "HeapAlloc, HeapReAlloc, HeapFree, SegmentExpand", true, "Windows 11",
            "Native Win32 Heap Block Allocations, Surviving Heap Bytes, Call Stacks",
            "Per-allocation stack walk requires Administrator + IFEO stack tracing + PDB symbols.", DataSourceProvenance.EtwRequired),
        new("Windows-Kernel-Pool-Tag-Monitor", new Guid("20C46239-D059-4214-A11E-7D6769CBE020"),
            "PoolAlloc, PoolFree (Paged & Nonpaged Pool Tags)", true, "Windows 11",
            "4-Character Driver Pool Tag Attribution (e.g., MmSt, File, NVrm)",
            "Driver-to-tag resolution depends on loaded kernel module symbols.", DataSourceProvenance.AdminRequired)
    ];

    private const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
    private const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
    private const uint EVENT_TRACE_CONTROL_QUERY = 0;
    private const uint EVENT_TRACE_CONTROL_STOP = 1;
    private const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
    private const byte TRACE_LEVEL_INFORMATION = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct WNODE_HEADER
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public long TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_TRACE_PROPERTIES
    {
        public WNODE_HEADER Wnode;
        public uint BufferSize;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public nint LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint StartTraceW(out ulong sessionHandle, string sessionName, nint properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint ControlTraceW(ulong sessionHandle, string? sessionName, nint properties, uint controlCode);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint EnableTraceEx2(
        ulong traceHandle,
        ref Guid providerId,
        uint controlCode,
        byte level,
        ulong matchAnyKeyword,
        ulong matchAllKeyword,
        uint timeout,
        nint enableParameters);

    private readonly object _etwSync = new();
    private EtwRecordingSessionState? _currentSession;
    private ulong _nativeSessionHandle;
    private string _nativeSessionName = string.Empty;

    public EtwRecordingSessionState? CurrentSession { get { lock (_etwSync) return _currentSession; } }

    public Task<EtwRecordingSessionState> StartSessionAsync(uint targetPid, string processName, IReadOnlyList<string> selectedProviders)
        => Task.Run(() => StartSession(targetPid, processName, selectedProviders));

    public Task<EtwRecordingSessionState?> StopSessionAsync()
        => Task.Run(StopSession);

    public EtwRecordingSessionState StartSession(uint targetPid, string processName, IReadOnlyList<string> selectedProviders)
    {
        lock (_etwSync)
        {
            StopSessionCore();
            bool isAdmin = WindowsProcessCollector.IsRunningAsAdministrator();
            string sessionName = $"RAMYXEN_ETW_{Environment.ProcessId}_{DateTime.UtcNow:HHmmss}";
            _nativeSessionName = sessionName;

            nint propsPtr = AllocateTraceProperties(sessionName);
            try
            {
                uint rc = StartTraceW(out ulong handle, sessionName, propsPtr);
                if (rc == 0 && handle != 0)
                {
                    _nativeSessionHandle = handle;
                    int enabledCount = 0;
                    foreach (var p in SupportedProviders.Where(sp => selectedProviders.Contains(sp.Name)))
                    {
                        if (!isAdmin && p.RequiresAdministrator) continue;
                        Guid guid = p.ProviderGuid;
                        uint enRc = EnableTraceEx2(handle, ref guid, EVENT_CONTROL_CODE_ENABLE_PROVIDER, TRACE_LEVEL_INFORMATION, 0xFFFFFFFFFFFFFFFFUL, 0, 0, nint.Zero);
                        if (enRc == 0) enabledCount++;
                    }

                    var props = Marshal.PtrToStructure<EVENT_TRACE_PROPERTIES>(propsPtr);
                    int bufPct = props.NumberOfBuffers > 0
                        ? (int)Math.Clamp(((props.NumberOfBuffers - Math.Min(props.NumberOfBuffers, props.FreeBuffers)) * 100U) / props.NumberOfBuffers, 0U, 100U)
                        : 0;

                    _currentSession = new EtwRecordingSessionState(
                        SessionId: sessionName,
                        TargetPid: targetPid,
                        TargetProcessName: processName,
                        IsRecording: true,
                        IsAdminElevated: isAdmin,
                        StartedUtc: DateTimeOffset.UtcNow,
                        Elapsed: TimeSpan.Zero,
                        EventsCaptured: props.BuffersWritten,
                        LostEvents: props.EventsLost + props.RealTimeBuffersLost,
                        BufferUsagePercent: bufPct,
                        StatusMessage: $"Win32 ETW Real-Time Session Active ({enabledCount} provider(s) enabled; Buffers: {props.NumberOfBuffers} x {props.BufferSize} KB)",
                        ActiveProviders: selectedProviders,
                        Provenance: DataSourceProvenance.Live);
                    return _currentSession;
                }

                // Honest Win32 error reporting when unprivileged (ERROR_ACCESS_DENIED = 5) — NEVER fabricate event counts!
                string reason = rc == 5
                    ? "Win32 StartTraceW returned ERROR_ACCESS_DENIED (5). Windows real-time ETW sessions require Administrator or Performance Log Users membership. Zero simulated events generated."
                    : $"Win32 StartTraceW returned code {rc}. Real-time ETW session unavailable.";

                _currentSession = new EtwRecordingSessionState(
                    SessionId: sessionName,
                    TargetPid: targetPid,
                    TargetProcessName: processName,
                    IsRecording: false,
                    IsAdminElevated: isAdmin,
                    StartedUtc: DateTimeOffset.UtcNow,
                    Elapsed: TimeSpan.Zero,
                    EventsCaptured: 0,
                    LostEvents: 0,
                    BufferUsagePercent: 0,
                    StatusMessage: reason,
                    ActiveProviders: selectedProviders,
                    Provenance: DataSourceProvenance.AdminRequired);
                return _currentSession;
            }
            finally
            {
                Marshal.FreeHGlobal(propsPtr);
            }
        }
    }

    public EtwRecordingSessionState? PollSessionUpdate()
    {
        lock (_etwSync)
        {
            if (_currentSession == null || !_currentSession.IsRecording || _nativeSessionHandle == 0)
                return _currentSession;

            nint propsPtr = AllocateTraceProperties(_nativeSessionName);
            try
            {
                uint rc = ControlTraceW(_nativeSessionHandle, null, propsPtr, EVENT_TRACE_CONTROL_QUERY);
                var elapsed = DateTimeOffset.UtcNow - _currentSession.StartedUtc;
                if (rc == 0)
                {
                    var props = Marshal.PtrToStructure<EVENT_TRACE_PROPERTIES>(propsPtr);
                    int bufPct = props.NumberOfBuffers > 0
                        ? (int)Math.Clamp(((props.NumberOfBuffers - Math.Min(props.NumberOfBuffers, props.FreeBuffers)) * 100U) / props.NumberOfBuffers, 0U, 100U)
                        : 0;

                    _currentSession = _currentSession with
                    {
                        Elapsed = elapsed,
                        EventsCaptured = props.BuffersWritten,
                        LostEvents = props.EventsLost + props.RealTimeBuffersLost,
                        BufferUsagePercent = bufPct
                    };
                }
                else
                {
                    _currentSession = _currentSession with { Elapsed = elapsed };
                }
                return _currentSession;
            }
            finally
            {
                Marshal.FreeHGlobal(propsPtr);
            }
        }
    }

    public EtwRecordingSessionState? StopSession()
    {
        lock (_etwSync)
        {
            return StopSessionCore();
        }
    }

    private EtwRecordingSessionState? StopSessionCore()
    {
        if (_nativeSessionHandle != 0 || !string.IsNullOrEmpty(_nativeSessionName))
        {
            nint propsPtr = AllocateTraceProperties(_nativeSessionName);
            try
            {
                ControlTraceW(_nativeSessionHandle, string.IsNullOrEmpty(_nativeSessionName) ? null : _nativeSessionName, propsPtr, EVENT_TRACE_CONTROL_STOP);
            }
            catch { }
            finally
            {
                Marshal.FreeHGlobal(propsPtr);
                _nativeSessionHandle = 0;
                _nativeSessionName = string.Empty;
            }
        }

        if (_currentSession == null) return null;
        _currentSession = _currentSession with
        {
            IsRecording = false,
            Elapsed = DateTimeOffset.UtcNow - _currentSession.StartedUtc,
            BufferUsagePercent = 0,
            StatusMessage = _currentSession.IsRecording
                ? "Win32 ETW session stopped via ControlTraceW(EVENT_TRACE_CONTROL_STOP)."
                : _currentSession.StatusMessage
        };
        return _currentSession;
    }

    private static nint AllocateTraceProperties(string sessionName)
    {
        int structSize = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>();
        int nameBytes = (sessionName.Length + 1) * 2;
        int totalSize = structSize + nameBytes + 64;
        nint ptr = Marshal.AllocHGlobal(totalSize);
        Marshal.Copy(new byte[totalSize], 0, ptr, totalSize);

        var props = new EVENT_TRACE_PROPERTIES
        {
            Wnode = new WNODE_HEADER
            {
                BufferSize = (uint)totalSize,
                Guid = Guid.NewGuid(),
                ClientContext = 1, // QPC clock resolution
                Flags = WNODE_FLAG_TRACED_GUID
            },
            BufferSize = 64, // 64 KB buffers
            MinimumBuffers = 4,
            MaximumBuffers = 32,
            LogFileMode = EVENT_TRACE_REAL_TIME_MODE,
            LoggerNameOffset = (uint)structSize
        };
        Marshal.StructureToPtr(props, ptr, fDeleteOld: false);
        return ptr;
    }

    public void Dispose() => StopSession();
}
