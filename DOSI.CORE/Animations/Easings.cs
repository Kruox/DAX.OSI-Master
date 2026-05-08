using System;

namespace DOSI.CORE.Animations;

/// <summary>
/// Shared, allocation-free easing functions used across DOSI's hand-rolled
/// animations. Centralizing these keeps every screen, window, and control
/// using the same motion curves so the system feels visually consistent.
/// All inputs are normalized progress values in <c>[0, 1]</c>; outputs land
/// in the same range (give or take floating-point noise) so callers can
/// plug them straight into a lerp.
/// </summary>
public static class Easings
{
    /// <summary>
    /// Linear pass-through. Mostly useful as a default when a caller wants
    /// to opt out of easing without branching.
    /// </summary>
    public static double Linear(double t) => t;

    /// <summary>
    /// Ease-out cubic - the default "settle" curve used by most DOSI
    /// transitions. Starts fast, decelerates to a soft stop, which reads as
    /// natural for slide-ins, fades, and panel reveals.
    /// </summary>
    public static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);

    /// <summary>
    /// Ease-in cubic - the mirror of <see cref="EaseOutCubic"/>. Starts
    /// slow and accelerates, useful for "exit" or "dismiss" animations
    /// where the element should pick up speed as it leaves.
    /// </summary>
    public static double EaseInCubic(double t) => t * t * t;

    /// <summary>
    /// Ease-in-out cubic - symmetric S-curve. Used for transitions that
    /// move between two resting states (window snap, accent ripple) where
    /// both ends should feel anchored.
    /// </summary>
    public static double EaseInOutCubic(double t) =>
        t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    /// <summary>
    /// Ease-out back - decelerates toward 1 with a small overshoot before
    /// settling. Used for tactile "pop" effects (avatar reveal, success
    /// indicators) where the slight rebound communicates weight. The 1.70158
    /// constant is the conventional Penner back-easing magnitude (~10%
    /// overshoot).
    /// </summary>
    public static double EaseOutBack(double t)
    {
        const double c1 = 1.70158;
        const double c3 = c1 + 1;
        var u = t - 1;
        return 1 + c3 * u * u * u + c1 * u * u;
    }

    /// <summary>
    /// Linear interpolation between <paramref name="from"/> and
    /// <paramref name="to"/> by normalized <paramref name="t"/>. Provided
    /// here so call sites can stay on a single using directive instead of
    /// scattering inline math.
    /// </summary>
    public static double Lerp(double from, double to, double t) => from + (to - from) * t;
}
