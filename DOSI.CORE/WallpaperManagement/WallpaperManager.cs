using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DOSI.CORE.UserManagement;
using SkiaSharp;

namespace DOSI.CORE.WallpaperManagement;

/// <summary>
/// How the desktop wallpaper bitmap is sized into the screen rectangle.
/// Mirrors the most useful subset of <c>Avalonia.Media.Stretch</c> plus a
/// dedicated <see cref="Tile"/> mode so callers don't have to juggle two
/// orthogonal properties (Stretch + ImageBrush.TileMode). Persisted per
/// user under <c>UserManager.WallpaperFitPreferenceKey</c>.
/// </summary>
public enum WallpaperFitMode
{
    /// <summary>UniformToFill - keeps aspect, may crop edges to fill.</summary>
    Fill,
    /// <summary>Uniform - keeps aspect, leaves bars where the photo doesn't reach.</summary>
    Fit,
    /// <summary>Stretch in both axes - distorts aspect to exactly fill.</summary>
    Stretch,
    /// <summary>Centred at native size, no scaling.</summary>
    Center,
    /// <summary>Repeat at native size to cover the whole desktop.</summary>
    Tile
}

/// <summary>
/// Describes a single pre-shipped wallpaper available to DOSI users.
/// </summary>
public sealed class DOSIWallpaper
{
    /// <summary>Stable, file-system-safe key persisted to user preferences.</summary>
    public required string Key { get; init; }

    /// <summary>Friendly name shown in pickers.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The <c>avares://</c> URI of the full-size image.</summary>
    public required Uri AssetUri { get; init; }
}

/// <summary>
/// Centralized manager for the DOSI virtual-OS wallpaper. Wallpapers are
/// shipped as <c>AvaloniaResource</c>s under
/// <c>DOSI.CORE/Resources/Wallpapers/</c> and selected via
/// <see cref="SetWallpaper"/>. When no wallpaper is active the system falls
/// back to <see cref="AccentManagement.AccentManager.DesktopBackgroundBrush"/>.
/// </summary>
public sealed class WallpaperManager
{
    private static WallpaperManager? _instance;
    public static WallpaperManager Instance => _instance ??= new WallpaperManager();

    /// <summary>The preferences key under which a user's preferred wallpaper is stored.</summary>
    public const string WallpaperPreferenceKey = "wallpaper";

    /// <summary>Sentinel preference value meaning "no wallpaper, use accent only".</summary>
    public const string AccentOnlyKey = "__accent__";

    private const string AssemblyName = "DOSI.CORE";
    private const string ResourceFolder = "Resources/Wallpapers";

    private readonly List<DOSIWallpaper> _wallpapers = new()
    {
        new DOSIWallpaper
        {
            Key = "winter-water",
            DisplayName = "Winter Water",
            AssetUri = new Uri($"avares://{AssemblyName}/{ResourceFolder}/Amazing Winter Water.jpeg")
        }
    };

    private readonly Dictionary<string, Bitmap> _blurredBitmapCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Bitmap> _sharpBitmapCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Serializes <see cref="LoadBitmap"/> calls so the background prewarm
    /// thread (kicked off in the constructor) and the UI thread can both
    /// safely populate / read the bitmap caches. Without this a UI-thread
    /// call that arrives while prewarm is mid-bake could double-bake the
    /// same wallpaper and leak a <see cref="Bitmap"/>. The lock is held for
    /// the duration of one bake at most; once prewarm has finished every
    /// subsequent UI call is a sub-microsecond cache hit.
    /// </summary>
    private readonly object _loadLock = new();

    private WallpaperManager()
    {
        // React to sign-in / sign-out so the desktop wallpaper follows the
        // currently signed-in user automatically.
        UserManager.CurrentUserChanged += (_, user) => LoadFromUser(user);

        // Pre-warm both bitmap variants on a worker thread so the first
        // SetWallpaper / LoadBitmap call from the UI thread (typically
        // LoginScreen.SelectUser when the user clicks their tile, or
        // DesktopScreen toggling its wallpaper-blur preference) doesn't
        // pay the PNG-decode + downscale + Skia blur-bake cost
        // synchronously - which was visible as a noticeable delay between
        // clicking a user tile and the wallpaper / accent transition
        // actually starting. By the time the user is interactive every
        // shipped wallpaper is decoded, downscaled, and (for the blurred
        // variant) blur-baked, sitting in the appropriate cache ready for
        // an instant cross-fade.
        System.Threading.Tasks.Task.Run(PrewarmBitmapCache);
    }

    /// <summary>
    /// Best-effort background pass that warms BOTH the blurred and sharp
    /// variant of every shipped wallpaper. Failures are swallowed - if a
    /// particular wallpaper can't be decoded ahead of time the UI-thread
    /// call later will hit the same code path and surface (or also
    /// swallow) the error at that point.
    /// </summary>
    private void PrewarmBitmapCache()
    {
        foreach (var wallpaper in _wallpapers)
        {
            try { LoadBitmap(wallpaper.Key, blurred: true); }
            catch { /* prewarm is best-effort */ }
            try { LoadBitmap(wallpaper.Key, blurred: false); }
            catch { /* prewarm is best-effort */ }
        }
    }

    /// <summary>Raised whenever <see cref="CurrentWallpaperKey"/> changes.</summary>
    public event EventHandler? WallpaperChanged;

    /// <summary>
    /// Raised whenever a new entry is appended to <see cref="AvailableWallpapers"/>
    /// (e.g. the user picked a custom image from disk). Settings UIs subscribe
    /// to this so they can re-render the wallpaper grid live.
    /// </summary>
    public event EventHandler? WallpapersChanged;

    /// <summary>
    /// The key of the currently active wallpaper, <see cref="AccentOnlyKey"/>
    /// for "accent only", or <c>null</c> if nothing has been selected yet
    /// (callers should treat <c>null</c> the same as accent-only).
    /// </summary>
    public string? CurrentWallpaperKey { get; private set; }

    /// <summary>
    /// How the active wallpaper is sized into the screen. Defaults to
    /// <see cref="WallpaperFitMode.Fill"/> (UniformToFill) - the previous
    /// behaviour before the fit-mode preference existed.
    /// </summary>
    public WallpaperFitMode CurrentFitMode { get; private set; } = WallpaperFitMode.Fill;

    /// <summary>
    /// Raised whenever <see cref="CurrentFitMode"/> changes. <see cref="DOSIScreen"/>
    /// listens for this and re-applies the corresponding <c>Stretch</c> /
    /// brush configuration to its wallpaper layers in real time.
    /// </summary>
    public event EventHandler? WallpaperFitChanged;

    /// <summary>
    /// Sets the active fit mode. Idempotent - re-setting the same mode is a
    /// no-op so listeners don't see spurious updates.
    /// </summary>
    public void SetFitMode(WallpaperFitMode mode)
    {
        if (mode == CurrentFitMode) return;
        CurrentFitMode = mode;
        WallpaperFitChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Returns <c>true</c> if a real wallpaper image is currently active.</summary>
    public bool HasActiveWallpaper =>
        !string.IsNullOrEmpty(CurrentWallpaperKey) &&
        !string.Equals(CurrentWallpaperKey, AccentOnlyKey, StringComparison.OrdinalIgnoreCase) &&
        TryGetWallpaper(CurrentWallpaperKey!, out _);

    /// <summary>The list of wallpapers shipped with DOSI.</summary>
    public IReadOnlyList<DOSIWallpaper> AvailableWallpapers => _wallpapers;

    /// <summary>
    /// Looks up a wallpaper descriptor by key. Returns <c>false</c> for the
    /// sentinel <see cref="AccentOnlyKey"/> or any unknown key.
    /// </summary>
    public bool TryGetWallpaper(string key, out DOSIWallpaper wallpaper)
    {
        foreach (var w in _wallpapers)
        {
            if (string.Equals(w.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                wallpaper = w;
                return true;
            }
        }
        wallpaper = null!;
        return false;
    }

    /// <summary>
    /// Sets the active wallpaper. Pass <c>null</c> or <see cref="AccentOnlyKey"/>
    /// to disable the wallpaper and fall back to the accent-tinted desktop.
    /// If <paramref name="key"/> looks like a file path or <c>file://</c> URI
    /// that isn't yet registered, the file is auto-registered so a custom
    /// wallpaper persisted in user preferences re-resolves after sign-in.
    /// </summary>
    public void SetWallpaper(string? key)
    {
        var normalized = string.IsNullOrEmpty(key) ? AccentOnlyKey : key;

        // If it looks like a custom file-system wallpaper that hasn't been
        // registered yet (typical on sign-in: prefs persist the path string
        // but the in-memory catalog only knows about shipped images), pull
        // it into the catalog now so subsequent LoadBitmap calls succeed.
        if (!string.Equals(normalized, AccentOnlyKey, StringComparison.OrdinalIgnoreCase) &&
            !TryGetWallpaper(normalized, out _) &&
            LooksLikeFilePath(normalized))
        {
            TryRegisterCustomWallpaperInternal(normalized, raiseEvent: true);
        }

        if (string.Equals(normalized, CurrentWallpaperKey, StringComparison.OrdinalIgnoreCase))
            return;

        CurrentWallpaperKey = normalized;
        WallpaperChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Registers a wallpaper from an arbitrary file on disk. The file's
    /// absolute path doubles as the key (so user preferences round-trip
    /// without a separate registry). Returns the key on success, or
    /// <c>null</c> if the file doesn't exist or isn't readable.
    /// </summary>
    public string? RegisterCustomWallpaper(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return null;
        return TryRegisterCustomWallpaperInternal(absolutePath, raiseEvent: true);
    }

    private string? TryRegisterCustomWallpaperInternal(string absolutePath, bool raiseEvent)
    {
        try
        {
            if (!File.Exists(absolutePath)) return null;
        }
        catch { return null; }

        // Idempotent - re-adding the same path returns the existing key.
        if (TryGetWallpaper(absolutePath, out _)) return absolutePath;

        Uri uri;
        try { uri = new Uri(absolutePath); }
        catch { return null; }

        var displayName = Path.GetFileNameWithoutExtension(absolutePath);
        if (string.IsNullOrWhiteSpace(displayName)) displayName = "Custom";

        _wallpapers.Add(new DOSIWallpaper
        {
            Key = absolutePath,
            DisplayName = displayName,
            AssetUri = uri
        });

        if (raiseEvent) WallpapersChanged?.Invoke(this, EventArgs.Empty);
        return absolutePath;
    }

    private static bool LooksLikeFilePath(string s) =>
        s.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
        (s.Length > 2 && (s[1] == ':' || s.StartsWith("/", StringComparison.Ordinal) ||
                          s.StartsWith("\\\\", StringComparison.Ordinal)));

    /// <summary>
    /// Hard cap on either dimension of a cached wallpaper bitmap, in pixels.
    /// Source PNGs larger than this are downscaled once at load time so per-
    /// frame compositing operates on a smaller GPU texture. The wallpaper is
    /// stretched <c>UniformToFill</c> across the desktop, so a 4K source and
    /// a 2.5K source look identical at any reasonable display size while the
    /// 2.5K version costs roughly 60% less to resample for every dirty rect
    /// produced by window drags, shadows, and translucency.
    /// </summary>
    private const int MaxCachedWallpaperDimension = 2560;

    /// <summary>
    /// Canonical Gaussian blur sigma baked into every cached wallpaper at
    /// load time. Producing the blur once per wallpaper - rather than
    /// applying a live <c>BlurEffect</c> at composite time - keeps the
    /// wallpaper visually soft (easier on the eyes, hides the hard edges
    /// of UniformToFill scaling) while letting per-frame compositing stay
    /// a flat opaque bitmap blit. That means dragging <c>DOSIWindow</c>s
    /// over the wallpaper costs the same as dragging over any plain photo:
    /// no offscreen surface, no shader pass, no "trailing blur" smear.
    /// Set to 0 to ship a sharp wallpaper.
    /// </summary>
    private const float WallpaperBlurSigma = 22f;

    /// <summary>
    /// Loads the canonical (blurred) bitmap for the given wallpaper key.
    /// Equivalent to <c>LoadBitmap(key, blurred: true)</c>. Kept as a
    /// separate overload so the very common consumer pattern stays
    /// readable and so existing call sites compile unchanged.
    /// </summary>
    public Bitmap? LoadBitmap(string key) => LoadBitmap(key, blurred: true);

    /// <summary>
    /// Loads the bitmap for the given wallpaper key in either the soft
    /// (blur-baked) or sharp variant, cached per variant. Returns
    /// <c>null</c> for unknown keys or load failures. Source images
    /// larger than <see cref="MaxCachedWallpaperDimension"/> on either
    /// axis are downscaled once; if <paramref name="blurred"/> is
    /// <c>true</c> the canonical <see cref="WallpaperBlurSigma"/> blur
    /// is then baked in. Both cache slots are populated lazily and
    /// shared across every screen, so toggling the desktop's wallpaper-
    /// blur preference at runtime is just a bitmap-source swap with no
    /// re-decode / re-bake work.
    /// </summary>
    public Bitmap? LoadBitmap(string key, bool blurred)
    {
        if (!TryGetWallpaper(key, out var wallpaper)) return null;

        var cache = blurred ? _blurredBitmapCache : _sharpBitmapCache;

        // Fast path: lock-free read of the cache. Dictionary reads of an
        // already-published entry are safe enough here (we never remove
        // entries) and this keeps the steady-state hit cost effectively zero.
        if (cache.TryGetValue(wallpaper.Key, out var cached)) return cached;

        // Slow path: serialize the decode + bake so the prewarm thread and
        // the UI thread don't race and produce two copies of the bitmap.
        lock (_loadLock)
        {
            // Re-check inside the lock - a concurrent caller may have
            // populated the entry while we were waiting.
            if (cache.TryGetValue(wallpaper.Key, out cached)) return cached;

            try
            {
                // file:// URIs come from RegisterCustomWallpaper; avares://
                // ones from the shipped catalog. Source dispatched off the
                // URI scheme so user-picked photos and built-ins flow through
                // the same downscale / blur-bake / cache pipeline.
                Stream stream;
                if (wallpaper.AssetUri.IsAbsoluteUri && wallpaper.AssetUri.IsFile)
                    stream = File.OpenRead(wallpaper.AssetUri.LocalPath);
                else
                    stream = AssetLoader.Open(wallpaper.AssetUri);

                Bitmap bmp;
                using (stream) bmp = new Bitmap(stream);

                var maxDim = Math.Max(bmp.PixelSize.Width, bmp.PixelSize.Height);
                if (maxDim > MaxCachedWallpaperDimension)
                {
                    var scale = (double)MaxCachedWallpaperDimension / maxDim;
                    var target = new PixelSize(
                        Math.Max(1, (int)Math.Round(bmp.PixelSize.Width * scale)),
                        Math.Max(1, (int)Math.Round(bmp.PixelSize.Height * scale)));

                    // HighQuality interpolation here is a one-shot cost paid at
                    // load; subsequent per-frame compositing uses LowQuality
                    // sampling on this already-correctly-sized bitmap.
                    var scaled = bmp.CreateScaledBitmap(target, BitmapInterpolationMode.HighQuality);
                    bmp.Dispose();
                    bmp = scaled;
                }

                if (blurred && WallpaperBlurSigma > 0.5f)
                {
                    // Bake the canonical universal blur into the cached bitmap.
                    // From this point on the bitmap is just a soft photo - no
                    // BlurEffect needs to be attached anywhere in the visual tree.
                    var blurredBmp = BakeGaussianBlur(bmp, WallpaperBlurSigma);
                    if (blurredBmp != null)
                    {
                        bmp.Dispose();
                        bmp = blurredBmp;
                    }
                }

                cache[wallpaper.Key] = bmp;
                return bmp;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Renders a Gaussian-blurred copy of <paramref name="source"/> at the
    /// source's intrinsic dimensions using SkiaSharp. Called exactly once
    /// per wallpaper as part of <see cref="LoadBitmap"/> so the result can
    /// be cached and reused as a plain opaque bitmap on every screen.
    /// </summary>
    private static Bitmap? BakeGaussianBlur(Bitmap source, float sigma)
    {
        try
        {
            // Roundtrip through PNG so SkiaSharp can decode the Avalonia
            // bitmap into an SKBitmap. One-shot at load time, so the encode
            // / decode cost is irrelevant compared to the lifetime savings
            // of never touching a BlurEffect at composite time.
            using var encoded = new MemoryStream();
            source.Save(encoded);
            encoded.Position = 0;
            using var skSource = SKBitmap.Decode(encoded);
            if (skSource == null) return null;

            var info = new SKImageInfo(
                skSource.Width,
                skSource.Height,
                SKColorType.Bgra8888,
                SKAlphaType.Premul);

            using var surface = SKSurface.Create(info);
            if (surface == null) return null;

            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Black);

            using var blur = SKImageFilter.CreateBlur(sigma, sigma);
            using var paint = new SKPaint { ImageFilter = blur };
            using var skSourceImage = SKImage.FromBitmap(skSource);

            // 1:1 draw - source already matches destination dimensions, so
            // the blur kernel is the only sampling work Skia has to do.
            canvas.DrawImage(skSourceImage, 0, 0, new SKSamplingOptions(SKFilterMode.Linear), paint);

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var memOut = new MemoryStream();
            data.SaveTo(memOut);
            memOut.Position = 0;
            return new Bitmap(memOut);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Builds an <see cref="ImageBrush"/> for the currently active wallpaper
    /// honouring <see cref="CurrentFitMode"/>, or <c>null</c> when no
    /// wallpaper image is active. Used by anything that paints the wallpaper
    /// as a brush (e.g. translucent windows that bleed it through). For the
    /// desktop's hard-edge layers we paint via two <c>Image</c> controls
    /// instead - see <see cref="ResolveStretch"/> + <see cref="IsTiled"/>.
    /// </summary>
    public ImageBrush? BuildCurrentBrush()
    {
        if (!HasActiveWallpaper) return null;
        var bmp = LoadBitmap(CurrentWallpaperKey!);
        if (bmp == null) return null;

        return new ImageBrush(bmp)
        {
            Stretch = ResolveStretch(CurrentFitMode),
            TileMode = IsTiled(CurrentFitMode) ? TileMode.Tile : TileMode.None,
            AlignmentX = AlignmentX.Center,
            AlignmentY = AlignmentY.Center
        };
    }

    /// <summary>Avalonia <see cref="Stretch"/> equivalent of a <see cref="WallpaperFitMode"/>.</summary>
    public static Stretch ResolveStretch(WallpaperFitMode mode) => mode switch
    {
        WallpaperFitMode.Fit     => Stretch.Uniform,
        WallpaperFitMode.Stretch => Stretch.Fill,
        WallpaperFitMode.Center  => Stretch.None,
        WallpaperFitMode.Tile    => Stretch.None, // brush handles tiling
        _                        => Stretch.UniformToFill, // Fill
    };

    /// <summary>True when the mode wants the bitmap repeated rather than scaled.</summary>
    public static bool IsTiled(WallpaperFitMode mode) => mode == WallpaperFitMode.Tile;

    /// <summary>
    /// Returns the bitmap for the currently active wallpaper (blurred
    /// variant), or <c>null</c> when accent-only mode is active or the
    /// bitmap fails to load.
    /// </summary>
    public Bitmap? GetCurrentBitmap() => GetCurrentBitmap(blurred: true);

    /// <summary>
    /// Returns the bitmap for the currently active wallpaper in the
    /// requested variant, or <c>null</c> when accent-only mode is active
    /// or the bitmap fails to load.
    /// </summary>
    public Bitmap? GetCurrentBitmap(bool blurred)
    {
        if (!HasActiveWallpaper) return null;
        return LoadBitmap(CurrentWallpaperKey!, blurred);
    }

    /// <summary>Applies the wallpaper preference saved on the supplied user.</summary>
    public void LoadFromUser(DOSIUser? user)
    {
        if (user == null)
        {
            SetWallpaper(DefaultWallpaperKey);
            SetFitMode(WallpaperFitMode.Fill);
            return;
        }

        if (user.Preferences.TryGetValue(WallpaperPreferenceKey, out var key) && !string.IsNullOrEmpty(key))
            SetWallpaper(key);
        else
            SetWallpaper(DefaultWallpaperKey);

        // Fit mode persisted as the enum name; bad / missing values fall
        // back to Fill (the previous hard-coded behaviour).
        if (user.Preferences.TryGetValue(UserManager.WallpaperFitPreferenceKey, out var fit) &&
            Enum.TryParse<WallpaperFitMode>(fit, ignoreCase: true, out var parsed))
        {
            SetFitMode(parsed);
        }
        else
        {
            SetFitMode(WallpaperFitMode.Fill);
        }
    }

    /// <summary>
    /// The wallpaper used when a user has no <c>wallpaper</c> preference
    /// stored. Defaults to the first shipped wallpaper, or the accent-only
    /// sentinel if the wallpaper list is empty.
    /// </summary>
    public string DefaultWallpaperKey =>
        _wallpapers.Count > 0 ? _wallpapers[0].Key : AccentOnlyKey;
}
