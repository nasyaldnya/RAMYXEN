using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using RAMYXEN.App.Icons;
using RAMYXEN.Core.Models;
using RAMYXEN.Infrastructure.Windows;

namespace RAMYXEN.App.Services;

/// <summary>
/// UI-thread LRU cache mapping extracted BGRA executable icons to WinUI 3 WriteableBitmap surfaces (F-038, F-045).
/// Renders official Windows application icons (chrome.exe, explorer.exe, etc.) asynchronously at DPI-appropriate
/// raster resolutions (32/48/64 px) with crisp vector fallbacks while loading or for restricted/headless processes.
/// </summary>
public sealed class WinUiProcessIconCache
{
    private const int MaxCachedBitmaps = 256;
    private readonly WindowsShellIconService _shellIcons;
    private readonly DispatcherQueue _dispatcher;
    private readonly Dictionary<string, LinkedListNode<(string Key, WriteableBitmap Bitmap)>> _bitmapMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<(string Key, WriteableBitmap Bitmap)> _bitmapLru = new();

    public int CachedBitmapCount => _bitmapMap.Count;

    public WinUiProcessIconCache(WindowsShellIconService shellIcons, DispatcherQueue dispatcher)
    {
        _shellIcons = shellIcons;
        _dispatcher = dispatcher;
    }

    public static int SelectExtractionSizePx(double rasterizationScale)
    {
        if (rasterizationScale >= 1.75) return 64;
        if (rasterizationScale >= 1.25) return 48;
        return 32;
    }

    public FrameworkElement CreateProcessIconElement(
        ProcessMemoryInfo proc,
        double displaySizeDip,
        Brush fallbackForeground,
        double rasterizationScale = 1.0)
    {
        int extractPx = SelectExtractionSizePx(rasterizationScale);
        var host = new Grid
        {
            Width = displaySizeDip,
            Height = displaySizeDip,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        // Fast-path: pure O(1) memory lookup with ZERO UI-thread disk stat() calls (F-007)
        // Short-circuit BOTH positive AND negative cache hits so negative hits do not spawn redundant async work (F-038)
        if (_shellIcons.TryGetCached(proc.ExecutablePath, extractPx, out var cached))
        {
            if (cached is not null)
            {
                host.Children.Add(MakeBitmapImage(cached, displaySizeDip));
            }
            else
            {
                var negFallback = FluentIconCatalog.FallbackGlyphForProcess(proc.Category, proc.IsProtectedOrRestricted);
                host.Children.Add(FluentIconCatalog.Create(negFallback, Math.Max(14, displaySizeDip - 2), fallbackForeground));
            }
            return host;
        }

        // Immediate crisp Fluent vector fallback while official Windows Shell icon extracts asynchronously
        var fallbackGlyph = FluentIconCatalog.FallbackGlyphForProcess(proc.Category, proc.IsProtectedOrRestricted);
        host.Children.Add(FluentIconCatalog.Create(fallbackGlyph, Math.Max(14, displaySizeDip - 2), fallbackForeground));

        if (!string.IsNullOrWhiteSpace(proc.ExecutablePath) && Path.IsPathRooted(proc.ExecutablePath))
        {
            string exePath = proc.ExecutablePath;
            bool hostUnloaded = false;
            host.Unloaded += (_, _) => hostUnloaded = true;

            _ = _shellIcons.ResolveAsync(exePath, extractPx, CancellationToken.None).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _ = t.Exception;
                    return;
                }
                if (!t.IsCompletedSuccessfully || t.Result is null || hostUnloaded)
                    return;

                var resolved = t.Result;
                _dispatcher.TryEnqueue(() =>
                {
                    if (hostUnloaded)
                        return;
                    try
                    {
                        // Avoid mutating detached containers that were already unloaded (F-038)
                        host.Children.Clear();
                        host.Children.Add(MakeBitmapImage(resolved, displaySizeDip));
                    }
                    catch
                    {
                    }
                });
            }, TaskScheduler.Default);
        }

        return host;
    }

    private Image MakeBitmapImage(ResolvedExecutableIcon icon, double displaySizeDip)
    {
        WriteableBitmap wb;
        if (_bitmapMap.TryGetValue(icon.CacheKey, out var node))
        {
            _bitmapLru.Remove(node);
            _bitmapLru.AddFirst(node);
            wb = node.Value.Bitmap;
        }
        else
        {
            wb = new WriteableBitmap(icon.Width, icon.Height);
            using (Stream s = wb.PixelBuffer.AsStream())
            {
                s.Write(icon.BgraPixels, 0, icon.BgraPixels.Length);
            }
            wb.Invalidate();

            var newNode = new LinkedListNode<(string, WriteableBitmap)>((icon.CacheKey, wb));
            _bitmapLru.AddFirst(newNode);
            _bitmapMap[icon.CacheKey] = newNode;

            // True LRU eviction (evicts oldest single entry instead of clearing entire cache — F-038)
            while (_bitmapMap.Count > MaxCachedBitmaps && _bitmapLru.Last is not null)
            {
                var tail = _bitmapLru.Last;
                _bitmapLru.RemoveLast();
                _bitmapMap.Remove(tail.Value.Key);
            }
        }

        return new Image
        {
            Source = wb,
            Width = displaySizeDip,
            Height = displaySizeDip,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
    }

    public void Clear()
    {
        _bitmapMap.Clear();
        _bitmapLru.Clear();
    }
}
