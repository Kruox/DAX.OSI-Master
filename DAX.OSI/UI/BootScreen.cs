using Avalonia.Controls;
using Avalonia.Layout;
using DOSI.CORE.Animations;
using DOSI.CORE.UIComponents;

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
