using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using DAX.OSI.PluginSdk;
using DOSI.CORE.UserManagement;

namespace DOSI.CORE.Apps;

/// <summary>
/// Discovers and loads <see cref="IDOSIAppPlugin"/> assemblies from the
/// <b>signed-in user's</b> <c>Applications/</c> folder. Each user owns a
/// private set of installed applications: dropping a DLL into
/// <c>&lt;UserHome&gt;/Applications/</c> makes it available to that account
/// and that account only.
/// <para>
/// Lifecycle:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="LoadForUser"/> is called once per
///   sign-in. Every <see cref="IDOSIApp"/> the user's DLLs publish is
///   registered into <see cref="LoadedAppRegistry"/>.</description></item>
///   <item><description><see cref="UnloadAll"/> is called on sign-out so
///   the next user starts from an empty registry and gets only their own
///   apps.</description></item>
/// </list>
/// <para>
/// Each application DLL is loaded into its own <see cref="AssemblyLoadContext"/>
/// with a resolver that redirects <c>DOSI.CORE</c>, <c>DAX.OSI.PluginSdk</c>,
/// and any Avalonia / BCL assembly back to the host's already-loaded copy.
/// Without that redirect a private DLL would load a SECOND copy of those
/// assemblies into its context and the cast
/// <c>provider is IDOSIAppPlugin</c> would fail (different
/// <see cref="Type"/> identities for "the same" interface).
/// </para>
/// <para>
/// The contexts are NOT collectible: applications live for the entire
/// session of the user that signed them in (their windows hold delegates
/// back into the loaded assemblies), so unload would never actually free
/// anything until the user signs out / the host shuts down. Default,
/// non-collectible ALCs are cheaper to construct and have no runtime
/// overhead penalty.
/// </para>
/// </summary>
public static class AppLoader
{
    /// <summary>
    /// Folder name (relative to a user's home directory) scanned at
    /// sign-in. Created on first sign-in if absent so users can drop DLLs
    /// in without manually creating the folder.
    /// </summary>
    public const string ApplicationsFolderName = "Applications";

    private static string? _loadedForUsername;
    private static readonly List<AssemblyLoadContext> _activeContexts = new();
    private static readonly object _gate = new();

    // Per-user watcher: detects DLLs being added / removed / modified in
    // the active Applications folder. We DON'T attempt true hot-reload
    // (the load contexts are non-collectible and the apps may have live
    // windows holding delegates back into them); instead we surface a
    // toast so the user knows a sign-out / sign-in is needed to pick up
    // the change.
    private static FileSystemWatcher? _watcher;
    private static System.Threading.Timer? _watcherDebounce;

    /// <summary>
    /// Raised on the thread-pool when a change is detected in the active
    /// user's Applications folder. UI consumers should marshal to the
    /// dispatcher before touching visuals.
    /// </summary>
    public static event Action<string>? ApplicationsFolderChanged;

    /// <summary>
    /// Absolute path to <paramref name="user"/>'s <c>Applications/</c>
    /// folder. Useful for diagnostics and for file-explorer integrations
    /// that want to surface "open Applications folder".
    /// </summary>
    public static string GetApplicationsFolderPath(DOSIUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Path.Combine(UserManager.GetUserFolder(user.Username), ApplicationsFolderName);
    }

    /// <summary>
    /// Scans <paramref name="user"/>'s Applications folder for DLLs and
    /// registers every <see cref="IDOSIApp"/> they publish into
    /// <see cref="LoadedAppRegistry"/>. Idempotent for the same user; if a
    /// different user is currently loaded, the previous set is unloaded
    /// first.
    /// </summary>
    public static void LoadForUser(DOSIUser user)
    {
        if (user == null) return;

        lock (_gate)
        {
            if (string.Equals(_loadedForUsername, user.Username, StringComparison.OrdinalIgnoreCase))
                return;

            // Different (or no) user previously loaded - reset first.
            UnloadAllNoLock();

            var appsRoot = GetApplicationsFolderPath(user);
            try
            {
                if (!Directory.Exists(appsRoot))
                {
                    Directory.CreateDirectory(appsRoot);
                    _loadedForUsername = user.Username;
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppLoader] Could not access '{appsRoot}': {ex.Message}");
                return;
            }

            IEnumerable<string> dllPaths;
            try
            {
                dllPaths = EnumerateAppDlls(appsRoot);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppLoader] Enumerate failed: {ex.Message}");
                return;
            }

            foreach (var dll in dllPaths)
            {
                try { LoadOneNoLock(dll); }
                catch (Exception ex)
                {
                    // A single bad app must not break sign-in.
                    Debug.WriteLine($"[AppLoader] Skipping '{Path.GetFileName(dll)}': {ex.Message}");
                }
            }

            _loadedForUsername = user.Username;
            StartWatcherNoLock(appsRoot);
        }
    }

    /// <summary>
    /// Spins up a <see cref="FileSystemWatcher"/> on the active user's
    /// Applications folder. We watch DLL adds / removes / renames in both
    /// the root (legacy flat layout) and any subfolder (per-app folder
    /// layout). Bursts of FS events from a single copy / move are
    /// coalesced through a 500 ms debounce timer so the user gets ONE
    /// toast even when their tooling triggers four FS events for one
    /// logical change.
    /// </summary>
    private static void StartWatcherNoLock(string appsRoot)
    {
        StopWatcherNoLock();

        try
        {
            _watcher = new FileSystemWatcher(appsRoot)
            {
                Filter = "*.dll",
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _watcher.Created += OnWatcherEvent;
            _watcher.Deleted += OnWatcherEvent;
            _watcher.Renamed += OnWatcherEvent;
            _watcher.Changed += OnWatcherEvent;
        }
        catch (Exception ex)
        {
            // Some filesystems (network shares, fuse mounts) reject
            // watcher creation. Surface to the debug log and continue;
            // the sign-out -> sign-in cycle still picks up changes.
            Debug.WriteLine($"[AppLoader] Watcher init failed: {ex.Message}");
            _watcher = null;
        }
    }

    private static void StopWatcherNoLock()
    {
        if (_watcher != null)
        {
            try { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); }
            catch { /* best-effort */ }
            _watcher = null;
        }
        _watcherDebounce?.Dispose();
        _watcherDebounce = null;
    }

    private static void OnWatcherEvent(object sender, FileSystemEventArgs e)
    {
        // Debounce to coalesce burst events. Use a Timer (not async/await)
        // because we explicitly want the latest fire to win over earlier
        // pending fires.
        _watcherDebounce?.Dispose();
        _watcherDebounce = new System.Threading.Timer(_ =>
        {
            try { ApplicationsFolderChanged?.Invoke(e.FullPath); } catch { }
        }, null, 500, System.Threading.Timeout.Infinite);
    }

    /// <summary>
    /// Enumerates every application DLL the loader should try to load for
    /// a user, supporting BOTH on-disk layouts:
    /// <list type="bullet">
    ///   <item><description><b>Per-app folder (preferred):</b>
    ///   <c>Applications/&lt;AppName&gt;/&lt;AppName&gt;.dll</c>. The folder
    ///   name equals the main DLL name (without extension); any sibling
    ///   files in the same folder are private dependencies the
    ///   AppLoadContext resolver picks up automatically. This is the
    ///   layout the Application Manager and the seeder produce.</description></item>
    ///   <item><description><b>Legacy flat (back-compat):</b>
    ///   <c>Applications/&lt;AppName&gt;.dll</c>, with optional sibling
    ///   files at the same level. Older installs still work without a
    ///   manual migration step.</description></item>
    /// </list>
    /// Per-app folders win when both layouts contain the same DLL name -
    /// LoadedAppRegistry's first-id-wins de-duplication then drops the
    /// flat copy as a no-op.
    /// <para>
    /// Returns an empty sequence if <paramref name="appsRoot"/> doesn't
    /// exist, so callers can use this on a fresh user account without a
    /// pre-flight check.
    /// </para>
    /// </summary>
    public static IEnumerable<string> EnumerateAppDlls(string appsRoot)
    {
        if (string.IsNullOrEmpty(appsRoot) || !Directory.Exists(appsRoot))
            yield break;

        // Per-app subfolder layout: Applications/<AppName>/<AppName>.dll.
        // Folders that don't contain a matching DLL are silently ignored
        // so users can keep e.g. "Backups/" or "Old/" alongside their
        // installed apps without confusing the loader.
        foreach (var folder in Directory.EnumerateDirectories(appsRoot))
        {
            var folderName = Path.GetFileName(folder);
            if (string.IsNullOrEmpty(folderName)) continue;
            var candidate = Path.Combine(folder, folderName + ".dll");
            if (File.Exists(candidate)) yield return candidate;
        }

        // Legacy flat layout: Applications/<AppName>.dll.
        foreach (var dll in Directory.EnumerateFiles(appsRoot, "*.dll", SearchOption.TopDirectoryOnly))
            yield return dll;
    }

    /// <summary>
    /// Drops every app the previous user loaded from
    /// <see cref="LoadedAppRegistry"/>. Called automatically on sign-out
    /// via <see cref="UserManager.CurrentUserChanged"/>; safe to call
    /// repeatedly.
    /// </summary>
    public static void UnloadAll()
    {
        lock (_gate) { UnloadAllNoLock(); }
    }

    private static void UnloadAllNoLock()
    {
        StopWatcherNoLock();
        LoadedAppRegistry.Clear();
        // We deliberately don't AssemblyLoadContext.Unload() the contexts -
        // see the class-level remarks. Just drop our refs and let the GC
        // figure it out at process exit.
        _activeContexts.Clear();
        _loadedForUsername = null;
    }

    private static void LoadOneNoLock(string dllPath)
    {
        // Each application gets its own context so its private dependencies
        // (anything sitting next to it in the user's Applications/ folder)
        // don't pollute the host or other applications. The resolver below
        // unifies common assemblies back to the host so type identity is
        // preserved across the boundary.
        var context = new AppLoadContext(dllPath);
        _activeContexts.Add(context);

        // IMPORTANT: load the DLL (and its sidecar .pdb if present) from a
        // MemoryStream rather than via LoadFromAssemblyPath. On Windows the
        // path-based overload memory-maps the file and holds an OS lock for
        // the lifetime of the assembly - which is the entire user session
        // because our context is non-collectible. That lock is what makes
        // an in-session uninstall ("Application Manager" -> Uninstall) fail
        // with "Access to the path ... is denied" on Windows. Reading the
        // bytes up-front + handing them to LoadFromStream closes the OS
        // handle immediately, leaving the file free to be deleted.
        Assembly asm;
        try
        {
            var dllBytes = File.ReadAllBytes(dllPath);
            var pdbPath = Path.ChangeExtension(dllPath, ".pdb");
            byte[]? pdbBytes = null;
            if (File.Exists(pdbPath))
            {
                try { pdbBytes = File.ReadAllBytes(pdbPath); }
                catch { /* PDB optional - missing or locked symbols just lose stack-trace fidelity */ }
            }

            using var dllStream = new MemoryStream(dllBytes, writable: false);
            if (pdbBytes != null)
            {
                using var pdbStream = new MemoryStream(pdbBytes, writable: false);
                asm = context.LoadFromStream(dllStream, pdbStream);
            }
            else
            {
                asm = context.LoadFromStream(dllStream);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppLoader] Could not load '{Path.GetFileName(dllPath)}': {ex.Message}");
            return;
        }

        var providerTypes = asm.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true } &&
                        typeof(IDOSIAppPlugin).IsAssignableFrom(t));

        foreach (var t in providerTypes)
        {
            IDOSIAppPlugin provider;
            try
            {
                if (Activator.CreateInstance(t) is not IDOSIAppPlugin p) continue;
                provider = p;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppLoader] '{t.FullName}' ctor threw: {ex.Message}");
                continue;
            }

            IEnumerable<IDOSIApp> apps;
            try { apps = provider.GetApps(); }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppLoader] '{t.FullName}'.GetApps threw: {ex.Message}");
                continue;
            }

            foreach (var app in apps)
            {
                if (app == null) continue;
                LoadedAppRegistry.Register(app, dllPath);
                Debug.WriteLine($"[AppLoader] Registered '{app.Id}' from {Path.GetFileName(dllPath)}");
            }
        }
    }

    private sealed class AppLoadContext : AssemblyLoadContext
    {
        private readonly string _appRoot;

        public AppLoadContext(string appPath)
            : base(name: $"DosiApp:{Path.GetFileNameWithoutExtension(appPath)}", isCollectible: false)
        {
            _appRoot = Path.GetDirectoryName(appPath) ?? AppContext.BaseDirectory;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // 1. Prefer the host's already-loaded assembly so type identity
            //    matches across the application boundary. This is what makes
            //    `provider is IDOSIAppPlugin` actually evaluate true and
            //    what lets the application return a DOSIWindow the host can
            //    hand to its WindowManager.
            var hostMatch = AssemblyLoadContext.Default.Assemblies
                .FirstOrDefault(a => a.GetName().Name == assemblyName.Name);
            if (hostMatch != null) return hostMatch;

            // 2. Fall back to anything sitting next to the application DLL.
            //    Apps that ship their own private dependency (e.g. a native
            //    interop library) drop it into the user's Applications/
            //    folder alongside the main DLL. Loaded from a MemoryStream
            //    for the same uninstall-while-running reason described in
            //    AppLoader.LoadOneNoLock.
            if (assemblyName.Name == null) return null;
            var sibling = Path.Combine(_appRoot, assemblyName.Name + ".dll");
            if (File.Exists(sibling))
            {
                try
                {
                    var bytes = File.ReadAllBytes(sibling);
                    using var stream = new MemoryStream(bytes, writable: false);
                    return LoadFromStream(stream);
                }
                catch { /* fall through to default resolution */ }
            }

            return null; // let the default ALC try
        }
    }
}
