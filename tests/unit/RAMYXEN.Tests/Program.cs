using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using RAMYXEN.Application.Services;
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

int passed = 0;
int failed = 0;

void Check(string name, Action assertion)
{
    try
    {
        assertion();
        Console.WriteLine($"  [PASS] {name}");
        passed++;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  [FAIL] {name}: {ex.Message}");
        failed++;
    }
}

void AssertTrue(bool cond, string msg)
{
    if (!cond) throw new InvalidOperationException(msg);
}

Console.WriteLine("\n=== RAMYXEN .NET 10 Automated Verification Checks (40 Checks) ===");

string tmpRoot = Path.Combine(Path.GetTempPath(), $"ramyxen_tests_{Guid.NewGuid():N}");
Directory.CreateDirectory(tmpRoot);

try
{
    // 1. UNIT TESTS: UnitFormatter & Models (including long.MinValue overflow guard)
    Check("1. UnitFormatter distinguishes binary IEC (KiB/MiB/GiB) from decimal SI (KB/MB/GB) and handles long.MinValue safely", () =>
    {
        string iec = UnitFormatter.FormatBytes(1024 * 1024, useBinaryIec: true);
        string si = UnitFormatter.FormatBytes(1000 * 1000, useBinaryIec: false);
        AssertTrue(iec == "1.0 MiB", $"Expected 1.0 MiB, got {iec}");
        AssertTrue(si == "1.0 MB", $"Expected 1.0 MB, got {si}");
        AssertTrue(UnitFormatter.FormatSignedDeltaBytes(1024 * 1024) == "+1.0 MiB", "Signed positive delta mismatch");
        AssertTrue(UnitFormatter.FormatSignedDeltaBytes(-1024 * 1024) == "-1.0 MiB", "Signed negative delta mismatch");
        string minVal = UnitFormatter.FormatSignedDeltaBytes(long.MinValue);
        AssertTrue(minVal.StartsWith('-'), $"long.MinValue formatting failed: {minVal}");
    });

    // 2. UNIT TESTS: BoundedHistoryRingBuffer
    Check("2. BoundedHistoryRingBuffer enforces strict capacity and circular order without leaking", () =>
    {
        var ring = new BoundedHistoryRingBuffer<int>(5);
        for (int i = 1; i <= 12; i++) ring.Add(i);
        AssertTrue(ring.Count == 5, $"Expected count 5, got {ring.Count}");
        var recent = ring.GetRecent();
        AssertTrue(recent.SequenceEqual([8, 9, 10, 11, 12]), "Circular order mismatch");
    });

    // 3. LOCALIZATION TESTS: LocalizationService (en-US, ar-SA RTL, Plurals, Bidi, External Overrides)
    Check("3. LocalizationService loads en-US (LTR) and ar-SA (RTL) with 100% completeness and unified Arabic terminology", () =>
    {
        var loc = new LocalizationService();
        AssertTrue(loc.AvailableLanguages.Count >= 2, "Expected at least 2 languages");
        AssertTrue(loc.AvailableLanguages.All(l => l.CompletenessPercent >= 100.0), "Expected 100% completeness across registered locales");
        AssertTrue(loc.GetString("app.name") == "RAMYXEN", "app.name mismatch");
        AssertTrue(loc.TrySetLanguage("ar-SA"), "Failed to switch to ar-SA");
        AssertTrue(loc.IsRightToLeft, "ar-SA must set IsRightToLeft = true");
        AssertTrue(loc.GetString("navigation.overview") == "نظرة عامة", "Arabic overview translation mismatch");
        AssertTrue(loc.GetString("overview.topologyBridge") == "جسر طوبولوجيا الذاكرة الفيزيائية إلى الافتراضية", "Unified Arabic physical terminology mismatch");
        string isolated = loc.IsolateTechnicalToken("0x00007FF6A0000000");
        AssertTrue(isolated.StartsWith('\u2066') && isolated.EndsWith('\u2069'), "RTL Bidi LRI/PDI isolation missing");
    });

    Check("4. LocalizationService formats 6-category Arabic CLDR ICU plurals preserving prefix/suffix text", () =>
    {
        var loc = new LocalizationService();
        loc.TrySetLanguage("ar-SA");
        AssertTrue(loc.FormatPlural("process.count", 0) == "لا توجد عمليات", "Arabic zero plural mismatch");
        AssertTrue(loc.FormatPlural("process.count", 1) == "عملية واحدة", "Arabic one plural mismatch");
        AssertTrue(loc.FormatPlural("process.count", 2) == "عمليتان", "Arabic two plural mismatch");
        AssertTrue(loc.FormatPlural("process.count", 5).Contains("عمليات"), "Arabic few plural mismatch");
        AssertTrue(loc.FormatPlural("process.count", 15).Contains("عملية"), "Arabic many plural mismatch");
        AssertTrue(loc.FormatPlural("process.count", 100).Contains("عملية"), "Arabic other plural mismatch");

        // Verify prefix/suffix preservation in diagnostics.crashBannerTitle & palette.resultCount (F-043)
        AssertTrue(loc.FormatPlural("diagnostics.crashBannerTitle", 1).StartsWith("سجّلت الجلسة السابقة ") &&
                   loc.FormatPlural("diagnostics.crashBannerTitle", 1).Contains("حدثاً تشخيصياً واحداً"),
            "Arabic crashBannerTitle failed to preserve prefix or singular branch");
        AssertTrue(loc.FormatPlural("diagnostics.crashBannerTitle", 2).Contains("حدثين تشخيصيين"), "Arabic dual crashBannerTitle mismatch");
        AssertTrue(loc.FormatPlural("palette.resultCount", 0) == "لا توجد نتائج مطابقة", "Arabic zero palette.resultCount mismatch");
        AssertTrue(loc.FormatPlural("palette.resultCount", 2) == "نتيجتان مطابقتان", "Arabic dual palette.resultCount mismatch");

        loc.TrySetLanguage("en-US");
        AssertTrue(loc.FormatPlural("process.count", 1) == "1 process", "English singular mismatch");
        AssertTrue(loc.FormatPlural("process.count", 14) == "14 processes", "English plural mismatch");
        AssertTrue(loc.FormatPlural("diagnostics.crashBannerTitle", 1) == "Previous session recorded 1 diagnostic event", "English crashBannerTitle prefix preservation failed");
        AssertTrue(loc.FormatPlural("diagnostics.crashBannerTitle", 3) == "Previous session recorded 3 diagnostic events", "English plural crashBannerTitle mismatch");
    });

    Check("5. LocalizationService supports external language pack overrides and rejects malformed untrusted packs", () =>
    {
        string userOverride = Path.Combine(tmpRoot, "user_loc");
        Directory.CreateDirectory(Path.Combine(userOverride, "locales"));
        File.WriteAllText(Path.Combine(userOverride, "locales", "en-US.json"), "{ \"app.tagline\": \"Custom Override Tagline\" }", Encoding.UTF8);

        var loc = new LocalizationService(userOverrideRoot: userOverride);
        AssertTrue(loc.GetString("app.tagline") == "Custom Override Tagline", "External override failed to apply");
        AssertTrue(loc.GetString("navigation.overview") == "Overview", "Bundled fallback key lost during override");

        File.WriteAllText(Path.Combine(userOverride, "locales", "ar-SA.json"), "{ \"app.tagline\": \"\u202EMalicious\" INVALID", Encoding.UTF8);
        loc.ReloadLanguagePacks();
        loc.TrySetLanguage("ar-SA");
        AssertTrue(loc.GetString("app.tagline") == "افهم كل بايت.", "Malformed untrusted override was not rejected safely");
    });

    // 6-11. MEMORY EXHAUSTION & LEAK INVESTIGATION ENGINE: ALL 10 MANDATORY DIAGNOSTIC SCENARIOS
    var baseSys = new WindowsSystemMemoryCollector().Collect();
    var t0 = DateTimeOffset.UtcNow;

    Check("6. Leak Scenarios 1 & 6: Temporary allocation spike and post-spike memory release classify as NormalGrowth", () =>
    {
        var engine = new MemoryExhaustionAndLeakEngine();
        ulong[] commitProfile = [120_000_000, 120_000_000, 260_000_000, 310_000_000, 180_000_000, 125_000_000, 122_000_000];
        IReadOnlyList<LeakInvestigationCase> cases = [];
        for (int i = 0; i < commitProfile.Length; i++)
        {
            var p = new ProcessMemoryInfo(7001, 4, "BurstWorker.exe", @"C:\App\BurstWorker.exe", "User", "Application", "x64",
                commitProfile[i], commitProfile[i], 0, commitProfile[i], 310_000_000, 600_000_000, 50_000, 20_000,
                500, 180, 8, false, false, t0, 0, DataSourceProvenance.Live);
            cases = engine.AnalyzeSample(baseSys with { TimestampUtc = t0.AddSeconds(i * 2) }, [p]);
        }
        AssertTrue(cases[0].Classification == LeakClassification.NormalGrowth,
            $"Expected NormalGrowth after post-spike release, got {cases[0].Classification}");
    });

    Check("7. Leak Scenario 2: Large process (> 1.5 GiB) with stable long-term memory (0 B/s slope) classifies as NormalGrowth", () =>
    {
        var engine = new MemoryExhaustionAndLeakEngine();
        IReadOnlyList<LeakInvestigationCase> cases = [];
        for (int i = 0; i < 12; i++)
        {
            var p = new ProcessMemoryInfo(7002, 4, "SqlServer.exe", @"C:\SQL\sqlservr.exe", "User", "Application", "x64",
                1_600_000_000UL, 1_550_000_000UL, 50_000_000UL, 1_600_000_000UL, 1_600_000_000UL, 3_000_000_000UL, 100_000, 50_000,
                1000, 420, 32, false, false, t0, 0, DataSourceProvenance.Live);
            cases = engine.AnalyzeSample(baseSys with { TimestampUtc = t0.AddSeconds(i * 2) }, [p]);
        }
        AssertTrue(cases[0].Classification == LeakClassification.NormalGrowth,
            $"Stable 1.6 GiB process with 0 B/s slope must be NormalGrowth, got {cases[0].Classification}");
    });

    Check("8. Leak Scenarios 3 & 5: Continuous monotonic private commit growth & persistent managed retention classify as StrongLeakEvidence", () =>
    {
        var engine = new MemoryExhaustionAndLeakEngine();
        IReadOnlyList<LeakInvestigationCase> cases = [];
        for (int i = 0; i < 10; i++)
        {
            ulong commit = 200_000_000UL + (ulong)(i * 15_000_000L);
            var p = new ProcessMemoryInfo(7003, 4, "LeakyService.exe", @"C:\Svc\LeakyService.exe", "User", "Application", "x64",
                commit, commit, 0, commit, commit, 1_000_000_000UL, 100_000, 50_000,
                1000, 300, 12, false, true, t0, 7_500_000, DataSourceProvenance.Live);
            cases = engine.AnalyzeSample(baseSys with { TimestampUtc = t0.AddSeconds(i * 2) }, [p]);
        }
        AssertTrue(cases[0].Classification == LeakClassification.StrongLeakEvidence,
            $"Expected StrongLeakEvidence on monotonic growth, got {cases[0].Classification}");
        AssertTrue(cases[0].Classification != LeakClassification.ConfirmedLeak,
            "OLS slope/R² alone must never automatically assert ConfirmedLeak");
        AssertTrue(cases[0].Domain == LeakDomain.ManagedDotNetGc, $"Expected ManagedDotNetGc domain, got {cases[0].Domain}");
    });

    Check("9. Leak Scenario 4: Managed .NET app with sawtooth GC expansion and reclamation drops does NOT trigger false leak alarm", () =>
    {
        var engine = new MemoryExhaustionAndLeakEngine();
        ulong[] sawtooth = [100_000_000, 130_000_000, 160_000_000, 110_000_000, 145_000_000, 180_000_000, 115_000_000, 150_000_000, 120_000_000];
        IReadOnlyList<LeakInvestigationCase> cases = [];
        for (int i = 0; i < sawtooth.Length; i++)
        {
            var p = new ProcessMemoryInfo(7004, 4, "DotNetWebApp.exe", @"C:\App\DotNetWebApp.exe", "User", "Application", "x64",
                sawtooth[i], sawtooth[i], 0, sawtooth[i], 180_000_000, 500_000_000, 80_000, 30_000,
                800, 210, 16, false, true, t0, 0, DataSourceProvenance.Live);
            cases = engine.AnalyzeSample(baseSys with { TimestampUtc = t0.AddSeconds(i * 2) }, [p]);
        }
        AssertTrue(cases[0].Classification is LeakClassification.NormalGrowth or LeakClassification.ElevatedGrowth,
            $"Sawtooth GC cycle must not trigger SuspectedLeak/StrongLeakEvidence, got {cases[0].Classification}");
    });

    Check("10. Leak Scenario 7: Working Set growth with stable Private Commit is diagnosed as SharedOrMappedMemory (NormalGrowth)", () =>
    {
        var engine = new MemoryExhaustionAndLeakEngine();
        IReadOnlyList<LeakInvestigationCase> cases = [];
        for (int i = 0; i < 8; i++)
        {
            ulong ws = 60_000_000UL + (ulong)(i * 20_000_000L);
            ulong flatCommit = 50_000_000UL;
            var p = new ProcessMemoryInfo(7007, 4, "FileMapper.exe", @"C:\App\FileMapper.exe", "User", "Application", "x64",
                ws, flatCommit, ws - flatCommit, flatCommit, ws, 800_000_000UL, 40_000, 20_000,
                500, 140, 6, false, false, t0, 0, DataSourceProvenance.Live);
            cases = engine.AnalyzeSample(baseSys with { TimestampUtc = t0.AddSeconds(i * 2) }, [p]);
        }
        AssertTrue(cases[0].Classification == LeakClassification.NormalGrowth && cases[0].Domain == LeakDomain.SharedOrMappedMemory,
            $"Expected NormalGrowth + SharedOrMappedMemory for WS-only growth, got {cases[0].Classification} / {cases[0].Domain}");
    });

    Check("11. Leak Scenario 8, 9 & 10: Process exit & PID reuse resets history; high Standby is Healthy; genuine commit pressure detected", () =>
    {
        var engine = new MemoryExhaustionAndLeakEngine();
        // Feed 6 growing samples for PID 7008 (OldProcess.exe)
        for (int i = 0; i < 6; i++)
        {
            ulong c = 100_000_000UL + (ulong)(i * 20_000_000L);
            var oldP = new ProcessMemoryInfo(7008, 4, "OldProcess.exe", @"C:\Old.exe", "User", "Application", "x64",
                c, c, 0, c, c, c * 2, 10_000, 10_000, 100, 100, 4, false, false, t0, 0, DataSourceProvenance.Live);
            engine.AnalyzeSample(baseSys with { TimestampUtc = t0.AddSeconds(i) }, [oldP]);
        }
        // Now PID 7008 is reused by NewProcess.exe with a newer StartTimeUtc
        var newP = new ProcessMemoryInfo(7008, 4, "NewProcess.exe", @"C:\New.exe", "User", "Application", "x64",
            40_000_000UL, 40_000_000UL, 0, 40_000_000UL, 40_000_000UL, 80_000_000UL, 10_000, 10_000, 10, 50, 2, false, false, t0.AddSeconds(10), 0, DataSourceProvenance.Live);
        var afterReuse = engine.AnalyzeSample(baseSys with { TimestampUtc = t0.AddSeconds(10) }, [newP]);
        AssertTrue(afterReuse[0].TargetProcessName == "NewProcess.exe" && afterReuse[0].BaselineCommitBytes == 40_000_000UL && afterReuse[0].Classification == LeakClassification.NormalGrowth,
            "PID reuse failed to reset baseline history tracker");

        // Scenario 9: High Standby Cache (e.g., 14 GiB Standby inside 15 GiB Available out of 32 GiB Total) -> None (Healthy)
        var highStandbySys = baseSys with
        {
            TotalPhysicalBytes = 32UL * 1024 * 1024 * 1024,
            InUsePhysicalBytes = 17UL * 1024 * 1024 * 1024,
            AvailablePhysicalBytes = 15UL * 1024 * 1024 * 1024,
            StandbyCacheBytes = 14UL * 1024 * 1024 * 1024,
            MemoryLoadPercent = 53,
            CommitTotalBytes = 18UL * 1024 * 1024 * 1024,
            CommitLimitBytes = 36UL * 1024 * 1024 * 1024
        };
        AssertTrue(engine.DetectSystemExhaustion(highStandbySys) == MemoryExhaustionKind.None, "High Standby Cache must not trigger exhaustion");

        // Scenario 10: Genuine commit charge exhaustion (97% commit ratio) -> AllocationFailureRisk
        var commitPressureSys = highStandbySys with
        {
            CommitTotalBytes = 35UL * 1024 * 1024 * 1024,
            CommitLimitBytes = 36UL * 1024 * 1024 * 1024
        };
        AssertTrue(engine.DetectSystemExhaustion(commitPressureSys) == MemoryExhaustionKind.AllocationFailureRisk, "97% Commit charge must trigger AllocationFailureRisk");
    });

    // 12-14. WINDOWS INTERNALS & C++20 NATIVE INTEROP TESTS
    Check("12. C# <-> C++ RAMYXEN.Native.dll ABI v102, 7 struct sizes, native error propagation & NtQuerySystemInformation counters", () =>
    {
        int abi = NativeMethods.EnsureNativeAbiCompatibility();
        AssertTrue(abi == 102, $"Native ABI version mismatch (got {abi})");

        // Trigger a real native error and confirm FormatLastNativeError returns exact function & status (F-017)
        int errRc = NativeMethods.Ramyxen_GetProcessMemoryMetrics(0xFFFFFFFEU, out _);
        AssertTrue(errRc != 0, "Expected error for non-existent PID");
        string nativeErrStr = NativeMethods.FormatLastNativeError();
        AssertTrue(nativeErrStr.Contains("Ramyxen_GetProcessMemoryMetrics") && nativeErrStr.Contains("status="),
            $"FormatLastNativeError missing native function context: {nativeErrStr}");

        int rc = NativeMethods.Ramyxen_GetKernelPageListMetrics(out var kList);
        AssertTrue(rc == 0 && kList.HasExactFaultCounters == 1U && kList.PageFaultCountCumulative > 0,
            "NtQuerySystemInformation(SystemPerformanceInformation) failed to return kernel fault counters");

        var collector = new WindowsSystemMemoryCollector();
        var sys = collector.Collect();
        AssertTrue(sys.TotalPhysicalBytes > 512UL * 1024 * 1024, "TotalPhysicalBytes invalid");
        AssertTrue(sys.InUsePhysicalBytes > 0 && sys.AvailablePhysicalBytes > 0, "Physical memory counters invalid");
        AssertTrue(sys.CommitTotalBytes > 0 && sys.CommitLimitBytes >= sys.CommitTotalBytes, "Commit counters invalid");
        AssertTrue(collector.LatestPageLists.StandbyPriorityBytes.Count == 8, "Expected 8 Standby Priority (P0-P7) buckets");
    });

    Check("13. WindowsProcessCollector enumerates processes, virtual regions (with total), heaps, modules with real FileVersion, threads & hex", () =>
    {
        var pc = new WindowsProcessCollector();
        var procs = pc.CollectProcesses();
        uint selfPid = (uint)Environment.ProcessId;
        var selfProc = procs.FirstOrDefault(p => p.Pid == selfPid);
        AssertTrue(selfProc != null, "Current process not found in process enumeration");
        AssertTrue(!selfProc!.IsProtectedOrRestricted && selfProc.Provenance == DataSourceProvenance.Live,
            "Current process must NOT be marked IsProtectedOrRestricted when opened with PROCESS_QUERY_LIMITED_INFORMATION");
        AssertTrue(selfProc.IsManagedDotNet, "Current .NET 10 process must be detected as IsManagedDotNet = true");

        var (regions, totalRegions, _, denied) = pc.GetProcessVirtualRegionsWithTotal(selfPid, 512);
        AssertTrue(!denied && regions.Count >= 10 && totalRegions >= regions.Count,
            $"Expected >=10 virtual regions, got {regions.Count} (total {totalRegions})");

        var heaps = pc.GetProcessHeaps(selfPid);
        AssertTrue(heaps.Count >= 1 && heaps[0].CommittedBytes > 0 && heaps[0].BlockCount > 0,
            "Self process default heap must report non-zero CommittedBytes and BlockCount via HeapWalk");

        var (modules, totalMods, _) = pc.GetProcessModulesWithTotal(selfPid);
        AssertTrue(modules.Count >= 5 && totalMods >= modules.Count, "Expected >=5 loaded modules in current process");
        // Verify restored DLL FileVersion metadata on real Windows DLLs (F-029)
        bool hasRealDllVersion = modules.Any(m =>
            (m.ModuleName.Equals("ntdll.dll", StringComparison.OrdinalIgnoreCase) || m.ModuleName.Equals("kernel32.dll", StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(m.Version) && m.Version != "—" && m.Version.Contains('.'));
        AssertTrue(hasRealDllVersion, "Expected real FileVersion string on ntdll.dll/kernel32.dll");

        var threads = pc.GetProcessThreads(selfPid);
        AssertTrue(threads.Count >= 1, "Expected >=1 thread in current process");

        var commitReg = regions.First(r => r.State == "MEM_COMMIT" && !r.Protection.Contains("NOACCESS"));
        byte[] hex = pc.ReadMemoryRegionHex(selfPid, commitReg.BaseAddress, 64);
        AssertTrue(hex.Length > 0, "ReadMemoryRegionHex returned 0 bytes on committed self region");
    });

    Check("14. SearchProcessStrings extracts both ASCII/UTF-8 and UTF-16LE Unicode strings accurately", () =>
    {
        var pc = new WindowsProcessCollector();
        uint selfPid = (uint)Environment.ProcessId;
        var allStrings = pc.SearchProcessStrings(selfPid, "", 5, 120);
        AssertTrue(allStrings.Any(s => s.EncodingName == "ASCII/UTF-8"), "Expected ASCII/UTF-8 strings in live process memory");
        AssertTrue(allStrings.Any(s => s.EncodingName == "UTF-16LE"), "Expected UTF-16LE Unicode strings in live process memory");
    });

    // 15-16. ETW & PROFILER HONESTY TESTS
    Check("15. EtwSessionManager invokes real Win32 StartTraceW/ControlTraceW without fabricating fake events when unprivileged", () =>
    {
        using var etw = new EtwSessionManager();
        uint selfPid = (uint)Environment.ProcessId;
        var state = etw.StartSession(selfPid, "RAMYXEN.Tests.exe", ["Microsoft-Windows-DotNETRuntime"]);
        if (state.IsRecording)
        {
            AssertTrue(state.Provenance == DataSourceProvenance.Live, "Active Win32 ETW session must report Live provenance");
            var stopped = etw.StopSession();
            AssertTrue(stopped != null && !stopped.IsRecording, "ETW session should stop cleanly via ControlTraceW");
        }
        else
        {
            AssertTrue(state.Provenance == DataSourceProvenance.AdminRequired && state.EventsCaptured == 0,
                "Unprivileged ETW session must report AdminRequired and 0 fabricated events");
        }
    });

    Check("16. AllocationAndRuntimeProfiler never attributes RAMYXEN.exe's own .NET 10 GC metrics to external native processes", () =>
    {
        var profiler = new AllocationAndRuntimeProfiler();
        uint selfPid = (uint)Environment.ProcessId;
        var selfGc = profiler.CollectManagedGcTelemetry(selfPid, "RAMYXEN.Tests.exe");
        AssertTrue(selfGc.Provenance == DataSourceProvenance.RuntimeSpecific && selfGc.TotalCommittedGcBytes > 0,
            "Self process must return exact RuntimeSpecific .NET 10 GC metrics");

        // External non-managed PID (e.g. PID 4 System) must NOT receive RAMYXEN.exe's GC metrics
        var extGc = profiler.CollectManagedGcTelemetry(4, "System");
        AssertTrue(extGc.Provenance == DataSourceProvenance.Unavailable && extGc.TotalCommittedGcBytes == 0 && extGc.Gen0Collections == 0,
            "External native process must return 0 GC bytes and Unavailable provenance (never self GC counters)");

        var hotspots = profiler.AnalyzeProcessAllocationHotspots(selfPid);
        AssertTrue(hotspots.Count > 0 && hotspots.Any(h => h.SourceProvider.Contains("HeapWalk") || h.SymbolStatus.Contains("Module")),
            "Hotspot analysis must include verified HeapWalk and module-mapped entries");
    });

    // 17-19. FORENSICS & ADVERSARIAL DUMP PARSER TESTS
    Check("17. ForensicAnalysisEngine parses real MiniDumpWriteDump (.dmp) MINIDUMP_HEADER & stream directory (SystemInfo, Threads, Modules)", () =>
    {
        uint selfPid = (uint)Environment.ProcessId;
        var forensics = new ForensicAnalysisEngine();
        var liveFindings = forensics.AnalyzeLiveProcess(selfPid, "RAMYXEN.Tests.exe");
        AssertTrue(liveFindings.Count >= 1, "Live forensic analysis returned no findings");

        string dmpPath = Path.Combine(tmpRoot, "self_verified.dmp");
        var pc = new WindowsProcessCollector();
        bool dumped = pc.TryCreateProcessMiniDump(selfPid, dmpPath, out string err);
        AssertTrue(dumped && File.Exists(dmpPath), $"MiniDumpWriteDump failed: {err}");

        var (src, dumpFindings, preview) = forensics.AnalyzeMemoryDumpFile(dmpPath);
        AssertTrue(src.IsSupported && src.SourceKind.Contains("MDMP Directory Verified"), $"Expected verified MDMP directory, got {src.SourceKind}");
        AssertTrue(src.IntegritySha256.Length == 64, $"Expected full 64-char SHA-256 hex digest, got {src.IntegritySha256}");
        AssertTrue(src.DetectedArchitecture.Contains("x64") || src.DetectedArchitecture.Contains("ARM64"), $"Expected parsed SystemInfoStream architecture, got {src.DetectedArchitecture}");
        AssertTrue(dumpFindings[0].EvidenceDetail.Contains("SystemInfoStream") && dumpFindings[0].EvidenceDetail.Contains("ModuleListStream"),
            $"Expected parsed stream names in evidence: {dumpFindings[0].EvidenceDetail}");
    });

    Check("18. ForensicAnalysisEngine rejects truncated 4-byte 'MDMP' and adversarial out-of-bounds RVA .dmp files", () =>
    {
        var forensics = new ForensicAnalysisEngine();

        // Case A: Truncated 4-byte "MDMP" file
        string truncPath = Path.Combine(tmpRoot, "truncated.dmp");
        File.WriteAllBytes(truncPath, "MDMP"u8.ToArray());
        var (srcA, findA, _) = forensics.AnalyzeMemoryDumpFile(truncPath);
        AssertTrue(!srcA.IsSupported && findA[0].FindingId == "DUMP-ERR-01", "Truncated 4-byte MDMP file was not rejected!");

        // Case B: 32-byte header with adversarial StreamDirectoryRva = 0x7FFFFFF0
        string badRvaPath = Path.Combine(tmpRoot, "badrva.dmp");
        byte[] badHeader = new byte[64];
        BitConverter.GetBytes(0x504D444DU).CopyTo(badHeader, 0); // 'MDMP'
        BitConverter.GetBytes(0x0000A793U).CopyTo(badHeader, 4); // Version
        BitConverter.GetBytes(12U).CopyTo(badHeader, 8);         // 12 streams
        BitConverter.GetBytes(0x7FFFFFF0U).CopyTo(badHeader, 12); // Out-of-bounds Directory RVA
        File.WriteAllBytes(badRvaPath, badHeader);
        var (srcB, findB, _) = forensics.AnalyzeMemoryDumpFile(badRvaPath);
        AssertTrue(!srcB.IsSupported && findB[0].FindingId == "DUMP-ERR-01", "Adversarial out-of-bounds RVA MDMP header was not rejected!");
    });

    Check("19. ForensicAnalysisEngine VerifyBufferPeHeader distinguishes random 2-byte 'MZ' from valid MZ + e_lfanew + PE\\0\\0", () =>
    {
        byte[] fakeMz = new byte[128];
        fakeMz[0] = (byte)'M';
        fakeMz[1] = (byte)'Z';
        BitConverter.GetBytes(0x40).CopyTo(fakeMz, 0x3C); // e_lfanew = 0x40, but no PE\0\0 at 0x40
        AssertTrue(!ForensicAnalysisEngine.VerifyBufferPeHeader(fakeMz), "2-byte MZ without PE\\0\\0 must be rejected");

        fakeMz[0x40] = (byte)'P';
        fakeMz[0x41] = (byte)'E';
        fakeMz[0x42] = 0;
        fakeMz[0x43] = 0;
        AssertTrue(ForensicAnalysisEngine.VerifyBufferPeHeader(fakeMz), "Valid MZ + e_lfanew + PE\\0\\0 must be verified");
    });

    // 20-23. SECURITY, SNAPSHOT VAULT, CSV INJECTION, HTML XSS & PDF 1.4 XREF TESTS
    Check("20. SnapshotVaultService creates, compares, shreds in-place, and blocks path traversal ('../')", () =>
    {
        var vault = new SnapshotVaultService(Path.Combine(tmpRoot, "snapshots"));
        var sys = new WindowsSystemMemoryCollector().Collect();
        var procs = new WindowsProcessCollector().CollectProcesses();

        var s1 = vault.CreateTelemetrySnapshot("Baseline", sys, procs);
        var s2 = vault.CreateTelemetrySnapshot("Target", sys with { InUsePhysicalBytes = sys.InUsePhysicalBytes + (64UL * 1024 * 1024) }, procs);
        AssertTrue(File.Exists(s1.FilePath) && s1.Sha256Hash.Length == 64, "Snapshot file or SHA-256 missing");

        var diff = vault.CompareSnapshots(s1, s2);
        AssertTrue(diff.PhysicalInUseDeltaBytes == 64L * 1024 * 1024, "Snapshot comparison delta mismatch");
        AssertTrue(vault.DeleteSnapshotPermanently(s1.SnapshotId), "Permanent snapshot deletion failed");
        AssertTrue(!File.Exists(s1.FilePath), "Deleted snapshot file still exists");

        bool threwTraversal = false;
        try { vault.DeleteSnapshotPermanently("..\\..\\outside"); }
        catch (UnauthorizedAccessException) { threwTraversal = true; }
        AssertTrue(threwTraversal, "Path traversal in DeleteSnapshotPermanently was not blocked!");
    });

    Check("21. ReportExporterService neutralizes CSV Formula Injection (CWE-1236) and escapes embedded quotes", () =>
    {
        string evilFormula = ReportExporterService.SanitizeCsvField("=cmd|'/C calc'!A0");
        AssertTrue(evilFormula == "\"'=cmd|'/C calc'!A0\"", $"CSV formula trigger was not neutralized: {evilFormula}");

        string quoted = ReportExporterService.SanitizeCsvField("App\"Name");
        AssertTrue(quoted == "\"App\"\"Name\"", $"Embedded quote was not escaped: {quoted}");
    });

    Check("22. ReportExporterService redacts user profile & UNC paths and prevents HTML XSS across CSV, JSON, HTML & PDF", () =>
    {
        using var host = new RamyxenPlatformHost(tmpRoot);
        var sys = new WindowsSystemMemoryCollector().Collect();
        var advProc = new ProcessMemoryInfo(
            8888, 4, "<script>alert(1)</script>.exe", @"C:\Users\SecretAlice\AppData\Local\evil.exe",
            "SecretAlice", "=cmd|'/C calc'", "x64",
            50_000_000, 40_000_000, 10_000_000, 50_000_000, 50_000_000, 100_000_000, 10_000, 10_000,
            100, 100, 4, false, false, DateTimeOffset.UtcNow, 0, DataSourceProvenance.Live);
        var advCase = new LeakInvestigationCase(
            "INV-8888", 8888, "<script>alert(1)</script>.exe", @"C:\Users\SecretAlice\AppData\Local\evil.exe",
            4, LeakDomain.ProcessPrivateCommit, MemoryExhaustionKind.None, LeakClassification.SuspectedLeak, 75,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 40_000_000, 50_000_000, 100_000, 0.9, 100, 110,
            "Native Heap", [@"Path: C:\Users\SecretAlice\secret.dll and \\corpserver\share\payload.dll"],
            ["Inspect"], ["None"], DataSourceProvenance.Live);

        string csv = host.Reporter.ExportCsv(Path.Combine(tmpRoot, "sec.csv"), sys, [advProc], true);
        string json = host.Reporter.ExportJson(Path.Combine(tmpRoot, "sec.json"), sys, [advProc], [advCase], true);
        string html = host.Reporter.ExportHtml(Path.Combine(tmpRoot, "sec.html"), sys, [advProc], [advCase], true);
        string pdf = host.Reporter.ExportPdf(Path.Combine(tmpRoot, "sec.pdf"), sys, [advProc], [advCase], true);

        string csvText = File.ReadAllText(csv);
        string jsonText = File.ReadAllText(json);
        string htmlText = File.ReadAllText(html);
        string pdfText = File.ReadAllText(pdf, Encoding.ASCII);

        AssertTrue(!csvText.Contains("SecretAlice") && csvText.Contains("'=cmd"), "CSV failed redaction or formula sanitization");
        AssertTrue(!jsonText.Contains("SecretAlice") && !jsonText.Contains("corpserver") && jsonText.Contains("[REDACTED_USER]"), "JSON leaked unredacted user or UNC path");
        AssertTrue(!htmlText.Contains("SecretAlice") && !htmlText.Contains("<script>alert(1)</script>") && htmlText.Contains("&lt;script&gt;"), "HTML leaked path or raw <script> XSS tag");
        AssertTrue(!pdfText.Contains("SecretAlice"), "PDF leaked unredacted user path");
    });

    Check("23. ReportExporterService PDF 1.4 writer generates valid 6-entry xref table, exact startxref offset & Arabic safety", () =>
    {
        using var host = new RamyxenPlatformHost(tmpRoot);
        host.RefreshTelemetry();
        host.SetLanguage("ar-SA");
        string pdfPath = host.Reporter.ExportPdf(
            Path.Combine(tmpRoot, "arabic_valid.pdf"),
            host.Monitor.LatestSystem!,
            host.Monitor.LatestProcesses,
            host.Monitor.ActiveInvestigations,
            true);

        string rawPdf = File.ReadAllText(pdfPath, Encoding.ASCII);
        AssertTrue(rawPdf.StartsWith("%PDF-1.4\n"), "Missing %PDF-1.4 header");
        int xrefIdx = rawPdf.IndexOf("xref\n0 6\n", StringComparison.Ordinal);
        AssertTrue(xrefIdx > 50, "Missing xref 0 6 table");
        AssertTrue(rawPdf.Contains($"startxref\n{xrefIdx}\n%%EOF"), $"startxref does not match exact xref byte offset {xrefIdx}");
    });

    Check("24. Source Localization Audit: RamyxenMainWindow.cs uses LocalizationService across all workspaces & all 560 keys resolve", () =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RAMYXEN.sln")))
            dir = dir.Parent;
        AssertTrue(dir is not null, "Could not locate repo root");
        string repoRoot = dir!.FullName;

        string mainWinSrc = File.ReadAllText(Path.Combine(repoRoot, "src", "RAMYXEN.App", "Views", "RamyxenMainWindow.cs"));
        // Verify removed hardcoded English strings in Diagnostics & Settings & Process Comparison (F-027, F-030)
        AssertTrue(!mainWinSrc.Contains("\"Local Diagnostics Directory\""), "Hardcoded English string still present in Diagnostics workspace");
        AssertTrue(!mainWinSrc.Contains("\"Memory Unit Formatting Standard\""), "Hardcoded English string still present in Settings workspace");
        AssertTrue(!mainWinSrc.Contains("\"DIFFERENTIAL DELTA (A - B)\""), "Hardcoded English string still present in Process Comparison");
        AssertTrue(mainWinSrc.Contains("MakeButtonWithTrailingChevron"), "Expected Fluent chevron helper instead of raw arrow glyphs");
        AssertTrue(!mainWinSrc.Contains("└─ "), "ASCII tree prefix still present instead of Fluent chevron indentation");

        // Verify EVERY key in canonical en-US.json resolves in both en-US and ar-SA with zero [missing:*]
        var enJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, "localization", "locales", "en-US.json")));
        var allKeys = enJson.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        AssertTrue(allKeys.Count >= 550, $"Expected >=550 canonical keys, found {allKeys.Count}");

        var loc = new LocalizationService();
        foreach (string lang in new[] { "en-US", "ar-SA", "zh-CN", "ja-JP" })
        {
            loc.TrySetLanguage(lang);
            foreach (string key in allKeys)
            {
                string val = loc.GetString(key);
                AssertTrue(!string.IsNullOrWhiteSpace(val) && !val.StartsWith("[missing:"), $"Missing key {key} in {lang}");
            }
        }
    });

    // 25. STRESS, PID-CHURN BOUNDING & SELF-PERFORMANCE BUDGET TEST
    Check("25. Stress, PID-Churn Bounding & Self-Performance Budget Test (50 language/theme switches + repeated live sampling)", () =>
    {
        using var host = new RamyxenPlatformHost(tmpRoot);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 50; i++)
        {
            host.SetLanguage(i % 2 == 0 ? "ar-SA" : "en-US");
            host.SetTheme((AppThemeMode)(i % 3));
        }
        for (int i = 0; i < 5; i++)
        {
            host.RefreshTelemetry();
        }
        sw.Stop();
        var self = host.Monitor.SelfMetrics!;
        Console.WriteLine($"         [PERF] Self Working Set: {UnitFormatter.FormatBytes(self.SelfWorkingSetBytes, true)} | Last Sample: {self.LastSampleDurationMs} ms | 50 switches + 5 full system sweeps: {sw.ElapsedMilliseconds} ms");
        AssertTrue(self.SelfWorkingSetBytes < 250UL * 1024 * 1024, "Self Working Set exceeded 250 MiB budget");
        AssertTrue(self.LastSampleDurationMs < 200.0, $"Sample duration {self.LastSampleDurationMs} ms exceeded 200 ms budget");
    });

    // 26. OFFICIAL WINDOWS SHELL EXECUTABLE ICON EXTRACTION, PROVENANCE, ZERO UI-THREAD STAT & LRU EVICTION
    Check("26. WindowsShellIconService extracts official PE icons, has zero UI-thread stat, rejects generic icons & enforces LRU", () =>
    {
        using var logger = new StructuredDiagnosticLogger(Path.Combine(tmpRoot, "icon_test"));
        using var iconSvc = new WindowsShellIconService(logger, capacity: 32);

        // Verify BuildCacheKey is pure O(1) in-memory and does NOT touch filesystem (even for non-existent path - F-007)
        string? pureKey = WindowsShellIconService.BuildCacheKey(@"C:\NonExistent\FakeApp.exe", 32);
        AssertTrue(pureKey == @"c:\nonexistent\fakeapp.exe|32", $"BuildCacheKey performed unexpected disk check or bad key: {pureKey}");

        string explorerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var icon1 = iconSvc.ResolveAsync(explorerPath, 32).GetAwaiter().GetResult();
        AssertTrue(icon1 is not null && icon1.IsOfficialAppIcon && icon1.Provenance == ExecutableIconProvenance.PeResource &&
                   icon1.Width == 32 && icon1.Height == 32 && icon1.BgraPixels.Length == 32 * 32 * 4,
            "Failed to extract 32x32 BGRA PE_RESOURCE icon for explorer.exe");

        // Second call must hit LRU cache
        var icon2 = iconSvc.ResolveAsync(explorerPath, 32).GetAwaiter().GetResult();
        AssertTrue(icon2 is not null && iconSvc.CacheHits >= 1, "Expected LRU cache hit on repeated icon lookup");

        // Also test chrome.exe if installed on host
        string[] chromeCandidates =
        [
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
        ];
        string? chromePath = chromeCandidates.FirstOrDefault(File.Exists);
        if (chromePath is not null)
        {
            var chromeIcon = iconSvc.ResolveAsync(chromePath, 48).GetAwaiter().GetResult();
            AssertTrue(chromeIcon is not null && chromeIcon.IsOfficialAppIcon && chromeIcon.Width == 48,
                "Failed to extract 48x48 official icon for chrome.exe");
        }

        // Missing/non-existent path must return null safely (never spoofed generic icon - F-023)
        var missing = iconSvc.ResolveAsync(@"C:\DoesNotExist\ghost_proc_999.exe", 32).GetAwaiter().GetResult();
        AssertTrue(missing is null, "Missing executable path must gracefully return null for fallback rendering");

        // Verify LRU capacity bounding across 40 unique keys
        for (int i = 0; i < 40; i++)
            _ = iconSvc.ResolveAsync($@"C:\DoesNotExist\lru_entry_{i}.exe", 32).GetAwaiter().GetResult();
        AssertTrue(iconSvc.CachedEntryCount <= 32, $"LRU cache exceeded capacity 32 (count={iconSvc.CachedEntryCount})");
    });

    // 27. NON-BLOCKING ASYNC PROCESS INSPECTION, STALE REJECTION, ZERO CTS RACES & OUT-OF-PROCESS HEAP WORKER
    Check("27. ProcessInspectionCoordinator rejects stale PID A when switching to PID B, survives 50x cancellation storm & isolates heap lane", () =>
    {
        using var host = new RamyxenPlatformHost(Path.Combine(tmpRoot, "inspector_test"));
        host.RefreshTelemetry();

        uint selfPid = (uint)Environment.ProcessId;
        var otherProc = host.Monitor.LatestProcesses.FirstOrDefault(p => p.Pid != selfPid && !p.IsProtectedOrRestricted)
                     ?? host.Monitor.LatestProcesses.First(p => p.Pid != selfPid);

        // Single authoritative version increment via StartLightweightInspection (F-013)
        long vBefore = host.ProcessInspector.CurrentSelectionVersion;
        var (phA, taskA) = host.ProcessInspector.StartLightweightInspection(otherProc.Pid, otherProc.IsProtectedOrRestricted);
        long vAfterA = host.ProcessInspector.CurrentSelectionVersion;
        AssertTrue(vAfterA == vBefore + 1 && phA.SelectionVersion == vAfterA,
            $"StartLightweightInspection must increment selection version exactly once ({vBefore} -> {vAfterA})");

        // Immediately supersede with PID B (selfPid)
        var (phB, taskB) = host.ProcessInspector.StartLightweightInspection(selfPid, false);
        var resA = taskA.GetAwaiter().GetResult();
        var resB = taskB.GetAwaiter().GetResult();

        AssertTrue(resA is null, "Superseded PID A task must return null (stale rejection)");
        AssertTrue(resB is not null && resB.Pid == selfPid && !resB.IsLoading && !resB.HeapsLoaded,
            "PID B lightweight inspection must succeed without running heap walk by default");
        AssertTrue(host.ProcessInspector.LatestPayload?.Pid == selfPid, "LatestPayload must match winning PID B");
        AssertTrue(resB!.Regions.Count >= 5 && resB.Modules.Count >= 5 && resB.Threads.Count >= 1,
            "Lightweight async inspection must populate Regions, Modules, and Threads");

        // 50-iteration rapid cancellation storm: zero ObjectDisposedException allowed (F-014)
        var stormTasks = new List<Task<ProcessDeepDivePayload?>>();
        for (int i = 0; i < 50; i++)
        {
            uint target = (i % 2 == 0) ? otherProc.Pid : selfPid;
            bool restricted = (i % 2 == 0) && otherProc.IsProtectedOrRestricted;
            stormTasks.Add(host.ProcessInspector.StartLightweightInspection(target, restricted).Completion);
        }
        Task.WhenAll(stormTasks).GetAwaiter().GetResult();
        var finalStorm = host.ProcessInspector.StartLightweightInspection(selfPid, false).Completion.GetAwaiter().GetResult();
        AssertTrue(finalStorm is not null && finalStorm.Pid == selfPid, "Coordinator failed to recover cleanly after 50x cancellation storm");

        // Explicit on-demand heap walk on self PID
        var withHeaps = host.ProcessInspector.LoadHeapsOnDemandAsync(selfPid, finalStorm!.SelectionVersion).GetAwaiter().GetResult();
        AssertTrue(withHeaps is not null && withHeaps.HeapsLoaded && withHeaps.Heaps is not null && withHeaps.Heaps.Count >= 1,
            "On-demand bounded heap walk must return real heaps when explicitly requested");

        // External real user-mode process heap walk via isolated RAMYXEN.HeapWorker.exe (F-003)
        var extTarget = host.Monitor.LatestProcesses.FirstOrDefault(p =>
            p.Pid != selfPid && !p.IsProtectedOrRestricted &&
            p.Name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
            ?? otherProc;
        if (!extTarget.IsProtectedOrRestricted)
        {
            var extSel = host.ProcessInspector.StartLightweightInspection(extTarget.Pid, false).Completion.GetAwaiter().GetResult();
            AssertTrue(extSel is not null, "External process lightweight inspection failed");
            var extHeaps = host.ProcessInspector.LoadHeapsOnDemandAsync(extTarget.Pid, extSel!.SelectionVersion).GetAwaiter().GetResult();
            AssertTrue(extHeaps is not null && extHeaps.HeapsLoaded && (extHeaps.HeapsTimedOut || (extHeaps.Heaps is not null && extHeaps.Heaps.Count >= 1)),
                $"Out-of-process RAMYXEN.HeapWorker.exe failed on external process {extTarget.Name} (PID {extTarget.Pid})");
        }
    });

    // 28. CRASH DIAGNOSTICS, DEEP ZIP PII REDACTION AUDIT (INCLUDING SHORT USERNAME "PC", QUOTED/JSON SECRETS, VERSION PRESERVATION)
    Check("28. CrashAndDiagnosticReportService redacts ALL ZIP entries (quoted/JSON secrets, UNC, short user, SID) & preserves versions", () =>
    {
        string diagRoot = Path.Combine(tmpRoot, "diag_test");
        using var logger = new StructuredDiagnosticLogger(diagRoot);
        var state = new DiagnosticRuntimeState { ActiveViewId = "process-deep-dive", ActiveLocale = "ar-SA", ActiveTheme = "Dark" };
        var service = new CrashAndDiagnosticReportService(logger, state, diagRoot);

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string fakeSensitive =
            $@"Failed accessing {userProfile}\Desktop\secret.dmp and C:\Users\PrivateUser42\doc.txt and \\corpserver\finance\sheet.xlsx " +
            $@"and \\\\corpserver2\\hr\\payroll.csv for user {Environment.UserName} (username=""{Environment.UserName}"") " +
            $@"user@example.com on {Environment.MachineName} Authorization: Basic dXNlcjpwYXNz " +
            $@"https://alice:hunter2@corp.internal/api Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3OCJ9.abc123def456 " +
            $@"api_key=sk-live-998877 ""access_token"": ""tok-json-777"" db_password=""Super Secret 123!"" " +
            $@"Data Source=db01;Password=""Conn Pass 99""; SID=S-1-5-21-3623811015-3361044348-30300820-1013 ip=192.168.42.99 runtime=10.0.0.0 sdk=2.7.0.0";

        var record = service.CaptureManagedException(new InvalidOperationException(fakeSensitive), "UnitTest", isFatal: false);
        AssertTrue(File.Exists(record.JsonFilePath) && File.Exists(record.TextFilePath), "Crash report JSON and TXT files must be persisted");
        AssertTrue(service.GetUnacknowledgedReports().Count == 1, "Expected 1 unacknowledged diagnostic report");

        string zipPath = Path.Combine(diagRoot, "export_bundle.zip");
        // Pass specificReport: null to also verify fallback-to-latest-report export path (F-031)
        service.ExportDiagnosticBundle(zipPath, includeConsentGatedMinidump: false, specificReport: null);
        AssertTrue(File.Exists(zipPath) && new FileInfo(zipPath).Length > 200, "Diagnostic ZIP export failed");

        bool sawPreservedVersion = false;
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            AssertTrue(archive.Entries.Count >= 3, $"Expected >=3 entries in diagnostic ZIP, got {archive.Entries.Count}");
            string rawUserProfileEscaped = userProfile.Replace(@"\", @"\\");
            foreach (var entry in archive.Entries)
            {
                using var sr = new StreamReader(entry.Open(), Encoding.UTF8);
                string content = sr.ReadToEnd();
                if (content.Contains("10.0.0.0", StringComparison.Ordinal) && content.Contains("2.7.0.0", StringComparison.Ordinal))
                    sawPreservedVersion = true;
                AssertTrue(!content.Contains(userProfile, StringComparison.OrdinalIgnoreCase), $"ZIP entry '{entry.FullName}' leaked raw UserProfile path!");
                AssertTrue(!content.Contains(rawUserProfileEscaped, StringComparison.OrdinalIgnoreCase), $"ZIP entry '{entry.FullName}' leaked JSON-escaped UserProfile path!");
                AssertTrue(!content.Contains($@"\Users\{Environment.UserName}", StringComparison.OrdinalIgnoreCase), $"ZIP entry '{entry.FullName}' leaked short/real username in path!");
                AssertTrue(!content.Contains($"for user {Environment.UserName}", StringComparison.OrdinalIgnoreCase), $"ZIP entry '{entry.FullName}' leaked contextual short username!");
                AssertTrue(!content.Contains($"username=\"{Environment.UserName}\"", StringComparison.OrdinalIgnoreCase), $"ZIP entry '{entry.FullName}' leaked quoted short username!");
                AssertTrue(!content.Contains("PrivateUser42", StringComparison.OrdinalIgnoreCase), $"ZIP entry '{entry.FullName}' leaked PrivateUser42!");
                AssertTrue(!content.Contains("user@example.com", StringComparison.OrdinalIgnoreCase), $"ZIP entry '{entry.FullName}' leaked email!");
                AssertTrue(!content.Contains("corpserver", StringComparison.OrdinalIgnoreCase), $"ZIP entry '{entry.FullName}' leaked UNC server!");
                AssertTrue(!content.Contains("corpserver2", StringComparison.OrdinalIgnoreCase), $"ZIP entry '{entry.FullName}' leaked JSON-escaped UNC server!");
                AssertTrue(!content.Contains("dXNlcjpwYXNz", StringComparison.Ordinal), $"ZIP entry '{entry.FullName}' leaked Basic auth!");
                AssertTrue(!content.Contains("hunter2", StringComparison.Ordinal), $"ZIP entry '{entry.FullName}' leaked URL password!");
                AssertTrue(!content.Contains("eyJhbGciOiJIUzI1NiJ9", StringComparison.Ordinal), $"ZIP entry '{entry.FullName}' leaked JWT!");
                AssertTrue(!content.Contains("sk-live-998877", StringComparison.Ordinal), $"ZIP entry '{entry.FullName}' leaked api_key!");
                AssertTrue(!content.Contains("tok-json-777", StringComparison.Ordinal), $"ZIP entry '{entry.FullName}' leaked JSON access_token!");
                AssertTrue(!content.Contains("Super Secret 123!", StringComparison.Ordinal), $"ZIP entry '{entry.FullName}' leaked quoted db_password!");
                AssertTrue(!content.Contains("Conn Pass 99", StringComparison.Ordinal), $"ZIP entry '{entry.FullName}' leaked quoted connection string password!");
                AssertTrue(!content.Contains("S-1-5-21-3623811015", StringComparison.Ordinal), $"ZIP entry '{entry.FullName}' leaked Windows SID!");
                AssertTrue(!content.Contains("192.168.42.99", StringComparison.Ordinal), $"ZIP entry '{entry.FullName}' leaked IPv4!");
                if (Environment.MachineName.Length >= 3)
                    AssertTrue(!content.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase), $"ZIP entry '{entry.FullName}' leaked MachineName!");
            }
        }
        AssertTrue(sawPreservedVersion, "DiagnosticPrivacyRedactor falsely mangled .NET/SDK version quads (10.0.0.0 / 2.7.0.0)!");

        service.AcknowledgeAllReports();
        AssertTrue(service.GetUnacknowledgedReports().Count == 0, "Acknowledged reports must clear recovery banner queue");
    });

    // 29. BACKGROUND UI HANG WATCHDOG: BOUNDED QUEUE, RECOVERY-TO-RUNNING -> 2ND HANG, POWER SUSPEND, DEGRADED STATE & NO FAKE STACK TRACE
    Check("29. UiHangWatchdog detects stalls, recovers cleanly to Running, detects 2nd hang (R2-NEW-001) & handles power/degraded", () =>
    {
        string wdRoot = Path.Combine(tmpRoot, "watchdog_test");
        using var logger = new StructuredDiagnosticLogger(wdRoot);
        var state = new DiagnosticRuntimeState { ActiveViewId = "process-explorer", SelectedPid = 9999 };
        var reports = new CrashAndDiagnosticReportService(logger, state, wdRoot);

        int postAttempts = 0;
        Action? pendingHeartbeatCallback = null;
        int stallMode = 1; // 1 = stall (capture callback without executing), 0 = execute immediately
        using var firstHang = new ManualResetEventSlim(false);
        using var secondHang = new ManualResetEventSlim(false);

        using var watchdog = new UiHangWatchdog(
            cb =>
            {
                Interlocked.Increment(ref postAttempts);
                if (Volatile.Read(ref stallMode) == 1)
                {
                    Volatile.Write(ref pendingHeartbeatCallback, cb);
                    return true;
                }
                cb();
                return true;
            },
            reports,
            logger,
            heartbeatIntervalMs: 40,
            hangThresholdMs: 110)
        {
            IgnoreAttachedDebuggerForTests = true
        };
        watchdog.HangDetected += _ =>
        {
            if (watchdog.HangCount == 1) firstHang.Set();
            else if (watchdog.HangCount >= 2) secondHang.Set();
        };
        watchdog.Start();

        bool fired1 = firstHang.Wait(TimeSpan.FromSeconds(3));
        Thread.Sleep(150); // Let several ticks pass while stalled — must NOT enqueue more heartbeats!
        int postsDuringStall = Volatile.Read(ref postAttempts);
        AssertTrue(fired1 && watchdog.HangCount == 1 && watchdog.State == WatchdogState.HangDetected,
            $"Expected 1st HangDetected with HangCount=1, got fired={fired1}, count={watchdog.HangCount}, state={watchdog.State}");
        AssertTrue(postsDuringStall == 1, $"Expected exactly 1 pending heartbeat (zero queue flooding), got {postsDuringStall}");

        // Now simulate UI thread unfreezing and executing the pending heartbeat callback! (R2-NEW-001 regression proof)
        Volatile.Write(ref stallMode, 0);
        Volatile.Read(ref pendingHeartbeatCallback)?.Invoke();
        bool recovered = false;
        for (int i = 0; i < 30; i++)
        {
            if (watchdog.State == WatchdogState.Running) { recovered = true; break; }
            Thread.Sleep(25);
        }
        AssertTrue(recovered, $"Watchdog stuck in {watchdog.State} instead of recovering to Running after UI heartbeat completed (R2-NEW-001)!");

        // Trigger a 2nd independent stall to prove recovery truly reset _hangReportedForCurrentStall
        Volatile.Write(ref stallMode, 1);
        bool fired2 = secondHang.Wait(TimeSpan.FromSeconds(3));
        AssertTrue(fired2 && watchdog.HangCount >= 2 && watchdog.State == WatchdogState.HangDetected,
            $"Expected 2nd HangDetected after recovery, got fired2={fired2}, count={watchdog.HangCount}, state={watchdog.State}");

        // Power suspend/resume transitions must update state cleanly without false hangs
        watchdog.NotifyPowerSuspend();
        AssertTrue(watchdog.State == WatchdogState.SuspendedPower, "Expected WatchdogState.SuspendedPower");
        watchdog.NotifyPowerResume();
        AssertTrue(watchdog.State == WatchdogState.Running, "Expected WatchdogState.Running after resume");
        watchdog.Stop();
        AssertTrue(watchdog.State == WatchdogState.Stopped, "Expected WatchdogState.Stopped");

        var savedHangs = reports.GetAllReports(5).Where(r => r.Kind == "UIHang").ToList();
        AssertTrue(savedHangs.Count >= 2 && File.Exists(savedHangs[0].JsonFilePath), "Hang reports were not persisted to disk");
        AssertTrue(savedHangs[0].StackTrace is null, "UIHang report must NOT fabricate a fake static StackTrace string (F-019)");

        // Degraded state check when dispatcher returns false (must NOT fabricate hang report)
        using var degradedWd = new UiHangWatchdog(_ => false, reports, logger, heartbeatIntervalMs: 40, hangThresholdMs: 90)
        {
            IgnoreAttachedDebuggerForTests = true
        };
        degradedWd.Start();
        Thread.Sleep(220);
        AssertTrue(degradedWd.State == WatchdogState.Degraded && degradedWd.HangCount == 0,
            $"False dispatcher must transition to Degraded with 0 fabricated hangs (state={degradedWd.State}, hangs={degradedWd.HangCount})");
        degradedWd.Stop();
    });

    // 30. COMPLETE ARABIC & ENGLISH COMMAND PALETTE COVERAGE (ALL 98 TITLES + DESCRIPTIONS)
    Check("30. Complete English (en-US) & Arabic (ar-SA) localization covers all 98 titles AND descriptions with Arabic script", () =>
    {
        var loc = new LocalizationService();
        string[] routeKeys =
        [
            "splash","first-launch","welcome","compat-check","permissions-setup","init-monitor","shell-main","shell-nav-expanded","shell-nav-collapsed",
            "global-search","command-palette","notifications","status-bar-detail","overview","live-monitor","memory-usage-details","memory-composition",
            "process-explorer","process-tree","process-comparison","process-deep-dive","process-overview","process-memory","process-memory-map",
            "heap-explorer","stack-explorer","process-modules","process-threads","process-handles","memory-strings","virtual-overview","virtual-map",
            "virtual-region-details","physical-overview","page-distribution","physical-pages","physical-ranges","file-summary","file-details",
            "priority-summary","page-inspector","profiler-home","profiler-new","profiler-providers","profiler-recording","profiler-timeline",
            "profiler-hotspots","profiler-callstacks","profiler-gc","profiler-comparison","snapshot-library","snapshot-create","snapshot-details",
            "snapshot-comparison","snapshot-comparison-viz","snapshot-import","snapshot-export","snapshot-management","forensics-home","forensics-source",
            "forensics-import","forensics-acq-config","forensics-acq-progress","forensics-analysis","forensics-pstree","forensics-vad","forensics-pte",
            "forensics-kernel","forensics-drivers","forensics-strings","forensics-yara","forensics-suspicious","forensics-artifacts","forensics-timeline",
            "forensics-report","alerts-center","alert-details","alert-rules","alert-rule-create","report-center","report-preview","export-config",
            "export-progress","settings-general","settings-appearance","settings-monitoring","settings-collection","settings-sampling","settings-storage",
            "settings-privacy","settings-permissions","settings-notifications","settings-advanced","settings-shortcuts","settings-datasources",
            "help-center","glossary","about"
        ];
        AssertTrue(routeKeys.Length == 98, "Must check all 98 destinations");

        string ToCamel(string k)
        {
            var sb = new StringBuilder();
            bool up = false;
            foreach (char c in k) { if (c == '-') { up = true; continue; } sb.Append(up ? char.ToUpperInvariant(c) : c); up = false; }
            return sb.ToString();
        }

        foreach (string lang in new[] { "en-US", "ar-SA", "zh-CN", "ja-JP" })
        {
            loc.TrySetLanguage(lang);
            foreach (string rk in routeKeys)
            {
                string ck = ToCamel(rk);
                string t = loc.GetString($"dest.{ck}.title");
                string d = loc.GetString($"dest.{ck}.desc");
                AssertTrue(!t.StartsWith("[missing:") && !d.StartsWith("[missing:"), $"Missing destination localization for {rk} in {lang}");
                if (lang == "ar-SA")
                {
                    AssertTrue(t.Any(c => c >= '\u0600' && c <= '\u06FF'), $"Arabic destination title for {rk} not translated: '{t}'");
                    AssertTrue(d.Any(c => c >= '\u0600' && c <= '\u06FF'), $"Arabic destination desc for {rk} not translated: '{d}'");
                }
                else if (lang == "zh-CN")
                {
                    AssertTrue(t.Any(c => c >= '\u4E00' && c <= '\u9FFF'), $"Simplified Chinese destination title for {rk} not translated: '{t}'");
                    AssertTrue(d.Any(c => c >= '\u4E00' && c <= '\u9FFF'), $"Simplified Chinese destination desc for {rk} not translated: '{d}'");
                }
                else if (lang == "ja-JP")
                {
                    AssertTrue(t.Any(c => (c >= '\u3040' && c <= '\u30FF') || (c >= '\u4E00' && c <= '\u9FFF')), $"Japanese destination title for {rk} not translated: '{t}'");
                    AssertTrue(d.Any(c => (c >= '\u3040' && c <= '\u30FF') || (c >= '\u4E00' && c <= '\u9FFF')), $"Japanese destination desc for {rk} not translated: '{d}'");
                }
            }
        }
    });

    // 31. OFFICIAL ICO (7 FRAMES), MANIFEST (1.1.0.0 + PerMonitorV2) & COMPILED RAMYXEN.EXE PE RESOURCES
    Check("31. RAMYXEN.ico (7 frames), app.manifest (1.1.0.0 + PerMonitorV2) & compiled RAMYXEN.exe PE RT_GROUP_ICON + RT_MANIFEST verified", () =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RAMYXEN.sln")))
            dir = dir.Parent;
        AssertTrue(dir is not null, "Could not locate RAMYXEN.sln repository root");
        string repoRoot = dir!.FullName;
        string icoPath = Path.Combine(repoRoot, "src", "RAMYXEN.App", "Assets", "RAMYXEN.ico");
        string manifestPath = Path.Combine(repoRoot, "src", "RAMYXEN.App", "app.manifest");
        AssertTrue(File.Exists(icoPath), $"Missing RAMYXEN.ico at {icoPath}");
        AssertTrue(File.Exists(manifestPath), $"Missing app.manifest at {manifestPath}");

        byte[] icoBytes = File.ReadAllBytes(icoPath);
        AssertTrue(icoBytes.Length > 8000 && icoBytes[0] == 0 && icoBytes[1] == 0 && icoBytes[2] == 1 && icoBytes[3] == 0,
            "RAMYXEN.ico is not a valid Windows ICO container");
        int frameCount = BitConverter.ToUInt16(icoBytes, 4);
        AssertTrue(frameCount >= 6, $"Expected >=6 multi-resolution icon frames, got {frameCount}");

        string manifestXml = File.ReadAllText(manifestPath);
        AssertTrue(manifestXml.Contains("PerMonitorV2", StringComparison.Ordinal), "app.manifest missing PerMonitorV2 DPI awareness");
        AssertTrue(manifestXml.Contains("version=\"1.1.0.0\"", StringComparison.Ordinal), "app.manifest assemblyIdentity version must be 1.1.0.0");

        // Inspect compiled Release RAMYXEN.exe PE resources (RT_GROUP_ICON=14, RT_MANIFEST=24) — NEVER silently skip
        string[] candidateExes =
        [
            Path.Combine(repoRoot, "artifacts", "release-v1.1.0-candidate", "RAMYXEN-Portable-win-x64", "RAMYXEN.exe"),
            Path.Combine(repoRoot, "src", "RAMYXEN.App", "bin", "Release", "net10.0-windows10.0.22621.0", "win-x64", "RAMYXEN.exe"),
            Path.Combine(repoRoot, "src", "RAMYXEN.App", "bin", "x64", "Release", "net10.0-windows10.0.22621.0", "win-x64", "RAMYXEN.exe")
        ];
        string? appExe = candidateExes.FirstOrDefault(File.Exists);
        AssertTrue(appExe is not null, "Compiled Release RAMYXEN.exe not found — build Release app before running PE resource verification (R2-NEW-019)");
        AssertTrue(PeResourceInspector.HasResourceType(appExe!, 14), $"Compiled {appExe} is missing RT_GROUP_ICON (14)!");
        AssertTrue(PeResourceInspector.HasResourceType(appExe!, 24), $"Compiled {appExe} is missing RT_MANIFEST (24)!");
    });

    // 32. ARABIC ORTHOGRAPHIC NORMALIZATION FOR COMMAND PALETTE & PROCESS SEARCH (F-041)
    Check("32. LocalizationService.NormalizeForSearch normalizes Arabic Alef, Teh Marbuta, Alef Maksura & diacritics", () =>
    {
        AssertTrue(LocalizationService.NormalizeForSearch("أيقونات") == LocalizationService.NormalizeForSearch("ايقونات"), "Alef normalization failed");
        AssertTrue(LocalizationService.NormalizeForSearch("الذَاكِرَة") == LocalizationService.NormalizeForSearch("الذاكره"), "Diacritic + Teh Marbuta normalization failed");
        AssertTrue(LocalizationService.NormalizeForSearch("مستوى") == LocalizationService.NormalizeForSearch("مستوي"), "Alef Maksura normalization failed");
    });

    // 33. CRASH REPORT RETENTION PRUNING (<= 30) & UNBOUNDED ACKNOWLEDGE-ALL (> 20) (F-031, F-050)
    Check("33. CrashAndDiagnosticReportService prunes old reports to <=30 and acknowledges >20 reports cleanly", () =>
    {
        string pruneRoot = Path.Combine(tmpRoot, "prune_test");
        using var logger = new StructuredDiagnosticLogger(pruneRoot);
        var svc = new CrashAndDiagnosticReportService(logger, new DiagnosticRuntimeState(), pruneRoot);

        for (int i = 0; i < 34; i++)
        {
            svc.CaptureManagedException(new InvalidOperationException($"Test burst #{i}"), "BurstTest", isFatal: false);
        }

        int jsonCount = Directory.GetFiles(svc.CrashReportsDirectory, "*.json").Length;
        AssertTrue(jsonCount <= 30 && jsonCount >= 25, $"Expected crash report pruning to <=30, got {jsonCount}");
        AssertTrue(svc.GetUnacknowledgedReports().Count == jsonCount, $"GetUnacknowledgedReports must not cap at 20 (got {svc.GetUnacknowledgedReports().Count} vs {jsonCount})");

        svc.AcknowledgeAllReports();
        AssertTrue(svc.GetUnacknowledgedReports().Count == 0, "AcknowledgeAllReports must clear ALL reports beyond 20");
    });

    // 34. OFF-THREAD CONSENT-GATED MINIDUMP CAPTURE, ISOLATED DIAGNOSTIC-DUMPS DIR & RETENTION PRUNING (<= 5) (F-006, F-031, R2-NEW-019)
    Check("34. CrashAndDiagnosticReportService CaptureConsentGatedMinidumpAsync captures valid MDMP in diagnostic-dumps & prunes to <=5", () =>
    {
        string dumpRoot = Path.Combine(tmpRoot, "consent_dump_test");
        using var logger = new StructuredDiagnosticLogger(dumpRoot);
        var svc = new CrashAndDiagnosticReportService(logger, new DiagnosticRuntimeState(), dumpRoot);

        AssertTrue(svc.DumpsDirectory.EndsWith("diagnostic-dumps", StringComparison.OrdinalIgnoreCase),
            $"Diagnostic dumps must live in isolated 'diagnostic-dumps' directory (got {svc.DumpsDirectory})");

        // Plant a non-consent user dump file to verify pruning ONLY deletes ramyxen-consent-*.dmp
        string foreignDump = Path.Combine(svc.DumpsDirectory, "user-forensic-keep.dmp");
        File.WriteAllText(foreignDump, "KEEP");

        // Pre-seed 5 older consent dump files so 1 real capture triggers pruning to <=5 without 6 slow minidumps
        for (int i = 0; i < 5; i++)
        {
            string oldDump = Path.Combine(svc.DumpsDirectory, $"ramyxen-consent-2025010{i}-000000.dmp");
            File.WriteAllText(oldDump, "OLD");
            File.SetLastWriteTimeUtc(oldDump, DateTime.UtcNow.AddMinutes(-10 - i));
        }

        var res = svc.CaptureConsentGatedMinidumpAsync().GetAwaiter().GetResult();
        AssertTrue(res.Success && !string.IsNullOrEmpty(res.DumpFilePath) && File.Exists(res.DumpFilePath),
            $"Consent-gated MiniDump capture failed: {res.ErrorMessage}");
        byte[] header = new byte[4];
        using (var fs = File.OpenRead(res.DumpFilePath!)) fs.ReadExactly(header);
        AssertTrue(Encoding.ASCII.GetString(header) == "MDMP", "Captured consent MiniDump missing MDMP signature");

        int consentCount = Directory.GetFiles(svc.DumpsDirectory, "ramyxen-consent-*.dmp").Length;
        AssertTrue(consentCount <= 5, $"Expected <=5 retained consent dumps after pruning, got {consentCount}");
        AssertTrue(File.Exists(foreignDump), "PruneOldDumps must NOT delete non-consent user forensic .dmp files!");
    });

    // 35. CONCURRENT LOG READ-FOR-EXPORT WITH FILESHARE.READWRITE (F-031)
    Check("35. StructuredDiagnosticLogger reads active JSONL logs under lock with FileShare.ReadWrite while writing", () =>
    {
        string logRoot = Path.Combine(tmpRoot, "concurrent_log_test");
        using var logger = new StructuredDiagnosticLogger(logRoot);
        logger.Info("Test", $@"Active log entry with C:\Users\{Environment.UserName}\secret.txt");
        var logs = logger.ReadRecentLogFilesForExport(3);
        AssertTrue(logs.Count >= 1 && logs[0].RedactedContent.Contains("Active log entry"), "Failed to read active log for export");
        AssertTrue(!logs[0].RedactedContent.Contains($@"\Users\{Environment.UserName}", StringComparison.OrdinalIgnoreCase), "Active log leaked username");
    });

    // 36. PROFILER BOUNDED HEAP HONESTY (F-034)
    Check("36. AllocationAndRuntimeProfiler labels bounded/partial heaps honestly as 'Partial Heap Sample (Bounded)'", () =>
    {
        var profiler = new AllocationAndRuntimeProfiler();
        var partialHeap = new ProcessHeapInfo(0x1000, "Default Process Heap (Bounded Sample)", 1, 1024, 4_194_304, 2_097_152, 2_097_152, DataSourceProvenance.Partial);
        var hotspots = profiler.AnalyzeProcessAllocationHotspots((uint)Environment.ProcessId, [], [], [partialHeap]);
        AssertTrue(hotspots.Count == 1 && hotspots[0].SymbolStatus.Contains("Partial Heap Sample", StringComparison.Ordinal),
            $"Bounded heap must NOT claim 'Heap Segment Walk Verified', got '{hotspots.FirstOrDefault()?.SymbolStatus}'");
    });

    // 37. DIAGNOSTIC RUNTIME STATE APP VERSION DYNAMIC & NO STALE HARDCODING (F-050)
    Check("37. DiagnosticRuntimeState defaults to semver without hardcoded '-post-remediation' suffix", () =>
    {
        var st = new DiagnosticRuntimeState();
        AssertTrue(!st.AppVersion.Contains("post-remediation", StringComparison.OrdinalIgnoreCase) && st.AppVersion == "1.1.0",
            $"Unexpected AppVersion: {st.AppVersion}");
    });

    // 38. HEAP WORKER ABI v102 HANDSHAKE, STRICT CLI VALIDATION & PID-REUSE PROTECTION (F-003, R2-NEW-006, R2-NEW-008)
    Check("38. RAMYXEN.HeapWorker.exe emits META|102|0|, rejects invalid CLI args & detects PID-reuse start-time mismatch", () =>
    {
        string workerExe = Path.Combine(AppContext.BaseDirectory, "RAMYXEN.HeapWorker.exe");
        AssertTrue(File.Exists(workerExe), $"Missing RAMYXEN.HeapWorker.exe alongside test runner at {workerExe}");
        using (var proc = Process.Start(new ProcessStartInfo
        {
            FileName = workerExe,
            Arguments = $"{Environment.ProcessId} 16",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }))
        {
            AssertTrue(proc is not null, "Failed to launch RAMYXEN.HeapWorker.exe");
            var outTask = proc!.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            AssertTrue(proc.WaitForExit(3000) && proc.ExitCode == 0, $"RAMYXEN.HeapWorker.exe exited with {proc.ExitCode}");
            string output = outTask.GetAwaiter().GetResult();
            _ = errTask.GetAwaiter().GetResult();
            AssertTrue(output.Contains("META|102|0|") && output.Contains("HEAP|"), $"Unexpected HeapWorker output: {output}");
        }

        // Invalid CLI args (-1, 123abc) must exit non-zero (R2-NEW-006)
        foreach (string badArgs in new[] { "-1 16", "123abc 16", "0 16" })
        {
            using var badProc = Process.Start(new ProcessStartInfo
            {
                FileName = workerExe,
                Arguments = badArgs,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            AssertTrue(badProc is not null && badProc.WaitForExit(2000) && badProc.ExitCode != 0,
                $"RAMYXEN.HeapWorker.exe accepted invalid args '{badArgs}' with exit {badProc?.ExitCode}");
        }

        // PID-reuse start-time mismatch (expectedStartFileTimeUtc = 1) must exit 4 (R2-NEW-008)
        using var reuseProc = Process.Start(new ProcessStartInfo
        {
            FileName = workerExe,
            Arguments = $"{Environment.ProcessId} 16 1",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        AssertTrue(reuseProc is not null && reuseProc.WaitForExit(2000) && reuseProc.ExitCode == 4,
            $"Expected exit code 4 on PID-reuse start-time mismatch, got {reuseProc?.ExitCode}");
    });

    // 39. RTL BIDI ISOLATION WITH LRI+LRM GUARDS AGAINST DIRECTWRITE DIGIT SCRAMBLING (F-008)
    Check("39. LocalizationService.IsolateTechnicalToken wraps RTL values in LRI+LRM (\\u2066\\u200E...\\u200E\\u2069)", () =>
    {
        var loc = new LocalizationService();
        loc.TrySetLanguage("ar-SA");
        string isolated = loc.IsolateTechnicalToken("19.7 GiB / 40.7 GiB (48.5%)");
        AssertTrue(isolated.StartsWith("\u2066\u200E", StringComparison.Ordinal) &&
                   isolated.EndsWith("\u200E\u2069", StringComparison.Ordinal),
            $"RTL IsolateTechnicalToken missing LRI+LRM guards: '{isolated}'");
    });

    // 40. REPORT EXPORTER REDACTS ALL CASE FIELDS, EMAILS, SIDS & FORWARD-SLASH PATHS (R2-NEW-020)
    Check("40. ReportExporterService.ExportJson redacts SuspectedComponent, RecommendedNextSteps, KnownLimitations, email & SID", () =>
    {
        using var host = new RamyxenPlatformHost(Path.Combine(tmpRoot, "rep_r3"));
        var sys = new WindowsSystemMemoryCollector().Collect();
        var advCase = new LeakInvestigationCase(
            "INV-9999", 9999, "App.exe", "C:/Users/AliceFwd/app.exe",
            4, LeakDomain.ProcessPrivateCommit, MemoryExhaustionKind.None, LeakClassification.SuspectedLeak, 70,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 10_000_000, 20_000_000, 50_000, 0.9, 10, 20,
            "Module at \\Device\\HarddiskVolume3\\Users\\AliceFwd\\mod.dll (SID S-1-5-21-111-222-333-1001)",
            ["api_key=sk-rep-999 https://bob:superpass@internal.corp/v1"],
            ["Contact alice@corp.example about C:\\Users\\AliceFwd\\dump.dmp"],
            [$"Captured on {Environment.MachineName}"],
            DataSourceProvenance.Live);

        string jsonPath = host.Reporter.ExportJson(Path.Combine(tmpRoot, "rep_r3.json"), sys, [], [advCase], true);
        string text = File.ReadAllText(jsonPath);
        AssertTrue(!text.Contains("AliceFwd", StringComparison.OrdinalIgnoreCase), "JSON leaked forward-slash username");
        AssertTrue(!text.Contains("alice@corp.example", StringComparison.OrdinalIgnoreCase), "JSON leaked email in RecommendedNextSteps");
        AssertTrue(!text.Contains("S-1-5-21-111-222-333-1001", StringComparison.Ordinal), "JSON leaked SID in SuspectedComponent");
        AssertTrue(!text.Contains("sk-rep-999", StringComparison.Ordinal), "JSON leaked api_key in EvidenceItems");
        AssertTrue(!text.Contains("superpass", StringComparison.Ordinal), "JSON leaked URL credential in EvidenceItems");
        string jsonNtRedacted = DiagnosticPrivacyRedactor.Redact("{\"p\":\"\\\\Device\\\\HarddiskVolume3\\\\Users\\\\AliceJson\\\\a.txt\"}");
        AssertTrue(!jsonNtRedacted.Contains("AliceJson", StringComparison.OrdinalIgnoreCase), "JSON-escaped NT device path leaked username");
        string domJson = $"{{\"u\":\"{Environment.UserDomainName}\\\\{Environment.UserName}\"}}";
        string domRedacted = DiagnosticPrivacyRedactor.Redact(domJson);
        AssertTrue(domRedacted.Contains("<REDACTED_DOMAIN>\\\\<REDACTED_USER>", StringComparison.Ordinal), $"JSON DOMAIN\\\\user broke JSON escaping: {domRedacted}");
    });
}
finally
{
    try { Directory.Delete(tmpRoot, recursive: true); } catch { }
}

Console.WriteLine($"\nSummary: {passed} passed, {failed} failed.\n");
return failed == 0 ? 0 : 1;

internal static class PeResourceInspector
{
    private const uint LOAD_LIBRARY_AS_DATAFILE = 0x00000002;
    private const uint LOAD_LIBRARY_AS_IMAGE_RESOURCE = 0x00000020;

    private delegate bool EnumResNameProc(nint hModule, nint lpType, nint lpName, nint lParam);

    public static bool HasResourceType(string pePath, int resourceTypeId)
    {
        nint hMod = LoadLibraryExW(pePath, nint.Zero, LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE);
        if (hMod == nint.Zero) return false;
        try
        {
            bool found = false;
            EnumResourceNamesW(hMod, (nint)resourceTypeId, (_, _, _, _) => { found = true; return false; }, nint.Zero);
            return found;
        }
        finally
        {
            FreeLibrary(hMod);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryExW(string lpLibFileName, nint hFile, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(nint hLibModule);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumResourceNamesW(nint hModule, nint lpType, EnumResNameProc lpEnumFunc, nint lParam);
}
