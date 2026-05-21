using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace DOSI.CORE;

/// <summary>
/// Catches unhandled exceptions reaching the <see cref="AppDomain"/> or
/// the task scheduler, writes a timestamped crash log next to the
/// executable, and surfaces the most recent crash on the next startup so
/// the user (and the developer reading the log) has actionable evidence.
/// <para>
/// LIFETIME: <see cref="Install"/> is called once from <c>Program.Main</c>
/// before Avalonia spins up. <see cref="ConsumePendingCrashAsync"/> is
/// called from <c>App.Initialize</c> (or as soon as the dispatcher is
/// running) to surface any prior-run crash.
/// </para>
/// <para>
/// FILE LOCATION: <c>&lt;executable&gt;/crash.log</c>. Same folder as
/// <c>SystemSettings.json</c> so a user reporting "DAX.OSI crashed" can
/// be told exactly where to find the file in one sentence. Old logs are
/// rotated to <c>crash.log.1</c> (one generation only - we don't need a
/// full history, just the previous run's evidence).
/// </para>
/// </summary>
public static class CrashReporter
{
    /// <summary>Filename of the current-run crash log.</summary>
    public const string CrashLogFileName = "crash.log";

    private static bool _installed;

    /// <summary>
    /// Hooks the global handlers. Idempotent. Safe to call before any
    /// Avalonia infrastructure is initialised - we only touch BCL APIs.
    /// </summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            // IsTerminating is true here in practice (UnhandledException on
            // .NET means the process is going down). Write synchronously so
            // the log lands before the runtime tears down, and never throw
            // from inside the handler.
            try { WriteCrashLog(e.ExceptionObject as Exception, isTerminating: e.IsTerminating); }
            catch { /* swallow - the process is already dying */ }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // Unobserved exceptions don't kill the process by default, but
            // they're still bugs. Log and mark observed so we don't get
            // re-raised as an UnhandledException later.
            try { WriteCrashLog(e.Exception, isTerminating: false); }
            catch { /* same as above */ }
            e.SetObserved();
        };
    }

    /// <summary>
    /// If a crash log from the previous run is present, returns its full
    /// path and rotates it to <c>crash.log.1</c>. Returns <c>null</c> when
    /// no log exists. Callers (<c>App.Initialize</c>) typically use this
    /// to pop a one-time dialog informing the user about the prior crash.
    /// </summary>
    public static string? ConsumePendingCrash()
    {
        try
        {
            var path = GetCrashLogPath();
            if (!File.Exists(path)) return null;

            // Rotate so the next run doesn't re-report the same crash.
            var rotated = path + ".1";
            try { if (File.Exists(rotated)) File.Delete(rotated); } catch { /* best-effort */ }
            try { File.Move(path, rotated); } catch { /* same */ }

            return rotated;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Absolute path of the active crash log.</summary>
    public static string GetCrashLogPath() =>
        Path.Combine(AppContext.BaseDirectory, CrashLogFileName);

    private static void WriteCrashLog(Exception? ex, bool isTerminating)
    {
        var path = GetCrashLogPath();
        var sb = new StringBuilder();
        sb.AppendLine($"DAX.OSI crash report");
        sb.AppendLine($"When (UTC):    {DateTime.UtcNow:O}");
        sb.AppendLine($"When (local):  {DateTime.Now}");
        sb.AppendLine($"Terminating:   {isTerminating}");
        sb.AppendLine($"OS:            {Environment.OSVersion}");
        sb.AppendLine($".NET:          {Environment.Version}");
        sb.AppendLine($"Process:       PID {Environment.ProcessId}");
        sb.AppendLine();
        if (ex == null)
        {
            sb.AppendLine("(no exception object - unknown crash source)");
        }
        else
        {
            // Walk the inner-exception chain manually so we always see the
            // root cause even if intermediate exceptions wrap unhelpfully.
            var current = ex;
            int depth = 0;
            while (current != null)
            {
                sb.AppendLine($"--- Exception [{depth}]: {current.GetType().FullName} ---");
                sb.AppendLine(current.Message);
                if (!string.IsNullOrEmpty(current.StackTrace))
                    sb.AppendLine(current.StackTrace);
                sb.AppendLine();
                current = current.InnerException;
                depth++;
            }
        }
        File.WriteAllText(path, sb.ToString());
    }
}
