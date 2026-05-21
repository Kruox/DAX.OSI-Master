using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace DOSI.CORE.UIComponents.WindowManagement;

/// <summary>
/// Borderless, transparent, topmost native Avalonia <c>Window</c> that
/// displays a bitmap snapshot of a <see cref="DOSIWindow"/> while it is
/// being dragged across physical monitors. Without this, a cross-monitor
/// drag is invisible during the gap between displays - the source
/// <see cref="DOSIWindow"/> can't render outside its parent native window
/// (that's a fundamental Avalonia / OS-compositor limitation), so the
/// user only sees the cursor moving while the actual window stays clipped
/// at the source monitor's edge.
///
/// Pooled lifecycle: a SINGLE process-wide ghost is lazily created on the
/// first cross-monitor drag and reused forever after via <see cref="Shared"/>.
/// Recreating the ghost per-drag triggers a one-frame "first paint" flicker
/// of the transparent topmost window on every drag start (well-known
/// Avalonia / Windows compositor quirk). Pooling kills the flicker because
/// the OS only ever has to allocate the layered window once.
///
/// Usage from <c>DOSIWindow</c>:
///   1. <see cref="ShowAt"/> at drag start (snapshot, size, screen position).
///   2. <see cref="MoveTo"/> on every PointerMoved.
///   3. <see cref="HideGhost"/> at drag end (NOT <c>Close()</c> - we keep
///      the native window alive in the pool).
///
/// Caveats:
///   * The snapshot is static - mid-drag content updates won't render.
///     Acceptable trade-off (Windows Explorer behaves the same).
///   * Native-rendered children (e.g. WebView) do not participate in
///     <see cref="RenderTargetBitmap.Render"/> - their region in the ghost
///     will be blank. Acceptable for v1.
///   * On mixed-DPI multi-monitor setups the ghost may briefly look the
///     wrong physical size as it crosses the monitor boundary. The OS
///     scales it per its current monitor's DPI; the snapshot was rendered
///     at source DPI. Polishable later.
/// </summary>
public sealed class DragGhostWindow : Window
{
    private readonly Image _image;

    /// <summary>
    /// Process-wide pooled instance. Created lazily on first call to
    /// <see cref="GetOrCreate"/> from the UI thread.
    /// </summary>
    public static DragGhostWindow? Shared { get; private set; }

    /// <summary>
    /// Returns the pooled ghost, creating it on first call. MUST be called
    /// from the UI thread (Avalonia <c>Window</c> construction is UI-only).
    /// </summary>
    public static DragGhostWindow GetOrCreate()
    {
        Shared ??= new DragGhostWindow();
        return Shared;
    }

    /// <summary>
    /// Eagerly creates the pooled ghost AND forces the OS to allocate the
    /// underlying transparent layered window by showing it off-screen at
    /// Opacity=0. This is the only way to avoid the one-frame flicker on
    /// the FIRST drag of the process lifetime - Windows / the Avalonia
    /// composition layer defer the layered-surface allocation until first
    /// paint, and that first paint flashes briefly even when the window
    /// is transparent. After this call, the ghost stays SHOWN forever
    /// (parked off-screen at Opacity=0 when not in use); subsequent
    /// drag starts only toggle Position / Opacity, NEVER Show()/Hide().
    /// Hide() + Show() round-trips re-trigger the same layered-window
    /// composition path that flickers, defeating the pool entirely.
    /// </summary>
    public static void Prewarm()
    {
        var ghost = GetOrCreate();
        if (ghost._prewarmed) return;
        try
        {
            // 1x1 transparent footprint parked at deep-negative pixel coords
            // so it's guaranteed off every connected monitor's bounds. The
            // OS still allocates the layered window even off-screen.
            ghost.Width = 1;
            ghost.Height = 1;
            ghost.Position = new PixelPoint(-32000, -32000);
            ghost.Opacity = 0;
            ghost.Show();
            ghost._prewarmed = true;
        }
        catch
        {
            // Pre-warm is a pure optimization; if it throws, the first
            // real drag just absorbs the original first-paint flicker.
        }
    }

    private bool _prewarmed;

    private DragGhostWindow()
    {
        // Avalonia 12 split decorations into a new WindowDecorations
        // property and deprecated the old SystemDecorations setter.
        // Setting None strips the OS title bar + chrome - the
        // ExtendClientArea hints alone leave a visible "Window" title
        // bar floating around the snapshot, which looks awful.
        WindowDecorations = WindowDecorations.None;

        // Empty title prevents anything from leaking into Alt+Tab UI / OS
        // window list as a stray "Window" entry while a drag is in flight.
        Title = string.Empty;

        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        ShowInTaskbar = false;
        CanResize = false;
        Topmost = true;
        // Critical: do NOT activate when shown. Activating would steal
        // focus from the source window and break its pointer capture,
        // killing the drag mid-motion.
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        // Critical: the ghost Window is permanently SHOWN (pool design)
        // and ALWAYS sits Topmost over every other window. Even at
        // Opacity=0 a transparent topmost window with Background=Transparent
        // is HIT-TESTABLE in Avalonia (only Background=null is pass-through),
        // which means it would silently swallow every click that lands on
        // its current position. Marking the Window itself non-hit-testable
        // makes it click-through regardless of opacity / position / size.
        // The drag still works because the source window owns pointer
        // capture for the entire drag lifetime - ghost never needs input.
        IsHitTestVisible = false;

        _image = new Image
        {
            Stretch = Stretch.Fill,
            // Belt-and-braces: even with the Window above non-hit-testable,
            // mark the Image too so future Window-level changes can't
            // accidentally re-introduce input interception.
            IsHitTestVisible = false
        };
        Content = _image;
    }

    /// <summary>
    /// Configures the pooled ghost with a fresh snapshot, sizes it to the
    /// source DOSIWindow's dimensions, and positions it - WITHOUT changing
    /// its opacity. Used by <c>DOSIWindow</c> at drag start to "arm" the
    /// ghost so its layered window already has the right content at the
    /// right place but stays invisible (Opacity 0). When the user later
    /// drags across to another monitor, only the opacity flips, which the
    /// OS DWM applies atomically - no first-paint hiccup, no 1-frame swap
    /// gap between source disappearing and ghost appearing.
    ///
    /// Defensive Show(): if Prewarm wasn't called (e.g. single-monitor at
    /// startup, monitor hot-plugged), the OS window may not be up yet.
    /// After this point it stays shown forever - HideGhost just parks it
    /// off-screen at Opacity=0.
    /// </summary>
    public void ConfigureFor(RenderTargetBitmap snapshot, double dipWidth, double dipHeight, PixelPoint screenPosition)
    {
        _image.Source = snapshot;
        Width = dipWidth;
        Height = dipHeight;
        Position = screenPosition;
        if (!IsVisible) Show();
    }

    /// <summary>
    /// Atomically toggles ghost visibility via Opacity. The layered window
    /// stays SHOWN at the OS level - we never call Show()/Hide() because
    /// that round-trip re-triggers the layered-surface composition path
    /// that flickers on transparent topmost windows.
    /// </summary>
    public void SetVisible(bool visible) => Opacity = visible ? 1 : 0;

    /// <summary>
    /// Re-arms the pooled ghost AND brings it on-screen via Opacity. Kept
    /// for back-compat with single-shot show callers; new code should use
    /// the <see cref="ConfigureFor"/> + <see cref="SetVisible"/> split so
    /// the layered window has a chance to compose the new content at the
    /// new position BEFORE opacity flips.
    /// </summary>
    public void ShowAt(RenderTargetBitmap snapshot, double dipWidth, double dipHeight, PixelPoint screenPosition)
    {
        ConfigureFor(snapshot, dipWidth, dipHeight, screenPosition);
        SetVisible(true);
    }

    /// <summary>Updates the ghost's screen-pixel position. Hot path - called every PointerMoved.</summary>
    public void MoveTo(PixelPoint screenPosition) => Position = screenPosition;

    /// <summary>
    /// Visually retracts the pooled ghost without destroying or hiding the
    /// native window: drops Opacity to 0, parks the position off every
    /// connected monitor, and clears the snapshot reference so the source
    /// DOSIWindow's bitmap can be GC'd between drags. The OS-level layered
    /// window stays SHOWN forever - we never call <see cref="Window.Hide"/>
    /// because re-showing it on the next drag triggers the exact one-frame
    /// flicker we're pooling to avoid.
    /// </summary>
    public void HideGhost()
    {
        Opacity = 0;
        Position = new PixelPoint(-32000, -32000);
        _image.Source = null;
    }

    /// <summary>
    /// Permanently destroys the pooled ghost. MUST be called during app
    /// shutdown - because we keep the OS-level layered window SHOWN for
    /// the entire process lifetime to avoid re-show flicker, the
    /// <c>ClassicDesktopStyleApplicationLifetime</c> default
    /// <c>ShutdownMode = OnLastWindowClose</c> will count the ghost as a
    /// living window and refuse to exit the process. Without this call,
    /// the app hangs forever after MainWindow + every MonitorWindow has
    /// closed but the ghost is still up. Safe to call multiple times.
    /// </summary>
    public static void Shutdown()
    {
        var ghost = Shared;
        if (ghost == null) return;
        Shared = null;
        try { ghost.Close(); } catch { /* best-effort during teardown */ }
    }
}
