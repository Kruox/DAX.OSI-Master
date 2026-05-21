using System;
using System.Collections.Generic;
using System.Linq;
using DAX.OSI.PluginSdk;

namespace DOSI.CORE.Apps;

/// <summary>
/// Process-wide registry of every application loaded by
/// <see cref="AppLoader"/> for the currently signed-in user. The host's
/// apps menu and file-explorer integration query this registry instead of
/// hard-coding a list of built-in app types, so any application DLL
/// dropped into the user's <c>Applications/</c> folder lights up
/// automatically.
/// </summary>
public static class LoadedAppRegistry
{
    private static readonly List<IDOSIApp> _apps = new();
    // Maps a normalized DLL path -> the apps that DLL contributed. Lets the
    // "Application Manager" remove an entry from the apps menu the moment
    // its DLL is deleted, without needing to wait for the next sign-in.
    private static readonly Dictionary<string, List<IDOSIApp>> _appsByDll =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _gate = new();

    /// <summary>Raised whenever an app is added to the registry.</summary>
    public static event EventHandler? AppsChanged;

    /// <summary>Snapshot of every registered app, in registration order.</summary>
    public static IReadOnlyList<IDOSIApp> All
    {
        get { lock (_gate) return _apps.ToArray(); }
    }

    /// <summary>
    /// Adds <paramref name="app"/> to the registry, tracking which DLL
    /// contributed it so <see cref="UnregisterByDllPath"/> can remove it
    /// when the user uninstalls. Duplicate ids are silently ignored
    /// (first-registered wins) so a stray DLL re-load can't shadow a real
    /// application.
    /// </summary>
    public static void Register(IDOSIApp app, string? sourceDllPath = null)
    {
        if (app == null) return;
        lock (_gate)
        {
            if (_apps.Any(a => string.Equals(a.Id, app.Id, StringComparison.OrdinalIgnoreCase)))
                return;
            _apps.Add(app);

            if (!string.IsNullOrEmpty(sourceDllPath))
            {
                if (!_appsByDll.TryGetValue(sourceDllPath, out var list))
                {
                    list = new List<IDOSIApp>();
                    _appsByDll[sourceDllPath] = list;
                }
                list.Add(app);
            }
        }
        AppsChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Removes every app that was registered with the given
    /// <paramref name="sourceDllPath"/>. Called by the Application
    /// Manager immediately after deleting an app DLL so the apps menu
    /// updates without requiring sign-out / sign-in.
    /// <para>
    /// The underlying CLR <c>Assembly</c> stays loaded (our
    /// <see cref="AppLoader"/> contexts are non-collectible), but with no
    /// <see cref="IDOSIApp"/> instance left in the registry the host has
    /// no way to invoke it again.
    /// </para>
    /// </summary>
    public static bool UnregisterByDllPath(string sourceDllPath)
    {
        if (string.IsNullOrEmpty(sourceDllPath)) return false;
        bool changed = false;
        lock (_gate)
        {
            if (_appsByDll.TryGetValue(sourceDllPath, out var list))
            {
                foreach (var app in list)
                {
                    if (_apps.Remove(app)) changed = true;
                }
                _appsByDll.Remove(sourceDllPath);
            }
        }
        if (changed) AppsChanged?.Invoke(null, EventArgs.Empty);
        return changed;
    }

    /// <summary>
    /// Drops every registered app. Called by <see cref="AppLoader.UnloadAll"/>
    /// on sign-out so the next user starts from a clean slate.
    /// </summary>
    public static void Clear()
    {
        bool changed;
        lock (_gate)
        {
            changed = _apps.Count > 0;
            _apps.Clear();
            _appsByDll.Clear();
        }
        if (changed) AppsChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Returns the first registered app whose <see cref="IDOSIApp.CanOpenFile"/>
    /// claims the given <paramref name="extension"/>, or <c>null</c> if none
    /// match. <paramref name="extension"/> arrives in the form returned by
    /// <c>Path.GetExtension</c> (leading dot, original case).
    /// </summary>
    public static IDOSIApp? FindForFile(string extension)
    {
        if (string.IsNullOrEmpty(extension)) return null;
        lock (_gate)
        {
            foreach (var app in _apps)
            {
                try { if (app.CanOpenFile(extension)) return app; }
                catch { /* an app that throws here is broken; skip it */ }
            }
        }
        return null;
    }
}
