using Avalonia.Controls;
using Avalonia.Layout;
using DOSI.CORE.Animations;
using DOSI.CORE.UIComponents;
using DOSI.CORE.UserManagement;
using DOSI.CORE.WallpaperManagement;

namespace DAX.OSI.UI;

/// <summary>
/// The boot screen displayed when the virtual operating system starts.
/// </summary>
public class BootScreen : DOSIScreen, IDisposable
{
    public override string ScreenId => "boot";
    public override string ScreenName => "Boot";

    private readonly DOSILoadingAnim _loadingAnim;
    private readonly Grid _centeringGrid;
    private bool _isDisposed;

    public BootScreen()
    {
        _loadingAnim = new DOSILoadingAnim(LoadingSize.Large);

        // Use a Grid overlay to center the loading animation
        _centeringGrid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _centeringGrid.Children.Add(_loadingAnim);

        Desktop.Children.Add(_centeringGrid);

        // Update grid size when desktop resizes
        Desktop.LayoutUpdated += (s, e) =>
        {
            _centeringGrid.Width = Desktop.Bounds.Width;
            _centeringGrid.Height = Desktop.Bounds.Height;
        };
    }

    public override void OnNavigatedTo()
    {
        base.OnNavigatedTo();
        NotifyScreenReady();

        // Pre-warm each known user's preferred wallpaper bitmap off-thread
        // while the boot loading animation is on screen. This eliminates
        // the multi-second hang the user sees after clicking their tile on
        // the login screen: by the time the picker shows, every user's
        // wallpaper is already decoded + downscaled + blur-baked, so
        // WallpaperManager.SetWallpaper from SelectUser hits the warm
        // cache and the cross-fade kicks off immediately as part of the
        // picker -> sign-in panel transition.
        //
        // Runs entirely on a background thread and only touches the
        // WallpaperManager bitmap caches, so it cannot affect the boot
        // loading animation or the screen-ready notification timing.
        PrewarmUserWallpapersInBackground();
    }

    /// <summary>
    /// Best-effort background pass that decodes + downscales + blur-bakes
    /// every known user's preferred wallpaper into the
    /// <see cref="WallpaperManager"/> cache. Errors are swallowed - if a
    /// particular wallpaper can't be decoded ahead of time the UI-thread
    /// resolve later will hit the same code path and surface (or also
    /// swallow) the error at that point. Safe to call repeatedly; cache
    /// hits are no-ops.
    /// </summary>
    private static void PrewarmUserWallpapersInBackground()
    {
        System.Threading.Tasks.Task.Run(() =>
        {
            System.Collections.Generic.IReadOnlyList<DOSIUser> users;
            try { users = UserManager.GetAllUsers(); }
            catch { return; }

            var wm = WallpaperManager.Instance;
            foreach (var u in users)
            {
                string? k;
                try { k = UserManager.GetUserWallpaper(u); }
                catch { continue; }
                if (string.IsNullOrWhiteSpace(k)) continue;
                // WallpaperManager.Prewarm internally short-circuits the
                // accent-only sentinel and auto-registers custom file
                // paths before decoding. Cache hits are no-ops.
                try { wm.Prewarm(k!); } catch { /* prewarm is best-effort */ }
            }
        });
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _loadingAnim.Dispose();
        _centeringGrid.Children.Clear();
        Desktop.Children.Clear();

        GC.SuppressFinalize(this);
    }
}
