using System;
using System.IO;
using DOSI.CORE.UserManagement;

namespace DOSI.CORE.Security;

/// <summary>
/// Thrown when code attempts to read or write a path that does not belong
/// to the currently signed-in user (and the user is not an administrator).
/// </summary>
public sealed class UserAccessDeniedException : UnauthorizedAccessException
{
    public UserAccessDeniedException(string path)
        : base($"Access denied: '{path}' is outside the current user's home folder.")
    {
        Path = path;
    }

    /// <summary>The offending path that triggered the denial.</summary>
    public string Path { get; }
}

/// <summary>
/// Centralized per-user filesystem access enforcement.
/// <para>
/// Every file/directory operation that touches user data should funnel
/// through <see cref="AssertReadAccess"/> or <see cref="AssertWriteAccess"/>
/// so we have a single, auditable place that enforces "user A cannot read
/// user B's files" — even when the call comes from a third-party DOSI
/// app compiled inside DOSIIDE.
/// </para>
/// <para>
/// Administrators (<see cref="DOSIUser.IsAdministrator"/>) bypass the check.
/// The system-wide <c>AppContext.BaseDirectory</c> (where the executable
/// lives) is always readable so things like wallpaper assets continue to
/// work; only writes outside the user's home folder are blocked for
/// non-admins.
/// </para>
/// </summary>
public static class UserSandbox
{
    /// <summary>
    /// Returns <c>true</c> if <paramref name="path"/> is inside the current
    /// user's home folder. Returns <c>false</c> when no user is signed in
    /// or when the path is empty/invalid.
    /// </summary>
    public static bool IsPathInsideCurrentUserHome(string path)
    {
        var user = UserManager.CurrentUser;
        if (user == null) return false;
        return IsPathInsideUserHome(path, user);
    }

    /// <summary>
    /// Returns <c>true</c> if <paramref name="path"/> is inside the home
    /// folder of <paramref name="user"/>.
    /// </summary>
    public static bool IsPathInsideUserHome(string path, DOSIUser user)
    {
        if (string.IsNullOrWhiteSpace(path) || user == null) return false;

        try
        {
            var fullPath = System.IO.Path.GetFullPath(path);
            var home = System.IO.Path.GetFullPath(UserManager.GetUserFolder(user.Username));

            // Append the directory separator so "C:\Users\alice2" is not
            // considered inside "C:\Users\alice".
            var sep = System.IO.Path.DirectorySeparatorChar;
            if (!home.EndsWith(sep)) home += sep;
            if (string.Equals(fullPath + sep, home, StringComparison.OrdinalIgnoreCase)) return true;

            return fullPath.StartsWith(home, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Throws <see cref="UserAccessDeniedException"/> if the current user
    /// (when not an administrator) is not allowed to READ <paramref name="path"/>.
    /// Reads under the executable's base directory are always allowed
    /// (system assets); reads under another user's folder are denied.
    /// </summary>
    public static void AssertReadAccess(string path)
    {
        var user = UserManager.CurrentUser;

        // Pre-login or service contexts: nothing to enforce.
        if (user == null) return;

        // Admins bypass.
        if (user.IsAdministrator) return;

        // System assets next to the executable are public-readable.
        if (IsInsideSystemAssets(path)) return;

        // Anything inside the active user's home folder is fine.
        if (IsPathInsideCurrentUserHome(path)) return;

        // Anything outside the Users/ tree (system temp, etc.) is allowed
        // for reads — we only police inter-user snooping inside Users/.
        if (!IsInsideUsersTree(path)) return;

        throw new UserAccessDeniedException(path);
    }

    /// <summary>
    /// Throws <see cref="UserAccessDeniedException"/> if the current user
    /// (when not an administrator) is not allowed to WRITE
    /// <paramref name="path"/>. Writes are only ever allowed inside the
    /// signed-in user's home folder. Admins bypass the check.
    /// </summary>
    public static void AssertWriteAccess(string path)
    {
        var user = UserManager.CurrentUser;
        if (user == null) return;
        if (user.IsAdministrator) return;

        if (IsPathInsideCurrentUserHome(path)) return;

        // Writes anywhere else (including other users' folders, the system
        // assets folder, or random absolute paths) are denied for
        // non-admins.
        throw new UserAccessDeniedException(path);
    }

    private static bool IsInsideSystemAssets(string path)
    {
        try
        {
            var full = System.IO.Path.GetFullPath(path);
            var baseDir = System.IO.Path.GetFullPath(AppContext.BaseDirectory);
            var sep = System.IO.Path.DirectorySeparatorChar;
            if (!baseDir.EndsWith(sep)) baseDir += sep;

            // Allow reads anywhere under BaseDirectory EXCEPT under Users/,
            // which is policed separately.
            if (!full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase)) return false;
            return !IsInsideUsersTree(path);
        }
        catch { return false; }
    }

    private static bool IsInsideUsersTree(string path)
    {
        try
        {
            var full = System.IO.Path.GetFullPath(path);
            var users = System.IO.Path.GetFullPath(UserManager.UsersRootPath);
            var sep = System.IO.Path.DirectorySeparatorChar;
            if (!users.EndsWith(sep)) users += sep;
            return full.StartsWith(users, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
