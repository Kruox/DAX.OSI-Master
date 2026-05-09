using System;
using System.IO;
using System.Text;
using DOSI.CORE.UserManagement;

namespace DOSI.CORE.Security;

/// <summary>
/// Categories of security-relevant events recorded by <see cref="SecurityAuditLog"/>.
/// </summary>
public enum SecurityAuditEventType
{
    LoginSuccess,
    LoginFailure,
    LoginLockedOut,
    SignedOut,
    PasswordChanged,
    SessionLocked,
    SessionUnlocked,
    AccessDenied
}

/// <summary>
/// Append-only per-user audit log. Each user's events are written to
/// <c>Users/&lt;username&gt;/.audit.log</c> as one event per line:
/// <c>UTC_ISO8601\tEVENT\tDETAILS</c>.
/// <para>
/// The log is best-effort: failures to write never throw and never affect
/// the calling operation. Subscribes to <see cref="UserManager"/> security
/// events on first use.
/// </para>
/// </summary>
public static class SecurityAuditLog
{
    private const string AuditFileName = ".audit.log";
    private static readonly object SyncRoot = new();
    private static bool _initialized;

    /// <summary>
    /// Subscribes to <see cref="UserManager"/> security events so they are
    /// automatically persisted. Safe to call multiple times.
    /// </summary>
    public static void Initialize()
    {
        lock (SyncRoot)
        {
            if (_initialized) return;
            _initialized = true;

            UserManager.LoginSucceeded += (_, name) =>
                AppendForUser(name, SecurityAuditEventType.LoginSuccess, null);

            UserManager.LoginFailed += (_, name) =>
                AppendForUser(name, SecurityAuditEventType.LoginFailure, null);

            UserManager.LoginLockedOut += (_, payload) =>
                AppendForUser(payload.Username, SecurityAuditEventType.LoginLockedOut,
                    $"unlock_in={payload.SecondsUntilUnlock}s");

            UserManager.UserSignedOut += (_, name) =>
                AppendForUser(name, SecurityAuditEventType.SignedOut, null);

            UserManager.PasswordChanged += (_, name) =>
                AppendForUser(name, SecurityAuditEventType.PasswordChanged, null);
        }
    }

    /// <summary>
    /// Appends an event for an arbitrary user (used by external code, e.g.
    /// <c>SessionLockManager</c> for session lock/unlock events).
    /// </summary>
    public static void AppendForUser(string username, SecurityAuditEventType type, string? details)
    {
        if (string.IsNullOrWhiteSpace(username)) return;

        try
        {
            var dir = UserManager.GetUserFolder(username);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, AuditFileName);

            var line = string.Concat(
                DateTime.UtcNow.ToString("O"),
                "\t", type.ToString(),
                "\t", details ?? string.Empty,
                Environment.NewLine);

            File.AppendAllText(path, line, Encoding.UTF8);
        }
        catch
        {
            // Best-effort logging; never propagate errors to the caller.
        }
    }

    /// <summary>
    /// Convenience overload that appends an event for the currently signed-in user.
    /// No-op when nobody is signed in.
    /// </summary>
    public static void Append(SecurityAuditEventType type, string? details = null)
    {
        var user = UserManager.CurrentUser;
        if (user == null) return;
        AppendForUser(user.Username, type, details);
    }
}
