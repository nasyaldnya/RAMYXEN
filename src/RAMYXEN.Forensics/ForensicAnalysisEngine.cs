using System.Security.Cryptography;
using System.Text;
using RAMYXEN.Core.Models;
using RAMYXEN.Infrastructure.Windows;

namespace RAMYXEN.Forensics;

public sealed record ForensicSourceDescriptor(
    string SourceName,
    string SourceKind,
    string FilePathOrPid,
    long SizeBytes,
    string IntegritySha256,
    string DetectedArchitecture,
    bool IsSupported,
    DataSourceProvenance Provenance);

public sealed class ForensicAnalysisEngine
{
    private const long MaxSafeImportBytes = 64L * 1024 * 1024 * 1024; // 64 GB safety boundary
    private const uint MINIDUMP_SIGNATURE = 0x504D444D; // 'MDMP' in little-endian
    private const ushort MINIDUMP_VERSION = 0xA793;

    // Standard Windows MiniDump Stream Types (dbghelp.h)
    private const uint ThreadListStream = 3;
    private const uint ModuleListStream = 4;
    private const uint MemoryListStream = 5;
    private const uint SystemInfoStream = 7;
    private const uint Memory64ListStream = 9;
    private const uint MemoryInfoListStream = 16;

    private readonly WindowsProcessCollector _procCollector = new();

    /// <summary>
    /// Performs live forensic triage on an accessible process:
    /// scans up to 4,096 virtual memory regions for private/mapped RWX pages and verifies both
    /// the DOS MZ header (4D 5A) and the e_lfanew -> PE\0\0 (50 45 00 00) NT header signature.
    /// </summary>
    public IReadOnlyList<ForensicFinding> AnalyzeLiveProcess(uint pid, string processName)
    {
        var findings = new List<ForensicFinding>();
        var regions = _procCollector.GetProcessVirtualRegions(pid, 4096);
        int idx = 1;

        var candidateRegions = regions.Where(x =>
            x.IsSuspiciousRwx ||
            (x.State == "MEM_COMMIT" && x.RegionType == "Private" && x.Protection.Contains("PAGE_EXECUTE", StringComparison.OrdinalIgnoreCase)));

        foreach (var r in candidateRegions)
        {
            byte[] header = _procCollector.ReadMemoryRegionHex(pid, r.BaseAddress, 64);
            bool hasConfirmedPe = VerifyInMemoryPeHeader(pid, r.BaseAddress, header);

            if (!r.IsSuspiciousRwx && !hasConfirmedPe)
                continue;

            findings.Add(new ForensicFinding(
                FindingId: $"FOR-{idx++:D2}",
                FindingType: hasConfirmedPe
                    ? "Malfind / Unbacked Executable Region with Verified PE Header (MZ + PE\\0\\0)"
                    : "Private/Mapped Executable-Write Region (RWX / WCX)",
                Category: hasConfirmedPe ? "Potential Suspicion" : "Heuristic Finding",
                Severity: hasConfirmedPe ? "Critical" : "Warning",
                ConfidencePercent: hasConfirmedPe ? 92 : 55,
                TargetProcessOrModule: $"{processName} (PID {pid})",
                Address: r.BaseAddress,
                SizeBytes: r.SizeBytes,
                EvidenceDetail: hasConfirmedPe
                    ? $"Unbacked {r.RegionType} region at {r.BaseAddressHex} ({UnitFormatter.FormatBytes(r.SizeBytes, true)}, {r.Protection}) contains a verified DOS MZ + e_lfanew + PE\\0\\0 portable executable header."
                    : $"{r.RegionType} region at {r.BaseAddressHex} ({UnitFormatter.FormatBytes(r.SizeBytes, true)}) has {r.Protection}. May be legitimate JIT codegen (.NET/Browser) or dynamic code allocation.",
                DetectionMethod: "VirtualQueryEx VAD Protection + MZ/e_lfanew/PE\\0\\0 Header Verification",
                LimitationNote: "Managed runtimes (.NET CLR) and browser engines (V8) legitimately allocate private RWX pages for JIT compilation. Always verify process context.",
                Provenance: DataSourceProvenance.Live));
        }

        if (findings.Count == 0)
        {
            findings.Add(new ForensicFinding(
                FindingId: "FOR-01",
                FindingType: "Clean VAD Protection & PE Header Scan",
                Category: "Observed Data",
                Severity: "Informational",
                ConfidencePercent: 95,
                TargetProcessOrModule: $"{processName} (PID {pid})",
                Address: regions.FirstOrDefault()?.BaseAddress ?? 0,
                SizeBytes: (ulong)regions.Sum(r => (long)r.SizeBytes),
                EvidenceDetail: $"Scanned {regions.Count:N0} virtual memory regions in {processName}. Zero unbacked private RWX regions or reflective PE headers detected.",
                DetectionMethod: "VirtualQueryEx User-Mode Sweep (4,096 Region Ceiling)",
                LimitationNote: "Kernel-mode VAD tree & unlinked EPROCESS detection require an offline physical memory dump or signed kernel driver.",
                Provenance: DataSourceProvenance.Live));
        }

        return findings;
    }

    public bool VerifyInMemoryPeHeader(uint pid, ulong baseAddress, byte[] first64Bytes)
    {
        if (first64Bytes.Length < 64 || first64Bytes[0] != 0x4D || first64Bytes[1] != 0x5A)
            return false;

        int eLfanew = BitConverter.ToInt32(first64Bytes, 0x3C);
        if (eLfanew < 0x40 || eLfanew > 0x1000)
            return false;

        byte[] peSig = _procCollector.ReadMemoryRegionHex(pid, baseAddress + (ulong)eLfanew, 16);
        return peSig.Length >= 4 && peSig[0] == 0x50 && peSig[1] == 0x45 && peSig[2] == 0x00 && peSig[3] == 0x00;
    }

    public static bool VerifyBufferPeHeader(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 64 || buffer[0] != 0x4D || buffer[1] != 0x5A)
            return false;
        int eLfanew = BitConverter.ToInt32(buffer.Slice(0x3C, 4));
        if (eLfanew < 0x40 || eLfanew > buffer.Length - 4)
            return false;
        return buffer[eLfanew] == 0x50 && buffer[eLfanew + 1] == 0x45 && buffer[eLfanew + 2] == 0x00 && buffer[eLfanew + 3] == 0x00;
    }

    /// <summary>
    /// Safely parses an external memory dump (.dmp Windows MiniDump MDMP or .raw image) as untrusted binary data.
    /// Validates the 32-byte MINIDUMP_HEADER, bounds-checks and parses the MINIDUMP_DIRECTORY stream array
    /// (SystemInfoStream, ThreadListStream, ModuleListStream, MemoryListStream, Memory64ListStream, MemoryInfoListStream),
    /// and computes the full-file streaming SHA-256 hash.
    /// </summary>
    public (ForensicSourceDescriptor Source, IReadOnlyList<ForensicFinding> Findings, byte[] PreviewBytes) AnalyzeMemoryDumpFile(string filePath)
    {
        var fi = new FileInfo(filePath);
        if (!fi.Exists || fi.Length <= 0 || fi.Length > MaxSafeImportBytes)
            throw new InvalidOperationException("Dump file is missing, empty, or exceeds safe processing boundary.");

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        // Compute full-file streaming SHA-256 digest
        string fullSha256 = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        fs.Position = 0;

        int sampleSize = (int)Math.Min(fi.Length, 256 * 1024);
        byte[] sample = new byte[sampleSize];
        int totalRead = 0;
        while (totalRead < sampleSize)
        {
            int r = fs.Read(sample, totalRead, sampleSize - totalRead);
            if (r <= 0) break;
            totalRead += r;
        }
        byte[] preview = totalRead > 256 ? sample[..256] : sample[..totalRead];

        bool hasMdmpPrefix = totalRead >= 4 && sample[0] == 'M' && sample[1] == 'D' && sample[2] == 'M' && sample[3] == 'P';
        var findings = new List<ForensicFinding>();

        if (hasMdmpPrefix)
        {
            // Strict 32-byte MINIDUMP_HEADER validation
            if (fi.Length < 32 || totalRead < 32)
            {
                return BuildMalformedMdmpResult(fi, fullSha256, preview, "Truncated MiniDump file (< 32-byte MINIDUMP_HEADER).");
            }

            uint sig = BitConverter.ToUInt32(sample, 0);
            uint ver = BitConverter.ToUInt32(sample, 4);
            uint numStreams = BitConverter.ToUInt32(sample, 8);
            uint dirRva = BitConverter.ToUInt32(sample, 12);
            uint timeDateStamp = BitConverter.ToUInt32(sample, 20);
            ulong flags = BitConverter.ToUInt64(sample, 24);

            ushort lowVer = (ushort)(ver & 0xFFFF);
            long dirEnd = (long)dirRva + ((long)numStreams * 12L);

            if (sig != MINIDUMP_SIGNATURE || lowVer != MINIDUMP_VERSION || numStreams == 0 || numStreams > 256 ||
                dirRva < 32 || dirEnd > fi.Length)
            {
                return BuildMalformedMdmpResult(
                    fi, fullSha256, preview,
                    $"Malformed or adversarial MINIDUMP_HEADER (Version=0x{lowVer:X4}, Streams={numStreams}, DirectoryRva=0x{dirRva:X}, FileSize={fi.Length}).");
            }

            // Read MINIDUMP_DIRECTORY entries safely from FileStream
            byte[] dirBytes = new byte[numStreams * 12];
            fs.Position = dirRva;
            int dirRead = fs.Read(dirBytes, 0, dirBytes.Length);
            if (dirRead < dirBytes.Length)
            {
                return BuildMalformedMdmpResult(fi, fullSha256, preview, "Truncated MINIDUMP_DIRECTORY table.");
            }

            string detectedArch = "Unknown Architecture";
            uint threadCount = 0;
            uint moduleCount = 0;
            ulong memoryRangeCount = 0;
            int validStreams = 0;
            int malformedStreams = 0;
            var streamNames = new List<string>();

            for (int i = 0; i < numStreams; i++)
            {
                int entryOff = i * 12;
                uint streamType = BitConverter.ToUInt32(dirBytes, entryOff);
                uint dataSize = BitConverter.ToUInt32(dirBytes, entryOff + 4);
                uint rva = BitConverter.ToUInt32(dirBytes, entryOff + 8);

                if (streamType == 0) continue; // UnusedStream

                if (rva < 32 || (long)rva + dataSize > fi.Length)
                {
                    malformedStreams++;
                    continue;
                }

                validStreams++;
                switch (streamType)
                {
                    case SystemInfoStream:
                        streamNames.Add("SystemInfoStream");
                        if (dataSize >= 12)
                        {
                            byte[] sysBuf = ReadStreamSlice(fs, rva, (int)Math.Min(dataSize, 32));
                            if (sysBuf.Length >= 12)
                            {
                                ushort archCode = BitConverter.ToUInt16(sysBuf, 0);
                                uint buildNum = BitConverter.ToUInt32(sysBuf, 16 <= sysBuf.Length ? 12 : 8);
                                detectedArch = archCode switch
                                {
                                    9 => $"x64 (AMD64 · Windows NT Build {buildNum})",
                                    12 => $"ARM64 (Windows NT Build {buildNum})",
                                    0 => $"x86 (INTEL · Windows NT Build {buildNum})",
                                    _ => $"ArchCode {archCode} (Windows NT)"
                                };
                            }
                        }
                        break;

                    case ThreadListStream:
                        streamNames.Add("ThreadListStream");
                        if (dataSize >= 4)
                        {
                            byte[] tBuf = ReadStreamSlice(fs, rva, 4);
                            if (tBuf.Length == 4) threadCount = BitConverter.ToUInt32(tBuf, 0);
                        }
                        break;

                    case ModuleListStream:
                        streamNames.Add("ModuleListStream");
                        if (dataSize >= 4)
                        {
                            byte[] mBuf = ReadStreamSlice(fs, rva, 4);
                            if (mBuf.Length == 4) moduleCount = BitConverter.ToUInt32(mBuf, 0);
                        }
                        break;

                    case MemoryListStream:
                        streamNames.Add("MemoryListStream");
                        if (dataSize >= 4)
                        {
                            byte[] memBuf = ReadStreamSlice(fs, rva, 4);
                            if (memBuf.Length == 4) memoryRangeCount += BitConverter.ToUInt32(memBuf, 0);
                        }
                        break;

                    case Memory64ListStream:
                        streamNames.Add("Memory64ListStream");
                        if (dataSize >= 16)
                        {
                            byte[] mem64Buf = ReadStreamSlice(fs, rva, 16);
                            if (mem64Buf.Length == 16) memoryRangeCount += BitConverter.ToUInt64(mem64Buf, 0);
                        }
                        break;

                    case MemoryInfoListStream:
                        streamNames.Add("MemoryInfoListStream");
                        if (dataSize >= 16)
                        {
                            byte[] miBuf = ReadStreamSlice(fs, rva, 16);
                            if (miBuf.Length == 16) memoryRangeCount += BitConverter.ToUInt64(miBuf, 8);
                        }
                        break;

                    default:
                        streamNames.Add($"Stream#{streamType}");
                        break;
                }
            }

            if (malformedStreams > 0 || validStreams == 0)
            {
                return BuildMalformedMdmpResult(
                    fi, fullSha256, preview,
                    $"MiniDump directory contains {malformedStreams} out-of-bounds stream RVA(s) and {validStreams} valid stream(s).");
            }

            var src = new ForensicSourceDescriptor(
                SourceName: fi.Name,
                SourceKind: "Windows MiniDump (MDMP Directory Verified)",
                FilePathOrPid: fi.FullName,
                SizeBytes: fi.Length,
                IntegritySha256: fullSha256,
                DetectedArchitecture: detectedArch,
                IsSupported: true,
                Provenance: DataSourceProvenance.DumpRequired);

            findings.Add(new ForensicFinding(
                FindingId: "DUMP-01",
                FindingType: "Verified Windows MiniDump Header & Stream Directory",
                Category: "Observed Data",
                Severity: "Informational",
                ConfidencePercent: 99,
                TargetProcessOrModule: fi.Name,
                Address: dirRva,
                SizeBytes: (ulong)fi.Length,
                EvidenceDetail: $"Parsed {validStreams} MDMP streams ({string.Join(", ", streamNames.Take(6))}) · Arch: {detectedArch} · Threads: {threadCount} · Modules: {moduleCount} · Memory Ranges: {memoryRangeCount:N0} · Flags: 0x{flags:X} · Timestamp: {timeDateStamp}.",
                DetectionMethod: "MINIDUMP_HEADER & MINIDUMP_DIRECTORY Stream Parser",
                LimitationNote: "Offline dump analysis parses stream headers in read-only mode without modifying host memory.",
                Provenance: DataSourceProvenance.DumpRequired));

            AppendSignatureFindings(sample.AsSpan(0, totalRead), fi.Name, findings);
            return (src, findings, preview);
        }

        // Raw / Binary Memory Image branch
        var rawSrc = new ForensicSourceDescriptor(
            SourceName: fi.Name,
            SourceKind: "Raw / Binary Memory Image",
            FilePathOrPid: fi.FullName,
            SizeBytes: fi.Length,
            IntegritySha256: fullSha256,
            DetectedArchitecture: "Raw Binary Slice (Architecture Unspecified)",
            IsSupported: true,
            Provenance: DataSourceProvenance.DumpRequired);

        findings.Add(new ForensicFinding(
            FindingId: "DUMP-01",
            FindingType: "Raw Binary Memory Slice Loaded",
            Category: "Observed Data",
            Severity: "Informational",
            ConfidencePercent: 90,
            TargetProcessOrModule: fi.Name,
            Address: 0UL,
            SizeBytes: (ulong)fi.Length,
            EvidenceDetail: $"Parsed {UnitFormatter.FormatBytes((ulong)fi.Length, true)} raw binary image (SHA-256: {fullSha256[..16]}…).",
            DetectionMethod: "Isolated Read-Only Binary Stream Inspector",
            LimitationNote: "Full physical memory images require symbol/CR3 page-table reconstruction for EPROCESS walking.",
            Provenance: DataSourceProvenance.DumpRequired));

        AppendSignatureFindings(sample.AsSpan(0, totalRead), fi.Name, findings);
        return (rawSrc, findings, preview);
    }

    private static (ForensicSourceDescriptor Source, IReadOnlyList<ForensicFinding> Findings, byte[] PreviewBytes) BuildMalformedMdmpResult(
        FileInfo fi,
        string fullSha256,
        byte[] preview,
        string reason)
    {
        var src = new ForensicSourceDescriptor(
            SourceName: fi.Name,
            SourceKind: "Malformed / Corrupted Windows MiniDump (Rejected)",
            FilePathOrPid: fi.FullName,
            SizeBytes: fi.Length,
            IntegritySha256: fullSha256,
            DetectedArchitecture: "Invalid / Truncated Header",
            IsSupported: false,
            Provenance: DataSourceProvenance.Unavailable);

        var findings = new List<ForensicFinding>
        {
            new(
                FindingId: "DUMP-ERR-01",
                FindingType: "Malformed or Truncated MiniDump File Rejected",
                Category: "Confirmed Evidence",
                Severity: "Critical",
                ConfidencePercent: 100,
                TargetProcessOrModule: fi.Name,
                Address: 0UL,
                SizeBytes: (ulong)fi.Length,
                EvidenceDetail: reason,
                DetectionMethod: "MINIDUMP_HEADER & Stream RVA Bounds Validator",
                LimitationNote: "Adversarial or truncated dump files are rejected before stream dereferencing to prevent out-of-bounds reads.",
                Provenance: DataSourceProvenance.Unavailable)
        };
        return (src, findings, preview);
    }

    private static byte[] ReadStreamSlice(FileStream fs, uint rva, int length)
    {
        if (length <= 0) return [];
        byte[] buf = new byte[length];
        fs.Position = rva;
        int read = fs.Read(buf, 0, length);
        return read == length ? buf : buf[..Math.Max(0, read)];
    }

    private static void AppendSignatureFindings(ReadOnlySpan<byte> sample, string fileName, List<ForensicFinding> findings)
    {
        byte[] dosStubPattern = Encoding.ASCII.GetBytes("This program cannot be run in DOS mode");
        int offset = sample.IndexOf(dosStubPattern);
        if (offset >= 0)
        {
            findings.Add(new ForensicFinding(
                FindingId: "DUMP-02",
                FindingType: "Embedded PE DOS Stub Signature",
                Category: "Heuristic Finding",
                Severity: "Warning",
                ConfidencePercent: 80,
                TargetProcessOrModule: fileName,
                Address: (ulong)offset,
                SizeBytes: (ulong)dosStubPattern.Length,
                EvidenceDetail: $"Detected PE DOS stub ('This program cannot be run in DOS mode') at file offset 0x{offset:X}.",
                DetectionMethod: "Binary Signature Pattern Matcher",
                LimitationNote: "An embedded PE stub indicates an executable or DLL image is present in the dump; it is not standalone proof of malware.",
                Provenance: DataSourceProvenance.DumpRequired));
        }
    }
}
