using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DOSI.CORE.AccentManagement;

namespace DOSI.CORE.UserManagement;

/// <summary>
/// Represents a single DOSI user account persisted to disk as
/// <c>&lt;DOSI base&gt;/Users/&lt;username&gt;/&lt;username&gt;.json</c>.
/// </summary>
public sealed class DOSIUser
{
    /// <summary>The lowercase, file-system-safe username (also the folder name).</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>The user's preferred display name. Defaults to <see cref="Username"/>.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Base64-encoded PBKDF2 password hash.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Base64-encoded random salt.</summary>
    public string PasswordSalt { get; set; } = string.Empty;

    /// <summary>PBKDF2 iteration count used to derive <see cref="PasswordHash"/>.</summary>
    public int PasswordIterations { get; set; } = UserManager.PasswordIterationCount;

    /// <summary>UTC time the account was created.</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>UTC time of the last successful login. <c>null</c> if never signed in.</summary>
    public DateTime? LastLoginUtc { get; set; }

    /// <summary>Whether this account has administrator privileges.</summary>
    public bool IsAdministrator { get; set; }

    /// <summary>Optional path (relative to the user folder) to an avatar image.</summary>
    public string? AvatarPath { get; set; }

    /// <summary>Free-form per-user preferences (accent overrides, wallpaper, etc.).</summary>
    public Dictionary<string, string> Preferences { get; set; } = new();

    // ----- File vault (transparent at-rest encryption; see DOSI.CORE.Security.UserVault) -----

    /// <summary>Base64-encoded random salt used to derive the vault password key.</summary>
    public string? VaultPasswordSalt { get; set; }

    /// <summary>PBKDF2 iteration count used to derive the vault password key.</summary>
    public int VaultPasswordIterations { get; set; }

    /// <summary>
    /// Base64-encoded data key wrapped (AES-GCM-encrypted) with the password key.
    /// Layout: <c>ciphertext || tag</c>.
    /// </summary>
    public string? VaultWrappedDataKey { get; set; }

    /// <summary>Base64-encoded AES-GCM nonce used when wrapping the data key.</summary>
    public string? VaultDataKeyNonce { get; set; }
}

/// <summary>
/// Result codes returned when creating a new user account.
/// </summary>
public enum UserCreationResult
{
    Success,
    InvalidUsername,
    UsernameAlreadyExists,
    InvalidPassword,
    IOError
}

/// <summary>
/// Manages DOSI user accounts. Accounts are stored on disk as JSON files under
/// <c>&lt;DOSI base directory&gt;/Users/&lt;username&gt;/&lt;username&gt;.json</c>.
/// </summary>
public static class UserManager
{
    /// <summary>Iteration count used by PBKDF2 when hashing passwords.</summary>
    public const int PasswordIterationCount = 120_000;

    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;

    private const string UsersFolderName = "Users";
    private const string AccountFileExtension = ".json";

    // Allowed: 3-32 chars, lowercase letters, digits, underscore, hyphen, must start with letter.
    private static readonly Regex UsernamePattern =
        new(@"^[a-z][a-z0-9_\-]{2,31}$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly object SyncRoot = new();

    /// <summary>The currently logged-in user, or <c>null</c> if no one is signed in.</summary>
    public static DOSIUser? CurrentUser { get; private set; }

    /// <summary>Raised after a new user account has been successfully created.</summary>
    public static event EventHandler<DOSIUser>? UserCreated;

    /// <summary>Raised after a user account has been deleted from disk.</summary>
    public static event EventHandler<DOSIUser>? UserDeleted;

    /// <summary>Raised when <see cref="CurrentUser"/> changes (sign-in or sign-out).</summary>
    public static event EventHandler<DOSIUser?>? CurrentUserChanged;

    // ----- Security events (consumed by SecurityAuditLog and SessionLockManager) -----

    /// <summary>Raised after a successful authentication. Argument is the username.</summary>
    public static event EventHandler<string>? LoginSucceeded;

    /// <summary>Raised after a failed authentication attempt. Argument is the username.</summary>
    public static event EventHandler<string>? LoginFailed;

    /// <summary>
    /// Raised when an authentication attempt is rejected because the account
    /// is currently locked out. Argument is (username, secondsUntilUnlock).
    /// </summary>
    public static event EventHandler<(string Username, int SecondsUntilUnlock)>? LoginLockedOut;

    /// <summary>Raised after the user signs out (or is signed out by deletion).</summary>
    public static event EventHandler<string>? UserSignedOut;

    /// <summary>Raised after a password is successfully changed. Argument is the username.</summary>
    public static event EventHandler<string>? PasswordChanged;

    // ----- Login lockout (in-memory; resets on app restart by design) -----

    /// <summary>Failed-attempt threshold before the lockout cool-down kicks in.</summary>
    public const int FailedAttemptThreshold = 3;

    /// <summary>Maximum lockout duration in seconds (cap on the exponential back-off).</summary>
    public const int MaxLockoutSeconds = 300;

    private sealed class LockoutEntry
    {
        public int FailureCount;
        public DateTime? LockedUntilUtc;
    }

    private static readonly Dictionary<string, LockoutEntry> Lockouts =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the number of seconds <paramref name="username"/> must wait
    /// before another login attempt will be processed, or <c>0</c> if the
    /// account is not currently locked out.
    /// </summary>
    public static int GetLockoutSecondsRemaining(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return 0;
        lock (SyncRoot) { return GetLockoutSecondsRemainingNoLock(username); }
    }

    /// <summary>Clears the failed-attempt counter and lockout for the given user.</summary>
    public static void ResetLockout(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return;
        lock (SyncRoot) { Lockouts.Remove(username); }
    }

    private static int GetLockoutSecondsRemainingNoLock(string username)
    {
        if (!Lockouts.TryGetValue(username, out var entry)) return 0;
        if (entry.LockedUntilUtc == null) return 0;
        var remaining = (entry.LockedUntilUtc.Value - DateTime.UtcNow).TotalSeconds;
        return remaining <= 0 ? 0 : (int)Math.Ceiling(remaining);
    }

    private static void RegisterFailedAttemptNoLock(string username)
    {
        if (!Lockouts.TryGetValue(username, out var entry))
        {
            entry = new LockoutEntry();
            Lockouts[username] = entry;
        }
        entry.FailureCount++;
        var seconds = ComputeLockoutSeconds(entry.FailureCount);
        entry.LockedUntilUtc = seconds > 0 ? DateTime.UtcNow.AddSeconds(seconds) : (DateTime?)null;
    }

    private static int ComputeLockoutSeconds(int failureCount)
    {
        // Exponential back-off after FailedAttemptThreshold failures:
        // attempt 4 -> 5s, 5 -> 15s, 6 -> 45s, 7 -> 135s, 8+ -> capped at MaxLockoutSeconds.
        var over = failureCount - FailedAttemptThreshold;
        if (over <= 0) return 0;
        var seconds = (int)Math.Min(MaxLockoutSeconds, 5L * (long)Math.Pow(3, over - 1));
        return seconds;
    }

    /// <summary>The absolute path to the root <c>Users</c> directory.</summary>
    public static string UsersRootPath => Path.Combine(AppContext.BaseDirectory, UsersFolderName);

    /// <summary>
    /// Ensures the root <c>Users</c> directory exists. Safe to call multiple times.
    /// </summary>
    public static void Initialize()
    {
        Directory.CreateDirectory(UsersRootPath);
    }

    /// <summary>
    /// Returns <c>true</c> if at least one user account folder exists on disk.
    /// Used by first-run detection.
    /// </summary>
    public static bool HasAnyUsers()
    {
        if (!Directory.Exists(UsersRootPath)) return false;

        foreach (var dir in Directory.EnumerateDirectories(UsersRootPath))
        {
            var name = Path.GetFileName(dir);
            if (IsValidUsername(name) && File.Exists(Path.Combine(dir, name + AccountFileExtension)))
                return true;
        }
        return false;
    }

    #region Accent Preference

    /// <summary>The preferences key under which a user's preferred accent is stored.</summary>
    public const string AccentPreferenceKey = "accent";

    /// <summary>
    /// Returns the user's preferred <see cref="DOSIAccent"/>, or <c>null</c> if none is set
    /// or the stored value is invalid.
    /// </summary>
    public static DOSIAccent? GetUserAccent(DOSIUser user)
    {
        if (user == null) return null;
        if (!user.Preferences.TryGetValue(AccentPreferenceKey, out var value)) return null;
        return Enum.TryParse<DOSIAccent>(value, ignoreCase: true, out var accent) ? accent : null;
    }

    /// <summary>
    /// Stores the user's preferred accent in <see cref="DOSIUser.Preferences"/> and persists
    /// the account to disk.
    /// </summary>
    public static bool SetUserAccent(DOSIUser user, DOSIAccent accent)
    {
        if (user == null) return false;
        user.Preferences[AccentPreferenceKey] = accent.ToString();
        return SaveUser(user);
    }

    #endregion

    #region Wallpaper Preference

    /// <summary>The preferences key under which a user's preferred wallpaper is stored.</summary>
    public const string WallpaperPreferenceKey = "wallpaper";

    /// <summary>
    /// Returns the user's preferred wallpaper key, or <c>null</c> if none is set.
    /// A value equal to <c>"__accent__"</c> means the user explicitly opted out
    /// of an image wallpaper in favor of the accent-tinted desktop.
    /// </summary>
    public static string? GetUserWallpaper(DOSIUser user)
    {
        if (user == null) return null;
        if (!user.Preferences.TryGetValue(WallpaperPreferenceKey, out var value)) return null;
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// Stores the user's preferred wallpaper key in <see cref="DOSIUser.Preferences"/>
    /// and persists the account to disk. Pass <c>null</c> to remove the preference.
    /// </summary>
    public static bool SetUserWallpaper(DOSIUser user, string? wallpaperKey)
    {
        if (user == null) return false;
        if (string.IsNullOrEmpty(wallpaperKey))
            user.Preferences.Remove(WallpaperPreferenceKey);
        else
            user.Preferences[WallpaperPreferenceKey] = wallpaperKey;
        return SaveUser(user);
    }

    /// <summary>
    /// The preferences key under which the per-user desktop wallpaper-blur
    /// toggle is stored. Only consumed by <c>DesktopScreen</c> - the
    /// login / sign-out / shutdown / setup screens always render the
    /// blurred wallpaper variant regardless of this preference.
    /// </summary>
    public const string WallpaperBlurPreferenceKey = "wallpaper_blur";

    /// <summary>
    /// The preferences key under which the wallpaper fit mode (Fill / Fit
    /// / Stretch / Center / Tile) is stored. Stored as the enum's string
    /// name for forward-compat - reordering / inserting enum values
    /// doesn't break existing user files the way storing the int would.
    /// </summary>
    public const string WallpaperFitPreferenceKey = "wallpaper_fit";

    public static string? GetUserWallpaperFit(DOSIUser user)
    {
        if (user == null) return null;
        return user.Preferences.TryGetValue(WallpaperFitPreferenceKey, out var v) ? v : null;
    }

    public static bool SetUserWallpaperFit(DOSIUser user, string mode)
    {
        if (user == null) return false;
        user.Preferences[WallpaperFitPreferenceKey] = mode;
        return SaveUser(user);
    }

    /// <summary>
    /// Returns whether the user has the desktop wallpaper-blur toggle on.
    /// Defaults to <c>true</c> when no preference has been saved (matches
    /// the visual every other DOSI screen ships with).
    /// </summary>
    public static bool GetUserWallpaperBlur(DOSIUser user)
    {
        if (user == null) return true;
        if (!user.Preferences.TryGetValue(WallpaperBlurPreferenceKey, out var value)) return true;
        return !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Stores the user's desktop wallpaper-blur preference and persists the
    /// account to disk.
    /// </summary>
    public static bool SetUserWallpaperBlur(DOSIUser user, bool enabled)
    {
        if (user == null) return false;
        user.Preferences[WallpaperBlurPreferenceKey] = enabled ? "true" : "false";
        return SaveUser(user);
    }

    /// <summary>The preferences key under which the per-user window opacity (0.5..1.0) is stored.</summary>
    public const string WindowOpacityPreferenceKey = "window_opacity";

    /// <summary>Default opacity used when the user has no saved preference.</summary>
    public const double DefaultWindowOpacity = 1.0;

    /// <summary>Lower bound of the window-opacity slider. Values below this are clamped.</summary>
    public const double MinWindowOpacity = 0.5;

    /// <summary>
    /// Returns the user's preferred DOSIWindow opacity (between <see cref="MinWindowOpacity"/>
    /// and 1.0). Defaults to <see cref="DefaultWindowOpacity"/> when no preference is saved.
    /// </summary>
    public static double GetUserWindowOpacity(DOSIUser user)
    {
        if (user == null) return DefaultWindowOpacity;
        if (!user.Preferences.TryGetValue(WindowOpacityPreferenceKey, out var value)) return DefaultWindowOpacity;
        if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return DefaultWindowOpacity;
        return Math.Clamp(parsed, MinWindowOpacity, 1.0);
    }

    /// <summary>
    /// Stores the user's preferred window opacity (clamped to [<see cref="MinWindowOpacity"/>, 1.0])
    /// and persists the account to disk.
    /// </summary>
    public static bool SetUserWindowOpacity(DOSIUser user, double opacity)
    {
        if (user == null) return false;
        opacity = Math.Clamp(opacity, MinWindowOpacity, 1.0);
        user.Preferences[WindowOpacityPreferenceKey] =
            opacity.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        return SaveUser(user);
    }

    #endregion

    #region Path Helpers

    /// <summary>Returns the folder that contains a user's data files.</summary>
    public static string GetUserFolder(string username)
    {
        var normalized = NormalizeUsername(username);
        return Path.Combine(UsersRootPath, normalized);
    }

    /// <summary>Returns the path to the user's account JSON file.</summary>
    public static string GetUserFilePath(string username)
    {
        var normalized = NormalizeUsername(username);
        return Path.Combine(GetUserFolder(normalized), normalized + AccountFileExtension);
    }

    /// <summary>
    /// Standard top-level subfolders created inside every user's home directory
    /// the first time their account is provisioned. Mirrors a real OS layout
    /// (Documents, Pictures, Music, ...).
    /// </summary>
    public static IReadOnlyList<string> StandardUserSubfolders { get; } = new[]
    {
        "Desktop",
        "Documents",
        "Downloads",
        "Music",
        "Pictures",
        "Projects",
        "Videos"
    };

    /// <summary>
    /// Returns the path to a known subfolder inside <paramref name="user"/>'s
    /// home directory (e.g. "Documents"). The folder is NOT auto-created here;
    /// call <see cref="EnsureUserSubfolders"/> first if you need it on disk.
    /// </summary>
    public static string GetUserSubfolder(DOSIUser user, string subfolderName)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (string.IsNullOrWhiteSpace(subfolderName))
            throw new ArgumentException("Subfolder name cannot be empty.", nameof(subfolderName));

        return Path.Combine(GetUserFolder(user.Username), subfolderName);
    }

    /// <summary>
    /// Creates every entry in <see cref="StandardUserSubfolders"/> inside the
    /// user's home directory if it doesn't already exist. Safe to call repeatedly
    /// (acts as a self-heal for older accounts that pre-date this feature).
    /// </summary>
    public static void EnsureUserSubfolders(DOSIUser user)
    {
        if (user == null || !IsValidUsername(user.Username)) return;

        try
        {
            var home = GetUserFolder(user.Username);
            Directory.CreateDirectory(home);
            foreach (var name in StandardUserSubfolders)
            {
                Directory.CreateDirectory(Path.Combine(home, name));
            }
        }
        catch
        {
            // Folder bootstrap is best-effort; never fail account ops because of it.
        }
    }

    #endregion

    #region Validation

    /// <summary>
    /// Returns <c>true</c> if <paramref name="username"/> matches the allowed pattern
    /// (3-32 chars, lowercase letters/digits/underscore/hyphen, must start with a letter).
    /// </summary>
    public static bool IsValidUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return false;
        return UsernamePattern.IsMatch(username.Trim().ToLowerInvariant());
    }

    /// <summary>Minimum allowed password length for new accounts and password changes.</summary>
    public const int MinimumPasswordLength = 8;

    /// <summary>
    /// Returns <c>true</c> if the password meets minimum complexity requirements
    /// (at least <see cref="MinimumPasswordLength"/> characters, no leading/trailing whitespace).
    /// </summary>
    public static bool IsValidPassword(string? password)
    {
        if (string.IsNullOrEmpty(password)) return false;
        if (password.Length < MinimumPasswordLength) return false;
        if (password != password.Trim()) return false;
        return true;
    }

    private static string NormalizeUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Username cannot be empty.", nameof(username));
        return username.Trim().ToLowerInvariant();
    }

    #endregion

    #region Enumeration

    /// <summary>Returns <c>true</c> if an account with the given username exists on disk.</summary>
    public static bool UserExists(string username)
    {
        if (!IsValidUsername(username)) return false;
        return File.Exists(GetUserFilePath(username));
    }

    /// <summary>
    /// Loads and returns every user account on disk. Corrupt files are skipped silently.
    /// </summary>
    public static IReadOnlyList<DOSIUser> GetAllUsers()
    {
        Initialize();

        var users = new List<DOSIUser>();
        foreach (var dir in Directory.EnumerateDirectories(UsersRootPath))
        {
            var name = Path.GetFileName(dir);
            if (!IsValidUsername(name)) continue;

            var user = LoadUserFromDisk(name);
            if (user != null) users.Add(user);
        }

        return users
            .OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Loads the account with the given username, or <c>null</c> if it does not exist.</summary>
    public static DOSIUser? GetUser(string username)
    {
        if (!IsValidUsername(username)) return null;
        return LoadUserFromDisk(username);
    }

    private static DOSIUser? LoadUserFromDisk(string username)
    {
        var path = GetUserFilePath(username);
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            var user = JsonSerializer.Deserialize<DOSIUser>(json, JsonOptions);
            if (user == null) return null;

            // Self-heal: ensure the username on disk matches the folder name.
            user.Username = NormalizeUsername(user.Username.Length == 0 ? username : user.Username);
            if (string.IsNullOrWhiteSpace(user.DisplayName))
                user.DisplayName = user.Username;

            return user;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region Create / Save / Delete

    /// <summary>
    /// Creates a new user account on disk. Returns a <see cref="UserCreationResult"/> describing
    /// the outcome, plus the created user (or <c>null</c> on failure) via <paramref name="user"/>.
    /// </summary>
    public static UserCreationResult CreateUser(
        string username,
        string password,
        out DOSIUser? user,
        string? displayName = null,
        bool isAdministrator = false)
    {
        user = null;

        if (!IsValidUsername(username)) return UserCreationResult.InvalidUsername;
        if (!IsValidPassword(password)) return UserCreationResult.InvalidPassword;

        var normalized = NormalizeUsername(username);

        lock (SyncRoot)
        {
            if (UserExists(normalized)) return UserCreationResult.UsernameAlreadyExists;

            try
            {
                Initialize();
                Directory.CreateDirectory(GetUserFolder(normalized));

                var (hash, salt, iterations) = HashPassword(password);

                var newUser = new DOSIUser
                {
                    Username = normalized,
                    DisplayName = string.IsNullOrWhiteSpace(displayName) ? normalized : displayName!.Trim(),
                    PasswordHash = hash,
                    PasswordSalt = salt,
                    PasswordIterations = iterations,
                    CreatedUtc = DateTime.UtcNow,
                    IsAdministrator = isAdministrator
                };

                WriteUserToDisk(newUser);
                EnsureUserSubfolders(newUser);
                user = newUser;
            }
            catch
            {
                return UserCreationResult.IOError;
            }
        }

        UserCreated?.Invoke(null, user!);
        return UserCreationResult.Success;
    }

    /// <summary>Persists changes to an existing user back to disk.</summary>
    public static bool SaveUser(DOSIUser user)
    {
        if (user == null) return false;
        if (!IsValidUsername(user.Username)) return false;

        lock (SyncRoot)
        {
            try
            {
                Directory.CreateDirectory(GetUserFolder(user.Username));
                WriteUserToDisk(user);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Deletes a user account and its entire user folder. Logs the user out if they
    /// were currently signed in.
    /// </summary>
    public static bool DeleteUser(string username)
    {
        if (!IsValidUsername(username)) return false;

        DOSIUser? deleted;
        lock (SyncRoot)
        {
            deleted = LoadUserFromDisk(username);
            if (deleted == null) return false;

            try
            {
                var folder = GetUserFolder(username);
                if (Directory.Exists(folder))
                    Directory.Delete(folder, recursive: true);
            }
            catch
            {
                return false;
            }

            if (CurrentUser != null &&
                string.Equals(CurrentUser.Username, deleted.Username, StringComparison.OrdinalIgnoreCase))
            {
                CurrentUser = null;
                CurrentUserChanged?.Invoke(null, null);
            }
        }

        UserDeleted?.Invoke(null, deleted);
        return true;
    }

    private static void WriteUserToDisk(DOSIUser user)
    {
        var path = GetUserFilePath(user.Username);
        var json = JsonSerializer.Serialize(user, JsonOptions);
        File.WriteAllText(path, json);
    }

    #endregion

    #region Authentication

    /// <summary>Returns <c>true</c> if the supplied password matches the stored hash.</summary>
    public static bool ValidatePassword(string username, string password)
    {
        if (!IsValidUsername(username) || string.IsNullOrEmpty(password)) return false;

        var user = LoadUserFromDisk(username);
        if (user == null) return false;

        return VerifyPassword(password, user);
    }

    /// <summary>
    /// Validates credentials and, on success, sets <see cref="CurrentUser"/>, updates
    /// <see cref="DOSIUser.LastLoginUtc"/>, persists the change, and returns the user.
    /// Failed attempts increment a per-username counter; after
    /// <see cref="FailedAttemptThreshold"/> failures the account is locked out
    /// with an exponential cool-down (capped at <see cref="MaxLockoutSeconds"/>).
    /// </summary>
    public static DOSIUser? Authenticate(string username, string password)
    {
        if (!IsValidUsername(username) || string.IsNullOrEmpty(password)) return null;

        var normalized = NormalizeUsername(username);

        // Lockout gate: avoid running PBKDF2 for a known-locked account.
        int locked;
        lock (SyncRoot) { locked = GetLockoutSecondsRemainingNoLock(normalized); }
        if (locked > 0)
        {
            LoginLockedOut?.Invoke(null, (normalized, locked));
            return null;
        }

        DOSIUser? user;
        lock (SyncRoot)
        {
            user = LoadUserFromDisk(normalized);
            if (user == null || !VerifyPassword(password, user))
            {
                RegisterFailedAttemptNoLock(normalized);
                LoginFailed?.Invoke(null, normalized);
                return null;
            }

            // Success - clear any prior lockout.
            Lockouts.Remove(normalized);

            user.LastLoginUtc = DateTime.UtcNow;
            try { WriteUserToDisk(user); } catch { /* best-effort */ }

            // Self-heal: ensure standard subfolders exist for accounts that
            // pre-date the file-system layout feature.
            EnsureUserSubfolders(user);

            CurrentUser = user;
        }

        // Vault key handling: if the vault has been enabled, unwrap it now so
        // file IO that goes through UserVault can transparently decrypt.
        // If it hasn't been enabled yet, lazily enable it on this sign-in so
        // future writes are encrypted (but never auto-encrypt existing files -
        // that requires explicit migration).
        if (DOSI.CORE.Security.UserVault.IsEnabledForUser(user))
        {
            DOSI.CORE.Security.UserVault.Unlock(user, password);
        }
        else
        {
            DOSI.CORE.Security.UserVault.EnableForUser(user, password);
        }

        CurrentUserChanged?.Invoke(null, user);
        LoginSucceeded?.Invoke(null, user.Username);
        return user;
    }

    /// <summary>
    /// Signs out the currently logged-in user. Clears <see cref="CurrentUser"/>
    /// and raises <see cref="CurrentUserChanged"/>. No-op if no one is signed in.
    /// </summary>
    public static void SignOut()
    {
        DOSIUser? previous;
        lock (SyncRoot)
        {
            if (CurrentUser == null) return;
            previous = CurrentUser;
            CurrentUser = null;
        }

        CurrentUserChanged?.Invoke(null, null);
        UserSignedOut?.Invoke(null, previous!.Username);
    }

    /// <summary>
    /// Replaces the password hash on the account with one derived from <paramref name="newPassword"/>.
    /// </summary>
    public static bool UpdatePassword(string username, string newPassword)
    {
        if (!IsValidPassword(newPassword)) return false;

        lock (SyncRoot)
        {
            var user = LoadUserFromDisk(username);
            if (user == null) return false;

            var (hash, salt, iterations) = HashPassword(newPassword);
            user.PasswordHash = hash;
            user.PasswordSalt = salt;
            user.PasswordIterations = iterations;

            try
            {
                WriteUserToDisk(user);
                // Re-wrap the vault data key with the new password so encrypted
                // files remain readable. Requires the vault to be unlocked
                // (it is, when the active user changes their own password).
                try { DOSI.CORE.Security.UserVault.RewrapForPasswordChange(user, newPassword); } catch { /* best-effort */ }
                PasswordChanged?.Invoke(null, user.Username);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Clears <see cref="CurrentUser"/>.</summary>
    public static void Logout()
    {
        if (CurrentUser == null) return;
        var previousName = CurrentUser.Username;
        CurrentUser = null;
        CurrentUserChanged?.Invoke(null, null);
        UserSignedOut?.Invoke(null, previousName);
    }

    #endregion

    #region Password Hashing (PBKDF2-HMAC-SHA256)

    private static (string Hash, string Salt, int Iterations) HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            PasswordIterationCount,
            HashAlgorithmName.SHA256,
            HashSizeBytes);

        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt), PasswordIterationCount);
    }

    private static bool VerifyPassword(string password, DOSIUser user)
    {
        if (string.IsNullOrEmpty(user.PasswordHash) || string.IsNullOrEmpty(user.PasswordSalt))
            return false;

        byte[] expectedHash;
        byte[] salt;
        try
        {
            expectedHash = Convert.FromBase64String(user.PasswordHash);
            salt = Convert.FromBase64String(user.PasswordSalt);
        }
        catch
        {
            return false;
        }

        var iterations = user.PasswordIterations <= 0 ? PasswordIterationCount : user.PasswordIterations;

        var actualHash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            expectedHash.Length);

        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    #endregion
}

