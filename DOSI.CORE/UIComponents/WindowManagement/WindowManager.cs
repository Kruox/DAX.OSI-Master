using Avalonia.Controls;
using DOSI.CORE.UIComponents;

namespace DOSI.CORE.UIComponents.WindowManagement;

/// <summary>
/// Manages all windows in the DOSI virtual operating system.
/// Handles z-ordering, focus management, window lifecycle, and window operations.
/// </summary>
public class WindowManager
{
    private static WindowManager? _instance;
    public static WindowManager? Instance => _instance;

    private readonly Canvas _desktop;
    private readonly List<DOSIWindow> _windows = [];
    private DOSIWindow? _focusedWindow;
    private const int BaseZIndex = 100;

    /// <summary>
    /// Gets all managed windows.
    /// </summary>
    public IReadOnlyList<DOSIWindow> Windows => _windows.AsReadOnly();

    /// <summary>
    /// Gets the currently focused window.
    /// </summary>
    public DOSIWindow? FocusedWindow => _focusedWindow;

    /// <summary>
    /// Gets the number of open windows.
    /// </summary>
    public int WindowCount => _windows.Count;

    /// <summary>
    /// Top reserved area (in pixels) of the desktop canvas that windows must
    /// not overlap. Hosts that render persistent chrome above the windows
    /// (taskbar, menu bar, ...) set this so new windows cascade below it,
    /// drag-clamp can't push a window under it, and Maximize fills only the
    /// remaining work area instead of the full canvas.
    /// </summary>
    public double TopWorkAreaInset { get; set; } = 0;

    /// <summary>
    /// Counterpart to <see cref="TopWorkAreaInset"/>: pixels reserved
    /// at the BOTTOM of the desktop for chrome (a bottom-docked
    /// taskbar). Maximize subtracts this from the available height so
    /// a maximized window doesn't render under the bottom taskbar, and
    /// new-window placement keeps clear of it. Defaults to 0 - chrome
    /// owners (DesktopScreen, ExtensionScreen) write to it on dock and
    /// reset to 0 on detach.
    /// </summary>
    public double BottomWorkAreaInset { get; set; } = 0;

    // Events
    public event EventHandler<DOSIWindowEventArgs>? WindowOpened;
    public event EventHandler<DOSIWindowEventArgs>? WindowClosed;
    public event EventHandler<DOSIWindowFocusEventArgs>? WindowFocusChanged;
    public event EventHandler<DOSIWindowEventArgs>? WindowsChanged;

    public WindowManager(Canvas desktop) : this(desktop, makeActive: true) { }

    /// <summary>
    /// Creates a WindowManager bound to <paramref name="desktop"/>. When
    /// <paramref name="makeActive"/> is false the new instance does NOT replace
    /// the global <see cref="Instance"/>. Useful for top-level overlay layers
    /// (e.g. global terminals) that should manage their own windows without
    /// stealing focus routing from the active screen's manager.
    /// </summary>
    public WindowManager(Canvas desktop, bool makeActive)
    {
        _desktop = desktop ?? throw new ArgumentNullException(nameof(desktop));
        if (makeActive)
            _instance = this;

        // Wire snap-to-edges. WindowSnapManager attaches itself to every
        // open + future window's drag pipeline and renders its own
        // preview Border into the desktop canvas; it lives as long as
        // this WindowManager does. No DesktopScreen / ExtensionScreen
        // wiring required - every monitor that builds a WindowManager
        // gets snap for free, which is what we want.
        _snapManager = new WindowSnapManager(desktop, this);
    }

    private readonly WindowSnapManager _snapManager;

    /// <summary>
    /// Promotes this WindowManager instance to be the current <see cref="Instance"/>.
    /// Call when the owning screen becomes active so global shortcuts (e.g. Ctrl+T)
    /// open windows on the correct desktop canvas.
    /// </summary>
    public void MakeActive()
    {
        _instance = this;
    }

    /// <summary>
    /// Opens a new window and adds it to the desktop.
    /// </summary>
    public void OpenWindow(DOSIWindow window, double? x = null, double? y = null)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (_windows.Contains(window))
            return;

        _windows.Add(window);
        window.OwnerManager = this;

        // Restore last-known geometry per window Title when the caller
        // didn't pin a position. An explicit x/y from the caller always
        // wins (file-explorer-double-click drops a viewer at a specific
        // spot, drag-and-drop, etc.), but a vanilla LaunchApplication call
        // gets the saved layout - the single biggest QOL win for window
        // management. Skipped on the very first run for a window title -
        // CalculateCascadeX/Y still produces a sane default.
        var saved = (!x.HasValue && !y.HasValue)
            ? WindowGeometryRegistry.Get(window.Title)
            : null;

        // Position window
        if (x.HasValue)
            window.WindowX = x.Value;
        else if (saved != null)
            window.WindowX = saved.X;
        else
            window.WindowX = CalculateCascadeX();

        if (y.HasValue)
            window.WindowY = Math.Max(TopWorkAreaInset, y.Value);
        else if (saved != null)
            window.WindowY = Math.Max(TopWorkAreaInset, saved.Y);
        else
            window.WindowY = CalculateCascadeY();

        // Restore size too if we have it. Bound it to a minimum so we
        // don't restore a tiny window the user accidentally shrank to
        // 1x1 in a previous session.
        if (saved != null)
        {
            if (saved.Width  > 80) window.WindowWidth  = saved.Width;
            if (saved.Height > 60) window.WindowHeight = saved.Height;
        }

        // Add to desktop
        _desktop.Children.Add(window);

        // Set z-order and focus
        BringToFront(window);

        // Play open animation
        _ = window.PlayOpenAnimationAsync();

        WindowOpened?.Invoke(this, new DOSIWindowEventArgs(window));
        WindowsChanged?.Invoke(this, new DOSIWindowEventArgs(window));
    }

    /// <summary>
    /// Closes and removes a window from the desktop.
    /// </summary>
    public void CloseWindow(DOSIWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!_windows.Contains(window))
            return;

        // Give the window a chance to release native / unmanaged resources
        // (e.g. DOSIWebBrowser disposing its WebView2 HWND). Without this,
        // shutdown - which calls CloseAllWindows directly - bypasses each
        // window's own Closing handler and the native bits leak / linger
        // visible after the Avalonia chrome is gone.
        window.NotifyClosingForRemoval();

        // Capture the last-known geometry so re-opening this kind of
        // window lands in the same place. Done before we strip the
        // window from _windows / _desktop so the X/Y/Size readings are
        // still meaningful (a removed window's coordinates can be reset
        // by some teardown paths).
        try
        {
            WindowGeometryRegistry.Save(window.Title,
                window.WindowX, window.WindowY,
                window.WindowWidth, window.WindowHeight);
        }
        catch { /* persistence is best-effort; never break window close */ }

        _windows.Remove(window);
        _desktop.Children.Remove(window);
        window.OwnerManager = null;

        // Update focus if this was the focused window
        if (_focusedWindow == window)
        {
            _focusedWindow = null;
            FocusTopWindow();
        }

        RecalculateZOrder();

        WindowClosed?.Invoke(this, new DOSIWindowEventArgs(window));
        WindowsChanged?.Invoke(this, new DOSIWindowEventArgs(window));
    }

    /// <summary>
    /// Closes all open windows.
    /// </summary>
    public void CloseAllWindows()
    {
        var windowsToClose = _windows.ToList();
        foreach (var window in windowsToClose)
        {
            CloseWindow(window);
        }
    }

    /// <summary>
    /// Removes <paramref name="window"/> from this manager WITHOUT firing
    /// <see cref="WindowClosed"/> or running close animations / native
    /// teardown. Used by the multi-monitor cross-display drag handoff to
    /// hand a window over to a different monitor's manager via
    /// <see cref="AdoptWindow"/>. Pair every <c>RelinquishWindow</c> with
    /// a matching <c>AdoptWindow</c> on the receiving manager - otherwise
    /// the window is orphaned (no canvas parent, no manager) and will
    /// silently disappear.
    /// </summary>
    public void RelinquishWindow(DOSIWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!_windows.Contains(window)) return;

        _windows.Remove(window);
        _desktop.Children.Remove(window);
        window.OwnerManager = null;

        if (_focusedWindow == window)
        {
            _focusedWindow = null;
            FocusTopWindow();
        }

        RecalculateZOrder();

        // Intentionally NOT firing WindowClosed - the window is alive,
        // just temporarily unparented. WindowsChanged fires so any
        // observers (taskbar item lists, etc.) refresh.
        WindowsChanged?.Invoke(this, new DOSIWindowEventArgs(window));
    }

    /// <summary>
    /// Quietly adopts a window that was previously <see cref="RelinquishWindow"/>ed
    /// from another <see cref="WindowManager"/>. Skips the cascade-position
    /// logic and the open animation that <see cref="OpenWindow"/> runs, so a
    /// drag-handoff feels like a continuous motion instead of a teleport
    /// followed by a pop. The caller is responsible for placing the window
    /// at sensible <paramref name="x"/>/<paramref name="y"/> coords (typically
    /// chosen so the cursor stays inside the title bar).
    /// </summary>
    public void AdoptWindow(DOSIWindow window, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (_windows.Contains(window)) return;

        _windows.Add(window);
        window.OwnerManager = this;

        // Re-parent BEFORE writing position. WindowX/WindowY setters use
        // Canvas.SetLeft/SetTop attached properties, and Width/Height on
        // Layoutable triggers InvalidateMeasure / InvalidateArrange routed
        // through the control's current LayoutManager. If we set position
        // while the control is unparented (RelinquishWindow removed it
        // from the source canvas just before this call), the layout
        // invalidation can be queued against a stale LayoutManager
        // reference - which throws "Attempt to call InvalidateArrange on
        // wrong LayoutManager" the moment the dispatcher tries to flush
        // the layout. Adding to the new canvas FIRST attaches the control
        // to the target TopLevel's LayoutManager so subsequent property
        // changes route correctly.
        _desktop.Children.Add(window);
        window.WindowX = x;
        window.WindowY = Math.Max(TopWorkAreaInset, y);

        BringToFront(window);

        WindowOpened?.Invoke(this, new DOSIWindowEventArgs(window));
        WindowsChanged?.Invoke(this, new DOSIWindowEventArgs(window));
    }

    /// <summary>
    /// Brings a window to the front (highest z-order) and gives it focus.
    /// </summary>
    public void BringToFront(DOSIWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!_windows.Contains(window))
            return;

        // Move window to end of list (highest z-order)
        _windows.Remove(window);
        _windows.Add(window);

        RecalculateZOrder();
        SetFocus(window);
    }

    /// <summary>
    /// Sends a window to the back (lowest z-order).
    /// </summary>
    public void SendToBack(DOSIWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!_windows.Contains(window))
            return;

        // Move window to beginning of list (lowest z-order)
        _windows.Remove(window);
        _windows.Insert(0, window);

        RecalculateZOrder();

        // Focus the new top window
        FocusTopWindow();
    }

    /// <summary>
    /// Moves a window one level up in the z-order.
    /// </summary>
    public void MoveUp(DOSIWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var index = _windows.IndexOf(window);
        if (index < 0 || index >= _windows.Count - 1)
            return;

        _windows.RemoveAt(index);
        _windows.Insert(index + 1, window);

        RecalculateZOrder();
    }

    /// <summary>
    /// Moves a window one level down in the z-order.
    /// </summary>
    public void MoveDown(DOSIWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var index = _windows.IndexOf(window);
        if (index <= 0)
            return;

        _windows.RemoveAt(index);
        _windows.Insert(index - 1, window);

        RecalculateZOrder();
    }

    /// <summary>
    /// Sets focus to the specified window.
    /// </summary>
    public void SetFocus(DOSIWindow? window)
    {
        if (_focusedWindow == window)
            return;

        var previousFocused = _focusedWindow;

        // Remove focus from previous window
        if (previousFocused != null)
        {
            previousFocused.IsFocused = false;
        }

        _focusedWindow = window;

        // Set focus to new window
        if (window != null)
        {
            window.IsFocused = true;

            // Restore if minimized
            if (window.WindowState == DOSIWindowState.Minimized)
            {
                window.WindowState = DOSIWindowState.Normal;
            }
        }

        WindowFocusChanged?.Invoke(this, new DOSIWindowFocusEventArgs(window!, window != null));
    }

    /// <summary>
    /// Clears focus from all windows.
    /// </summary>
    public void ClearFocus()
    {
        SetFocus(null);
    }

    /// <summary>
    /// Gets the window at the specified point, if any.
    /// </summary>
    public DOSIWindow? GetWindowAt(double x, double y)
    {
        // Check windows in reverse z-order (top to bottom)
        for (int i = _windows.Count - 1; i >= 0; i--)
        {
            var window = _windows[i];
            if (window.WindowState == DOSIWindowState.Minimized)
                continue;

            var bounds = new Avalonia.Rect(window.WindowX, window.WindowY, window.Width, window.Height);
            if (bounds.Contains(new Avalonia.Point(x, y)))
                return window;
        }

        return null;
    }

    /// <summary>
    /// Gets all windows in z-order (bottom to top).
    /// </summary>
    public IEnumerable<DOSIWindow> GetWindowsInZOrder()
    {
        return _windows.AsReadOnly();
    }

    /// <summary>
    /// Gets all windows in reverse z-order (top to bottom).
    /// </summary>
    public IEnumerable<DOSIWindow> GetWindowsInReverseZOrder()
    {
        return _windows.AsEnumerable().Reverse();
    }

    /// <summary>
    /// Gets the topmost window.
    /// </summary>
    public DOSIWindow? GetTopWindow()
    {
        return _windows.LastOrDefault(w => w.WindowState != DOSIWindowState.Minimized);
    }

    /// <summary>
    /// Gets the bottommost window.
    /// </summary>
    public DOSIWindow? GetBottomWindow()
    {
        return _windows.FirstOrDefault(w => w.WindowState != DOSIWindowState.Minimized);
    }

    /// <summary>
    /// Minimizes all windows.
    /// </summary>
    public void MinimizeAll()
    {
        foreach (var window in _windows)
        {
            if (window.CanMinimize)
                window.WindowState = DOSIWindowState.Minimized;
        }
        ClearFocus();
    }

    /// <summary>
    /// Restores all minimized windows.
    /// </summary>
    public void RestoreAll()
    {
        foreach (var window in _windows)
        {
            if (window.WindowState == DOSIWindowState.Minimized)
                window.WindowState = DOSIWindowState.Normal;
        }
        FocusTopWindow();
    }

    /// <summary>
    /// Cascades all windows.
    /// </summary>
    public void CascadeWindows()
    {
        const double offsetX = 30;
        const double offsetY = 30;
        double x = 20;
        double y = 20;

        foreach (var window in _windows)
        {
            if (window.WindowState == DOSIWindowState.Maximized)
                window.WindowState = DOSIWindowState.Normal;

            if (window.WindowState != DOSIWindowState.Minimized)
            {
                window.WindowX = x;
                window.WindowY = y;
                x += offsetX;
                y += offsetY;
            }
        }
    }

    /// <summary>
    /// Tiles all windows horizontally.
    /// </summary>
    public void TileHorizontal()
    {
        var visibleWindows = _windows.Where(w => w.WindowState != DOSIWindowState.Minimized).ToList();
        if (visibleWindows.Count == 0) return;

        var height = _desktop.Bounds.Height / visibleWindows.Count;
        double y = 0;

        foreach (var window in visibleWindows)
        {
            if (window.WindowState == DOSIWindowState.Maximized)
                window.WindowState = DOSIWindowState.Normal;

            window.WindowX = 0;
            window.WindowY = y;
            window.Width = _desktop.Bounds.Width;
            window.Height = height;
            y += height;
        }
    }

    /// <summary>
    /// Tiles all windows vertically.
    /// </summary>
    public void TileVertical()
    {
        var visibleWindows = _windows.Where(w => w.WindowState != DOSIWindowState.Minimized).ToList();
        if (visibleWindows.Count == 0) return;

        var width = _desktop.Bounds.Width / visibleWindows.Count;
        double x = 0;

        foreach (var window in visibleWindows)
        {
            if (window.WindowState == DOSIWindowState.Maximized)
                window.WindowState = DOSIWindowState.Normal;

            window.WindowX = x;
            window.WindowY = 0;
            window.Width = width;
            window.Height = _desktop.Bounds.Height;
            x += width;
        }
    }

    /// <summary>
    /// Cycles focus to the next window.
    /// </summary>
    public void FocusNextWindow()
    {
        var visibleWindows = _windows.Where(w => w.WindowState != DOSIWindowState.Minimized).ToList();
        if (visibleWindows.Count == 0) return;

        if (_focusedWindow == null)
        {
            BringToFront(visibleWindows[0]);
            return;
        }

        var currentIndex = visibleWindows.IndexOf(_focusedWindow);
        var nextIndex = (currentIndex + 1) % visibleWindows.Count;
        BringToFront(visibleWindows[nextIndex]);
    }

    /// <summary>
    /// Cycles focus to the previous window.
    /// </summary>
    public void FocusPreviousWindow()
    {
        var visibleWindows = _windows.Where(w => w.WindowState != DOSIWindowState.Minimized).ToList();
        if (visibleWindows.Count == 0) return;

        if (_focusedWindow == null)
        {
            BringToFront(visibleWindows[^1]);
            return;
        }

        var currentIndex = visibleWindows.IndexOf(_focusedWindow);
        var prevIndex = currentIndex <= 0 ? visibleWindows.Count - 1 : currentIndex - 1;
        BringToFront(visibleWindows[prevIndex]);
    }

    private void RecalculateZOrder()
    {
        for (int i = 0; i < _windows.Count; i++)
        {
            _windows[i].ZIndex = BaseZIndex + i;
        }
    }

    private void FocusTopWindow()
    {
        var topWindow = GetTopWindow();
        SetFocus(topWindow);
    }

    private double CalculateCascadeX()
    {
        const double offset = 30;
        const double startX = 50;
        return startX + (_windows.Count * offset) % (_desktop.Bounds.Width - 200);
    }

    private double CalculateCascadeY()
    {
        const double offset = 30;
        const double startY = 50;
        var available = Math.Max(1, _desktop.Bounds.Height - 200 - TopWorkAreaInset);
        return TopWorkAreaInset + startY + (_windows.Count * offset) % available;
    }
}

