using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DAX.OSI.DefaultApplications;
using DAX.OSI.UI;
using DOSI.CORE;
using DOSI.CORE.Security;
using DOSI.CORE.UIComponents;
using DOSI.CORE.UIComponents.WindowManagement;
using DOSI.CORE.UserManagement;

namespace DAX.OSI;

/// <summary>
/// Main application window for the DAX.OSI virtual operating system.
/// </summary>
public class MainWindow : Window
{
    private readonly ScreenManager _screenManager;
    private readonly BootScreen _bootScreen;
    private LoginScreen _loginScreen;
    private InitialStartup? _initialStartup;

    /// <summary>
    /// Persistent overlay above all <see cref="DOSIScreen"/>s where every
    /// <see cref="DOSIWindow"/> lives. Survives screen transitions so any
    /// open window (terminal, browser, etc.) stays visible the whole time.
    /// </summary>
    private readonly Canvas _globalOverlay;

    /// <summary>
    /// The single application-wide WindowManager. Created in the MainWindow
    /// constructor and never replaced - per-screen managers are inert
    /// (they pass <c>makeActive: false</c>), so <see cref="WindowManager.Instance"/>
    /// always points here. That's why Ctrl+T and the apps menu both target
    /// the same persistent canvas.
    /// </summary>
    private readonly WindowManager _globalWindowManager;

    /// <summary>
    /// Aero-style window snapping (drag a <see cref="DOSIWindow"/> to a screen
    /// edge or corner to half / quarter / maximize). Lives on the same global
    /// overlay as every window so snap previews appear underneath the dragged
    /// window regardless of which screen is currently visible.
    /// </summary>
    private readonly WindowSnapManager _windowSnapManager;

    /// <summary>Top layer for screen-attached chrome (taskbar, apps menu, popups).</summary>
    private readonly Canvas _popupOverlay;

    /// <summary>
    /// Topmost overlay used by <see cref="DOSI.CORE.Security.SessionLockManager"/>
    /// to host the lock screen above all other UI when the session is locked.
    /// Empty / not hit-testable while the session is unlocked.
    /// </summary>
    private readonly Panel _lockOverlay;

    /// <summary>Per-app-instance session lock manager (idle timeout + manual lock).</summary>
    private readonly SessionLockManager _sessionLock;

    /// <summary>Static accessor for the application-wide popup overlay panel.</summary>
    public static Panel? PopupHost { get; private set; }

    public MainWindow()
    {
        Title = "DAX.OSI";
        Width = 1280;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.Black;

        // Use the OS arrow as the window-wide cursor. Child controls
        // inherit Cursor unless they explicitly override it (text boxes
        // set the I-beam, etc.).
        Cursor = new Cursor(StandardCursorType.Arrow);

        var screenContainer = new ContentControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        // Background = null (NOT Transparent) so empty regions of the overlay
        // pass pointer hits through to the screen behind it.
        _globalOverlay = new Canvas { Background = null, ClipToBounds = false };
        _globalWindowManager = new WindowManager(_globalOverlay); // makeActive=true (default)
        _windowSnapManager = new WindowSnapManager(_globalOverlay, _globalWindowManager);

        _popupOverlay = new Canvas { Background = null, ClipToBounds = false };
        PopupHost = _popupOverlay;

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
            // Layer order (bottom -> top):
            //   1. screenContainer  - active DOSIScreen (wallpaper, login UI, ...)
            //   2. _globalOverlay   - all DOSIWindow instances (persists across screens)
            //   3. _popupOverlay    - taskbar, apps menu, notifications - always on top
            //   4. _lockOverlay     - session lock screen (above EVERYTHING when active)
            Children = { screenContainer, _globalOverlay, _popupOverlay, _lockOverlay }
        };

        Content = rootGrid;

        // One-time wiring of the per-user audit log (no-op if already done).
        SecurityAuditLog.Initialize();

        // Session lock manager: idle detection + lock screen plumbing.
        // Started after sign-in (OnSignInCompleted), stopped on sign-out.
        _sessionLock = new SessionLockManager(
            lockHost: _lockOverlay,
            inputSource: this,
            lockScreenFactory: user => new LockScreen(user));
        _sessionLock.SignOutRequested += (_, _) =>
        {
            // Restore overlay visibility before kicking off the sign-out
            // sequence so its fade-out animation has something to fade.
            _globalOverlay.IsVisible = true;
            _popupOverlay.IsVisible = true;
            try { SystemSignOut.Begin(); } catch { /* best effort */ }
        };

        // Native-rendered controls (e.g. the browser's WebView) are composed
        // by the OS above Avalonia's surface, so they would punch through the
        // lock overlay. Hide the global window layer and chrome layer entirely
        // while locked so nothing leaks through; restore on unlock.
        _sessionLock.Locked += (_, _) =>
        {
            _globalOverlay.IsVisible = false;
            _globalOverlay.IsHitTestVisible = false;
            _popupOverlay.IsVisible = false;
            _popupOverlay.IsHitTestVisible = false;
        };
        _sessionLock.Unlocked += (_, _) =>
        {
            _globalOverlay.IsVisible = true;
            _globalOverlay.IsHitTestVisible = true;
            _popupOverlay.IsVisible = true;
            _popupOverlay.IsHitTestVisible = true;
        };

        _screenManager = new ScreenManager(screenContainer);
        _bootScreen = new BootScreen();
        _loginScreen = new LoginScreen();
        _loginScreen.SignInCompleted += OnSignInCompleted;
        _screenManager.RegisterScreen(_bootScreen);
        _screenManager.RegisterScreen(_loginScreen);

        // Listen for KeyDown events even if a focused child (e.g. DOSITextBox on
        // LoginScreen) has already marked them Handled, so global hotkeys still work.
        // Single registration with handledEventsToo=true so each key press fires once.
        AddHandler(KeyDownEvent, OnGlobalKeyDown, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        Loaded += OnLoaded;

        // Hook the global shutdown pipeline so we can release host-owned
        // resources (open windows, screens, popup overlay) before the
        // application lifetime tears down.
        SystemShutdown.ShuttingDown += OnSystemShuttingDown;
        SystemShutdown.ShutdownSequence = RunShutdownSequenceAsync;

        // Hook the global sign-out pipeline so the terminal `signout` command
        // and the apps menu Sign Out item can return us to the login screen.
        SystemSignOut.SignOutHandler = RunSignOutSequenceAsync;
        Closed += (_, _) =>
        {
            SystemShutdown.ShuttingDown -= OnSystemShuttingDown;
            SystemShutdown.ShutdownSequence = null;
            SystemSignOut.SignOutHandler = null;
        };
    }

    private async System.Threading.Tasks.Task RunShutdownSequenceAsync()
    {
        // Disable input on the persistent overlays (open windows + desktop
        // chrome) so nothing can be clicked during the shutdown animation,
        // then gracefully fade them out in parallel with the crossfade so
        // the shutdown screen takes the surface without anything snapping off.
        _globalOverlay.IsHitTestVisible = false;
        _popupOverlay.IsHitTestVisible = false;

        var shutdownScreen = new ShutdownScreen();
        _screenManager.RegisterScreen(shutdownScreen);

        var fadeTask = FadeOverlaysAsync(_globalOverlay.Opacity, 0d, 500);
        var navTask = _screenManager.NavigateToWithCrossfadeAsync(
            "shutdown",
            System.TimeSpan.FromMilliseconds(500));
        await Task.WhenAll(fadeTask, navTask);

        await shutdownScreen.RunAsync();
    }

    /// <summary>
    /// Drives the sign-out experience: fades the desktop chrome and any open
    /// application windows out (without closing them), plays the SignoutScreen
    /// farewell, clears the current user, crossfades back to a freshly-built
    /// LoginScreen, and on the next successful sign-in fades the preserved
    /// windows + new desktop chrome back in. This mimics a real OS sign-out
    /// where the user's apps "wait" for them across the lock screen.
    /// </summary>
    private async Task RunSignOutSequenceAsync()
    {
        // Capture the user before we clear it so the screen can greet them.
        var leavingUser = UserManager.CurrentUser;

        // Stop the idle-lock tracker (and dismiss the lock screen if active)
        // before we tear down the user's session.
        _sessionLock.Stop();

        // Disable input on the overlays (windows + desktop chrome) and start
        // their fade-out in parallel with the crossfade to the signout screen.
        _globalOverlay.IsHitTestVisible = false;
        _popupOverlay.IsHitTestVisible = false;

        var signoutScreen = new SignoutScreen(leavingUser);
        _screenManager.RegisterScreen(signoutScreen);

        var fadeTask = FadeOverlaysAsync(_globalOverlay.Opacity, 0d, 450);
        var navTask = _screenManager.NavigateToWithCrossfadeAsync(
            "signout",
            TimeSpan.FromMilliseconds(450));
        await Task.WhenAll(fadeTask, navTask);

        // Collapse the overlays entirely once they're invisible. With
        // IsVisible=false the persistent DOSIWindow instances drop out of
        // the keyboard focus chain, so Tab on the login screen can no
        // longer cycle through windows the user can't see.
        _globalOverlay.IsVisible = false;
        _popupOverlay.IsVisible = false;

        // Play the farewell animation.
        await signoutScreen.RunAsync();

        // Clear the user session. We deliberately do NOT close open windows -
        // they live on in _globalOverlay (currently faded to 0) so they can
        // come back when the user (or a different one) signs in again.
        UserManager.SignOut();

        // Tear down the desktop screen. This unhooks its taskbar / apps menu
        // chrome from _popupOverlay, but the overlay itself stays alive so
        // the next DesktopScreen can attach fresh chrome.
        _screenManager.RemoveScreen("desktop");

        // Build a fresh LoginScreen instance and register it.
        _loginScreen = new LoginScreen();
        _loginScreen.SignInCompleted += OnSignInCompleted;
        _screenManager.RegisterScreen(_loginScreen);

        // Crossfade signout -> login. Overlays remain at opacity 0 and
        // hit-test disabled until the next successful sign-in.
        await _screenManager.NavigateToWithCrossfadeAsync(
            "login",
            TimeSpan.FromMilliseconds(700));

        _screenManager.RemoveScreen("signout");
    }

    /// <summary>
    /// Animates the opacity of the global window overlay and the popup overlay
    /// (taskbar / apps menu / notifications) together. Used by the shutdown
    /// and sign-out sequences so the desktop chrome and any open windows fade
    /// in/out gracefully instead of snapping on or off.
    /// </summary>
    private Task FadeOverlaysAsync(double from, double to, int durationMs)
    {
        var tcs = new TaskCompletionSource<bool>();
        var startTime = DateTime.UtcNow;
        var duration = Math.Max(1, durationMs);

        // Seed the starting state so the first frame doesn't pop.
        _globalOverlay.Opacity = from;
        _popupOverlay.Opacity = from;

        var timer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };

        timer.Tick += (_, _) =>
        {
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            var t = Math.Clamp(elapsed / duration, 0d, 1d);
            // Ease-in-out cubic for a smooth, natural feel.
            var eased = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
            var value = from + (to - from) * eased;

            _globalOverlay.Opacity = value;
            _popupOverlay.Opacity = value;

            if (t >= 1d)
            {
                timer.Stop();
                tcs.TrySetResult(true);
            }
        };
        timer.Start();
        return tcs.Task;
    }

    private void OnSystemShuttingDown()
    {
        try
        {
            _loginScreen.SignInCompleted -= OnSignInCompleted;
            if (_initialStartup != null)
                _initialStartup.SetupCompleted -= OnInitialStartupCompleted;

            _globalWindowManager.CloseAllWindows();
            _screenManager.DisposeAll();

            _globalOverlay.Children.Clear();
            _popupOverlay.Children.Clear();
            PopupHost = null;
        }
        catch
        {
            // Never let cleanup throw during shutdown.
        }
    }

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.T && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            OpenNewTerminal();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Opens a new terminal. Identical to launching one from the desktop apps
    /// menu - both go through <see cref="WindowManager.Instance"/> which is
    /// the persistent overlay manager.
    /// </summary>
    private void OpenNewTerminal()
    {
        WindowManager.Instance?.OpenWindow(new DOSITerminal());
    }

    private async void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Make sure the Users folder exists on disk before we ask whether anyone has signed up.
        UserManager.Initialize();

        _screenManager.NavigateTo("boot");

        // Show the boot screen for 4 seconds.
        await Task.Delay(TimeSpan.FromSeconds(4));

        if (UserManager.HasAnyUsers())
        {
            await GoToLoginAsync(removePrevious: "boot");
        }
        else
        {
            await GoToInitialStartupAsync();
        }
    }

    private async Task GoToInitialStartupAsync()
    {
        _initialStartup = new InitialStartup();
        _initialStartup.SetupCompleted += OnInitialStartupCompleted;
        _screenManager.RegisterScreen(_initialStartup);

        await _screenManager.NavigateToWithCrossfadeAsync(
            "initial-startup",
            TimeSpan.FromMilliseconds(800));

        _screenManager.RemoveScreen("boot");
    }

    private async void OnInitialStartupCompleted(object? sender, DOSIUser e)
    {
        if (_initialStartup != null)
        {
            _initialStartup.SetupCompleted -= OnInitialStartupCompleted;
        }

        await GoToLoginAsync(removePrevious: "initial-startup");
        _initialStartup = null;
    }

    private async Task GoToLoginAsync(string removePrevious)
    {
        await _screenManager.NavigateToWithCrossfadeAsync(
            "login",
            TimeSpan.FromMilliseconds(800));

        _screenManager.RemoveScreen(removePrevious);
    }

    private async void OnSignInCompleted(object? sender, DOSIUser user)
    {
        // Detach the handler so a future re-registered LoginScreen instance
        // (rare but possible) doesn't double-fire.
        _loginScreen.SignInCompleted -= OnSignInCompleted;

        // Lazily build and register the desktop screen, then crossfade in.
        var desktop = new DesktopScreen();
        _screenManager.RegisterScreen(desktop);

        await _screenManager.NavigateToWithCrossfadeAsync(
            "desktop",
            TimeSpan.FromMilliseconds(700));

        // Tear down the login screen now that it isn't visible. RemoveScreen
        // disposes the instance if it implements IDisposable.
        _screenManager.RemoveScreen("login");

        // Bring the desktop chrome and any preserved windows (kept alive
        // through a sign-out) back to full opacity. On a brand-new sign-in
        // the overlays are already at 1, so this is effectively a no-op
        // animation in that case.
        if (_globalOverlay.Opacity < 1d || _popupOverlay.Opacity < 1d)
        {
            // Re-attach to layout/focus before fading in. Sign-out collapses
            // these overlays so their hidden DOSIWindows can't be reached by
            // Tab from the login screen; restore visibility now that we're
            // about to show them again.
            _globalOverlay.IsVisible = true;
            _popupOverlay.IsVisible = true;
            await FadeOverlaysAsync(_globalOverlay.Opacity, 1d, 500);
        }
        else
        {
            _globalOverlay.IsVisible = true;
            _popupOverlay.IsVisible = true;
            _globalOverlay.Opacity = 1d;
            _popupOverlay.Opacity = 1d;
        }

        _globalOverlay.IsHitTestVisible = true;
        _popupOverlay.IsHitTestVisible = true;

        // Begin idle-lock tracking for the freshly signed-in user.
        _sessionLock.Start();
    }
}


