using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DOSI.CORE.AccentManagement;
using DOSI.CORE.UIComponents.WindowManagement;

namespace DOSI.CORE.UIComponents;

/// <summary>
/// Represents a window in the DOSI virtual operating system.
/// Provides a complete window experience similar to traditional desktop OS windows.
/// </summary>
public class DOSIWindow : UserControl
{
    #region Fields

    private readonly Border _chromeRoot;
    private readonly TextBlock _titleText;
    private readonly Panel _iconHost;
    private readonly Button _minimizeButton;
    private readonly Button _maximizeButton;
    private readonly Button _closeButton;

    private readonly ContentControl _contentHost;
    private readonly Grid _rootGrid;
    private readonly Border _windowBorder;
    private readonly Border _contentContainer;

    private readonly Border _resizeTop;
    private readonly Border _resizeBottom;
    private readonly Border _resizeLeft;
    private readonly Border _resizeRight;
    private readonly Border _resizeTopLeft;
    private readonly Border _resizeTopRight;
    private readonly Border _resizeBottomLeft;
    private readonly Border _resizeBottomRight;

    private Point _dragStartPoint;
    private Point _windowStartPosition;
    private bool _isDragging;
    private bool _isResizing;
    private ResizeDirection _resizeDirection;
    private Size _resizeStartSize;
    private Point _resizeStartPosition;

    // Multi-monitor: drag-ghost state. The actual ghost Window is pooled
    // process-wide on DragGhostWindow.Shared so the transparent topmost
    // window only gets allocated once (avoiding a per-drag flicker).
    //
    // Two-stage activation:
    //   * "armed"  - drag started on a multi-monitor system; snapshot has
    //                been taken and ghost is ready to be shown, but source
    //                is still visible. This stage costs nothing visually
    //                and avoids the swap-to-ghost flash for same-monitor
    //                drags (which never need the ghost).
    //   * "shown"  - cursor has actually crossed outside the source
    //                TopLevel's bounds, so we now hide the source and show
    //                the ghost. Toggled back to "armed" if the cursor
    //                returns to source bounds (e.g. user wiggles back and
    //                forth across the monitor boundary mid-drag).
    private bool _dragGhostArmed;
    private bool _dragGhostShown;
    private Avalonia.Media.Imaging.RenderTargetBitmap? _dragGhostSnapshot;
    private Avalonia.PixelPoint _dragGhostCursorOffset;
    private Avalonia.Controls.TopLevel? _dragSourceTopLevel;

    // Snap-restore-on-drag state. WindowSnapManager calls MarkSnapped after
    // animating the window into a snap zone (left half / right half / etc.)
    // with the size the window had BEFORE the snap. The next drag then
    // restores those dimensions and re-anchors the cursor inside the title
    // bar - matching Windows' "drag a snapped window to unsnap" gesture.
    // Cleared when the user explicitly resizes via a grip (so a manually-
    // sized post-snap state isn't blown away by the next drag) or after
    // the restore fires.
    private bool _isSnapped;
    private double _preSnapWidth;
    private double _preSnapHeight;

    private DOSIWindowState _windowState = DOSIWindowState.Normal;
    private Rect _restoreBounds;
    private int _zIndex;
    private bool _isFocused;
    private bool _isAnimating;
    private bool _isClosing;

    private const double ResizeGripSize = 5;
    private const double CornerGripSize = 10;
    private const double ShadowMargin = 50;  // Space for shadow to render

    // Animation durations
    private static readonly TimeSpan OpenAnimationDuration = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan CloseAnimationDuration = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan StateAnimationDuration = TimeSpan.FromMilliseconds(250);

    private static AccentManager Accents => AccentManager.Instance;

    #endregion

    #region Properties

    public string Title
    {
        get => _titleText.Text ?? string.Empty;
        set => _titleText.Text = value;
    }

    public new object? Content
    {
        get => _contentHost.Content;
        set => _contentHost.Content = value;
    }

    public Control? Icon
    {
        get => _iconHost.Children.FirstOrDefault();
        set
        {
            _iconHost.Children.Clear();
            if (value != null)
                _iconHost.Children.Add(value);
            else
                _iconHost.Children.Add(CreateDefaultIcon());
        }
    }

    public DOSIWindowState WindowState
    {
        get => _windowState;
        set => SetWindowState(value);
    }

    public new int ZIndex
    {
        get => _zIndex;
        internal set
        {
            _zIndex = value;
            SetValue(Canvas.ZIndexProperty, value);
        }
    }

    public new bool IsFocused
    {
        get => _isFocused;
        internal set
        {
            if (_isFocused != value)
            {
                _isFocused = value;
                UpdateFocusVisuals();
                FocusChanged?.Invoke(this, new DOSIWindowFocusEventArgs(this, value));
            }
        }
    }

    /// <summary>
    /// The <see cref="WindowManager"/> that opened this window. Used so that
    /// per-window operations (BringToFront, Close, etc.) target the correct
    /// manager instead of the globally-active <see cref="WindowManager.Instance"/>,
    /// which can change when screens transition.
    /// Exposed publicly (read-only outside DOSI.CORE) so cross-assembly
    /// windows can subscribe to events on the manager that actually owns
    /// them - critical for native-content windows whose occlusion logic must
    /// follow them across cross-monitor handoffs.
    /// </summary>
    public WindowManager? OwnerManager { get; internal set; }

    public double WindowX
    {
        get => Canvas.GetLeft(this) + ShadowMargin;
        set => Canvas.SetLeft(this, value - ShadowMargin);
    }

    public double WindowY
    {
        get => Canvas.GetTop(this) + ShadowMargin;
        // Floor against the owning manager's reserved top inset (taskbar
        // height on desktops, 0 elsewhere) so EVERY path that writes Y -
        // OpenWindow's saved-geometry restore, maximize, restore-from-
        // minimize tween, snap-to-edges, AdoptWindow on cross-monitor
        // handoff - is incapable of placing the title bar under the
        // taskbar. The drag handler is excluded from this floor because
        // cross-monitor drag intentionally sends Y negative so the cursor
        // can travel to a monitor above the source (see the multi-monitor
        // relaxation in OnChromeDragMove); only clamp values that are
        // between 0 and the inset, i.e. the dead zone behind the taskbar
        // on the CURRENT monitor.
        set
        {
            var inset = OwnerManager?.TopWorkAreaInset ?? 0;
            var clamped = (value >= 0 && value < inset) ? inset : value;
            Canvas.SetTop(this, clamped - ShadowMargin);
        }
    }

    /// <summary>
    /// Gets or sets the visual width of the window (excluding shadow margin).
    /// </summary>
    public double WindowWidth
    {
        get => Width - ShadowMargin * 2;
        set => Width = value + ShadowMargin * 2;
    }

    /// <summary>
    /// Gets or sets the visual height of the window (excluding shadow margin).
    /// </summary>
    public double WindowHeight
    {
        get => Height - ShadowMargin * 2;
        set => Height = value + ShadowMargin * 2;
    }

    // Minimum size for macOS-style buttons (3 buttons * 12px + spacing + margins = ~76px, plus title space)
    public Size MinimumSize { get; set; } = new Size(150, 50);
    public Size MaximumSize { get; set; } = new Size(double.MaxValue, double.MaxValue);
    public bool CanResize { get; set; } = true;

    private bool _canMinimize = true;
    public bool CanMinimize
    {
        get => _canMinimize;
        set
        {
            _canMinimize = value;
            _minimizeButton.IsVisible = value;
        }
    }

    private bool _canMaximize = true;
    public bool CanMaximize
    {
        get => _canMaximize;
        set
        {
            _canMaximize = value;
            _maximizeButton.IsVisible = value;
        }
    }

    public bool ShowInTaskbar { get; set; } = true;

    /// <summary>
    /// Global translucency applied to every <see cref="DOSIWindow"/> (and to
    /// every accent-themed control inside it). The desktop wallpaper bleeds
    /// through windows for a modern translucent look. Forwarded to
    /// <see cref="AccentManager.WindowOpacity"/> so a single AccentChanged
    /// pass repaints all UI without an offscreen compositing layer.
    /// Valid range: 0.5 – 1.0.
    /// </summary>
    public static double WindowOpacity
    {
        get => Accents.WindowOpacity;
        set
        {
            var clamped = Math.Clamp(value, 0.5, 1.0);
            if (Math.Abs(Accents.WindowOpacity - clamped) < double.Epsilon) return;
            Accents.WindowOpacity = clamped;
            WindowOpacityChanged?.Invoke(null, clamped);
        }
    }

    /// <summary>
    /// Raised whenever <see cref="WindowOpacity"/> changes. Most repainting
    /// happens via <see cref="AccentManager.AccentChanged"/>; this event is
    /// kept for callers that want a typed double payload.
    /// </summary>
    public static event EventHandler<double>? WindowOpacityChanged;

    /// <summary>
    /// Raised when ANY <see cref="DOSIWindow"/> enters or leaves immersive
    /// fullscreen (the page-driven fullscreen used by e.g. YouTube videos).
    /// The desktop chrome (taskbar, version label, ambient clock) subscribes
    /// to this so it can hide itself while a window is in immersive mode -
    /// otherwise the always-on-top taskbar would cover the top of the
    /// fullscreened content.
    /// </summary>
    public static event EventHandler<bool>? AnyWindowFullScreenChanged;

    /// <summary>
    /// True while at least one <see cref="DOSIWindow"/> is currently in
    /// immersive fullscreen. Useful for late-attaching listeners that need
    /// to sync up with the current state on attach.
    /// </summary>
    public static bool IsAnyWindowFullScreen { get; private set; }

    /// <summary>
    /// Gets or sets whether the window chrome (title bar) is visible.
    /// Used for fullscreen mode in applications like the browser.
    /// </summary>
    public bool ShowChrome
    {
        get => _chromeRoot.IsVisible;
        set => _chromeRoot.IsVisible = value;
    }

    /// <summary>
    /// The visual that represents the actual window UI: chrome (title bar
    /// + traffic-light buttons), content area, accent border, and rounded
    /// corners. Excludes the 50 px shadow gutter that surrounds the
    /// window for drop-shadow rendering. Exposed for snapshot consumers
    /// (the taskbar live-preview popover) so they can capture a clean
    /// rectangle of "just the window" without the surrounding shadow
    /// margin that would otherwise force a tiny letterboxed thumbnail.
    /// </summary>
    internal Visual WindowVisual => _windowBorder;

    /// <summary>
    /// True while this window is currently in immersive fullscreen
    /// (filling the entire DOSI desktop canvas, with chrome hidden).
    /// </summary>
    public bool IsImmersiveFullScreen => _isImmersiveFullScreen;

    private bool _isImmersiveFullScreen;
    private Rect _savedFullScreenBounds;
    private DOSIWindowState _savedFullScreenState;
    private bool _savedFullScreenChrome;

    #endregion

    #region Events

    public event EventHandler<DOSIWindowClosingEventArgs>? Closing;
    public event EventHandler<DOSIWindowEventArgs>? Closed;
    public event EventHandler<DOSIWindowStateChangedEventArgs>? StateChanged;
    public event EventHandler<DOSIWindowFocusEventArgs>? FocusChanged;

    /// <summary>
    /// Raised when the user starts (true) or stops (false) dragging this
    /// window by its chrome. Hosted controls that can't keep up with the
    /// compositor (e.g. native WebView2 HWNDs that lag behind Avalonia
    /// visuals during a drag) subscribe to hide themselves while the drag
    /// is in progress and re-show themselves on release.
    /// </summary>
    public event EventHandler<bool>? DragStateChanged;

    /// <summary>True while the user is currently dragging this window.</summary>
    public bool IsBeingDragged => _isDragging;

    /// <summary>
    /// Called by <see cref="WindowManagement.WindowSnapManager"/> after it
    /// animates this window into a snap zone. Records the size the window
    /// had BEFORE the snap so a subsequent drag-to-unsnap gesture can
    /// restore it (mirrors Windows behavior). Pre-snap position isn't
    /// stored - the next drag re-anchors the window under the cursor.
    /// </summary>
    public void MarkSnapped(double preSnapWidth, double preSnapHeight)
    {
        if (preSnapWidth <= 0 || preSnapHeight <= 0) return;
        _isSnapped = true;
        _preSnapWidth = preSnapWidth;
        _preSnapHeight = preSnapHeight;
    }

    /// <summary>
    /// Clears any pending unsnap-restore. Called by the resize-grip handler
    /// (the user explicitly chose a new size, so the pre-snap dimensions
    /// are no longer the right thing to restore to).
    /// </summary>
    public void ClearSnapMark()
    {
        _isSnapped = false;
        _preSnapWidth = 0;
        _preSnapHeight = 0;
    }

    #endregion

    #region Constructor

    static DOSIWindow()
    {
        // Suppress the default white focus rectangle on this DOSI control.
        FocusAdornerProperty.OverrideDefaultValue<DOSIWindow>(null);
    }

    public DOSIWindow()
    {
        // Suppress the default Fluent focus rectangle (white outline). Setting this
        // as a local value overrides the accent style that re-applies it.
        FocusAdorner = null;

        // === BUILD CHROME (TITLE BAR) ===
        _iconHost = new Panel
        {
            Width = 16,
            Height = 16,
            Margin = new Thickness(8, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        _iconHost.Children.Add(CreateDefaultIcon());

        _titleText = new TextBlock
        {
            Text = "Window",
            Foreground = Accents.TextPrimaryBrush,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 1, 0, 0)
        };

        // macOS-style traffic light buttons (right side)
        _closeButton = CreateMacOSButton(MacOSButtonType.Close);
        _closeButton.Click += (s, e) => Close();

        _minimizeButton = CreateMacOSButton(MacOSButtonType.Minimize);
        _minimizeButton.Click += (s, e) => { if (CanMinimize) WindowState = DOSIWindowState.Minimized; };

        _maximizeButton = CreateMacOSButton(MacOSButtonType.Maximize);
        _maximizeButton.Click += (s, e) =>
        {
            if (CanMaximize)
            {
                WindowState = WindowState == DOSIWindowState.Maximized ? DOSIWindowState.Normal : DOSIWindowState.Maximized;
                // Restore focus to content after maximize/restore
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (_contentHost?.Content is Control focusableContent)
                        focusableContent.Focus();
                }, Avalonia.Threading.DispatcherPriority.Input);
            }
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 8, 0),
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        // Order: minimize, maximize, close (right to left visually becomes close on far right)
        buttonPanel.Children.Add(_minimizeButton);
        buttonPanel.Children.Add(_maximizeButton);
        buttonPanel.Children.Add(_closeButton);

        // Title with icon on left
        var titleArea = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(8, 0, 0, 0)
        };
        titleArea.Children.Add(_iconHost);
        titleArea.Children.Add(_titleText);

        var chromeGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        chromeGrid.Children.Add(titleArea);
        Grid.SetColumn(titleArea, 0);
        chromeGrid.Children.Add(new Border { Background = Brushes.Transparent });
        Grid.SetColumn(chromeGrid.Children[1], 1);
        chromeGrid.Children.Add(buttonPanel);
        Grid.SetColumn(buttonPanel, 2);

        _chromeRoot = new Border
        {
            Background = Accents.WindowChromeBrush,
            Height = 24,
            Child = chromeGrid
        };

        // Chrome drag handling
        _chromeRoot.PointerPressed += OnChromeDragStarted;
        _chromeRoot.PointerMoved += OnChromeDragMoved;
        _chromeRoot.PointerReleased += OnChromeDragEnded;
        _chromeRoot.DoubleTapped += (s, e) =>
        {
            if (CanMaximize)
            {
                WindowState = WindowState == DOSIWindowState.Maximized ? DOSIWindowState.Normal : DOSIWindowState.Maximized;
                e.Handled = true;
            }
        };

        // === BUILD CONTENT AREA ===
        _contentHost = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };

        // === BUILD MAIN LAYOUT ===
        _rootGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            ClipToBounds = true
        };

        var chromeContainer = new Border
        {
            CornerRadius = new CornerRadius(8, 8, 0, 0),
            ClipToBounds = true,
            Child = _chromeRoot
        };
        _rootGrid.Children.Add(chromeContainer);
        Grid.SetRow(chromeContainer, 0);

        _contentContainer = new Border
        {
            CornerRadius = new CornerRadius(0, 0, 7, 7),
            ClipToBounds = true,
            Background = Accents.WindowContentBrush,
            Child = _contentHost
        };
        _rootGrid.Children.Add(_contentContainer);
        Grid.SetRow(_contentContainer, 1);

        _windowBorder = new Border
        {
            Background = Accents.WindowBackgroundBrush,
            BorderBrush = Accents.WindowBorderUnfocusedBrush,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(10),
            BoxShadow = CreateMacOSShadow(),
            Child = _rootGrid
        };

        // === BUILD RESIZE GRIPS ===
        _resizeTop = CreateResizeGrip(ResizeDirection.Top, StandardCursorType.SizeNorthSouth);
        _resizeBottom = CreateResizeGrip(ResizeDirection.Bottom, StandardCursorType.SizeNorthSouth);
        _resizeLeft = CreateResizeGrip(ResizeDirection.Left, StandardCursorType.SizeWestEast);
        _resizeRight = CreateResizeGrip(ResizeDirection.Right, StandardCursorType.SizeWestEast);
        _resizeTopLeft = CreateResizeGrip(ResizeDirection.TopLeft, StandardCursorType.TopLeftCorner);
        _resizeTopRight = CreateResizeGrip(ResizeDirection.TopRight, StandardCursorType.TopRightCorner);
        _resizeBottomLeft = CreateResizeGrip(ResizeDirection.BottomLeft, StandardCursorType.BottomLeftCorner);
        _resizeBottomRight = CreateResizeGrip(ResizeDirection.BottomRight, StandardCursorType.BottomRightCorner);

        // Add margin to give space for the shadow to render
        var mainContainer = new Grid
        {
            Margin = new Thickness(ShadowMargin),
            ClipToBounds = false
        };
        mainContainer.Children.Add(_windowBorder);
        mainContainer.Children.Add(_resizeTop);
        mainContainer.Children.Add(_resizeBottom);
        mainContainer.Children.Add(_resizeLeft);
        mainContainer.Children.Add(_resizeRight);
        mainContainer.Children.Add(_resizeTopLeft);
        mainContainer.Children.Add(_resizeTopRight);
        mainContainer.Children.Add(_resizeBottomLeft);
        mainContainer.Children.Add(_resizeBottomRight);

        ClipToBounds = false;
        base.Content = mainContainer;

        Width = 400;
        Height = 300;

        // Start invisible for open animation
        Opacity = 0;
        RenderTransform = new ScaleTransform(0.95, 0.95);
        RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);

        AddHandler(PointerPressedEvent, OnWindowPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        LayoutUpdated += (s, e) => UpdateResizeGripPositions();

        // Subscribe/unsubscribe properly to avoid memory leaks
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;

        UpdateFocusVisuals();
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        Accents.AccentChanged += OnAccentChanged;
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        Accents.AccentChanged -= OnAccentChanged;
    }

    #endregion

    #region Animations

    /// <summary>
    /// Plays the window open animation (fade in + scale up).
    /// </summary>
    public async Task PlayOpenAnimationAsync()
    {
        if (_isAnimating) return;
        _isAnimating = true;

        var animation = new Animation
        {
            Duration = OpenAnimationDuration,
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0),
                    Setters =
                    {
                        new Setter(OpacityProperty, 0.0),
                        new Setter(ScaleTransform.ScaleXProperty, 0.95),
                        new Setter(ScaleTransform.ScaleYProperty, 0.95)
                    }
                },
                new KeyFrame
                {
                    Cue = new Cue(1),
                    Setters =
                    {
                        new Setter(OpacityProperty, 1.0),
                        new Setter(ScaleTransform.ScaleXProperty, 1.0),
                        new Setter(ScaleTransform.ScaleYProperty, 1.0)
                    }
                }
            }
        };

        await animation.RunAsync(this);

        Opacity = 1;
        RenderTransform = new ScaleTransform(1, 1);
        _isAnimating = false;
    }

    /// <summary>
    /// Plays the window close animation (fade out + scale down).
    /// </summary>
    public async Task PlayCloseAnimationAsync()
    {
        if (_isAnimating || _isClosing) return;
        _isAnimating = true;
        _isClosing = true;

        var animation = new Animation
        {
            Duration = CloseAnimationDuration,
            Easing = new CubicEaseIn(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0),
                    Setters =
                    {
                        new Setter(OpacityProperty, 1.0),
                        new Setter(ScaleTransform.ScaleXProperty, 1.0),
                        new Setter(ScaleTransform.ScaleYProperty, 1.0)
                    }
                },
                new KeyFrame
                {
                    Cue = new Cue(1),
                    Setters =
                    {
                        new Setter(OpacityProperty, 0.0),
                        new Setter(ScaleTransform.ScaleXProperty, 0.95),
                        new Setter(ScaleTransform.ScaleYProperty, 0.95)
                    }
                }
            }
        };

        await animation.RunAsync(this);
        _isAnimating = false;
    }

    /// <summary>
    /// Animates the window to a new position and size with cubic ease in/out.
    /// Uses WindowX/Y and WindowWidth/Height which account for shadow margin.
    /// </summary>
    private async Task AnimateWindowToAsync(double targetX, double targetY, double targetWidth, double targetHeight)
    {
        if (_isAnimating) return;
        _isAnimating = true;

        // Snapshot the parent at animation start. If the window gets
        // reparented mid-tween (cross-monitor drag handoff
        // RelinquishWindow + AdoptWindow swaps it to a different TopLevel's
        // LayoutManager), every subsequent WindowX/Y/Width/Height write
        // would queue an InvalidateArrange against the OLD LayoutManager
        // and throw "Attempt to call InvalidateArrange on wrong
        // LayoutManager" the moment the dispatcher flushes layout. Bailing
        // when the parent changes leaves the window at whatever bounds it
        // had reached so far - the new owner / drag handler takes over.
        // CRITICAL: also clears _isAnimating before returning so a
        // subsequent state change (the user clicking maximize again, etc.)
        // isn't permanently blocked by the latch.
        var startParent = this.Parent;

        var startX = WindowX;
        var startY = WindowY;
        var startWidth = WindowWidth;
        var startHeight = WindowHeight;

        var duration = StateAnimationDuration;
        var easing = new CubicEaseInOut();
        var startTime = DateTime.Now;

        while (true)
        {
            if (this.Parent != startParent || _isClosing)
            {
                _isAnimating = false;
                return;
            }

            var elapsed = DateTime.Now - startTime;
            var progress = Math.Min(1.0, elapsed.TotalMilliseconds / duration.TotalMilliseconds);
            var easedProgress = easing.Ease(progress);

            WindowX = Lerp(startX, targetX, easedProgress);
            WindowY = Lerp(startY, targetY, easedProgress);
            WindowWidth = Lerp(startWidth, targetWidth, easedProgress);
            WindowHeight = Lerp(startHeight, targetHeight, easedProgress);

            if (progress >= 1.0) break;

            await Task.Delay(8); // ~120fps
        }

        // Ensure final values are set exactly - only if we're still in the
        // same tree.
        if (this.Parent == startParent && !_isClosing)
        {
            WindowX = targetX;
            WindowY = targetY;
            WindowWidth = targetWidth;
            WindowHeight = targetHeight;
        }

        _isAnimating = false;
    }

    private static double Lerp(double start, double end, double t)
    {
        return start + (end - start) * t;
    }

    /// <summary>
    /// Animates only <see cref="WindowWidth"/> / <see cref="WindowHeight"/>
    /// from the current snapped dimensions to the original pre-snap size,
    /// matching the easing, duration, and tick rate of
    /// <see cref="AnimateWindowToAsync"/> (the maximize / restore animation)
    /// so drag-to-unsnap looks and feels like a sibling of every other
    /// state-change animation in the window. INTENTIONALLY does not touch
    /// X/Y - the drag handler keeps anchoring the window under the cursor
    /// every PointerMoved while the size tweens. Skips the global
    /// <see cref="_isAnimating"/> latch so it cannot block subsequent
    /// state changes (drag is the source of truth here).
    /// </summary>
    private async Task AnimateUnsnapSizeAsync(double fromW, double fromH, double toW, double toH)
    {
        if (Math.Abs(fromW - toW) < 0.5 && Math.Abs(fromH - toH) < 0.5) return;

        // Snapshot the parent at animation start. If the window gets
        // reparented mid-tween (cross-monitor drag handoff
        // RelinquishWindow + AdoptWindow swaps it to a different TopLevel's
        // LayoutManager), every subsequent WindowWidth/Height write would
        // queue an InvalidateArrange against the OLD LayoutManager - which
        // throws "Attempt to call InvalidateArrange on wrong LayoutManager"
        // the moment the dispatcher flushes layout. Bailing immediately
        // when the parent changes (or is null) leaves the window at
        // whatever size it had reached so far; the drag handler will
        // continue moving it under the cursor on the new monitor.
        var startParent = this.Parent;

        var duration = StateAnimationDuration;
        var easing = new CubicEaseInOut();
        var startTime = DateTime.Now;

        while (true)
        {
            // Bail if we got reparented OR detached entirely.
            if (this.Parent != startParent || _isClosing) return;

            var elapsed = DateTime.Now - startTime;
            var progress = Math.Min(1.0, elapsed.TotalMilliseconds / duration.TotalMilliseconds);
            var eased = easing.Ease(progress);

            WindowWidth = Lerp(fromW, toW, eased);
            WindowHeight = Lerp(fromH, toH, eased);

            if (progress >= 1.0) break;
            await Task.Delay(8); // ~120fps - matches AnimateWindowToAsync
        }

        // Final snap only if we're still in the same tree.
        if (this.Parent == startParent && !_isClosing)
        {
            WindowWidth = toW;
            WindowHeight = toH;
        }
    }

    /// <summary>
    /// Creates a beautiful macOS-style multi-layered drop shadow.
    /// </summary>
    private static BoxShadows CreateMacOSShadow()
    {
        // macOS uses multiple shadow layers for a realistic effect:
        // 1. Soft ambient shadow (large, subtle)
        // 2. Direct shadow (medium, offset down)
        // 3. Close contact shadow (small, sharp)
        return new BoxShadows(
            new BoxShadow
            {
                OffsetX = 0,
                OffsetY = 3,
                Blur = 6,
                Spread = 1,
                Color = Color.FromArgb(80, 0, 0, 0)  // Sharp contact shadow
            },
            [
                new BoxShadow
                {
                    OffsetX = 0,
                    OffsetY = 12,
                    Blur = 24,
                    Spread = 0,
                    Color = Color.FromArgb(65, 0, 0, 0)  // Medium shadow
                },
                new BoxShadow
                {
                    OffsetX = 0,
                    OffsetY = 28,
                    Blur = 50,
                    Spread = -2,
                    Color = Color.FromArgb(45, 0, 0, 0)  // Soft ambient
                }
            ]);
    }

    #endregion

    #region Chrome Icon Creation

    private static Control CreateDefaultIcon()
    {
        // Outer border with accent gradient
        var icon = new Border
        {
            Width = 16,
            Height = 16,
            Background = Accents.AccentGradientBrush,
            CornerRadius = new CornerRadius(3)
        };

        // Inner diamond shape using a rotated rectangle
        var diamond = new Border
        {
            Width = 6,
            Height = 6,
            Background = new SolidColorBrush(Accents.TextOnAccent),
            RenderTransform = new RotateTransform(45),
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        icon.Child = diamond;
        return icon;
    }

    private static Control CreateMinimizeIcon()
    {
        return new Line
        {
            StartPoint = new Point(0, 5),
            EndPoint = new Point(10, 5),
            Stroke = Accents.TextSecondaryBrush,
            StrokeThickness = 1
        };
    }

    private Control CreateMaximizeIcon()
    {
        return new Rectangle
        {
            Width = 10,
            Height = 10,
            Stroke = Accents.TextSecondaryBrush,
            StrokeThickness = 1,
            Fill = Brushes.Transparent
        };
    }

    private Control CreateRestoreIcon()
    {
        var canvas = new Canvas { Width = 10, Height = 10 };

        var backRect = new Rectangle
        {
            Width = 8,
            Height = 8,
            Stroke = Accents.TextSecondaryBrush,
            StrokeThickness = 1,
            Fill = Brushes.Transparent
        };
        Canvas.SetLeft(backRect, 2);
        Canvas.SetTop(backRect, 0);

        var frontRect = new Rectangle
        {
            Width = 8,
            Height = 8,
            Stroke = Accents.TextSecondaryBrush,
            StrokeThickness = 1,
            Fill = Accents.WindowChromeBrush
        };
        Canvas.SetLeft(frontRect, 0);
        Canvas.SetTop(frontRect, 2);

        canvas.Children.Add(backRect);
        canvas.Children.Add(frontRect);
        return canvas;
    }

    private static Control CreateCloseIcon()
    {
        var canvas = new Canvas { Width = 10, Height = 10 };
        canvas.Children.Add(new Line
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(10, 10),
            Stroke = Accents.TextSecondaryBrush,
            StrokeThickness = 1
        });
        canvas.Children.Add(new Line
        {
            StartPoint = new Point(10, 0),
            EndPoint = new Point(0, 10),
            Stroke = Accents.TextSecondaryBrush,
            StrokeThickness = 1
        });
        return canvas;
    }

    private static Button CreateCaptionButton(Control icon, bool isCloseButton)
    {
        var button = new Button
        {
            Width = 46,
            Height = 24,
            Content = icon,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };

        button.PointerEntered += (s, e) =>
        {
            button.Background = isCloseButton
                ? Accents.CloseButtonHoverBrush
                : Accents.ButtonBackgroundHoverBrush;

            if (isCloseButton && button.Content is Canvas canvas)
            {
                foreach (var child in canvas.Children.OfType<Line>())
                    child.Stroke = Brushes.White;
            }
        };

        button.PointerExited += (s, e) =>
        {
            button.Background = Brushes.Transparent;

            if (isCloseButton && button.Content is Canvas canvas)
            {
                foreach (var child in canvas.Children.OfType<Line>())
                    child.Stroke = Accents.TextSecondaryBrush;
            }
        };

        return button;
    }

    private enum MacOSButtonType { Close, Minimize, Maximize }

    private static Button CreateMacOSButton(MacOSButtonType type)
    {
        // macOS traffic light colors
        var (normalColor, hoverColor) = type switch
        {
            MacOSButtonType.Close => (Color.FromRgb(255, 95, 87), Color.FromRgb(255, 70, 60)),
            MacOSButtonType.Minimize => (Color.FromRgb(255, 189, 46), Color.FromRgb(230, 168, 35)),
            MacOSButtonType.Maximize => (Color.FromRgb(40, 200, 65), Color.FromRgb(30, 175, 55)),
            _ => (Colors.Gray, Colors.DarkGray)
        };

        var circle = new Ellipse
        {
            Width = 12,
            Height = 12,
            Fill = new SolidColorBrush(normalColor),
            Stroke = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)),
            StrokeThickness = 0.5
        };

        // Icon container (hidden by default, shown on hover)
        var iconCanvas = new Canvas { Width = 12, Height = 12, IsVisible = false };

        // Create icon based on type
        if (type == MacOSButtonType.Close)
        {
            // X icon
            iconCanvas.Children.Add(new Line
            {
                StartPoint = new Point(3, 3),
                EndPoint = new Point(9, 9),
                Stroke = new SolidColorBrush(Color.FromArgb(180, 80, 0, 0)),
                StrokeThickness = 1.5,
                StrokeLineCap = PenLineCap.Round
            });
            iconCanvas.Children.Add(new Line
            {
                StartPoint = new Point(9, 3),
                EndPoint = new Point(3, 9),
                Stroke = new SolidColorBrush(Color.FromArgb(180, 80, 0, 0)),
                StrokeThickness = 1.5,
                StrokeLineCap = PenLineCap.Round
            });
        }
        else if (type == MacOSButtonType.Minimize)
        {
            // - icon
            iconCanvas.Children.Add(new Line
            {
                StartPoint = new Point(3, 6),
                EndPoint = new Point(9, 6),
                Stroke = new SolidColorBrush(Color.FromArgb(180, 120, 70, 0)),
                StrokeThickness = 1.5,
                StrokeLineCap = PenLineCap.Round
            });
        }
        else if (type == MacOSButtonType.Maximize)
        {
            // Expand arrows icon (diagonal arrows)
            var arrowBrush = new SolidColorBrush(Color.FromArgb(180, 0, 80, 20));
            // Top-left to bottom-right diagonal
            iconCanvas.Children.Add(new Line
            {
                StartPoint = new Point(3, 9),
                EndPoint = new Point(9, 3),
                Stroke = arrowBrush,
                StrokeThickness = 1.5,
                StrokeLineCap = PenLineCap.Round
            });
            // Arrow heads
            iconCanvas.Children.Add(new Line
            {
                StartPoint = new Point(5, 3),
                EndPoint = new Point(9, 3),
                Stroke = arrowBrush,
                StrokeThickness = 1.5,
                StrokeLineCap = PenLineCap.Round
            });
            iconCanvas.Children.Add(new Line
            {
                StartPoint = new Point(9, 3),
                EndPoint = new Point(9, 7),
                Stroke = arrowBrush,
                StrokeThickness = 1.5,
                StrokeLineCap = PenLineCap.Round
            });
            iconCanvas.Children.Add(new Line
            {
                StartPoint = new Point(3, 5),
                EndPoint = new Point(3, 9),
                Stroke = arrowBrush,
                StrokeThickness = 1.5,
                StrokeLineCap = PenLineCap.Round
            });
            iconCanvas.Children.Add(new Line
            {
                StartPoint = new Point(3, 9),
                EndPoint = new Point(7, 9),
                Stroke = arrowBrush,
                StrokeThickness = 1.5,
                StrokeLineCap = PenLineCap.Round
            });
        }

        var grid = new Grid { Width = 12, Height = 12 };
        grid.Children.Add(circle);
        grid.Children.Add(iconCanvas);

        var button = new Button
        {
            Width = 12,
            Height = 12,
            Content = grid,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            // Kill the focus rectangle so Tab-cycling never draws a halo
            // around the 12x12 traffic-light circle.
            FocusAdorner = null
        };

        // The Fluent theme's default Button template paints a faint white
        // overlay on :pointerover and :pressed - that's the flicker the user
        // sees when holding the mouse on a traffic-light. We don't need any
        // of that chrome (the Ellipse handles its own hover color), so we
        // swap in a bare ContentPresenter template with zero visual states.
        button.Template = new FuncControlTemplate<Button>((b, _) => new ContentPresenter
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            [!ContentPresenter.ContentProperty] = b[!ContentControl.ContentProperty]
        });

        button.PointerEntered += (s, e) =>
        {
            circle.Fill = new SolidColorBrush(hoverColor);
            iconCanvas.IsVisible = true;
        };

        button.PointerExited += (s, e) =>
        {
            circle.Fill = new SolidColorBrush(normalColor);
            iconCanvas.IsVisible = false;
        };

        return button;
    }

    /// <summary>
    /// Updates the maximize button icon to show expand or restore arrows based on window state.
    /// </summary>
    private void UpdateMaximizeButtonIcon(bool isMaximized)
    {
        if (_maximizeButton.Content is not Grid grid || grid.Children.Count < 2)
            return;

        if (grid.Children[1] is not Canvas iconCanvas)
            return;

        iconCanvas.Children.Clear();

        var arrowBrush = new SolidColorBrush(Color.FromArgb(180, 0, 80, 20));

        if (isMaximized)
        {
            // Restore/shrink arrows (pointing inward)
            // Top-right arrow pointing toward center
            iconCanvas.Children.Add(new Line
            {
                StartPoint = new Point(9, 3),
                EndPoint = new Point(6, 6),
                Stroke = arrowBrush,
                StrokeThickness = 1.5,
                StrokeLineCap = PenLineCap.Round
            });
            iconCanvas.Children.Add(new Line
            {
                StartPoint = new Point(9, 3),
                EndPoint = new Point(9, 6),
                Stroke = arrowBrush,
                StrokeThickness = 1.5,
                StrokeLineCap = PenLineCap.Round
            });
            iconCanvas.Children.Add(new Line
            {
                StartPoint = new Point(9, 3),
                EndPoint = new Point(6, 3),
                Stroke = arrowBrush,
                StrokeThickness = 1.5,
                StrokeLineCap = PenLineCap.Round
            });

            // Bottom-left arrow pointing toward center
            iconCanvas.Children.Add(new Line
            {
                StartPoint = new Point(3, 9),
                EndPoint = new Point(6, 6),
                Stroke = arrowBrush,
                StrokeThickness = 1.5,
                StrokeLineCap = PenLineCap.Round
            });
            iconCanvas.Children.Add(new Line
            {
                StartPoint = new Point(3, 9),
                EndPoint = new Point(3, 6),
                Stroke = arrowBrush,
                StrokeThickness = 1.5,
                StrokeLineCap = PenLineCap.Round
            });
            iconCanvas.Children.Add(new Line
            {
                StartPoint = new Point(3, 9),
                EndPoint = new Point(6, 9),
                Stroke = arrowBrush,
                StrokeThickness = 1.5,
                StrokeLineCap = PenLineCap.Round
            });
        }
        else
        {
            // Expand arrows (pointing outward) - same as CreateMacOSButton
            iconCanvas.Children.Add(new Line
            {
                StartPoint = new Point(3, 9),
                EndPoint = new Point(9, 3),
                Stroke = arrowBrush,
                StrokeThickness = 1.5,
                StrokeLineCap = PenLineCap.Round
            });
            iconCanvas.Children.Add(new Line
            {
                StartPoint = new Point(5, 3),
                EndPoint = new Point(9, 3),
                Stroke = arrowBrush,
                StrokeThickness = 1.5,
                StrokeLineCap = PenLineCap.Round
            });
            iconCanvas.Children.Add(new Line
            {
                StartPoint = new Point(9, 3),
                EndPoint = new Point(9, 7),
                Stroke = arrowBrush,
                StrokeThickness = 1.5,
                StrokeLineCap = PenLineCap.Round
            });
            iconCanvas.Children.Add(new Line
            {
                StartPoint = new Point(3, 5),
                EndPoint = new Point(3, 9),
                Stroke = arrowBrush,
                StrokeThickness = 1.5,
                StrokeLineCap = PenLineCap.Round
            });
            iconCanvas.Children.Add(new Line
            {
                StartPoint = new Point(3, 9),
                EndPoint = new Point(7, 9),
                Stroke = arrowBrush,
                StrokeThickness = 1.5,
                StrokeLineCap = PenLineCap.Round
            });
        }
    }

    #endregion

    #region Resize Grips

    private Border CreateResizeGrip(ResizeDirection direction, StandardCursorType cursorType)
    {
        var grip = new Border
        {
            Background = Brushes.Transparent,
            Cursor = new Cursor(cursorType),
            Tag = direction
        };
        grip.PointerPressed += OnResizeGripPointerPressed;
        grip.PointerMoved += OnResizeGripPointerMoved;
        grip.PointerReleased += OnResizeGripPointerReleased;
        return grip;
    }

    private void UpdateResizeGripPositions()
    {
        // Use the window border bounds, not the control bounds (which includes shadow margin)
        var w = _windowBorder.Bounds.Width;
        var h = _windowBorder.Bounds.Height;
        if (w <= 0 || h <= 0) return;

        SetGripBounds(_resizeTop, CornerGripSize, 0, w - CornerGripSize * 2, ResizeGripSize);
        SetGripBounds(_resizeBottom, CornerGripSize, h - ResizeGripSize, w - CornerGripSize * 2, ResizeGripSize);
        SetGripBounds(_resizeLeft, 0, CornerGripSize, ResizeGripSize, h - CornerGripSize * 2);
        SetGripBounds(_resizeRight, w - ResizeGripSize, CornerGripSize, ResizeGripSize, h - CornerGripSize * 2);

        SetGripBounds(_resizeTopLeft, 0, 0, CornerGripSize, CornerGripSize);
        SetGripBounds(_resizeTopRight, w - CornerGripSize, 0, CornerGripSize, CornerGripSize);
        SetGripBounds(_resizeBottomLeft, 0, h - CornerGripSize, CornerGripSize, CornerGripSize);
        SetGripBounds(_resizeBottomRight, w - CornerGripSize, h - CornerGripSize, CornerGripSize, CornerGripSize);

        var showGrips = CanResize && WindowState == DOSIWindowState.Normal;
        _resizeTop.IsVisible = showGrips;
        _resizeBottom.IsVisible = showGrips;
        _resizeLeft.IsVisible = showGrips;
        _resizeRight.IsVisible = showGrips;
        _resizeTopLeft.IsVisible = showGrips;
        _resizeTopRight.IsVisible = showGrips;
        _resizeBottomLeft.IsVisible = showGrips;
        _resizeBottomRight.IsVisible = showGrips;
    }

    private static void SetGripBounds(Border grip, double x, double y, double width, double height)
    {
        grip.Width = Math.Max(0, width);
        grip.Height = Math.Max(0, height);
        grip.Margin = new Thickness(x, y, 0, 0);
        grip.HorizontalAlignment = HorizontalAlignment.Left;
        grip.VerticalAlignment = VerticalAlignment.Top;
    }

    #endregion

    #region Event Handlers

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        (OwnerManager ?? WindowManager.Instance)?.BringToFront(this);
    }

    private void OnChromeDragStarted(object? sender, PointerPressedEventArgs e)
    {
        if (_isResizing) return;
        if (e.Source == _minimizeButton || e.Source == _maximizeButton || e.Source == _closeButton) return;

        _isDragging = true;
        _dragStartPoint = e.GetPosition(Parent as Visual);
        _windowStartPosition = new Point(WindowX, WindowY);
        e.Pointer.Capture(_chromeRoot);
        e.Handled = true;
        DragStateChanged?.Invoke(this, true);

        // Multi-monitor ghost: when there's more than one host registered,
        // snapshot this window into a topmost transparent ghost that follows
        // the cursor in screen-pixel coords. Source goes invisible for the
        // drag, ghost takes over visually. Without this, dragging across
        // monitors leaves only the cursor visible in the gap because the
        // source DOSIWindow can't render outside its parent native window.
        TryStartDragGhost(e);
    }

    /// <summary>
    /// At drag start, captures a <see cref="Avalonia.Media.Imaging.RenderTargetBitmap"/>
    /// of the source window and ARMS the ghost - but does not yet show it
    /// or hide the source. The actual swap to the ghost is deferred until
    /// <see cref="OnChromeDragMoved"/> detects the cursor leaving the source
    /// TopLevel's bounds (i.e. heading toward another monitor). Same-monitor
    /// drags never trigger the swap, which avoids the source-vs-ghost flash
    /// that happens when both share the same screen position.
    /// No-op on single-monitor systems and best-effort: a snapshot failure
    /// just leaves the drag with the legacy "window clips at edge" behavior.
    /// </summary>
    private void TryStartDragGhost(PointerPressedEventArgs e)
    {
        // Only spend the snapshot cost when there's actually somewhere to
        // drag TO. Single-monitor: legacy in-canvas drag is fine.
        if (DOSI.CORE.UIComponents.DosiHostRegistry.All.Count <= 1) return;

        var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        try
        {
            // Anchor the ghost's screen position to the cursor by capturing
            // the pixel offset from the cursor to the window's top-left at
            // drag start. As the cursor moves (in screen space), ghost.Position
            // tracks (cursor - offset).
            var cursorLocal = e.GetPosition(topLevel);
            var cursorScreen = topLevel.PointToScreen(cursorLocal);
            var windowLocalDip = this.TranslatePoint(new Point(0, 0), topLevel) ?? new Point(0, 0);
            var windowScreen = topLevel.PointToScreen(windowLocalDip);
            _dragGhostCursorOffset = new Avalonia.PixelPoint(
                cursorScreen.X - windowScreen.X,
                cursorScreen.Y - windowScreen.Y);
            _dragSourceTopLevel = topLevel;

            // Snapshot at the source TopLevel's render scaling so the ghost
            // looks pixel-identical when displayed on the same monitor at
            // its current DPI. Cross-DPI moves get a brief size mismatch.
            var scaling = topLevel.RenderScaling > 0 ? topLevel.RenderScaling : 1.0;
            var pixelSize = new Avalonia.PixelSize(
                System.Math.Max(1, (int)(this.Bounds.Width * scaling)),
                System.Math.Max(1, (int)(this.Bounds.Height * scaling)));
            var dpi = new Avalonia.Vector(96 * scaling, 96 * scaling);
            _dragGhostSnapshot = new Avalonia.Media.Imaging.RenderTargetBitmap(pixelSize, dpi);
            _dragGhostSnapshot.Render(this);

            // CONFIGURE the pooled ghost (sets bitmap, size, position) but
            // do NOT show it yet. The OS DWM gets a full composition cycle
            // to lay out the new content at the new position while we're
            // still in same-monitor drag mode (source visible, ghost
            // Opacity=0). When the cursor finally crosses to another
            // monitor, the only thing that changes is opacities - the
            // layered window already has the right pixels at the right
            // place, so the swap is atomic and flicker-free.
            var ghost = DOSI.CORE.UIComponents.WindowManagement.DragGhostWindow.GetOrCreate();
            ghost.ConfigureFor(_dragGhostSnapshot, this.Bounds.Width, this.Bounds.Height, windowScreen);
            ghost.SetVisible(false);

            // Armed but not shown. Source stays visible. The swap fires only
            // if/when the cursor leaves the source TopLevel's bounds in
            // OnChromeDragMoved -> EnsureGhostShownIfCursorLeftSource().
            _dragGhostArmed = true;
            _dragGhostShown = false;
        }
        catch
        {
            // Snapshot failed - clean up partial state and let the drag
            // continue with the legacy behavior.
            _dragGhostSnapshot = null;
            _dragGhostArmed = false;
            _dragGhostShown = false;
            _dragSourceTopLevel = null;
        }
    }

    /// <summary>
    /// Called from <see cref="OnChromeDragMoved"/> on every pointer move
    /// while a ghost-armed drag is in flight. Lazily shows the ghost (and
    /// hides the source) the first time the cursor leaves the source
    /// TopLevel's DIP bounds, and toggles back if the cursor returns.
    /// This is what makes same-monitor drags flash-free: the ghost is only
    /// brought on-screen when the source actually CAN'T render where the
    /// user is dragging to.
    /// </summary>
    private void EnsureGhostShownIfCursorLeftSource(PointerEventArgs e)
    {
        if (!_dragGhostArmed || _dragSourceTopLevel == null || _dragGhostSnapshot == null) return;

        var cursorLocal = e.GetPosition(_dragSourceTopLevel);
        var sourceBounds = _dragSourceTopLevel.ClientSize;
        bool outside =
            cursorLocal.X < 0 || cursorLocal.X >= sourceBounds.Width ||
            cursorLocal.Y < 0 || cursorLocal.Y >= sourceBounds.Height;

        if (outside && !_dragGhostShown)
        {
            try
            {
                // Ghost is already at the right position with the right
                // bitmap (kept current by MoveTo on every PointerMoved
                // since drag start). The crossing is just an atomic
                // opacity swap - no Show()/Hide() round-trip, no bitmap
                // re-upload, no layout pass. This is what kills the
                // initial first-transfer flicker.
                DOSI.CORE.UIComponents.WindowManagement.DragGhostWindow.Shared?.SetVisible(true);
                this.Opacity = 0;
                _dragGhostShown = true;
            }
            catch { /* best-effort - leave armed for retry */ }
        }
        else if (!outside && _dragGhostShown)
        {
            // Cursor returned to the source monitor mid-drag. Restore source
            // and hide ghost so the user gets crisp live rendering again.
            try { DOSI.CORE.UIComponents.WindowManagement.DragGhostWindow.Shared?.SetVisible(false); } catch { }
            this.Opacity = 1;
            _dragGhostShown = false;
        }
    }

    private void OnChromeDragMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging || _isResizing) return;

        var currentPoint = e.GetPosition(Parent as Visual);
        var delta = currentPoint - _dragStartPoint;

        // A maximized window has no draggable area in DAX.OSI - it fills
        // the entire desktop work area, and the OS-style "drag-from-the-
        // top-to-restore" gesture is INTENTIONALLY not implemented here.
        // Restore-on-drag is reserved for half/quarter-snapped windows
        // (handled by the _isSnapped branch below). To bring a maximized
        // window back to its previous size, the user clicks the
        // restore/maximize chrome button - same as a real OS Win+Down.
        //
        // Returning here also avoids the buggy cursor-anchor math the
        // previous implementation used (percentX based on Bounds.Width
        // produced a wildly wrong WindowX after the size collapse, leaving
        // the window dropped far from the cursor).
        if (WindowState == DOSIWindowState.Maximized)
        {
            return;
        }

        // Drag-to-unsnap: if this window was previously snapped (left half /
        // right half / quarter / etc.) by WindowSnapManager, restore it to
        // its pre-snap dimensions and re-anchor the cursor inside the new
        // (smaller) title bar. Same gesture and feel as the Maximized restore
        // above, just for half/quarter snaps that leave WindowState=Normal.
        // The size shrink is ANIMATED (matching the snap-IN animation) so
        // the window doesn't pop instantly to its pre-snap dimensions.
        if (_isSnapped && (Math.Abs(delta.X) > 5 || Math.Abs(delta.Y) > 5))
        {
            var mousePos = currentPoint;
            // Anchor the new window so the cursor stays at the same horizontal
            // fraction within the title bar that the user originally grabbed.
            var percentX = Bounds.Width > 0 ? _dragStartPoint.X / Bounds.Width : 0.5;
            // Clamp the anchor so a near-edge grab on a tiny snapped window
            // doesn't fling the restored window way off-screen.
            percentX = Math.Clamp(percentX, 0.05, 0.95);

            // Set X immediately so the cursor lands at the right horizontal
            // fraction of the FINAL pre-snap width. During the size tween
            // the title bar will be wider than pre-snap, so the cursor is
            // briefly left-of-anchor; it settles to the right spot when the
            // animation completes. Y just hugs the title bar.
            WindowX = mousePos.X - (_preSnapWidth * percentX);
            WindowY = mousePos.Y - 12;

            // Snapshot current (snapped) dimensions BEFORE we hand off to
            // the animation - that's the tween's start frame.
            var fromW = WindowWidth;
            var fromH = WindowHeight;
            var toW = _preSnapWidth;
            var toH = _preSnapHeight;

            // Consume the snap mark and re-baseline the drag so subsequent
            // PointerMoved deltas use the new window position as the origin.
            _isSnapped = false;
            _dragStartPoint = currentPoint;
            _windowStartPosition = new Point(WindowX, WindowY);

            // Fire-and-forget size tween. Drag handler keeps controlling X/Y
            // every PointerMoved (its writes always win the race because they
            // run on the same UI thread between animation ticks).
            _ = AnimateUnsnapSizeAsync(fromW, fromH, toW, toH);
            return;
        }

        var newX = _windowStartPosition.X + delta.X;
        var newY = _windowStartPosition.Y + delta.Y;

        // Get parent bounds for clamping
        if (Parent is Canvas canvas)
        {
            var canvasWidth = canvas.Bounds.Width;
            var canvasHeight = canvas.Bounds.Height;

            const double minVisibleWidth = 100;  // Minimum visible width on left/right edges
            const double titleBarHeight = 26;    // Keep title bar visible at bottom
            var topInset = OwnerManager?.TopWorkAreaInset ?? 0;

            // Multi-monitor: when there's MORE THAN ONE host registered, the
            // user might be dragging the window toward another monitor. The
            // pointer is captured by the chrome so events keep flowing even
            // when the cursor leaves the source window's bounds - which means
            // we MUST allow the cursor to travel freely onto a neighbouring
            // monitor without the window's clamps yanking the cursor along
            // with it. Original strict X clamp was: pin the window so its
            // left edge can't go past `-WindowWidth + minVisibleWidth` and
            // its right edge stays at least `minVisibleWidth` inside the
            // canvas - that pinned the title bar inside the source monitor,
            // and the cursor (which lives ON the title bar) couldn't escape.
            //
            // Multi-monitor relaxation: clamp ONLY enough to prevent the
            // window from being completely lost off-screen on a single-
            // monitor system, and to keep some sliver visible on multi-
            // monitor systems if the drag ends back on the source monitor.
            // The actual cross-monitor handoff fires on pointer release in
            // OnChromeDragEnded -> TryHandoffToMonitorAtCursor.
            var multiMonitor = DOSI.CORE.UIComponents.DosiHostRegistry.All.Count > 1;
            if (multiMonitor)
            {
                // Allow the window to extend FULLY off either side - the user
                // needs the cursor (which they're still grabbing the title bar
                // with) to be able to reach a neighbouring monitor's bounds
                // for the screen-bounds release test in OnChromeDragEnded to
                // detect the handoff target.
                newX = Math.Max(-WindowWidth, newX);
                newX = Math.Min(canvasWidth, newX);
            }
            else
            {
                // Single-monitor: original strict clamps keep the window on-screen.
                newX = Math.Max(-WindowWidth + minVisibleWidth, newX);
                newX = Math.Min(canvasWidth - minVisibleWidth, newX);
            }

            // Clamp Y: top edge respects the host's reserved top area (e.g.
            // taskbar height) so the window's title bar can never disappear
            // behind persistent chrome. Multi-monitor: same relaxation as X
            // for vertically-stacked displays - the user's cursor must be
            // able to reach a monitor above or below the source.
            if (multiMonitor)
            {
                newY = Math.Max(-WindowHeight, newY);
                newY = Math.Min(canvasHeight, newY);
            }
            else
            {
                newY = Math.Max(topInset, newY);                          // Top edge
                newY = Math.Min(canvasHeight - titleBarHeight, newY);     // Bottom edge
            }
        }

        // Multi-monitor flicker fix: if the cursor JUST crossed out of the
        // source TopLevel's bounds, hide the source NOW - BEFORE the
        // WindowX/Y update below pushes its in-canvas position far off the
        // canvas edge. The drag math below sets newX = _windowStartPosition.X
        // + delta.X, which on the cross frame becomes a large value (e.g.
        // 2400 on a 1920-wide canvas). For one frame, between Avalonia's
        // layout pass applying the new position and its render pass applying
        // the new opacity, the source can briefly render at its clipped-at-
        // edge position WHILE STILL VISIBLE - that's the residual cross-
        // monitor flash. Hiding source first means the position update is
        // invisible.
        if (_dragGhostArmed && _dragSourceTopLevel != null && !_dragGhostShown)
        {
            try
            {
                var probeLocal = e.GetPosition(_dragSourceTopLevel);
                var probeBounds = _dragSourceTopLevel.ClientSize;
                bool aboutToCross =
                    probeLocal.X < 0 || probeLocal.X >= probeBounds.Width ||
                    probeLocal.Y < 0 || probeLocal.Y >= probeBounds.Height;
                if (aboutToCross)
                {
                    this.Opacity = 0;
                }
            }
            catch { /* probe failed; non-fatal */ }
        }

        // Defensive: catch the very rare "wrong LayoutManager" exception
        // that Avalonia's Win32 backend can fire when the cursor crosses
        // a monitor boundary mid-drag with cross-DPI scaling and pointer
        // capture in flight. The Win32 pointer-routing layer can briefly
        // re-target the captured control via a different TopLevel's
        // LayoutManager during the cross-monitor pointer relay, and the
        // attached-property writes below would then propagate an arrange
        // invalidation through that wrong manager. Swallowing here lets
        // the drag survive a single bad frame instead of crashing the app;
        // the next PointerMoved with consistent state writes correctly.
        try
        {
            WindowX = newX;
            WindowY = newY;
        }
        catch (System.ArgumentException)
        {
            // "Attempt to call InvalidateArrange on wrong LayoutManager."
            // The drag will recover on the next pointer move.
        }

        // Multi-monitor ghost: lazy-show the moment the cursor leaves source
        // monitor bounds (avoids the same-monitor swap flash). Position is
        // updated EVERY pointer move - even while still invisible - so the
        // layered window's content is continuously aligned with where the
        // cursor will be when the swap fires. This pre-positioning is what
        // makes the first cross-monitor transition atomic and flicker-free:
        // when SetVisible(true) lands, the bitmap is already at the right
        // place; the OS just toggles alpha.
        if (_dragGhostArmed && _dragSourceTopLevel != null)
        {
            try
            {
                var cursorLocal = e.GetPosition(_dragSourceTopLevel);
                var cursorScreen = _dragSourceTopLevel.PointToScreen(cursorLocal);
                DOSI.CORE.UIComponents.WindowManagement.DragGhostWindow.Shared?.MoveTo(
                    new Avalonia.PixelPoint(
                        cursorScreen.X - _dragGhostCursorOffset.X,
                        cursorScreen.Y - _dragGhostCursorOffset.Y));
            }
            catch { /* drop frame on PointToScreen failure */ }
            EnsureGhostShownIfCursorLeftSource(e);
        }
    }

    private void OnChromeDragEnded(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            e.Pointer.Capture(null);
            DragStateChanged?.Invoke(this, false);

            // Multi-monitor: if the cursor was released over a different
            // monitor's host window, transfer this DOSIWindow to that
            // monitor's WindowManager. NOTE: TryHandoffToMonitorAtCursor
            // resolves the target SYNCHRONOUSLY but DEFERS the actual
            // reparent to the next dispatcher tick (see its docs for why -
            // reparenting mid-PointerReleased throws "wrong LayoutManager"
            // out of Avalonia's Win32 pointer-routing layer). We capture
            // whether a handoff is queued so the post-cleanup below can
            // ALSO defer (otherwise we'd Opacity=1 the source while it's
            // still on the source canvas at its off-edge drag position,
            // briefly showing it clipped at the source monitor's edge).
            bool handoffQueued = TryHandoffToMonitorAtCursor(e);

            // Stash drag-ghost state for the deferred / immediate cleanup
            // below. Local copies because the field clears must happen
            // synchronously here (so a fast follow-up drag doesn't see
            // stale armed=true state) but the ghost.HideGhost call has
            // to wait for the deferred reparent.
            bool wasArmed = _dragGhostArmed;
            _dragGhostArmed = false;
            _dragGhostShown = false;
            _dragGhostSnapshot = null;
            _dragSourceTopLevel = null;

            void CompleteCleanup()
            {
                // Restore source visibility AFTER the handoff so the source
                // reappears at its final position - either at the dragged-
                // to coordinates on the source monitor (no handoff) or
                // under the cursor on the target monitor (handoff
                // succeeded). Then park the ghost (Hide via Opacity +
                // off-screen position - the pool keeps the native window
                // alive for the next drag). Order matters: if we hid the
                // ghost FIRST we'd get a one-frame flash of nothing while
                // the source is still invisible.
                this.Opacity = 1;
                if (wasArmed)
                {
                    try { DOSI.CORE.UIComponents.WindowManagement.DragGhostWindow.Shared?.HideGhost(); } catch { }
                }
            }

            if (handoffQueued)
            {
                // Defer cleanup so it runs in the SAME dispatcher tick as
                // (and after) the deferred reparent. Background priority
                // matches what TryHandoffToMonitorAtCursor used; FIFO
                // ordering at the same priority guarantees the reparent
                // post runs before this cleanup post.
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    CompleteCleanup,
                    Avalonia.Threading.DispatcherPriority.Background);
            }
            else
            {
                // Single-monitor or same-monitor release: nothing to wait
                // for, restore visibility immediately.
                CompleteCleanup();
            }
        }
    }

    /// <summary>
    /// If the pointer's release-point is over a different <see cref="DOSI.CORE.UIComponents.IDosiHost"/>
    /// than the one currently owning this window, hand the window over to
    /// that host's <see cref="WindowManager"/>. Position the window so the
    /// cursor lands inside the title bar at roughly the same horizontal
    /// offset the user grabbed it at on the source monitor.
    ///
    /// IMPORTANT: this method only RESOLVES the target and computes drop
    /// coordinates synchronously while the PointerReleased event args are
    /// still valid. The actual reparent (RelinquishWindow + AdoptWindow)
    /// is deferred via <see cref="Avalonia.Threading.Dispatcher"/> so it
    /// runs AFTER the current pointer event has fully unwound. Reparenting
    /// the captured chrome's visual tree while we're still inside the
    /// PointerReleased handler causes Avalonia's Win32 backend to call
    /// InvalidateArrange via the wrong (now-changed) LayoutManager when it
    /// fires the post-release PointerExited / cursor-update events on the
    /// way out - that's the "Attempt to call InvalidateArrange on wrong
    /// LayoutManager" exception. Deferring lets the pointer event delivery
    /// machinery finish its routing through the ORIGINAL LayoutManager
    /// before we yank the visual tree out from under it.
    /// </summary>
    private bool TryHandoffToMonitorAtCursor(PointerReleasedEventArgs e)
    {
        var hosts = DOSI.CORE.UIComponents.DosiHostRegistry.All;
        if (hosts.Count <= 1) return false;

        // Translate the pointer's local position (relative to the source
        // host's TopLevel) into screen-pixel coords so we can test it
        // against every host's TargetScreen.Bounds.
        var sourceTop = Avalonia.Controls.TopLevel.GetTopLevel(this);
        if (sourceTop == null) return false;
        var localPos = e.GetPosition(sourceTop);
        Avalonia.PixelPoint screenPos;
        try { screenPos = sourceTop.PointToScreen(localPos); }
        catch { return false; /* not yet attached */ }

        DOSI.CORE.UIComponents.IDosiHost? targetHost = null;
        foreach (var h in hosts)
        {
            var s = h.TargetScreen;
            if (s == null) continue;
            if (ReferenceEquals(h, OwnerManager == null ? null : FindHostFor(OwnerManager, hosts))) continue;
            if (s.Bounds.Contains(screenPos))
            {
                targetHost = h;
                break;
            }
        }

        if (targetHost == null) return false;
        if (ReferenceEquals(targetHost.WindowManager, OwnerManager)) return false;

        var sourceManager = OwnerManager;
        if (sourceManager == null) return false;

        // Compute target-canvas-relative position. Map the screen-pixel
        // release point into the target host's TopLevel DIP coords so the
        // window appears under the cursor on the new monitor.
        if (targetHost is not Avalonia.Controls.TopLevel targetTop) return false;
        Avalonia.Point targetLocal;
        try
        {
            // PointToClient is the inverse of PointToScreen.
            targetLocal = targetTop.PointToClient(screenPos);
        }
        catch { return false; }

        // Preserve the cursor's offset within the title bar from the drag
        // start so the window slides into place under the user's finger
        // instead of teleporting to its top-left.
        //
        //   offset_in_titlebar = _dragStartPoint - _windowStartPosition
        //                        (cursor pos relative to source canvas
        //                         MINUS source window's top-left in same
        //                         coords = pure intra-window offset)
        //   newWindowPos       = currentCursor_in_target - offset_in_titlebar
        //                      = targetLocal - _dragStartPoint + _windowStartPosition
        var dropX = targetLocal.X - _dragStartPoint.X + _windowStartPosition.X;
        var dropY = targetLocal.Y - _dragStartPoint.Y + _windowStartPosition.Y;

        // Snapshot the references; the deferred lambda below mustn't close
        // over `this`'s mutable state any more than necessary.
        var pendingTargetHost = targetHost;
        var pendingSourceManager = sourceManager;
        var pendingDropX = dropX;
        var pendingDropY = dropY;

        // Defer the actual reparent. DispatcherPriority.Background runs
        // AFTER pending input / render frames, which is exactly what we
        // need - the PointerReleased event chain (including any post-
        // release PointerExited / cursor updates Avalonia's Win32 backend
        // wants to fire on the captured chrome) finishes routing through
        // the source TopLevel's LayoutManager before the reparent yanks
        // the visual tree out from under it.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // Re-validate: the window may have been closed / re-parented
            // by some other code path between event time and now.
            if (_isClosing) return;
            if (OwnerManager != pendingSourceManager) return;

            try
            {
                // CRITICAL: flush any pending layout invalidations queued
                // against the SOURCE TopLevel's LayoutManager BEFORE we
                // reparent. Without this, those invalidations would fire
                // on the next dispatcher iteration AFTER the swap, by
                // which point the control belongs to the target's
                // LayoutManager - and Avalonia's layout system would
                // throw "Attempt to call InvalidateArrange on wrong
                // LayoutManager" because the queued invalidation still
                // references the source manager. UpdateLayout() forces
                // an immediate measure+arrange pass, draining the queue
                // against the (still correct) source manager.
                var sourceTopForFlush = Avalonia.Controls.TopLevel.GetTopLevel(this);
                try { sourceTopForFlush?.UpdateLayout(); } catch { }

                pendingSourceManager.RelinquishWindow(this);
                pendingTargetHost.WindowManager.AdoptWindow(this, pendingDropX, pendingDropY);

                // Symmetric flush on the target so any layout work the
                // newly-adopted control needs runs immediately under the
                // correct manager - not later, possibly racing with
                // other dispatcher work.
                var targetTopForFlush = Avalonia.Controls.TopLevel.GetTopLevel(this);
                try { targetTopForFlush?.UpdateLayout(); } catch { }
            }
            catch
            {
                // If the handoff fails for any reason, best-effort: re-adopt
                // back into the source manager so the window doesn't vanish.
                try { pendingSourceManager.AdoptWindow(this, WindowX, WindowY); } catch { }
            }
        }, Avalonia.Threading.DispatcherPriority.Background);
        return true;
    }

    /// <summary>
    /// Reverse-lookup: find the <see cref="DOSI.CORE.UIComponents.IDosiHost"/>
    /// whose <c>WindowManager</c> equals <paramref name="manager"/>.
    /// </summary>
    private static DOSI.CORE.UIComponents.IDosiHost? FindHostFor(
        WindowManager manager,
        IReadOnlyList<DOSI.CORE.UIComponents.IDosiHost> hosts)
    {
        foreach (var h in hosts)
            if (ReferenceEquals(h.WindowManager, manager)) return h;
        return null;
    }

    private void OnResizeGripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!CanResize || WindowState != DOSIWindowState.Normal) return;
        if (sender is not Border grip || grip.Tag is not ResizeDirection direction) return;

        _isResizing = true;
        _resizeDirection = direction;
        _resizeStartSize = new Size(Width, Height);
        _resizeStartPosition = new Point(WindowX, WindowY);
        _dragStartPoint = e.GetPosition(Parent as Visual);
        e.Pointer.Capture(grip);
        e.Handled = true;

        // User explicitly chose a new size - the pre-snap dimensions are no
        // longer the right thing to restore on a future drag.
        ClearSnapMark();
    }

    private void OnResizeGripPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isResizing) return;
        var currentPoint = e.GetPosition(Parent as Visual);
        var delta = currentPoint - _dragStartPoint;
        ApplyResize(delta);
        e.Handled = true;
    }

    private void OnResizeGripPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isResizing)
        {
            _isResizing = false;
            _resizeDirection = ResizeDirection.None;
            e.Pointer.Capture(null);
        }
    }

    private void ApplyResize(Point delta)
    {
        var newWidth = _resizeStartSize.Width;
        var newHeight = _resizeStartSize.Height;
        var newX = _resizeStartPosition.X;
        var newY = _resizeStartPosition.Y;

        if (_resizeDirection.HasFlag(ResizeDirection.Right))
            newWidth = Math.Clamp(_resizeStartSize.Width + delta.X, MinimumSize.Width, MaximumSize.Width);

        if (_resizeDirection.HasFlag(ResizeDirection.Bottom))
            newHeight = Math.Clamp(_resizeStartSize.Height + delta.Y, MinimumSize.Height, MaximumSize.Height);

        if (_resizeDirection.HasFlag(ResizeDirection.Left))
        {
            var proposedWidth = _resizeStartSize.Width - delta.X;
            if (proposedWidth >= MinimumSize.Width && proposedWidth <= MaximumSize.Width)
            {
                newWidth = proposedWidth;
                newX = _resizeStartPosition.X + delta.X;
            }
        }

        if (_resizeDirection.HasFlag(ResizeDirection.Top))
        {
            var proposedHeight = _resizeStartSize.Height - delta.Y;
            if (proposedHeight >= MinimumSize.Height && proposedHeight <= MaximumSize.Height)
            {
                newHeight = proposedHeight;
                newY = Math.Max(0, _resizeStartPosition.Y + delta.Y);
            }
        }

        Width = newWidth;
        Height = newHeight;
        WindowX = newX;
        WindowY = newY;
    }

    #endregion

    #region Window State Management

    /// <summary>
    /// Expands this window to fill the entire DOSI desktop canvas (including
    /// the area normally reserved for the taskbar) and hides its title-bar
    /// chrome. Intended for page-driven fullscreen scenarios such as a
    /// YouTube video clicking the fullscreen button - the host browser
    /// then also hides its own toolbar / status bar to leave the WebView
    /// surface edge-to-edge. Idempotent; calling twice is a no-op.
    /// <para>
    /// This is NOT the same as <see cref="DOSIWindowState.Maximized"/>:
    /// maximize respects <see cref="WindowManager.TopWorkAreaInset"/> (the
    /// taskbar stays visible above the window). Immersive fullscreen
    /// bypasses that inset and additionally fires
    /// <see cref="AnyWindowFullScreenChanged"/> so the desktop chrome
    /// hides itself for the duration.
    /// </para>
    /// </summary>
    public void EnterImmersiveFullScreen()
    {
        if (_isImmersiveFullScreen) return;
        if (Parent is not Canvas canvas) return;

        // Capture pre-fullscreen geometry so ExitImmersiveFullScreen can
        // restore exactly where the window was before YouTube grabbed it.
        // If the window was already Maximized we drop back to Normal first
        // so the saved bounds reflect the underlying user-set size.
        _savedFullScreenChrome = _chromeRoot.IsVisible;
        _savedFullScreenState = _windowState;
        _savedFullScreenBounds = _windowState == DOSIWindowState.Normal
            ? new Rect(WindowX, WindowY, WindowWidth, WindowHeight)
            : _restoreBounds;

        _isImmersiveFullScreen = true;
        _chromeRoot.IsVisible = false;

        // Snap (no animation) - fullscreen is meant to feel instantaneous,
        // and any animation here would race the WebView's own enter-fs
        // resize and produce a visible jitter as the video reflows mid-tween.
        WindowX = 0;
        WindowY = 0;
        WindowWidth = canvas.Bounds.Width;
        WindowHeight = canvas.Bounds.Height;

        BringToFront();

        IsAnyWindowFullScreen = true;
        AnyWindowFullScreenChanged?.Invoke(this, true);
    }

    /// <summary>
    /// Reverses <see cref="EnterImmersiveFullScreen"/>: restores the saved
    /// geometry, chrome visibility, and state, and notifies the desktop so
    /// it can re-show its taskbar.
    /// </summary>
    public void ExitImmersiveFullScreen()
    {
        if (!_isImmersiveFullScreen) return;
        _isImmersiveFullScreen = false;

        _chromeRoot.IsVisible = _savedFullScreenChrome;
        WindowX = _savedFullScreenBounds.X;
        WindowY = _savedFullScreenBounds.Y;
        WindowWidth = _savedFullScreenBounds.Width;
        WindowHeight = _savedFullScreenBounds.Height;

        // If we were maximized before fullscreen, re-apply maximize so the
        // window snaps back to filling the work area (above the taskbar).
        if (_savedFullScreenState == DOSIWindowState.Maximized)
        {
            _windowState = DOSIWindowState.Normal; // force the setter to act
            SetWindowState(DOSIWindowState.Maximized);
        }

        IsAnyWindowFullScreen = false;
        AnyWindowFullScreenChanged?.Invoke(this, false);
    }

    private void SetWindowState(DOSIWindowState newState)
    {
        if (_windowState == newState || _isAnimating) return;

        var oldState = _windowState;
        if (oldState == DOSIWindowState.Normal)
            _restoreBounds = new Rect(WindowX, WindowY, WindowWidth, WindowHeight);

        _windowState = newState;

        switch (newState)
        {
            case DOSIWindowState.Normal:
                IsVisible = true;
                UpdateMaximizeButtonIcon(isMaximized: false);
                _ = AnimateWindowToAsync(_restoreBounds.X, _restoreBounds.Y, _restoreBounds.Width, _restoreBounds.Height);
                break;

            case DOSIWindowState.Minimized:
                _ = PlayMinimizeAnimationAsync();
                break;

            case DOSIWindowState.Maximized:
                IsVisible = true;
                UpdateMaximizeButtonIcon(isMaximized: true);
                if (Parent is Canvas canvas)
                {
                    // Fill the desktop work area, leaving the host's reserved
                    // top inset (taskbar / menu bar) visible above the window.
                    var topInset = OwnerManager?.TopWorkAreaInset ?? 0;
                    _ = AnimateWindowToAsync(0, topInset,
                                             canvas.Bounds.Width,
                                             canvas.Bounds.Height - topInset);
                }
                break;
        }

        UpdateResizeGripPositions();
        StateChanged?.Invoke(this, new DOSIWindowStateChangedEventArgs(this, oldState, newState));
    }

    private async Task PlayMinimizeAnimationAsync()
    {
        if (_isAnimating) return;
        _isAnimating = true;

        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(180),
            Easing = new CubicEaseIn(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0),
                    Setters =
                    {
                        new Setter(OpacityProperty, 1.0),
                        new Setter(ScaleTransform.ScaleXProperty, 1.0),
                        new Setter(ScaleTransform.ScaleYProperty, 1.0)
                    }
                },
                new KeyFrame
                {
                    Cue = new Cue(1),
                    Setters =
                    {
                        new Setter(OpacityProperty, 0.0),
                        new Setter(ScaleTransform.ScaleXProperty, 0.8),
                        new Setter(ScaleTransform.ScaleYProperty, 0.8)
                    }
                }
            }
        };

        await animation.RunAsync(this);

        IsVisible = false;
        Opacity = 1;
        RenderTransform = new ScaleTransform(1, 1);
        _isAnimating = false;
    }

    private async Task PlayRestoreFromMinimizeAnimationAsync()
    {
        IsVisible = true;
        Opacity = 0;
        RenderTransform = new ScaleTransform(0.8, 0.8);

        if (_isAnimating) return;
        _isAnimating = true;

        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(200),
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0),
                    Setters =
                    {
                        new Setter(OpacityProperty, 0.0),
                        new Setter(ScaleTransform.ScaleXProperty, 0.8),
                        new Setter(ScaleTransform.ScaleYProperty, 0.8)
                    }
                },
                new KeyFrame
                {
                    Cue = new Cue(1),
                    Setters =
                    {
                        new Setter(OpacityProperty, 1.0),
                        new Setter(ScaleTransform.ScaleXProperty, 1.0),
                        new Setter(ScaleTransform.ScaleYProperty, 1.0)
                    }
                }
            }
        };

        await animation.RunAsync(this);

        Opacity = 1;
        RenderTransform = new ScaleTransform(1, 1);
        _isAnimating = false;
    }

    private void UpdateFocusVisuals()
    {
        _chromeRoot.Background = _isFocused
            ? Accents.WindowChromeBrush
            : Accents.WindowChromeUnfocusedBrush;

        _titleText.Foreground = _isFocused
            ? Accents.TextPrimaryBrush
            : Accents.TextSecondaryBrush;

        _windowBorder.BorderBrush = _isFocused
            ? Accents.WindowBorderFocusedBrush
            : Accents.WindowBorderUnfocusedBrush;

        // Subtle opacity dip for unfocused windows so the active window
        // visually pops without us having to brighten its chrome. 0.92
        // is enough to register peripherally without making background
        // windows feel disabled. Skipped while a drag/resize/animation
        // is in flight - changing alpha mid-tween would jitter the
        // composite.
        if (!_isAnimating)
        {
            _windowBorder.Opacity = _isFocused ? 1.0 : 0.92;
        }
    }

    private void OnAccentChanged(object? sender, EventArgs e)
    {
        // Window backgrounds (and every accent-themed control inside) repaint
        // through this single hook. The brushes returned by AccentManager are
        // already alpha-modulated by AccentManager.WindowOpacity, so this also
        // applies live transparency changes without an extra code path.
        _windowBorder.Background = Accents.WindowBackgroundBrush;
        _windowBorder.BoxShadow = CreateMacOSShadow();
        _contentContainer.Background = Accents.WindowContentBrush;

        // Update icon
        _iconHost.Children.Clear();
        _iconHost.Children.Add(CreateDefaultIcon());

        // Note: macOS-style traffic light buttons don't need updating - their colors are fixed

        // Refresh focus visuals
        UpdateFocusVisuals();
    }

    #endregion

    #region Public Methods

    public void Close()
    {
        var closingArgs = new DOSIWindowClosingEventArgs(this);
        Closing?.Invoke(this, closingArgs);

        if (!closingArgs.Cancel)
        {
            _ = CloseWithAnimationAsync();
        }
    }

    /// <summary>
    /// Raises <see cref="Closing"/> and <see cref="Closed"/> without playing
    /// the close animation or removing the window from the canvas. Used by
    /// <c>WindowManager.CloseWindow</c> / <c>CloseAllWindows</c> so handlers
    /// that own native resources (e.g. <c>DOSIWebBrowser</c> disposing its
    /// WebView2 HWND) still get a chance to clean up when the manager - not
    /// the window itself - initiates the close (e.g. system shutdown).
    /// Cancellation is intentionally ignored on this path: the manager has
    /// already decided to close the window.
    /// </summary>
    internal void NotifyClosingForRemoval()
    {
        try { Closing?.Invoke(this, new DOSIWindowClosingEventArgs(this)); } catch { }
        try { Closed?.Invoke(this, new DOSIWindowEventArgs(this)); } catch { }
    }

    private async Task CloseWithAnimationAsync()
    {
        await PlayCloseAnimationAsync();
        (OwnerManager ?? WindowManager.Instance)?.CloseWindow(this);
        Closed?.Invoke(this, new DOSIWindowEventArgs(this));
    }

    public void BringToFront() => (OwnerManager ?? WindowManager.Instance)?.BringToFront(this);

    public void SendToBack() => (OwnerManager ?? WindowManager.Instance)?.SendToBack(this);

    public void Restore()
    {
        if (WindowState == DOSIWindowState.Minimized)
        {
            _windowState = DOSIWindowState.Normal;
            _ = PlayRestoreFromMinimizeAnimationAsync();
            (OwnerManager ?? WindowManager.Instance)?.BringToFront(this);
            StateChanged?.Invoke(this, new DOSIWindowStateChangedEventArgs(this, DOSIWindowState.Minimized, DOSIWindowState.Normal));
        }
    }

    public void Activate()
    {
        Restore();
        BringToFront();
    }

    #endregion
}

