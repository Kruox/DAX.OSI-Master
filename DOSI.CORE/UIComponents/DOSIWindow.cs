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
    /// </summary>
    internal WindowManager? OwnerManager { get; set; }

    public double WindowX
    {
        get => Canvas.GetLeft(this) + ShadowMargin;
        set => Canvas.SetLeft(this, value - ShadowMargin);
    }

    public double WindowY
    {
        get => Canvas.GetTop(this) + ShadowMargin;
        set => Canvas.SetTop(this, value - ShadowMargin);
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

        var startX = WindowX;
        var startY = WindowY;
        var startWidth = WindowWidth;
        var startHeight = WindowHeight;

        var duration = StateAnimationDuration;
        var easing = new CubicEaseInOut();
        var startTime = DateTime.Now;

        while (true)
        {
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

        // Ensure final values are set exactly
        WindowX = targetX;
        WindowY = targetY;
        WindowWidth = targetWidth;
        WindowHeight = targetHeight;

        _isAnimating = false;
    }

    private static double Lerp(double start, double end, double t)
    {
        return start + (end - start) * t;
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
    }

    private void OnChromeDragMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging || _isResizing) return;

        var currentPoint = e.GetPosition(Parent as Visual);
        var delta = currentPoint - _dragStartPoint;

        // Only restore from maximized when user actually starts dragging
        if (WindowState == DOSIWindowState.Maximized)
        {
            // Check if user moved enough to consider it a drag (not just a click)
            if (Math.Abs(delta.X) > 5 || Math.Abs(delta.Y) > 5)
            {
                var mousePos = currentPoint;
                var percentX = _dragStartPoint.X / Bounds.Width;
                WindowState = DOSIWindowState.Normal;
                WindowX = mousePos.X - (WindowWidth * percentX);
                WindowY = mousePos.Y - 12; // Center on title bar height
                _dragStartPoint = currentPoint;
                _windowStartPosition = new Point(WindowX, WindowY);
            }
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

            // Clamp X: keep at least minVisibleWidth visible on screen
            newX = Math.Max(-WindowWidth + minVisibleWidth, newX);  // Left edge
            newX = Math.Min(canvasWidth - minVisibleWidth, newX);   // Right edge

            // Clamp Y: top edge respects the host's reserved top area (e.g.
            // taskbar height) so the window's title bar can never disappear
            // behind persistent chrome.
            newY = Math.Max(topInset, newY);                          // Top edge
            newY = Math.Min(canvasHeight - titleBarHeight, newY);     // Bottom edge
        }

        WindowX = newX;
        WindowY = newY;
    }

    private void OnChromeDragEnded(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            e.Pointer.Capture(null);
            DragStateChanged?.Invoke(this, false);
        }
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

