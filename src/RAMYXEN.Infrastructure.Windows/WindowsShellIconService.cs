using System.Collections.Concurrent;
using RAMYXEN.Diagnostics;

namespace RAMYXEN.Infrastructure.Windows;

/// <summary>
/// Explicit provenance of an extracted executable icon (F-023).
/// </summary>
public enum ExecutableIconProvenance
{
    None = 0,
    PeResource = 1,
    ShellFile = 2,
    GenericFileType = 3
}

/// <summary>
/// Represents a 32-bit BGRA executable icon extracted from Windows PE resources or Shell
/// with zero GDI/HICON handles held open after extraction.
/// </summary>
public sealed record ResolvedExecutableIcon(
    string CacheKey,
    int Width,
    int Height,
    byte[] BgraPixels,
    bool IsOfficialAppIcon,
    ExecutableIconProvenance Provenance = ExecutableIconProvenance.PeResource);

/// <summary>
/// Asynchronous, bounded, deduplicated, LRU-cached Windows Shell executable icon resolver.
/// Zero disk I/O occurs on the caller/UI thread in TryGetCached or BuildCacheKey (F-007);
/// all filesystem checks and native extractions execute exclusively on background threads.
/// </summary>
public sealed class WindowsShellIconService : IDisposable
{
    private const int DefaultCapacity = 256;
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromSeconds(30);

    private readonly int _capacity;
    private readonly SemaphoreSlim _concurrencyGate = new(3, 3);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _lruLock = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _cacheMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<CacheEntry> _lruList = new();
    private readonly ConcurrentDictionary<string, Task<ResolvedExecutableIcon?>> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly StructuredDiagnosticLogger? _logger;
    private long _cacheHits;
    private long _cacheMisses;
    private long _extractedCount;
    private volatile bool _disposed;

    private sealed record CacheEntry(string Key, ResolvedExecutableIcon? Icon, DateTimeOffset ExpiresAtUtc);

    public long CacheHits => Interlocked.Read(ref _cacheHits);
    public long CacheMisses => Interlocked.Read(ref _cacheMisses);
    public long ExtractedCount => Interlocked.Read(ref _extractedCount);
    public int CachedEntryCount { get { lock (_lruLock) return _cacheMap.Count; } }

    public WindowsShellIconService(StructuredDiagnosticLogger? logger = null, int capacity = DefaultCapacity)
    {
        _logger = logger;
        _capacity = Math.Clamp(capacity, 32, 1024);
    }

    /// <summary>
    /// Pure O(1) in-memory cache key builder with ZERO filesystem stat() calls (F-007).
    /// </summary>
    public static string? BuildCacheKey(string? executablePath, int sizePx)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return null;

        string trimmed = executablePath.Trim();
        if (!Path.IsPathRooted(trimmed))
            return null;

        int clampedSize = Math.Clamp(sizePx, 16, 64);
        return $"{trimmed.ToLowerInvariant()}|{clampedSize}";
    }

    public bool TryGetCached(string? executablePath, int sizePx, out ResolvedExecutableIcon? icon)
    {
        icon = null;
        if (_disposed || string.IsNullOrWhiteSpace(executablePath))
            return false;

        string? key = BuildCacheKey(executablePath, sizePx);
        if (key is null)
            return false;

        var now = DateTimeOffset.UtcNow;
        lock (_lruLock)
        {
            if (_cacheMap.TryGetValue(key, out var node))
            {
                if (node.Value.Icon is null && now >= node.Value.ExpiresAtUtc)
                {
                    _lruList.Remove(node);
                    _cacheMap.Remove(key);
                    return false;
                }

                _lruList.Remove(node);
                _lruList.AddFirst(node);
                icon = node.Value.Icon;
                Interlocked.Increment(ref _cacheHits);
                return true;
            }
        }
        return false;
    }

    public async Task<ResolvedExecutableIcon?> ResolveAsync(string? executablePath, int sizePx = 32, CancellationToken ct = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(executablePath))
            return null;

        string? key = BuildCacheKey(executablePath, sizePx);
        if (key is null)
            return null;

        var now = DateTimeOffset.UtcNow;
        lock (_lruLock)
        {
            if (_cacheMap.TryGetValue(key, out var node))
            {
                if (node.Value.Icon is null && now >= node.Value.ExpiresAtUtc)
                {
                    _lruList.Remove(node);
                    _cacheMap.Remove(key);
                }
                else
                {
                    _lruList.Remove(node);
                    _lruList.AddFirst(node);
                    Interlocked.Increment(ref _cacheHits);
                    return node.Value.Icon;
                }
            }
        }

        Interlocked.Increment(ref _cacheMisses);
        string normalizedPath = executablePath.Trim();
        int clampedSize = Math.Clamp(sizePx, 16, 64);

        // Decouple shared in-flight extraction from individual caller cancellation (F-038)
        CancellationToken disposeToken;
        try
        {
            if (_disposed) return null;
            disposeToken = _disposeCts.Token;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }

        Task<ResolvedExecutableIcon?> sharedTask = _inFlight.GetOrAdd(
            key,
            _ => ExtractAndCacheAsync(key, normalizedPath, clampedSize, disposeToken));

        if (!ct.CanBeCanceled)
            return await sharedTask.ConfigureAwait(false);

        try
        {
            return await sharedTask.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<ResolvedExecutableIcon?> ExtractAndCacheAsync(
        string cacheKey,
        string executablePath,
        int sizePx,
        CancellationToken serviceCt)
    {
        bool acquired = false;
        try
        {
            await _concurrencyGate.WaitAsync(serviceCt).ConfigureAwait(false);
            acquired = true;

            if (_disposed || serviceCt.IsCancellationRequested)
                return null;

            ResolvedExecutableIcon? extracted = await Task.Run(() =>
            {
                if (!File.Exists(executablePath))
                    return null;

                int neededBytes = sizePx * sizePx * 4;
                byte[] buffer = new byte[neededBytes];
                int rc = NativeMethods.Ramyxen_ExtractExecutableIconBgra(
                    executablePath,
                    sizePx,
                    buffer,
                    neededBytes,
                    out int outW,
                    out int outH,
                    out int outProv);

                if (rc != 0 || outW != sizePx || outH != sizePx)
                    return null;

                var prov = Enum.IsDefined(typeof(ExecutableIconProvenance), outProv)
                    ? (ExecutableIconProvenance)outProv
                    : ExecutableIconProvenance.None;

                // Never claim generic file-type icons as official application icons (F-023)
                if (prov is ExecutableIconProvenance.None or ExecutableIconProvenance.GenericFileType)
                    return null;

                bool hasVisiblePixel = false;
                for (int i = 3; i < buffer.Length; i += 4)
                {
                    if (buffer[i] != 0)
                    {
                        hasVisiblePixel = true;
                        break;
                    }
                }
                if (!hasVisiblePixel)
                    return null;

                bool isOfficial = prov is ExecutableIconProvenance.PeResource or ExecutableIconProvenance.ShellFile;
                return new ResolvedExecutableIcon(cacheKey, sizePx, sizePx, buffer, isOfficial, prov);
            }, serviceCt).ConfigureAwait(false);

            if (extracted is not null)
                Interlocked.Increment(ref _extractedCount);

            PutInCache(cacheKey, extracted);
            return extracted;
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
            _logger?.Warning("ShellIcons", $"Safe fallback while extracting icon for {Path.GetFileName(executablePath)}: {ex.Message}");
            if (!_disposed) PutInCache(cacheKey, null);
            return null;
        }
        finally
        {
            if (acquired)
            {
                try { _concurrencyGate.Release(); } catch { }
            }
            _inFlight.TryRemove(cacheKey, out _);
        }
    }

    private void PutInCache(string key, ResolvedExecutableIcon? icon)
    {
        if (_disposed) return;
        var expiresAt = icon is null
            ? DateTimeOffset.UtcNow + NegativeCacheTtl
            : DateTimeOffset.MaxValue;

        lock (_lruLock)
        {
            if (_cacheMap.TryGetValue(key, out var existing))
            {
                _lruList.Remove(existing);
                existing.Value = new CacheEntry(key, icon, expiresAt);
                _lruList.AddFirst(existing);
                return;
            }

            var node = new LinkedListNode<CacheEntry>(new CacheEntry(key, icon, expiresAt));
            _lruList.AddFirst(node);
            _cacheMap[key] = node;

            while (_cacheMap.Count > _capacity && _lruList.Last is not null)
            {
                var tail = _lruList.Last;
                _lruList.RemoveLast();
                _cacheMap.Remove(tail.Value.Key);
            }
        }
    }

    public void ClearCache()
    {
        lock (_lruLock)
        {
            _cacheMap.Clear();
            _lruList.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _disposeCts.Cancel(); } catch { }
        try { _disposeCts.Dispose(); } catch { }
        ClearCache();
        _concurrencyGate.Dispose();
    }
}
