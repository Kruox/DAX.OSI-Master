using System;
using System.Diagnostics;
using Avalonia.Threading;

namespace DOSI.CORE.Animations;

/// <summary>
/// A small, cancellable, allocation-light animation primitive that wraps a
/// <see cref="DispatcherTimer"/> + monotonic clock + easing function +
/// per-frame apply callback. Replaces the dozen-plus near-identical
/// hand-rolled timer blocks that previously lived inside each screen and
/// control.
/// <para>
/// Typical usage:
/// </para>
/// <code>
/// _fadeTween?.Stop(snapToEnd: true);   // optional finalize on re-trigger
/// _fadeTween = Tween.Run(280, Easings.EaseInOutCubic,
///     apply: t =>
///     {
///         from.Opacity = 1 - t;
///         to.Opacity   = t;
///     },
///     onCompleted: () =&gt; to.IsHitTestVisible = true);
/// </code>
/// <para>
/// Two important guarantees vs. the older inline pattern:
/// </para>
/// <list type="bullet">
///   <item><description>Uses <see cref="Stopwatch"/> instead of <c>DateTime.UtcNow</c>
///   for monotonic timing - immune to wall-clock jumps and has higher
///   resolution than the ~15&#160;ms <c>UtcNow</c> tick.</description></item>
///   <item><description><see cref="Stop"/> with <c>snapToEnd: true</c>
///   performs the final <c>apply(1.0)</c> + <c>onCompleted</c> in one call.
///   This is what every <c>Finalize*</c> helper in the screens used to do
///   by hand and what the recent LoginScreen reparent bugs all needed.</description></item>
/// </list>
/// </summary>
public sealed class Tween
{
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock;
    private readonly double _durationMs;
    private readonly Func<double, double> _ease;
    private readonly Action<double> _apply;
    private readonly Action? _onCompleted;
    private bool _finished;

    private Tween(double durationMs, Func<double, double> ease,
                  Action<double> apply, Action? onCompleted)
    {
        _durationMs = Math.Max(1, durationMs);
        _ease = ease;
        _apply = apply;
        _onCompleted = onCompleted;
        _clock = Stopwatch.StartNew();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTick;
    }

    /// <summary>
    /// True until the tween either runs to completion or is stopped.
    /// </summary>
    public bool IsRunning => !_finished;

    /// <summary>
    /// Starts a new tween that interpolates a normalized progress value
    /// from 0 to 1 over <paramref name="durationMs"/>, easing it through
    /// <paramref name="ease"/> and calling <paramref name="apply"/> on
    /// every frame with the eased value. <paramref name="onCompleted"/>
    /// fires once when the tween reaches <c>t == 1</c> (or when
    /// <see cref="Stop"/> is called with <c>snapToEnd: true</c>).
    /// </summary>
    public static Tween Run(double durationMs, Func<double, double> ease,
                            Action<double> apply, Action? onCompleted = null)
    {
        var t = new Tween(durationMs, ease, apply, onCompleted);
        t._timer.Start();
        return t;
    }

    /// <summary>
    /// Cancels the tween. If <paramref name="snapToEnd"/> is true the
    /// apply callback is invoked one last time with the final eased value
    /// (<c>ease(1)</c>) and <c>onCompleted</c> is fired. This is the
    /// "finalize on detach / re-trigger" pattern that every screen used to
    /// reimplement by hand.
    /// </summary>
    public void Stop(bool snapToEnd = false)
    {
        if (_finished) return;
        _finished = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _clock.Stop();

        if (snapToEnd)
        {
            try { _apply(_ease(1d)); }
            catch { /* swallow - finalize is best-effort */ }
            _onCompleted?.Invoke();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_finished) return;

        var elapsed = _clock.Elapsed.TotalMilliseconds;
        var t = elapsed / _durationMs;
        if (t >= 1d)
        {
            _finished = true;
            _timer.Stop();
            _timer.Tick -= OnTick;
            _clock.Stop();
            _apply(_ease(1d));
            _onCompleted?.Invoke();
            return;
        }

        _apply(_ease(t));
    }
}
