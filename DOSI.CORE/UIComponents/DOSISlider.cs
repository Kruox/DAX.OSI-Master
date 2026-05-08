using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DOSI.CORE.AccentManagement;

namespace DOSI.CORE.UIComponents;

/// <summary>
/// A 100% custom-drawn horizontal slider control for the DOSI virtual operating
/// system. Renders an accent-aware track with a filled portion behind the thumb,
/// an animated halo around the thumb on hover/drag, and an optional floating
/// value bubble that appears while the user is interacting with the control.
///
/// Designed to mirror the look-and-feel of <see cref="DOSIButton"/>,
/// <see cref="DOSITabControl"/>, and the rest of the DOSI UI kit.
/// </summary>
public class DOSISlider : Control
{
    #region Fields

    private static AccentManager Accents => AccentManager.Instance;

    private readonly Border _root;
    private readonly Grid _layoutGrid;
    private readonly Border _track;
    private readonly Border _fill;
    private readonly Border _thumbHalo;
    private readonly Border _thumb;
    private readonly Border _valueBubble;
    private readonly TextBlock _valueBubbleText;

    private bool _isHovered;
    private bool _isDragging;

    // Halo animation state (animated grow / shrink on hover or drag)
    private double _haloCurrent;
    private double _haloTarget;
    private DispatcherTimer? _haloTimer;

    private const double TrackHeight = 4;
    private const double ThumbSize = 16;
    private const double HaloSizeIdle = 16;
    private const double HaloSizeHover = 26;
    private const double HaloSizeDrag = 32;
    private const double HaloAnimMs = 140;
    private const double DefaultControlHeight = 28;

    #endregion

    #region Styled Properties

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<DOSISlider, double>(nameof(Minimum), defaultValue: 0.0);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<DOSISlider, double>(nameof(Maximum), defaultValue: 1.0);

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<DOSISlider, double>(nameof(Value), defaultValue: 0.0);

    public static readonly StyledProperty<double> StepProperty =
        AvaloniaProperty.Register<DOSISlider, double>(nameof(Step), defaultValue: 0.0);

    public static readonly StyledProperty<bool> ShowValueBubbleProperty =
        AvaloniaProperty.Register<DOSISlider, bool>(nameof(ShowValueBubble), defaultValue: true);

    public static readonly StyledProperty<string> ValueFormatProperty =
        AvaloniaProperty.Register<DOSISlider, string>(nameof(ValueFormat), defaultValue: "0.##");

    public static new readonly StyledProperty<bool> IsEnabledProperty =
        AvaloniaProperty.Register<DOSISlider, bool>(nameof(IsEnabled), defaultValue: true);

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
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Optional snap step. <c>0</c> (default) means continuous.</summary>
    public double Step
    {
        get => GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public bool ShowValueBubble
    {
        get => GetValue(ShowValueBubbleProperty);
        set => SetValue(ShowValueBubbleProperty, value);
    }

    /// <summary>
    /// Standard numeric format string used to render the value bubble label.
    /// </summary>
    public string ValueFormat
    {
        get => GetValue(ValueFormatProperty);
        set => SetValue(ValueFormatProperty, value);
    }

    public new bool IsEnabled
    {
        get => GetValue(IsEnabledProperty);
        set => SetValue(IsEnabledProperty, value);
    }

    #endregion

    #region Events

    /// <summary>Raised whenever <see cref="Value"/> changes (including via drag, key, or code).</summary>
    public event EventHandler<double>? ValueChanged;

    #endregion

    #region Constructor

    static DOSISlider()
    {
        FocusAdornerProperty.OverrideDefaultValue<DOSISlider>(null);

        ValueProperty.Changed.AddClassHandler<DOSISlider>((s, _) => s.OnValueChangedInternal());
        MinimumProperty.Changed.AddClassHandler<DOSISlider>((s, _) => s.RefreshLayout());
        MaximumProperty.Changed.AddClassHandler<DOSISlider>((s, _) => s.RefreshLayout());
        IsEnabledProperty.Changed.AddClassHandler<DOSISlider>((s, _) => s.RefreshVisuals());
    }

    public DOSISlider()
    {
        FocusAdorner = null;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        Height = DefaultControlHeight;

        // === Track (background rail) ===
        _track = new Border
        {
            Height = TrackHeight,
            CornerRadius = new CornerRadius(TrackHeight / 2),
            Background = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsHitTestVisible = false
        };

        // === Filled portion (left of thumb) ===
        _fill = new Border
        {
            Height = TrackHeight,
            CornerRadius = new CornerRadius(TrackHeight / 2),
            // Flat AccentPrimary instead of the gradient brush: a horizontal
            // gradient on a tiny fill width only shows the start color and
            // looks visually different from other accent surfaces nearby.
            Background = Accents.AccentPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0,
            IsHitTestVisible = false
        };

        // === Halo (animated glow behind thumb on hover/drag) ===
        _thumbHalo = new Border
        {
            Width = HaloSizeIdle,
            Height = HaloSizeIdle,
            CornerRadius = new CornerRadius(HaloSizeIdle / 2),
            Background = new SolidColorBrush(Color.FromArgb(0, Accents.AccentPrimary.R, Accents.AccentPrimary.G, Accents.AccentPrimary.B)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };

        // === Thumb (draggable handle) ===
        _thumb = new Border
        {
            Width = ThumbSize,
            Height = ThumbSize,
            CornerRadius = new CornerRadius(ThumbSize / 2),
            Background = Brushes.White,
            BorderBrush = Accents.AccentPrimaryBrush,
            BorderThickness = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0,
                OffsetY = 1,
                Blur = 4,
                Color = Color.FromArgb(80, 0, 0, 0)
            })
        };

        // === Value bubble (tooltip-style label above the thumb) ===
        _valueBubbleText = new TextBlock
        {
            Text = string.Empty,
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Accents.TextOnAccent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _valueBubble = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = Accents.AccentGradientBrush,
            Padding = new Thickness(8, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Opacity = 0,
            Child = _valueBubbleText,
            Margin = new Thickness(0, -4, 0, 0)
        };

        _layoutGrid = new Grid();
        _layoutGrid.Children.Add(_track);
        _layoutGrid.Children.Add(_fill);
        _layoutGrid.Children.Add(_thumbHalo);
        _layoutGrid.Children.Add(_thumb);
        _layoutGrid.Children.Add(_valueBubble);

        _root = new Border
        {
            Padding = new Thickness(ThumbSize / 2, 0),
            // Transparent background so the entire control surface is
            // hit-testable - without this, clicks pass through because all
            // child visuals are IsHitTestVisible = false.
            Background = Brushes.Transparent,
            Child = _layoutGrid
        };

        VisualChildren.Add(_root);
        LogicalChildren.Add(_root);

        PointerEntered += (_, _) => { _isHovered = true; UpdateHaloTarget(); };
        PointerExited += (_, _) => { _isHovered = false; UpdateHaloTarget(); };

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        KeyDown += OnKeyDown;

        AttachedToVisualTree += (_, _) =>
        {
            Accents.AccentChanged += OnAccentChanged;
            RefreshVisuals();
            RefreshLayout();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            Accents.AccentChanged -= OnAccentChanged;
            _haloTimer?.Stop();
            _haloTimer = null;
        };
    }

    #endregion

    #region Value / Layout

    private void OnValueChangedInternal()
    {
        var clamped = ClampToRange(Value);
        if (Math.Abs(clamped - Value) > double.Epsilon)
        {
            // Re-set with clamped value; this will re-enter and exit early below.
            Value = clamped;
            return;
        }

        RefreshLayout();
        UpdateValueBubbleText();
        ValueChanged?.Invoke(this, Value);
    }

    private double ClampToRange(double v)
    {
        var min = Math.Min(Minimum, Maximum);
        var max = Math.Max(Minimum, Maximum);
        v = Math.Clamp(v, min, max);
        if (Step > 0)
        {
            var stepped = Math.Round((v - min) / Step) * Step + min;
            v = Math.Clamp(stepped, min, max);
        }
        return v;
    }

    private double GetTrackWidth() =>
        Math.Max(0, _layoutGrid.Bounds.Width);

    private double GetNormalized()
    {
        var range = Maximum - Minimum;
        if (range <= 0) return 0;
        return Math.Clamp((Value - Minimum) / range, 0, 1);
    }

    private void RefreshLayout()
    {
        var trackWidth = GetTrackWidth();
        if (trackWidth <= 0) return;

        var t = GetNormalized();
        var thumbX = t * trackWidth - ThumbSize / 2;
        thumbX = Math.Clamp(thumbX, -ThumbSize / 2, trackWidth - ThumbSize / 2);

        _fill.Width = Math.Max(0, t * trackWidth);
        _thumb.Margin = new Thickness(Math.Max(0, t * trackWidth - ThumbSize / 2), 0, 0, 0);

        var haloOffset = t * trackWidth - _thumbHalo.Width / 2;
        _thumbHalo.Margin = new Thickness(Math.Max(-_thumbHalo.Width / 2, haloOffset), 0, 0, 0);

        // Position value bubble centered above the thumb.
        var bubbleWidth = _valueBubble.Bounds.Width;
        if (bubbleWidth <= 0) bubbleWidth = 36;
        var bubbleX = t * trackWidth - bubbleWidth / 2;
        _valueBubble.Margin = new Thickness(
            Math.Max(0, bubbleX),
            -((Bounds.Height / 2) + 6),
            0, 0);
    }

    private void UpdateValueBubbleText()
    {
        _valueBubbleText.Text = Value.ToString(ValueFormat,
            System.Globalization.CultureInfo.InvariantCulture);
    }

    #endregion

    #region Pointer / Keyboard

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsEnabled) return;
        e.Handled = true;
        Focus();
        _isDragging = true;
        e.Pointer.Capture(this);
        SetValueFromPointer(e.GetPosition(_layoutGrid));
        ShowValueBubble2(true);
        UpdateHaloTarget();
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging || !IsEnabled) return;
        SetValueFromPointer(e.GetPosition(_layoutGrid));
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        e.Pointer.Capture(null);
        ShowValueBubble2(false);
        UpdateHaloTarget();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsEnabled) return;
        var range = Maximum - Minimum;
        var step = Step > 0 ? Step : range / 100.0;

        switch (e.Key)
        {
            case Key.Left:
            case Key.Down:
                Value = ClampToRange(Value - step);
                e.Handled = true;
                break;
            case Key.Right:
            case Key.Up:
                Value = ClampToRange(Value + step);
                e.Handled = true;
                break;
            case Key.Home:
                Value = Minimum;
                e.Handled = true;
                break;
            case Key.End:
                Value = Maximum;
                e.Handled = true;
                break;
        }
    }

    private void SetValueFromPointer(Point p)
    {
        var trackWidth = GetTrackWidth();
        if (trackWidth <= 0) return;

        var t = Math.Clamp(p.X / trackWidth, 0, 1);
        Value = ClampToRange(Minimum + t * (Maximum - Minimum));
    }

    #endregion

    #region Halo / Bubble Animations

    private void UpdateHaloTarget()
    {
        if (!IsEnabled) { _haloTarget = HaloSizeIdle; }
        else if (_isDragging) _haloTarget = HaloSizeDrag;
        else if (_isHovered) _haloTarget = HaloSizeHover;
        else _haloTarget = HaloSizeIdle;

        AnimateHalo();
    }

    private void AnimateHalo()
    {
        _haloTimer?.Stop();

        var startSize = _haloCurrent <= 0 ? _thumbHalo.Width : _haloCurrent;
        var endSize = _haloTarget;
        var startTime = DateTime.UtcNow;

        // Halo fade alpha follows hover/drag state too.
        byte targetAlpha = (byte)(_isDragging ? 70 : (_isHovered ? 45 : 0));
        var startAlpha = (_thumbHalo.Background as SolidColorBrush)?.Color.A ?? (byte)0;

        _haloTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _haloTimer.Tick += (_, _) =>
        {
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            var t = Math.Clamp(elapsed / HaloAnimMs, 0.0, 1.0);
            var eased = 1 - Math.Pow(1 - t, 3);

            _haloCurrent = startSize + (endSize - startSize) * eased;
            _thumbHalo.Width = _haloCurrent;
            _thumbHalo.Height = _haloCurrent;
            _thumbHalo.CornerRadius = new CornerRadius(_haloCurrent / 2);

            var alpha = (byte)(startAlpha + (targetAlpha - startAlpha) * eased);
            _thumbHalo.Background = new SolidColorBrush(Color.FromArgb(
                alpha, Accents.AccentPrimary.R, Accents.AccentPrimary.G, Accents.AccentPrimary.B));

            RefreshLayout();

            if (t >= 1.0)
            {
                _haloTimer?.Stop();
                _haloTimer = null;
            }
        };
        _haloTimer.Start();
    }

    private void ShowValueBubble2(bool visible)
    {
        if (!ShowValueBubble) { _valueBubble.Opacity = 0; return; }
        _valueBubble.Opacity = visible ? 1 : 0;
    }

    #endregion

    #region Accent

    private void OnAccentChanged(object? sender, EventArgs e)
    {
        RefreshVisuals();
    }

    private void RefreshVisuals()
    {
        _fill.Background = Accents.AccentPrimaryBrush;
        _thumb.BorderBrush = Accents.AccentPrimaryBrush;
        _valueBubble.Background = Accents.AccentPrimaryBrush;
        _valueBubbleText.Foreground = new SolidColorBrush(Accents.TextOnAccent);

        if (!IsEnabled)
        {
            _thumb.Background = new SolidColorBrush(Color.FromArgb(180, 200, 200, 200));
            _thumb.BorderBrush = new SolidColorBrush(Color.FromArgb(120, 150, 150, 150));
            Cursor = new Cursor(StandardCursorType.No);
        }
        else
        {
            _thumb.Background = Brushes.White;
            Cursor = new Cursor(StandardCursorType.Hand);
        }

        UpdateHaloTarget();
        UpdateValueBubbleText();
    }

    #endregion

    #region Layout overrides

    protected override Size MeasureOverride(Size availableSize)
    {
        _root.Measure(availableSize);
        var w = double.IsFinite(availableSize.Width) ? availableSize.Width : 200;
        return new Size(w, Math.Max(DefaultControlHeight, _root.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _root.Arrange(new Rect(finalSize));
        // Sync thumb / fill / bubble positions to the freshly-arranged track.
        // Done synchronously here (instead of via LayoutUpdated) to avoid an
        // infinite layout pass: mutating child margins re-triggers layout.
        RefreshLayout();
        return finalSize;
    }

    #endregion
}
