using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DAX.OSI.Controls;

/// <summary>
/// Cross-platform best-effort helper that transforms a recently-launched
/// game's top-level window into a chrome-less, fullscreen surface above
/// DAX.OSI. We launch the game through its native launcher (Steam, Epic, etc.)
/// and then post-process the resulting window so the user doesn't see the
/// host OS's title bar / borders / taskbar fighting with DAX.OSI's own UI.
///
/// PLATFORM CAVEATS
///   Windows: solid - SetWindowLong + SetWindowPos work reliably for
///     virtually every game engine. Some borderless-fullscreen games will
///     re-grab focus and resize themselves; we apply once, the game wins.
///   macOS: best-effort - sends the system "Enter Fullscreen"
///     shortcut (Ctrl+Cmd+F) to the launched app via osascript. Only works
///     for games that opt into AppKit fullscreen; many ports implement
///     their own fullscreen mode and ignore this. Requires Accessibility
///     permission to be granted once.
///   Linux (X11): best-effort - shells out to `wmctrl` to set the
///     _NET_WM_STATE_FULLSCREEN hint. Requires wmctrl to be installed
///     (common on most distros, not always installed by default).
///   Linux (Wayland): no-op - Wayland forbids cross-process window
///     manipulation by design. Anyone wanting this on Wayland needs to
///     run inside a nested compositor like gamescope.
///
/// We intentionally do NOT try to host the game inside our own window
/// (SetParent / NativeControlHost reparenting). That breaks input routing,
/// anti-cheat, the Steam overlay, and DPI scaling. Borderless top-level is
/// the same approach the popular "Borderless Gaming" tool uses, and it's the
/// one that doesn't get users VAC-banned.
/// </summary>
public static class BorderlessGameLauncher
{
    /// <summary>Outcome of an attempted borderless transform, surfaced to UI.</summary>
    public enum Result
    {
        /// <summary>Borderless was successfully applied to a detected game window.</summary>
        Applied,
        /// <summary>We waited but never saw a candidate window appear.</summary>
        TimedOut,
        /// <summary>The current platform / session can't manipulate other-process windows.</summary>
        Unsupported,
        /// <summary>Required external tool (e.g. wmctrl) is missing on this system.</summary>
        ToolMissing
    }

    /// <summary>
    /// Background-watch for a new top-level game window for up to ~60s and
    /// apply the borderless transform when one shows up. Safe to fire-and-
    /// forget from a UI-thread caller. Never throws - any error is folded
    /// into a <see cref="Result"/> the caller can render in a status bar.
    /// </summary>
    /// <param name="gameNameHint">
    /// Best-effort filter. We match a new window's process name (or main-
    /// window title) against this so a game called "Half-Life 2" doesn't
    /// get borderless'd onto, say, a Notepad window the user just opened.
    /// Pass null to apply to the first new fullscreen-sized window we see.
    /// </param>
    /// <param name="timeout">
    /// Max wait time before we give up. Defaults to 60s - some launchers
    /// (Steam first-run, Epic patches) take that long to actually start
    /// the game's process after the URI is invoked.
    /// </param>
    public static Task<Result> MakeBorderlessAsync(string? gameNameHint, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));

        if (OperatingSystem.IsWindows())
            return Task.Run(() => OperatingSystem.IsWindows()
                ? WindowsImpl.WatchAndApply(gameNameHint, deadline)
                : Result.Unsupported);

        if (OperatingSystem.IsMacOS())
            return Task.Run(() => OperatingSystem.IsMacOS()
                ? MacImpl.WatchAndApply(gameNameHint, deadline)
                : Result.Unsupported);

        if (OperatingSystem.IsLinux())
        {
            // Wayland clients can't touch each other's windows. Detect via
            // the standard freedesktop session-type env var; XWayland sessions
            // also report "wayland", which is correct - X11 control under
            // XWayland is unreliable for fullscreen toggles.
            var sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
            if (string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(Result.Unsupported);

            return Task.Run(() => OperatingSystem.IsLinux()
                ? LinuxX11Impl.WatchAndApply(gameNameHint, deadline)
                : Result.Unsupported);
        }

        return Task.FromResult(Result.Unsupported);
    }

    // =====================================================================
    // Windows implementation
    // =====================================================================

    [SupportedOSPlatform("windows")]
    private static class WindowsImpl
    {
        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;

        // Window styles we strip to remove chrome.
        private const uint WS_CAPTION     = 0x00C00000;
        private const uint WS_THICKFRAME  = 0x00040000;
        private const uint WS_MINIMIZE    = 0x20000000;
        private const uint WS_MAXIMIZE    = 0x01000000;
        private const uint WS_SYSMENU     = 0x00080000;
        private const uint WS_BORDER      = 0x00800000;
        private const uint WS_DLGFRAME    = 0x00400000;

        private const uint WS_EX_DLGMODALFRAME  = 0x00000001;
        private const uint WS_EX_CLIENTEDGE     = 0x00000200;
        private const uint WS_EX_STATICEDGE     = 0x00020000;
        private const uint WS_EX_WINDOWEDGE     = 0x00000100;

        // SetWindowPos flags.
        private const uint SWP_NOZORDER       = 0x0004;
        private const uint SWP_FRAMECHANGED   = 0x0020;
        private const uint SWP_SHOWWINDOW     = 0x0040;
        private static readonly IntPtr HWND_TOPMOST = new(-1);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SetWindowLong(IntPtr hWnd, int nIndex, uint dwNewLong);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
        // Foreground / focus management. We need these to actually surface
        // the game window above DAX.OSI's own fullscreen MainWindow:
        // SetForegroundWindow alone is rejected by Windows when the calling
        // process is not the current foreground owner, so we first
        // AllowSetForegroundWindow(ASFW_ANY) from our process and then
        // BringWindowToTop + SetForegroundWindow on the game hwnd.
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllowSetForegroundWindow(uint dwProcessId);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        private const int SW_MINIMIZE = 6;
        private const uint ASFW_ANY = 0xFFFFFFFF;
        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        public static Result WatchAndApply(string? gameNameHint, DateTime deadlineUtc)
        {
            // Snapshot existing top-level windows so we only consider NEW ones
            // (otherwise we'd target whatever Steam window happened to be open).
            var beforeIds = SnapshotTopLevelHwnds();

            while (DateTime.UtcNow < deadlineUtc)
            {
                Thread.Sleep(500);
                var current = SnapshotTopLevelHwnds();
                foreach (var hwnd in current)
                {
                    if (beforeIds.Contains(hwnd)) continue;
                    if (!IsWindowVisible(hwnd)) continue;

                    if (!IsCandidate(hwnd, gameNameHint)) continue;

                    if (TryApplyBorderless(hwnd))
                    {
                        // Borderless succeeded - now MAKE SURE the game ends
                        // up actually visible above DAX.OSI's own fullscreen
                        // MainWindow. Without these calls the game wins the
                        // z-order race against any other process but loses
                        // it against another foreground window in OUR process,
                        // which is exactly the situation we're in.
                        try
                        {
                            // Permit the game's process to take foreground
                            // from us (we're currently foreground because
                            // DAX.OSI's window is fullscreen + active).
                            _ = GetWindowThreadProcessId(hwnd, out var gamePid);
                            if (gamePid != 0) _ = AllowSetForegroundWindow(gamePid);

                            _ = BringWindowToTop(hwnd);
                            _ = SetForegroundWindow(hwnd);

                            // If foreground STILL didn't take (foreground-lock
                            // rules can deny us when DAX.OSI was the most-
                            // recently-active window), nudge our own host
                            // out of the way by minimizing it. The user can
                            // alt+tab back when they're done with the game.
                            if (GetForegroundWindow() != hwnd)
                                MinimizeOwnForegroundWindow();
                        }
                        catch { /* best-effort; borderless already applied */ }

                        return Result.Applied;
                    }
                }
            }
            return Result.TimedOut;
        }

        private static HashSet<IntPtr> SnapshotTopLevelHwnds()
        {
            var set = new HashSet<IntPtr>();
            EnumWindows((h, _) => { if (IsWindowVisible(h)) set.Add(h); return true; }, IntPtr.Zero);
            return set;
        }

        private static bool IsCandidate(IntPtr hwnd, string? gameNameHint)
        {
            // Skip windows owned by the launcher process tree (steam.exe etc.)
            // and our own DAX.OSI process. The "interesting" window is the
            // one owned by the GAME, not by the launcher.
            try
            {
                _ = GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == 0) return false;
                using var p = Process.GetProcessById((int)pid);
                var pname = p.ProcessName ?? string.Empty;
                if (string.Equals(pname, "steam", StringComparison.OrdinalIgnoreCase)) return false;
                if (string.Equals(pname, "steamwebhelper", StringComparison.OrdinalIgnoreCase)) return false;
                if (pname.StartsWith("DAX.OSI", StringComparison.OrdinalIgnoreCase)) return false;

                // Optional name-hint filter so we don't borderless-ify something
                // unrelated the user happened to open while the game was loading.
                if (!string.IsNullOrWhiteSpace(gameNameHint))
                {
                    var title = GetTitle(hwnd);
                    var titleHits = ContainsAny(title, gameNameHint);
                    var nameHits = ContainsAny(pname, gameNameHint);
                    if (!titleHits && !nameHits)
                    {
                        // Fall through anyway if the window is fullscreen-sized
                        // (covers titles whose process name is something cryptic
                        // like "GameLauncher.x64.exe").
                        if (!IsFullscreenSized(hwnd)) return false;
                    }
                }

                return true;
            }
            catch { return false; }
        }

        private static bool ContainsAny(string haystack, string hint)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(hint)) return false;
            // Match on any whitespace-separated word from the hint - "Half-Life 2"
            // hinting against title "Half-Life 2 - Direct3D 9" should still match.
            foreach (var word in hint.Split(new[] { ' ', '\t', '-', ':' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Length < 3) continue;
                if (haystack.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string GetTitle(IntPtr hwnd)
        {
            var len = GetWindowTextLength(hwnd);
            if (len <= 0) return string.Empty;
            var sb = new System.Text.StringBuilder(len + 1);
            _ = GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static bool IsFullscreenSized(IntPtr hwnd)
        {
            // "Fullscreen-sized" = within 32px of the monitor's full bounds.
            // Real game windows are exactly the monitor size; small dialogs
            // / launchers are obviously not.
            if (!GetWindowRect(hwnd, out var r)) return false;
            var mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref info)) return false;
            var winW = r.Right - r.Left;
            var winH = r.Bottom - r.Top;
            var monW = info.rcMonitor.Right - info.rcMonitor.Left;
            var monH = info.rcMonitor.Bottom - info.rcMonitor.Top;
            return Math.Abs(winW - monW) < 32 && Math.Abs(winH - monH) < 32;
        }

        /// <summary>
        /// Last-resort fallback when Windows refuses to surface the game
        /// window above DAX.OSI's fullscreen MainWindow. We minimize OUR
        /// own foreground window (the DAX.OSI shell) so the game is the
        /// only candidate for the foreground slot. Skips the action if
        /// the current foreground window doesn't belong to our process,
        /// to avoid messing with anything outside DAX.OSI.
        /// </summary>
        private static void MinimizeOwnForegroundWindow()
        {
            try
            {
                var fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return;

                _ = GetWindowThreadProcessId(fg, out var fgPid);
                var ownPid = (uint)Environment.ProcessId;
                if (fgPid == 0 || fgPid != ownPid) return;

                _ = ShowWindow(fg, SW_MINIMIZE);
            }
            catch { /* best effort */ }
        }

        private static bool TryApplyBorderless(IntPtr hwnd)
        {
            try
            {
                var style = GetWindowLong(hwnd, GWL_STYLE);
                var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);

                // Strip every chrome-bearing flag. Leaving WS_VISIBLE +
                // WS_CLIPSIBLINGS (the bits we DON'T touch) keeps the window
                // composited and visible.
                style &= ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZE |
                           WS_MAXIMIZE | WS_SYSMENU | WS_BORDER | WS_DLGFRAME);
                exStyle &= ~(WS_EX_DLGMODALFRAME | WS_EX_CLIENTEDGE |
                             WS_EX_STATICEDGE | WS_EX_WINDOWEDGE);

                _ = SetWindowLong(hwnd, GWL_STYLE, style);
                _ = SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);

                // Pin to the monitor the window currently lives on, at full
                // size, topmost. SWP_FRAMECHANGED forces the WM to recompute
                // the non-client area now that the chrome flags are gone.
                var mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (!GetMonitorInfo(mon, ref info)) return false;
                var w = info.rcMonitor.Right - info.rcMonitor.Left;
                var h = info.rcMonitor.Bottom - info.rcMonitor.Top;

                return SetWindowPos(hwnd, HWND_TOPMOST,
                    info.rcMonitor.Left, info.rcMonitor.Top, w, h,
                    SWP_FRAMECHANGED | SWP_SHOWWINDOW);
            }
            catch { return false; }
        }
    }

    // =====================================================================
    // macOS implementation - best effort via AppleScript
    // =====================================================================

    [SupportedOSPlatform("macos")]
    private static class MacImpl
    {
        public static Result WatchAndApply(string? gameNameHint, DateTime deadlineUtc)
        {
            // We can't enumerate other processes' AppKit windows from C#
            // without a private framework. Instead we wait briefly for the
            // game launcher to bring up a new app, then send the standard
            // "Enter Fullscreen" shortcut to the frontmost app via osascript.
            // For games that support AppKit fullscreen this hides the menu
            // bar and the Dock, which is the closest macOS equivalent of
            // Windows' borderless. Games that implement their own custom
            // fullscreen ignore this entirely - nothing we can do about that.
            //
            // Wait a fixed amount of time before sending the shortcut. We
            // can't reliably detect "new game window appeared" without the
            // Accessibility API and an entitlement, so we just give it a
            // reasonable head start. The caller surfaces the result as
            // "best effort", not "guaranteed".
            var waitMs = (int)Math.Min(30000, Math.Max(5000,
                (deadlineUtc - DateTime.UtcNow).TotalMilliseconds / 2));
            Thread.Sleep(waitMs);

            // Ctrl+Cmd+F = control(^) + command(⌘) + F. AppleScript "key code 3"
            // is the F key. Modifiers are passed as a list.
            const string script = """
                tell application "System Events"
                    key code 3 using {control down, command down}
                end tell
            """;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "/usr/bin/osascript",
                    Arguments = "-e " + EscapeForShell(script),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                };
                using var p = Process.Start(psi);
                if (p == null) return Result.Unsupported;
                if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return Result.TimedOut; }
                return p.ExitCode == 0 ? Result.Applied : Result.Unsupported;
            }
            catch { return Result.Unsupported; }
        }

        private static string EscapeForShell(string s) =>
            "'" + s.Replace("'", "'\\''") + "'";
    }

    // =====================================================================
    // Linux X11 implementation - best effort via wmctrl
    // =====================================================================

    [SupportedOSPlatform("linux")]
    private static class LinuxX11Impl
    {
        public static Result WatchAndApply(string? gameNameHint, DateTime deadlineUtc)
        {
            // wmctrl is the only widely-shipped CLI for cross-process X11
            // window manipulation. Probe for it once - on systems without
            // wmctrl we can't help, but at least we say so clearly.
            if (!IsCommandAvailable("wmctrl")) return Result.ToolMissing;

            // Wait for a new fullscreen-able window. wmctrl -l lists windows;
            // we capture the baseline, poll until something new shows up, then
            // toggle its fullscreen state. The hint, if provided, narrows the
            // match by window-title substring.
            var beforeIds = ListWmctrlWindowIds();

            while (DateTime.UtcNow < deadlineUtc)
            {
                Thread.Sleep(500);
                var current = ListWmctrlWindowIds();
                foreach (var (id, title) in current)
                {
                    if (beforeIds.Any(b => b.Id == id)) continue;
                    if (!string.IsNullOrWhiteSpace(gameNameHint) &&
                        !title.Contains(gameNameHint, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // _NET_WM_STATE_FULLSCREEN is the freedesktop standard for
                    // borderless-fullscreen on X11. Most WMs implement it.
                    var ok = RunCommand("wmctrl", $"-i -r {id} -b add,fullscreen") &&
                             RunCommand("wmctrl", $"-i -a {id}");
                    if (ok) return Result.Applied;
                }
            }
            return Result.TimedOut;
        }

        private static List<(string Id, string Title)> ListWmctrlWindowIds()
        {
            var list = new List<(string, string)>();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "wmctrl",
                    Arguments = "-l",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return list;
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(2000);
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    // Format:  0x0a000004  0  hostname  Window Title Goes Here
                    var parts = line.Split(new[] { ' ' }, 5, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 4)
                        list.Add((parts[0], parts.Length >= 5 ? parts[4] : string.Empty));
                }
            }
            catch { /* wmctrl flake -> empty list */ }
            return list;
        }

        private static bool IsCommandAvailable(string name)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "/usr/bin/which",
                    Arguments = name,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                p.WaitForExit(1500);
                return p.ExitCode == 0;
            }
            catch { return false; }
        }

        private static bool RunCommand(string file, string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = file,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                p.WaitForExit(2000);
                return p.ExitCode == 0;
            }
            catch { return false; }
        }
    }
}
