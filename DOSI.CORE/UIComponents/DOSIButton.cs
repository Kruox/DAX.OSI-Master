using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DOSI.CORE.AccentManagement;

namespace DOSI.CORE.UIComponents;

/// <summary>
/// A 100% custom-drawn Button control for the DOSI operating system.
/// Supports icons, rounded corners, custom theming, and hover/press states.
/// </summary>
public class DOSIButton : Control
{
    #region Fields

        private bool _isHovered;
        private bool _isPressed;

        // Shadow and depth settings for a polished OS look
        private const double ShadowBlurRadius = 4.0;
        private const double ShadowOffsetY = 2.0;
        private const double PressedOffsetY = 0.0; // No vertical movement on press to avoid jitter
        private const double ShadowOpacity = 0.25;

        private static AccentManager Accents => AccentManager.Instance;

        #endregion

    #region Styled Properties

    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<DOSIButton, string>(nameof(Text), defaultValue: "");

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<DOSIButton, double>(nameof(FontSize), defaultValue: 14.0);

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<DOSIButton, CornerRadius>(nameof(CornerRadius), defaultValue: new CornerRadius(6));

    public static readonly StyledProperty<Thickness> PaddingProperty =
        AvaloniaProperty.Register<DOSIButton, Thickness>(nameof(Padding), defaultValue: new Thickness(16, 8));

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<DOSIButton, IBrush?>(nameof(Background));

    public static readonly StyledProperty<IBrush?> BackgroundHoverProperty =
        AvaloniaProperty.Register<DOSIButton, IBrush?>(nameof(BackgroundHover));

    public static readonly StyledProperty<IBrush?> BackgroundPressedProperty =
        AvaloniaProperty.Register<DOSIButton, IBrush?>(nameof(BackgroundPressed));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<DOSIButton, IBrush?>(nameof(Foreground));

    public static readonly StyledProperty<IBrush?> BorderBrushProperty =
        AvaloniaProperty.Register<DOSIButton, IBrush?>(nameof(BorderBrush));

    public static readonly StyledProperty<double> BorderThicknessProperty =
        AvaloniaProperty.Register<DOSIButton, double>(nameof(BorderThickness), defaultValue: 1.0);

    public static readonly StyledProperty<IImage?> IconProperty =
        AvaloniaProperty.Register<DOSIButton, IImage?>(nameof(Icon));

    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<DOSIButton, double>(nameof(IconSize), defaultValue: 16.0);

    public static readonly StyledProperty<double> IconSpacingProperty =
        AvaloniaProperty.Register<DOSIButton, double>(nameof(IconSpacing), defaultValue: 8.0);

    public static readonly StyledProperty<bool> UseRoundedEndsProperty =
        AvaloniaProperty.Register<DOSIButton, bool>(nameof(UseRoundedEnds), defaultValue: false);

    public static new readonly StyledProperty<bool> IsEnabledProperty =
        AvaloniaProperty.Register<DOSIButton, bool>(nameof(IsEnabled), defaultValue: true);

    public static readonly StyledProperty<global::Avalonia.Layout.HorizontalAlignment> HorizontalContentAlignmentProperty =
        AvaloniaProperty.Register<DOSIButton, global::Avalonia.Layout.HorizontalAlignment>(nameof(HorizontalContentAlignment), defaultValue: global::Avalonia.Layout.HorizontalAlignment.Center);

    #endregion

    #region Properties

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public Thickness Padding
    {
        get => GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public IBrush? BackgroundHover
    {
        get => GetValue(BackgroundHoverProperty);
        set => SetValue(BackgroundHoverProperty, value);
    }

    public IBrush? BackgroundPressed
    {
        get => GetValue(BackgroundPressedProperty);
        set => SetValue(BackgroundPressedProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush? BorderBrush
    {
        get => GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    public double BorderThickness
    {
        get => GetValue(BorderThicknessProperty);
        set => SetValue(BorderThicknessProperty, value);
    }

    public IImage? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public double IconSpacing
    {
        get => GetValue(IconSpacingProperty);
        set => SetValue(IconSpacingProperty, value);
    }

    /// <summary>
    /// When true, corner radius is automatically set to half the height for pill-shaped ends.
    /// </summary>
    public bool UseRoundedEnds
    {
        get => GetValue(UseRoundedEndsProperty);
        set => SetValue(UseRoundedEndsProperty, value);
    }

    public new bool IsEnabled
    {
        get => GetValue(IsEnabledProperty);
        set => SetValue(IsEnabledProperty, value);
    }

    public global::Avalonia.Layout.HorizontalAlignment HorizontalContentAlignment
    {
        get => GetValue(HorizontalContentAlignmentProperty);
        set => SetValue(HorizontalContentAlignmentProperty, value);
    }

    #endregion

    #region Events

    public event EventHandler<RoutedEventArgs>? Click;

    #endregion

    #region Constructor

    static DOSIButton()
    {
        FocusableProperty.OverrideDefaultValue<DOSIButton>(true);
        // Suppress the default white focus rectangle; we draw our own accent-coloured
        // focus indicator inside Render().
        FocusAdornerProperty.OverrideDefaultValue<DOSIButton>(null);

        TextProperty.Changed.AddClassHandler<DOSIButton>((btn, _) => btn.InvalidateVisual());
        FontSizeProperty.Changed.AddClassHandler<DOSIButton>((btn, _) => btn.InvalidateVisual());
        IconProperty.Changed.AddClassHandler<DOSIButton>((btn, _) => btn.InvalidateVisual());
        IconSizeProperty.Changed.AddClassHandler<DOSIButton>((btn, _) => btn.InvalidateVisual());
        UseRoundedEndsProperty.Changed.AddClassHandler<DOSIButton>((btn, _) => btn.InvalidateVisual());
        IsEnabledProperty.Changed.AddClassHandler<DOSIButton>((btn, _) => btn.InvalidateVisual());
    }

    public DOSIButton()
    {
        Cursor = new Cursor(StandardCursorType.Hand);

        // Suppress the default Fluent focus rectangle (white outline). Setting this
        // as a local value overrides the accent style that re-applies it.
        FocusAdorner = null;

        // Set default accent colors
        Background = Accents.ButtonBackgroundBrush;
        BackgroundHover = Accents.ButtonBackgroundHoverBrush;
        BackgroundPressed = Accents.ButtonBackgroundPressedBrush;
        Foreground = Accents.TextPrimaryBrush;
        BorderBrush = new SolidColorBrush(Accents.ControlBorder);

        // Repaint when focus changes so the accent focus ring appears/disappears.
        GotFocus += (_, _) => InvalidateVisual();
        LostFocus += (_, _) => InvalidateVisual();

        // Subscribe to accent changes
        AttachedToVisualTree += (s, e) => Accents.AccentChanged += OnAccentChanged;
        DetachedFromVisualTree += (s, e) => Accents.AccentChanged -= OnAccentChanged;
    }

    #endregion

    #region Accent Handling

    private void OnAccentChanged(object? sender, EventArgs e)
    {
        Background = Accents.ButtonBackgroundBrush;
        BackgroundHover = Accents.ButtonBackgroundHoverBrush;
        BackgroundPressed = Accents.ButtonBackgroundPressedBrush;
        Foreground = Accents.TextPrimaryBrush;
        BorderBrush = new SolidColorBrush(Accents.ControlBorder);
        InvalidateVisual();
    }

    #endregion

    #region Rendering

    public override void Render(DrawingContext context)
    {
        var fullBounds = new Rect(0, 0, Bounds.Width, Bounds.Height);
        var padding = Padding;

        // Reserve a constant amount of space for the drop shadow so the button
        // never resizes between hover/pressed states (which would cause jitter).
        var shadowSpace = ShadowBlurRadius;
        var buttonBounds = new Rect(0, 0, fullBounds.Width, fullBounds.Height - shadowSpace);

        // Apply pressed offset for tactile feedback
        var pressedOffset = _isPressed && IsEnabled ? PressedOffsetY : 0;
        var adjustedBounds = buttonBounds.Translate(new Vector(0, pressedOffset));

        var cornerRadius = UseRoundedEnds ? new CornerRadius(adjustedBounds.Height / 2) : CornerRadius;

        // Draw shadow for depth (only when not pressed and enabled)
        if (IsEnabled && !_isPressed)
        {
            DrawButtonShadow(context, adjustedBounds, cornerRadius);
        }
        else if (IsEnabled && _isPressed)
        {
            // Subtle inner shadow when pressed
            DrawPressedShadow(context, adjustedBounds, cornerRadius);
        }

        // Determine background based on state - use gradients for depth
        IBrush backgroundBrush = CreateBackgroundBrush(adjustedBounds);

        // Draw background
        var geometry = CreateRoundedRectGeometry(adjustedBounds, cornerRadius);
        context.DrawGeometry(backgroundBrush, null, geometry);

        // Draw subtle top highlight for 3D effect (glass-like shine)
        if (IsEnabled && !_isPressed)
        {
            DrawTopHighlight(context, adjustedBounds, cornerRadius);
        }

        // Draw border - use accent color when hovered, pressed, or focused
        if (BorderThickness > 0)
        {
            IBrush borderBrush;
            double borderWidth = BorderThickness;

            if (_isPressed)
            {
                borderBrush = Accents.AccentPrimaryBrush;
                borderWidth = BorderThickness + 0.5;
            }
            else if (_isHovered)
            {
                borderBrush = Accents.AccentSecondaryBrush;
            }
            else if (IsFocused && IsEnabled)
            {
                borderBrush = Accents.AccentPrimaryBrush;
            }
            else
            {
                borderBrush = BorderBrush ?? new SolidColorBrush(Accents.ControlBorder);
            }

            var borderPen = new Pen(borderBrush, borderWidth);
            context.DrawGeometry(null, borderPen, geometry);
        }

        // Calculate content dimensions
        var iconWidth = Icon != null ? IconSize : 0;
        var textWidth = !string.IsNullOrEmpty(Text) ? GetTextWidth(Text) : 0;
        var spacing = (Icon != null && !string.IsNullOrEmpty(Text)) ? IconSpacing : 0;
        var totalContentWidth = iconWidth + spacing + textWidth;

        // Calculate starting X position based on alignment
        double contentX;
        switch (HorizontalContentAlignment)
        {
            case global::Avalonia.Layout.HorizontalAlignment.Left:
                contentX = padding.Left;
                break;
            case global::Avalonia.Layout.HorizontalAlignment.Right:
                contentX = adjustedBounds.Width - padding.Right - totalContentWidth;
                break;
            case global::Avalonia.Layout.HorizontalAlignment.Center:
            default:
                contentX = (adjustedBounds.Width - totalContentWidth) / 2;
                break;
        }

        // Determine foreground color
        var foregroundBrush = IsEnabled
            ? (Foreground ?? Accents.TextPrimaryBrush)
            : new SolidColorBrush(Accents.TextDisabled);

        // Draw icon if present
        if (Icon != null)
        {
            var iconY = adjustedBounds.Top + (adjustedBounds.Height - IconSize) / 2;
            var iconRect = new Rect(contentX, iconY, IconSize, IconSize);

            if (!IsEnabled)
            {
                using (context.PushOpacity(0.5))
                {
                    context.DrawImage(Icon, iconRect);
                }
            }
            else
            {
                context.DrawImage(Icon, iconRect);
            }

            contentX += IconSize + spacing;
        }

        // Draw text if present
        if (!string.IsNullOrEmpty(Text))
        {
            var formattedText = CreateFormattedText(Text, foregroundBrush);
            var textY = adjustedBounds.Top + (adjustedBounds.Height - formattedText.Height) / 2;
            context.DrawText(formattedText, new Point(contentX, textY));
        }

        // Draw focus indicator
        if (IsFocused && IsEnabled)
        {
            var focusBounds = adjustedBounds.Inflate(-2);
            var focusRadius = UseRoundedEnds 
                ? new CornerRadius(focusBounds.Height / 2) 
                : new CornerRadius(Math.Max(0, cornerRadius.TopLeft - 2), 
                    Math.Max(0, cornerRadius.TopRight - 2),
                    Math.Max(0, cornerRadius.BottomRight - 2),
                    Math.Max(0, cornerRadius.BottomLeft - 2));
            var focusGeometry = CreateRoundedRectGeometry(focusBounds, focusRadius);
            var focusPen = new Pen(Accents.AccentPrimaryBrush, 1.5);
            context.DrawGeometry(null, focusPen, focusGeometry);
        }
    }

    private IBrush CreateBackgroundBrush(Rect bounds)
    {
        if (!IsEnabled)
        {
            return new SolidColorBrush(Color.FromArgb(128, 
                Accents.ControlBackground.R, 
                Accents.ControlBackground.G, 
                Accents.ControlBackground.B));
        }

        // Get base color from state
        Color baseColor;
        if (_isPressed)
        {
            baseColor = GetColorFromBrush(BackgroundPressed ?? Accents.ButtonBackgroundPressedBrush);
        }
        else if (_isHovered)
        {
            baseColor = GetColorFromBrush(BackgroundHover ?? Accents.ButtonBackgroundHoverBrush);
        }
        else
        {
            baseColor = GetColorFromBrush(Background ?? Accents.ButtonBackgroundBrush);
        }

        // Create subtle vertical gradient for depth
        var lighterColor = LightenColor(baseColor, _isPressed ? 0 : 0.08);
        var darkerColor = DarkenColor(baseColor, _isPressed ? 0.05 : 0.03);

        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(lighterColor, 0),
                new GradientStop(baseColor, 0.5),
                new GradientStop(darkerColor, 1)
            }
        };
    }

    private void DrawButtonShadow(DrawingContext context, Rect bounds, CornerRadius cornerRadius)
    {
        // Draw multiple layers of shadow for soft effect
        var shadowColor = Color.FromArgb((byte)(255 * ShadowOpacity * 0.3), 0, 0, 0);
        var shadowBounds = bounds.Translate(new Vector(0, ShadowOffsetY)).Inflate(1);
        var shadowGeometry = CreateRoundedRectGeometry(shadowBounds, cornerRadius);
        context.DrawGeometry(new SolidColorBrush(shadowColor), null, shadowGeometry);

        // Second layer - tighter shadow
        var shadowColor2 = Color.FromArgb((byte)(255 * ShadowOpacity * 0.5), 0, 0, 0);
        var shadowBounds2 = bounds.Translate(new Vector(0, ShadowOffsetY * 0.5));
        var shadowGeometry2 = CreateRoundedRectGeometry(shadowBounds2, cornerRadius);
        context.DrawGeometry(new SolidColorBrush(shadowColor2), null, shadowGeometry2);
    }

    private void DrawPressedShadow(DrawingContext context, Rect bounds, CornerRadius cornerRadius)
    {
        // Subtle inset shadow effect when pressed
        var innerShadowColor = Color.FromArgb((byte)(255 * 0.15), 0, 0, 0);
        var innerBounds = bounds.Inflate(-1);
        var innerGeometry = CreateRoundedRectGeometry(innerBounds, 
            new CornerRadius(
                Math.Max(0, cornerRadius.TopLeft - 1),
                Math.Max(0, cornerRadius.TopRight - 1),
                Math.Max(0, cornerRadius.BottomRight - 1),
                Math.Max(0, cornerRadius.BottomLeft - 1)));

        // Top edge shadow
        var topEdge = new Rect(innerBounds.X, innerBounds.Y, innerBounds.Width, 2);
        context.DrawRectangle(new SolidColorBrush(innerShadowColor), null, topEdge);
    }

    private void DrawTopHighlight(DrawingContext context, Rect bounds, CornerRadius cornerRadius)
    {
        // Subtle top highlight for glass-like effect
        var highlightHeight = Math.Min(bounds.Height * 0.4, 12);
        var highlightBounds = new Rect(bounds.X + 1, bounds.Y + 1, bounds.Width - 2, highlightHeight);

        var highlightRadius = new CornerRadius(
            Math.Max(0, cornerRadius.TopLeft - 1),
            Math.Max(0, cornerRadius.TopRight - 1),
            0, 0);

        var highlightBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(Color.FromArgb(25, 255, 255, 255), 0),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 1)
            }
        };

        var highlightGeometry = CreateRoundedRectGeometry(highlightBounds, highlightRadius);
        context.DrawGeometry(highlightBrush, null, highlightGeometry);
    }

    private static Color GetColorFromBrush(IBrush brush)
    {
        return brush switch
        {
            SolidColorBrush scb => scb.Color,
            LinearGradientBrush lgb when lgb.GradientStops.Count > 0 => lgb.GradientStops[0].Color,
            _ => Colors.Gray
        };
    }

    private static Color LightenColor(Color color, double amount)
    {
        return Color.FromArgb(
            color.A,
            (byte)Math.Min(255, color.R + (255 - color.R) * amount),
            (byte)Math.Min(255, color.G + (255 - color.G) * amount),
            (byte)Math.Min(255, color.B + (255 - color.B) * amount));
    }

    private static Color DarkenColor(Color color, double amount)
    {
        return Color.FromArgb(
            color.A,
            (byte)Math.Max(0, color.R * (1 - amount)),
            (byte)Math.Max(0, color.G * (1 - amount)),
            (byte)Math.Max(0, color.B * (1 - amount)));
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

    private FormattedText CreateFormattedText(string text, IBrush brush)
    {
        return new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default),
            FontSize,
            brush);
    }

    private double GetTextWidth(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var ft = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default),
            FontSize,
            Brushes.Black);
        return ft.Width;
    }

    #endregion

    #region Input Handling

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        if (IsEnabled)
        {
            _isHovered = true;
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _isHovered = false;
        _isPressed = false;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (IsEnabled && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _isPressed = true;
            e.Handled = true;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (IsEnabled && _isPressed)
        {
            _isPressed = false;
            
            // Check if pointer is still over the button
            var position = e.GetPosition(this);
            if (position.X >= 0 && position.X <= Bounds.Width &&
                position.Y >= 0 && position.Y <= Bounds.Height)
            {
                OnClick();
            }
            
            InvalidateVisual();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (IsEnabled && (e.Key == Key.Enter || e.Key == Key.Space))
        {
            _isPressed = true;
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (IsEnabled && _isPressed && (e.Key == Key.Enter || e.Key == Key.Space))
        {
            _isPressed = false;
            OnClick();
            InvalidateVisual();
            e.Handled = true;
        }
    }

    private void OnClick()
    {
        Click?.Invoke(this, new RoutedEventArgs());
    }

    #endregion

    #region Measure/Arrange

    protected override Size MeasureOverride(Size availableSize)
    {
        var padding = Padding;
        var iconWidth = Icon != null ? IconSize : 0;
        var textWidth = !string.IsNullOrEmpty(Text) ? GetTextWidth(Text) : 0;
        var spacing = (Icon != null && !string.IsNullOrEmpty(Text)) ? IconSpacing : 0;

        var textHeight = !string.IsNullOrEmpty(Text) ? FontSize * 1.2 : 0;
        var iconHeight = Icon != null ? IconSize : 0;

        var width = padding.Left + iconWidth + spacing + textWidth + padding.Right;
        // Add shadow space to height for proper rendering
        var height = padding.Top + Math.Max(iconHeight, textHeight) + padding.Bottom + ShadowBlurRadius;

        return new Size(
            Math.Min(width, availableSize.Width),
            Math.Min(height, availableSize.Height));
    }

    #endregion
}
