using System;

namespace DOSI.CORE.Apps;

/// <summary>
/// Process-wide single-entry clipboard for files and folders. Used by
/// <c>DOSIFileExplorer</c> (Ctrl+C / Ctrl+X / Ctrl+V) and the desktop
/// context menu so the user can lift a file in one explorer window and
/// paste it in another - or onto the desktop - without going through the
/// host OS clipboard.
/// <para>
/// HOST OS CLIPBOARD: deliberately NOT used. Avalonia's <c>IClipboard</c>
/// works with text and storage items, but cross-process file moves /
/// copies require special data formats per platform (CF_HDROP on Windows,
/// <c>x-special/gnome-copied-files</c> on Linux, NSPasteboard URLs on
/// macOS). The user is operating ENTIRELY inside DAX.OSI's sandboxed
/// home directory anyway, so a process-local clipboard is both simpler
/// and safer (it can never accidentally sync sensitive paths to the OS).
/// </para>
/// <para>
/// THREADING: every method locks. Reads return immutable copies of the
/// path (string is immutable so no defensive-copy needed).
/// </para>
/// </summary>
public static class FileClipboard
{
    /// <summary>The two operations a paste can complete.</summary>
    public enum Mode
    {
        /// <summary>Source remains on disk; paste copies.</summary>
        Copy,
        /// <summary>Source is removed on paste (move semantics).</summary>
        Cut
    }

    private static readonly object _gate = new();
    private static System.Collections.Generic.List<string> _paths = new();
    private static Mode _mode;

    /// <summary>Raised whenever the clipboard contents change.</summary>
    public static event EventHandler? Changed;

    /// <summary>
    /// Absolute path of the first staged item, or <c>null</c> when empty.
    /// Kept for back-compat with single-path consumers (desktop paste,
    /// status-bar previews). Multi-path consumers should read <see cref="Paths"/>.
    /// </summary>
    public static string? Path
    {
        get { lock (_gate) return _paths.Count == 0 ? null : _paths[0]; }
    }

    /// <summary>Snapshot of every staged path. Always safe to enumerate.</summary>
    public static System.Collections.Generic.IReadOnlyList<string> Paths
    {
        get { lock (_gate) return _paths.ToArray(); }
    }

    /// <summary>Number of staged items (0 when empty).</summary>
    public static int Count
    {
        get { lock (_gate) return _paths.Count; }
    }

    /// <summary>Operation that will be performed on the next paste.</summary>
    public static Mode CurrentMode
    {
        get { lock (_gate) return _mode; }
    }

    /// <summary><c>true</c> if at least one path is staged for paste.</summary>
    public static bool HasContent
    {
        get { lock (_gate) return _paths.Count > 0; }
    }

    /// <summary>Stages <paramref name="path"/> for a copy on next paste.</summary>
    public static void Copy(string path) => SetSingle(path, Mode.Copy);

    /// <summary>Stages <paramref name="path"/> for a move on next paste.</summary>
    public static void Cut(string path) => SetSingle(path, Mode.Cut);

    /// <summary>Stages multiple paths for a copy on next paste.</summary>
    public static void CopyMany(System.Collections.Generic.IEnumerable<string> paths) => SetMany(paths, Mode.Copy);

    /// <summary>Stages multiple paths for a move on next paste.</summary>
    public static void CutMany(System.Collections.Generic.IEnumerable<string> paths) => SetMany(paths, Mode.Cut);

    /// <summary>Empties the clipboard.</summary>
    public static void Clear()
    {
        bool changed;
        lock (_gate)
        {
            changed = _paths.Count > 0;
            _paths = new System.Collections.Generic.List<string>();
            _mode = Mode.Copy;
        }
        if (changed) Changed?.Invoke(null, EventArgs.Empty);
    }

    private static void SetSingle(string path, Mode mode)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (_gate)
        {
            _paths = new System.Collections.Generic.List<string> { path };
            _mode = mode;
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static void SetMany(System.Collections.Generic.IEnumerable<string> paths, Mode mode)
    {
        if (paths == null) return;
        var list = new System.Collections.Generic.List<string>();
        foreach (var p in paths)
            if (!string.IsNullOrWhiteSpace(p)) list.Add(p);
        if (list.Count == 0) return;
        lock (_gate)
        {
            _paths = list;
            _mode = mode;
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
