using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using DOSI.CORE.UserManagement;

namespace DOSI.CORE.UIComponents.WindowManagement;

/// <summary>
/// Per-user persistence of desktop-icon positions, keyed by the icon's
/// FILE NAME (not full path). Stored next to the user's other settings
/// at <c>&lt;UserHome&gt;/.desktop-layout.json</c>.
/// <para>
/// KEY CHOICE: file name, not full path. The desktop folder is fixed
/// (<c>&lt;UserHome&gt;/Desktop/</c>), so the path prefix is constant
/// and storing it would just waste bytes - more importantly, when an
/// icon is renamed by the user we want to lose the saved position
/// (it's a new file from the layout's perspective) rather than
/// mistakenly applying the old position to the new name. File-name
/// keying gives us that for free.
/// </para>
/// <para>
/// Same atomic-write + debounced-flush model as
/// <see cref="WindowGeometryRegistry"/>: writes during a drag are
/// coalesced by a 500 ms timer so a long drag doesn't hammer disk.
/// </para>
/// </summary>
public static class DesktopIconLayout
{
    private const string FileName = ".desktop-layout.json";
    private const int FlushDebounceMs = 500;

    private static readonly object _gate = new();
    private static Dictionary<string, IconPosition>? _cache;
    private static string? _loadedForUsername;
    private static System.Threading.Timer? _flushTimer;
    private static bool _dirty;

    /// <summary>X / Y in desktop-canvas pixels.</summary>
    public sealed record IconPosition(double X, double Y);

    /// <summary>Saved position for the named icon, or <c>null</c> if none.</summary>
    public static IconPosition? Get(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        EnsureLoaded();
        lock (_gate)
        {
            return _cache != null && _cache.TryGetValue(fileName, out var p) ? p : null;
        }
    }

    /// <summary>
    /// Records a position and schedules a debounced flush. Cheap to call
    /// per drag tick.
    /// </summary>
    public static void Save(string fileName, double x, double y)
    {
        if (string.IsNullOrEmpty(fileName)) return;
        EnsureLoaded();
        lock (_gate)
        {
            if (_cache == null) return;
            _cache[fileName] = new IconPosition(x, y);
            _dirty = true;
            ScheduleFlushNoLock();
        }
    }

    /// <summary>Drops the saved position for one icon (used after delete / rename).</summary>
    public static void Forget(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return;
        EnsureLoaded();
        lock (_gate)
        {
            if (_cache == null || !_cache.Remove(fileName)) return;
            _dirty = true;
            ScheduleFlushNoLock();
        }
    }

    /// <summary>
    /// If <paramref name="fullPath"/> lives inside one of the user's
    /// desktop folders (<c>~/Desktop</c>, <c>~/Desktop-Monitor2</c>, ...),
    /// drops its saved position. No-op otherwise. Lets the file explorer,
    /// trash, and paste paths keep the layout JSON tidy without needing
    /// to know which monitor a given path belongs to.
    /// </summary>
    public static void ForgetIfOnDesktop(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return;
        if (!IsOnDesktop(fullPath)) return;
        Forget(Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar)));
    }

    /// <summary>
    /// Self-healing pruner. Walks EVERY desktop folder under the current
    /// user's home (<c>~/Desktop</c>, <c>~/Desktop-Monitor2</c>, ...) and
    /// drops any layout key whose file name is not actually on disk in
    /// at least one of those folders.
    /// <para>
    /// Why this exists: every other code path (Forget, ForgetIfOnDesktop,
    /// RenameIfOnDesktop, DeleteTiles) only fires when the shell is the
    /// one performing the mutation. Anything that bypasses the shell -
    /// pre-fix orphans already in the JSON, a manual file delete from
    /// outside DAX.OSI, a crash mid-delete, a watcher event that landed
    /// while the disk was in a transient state - leaves the JSON
    /// permanently desynced. Without a self-heal pass the file
    /// monotonically grows ("New folder", "New folder (2)", ... "(N)")
    /// even though none of those folders exist. Called from every
    /// <see cref="DesktopIconLayer"/> rebuild/reconcile, so the JSON
    /// converges to reality within one watcher tick of disk truth.
    /// </para>
    /// <para>
    /// MULTI-MONITOR SAFETY: keys are file-name scoped (intentionally,
    /// see the type comment), but each physical monitor has its OWN
    /// desktop folder. We must NEVER drop a key just because it isn't
    /// in layer A's folder if it's still alive in layer B's. We solve
    /// that here by enumerating ALL Desktop* folders under the user
    /// home in one pass and computing the union of file names. A key
    /// is only dropped when it isn't present in ANY of them.
    /// </para>
    /// </summary>
    public static void PruneOrphans()
    {
        EnsureLoaded();
        string? userRoot;
        try
        {
            var user = UserManager.CurrentUser;
            if (user == null) return;
            userRoot = UserManager.GetUserFolder(user.Username);
        }
        catch { return; }
        if (string.IsNullOrEmpty(userRoot) || !Directory.Exists(userRoot)) return;

        // Union of file names across every Desktop* folder. Comparison
        // matches the cache's StringComparer (Ordinal, file-name
        // semantics on the platforms DAX.OSI targets).
        HashSet<string> alive;
        try
        {
            alive = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dir in Directory.EnumerateDirectories(userRoot))
            {
                var leaf = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(leaf)) continue;
                if (!leaf.Equals("Desktop", StringComparison.OrdinalIgnoreCase) &&
                    !leaf.StartsWith("Desktop-Monitor", StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
                {
                    var name = Path.GetFileName(entry);
                    if (string.IsNullOrEmpty(name)) continue;
                    // Skip dotfile metadata (matches DesktopIconLayer's
                    // IsHiddenSettingsFile filter) so we don't keep a
                    // layout entry for something the icon layer
                    // intentionally hides.
                    if (name.StartsWith('.')) continue;
                    alive.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DesktopIconLayout] PruneOrphans enumerate failed: {ex.Message}");
            return;
        }

        lock (_gate)
        {
            if (_cache == null) return;
            // Copy keys first so we can mutate _cache during the walk.
            var stale = new List<string>();
            foreach (var key in _cache.Keys)
            {
                if (!alive.Contains(key)) stale.Add(key);
            }
            if (stale.Count == 0) return;
            foreach (var key in stale) _cache.Remove(key);
            _dirty = true;
            ScheduleFlushNoLock();
            Debug.WriteLine($"[DesktopIconLayout] Pruned {stale.Count} orphan layout entr{(stale.Count == 1 ? "y" : "ies")}.");
        }
    }

    /// <summary>
    /// If <paramref name="oldPath"/> lived on a desktop folder, transfers
    /// any saved position from its old file name onto the new file name
    /// (when <paramref name="newPath"/> also lives on a desktop folder).
    /// Used by the file explorer's rename path so a desktop icon keeps
    /// its spatial position after the user renames it from outside the
    /// desktop UI.
    /// </summary>
    public static void RenameIfOnDesktop(string oldPath, string newPath)
    {
        if (string.IsNullOrEmpty(oldPath) || string.IsNullOrEmpty(newPath)) return;
        bool oldOnDesk = IsOnDesktop(oldPath);
        bool newOnDesk = IsOnDesktop(newPath);
        if (!oldOnDesk && !newOnDesk) return;

        var oldName = Path.GetFileName(oldPath.TrimEnd(Path.DirectorySeparatorChar));
        var newName = Path.GetFileName(newPath.TrimEnd(Path.DirectorySeparatorChar));

        if (oldOnDesk && newOnDesk)
        {
            // Carry the old position forward under the new key.
            var saved = Get(oldName);
            if (saved != null) Save(newName, saved.X, saved.Y);
            Forget(oldName);
        }
        else if (oldOnDesk)
        {
            // Moved off the desktop entirely - just forget the old entry.
            Forget(oldName);
        }
        // newOnDesk only: nothing to do - a fresh icon arrives without a
        // saved position and DesktopIconLayer.AutoPlace will assign one.
    }

    /// <summary>
    /// True when <paramref name="fullPath"/> is directly inside a folder
    /// named "Desktop" or "Desktop-Monitor&lt;n&gt;" under the current
    /// user's home. Conservative on purpose: deep descendants (e.g. a
    /// file inside <c>~/Desktop/MyFolder/</c>) do NOT count - icons only
    /// render at the top level of a desktop folder.
    /// </summary>
    private static bool IsOnDesktop(string fullPath)
    {
        try
        {
            var user = UserManager.CurrentUser;
            if (user == null) return false;
            var parent = Path.GetDirectoryName(Path.GetFullPath(fullPath.TrimEnd(Path.DirectorySeparatorChar)));
            if (string.IsNullOrEmpty(parent)) return false;
            var userRoot = Path.GetFullPath(UserManager.GetUserFolder(user.Username));
            // Only top-level icons of a desktop folder count - so the
            // parent must live directly under the user home AND its
            // leaf name must be "Desktop" or "Desktop-Monitor*".
            var grand = Path.GetDirectoryName(parent);
            if (string.IsNullOrEmpty(grand)) return false;
            if (!string.Equals(grand.TrimEnd(Path.DirectorySeparatorChar),
                               userRoot.TrimEnd(Path.DirectorySeparatorChar),
                               StringComparison.OrdinalIgnoreCase))
                return false;
            var leaf = Path.GetFileName(parent);
            return leaf.Equals("Desktop", StringComparison.OrdinalIgnoreCase) ||
                   leaf.StartsWith("Desktop-Monitor", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Forces a synchronous flush. Called from sign-out / shutdown.</summary>
    public static void FlushNow()
    {
        lock (_gate)
        {
            _flushTimer?.Dispose();
            _flushTimer = null;
            if (!_dirty) return;
            FlushNoLock();
        }
    }

    private static void ScheduleFlushNoLock()
    {
        _flushTimer?.Dispose();
        _flushTimer = new System.Threading.Timer(_ =>
        {
            lock (_gate)
            {
                if (!_dirty) return;
                FlushNoLock();
            }
        }, null, FlushDebounceMs, System.Threading.Timeout.Infinite);
    }

    private static void EnsureLoaded()
    {
        var user = UserManager.CurrentUser;
        var username = user?.Username;
        lock (_gate)
        {
            if (_cache != null && string.Equals(_loadedForUsername, username, StringComparison.OrdinalIgnoreCase))
                return;

            _cache = new Dictionary<string, IconPosition>(StringComparer.Ordinal);
            _loadedForUsername = username;
            if (username == null) return;

            try
            {
                var path = GetStoragePath(username);
                if (!File.Exists(path)) return;
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, IconPosition>>(json);
                if (loaded != null)
                {
                    foreach (var kv in loaded) _cache[kv.Key] = kv.Value;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DesktopIconLayout] Load failed: {ex.Message}");
            }
        }
    }

    private static void FlushNoLock()
    {
        _dirty = false;
        if (_loadedForUsername == null || _cache == null) return;
        try
        {
            var path = GetStoragePath(_loadedForUsername);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_cache,
                new JsonSerializerOptions { WriteIndented = false }));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DesktopIconLayout] Flush failed: {ex.Message}");
        }
    }

    private static string GetStoragePath(string username) =>
        Path.Combine(UserManager.GetUserFolder(username), FileName);
}
