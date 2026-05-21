using System;
using System.Diagnostics;
using System.IO;
using DOSI.CORE.UserManagement;

namespace DOSI.CORE.Apps;

/// <summary>
/// Seeds a freshly-created user account with the default set of pre-
/// installed applications (currently: the DOSI IDE). Runs at most ONCE
/// per user account, gated by a small stamp file in the user's home
/// directory.
///
/// <para>
/// DESIGN
/// </para>
/// The seed source is the host's <c>&lt;executable&gt;/Plugins/</c> folder
/// - the same folder the proprietary DAX.OSI.IDE.Plugin's MSBuild target
/// drops its DLL into. We copy from there into
/// <c>&lt;UserHome&gt;/Applications/</c> so the per-user
/// <see cref="AppLoader"/> picks the apps up exactly the same way it
/// would a manually-installed DLL. No special-casing: a seeded app and a
/// hand-installed app are indistinguishable to the rest of the system.
///
/// <para>
/// LIFECYCLE
/// </para>
/// <list type="bullet">
///   <item><description>First sign-in for a user: stamp absent →
///   copy every seed DLL → write stamp.</description></item>
///   <item><description>Subsequent sign-ins: stamp present →
///   skip entirely. The user is in charge of their Applications folder
///   from this point on.</description></item>
///   <item><description>User deletes the IDE DLL but keeps the stamp:
///   we deliberately do NOT reinstall. The intent is "you can remove
///   it if you don't want it" - constantly putting it back would be
///   user-hostile.</description></item>
///   <item><description>Power-user wants the seed re-run: delete the
///   stamp file and sign in again.</description></item>
/// </list>
///
/// <para>
/// FAILURE MODES
/// </para>
/// Every step is best-effort. If the host's Plugins folder doesn't exist
/// yet (proprietary plug-in hasn't been built locally), we still write
/// the stamp so we don't keep re-scanning every sign-in - the user can
/// always sideload the DLL later. If a copy throws (file in use, perms),
/// we log and continue with the rest of the seed list.
/// </summary>
public static class DefaultAppSeeder
{
    /// <summary>
    /// Stamp file written into the user's home folder once seeding has
    /// run. Name is intentionally hidden-style on Unix and "looks
    /// internal" on Windows so it doesn't clutter the user's File
    /// Explorer view.
    /// </summary>
    private const string StampFileName = ".dosi-seeded-apps";

    /// <summary>
    /// Filenames inside the host's <c>Plugins/</c> folder that should
    /// be auto-installed on first sign-in. Adding a new entry here will
    /// retroactively seed it for any user whose stamp pre-dates the new
    /// entry only if you also bump <see cref="StampFileName"/> - which
    /// is intentional friction so we don't surprise existing users with
    /// new defaults.
    /// </summary>
    private static readonly string[] _seedDllNames =
    {
        "DAX.OSI.IDE.Plugin.dll",
    };

    /// <summary>
    /// Idempotent seed step. Safe to call on every sign-in - subsequent
    /// calls for the same user are a single file-exists check.
    /// </summary>
    public static void SeedIfNeeded(DOSIUser user)
    {
        if (user == null) return;

        string userHome;
        try { userHome = UserManager.GetUserFolder(user.Username); }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DefaultAppSeeder] Could not resolve home for '{user.Username}': {ex.Message}");
            return;
        }

        var stampPath = Path.Combine(userHome, StampFileName);
        if (File.Exists(stampPath)) return; // already seeded for this user

        var sourceDir = Path.Combine(AppContext.BaseDirectory, "Plugins");
        var destDir = AppLoader.GetApplicationsFolderPath(user);

        try { Directory.CreateDirectory(destDir); }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DefaultAppSeeder] Could not ensure '{destDir}': {ex.Message}");
            return;
        }

        if (Directory.Exists(sourceDir))
        {
            foreach (var dllName in _seedDllNames)
            {
                CopySeed(sourceDir, destDir, dllName);
            }
        }
        else
        {
            // Proprietary plug-in not built locally. We still mark the
            // user as seeded so we don't keep probing on every sign-in;
            // they can sideload the DLL into their Applications folder
            // any time.
            Debug.WriteLine($"[DefaultAppSeeder] No host Plugins folder at '{sourceDir}'; marking seeded anyway.");
        }

        try { File.WriteAllText(stampPath, "seeded " + DateTime.UtcNow.ToString("O")); }
        catch (Exception ex)
        {
            // If we can't write the stamp the user will get re-seeded next
            // sign-in - annoying but not broken. Worth a log line so the
            // root cause (perms / disk full) is recoverable.
            Debug.WriteLine($"[DefaultAppSeeder] Stamp write failed: {ex.Message}");
        }
    }

    private static void CopySeed(string sourceDir, string destDir, string dllName)
    {
        var src = Path.Combine(sourceDir, dllName);
        if (!File.Exists(src))
        {
            Debug.WriteLine($"[DefaultAppSeeder] Seed DLL missing: {src}");
            return;
        }

        try
        {
            // Per-app folder layout: Applications/<AppName>/<AppName>.dll.
            // The AppLoader's discovery prefers this layout because it lets
            // a single app keep its private dependencies (other DLLs, .pdb,
            // future config files) cleanly grouped together rather than
            // scattered at the Applications/ root.
            var appFolderName = Path.GetFileNameWithoutExtension(dllName);
            var appFolder = Path.Combine(destDir, appFolderName);
            Directory.CreateDirectory(appFolder);

            // Copy main DLL into the per-app folder.
            File.Copy(src, Path.Combine(appFolder, dllName), overwrite: true);

            // Carry along siblings the plug-in's MSBuild target deposited
            // next to it (currently: matching .pdb for debug-build symbol
            // info; future: any private dependency the plug-in ships).
            var pdb = Path.ChangeExtension(src, ".pdb");
            if (File.Exists(pdb))
            {
                File.Copy(pdb, Path.Combine(appFolder, Path.GetFileName(pdb)), overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DefaultAppSeeder] Copy failed for '{dllName}': {ex.Message}");
        }
    }
}
