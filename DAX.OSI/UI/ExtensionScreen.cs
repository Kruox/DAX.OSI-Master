using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DOSI.CORE;
using DOSI.CORE.AccentManagement;
using DOSI.CORE.UIComponents;
using DOSI.CORE.UIComponents.WindowManagement;
using DOSI.CORE.UserManagement;
using DOSI.CORE.WallpaperManagement;
using Avalonia.VisualTree;

namespace DAX.OSI.UI;

/// <summary>
/// Minimal <see cref="DOSIScreen"/> shown on every secondary physical
/// monitor. Renders the system wallpaper + accent backdrop the
/// <see cref="DOSIScreen"/> base class provides for free, PLUS its own
/// per-monitor <see cref="DesktopIconLayer"/> so the user can drop
/// files / folders on any monitor and they stay spatially anchored to
/// that display. No taskbar, no apps menu, no clock, no version label -
/// those remain primary-only, same as Windows / macOS conventions.
///
/// Each extension monitor binds its icon layer to a distinct subfolder
/// of the user's home (<c>~/Desktop-Monitor2</c>, <c>~/Desktop-Monitor3</c>,
/// ...) so dragging an icon on one monitor doesn't move it on the others.
/// The folder is auto-created on first bind.
///
/// Honours the per-user wallpaper-blur preference (read from
/// <see cref="UserManager.GetUserWallpaperBlur"/> on attach, refreshed
/// when <c>DOSISettingsScreen.WallpaperBlurChanged</c> fires) so toggling
/// blur in Settings cross-fades EVERY monitor's wallpaper, not just the
/// primary's. Without this, the default
/// <see cref="DOSIScreen.ResolveWallpaperBitmap"/> always returned the
/// blurred variant on extensions and the toggle visibly applied only to
/// the primary.
/// </summary>
public class ExtensionScreen : DOSIScreen
{
    public override string ScreenId => "extension";
    public override string ScreenName => "Extension";

    /// <summary>
    /// Extension screens are pure FOLLOWERS of the primary's wallpaper
    /// state - they never originate a transition, only mirror primary
    /// broadcasts. Setting this to false prevents an echo: if the
    /// extension broadcast back, the primary would re-mirror, and the
    /// transition would loop or stutter.
    /// </summary>
    protected override bool IsWallpaperBroadcaster => false;

    private bool _wallpaperBlurEnabled = true;
    private readonly int _monitorIndex;
    private DesktopIconLayer? _iconLayer;
    // Visual-only top taskbar that matches the primary monitor's bar.
    // No apps button, clock, user chip, or running-apps strip - just
    // the same gradient + accent border so the chrome reads as
    // continuous across all monitors. Reserves the same work-
    // area inset on the extension's WindowManager so DOSIWindows on
    // this monitor can't open or be dragged behind it. Mounted into
    // the owning host's PopupHost on attach so it renders above
    // DOSIWindows living in the host's window overlay.
    private static double TaskbarHeight =>
        DOSI.CORE.UIComponents.WindowManagement.TaskbarMetrics.Height;
    private static bool TaskbarIsTop =>
        DOSI.CORE.UIComponents.WindowManagement.TaskbarMetrics.Position
            == DOSI.CORE.UIComponents.WindowManagement.TaskbarPosition.Top;
    private static double OffScreenSlideY =>
        TaskbarIsTop ? -TaskbarHeight : TaskbarHeight;
    private Border? _visualTaskbar;
    private IDosiHost? _ownerHost;
    // Drives the slide-in/out animation. The Border's Y translation
    // moves between -TaskbarHeight (off-screen above) and 0 (docked).
    // Mirrors the primary monitor's _taskbarSlide pattern in
    // DesktopScreen so both bars animate identically.
    private TranslateTransform? _visualTaskbarSlide;
    // First-slide-in latch. Mirrors DesktopScreen._taskbarHasSlidIn so
    // the bar only does its drop-down animation ONCE per session - any
    // subsequent attach (screen-manager reparent during a crossfade)
    // leaves the bar in its docked position instead of re-running the
    // slide, which would otherwise read as a duplicate animation while
    // the desktop is being crossfaded in.
    private bool _visualTaskbarHasSlidIn;
    // Active animation handle so a follow-up slide (e.g. fast sign-out
    // while sign-in is still animating) can supersede the in-flight one.
    private DispatcherTimer? _visualTaskbarAnimTimer;

    /// <summary>
    /// Creates an extension screen for monitor <paramref name="monitorIndex"/>
    /// (2 = first secondary, 3 = second secondary, ...). The index drives
    /// the per-monitor icon folder name so each display gets its own tile set.
    /// </summary>
    public ExtensionScreen(int monitorIndex = 2)
    {
        _monitorIndex = monitorIndex < 2 ? 2 : monitorIndex;

        // Per-monitor desktop icon layer. Inserted at the bottom of the
        // Desktop canvas so the wallpaper paints behind it (matches how
        // DesktopScreen.cs stacks the primary's icon layer).
        //
        // VISIBILITY GATE: born hidden so secondary monitors don't render
        // tiles before the primary's DesktopScreen has finished its
        // crossfade-in. Flipped visible by OnPrimaryDesktopReadyChanged
        // the moment the primary signals ready - both monitors then show
        // their tiles in the same frame. Without this gate the secondary
        // monitors visibly render tiles + taskbar ~200-500 ms before the
        // primary's desktop appears, which reads as "the side monitors
        // loaded first".
        _iconLayer = new DesktopIconLayer($"Desktop-Monitor{_monitorIndex}");
        _iconLayer.IsVisible = DesktopScreen.PrimaryDesktopReady;
        Desktop.Children.Insert(0, _iconLayer);

        // Wallpaper right-click menu - was previously primary-only because
        // DesktopScreen owned the implementation outright. Now hosted on
        // DesktopIconLayer so each monitor gets a menu whose Paste / New
        // folder / Open Files actions all target THIS monitor's desktop
        // folder (~/Desktop-MonitorN) instead of the primary's.
        Desktop.ContextMenu = _iconLayer.BuildWallpaperContextMenu();

        AttachedToVisualTree += (_, _) =>
        {
            RefreshBlurFromUser();
            DAX.OSI.DefaultApplications.DOSISettingsScreen.WallpaperBlurChanged += OnWallpaperBlurChanged;
            // Extension monitors usually attach BEFORE the user signs in
            // (boot screen comes up immediately on every monitor). Without
            // this hook the screen stays at the default (blurred) and
            // never picks up the user's saved preference, leaving every
            // secondary display blurred while the primary correctly shows
            // the unblurred wallpaper. Re-pull on every user change so
            // sign-in / sign-out flips both monitors in lock-step.
            UserManager.CurrentUserChanged += OnCurrentUserChanged;
            Accents.AccentChanged += OnAccentChanged;
            DesktopScreen.PrimaryDesktopReadyChanged += OnPrimaryDesktopReadyChanged;
            // Mirror EVERY primary wallpaper transition (sign-in, sign-out,
            // blur toggle, wallpaper change, accent flip) in the same
            // frame and with the same duration so all monitors animate
            // as a single system-wide gesture.
            DOSIScreen.WallpaperSyncBroadcast += OnPrimaryWallpaperSyncBroadcast;
            // Hide desktop tiles the instant sign-out begins. Without this
            // the tiles stay visible on every secondary monitor through the
            // entire sign-out animation (the secondaries intentionally don't
            // fade their wallpaper layer, and the icon layer lives on the
            // SAME wallpaper canvas, so it inherits the same "stay visible"
            // policy). NotifyPrimaryDesktopGone() does eventually hide them
            // via OnPrimaryDesktopReadyChanged, but it doesn't fire until
            // AFTER the signout screen has fully played - far too late.
            SystemSignOut.SignOutStarting += OnSignOutStarting;
            SystemShutdown.ShutdownStarting += OnSignOutStarting;
            MountVisualTaskbar();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            DAX.OSI.DefaultApplications.DOSISettingsScreen.WallpaperBlurChanged -= OnWallpaperBlurChanged;
            UserManager.CurrentUserChanged -= OnCurrentUserChanged;
            Accents.AccentChanged -= OnAccentChanged;
            DesktopScreen.PrimaryDesktopReadyChanged -= OnPrimaryDesktopReadyChanged;
            DOSIScreen.WallpaperSyncBroadcast -= OnPrimaryWallpaperSyncBroadcast;
            SystemSignOut.SignOutStarting -= OnSignOutStarting;
            SystemShutdown.ShutdownStarting -= OnSignOutStarting;
            UnmountVisualTaskbar();
        };
    }

    /// <summary>
    /// Primary monitor signalled its DesktopScreen has finished
    /// navigating in (true) or has been torn down (false). Reveals
    /// the icon layer and mounts the visual taskbar so both monitors
    /// finish their entrance together. On false, hides the icon layer
    /// and unmounts the bar so the secondaries fade away alongside
    /// the primary's sign-out / shutdown sequence.
    /// </summary>
    private void OnPrimaryDesktopReadyChanged(object? sender, bool ready)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_iconLayer != null)
            {
                _iconLayer.IsVisible = ready;
                // Restore pointer interaction. AnimateAllTilesOutAsync
                // (run from OnSignOutStarting) flipped IsHitTestVisible
                // to false to block clicks during the farewell tween,
                // and nothing else restores it - so on the very next
                // sign-in the layer becomes visible again but rejects
                // every pointer event, making the desktop tiles look
                // "frozen / unresponsive" on every secondary monitor.
                if (ready) _iconLayer.IsHitTestVisible = true;
            }
            if (ready)
            {
                // Refresh the cached blur preference for any LATER local
                // transition (e.g. live blur toggle from Settings). The
                // actual sign-in wallpaper cross-fade is NOT kicked off
                // here - it arrives via WallpaperSyncBroadcast when the
                // primary's DesktopScreen actually starts its transition,
                // so every monitor animates on the same frame.
                RefreshBlurFromUser();
                MountVisualTaskbar();
            }
            else
            {
                UnmountVisualTaskbar();
                _visualTaskbarHasSlidIn = false;
            }
        });
    }

    /// <summary>
    /// Primary monitor's <see cref="DesktopScreen"/> (or any other
    /// broadcaster screen) just emitted a per-frame wallpaper sync
    /// packet. Apply it directly to this screen's layers - no local
    /// timer, no compositor scheduling, no per-monitor drift. The
    /// master's <see cref="DispatcherTimer"/> is the single source of
    /// truth for every monitor's wallpaper opacities, which is the only
    /// way to guarantee all monitors show the SAME coverage on the
    /// SAME frame regardless of which TopLevel paints first.
    /// </summary>
    private void OnPrimaryWallpaperSyncBroadcast(object? sender, DOSIScreen.WallpaperSyncFrame frame)
    {
        // Sender check: only act when the broadcaster is a DIFFERENT
        // screen instance.
        if (ReferenceEquals(sender, this)) return;
        ApplyWallpaperSyncFrame(frame);
    }

    /// <summary>
    /// Hides this monitor's desktop tiles the instant a sign-out begins.
    /// Fires from <see cref="SystemSignOut.SignOutStarting"/> on the UI
    /// thread BEFORE any sign-out animation runs, so the tiles disappear
    /// in the same frame the primary's taskbar starts retracting and the
    /// signout-screen crossfade begins. Also retracts this monitor's
    /// visual taskbar in lockstep with the primary's.
    /// <para>
    /// Without this, the tiles + visual taskbar stay painted on every
    /// secondary monitor through the entire sign-out sequence - the
    /// secondaries intentionally don't fade their wallpaper layer (so
    /// the user sees continuous wallpaper across monitors during the
    /// user switch), and the icon layer + taskbar both live on that
    /// same wallpaper canvas, inheriting the "stay visible" policy.
    /// </para>
    /// </summary>
    private void OnSignOutStarting()
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Play the per-tile farewell animation so the icons on this
            // secondary monitor gracefully retract in lockstep with the
            // primary's. AnimateAllTilesOutAsync hides the layer on
            // completion (and no-ops if no tiles are present), so we
            // don't need a separate IsVisible flip here.
            _ = _iconLayer?.AnimateAllTilesOutAsync();
            UnmountVisualTaskbar();
            _visualTaskbarHasSlidIn = false;
        });
    }

    private static AccentManager Accents => AccentManager.Instance;

    private void OnAccentChanged(object? sender, EventArgs e)
    {
        // Re-tint to follow live accent flips (same behaviour the primary
        // taskbar gets via DesktopScreen.OnAccentAccentChanged).
        if (_visualTaskbar != null)
        {
            _visualTaskbar.Background = DesktopScreen.BuildTaskbarBackground();
            _visualTaskbar.BorderBrush = DesktopScreen.BuildTaskbarBorderBrush();
        }
    }

    private IDosiHost? ResolveOwnerHost()
    {
        Avalonia.Visual? v = this;
        while (v != null)
        {
            if (v is IDosiHost host) return host;
            v = v.GetVisualParent();
        }
        return null;
    }

    /// <summary>
    /// Builds the visual-only taskbar and parents it into this monitor's
    /// PopupHost so it always renders above any DOSIWindow on this
    /// display. Also reserves the same 28 px top inset on the extension's
    /// WindowManager so windows opened or dragged on this monitor can't
    /// fall behind the bar - matches the contract DesktopScreen sets on
    /// the primary's WindowManager.
    /// <para>
    /// VISIBILITY GATE: only mounts when a user is signed in. The bar is
    /// part of the post-login chrome - boot / login screens already lay
    /// their own decorative content over the wallpaper and a second bar
    /// on the secondary monitor would be visual noise during sign-in.
    /// Sign-out cleanly unmounts via <see cref="UnmountVisualTaskbar"/>
    /// so the bar disappears in lockstep with the primary's slide-out.
    /// </para>
    /// </summary>
    private void MountVisualTaskbar()
    {
        if (_visualTaskbar != null) return;

        // Gate: no user signed in -> no chrome. OnCurrentUserChanged will
        // re-call us on sign-in, at which point the bar drops in.
        if (UserManager.CurrentUser == null) return;

        // Gate: primary monitor's DesktopScreen hasn't finished its
        // entrance yet. OnPrimaryDesktopReadyChanged re-calls us the
        // instant the primary signals ready so both monitors animate
        // their bars in together.
        if (!DesktopScreen.PrimaryDesktopReady) return;

        _ownerHost = ResolveOwnerHost();

        // Reserve the inset on this monitor's WindowManager on the side
        // that matches the user's dock preference.
        var wm = _ownerHost?.WindowManager ?? WindowManager.Instance;
        ApplyWmInset(wm);

        // Slide-in transform - born off-screen on the side we're docking
        // against. Top dock => -Height, bottom dock => +Height. The
        // animation below tweens Y back to 0.
        _visualTaskbarSlide = new TranslateTransform(0, OffScreenSlideY);

        _visualTaskbar = new Border
        {
            Height = TaskbarHeight,
            // NOTE: PopupHost is a Canvas on every IDosiHost - Canvas
            // ignores HorizontalAlignment.Stretch / VerticalAlignment.Top
            // and sizes children to their own content. We pin via
            // Canvas.Left / Canvas.Top in SyncBarLayout (top dock = 0,
            // bottom dock = canvasHeight - TaskbarHeight) which also
            // tracks DPI / monitor changes.
            Background = DesktopScreen.BuildTaskbarBackground(),
            BorderBrush = DesktopScreen.BuildTaskbarBorderBrush(),
            // Border line on the inside edge: bottom for top dock, top
            // for bottom dock.
            BorderThickness = TaskbarIsTop
                ? new Thickness(0, 0, 0, 1)
                : new Thickness(0, 1, 0, 0),
            IsHitTestVisible = false, // pure visual continuity; no clicks
            RenderTransform = _visualTaskbarSlide
        };
        Canvas.SetLeft(_visualTaskbar, 0);
        Canvas.SetTop(_visualTaskbar, 0);

        var popup = _ownerHost?.PopupHost as Panel;
        if (popup != null)
        {
            popup.Children.Add(_visualTaskbar);

            // Width + top-anchor sync - Canvas-children don't auto-stretch.
            void SyncBarLayout(object? _, EventArgs __)
            {
                if (_visualTaskbar == null) return;
                _visualTaskbar.Width = popup.Bounds.Width;
                Canvas.SetTop(_visualTaskbar,
                    TaskbarIsTop ? 0 : Math.Max(0, popup.Bounds.Height - TaskbarHeight));
            }
            popup.LayoutUpdated += SyncBarLayout;
            SyncBarLayout(null, EventArgs.Empty);
        }
        else
        {
            // Last-resort: park the bar on the screen's Desktop canvas so
            // the visual still appears (under windows). Strictly worse
            // than the popup mount but keeps the bar visible if the host
            // doesn't expose a PopupHost yet.
            Desktop.Children.Add(_visualTaskbar);
        }

        // First-attach: slide the bar down. Posted at Loaded priority so
        // it runs AFTER the parent screen crossfade has started and the
        // popup canvas has measured itself - same timing the primary
        // monitor's taskbar uses. Subsequent attaches (screen-manager
        // reparent during sign-out / shutdown crossfades) skip the
        // slide so we don't visibly drop the bar in WHILE the sign-out
        // fade is running.
        if (!_visualTaskbarHasSlidIn)
        {
            _visualTaskbarHasSlidIn = true;
            Dispatcher.UIThread.Post(
                () => AnimateVisualTaskbar(targetY: 0, durationMs: 320, easeOut: true),
                DispatcherPriority.Loaded);
        }
        else
        {
            // Already shown once this session - just snap to docked so
            // a re-mount (rare) doesn't leave the bar off-screen.
            _visualTaskbarSlide.Y = 0;
        }

        // React to live taskbar-height + position changes from
        // Settings: resize the bar, flip alignment when the dock edge
        // changes, re-publish the work-area inset on the right side
        // so DOSIWindow maximize / drag-clamp respect the new geometry.
        DOSI.CORE.UIComponents.WindowManagement.TaskbarMetrics.HeightChanged += OnTaskbarHeightChanged;
        DOSI.CORE.UIComponents.WindowManagement.TaskbarMetrics.PositionChanged += OnTaskbarPositionChanged;
    }

    private void ApplyWmInset(WindowManager? wm)
    {
        if (wm == null) return;
        if (TaskbarIsTop)
        {
            if (wm.TopWorkAreaInset < TaskbarHeight) wm.TopWorkAreaInset = TaskbarHeight;
            wm.BottomWorkAreaInset = 0;
        }
        else
        {
            wm.TopWorkAreaInset = 0;
            if (wm.BottomWorkAreaInset < TaskbarHeight) wm.BottomWorkAreaInset = TaskbarHeight;
        }
    }

    private void OnTaskbarHeightChanged(object? sender, double newHeight)
    {
        if (_visualTaskbar != null) _visualTaskbar.Height = newHeight;
        if (_visualTaskbarSlide != null && Math.Abs(_visualTaskbarSlide.Y) > 0.5)
            _visualTaskbarSlide.Y = OffScreenSlideY;
        // Reposition for bottom dock - the canvas-anchor uses height.
        if (_visualTaskbar?.Parent is Panel popup)
            Canvas.SetTop(_visualTaskbar,
                TaskbarIsTop ? 0 : Math.Max(0, popup.Bounds.Height - newHeight));
        ApplyWmInset(_ownerHost?.WindowManager ?? WindowManager.Instance);
    }

    private void OnTaskbarPositionChanged(object? sender,
        DOSI.CORE.UIComponents.WindowManagement.TaskbarPosition pos)
    {
        if (_visualTaskbar != null)
        {
            _visualTaskbar.BorderThickness = TaskbarIsTop
                ? new Thickness(0, 0, 0, 1)
                : new Thickness(0, 1, 0, 0);
            if (_visualTaskbar.Parent is Panel popup)
                Canvas.SetTop(_visualTaskbar,
                    TaskbarIsTop ? 0 : Math.Max(0, popup.Bounds.Height - TaskbarHeight));
        }
        if (_visualTaskbarSlide != null) _visualTaskbarSlide.Y = 0;
        ApplyWmInset(_ownerHost?.WindowManager ?? WindowManager.Instance);
    }

    private void UnmountVisualTaskbar()
    {
        if (_visualTaskbar == null) return;

        DOSI.CORE.UIComponents.WindowManagement.TaskbarMetrics.HeightChanged -= OnTaskbarHeightChanged;
        DOSI.CORE.UIComponents.WindowManagement.TaskbarMetrics.PositionChanged -= OnTaskbarPositionChanged;

        _visualTaskbarAnimTimer?.Stop();
        _visualTaskbarAnimTimer = null;

        if (_visualTaskbar.Parent is Panel parent)
            parent.Children.Remove(_visualTaskbar);

        var wm = _ownerHost?.WindowManager ?? WindowManager.Instance;
        if (wm != null)
        {
            // Clear whichever side is currently reserving for our bar -
            // we don't know which dock was active when we last applied,
            // so clear both edges if they match TaskbarHeight.
            if (Math.Abs(wm.TopWorkAreaInset - TaskbarHeight) < 0.5)
                wm.TopWorkAreaInset = 0;
            if (Math.Abs(wm.BottomWorkAreaInset - TaskbarHeight) < 0.5)
                wm.BottomWorkAreaInset = 0;
        }

        _visualTaskbar = null;
        _visualTaskbarSlide = null;
        _ownerHost = null;
    }

    /// <summary>
    /// Tweens <see cref="_visualTaskbarSlide"/>.Y to <paramref name="targetY"/>.
    /// Cubic ease-out for entrance, cubic ease-in for retraction - the
    /// same easing pair DesktopScreen uses for its taskbar slide.
    /// Supersedes any in-flight animation so a fast sign-out during a
    /// still-running sign-in entrance reads cleanly.
    /// </summary>
    private void AnimateVisualTaskbar(double targetY, int durationMs, bool easeOut)
    {
        if (_visualTaskbarSlide == null) return;

        _visualTaskbarAnimTimer?.Stop();

        var startY = _visualTaskbarSlide.Y;
        if (Math.Abs(startY - targetY) < 0.5)
        {
            _visualTaskbarSlide.Y = targetY;
            return;
        }

        var startTime = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _visualTaskbarAnimTimer = timer;
        timer.Tick += (_, _) =>
        {
            if (_visualTaskbarSlide == null || !ReferenceEquals(_visualTaskbarAnimTimer, timer))
            {
                timer.Stop();
                return;
            }
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            var t = Math.Clamp(elapsed / durationMs, 0d, 1d);
            var eased = easeOut
                ? 1 - Math.Pow(1 - t, 3)   // cubic ease-out for entrance
                : t * t * t;                // cubic ease-in for exit
            _visualTaskbarSlide.Y = startY + (targetY - startY) * eased;

            if (t >= 1d)
            {
                timer.Stop();
                _visualTaskbarAnimTimer = null;
            }
        };
        timer.Start();
    }

    protected override Avalonia.Media.Imaging.Bitmap? ResolveWallpaperBitmap()
    {
        return WallpaperManager.Instance.GetCurrentBitmap(blurred: _wallpaperBlurEnabled);
    }

    private void RefreshBlurFromUser()
    {
        var user = UserManager.CurrentUser;
        // Default (no user signed in) keeps the historical blurred look so
        // the boot / login backdrop matches the primary's behaviour.
        _wallpaperBlurEnabled = user == null || UserManager.GetUserWallpaperBlur(user);
    }

    private void OnCurrentUserChanged(object? sender, DOSIUser? user)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Update the cached preference value, but do NOT trigger a
            // local refresh. The primary's DesktopScreen will broadcast
            // its actual wallpaper cross-fade via WallpaperSyncBroadcast
            // when it's ready - OnPrimaryWallpaperSyncBroadcast above
            // mirrors that transition in the same frame, so every monitor
            // animates as one continuous system-wide gesture.
            RefreshBlurFromUser();

            // Visual taskbar follows the user state: sign-in mounts +
            // slides in, sign-out unmounts. Reset the once-per-session
            // latch on sign-out so the NEXT sign-in animates the bar
            // back in instead of snapping it.
            if (user == null)
            {
                UnmountVisualTaskbar();
                _visualTaskbarHasSlidIn = false;
            }
            else
            {
                MountVisualTaskbar();
            }
        });
    }

    private void OnWallpaperBlurChanged(object? sender, bool enabled)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_wallpaperBlurEnabled == enabled) return;
            _wallpaperBlurEnabled = enabled;
            RefreshWallpaper();
        });
    }
}
