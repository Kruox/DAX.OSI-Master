using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
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
/// Implements <see cref="IDosiHost"/> so the per-monitor system can treat
/// the primary window the same as any secondary <c>MonitorWindow</c> when
/// resolving popup hosts / window managers.
/// </summary>
public class MainWindow : Window, IDosiHost
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

    /// <summary>
    /// Static accessor for the application-wide popup overlay panel.
    /// Kept for back-compat with call sites that pre-date <see cref="IDosiHost"/>.
    /// New code should resolve the popup host from the owning <see cref="IDosiHost"/>
    /// (walk up the visual tree to the hosting <c>Window</c>) so multi-monitor
    /// setups route chrome to the correct monitor.
    /// </summary>
    public static Panel? PopupHost { get; private set; }

    // ===== IDosiHost =====
    // The primary MainWindow exposes the same surfaces it always had,
    // just behind a contract MonitorWindow can also satisfy. DesktopScreen
    // walks up the visual tree to find this contract instead of touching
    // the static MainWindow.PopupHost directly.
    Panel IDosiHost.PopupHost => _popupOverlay;
    Canvas IDosiHost.WindowOverlayHost => _globalOverlay;
    WindowManager IDosiHost.WindowManager => _globalWindowManager;
    Avalonia.Platform.Screen? IDosiHost.TargetScreen => Screens?.Primary;

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

        // Make the popup overlay the application-wide default host for toast
        // notifications. This layer sits above _globalOverlay (DOSIWindows),
        // so toasts always float on top - even over maximized/fullscreen apps.
        DOSIPopNotification.DefaultHost = _popupOverlay;

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
            try { SystemSignOut.Begin(); }
            catch (Exception ex) { Trace.WriteLine($"[MainWindow] SystemSignOut.Begin failed: {ex}"); }
        };

        // Native-rendered controls (e.g. the browser's WebView) are composed
        // by the OS above Avalonia's surface, so they would punch through the
        // lock overlay. Fade out the global window layer and chrome layer
        // entirely while locked so nothing leaks through; fade back on unlock.
        _sessionLock.Locked += async (_, _) =>
        {
            await FadeOverlaysAsync(_globalOverlay.Opacity, 0d, 350);
            _globalOverlay.IsVisible = false;
            _globalOverlay.IsHitTestVisible = false;
            _popupOverlay.IsVisible = false;
            _popupOverlay.IsHitTestVisible = false;
            // Multi-monitor: secondaries go dark + non-interactive while
            // the primary's lock screen handles authentication.
            ApplyMonitorLockState(locked: true);
        };
        _sessionLock.Unlocked += async (_, _) =>
        {
            _globalOverlay.IsVisible = true;
            _globalOverlay.IsHitTestVisible = true;
            _popupOverlay.IsVisible = true;
            _popupOverlay.IsHitTestVisible = true;
            await FadeOverlaysAsync(0d, 1d, 350);
            // Multi-monitor: bring secondaries back to full interaction.
            ApplyMonitorLockState(locked: false);
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
        AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
        Loaded += OnLoaded;

        // Multi-monitor: when the user clicks/focuses this native Window,
        // promote our WindowManager to be the global Instance so app-launch
        // sites (apps menu, terminal, hotkeys) target the monitor the user
        // just interacted with. MonitorWindow does the same on its side.
        Activated += (_, _) => _globalWindowManager.MakeActive();

        // Hook the global shutdown pipeline so we can release host-owned
        // resources (open windows, screens, popup overlay) before the
        // application lifetime tears down.
        SystemShutdown.ShuttingDown += OnSystemShuttingDown;
        SystemShutdown.ShutdownSequence = RunShutdownSequenceAsync;

        // Hook the global sign-out pipeline so the terminal `signout` command
        // and the apps menu Sign Out item can return us to the login screen.
        SystemSignOut.SignOutHandler = RunSignOutSequenceAsync;
        // Multi-monitor: register so the cross-monitor drag handoff in
        // DOSIWindow can locate this primary host alongside any secondaries.
        DosiHostRegistry.Register(this);
        Closed += (_, _) =>
        {
            // ===== Hard teardown for "user closed via taskbar / OS chrome" =====
            // The graceful SystemShutdown pipeline is bypassed in this path,
            // so we have to release every static / external reference that
            // would otherwise keep managed graph reachable (preventing the
            // .NET runtime from finalizing) or keep the Avalonia dispatcher
            // alive (preventing the process from exiting). Each item below
            // pinned at least one root that survived a taskbar-close.

            SystemShutdown.ShuttingDown -= OnSystemShuttingDown;
            SystemShutdown.ShutdownSequence = null;
            SystemSignOut.SignOutHandler = null;
            DosiHostRegistry.Unregister(this);

            // Stop the idle-lock tracker. It owns global input listeners on
            // `this` (MainWindow) and a DispatcherTimer; without Stop() the
            // timer would keep the dispatcher alive after Close.
            try { _sessionLock.Stop(); }
            catch (Exception ex) { Trace.WriteLine($"[MainWindow] _sessionLock.Stop failed: {ex}"); }

            // Unhook the LoginScreen sign-in event - LoginScreen instances
            // can outlive MainWindow if they were registered with a screen
            // manager that holds a reference.
            try { _loginScreen.SignInCompleted -= OnSignInCompleted; }
            catch (Exception ex) { Trace.WriteLine($"[MainWindow] LoginScreen.SignInCompleted unhook failed: {ex}"); }
            if (_initialStartup != null)
            {
                try { _initialStartup.SetupCompleted -= OnInitialStartupCompleted; }
                catch (Exception ex) { Trace.WriteLine($"[MainWindow] InitialStartup.SetupCompleted unhook failed: {ex}"); }
            }

            // Drop the hot-plug subscription. Avalonia's Screens object lives
            // for the process lifetime, so without this it pins MainWindow.
            if (_screensChangedHooked)
            {
                try { Screens.Changed -= OnScreensChanged; }
                catch (Exception ex) { Trace.WriteLine($"[MainWindow] Screens.Changed unhook failed: {ex}"); }
                _screensChangedHooked = false;
            }

            // Release static surfaces that pointed at this window's overlays.
            // Without these clears, the entire visual tree (every open
            // DOSIWindow, every screen, every cached bitmap) stays rooted by
            // these statics for the rest of the process - which since the
            // process can't exit either, is forever.
            if (PopupHost == _popupOverlay) PopupHost = null;
            if (DOSIPopNotification.DefaultHost == _popupOverlay)
                DOSIPopNotification.DefaultHost = null;

            // Tear down secondary monitor windows.
            CloseAllMonitorWindows();

            // Belt-and-braces: if the user closed MainWindow directly via
            // OS chrome (skipping the SystemShutdown pipeline) the ghost
            // would still be up and would prevent process exit under
            // OnLastWindowClose. Idempotent with the OnSystemShuttingDown
            // call above.
            DragGhostWindow.Shutdown();
        };
    }

    // =====================================================================
    // Multi-monitor (M1)
    //
    // For each non-primary connected display we spawn a borderless
    // MonitorWindow with its own DesktopScreen, WindowManager, and chrome,
    // so DAX.OSI takes over every monitor instead of leaving extras with
    // the host OS desktop visible. Spawned after sign-in (so the login
    // screen stays single-monitor), torn down on sign-out / shutdown.
    // =====================================================================

    private readonly List<MonitorWindow> _monitorWindows = [];
    private bool _screensChangedHooked;

    /// <summary>
    /// Brings the per-display MonitorWindow set in line with the currently-
    /// connected screens. Closes any windows whose target monitor has been
    /// disconnected and opens new ones for newly-connected non-primary
    /// displays. Idempotent and safe to call repeatedly (driven by the
    /// Screens.Changed hot-plug event).
    /// </summary>
    private void RebuildMonitorWindows()
    {
        var screens = Screens;
        if (screens == null) return;

        var primary = screens.Primary;
        var desired = screens.All
            .Where(s => s != null && s != primary)
            .ToList();

        // Close every existing monitor window and rebuild from scratch.
        // The set is small (typically 0-3 secondaries) so the simplicity is
        // worth more than the churn; and it avoids the booby-trap of trying
        // to map "old MonitorWindow" -> "new Screen handle" across a hot-plug
        // event where the platform may have invalidated screen identities.
        CloseAllMonitorWindows();

        foreach (var screen in desired)
        {
            try
            {
                // Index secondaries starting at 2 (primary is implicitly 1)
                // so the per-monitor desktop folder name is stable across
                // sessions: "Desktop-Monitor2" for the first secondary,
                // "Desktop-Monitor3" for the next, etc. Order matches
                // Avalonia's Screens.All enumeration; if the OS reorders
                // displays the folders stay the same shape but their
                // contents follow the screen at that ordinal position.
                var monitorIndex = _monitorWindows.Count + 2;
                var mw = new MonitorWindow(screen, monitorIndex);
                _monitorWindows.Add(mw);
                mw.Show();
            }
            catch (Exception ex)
            {
                // Best-effort: a flaky monitor (e.g. partially-attached USB-C
                // dock) shouldn't crash the primary desktop.
                Trace.WriteLine($"[MainWindow] MonitorWindow open failed: {ex}");
            }
        }

        // Each MonitorWindow.Show() activates that secondary, which would
        // leave WindowManager.Instance pointing at the LAST-spawned monitor.
        // Re-promote the primary so the user's first click / session-restore
        // launch lands on the main display by default. Subsequent clicks on
        // any monitor will flip Instance per the Activated handlers.
        _globalWindowManager.MakeActive();
        try { Activate(); }
        catch (Exception ex) { Trace.WriteLine($"[MainWindow] Activate failed: {ex}"); }

        // Multi-monitor: pre-warm the cross-monitor drag ghost so the FIRST
        // drag-to-another-monitor doesn't pay the OS-level layered-window
        // allocation cost (which manifests as a one-frame flicker on first
        // Show). Off-screen Show + Hide forces Windows to allocate the
        // transparent surface now, while the user is still settling in.
        // Single-monitor systems skip this - no cross-monitor drags possible.
        if (_monitorWindows.Count > 0)
        {
            DragGhostWindow.Prewarm();
        }

        // Hot-plug subscription is lazy: Screens isn't populated until the
        // window is shown, and we only need it once per process lifetime.
        if (!_screensChangedHooked && screens != null)
        {
            try
            {
                screens.Changed += OnScreensChanged;
                _screensChangedHooked = true;
            }
            catch (Exception ex)
            {
                // Some Avalonia backends may not raise Changed; non-fatal.
                Trace.WriteLine($"[MainWindow] Screens.Changed hook failed: {ex}");
            }
        }
    }

    private void OnScreensChanged(object? sender, EventArgs e)
    {
        // Fired when a monitor is connected / disconnected / configuration
        // changes. Always marshal back to the UI thread - some backends
        // raise this from a platform watcher thread.
        //
        // Windowed-launch mode never spawns secondary monitor windows in the
        // first place (see OnLoaded), so a hot-plug event has nothing to
        // rebuild - bail before touching anything.
        if (!SystemCore.Settings.Fullscreen) return;
        Dispatcher.UIThread.Post(RebuildMonitorWindows);
    }

    /// <summary>
    /// Closes every spawned <see cref="MonitorWindow"/> and clears the list.
    /// Used by sign-out, shutdown, and process teardown. Best-effort: a
    /// failure to close one window must not prevent us from closing the rest.
    /// </summary>
    private void CloseAllMonitorWindows()
    {
        foreach (var mw in _monitorWindows.ToList())
        {
            try { mw.Close(); }
            catch (Exception ex) { Trace.WriteLine($"[MainWindow] MonitorWindow close failed: {ex}"); }
        }
        _monitorWindows.Clear();
    }

    /// <summary>
    /// Mirrors the lock state to every secondary monitor: hides their
    /// content while locked so the user can't interact with the desktop on
    /// extra displays, restores it on unlock. The primary's lock screen
    /// (driven by <see cref="SessionLockManager"/>) continues to handle the
    /// actual unlock UI; secondaries just go dark for security.
    /// </summary>
    private void ApplyMonitorLockState(bool locked)
    {
        foreach (var mw in _monitorWindows)
        {
            if (mw.Content is Control c)
            {
                c.IsHitTestVisible = !locked;
                c.Opacity = locked ? 0d : 1d;
            }
        }
    }

    /// <summary>
    /// Animates every secondary <see cref="MonitorWindow"/>'s root content
    /// opacity in parallel with the primary's <see cref="FadeOverlaysAsync"/>
    /// fade. Returns a single Task that completes when EVERY monitor's
    /// fade has finished, so the caller can <c>Task.WhenAll</c> with the
    /// primary fade and have all displays animate as one synchronised
    /// transition (sign-out / shutdown). Snaps to the end value if there
    /// are no secondaries (no-op).
    /// </summary>
    private Task FadeMonitorsAsync(double from, double to, int durationMs)
    {
        if (_monitorWindows.Count == 0) return Task.CompletedTask;
        List<Control> controls = [];
        foreach (var mw in _monitorWindows)
        {
            if (mw.Content is Control c) controls.Add(c);
        }
        return AnimateOpacityAsync(controls, from, to, durationMs);
    }

    /// <summary>
    /// Animates JUST the per-monitor DOSIWindow + popup overlays (NOT the
    /// wallpaper / desktop layer) on every secondary <see cref="MonitorWindow"/>.
    /// Used by sign-out so application windows opened on secondary displays
    /// fade away with the primary's windows, while the secondaries' wallpaper
    /// stays visible across the user switch (matching FadeMonitorsAsync's
    /// "secondaries are pure wallpaper extensions" intent on signout). Also
    /// flips IsHitTestVisible at the start so secondary windows can't be
    /// clicked mid-fade. Snaps to no-op if there are no secondaries.
    /// </summary>
    private Task FadeMonitorWindowOverlaysAsync(double from, double to, int durationMs, bool disableHitTest)
    {
        if (_monitorWindows.Count == 0) return Task.CompletedTask;
        List<Control> controls = [];
        foreach (var mw in _monitorWindows)
        {
            // Resolve through IDosiHost so we get the SAME canvases the
            // monitor's DesktopScreen / WindowManager are using - not some
            // accidental different reference.
            var host = (IDosiHost)mw;
            if (host.WindowOverlayHost is Control winOverlay)
            {
                if (disableHitTest) winOverlay.IsHitTestVisible = false;
                controls.Add(winOverlay);
            }
            if (host.PopupHost is Control popOverlay)
            {
                if (disableHitTest) popOverlay.IsHitTestVisible = false;
                controls.Add(popOverlay);
            }
        }
        return AnimateOpacityAsync(controls, from, to, durationMs);
    }

    /// <summary>
    /// Snaps every secondary monitor's window + popup overlays back to fully
    /// visible / interactive. Called on sign-in to undo the fade-out applied
    /// by <see cref="FadeMonitorWindowOverlaysAsync"/> during the previous
    /// sign-out, so apps the new user opens on a secondary display work
    /// immediately.
    /// </summary>
    private void RestoreMonitorWindowOverlays()
    {
        foreach (var mw in _monitorWindows)
        {
            var host = (IDosiHost)mw;
            if (host.WindowOverlayHost is Control winOverlay)
            {
                winOverlay.Opacity = 1d;
                winOverlay.IsHitTestVisible = true;
            }
            if (host.PopupHost is Control popOverlay)
            {
                popOverlay.Opacity = 1d;
                popOverlay.IsHitTestVisible = true;
            }
        }
    }

    /// <summary>
    /// Multi-target opacity tween. Drives every supplied <see cref="Control"/>
    /// from <paramref name="from"/> to <paramref name="to"/> in lockstep on a
    /// single <see cref="DispatcherTimer"/>, using a monotonic <see cref="Stopwatch"/>
    /// so wall-clock jumps (NTP / DST / manual change) can't desync the fade.
    /// All callers (overlay fade, secondary monitor fade, per-monitor window/popup
    /// fade) funnel through here so easing + timing stay identical frame-by-frame.
    /// No-ops when no targets are supplied.
    /// </summary>
    private static Task AnimateOpacityAsync(IReadOnlyList<Control> targets, double from, double to, int durationMs)
    {
        if (targets.Count == 0) return Task.CompletedTask;

        var tcs = new TaskCompletionSource<bool>();
        var stopwatch = Stopwatch.StartNew();
        var duration = Math.Max(1, durationMs);

        // Seed the starting state so the first frame doesn't pop.
        for (var i = 0; i < targets.Count; i++) targets[i].Opacity = from;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            var t = Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / duration, 0d, 1d);
            // Ease-in-out cubic for a smooth, natural feel.
            var eased = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
            var value = from + (to - from) * eased;
            for (var i = 0; i < targets.Count; i++) targets[i].Opacity = value;

            if (t >= 1d)
            {
                timer.Stop();
                tcs.TrySetResult(true);
            }
        };
        timer.Start();
        return tcs.Task;
    }

    private async Task RunShutdownSequenceAsync()
    {
        // Disable input on the persistent overlays (open windows + desktop
        // chrome) so nothing can be clicked during the shutdown animation,
        // then gracefully fade them out in parallel with the crossfade so
        // the shutdown screen takes the surface without anything snapping off.
        _globalOverlay.IsHitTestVisible = false;
        _popupOverlay.IsHitTestVisible = false;

        var shutdownScreen = new ShutdownScreen();
        _screenManager.RegisterScreen(shutdownScreen);

        // Retract the taskbar first so its only animation is the upward slide
        // (see RunSignOutSequenceAsync for why parallel-with-fade looked wrong).
        var desktop = _screenManager.GetScreen<DesktopScreen>("desktop");
        if (desktop != null) await desktop.AnimateTaskbarOutAsync();

        // Signal secondaries to retract their visual taskbars in lockstep
        // with the primary's slide-out so all monitors lose their chrome
        // together. Posted AFTER the primary's await so the primary's
        // upward slide is the visually dominant motion - the secondaries'
        // ~320 ms cubic-in matches the primary's pacing closely enough
        // that the eye reads it as one synchronised retraction.
        DAX.OSI.UI.DesktopScreen.NotifyPrimaryDesktopGone();

        // Multi-monitor: fade every secondary to BLACK in lockstep with
        // the primary's overlay fade. We INTENTIONALLY do NOT close the
        // monitor windows here - that would expose the host OS desktop
        // underneath while shutdownScreen.RunAsync() plays its animation.
        // Instead they stay alive (their black Window.Background showing
        // through the faded content) for the entire shutdown sequence;
        // MainWindow.Closed will tear them down at process exit so they
        // disappear with the primary as the app dies, not before.
        var fadeTask = FadeOverlaysAsync(_globalOverlay.Opacity, 0d, 500);
        var monitorFadeTask = FadeMonitorsAsync(from: 1d, to: 0d, 500);
        var navTask = _screenManager.NavigateToWithCrossfadeAsync(
            "shutdown",
            System.TimeSpan.FromMilliseconds(500));
        await Task.WhenAll(fadeTask, monitorFadeTask, navTask);

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

        // Retract the desktop taskbar BEFORE starting the overlay fade so
        // its only visible motion is the upward slide. If we ran the slide
        // in parallel with FadeOverlaysAsync, the bar would simultaneously
        // animate AND fade (since it lives inside _popupOverlay), which
        // reads as "slides down then dissolves" rather than a clean retract.
        var desktop = _screenManager.GetScreen<DesktopScreen>("desktop");
        if (desktop != null) await desktop.AnimateTaskbarOutAsync();

        // Multi-monitor: secondaries are pure wallpaper extensions and
        // INTENTIONALLY do not participate in the signout fade. Fading
        // their content to 0 would reveal the Window's black background
        // (looks like "the other monitors went dead") instead of leaving
        // the wallpaper visible across the user switch. They simply stay
        // showing wallpaper through the entire signout-to-login round-trip,
        // which feels right for a multi-monitor system.
        //
        // HOWEVER: any DOSIWindow the user opened on a secondary display
        // lives on THAT secondary's _globalOverlay (and any popup chrome
        // on its _popupOverlay) - both completely unrelated to the primary's
        // overlays we're fading above. Fade those secondary overlays in
        // lockstep so application windows leave the screen with the user,
        // while the underlying wallpaper layer stays put.
        var fadeTask = FadeOverlaysAsync(_globalOverlay.Opacity, 0d, 450);
        var monitorWindowFadeTask = FadeMonitorWindowOverlaysAsync(
            from: 1d, to: 0d, 450, disableHitTest: true);
        var navTask = _screenManager.NavigateToWithCrossfadeAsync(
            "signout",
            TimeSpan.FromMilliseconds(450));
        await Task.WhenAll(fadeTask, monitorWindowFadeTask, navTask);

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

        // Reset the global window-translucency multiplier to fully opaque
        // BEFORE the login screen renders. AccentManager.WindowOpacity is a
        // process-wide alpha multiplier applied to every container-style
        // brush (window background, chrome, content, controls, buttons,
        // listbox). DesktopScreen.OnNavigatedTo set it from the user's
        // saved preference on sign-in (e.g. 0.75), and nothing in
        // UserManager.SignOut clears it - so the freshly-built LoginScreen
        // would inherit the leaving user's translucency value and paint
        // every chrome surface at reduced alpha over the host window's
        // black background. The visible result reads as "duller", most
        // pronounced under the Light accent where the surfaces are
        // luminous near-whites and the black-bleed delta is largest.
        // Snapping back to 1.0 here keeps the login screen visually
        // consistent regardless of which user just left.
        DOSIWindow.WindowOpacity = UserManager.DefaultWindowOpacity;

        // Tear down the desktop screen. This unhooks its taskbar / apps menu
        // chrome from _popupOverlay, but the overlay itself stays alive so
        // the next DesktopScreen can attach fresh chrome.
        // Signal secondaries FIRST so their visual taskbars retract /
        // icon layers hide in the same frame the primary's chrome goes
        // away. Without this they'd linger past the primary's slide-out
        // until the next sign-in re-armed the gate.
        DAX.OSI.UI.DesktopScreen.NotifyPrimaryDesktopGone();
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
        => AnimateOpacityAsync([_globalOverlay, _popupOverlay], from, to, durationMs);

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

            // Close the pooled cross-monitor drag ghost. We deliberately
            // keep its OS-level layered window SHOWN for the entire
            // process lifetime to avoid the per-drag transparent-topmost
            // re-show flicker - which means Avalonia's default
            // OnLastWindowClose shutdown mode counts it as a living window
            // and would hang the process exit forever. Closing it here
            // releases that anchor so the app can actually exit.
            DragGhostWindow.Shutdown();
        }
        catch (Exception ex)
        {
            // Never let cleanup throw during shutdown.
            Trace.WriteLine($"[MainWindow] OnSystemShuttingDown failed: {ex}");
        }
    }

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.T && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Ctrl+T opens a terminal anywhere in the lifecycle - boot,
            // login, signed-in desktop, or post-signout login. When no user
            // is signed in, the global overlay may be collapsed / faded
            // (sign-out path) or simply hosting a screen with no chrome
            // (boot / login path); either way we have to make sure the
            // overlay is visible, hit-testable, and opaque BEFORE parenting
            // a new DOSITerminal into it - otherwise the terminal would be
            // invisible / non-interactive and the user would see nothing
            // happen.
            EnsureGlobalOverlayVisible();
            OpenNewTerminal();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Restores the global window overlay to a fully-visible, interactive,
    /// opaque state. Safe to call at any point in the lifecycle; a no-op when
    /// the overlay is already in that state.
    /// </summary>
    private void EnsureGlobalOverlayVisible()
    {
        _globalOverlay.IsVisible = true;
        _globalOverlay.IsHitTestVisible = true;
        _globalOverlay.Opacity = 1d;
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

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // Make sure the Users folder exists on disk before we ask whether anyone has signed up.
        UserManager.Initialize();

        _screenManager.NavigateTo("boot");

        // Multi-monitor: light up DAX.OSI on every other connected display
        // RIGHT AWAY (alongside the boot screen on primary), so extra monitors
        // never briefly flash the host OS desktop during startup. Each
        // secondary shows a wallpaper-only ExtensionScreen and stays up
        // through the entire boot -> login -> desktop -> signout lifecycle.
        //
        // Skipped entirely when the system is configured to launch windowed
        // (SystemSettings.Fullscreen == false): in that mode DAX.OSI is just
        // a regular floating window on the user's host OS, so taking over
        // their secondary monitors with full-screen ExtensionScreens would
        // be wildly out of place. Single primary window only.
        if (SystemCore.Settings.Fullscreen)
        {
            RebuildMonitorWindows();
        }

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

        // Multi-monitor: defensive snap-to-1 in case any prior code path
        // left a secondary's content opacity below full. Signout no longer
        // fades them, but shutdown (interrupted) or future code paths might.
        foreach (var mw in _monitorWindows)
            if (mw.Content is Control c) c.Opacity = 1d;

        // Restore secondary monitors' window + popup overlays that signout
        // faded out and disabled. Without this the new user's apps would
        // be invisible / non-interactive on every secondary display.
        RestoreMonitorWindowOverlays();

        _globalOverlay.IsHitTestVisible = true;
        _popupOverlay.IsHitTestVisible = true;

        // Begin idle-lock tracking for the freshly signed-in user.
        _sessionLock.Start();
    }
}


