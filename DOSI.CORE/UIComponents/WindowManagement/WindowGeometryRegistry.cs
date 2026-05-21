using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using DOSI.CORE.UserManagement;

namespace DOSI.CORE.UIComponents.WindowManagement;

/// <summary>
/// Per-user persistence of window geometry (X / Y / Width / Height) keyed
/// by <see cref="DOSIWindow.Title"/>. Lets re-opening an app land on the
/// same monitor at the same place / size the user left it last time -
/// the single biggest window-management QOL win for a desktop OS.
/// <para>
/// STORAGE: a single JSON file at
/// <c>&lt;UserHome&gt;/.window-geometry.json</c>. One blob per user, all
/// records together; the registry is read once on first access and
/// flushed atomically on every <see cref="Save"/> call. The atomic write
/// goes via a <c>.tmp</c> sibling + <see cref="File.Move(string,string,bool)"/>
/// so a crash mid-flush never corrupts the file - either the old contents
/// or the new contents survive, never a half-written mix.
/// </para>
/// <para>
/// KEYS: window <see cref="DOSIWindow.Title"/>. We deliberately don't
/// key by app id (no such thing for built-ins) or by GUID (would need
/// every DOSIWindow to declare one). Title is what the user sees and
/// what they associate with "this kind of window", which is also what
/// they expect to see open in the same place. Title collisions are rare
/// and harmless - two windows with the same Title share a slot.
/// </para>
/// <para>
/// THREADING: every public member locks. Read returns a value-typed
/// snapshot. Save coalesces frequent writes (drag, resize) onto a single
/// debounced timer hand-off so we don't hammer the disk during a drag.
/// </para>
/// </summary>
public static class WindowGeometryRegistry
{
    private const string FileName = ".window-geometry.json";
    private const int FlushDebounceMs = 750;

    private static readonly object _gate = new();
    private static Dictionary<string, GeometryRecord>? _cache;
    private static string? _loadedForUsername;
    private static System.Threading.Timer? _flushTimer;
    private static bool _dirty;

    /// <summary>Restored geometry for one window, all coordinates in screen pixels.</summary>
    public sealed record GeometryRecord(double X, double Y, double Width, double Height);

    /// <summary>
    /// Returns the saved geometry for the window with the given title, or
    /// <c>null</c> if none has been recorded yet (the caller should fall
    /// back to its default position / size in that case).
    /// </summary>
    public static GeometryRecord? Get(string title)
    {
        if (string.IsNullOrEmpty(title)) return null;
        EnsureLoaded();
        lock (_gate)
        {
            return _cache != null && _cache.TryGetValue(title, out var r) ? r : null;
        }
    }

    /// <summary>
    /// Records geometry for the window with the given title and schedules
    /// a debounced flush to disk. Callers can fire this on every drag
    /// / resize tick without performance concern - actual disk I/O happens
    /// at most once per <see cref="FlushDebounceMs"/> ms.
    /// </summary>
    public static void Save(string title, double x, double y, double width, double height)
    {
        if (string.IsNullOrEmpty(title)) return;
        EnsureLoaded();

        lock (_gate)
        {
            if (_cache == null) return;
            _cache[title] = new GeometryRecord(x, y, width, height);
            _dirty = true;
            ScheduleFlushNoLock();
        }
    }

    /// <summary>Drops every saved record for the current user.</summary>
    public static void Reset()
    {
        lock (_gate)
        {
            _cache?.Clear();
            _dirty = true;
            ScheduleFlushNoLock();
        }
    }

    /// <summary>
    /// Forces a synchronous flush right now (bypassing the debounce).
    /// Called from sign-out / shutdown so the latest geometry isn't lost
    /// to the debounce window.
    /// </summary>
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

            _cache = new Dictionary<string, GeometryRecord>(StringComparer.Ordinal);
            _loadedForUsername = username;

            if (username == null) return;

            try
            {
                var path = GetStoragePath(username);
                if (!File.Exists(path)) return;
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, GeometryRecord>>(json);
                if (loaded != null)
                {
                    foreach (var kv in loaded) _cache[kv.Key] = kv.Value;
                }
            }
            catch (Exception ex)
            {
                // Corrupt file or perms issue. Start fresh - we'll
                // overwrite on the next Save.
                Debug.WriteLine($"[WindowGeometryRegistry] Load failed: {ex.Message}");
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
            // Atomic replace - either the old or the new file always
            // exists, never a half-written one.
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowGeometryRegistry] Flush failed: {ex.Message}");
        }
    }

    private static string GetStoragePath(string username) =>
        Path.Combine(UserManager.GetUserFolder(username), FileName);
}
