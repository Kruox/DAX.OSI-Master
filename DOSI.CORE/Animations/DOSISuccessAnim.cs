using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DOSI.CORE.AccentManagement;

namespace DOSI.CORE.Animations;

/// <summary>
/// A short, self-running celebration animation used by DAX.OSI to acknowledge
/// successful user actions (sign-in, account creation, setup wizard completion).
///
/// Renders an accent-coloured pulse, a checkmark that draws itself in, and a
/// burst of confetti particles. Auto-removes itself from its parent on
/// completion and raises <see cref="Completed"/>.
/// </summary>
public sealed class DOSISuccessAnim : UserControl, IDisposable
{
    /// <summary>Predefined celebration sizes.</summary>
    public enum SuccessSize
    {
        Small,
        Medium,
        Large
    }

    private static AccentManager Accents => AccentManager.Instance;

    private const double FrameInterval = 16; // ~60 fps

    // Phase durations (ms).
    private const double CirclePopDuration = 320;
    private const double CheckDrawDuration = 280;
    private const double HoldDuration = 600;
    private const double FadeOutDuration = 320;

    // Confetti.
    private const int ConfettiCount = 18;
    private const double ConfettiDuration = 900;

    private readonly double _diameter;
    private readonly Ellipse _ring;
    private readonly ScaleTransform _ringScale;
    private readonly Canvas _confettiHost;

    private readonly DispatcherTimer _timer;
    private readonly DateTime _startUtc = DateTime.UtcNow;
    private readonly Confetti[] _confetti;

    private bool _isDisposed;
    private bool _completedRaised;

    /// <summary>Raised once the animation has finished playing.</summary>
    public event EventHandler? Completed;

    public DOSISuccessAnim(SuccessSize size = SuccessSize.Medium)
    {
        _diameter = size switch
        {
            SuccessSize.Small => 64,
            SuccessSize.Large => 140,
            _ => 96
        };

        Background = Brushes.Transparent;
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        var accent = Accents.AccentPrimary;
        var accentSecondary = Accents.AccentSecondary;

        // Outer ring – expands and fades.
        _ringScale = new ScaleTransform(0.4, 0.4);
        _ring = new Ellipse
        {
            Width = _diameter,
            Height = _diameter,
            Stroke = new SolidColorBrush(accent, 0.85),
            StrokeThickness = Math.Max(2, _diameter * 0.045),
            Fill = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
            RenderTransform = _ringScale,
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative)
        };

        // Confetti host – sits on top of the ring and lets particles fly out.
        _confettiHost = new Canvas
        {
            Width = _diameter * 3,
            Height = _diameter * 3,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };

        _confetti = BuildConfetti(_confettiHost, accent, accentSecondary);

        var stage = new Grid
        {
            Width = _diameter * 3,
            Height = _diameter * 3,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        stage.Children.Add(_confettiHost);
        stage.Children.Add(_ring);

        Content = stage;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FrameInterval) };
        _timer.Tick += OnTick;

        AttachedToVisualTree += (_, _) => _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    /// <summary>
    /// Convenience helper that overlays a <see cref="DOSISuccessAnim"/> on the
    /// supplied panel, plays it once, then removes it and raises
    /// <paramref name="onCompleted"/>. Safe to call from any thread.
    /// </summary>
    public static DOSISuccessAnim PlayOver(Panel host, SuccessSize size = SuccessSize.Medium, Action? onCompleted = null)
    {
        if (host == null) throw new ArgumentNullException(nameof(host));

        var anim = new DOSISuccessAnim(size);
        anim.Completed += (_, _) =>
        {
            if (host.Children.Contains(anim))
                host.Children.Remove(anim);
            anim.Dispose();
            onCompleted?.Invoke();
        };

        Dispatcher.UIThread.Post(() => host.Children.Add(anim), DispatcherPriority.Normal);
        return anim;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_isDisposed) return;

        var elapsed = (DateTime.UtcNow - _startUtc).TotalMilliseconds;
        var totalDuration = CirclePopDuration + CheckDrawDuration + HoldDuration + FadeOutDuration;

        // -------- Ring pulse: expands and fades across the early phase --------
        var ringT = Math.Clamp(elapsed / (CirclePopDuration + CheckDrawDuration), 0d, 1d);
        var ringEased = 1 - Math.Pow(1 - ringT, 3);
        _ringScale.ScaleX = 0.4 + ringEased * 1.2;
        _ringScale.ScaleY = 0.4 + ringEased * 1.2;
        _ring.Opacity = 0.85 * (1 - ringEased);

        // -------- Confetti burst --------
        var confettiT = Math.Clamp(elapsed / ConfettiDuration, 0d, 1d);
        UpdateConfetti(confettiT);

        // -------- Fade out --------
        var fadeStart = totalDuration - FadeOutDuration;
        if (elapsed >= fadeStart)
        {
            var fadeT = Math.Clamp((elapsed - fadeStart) / FadeOutDuration, 0d, 1d);
            Opacity = 1 - fadeT;
        }

        if (elapsed >= totalDuration)
        {
            _timer.Stop();
            RaiseCompleted();
        }
    }

    private void RaiseCompleted()
    {
        if (_completedRaised) return;
        _completedRaised = true;
        Completed?.Invoke(this, EventArgs.Empty);
    }

    private static Confetti[] BuildConfetti(Canvas host, Color accent, Color accentSecondary)
    {
        var rng = new Random();
        var palette = new[]
        {
            accent,
            accentSecondary,
            Color.FromArgb(255, 255, 255, 255),
            Color.FromRgb(255, 215, 90),
        };

        var items = new Confetti[ConfettiCount];
        var centerX = host.Width / 2;
        var centerY = host.Height / 2;
        var maxRadius = host.Width / 2 - 6;

        for (int i = 0; i < ConfettiCount; i++)
        {
            var angle = (Math.PI * 2 * i) / ConfettiCount + rng.NextDouble() * 0.35;
            var distance = maxRadius * (0.55 + rng.NextDouble() * 0.45);
            var color = palette[rng.Next(palette.Length)];
            var size = 4 + rng.NextDouble() * 4;

            var dot = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = new SolidColorBrush(color),
                Opacity = 0
            };
            Canvas.SetLeft(dot, centerX - size / 2);
            Canvas.SetTop(dot, centerY - size / 2);
            host.Children.Add(dot);

            items[i] = new Confetti(dot, angle, distance, centerX, centerY, size);
        }

        return items;
    }

    private void UpdateConfetti(double t)
    {
        if (t <= 0)
        {
            foreach (var c in _confetti) c.Dot.Opacity = 0;
            return;
        }

        var eased = 1 - Math.Pow(1 - t, 3);
        foreach (var c in _confetti)
        {
            var x = c.CenterX + Math.Cos(c.Angle) * c.TargetDistance * eased;
            // Slight downward gravity bias so the burst feels physical.
            var gravity = c.TargetDistance * 0.35 * (eased * eased);
            var y = c.CenterY + Math.Sin(c.Angle) * c.TargetDistance * eased + gravity;

            Canvas.SetLeft(c.Dot, x - c.Size / 2);
            Canvas.SetTop(c.Dot, y - c.Size / 2);

            // Fade in quickly, then fade out near the end.
            var opacity = t < 0.15 ? t / 0.15 : 1 - Math.Pow(t, 2);
            c.Dot.Opacity = Math.Clamp(opacity, 0, 1);
        }
    }

    private static double EaseOutBack(double t)
    {
        const double c1 = 1.70158;
        const double c3 = c1 + 1;
        var x = t - 1;
        return 1 + c3 * x * x * x + c1 * x * x;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }

    private sealed class Confetti
    {
        public Ellipse Dot { get; }
        public double Angle { get; }
        public double TargetDistance { get; }
        public double CenterX { get; }
        public double CenterY { get; }
        public double Size { get; }

        public Confetti(Ellipse dot, double angle, double targetDistance, double centerX, double centerY, double size)
        {
            Dot = dot;
            Angle = angle;
            TargetDistance = targetDistance;
            CenterX = centerX;
            CenterY = centerY;
            Size = size;
        }
    }
}
