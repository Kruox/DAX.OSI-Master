using System.Collections.Generic;
using Avalonia.Controls;

namespace DAX.OSI.PluginSdk;

/// <summary>
/// Contract a single DAX.OSI application implements. The host treats every
/// returned <see cref="IDOSIApp"/> exactly like a built-in default app:
/// it gets a tile in the apps menu, it can be opened from the file
/// explorer when its <see cref="CanOpenFile"/> returns <c>true</c>, and
/// it owns its own <see cref="Window"/>-style chrome.
/// <para>
/// Apps MUST be cheap to construct - the host enumerates every available
/// app at apps-menu-build time. Heavy work (loading state, scanning the
/// file system, etc.) belongs inside <see cref="Activate"/> or later, NOT
/// in the parameterless constructor.
/// </para>
/// </summary>
public interface IDOSIApp
{
    /// <summary>Stable, file-system-safe id (e.g. <c>"dosi.ide"</c>).</summary>
    string Id { get; }

    /// <summary>Display title shown in the apps menu and the taskbar.</summary>
    string Title { get; }

    /// <summary>One-line description shown under the title in the apps menu.</summary>
    string Description { get; }

    /// <summary>
    /// Builds the small (≈26x26) glyph control rendered next to the title in
    /// the apps menu. Called every time the menu is rebuilt, so callers
    /// should NOT cache the result.
    /// </summary>
    Control BuildGlyph();

    /// <summary>
    /// Constructs a fresh application instance. Returned <see cref="Control"/>
    /// must be a <c>DOSIWindow</c> (from DOSI.CORE) - the host adds it to the
    /// active <c>WindowManager</c>. Returning anything else is an
    /// implementation error and the host will discard it.
    /// </summary>
    Control Activate();

    /// <summary>
    /// Whether this app claims ownership of files with the given extension.
    /// Extensions arrive in the form returned by <c>Path.GetExtension</c>
    /// (leading dot, original case). Comparisons should be ordinal-ignore-case.
    /// Return <c>false</c> from apps that have no file association.
    /// </summary>
    bool CanOpenFile(string extension);

    /// <summary>
    /// Opens an existing app instance against <paramref name="path"/>. The
    /// host calls this immediately after <see cref="Activate"/> when routing
    /// a double-click from the file explorer. Implementations should defer
    /// the actual open until the app is attached to the visual tree.
    /// </summary>
    void OpenPath(Control instance, string path);
}

/// <summary>
/// Discovery hook the host scans for in every plug-in DLL. Implement this
/// once per DLL and return one or more <see cref="IDOSIApp"/> instances.
/// <para>
/// Most plug-in DLLs return a single app. The list shape is here so a
/// "suite" DLL (think: Microsoft Office) can ship multiple related apps in
/// one assembly without forcing the host to load N separate files.
/// </para>
/// <para>
/// NAME CONTRACT - this interface is named <c>IDOSIAppPlugin</c> (NOT
/// <c>IDOSIAppProvider</c>) because the proprietary plug-in repo's source
/// already references that exact name. Renaming it here breaks every
/// existing plug-in DLL on disk, so don't unless you also coordinate
/// the plug-in repo.
/// </para>
/// </summary>
public interface IDOSIAppPlugin
{
    /// <summary>
    /// Returns every app this DLL contributes. Called once per host
    /// session whenever a user signs in (during AppLoader.LoadForUser);
    /// the returned sequence is snapshotted into a list so generators are
    /// fine.
    /// </summary>
    IEnumerable<IDOSIApp> GetApps();
}
