using System;

namespace DOSI.CORE.UIComponents.WindowManagement;

/// <summary>
/// Where the taskbar docks. Vertical docking (left / right) is
/// intentionally out of scope here - the chrome strip is built as a
/// horizontal Grid and would need a full rebuild to flip orientation.
/// </summary>
public enum TaskbarPosition
{
    Top,
    Bottom
}

/// <summary>
/// Where the ambient clock + date overlay anchors on the desktop +
/// login screen. Independent of taskbar position - a user can have
/// the taskbar at the top with the clock in the bottom-right, or any
/// other combination. The clock auto-shifts to clear the taskbar via
/// the margin formula in DesktopScreen.ComputeClockMargin.
/// </summary>
public enum ClockPosition
{
    BottomLeft,
    BottomRight,
    TopLeft,
    TopRight
}

/// <summary>
/// Process-wide source of truth for the active user's taskbar height.
/// Read by every chrome / layout primitive that needs to leave clearance
/// for the taskbar (DesktopScreen, ExtensionScreen, DesktopIconLayer,
/// notification popovers, apps menu, etc.). Written by the sign-in
/// pipeline + the Settings "Taskbar" section.
///
/// Why a static? Half the consumers are layout primitives constructed
/// before the active <see cref="UserManagement.DOSIUser"/> is even
/// resolvable - they can't reach a per-user instance. A static with a
/// default that matches the historical hard-coded value (28 px) keeps
/// every existing call site working without any null-checks while the
/// "current user" is still being resolved at boot.
/// </summary>
public static class TaskbarMetrics
{
    /// <summary>Historical default; matches the original const.</summary>
    public const double DefaultHeight = 28.0;

    private static double _height = DefaultHeight;
    private static TaskbarPosition _position = TaskbarPosition.Top;
    private static ClockPosition _clockPosition = ClockPosition.BottomLeft;

    /// <summary>
    /// Live taskbar height in CSS pixels. Subscribers should listen to
    /// <see cref="HeightChanged"/> to react when the user adjusts it
    /// in Settings - re-reading this property at any later time is
    /// always safe and returns the current value.
    /// </summary>
    public static double Height
    {
        get => _height;
        set
        {
            // Defensive: a clamped value lives inside the user-prefs
            // accessors, but any direct caller that bypasses them
            // shouldn't be able to break the layout with a 0 / negative
            // value. Match the DOSIUser-side bounds.
            var v = Math.Clamp(value, 18.0, 80.0);
            if (Math.Abs(v - _height) < 0.01) return;
            _height = v;
            HeightChanged?.Invoke(null, v);
        }
    }

    /// <summary>
    /// Live taskbar dock edge. Subscribers listen to
    /// <see cref="PositionChanged"/> to relayout in place without
    /// requiring a sign-out cycle.
    /// </summary>
    public static TaskbarPosition Position
    {
        get => _position;
        set
        {
            if (value == _position) return;
            _position = value;
            PositionChanged?.Invoke(null, value);
        }
    }

    /// <summary>
    /// Pixels reserved at the top of the desktop work area for chrome.
    /// Convenience: returns Height when the bar is docked at the top,
    /// otherwise 0. Lets layout code stay declarative
    /// ("avoid the top reserve") instead of branching on Position
    /// everywhere.
    /// </summary>
    public static double TopReserve => Position == TaskbarPosition.Top ? Height : 0;

    /// <summary>
    /// Pixels reserved at the bottom of the desktop work area for
    /// chrome. Counterpart to <see cref="TopReserve"/>.
    /// </summary>
    public static double BottomReserve => Position == TaskbarPosition.Bottom ? Height : 0;

    /// <summary>
    /// Raised whenever <see cref="Height"/> changes. Layout primitives
    /// that have already laid themselves out at the previous height
    /// subscribe so they can resize / re-margin in place without
    /// requiring a sign-out cycle.
    /// </summary>
    public static event EventHandler<double>? HeightChanged;

    /// <summary>
    /// Raised whenever <see cref="Position"/> changes. Layout primitives
    /// that have laid themselves out for the previous edge subscribe so
    /// they can flip alignment + animations + reserved-space side
    /// without a sign-out.
    /// </summary>
    public static event EventHandler<TaskbarPosition>? PositionChanged;

    /// <summary>
    /// Live ambient-clock corner. Subscribers (DesktopScreen,
    /// LoginScreen) listen to <see cref="ClockPositionChanged"/> to
    /// re-anchor the clock + date stack without rebuilding it.
    /// </summary>
    public static ClockPosition ClockPosition
    {
        get => _clockPosition;
        set
        {
            if (value == _clockPosition) return;
            _clockPosition = value;
            ClockPositionChanged?.Invoke(null, value);
        }
    }

    /// <summary>
    /// Raised whenever <see cref="ClockPosition"/> changes. Subscribers
    /// flip the ambient clock's alignment + margin in place.
    /// </summary>
    public static event EventHandler<ClockPosition>? ClockPositionChanged;
}

