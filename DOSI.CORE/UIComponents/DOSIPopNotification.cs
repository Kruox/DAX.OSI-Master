using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using DOSI.CORE.AccentManagement;

namespace DOSI.CORE.UIComponents;

/// <summary>
/// A transient toast-style notification that appears centered horizontally
/// just under the taskbar. Notifications stack downward and automatically
/// fade out after <see cref="DefaultLifetime"/>. Use <see cref="Show"/>
/// to display one.
/// </summary>
public class DOSIPopNotification : Border
{
    #region Constants

    private const double TopOffset = 36;          // taskbar (28) + small gap
    private const double NotifSpacing = 8;        // vertical gap between stacked toasts
    private const double SlideInDistance = 16;    // px traveled during fade-in

    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FadeInDuration = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan FadeOutDuration = TimeSpan.FromMilliseconds(280);
    private static readonly TimeSpan ReflowDuration = TimeSpan.FromMilliseconds(220);

    #endregion

    #region Static stack management

    private static readonly List<DOSIPopNotification> _active = [];
    private static Panel? _host;
    private static bool _hostSizeHooked;

    /// <summary>
    /// Application-wide default host panel for notifications. When set
    /// (typically once at startup by <c>MainWindow</c> to the popup overlay
    /// layer that sits ABOVE all <see cref="WindowManagement.DOSIWindow"/>
    /// instances), the parameterless <see cref="Show(string,TimeSpan?)"/>
    /// overload will use it. This guarantees toasts float over maximized
    /// or fullscreen windows.
    /// </summary>
    public static Panel? DefaultHost { get; set; }

    private static AccentManager Accents => AccentManager.Instance;

    /// <summary>
    /// Displays a notification with the given text on the supplied host panel.
    /// The host should be the desktop overlay layer (e.g. the desktop Canvas
    /// or root Grid). The same host is reused for subsequent calls.
    /// </summary>
    public static DOSIPopNotification Show(Panel host, string text, TimeSpan? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(host);

        _host = host;
        HookHostIfNeeded(host);

        var notif = new DOSIPopNotification(text);
        host.Children.Add(notif);
        _active.Add(notif);

        // On Canvas hosts, HorizontalAlignment.Center is ignored - manually
        // pin Canvas.Left so the toast stays horizontally centered. We do it
        // once now (best-effort) and again on the first layout pass via the
        // notification's own SizeChanged handler so we have a real width.
        Recenter(notif);
        notif.SizeChanged += (_, _) => Recenter(notif);

        ReflowStack(animate: true);
        _ = notif.RunLifecycleAsync(lifetime ?? DefaultLifetime);
        return notif;
    }

    /// <summary>
    /// Displays a notification on <see cref="DefaultHost"/> (or the most
    /// recently used host as a fallback). Throws <see cref="InvalidOperationException"/>
    /// if no host has been registered yet.
    /// </summary>
    public static DOSIPopNotification Show(string text, TimeSpan? lifetime = null)
    {
        var host = DefaultHost ?? _host
            ?? throw new InvalidOperationException(
                "DOSIPopNotification.DefaultHost has not been set. Call Show(host, text) at least once or assign DefaultHost.");
        return Show(host, text, lifetime);
    }

    private static void HookHostIfNeeded(Panel host)
    {
        if (_hostSizeHooked) return;
        if (host is not Canvas) return;
        _hostSizeHooked = true;
        host.SizeChanged += (_, _) => RecenterAll();
    }

    private static void RecenterAll()
    {
        foreach (var n in _active) Recenter(n);
    }

    private static void Recenter(DOSIPopNotification n)
    {
        if (_host is not Canvas canvas) return;     // Grid/Panel hosts: alignment works natively
        var hostWidth = canvas.Bounds.Width;
        if (hostWidth <= 0) return;

        var w = n.Bounds.Width;
        if (w <= 0)
        {
            n.Measure(Size.Infinity);
            w = n.DesiredSize.Width;
        }
        if (w <= 0) return;

        Canvas.SetLeft(n, Math.Max(0, (hostWidth - w) / 2));
        Canvas.SetTop(n, 0); // vertical position is driven by TranslateTransform
    }

    private static void ReflowStack(bool animate)
    {
        double y = TopOffset;
        foreach (var n in _active)
        {
            n.SetTargetY(y, animate);
            y += n.MeasuredHeight() + NotifSpacing;
        }
    }

    #endregion

    #region Instance

    private readonly TranslateTransform _translate;
    private double _currentY;
    private bool _isClosing;

    private DOSIPopNotification(string text)
    {
        var accent = Accents.AccentPrimary;

        // Pill background - dark, slightly translucent, accent-tinted border.
        Background = new SolidColorBrush(Color.FromArgb(235, 18, 24, 32));
        BorderBrush = new SolidColorBrush(Color.FromArgb(170, accent.R, accent.G, accent.B));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(20);
        Padding = new Thickness(20, 8);
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Top;
        BoxShadow = new BoxShadows(
            new BoxShadow
            {
                OffsetX = 0,
                OffsetY = 4,
                Blur = 16,
                Spread = 0,
                Color = Color.FromArgb(110, 0, 0, 0)
            },
            [
                new BoxShadow
                {
                    OffsetX = 0,
                    OffsetY = 0,
                    Blur = 18,
                    Spread = -2,
                    Color = Color.FromArgb(90, accent.R, accent.G, accent.B)
                }
            ]);
        IsHitTestVisible = false;

        Child = new TextBlock
        {
            Text = text,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            // Pinned to white because the pill background is fixed dark - using
            // the accent's TextPrimaryBrush would make the label disappear under
            // the Light accent (dark text on dark pill).
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        // Start hidden + slightly above target for slide-in.
        Opacity = 0;
        _translate = new TranslateTransform(0, TopOffset - SlideInDistance);
        _currentY = TopOffset - SlideInDistance;
        RenderTransform = _translate;

        SetValue(Canvas.ZIndexProperty, 10000);
    }

    private double MeasuredHeight()
    {
        if (Bounds.Height > 0) return Bounds.Height;
        Measure(Size.Infinity);
        return DesiredSize.Height > 0 ? DesiredSize.Height : 36;
    }

    private void SetTargetY(double y, bool animate)
    {
        if (!animate)
        {
            _translate.Y = y;
            _currentY = y;
            return;
        }

        _ = AnimateYAsync(_currentY, y, ReflowDuration);
        _currentY = y;
    }

    private async Task AnimateYAsync(double from, double to, TimeSpan duration)
    {
        var easing = new CubicEaseOut();
        var startTime = DateTime.UtcNow;
        while (true)
        {
            var elapsed = DateTime.UtcNow - startTime;
            var t = Math.Min(1.0, elapsed.TotalMilliseconds / duration.TotalMilliseconds);
            var eased = easing.Ease(t);
            _translate.Y = from + (to - from) * eased;
            if (t >= 1.0) break;
            await Task.Delay(8);
        }
        _translate.Y = to;
    }

    private async Task RunLifecycleAsync(TimeSpan lifetime)
    {
        // Fade in (opacity only — Y is driven by the reflow animation).
        await new Animation
        {
            Duration = FadeInDuration,
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0.0) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1.0) } }
            }
        }.RunAsync(this);
        Opacity = 1;

        await Task.Delay(lifetime);

        await DismissAsync();
    }

    /// <summary>
    /// Fades the notification out and removes it from the host, reflowing
    /// remaining notifications to fill the gap.
    /// </summary>
    public async Task DismissAsync()
    {
        if (_isClosing) return;
        _isClosing = true;

        await new Animation
        {
            Duration = FadeOutDuration,
            Easing = new CubicEaseIn(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 1.0) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 0.0) } }
            }
        }.RunAsync(this);

        Dispatcher.UIThread.Post(() =>
        {
            _active.Remove(this);
            if (_host is { } host && host.Children.Contains(this))
                host.Children.Remove(this);
            ReflowStack(animate: true);
        });
    }

    #endregion
}
