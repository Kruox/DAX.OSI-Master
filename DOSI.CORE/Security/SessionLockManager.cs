using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DOSI.CORE.Animations;
using DOSI.CORE.UIComponents;
using DOSI.CORE.UserManagement;

namespace DOSI.CORE.Security;

/// <summary>
/// Tracks user idle time and locks the session after a configurable timeout
/// by showing a host-supplied lock screen control over the entire desktop.
/// <para>
/// Lifecycle: the host (typically <c>MainWindow</c>) constructs one instance
/// after sign-in, supplying the <see cref="Panel"/> the lock screen should be
/// hosted in (above all other layers) and a factory that returns a fresh
/// lock-screen control when invoked. The manager wires global pointer/key
/// handlers and a timer; the host calls <see cref="Stop"/> on sign-out and
/// <see cref="Start"/> on the next sign-in.
/// </para>
/// </summary>
public sealed class SessionLockManager : IDisposable
{
    /// <summary>Per-user preference key for idle-lock minutes.</summary>
    public const string IdleMinutesPreferenceKey = "session_lock_minutes";

    /// <summary>Default idle timeout when no preference is saved.</summary>
    public const int DefaultIdleMinutes = 5;

    /// <summary>Smallest accepted idle timeout (clamps lower values).</summary>
    public const int MinIdleMinutes = 1;

    /// <summary>Largest accepted idle timeout (clamps higher values).</summary>
    public const int MaxIdleMinutes = 240;

    /// <summary>Sentinel value disabling the idle lock entirely.</summary>
    public const int DisabledIdleMinutes = 0;

    private readonly Panel _lockHost;
    private readonly InputElement _inputSource;
    private readonly Func<DOSIUser, Control> _lockScreenFactory;

    private DispatcherTimer? _idleTimer;
    private DateTime _lastActivityUtc = DateTime.UtcNow;
    private Control? _activeLockControl;
    private Tween? _activeFadeTween;
    private bool _running;
    private bool _idleWarningShown;

    /// <summary>
    /// Number of seconds before the lock fires that the manager will show a
    /// "locking soon" toast notification. Set to 0 to disable the warning.
    /// </summary>
    public const int IdleWarningSeconds = 60;

    /// <summary>Duration of the lock screen fade-in / fade-out animations.</summary>
    public const int LockFadeMilliseconds = 350;

    /// <summary>The currently-active manager, or <c>null</c> if none has been started.</summary>
    public static SessionLockManager? Instance { get; private set; }

    /// <summary><c>true</c> while the lock screen is visible.</summary>
    public bool IsLocked => _activeLockControl != null;

    /// <summary>Raised when the idle timer fires and the session is locked.</summary>
    public event EventHandler? Locked;

    /// <summary>Raised once the user has unlocked the session.</summary>
    public event EventHandler? Unlocked;

    /// <summary>
    /// Raised when the user clicks "Sign out" from the lock screen instead of
    /// unlocking. The host should run its sign-out sequence.
    /// </summary>
    public event EventHandler? SignOutRequested;

    /// <param name="lockHost">
    /// Top-most panel where the lock screen is added when triggered. It should
    /// sit above all desktop chrome so nothing is interactable while locked.
    /// </param>
    /// <param name="inputSource">
    /// The visual whose pointer/keyboard events count as "user activity"
    /// (typically the application <c>Window</c>).
    /// </param>
    /// <param name="lockScreenFactory">
    /// Factory invoked each time the session locks; returns a control that
    /// fills the host. The control should expose <c>Unlocked</c> and
    /// <c>SignOutRequested</c> events (we discover them by name) - the
    /// <c>DAX.OSI.UI.LockScreen</c> implementation in this repo does both.
    /// </param>
    public SessionLockManager(Panel lockHost, InputElement inputSource,
                              Func<DOSIUser, Control> lockScreenFactory)
    {
        _lockHost = lockHost ?? throw new ArgumentNullException(nameof(lockHost));
        _inputSource = inputSource ?? throw new ArgumentNullException(nameof(inputSource));
        _lockScreenFactory = lockScreenFactory ?? throw new ArgumentNullException(nameof(lockScreenFactory));
    }

    /// <summary>
    /// Starts tracking idle time for <see cref="UserManager.CurrentUser"/>.
    /// No-op if already running or if no user is signed in.
    /// </summary>
    public void Start()
    {
        if (_running) return;
        if (UserManager.CurrentUser == null) return;

        _running = true;
        Instance = this;
        _lastActivityUtc = DateTime.UtcNow;

        _inputSource.AddHandler(InputElement.PointerMovedEvent, OnActivity, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        _inputSource.AddHandler(InputElement.PointerPressedEvent, OnActivity, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        _inputSource.AddHandler(InputElement.KeyDownEvent, OnActivity, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _idleTimer.Tick += OnIdleTick;
        _idleTimer.Start();
    }

    /// <summary>
    /// Stops the manager: removes input handlers, stops the timer, and dismisses
    /// the lock screen if one is active. Call on sign-out / shutdown.
    /// </summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;

        _inputSource.RemoveHandler(InputElement.PointerMovedEvent, OnActivity);
        _inputSource.RemoveHandler(InputElement.PointerPressedEvent, OnActivity);
        _inputSource.RemoveHandler(InputElement.KeyDownEvent, OnActivity);

        if (_idleTimer != null) { _idleTimer.Stop(); _idleTimer.Tick -= OnIdleTick; _idleTimer = null; }

        if (_activeLockControl != null) DismissImmediate();

        if (Instance == this) Instance = null;
    }

    /// <summary>Records user activity, resetting the idle countdown.</summary>
    public void NotifyActivity() => _lastActivityUtc = DateTime.UtcNow;

    /// <summary>Returns the configured idle timeout in minutes for the current user.</summary>
    public static int GetIdleMinutesForCurrentUser()
    {
        var user = UserManager.CurrentUser;
        if (user == null) return DefaultIdleMinutes;
        if (!user.Preferences.TryGetValue(IdleMinutesPreferenceKey, out var raw)) return DefaultIdleMinutes;
        if (!int.TryParse(raw, out var minutes)) return DefaultIdleMinutes;
        if (minutes <= DisabledIdleMinutes) return DisabledIdleMinutes;
        return Math.Clamp(minutes, MinIdleMinutes, MaxIdleMinutes);
    }

    /// <summary>Persists the current user's idle-lock preference (in minutes).</summary>
    public static void SetIdleMinutesForCurrentUser(int minutes)
    {
        var user = UserManager.CurrentUser;
        if (user == null) return;
        if (minutes < DisabledIdleMinutes) minutes = DisabledIdleMinutes;
        if (minutes > MaxIdleMinutes) minutes = MaxIdleMinutes;
        user.Preferences[IdleMinutesPreferenceKey] = minutes.ToString();
        UserManager.SaveUser(user);
    }

    /// <summary>
    /// Locks the session immediately (e.g. invoked from the apps menu
    /// "Lock" item).
    /// </summary>
    public void LockNow() => ShowLockScreenInternal();

    private void OnActivity(object? sender, RoutedEventArgs e)
    {
        if (_activeLockControl != null) return; // ignore activity while locked
        _idleWarningShown = false;
        NotifyActivity();
    }

    private void OnIdleTick(object? sender, EventArgs e)
    {
        if (_activeLockControl != null) return;
        var minutes = GetIdleMinutesForCurrentUser();
        if (minutes <= DisabledIdleMinutes) return;

        var idle = DateTime.UtcNow - _lastActivityUtc;
        var lockAtSeconds = minutes * 60d;
        var idleSeconds = idle.TotalSeconds;

        // One-minute warning: only show when the configured timeout is at
        // least 2 minutes (a 1-minute timeout would warn immediately).
        if (IdleWarningSeconds > 0 && !_idleWarningShown && minutes >= 2 &&
            idleSeconds >= lockAtSeconds - IdleWarningSeconds && idleSeconds < lockAtSeconds)
        {
            _idleWarningShown = true;
            try
            {
                DOSIPopNotification.Show(
                    "Session will lock in 1 minute. Move the mouse to stay signed in.",
                    TimeSpan.FromSeconds(8));
            }
            catch { /* host may not be set yet during early boot - ignore */ }
        }

        if (idleSeconds >= lockAtSeconds) ShowLockScreenInternal();
    }

    private void ShowLockScreenInternal()
    {
        if (_activeLockControl != null) return;
        var user = UserManager.CurrentUser;
        if (user == null) return;

        var control = _lockScreenFactory(user);
        if (control == null) return;

        // Discover Unlocked / SignOutRequested events via reflection so the
        // manager doesn't take a hard dependency on a specific lock-screen type.
        WireEvent(control, "Unlocked", () => DismissWithFade());
        WireEvent(control, "SignOutRequested", () =>
        {
            // Sign-out doesn't need a fade-out (the host runs its own farewell
            // animation), but we still need to remove our control + fire the
            // event so the host can react.
            DismissImmediate();
            SignOutRequested?.Invoke(this, EventArgs.Empty);
        });

        _activeLockControl = control;
        control.Opacity = 0;
        _lockHost.Children.Add(control);
        _lockHost.IsHitTestVisible = true;
        _lockHost.IsVisible = true;

        SecurityAuditLog.AppendForUser(user.Username, SecurityAuditEventType.SessionLocked, null);
        Locked?.Invoke(this, EventArgs.Empty);

        // Fade in over LockFadeMilliseconds (parallel with the host fading
        // its own overlays out).
        _activeFadeTween?.Stop(snapToEnd: true);
        _activeFadeTween = Tween.Run(
            durationMs: LockFadeMilliseconds,
            ease: Easings.EaseOutCubic,
            apply: t => { if (_activeLockControl != null) _activeLockControl.Opacity = t; });
    }

    private void DismissWithFade()
    {
        if (_activeLockControl == null) return;
        var control = _activeLockControl;
        // Fire Unlocked immediately so the host can fade its overlays back
        // in parallel with our fade-out (no perceived dead time).
        Unlocked?.Invoke(this, EventArgs.Empty);

        _activeFadeTween?.Stop(snapToEnd: true);
        _activeFadeTween = Tween.Run(
            durationMs: LockFadeMilliseconds,
            ease: Easings.EaseInCubic,
            apply: t => { control.Opacity = 1d - t; },
            onCompleted: DismissImmediate);
    }

    private void DismissImmediate()
    {
        if (_activeLockControl == null) return;
        _activeFadeTween?.Stop();
        _activeFadeTween = null;
        _lockHost.Children.Remove(_activeLockControl);
        _activeLockControl = null;
        _lastActivityUtc = DateTime.UtcNow;
        _idleWarningShown = false;
    }

    private static void WireEvent(object target, string eventName, Action handler)
    {
        var evt = target.GetType().GetEvent(eventName);
        if (evt == null) return;
        var del = Delegate.CreateDelegate(evt.EventHandlerType!, handler.Target!,
            handler.Method.Name, false, false);
        // Fallback path: use a thunked EventHandler since the event signature is EventHandler.
        if (del == null)
        {
            EventHandler thunk = (_, _) => handler();
            evt.AddEventHandler(target, thunk);
            return;
        }
        evt.AddEventHandler(target, del);
    }

    public void Dispose() => Stop();
}
