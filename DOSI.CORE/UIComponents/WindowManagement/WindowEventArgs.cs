namespace DOSI.CORE.UIComponents.WindowManagement;

/// <summary>
/// Event arguments for window-related events.
/// </summary>
public class DOSIWindowEventArgs : EventArgs
{
    public DOSIWindow Window { get; }

    public DOSIWindowEventArgs(DOSIWindow window)
    {
        Window = window;
    }
}

/// <summary>
/// Event arguments for window closing events with cancel support.
/// </summary>
public class DOSIWindowClosingEventArgs : DOSIWindowEventArgs
{
    public bool Cancel { get; set; }

    public DOSIWindowClosingEventArgs(DOSIWindow window) : base(window)
    {
    }
}

/// <summary>
/// Event arguments for window state change events.
/// </summary>
public class DOSIWindowStateChangedEventArgs : DOSIWindowEventArgs
{
    public DOSIWindowState OldState { get; }
    public DOSIWindowState NewState { get; }

    public DOSIWindowStateChangedEventArgs(DOSIWindow window, DOSIWindowState oldState, DOSIWindowState newState)
        : base(window)
    {
        OldState = oldState;
        NewState = newState;
    }
}

/// <summary>
/// Event arguments for window focus events.
/// </summary>
public class DOSIWindowFocusEventArgs : DOSIWindowEventArgs
{
    public bool IsFocused { get; }

    public DOSIWindowFocusEventArgs(DOSIWindow window, bool isFocused) : base(window)
    {
        IsFocused = isFocused;
    }
}
