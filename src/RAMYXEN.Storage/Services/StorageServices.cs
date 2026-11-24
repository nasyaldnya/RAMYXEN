using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RAMYXEN.Core.Models;

namespace RAMYXEN.Storage.Services;

public sealed class BoundedHistoryRingBuffer<T>
{
    private readonly T[] _buffer;
    private readonly object _lock = new();
    private int _head;
    private int _count;

    public int Capacity => _buffer.Length;
    public int Count { get { lock (_lock) return _count; } }

    public BoundedHistoryRingBuffer(int capacity = 3600)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 4);
        _buffer = new T[capacity];
    }

    public void Add(T item)
    {
        lock (_lock)
        {
            _buffer[_head] = item;
            _head = (_head + 1) % _buffer.Length;
            if (_count < _buffer.Length) _count++;
        }
    }

    public IReadOnlyList<T> GetRecent(int maxItems = int.MaxValue)
    {
        lock (_lock)
        {
            int take = Math.Min(_count, Math.Max(0, maxItems));
            var list = new List<T>(take);
            int start = (_head - take + _buffer.Length) % _buffer.Length;
            for (int i = 0; i < take; i++)
            {
                list.Add(_buffer[(start + i) % _buffer.Length]);
            }
            return list;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_buffer);
            _head = 0;
            _count = 0;
        }
    }
}

public sealed record AppSettingsModel(
    AppThemeMode Theme = AppThemeMode.Dark,
    string Language = "en-US",
    int SamplingIntervalMs = 1000,
    int HistoryRetentionSamples = 1800,
    bool UseBinaryUnits = true,
    bool RequireConsentForStrings = true,
    bool RequireConsentForProcessDumps = true,
    bool RedactPathsOnExport = true,
    string SymbolServerPath = @"srv*C:\Symbols*https://msdl.microsoft.com/download/symbols");

public sealed class SettingsStore
{
    private readonly string _settingsFilePath;
    private readonly object _lock = new();
    public AppSettingsModel Current { get; private set; } = new();

    public SettingsStore(string? storageRoot = null)
    {
        string root = storageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RAMYXEN");
        Directory.CreateDirectory(root);
        _settingsFilePath = Path.Combine(root, "settings.json");
        Load();
    }

    public AppSettingsModel Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    var loaded = JsonSerializer.Deserialize<AppSettingsModel>(File.ReadAllText(_settingsFilePath, Encoding.UTF8));
                    if (loaded != null) Current = loaded;
                }
            }
            catch
            {
                Current = new AppSettingsModel();
            }
            return Current;
        }
    }

    public void Save(AppSettingsModel updated)
    {
        lock (_lock)
        {
            Current = updated;
            string json = JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true });
            string tmpPath = _settingsFilePath + ".tmp";
            File.WriteAllText(tmpPath, json, Encoding.UTF8);
            File.Move(tmpPath, _settingsFilePath, overwrite: true);
        }
    }
}

public sealed class SnapshotVaultService
{
    private readonly string _vaultRoot;
    private readonly object _lock = new();
    private readonly List<MemorySnapshotRecord> _records = [];

    public string VaultDirectory => _vaultRoot;

    public SnapshotVaultService(string? vaultRoot = null)
    {
        _vaultRoot = Path.GetFullPath(vaultRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RAMYXEN", "Snapshots"));
        Directory.CreateDirectory(_vaultRoot);
        LoadAllFromDisk();
    }

    public IReadOnlyList<MemorySnapshotRecord> GetSnapshots()
    {
        lock (_lock) return _records.OrderByDescending(r => r.CreatedUtc).ToList();
    }

    public MemorySnapshotRecord CreateTelemetrySnapshot(
        string name,
        SystemMemoryTelemetry systemState,
        IReadOnlyList<ProcessMemoryInfo> processes,
        IReadOnlyList<string>? tags = null,
        string notes = "")
    {
        string id = $"SNAP-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Random.Shared.Next(100, 999)}";
        string filePath = EnsureInsideVault($"{id}.rmxsnap.json");
        var topProcs = processes.OrderByDescending(p => p.WorkingSetBytes).Take(64).ToList();

        var placeholder = new MemorySnapshotRecord(
            id, name, SnapshotKind.TelemetrySnapshot, DateTimeOffset.UtcNow, null,
            $"Full System Telemetry ({processes.Count} processes)", filePath, 0, "",
            systemState, topProcs, tags ?? ["telemetry", "live"], notes);

        string json = JsonSerializer.Serialize(placeholder, new JsonSerializerOptions { WriteIndented = true });
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var record = placeholder with { FileSizeBytes = bytes.Length, Sha256Hash = hash };
        string finalJson = JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true });
        string tmpFile = filePath + ".tmp";
        File.WriteAllText(tmpFile, finalJson, Encoding.UTF8);
        File.Move(tmpFile, filePath, overwrite: true);

        lock (_lock) _records.Add(record);
        return record;
    }

    public SnapshotComparisonResult CompareSnapshots(MemorySnapshotRecord baseline, MemorySnapshotRecord target)
    {
        long physDelta = (long)target.SystemState.InUsePhysicalBytes - (long)baseline.SystemState.InUsePhysicalBytes;
        long commitDelta = (long)target.SystemState.CommitTotalBytes - (long)baseline.SystemState.CommitTotalBytes;
        long availDelta = (long)target.SystemState.AvailablePhysicalBytes - (long)baseline.SystemState.AvailablePhysicalBytes;
        long npDelta = (long)target.SystemState.KernelNonpagedBytes - (long)baseline.SystemState.KernelNonpagedBytes;

        // Key by PID and verify process name & start time to prevent PID-reuse false comparisons
        var baseMap = baseline.TopProcesses
            .GroupBy(p => p.Pid)
            .ToDictionary(g => g.Key, g => g.First());
        var diffs = new List<ProcessSnapshotDiff>();

        foreach (var tp in target.TopProcesses)
        {
            if (baseMap.TryGetValue(tp.Pid, out var bp) &&
                string.Equals(bp.Name, tp.Name, StringComparison.OrdinalIgnoreCase) &&
                (!bp.StartTimeUtc.HasValue || !tp.StartTimeUtc.HasValue ||
                 Math.Abs((bp.StartTimeUtc.Value - tp.StartTimeUtc.Value).TotalSeconds) <= 1.0))
            {
                long wsD = (long)tp.WorkingSetBytes - (long)bp.WorkingSetBytes;
                long cmD = (long)tp.PrivateCommitBytes - (long)bp.PrivateCommitBytes;
                int hdD = (int)tp.HandleCount - (int)bp.HandleCount;
                string st = cmD > (5 * 1024 * 1024) ? "Growing" : cmD < -(5 * 1024 * 1024) ? "Reclaimed" : "Stable";
                diffs.Add(new ProcessSnapshotDiff(tp.Pid, tp.Name, bp.WorkingSetBytes, tp.WorkingSetBytes, wsD, bp.PrivateCommitBytes, tp.PrivateCommitBytes, cmD, hdD, st));
            }
            else
            {
                diffs.Add(new ProcessSnapshotDiff(tp.Pid, tp.Name, 0, tp.WorkingSetBytes, (long)tp.WorkingSetBytes, 0, tp.PrivateCommitBytes, (long)tp.PrivateCommitBytes, (int)tp.HandleCount, "New Process"));
            }
        }

        return new SnapshotComparisonResult(
            baseline, target, physDelta, commitDelta, availDelta, npDelta,
            diffs.OrderByDescending(d => Math.Abs(d.CommitDeltaBytes)).ToList());
    }

    /// <summary>
    /// Overwrites a snapshot file in-place using a cryptographic random pass followed by a zero pass and
    /// disk flush before unlinking. Note: On wear-leveled NVMe/SATA SSDs or copy-on-write filesystems,
    /// user-mode overwriting cannot guarantee physical NAND block erasure; full-disk encryption (BitLocker) is recommended.
    /// </summary>
    public bool DeleteSnapshotPermanently(string snapshotId)
    {
        if (string.IsNullOrWhiteSpace(snapshotId) ||
            snapshotId.Contains("..", StringComparison.Ordinal) ||
            snapshotId.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new UnauthorizedAccessException("Snapshot ID contains forbidden path traversal characters.");
        }

        lock (_lock)
        {
            var rec = _records.FirstOrDefault(r => r.SnapshotId.Equals(snapshotId, StringComparison.OrdinalIgnoreCase));
            if (rec == null) return false;

            string safePath = EnsureInsideVault(Path.GetFileName(rec.FilePath));
            if (!string.Equals(Path.GetFullPath(rec.FilePath), safePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException("Snapshot file path points outside the authorized Snapshot Vault directory.");
            }

            if (File.Exists(safePath))
            {
                OverwriteAndUnlinkFileInPlace(safePath);
            }

            _records.Remove(rec);
            return true;
        }
    }

    private string EnsureInsideVault(string fileNameOrRelative)
    {
        string fullRoot = _vaultRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullTarget = Path.GetFullPath(Path.Combine(_vaultRoot, fileNameOrRelative));
        if (!fullTarget.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"Path traversal rejected: {fileNameOrRelative}");
        }
        return fullTarget;
    }

    private static void OverwriteAndUnlinkFileInPlace(string fullPath)
    {
        long length = new FileInfo(fullPath).Length;
        if (length > 0)
        {
            byte[] block = new byte[(int)Math.Min(65536L, length)];
            using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                // Pass 1: CSPRNG random overwrite
                long remaining = length;
                fs.Position = 0;
                while (remaining > 0)
                {
                    int chunk = (int)Math.Min(block.Length, remaining);
                    RandomNumberGenerator.Fill(block.AsSpan(0, chunk));
                    fs.Write(block, 0, chunk);
                    remaining -= chunk;
                }
                fs.Flush(flushToDisk: true);

                // Pass 2: 0x00 zero overwrite
                Array.Clear(block);
                remaining = length;
                fs.Position = 0;
                while (remaining > 0)
                {
                    int chunk = (int)Math.Min(block.Length, remaining);
                    fs.Write(block, 0, chunk);
                    remaining -= chunk;
                }
                fs.Flush(flushToDisk: true);
            }
        }
        File.Delete(fullPath);
    }

    private void LoadAllFromDisk()
    {
        lock (_lock)
        {
            _records.Clear();
            if (!Directory.Exists(_vaultRoot)) return;
            foreach (string file in Directory.GetFiles(_vaultRoot, "*.rmxsnap.json"))
            {
                try
                {
                    string safePath = EnsureInsideVault(Path.GetFileName(file));
                    var rec = JsonSerializer.Deserialize<MemorySnapshotRecord>(File.ReadAllText(safePath, Encoding.UTF8));
                    if (rec != null)
                    {
                        _records.Add(rec with { FilePath = safePath });
                    }
                }
                catch { }
            }
        }
    }
}
