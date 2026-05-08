using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

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
    public async Task<bool> NavigateToWithCrossfadeAsync(string screenId, TimeSpan duration, object? parameter = null)
    {
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
        // While the two screens are stacked, hide the incoming screen's
        // accent vignette + wallpaper layers. Both screens own their own
        // backdrop, so without this we composite two accents and two
        // wallpapers on top of each other for the duration of the fade,
        // producing a visible "ghost" (most obvious going from LoginScreen
        // to DesktopScreen, where both render the user's accent + wallpaper).
        // The outgoing screen's backdrop continues to render at full opacity
        // and the new screen's UI fades in on top of it; the cutover at the
        // end of the fade is invisible because both screens reference the
        // same WallpaperManager bitmap and AccentManager brush.
        newScreen.SetBackdropVisible(false);
        overlay.Children.Add(previousScreen);
        overlay.Children.Add(newScreen);
        _container.Content = overlay;

        _currentScreen = newScreen;
        newScreen.OnNavigatedTo();

        await RunCrossfadeAsync(previousScreen, newScreen, duration);

        // Restore the new screen's backdrop before reparenting it so that
        // when it's promoted back to top-level Content there is no frame
        // where the desktop has no wallpaper / accent behind it.
        //
        // Snap the new screen's wallpaper layer to whatever the outgoing
        // screen had on display first. The two screens may want different
        // variants of the same wallpaper (e.g. DesktopScreen with blur
        // disabled coming in over LoginScreen which always shows the soft
        // variant, or DesktopScreen handing off to SignoutScreen / Shutdown
        // which always show the soft variant). Without the snap, revealing
        // the new screen's backdrop produces a visible "cut" from the
        // outgoing variant straight to the incoming variant - which the
        // hidden-backdrop cross-fade trick would otherwise hide a smooth
        // transition behind. With the snap, the reveal is seamless and
        // OnTransitionComplete (called below) animates any variant
        // difference visibly on top of the freshly-revealed backdrop.
        newScreen.SetWallpaperFrontSourceImmediate(previousScreen.GetWallpaperFrontSource());
        newScreen.SetBackdropVisible(true);

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

    private static Task RunCrossfadeAsync(Visual fadeOut, Visual fadeIn, TimeSpan duration)
    {
        var tcs = new TaskCompletionSource<bool>();

        // Keep the outgoing screen fully opaque and fade the new screen in on top.
        // This avoids the alpha-compositing darkening that occurs when both layers
        // are partially transparent (combined coverage 1 - (1-a)(1-b) < 1, letting
        // the window background bleed through).
        fadeOut.Opacity = 1;
        fadeIn.Opacity = 0;

        // ~60 FPS
        var interval = TimeSpan.FromMilliseconds(16);
        var totalMs = Math.Max(1, duration.TotalMilliseconds);
        var startTime = DateTime.UtcNow;

        var timer = new DispatcherTimer { Interval = interval };
        timer.Tick += (_, _) =>
        {
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            var t = Math.Clamp(elapsed / totalMs, 0d, 1d);

            fadeIn.Opacity = t;

            if (t >= 1d)
            {
                timer.Stop();
                tcs.TrySetResult(true);
            }
        };
        timer.Start();

        return tcs.Task;
    }
}
