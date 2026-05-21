using Avalonia.Controls;
using Avalonia.Platform;
using DOSI.CORE.UIComponents.WindowManagement;

namespace DOSI.CORE.UIComponents;

/// <summary>
/// Contract implemented by every native Avalonia <c>Window</c> that hosts a
/// DOSI desktop surface. Decouples <see cref="UIComponents.DOSIScreen"/>s
/// (and especially <c>DesktopScreen</c>'s chrome reparenting) from the
/// process-singleton MainWindow, so multiple physical monitors can each run
/// their own independent desktop while still sharing the same screen
/// classes, wallpaper manager, and accent system.
///
/// Layering convention (matches the original MainWindow stack from bottom to top):
///   1. screen container - the active <see cref="DOSIScreen"/>
///   2. <see cref="WindowOverlayHost"/>   - all DOSIWindow instances
///   3. <see cref="PopupHost"/>           - taskbar / apps menu / notifications
///   4. lock overlay                       - lock screen (when active)
///
/// Each monitor's host is also the owner of its own <see cref="WindowManager"/>;
/// <see cref="WindowManager.Instance"/> follows whichever host most recently
/// became <c>Activated</c> so app-launch APIs naturally land windows on the
/// monitor the user just clicked.
/// </summary>
public interface IDosiHost
{
    /// <summary>
    /// Top-of-stack panel where transient chrome (taskbar, apps menu,
    /// toast notifications) is reparented. <c>DesktopScreen</c> hands its
    /// <c>_layoutRoot</c> to this panel on attach so the chrome floats
    /// above any open application windows.
    /// </summary>
    Panel PopupHost { get; }

    /// <summary>
    /// Canvas that owns every <see cref="DOSIWindow"/> on this monitor.
    /// Backing surface for <see cref="WindowManager"/>.
    /// </summary>
    Canvas WindowOverlayHost { get; }

    /// <summary>
    /// The <see cref="WindowManager"/> bound to this host's
    /// <see cref="WindowOverlayHost"/>. When this host's native window
    /// becomes <c>Activated</c>, this manager is promoted to
    /// <see cref="WindowManager.Instance"/> so global launch sites
    /// (apps menu, terminal, hotkeys) target this monitor.
    /// </summary>
    WindowManager WindowManager { get; }

    /// <summary>
    /// The physical monitor this host is rendered onto, or <c>null</c> if
    /// the screen handle isn't known yet (e.g. before the window has been
    /// shown). Used by the cross-monitor DOSIWindow drag handoff to test
    /// whether a release-point cursor is over a different display.
    /// </summary>
    Screen? TargetScreen { get; }
}

/// <summary>
/// Process-wide registry of every live <see cref="IDosiHost"/>. Walked by
/// the cross-monitor drag handoff in <c>DOSIWindow</c> to find the target
/// monitor for a window that's been dragged across a screen boundary.
/// Hosts must self-register on construction and unregister on close.
/// </summary>
public static class DosiHostRegistry
{
    private static readonly List<IDosiHost> _hosts = new();
    private static readonly object _gate = new();

    /// <summary>Snapshot of every currently-registered host.</summary>
    public static IReadOnlyList<IDosiHost> All
    {
        get { lock (_gate) return _hosts.ToArray(); }
    }

    public static void Register(IDosiHost host)
    {
        if (host == null) return;
        lock (_gate)
        {
            if (!_hosts.Contains(host)) _hosts.Add(host);
        }
    }

    public static void Unregister(IDosiHost host)
    {
        if (host == null) return;
        lock (_gate) _hosts.Remove(host);
    }
}
