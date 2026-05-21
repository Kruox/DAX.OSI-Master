using Avalonia;
using Avalonia.Controls;
using DOSI.CORE.Animations;

namespace DOSI.CORE.UIComponents;

/// <summary>
/// Manages screen navigation and transitions in the DOSI virtual operating system.
/// </summary>
public class ScreenManager
{
    private readonly ContentControl _container;
    private readonly Dictionary<string, Func<DOSIScreen>> _screenFactories = new();
    private readonly Dictionary<string, DOSIScreen> _screenCache = new();

    private DOSIScreen? _currentScreen;

    // ----- Transition cancellation -----
    // Tracks the in-flight crossfade (if any) so a second navigation request
    // arriving mid-transition can finalize the first one cleanly instead of
    // racing with it. The Tween is snapped to its end state, the overlay is
    // torn down on the spot, and the new navigation starts from a stable
    // _currentScreen / _container.Content. Without this, rapid double-clicks
    // on a login tile, or a sign-out fired before a startup crossfade
    // completed, could leave two screens parented under the overlay grid
    // with mismatched opacities.
    private Tween? _activeCrossfade;
    private Action? _activeCrossfadeFinalize;

    public DOSIScreen? CurrentScreen => _currentScreen;
    public string? CurrentScreenId => _currentScreen?.ScreenId;

    public event EventHandler<ScreenNavigationEventArgs>? ScreenChanged;

    public ScreenManager(ContentControl container)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
    }

    /// <summary>
    /// Registers a screen factory for lazy instantiation.
    /// </summary>
    public void RegisterScreen<T>(string screenId) where T : DOSIScreen, new()
    {
        _screenFactories[screenId] = () => new T();
    }

    /// <summary>
    /// Registers a screen factory with a custom factory method.
    /// </summary>
    public void RegisterScreen(string screenId, Func<DOSIScreen> factory)
    {
        _screenFactories[screenId] = factory;
    }

    /// <summary>
    /// Registers an existing screen instance.
    /// </summary>
    public void RegisterScreen(DOSIScreen screen)
    {
        _screenCache[screen.ScreenId] = screen;
        screen.NavigationRequested += OnScreenNavigationRequested;
    }

    /// <summary>
    /// Navigates to the specified screen.
    /// </summary>
    public bool NavigateTo(string screenId, object? parameter = null)
    {
        var screen = GetOrCreateScreen(screenId);
        if (screen == null) return false;

        var previousScreen = _currentScreen;
        previousScreen?.OnNavigatedFrom();

        _currentScreen = screen;
        _container.Content = screen;
        screen.OnNavigatedTo();

        ScreenChanged?.Invoke(this, new ScreenNavigationEventArgs(screenId, parameter));
        return true;
    }

    /// <summary>
    /// Gets a screen by its ID.
    /// </summary>
    public DOSIScreen? GetScreen(string screenId)
    {
        return GetOrCreateScreen(screenId);
    }

    /// <summary>
    /// Gets a typed screen by its ID.
    /// </summary>
    public T? GetScreen<T>(string screenId) where T : DOSIScreen
    {
        return GetOrCreateScreen(screenId) as T;
    }

    private DOSIScreen? GetOrCreateScreen(string screenId)
    {
        if (_screenCache.TryGetValue(screenId, out var cachedScreen))
            return cachedScreen;

        if (_screenFactories.TryGetValue(screenId, out var factory))
        {
            var screen = factory();
            _screenCache[screenId] = screen;
            screen.NavigationRequested += OnScreenNavigationRequested;
            return screen;
        }

        return null;
    }

    private void OnScreenNavigationRequested(object? sender, ScreenNavigationEventArgs e)
    {
        NavigateTo(e.TargetScreenId, e.Parameter);
    }

    /// <summary>
    /// Navigates to the specified screen using a crossfade transition between the
    /// previously displayed screen and the new one.
    /// </summary>
    /// <param name="screenId">The target screen's id.</param>
    /// <param name="duration">Total crossfade duration.</param>
    /// <param name="parameter">Optional navigation parameter forwarded via <see cref="ScreenChanged"/>.</param>
    /// <param name="ease">
    /// Easing curve applied to the wedge's progress. Defaults to <see cref="Easings.Linear"/>
    /// which preserves the "constant coverage" math the wedge relies on; pass
    /// <see cref="Easings.EaseInOutCubic"/> for a weightier feel on user-driven
    /// transitions (desktop arrival), or <see cref="Easings.EaseOutCubic"/> for
    /// settle-to-rest endings (login → desktop).
    /// </param>
    public async Task<bool> NavigateToWithCrossfadeAsync(string screenId, TimeSpan duration,
        object? parameter = null, Func<double, double>? ease = null)
    {
        // If another crossfade is still in flight, finalize it on the spot
        // so we don't end up with two overlapping overlays + two screens
        // both believing they own _container.Content. The finalize closure
        // (set up below) handles overlay teardown + reparenting the
        // then-current incoming screen to Content with full opacity.
        if (_activeCrossfade != null)
        {
            var snap = _activeCrossfadeFinalize;
            _activeCrossfade.Stop(snapToEnd: true);
            _activeCrossfade = null;
            _activeCrossfadeFinalize = null;
            try { snap?.Invoke(); } catch { /* best-effort */ }
        }

        var newScreen = GetOrCreateScreen(screenId);
        if (newScreen == null) return false;

        var previousScreen = _currentScreen;

        // No previous screen to fade from; fall back to a regular navigation.
        if (previousScreen == null)
        {
            return NavigateTo(screenId, parameter);
        }

        previousScreen.OnNavigatedFrom();

        // Host both screens in an overlay grid so they can fade simultaneously.
        // Fully detach previousScreen from the container BEFORE adding it to
        // the overlay - otherwise it briefly lives in two visual trees, which
        // re-fires AttachedToVisualTree and can trigger layout invalidations
        // while a measure pass is already in flight (root cause of past
        // "Infinite layout loop detected" crashes).
        _container.Content = null;

        var overlay = new Grid();
        newScreen.Opacity = 0;
        // Reveal the incoming screen's backdrop (vignette + wallpaper)
        // BEFORE the crossfade starts, snapped to whatever the outgoing
        // screen is currently displaying. This is essential for the
        // two-phase wedge crossfade in RunCrossfadeAsync to work without
        // exposing the host window background: during phase B (outgoing
        // fading from 1 -> 0 over a fully-opaque incoming) the incoming
        // screen has to actually paint pixels everywhere outgoing used
        // to, otherwise the regions the incoming screen's UI doesn't
        // cover (most of the surface - the login is mostly a small card
        // over wallpaper) drop to the window background and produce a
        // black flash. The earlier "hide incoming backdrop for the whole
        // fade, snap it on at the end" trick assumed outgoing stays at
        // full opacity throughout, which the wedge no longer does.
        //
        // The earlier "ghost from compositing two accents/wallpapers"
        // worry doesn't materialise here: during phase A the outgoing
        // fully covers the incoming so the incoming backdrop is
        // invisible, and during phase B the incoming covers fully and
        // the outgoing fades cleanly on top. Pre-snapping the incoming
        // wallpaper source to the outgoing's avoids any visible variant
        // change at the start.
        newScreen.SetWallpaperFrontSourceImmediate(previousScreen.GetWallpaperFrontSource());
        newScreen.SetBackdropVisible(true);
        overlay.Children.Add(previousScreen);
        overlay.Children.Add(newScreen);
        _container.Content = overlay;

        _currentScreen = newScreen;
        newScreen.OnNavigatedTo();

        // Capture a finalize closure that the cancellation path (above)
        // can invoke to bring the overlay down cleanly mid-fade. Same
        // body as the post-await cleanup below, but callable from the
        // tween's snap-to-end branch.
        void FinalizeHandoff()
        {
            _container.Content = null;
            overlay.Children.Clear();
            _container.Content = newScreen;
            newScreen.Opacity = 1;
            previousScreen.Opacity = 1;
            newScreen.OnTransitionComplete();
        }
        _activeCrossfadeFinalize = FinalizeHandoff;

        await RunCrossfadeAsync(previousScreen, newScreen, duration,
            ease ?? Easings.Linear,
            tween => _activeCrossfade = tween);

        // Clear the in-flight marker - if cancellation didn't fire, the
        // tween ran to completion and the standard post-fade cleanup
        // takes over below.
        _activeCrossfade = null;
        _activeCrossfadeFinalize = null;

        // Backdrop is already visible (we revealed it before the fade).
        // Detach both screens from the overlay before reparenting the new one.
        // Order matters: clear the container first so newScreen has no parent
        // when we assign it as Content (avoids the "logical child already has
        // a parent" path that can invalidate layout mid-pass).
        _container.Content = null;
        overlay.Children.Clear();
        _container.Content = newScreen;
        newScreen.Opacity = 1;
        previousScreen.Opacity = 1;

        // Now that the new screen is the sole Content and its backdrop is
        // visible, kick off any post-handoff wallpaper variant animation.
        // Most screens are no-ops here (their desired bitmap matches what's
        // already displayed); DesktopScreen uses this to cross-fade between
        // the soft and sharp variants when the user has wallpaper-blur off.
        newScreen.OnTransitionComplete();

        ScreenChanged?.Invoke(this, new ScreenNavigationEventArgs(screenId, parameter));
        return true;
    }

    /// <summary>
    /// Removes a registered/cached screen, detaches event handlers and disposes
    /// it if it implements <see cref="IDisposable"/>.
    /// </summary>
    public void RemoveScreen(string screenId)
    {
        if (!_screenCache.TryGetValue(screenId, out var screen))
            return;

        screen.NavigationRequested -= OnScreenNavigationRequested;
        _screenCache.Remove(screenId);
        _screenFactories.Remove(screenId);

        if (ReferenceEquals(_currentScreen, screen))
        {
            _currentScreen = null;
        }

        (screen as IDisposable)?.Dispose();
    }

    /// <summary>
    /// Detaches handlers from and disposes every cached screen, then clears the
    /// container. Intended for application shutdown so no DOSIScreen leaks.
    /// </summary>
    public void DisposeAll()
    {
        foreach (var screen in _screenCache.Values)
        {
            screen.NavigationRequested -= OnScreenNavigationRequested;
            (screen as IDisposable)?.Dispose();
        }

        _screenCache.Clear();
        _screenFactories.Clear();
        _currentScreen = null;
        _container.Content = null;
    }

    private static Task RunCrossfadeAsync(Visual fadeOut, Visual fadeIn, TimeSpan duration,
        Func<double, double> ease, Action<Tween>? onStarted = null)
    {
        // Two-phase wedge crossfade. The naive "fade both 0..1 in opposite
        // directions" path produces the alpha-compositing darkening the
        // original implementation worked around (combined coverage
        // 1 - (1-a)(1-b) drops below 1 for the entire middle of the
        // transition, letting the window background bleed through and
        // flashing black on sign-out / shutdown screens that don't paint
        // a solid backdrop).
        //
        // The original workaround held the outgoing screen at Opacity=1
        // for the ENTIRE crossfade and only ever animated the incoming
        // one. That fixed the darkening but introduced a visible artifact
        // at the end: the outgoing screen stayed fully visible right up
        // until the manager swapped Content, so it appeared to "snap"
        // away in a single frame - most obvious on the InitialStartup
        // -> LoginScreen handoff where the wizard's final step lingered
        // behind / around the login card until the very last frame.
        //
        // Wedge fix:
        //   Phase A (first half): incoming fades 0 -> 1, outgoing stays at 1.
        //                         Coverage = 1 (outgoing alone).
        //   Phase B (second half): outgoing fades 1 -> 0, incoming stays at 1.
        //                          Coverage = 1 (incoming alone).
        // Both phases run inside the same tween so the perceived motion
        // is a continuous crossfade with no plateau, and the union of
        // opacities never drops below 1 - so screens with non-opaque
        // backdrops (sign-out, shutdown) still don't expose the window
        // background.
        fadeOut.Opacity = 1;
        fadeIn.Opacity = 0;

        var tcs = new TaskCompletionSource<bool>();
        var tween = Tween.Run(
            durationMs: Math.Max(1, duration.TotalMilliseconds),
            ease: ease,
            apply: t =>
            {
                // First half: bring the new screen up to full opacity over
                // its own backdrop (revealed by NavigateToWithCrossfadeAsync
                // before the fade starts, snapped to the outgoing screen's
                // wallpaper source so there's no visible cutover).
                if (t < 0.5)
                {
                    fadeIn.Opacity = t * 2.0;
                    fadeOut.Opacity = 1.0;
                }
                else
                {
                    // Second half: incoming is already at 1, now retire the
                    // outgoing screen. Linear is fine here - the incoming
                    // is fully covering, so the outgoing's alpha contributes
                    // nothing visible past pixels the incoming doesn't paint
                    // (which on every DOSIScreen is zero, since each owns an
                    // opaque accent + wallpaper backdrop).
                    fadeIn.Opacity = 1.0;
                    fadeOut.Opacity = 1.0 - (t - 0.5) * 2.0;
                }
            },
            onCompleted: () =>
            {
                fadeIn.Opacity = 1;
                fadeOut.Opacity = 0;
                tcs.TrySetResult(true);
            });
        onStarted?.Invoke(tween);
        return tcs.Task;
    }
}
