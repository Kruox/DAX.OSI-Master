namespace DOSI.CORE.UIComponents.WindowManagement;

/// <summary>
/// Represents the state of a DOSI window.
/// </summary>
public enum DOSIWindowState
{
    Normal,
    Minimized,
    Maximized
}

/// <summary>
/// Represents the resize direction for window resizing operations.
/// </summary>
[Flags]
public enum ResizeDirection
{
    None = 0,
    Left = 1,
    Right = 2,
    Top = 4,
    Bottom = 8,
    TopLeft = Top | Left,
    TopRight = Top | Right,
    BottomLeft = Bottom | Left,
    BottomRight = Bottom | Right
}
