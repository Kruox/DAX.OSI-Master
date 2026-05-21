using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using DOSI.CORE.UIComponents.WindowManagement;

namespace DOSI.CORE;

/// <summary>
/// Coordinates a clean, leak-free shutdown of the DAX.OSI / DOSI.CORE process.
/// Supports immediate shutdown and timed (countdown) shutdown that can be
/// cancelled before it completes.
/// </summary>
public static class SystemShutdown
{
    private static DispatcherTimer? _timer;
    private static int _remainingSeconds;
    private static bool _isExecuting;

    /// <summary>True while a timed shutdown is counting down.</summary>
    public static bool IsShutdownPending => _timer != null;

    /// <summary>Seconds remaining on the active countdown, or 0 if none.</summary>
    public static int RemainingSeconds => _remainingSeconds;

    /// <summary>
    /// Fires once per second of remaining time during a timed shutdown,
    /// starting with the initial total. Listeners must remove themselves
    /// (e.g. on <see cref="CountdownCancelled"/> or <see cref="ShuttingDown"/>)
    /// to avoid leaks.
    /// </summary>
    public static event Action<int>? CountdownTick;

    /// <summary>Fires when a pending countdown is cancelled.</summary>
    public static event Action? CountdownCancelled;

    /// <summary>
    /// Fires immediately before the application is torn down. Subscribers
    /// should perform their own cleanup (close windows, save state, dispose
    /// timers, etc.). Always invoked on the UI thread.
    /// </summary>
    public static event Action? ShuttingDown;

    /// <summary>
    /// Fires the moment shutdown is initiated, BEFORE the
    /// <see cref="ShutdownSequence"/> UI animation runs. Use this to dispose
    /// resources that don't honour Avalonia's z-order (e.g. native WebView2
    /// HWNDs that would otherwise float on top of the shutdown screen until
    /// <see cref="ShuttingDown"/> fires after the animation). Always invoked
    /// on the UI thread.
    /// </summary>
    public static event Action? ShutdownStarting;

    /// <summary>
    /// Optional asynchronous sequence to run *before* tear-down. Typically
    /// the host wires this to display a shutdown splash screen and awaits
    /// its completion. Exceptions are swallowed so a failed animation
    /// never blocks shutdown.
    /// </summary>
    public static Func<Task>? ShutdownSequence { get; set; }

    /// <summary>
    /// Begins shutdown. When <paramref name="seconds"/> is 0 (default) the
    /// process exits immediately; otherwise a countdown is started and
    /// <see cref="CountdownTick"/> fires every second.
    /// </summary>
    public static void Begin(int seconds = 0)
    {
        if (_isExecuting) return;

        Cancel();

        if (seconds <= 0)
        {
            Execute();
            return;
        }

        _remainingSeconds = seconds;
        // Emit the initial tick synchronously so UIs can show "N..." immediately.
        SafeInvoke(CountdownTick, _remainingSeconds);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnCountdownTick;
        _timer.Start();
    }

    /// <summary>
    /// Cancels a pending timed shutdown. No-op if none is scheduled.
    /// </summary>
    public static void Cancel()
    {
        if (_timer == null) return;

        _timer.Stop();
        _timer.Tick -= OnCountdownTick;
        _timer = null;
        _remainingSeconds = 0;

        SafeInvoke(CountdownCancelled);
    }

    private static void OnCountdownTick(object? sender, EventArgs e)
    {
        _remainingSeconds--;

        if (_remainingSeconds <= 0)
        {
            Execute();
            return;
        }

        SafeInvoke(CountdownTick, _remainingSeconds);
    }

    private static void Execute()
    {
        if (_isExecuting) return;
        _isExecuting = true;

        // Stop and detach the timer first so it can never tick again mid-tear-down.
        if (_timer != null)
        {
            _timer.Stop();
            _timer.Tick -= OnCountdownTick;
            _timer = null;
        }
        _remainingSeconds = 0;

        // Hand off to the host's UI sequence (e.g. ShutdownScreen). When it
        // resolves, finish the actual tear-down. Always runs on the UI thread.
        Dispatcher.UIThread.Post(async () =>
        {
            // Give native-resource owners (e.g. DOSIWebBrowser's WebView2
            // HWND) a chance to hide / dispose BEFORE the shutdown overlay
            // animates in. Otherwise the native HWND ignores Avalonia z-order
            // and remains visible on top of the shutdown screen.
            SafeInvoke(ShutdownStarting);

            // Persist any debounced window-geometry writes before the
            // dispatcher tears down. Same reason as in SystemSignOut.Begin.
            try { DOSI.CORE.UIComponents.WindowManagement.WindowGeometryRegistry.FlushNow(); } catch { }
            try { DOSI.CORE.UIComponents.WindowManagement.DesktopIconLayout.FlushNow(); } catch { }

            var seq = ShutdownSequence;
            if (seq != null)
            {
                try { await seq(); } catch { }
            }

            FinalizeShutdown();
        }, DispatcherPriority.Background);
    }

    private static void FinalizeShutdown()
    {
        // 1. Let listeners (MainWindow, screens, terminals) clean up first.
        SafeInvoke(ShuttingDown);

        // 2. Persist any settings changes.
        try { SystemCore.SaveSettings(); } catch { }

        // 3. Close every managed window so their resources are released.
        try { WindowManager.Instance?.CloseAllWindows(); } catch { }

        // 4. Drop event subscribers so we don't hold references after exit.
        CountdownTick = null;
        CountdownCancelled = null;
        ShuttingDown = null;
        ShutdownStarting = null;
        ShutdownSequence = null;

        // 5. Shut down the Avalonia application lifetime cleanly.
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
        else
        {
            Environment.Exit(0);
        }
    }

    private static void SafeInvoke(Action? handler)
    {
        try { handler?.Invoke(); } catch { }
    }

    private static void SafeInvoke(Action<int>? handler, int value)
    {
        try { handler?.Invoke(value); } catch { }
    }
}
