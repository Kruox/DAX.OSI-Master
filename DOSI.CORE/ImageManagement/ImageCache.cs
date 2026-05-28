using System;
using System.Collections.Concurrent;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace DOSI.CORE.ImageManagement;

/// <summary>
/// Process-wide cache + helpers for decoding arbitrary image files into
/// <see cref="Bitmap"/>s without forcing every caller to write the same
/// "open the file off the UI thread, downscale, dispose the old bitmap,
/// hand the result back to the UI thread" boilerplate. Mirrors the
/// optimization recipe used by
/// <see cref="DOSI.CORE.WallpaperManagement.WallpaperManager"/>:
/// <list type="bullet">
///   <item><description>Decoding is performed on a background thread so
///   the UI never freezes while a large photo is being parsed.</description></item>
///   <item><description>Sources larger than a target dimension are
///   downscaled exactly once at decode time using
///   <see cref="Bitmap.DecodeToWidth"/> /
///   <see cref="Bitmap.DecodeToHeight"/>, which lets the platform decoder
///   skip pixels it would otherwise hold in memory just to throw away.</description></item>
///   <item><description>Per-(path, target-dimension) thumbnails are cached
///   so repeated previews of the same file (e.g. clicking through a
///   folder of photos in DOSIFileExplorer's details panel) are
///   free.</description></item>
/// </list>
/// </summary>
public static class ImageCache
{
    /// <summary>
    /// Long-edge size in pixels for "thumbnail" previews (DOSIFileExplorer
    /// details panel, future picker grids, etc). A 320 px JPEG occupies
    /// ~400 KB of resident GPU texture; a 6000 px phone photo occupies
    /// ~140 MB. Capping at 320 px makes a folder of N image previews
    /// effectively free instead of catastrophic for the compositor.
    /// </summary>
    public const int ThumbnailMaxDimension = 320;

    /// <summary>
    /// Long-edge size in pixels for "view" decodes used by the image
    /// viewer. 3840 (4K width) matches the wallpaper cap and is the
    /// "fidelity sweet spot" for full-window viewing on every monitor up
    /// to 4K; bigger sources are downsampled once at load time so per-
    /// frame compositing only ever blits an already-correctly-sized
    /// bitmap.
    /// </summary>
    public const int ViewMaxDimension = 3840;

    // Per-(path, target-dimension) cache. Key is "<absolutePath>|<dim>" so
    // a thumbnail and a view of the same file coexist without
    // overwriting each other. Concurrent so the dispatcher and any number
    // of background decoders can read/write without an external lock.
    private static readonly ConcurrentDictionary<string, Bitmap> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static string KeyFor(string absolutePath, int maxDim) =>
        absolutePath + "|" + maxDim;

    /// <summary>
    /// Synchronously returns a cached bitmap for <paramref name="path"/>
    /// downscaled so its long edge is at most <paramref name="maxDimension"/>
    /// pixels, decoding it if necessary. Safe to call from the UI thread
    /// for files we KNOW are small (icons, generated PNGs); for arbitrary
    /// user files prefer <see cref="LoadAsync"/> so the dispatcher isn't
    /// blocked on a multi-megabyte JPEG decode.
    /// </summary>
    public static Bitmap? Load(string path, int maxDimension)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (maxDimension <= 0) maxDimension = ViewMaxDimension;

        var key = KeyFor(path, maxDimension);
        if (_cache.TryGetValue(key, out var cached)) return cached;

        Bitmap? produced = null;
        try
        {
            var winner = _cache.GetOrAdd(key, _ =>
            {
                produced = DecodeFromDisk(path, maxDimension);
                return produced!;
            });

            // If GetOrAdd accepted another thread's bitmap as the winner
            // (rare but possible under contention), dispose ours.
            if (produced != null && !ReferenceEquals(produced, winner))
                produced.Dispose();

            return winner;
        }
        catch
        {
            produced?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Asynchronously decodes the image at <paramref name="path"/> on a
    /// worker thread and posts the resulting bitmap to
    /// <paramref name="onLoaded"/> on the UI thread. The callback receives
    /// <c>null</c> on any failure. The decoded bitmap is cached so a
    /// subsequent <see cref="Load"/> / <see cref="LoadAsync"/> for the
    /// same path + dimension is free.
    /// </summary>
    public static void LoadAsync(string path, int maxDimension, Action<Bitmap?> onLoaded)
    {
        if (string.IsNullOrWhiteSpace(path) || onLoaded == null)
        {
            onLoaded?.Invoke(null);
            return;
        }
        if (maxDimension <= 0) maxDimension = ViewMaxDimension;

        // Fast path: already cached - skip the Task hop.
        var key = KeyFor(path, maxDimension);
        if (_cache.TryGetValue(key, out var cached))
        {
            onLoaded(cached);
            return;
        }

        System.Threading.Tasks.Task.Run(() =>
        {
            Bitmap? bmp = null;
            try { bmp = Load(path, maxDimension); }
            catch { bmp = null; }
            var produced = bmp;
            Dispatcher.UIThread.Post(() => onLoaded(produced));
        });
    }

    /// <summary>
    /// Best-effort: kicks a background decode for the given path so a
    /// later <see cref="Load"/> / <see cref="LoadAsync"/> hits the warm
    /// cache. Useful for "I'm about to show this image" hints (e.g.
    /// pre-decoding the previous / next sibling in a folder).
    /// </summary>
    public static void Prewarm(string path, int maxDimension)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (_cache.ContainsKey(KeyFor(path, maxDimension))) return;
        System.Threading.Tasks.Task.Run(() =>
        {
            try { Load(path, maxDimension); } catch { /* best-effort */ }
        });
    }

    /// <summary>
    /// Drops any cached bitmap for the given path (every dimension).
    /// Call when the file on disk changes so the next load picks up the
    /// new pixels instead of returning a stale decode.
    /// </summary>
    public static void Invalidate(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        foreach (var k in _cache.Keys)
        {
            // Key format is "<path>|<dim>"; compare prefix.
            var sep = k.LastIndexOf('|');
            var keyPath = sep > 0 ? k.Substring(0, sep) : k;
            if (string.Equals(keyPath, path, StringComparison.OrdinalIgnoreCase))
            {
                _cache.TryRemove(k, out _);
            }
        }
    }

    /// <summary>
    /// Reads the file from disk and produces a downscaled bitmap whose
    /// long edge does not exceed <paramref name="maxDim"/> pixels. Uses
    /// Avalonia's platform decoder via <see cref="Bitmap.DecodeToWidth"/>
    /// / <see cref="Bitmap.DecodeToHeight"/> so oversized sources never
    /// fully materialize in memory.
    /// </summary>
    private static Bitmap? DecodeFromDisk(string path, int maxDim)
    {
        if (!File.Exists(path)) return null;

        // First decode peek: read the header without downscaling so we
        // know the source dimensions. DecodeToWidth would also work as a
        // single-shot but we want to NOT downscale when the source is
        // already smaller than the cap (avoids upscaling small icons,
        // which DecodeToWidth would happily do).
        //
        // We have to open the stream twice for that: once for the size
        // probe, once for the (possibly downscaled) decode. File handles
        // are cheap; the data goes through the OS page cache so the
        // second open is effectively free.
        int srcW, srcH;
        try
        {
            using var probe = File.OpenRead(path);
            using var head = new Bitmap(probe);
            srcW = head.PixelSize.Width;
            srcH = head.PixelSize.Height;

            // Source already fits inside the cap - reuse the probe bitmap
            // so we don't pay the decode cost twice for small files.
            if (srcW <= maxDim && srcH <= maxDim)
            {
                // Construct a fresh Bitmap so the caller owns it
                // independently of the using-scoped probe disposal.
                using var src = File.OpenRead(path);
                return new Bitmap(src);
            }
        }
        catch
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            // Choose the axis that drives the long edge so we never
            // exceed the cap on either axis.
            if (srcW >= srcH)
            {
                return Bitmap.DecodeToWidth(stream, maxDim, BitmapInterpolationMode.HighQuality);
            }
            return Bitmap.DecodeToHeight(stream, maxDim, BitmapInterpolationMode.HighQuality);
        }
        catch
        {
            return null;
        }
    }
}
