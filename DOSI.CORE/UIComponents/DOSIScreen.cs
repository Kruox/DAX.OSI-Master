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

    /// <summary>
    /// When <c>true</c>, this screen's wallpaper transitions are broadcast
    /// system-wide via <see cref="WallpaperSyncBroadcast"/> so secondary
    /// monitors (which run their own <c>DOSIScreen</c> instance per
    /// physical display) can mirror the animation in the SAME frame and
    /// for the SAME duration. Set to <c>false</c> on screens that exist
    /// to FOLLOW the primary (e.g. <c>ExtensionScreen</c>) so they don't
    /// echo the broadcast back and trigger a feedback loop.
    /// </summary>
    protected virtual bool IsWallpaperBroadcaster => true;

    /// <summary>
    /// Per-frame state of a system-wide wallpaper cross-fade. Emitted by
    /// the master (primary) screen's transition timer on every tick so
    /// secondary monitors can apply the IDENTICAL opacities in the same
    /// frame instead of running their own (drift-prone) timers.
    /// </summary>
    /// <param name="Target">Bitmap being faded TO. Null = fade to accent-only.</param>
    /// <param name="FrontOpacity">Opacity to apply to the local front wallpaper layer.</param>
    /// <param name="BackOpacity">Opacity to apply to the local back wallpaper layer.</param>
    /// <param name="UseFrontForTarget">
    /// True when the master is ramping the FRONT layer (front was empty
    /// at start of fade); false when it's ramping the BACK over a
    /// visible front. Tells the secondary which layer to stage the
    /// target bitmap onto so the visual matches exactly.
    /// </param>
    /// <param name="IsFinal">
    /// True for the last tick of the animation. Tells secondaries to
    /// promote back-&gt;front (when applicable) and reset the back layer,
    /// keeping post-transition state identical to the master.
    /// </param>
    public readonly record struct WallpaperSyncFrame(
        Bitmap? Target,
        double FrontOpacity,
        double BackOpacity,
        bool UseFrontForTarget,
        bool IsFinal);

    /// <summary>
    /// Fired on EVERY tick of a broadcaster screen's wallpaper cross-fade,
    /// carrying the exact per-frame opacity state. Secondary monitors
    /// subscribe to this and apply the values to their own layers - no
    /// local timer, no per-monitor drift, no compositor desync. Also
    /// fires once with the FINAL frame so listeners can settle post-state.
    /// <para>
    /// Static so listeners don't have to find the broadcaster screen
    /// instance - it can be on a different visual tree (different
    /// IDosiHost) entirely.
    /// </para>
    /// </summary>
    public static event EventHandler<WallpaperSyncFrame>? WallpaperSyncBroadcast;

    /// <summary>
    /// Previously drove <c>_accentBackdrop.Opacity</c> as the inverse of
    /// current wallpaper coverage to prevent any accent bleed-through
    /// during fade-in seams. That bleed-through is already prevented at
    /// the source: the empty-front fade-in path snaps the target bitmap
    /// onto the front at <c>Opacity = 1</c> in a single frame instead
    /// of ramping (see <see cref="AnimateWallpaperTransition"/>), and the
    /// back-ramp path pins the front opaque throughout.
    /// <para>
    /// Keeping the accent permanently masked below the wallpaper layer
    /// (even when the wallpaper layer reports <c>Opacity = 1</c>) visibly
    /// flattened screens under the Light accent: at scaled-bitmap edge
    /// AA, letterbox bands in <c>Stretch.Uniform</c> fit modes, and the
    /// faint sub-pixel transparency along the wallpaper rectangle's
    /// outline, the accent vignette used to lift the composite and give
    /// the Light theme its luminous quality. Masking it to 0 killed that
    /// lift - <c>LoginScreen</c> in particular read as "duller" under
    /// Light. The accent backdrop is now always visible underneath the
    /// wallpaper, restoring the original look while the snap-on-fade-in
    /// keeps the ghost gone.
    /// </para>
    /// <para>
    /// Method kept as a no-op so the existing call sites in the
    /// animation paths don't need surgery; if a future regression
    /// surfaces an accent ghost we have one place to re-enable the
    /// inverse-mask behaviour.
    /// </para>
    /// </summary>
    private void RefreshAccentMask()
    {
        // Intentionally empty - see XML doc.
    }

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

        // Bilinear (LowQuality) interpolation for the wallpaper layers. The
        // historical comment that lived here claimed HighQuality was needed
        // to avoid a "blurred / muddy" look at non-integer scales - but that
        // was written when the source bitmap was the raw photo. Today the
        // wallpaper pipeline pre-bakes a Gaussian blur into the cached
        // bitmap AND caps the cached dimension at 4K (see WallpaperManager),
        // so:
        //   * Bicubic sharpening has nothing left to sharpen - the high
        //     frequencies were intentionally destroyed at load time.
        //   * Bilinear is 4x cheaper per sample (2x2 vs 4x4 source taps),
        //     which dominates the per-frame cost because window drag /
        //     scale-on-open / menu slide-in all produce dirty rectangles
        //     that cover large stretches of wallpaper.
        // Net result: identical-looking wallpaper, smooth animations even
        // on integrated GPUs and with large custom photos selected.
        RenderOptions.SetBitmapInterpolationMode(_wallpaperFront, BitmapInterpolationMode.LowQuality);
        RenderOptions.SetBitmapInterpolationMode(_wallpaperBack, BitmapInterpolationMode.LowQuality);

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

        // Initial accent-mask sync so the backdrop opacity is correctly
        // inverted against the starting wallpaper coverage. Without this,
        // a screen born with no resolvable bitmap would render with both
        // the (full) accent vignette AND a transparent wallpaper layer
        // until the first opacity mutation - the same composite that
        // produces the accent ghost during transitions.
        RefreshAccentMask();
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

        // Resolve the target bitmap WITHOUT blocking the UI thread. The
        // bitmap may still need to decode + downscale + Skia blur-bake -
        // up to a couple of seconds on a 24 MP source - and doing that
        // synchronously here freezes the whole dispatcher for the
        // duration, which kills every concurrent animation (the accent
        // tween, the panel cross-fade, the avatar pop-in). That hang is
        // exactly the "click user tile, screen freezes for a second,
        // then everything snaps in" symptom.
        //
        // Off-thread resolve + post-back to UI thread for the actual
        // fade keeps every animation smooth. The wallpaper fade-in
        // simply starts a tick later than the accent/panel animations,
        // which reads naturally - the wallpaper is the heaviest visual
        // change on screen and a tiny extra delay before it lands is
        // imperceptible compared to a frozen dispatcher.
        var currentKey = WallpaperManager.Instance.CurrentWallpaperKey;
        System.Threading.Tasks.Task.Run(() =>
        {
            Bitmap? bmp;
            try { bmp = ResolveWallpaperBitmap(); }
            catch { bmp = null; }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // Bail if the wallpaper changed again before we finished
                // resolving (rapid user-tile-clicking on the login screen).
                // The next OnWallpaperChanged will animate to the latest.
                if (!_isCurrentScreen) return;
                if (!string.Equals(WallpaperManager.Instance.CurrentWallpaperKey,
                                   currentKey, StringComparison.OrdinalIgnoreCase)) return;
                AnimateWallpaperTransition(bmp);
            });
        });
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
        if (visible) RefreshAccentMask();
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
        RefreshAccentMask();
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
    protected void AnimateWallpaperTransition(Bitmap? targetBitmap)
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
        bool broadcaster = IsWallpaperBroadcaster;

        // Helper: fire a per-frame sync packet with the current
        // opacities + bitmap. Secondaries apply these values directly,
        // so the master and every listener show the same coverage on
        // the same compositor frame.
        void Emit(double frontOp, double backOp, bool useFront, bool isFinal)
        {
            if (!broadcaster) return;
            try
            {
                WallpaperSyncBroadcast?.Invoke(this,
                    new WallpaperSyncFrame(targetBitmap, frontOp, backOp, useFront, isFinal));
            }
            catch { /* never let a listener break the primary transition */ }
        }

        if (targetBitmap != null)
        {
            // FRONT-COVERAGE GUARANTEE during a fade-in:
            //
            // The naive "stage target on back, ramp back 0->1, leave front
            // alone" plan only holds if the front is currently FULLY opaque
            // with a real bitmap. Several paths violate that assumption:
            //   * SetWallpaperFrontSourceImmediate(null) leaves front
            //     Opacity=0 with source=null,
            //   * a previous fade-out branch completed and left front=0,
            //   * a screen's initial ResolveWallpaperBitmap returned null.
            // Whenever the front is empty/transparent at the start of a
            // fade-in, the back ramping 0->1 visually composites OVER the
            // accent vignette below the wallpaper host - which is exactly
            // the "accent color flashes during the wallpaper transition"
            // symptom.
            //
            // Fix: when the front carries a visible bitmap, pin it at 1
            // and run a true cross-fade on the back. When the front is
            // EMPTY, skip the back-layer staging entirely - put the target
            // straight on the front and ramp the FRONT's own opacity to 1.
            bool frontHasImage = GetLayerSource(_wallpaperFront) != null
                                 && _wallpaperFront.Opacity > 0.001;

            if (frontHasImage)
            {
                _wallpaperFront.Opacity = 1; // defensive: kill any lingering partial state
                SetLayerSource(_wallpaperBack, targetBitmap);
                _wallpaperBack.Opacity = 0;
                RefreshAccentMask();

                Emit(frontOp: 1, backOp: 0, useFront: false, isFinal: false);

                _wallpaperAnimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                _wallpaperAnimTimer.Tick += (_, _) =>
                {
                    var t = Math.Clamp((DateTime.UtcNow - startTime).TotalMilliseconds / duration, 0d, 1d);
                    var eased = 1 - Math.Pow(1 - t, 3); // ease-out cubic
                    _wallpaperBack.Opacity = eased;
                    RefreshAccentMask();

                    if (t < 1d)
                    {
                        Emit(frontOp: 1, backOp: eased, useFront: false, isFinal: false);
                    }
                    else
                    {
                        _wallpaperAnimTimer?.Stop();
                        _wallpaperAnimTimer = null;
                        SetLayerSource(_wallpaperFront, targetBitmap);
                        _wallpaperFront.Opacity = 1;
                        SetLayerSource(_wallpaperBack, null);
                        _wallpaperBack.Opacity = 0;
                        RefreshAccentMask();
                        Emit(frontOp: 1, backOp: 0, useFront: false, isFinal: true);
                    }
                };
                _wallpaperAnimTimer.Start();
            }
            else
            {
                // Front is currently invisible/empty (e.g. after a fade
                // back to accent-only, or first attach with no resolvable
                // bitmap). Stage the target ON THE FRONT at Opacity=0 and
                // ramp it up to 1 with an ease-out cubic. The accent
                // vignette underneath remains fully opaque the whole time
                // and is gradually covered by the rising wallpaper - which
                // is exactly the visual the login screen wants when the
                // user picks their tile (accent gracefully dissolves into
                // the chosen wallpaper instead of snapping in one frame).
                //
                // HISTORY: an earlier version of this branch snapped the
                // wallpaper to Opacity=1 in a single frame to avoid what
                // was called an "accent ghost" - i.e. seeing the accent
                // through a partially-opaque wallpaper during the ramp.
                // That snap eliminated the cross-fade itself; on the boot
                // -> login -> user-wallpaper path the wallpaper appeared
                // instantaneously which read as cheap. The accent
                // "bleed-through" IS the cross-fade in this branch: the
                // accent backdrop is the FROM frame, the wallpaper is the
                // TO frame, and the ramp is the interpolation between
                // them. Restoring the ramp gives the login transition its
                // beautiful "dissolve into the user's world" feel.
                SetLayerSource(_wallpaperFront, targetBitmap);
                SetLayerSource(_wallpaperBack, null);
                _wallpaperFront.Opacity = 0;
                _wallpaperBack.Opacity = 0;
                RefreshAccentMask();

                Emit(frontOp: 0, backOp: 0, useFront: true, isFinal: false);

                _wallpaperAnimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                _wallpaperAnimTimer.Tick += (_, _) =>
                {
                    var t = Math.Clamp((DateTime.UtcNow - startTime).TotalMilliseconds / duration, 0d, 1d);
                    var eased = 1 - Math.Pow(1 - t, 3); // ease-out cubic
                    _wallpaperFront.Opacity = eased;
                    RefreshAccentMask();

                    if (t < 1d)
                    {
                        Emit(frontOp: eased, backOp: 0, useFront: true, isFinal: false);
                    }
                    else
                    {
                        _wallpaperAnimTimer?.Stop();
                        _wallpaperAnimTimer = null;
                        _wallpaperFront.Opacity = 1;
                        SetLayerSource(_wallpaperBack, null);
                        _wallpaperBack.Opacity = 0;
                        RefreshAccentMask();
                        Emit(frontOp: 1, backOp: 0, useFront: true, isFinal: true);
                    }
                };
                _wallpaperAnimTimer.Start();
            }
        }
        else
        {
            // Fading OUT to accent-only: nothing to bring in, just dissolve
            // whichever image layers are currently visible.
            var startFront = _wallpaperFront.Opacity;
            var startBack = _wallpaperBack.Opacity;

            Emit(frontOp: startFront, backOp: startBack, useFront: false, isFinal: false);

            _wallpaperAnimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _wallpaperAnimTimer.Tick += (_, _) =>
            {
                var t = Math.Clamp((DateTime.UtcNow - startTime).TotalMilliseconds / duration, 0d, 1d);
                var eased = t * t; // ease-in quad
                var fop = startFront * (1 - eased);
                var bop = startBack * (1 - eased);
                _wallpaperFront.Opacity = fop;
                _wallpaperBack.Opacity = bop;
                RefreshAccentMask();

                if (t < 1d)
                {
                    Emit(frontOp: fop, backOp: bop, useFront: false, isFinal: false);
                }
                else
                {
                    _wallpaperAnimTimer?.Stop();
                    _wallpaperAnimTimer = null;
                    SetLayerSource(_wallpaperFront, null);
                    _wallpaperFront.Opacity = 0;
                    SetLayerSource(_wallpaperBack, null);
                    _wallpaperBack.Opacity = 0;
                    RefreshAccentMask();
                    Emit(frontOp: 0, backOp: 0, useFront: false, isFinal: true);
                }
            };
            _wallpaperAnimTimer.Start();
        }
    }

    /// <summary>
    /// Applies a single per-frame wallpaper sync packet to this screen.
    /// Called by follower screens (e.g. <c>ExtensionScreen</c>) from the
    /// master's tick broadcast so every monitor's compositor frame shows
    /// the IDENTICAL coverage with zero local timer drift.
    /// </summary>
    protected void ApplyWallpaperSyncFrame(WallpaperSyncFrame frame)
    {
        // Kill any local timer that was still running from a previous
        // (non-synced) path so we can't have two writers fighting over
        // the same layers.
        _wallpaperAnimTimer?.Stop();
        _wallpaperAnimTimer = null;

        if (frame.Target != null)
        {
            // Mode parity with the broadcaster: front-ramp vs back-ramp.
            // Stage the target on whichever layer the master is animating
            // so the bitmap arrives synchronously with the first ramped
            // opacity > 0.
            if (frame.UseFrontForTarget)
            {
                if (!ReferenceEquals(GetLayerSource(_wallpaperFront), frame.Target))
                    SetLayerSource(_wallpaperFront, frame.Target);
                if (GetLayerSource(_wallpaperBack) != null)
                    SetLayerSource(_wallpaperBack, null);
            }
            else
            {
                // Master is back-ramping: front shows the OUTGOING bitmap,
                // back shows the target. Pin the front opaque (defensive)
                // and stage target on back.
                if (!ReferenceEquals(GetLayerSource(_wallpaperBack), frame.Target))
                    SetLayerSource(_wallpaperBack, frame.Target);
            }
        }

        _wallpaperFront.Opacity = frame.FrontOpacity;
        _wallpaperBack.Opacity = frame.BackOpacity;
        RefreshAccentMask();

        if (frame.IsFinal)
        {
            // Settle into the steady-state the master ends in: target on
            // front at 1, back cleared. Skipped when fading to null - the
            // ramps already drove both to 0 above and we want to keep them
            // there (no bitmap painted).
            if (frame.Target != null)
            {
                SetLayerSource(_wallpaperFront, frame.Target);
                _wallpaperFront.Opacity = 1;
                SetLayerSource(_wallpaperBack, null);
                _wallpaperBack.Opacity = 0;
            }
            else
            {
                SetLayerSource(_wallpaperFront, null);
                SetLayerSource(_wallpaperBack, null);
            }
            RefreshAccentMask();
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
