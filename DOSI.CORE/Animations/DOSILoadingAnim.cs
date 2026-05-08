using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using DOSI.CORE.AccentManagement;

namespace DOSI.CORE.Animations;

/// <summary>
/// A modern, elegant loading animation control for the DOSI operating system.
/// Features dual spinning arcs with glow effects and accent color theming.
/// </summary>
public class DOSILoadingAnim : UserControl, IDisposable
{
    private readonly Arc _outerArc;
    private readonly Arc _innerArc;
    private readonly Ellipse _glowRing;
    private readonly RotateTransform _outerRotate;
    private readonly RotateTransform _innerRotate;
    private readonly TextBlock? _label;
    private DispatcherTimer? _animationTimer;
    private double _outerAngle;
    private double _innerAngle;
    private double _glowOpacity = 0.3;
    private int _glowDirection = 1;
    private bool _isDisposed;

    private static AccentManager Accents => AccentManager.Instance;

    /// <summary>
    /// Gets the size of the loading animation.
    /// </summary>
    public LoadingSize Size { get; }

    /// <summary>
    /// Gets or sets optional label text displayed below the spinner.
    /// </summary>
    public string? LabelText
    {
        get => _label?.Text;
        set
        {
            if (_label != null)
                _label.Text = value;
        }
    }

    public DOSILoadingAnim(LoadingSize size = LoadingSize.Medium, string? labelText = null)
    {
        Size = size;
        var (diameter, strokeWidth) = GetSizeValues(size);
        var innerDiameter = diameter * 0.6;
        var innerStroke = strokeWidth * 0.7;

        _outerRotate = new RotateTransform(0);
        _innerRotate = new RotateTransform(0);

        var accentColor = Accents.AccentPrimary;

        // Subtle glow ring behind the arcs
        _glowRing = new Ellipse
        {
            Width = diameter + strokeWidth * 2,
            Height = diameter + strokeWidth * 2,
            Fill = Brushes.Transparent,
            Stroke = new ImmutableSolidColorBrush(accentColor, 0.3),
            StrokeThickness = strokeWidth * 2,
            Opacity = 0.3,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Outer spinning arc
        _outerArc = new Arc
        {
            Width = diameter,
            Height = diameter,
            StrokeThickness = strokeWidth,
            Stroke = CreateGradientBrush(accentColor),
            StrokeLineCap = PenLineCap.Round,
            StartAngle = 0,
            SweepAngle = 120,
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RenderTransform = _outerRotate,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Inner spinning arc (rotates opposite direction)
        _innerArc = new Arc
        {
            Width = innerDiameter,
            Height = innerDiameter,
            StrokeThickness = innerStroke,
            Stroke = new ImmutableSolidColorBrush(accentColor, 0.6),
            StrokeLineCap = PenLineCap.Round,
            StartAngle = 0,
            SweepAngle = 90,
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RenderTransform = _innerRotate,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Container for the spinner elements
        var spinnerGrid = new Grid
        {
            Width = diameter + strokeWidth * 4,
            Height = diameter + strokeWidth * 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        spinnerGrid.Children.Add(_glowRing);
        spinnerGrid.Children.Add(_outerArc);
        spinnerGrid.Children.Add(_innerArc);

        // Main container
        var container = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = size == LoadingSize.Small ? 8 : 16
        };

        container.Children.Add(spinnerGrid);

        // Optional label
        if (!string.IsNullOrEmpty(labelText))
        {
            _label = new TextBlock
            {
                Text = labelText,
                Foreground = Accents.TextSecondaryBrush,
                FontSize = size == LoadingSize.Small ? 11 : size == LoadingSize.Medium ? 13 : 15,
                FontWeight = FontWeight.Light,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Opacity = 0.9
            };
            container.Children.Add(_label);
        }

        // Root grid for centering
        var rootGrid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        rootGrid.Children.Add(container);

        Content = rootGrid;

        // Subscribe to lifecycle events
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        Accents.AccentChanged += OnAccentChanged;
        StartAnimation();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        Accents.AccentChanged -= OnAccentChanged;
        StopAnimation();
    }

    private static ImmutableLinearGradientBrush CreateGradientBrush(Color accent)
    {
        return new ImmutableLinearGradientBrush(
            new[]
            {
                new ImmutableGradientStop(0, accent),
                new ImmutableGradientStop(0.5, Color.FromArgb(200, accent.R, accent.G, accent.B)),
                new ImmutableGradientStop(1, Color.FromArgb(120, accent.R, accent.G, accent.B))
            },
            startPoint: new RelativePoint(0, 0, RelativeUnit.Relative),
            endPoint: new RelativePoint(1, 1, RelativeUnit.Relative));
    }

    private static (double diameter, double strokeWidth) GetSizeValues(LoadingSize size) => size switch
    {
        LoadingSize.Small => (24, 3),
        LoadingSize.Medium => (44, 4),
        LoadingSize.Large => (64, 5),
        LoadingSize.ExtraLarge => (90, 6),
        _ => (44, 4)
    };

    private void OnAccentChanged(object? sender, EventArgs e)
    {
        if (_isDisposed) return;

        var accentColor = Accents.AccentPrimary;
        _outerArc.Stroke = CreateGradientBrush(accentColor);
        _innerArc.Stroke = new ImmutableSolidColorBrush(accentColor, 0.6);
        _glowRing.Stroke = new ImmutableSolidColorBrush(accentColor, 0.3);

        if (_label != null)
            _label.Foreground = Accents.TextSecondaryBrush;
    }

    /// <summary>
    /// Starts the loading animation.
    /// </summary>
    public void StartAnimation()
    {
        if (_isDisposed) return;

        StopAnimation();

        _animationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16) // ~60fps
        };
        _animationTimer.Tick += OnAnimationTick;
        _animationTimer.Start();
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        if (_isDisposed) return;

        // Outer arc rotates clockwise
        _outerAngle = (_outerAngle + 4) % 360;
        _outerRotate.Angle = _outerAngle;

        // Inner arc rotates counter-clockwise (faster)
        _innerAngle = (_innerAngle - 6 + 360) % 360;
        _innerRotate.Angle = _innerAngle;

        // Subtle glow pulse
        _glowOpacity += _glowDirection * 0.008;
        if (_glowOpacity >= 0.5)
        {
            _glowOpacity = 0.5;
            _glowDirection = -1;
        }
        else if (_glowOpacity <= 0.15)
        {
            _glowOpacity = 0.15;
            _glowDirection = 1;
        }
        _glowRing.Opacity = _glowOpacity;
    }

    /// <summary>
    /// Stops the loading animation.
    /// </summary>
    public void StopAnimation()
    {
        if (_animationTimer != null)
        {
            _animationTimer.Stop();
            _animationTimer.Tick -= OnAnimationTick;
            _animationTimer = null;
        }
    }

    /// <summary>
    /// Creates a loading overlay that covers its parent container.
    /// </summary>
    public static Border CreateOverlay(LoadingSize size = LoadingSize.Medium, string? labelText = null)
    {
        return new Border
        {
            Background = new ImmutableSolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = new DOSILoadingAnim(size, labelText)
        };
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        StopAnimation();
        Accents.AccentChanged -= OnAccentChanged;
        AttachedToVisualTree -= OnAttachedToVisualTree;
        DetachedFromVisualTree -= OnDetachedFromVisualTree;

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Predefined sizes for the loading animation.
/// </summary>
public enum LoadingSize
{
    /// <summary>Small spinner (24px) - for inline use</summary>
    Small,
    /// <summary>Medium spinner (44px) - default size</summary>
    Medium,
    /// <summary>Large spinner (64px) - for prominent loading states</summary>
    Large,
    /// <summary>Extra large spinner (90px) - for full-screen loading</summary>
    ExtraLarge
}
