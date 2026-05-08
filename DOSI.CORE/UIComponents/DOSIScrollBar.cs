using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DOSI.CORE.AccentManagement;

namespace DOSI.CORE.UIComponents;

/// <summary>
/// A 100% custom-drawn ScrollBar control for the DOSI operating system.
/// Features smooth animations, hover effects, and accent integration.
/// </summary>
public class DOSIScrollBar : Control
{
    #region Fields

    private bool _isHovered;
    private bool _isThumbHovered;
    private bool _isThumbPressed;
    private bool _isUpButtonHovered;
    private bool _isUpButtonPressed;
    private bool _isDownButtonHovered;
    private bool _isDownButtonPressed;
    // _isTrackPressed previously tracked track-area mouse-down for visual feedback;
    // it was never read so it has been removed.
    private double _dragStartY;
    private double _dragStartValue;

    // Animation fields
    private double _currentThumbOpacity = 0.6;
    private double _targetThumbOpacity = 0.6;
    private double _currentWidth;
    private double _targetWidth;
    private DispatcherTimer? _animationTimer;

    private static AccentManager Accents => AccentManager.Instance;

    // Layout constants
    private const double CollapsedWidth = 8;
    private const double ExpandedWidth = 14;
    private const double ButtonHeight = 20;
    private const double MinThumbHeight = 30;
    private const double AnimationDuration = 150; // ms

    #endregion

    #region Styled Properties

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<DOSIScrollBar, double>(nameof(Minimum), defaultValue: 0.0);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<DOSIScrollBar, double>(nameof(Maximum), defaultValue: 100.0);

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<DOSIScrollBar, double>(nameof(Value), defaultValue: 0.0);

    public static readonly StyledProperty<double> ViewportSizeProperty =
        AvaloniaProperty.Register<DOSIScrollBar, double>(nameof(ViewportSize), defaultValue: 10.0);

    public static readonly StyledProperty<double> SmallChangeProperty =
        AvaloniaProperty.Register<DOSIScrollBar, double>(nameof(SmallChange), defaultValue: 1.0);

    public static readonly StyledProperty<double> LargeChangeProperty =
        AvaloniaProperty.Register<DOSIScrollBar, double>(nameof(LargeChange), defaultValue: 10.0);

    public static readonly StyledProperty<bool> ShowButtonsProperty =
        AvaloniaProperty.Register<DOSIScrollBar, bool>(nameof(ShowButtons), defaultValue: true);

    public static readonly StyledProperty<bool> AutoHideProperty =
        AvaloniaProperty.Register<DOSIScrollBar, bool>(nameof(AutoHide), defaultValue: false);

    public static readonly StyledProperty<Orientation> OrientationProperty =
        AvaloniaProperty.Register<DOSIScrollBar, Orientation>(nameof(Orientation), defaultValue: Orientation.Vertical);

    #endregion

    #region Properties

    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, Math.Clamp(value, Minimum, Maximum));
    }

    public double ViewportSize
    {
        get => GetValue(ViewportSizeProperty);
        set => SetValue(ViewportSizeProperty, value);
    }

    public double SmallChange
    {
        get => GetValue(SmallChangeProperty);
        set => SetValue(SmallChangeProperty, value);
    }

    public double LargeChange
    {
        get => GetValue(LargeChangeProperty);
        set => SetValue(LargeChangeProperty, value);
    }

    public bool ShowButtons
    {
        get => GetValue(ShowButtonsProperty);
        set => SetValue(ShowButtonsProperty, value);
    }

    public bool AutoHide
    {
        get => GetValue(AutoHideProperty);
        set => SetValue(AutoHideProperty, value);
    }

    public Orientation Orientation
    {
        get => GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    #endregion

    #region Events

    public event EventHandler<ScrollEventArgs>? Scroll;

    #endregion

    #region Constructor

    static DOSIScrollBar()
    {
        // Suppress the default white focus rectangle on this DOSI control.
        FocusAdornerProperty.OverrideDefaultValue<DOSIScrollBar>(null);

        ValueProperty.Changed.AddClassHandler<DOSIScrollBar>((sb, _) => sb.OnValueChanged());
        MaximumProperty.Changed.AddClassHandler<DOSIScrollBar>((sb, _) => sb.InvalidateVisual());
        ViewportSizeProperty.Changed.AddClassHandler<DOSIScrollBar>((sb, _) => sb.InvalidateVisual());
        ShowButtonsProperty.Changed.AddClassHandler<DOSIScrollBar>((sb, _) => sb.InvalidateVisual());
        OrientationProperty.Changed.AddClassHandler<DOSIScrollBar>((sb, _) => sb.InvalidateMeasure());
    }

    public DOSIScrollBar()
    {
        // Suppress the default Fluent focus rectangle (white outline). Setting this
        // as a local value overrides the accent style that re-applies it.
        FocusAdorner = null;

        _currentWidth = CollapsedWidth;
        _targetWidth = CollapsedWidth;

        // Animation timer for smooth transitions
        _animationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _animationTimer.Tick += OnAnimationTick;

        // Subscribe to accent changes
        AttachedToVisualTree += (s, e) => Accents.AccentChanged += OnAccentChanged;
        DetachedFromVisualTree += (s, e) =>
        {
            Accents.AccentChanged -= OnAccentChanged;
            _animationTimer?.Stop();
        };
    }

    #endregion

    #region Animation

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        bool needsUpdate = false;

        // Animate width
        if (Math.Abs(_currentWidth - _targetWidth) > 0.1)
        {
            _currentWidth += (_targetWidth - _currentWidth) * 0.25;
            needsUpdate = true;
        }
        else if (_currentWidth != _targetWidth)
        {
            _currentWidth = _targetWidth;
            needsUpdate = true;
        }

        // Animate opacity
        if (Math.Abs(_currentThumbOpacity - _targetThumbOpacity) > 0.01)
        {
            _currentThumbOpacity += (_targetThumbOpacity - _currentThumbOpacity) * 0.25;
            needsUpdate = true;
        }
        else if (_currentThumbOpacity != _targetThumbOpacity)
        {
            _currentThumbOpacity = _targetThumbOpacity;
            needsUpdate = true;
        }

        if (needsUpdate)
        {
            InvalidateVisual();
        }
        else
        {
            _animationTimer?.Stop();
        }
    }

    private void StartAnimation()
    {
        if (_animationTimer != null && !_animationTimer.IsEnabled)
        {
            _animationTimer.Start();
        }
    }

    #endregion

    #region Accent Handling

    private void OnAccentChanged(object? sender, EventArgs e)
    {
        InvalidateVisual();
    }

    private void OnValueChanged()
    {
        Scroll?.Invoke(this, new ScrollEventArgs(Value));
        InvalidateVisual();
    }

    #endregion

    #region Layout

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Orientation == Orientation.Vertical)
        {
            // Use full available height, not limited to 200
            var height = double.IsInfinity(availableSize.Height) ? 200 : availableSize.Height;
            return new Size(ExpandedWidth, height);
        }
        else
        {
            // Use full available width, not limited to 200
            var width = double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width;
            return new Size(width, ExpandedWidth);
        }
    }

    private double GetTrackStart() => ShowButtons ? ButtonHeight : 0;
    private double GetTrackLength() => Bounds.Height - (ShowButtons ? ButtonHeight * 2 : 0);

    private double GetThumbHeight()
    {
        var range = Maximum - Minimum + ViewportSize;
        if (range <= 0) return GetTrackLength();

        var ratio = ViewportSize / range;
        var thumbHeight = Math.Max(MinThumbHeight, GetTrackLength() * ratio);
        return Math.Min(thumbHeight, GetTrackLength());
    }

    private double GetThumbPosition()
    {
        var range = Maximum - Minimum;
        if (range <= 0) return GetTrackStart();

        var ratio = (Value - Minimum) / range;
        var availableTrack = GetTrackLength() - GetThumbHeight();
        return GetTrackStart() + (availableTrack * ratio);
    }

    private Rect GetUpButtonBounds() => new(0, 0, Bounds.Width, ButtonHeight);
    private Rect GetDownButtonBounds() => new(0, Bounds.Height - ButtonHeight, Bounds.Width, ButtonHeight);
    private Rect GetTrackBounds() => new(0, GetTrackStart(), Bounds.Width, GetTrackLength());
    private Rect GetThumbBounds()
    {
        var thumbMargin = (_isHovered || _isThumbPressed) ? 2 : 3;
        var thumbWidth = _currentWidth - (thumbMargin * 2);
        var xOffset = (Bounds.Width - thumbWidth) / 2;
        return new Rect(xOffset, GetThumbPosition(), thumbWidth, GetThumbHeight());
    }

    #endregion

    #region Rendering

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(0, 0, Bounds.Width, Bounds.Height);

        // Draw track background
        var trackBrush = new SolidColorBrush(Color.FromArgb(40,
            Accents.ControlBackground.R,
            Accents.ControlBackground.G,
            Accents.ControlBackground.B));

        var trackGeometry = CreateRoundedRectGeometry(bounds, new CornerRadius(bounds.Width / 2));
        context.DrawGeometry(trackBrush, null, trackGeometry);

        // Draw buttons if enabled
        if (ShowButtons)
        {
            DrawButton(context, GetUpButtonBounds(), true, _isUpButtonHovered, _isUpButtonPressed);
            DrawButton(context, GetDownButtonBounds(), false, _isDownButtonHovered, _isDownButtonPressed);
        }

        // Draw thumb
        DrawThumb(context);

        // Draw subtle border
        var borderPen = new Pen(new SolidColorBrush(Color.FromArgb(30, 
            Accents.ControlBorder.R, 
            Accents.ControlBorder.G, 
            Accents.ControlBorder.B)), 1);
        context.DrawGeometry(null, borderPen, trackGeometry);
    }

    private void DrawButton(DrawingContext context, Rect bounds, bool isUp, bool isHovered, bool isPressed)
    {
        // Button background
        Color bgColor;
        if (isPressed)
            bgColor = Accents.ControlBackgroundPressed;
        else if (isHovered)
            bgColor = Accents.ControlBackgroundHover;
        else
            bgColor = Color.FromArgb(60, Accents.ControlBackground.R, Accents.ControlBackground.G, Accents.ControlBackground.B);

        var bgBrush = new SolidColorBrush(bgColor);
        var cornerRadius = isUp ? new CornerRadius(bounds.Width / 2, bounds.Width / 2, 0, 0)
                                : new CornerRadius(0, 0, bounds.Width / 2, bounds.Width / 2);
        var geometry = CreateRoundedRectGeometry(bounds, cornerRadius);
        context.DrawGeometry(bgBrush, null, geometry);

        // Arrow
        var arrowColor = isHovered || isPressed ? Accents.TextPrimary : Accents.TextSecondary;
        var arrowBrush = new SolidColorBrush(arrowColor);
        var arrowSize = 6.0;
        var centerX = bounds.Center.X;
        var centerY = bounds.Center.Y;

        var arrowGeometry = new StreamGeometry();
        using (var ctx = arrowGeometry.Open())
        {
            if (isUp)
            {
                ctx.BeginFigure(new Point(centerX, centerY - arrowSize / 2), true);
                ctx.LineTo(new Point(centerX + arrowSize / 2, centerY + arrowSize / 2));
                ctx.LineTo(new Point(centerX - arrowSize / 2, centerY + arrowSize / 2));
            }
            else
            {
                ctx.BeginFigure(new Point(centerX, centerY + arrowSize / 2), true);
                ctx.LineTo(new Point(centerX + arrowSize / 2, centerY - arrowSize / 2));
                ctx.LineTo(new Point(centerX - arrowSize / 2, centerY - arrowSize / 2));
            }
            ctx.EndFigure(true);
        }
        context.DrawGeometry(arrowBrush, null, arrowGeometry);
    }

    private void DrawThumb(DrawingContext context)
    {
        var thumbBounds = GetThumbBounds();

        // Create gradient for thumb
        Color thumbColor;
        if (_isThumbPressed)
            thumbColor = Accents.AccentPrimary;
        else if (_isThumbHovered)
            thumbColor = Color.FromArgb(220, Accents.AccentPrimary.R, Accents.AccentPrimary.G, Accents.AccentPrimary.B);
        else
            thumbColor = Color.FromArgb((byte)(255 * _currentThumbOpacity),
                Accents.AccentSecondary.R, Accents.AccentSecondary.G, Accents.AccentSecondary.B);

        // Subtle gradient for depth
        var topColor = LightenColor(thumbColor, 0.15);
        var bottomColor = DarkenColor(thumbColor, 0.1);

        var gradientBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(topColor, 0),
                new GradientStop(thumbColor, 0.5),
                new GradientStop(bottomColor, 1)
            }
        };

        var thumbRadius = new CornerRadius(thumbBounds.Width / 2);
        var thumbGeometry = CreateRoundedRectGeometry(thumbBounds, thumbRadius);

        // Draw glow when pressed or hovered
        if (_isThumbPressed || _isThumbHovered)
        {
            var glowBounds = thumbBounds.Inflate(1);
            var glowGeometry = CreateRoundedRectGeometry(glowBounds, new CornerRadius(glowBounds.Width / 2));
            var glowBrush = new SolidColorBrush(Color.FromArgb(50, 
                Accents.AccentPrimary.R, 
                Accents.AccentPrimary.G, 
                Accents.AccentPrimary.B));
            context.DrawGeometry(glowBrush, null, glowGeometry);
        }

        context.DrawGeometry(gradientBrush, null, thumbGeometry);

        // Draw grip lines on thumb when it's wide enough for them to read.
        // We previously gated on (_isHovered || _isThumbPressed), but
        // pointer-entered/exited events for the scrollbar can be missed
        // when the bar sits next to a native surface (e.g. the WebView2
        // HWND in DOSIWebBrowser) - the bar still animates to its
        // expanded width via the wider hit area, but _isHovered ends up
        // stale and the grip never appears even though visually the bar
        // is expanded. Driving off the thumb's actual rendered width is
        // equivalent for every other DOSIScrollBar (the thumb is only
        // wide enough on hover/drag) and works reliably everywhere.
        if (thumbBounds.Height > 50 && thumbBounds.Width >= 6)
        {
            var gripColor = Color.FromArgb(80, 255, 255, 255);
            var gripPen = new Pen(new SolidColorBrush(gripColor), 1);
            var centerY = thumbBounds.Center.Y;
            var lineWidth = thumbBounds.Width * 0.4;
            var lineX = thumbBounds.Center.X - lineWidth / 2;

            for (int i = -1; i <= 1; i++)
            {
                var y = centerY + (i * 4);
                context.DrawLine(gripPen, new Point(lineX, y), new Point(lineX + lineWidth, y));
            }
        }
    }

    private Color LightenColor(Color color, double factor)
    {
        return Color.FromArgb(color.A,
            (byte)Math.Min(255, color.R + (255 - color.R) * factor),
            (byte)Math.Min(255, color.G + (255 - color.G) * factor),
            (byte)Math.Min(255, color.B + (255 - color.B) * factor));
    }

    private Color DarkenColor(Color color, double factor)
    {
        return Color.FromArgb(color.A,
            (byte)(color.R * (1 - factor)),
            (byte)(color.G * (1 - factor)),
            (byte)(color.B * (1 - factor)));
    }

    private StreamGeometry CreateRoundedRectGeometry(Rect rect, CornerRadius radius)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();

        var topLeft = Math.Min(radius.TopLeft, Math.Min(rect.Width / 2, rect.Height / 2));
        var topRight = Math.Min(radius.TopRight, Math.Min(rect.Width / 2, rect.Height / 2));
        var bottomRight = Math.Min(radius.BottomRight, Math.Min(rect.Width / 2, rect.Height / 2));
        var bottomLeft = Math.Min(radius.BottomLeft, Math.Min(rect.Width / 2, rect.Height / 2));

        ctx.BeginFigure(new Point(rect.Left + topLeft, rect.Top), true);

        ctx.LineTo(new Point(rect.Right - topRight, rect.Top));
        if (topRight > 0)
            ctx.ArcTo(new Point(rect.Right, rect.Top + topRight), new Size(topRight, topRight), 0, false, SweepDirection.Clockwise);

        ctx.LineTo(new Point(rect.Right, rect.Bottom - bottomRight));
        if (bottomRight > 0)
            ctx.ArcTo(new Point(rect.Right - bottomRight, rect.Bottom), new Size(bottomRight, bottomRight), 0, false, SweepDirection.Clockwise);

        ctx.LineTo(new Point(rect.Left + bottomLeft, rect.Bottom));
        if (bottomLeft > 0)
            ctx.ArcTo(new Point(rect.Left, rect.Bottom - bottomLeft), new Size(bottomLeft, bottomLeft), 0, false, SweepDirection.Clockwise);

        ctx.LineTo(new Point(rect.Left, rect.Top + topLeft));
        if (topLeft > 0)
            ctx.ArcTo(new Point(rect.Left + topLeft, rect.Top), new Size(topLeft, topLeft), 0, false, SweepDirection.Clockwise);

        ctx.EndFigure(true);
        return geometry;
    }

    #endregion

    #region Input Handling

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _isHovered = true;
        _targetWidth = ExpandedWidth;
        _targetThumbOpacity = 0.85;
        StartAnimation();
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _isHovered = false;
        _isThumbHovered = false;
        _isUpButtonHovered = false;
        _isDownButtonHovered = false;

        if (!_isThumbPressed)
        {
            _targetWidth = CollapsedWidth;
            _targetThumbOpacity = 0.6;
            StartAnimation();
        }
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);

        // Update hover states
        var wasThumbHovered = _isThumbHovered;
        var wasUpHovered = _isUpButtonHovered;
        var wasDownHovered = _isDownButtonHovered;

        _isThumbHovered = GetThumbBounds().Contains(pos);
        _isUpButtonHovered = ShowButtons && GetUpButtonBounds().Contains(pos);
        _isDownButtonHovered = ShowButtons && GetDownButtonBounds().Contains(pos);

        if (wasThumbHovered != _isThumbHovered || wasUpHovered != _isUpButtonHovered || wasDownHovered != _isDownButtonHovered)
        {
            _targetThumbOpacity = _isThumbHovered ? 1.0 : 0.85;
            StartAnimation();
            InvalidateVisual();
        }

        // Handle thumb dragging
        if (_isThumbPressed)
        {
            var deltaY = pos.Y - _dragStartY;
            var range = Maximum - Minimum;
            var availableTrack = GetTrackLength() - GetThumbHeight();

            if (availableTrack > 0)
            {
                var deltaValue = (deltaY / availableTrack) * range;
                Value = Math.Clamp(_dragStartValue + deltaValue, Minimum, Maximum);
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pos = e.GetPosition(this);

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            // Check thumb first
            if (GetThumbBounds().Contains(pos))
            {
                _isThumbPressed = true;
                _dragStartY = pos.Y;
                _dragStartValue = Value;
                e.Handled = true;
            }
            // Check up button
            else if (ShowButtons && GetUpButtonBounds().Contains(pos))
            {
                _isUpButtonPressed = true;
                Value = Math.Max(Minimum, Value - SmallChange);
                e.Handled = true;
            }
            // Check down button
            else if (ShowButtons && GetDownButtonBounds().Contains(pos))
            {
                _isDownButtonPressed = true;
                Value = Math.Min(Maximum, Value + SmallChange);
                e.Handled = true;
            }
            // Click on track - page up/down
            else if (GetTrackBounds().Contains(pos))
            {
                if (pos.Y < GetThumbPosition())
                    Value = Math.Max(Minimum, Value - LargeChange);
                else
                    Value = Math.Min(Maximum, Value + LargeChange);
                e.Handled = true;
            }

            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        _isThumbPressed = false;
        _isUpButtonPressed = false;
        _isDownButtonPressed = false;

        if (!_isHovered)
        {
            _targetWidth = CollapsedWidth;
            _targetThumbOpacity = 0.6;
            StartAnimation();
        }

        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var delta = e.Delta.Y * SmallChange * 3;
        Value = Math.Clamp(Value - delta, Minimum, Maximum);
        e.Handled = true;
    }

    #endregion
}

/// <summary>
/// Event args for scroll events.
/// </summary>
public class ScrollEventArgs : EventArgs
{
    public double NewValue { get; }

    public ScrollEventArgs(double newValue)
    {
        NewValue = newValue;
    }
}
