using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using DAX.OSI.UI;
using DOSI.CORE.UIComponents;
using DOSI.CORE.UIComponents.WindowManagement;

namespace DAX.OSI;

/// <summary>
/// A borderless native <c>Window</c> rendered onto a non-primary physical
/// monitor so DAX.OSI takes over every connected display instead of
/// leaving extras showing the host OS desktop. By design these are pure
/// "extension surfaces": they show the system wallpaper + accent (matching
/// the primary), but no taskbar, apps menu, clock, or other chrome -
/// exactly how a real OS treats secondary monitors by default.
///
/// MonitorWindow mirrors <see cref="MainWindow"/>'s 4-layer composition
/// (screen container / global overlay / popup overlay / lock overlay) and
/// owns its own <see cref="WindowManager"/> so future work (M2) can drag
/// app windows from primary onto a secondary. When the user clicks
/// anywhere on this Window, the <c>Activated</c> event flips
/// <see cref="WindowManager.Instance"/> to point at this monitor's manager
/// so subsequent app launches land here.
///
/// Lifecycle: created by <see cref="MainWindow"/> at app startup (one per
/// non-primary <see cref="Avalonia.Platform.Screen"/>) and kept alive
/// across boot / login / desktop / lock / signout. Closed only on
/// shutdown, process exit, or monitor disconnect (hot-plug).
/// </summary>
public class MonitorWindow : Window, IDosiHost
{
    private readonly Canvas _globalOverlay;
    private readonly WindowManager _windowManager;
    private readonly WindowSnapManager _snapManager;
    private readonly Canvas _popupOverlay;
    private readonly Panel _lockOverlay;
    private readonly ScreenManager _screenManager;
    private readonly ExtensionScreen _extensionScreen;
    private readonly Avalonia.Platform.Screen _targetScreen;

    Panel IDosiHost.PopupHost => _popupOverlay;
    Canvas IDosiHost.WindowOverlayHost => _globalOverlay;
    WindowManager IDosiHost.WindowManager => _windowManager;
    Avalonia.Platform.Screen? IDosiHost.TargetScreen => _targetScreen;

    /// <summary>
    /// The lock overlay panel for this monitor. <see cref="MainWindow"/>
    /// drives visibility on its own primary lock overlay AND walks every
    /// open MonitorWindow toggling this property when the session locks /
    /// unlocks, so the lock UI is mirrored across all displays for security.
    /// </summary>
    public Panel LockOverlay => _lockOverlay;

    /// <summary>
    /// The extension screen instance hosted by this monitor (wallpaper +
    /// accent only, no chrome). Exposed so the host can reach into the
    /// per-monitor surface during multi-monitor coordination work.
    /// </summary>
    public ExtensionScreen Surface => _extensionScreen;

    public MonitorWindow(Avalonia.Platform.Screen targetScreen, int monitorIndex = 2)
    {
        _targetScreen = targetScreen;
        Title = "DAX.OSI Display";
        Background = Brushes.Black;
        // Borderless: matches MainWindow's approach of going FullScreen
        // for an immersive desktop experience. Position the window into
        // the target monitor first (in physical PixelPoint coords), then
        // promote to FullScreen on Opened so the OS snaps us to that
        // monitor at native resolution. Doing it in the constructor
        // before Show would race with the platform's window placement.
        Opened += (_, _) => WindowState = WindowState.FullScreen;
        ShowInTaskbar = false;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = new Cursor(StandardCursorType.Arrow);

        // Position + size into the target monitor BEFORE Show. Avalonia
        // Screen.Bounds is in physical pixels; ClientSize is in DIPs, so
        // we scale the size by the screen's DPI scaling factor. Position
        // stays in PixelPoint - that maps 1:1 to the physical desktop.
        Position = targetScreen.Bounds.Position;
        var scaling = targetScreen.Scaling > 0 ? targetScreen.Scaling : 1.0;
        Width = targetScreen.Bounds.Size.Width / scaling;
        Height = targetScreen.Bounds.Size.Height / scaling;

        // ---- 4-layer composition (matches MainWindow) ----
        var screenContainer = new ContentControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        // null Background so empty regions pass pointer hits through to
        // whatever's behind the overlay (the active screen's wallpaper).
        _globalOverlay = new Canvas { Background = null, ClipToBounds = false };
        _windowManager = new WindowManager(_globalOverlay, makeActive: false);
        _snapManager = new WindowSnapManager(_globalOverlay, _windowManager);

        _popupOverlay = new Canvas { Background = null, ClipToBounds = false };

        _lockOverlay = new Panel
        {
            Background = null,
            IsHitTestVisible = false,
            ClipToBounds = false
        };

        var rootGrid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Children = { screenContainer, _globalOverlay, _popupOverlay, _lockOverlay }
        };
        Content = rootGrid;

        // ---- Per-monitor extension surface ----
        // Pure wallpaper + accent backdrop, no chrome. Matches how Windows /
        // macOS treat secondary monitors by default - the system wallpaper
        // is present everywhere, but the taskbar / apps menu stay on primary.
        _screenManager = new ScreenManager(screenContainer);
        _extensionScreen = new ExtensionScreen(monitorIndex);
        _screenManager.RegisterScreen(_extensionScreen);
        _screenManager.NavigateTo(_extensionScreen.ScreenId);

        // When this monitor becomes the active native window, promote our
        // WindowManager to the global Instance so the next app launch lands
        // on this monitor (apps menu / terminal / Ctrl+T all flow through
        // WindowManager.Instance). MainWindow does the same flip on its
        // Activated, so input focus is the source of truth.
        Activated += (_, _) => _windowManager.MakeActive();

        // Register so the cross-monitor DOSIWindow drag handoff can find
        // us by screen membership. Unregister on Closed so a stale entry
        // for a disconnected monitor doesn't keep adopting transferred
        // windows into a dead canvas.
        DOSI.CORE.UIComponents.DosiHostRegistry.Register(this);
        Closed += (_, _) =>
        {
            DOSI.CORE.UIComponents.DosiHostRegistry.Unregister(this);
            if (ReferenceEquals(WindowManager.Instance, _windowManager))
            {
                // Hand the active-manager seat back to whatever host the
                // user clicks on next. No automatic fallback to avoid
                // surprising launches landing on a non-focused monitor.
            }
        };
    }
}
