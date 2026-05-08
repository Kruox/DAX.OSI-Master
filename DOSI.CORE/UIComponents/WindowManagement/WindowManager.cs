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
    private readonly Stack<DOSIWindow> _zOrderStack = new();
    private DOSIWindow? _focusedWindow;
    private int _baseZIndex = 100;

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
    }

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

        // Position window
        if (x.HasValue)
            window.WindowX = x.Value;
        else
            window.WindowX = CalculateCascadeX();

        if (y.HasValue)
            window.WindowY = Math.Max(TopWorkAreaInset, y.Value);
        else
            window.WindowY = CalculateCascadeY();

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
            _windows[i].ZIndex = _baseZIndex + i;
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

