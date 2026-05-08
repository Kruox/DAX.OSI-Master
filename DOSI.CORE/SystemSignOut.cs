using System;
using System.Threading.Tasks;

namespace DOSI.CORE;

/// <summary>
/// Coordinates a clean sign-out of the currently logged-in DOSI user. Unlike
/// <see cref="SystemShutdown"/>, this does not tear the process down - it
/// returns the host UI to the login screen so a different account can sign in.
///
/// The host (typically the application MainWindow) sets
/// <see cref="SignOutHandler"/> to a delegate that runs the visual sign-out
/// sequence (closing windows, showing the SignoutScreen, navigating back to
/// the login screen, etc.). Callers (terminal, apps menu, hotkey, ...) just
/// invoke <see cref="Begin"/>.
/// </summary>
public static class SystemSignOut
{
    /// <summary>True while a sign-out sequence is currently running.</summary>
    public static bool IsSigningOut { get; private set; }

    /// <summary>
    /// Fires the moment sign-out is initiated, BEFORE the
    /// <see cref="SignOutHandler"/> animation runs. Use this to dispose
    /// resources that don't honour Avalonia's z-order (e.g. native WebView2
    /// HWNDs that would otherwise float on top of the sign-out overlay until
    /// the handler completes).
    /// </summary>
    public static event Action? SignOutStarting;

    /// <summary>
    /// Optional asynchronous handler the host installs to drive the sign-out
    /// transition (close windows, animate the SignoutScreen, return to login).
    /// Exceptions are swallowed so a UI glitch never strands the user.
    /// </summary>
    public static Func<Task>? SignOutHandler { get; set; }

    /// <summary>
    /// Begins the sign-out sequence. No-op if a sign-out is already in flight
    /// or no <see cref="SignOutHandler"/> is registered.
    /// </summary>
    public static async void Begin()
    {
        if (IsSigningOut) return;
        if (SignOutHandler == null) return;

        IsSigningOut = true;
        try
        {
            try { SignOutStarting?.Invoke(); } catch { }
            await SignOutHandler.Invoke();
        }
        catch
        {
            // Never let the sign-out animation crash the host.
        }
        finally
        {
            IsSigningOut = false;
        }
    }
}
