using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DAX.OSI.DefaultApplications;
using DOSI.CORE;
using DOSI.CORE.AccentManagement;
using DOSI.CORE.Apps;
using DOSI.CORE.ProjectSystem;
using DOSI.CORE.UIComponents;
using DOSI.CORE.UIComponents.WindowManagement;
using DOSI.CORE.UserManagement;
using DOSI.CORE.WallpaperManagement;

namespace DAX.OSI.UI;

/// <summary>
/// The desktop screen shown after a successful sign-in. Hosts open windows,
/// shows a transient welcome notification, and renders a thin top taskbar
/// with an Applications launcher and the signed-in user / live clock.
/// </summary>
public class DesktopScreen : DOSIScreen
{
    public override string ScreenId => "desktop";
    public override string ScreenName => "Desktop";

    private static AccentManager Accents => AccentManager.Instance;

    /// <summary>
    /// Sticky flag set true the first time the primary monitor's
    /// DesktopScreen finishes <see cref="OnNavigatedTo"/>. Secondary
    /// monitors (ExtensionScreen) check this on attach to decide whether
    /// to render their post-login chrome (icon-layer tiles + visual
    /// taskbar) immediately or wait. Reset by the sign-out / shutdown
    /// flow when the primary's desktop is torn down so the NEXT sign-in
    /// re-gates secondaries until the primary catches up.
    /// </summary>
    public static bool PrimaryDesktopReady { get; private set; }

    /// <summary>
    /// Raised whenever <see cref="PrimaryDesktopReady"/> flips. The bool
    /// payload is the new value. Secondary monitors hook this to
    /// slide their taskbar in / rebuild their icon layer the moment
    /// the primary signals ready - both monitors then animate in
    /// together instead of the secondaries running ahead.
    /// </summary>
    public static event EventHandler<bool>? PrimaryDesktopReadyChanged;

    /// <summary>
    /// Called by the sign-out and shutdown pipelines so secondaries
    /// know the primary is leaving the desktop. Re-arms the gate so
    /// the next sign-in's secondary attach waits for the primary's
    /// fresh OnNavigatedTo.
    /// </summary>
    public static void NotifyPrimaryDesktopGone()
    {
        if (!PrimaryDesktopReady) return;
        PrimaryDesktopReady = false;
        PrimaryDesktopReadyChanged?.Invoke(null, false);
    }

    // Live taskbar height: reads from TaskbarMetrics so the user's
    // saved preference applies on every layout pass. Subscribe to
    // TaskbarMetrics.HeightChanged where you need to react to live
    // updates (we do this in AttachedToVisualTree below).
    private static double TaskbarHeight =>
        DOSI.CORE.UIComponents.WindowManagement.TaskbarMetrics.Height;

    // Live taskbar dock edge. Same metric, sister property. Layout
    // primitives that need to flip alignment / margin direction read
    // this once at construction and then react to PositionChanged.
    private static DOSI.CORE.UIComponents.WindowManagement.TaskbarPosition TaskbarMetricsPosition =>
        DOSI.CORE.UIComponents.WindowManagement.TaskbarMetrics.Position;

    /// <summary>
    /// The off-screen Y the slide transform parks at when the bar is
    /// hidden. Negative for top dock (above the screen), positive for
    /// bottom dock (below the screen). Always re-derived from the
    /// current Position + Height so a live position swap correctly
    /// re-parks any in-flight animation.
    /// </summary>
    private static double OffScreenSlideY =>
        TaskbarMetricsPosition == DOSI.CORE.UIComponents.WindowManagement.TaskbarPosition.Top
            ? -TaskbarHeight
            :  TaskbarHeight;
    // ----- Layout -----
    private readonly Grid _layoutRoot;
    private readonly Grid _ambientLayer;
    private DesktopIconLayer? _iconLayer;
    private readonly Border _taskbar;
    // Drives the taskbar slide-in / slide-out animations. The Border's Y
    // translation moves between -TaskbarHeight (off-screen above) and 0
    // (docked at the top of the desktop).
    private readonly TranslateTransform _taskbarSlide = null!;
    // Latched true after the first slide-in completes so we don't re-run
    // it on every visual-tree reattach (the screen manager's crossfade
    // reparents this screen mid sign-out / shutdown, which would otherwise
    // make the bar slide back in WHILE the chrome is fading away).
    private bool _taskbarHasSlidIn;

    // The IDosiHost (MainWindow OR a secondary MonitorWindow) that this
    // DesktopScreen instance is rendered into. Resolved on attach by walking
    // up the visual tree; cached so detach can reach the SAME host even if
    // the screen has already been pulled out of the tree by ScreenManager.
    private IDosiHost? _ownerHost;

    /// <summary>
    /// Walks up the visual tree from this screen looking for the owning
    /// <see cref="IDosiHost"/>. Falls back to the static
    /// <c>MainWindow.PopupHost</c> / <c>WindowManager.Instance</c> path so
    /// any caller hosting DesktopScreen outside an IDosiHost (legacy tests,
    /// designer previews, ...) keeps working.
    /// </summary>
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
    private readonly Border _appsButton;
    private readonly Border _appsButtonAccent;
    private readonly TextBlock _appsButtonLabel;
    private readonly TextBlock _taskbarUser;
    private readonly Border _userAvatarChip;
    private readonly TextBlock _userAvatarInitial;
    private readonly TextBlock _clockText;
    private readonly TextBlock _dateText;
    private readonly TextBlock _versionText;
    // Held so OnTaskbarPositionChanged can re-margin the clock when
    // the user moves the taskbar between top and bottom dock.
    private StackPanel? _clockStack;

    // ----- Apps menu -----
    private readonly Border _appsMenuBackdrop;
    private readonly Border _appsMenu;
    private readonly TranslateTransform _appsMenuTranslate;
    private DispatcherTimer? _appsMenuAnimTimer;
    private bool _appsMenuOpen;
    private StackPanel? _appsMenuItems;
    // Type-to-search state. The search box at the top of the menu filters
    // the visible tiles in real time; the filtered list is what the menu
    // renders. Empty filter == show everything (including section headers).
    private DOSITextBox? _appsMenuSearch;
    private string _appsMenuFilter = string.Empty;
    // Glyph cache for the built-in apps (Terminal, Browser, Files, etc.).
    // Without this, every menu open rebuilt 6+ Avalonia control trees just
    // for the icons; this is a tangible reduction in pointer churn for a
    // surface the user opens dozens of times a session.
    private readonly Dictionary<string, Control> _builtInGlyphCache = new(StringComparer.Ordinal);

    // ----- Notification Center -----
    // Bell button on the taskbar plus a dropdown popover that lists every
    // entry in NotificationHistory. The bell pulses while there are
    // unread items (entries added since the user last opened the popover).
    private Border? _notifBell;
    private Border? _notifBadge;
    private TextBlock? _notifBadgeText;
    private Border? _notifPopover;
    private Border? _notifPopoverBackdrop;
    private StackPanel? _notifList;
    private TextBlock? _notifPopoverTitle;
    private bool _notifPopoverOpen;
    // Animation: identical contract to the apps menu - a TranslateTransform
    // for the slide-in offset and a DispatcherTimer for the per-frame tween.
    // Keeping the popover and the apps menu visually in lock-step is a
    // deliberate choice so right-side and left-side affordances feel
    // unified.
    private TranslateTransform? _notifPopoverTranslate;
    private DispatcherTimer? _notifAnimTimer;
    private int _notifLastSeenCount;

    // ----- State -----
    private readonly DispatcherTimer _clockTimer;

    /// <summary>
    /// Tracks whether the desktop is currently displaying the soft (blurred)
    /// or sharp variant of the wallpaper. Mirrors the per-user
    /// <see cref="UserManager.WallpaperBlurPreferenceKey"/> preference and
    /// is consulted by <see cref="ResolveWallpaperBitmap"/> so the base
    /// <c>DOSIScreen</c> cross-fade machinery picks up the right variant.
    /// Defaults to <c>true</c> so the desktop's first frame matches every
    /// other DOSI screen.
    /// </summary>
    private bool _wallpaperBlurEnabled = true;

    public DesktopScreen()
    {
        // ===== Top taskbar =====
        // Modern 2x2 rounded-tile launcher glyph. Classic "apps" iconography
        // (Windows Start / macOS Launchpad), painted in the accent gradient
        // so it tracks theme changes. Each tile is parented to a UniformGrid
        // host so OnAccentAccentChanged can re-tint all four in one pass.
        var appsTileGrid = new UniformGrid
        {
            Rows = 2,
            Columns = 2,
            Width = 14,
            Height = 14
        };
        for (int i = 0; i < 4; i++)
        {
            appsTileGrid.Children.Add(new Border
            {
                Width = 5,
                Height = 5,
                CornerRadius = new CornerRadius(1.5),
                Background = Accents.AccentGradientBrush,
                Margin = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        _appsButtonAccent = new Border
        {
            Width = 14,
            Height = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Child = appsTileGrid
        };

        _appsButtonLabel = new TextBlock
        {
            Text = "Applications",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center
        };

        _appsButton = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { _appsButtonAccent, _appsButtonLabel }
            }
        };
        _appsButton.PointerEntered += (_, _) =>
        {
            if (!_appsMenuOpen)
                _appsButton.Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
        };
        _appsButton.PointerExited += (_, _) =>
        {
            if (!_appsMenuOpen)
                _appsButton.Background = Brushes.Transparent;
        };
        _appsButton.PointerPressed += OnAppsButtonPressed;

        // Compact clock + date are no longer in the taskbar - they live as an
        // ambient overlay in the bottom-left of the desktop (built below).
        _clockText = new TextBlock
        {
            FontSize = 42,
            FontWeight = FontWeight.Light,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };

        _dateText = new TextBlock
        {
            FontSize = 14,
            Foreground = Brushes.White,
            Opacity = 0.85,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };

        _clockStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            // Margin is recomputed by OnTaskbarPositionChanged whenever
            // the dock edge changes. Initial value here is just the
            // top-dock case; the synchronous OnTaskbarPositionChanged
            // call in AttachedToVisualTree corrects it for bottom dock
            // before the first paint.
            Margin = ComputeClockMargin(),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Spacing = 2,
            Children = { _clockText, _dateText }
        };

        // User chip (mini avatar + display name).
        _userAvatarInitial = new TextBlock
        {
            Text = "?",
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Accents.TextOnAccent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var avatarRing = new Ellipse
        {
            Width = 18,
            Height = 18,
            Fill = Accents.AccentGradientBrush
        };

        _userAvatarChip = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Grid
            {
                Width = 18,
                Height = 18,
                Children = { avatarRing, _userAvatarInitial }
            }
        };

        _taskbarUser = new TextBlock
        {
            Text = string.Empty,
            FontSize = 12,
            Foreground = Accents.TextPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center
        };

        var userStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 6,
            Children = { _userAvatarChip, _taskbarUser }
        };

        var rightStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 14, 0),
            Spacing = 18,
            Children = { BuildNotificationBell(), userStack }
        };

        var taskbarGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto")
        };
        taskbarGrid.Children.Add(_appsButton);
        Grid.SetColumn(_appsButton, 0);
        // Middle column hosts the running-apps strip - one chip per
        // open DOSIWindow. Subscribes to WindowManager events itself,
        // so DesktopScreen doesn't need to wire anything beyond
        // dropping it in. This is also what finally makes the title-bar
        // Minimize button do something visible: minimized windows stay
        // in the strip tinted-down, and clicking the chip restores them.
        var taskbarApps = new DOSI.CORE.UIComponents.WindowManagement.TaskbarAppsStrip();
        taskbarGrid.Children.Add(taskbarApps);
        Grid.SetColumn(taskbarApps, 1);
        taskbarGrid.Children.Add(rightStack);
        Grid.SetColumn(rightStack, 2);

        _taskbar = new Border
        {
            Height = TaskbarHeight,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            // Position-aware: TaskbarMetrics.Position drives whether the
            // bar docks at the top or the bottom of the desktop. The
            // initial value is read at construction; later changes from
            // Settings flow through OnTaskbarPositionChanged below.
            VerticalAlignment = TaskbarMetricsPosition == DOSI.CORE.UIComponents.WindowManagement.TaskbarPosition.Top
                ? VerticalAlignment.Top
                : VerticalAlignment.Bottom,
            Background = BuildTaskbarBackground(),
            BorderBrush = BuildTaskbarBorderBrush(),
            // Border-line goes on the inside edge: bottom for a top
            // dock, top for a bottom dock. Reads as a single 1px
            // separator between chrome and content.
            BorderThickness = TaskbarMetricsPosition == DOSI.CORE.UIComponents.WindowManagement.TaskbarPosition.Top
                ? new Thickness(0, 0, 0, 1)
                : new Thickness(0, 1, 0, 0),
            Child = taskbarGrid,
            // Park off-screen on the same edge we're docking against so
            // the slide-in animation feels like the bar is entering from
            // outside the screen. Top dock => -Height (above), bottom
            // dock => +Height (below). AnimateTaskbarInAsync drives the
            // translation back to 0.
            RenderTransform = _taskbarSlide = new TranslateTransform(0, OffScreenSlideY)
        };

        // ===== Apps menu (popup) =====
        _appsMenuBackdrop = new Border
        {
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsVisible = false
        };
        _appsMenuBackdrop.PointerPressed += (_, _) => CloseAppsMenu();

        _appsMenu = BuildAppsMenu();

        // ===== Bottom-right version label =====
        _versionText = new TextBlock
        {
            Text = "DAX.OSI  v1.0",
            FontSize = 11,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            // Margin recomputed with the dock edge so a bottom taskbar
            // doesn't bury the version label. ComputeVersionMargin
            // mirrors ComputeClockMargin.
            Margin = ComputeVersionMargin()
        };

        _appsMenuTranslate = new TranslateTransform(0, -8);
        _appsMenu.RenderTransform = _appsMenuTranslate;
        _appsMenu.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);

        _layoutRoot = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            // Order matters: backdrop and menu sit above the taskbar so they
            // catch clicks anywhere on the screen. Only elements that MUST
            // render above DOSIWindows live here - the layout root is later
            // re-parented into MainWindow.PopupHost (above the windows canvas).
            Children = { _taskbar, _appsMenuBackdrop, _appsMenu }
        };

        // Ambient decorative layer (clock, date, version) lives in the desktop
        // canvas BELOW windows. With translucent windows enabled, drawing these
        // elements above windows would make them bleed through every window.
        // Keeping them on the desktop layer lets translucent windows obscure
        // them naturally while the wallpaper still shows through window bodies.
        _ambientLayer = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false,
            Children = { _clockStack, _versionText }
        };

        Desktop.Children.Add(_ambientLayer);
        Desktop.Children.Add(_layoutRoot);

        // Desktop icon layer - draggable file/folder tiles mirroring the
        // user's Desktop folder. Inserted AT THE BOTTOM of the desktop
        // overlay so the ambient layer (clock / version) and the popup
        // layer (taskbar, menus) render above it. Tiles set their own
        // ContextMenu so per-tile right-click takes priority over the
        // wallpaper context menu set above.
        //
        // Built BEFORE the wallpaper ContextMenu assignment below so we
        // can route both primary and secondary monitors through the
        // SAME DesktopIconLayer.BuildWallpaperContextMenu - a single
        // implementation that includes Snap-to-grid, Auto-arrange, and
        // per-monitor folder routing for Paste / New folder / Open
        // Files. Previously the primary built its own context menu via
        // BuildDesktopContextMenu (missing Snap + Auto-arrange) while
        // ExtensionScreen used the icon-layer one, so the wallpaper
        // menus differed across monitors.
        _iconLayer = new DesktopIconLayer();
        Desktop.Children.Insert(0, _iconLayer);
        Desktop.ContextMenu = _iconLayer.BuildWallpaperContextMenu();
        Desktop.LayoutUpdated += (_, _) =>
        {
            _layoutRoot.Width = Desktop.Bounds.Width;
            _layoutRoot.Height = Desktop.Bounds.Height;
            _ambientLayer.Width = Desktop.Bounds.Width;
            _ambientLayer.Height = Desktop.Bounds.Height;
        };

        UpdateClock();
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();

        AttachedToVisualTree += (_, _) =>
        {
            Accents.AccentChanged += OnAccentAccentChanged;
            DOSIPublishedAppRegistry.AppsChanged += OnPublishedAppsChanged;
            // Per-user installed apps (DLLs in <UserHome>/Applications/)
            // populate AFTER the desktop first builds its apps menu, because
            // sign-in and AppLoader.LoadForUser are async w.r.t. the visual
            // tree being attached. Without this subscription a freshly
            // installed (or freshly seeded) plug-in only shows up after a
            // sign-out / sign-in cycle - which is exactly the symptom the
            // user reported with the IDE plug-in.
            DOSI.CORE.Apps.LoadedAppRegistry.AppsChanged += OnLoadedAppsChanged;
            DOSI.CORE.Apps.AppLoader.ApplicationsFolderChanged += OnApplicationsFolderChanged;
            DefaultApplications.DOSISettingsScreen.WallpaperBlurChanged += OnWallpaperBlurChanged;
            DOSIWindow.AnyWindowFullScreenChanged += OnAnyWindowFullScreenChanged;
            NotificationHistory.Changed += OnNotificationHistoryChanged;
            // Desktop icons get a farewell animation (per-tile scale +
            // fade) the instant a sign-out or shutdown begins, so the
            // tiles gracefully retract instead of vanishing in a single
            // frame when the chrome unmounts. The same hooks run on every
            // ExtensionScreen so secondary monitors animate their own
            // tiles in lockstep with the primary's.
            SystemSignOut.SignOutStarting += OnSystemFarewellStarting;
            SystemShutdown.ShutdownStarting += OnSystemFarewellStarting;
            // The badge starts un-pulsed even if the previous session left
            // entries in history (e.g. fast user-switch); align the seen
            // count with the current count so we only highlight NEW arrivals.
            _notifLastSeenCount = NotificationHistory.All.Count;
            UpdateNotifBadge();

            // One-time crash recovery toast. The Reporter rotated the file
            // (so we won't re-toast on the next attach), and the toast
            // itself goes through DOSIPopNotification which records it in
            // NotificationHistory - so the user can re-read it later from
            // the bell popover even if they dismissed the toast.
            SurfacePendingCrashOnce();
            // Sync up immediately in case a window is already fullscreen when
            // the desktop attaches (e.g. fast user switch mid-video).
            ApplyFullScreenChromeVisibility(!DOSIWindow.IsAnyWindowFullScreen);
            _clockTimer.Start();

            // Reserve the taskbar's vertical space in this monitor's window
            // manager so no DOSIWindow can be cascaded, dragged, or maximized
            // up under the taskbar. Resolve the owning host so secondary
            // monitors reserve the inset on THEIR manager (not the global
            // one, which belongs to the primary monitor).
            _ownerHost = ResolveOwnerHost();
            // Note: the work-area inset is published below via the
            // synchronous OnTaskbarPositionChanged call - that path knows
            // which side (top or bottom) to write so we don't have to
            // branch here.

            // React to live taskbar-height + position changes from
            // Settings: resize the chrome border, flip alignment +
            // margin direction, re-park any off-screen slide, and
            // re-publish the work-area inset so DOSIWindow maximize +
            // drag-clamp respect the new geometry immediately.
            DOSI.CORE.UIComponents.WindowManagement.TaskbarMetrics.HeightChanged += OnTaskbarHeightChanged;
            DOSI.CORE.UIComponents.WindowManagement.TaskbarMetrics.PositionChanged += OnTaskbarPositionChanged;
            DOSI.CORE.UIComponents.WindowManagement.TaskbarMetrics.ClockPositionChanged += OnClockPositionChanged;

            // Re-apply position-dependent surfaces in case TaskbarMetrics
            // was mutated AFTER our constructor ran (the sign-in pipeline
            // pushes the user's persisted position right before this
            // screen attaches). Cheaper than guarding every visual at
            // construction time against a not-yet-applied preference.
            OnTaskbarPositionChanged(null,
                DOSI.CORE.UIComponents.WindowManagement.TaskbarMetrics.Position);

            // Move the desktop chrome (taskbar / version / apps menu) into the
            // owning host's popup overlay so it always renders above any
            // persistent DOSIWindow living in the host's window overlay.
            // On primary this is MainWindow's _popupOverlay; on secondaries
            // it's the MonitorWindow's _popupOverlay - keeping each desktop's
            // chrome on its OWN monitor.
            var popup = _ownerHost?.PopupHost
                ?? DAX.OSI.MainWindow.PopupHost as Panel;
            if (popup != null)
            {
                if (_layoutRoot.Parent is Panel oldParent)
                    oldParent.Children.Remove(_layoutRoot);
                popup.Children.Add(_layoutRoot);

                void SyncSize(object? _, EventArgs __)
                {
                    _layoutRoot.Width = popup.Bounds.Width;
                    _layoutRoot.Height = popup.Bounds.Height;
                }
                popup.LayoutUpdated += SyncSize;
                SyncSize(null, EventArgs.Empty);
            }

            // Slide the taskbar in from above on first attach. Posted at
            // Loaded priority so it runs AFTER the parent crossfade has
            // started and the popup overlay has measured itself - this way
            // the user sees a clean drop-down even if the desktop is being
            // crossfaded in from the login screen at the same time.
            //
            // Only on the FIRST attach: the screen manager reparents this
            // screen into a transient overlay grid for every crossfade (and
            // back out again), each round-trip firing Attached/Detached.
            // Re-running the slide-in on those re-attaches makes the bar
            // visibly slide back in WHILE the sign-out / shutdown fade is
            // running, which reads as a duplicate animation.
            if (!_taskbarHasSlidIn)
            {
                _taskbarHasSlidIn = true;
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => _ = AnimateTaskbarInAsync(),
                    Avalonia.Threading.DispatcherPriority.Loaded);
            }
        };
        DetachedFromVisualTree += (_, _) =>
        {
            Accents.AccentChanged -= OnAccentAccentChanged;
            DOSIPublishedAppRegistry.AppsChanged -= OnPublishedAppsChanged;
            DOSI.CORE.Apps.LoadedAppRegistry.AppsChanged -= OnLoadedAppsChanged;
            DOSI.CORE.Apps.AppLoader.ApplicationsFolderChanged -= OnApplicationsFolderChanged;
            DefaultApplications.DOSISettingsScreen.WallpaperBlurChanged -= OnWallpaperBlurChanged;
            DOSIWindow.AnyWindowFullScreenChanged -= OnAnyWindowFullScreenChanged;
            NotificationHistory.Changed -= OnNotificationHistoryChanged;
            SystemSignOut.SignOutStarting -= OnSystemFarewellStarting;
            SystemShutdown.ShutdownStarting -= OnSystemFarewellStarting;
            _clockTimer.Stop();
            _appsMenuAnimTimer?.Stop();
            _appsMenuAnimTimer = null;
            _notifAnimTimer?.Stop();
            _notifAnimTimer = null;
            // Don't just stop the slide timer - completing the TCS too is
            // critical so any sign-out / shutdown sequence awaiting the
            // slide-out doesn't deadlock when the crossfade detaches us
            // mid-animation. (Without this, IsSigningOut stays stuck true
            // and every later Sign Out click silently no-ops.)
            CompletePendingTaskbarAnim();

            // Defensive: if we're being torn down while a window is
            // immersive-fullscreen, restore chrome visibility so the next
            // screen we navigate to (sign-out, shutdown) doesn't inherit
            // a hidden taskbar.
            ApplyFullScreenChromeVisibility(true);

            // Release the reserved top inset so screens without a taskbar
            // (login, signout, shutdown) get the full canvas back. Use the
            // monitor's OWN manager (cached at attach time) so we clear the
            // inset on the right canvas in a multi-monitor setup.
            var detachWm = _ownerHost?.WindowManager ?? WindowManager.Instance;
            if (detachWm != null)
                detachWm.TopWorkAreaInset = 0;
                detachWm.BottomWorkAreaInset = 0;

            DOSI.CORE.UIComponents.WindowManagement.TaskbarMetrics.HeightChanged -= OnTaskbarHeightChanged;
            DOSI.CORE.UIComponents.WindowManagement.TaskbarMetrics.PositionChanged -= OnTaskbarPositionChanged;
            DOSI.CORE.UIComponents.WindowManagement.TaskbarMetrics.ClockPositionChanged -= OnClockPositionChanged;

            // Pull desktop chrome back out of the popup overlay so it doesn't
            // leak when this DesktopScreen instance is removed. Resolve via
            // the cached host so secondary monitors clean up THEIR popup
            // layer, not the primary's.
            var detachPopup = _ownerHost?.PopupHost
                ?? DAX.OSI.MainWindow.PopupHost as Panel;
            if (detachPopup != null && _layoutRoot.Parent == detachPopup)
            {
                detachPopup.Children.Remove(_layoutRoot);
            }
            _ownerHost = null;
        };
    }

    public override void OnNavigatedTo()
    {
        base.OnNavigatedTo();

        UpdateUserChip(UserManager.CurrentUser);

        // Apply the user's saved DOSIWindow opacity preference so newly-opened
        // (and currently-open) windows use the configured translucency.
        DOSIWindow.WindowOpacity = UserManager.CurrentUser != null
            ? UserManager.GetUserWindowOpacity(UserManager.CurrentUser)
            : UserManager.DefaultWindowOpacity;

        // Apply the user's saved wallpaper-blur preference. We only update
        // the field here - the actual cross-fade between the soft and
        // sharp variants is kicked off by DOSIScreen.OnTransitionComplete
        // AFTER the screen-level cross-fade settles and our backdrop is
        // visible. Doing the swap here would play the wallpaper animation
        // INVISIBLY behind the hidden backdrop, making the change look
        // like an instant snap when the desktop appears. Login,
        // sign-out, shutdown, and the setup wizard intentionally ignore
        // this preference and always render the soft variant.
        _wallpaperBlurEnabled = UserManager.CurrentUser == null ||
                                UserManager.GetUserWallpaperBlur(UserManager.CurrentUser);

        NotifyScreenReady();

        // Signal secondary monitors (ExtensionScreen) that the primary
        // desktop has finished navigating in. They use this to defer
        // their own chrome (icon layer rebuild + visual taskbar slide)
        // until the primary is visible - without this gate the secondary
        // monitors render their tiles + taskbar a few hundred ms BEFORE
        // the primary's crossfade completes, which reads as "the side
        // monitors loaded first". Sticky flag (PrimaryDesktopReady) so
        // a secondary attaching AFTER the primary already signalled
        // still gets the green light.
        PrimaryDesktopReady = true;
        PrimaryDesktopReadyChanged?.Invoke(null, true);

        // Show welcome toast once the desktop is up.
        Dispatcher.UIThread.Post(() =>
        {
            var name = UserManager.CurrentUser?.DisplayName
                       ?? UserManager.CurrentUser?.Username
                       ?? "User";
            DOSIPopNotification.Show($"Welcome, {name}");
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Returns the wallpaper bitmap variant the desktop should currently
    /// show. Honors the per-user wallpaper-blur preference so toggling it
    /// in Settings cross-fades between the soft and sharp variants of the
    /// same underlying wallpaper. Falls back to the soft variant whenever
    /// the preference is enabled (or no preference is known).
    /// </summary>
    protected override Avalonia.Media.Imaging.Bitmap? ResolveWallpaperBitmap()
    {
        return WallpaperManager.Instance.GetCurrentBitmap(blurred: _wallpaperBlurEnabled);
    }

    /// <summary>
    /// Live-applies the wallpaper-blur preference when the user toggles it
    /// in Settings. Persists the change to the active user (already done
    /// by the Settings handler) and animates a cross-fade between the
    /// blurred and sharp variants of the same wallpaper.
    /// </summary>
    private void OnWallpaperBlurChanged(object? sender, bool enabled)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_wallpaperBlurEnabled == enabled) return;
            _wallpaperBlurEnabled = enabled;
            RefreshWallpaper();
        });
    }

    /// <summary>
    /// Fires from <see cref="SystemSignOut.SignOutStarting"/> and
    /// <see cref="SystemShutdown.ShutdownStarting"/> the instant a session
    /// teardown begins. Plays the per-tile pop-out animation across every
    /// icon currently on the primary desktop so the user sees the icons
    /// gracefully retract during the sign-out / shutdown crossfade, rather
    /// than the layer snapping to invisible when the chrome unmounts.
    /// </summary>
    private void OnSystemFarewellStarting()
    {
        // The event fires synchronously on whichever thread invoked
        // Begin(); marshal to the UI thread defensively.
        Dispatcher.UIThread.Post(() =>
        {
            // Fire-and-forget: the sign-out / shutdown flow runs its own
            // chrome retraction animations in parallel, and the tile
            // pop-out completes well within the same crossfade window.
            _ = _iconLayer?.AnimateAllTilesOutAsync();
        });
    }

    // =====================================================================
    // Apps menu
    // =====================================================================

    private Border BuildAppsMenu()
    {
        _appsMenuSearch = new DOSITextBox
        {
            PlaceholderText = "Search apps",
            FontSize = 13,
            Margin = new Thickness(2, 2, 2, 6),
            Height = 32
        };
        // Live filter as the user types. Up/Down/Enter handled separately
        // (see ApplyAppsMenuKey) for keyboard navigation.
        _appsMenuSearch.PropertyChanged += (_, e) =>
        {
            if (e.Property == DOSITextBox.TextProperty)
            {
                _appsMenuFilter = (_appsMenuSearch.Text ?? string.Empty).Trim();
                RebuildAppsMenuItems();
            }
        };

        _appsMenuItems = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 2
        };

        RebuildAppsMenuItems();

        var contentStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Children = { _appsMenuSearch, _appsMenuItems }
        };

        var menuBorder = new Border
        {
            Width = 280,
            HorizontalAlignment = HorizontalAlignment.Left,
            // Anchor to the same edge the taskbar lives on so the menu
            // appears to grow OUT of the apps button.
            VerticalAlignment = TaskbarMetricsPosition == DOSI.CORE.UIComponents.WindowManagement.TaskbarPosition.Top
                ? VerticalAlignment.Top
                : VerticalAlignment.Bottom,
            // Top-dock: leave room below the taskbar. Bottom-dock: leave
            // room above the taskbar. Margin's 4-tuple is (L,T,R,B).
            Margin = TaskbarMetricsPosition == DOSI.CORE.UIComponents.WindowManagement.TaskbarPosition.Top
                ? new Thickness(8, TaskbarHeight + 4, 0, 0)
                : new Thickness(8, 0, 0, TaskbarHeight + 4),
            Padding = new Thickness(8),
            Background = BuildMenuBackground(),
            BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0,
                OffsetY = 14,
                Blur = 36,
                Spread = 0,
                Color = Color.FromArgb(150, 0, 0, 0)
            }),
            Opacity = 0,
            IsVisible = false,
            Child = contentStack
        };

        // Enter on the search box launches the first visible match. We
        // attach this here (after _appsMenu is conceptually live) rather
        // than in the search box ctor because the launcher needs to walk
        // the items list.
        _appsMenuSearch.KeyDown += OnAppsMenuSearchKey;

        return menuBorder;
    }

    /// <summary>
    /// Routes keyboard events from the apps-menu search box. Enter launches
    /// the first visible tile (the most-likely match), Escape closes the
    /// menu without launching anything.
    /// </summary>
    private void OnAppsMenuSearchKey(object? sender, KeyEventArgs e)
    {
        if (_appsMenuItems == null) return;
        if (e.Key == Key.Escape) { CloseAppsMenu(); e.Handled = true; return; }
        if (e.Key != Key.Enter) return;

        // Find the first row in the rendered list and trigger it. Section
        // headers are TextBlocks (not Borders); rows are Borders with a
        // captured launcher in their PointerPressed handler.
        var firstRow = _appsMenuItems.Children
            .OfType<Border>()
            .Select(b => b.Tag as Action)
            .FirstOrDefault(a => a != null);
        if (firstRow != null)
        {
            e.Handled = true;
            CloseAppsMenu();
            // Normal priority - see BuildAppsMenuItem for the rationale
            // (Background priority can get starved by wallpaper sync
            // broadcast ticks and the menu-close tween).
            Dispatcher.UIThread.Post(firstRow, DispatcherPriority.Normal);
        }
    }

    /// <summary>
    /// (Re)builds the contents of the Applications menu. Called once at startup,
    /// every time the menu opens, every keystroke in the search box, and
    /// whenever the published-app registry changes.
    /// <para>
    /// Honours <c>_appsMenuFilter</c>: when non-empty, items whose Title or
    /// Description don't contain the filter (case-insensitive) are hidden,
    /// along with section headers that end up empty.
    /// </para>
    /// </summary>
    private void RebuildAppsMenuItems()
    {
        if (_appsMenuItems == null) return;
        var items = _appsMenuItems;
        items.Children.Clear();

        var filter = _appsMenuFilter;
        bool MatchesFilter(string title, string subtitle) =>
            string.IsNullOrEmpty(filter) ||
            title.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            subtitle.Contains(filter, StringComparison.OrdinalIgnoreCase);

        // ----- Built-ins -----
        var builtIns = new (string Title, string Subtitle, string GlyphKey, Action Launch)[]
        {
            ("Terminal", "Command-line shell", "terminal", () => LaunchApplication(new DOSITerminal())),
            ("Browser",  "Browse the web",     "browser",  () => LaunchApplication(new DOSIWebBrowser())),
            ("Files",    "Browse your files",  "files",    () => LaunchApplication(new DOSIFileExplorer())),
            ("Image Viewer", "View pictures and screenshots", "image", () => LaunchApplication(new DOSIImageViewer())),
            ("Steam",    "Browse your Steam library", "steam", () => LaunchApplication(new DOSISteamApp())),
        };
        var visibleBuiltIns = builtIns.Where(b => MatchesFilter(b.Title, b.Subtitle)).ToList();
        if (visibleBuiltIns.Count > 0)
        {
            items.Children.Add(BuildAppsMenuHeader("Applications"));
            foreach (var b in visibleBuiltIns)
                items.Children.Add(BuildAppsMenuItem(b.Title, b.Subtitle, GetBuiltInGlyph(b.GlyphKey), b.Launch));
        }

        // ----- Per-user installed applications -----
        var pluginApps = LoadedAppRegistry.All
            .Where(a => MatchesFilter(a.Title, a.Description))
            .ToList();
        foreach (var pluginApp in pluginApps)
        {
            var captured = pluginApp;
            items.Children.Add(BuildAppsMenuItem(
                captured.Title,
                captured.Description,
                captured.BuildGlyph(),
                () =>
                {
                    CloseAppsMenu();
                    if (captured.Activate() is DOSIWindow w)
                        LaunchApplication(w);
                }));
        }

        // ----- Published apps -----
        var published = DOSIPublishedAppRegistry.GetAll(UserManager.CurrentUser)
            .Where(p => MatchesFilter(p.Name, p.Description ?? string.Empty))
            .ToList();
        if (published.Count > 0)
        {
            if (items.Children.Count > 0) items.Children.Add(BuildMenuDivider());
            items.Children.Add(BuildAppsMenuHeader("Published"));
            foreach (var app in published)
            {
                var captured = app;
                items.Children.Add(BuildAppsMenuItem(
                    captured.Name,
                    captured.Description ?? "Published DOSI app",
                    BuildPublishedAppGlyph(),
                    () =>
                    {
                        CloseAppsMenu();
                        DOSIPublishedAppLauncher.Launch(captured);
                    }));
            }
        }

        // ----- System -----
        var systemItems = new (string Title, string Subtitle, Func<Control> Glyph, Action Launch)[]
        {
            ("Settings", "Personalize and configure DOSI", () => GetBuiltInGlyph("settings"), () => LaunchApplication(new DOSISettingsScreen())),
            ("Application Manager", "Uninstall installed apps", () => GetBuiltInGlyph("appmgr"),  () => LaunchApplication(new DeregisterApplicationScreen())),
            ("Sign out", "Switch user / lock session",     () => GetBuiltInGlyph("signout"),  () => SystemSignOut.Begin()),
            ("Shutdown", "Power off DAX.OSI",              () => GetBuiltInGlyph("shutdown"), () => SystemShutdown.Begin(0)),
        };
        var visibleSystem = systemItems.Where(s => MatchesFilter(s.Title, s.Subtitle)).ToList();
        if (visibleSystem.Count > 0)
        {
            if (items.Children.Count > 0) items.Children.Add(BuildMenuDivider());
            items.Children.Add(BuildAppsMenuHeader("System"));
            foreach (var s in visibleSystem)
                items.Children.Add(BuildAppsMenuItem(s.Title, s.Subtitle, s.Glyph(), s.Launch));
        }

        // Empty state - shown only when the filter excludes everything.
        if (items.Children.Count == 0 && !string.IsNullOrEmpty(filter))
        {
            items.Children.Add(new TextBlock
            {
                Text = $"No matches for \u201c{filter}\u201d",
                FontSize = 12,
                Foreground = Accents.TextSecondaryBrush,
                Opacity = 0.7,
                Margin = new Thickness(10, 14, 10, 14),
                HorizontalAlignment = HorizontalAlignment.Center
            });
        }
    }

    /// <summary>
    /// Returns the cached glyph control for a built-in app, building it on
    /// first request. Guarantees the same Control instance across menu
    /// rebuilds, which matters because Avalonia controls can only have one
    /// parent - if a glyph were ever returned to two different menu rebuilds
    /// without being detached first the second insertion would throw. The
    /// cache always returns the same instance, but BuildAppsMenuItem wraps
    /// every glyph in a fresh Border parent so successive Children.Clear()
    /// + re-add cycles work cleanly (the glyph's parent on re-add is the
    /// new wrapper, not the old one).
    /// </summary>
    private Control GetBuiltInGlyph(string key)
    {
        if (_builtInGlyphCache.TryGetValue(key, out var cached)) return cached;
        Control built = key switch
        {
            "terminal" => BuildTerminalGlyph(),
            "browser"  => BuildBrowserGlyph(),
            "files"    => BuildFilesGlyph(),
            "image"    => BuildImageViewerGlyph(),
            "steam"    => BuildSteamGlyph(),
            "settings" => BuildSettingsGlyph(),
            "appmgr"   => BuildAppManagerGlyph(),
            "signout"  => BuildSignOutGlyph(),
            "shutdown" => BuildShutdownGlyph(),
            _ => new Border()
        };
        _builtInGlyphCache[key] = built;
        return built;
    }

    private static Border BuildMenuDivider() => new()
    {
        Height = 1,
        Margin = new Thickness(8, 6, 8, 6),
        Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255))
    };

    private void OnPublishedAppsChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(RebuildAppsMenuItems);
    }

    private void OnLoadedAppsChanged(object? sender, EventArgs e)
    {
        // Same pattern as the published-apps refresh - re-render the menu on
        // the UI thread when AppLoader (de)registers a per-user plug-in.
        Dispatcher.UIThread.Post(RebuildAppsMenuItems);
    }

    /// <summary>
    /// Hides desktop chrome (taskbar + apps menu host + ambient clock /
    /// version label) while any DOSIWindow is in immersive fullscreen, and
    /// restores it on exit. Without this the taskbar - which lives in
    /// MainWindow.PopupHost above the windows canvas - would cover the
    /// top of a fullscreened YouTube video.
    /// </summary>
    private void OnAnyWindowFullScreenChanged(object? sender, bool fullscreen)
    {
        Dispatcher.UIThread.Post(() => ApplyFullScreenChromeVisibility(!fullscreen));
    }

    private void ApplyFullScreenChromeVisibility(bool chromeVisible)
    {
        // Close the apps menu if it's open - leaving it dangling on top of
        // a fullscreened video would defeat the purpose.
        if (!chromeVisible && _appsMenuOpen)
            CloseAppsMenu();

        _layoutRoot.IsVisible = chromeVisible;
        _ambientLayer.IsVisible = chromeVisible;
    }

    private static Control BuildPublishedAppGlyph() => new Border
    {
        Width = 26,
        Height = 26,
        CornerRadius = new CornerRadius(6),
        Background = new SolidColorBrush(Accents.AccentPrimary),
        Margin = new Thickness(0, 0, 12, 0),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        Child = new TextBlock
        {
            Text = "\u2756",
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Accents.TextOnAccent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    private static Control BuildAppsMenuHeader(string text) => new TextBlock
    {
        Text = text,
        FontSize = 10,
        FontWeight = FontWeight.SemiBold,
        Foreground = Accents.TextSecondaryBrush,
        Opacity = 0.75,
        Margin = new Thickness(10, 6, 10, 8),
        TextAlignment = TextAlignment.Left
    };

    private Control BuildAppsMenuItem(string title, string subtitle, Control glyph, Action onSelected)
    {
        // Cached glyphs (built-in apps) are reused across menu rebuilds.
        // Avalonia controls can have at most one parent, so detach from
        // any previous wrapper Border before re-parenting into this row.
        if (glyph.Parent is ContentControl prevContent)
            prevContent.Content = null;
        else if (glyph.Parent is Panel prevPanel)
            prevPanel.Children.Remove(glyph);
        else if (glyph.Parent is Decorator prevDecorator)
            prevDecorator.Child = null;

        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center
        };

        var subtitleText = new TextBlock
        {
            Text = subtitle,
            FontSize = 11,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.85,
            VerticalAlignment = VerticalAlignment.Center
        };

        var textStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 1,
            Children = { titleText, subtitleText }
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            VerticalAlignment = VerticalAlignment.Center
        };
        grid.Children.Add(glyph);
        Grid.SetColumn(glyph, 0);
        grid.Children.Add(textStack);
        Grid.SetColumn(textStack, 1);

        var row = new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            // Tag stores the launcher so the search box's Enter key can
            // invoke the first visible row without re-walking the menu
            // construction.
            Tag = onSelected,
            Child = grid
        };

        row.PointerEntered += (_, _) =>
            row.Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
        row.PointerExited += (_, _) =>
            row.Background = Brushes.Transparent;
        row.PointerPressed += (_, e) =>
        {
            e.Handled = true; // don't bubble to backdrop
            CloseAppsMenu();
            // Defer launch so the close animation can begin first.
            //
            // Use Normal priority (not Background) - Background sits
            // BELOW the dispatcher timers that drive our wallpaper-sync
            // broadcast and any in-flight transition animations. While
            // the apps-menu close tween or a wallpaper sync tick is
            // running at Normal, a Background post can stay queued
            // indefinitely - the user clicks Sign Out, the menu closes,
            // but SystemSignOut.Begin is never invoked. Posting at
            // Normal puts the launcher in the same priority lane as the
            // close tween so it runs immediately after.
            Dispatcher.UIThread.Post(onSelected, DispatcherPriority.Normal);
        };

        return row;
    }

    private static Control BuildTerminalGlyph() => new Border
    {
        Width = 26,
        Height = 26,
        CornerRadius = new CornerRadius(6),
        Background = new SolidColorBrush(Color.FromRgb(20, 20, 26)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
        BorderThickness = new Thickness(1),
        Margin = new Thickness(0, 0, 12, 0),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        Child = new TextBlock
        {
            Text = ">_",
            FontFamily = new FontFamily("Consolas"),
            FontWeight = FontWeight.Bold,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 210)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    private static Control BuildBrowserGlyph() => new Border
    {
        Width = 26,
        Height = 26,
        CornerRadius = new CornerRadius(13),
        Background = Accents.AccentGradientBrush,
        Margin = new Thickness(0, 0, 12, 0),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        Child = new TextBlock
        {
            Text = "\u25CC", // dotted circle - simple "globe" stand-in
            FontSize = 16,
            Foreground = new SolidColorBrush(Accents.TextOnAccent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    private static Control BuildFilesGlyph()
    {
        var tab = new Border
        {
            Width = 14,
            Height = 4,
            CornerRadius = new CornerRadius(1, 1, 0, 0),
            Background = Accents.AccentGradientBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(2, 4, 0, 0)
        };

        var body = new Border
        {
            Width = 22,
            Height = 16,
            CornerRadius = new CornerRadius(2, 4, 4, 4),
            Background = Accents.AccentGradientBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 2)
        };

        var grid = new Grid
        {
            Width = 26,
            Height = 26,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        grid.Children.Add(tab);
        grid.Children.Add(body);
        return grid;
    }

    private static Control BuildImageViewerGlyph()
    {
        // Mountain + sun pictogram - matches the icon used in the viewer's
        // title bar so the menu entry is visually self-identifying.
        var border = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromRgb(40, 80, 140)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            ClipToBounds = true
        };

        var canvas = new Canvas { Width = 26, Height = 26 };
        // Sun
        canvas.Children.Add(new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = new SolidColorBrush(Color.FromRgb(255, 220, 110)),
            [Canvas.LeftProperty] = 16d,
            [Canvas.TopProperty] = 4d
        });
        // Mountain silhouette
        canvas.Children.Add(new Avalonia.Controls.Shapes.Polygon
        {
            Points = new Avalonia.Collections.AvaloniaList<Point>
            {
                new(0, 22), new(9, 11), new(15, 17), new(22, 8), new(26, 11), new(26, 26), new(0, 26)
            },
            Fill = new SolidColorBrush(Color.FromRgb(220, 230, 240))
        });
        border.Child = canvas;
        return border;
    }

    private static Control BuildSteamGlyph() => new Border
    {
        Width = 26,
        Height = 26,
        CornerRadius = new CornerRadius(13),
        Background = Accents.AccentGradientBrush,
        Margin = new Thickness(0, 0, 12, 0),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        Child = new TextBlock
        {
            Text = "S",
            FontFamily = new FontFamily("Consolas"),
            FontWeight = FontWeight.Bold,
            FontSize = 14,
            Foreground = new SolidColorBrush(Accents.TextOnAccent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    private static Control BuildShutdownGlyph() => new Border
    {
        Width = 26,
        Height = 26,
        CornerRadius = new CornerRadius(13),
        Background = new SolidColorBrush(Color.FromRgb(60, 18, 18)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(180, 240, 90, 90)),
        BorderThickness = new Thickness(1),
        Margin = new Thickness(0, 0, 12, 0),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        Child = new TextBlock
        {
            Text = "\u23FB", // power symbol
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(255, 150, 150)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    private static Control BuildSignOutGlyph() => new Border
    {
        Width = 26,
        Height = 26,
        CornerRadius = new CornerRadius(13),
        Background = Accents.AccentGradientBrush,
        BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
        BorderThickness = new Thickness(1),
        Margin = new Thickness(0, 0, 12, 0),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        Child = new TextBlock
        {
            Text = "\u21AA", // rightwards arrow with hook - "leave"
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Accents.TextOnAccent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    private static Control BuildSettingsGlyph() => new Border
    {
        Width = 26,
        Height = 26,
        CornerRadius = new CornerRadius(6),
        Background = Accents.AccentGradientBrush,
        Margin = new Thickness(0, 0, 12, 0),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        Child = new TextBlock
        {
            Text = "\u2699", // gear
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Accents.TextOnAccent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    private static Control BuildAppManagerGlyph() => new Border
    {
        Width = 26,
        Height = 26,
        CornerRadius = new CornerRadius(6),
        Background = new SolidColorBrush(Color.FromRgb(60, 18, 18)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(180, 240, 90, 90)),
        BorderThickness = new Thickness(1),
        Margin = new Thickness(0, 0, 12, 0),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        Child = new TextBlock
        {
            Text = "\u2715", // multiplication X - "remove"
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(255, 170, 170)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    private void OnAppsButtonPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        if (_appsMenuOpen)
            CloseAppsMenu();
        else
            OpenAppsMenu();
    }

    private void OpenAppsMenu()
    {
        if (_appsMenuOpen) return;
        _appsMenuOpen = true;

        // Reset the type-to-search filter so the menu always opens on the
        // full list. Suppress the implicit Rebuild from the property change
        // by setting the field first - the next Rebuild call below covers it.
        _appsMenuFilter = string.Empty;
        if (_appsMenuSearch != null) _appsMenuSearch.Text = string.Empty;

        // Refresh in case the user just published / unpublished an app while
        // this DesktopScreen was alive.
        RebuildAppsMenuItems();

        _appsButton.Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
        _appsMenuBackdrop.IsVisible = true;
        _appsMenu.IsVisible = true;

        // Focus the search box so the user can immediately type-to-find
        // without clicking. Posted at Background priority so the menu's
        // visibility / animation start has a chance to commit a layout
        // pass first; without that, FocusManager has nowhere to put focus.
        Dispatcher.UIThread.Post(() => _appsMenuSearch?.Focus(), DispatcherPriority.Background);

        // Hide every native WebView surface so the menu isn't occluded by
        // the OS-level browser composition (airspace problem). Restored in
        // CloseAppsMenu below.
        DAX.OSI.Controls.WebViewWrapper.SetAllPaused(true);

        AnimateAppsMenu(opening: true);
    }

    private void CloseAppsMenu()
    {
        if (!_appsMenuOpen) return;
        _appsMenuOpen = false;
        _appsButton.Background = Brushes.Transparent;
        DAX.OSI.Controls.WebViewWrapper.SetAllPaused(false);
        AnimateAppsMenu(opening: false);
    }

    private void AnimateAppsMenu(bool opening)
    {
        const double duration = 180;
        // Slide direction matches the dock: top dock slides menu IN from
        // above (-8 -> 0), bottom dock slides it IN from below (+8 -> 0).
        // Closing reverses. Without this flip, a bottom-docked menu
        // would slide UP off the screen on close, away from the
        // direction the user clicked.
        double startOffset = TaskbarMetricsPosition == DOSI.CORE.UIComponents.WindowManagement.TaskbarPosition.Top
            ? -8
            :  8;

        var startOpacity = _appsMenu.Opacity;
        var targetOpacity = opening ? 1.0 : 0.0;
        var startY = _appsMenuTranslate.Y;
        var targetY = opening ? 0.0 : startOffset;

        var startTime = DateTime.UtcNow;

        _appsMenuAnimTimer?.Stop();
        _appsMenuAnimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _appsMenuAnimTimer.Tick += (_, _) =>
        {
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            var t = Math.Clamp(elapsed / duration, 0d, 1d);
            var eased = opening
                ? 1 - Math.Pow(1 - t, 3)   // ease-out for opening
                : t * t;                   // ease-in for closing

            _appsMenu.Opacity = startOpacity + (targetOpacity - startOpacity) * eased;
            _appsMenuTranslate.Y = startY + (targetY - startY) * eased;

            if (t >= 1d)
            {
                _appsMenuAnimTimer?.Stop();
                _appsMenuAnimTimer = null;

                if (!opening)
                {
                    _appsMenu.IsVisible = false;
                    _appsMenuBackdrop.IsVisible = false;
                }
            }
        };
        _appsMenuAnimTimer.Start();
    }

    // =====================================================================
    // Notification Center
    // =====================================================================

    /// <summary>
    /// Builds the bell affordance shown on the right side of the taskbar.
    /// Click toggles the popover; the badge counter updates as new toasts
    /// arrive while the popover is closed (handled in
    /// <see cref="OnNotificationHistoryChanged"/>).
    /// </summary>
    private Control BuildNotificationBell()
    {
        var glyph = new TextBlock
        {
            Text = "\uD83D\uDD14", // bell
            FontSize = 13,
            Foreground = Accents.TextPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        _notifBadgeText = new TextBlock
        {
            Text = "0",
            FontSize = 9,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Accents.TextOnAccent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _notifBadge = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(Color.FromRgb(232, 90, 90)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -4, -4, 0),
            IsVisible = false,
            Child = _notifBadgeText
        };

        var grid = new Grid
        {
            Width = 22,
            Height = 22,
            Children = { glyph, _notifBadge }
        };

        _notifBell = new Border
        {
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
            Child = grid
        };

        _notifBell.PointerEntered += (_, _) =>
            _notifBell.Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
        _notifBell.PointerExited += (_, _) =>
            _notifBell.Background = Brushes.Transparent;
        _notifBell.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            ToggleNotifPopover();
        };

        return _notifBell;
    }

    /// <summary>
    /// Builds the notification popover (anchored under the bell). Lives in
    /// the desktop overlay so it floats above every <c>DOSIWindow</c>.
    /// Constructed lazily on first open so a session that never opens it
    /// never builds the panel.
    /// </summary>
    private void EnsureNotifPopover()
    {
        if (_notifPopover != null) return;

        _notifList = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 6
        };

        var scroller = new DOSIScrollViewer
        {
            Content = _notifList,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 380
        };

        // ----- Header: bell glyph + title + Clear-all -----
        var headerGlyph = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(6),
            Background = Accents.AccentGradientBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "\uD83D\uDD14",
                FontSize = 12,
                Foreground = new SolidColorBrush(Accents.TextOnAccent),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        _notifPopoverTitle = new TextBlock
        {
            Text = "Notifications",
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };

        var titleStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { headerGlyph, _notifPopoverTitle }
        };

        var clearButton = new DOSIButton
        {
            Text = "Clear all",
            FontSize = 11,
            Height = 26,
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(12, 0)
        };
        clearButton.Click += (_, _) =>
        {
            NotificationHistory.Clear();
            RebuildNotifList();
        };

        var headerBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(2, 0, 2, 6)
        };
        headerBar.Children.Add(titleStack); Grid.SetColumn(titleStack, 0);
        headerBar.Children.Add(clearButton); Grid.SetColumn(clearButton, 1);

        // Accent-tinted divider so the header visually separates from the
        // list - same trick the apps menu uses between sections.
        var divider = new Border
        {
            Height = 1,
            Margin = new Thickness(2, 0, 2, 8),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 0),
                    new GradientStop(Color.FromArgb(70, Accents.AccentPrimary.R, Accents.AccentPrimary.G, Accents.AccentPrimary.B), 0.5),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 1)
                }
            }
        };

        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Children = { headerBar, divider, scroller }
        };

        _notifPopoverTranslate = new TranslateTransform(0, -8);

        _notifPopover = new Border
        {
            Width = 340,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = TaskbarMetricsPosition == DOSI.CORE.UIComponents.WindowManagement.TaskbarPosition.Top
                ? VerticalAlignment.Top
                : VerticalAlignment.Bottom,
            Margin = TaskbarMetricsPosition == DOSI.CORE.UIComponents.WindowManagement.TaskbarPosition.Top
                ? new Thickness(0, TaskbarHeight + 4, 8, 0)
                : new Thickness(0, 0, 8, TaskbarHeight + 4),
            Padding = new Thickness(10),
            Background = BuildMenuBackground(),
            BorderBrush = new SolidColorBrush(Accents.AccentSecondary),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0,
                OffsetY = 14,
                Blur = 36,
                Spread = 0,
                Color = Color.FromArgb(150, 0, 0, 0)
            }),
            Opacity = 0,
            IsVisible = false,
            Child = stack,
            RenderTransform = _notifPopoverTranslate,
            RenderTransformOrigin = new RelativePoint(1, 0, RelativeUnit.Relative)
        };

        // Full-screen backdrop that swallows clicks anywhere outside the
        // popover so click-anywhere-to-close works just like the apps
        // menu. MUST live in _layoutRoot (a Grid) - parenting into the
        // bare Canvas Desktop ignores Stretch alignment and the backdrop
        // ends up 0x0.
        _notifPopoverBackdrop = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.Transparent,
            IsHitTestVisible = true,
            IsVisible = false
        };
        _notifPopoverBackdrop.PointerPressed += (_, _) => CloseNotifPopover();

        // Add to _layoutRoot so HorizontalAlignment.Right (popover) and
        // HorizontalAlignment.Stretch (backdrop) are honoured. Order
        // matters: backdrop UNDER the popover so clicks ON the popover
        // hit the popover first and don't dismiss the panel.
        _layoutRoot.Children.Add(_notifPopoverBackdrop);
        _layoutRoot.Children.Add(_notifPopover);
    }

    private void ToggleNotifPopover()
    {
        EnsureNotifPopover();
        if (_notifPopoverOpen) CloseNotifPopover();
        else OpenNotifPopover();
    }

    private void OpenNotifPopover()
    {
        if (_notifPopover == null || _notifPopoverBackdrop == null) return;
        _notifPopoverOpen = true;
        RebuildNotifList();

        _notifPopoverBackdrop.IsVisible = true;
        _notifPopover.IsVisible = true;

        // Mark every current entry as seen so the badge clears.
        _notifLastSeenCount = NotificationHistory.All.Count;
        UpdateNotifBadge();

        AnimateNotifPopover(opening: true);
    }

    private void CloseNotifPopover()
    {
        if (_notifPopover == null || _notifPopoverBackdrop == null) return;
        _notifPopoverOpen = false;
        AnimateNotifPopover(opening: false);
    }

    /// <summary>
    /// Slide-and-fade tween cloned from <see cref="AnimateAppsMenu"/> so
    /// the notification popover feels identical to the apps menu - same
    /// 180 ms duration, same ease-out-cubic open / ease-in close, same
    /// 8 px slide offset (here from the top, since the popover anchors
    /// to the top-right corner under the bell).
    /// </summary>
    private void AnimateNotifPopover(bool opening)
    {
        if (_notifPopover == null || _notifPopoverBackdrop == null || _notifPopoverTranslate == null) return;

        const double duration = 180;
        // Slide direction matches the dock (same logic as AnimateAppsMenu).
        double startOffset = TaskbarMetricsPosition == DOSI.CORE.UIComponents.WindowManagement.TaskbarPosition.Top
            ? -8
            :  8;

        var startOpacity = _notifPopover.Opacity;
        var targetOpacity = opening ? 1.0 : 0.0;
        var startY = _notifPopoverTranslate.Y;
        var targetY = opening ? 0.0 : startOffset;

        var startTime = DateTime.UtcNow;

        _notifAnimTimer?.Stop();
        _notifAnimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _notifAnimTimer.Tick += (_, _) =>
        {
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            var t = Math.Clamp(elapsed / duration, 0d, 1d);
            var eased = opening
                ? 1 - Math.Pow(1 - t, 3)   // ease-out for opening
                : t * t;                   // ease-in for closing

            _notifPopover.Opacity = startOpacity + (targetOpacity - startOpacity) * eased;
            _notifPopoverTranslate.Y = startY + (targetY - startY) * eased;

            if (t >= 1d)
            {
                _notifAnimTimer?.Stop();
                _notifAnimTimer = null;

                if (!opening)
                {
                    _notifPopover.IsVisible = false;
                    _notifPopoverBackdrop.IsVisible = false;
                }
            }
        };
        _notifAnimTimer.Start();
    }

    private void RebuildNotifList()
    {
        if (_notifList == null) return;
        _notifList.Children.Clear();

        var entries = NotificationHistory.All;
        if (entries.Count == 0)
        {
            // Polished empty state - large faded bell glyph + helper line,
            // visually distinct from a populated list so the user knows
            // it's intentional and not a load failure.
            var emptyGlyph = new TextBlock
            {
                Text = "\uD83D\uDD15", // bell with slash
                FontSize = 32,
                Foreground = Accents.TextSecondaryBrush,
                Opacity = 0.45,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var emptyTitle = new TextBlock
            {
                Text = "You're all caught up",
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Foreground = Accents.TextPrimaryBrush,
                Opacity = 0.85,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 4)
            };
            var emptySub = new TextBlock
            {
                Text = "New notifications will appear here.",
                FontSize = 11,
                Foreground = Accents.TextSecondaryBrush,
                Opacity = 0.65,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            _notifList.Children.Add(new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 28, 0, 28),
                Children = { emptyGlyph, emptyTitle, emptySub }
            });
            return;
        }

        var now = DateTime.Now;
        foreach (var rec in entries)
        {
            _notifList.Children.Add(BuildNotifRow(rec, now));
        }
    }

    private Control BuildNotifRow(NotificationRecord rec, DateTime nowLocal)
    {
        // Accent-coloured indicator dot - mirrors macOS / iOS notification
        // styling and gives the row a strong visual anchor that picks up
        // the user's accent for free.
        var accentDot = new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = Accents.AccentGradientBrush,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 10, 0)
        };

        var body = new TextBlock
        {
            Text = rec.Text,
            FontSize = 12.5,
            Foreground = Accents.TextPrimaryBrush,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 17
        };

        var ts = new TextBlock
        {
            Text = FormatRelative(nowLocal - rec.WhenLocal),
            FontSize = 10,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.7,
            Margin = new Thickness(0, 4, 0, 0)
        };

        var textStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Children = { body, ts }
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*")
        };
        grid.Children.Add(accentDot); Grid.SetColumn(accentDot, 0);
        grid.Children.Add(textStack); Grid.SetColumn(textStack, 1);

        var idle = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
        var hover = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255));

        var row = new Border
        {
            Padding = new Thickness(12, 10),
            CornerRadius = new CornerRadius(8),
            Background = idle,
            BorderBrush = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Child = grid
        };
        row.PointerEntered += (_, _) => row.Background = hover;
        row.PointerExited += (_, _) => row.Background = idle;

        return row;
    }

    private static string FormatRelative(TimeSpan ago)
    {
        if (ago.TotalSeconds < 60) return "just now";
        if (ago.TotalMinutes < 60) return $"{(int)ago.TotalMinutes} min ago";
        if (ago.TotalHours < 24) return $"{(int)ago.TotalHours} h ago";
        return $"{(int)ago.TotalDays} d ago";
    }

    private void OnNotificationHistoryChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_notifPopoverOpen) RebuildNotifList();
            UpdateNotifBadge();
        });
    }

    // Single-shot per change: AppLoader debounces FS bursts, but the user
    // still might have several changes happen across a session. We toast
    // every distinct change but cap to one outstanding toast at a time so
    // a rapid series of edits doesn't spam.
    private bool _appsFolderToastPending;

    private void OnApplicationsFolderChanged(string changedPath)
    {
        // Watcher fires on a thread-pool thread.
        Dispatcher.UIThread.Post(() =>
        {
            if (_appsFolderToastPending) return;
            _appsFolderToastPending = true;
            try
            {
                var name = System.IO.Path.GetFileName(changedPath);
                DOSIPopNotification.Show(
                    string.IsNullOrEmpty(name)
                        ? "Your Applications folder changed. Sign out and back in to reload."
                        : $"\u201C{name}\u201D changed. Sign out and back in to reload.",
                    TimeSpan.FromSeconds(8));
            }
            catch { /* host may not be ready yet - history captures the entry */ }
            finally
            {
                // Clear after the toast lifetime + a margin.
                Dispatcher.UIThread.Post(() => _appsFolderToastPending = false,
                    DispatcherPriority.Background);
            }
        });
    }

    // =====================================================================
    // Desktop context menu
    //
    // The wallpaper right-click menu is built by
    // DesktopIconLayer.BuildWallpaperContextMenu and assigned to
    // Desktop.ContextMenu in the constructor above. Hosting the builder
    // on DesktopIconLayer keeps the menu identical across primary and
    // secondary monitors (Snap to grid, Auto-arrange icons, Paste, New
    // folder, Open Files, Open Trash) and lets every action route to
    // the layer's own _desktopPath - so right-clicking on monitor 2
    // creates files / pastes on monitor 2's desktop folder, not the
    // primary's.
    // =====================================================================

    private void UpdateNotifBadge()
    {
        if (_notifBadge == null || _notifBadgeText == null) return;
        var count = NotificationHistory.All.Count;
        var unread = count - _notifLastSeenCount;
        if (unread <= 0)
        {
            _notifBadge.IsVisible = false;
            return;
        }
        _notifBadge.IsVisible = true;
        _notifBadgeText.Text = unread > 99 ? "99+" : unread.ToString();
    }

    // Process-wide flag so a multi-monitor session (multiple DesktopScreen
    // instances attaching at sign-in) doesn't toast the same crash N times.
    private static bool _crashSurfaced;

    /// <summary>
    /// On the very first DesktopScreen attach of the session, checks
    /// whether the previous run wrote a crash log and surfaces a toast
    /// pointing the user at it. The Reporter rotated the file before
    /// this method runs, so the next sign-in won't re-toast.
    /// </summary>
    private void SurfacePendingCrashOnce()
    {
        if (_crashSurfaced) return;
        _crashSurfaced = true;

        var rotatedPath = CrashReporter.ConsumePendingCrash();
        if (rotatedPath == null) return;

        // Defer the toast slightly so the popup overlay is wired up and
        // the user has visually settled on the desktop. Without the
        // post-Loaded delay the toast can race the chrome slide-in and
        // animate from a partly-built parent.
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                DOSIPopNotification.Show(
                    $"DAX.OSI recovered from a crash on the previous run. " +
                    $"Details written to {System.IO.Path.GetFileName(rotatedPath)}.",
                    TimeSpan.FromSeconds(8));
            }
            catch { /* notification host may not be ready yet; bell still has the entry from history */ }
        }, DispatcherPriority.Background);
    }

    // =====================================================================
    // Taskbar slide animation
    // =====================================================================

    private DispatcherTimer? _taskbarAnimTimer;
    // Tracks the TCS for the currently-running taskbar slide so it can be
    // completed (instead of left dangling) if the desktop is detached
    // mid-animation. Without this, RunSignOutSequenceAsync's Task.WhenAll
    // hangs forever when the crossfade detaches us before the timer ticks
    // to completion - and SystemSignOut.IsSigningOut stays stuck `true`,
    // so future Sign Out clicks become silent no-ops.
    private TaskCompletionSource<bool>? _taskbarAnimTcs;
    private double _taskbarAnimTargetY;

    /// <summary>
    /// Drops the taskbar in from above. Awaitable so callers can sequence
    /// other startup work after the chrome is in place. Safe to call when
    /// the bar is already on-screen (no-op then).
    /// </summary>
    public Task AnimateTaskbarInAsync(int durationMs = 380)
        => AnimateTaskbarAsync(targetY: 0, durationMs, easeOut: true);    /// Slides the taskbar back up off-screen. Used by the sign-out and
    /// shutdown flows so the chrome retracts cleanly instead of just fading
    /// with everything else. Returns when the slide is complete.
    /// </summary>
    public Task AnimateTaskbarOutAsync(int durationMs = 280)
        => AnimateTaskbarAsync(targetY: OffScreenSlideY, durationMs, easeOut: false);

    private Task AnimateTaskbarAsync(double targetY, int durationMs, bool easeOut)
    {
        // Supersede any in-flight animation: snap its TCS to completed so
        // its awaiter resumes, then take over the timer slot.
        CompletePendingTaskbarAnim();

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _taskbarAnimTcs = tcs;
        _taskbarAnimTargetY = targetY;

        var startY = _taskbarSlide.Y;
        if (Math.Abs(startY - targetY) < 0.5)
        {
            _taskbarSlide.Y = targetY;
            CompletePendingTaskbarAnim();
            return tcs.Task;
        }

        var startTime = DateTime.UtcNow;
        _taskbarAnimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _taskbarAnimTimer.Tick += (_, _) =>
        {
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            var t = Math.Clamp(elapsed / durationMs, 0d, 1d);
            var eased = easeOut
                ? 1 - Math.Pow(1 - t, 3)   // cubic ease-out for entrance
                : t * t * t;                // cubic ease-in for exit

            _taskbarSlide.Y = startY + (targetY - startY) * eased;

            if (t >= 1d)
            {
                CompletePendingTaskbarAnim();
            }
        };
        _taskbarAnimTimer.Start();
        return tcs.Task;
    }

    /// <summary>
    /// Stops the active taskbar animation timer (if any), snaps the slide to
    /// its intended end position, and completes the pending TCS so any
    /// awaiter (the sign-out / shutdown sequence) can move on. Safe to call
    /// when nothing is animating.
    /// </summary>
    private void CompletePendingTaskbarAnim()
    {
        _taskbarAnimTimer?.Stop();
        _taskbarAnimTimer = null;
        var tcs = _taskbarAnimTcs;
        if (tcs == null) return;
        _taskbarAnimTcs = null;
        _taskbarSlide.Y = _taskbarAnimTargetY;
        tcs.TrySetResult(true);
    }

    /// <summary>
    /// Live-resize the taskbar Border + re-publish the work-area inset
    /// when the user changes the taskbar height in Settings. Subscribed
    /// in AttachedToVisualTree, unsubscribed in detach.
    /// </summary>
    private void OnTaskbarHeightChanged(object? sender, double newHeight)
    {
        if (_taskbar != null) _taskbar.Height = newHeight;
        // If the slide is parked off-screen at the OLD height, re-park
        // at the new one so an entrance animation that hasn't run yet
        // still starts from the correct off-screen Y. Use the absolute
        // distance so this works regardless of dock edge (top: negative
        // Y; bottom: positive Y).
        if (_taskbarSlide != null && Math.Abs(_taskbarSlide.Y) > 0.5)
            _taskbarSlide.Y = OffScreenSlideY;
        var wm = _ownerHost?.WindowManager ?? WindowManager.Instance;
        if (wm != null)
        {
            // Update whichever inset is currently active for this dock.
            if (TaskbarMetricsPosition == DOSI.CORE.UIComponents.WindowManagement.TaskbarPosition.Top)
            { wm.TopWorkAreaInset = newHeight; wm.BottomWorkAreaInset = 0; }
            else
            { wm.TopWorkAreaInset = 0; wm.BottomWorkAreaInset = newHeight; }
        }
        // Bottom-dock margin formula includes TaskbarHeight so a height
        // change has to re-lift the clock too.
        ApplyClockLayout();
        if (_versionText != null) _versionText.Margin = ComputeVersionMargin();
    }

    /// <summary>
    /// Live-relocate the taskbar Border + apps menu + notification
    /// popover to the new dock edge, and re-publish the work-area
    /// reserve to the matching side. Subscribed in AttachedToVisualTree.
    /// </summary>
    private void OnTaskbarPositionChanged(object? sender, DOSI.CORE.UIComponents.WindowManagement.TaskbarPosition pos)
    {
        bool top = pos == DOSI.CORE.UIComponents.WindowManagement.TaskbarPosition.Top;
        if (_taskbar != null)
        {
            _taskbar.VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            _taskbar.BorderThickness = top
                ? new Thickness(0, 0, 0, 1)
                : new Thickness(0, 1, 0, 0);
        }
        if (_taskbarSlide != null)
        {
            // Snap the slide to the docked position on the new edge so
            // a position swap reads as instant rather than animating
            // through the screen middle.
            _taskbarSlide.Y = 0;
        }
        // Apps menu: realign and remargin to the new edge.
        if (_appsMenu != null)
        {
            _appsMenu.VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            _appsMenu.Margin = top
                ? new Thickness(8, TaskbarHeight + 4, 0, 0)
                : new Thickness(8, 0, 0, TaskbarHeight + 4);
        }
        if (_notifPopover != null)
        {
            _notifPopover.VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            _notifPopover.Margin = top
                ? new Thickness(0, TaskbarHeight + 4, 8, 0)
                : new Thickness(0, 0, 8, TaskbarHeight + 4);
        }
        // Re-publish the work-area inset on the right side.
        var wm = _ownerHost?.WindowManager ?? WindowManager.Instance;
        if (wm != null)
        {
            if (top) { wm.TopWorkAreaInset = TaskbarHeight; wm.BottomWorkAreaInset = 0; }
            else     { wm.TopWorkAreaInset = 0; wm.BottomWorkAreaInset = TaskbarHeight; }
        }
        // Lift the clock above the bottom taskbar (if any) so it stays
        // at the same on-screen Y as the login screen's clock - the
        // visual continuity through sign-in is what makes it feel like
        // a single OS instead of two screens.
        ApplyClockLayout();
        if (_versionText != null) _versionText.Margin = ComputeVersionMargin();
    }

    /// <summary>
    /// Computes the bottom margin that keeps the clock floating ~50 px
    /// above whatever's beneath it - the screen edge for top dock, or
    /// the top of the bottom-docked taskbar otherwise. Matches the
    /// login screen's 50 px lift so the clock appears stationary
    /// across the sign-in transition.
    /// </summary>
    /// <summary>
    /// Returns the horizontal/vertical alignment + margin tuple that
    /// places the ambient clock at the user's preferred corner. Bottom
    /// rows include the dock-aware lift via TaskbarHeight so the clock
    /// always floats clear of a bottom-docked taskbar; top rows leave
    /// room for a top-docked one.
    /// </summary>
    private static (HorizontalAlignment H, VerticalAlignment V, Thickness M) ComputeClockLayout()
    {
        var pos = DOSI.CORE.UIComponents.WindowManagement.TaskbarMetrics.ClockPosition;
        bool top = pos == DOSI.CORE.UIComponents.WindowManagement.ClockPosition.TopLeft
                || pos == DOSI.CORE.UIComponents.WindowManagement.ClockPosition.TopRight;
        bool left = pos == DOSI.CORE.UIComponents.WindowManagement.ClockPosition.TopLeft
                 || pos == DOSI.CORE.UIComponents.WindowManagement.ClockPosition.BottomLeft;

        // Dock-aware lift: 50 px breathing space on the side AWAY from
        // the screen edge, plus TaskbarHeight if the taskbar shares
        // that edge.
        var topLift = 50d + (top && TaskbarMetricsPosition == DOSI.CORE.UIComponents.WindowManagement.TaskbarPosition.Top
            ? TaskbarHeight : 0);
        var bottomLift = 50d + (!top && TaskbarMetricsPosition == DOSI.CORE.UIComponents.WindowManagement.TaskbarPosition.Bottom
            ? TaskbarHeight : 0);
        var sideMargin = 36d;

        return (
            left ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            top ? VerticalAlignment.Top : VerticalAlignment.Bottom,
            new Thickness(
                left ? sideMargin : 0,
                top ? topLift : 0,
                left ? 0 : sideMargin,
                top ? 0 : bottomLift)
        );
    }

    /// <summary>
    /// Backward-compat shim: callers that still treat the clock layout
    /// as a margin alone get the margin component of the full layout.
    /// New chrome code should call <see cref="ComputeClockLayout"/> and
    /// apply all three tuple fields.
    /// </summary>
    private static Thickness ComputeClockMargin() => ComputeClockLayout().M;

    /// <summary>
    /// Re-applies the current ClockPosition to <see cref="_clockStack"/>:
    /// horizontal + vertical alignment, margin, AND the per-text-block
    /// alignment so the clock numerals align to the same edge as the
    /// stack (right-aligned text in a right-anchored stack reads as a
    /// real corner clock instead of a left-aligned blob shoved right).
    /// Idempotent.
    /// </summary>
    private void ApplyClockLayout()
    {
        if (_clockStack == null) return;
        var (h, v, m) = ComputeClockLayout();
        _clockStack.HorizontalAlignment = h;
        _clockStack.VerticalAlignment = v;
        _clockStack.Margin = m;
        var textAlign = h == HorizontalAlignment.Right
            ? TextAlignment.Right
            : TextAlignment.Left;
        if (_clockText != null)
        {
            _clockText.HorizontalAlignment = h;
            _clockText.TextAlignment = textAlign;
        }
        if (_dateText != null)
        {
            _dateText.HorizontalAlignment = h;
            _dateText.TextAlignment = textAlign;
        }
    }

    private void OnClockPositionChanged(object? sender,
        DOSI.CORE.UIComponents.WindowManagement.ClockPosition pos) => ApplyClockLayout();

    /// <summary>
    /// Bottom-right version label margin. Same dock-aware lift as the
    /// clock so the chrome reads consistently regardless of taskbar
    /// dock - "DAX.OSI v1.0" never gets buried under the bar.
    /// </summary>
    private static Thickness ComputeVersionMargin()
    {
        var bottom = 16d;
        if (TaskbarMetricsPosition == DOSI.CORE.UIComponents.WindowManagement.TaskbarPosition.Bottom)
            bottom += TaskbarHeight;
        return new Thickness(0, 0, 24, bottom);
    }

    private static void LaunchApplication(DOSIWindow window)
    {
        var manager = WindowManager.Instance;
        if (manager == null) return;

        manager.OpenWindow(window);
        window.BringToFront();
    }

    // =====================================================================
    // User chip / welcome / chrome
    // =====================================================================

    private void UpdateUserChip(DOSIUser? user)
    {
        var name = user?.DisplayName ?? user?.Username ?? string.Empty;
        _taskbarUser.Text = name;
        _userAvatarInitial.Text = string.IsNullOrWhiteSpace(name)
            ? "?"
            : char.ToUpperInvariant(name.Trim()[0]).ToString();
        _userAvatarChip.IsVisible = !string.IsNullOrWhiteSpace(name);
    }

    private void OnAccentAccentChanged(object? sender, EventArgs e)
    {
        _taskbar.Background = BuildTaskbarBackground();
        _taskbar.BorderBrush = BuildTaskbarBorderBrush();
        if (_appsButtonAccent.Child is UniformGrid appsTiles)
        {
            foreach (var tile in appsTiles.Children)
            {
                if (tile is Border b)
                    b.Background = Accents.AccentGradientBrush;
            }
        }
        _appsButtonLabel.Foreground = Accents.TextPrimaryBrush;
        _taskbarUser.Foreground = Accents.TextPrimaryBrush;
        _userAvatarInitial.Foreground = new SolidColorBrush(Accents.TextOnAccent);

        // _clockText / _dateText are intentionally pinned to white - they
        // never re-tint with the accent (matches LoginScreen's behavior).
        _versionText.Foreground = Accents.TextSecondaryBrush;

        _appsMenu.Background = BuildMenuBackground();

        // Notification popover surfaces (built lazily, so guard each one).
        // Background re-evaluates the menu gradient and the border picks
        // up the accent so a live accent flip is reflected immediately
        // even while the popover is open.
        if (_notifPopover != null)
        {
            _notifPopover.Background = BuildMenuBackground();
            _notifPopover.BorderBrush = new SolidColorBrush(Accents.AccentSecondary);
        }
        if (_notifPopoverTitle != null)
            _notifPopoverTitle.Foreground = Accents.TextPrimaryBrush;
        if (_notifBadgeText != null)
            _notifBadgeText.Foreground = new SolidColorBrush(Accents.TextOnAccent);
        // List rows capture brushes at row-build time; rebuild while open
        // so the new accent shows up without waiting for the next toast.
        if (_notifPopoverOpen) RebuildNotifList();
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        _clockText.Text = now.ToString("h:mm tt");
        _dateText.Text = now.ToString("dddd, MMMM d");
    }

    internal static IBrush BuildTaskbarBackground()
    {
        // Light accent needs a light surface so the (dark) TextPrimaryBrush
        // labels in the taskbar stay readable. All other accents keep the
        // original deep navy so the chrome reads as "system bar".
        if (Accents.CurrentAccent == DOSIAccent.Light)
        {
            return new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(235, 248, 249, 252), 0),
                    new GradientStop(Color.FromArgb(225, 232, 236, 244), 1)
                }
            };
        }

        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(220, 22, 24, 30), 0),
                new GradientStop(Color.FromArgb(210, 14, 16, 22), 1)
            }
        };
    }

    /// <summary>
    /// Builds an accent-tinted gradient for the bottom border of the taskbar
    /// so the user's chosen accent is visible at all times.
    /// </summary>
    internal static IBrush BuildTaskbarBorderBrush()
    {
        var a = Accents.AccentPrimary;
        var b = Accents.AccentSecondary;
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(180, a.R, a.G, a.B), 0),
                new GradientStop(Color.FromArgb(200, b.R, b.G, b.B), 1)
            }
        };
    }

    private static IBrush BuildMenuBackground()
    {
        // Match the taskbar: light surface under the Light accent so menu
        // item text (TextPrimaryBrush, dark in Light mode) stays readable.
        if (Accents.CurrentAccent == DOSIAccent.Light)
        {
            return new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(245, 250, 251, 254), 0),
                    new GradientStop(Color.FromArgb(238, 232, 236, 244), 1)
                }
            };
        }

        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(235, 28, 30, 38), 0),
                new GradientStop(Color.FromArgb(225, 16, 18, 26), 1)
            }
        };
    }
}
