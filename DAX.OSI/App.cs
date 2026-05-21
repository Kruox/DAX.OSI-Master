using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using DOSI.CORE;
using DOSI.CORE.AccentManagement;
using DOSI.CORE.Apps;
using DOSI.CORE.UserManagement;
using System.Runtime.InteropServices;

namespace DAX.OSI;

public class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Default;

        // Apply the universal UI-text drop shadow to every TextBlock in
        // the app. TextBlock is the underlying glyph-rendering primitive
        // for nearly every Avalonia text-displaying control (Buttons,
        // ContentPresenters, MenuItems, ListBoxItems, etc.), so a single
        // selector here gives the entire UI a consistent soft shadow.
        //
        // Intentionally NOT applied to monospaced / terminal text: the
        // exclusion happens naturally because DOSITerminalIO (and any
        // future DOSIFonts.Mono consumer) draws glyphs via
        // DrawingContext.DrawText directly instead of instantiating a
        // TextBlock, so this style never matches it. See DOSIFonts'
        // class doc-comment for the boundary contract.
        Styles.Add(new Style(s => s.OfType<TextBlock>())
        {
            Setters =
            {
                new Setter(TextBlock.EffectProperty, DOSIFonts.CreateUiTextDropShadow())
            }
        });

        // Initialize core system services and load settings
        SystemCore.Initialize();

        // Apply the accent from settings
        AccentManager.Instance.InitializeFromSettings();

        // Per-user application loading. Each account has its own
        // <UserHome>/Applications/ folder; on sign-in we scan it and
        // register every IDOSIApp those DLLs publish, on sign-out we
        // drop them so the next user starts from a clean registry.
        UserManager.CurrentUserChanged += (_, user) =>
        {
            if (user != null)
            {
                UserManager.EnsureUserSubfolders(user);
                // First-sign-in seed: drop the bundled default plug-ins
                // (currently the IDE) into the user's Applications folder.
                // Idempotent + stamp-gated so subsequent sign-ins are a
                // single file-exists check, and so a user who deletes a
                // seeded app stays in control of their Applications folder.
                // Must run BEFORE LoadForUser so the seeded DLL is on disk
                // when the loader scans.
                DefaultAppSeeder.SeedIfNeeded(user);
                AppLoader.LoadForUser(user);
            }
            else
            {
                AppLoader.UnloadAll();
            }
        };
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow();

            // Apply fullscreen setting
            if (SystemCore.Settings.Fullscreen)
            {
                mainWindow.WindowState = WindowState.FullScreen;

                // Hide the host OS taskbar for the lifetime of the process so
                // DAX.OSI's own taskbar isn't visually competing with (and
                // sitting under) the Windows shell tray. Avalonia's
                // FullScreen WindowState pins us above normal windows but
                // doesn't suppress Shell_TrayWnd on Win11 - the tray still
                // peeks through if it's set to "always on top" or if the
                // user hovers the bottom edge. ShowWindow(SW_HIDE) on the
                // tray HWND is the same trick kiosk apps use; we restore it
                // on shutdown via desktop.Exit so the user's machine isn't
                // left without a taskbar if DAX.OSI dies unexpectedly.
                ShellTrayHider.Hide();
                desktop.Exit += (_, _) => ShellTrayHider.Restore();
            }

            desktop.MainWindow = mainWindow;
        }

        // ===== Belt-and-braces: filter the known-benign Avalonia exception =====
        // "Attempt to call InvalidateArrange on wrong LayoutManager." can fire
        // during cross-monitor DOSIWindow drag-handoff (we reparent the control
        // from one MonitorWindow's Canvas to another's, swapping its
        // LayoutManager). DOSIWindow.TryHandoffToMonitorAtCursor takes
        // extensive precautions (deferred Dispatcher.Post + explicit layout
        // pass flushes on both source and target) but Avalonia's Win32
        // backend can still queue a follow-up invalidation against the OLD
        // LayoutManager from inside its pointer-event teardown. The reparent
        // itself completes correctly - the throw is purely cosmetic noise on
        // a follow-up dispatcher iteration.
        //
        // We also catch sibling layout/visual-root exceptions in the same
        // family ("Visual is attached to a different visual tree", "Specified
        // element is already the logical child", "control's LayoutManager is
        // not the same") - all of which are documented Avalonia issues on
        // reparent and produce no functional damage. Anything that doesn't
        // match is re-raised so real bugs still surface, and every match is
        // logged to debug output with the originating window's title for
        // post-mortem visibility.
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, ex) =>
        {
            var msg = ex.Exception?.Message ?? string.Empty;
            bool isKnownLayoutNoise =
                msg.Contains("InvalidateArrange on wrong LayoutManager", System.StringComparison.Ordinal) ||
                msg.Contains("Visual is attached to a different", System.StringComparison.Ordinal) ||
                msg.Contains("Specified element is already the logical child", System.StringComparison.Ordinal) ||
                msg.Contains("LayoutManager is not the same", System.StringComparison.Ordinal) ||
                msg.Contains("InvalidateMeasure on wrong LayoutManager", System.StringComparison.Ordinal);

            if (isKnownLayoutNoise)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[LayoutNoise:Swallowed] {ex.Exception?.GetType().Name}: {msg}");
                ex.Handled = true;
                return;
            }

            // Not a known-benign case. Log the full exception (type, message,
            // stack) to debug output so the next reproducer crash can be
            // diagnosed without re-running with the debugger attached. We
            // DON'T set Handled here - we want the process to crash visibly
            // so the user knows something went wrong (and so the framework
            // dumps its own diagnostics).
            System.Diagnostics.Debug.WriteLine(
                $"[Unhandled] {ex.Exception?.GetType().FullName}: {msg}\n{ex.Exception?.StackTrace}");
        };

        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>
/// Hides / restores the Windows shell taskbar (Shell_TrayWnd + the secondary
/// taskbars on multi-monitor setups, and the Start orb in Win10/11) for the
/// lifetime of the DAX.OSI process when launched fullscreen. No-op on
/// non-Windows platforms.
///
/// We do this with raw <c>ShowWindow</c> calls instead of changing
/// AppBar / work-area state so we don't pollute the user's persistent shell
/// configuration - if DAX.OSI crashes mid-session a quick restart of
/// explorer.exe brings the tray back, and on a clean exit we do it ourselves.
/// </summary>
internal static class ShellTrayHider
{
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern System.IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern System.IntPtr FindWindowEx(System.IntPtr parent, System.IntPtr childAfter,
        string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(System.IntPtr hWnd, int nCmdShow);

    private static bool _hidden;

    public static void Hide()
    {
        if (!OperatingSystem.IsWindows() || _hidden) return;
        try
        {
            // Primary taskbar.
            var tray = FindWindow("Shell_TrayWnd", null);
            if (tray != System.IntPtr.Zero) ShowWindow(tray, SW_HIDE);

            // Secondary taskbars on extra monitors. There can be more than
            // one - keep walking until FindWindowEx runs out of matches.
            System.IntPtr secondary = System.IntPtr.Zero;
            while ((secondary = FindWindowEx(System.IntPtr.Zero, secondary,
                       "Shell_SecondaryTrayWnd", null)) != System.IntPtr.Zero)
            {
                ShowWindow(secondary, SW_HIDE);
            }

            // Start button (separate HWND on Win10; Win11 hosts it inside
            // Shell_TrayWnd, in which case this is a no-op).
            var startOrb = FindWindow("Button", "Start");
            if (startOrb != System.IntPtr.Zero) ShowWindow(startOrb, SW_HIDE);

            _hidden = true;
        }
        catch { /* best-effort; never block startup over chrome */ }
    }

    public static void Restore()
    {
        if (!OperatingSystem.IsWindows() || !_hidden) return;
        try
        {
            var tray = FindWindow("Shell_TrayWnd", null);
            if (tray != System.IntPtr.Zero) ShowWindow(tray, SW_SHOW);

            System.IntPtr secondary = System.IntPtr.Zero;
            while ((secondary = FindWindowEx(System.IntPtr.Zero, secondary,
                       "Shell_SecondaryTrayWnd", null)) != System.IntPtr.Zero)
            {
                ShowWindow(secondary, SW_SHOW);
            }

            var startOrb = FindWindow("Button", "Start");
            if (startOrb != System.IntPtr.Zero) ShowWindow(startOrb, SW_SHOW);
        }
        catch { /* user can always fix with `taskkill /F /IM explorer.exe & start explorer.exe` */ }
        finally { _hidden = false; }
    }
}
