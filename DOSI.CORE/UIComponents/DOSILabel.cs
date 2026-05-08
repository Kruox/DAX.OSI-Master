using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using DOSI.CORE.AccentManagement;

namespace DOSI.CORE.UIComponents;

/// <summary>
/// Themed static-text control matching the rest of the DOSI control set.
/// Renders the text with a subtle drop shadow (1px down, soft black) so
/// labels read crisply on both the chrome and content backgrounds without
/// extra effort from callers - the same look the OS uses elsewhere.
///
/// Why a custom Render rather than a TextBlock + DropShadowEffect: the
/// shadow effect spins up a per-control GPU pass which is overkill for a
/// label, and Avalonia's default TextBlock theming pulls in fluent styles
/// that don't match DOSI's accent system. Drawing it ourselves keeps the
/// control cheap, theme-aware, and visually consistent with DOSIButton /
/// DOSITextBox.
/// </summary>
public class DOSILabel : Control
{
    private static readonly Typeface s_typeface = new(FontFamily.Default);
    private static AccentManager Accents => AccentManager.Instance;

    #region Styled properties

    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<DOSILabel, string>(nameof(Text), defaultValue: "Label");

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<DOSILabel, double>(nameof(FontSize), defaultValue: 13.0);

    public static readonly StyledProperty<FontWeight> FontWeightProperty =
        AvaloniaProperty.Register<DOSILabel, FontWeight>(nameof(FontWeight), defaultValue: FontWeight.Normal);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<DOSILabel, IBrush?>(nameof(Foreground));

    public static readonly StyledProperty<bool> UseDropShadowProperty =
        AvaloniaProperty.Register<DOSILabel, bool>(nameof(UseDropShadow), defaultValue: true);

    public static readonly StyledProperty<TextAlignment> TextAlignmentProperty =
        AvaloniaProperty.Register<DOSILabel, TextAlignment>(nameof(TextAlignment), defaultValue: TextAlignment.Left);

    #endregion

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

    public FontWeight FontWeight
    {
        get => GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>When true, paints a 1-px-down soft-black shadow under the text.</summary>
    public bool UseDropShadow
    {
        get => GetValue(UseDropShadowProperty);
        set => SetValue(UseDropShadowProperty, value);
    }

    public TextAlignment TextAlignment
    {
        get => GetValue(TextAlignmentProperty);
        set => SetValue(TextAlignmentProperty, value);
    }

    static DOSILabel()
    {
        // Any property change repaints. Cheap because the control draws a
        // single FormattedText.
        AffectsMeasure<DOSILabel>(TextProperty, FontSizeProperty, FontWeightProperty, TextAlignmentProperty);
        AffectsRender<DOSILabel>(TextProperty, FontSizeProperty, FontWeightProperty, ForegroundProperty,
                                 UseDropShadowProperty, TextAlignmentProperty);
    }

    public DOSILabel()
    {
        Foreground = Accents.TextPrimaryBrush;
        AttachedToVisualTree += (_, _) => Accents.AccentChanged += OnAccentChanged;
        DetachedFromVisualTree += (_, _) => Accents.AccentChanged -= OnAccentChanged;
    }

    /// <summary>
    /// Raised when the user clicks the label. Labels are commonly used as
    /// link / hyperlink targets in DOSI apps, so we surface a Click event
    /// instead of forcing callers to wire raw pointer events.
    /// </summary>
    public event EventHandler<RoutedEventArgs>? Click;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        // Only fire on left-button clicks. Right / middle stay routable so
        // callers can attach context menus without piggybacking on Click.
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            Click?.Invoke(this, new RoutedEventArgs());
    }

    private void OnAccentChanged(object? sender, EventArgs e)
    {
        // Only refresh the brush if the caller hasn't overridden it - we
        // detect "default" by reference equality with the previous accent
        // brush. If users want a fixed colour they set Foreground to their
        // own SolidColorBrush and we leave it alone.
        if (Foreground is SolidColorBrush sb && sb.Color == ((SolidColorBrush)Accents.TextPrimaryBrush).Color)
            Foreground = Accents.TextPrimaryBrush;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var ft = BuildFormattedText(Foreground ?? Accents.TextPrimaryBrush);
        // +1 to height to account for the drop shadow's pixel offset so it
        // doesn't get clipped by tight parent layouts.
        return new Size(Math.Min(availableSize.Width, ft.Width),
                        Math.Min(availableSize.Height, ft.Height + (UseDropShadow ? 1 : 0)));
    }

    public override void Render(DrawingContext context)
    {
        if (string.IsNullOrEmpty(Text)) return;

        var fg = Foreground ?? Accents.TextPrimaryBrush;
        var ft = BuildFormattedText(fg);

        // Horizontal alignment within our bounds - vertical is always centred.
        var x = TextAlignment switch
        {
            TextAlignment.Center => (Bounds.Width - ft.Width) / 2,
            TextAlignment.Right  => Bounds.Width - ft.Width,
            _ => 0.0
        };
        var y = (Bounds.Height - ft.Height) / 2;

        if (UseDropShadow)
        {
            // Soft 1-px shadow underneath. Drawn first so the foreground
            // glyph sits cleanly on top.
            var shadowFt = BuildFormattedText(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)));
            context.DrawText(shadowFt, new Point(x, y + 1));
        }

        context.DrawText(ft, new Point(x, y));
    }

    private FormattedText BuildFormattedText(IBrush brush) => new(
        Text ?? string.Empty,
        System.Globalization.CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        new Typeface(s_typeface.FontFamily, FontStyle.Normal, FontWeight),
        FontSize,
        brush);
}
