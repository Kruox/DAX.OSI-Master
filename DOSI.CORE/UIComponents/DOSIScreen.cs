using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DOSI.CORE.AccentManagement;
using DOSI.CORE.UIComponents.WindowManagement;
using DOSI.CORE.WallpaperManagement;

namespace DOSI.CORE.UIComponents;

/// <summary>
/// Base class for all screens in the DOSI virtual operating system.
/// Screens are full-screen views that represent different states of the OS (Boot, Login, Desktop, etc.)
/// Includes a Canvas for hosting windows and initializes the WindowManager.
/// </summary>
public abstract class DOSIScreen : UserControl
{
    /// <summary>
    /// Gets the unique identifier for this screen.
    /// </summary>
    public abstract string ScreenId { get; }

    /// <summary>
    /// Gets the display name of this screen.
    /// </summary>
    public abstract string ScreenName { get; }

    /// <summary>
    /// Event raised when the screen requests navigation to another screen.
    /// </summary>
    public event EventHandler<ScreenNavigationEventArgs>? NavigationRequested;

    /// <summary>
    /// Event raised when the screen has fully loaded and is ready.
    /// </summary>
    public event EventHandler? ScreenReady;

    private static AccentManager Accents => AccentManager.Instance;

    /// <summary>
    /// The canvas that hosts all windows on this screen.
    /// </summary>
    protected Canvas Desktop { get; }

    /// <summary>
    /// The window manager for this screen.
    /// </summary>
    protected WindowManager WindowManager { get; }

    // ---- Layered backdrop ----
    // _accentBackdrop is always rendered first (the accent radial vignette).
    // _wallpaperFront sits above it and shows the currently visible image.
    // _wallpaperBack is used during transitions: its source is set to the
    // incoming bitmap and its opacity is animated 0 -> 1, then we copy it
    // into _wallpaperFront for the next swap.
    //
    // Layers are Rectangles filled with ImageBrushes (rather than raw Image
    // controls) so the brush handles every fit mode the user might pick:
    // UniformToFill / Uniform / Fill / None (Center) / Tile - all native to
    // ImageBrush, none of them native to Image. The bitmap reference lives
    // on the Fill's ImageBrush; helpers below abstract get/set so the rest
    // of this class can keep talking about "the source bitmap".
    private readonly Border _accentBackdrop;
    private readonly Rectangle _wallpaperFront;
    private readonly Rectangle _wallpaperBack;
    private readonly Grid _wallpaperHost;
    private DispatcherTimer? _wallpaperAnimTimer;

    /// <summary>Duration of the accent ↔ wallpaper cross-fade.</summary>
    protected virtual TimeSpan WallpaperTransitionDuration => TimeSpan.FromMilliseconds(550);

    protected DOSIScreen()
    {
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;

        _accentBackdrop = new Border
        {
            Background = Accents.DesktopBackgroundBrush,
            IsHitTestVisible = false
        };

        var initialBitmap = ResolveWallpaperBitmap();
        _wallpaperFront = new Rectangle
        {
            Fill = BuildWallpaperBrush(initialBitmap),
            Opacity = initialBitmap != null ? 1 : 0,
            IsHitTestVisible = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
        };
        _wallpaperBack = new Rectangle
        {
            Fill = BuildWallpaperBrush(null),
            Opacity = 0,
            IsHitTestVisible = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
        };

        // Use high-quality bitmap interpolation for the backdrop. The
        // wallpaper is the most prominent visual on screen, especially when
        // stretched UniformToFill across a 4K desktop - the previous
        // LowQuality setting saved a few ms per composite when translucent
        // windows were dragged on top, but produced visibly blurred / muddy
        // output at non-integer scales (most obvious with portrait
        // wallpapers stretched to a landscape display). Quality wins.
        RenderOptions.SetBitmapInterpolationMode(_wallpaperFront, BitmapInterpolationMode.HighQuality);
        RenderOptions.SetBitmapInterpolationMode(_wallpaperBack, BitmapInterpolationMode.HighQuality);

        // Disable edge antialiasing on the wallpaper layers. The wallpaper
        // fills the whole desktop and never has visible edges of its own
        // (any clipping happens at the outer ClipContainer), so paying for
        // edge AA on every dirty rect produced by a window drag / shadow
        // halo is pure overhead. With Aliased edges the per-frame wallpaper
        // blit is a straight textured-quad copy.
        RenderOptions.SetEdgeMode(_wallpaperFront, EdgeMode.Aliased);
        RenderOptions.SetEdgeMode(_wallpaperBack, EdgeMode.Aliased);

        // Both wallpaper layers live inside a single host so the cross-fade
        // can stage the incoming bitmap on the back layer while the front
        // continues to render the outgoing one. The accent vignette
        // underneath is intentionally left outside this host so it isn't
        // affected by per-wallpaper opacity changes.
        //
        // Use a Grid (not a raw Panel) so the host has deterministic
        // stretch-to-parent measure semantics. A Panel propagates its
        // children's desired size upward; combined with Image.UniformToFill
        // (whose desired size depends on the bitmap) that can produce a
        // measure/arrange feedback cycle that Avalonia reports as
        // "Infinite layout loop detected".
        _wallpaperHost = new Grid
        {
            IsHitTestVisible = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            ClipToBounds = true,
            Children = { _wallpaperFront, _wallpaperBack }
        };

        Desktop = new Canvas
        {
            Background = Brushes.Transparent,
            ClipToBounds = false
        };

        WindowManager = new WindowManager(Desktop, makeActive: false);

        var backdropStack = new Grid
        {
            Children = { _accentBackdrop, _wallpaperHost, Desktop }
        };

        var clipContainer = new Border
        {
            ClipToBounds = true,
            Child = backdropStack
        };

        Content = clipContainer;

        Desktop.PointerPressed += OnDesktopPointerPressed;

        // Subscribe/unsubscribe properly to avoid memory leaks
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        Accents.AccentChanged += OnAccentChanged;
        WallpaperManager.Instance.WallpaperChanged += OnWallpaperChanged;
        WallpaperManager.Instance.WallpaperFitChanged += OnWallpaperFitChanged;
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        Accents.AccentChanged -= OnAccentChanged;
        WallpaperManager.Instance.WallpaperChanged -= OnWallpaperChanged;
        WallpaperManager.Instance.WallpaperFitChanged -= OnWallpaperFitChanged;
        _wallpaperAnimTimer?.Stop();
        _wallpaperAnimTimer = null;
    }

    private void OnWallpaperFitChanged(object? sender, EventArgs e)
    {
        // Stretch / TileMode / alignment all live on the ImageBrush, so
        // changing the fit mode means rebuilding the brush from the same
        // bitmap. Cheap - no decode, no cache invalidation; the existing
        // bitmap reference is reused.
        _wallpaperFront.Fill = BuildWallpaperBrush(GetLayerSource(_wallpaperFront));
        _wallpaperBack.Fill = BuildWallpaperBrush(GetLayerSource(_wallpaperBack));
    }

    private void OnDesktopPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source == Desktop)
        {
            WindowManager.ClearFocus();
            // Also clear focus on the application-wide manager since persistent
            // windows live there, not on this screen's local desktop canvas.
            WindowManager.Instance?.ClearFocus();
        }
    }

    private void OnAccentChanged(object? sender, EventArgs e)
    {
        // Accent vignette is always rendered underneath the wallpaper, so
        // refreshing it is a simple brush swap. AccentManager.ApplyAccentAnimated
        // already handles the smooth color interpolation for us; we just need
        // to re-bind to the latest brush instance each tick.
        _accentBackdrop.Background = Accents.DesktopBackgroundBrush;
    }

    private void OnWallpaperChanged(object? sender, EventArgs e)
    {
        // Outgoing screens (already navigated FROM but still attached during
        // the screen-level crossfade) must NOT react to wallpaper changes.
        // Otherwise a click on a login tile during boot->login fires
        // WallpaperChanged on both boot and login: boot's backdrop is still
        // visible so its 550ms fade plays under the fading-out boot screen,
        // and by the time the screen crossfade ends boot.GetWallpaperFrontSource
        // is already the user's wallpaper. ScreenManager then snaps that into
        // the incoming login screen and OnTransitionComplete sees front == desired,
        // skipping the visible fade. Result: instant wallpaper on first select.
        if (!_isCurrentScreen) return;

        AnimateWallpaperTransition(ResolveWallpaperBitmap());
    }

    /// <summary>
    /// True between <see cref="OnNavigatedTo"/> and <see cref="OnNavigatedFrom"/>.
    /// Gates wallpaper-change reactions so an outgoing screen that's still
    /// attached during a navigation crossfade can't run its own wallpaper
    /// transition and visually "steal" the fade from the incoming screen.
    /// </summary>
    private bool _isCurrentScreen;

    /// <summary>
    /// Returns the wallpaper bitmap this screen should currently display.
    /// The default implementation returns the canonical (blurred) variant
    /// from <see cref="WallpaperManager"/>; subclasses can override to
    /// honor a per-screen preference (e.g. <c>DesktopScreen</c> swaps to
    /// the sharp variant when the user disables wallpaper blur in
    /// Settings). Called every time the wallpaper bitmap needs to be
    /// resolved - on construction, on <c>WallpaperManager.WallpaperChanged</c>,
    /// and from <see cref="RefreshWallpaper"/>.
    /// </summary>
    protected virtual Bitmap? ResolveWallpaperBitmap()
    {
        return WallpaperManager.Instance.GetCurrentBitmap();
    }

    /// <summary>
    /// Re-resolves the wallpaper bitmap via <see cref="ResolveWallpaperBitmap"/>
    /// and animates a cross-fade to it. Use this from a subclass when a
    /// per-screen preference (rather than the global wallpaper key)
    /// changes - e.g. <c>DesktopScreen</c> calls this when its blur
    /// toggle flips so the desktop animates between the sharp and
    /// blurred variants of the same underlying wallpaper.
    /// </summary>
    protected void RefreshWallpaper()
    {
        AnimateWallpaperTransition(ResolveWallpaperBitmap());
    }

    /// <summary>
    /// Toggles the per-screen backdrop layers (accent vignette + wallpaper)
    /// on or off. Used by <see cref="ScreenManager"/> while two screens are
    /// stacked in the visual tree during a cross-fade: rendering both
    /// screens' backdrops at once produces a visible "ghost" of two accent
    /// vignettes and two wallpaper bitmaps composited through the
    /// fading-in screen (most plainly visible during a successful
    /// LoginScreen -> DesktopScreen handoff). Hiding the incoming screen's
    /// backdrop for the duration of the fade leaves the outgoing screen's
    /// (visually identical) backdrop as the sole compositor input. The
    /// cutover after the fade is invisible because both screens point at
    /// the same <see cref="WallpaperManager"/> bitmap and the same
    /// <see cref="AccentManager"/> brush.
    /// </summary>
    internal void SetBackdropVisible(bool visible)
    {
        _accentBackdrop.IsVisible = visible;
        _wallpaperHost.IsVisible = visible;
    }

    /// <summary>
    /// Returns the bitmap currently bound to the front wallpaper layer.
    /// Used by <see cref="ScreenManager"/> right after a screen cross-fade
    /// to seed the incoming screen's wallpaper with whatever the outgoing
    /// screen had on display, so the reveal that follows is visually
    /// continuous (no "snap" between two different wallpaper variants /
    /// bitmaps when the new screen's backdrop becomes visible).
    /// </summary>
    internal Bitmap? GetWallpaperFrontSource() => GetLayerSource(_wallpaperFront);

    /// <summary>
    /// Builds an <see cref="ImageBrush"/> for a wallpaper layer that honours
    /// the current <see cref="WallpaperManager.CurrentFitMode"/>. Pass
    /// <c>null</c> for an empty/transparent brush (used by the back layer
    /// when nothing is staged for transition).
    /// </summary>
    private static ImageBrush BuildWallpaperBrush(Bitmap? bmp)
    {
        var mode = WallpaperManager.Instance.CurrentFitMode;
        return new ImageBrush(bmp)
        {
            Stretch = WallpaperManager.ResolveStretch(mode),
            TileMode = WallpaperManager.IsTiled(mode) ? TileMode.Tile : TileMode.None,
            AlignmentX = AlignmentX.Center,
            AlignmentY = AlignmentY.Center
        };
    }

    /// <summary>Reads the bitmap currently painted into a wallpaper layer.</summary>
    private static Bitmap? GetLayerSource(Rectangle layer) =>
        (layer.Fill as ImageBrush)?.Source as Bitmap;

    /// <summary>Re-paints a wallpaper layer with the supplied bitmap (or none).</summary>
    private static void SetLayerSource(Rectangle layer, Bitmap? bmp) =>
        layer.Fill = BuildWallpaperBrush(bmp);

    /// <summary>
    /// Snaps the front wallpaper layer to <paramref name="bitmap"/> with no
    /// cross-fade animation. Used by <see cref="ScreenManager"/> to align
    /// the incoming screen's visual state with the outgoing screen's just
    /// before the new screen's backdrop is revealed - any variant
    /// difference is then animated visibly via <see cref="OnTransitionComplete"/>
    /// instead of snapping behind the hidden backdrop.
    /// </summary>
    internal void SetWallpaperFrontSourceImmediate(Bitmap? bitmap)
    {
        // Cancel any in-flight cross-fade FIRST. Otherwise its completion
        // tick (which writes the user's target bitmap into _wallpaperFront)
        // can fire AFTER this snap, leaving the front showing the user's
        // wallpaper at full opacity. OnTransitionComplete would then see
        // front == desired and skip the visible fade - producing an
        // instant wallpaper snap when the backdrop reveals. Killing the
        // timer here lets the snap stick and lets OnTransitionComplete
        // run a clean visible animation from the snapped state to the
        // newly desired wallpaper.
        _wallpaperAnimTimer?.Stop();
        _wallpaperAnimTimer = null;

        SetLayerSource(_wallpaperFront, bitmap);
        _wallpaperFront.Opacity = bitmap != null ? 1 : 0;
        SetLayerSource(_wallpaperBack, null);
        _wallpaperBack.Opacity = 0;
    }

    /// <summary>
    /// Called by <see cref="ScreenManager"/> after the screen-level cross-
    /// fade has fully settled and this screen's backdrop is visible. The
    /// default implementation cross-fades the wallpaper layer from
    /// whatever bitmap is currently displayed (typically the outgoing
    /// screen's, just snapped in by <see cref="SetWallpaperFrontSourceImmediate"/>)
    /// to the result of <see cref="ResolveWallpaperBitmap"/>. This is what
    /// makes a per-screen wallpaper-variant difference (e.g. the desktop
    /// preferring the sharp variant while the login screen always uses the
    /// soft variant) animate visibly on top of the screen handoff instead
    /// of snapping invisibly behind the hidden backdrop.
    /// </summary>
    internal virtual void OnTransitionComplete()
    {
        var desired = ResolveWallpaperBitmap();
        if (ReferenceEquals(GetLayerSource(_wallpaperFront), desired)) return;
        AnimateWallpaperTransition(desired);
    }

    /// <summary>
    /// Cross-fades the backdrop from whatever is currently shown to
    /// <paramref name="targetBitmap"/>. Pass <c>null</c> to fade back to the
    /// accent-only vignette.
    /// </summary>
    private void AnimateWallpaperTransition(Bitmap? targetBitmap)
    {
        // No change? Nothing to animate.
        if (ReferenceEquals(GetLayerSource(_wallpaperFront), targetBitmap) &&
            Math.Abs(_wallpaperFront.Opacity - (targetBitmap != null ? 1 : 0)) < 0.001)
        {
            return;
        }

        _wallpaperAnimTimer?.Stop();

        var duration = WallpaperTransitionDuration.TotalMilliseconds;
        var startTime = DateTime.UtcNow;

        if (targetBitmap != null)
        {
            // Fading INTO an image: stage it on the back layer at opacity 0
            // and ramp it up over the front (which keeps showing whatever it
            // had until we swap on completion).
            SetLayerSource(_wallpaperBack, targetBitmap);
            _wallpaperBack.Opacity = 0;

            var startBack = 0d;
            const double targetBack = 1d;

            _wallpaperAnimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _wallpaperAnimTimer.Tick += (_, _) =>
            {
                var t = Math.Clamp((DateTime.UtcNow - startTime).TotalMilliseconds / duration, 0d, 1d);
                var eased = 1 - Math.Pow(1 - t, 3); // ease-out cubic
                _wallpaperBack.Opacity = startBack + (targetBack - startBack) * eased;

                if (t >= 1d)
                {
                    _wallpaperAnimTimer?.Stop();
                    _wallpaperAnimTimer = null;
                    // Promote back -> front and reset back so subsequent
                    // transitions start from a clean slate.
                    SetLayerSource(_wallpaperFront, targetBitmap);
                    _wallpaperFront.Opacity = 1;
                    SetLayerSource(_wallpaperBack, null);
                    _wallpaperBack.Opacity = 0;
                }
            };
            _wallpaperAnimTimer.Start();
        }
        else
        {
            // Fading OUT to accent-only: nothing to bring in, just dissolve
            // whichever image layers are currently visible.
            var startFront = _wallpaperFront.Opacity;
            var startBack = _wallpaperBack.Opacity;

            _wallpaperAnimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _wallpaperAnimTimer.Tick += (_, _) =>
            {
                var t = Math.Clamp((DateTime.UtcNow - startTime).TotalMilliseconds / duration, 0d, 1d);
                var eased = t * t; // ease-in quad
                _wallpaperFront.Opacity = startFront * (1 - eased);
                _wallpaperBack.Opacity = startBack * (1 - eased);

                if (t >= 1d)
                {
                    _wallpaperAnimTimer?.Stop();
                    _wallpaperAnimTimer = null;
                    SetLayerSource(_wallpaperFront, null);
                    _wallpaperFront.Opacity = 0;
                    SetLayerSource(_wallpaperBack, null);
                    _wallpaperBack.Opacity = 0;
                }
            };
            _wallpaperAnimTimer.Start();
        }
    }

    /// <summary>
    /// Called when the screen is about to be shown.
    /// </summary>
    public virtual void OnNavigatedTo()
    {
        // Mark this screen as the current one so it resumes reacting to
        // wallpaper changes. See _isCurrentScreen for the rationale.
        _isCurrentScreen = true;

        // Note: window operations target the persistent overlay manager owned
        // by MainWindow, not this screen's local WindowManager. The local one
        // is kept only for backwards compatibility with screens that needed
        // their own click-to-clear-focus on the desktop background.
    }

    /// <summary>
    /// Called when the screen is about to be hidden.
    /// </summary>
    public virtual void OnNavigatedFrom()
    {
        // Stop reacting to wallpaper changes while we're being faded out.
        // The incoming screen owns the visible wallpaper transition from
        // here on - see _isCurrentScreen and OnWallpaperChanged.
        _isCurrentScreen = false;
    }

    /// <summary>
    /// Called when the screen should be initialized.
    /// </summary>
    protected virtual void Initialize() { }

    /// <summary>
    /// Requests navigation to another screen.
    /// </summary>
    protected void NavigateTo(string screenId, object? parameter = null)
    {
        NavigationRequested?.Invoke(this, new ScreenNavigationEventArgs(screenId, parameter));
    }

    /// <summary>
    /// Notifies that the screen is ready.
    /// </summary>
    protected void NotifyScreenReady()
    {
        ScreenReady?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// Event arguments for screen navigation.
/// </summary>
public class ScreenNavigationEventArgs : EventArgs
{
    public string TargetScreenId { get; }
    public object? Parameter { get; }

    public ScreenNavigationEventArgs(string targetScreenId, object? parameter = null)
    {
        TargetScreenId = targetScreenId;
        Parameter = parameter;
    }
}
