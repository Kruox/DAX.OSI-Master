using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DOSI.CORE.UserManagement;

/// <summary>
/// Per-user trash bin. Replaces destructive <c>Directory.Delete</c> /
/// <c>File.Delete</c> calls with a soft-delete that moves the entry into
/// <c>&lt;UserHome&gt;/.trash/</c> and records its original location so it
/// can be restored on demand.
///
/// Layout:
/// <code>
/// &lt;UserHome&gt;/.trash/
///     manifest.json          (entries + original paths + delete times)
///     items/&lt;id&gt;/&lt;name&gt;  (the actual moved file or folder)
/// </code>
///
/// The manifest is rewritten in full on every change. Trash sizes are
/// typically &lt;100 entries so this stays cheap; the alternative (append +
/// compact) buys complexity we don't need yet.
/// </summary>
public static class FileTrash
{
    private const string TrashFolderName = ".trash";
    private const string ManifestFileName = "manifest.json";
    private const string ItemsFolderName  = "items";

    /// <summary>
    /// Fires whenever the trash contents change (item moved in, restored,
    /// or permanently deleted). Consumers (e.g. a Trash sidebar entry that
    /// shows a count) subscribe to refresh themselves.
    /// </summary>
    public static event EventHandler? Changed;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Returns the trash root folder for <paramref name="user"/> (created on access).</summary>
    public static string GetTrashRoot(DOSIUser user)
    {
        var root = Path.Combine(UserManager.GetUserFolder(user.Username), TrashFolderName);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, ItemsFolderName));
        return root;
    }

    /// <summary>
    /// Returns true if <paramref name="path"/> is inside the trash root of
    /// some user. Callers use this to suppress "soft-delete" semantics for
    /// paths that are already in the trash (e.g. Empty Trash should permanently
    /// delete, not loop back through the trash).
    /// </summary>
    public static bool IsInsideTrash(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            var full = Path.GetFullPath(path);
            // Match on the literal folder segment so we don't false-positive
            // on a folder that happens to be named e.g. ".trashbin".
            return full.Contains(Path.DirectorySeparatorChar + TrashFolderName + Path.DirectorySeparatorChar,
                                 StringComparison.OrdinalIgnoreCase)
                || full.EndsWith(Path.DirectorySeparatorChar + TrashFolderName, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Moves <paramref name="sourcePath"/> into the user's trash. Generates
    /// a unique id, records the original location + delete time in the
    /// manifest, and returns the new path inside the trash (or null on
    /// failure). The caller is responsible for refreshing any UI that was
    /// showing the old path.
    /// </summary>
    public static string? Send(DOSIUser user, string sourcePath)
    {
        if (string.IsNullOrEmpty(sourcePath)) return null;
        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath)) return null;
        if (IsInsideTrash(sourcePath)) return null;

        var root = GetTrashRoot(user);
        var entries = LoadManifest(root);

        var id = Guid.NewGuid().ToString("N").Substring(0, 12);
        var name = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(name)) name = "item";

        var entryDir = Path.Combine(root, ItemsFolderName, id);
        Directory.CreateDirectory(entryDir);
        var target = Path.Combine(entryDir, name);

        try
        {
            if (Directory.Exists(sourcePath))
                Directory.Move(sourcePath, target);
            else
                File.Move(sourcePath, target);
        }
        catch
        {
            // Clean up the empty entry folder so we don't leave litter.
            try { Directory.Delete(entryDir, recursive: true); } catch { }
            return null;
        }

        entries.Add(new TrashEntry
        {
            Id = id,
            OriginalPath = Path.GetFullPath(sourcePath),
            Name = name,
            IsDirectory = Directory.Exists(target),
            DeletedAtUtc = DateTime.UtcNow
        });
        SaveManifest(root, entries);
        Changed?.Invoke(null, EventArgs.Empty);
        return target;
    }

    /// <summary>
    /// Returns every entry currently in the trash, ordered newest-first.
    /// Stale entries (whose backing item has been removed out-of-band) are
    /// pruned from the manifest before returning.
    /// </summary>
    public static IReadOnlyList<TrashEntry> List(DOSIUser user)
    {
        var root = GetTrashRoot(user);
        var entries = LoadManifest(root);
        var alive = entries.Where(e =>
        {
            var p = ResolveItemPath(root, e);
            return File.Exists(p) || Directory.Exists(p);
        }).ToList();
        if (alive.Count != entries.Count) SaveManifest(root, alive);
        return alive.OrderByDescending(e => e.DeletedAtUtc).ToList();
    }

    /// <summary>
    /// Restores the entry with <paramref name="id"/> to its original path.
    /// If the original location is occupied, restores alongside it with a
    /// numbered suffix ("foo (2).txt"). Returns the final restore path on
    /// success, null on failure.
    /// </summary>
    public static string? Restore(DOSIUser user, string id)
    {
        var root = GetTrashRoot(user);
        var entries = LoadManifest(root);
        var entry = entries.FirstOrDefault(e => e.Id == id);
        if (entry == null) return null;

        var src = ResolveItemPath(root, entry);
        if (!File.Exists(src) && !Directory.Exists(src))
        {
            // Backing item is gone - drop the stale manifest entry too.
            entries.Remove(entry);
            SaveManifest(root, entries);
            return null;
        }

        var dst = ChooseUniqueDestination(entry.OriginalPath);
        try
        {
            var parent = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            if (Directory.Exists(src)) Directory.Move(src, dst);
            else                       File.Move(src, dst);
        }
        catch { return null; }

        // Wipe the now-empty per-entry folder + manifest row.
        try { Directory.Delete(Path.Combine(root, ItemsFolderName, entry.Id), recursive: true); } catch { }
        entries.Remove(entry);
        SaveManifest(root, entries);
        Changed?.Invoke(null, EventArgs.Empty);
        return dst;
    }

    /// <summary>
    /// Permanently deletes the entry with <paramref name="id"/>. Returns
    /// true on success. Used by "Empty Trash" and by the trash view's
    /// per-row Delete-Forever menu item.
    /// </summary>
    public static bool DeleteForever(DOSIUser user, string id)
    {
        var root = GetTrashRoot(user);
        var entries = LoadManifest(root);
        var entry = entries.FirstOrDefault(e => e.Id == id);
        if (entry == null) return false;

        var entryDir = Path.Combine(root, ItemsFolderName, entry.Id);
        try
        {
            if (Directory.Exists(entryDir)) Directory.Delete(entryDir, recursive: true);
        }
        catch { return false; }
        entries.Remove(entry);
        SaveManifest(root, entries);
        Changed?.Invoke(null, EventArgs.Empty);
        return true;
    }

    /// <summary>Empties every entry. Best-effort: skips any that fail to delete.</summary>
    public static int EmptyAll(DOSIUser user)
    {
        var entries = List(user).ToList();
        int removed = 0;
        foreach (var e in entries)
        {
            if (DeleteForever(user, e.Id)) removed++;
        }
        return removed;
    }

    /// <summary>
    /// User preference key for the trash auto-empty retention policy
    /// (in days). 0 / missing = retain forever (the historical default).
    /// </summary>
    public const string AutoEmptyDaysPreferenceKey = "trash.autoEmptyDays";

    /// <summary>
    /// Sweeps the user's trash and permanently deletes any entry older
    /// than <paramref name="retentionDays"/>. No-op when the retention
    /// is &lt;= 0 (the "keep forever" sentinel). Safe to call from any
    /// thread - each delete goes through the existing per-entry path
    /// which holds the manifest write internally. Returns the number of
    /// entries swept so callers can surface a status line.
    /// </summary>
    public static int SweepOlderThan(DOSIUser user, int retentionDays)
    {
        if (retentionDays <= 0) return 0;
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        var stale = List(user).Where(e => e.DeletedAtUtc < cutoff).ToList();
        int removed = 0;
        foreach (var e in stale)
        {
            if (DeleteForever(user, e.Id)) removed++;
        }
        return removed;
    }

    /// <summary>
    /// Reads <see cref="AutoEmptyDaysPreferenceKey"/> from the user's
    /// preferences and runs <see cref="SweepOlderThan"/> against it. The
    /// boot / sign-in path calls this so retention is enforced lazily
    /// (no background timer needed - the trash only grows when the user
    /// is signed in, and we sweep on every sign-in). Best-effort.
    /// </summary>
    public static int SweepUsingUserPreference(DOSIUser user)
    {
        if (user == null) return 0;
        try
        {
            if (!user.Preferences.TryGetValue(AutoEmptyDaysPreferenceKey, out var raw)) return 0;
            if (!int.TryParse(raw, out var days) || days <= 0) return 0;
            return SweepOlderThan(user, days);
        }
        catch { return 0; }
    }

    /// <summary>Absolute path of the file/folder backing this trash entry.</summary>
    public static string ResolveItemPath(DOSIUser user, TrashEntry entry)
        => ResolveItemPath(GetTrashRoot(user), entry);

    private static string ResolveItemPath(string root, TrashEntry entry)
        => Path.Combine(root, ItemsFolderName, entry.Id, entry.Name);

    private static List<TrashEntry> LoadManifest(string root)
    {
        var path = Path.Combine(root, ManifestFileName);
        if (!File.Exists(path)) return new List<TrashEntry>();
        try
        {
            var json = File.ReadAllText(path);
            var list = JsonSerializer.Deserialize<List<TrashEntry>>(json, JsonOpts);
            return list ?? new List<TrashEntry>();
        }
        catch { return new List<TrashEntry>(); }
    }

    private static void SaveManifest(string root, List<TrashEntry> entries)
    {
        var path = Path.Combine(root, ManifestFileName);
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(entries, JsonOpts));
        }
        catch { /* best-effort; manifest drift just means stale entries get pruned later */ }
    }

    private static string ChooseUniqueDestination(string desired)
    {
        if (!File.Exists(desired) && !Directory.Exists(desired)) return desired;
        var dir = Path.GetDirectoryName(desired) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(desired);
        var ext  = Path.GetExtension(desired);
        for (int i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        return desired;
    }
}

/// <summary>
/// Single row in the trash manifest. Mutable for JSON round-tripping.
/// </summary>
public sealed class TrashEntry
{
    public string Id { get; set; } = string.Empty;
    public string OriginalPath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public DateTime DeletedAtUtc { get; set; }
}
