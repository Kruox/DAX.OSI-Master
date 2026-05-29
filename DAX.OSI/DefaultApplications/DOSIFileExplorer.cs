using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using DOSI.CORE.AccentManagement;
using DOSI.CORE.Apps;
using DOSI.CORE.UIComponents;
using DOSI.CORE.UIComponents.WindowManagement;
using DOSI.CORE.UserManagement;
using Path = System.IO.Path;
using AvPath = Avalonia.Controls.Shapes.Path;

namespace DAX.OSI.DefaultApplications;

/// <summary>
/// File explorer for the DOSI virtual operating system. Sandboxed to the
/// signed-in user's home folder (under <c>Users/&lt;username&gt;/</c>) and
/// rendered entirely with custom DOSI controls.
/// </summary>
public class DOSIFileExplorer : DOSIWindow
{
    private static AccentManager Accents => AccentManager.Instance;

    private readonly DOSIUser? _user;
    private readonly string _rootPath;

    private string _currentPath;
    private readonly List<string> _backStack = new();
    private readonly List<string> _forwardStack = new();

    private readonly DOSITextBox _addressBar;
    private readonly StackPanel _breadcrumb;
    private readonly Border _backButton;
    private readonly Border _forwardButton;
    private readonly Border _upButton;
    private readonly Border _refreshButton;
    private readonly Border _newFolderButton;
    // Live text-filter input mounted in the toolbar. Filtering is
    // client-side over the already-populated tiles (IsVisible toggle),
    // so it's cheap and survives navigation/refresh by being re-applied
    // at the end of PopulateItems.
    private readonly DOSITextBox _searchBox;

    private readonly StackPanel _sidebarItems;
    private readonly WrapPanel _itemsPanel;
    private readonly DOSIScrollViewer _itemsScroller;
    private readonly TextBlock _statusItemCount;
    private readonly TextBlock _statusSelection;
    // Disk usage chip rendered in the middle of the status bar. Updated
    // on every PopulateItems pass (so it follows navigation + watcher
    // refreshes without us wiring a dedicated timer). Reports the
    // free/total on the host volume that backs _currentPath.
    private readonly TextBlock _statusDiskUsage;
    // Single-cell Grid that wraps the entire explorer rootGrid. Quick
    // Look lays its preview overlay in here so the surface covers the
    // whole window without us having to thread an overlay row through
    // every existing layout. Lazily populated - empty until the user
    // presses Space on a selection.
    private readonly Grid _overlayHost;
    // Currently-visible Quick Look overlay, or null when dismissed.
    // Stored as a field so the spacebar can toggle (second Space ->
    // dismiss) and Escape / outside-click handlers can find it without
    // walking the visual tree.
    private Border? _quickLookOverlay;
    // Sort dropdown lives on the right side of the breadcrumb row. Stored
    // as a field so PopulateItems can read the current SelectedItem and
    // sort the listings accordingly. Default is "Name (A-Z)" which matches
    // the long-standing OrderBy(Path.GetFileName) ordering, so persisted
    // user expectations don't shift on first launch.
    private readonly DOSIDropDown _sortDropdown;
    // Display strings shown in the dropdown; chosen to read like macOS
    // Finder labels rather than verbose "Sort by ..." prefixes.
    private const string SortNameAsc   = "Name (A–Z)";
    private const string SortNameDesc  = "Name (Z–A)";
    private const string SortDateNew   = "Date (newest)";
    private const string SortDateOld   = "Date (oldest)";
    private const string SortSizeLarge = "Size (largest)";
    private const string SortSizeSmall = "Size (smallest)";

    // Themed chrome surfaces - kept as fields so OnAccentChanged can re-theme them.
    private Border? _toolbar;
    private Border? _sidebar;
    private Border? _itemsArea;
    private Border? _statusBar;
    private readonly List<(Border Button, TextBlock Glyph)> _toolButtons = new();

    // ----- Open-instance registry (for cross-window drop targets) -----
    // Every constructed explorer registers itself on AttachedToVisualTree
    // and unregisters on DetachedFromVisualTree so other subsystems
    // (notably DesktopIconLayer's drag-handoff) can ask "is there an
    // open file-explorer window under this screen point?" without
    // walking the visual tree of every TopLevel. Lifetime tracking is
    // attach-based rather than ctor-based so a stale entry never lingers
    // for a window that was closed before it ever attached.
    private static readonly List<DOSIFileExplorer> _openInstances = new();

    /// <summary>
    /// Live count of attached file-explorer windows. Exposed for the
    /// desktop icon layer's drag-ghost arming logic - we only need to
    /// pay the snapshot cost when there's actually somewhere to drop
    /// (another monitor OR an open explorer).
    /// </summary>
    public static int OpenInstanceCount => _openInstances.Count;

    /// <summary>
    /// Returns the open explorer + its current path if <paramref name="screenPos"/>
    /// falls inside any open file-explorer's items area. Null when no
    /// explorer is under the point. Used by <see cref="DAX.OSI.UI.DesktopIconLayer"/>
    /// to redirect a dropped desktop tile into the explorer's current
    /// directory.
    /// <para>
    /// Resolution strategy: walks <see cref="DOSI.CORE.UIComponents.DosiHostRegistry.All"/>
    /// to identify which monitor owns the screen point (via
    /// <see cref="DOSI.CORE.UIComponents.IDosiHost.TargetScreen"/><c>.Bounds.Contains</c>),
    /// converts the point to that host's TopLevel DIP coords with its OWN
    /// <see cref="Avalonia.Controls.TopLevel.PointToClient"/>, then checks
    /// every open explorer hosted on that TopLevel for an items-area hit.
    /// This mirrors the proven multi-monitor pattern in
    /// <see cref="DOSI.CORE.UIComponents.WindowManagement.DOSIWindow.TryHandoffToMonitorAtCursor"/>
    /// and works reliably on borderless-FullScreen secondary monitors
    /// where the earlier per-window <c>ClientSize × RenderScaling</c>
    /// math failed.
    /// </para>
    /// </summary>
    public static (DOSIFileExplorer Explorer, string CurrentPath)? FindDropTarget(Avalonia.PixelPoint screenPos)
    {
        DOSI.CORE.UIComponents.IDosiHost? targetHost = null;
        foreach (var h in DOSI.CORE.UIComponents.DosiHostRegistry.All)
        {
            var s = h.TargetScreen;
            if (s != null && s.Bounds.Contains(screenPos)) { targetHost = h; break; }
        }
        if (targetHost is not Avalonia.Controls.TopLevel targetTop) return null;

        for (int i = _openInstances.Count - 1; i >= 0; i--)
        {
            var ex = _openInstances[i];
            var items = ex._itemsArea;
            if (items == null) continue;
            if (!ReferenceEquals(Avalonia.Controls.TopLevel.GetTopLevel(items), targetTop)) continue;

            Avalonia.Point local;
            try { local = targetTop.PointToClient(screenPos); }
            catch { continue; }

            var origin = items.TranslatePoint(new Avalonia.Point(0, 0), targetTop);
            if (origin == null) continue;
            var rect = new Avalonia.Rect(origin.Value.X, origin.Value.Y,
                                         items.Bounds.Width, items.Bounds.Height);
            if (rect.Contains(local))
            {
                // Don't accept a drop into the trash view - dropping a
                // file "into" trash via cross-window drag has confusing
                // semantics. The desktop's own delete affordance handles
                // intentional deletion.
                if (ex.IsAtTrashRoot()) continue;
                return (ex, ex._currentPath);
            }
        }
        return null;
    }

    private Border? _selectedTile;

    // ----- Live directory watcher -----
    // Per-window FileSystemWatcher on _currentPath. Fires the debounced
    // tick which re-populates the grid, then fades in any tiles whose
    // file name wasn't present in the prior population. The watcher is
    // re-bound on every Navigate so it always tracks the visible folder,
    // and torn down on detach so we don't leak handles.
    private FileSystemWatcher? _dirWatcher;
    private Avalonia.Threading.DispatcherTimer? _dirWatcherDebounce;
    // Snapshot of the file names visible on the last PopulateItems pass.
    // Used by the watcher tick to detect freshly-arrived files (so we
    // can pop them in with a small fade animation instead of redrawing
    // every tile silently).
    private readonly HashSet<string> _lastPopulatedNames = new(StringComparer.OrdinalIgnoreCase);

    // ----- Rubber-band (marquee) selection state -----
    // The marquee paints a translucent accent rectangle while the user
    // drags across empty space. On release, every tile whose visual
    // bounds intersect the rectangle is added to the multi-select tint
    // and the status bar shows count + size totals. Clears on the next
    // single-tile click.
    private bool _marqueeActive;
    private Point _marqueeStart;
    private Border? _marqueeRect;
    private Grid? _itemsOverlayHost;
    // Tiles tinted by the most recent marquee. Tracked separately from
    // _selectedTile (which is the singular focus that drives details +
    // operations) so clear / re-tint paths can find them without walking
    // every child every time.
    private readonly HashSet<Border> _marqueeSelected = new();
    // Single SolidColorBrush instances we recolor on accent change so
    // every marquee-tinted tile + the live marquee rectangle stay in
    // sync with the user's current accent. Using one brush per role
    // means the accent-change path is a single Color assignment, not a
    // visual-tree walk + brush rebuild.
    private SolidColorBrush? _marqueeSelectionFill;
    private SolidColorBrush? _marqueeRectFill;
    private SolidColorBrush? _marqueeRectStroke;

    // ----- Picker mode (file-open dialog) -----
    // When non-null, the explorer behaves as a modal-style file picker:
    // files outside the extension whitelist are hidden, double-clicking a
    // file invokes the callback + closes the window, and the title bar
    // shows the supplied prompt instead of "Files". Folders stay clickable
    // so the user can still navigate. Set via EnablePickerMode.
    private string[]? _pickerExtensions;
    private Action<string>? _pickerCallback;
    // "Choose" / "Cancel" buttons mounted in the status bar while in
    // picker mode. Without these the only way to commit a selection was
    // double-click, which isn't discoverable - users would single-click
    // a file, see it appear in the details panel, and then be stuck.
    private DOSIButton? _pickerChooseButton;
    private DOSIButton? _pickerCancelButton;
    private StackPanel? _pickerActionStack;

    // ----- Tile drag-out (file -> desktop / other explorer) -----
    // Mirrors the proven DesktopIconLayer.ArmDragGhost pattern so the
    // user can drag a file/folder tile OUT of the explorer onto any
    // monitor's desktop wallpaper (or into another open explorer
    // window's items area). On release the file is moved to the
    // target's backing folder; both source + target reconcile through
    // their existing FileSystemWatcher paths. No drag-out target =
    // no-op (the tile stays where it is and the existing per-tile
    // click semantics still resolve, since we only set _tileDragMoved
    // once the pointer crosses the threshold).
    private Border? _tileDragSource;
    private string? _tileDragSourcePath;
    private bool _tileDragSourceIsDirectory;
    private Point _tileDragOriginLocal;
    private Point _tileDragOriginTopLevel;
    private Avalonia.PixelPoint _tileDragOriginScreen;
    private bool _tileDragArmed;
    private bool _tileDragMoved;
    private Avalonia.PixelPoint? _tileLastDragScreenPos;
    private Avalonia.Media.Imaging.RenderTargetBitmap? _tileDragGhostSnapshot;
    private Avalonia.PixelPoint _tileDragGhostCursorOffset;
    private Avalonia.Controls.TopLevel? _tileDragSourceTopLevel;
    private bool _tileDragGhostShown;

    // ----- Details panel (slides in from left when a tile is selected) -----
    private Border? _detailsPanel;
    private TranslateTransform? _detailsTranslate;
    private Avalonia.Threading.DispatcherTimer? _detailsAnimTimer;
    private bool _detailsOpen;
    private Panel? _detailsIconHost;
    private TextBlock? _detailsName;
    private TextBlock? _detailsKind;
    private TextBlock? _detailsSizeRow;
    private TextBlock? _detailsModifiedRow;
    private TextBlock? _detailsPathRow;
    private const double DetailsPanelWidth = 240;

    public DOSIFileExplorer()
    {
        Title = "Files";
        WindowWidth = 880;
        WindowHeight = 540;
        MinimumSize = new Size(560, 360);
        Icon = CreateAppIcon();

        _user = UserManager.CurrentUser;
        if (_user != null)
        {
            UserManager.EnsureUserSubfolders(_user);
            _rootPath = UserManager.GetUserFolder(_user.Username);
        }
        else
        {
            // Fallback (shouldn't normally happen - explorer is opened from desktop).
            _rootPath = AppContext.BaseDirectory;
        }
        _currentPath = _rootPath;

        // ---------- Toolbar ----------
        _backButton = BuildToolButton("\u2190", "Back");
        _backButton.PointerReleased += (_, _) => GoBack();

        _forwardButton = BuildToolButton("\u2192", "Forward");
        _forwardButton.PointerReleased += (_, _) => GoForward();

        _upButton = BuildToolButton("\u2191", "Up one level");
        _upButton.PointerReleased += (_, _) => GoUp();

        _refreshButton = BuildToolButton("\u21BB", "Refresh");
        _refreshButton.PointerReleased += (_, _) => Refresh();

        _newFolderButton = BuildToolButton("\u002B", "New folder");
        _newFolderButton.PointerReleased += async (_, _) => await CreateNewFolderAsync();

        _addressBar = new DOSITextBox
        {
            FontSize = 12,
            Padding = new Thickness(10, 6),
            Height = 28,
            UseRoundedEnds = true,
            VerticalAlignment = VerticalAlignment.Center
        };
        _addressBar.KeyDown += OnAddressBarKeyDown;

        // Compact live-filter search box. Stays visually distinct from
        // the address bar (narrower, magnifier glyph placeholder) so users
        // don't confuse "type a path" with "filter what's shown". The
        // filter is purely client-side: every tile that doesn't match the
        // substring gets IsVisible=false and the item count is updated
        // to reflect the visible subset. Clearing the box restores the
        // full population without re-enumerating the directory.
        _searchBox = new DOSITextBox
        {
            FontSize = 12,
            Padding = new Thickness(10, 6),
            Height = 28,
            Width = 180,
            UseRoundedEnds = true,
            VerticalAlignment = VerticalAlignment.Center,
            PlaceholderText = "\U0001F50D  Search"
        };
        _searchBox.TextChanged += (_, _) => ApplySearchFilter();
        _searchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                _searchBox.Text = string.Empty;
            }
        };

        // Sort dropdown - drives PopulateItems' enumeration order. We
        // don't persist the choice across windows (each explorer instance
        // starts on Name A-Z, same as the previous hard-coded default);
        // adding per-folder persistence is a future enhancement.
        _sortDropdown = new DOSIDropDown
        {
            Width = 150,
            Height = 28,
            Margin = new Thickness(0, 4, 14, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        _sortDropdown.SetItems(new[]
        {
            SortNameAsc, SortNameDesc,
            SortDateNew, SortDateOld,
            SortSizeLarge, SortSizeSmall
        });
        _sortDropdown.SelectedItem = SortNameAsc;
        _sortDropdown.SelectionChanged += (_, _) => PopulateItems();

        var navGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _backButton, _forwardButton, _upButton, _refreshButton }
        };

        var toolbarGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
            Margin = new Thickness(10, 8)
        };
        toolbarGrid.Children.Add(navGroup); Grid.SetColumn(navGroup, 0);

        var addressContainer = new Border
        {
            Margin = new Thickness(10, 0),
            Child = _addressBar,
            VerticalAlignment = VerticalAlignment.Center
        };
        toolbarGrid.Children.Add(addressContainer); Grid.SetColumn(addressContainer, 1);
        toolbarGrid.Children.Add(_searchBox); Grid.SetColumn(_searchBox, 2);
        toolbarGrid.Children.Add(_newFolderButton); Grid.SetColumn(_newFolderButton, 3);

        var toolbar = new Border
        {
            Background = Accents.WindowChromeBrush,
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = toolbarGrid
        };
        _toolbar = toolbar;

        // ---------- Sidebar ----------
        _sidebarItems = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 2,
            Margin = new Thickness(8, 14, 8, 8)
        };
        BuildSidebar();

        var sidebarScroller = new DOSIScrollViewer
        {
            Content = _sidebarItems,
            ShowScrollButtons = false,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };

        var sidebar = new Border
        {
            Width = 200,
            Background = Accents.WindowChromeBrush,
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = sidebarScroller
        };
        _sidebar = sidebar;

        // ---------- Breadcrumb ----------
        // Each path segment is rendered as its own clickable TextBlock so
        // the user can jump to any ancestor with a single click - same
        // affordance Windows Explorer and macOS Finder have. The host
        // panel is rebuilt on every UpdateBreadcrumb call.
        _breadcrumb = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(14, 8, 14, 4),
            VerticalAlignment = VerticalAlignment.Center
        };

        // ---------- File grid ----------
        _itemsPanel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(10),
            ItemSpacing = 6,
            LineSpacing = 6
        };

        _itemsScroller = new DOSIScrollViewer
        {
            Content = _itemsPanel,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            ShowScrollButtons = false
        };

        // Overlay host: a Grid that stacks the scroller (full-bleed) and
        // a marquee Canvas where rubber-band rectangles live during
        // selection. The Canvas is empty most of the time and zero-cost.
        _itemsOverlayHost = new Grid();
        _itemsOverlayHost.Children.Add(_itemsScroller);
        var marqueeLayer = new Canvas
        {
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _itemsOverlayHost.Children.Add(marqueeLayer);

        var itemsArea = new Border
        {
            Background = Accents.WindowContentBrush,
            Child = _itemsOverlayHost
        };
        _itemsArea = itemsArea;
        itemsArea.ContextMenu = BuildEmptyAreaContextMenu();

        // Empty-area pointer handling: left-click on empty space clears
        // selection AND starts a rubber-band marquee. Determine "empty
        // space" by walking up from e.Source - anything that resolves
        // to a tile (a Border whose Tag is a string path) is handled by
        // the tile itself, never by us. This catches stray sources like
        // the WrapPanel's inner panel or the scrollviewer's transport
        // surface that the old source-equality check missed and that
        // caused the marquee to feel "glitchy" - sometimes it started,
        // sometimes it didn't.
        itemsArea.PointerPressed += (_, e) =>
        {
            var pp = e.GetCurrentPoint(itemsArea);
            if (pp.Properties.IsRightButtonPressed) return; // let the context menu open

            if (IsTileOrDescendant(e.Source as Visual)) return;

            ClearSelection();
            BeginMarquee(marqueeLayer, e.GetPosition(marqueeLayer));
            e.Pointer.Capture(itemsArea);
            e.Handled = true;
        };
        itemsArea.PointerMoved += (_, e) =>
        {
            if (_marqueeActive) UpdateMarquee(marqueeLayer, e.GetPosition(marqueeLayer));
        };
        itemsArea.PointerReleased += (_, e) =>
        {
            if (_marqueeActive)
            {
                CommitMarquee(marqueeLayer);
                e.Pointer.Capture(null);
            }
        };
        // Avalonia can steal pointer capture (e.g. a context menu opens,
        // a window-level drag handoff fires). Without this hook the
        // marquee rectangle would be stranded on screen and _marqueeActive
        // would stay true forever, breaking the next click.
        itemsArea.AddHandler(Control.PointerCaptureLostEvent, (_, _) =>
        {
            if (!_marqueeActive) return;
            _marqueeActive = false;
            if (_marqueeRect != null && marqueeLayer.Children.Contains(_marqueeRect))
                marqueeLayer.Children.Remove(_marqueeRect);
            _marqueeRect = null;
        });

        // Wrap the items area so the details panel can slide in over it
        // without disturbing the WrapPanel layout. The panel is anchored to
        // the right of the contentArea (and spans BOTH rows so its top
        // edge sits flush against the toolbar instead of leaving a gap
        // where the breadcrumb row peeks through).
        _detailsPanel = BuildDetailsPanel();

        // Permanently reserve the details panel's footprint on the items
        // area so tiles lay out for the smaller region from the very
        // first PopulateItems pass. The previous version toggled this
        // padding on/off in ShowDetailsPanel / HideDetailsPanel, which
        // worked but reflowed the WrapPanel on EVERY tile click - tiles
        // visibly jumped around the grid each time the panel opened or
        // closed, which was disorienting and (until the drag-arming was
        // moved to top-level coords) also spuriously triggered drag-out
        // because the tile moved out from under the cursor mid-click.
        // Reserving the space once gives the panel a stable docking
        // region; it still slides in/out via its TranslateTransform,
        // but tiles never reflow. Cost: a 240 px strip on the right
        // stays clear when no tile is selected - same convention as
        // Finder / Explorer with their preview panes, and far less
        // jarring than continuous layout churn.
        itemsArea.Padding = new Thickness(0, 0, DetailsPanelWidth, 0);

        var contentArea = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*")
        };
        // Breadcrumb row hosts the path segments on the left and the sort
        // dropdown on the right. Wrapped in a Grid (rather than docking
        // directly into contentArea) so the dropdown stays right-aligned
        // independent of breadcrumb length, and the panel's reserved
        // strip on the right doesn't collide with it.
        var breadcrumbRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            // Match the items-area reservation so the dropdown sits over
            // the items column, not over the details-panel column.
            Margin = new Thickness(0, 0, DetailsPanelWidth, 0)
        };
        breadcrumbRow.Children.Add(_breadcrumb); Grid.SetColumn(_breadcrumb, 0);
        breadcrumbRow.Children.Add(_sortDropdown); Grid.SetColumn(_sortDropdown, 1);
        contentArea.Children.Add(breadcrumbRow); Grid.SetRow(breadcrumbRow, 0);
        contentArea.Children.Add(itemsArea); Grid.SetRow(itemsArea, 1);
        contentArea.Children.Add(_detailsPanel);
        Grid.SetRow(_detailsPanel, 0);
        Grid.SetRowSpan(_detailsPanel, 2);

        // ---------- Status bar ----------
        _statusItemCount = new TextBlock
        {
            Text = "",
            FontSize = 11,
            Foreground = Accents.TextSecondaryBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        _statusDiskUsage = new TextBlock
        {
            Text = "",
            FontSize = 11,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.85,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _statusSelection = new TextBlock
        {
            Text = "",
            FontSize = 11,
            Foreground = Accents.TextSecondaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var statusGrid = new Grid
        {
            // Three columns: item count (left), disk usage (center), selection summary (right).
            ColumnDefinitions = new ColumnDefinitions("*,Auto,*,Auto"),
            Margin = new Thickness(14, 6)
        };
        statusGrid.Children.Add(_statusItemCount); Grid.SetColumn(_statusItemCount, 0);
        statusGrid.Children.Add(_statusDiskUsage); Grid.SetColumn(_statusDiskUsage, 1);
        statusGrid.Children.Add(_statusSelection); Grid.SetColumn(_statusSelection, 2);

        // Picker-mode actions: a Cancel + Choose pair lives in the
        // rightmost status-bar column and is invisible until
        // EnablePickerMode flips them on. This gives the user a clear,
        // discoverable way to commit (or back out of) a selection
        // instead of having to remember the double-click gesture. The
        // buttons reflect the live singular selection - Choose is only
        // enabled when a whitelisted FILE is selected (not a folder,
        // since folders are navigation-only in picker mode).
        _pickerCancelButton = new DOSIButton
        {
            Text = "Cancel",
            FontSize = 11,
            Height = 22,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        _pickerCancelButton.Click += (_, _) =>
        {
            _pickerCallback = null;
            _ = PlayCloseAnimationAsync().ContinueWith(_ => { });
        };
        _pickerChooseButton = new DOSIButton
        {
            Text = "Choose",
            FontSize = 11,
            Height = 22,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        _pickerChooseButton.Click += (_, _) =>
        {
            if (_pickerCallback == null) return;
            if (_selectedTile?.Tag is not string p) return;
            if (Directory.Exists(p)) return; // can't pick a folder
            if (_pickerExtensions != null &&
                !_pickerExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
                return;
            ActivateTile(p, isDirectory: false);
        };
        _pickerActionStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            IsVisible = false,
            Children = { _pickerCancelButton, _pickerChooseButton }
        };
        statusGrid.Children.Add(_pickerActionStack); Grid.SetColumn(_pickerActionStack, 3);

        var statusBar = new Border
        {
            Background = Accents.WindowChromeBrush,
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Height = 26,
            Child = statusGrid
        };
        _statusBar = statusBar;

        // ---------- Layout ----------
        var bodyGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*")
        };
        bodyGrid.Children.Add(sidebar); Grid.SetColumn(sidebar, 0);
        bodyGrid.Children.Add(contentArea); Grid.SetColumn(contentArea, 1);

        var rootGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto")
        };
        rootGrid.Children.Add(toolbar); Grid.SetRow(toolbar, 0);
        rootGrid.Children.Add(bodyGrid); Grid.SetRow(bodyGrid, 1);
        rootGrid.Children.Add(statusBar); Grid.SetRow(statusBar, 2);

        // Wrap rootGrid in a single-cell parent so Quick Look (and any
        // future modal overlay) can lay on top of the whole explorer
        // without us having to thread a row span through every layout
        // change. The Quick Look surface is added lazily on first use.
        _overlayHost = new Grid();
        _overlayHost.Children.Add(rootGrid);
        Content = _overlayHost;

        AttachedToVisualTree += (_, _) =>
        {
            Accents.AccentChanged += OnAccentChanged;
            if (!_openInstances.Contains(this)) _openInstances.Add(this);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            Accents.AccentChanged -= OnAccentChanged;
            _openInstances.Remove(this);
            _detailsAnimTimer?.Stop();
            _detailsAnimTimer = null;
            StopDirectoryWatcher();
        };

        // Process-wide clipboard hotkeys (Ctrl+C / Ctrl+X / Ctrl+V) bound
        // at the window level so they work regardless of which tile is
        // focused. Skipped when focus is in a TextBox so typing in the
        // address bar / inline rename isn't hijacked.
        KeyDown += OnExplorerKeyDown;

        Navigate(_rootPath, recordHistory: false);
    }

    /// <summary>
    /// Routes Ctrl+C / Ctrl+X / Ctrl+V / Ctrl+A and F5 into the explorer.
    /// Skips text-input controls so the address bar and inline renames
    /// keep working.
    /// </summary>
    private void OnExplorerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox) return;

        if (e.Key == Key.F5)
        {
            Refresh();
            e.Handled = true;
            return;
        }

        // Space: Quick Look preview for the singular selection. Toggles -
        // a second Space dismisses. Escape also dismisses (handled inside
        // the overlay itself). No-op when nothing is selected.
        if (e.Key == Key.Space)
        {
            if (_quickLookOverlay != null)
            {
                CloseQuickLook();
                e.Handled = true;
                return;
            }
            if (_selectedTile?.Tag is string qlPath)
            {
                e.Handled = true;
                OpenQuickLook(qlPath, Directory.Exists(qlPath));
            }
            return;
        }

        // Delete / Backspace: trash (or, inside the trash view, permanently
        // delete) the current selection - whether that's the singular
        // _selectedTile or an entire marquee multi-select. DeleteSelectionAsync
        // already routes both cases through CollectOperationTargets, so we
        // just need a tile + path to hand it. The address bar / inline-rename
        // text-input guard above is what keeps these keys from hijacking
        // editing - same protection Ctrl+X/C/V already rely on.
        if (e.Key == Key.Delete)
        {
            var anchor = _selectedTile;
            if (anchor == null && _marqueeSelected.Count > 0)
                anchor = _marqueeSelected.First();
            if (anchor?.Tag is string anchorPath)
            {
                e.Handled = true;
                bool isDir = Directory.Exists(anchorPath);
                _ = DeleteSelectionAsync(anchor, anchorPath, isDir);
            }
            return;
        }

        // Backspace doubles as "Go up one folder" when no tile is the
        // singular selection - matches the convention every file browser
        // uses (Windows Explorer, macOS Finder ⌘↑). When a tile IS
        // selected it still routes through the delete path above, so
        // power users keep the trash shortcut.
        if (e.Key == Key.Back)
        {
            if (_selectedTile != null && _selectedTile.Tag is string selectedPath)
            {
                e.Handled = true;
                bool isDir = Directory.Exists(selectedPath);
                _ = DeleteSelectionAsync(_selectedTile, selectedPath, isDir);
            }
            else
            {
                e.Handled = true;
                GoUp();
            }
            return;
        }

        // Enter / Return: activate the singular selection. Same semantics
        // as double-clicking - folders navigate into themselves, files
        // route through ActivateTile (picker callback, file-association
        // app, image viewer, or metadata dialog).
        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            if (_selectedTile?.Tag is string selPath)
            {
                e.Handled = true;
                ActivateTile(selPath, Directory.Exists(selPath));
            }
            return;
        }

        // Arrow keys: grid navigation. Left/Right step linearly through
        // the WrapPanel's tile order; Up/Down find the nearest tile on
        // the previous/next row using horizontal-center proximity. Falls
        // through to no-op (without setting Handled) when there are no
        // tiles, so the parent scroller can still keyboard-scroll.
        if (e.Key == Key.Left || e.Key == Key.Right ||
            e.Key == Key.Up   || e.Key == Key.Down)
        {
            if (NavigateSelectionWithArrow(e.Key))
            {
                e.Handled = true;
            }
            return;
        }

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (!ctrl) return;

        if (e.Key == Key.C)
        {
            var targets = CollectKeyboardTargets();
            if (targets.Count > 0)
            {
                FileClipboard.CopyMany(targets);
                _statusSelection.Text = targets.Count == 1
                    ? $"Copied: {Path.GetFileName(targets[0])}"
                    : $"Copied {targets.Count} items";
                e.Handled = true;
            }
        }
        else if (e.Key == Key.X)
        {
            var targets = CollectKeyboardTargets();
            if (targets.Count > 0)
            {
                FileClipboard.CutMany(targets);
                _statusSelection.Text = targets.Count == 1
                    ? $"Cut: {Path.GetFileName(targets[0])}"
                    : $"Cut {targets.Count} items";
                e.Handled = true;
            }
        }
        else if (e.Key == Key.V)
        {
            _ = PasteFromClipboardAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.A)
        {
            SelectAllVisible();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Arrow-key navigation across the tile grid. Left/Right step the
    /// singular selection through the WrapPanel's child order (so
    /// folders-then-files reads naturally); Up/Down geometrically find
    /// the nearest tile on the previous/next row using horizontal-center
    /// proximity. When nothing is selected yet the first arrow press
    /// lands on the first tile, matching the "press any arrow to start
    /// keyboard mode" convention every native file browser uses.
    /// </summary>
    /// <returns>True if the selection actually changed (so the caller can mark the key handled).</returns>
    private bool NavigateSelectionWithArrow(Key key)
    {
        var tiles = _itemsPanel.Children.OfType<Border>().ToList();
        if (tiles.Count == 0) return false;

        // First press with no selection: pick tile 0 so the user immediately
        // sees feedback, regardless of direction.
        if (_selectedTile == null || !tiles.Contains(_selectedTile))
        {
            var first = tiles[0];
            if (first.Tag is string fp) SelectTile(first, fp, Directory.Exists(fp));
            return true;
        }

        var current = _selectedTile;
        Border? next = null;
        int currentIndex = tiles.IndexOf(current);

        switch (key)
        {
            case Key.Left:
                if (currentIndex > 0) next = tiles[currentIndex - 1];
                break;
            case Key.Right:
                if (currentIndex < tiles.Count - 1) next = tiles[currentIndex + 1];
                break;
            case Key.Up:
            case Key.Down:
                next = FindTileOnAdjacentRow(current, tiles, key == Key.Down);
                break;
        }

        if (next == null || next == current) return false;
        if (next.Tag is not string p) return false;
        SelectTile(next, p, Directory.Exists(p));
        // Best-effort scroll-into-view: brings the new tile onscreen even
        // when the user is keyboarding past the visible region. Avalonia's
        // ScrollViewer doesn't have a direct BringDescendantIntoView for
        // arbitrary controls, but TranslatePoint + Offset gets us close.
        ScrollTileIntoView(next);
        return true;
    }

    /// <summary>
    /// Finds the tile on the row above/below <paramref name="current"/>
    /// whose horizontal center is closest to current's. Works on a
    /// WrapPanel by grouping children by their arranged Y coordinate
    /// (with a small tolerance to absorb sub-pixel rounding).
    /// </summary>
    private static Border? FindTileOnAdjacentRow(Border current, List<Border> tiles, bool down)
    {
        // Centre of the current tile in the items panel's coord space.
        double currCenterX = current.Bounds.X + current.Bounds.Width / 2;
        double currTop = current.Bounds.Y;
        const double rowTolerance = 2.0; // tiles on "the same row" share a Y within this many DIPs

        // Find candidate rows.
        var rows = tiles
            .GroupBy(t => Math.Round(t.Bounds.Y / 4) * 4) // bucket by 4-DIP rounding so jitter doesn't split a row
            .OrderBy(g => g.Key)
            .ToList();

        int currentRowIdx = rows.FindIndex(g => Math.Abs(g.Key - Math.Round(currTop / 4) * 4) < rowTolerance);
        if (currentRowIdx < 0) return null;
        int targetRowIdx = down ? currentRowIdx + 1 : currentRowIdx - 1;
        if (targetRowIdx < 0 || targetRowIdx >= rows.Count) return null;

        Border? best = null;
        double bestDist = double.MaxValue;
        foreach (var candidate in rows[targetRowIdx])
        {
            double centerX = candidate.Bounds.X + candidate.Bounds.Width / 2;
            double dist = Math.Abs(centerX - currCenterX);
            if (dist < bestDist) { bestDist = dist; best = candidate; }
        }
        return best;
    }

    /// <summary>
    /// Scrolls the items area so <paramref name="tile"/> is fully visible
    /// in the viewport. Cheap: just nudges the scroller's Offset.Y so the
    /// tile's vertical extent sits between the current top and bottom of
    /// the viewport, leaving X untouched.
    /// </summary>
    private void ScrollTileIntoView(Border tile)
    {
        var viewport = _itemsScroller.Viewport;
        double offsetY = _itemsScroller.VerticalOffset;
        double tileTop = tile.Bounds.Y;
        double tileBottom = tileTop + tile.Bounds.Height;
        if (tileTop < offsetY)
        {
            _itemsScroller.ScrollVerticalTo(Math.Max(0, tileTop - 8));
        }
        else if (tileBottom > offsetY + viewport.Height)
        {
            _itemsScroller.ScrollVerticalTo(tileBottom - viewport.Height + 8);
        }
    }

    /// <summary>
    /// Hides tiles whose file/folder name doesn't contain the search box's
    /// <summary>
    /// Refreshes the centre disk-usage chip with free/total space on the
    /// host volume backing the user root. Cheap (one DriveInfo lookup),
    /// called from PopulateItems so it tracks navigation + watcher
    /// refreshes without us needing a dedicated timer. Silently degrades
    /// to an empty string if the host platform doesn't expose drive info
    /// for the root path (e.g. on some sandboxed runs).
    /// </summary>
    private void UpdateDiskUsageStatus()
    {
        if (_statusDiskUsage == null) return;
        try
        {
            var fullRoot = Path.GetFullPath(_rootPath);
            var driveLetter = Path.GetPathRoot(fullRoot);
            if (string.IsNullOrEmpty(driveLetter)) { _statusDiskUsage.Text = string.Empty; return; }

            var info = new DriveInfo(driveLetter);
            if (!info.IsReady) { _statusDiskUsage.Text = string.Empty; return; }

            long free = info.AvailableFreeSpace;
            long total = info.TotalSize;
            long used = total - free;
            double pct = total > 0 ? (used * 100d / total) : 0;
            _statusDiskUsage.Text = $"{FormatSize(free)} free of {FormatSize(total)}  \u2022  {pct:0}% used";
        }
        catch
        {
            _statusDiskUsage.Text = string.Empty;
        }
    }

    /// <summary>
    /// Hides tiles whose file/folder name doesn't contain the search box's
    /// current text (case-insensitive substring match). Toggles IsVisible
    /// instead of repopulating so the filter is essentially free and a
    /// subsequent clear restores the prior tile set without re-enumerating
    /// the directory. Updates the status bar with the visible count so
    /// the user can see how many items the filter is matching.
    /// </summary>
    private void ApplySearchFilter()
    {
        if (_itemsPanel == null) return;
        var query = (_searchBox?.Text ?? string.Empty).Trim();
        int visible = 0;
        int total = _itemsPanel.Children.Count;
        foreach (var child in _itemsPanel.Children)
        {
            if (child is not Border tile) continue;
            string name = tile.Tag is string p ? (Path.GetFileName(p) ?? string.Empty) : string.Empty;
            bool match = query.Length == 0 ||
                         name.Contains(query, StringComparison.OrdinalIgnoreCase);
            tile.IsVisible = match;
            if (match) visible++;
        }

        if (query.Length == 0)
        {
            _statusItemCount.Text = $"{total} item{(total == 1 ? "" : "s")}";
        }
        else
        {
            _statusItemCount.Text = $"Filter: \u201C{query}\u201D  \u2022  {visible} of {total}";
        }
    }

    /// <summary>
    /// Lightweight "select all visible": tints every tile with the accent
    /// selection fill and updates the status bar with a count + summed
    /// size. Doesn't mutate the primary <see cref="_selectedTile"/>
    /// (operations still target whatever is the singular focus), so this
    /// is purely a visual + status-bar summary feature. Any subsequent
    /// click on a tile or empty area returns the view to normal single-
    /// select behaviour.
    /// </summary>
    private void SelectAllVisible()
    {
        // Respect the live search filter - "Select all visible" means the
        // visible subset, not every tile in the directory. When the filter
        // is empty IsVisible is true for every tile so this collapses to
        // the previous behaviour.
        var tiles = _itemsPanel.Children.OfType<Border>().Where(t => t.IsVisible).ToList();
        if (tiles.Count == 0) return;

        ClearMarqueeSelection();
        var brush = GetMarqueeSelectionBrush();
        long totalBytes = 0;
        int folders = 0, files = 0;
        foreach (var tile in tiles)
        {
            tile.Background = brush;
            _marqueeSelected.Add(tile);
            if (tile.Tag is string p)
            {
                if (Directory.Exists(p)) folders++;
                else if (File.Exists(p))
                {
                    files++;
                    totalBytes += SafeFileSize(p);
                }
            }
        }

        var size = files > 0 ? $"  \u2022  {FormatSize(totalBytes)}" : "";
        _statusSelection.Text = $"{tiles.Count} item{(tiles.Count == 1 ? "" : "s")} selected{size}";
    }

    /// <summary>
    /// Toggles <paramref name="tile"/> in/out of the marquee multi-select
    /// set. On the FIRST Ctrl-click the existing singular <see cref="_selectedTile"/>
    /// (if any, and not already the clicked tile) is rolled into the set
    /// so the user's prior selection isn't visually dropped when they
    /// start building a multi-select - matches Finder / Explorer
    /// convention. The details panel is hidden as soon as the selection
    /// becomes multi-tile since "details" is a single-item affordance.
    /// </summary>
    private void ToggleTileInMarquee(Border tile)
    {
        var brush = GetMarqueeSelectionBrush();

        // Seed the marquee with the prior singular selection so the
        // user's existing focus tile carries forward into the multi-set.
        if (_selectedTile != null &&
            _selectedTile != tile &&
            !_marqueeSelected.Contains(_selectedTile))
        {
            _marqueeSelected.Add(_selectedTile);
            _selectedTile.Background = brush;
        }

        if (_marqueeSelected.Contains(tile))
        {
            _marqueeSelected.Remove(tile);
            if (tile != _selectedTile) tile.Background = Brushes.Transparent;
        }
        else
        {
            _marqueeSelected.Add(tile);
            tile.Background = brush;
        }

        // Multi-select supersedes the singular focus - clear it so the
        // next Delete / Copy / etc. routes through the marquee set, and
        // hide the per-item details panel (re-shows on next plain click).
        _selectedTile = null;
        if (_detailsOpen) HideDetailsPanel();

        // Status-bar summary identical to CommitMarquee's, so multi-select
        // feedback is consistent across rubber-band, Ctrl+A, and Ctrl-click.
        long totalBytes = 0;
        int files = 0;
        foreach (var t in _marqueeSelected)
        {
            if (t.Tag is string p && File.Exists(p))
            {
                files++;
                totalBytes += SafeFileSize(p);
            }
        }
        if (_marqueeSelected.Count == 0)
        {
            _statusSelection.Text = string.Empty;
        }
        else
        {
            var size = files > 0 ? $"  \u2022  {FormatSize(totalBytes)}" : "";
            _statusSelection.Text =
                $"{_marqueeSelected.Count} item{(_marqueeSelected.Count == 1 ? "" : "s")} selected{size}";
        }
    }

    // =====================================================================
    // Rubber-band (marquee) selection
    // =====================================================================

    /// <summary>True if <paramref name="v"/> is a tile Border or sits inside one.</summary>
    private static bool IsTileOrDescendant(Visual? v)
    {
        while (v != null)
        {
            if (v is Border b && b.Tag is string) return true;
            v = v.GetVisualParent();
        }
        return false;
    }

    /// <summary>
    /// Returns the shared marquee-selection brush, allocating + colouring
    /// it on first use. Subsequent accent changes recolour the same brush
    /// in place so every tinted tile updates in one assignment.
    /// </summary>
    private SolidColorBrush GetMarqueeSelectionBrush()
    {
        var a = Accents.AccentPrimary;
        if (_marqueeSelectionFill == null)
            _marqueeSelectionFill = new SolidColorBrush(Color.FromArgb(80, a.R, a.G, a.B));
        else
            _marqueeSelectionFill.Color = Color.FromArgb(80, a.R, a.G, a.B);
        return _marqueeSelectionFill;
    }

    /// <summary>Clears the marquee-tinted set and restores transparent backgrounds.</summary>
    private void ClearMarqueeSelection()
    {
        if (_marqueeSelected.Count == 0) return;
        foreach (var tile in _marqueeSelected)
        {
            // Don't trample the singular _selectedTile's accent tint - it
            // owns its own ApplyTileVisualState equivalent (inline below).
            if (tile == _selectedTile) continue;
            tile.Background = Brushes.Transparent;
        }
        _marqueeSelected.Clear();
    }

    /// <summary>
    /// Starts a rubber-band selection at <paramref name="start"/> in
    /// <paramref name="layer"/> coordinates. Subsequent moves update the
    /// rectangle and release commits the intersected tiles.
    /// </summary>
    private void BeginMarquee(Canvas layer, Point start)
    {
        // Wipe any leftover marquee tint from the previous drag so the
        // new drag starts from a clean visual baseline.
        ClearMarqueeSelection();

        _marqueeActive = true;
        _marqueeStart = start;

        var a = Accents.AccentPrimary;
        if (_marqueeRectFill == null)
            _marqueeRectFill = new SolidColorBrush(Color.FromArgb(40, a.R, a.G, a.B));
        else
            _marqueeRectFill.Color = Color.FromArgb(40, a.R, a.G, a.B);
        if (_marqueeRectStroke == null)
            _marqueeRectStroke = new SolidColorBrush(Color.FromArgb(180, a.R, a.G, a.B));
        else
            _marqueeRectStroke.Color = Color.FromArgb(180, a.R, a.G, a.B);

        _marqueeRect = new Border
        {
            Background = _marqueeRectFill,
            BorderBrush = _marqueeRectStroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Width = 0,
            Height = 0,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(_marqueeRect, start.X);
        Canvas.SetTop(_marqueeRect, start.Y);
        layer.Children.Add(_marqueeRect);
    }

    private void UpdateMarquee(Canvas layer, Point current)
    {
        if (_marqueeRect == null) return;
        var x = Math.Min(current.X, _marqueeStart.X);
        var y = Math.Min(current.Y, _marqueeStart.Y);
        var w = Math.Abs(current.X - _marqueeStart.X);
        var h = Math.Abs(current.Y - _marqueeStart.Y);
        Canvas.SetLeft(_marqueeRect, x);
        Canvas.SetTop(_marqueeRect, y);
        _marqueeRect.Width = w;
        _marqueeRect.Height = h;

        // Live hit-test as the rectangle grows so users get immediate
        // feedback on what's about to be selected - same convention as
        // the desktop marquee. Cheap: a few intersect tests per move.
        HitTestMarqueeLive(layer, new Rect(x, y, w, h));
    }

    /// <summary>
    /// Live-paint marquee tint as the rectangle changes shape. Tiles
    /// that fall out of the rectangle (because the user is dragging the
    /// pointer back) have their tint cleared.
    /// </summary>
    private void HitTestMarqueeLive(Canvas layer, Rect marqueeBounds)
    {
        var brush = GetMarqueeSelectionBrush();
        foreach (var tile in _itemsPanel.Children.OfType<Border>())
        {
            var origin = tile.TranslatePoint(new Point(0, 0), layer);
            if (origin == null) continue;
            var tileRect = new Rect(origin.Value.X, origin.Value.Y, tile.Bounds.Width, tile.Bounds.Height);
            bool inside = marqueeBounds.Intersects(tileRect);
            bool wasSelected = _marqueeSelected.Contains(tile);
            if (inside && !wasSelected)
            {
                tile.Background = brush;
                _marqueeSelected.Add(tile);
            }
            else if (!inside && wasSelected)
            {
                if (tile != _selectedTile) tile.Background = Brushes.Transparent;
                _marqueeSelected.Remove(tile);
            }
        }
    }

    /// <summary>
    /// Commits the marquee: removes the rectangle from the overlay and
    /// updates the status bar with a count + summed size. The tiles
    /// themselves are already tinted (by the live hit-test on move).
    /// </summary>
    private void CommitMarquee(Canvas layer)
    {
        if (!_marqueeActive) return;
        _marqueeActive = false;
        if (_marqueeRect != null)
        {
            layer.Children.Remove(_marqueeRect);
            _marqueeRect = null;
        }

        if (_marqueeSelected.Count == 0)
        {
            _statusSelection.Text = string.Empty;
            return;
        }

        long totalBytes = 0;
        int files = 0;
        foreach (var tile in _marqueeSelected)
        {
            if (tile.Tag is string p && File.Exists(p))
            {
                files++;
                totalBytes += SafeFileSize(p);
            }
        }
        var size = files > 0 ? $"  \u2022  {FormatSize(totalBytes)}" : "";
        _statusSelection.Text = $"{_marqueeSelected.Count} item{(_marqueeSelected.Count == 1 ? "" : "s")} selected{size}";
    }

    private string? SelectedPath() => _selectedTile?.Tag as string;

    /// <summary>
    /// Path set targeted by keyboard shortcuts (Ctrl+C / Ctrl+X). Returns
    /// the marquee multi-selection when one is active, otherwise the
    /// singular selection, otherwise an empty list. Same scoping rule as
    /// the per-tile context menu's <c>CollectOperationTargets</c>.
    /// </summary>
    private List<string> CollectKeyboardTargets()
    {
        if (_marqueeSelected.Count > 1)
        {
            return _marqueeSelected
                .Select(b => b.Tag as string)
                .Where(s => !string.IsNullOrEmpty(s))
                .Cast<string>()
                .ToList();
        }
        var single = SelectedPath();
        return single != null ? new List<string> { single } : new List<string>();
    }

    /// <summary>
    /// Pastes every staged <see cref="FileClipboard"/> entry into
    /// <see cref="_currentPath"/>. Honours sandbox bounds and renames on
    /// collision so the user never silently overwrites an existing file.
    /// On a multi-paste the per-item failure mode is best-effort: one
    /// broken source doesn't abort the rest.
    /// </summary>
    private async Task PasteFromClipboardAsync()
    {
        if (!FileClipboard.HasContent) return;
        var dstDir = _currentPath;
        if (!IsInsideRoot(dstDir)) return;

        var sources = FileClipboard.Paths;
        var mode = FileClipboard.CurrentMode;
        int ok = 0, fail = 0;

        foreach (var src in sources)
        {
            if (!File.Exists(src) && !Directory.Exists(src)) { fail++; continue; }
            try
            {
                var name = Path.GetFileName(src.TrimEnd(Path.DirectorySeparatorChar));
                var dst = ChooseUniqueDestination(Path.Combine(dstDir, name));

                if (mode == FileClipboard.Mode.Cut && IsInsideRoot(src))
                {
                    if (Directory.Exists(src)) Directory.Move(src, dst);
                    else                       File.Move(src, dst);
                    // Cut = the source path is gone. Keep the desktop
                    // layout JSON in sync: if the source AND dest both
                    // live on a desktop folder, carry the position;
                    // otherwise just drop the source entry. No-op when
                    // neither side is on a desktop folder.
                    DOSI.CORE.UIComponents.WindowManagement.DesktopIconLayout
                        .RenameIfOnDesktop(src, dst);
                }
                else
                {
                    if (Directory.Exists(src)) CopyDirectoryRecursive(src, dst);
                    else                       File.Copy(src, dst, overwrite: false);
                }
                ok++;
            }
            catch { fail++; }
        }

        if (mode == FileClipboard.Mode.Cut) FileClipboard.Clear();

        _statusSelection.Text = sources.Count == 1
            ? (fail == 0
                ? (mode == FileClipboard.Mode.Cut ? $"Moved: {Path.GetFileName(sources[0])}" : $"Copied: {Path.GetFileName(sources[0])}")
                : $"Paste failed: {Path.GetFileName(sources[0])}")
            : (fail == 0
                ? (mode == FileClipboard.Mode.Cut ? $"Moved {ok} items" : $"Copied {ok} items")
                : $"{ok} succeeded, {fail} failed");

        await Task.Yield();
        PopulateItems();
    }

    /// <summary>
    /// Returns <paramref name="desired"/> if it doesn't exist, otherwise
    /// appends " (2)", " (3)", ... before the extension until a free name
    /// is found. Bounded to 1000 attempts to prevent runaway loops on a
    /// pathologically full directory.
    /// </summary>
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
        return desired; // give up gracefully - File.Copy/Move will throw cleanly
    }

    /// <summary>Recursive directory copy used by paste when the source is a folder.</summary>
    private static void CopyDirectoryRecursive(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.EnumerateFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: false);
        foreach (var sub in Directory.EnumerateDirectories(src))
            CopyDirectoryRecursive(sub, Path.Combine(dst, Path.GetFileName(sub)));
    }

    // =====================================================================
    // Sidebar
    // =====================================================================

    private void BuildSidebar()
    {
        _sidebarItems.Children.Clear();

        _sidebarItems.Children.Add(BuildSidebarHeader(_user?.DisplayName ?? "Home"));
        _sidebarItems.Children.Add(BuildSidebarItem("Home", "\u2302", _rootPath));

        _sidebarItems.Children.Add(BuildSidebarHeader("Library"));
        foreach (var name in UserManager.StandardUserSubfolders)
        {
            var path = Path.Combine(_rootPath, name);
            _sidebarItems.Children.Add(BuildSidebarItem(name, GlyphForLibrary(name), path));
        }

        // System category - the trash lives here so it doesn't clutter
        // the user's regular libraries. Only surfaced when there's a
        // signed-in user (the trash is per-user).
        if (_user != null)
        {
            _sidebarItems.Children.Add(BuildSidebarHeader("System"));
            var trashPath = FileTrash.GetTrashRoot(_user);
            _sidebarItems.Children.Add(BuildSidebarItem("Trash", "\u267A", trashPath));
        }
    }

    private Control BuildSidebarHeader(string text) => new TextBlock
    {
        Text = text.ToUpperInvariant(),
        FontSize = 10,
        FontWeight = FontWeight.SemiBold,
        Foreground = Accents.TextSecondaryBrush,
        Opacity = 0.7,
        Margin = new Thickness(8, 10, 8, 4),
        LetterSpacing = 1
    };

    private Border BuildSidebarItem(string label, string glyph, string path)
    {
        var glyphText = new TextBlock
        {
            Text = glyph,
            FontSize = 14,
            Foreground = Accents.AccentPrimaryBrush,
            Width = 20,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };

        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { glyphText, labelText }
        };

        var item = new Border
        {
            Padding = new Thickness(8, 6),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = stack
        };

        item.PointerEntered += (_, _) =>
        {
            if (!IsCurrentSidebarItem(path))
                item.Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
        };
        item.PointerExited += (_, _) =>
        {
            if (!IsCurrentSidebarItem(path))
                item.Background = Brushes.Transparent;
        };
        item.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            Navigate(path);
        };

        item.Tag = path;
        return item;
    }

    private bool IsCurrentSidebarItem(string path) =>
        string.Equals(Path.TrimEndingDirectorySeparator(_currentPath),
                      Path.TrimEndingDirectorySeparator(path),
                      StringComparison.OrdinalIgnoreCase);

    private void RefreshSidebarHighlight()
    {
        foreach (var child in _sidebarItems.Children)
        {
            if (child is Border b && b.Tag is string p)
            {
                b.Background = IsCurrentSidebarItem(p)
                    ? new SolidColorBrush(Color.FromArgb(55, Accents.AccentPrimary.R,
                                                            Accents.AccentPrimary.G,
                                                            Accents.AccentPrimary.B))
                    : Brushes.Transparent;
            }
        }
    }

    private static string GlyphForLibrary(string name) => name switch
    {
        "Desktop" => "\u25A2",
        // U+1F4C4 (📄) is outside the BMP, so it needs a surrogate pair -
        // the bare "\u1F4C4" escape only consumes 4 hex digits and rendered
        // as "ᴌ4" in the sidebar.
        "Documents" => "\U0001F4C4",
        "Downloads" => "\u2913",
        "Music" => "\u266B",
        "Pictures" => "\u25A3",
        "Videos" => "\u25B6",
        _ => "\u25A1"
    };

    // =====================================================================
    // Navigation
    // =====================================================================

    private void OnAddressBarKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            var requested = (_addressBar.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(requested))
            {
                Navigate(_rootPath);
                return;
            }

            // Treat as relative to root unless it's already fully qualified.
            string target = Path.IsPathFullyQualified(requested)
                ? requested
                : Path.Combine(_rootPath, requested);

            target = Path.GetFullPath(target);
            if (Directory.Exists(target) && IsInsideRoot(target))
                Navigate(target);
            else
                _addressBar.Text = _currentPath; // revert
        }
        else if (e.Key == Key.Tab)
        {
            // Tab autocomplete: resolve the partial path the user has
            // typed, look at its parent's child directories, and complete
            // to the first match (or cycle through matches on repeated
            // Tab). Folder-only - the explorer's address bar navigates
            // to directories, not files.
            e.Handled = true;
            TryAddressBarComplete();
        }
    }

    /// <summary>
    /// Tab-complete the address bar against the children of whichever
    /// directory the user's partial path resolves to. Cycles through
    /// multiple matches on repeated Tab so the user can browse without
    /// re-typing. Honours sandbox bounds - completions never escape the
    /// user's root.
    /// </summary>
    private int _tabCompletionIndex = -1;
    private string? _tabCompletionStem;
    private string? _tabCompletionDir;
    private List<string>? _tabCompletionMatches;

    private void TryAddressBarComplete()
    {
        var typed = _addressBar.Text ?? string.Empty;

        // Re-derive the candidate set on EVERY Tab unless the user is
        // cycling through an unchanged stem. The cheap discriminator is
        // "does the typed text still equal the last completion's prefix
        // OR the last completion itself?"
        bool cycling = _tabCompletionMatches != null &&
                       _tabCompletionMatches.Count > 0 &&
                       (typed == _tabCompletionStem ||
                        _tabCompletionMatches.Contains(typed, StringComparer.OrdinalIgnoreCase));

        if (!cycling)
        {
            // Map "~" / "~/foo" back to absolute under the user root so
            // the directory walk works the same as Enter-to-commit.
            var partial = typed.TrimStart();
            if (partial.StartsWith("~", StringComparison.Ordinal))
                partial = partial.Length == 1 ? _rootPath : Path.Combine(_rootPath, partial.Substring(1).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            else if (!Path.IsPathFullyQualified(partial))
                partial = Path.Combine(_rootPath, partial);

            string dir;
            string stem;
            if (Directory.Exists(partial))
            {
                // User typed exactly a directory. Cycle through ITS
                // children rather than its siblings - one extra Tab
                // step into the folder, matching shell convention.
                dir = partial;
                stem = string.Empty;
            }
            else
            {
                dir = Path.GetDirectoryName(partial) ?? _rootPath;
                stem = Path.GetFileName(partial) ?? string.Empty;
            }

            if (!IsInsideRoot(dir)) { _tabCompletionMatches = null; return; }

            try
            {
                _tabCompletionMatches = Directory.EnumerateDirectories(dir)
                    .Select(p => Path.GetFileName(p) ?? string.Empty)
                    .Where(n => n.Length > 0 &&
                                n.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch { _tabCompletionMatches = null; return; }

            _tabCompletionDir = dir;
            _tabCompletionStem = typed; // remember the trigger so we know we're cycling
            _tabCompletionIndex = -1;
        }

        if (_tabCompletionMatches == null || _tabCompletionMatches.Count == 0)
            return;

        _tabCompletionIndex = (_tabCompletionIndex + 1) % _tabCompletionMatches.Count;
        var match = _tabCompletionMatches[_tabCompletionIndex];
        var completed = Path.Combine(_tabCompletionDir ?? _rootPath, match);
        _addressBar.Text = RelativeToRoot(completed);
        // DOSITextBox doesn't expose a caret-position setter; the
        // user can re-press Tab to keep cycling regardless of caret
        // position because cycling is detected by the typed text
        // matching one of the completion candidates.
    }

    private bool IsInsideRoot(string path)
    {
        var fullRoot = Path.GetFullPath(_rootPath);
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private void Navigate(string path, bool recordHistory = true)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!Directory.Exists(path)) return;
        if (!IsInsideRoot(path)) return;

        if (recordHistory && !string.IsNullOrEmpty(_currentPath) &&
            !string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase))
        {
            _backStack.Add(_currentPath);
            _forwardStack.Clear();
        }

        _currentPath = Path.GetFullPath(path);
        _addressBar.Text = RelativeToRoot(_currentPath);
        UpdateBreadcrumb();
        RefreshSidebarHighlight();
        UpdateNavButtons();
        PopulateItems();
        StartDirectoryWatcher(_currentPath);
    }

    /// <summary>
    /// Public entry-point for callers that want to open this explorer at a
    /// specific subfolder (e.g. the Application Manager's "Open Applications
    /// folder" button). Defers the navigation until the visual tree is up
    /// so the address bar / breadcrumb have a chance to be measured. Falls
    /// back silently if the path is outside the user's sandboxed root - we
    /// don't want a misconfigured caller to crash the explorer at startup.
    /// </summary>
    public void RequestNavigate(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        void DoNav() { try { Navigate(path); } catch { /* sandbox / IO errors are non-fatal */ } }

        if (IsLoaded)
            DoNav();
        else
            AttachedToVisualTree += (_, _) => DoNav();
    }

    /// <summary>
    /// Opens a new file-explorer window navigated to the parent folder of
    /// <paramref name="targetPath"/> and, once the items grid has
    /// populated, selects (and scrolls to) the tile for that file. This
    /// is the "Reveal in Files" / "Show in Finder" convention every host
    /// OS supports - any DOSI app holding a file path can invoke it.
    /// </summary>
    /// <param name="targetPath">Absolute path of the file or folder to highlight.</param>
    /// <returns>The launched explorer (already handed to the window manager) or null if no window manager is available.</returns>
    public static DOSIFileExplorer? Reveal(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) return null;
        var parent = Path.GetDirectoryName(targetPath);
        // If the path itself is a directory, parent might be the right
        // landing spot too - but more useful to land INSIDE the directory
        // itself rather than alongside it.
        var landing = Directory.Exists(targetPath) ? targetPath : parent;
        if (string.IsNullOrEmpty(landing)) return null;

        var explorer = new DOSIFileExplorer();
        explorer.RequestNavigate(landing);

        // Defer the tile lookup until after Navigate has populated the
        // grid. Navigate is sync but the visual tree isn't ready until
        // after attach -> measure -> arrange; piggyback on the explorer's
        // own AttachedToVisualTree so we run after PopulateItems has
        // built the tiles.
        if (!Directory.Exists(targetPath))
        {
            void TryHighlight()
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    var match = explorer._itemsPanel.Children
                        .OfType<Border>()
                        .FirstOrDefault(b => b.Tag is string s &&
                                             string.Equals(Path.GetFullPath(s),
                                                           Path.GetFullPath(targetPath),
                                                           StringComparison.OrdinalIgnoreCase));
                    if (match != null && match.Tag is string mp)
                    {
                        explorer.SelectTile(match, mp, isDirectory: false);
                    }
                }, Avalonia.Threading.DispatcherPriority.Loaded);
            }

            if (explorer.IsLoaded) TryHighlight();
            else explorer.AttachedToVisualTree += (_, _) => TryHighlight();
        }

        WindowManager.Instance?.OpenWindow(explorer);
        return explorer;
    }

    private string RelativeToRoot(string path)
    {
        var rel = Path.GetRelativePath(_rootPath, path);
        if (rel == "." || string.IsNullOrEmpty(rel)) return "~";
        return "~" + Path.DirectorySeparatorChar + rel;
    }

    private void UpdateBreadcrumb()
    {
        _breadcrumb.Children.Clear();

        // Walk from the user root forward, generating one clickable
        // segment per directory level. The leading "~" segment is
        // always present and navigates Home.
        var rel = Path.GetRelativePath(_rootPath, _currentPath);
        var segments = new List<(string Label, string Path)>
        {
            ("~", _rootPath)
        };
        if (!string.IsNullOrEmpty(rel) && rel != ".")
        {
            var parts = rel.Split(Path.DirectorySeparatorChar,
                                  StringSplitOptions.RemoveEmptyEntries);
            var accumulated = _rootPath;
            foreach (var part in parts)
            {
                accumulated = Path.Combine(accumulated, part);
                segments.Add((part, accumulated));
            }
        }

        for (int i = 0; i < segments.Count; i++)
        {
            if (i > 0)
            {
                _breadcrumb.Children.Add(new TextBlock
                {
                    Text = "  \u203A  ",
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Accents.TextSecondaryBrush,
                    Opacity = 0.6,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            _breadcrumb.Children.Add(BuildBreadcrumbSegment(segments[i].Label, segments[i].Path,
                                                            isCurrent: i == segments.Count - 1));
        }
    }

    /// <summary>
    /// Builds a single clickable breadcrumb segment. The current segment
    /// (rightmost) is rendered with the primary text colour and is not
    /// interactive; ancestors are dimmer and click-navigate to that level.
    /// </summary>
    private Control BuildBreadcrumbSegment(string label, string path, bool isCurrent)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = isCurrent ? Accents.TextPrimaryBrush : Accents.TextSecondaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        if (isCurrent) return text;

        // Wrap in a host that shows a subtle hover state so users know
        // it's clickable. Cursor flips to hand on entry.
        var host = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 1),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = text
        };
        host.PointerEntered += (_, _) =>
        {
            host.Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
            text.Foreground = Accents.TextPrimaryBrush;
        };
        host.PointerExited += (_, _) =>
        {
            host.Background = Brushes.Transparent;
            text.Foreground = Accents.TextSecondaryBrush;
        };
        host.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            Navigate(path);
        };
        return host;
    }

    private void UpdateNavButtons()
    {
        SetToolButtonEnabled(_backButton, _backStack.Count > 0);
        SetToolButtonEnabled(_forwardButton, _forwardStack.Count > 0);
        SetToolButtonEnabled(_upButton, !string.Equals(
            Path.TrimEndingDirectorySeparator(_currentPath),
            Path.TrimEndingDirectorySeparator(_rootPath),
            StringComparison.OrdinalIgnoreCase));
    }

    private void GoBack()
    {
        if (_backStack.Count == 0) return;
        var prev = _backStack[^1];
        _backStack.RemoveAt(_backStack.Count - 1);
        _forwardStack.Add(_currentPath);
        Navigate(prev, recordHistory: false);
        UpdateNavButtons();
    }

    private void GoForward()
    {
        if (_forwardStack.Count == 0) return;
        var next = _forwardStack[^1];
        _forwardStack.RemoveAt(_forwardStack.Count - 1);
        _backStack.Add(_currentPath);
        Navigate(next, recordHistory: false);
        UpdateNavButtons();
    }

    private void GoUp()
    {
        var parent = Directory.GetParent(_currentPath)?.FullName;
        if (string.IsNullOrEmpty(parent) || !IsInsideRoot(parent)) return;
        Navigate(parent);
    }

    private void Refresh() => PopulateItems();

    // =====================================================================
    // Items
    // =====================================================================

    private bool IsAtTrashRoot()
    {
        if (_user == null) return false;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(_currentPath),
            Path.TrimEndingDirectorySeparator(FileTrash.GetTrashRoot(_user)),
            StringComparison.OrdinalIgnoreCase);
    }

    private void PopulateItems()
    {
        _itemsPanel.Children.Clear();
        _selectedTile = null;
        _statusSelection.Text = "";
        HideDetailsPanel();

        // Trash root: drive the grid from the trash manifest so tiles
        // carry the original file names + a special context menu
        // (Restore / Delete Forever) instead of the GUID folder names
        // and per-item house-keeping we normally would show.
        if (IsAtTrashRoot())
        {
            PopulateTrashItems();
            // Trash view swaps its empty-area context menu for an
            // "Empty Trash" action - update it once per population.
            if (_itemsArea != null)
                _itemsArea.ContextMenu = BuildTrashEmptyAreaContextMenu();
            return;
        }
        else
        {
            // Restore the standard empty-area context menu when leaving
            // the trash view.
            if (_itemsArea != null)
                _itemsArea.ContextMenu = BuildEmptyAreaContextMenu();
        }

        IEnumerable<string> dirs = Array.Empty<string>();
        IEnumerable<string> files = Array.Empty<string>();
        try
        {
            // Folders always sort by name (size/date sorting doesn't read
            // naturally on directories - users want them grouped
            // alphabetically regardless of file-side sort), but we still
            // honour the asc/desc choice for the name axes.
            var sort = _sortDropdown?.SelectedItem ?? SortNameAsc;
            bool dirsAsc = sort != SortNameDesc;

            var dirsRaw = Directory.EnumerateDirectories(_currentPath);
            dirs = dirsAsc
                ? dirsRaw.OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                : dirsRaw.OrderByDescending(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);

            var filesRaw = Directory.EnumerateFiles(_currentPath)
                .Where(f => !Path.GetFileName(f).Equals(_user?.Username + ".json",
                                                        StringComparison.OrdinalIgnoreCase))
                // In picker mode, hide everything that doesn't match the
                // extension whitelist so the user can't accidentally pick
                // an incompatible file. Folders are always shown so they
                // can keep navigating.
                .Where(f => _pickerExtensions == null ||
                            _pickerExtensions.Contains(Path.GetExtension(f),
                                                       StringComparer.OrdinalIgnoreCase));

            files = sort switch
            {
                SortNameDesc  => filesRaw.OrderByDescending(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase),
                SortDateNew   => filesRaw.OrderByDescending(SafeLastWriteTime),
                SortDateOld   => filesRaw.OrderBy(SafeLastWriteTime),
                SortSizeLarge => filesRaw.OrderByDescending(SafeFileSize),
                SortSizeSmall => filesRaw.OrderBy(SafeFileSize),
                _             => filesRaw.OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            };
        }
        catch { /* unreadable folder; show nothing */ }

        int dirCount = 0, fileCount = 0;
        var freshNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newcomerTiles = new List<Border>();
        foreach (var d in dirs)
        {
            var tile = BuildTile(d, isDirectory: true);
            _itemsPanel.Children.Add(tile);
            var n = Path.GetFileName(d);
            freshNames.Add(n);
            if (!_lastPopulatedNames.Contains(n)) newcomerTiles.Add(tile);
            dirCount++;
        }
        foreach (var f in files)
        {
            var tile = BuildTile(f, isDirectory: false);
            _itemsPanel.Children.Add(tile);
            var n = Path.GetFileName(f);
            freshNames.Add(n);
            if (!_lastPopulatedNames.Contains(n)) newcomerTiles.Add(tile);
            fileCount++;
        }

        // Skip the pop-in animation on the very first population so the
        // initial open doesn't flash through N fades; ditto when the user
        // navigated to a new folder (every tile is technically "new" but
        // the whole grid changing reads as a navigation, not a series of
        // arrivals). Animate only when the prior snapshot was non-empty
        // AND fewer than half the tiles are newcomers - i.e. genuine
        // incremental change.
        if (_lastPopulatedNames.Count > 0 &&
            newcomerTiles.Count > 0 &&
            newcomerTiles.Count <= Math.Max(1, freshNames.Count / 2))
        {
            foreach (var tile in newcomerTiles)
                AnimateTileFadeIn(tile);
        }

        _lastPopulatedNames.Clear();
        foreach (var n in freshNames) _lastPopulatedNames.Add(n);

        _statusItemCount.Text = $"{dirCount + fileCount} item{((dirCount + fileCount) == 1 ? "" : "s")}";
        UpdateDiskUsageStatus();

        // Re-apply the live search filter so a population that arrived via
        // FileSystemWatcher (or Refresh) doesn't blow away the user's
        // partially-typed query. No-op when the search box is empty.
        ApplySearchFilter();
    }

    /// <summary>
    /// Trash-mode population: one tile per <see cref="FileTrash"/>
    /// manifest entry. Tiles still point at the real on-disk path so
    /// preview / activation paths Just Work, but the per-tile context
    /// menu is replaced with Restore / Delete Forever.
    /// </summary>
    private void PopulateTrashItems()
    {
        if (_user == null) return;
        var entries = FileTrash.List(_user);
        foreach (var entry in entries)
        {
            var path = FileTrash.ResolveItemPath(_user, entry);
            if (!File.Exists(path) && !Directory.Exists(path)) continue;
            _itemsPanel.Children.Add(BuildTrashTile(entry, path));
        }
        _statusItemCount.Text = $"{entries.Count} item{(entries.Count == 1 ? "" : "s")}";
    }

    // =====================================================================
    // Live directory watcher
    // =====================================================================

    /// <summary>
    /// (Re)attaches the FileSystemWatcher to <paramref name="path"/>. Stops
    /// any prior watcher first so navigation across folders doesn't leak
    /// handles. Watcher events are coalesced through a 200 ms debounce
    /// timer so a burst of OS events (a copy can fire 4-5) results in a
    /// single repopulation pass, not a flicker storm.
    /// </summary>
    private void StartDirectoryWatcher(string path)
    {
        StopDirectoryWatcher();
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
        try
        {
            _dirWatcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                EnableRaisingEvents = true
            };
            FileSystemEventHandler tick = (_, _) => OnDirectoryWatcherEvent();
            _dirWatcher.Created += tick;
            _dirWatcher.Deleted += tick;
            _dirWatcher.Renamed += (_, _) => OnDirectoryWatcherEvent();
        }
        catch
        {
            // Best-effort - failing here just falls back to the manual
            // F5 / refresh-button flow.
            _dirWatcher = null;
        }
    }

    private void StopDirectoryWatcher()
    {
        if (_dirWatcher != null)
        {
            try { _dirWatcher.EnableRaisingEvents = false; _dirWatcher.Dispose(); } catch { }
            _dirWatcher = null;
        }
        _dirWatcherDebounce?.Stop();
        _dirWatcherDebounce = null;
    }

    private void OnDirectoryWatcherEvent()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _dirWatcherDebounce?.Stop();
            _dirWatcherDebounce = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _dirWatcherDebounce.Tick += (_, _) =>
            {
                _dirWatcherDebounce?.Stop();
                _dirWatcherDebounce = null;
                // PopulateItems compares against _lastPopulatedNames and
                // fades in any tile whose file name is new, so a drop
                // from the desktop reads as a smooth arrival without us
                // wiring a bespoke "incoming file" path.
                PopulateItems();
            };
            _dirWatcherDebounce.Start();
        });
    }

    /// <summary>
    /// Brief opacity+scale pop-in animation used when a tile appears in
    /// the grid due to an external change (a desktop drop, a `mv` from a
    /// terminal, etc.). 180 ms, ease-out cubic - matches the desktop
    /// icon layer's <c>AnimateTileIn</c> so the two surfaces feel
    /// consistent during cross-window operations.
    /// </summary>
    private static void AnimateTileFadeIn(Border tile)
    {
        const double duration = 180;
        var scale = new ScaleTransform(0.85, 0.85);
        tile.RenderTransform = scale;
        tile.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        tile.Opacity = 0;

        var start = DateTime.UtcNow;
        var timer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        timer.Tick += (_, _) =>
        {
            var t = Math.Clamp((DateTime.UtcNow - start).TotalMilliseconds / duration, 0, 1);
            var eased = 1 - Math.Pow(1 - t, 3);
            scale.ScaleX = scale.ScaleY = 0.85 + 0.15 * eased;
            tile.Opacity = eased;
            if (t >= 1)
            {
                timer.Stop();
                tile.RenderTransform = null;
            }
        };
        timer.Start();
    }

    // =====================================================================
    // Tile drag-out
    //
    // Lets the user pick a tile up and drop it on any desktop wallpaper
    // (primary or extension monitor) or into another open file explorer's
    // items area. Mirrors the proven DesktopIconLayer.ArmDragGhost /
    // UpdateDragGhost / TeardownDragGhost pattern so the floating preview
    // crosses native window boundaries (Avalonia controls can't render
    // outside their parent TopLevel - the pooled DragGhostWindow is the
    // only way to get a visible cross-window drag).
    // =====================================================================

    private void ArmTileDragOut(Border tile, string path, bool isDirectory, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(tile).Properties;
        if (props.IsRightButtonPressed) { _tileDragSource = null; _tileDragArmed = false; return; }

        // Skip drag-out from the trash root - moving a trashed item back
        // to a desktop via drag would silently bypass the "Restore"
        // semantics the user gets from the trash context menu. The
        // desktop-side delete already routes through FileTrash so this
        // direction is the one we want to gate.
        if (FileTrash.IsInsideTrash(path))
        {
            _tileDragSource = null;
            _tileDragArmed = false;
            return;
        }

        _tileDragSource = tile;
        _tileDragSourcePath = path;
        _tileDragSourceIsDirectory = isDirectory;
        _tileDragMoved = false;
        _tileDragArmed = true;
        _tileDragGhostShown = false;
        _tileDragOriginLocal = e.GetPosition(tile);

        var topLevel = TopLevel.GetTopLevel(tile);
        _tileDragSourceTopLevel = topLevel;
        // Capture the press point in TOP-LEVEL coords too. The threshold
        // check in UpdateTileDragOut compares against this, NOT against
        // the tile-local origin: SelectTile opens the details panel,
        // which reserves 240px on the items area and reflows the
        // WrapPanel. Every tile (including the one we just clicked)
        // shifts by tens of pixels in tile-local space even though the
        // pointer hasn't moved a pixel in screen space, which easily
        // exceeds the 4px threshold and used to spuriously arm a full
        // drag-out - making single-clicks pick up the file and "throw"
        // it to whatever monitor's desktop happened to be under the
        // cursor at release. TopLevel coords stay stable across the
        // details-panel reflow because the window itself doesn't move.
        _tileDragOriginTopLevel = topLevel != null
            ? e.GetPosition(topLevel)
            : default;
        if (topLevel != null)
        {
            try
            {
                var cursorTl = e.GetPosition(topLevel);
                _tileDragOriginScreen = topLevel.PointToScreen(cursorTl);
            }
            catch { _tileDragOriginScreen = default; }
        }

        // Snapshot the tile into a bitmap. Cheap (the tile is a small
        // composite of a glyph + label) and reused for the entire drag.
        try
        {
            if (topLevel == null) return;
            var scaling = topLevel.RenderScaling > 0 ? topLevel.RenderScaling : 1.0;
            var w = Math.Max(1, tile.Bounds.Width);
            var h = Math.Max(1, tile.Bounds.Height);
            var pixelSize = new PixelSize(
                Math.Max(1, (int)(w * scaling)),
                Math.Max(1, (int)(h * scaling)));
            var dpi = new Vector(96 * scaling, 96 * scaling);
            var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(pixelSize, dpi);
            bmp.Render(tile);
            _tileDragGhostSnapshot = bmp;

            // Cursor offset relative to the tile's top-left, in pixels.
            var tileOriginLocal = new Point(0, 0);
            var tileOriginScreen = topLevel.PointToScreen(
                tile.TranslatePoint(tileOriginLocal, topLevel) ?? tileOriginLocal);
            _tileDragGhostCursorOffset = new PixelPoint(
                _tileDragOriginScreen.X - tileOriginScreen.X,
                _tileDragOriginScreen.Y - tileOriginScreen.Y);

            var ghost = DOSI.CORE.UIComponents.WindowManagement.DragGhostWindow.GetOrCreate();
            ghost.ConfigureFor(bmp, w, h, tileOriginScreen);
            ghost.SetVisible(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DOSIFileExplorer] ArmTileDragOut snapshot failed: {ex.Message}");
            _tileDragGhostSnapshot = null;
        }
    }

    private void UpdateTileDragOut(PointerEventArgs e)
    {
        if (_tileDragSource == null) return;
        // Measure delta in top-level (window) coords - tile-local would
        // false-positive any time SelectTile opened the details panel
        // and reflowed the WrapPanel underneath us. See the matching
        // comment in ArmTileDragOut.
        if (!_tileDragMoved)
        {
            if (_tileDragSourceTopLevel == null) return;
            Point cur;
            try { cur = e.GetPosition(_tileDragSourceTopLevel); }
            catch { return; }
            var dx = cur.X - _tileDragOriginTopLevel.X;
            var dy = cur.Y - _tileDragOriginTopLevel.Y;
            if (Math.Abs(dx) + Math.Abs(dy) < 6) return;
            _tileDragMoved = true;
            try { e.Pointer.Capture(_tileDragSource); } catch { }
        }

        var ghost = DOSI.CORE.UIComponents.WindowManagement.DragGhostWindow.Shared;
        if (ghost == null || _tileDragSourceTopLevel == null) return;

        PixelPoint cursorScreen;
        try
        {
            var cursorLocal = e.GetPosition(_tileDragSourceTopLevel);
            cursorScreen = _tileDragSourceTopLevel.PointToScreen(cursorLocal);
        }
        catch { return; }
        _tileLastDragScreenPos = cursorScreen;

        var tilePos = new PixelPoint(
            cursorScreen.X - _tileDragGhostCursorOffset.X,
            cursorScreen.Y - _tileDragGhostCursorOffset.Y);
        ghost.MoveTo(tilePos);
        if (!_tileDragGhostShown)
        {
            ghost.SetVisible(true);
            if (_tileDragSource != null) _tileDragSource.Opacity = 0;
            _tileDragGhostShown = true;
        }
    }

    private void FinishTileDragOut(PointerReleasedEventArgs e)
    {
        var src = _tileDragSource;
        var srcPath = _tileDragSourcePath;
        var srcIsDir = _tileDragSourceIsDirectory;
        bool moved = _tileDragMoved;
        var screenPos = _tileLastDragScreenPos;

        // Reset state + tear down the ghost regardless of outcome.
        _tileDragSource = null;
        _tileDragSourcePath = null;
        _tileDragArmed = false;
        _tileDragMoved = false;
        try { DOSI.CORE.UIComponents.WindowManagement.DragGhostWindow.Shared?.HideGhost(); } catch { }
        if (src != null) src.Opacity = 1;
        _tileDragGhostSnapshot = null;
        _tileDragGhostShown = false;
        var sourceTopLevel = _tileDragSourceTopLevel;
        _tileDragSourceTopLevel = null;
        _tileLastDragScreenPos = null;
        try { e.Pointer.Capture(null); } catch { }

        if (!moved || string.IsNullOrEmpty(srcPath)) return;
        if (!File.Exists(srcPath) && !Directory.Exists(srcPath)) return;

        PixelPoint releaseScreen;
        if (screenPos.HasValue) releaseScreen = screenPos.Value;
        else if (sourceTopLevel != null)
        {
            try { releaseScreen = sourceTopLevel.PointToScreen(e.GetPosition(sourceTopLevel)); }
            catch { return; }
        }
        else return;

        // Priority 1: a desktop icon layer on ANY monitor.
        var deskHit = DAX.OSI.UI.DesktopIconLayer.FindDropTarget(releaseScreen);
        if (deskHit.HasValue)
        {
            var layer = deskHit.Value.Layer;
            var deskPath = layer.DesktopPath;
            if (string.IsNullOrEmpty(deskPath)) return;
            // Don't move when the source ALREADY lives on this desktop -
            // dragging an item onto its own monitor's wallpaper from
            // within an explorer pointed at the same folder is a no-op.
            if (string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(srcPath) ?? string.Empty),
                Path.TrimEndingDirectorySeparator(deskPath),
                StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                Directory.CreateDirectory(deskPath);
                var name = Path.GetFileName(srcPath.TrimEnd(Path.DirectorySeparatorChar));
                var dst = ChooseUniqueDestination(Path.Combine(deskPath, name));

                // Pre-save the drop coordinate so the destination layer's
                // reconcile lands the new tile under the user's cursor
                // (matching the cross-monitor handoff invariant used by
                // DesktopIconLayer.TryHandoffDraggedTiles).
                var localDrop = deskHit.Value.LocalPoint;
                double dropX = Math.Max(0, localDrop.X - _tileDragGhostCursorOffset.X);
                double dropY = Math.Max(32, localDrop.Y - _tileDragGhostCursorOffset.Y);
                DesktopIconLayout.Save(Path.GetFileName(dst), dropX, dropY);

                if (srcIsDir) Directory.Move(srcPath, dst);
                else          File.Move(srcPath, dst);

                // If the SOURCE was on a desktop folder too, drop its
                // stale layout entry. (RenameIfOnDesktop handles the
                // "neither side / one side" cases internally.)
                DesktopIconLayout.RenameIfOnDesktop(srcPath, dst);
                layer.ForceReconcileFromExternal();
                Refresh();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DOSIFileExplorer] Drag-to-desktop failed: {ex.Message}");
                try { DOSIPopNotification.Show($"Move failed: {ex.Message}"); } catch { }
            }
            return;
        }

        // Priority 2: another open explorer's items area.
        var explorerHit = FindDropTarget(releaseScreen);
        if (explorerHit.HasValue && !ReferenceEquals(explorerHit.Value.Explorer, this))
        {
            var dstDir = explorerHit.Value.CurrentPath;
            if (string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(srcPath) ?? string.Empty),
                Path.TrimEndingDirectorySeparator(dstDir),
                StringComparison.OrdinalIgnoreCase))
                return;
            try
            {
                Directory.CreateDirectory(dstDir);
                var name = Path.GetFileName(srcPath.TrimEnd(Path.DirectorySeparatorChar));
                var dst = ChooseUniqueDestination(Path.Combine(dstDir, name));
                if (srcIsDir) Directory.Move(srcPath, dst);
                else          File.Move(srcPath, dst);
                DesktopIconLayout.RenameIfOnDesktop(srcPath, dst);
                Refresh();
                explorerHit.Value.Explorer.Refresh();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DOSIFileExplorer] Cross-explorer drag failed: {ex.Message}");
                try { DOSIPopNotification.Show($"Move failed: {ex.Message}"); } catch { }
            }
        }
    }

    private Border BuildTile(string path, bool isDirectory)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name)) name = path;

        var icon = isDirectory ? BuildFolderIcon() : BuildFileIcon(Path.GetExtension(name));

        var label = new TextBlock
        {
            Text = name,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextPrimaryBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 92,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 6, 0, 0)
        };

        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Children = { icon, label }
        };

        var tile = new Border
        {
            Width = 104,
            Height = 110,
            Padding = new Thickness(6, 8),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = stack,
            Tag = path
        };

        tile.ContextMenu = BuildTileContextMenu(tile, path, isDirectory);

        // Hover repaint must respect BOTH the singular selection
        // (_selectedTile) and the marquee-tinted set. Without the marquee
        // check, dragging a rubber-band rectangle across tiles makes the
        // pointer-enter handler overwrite the marquee tint with the
        // lighter hover colour, and pointer-exit then resets to
        // transparent - so any tile the pointer physically passed over
        // appeared "deselected" even though HitTestMarqueeLive still
        // had it in _marqueeSelected. The live hit-test treats
        // "already selected + still inside" as a no-op, so the tint is
        // never restored on subsequent ticks. Easiest fix: skip the
        // hover repaint entirely while the tile is marquee-tinted.
        tile.PointerEntered += (_, _) =>
        {
            if (_selectedTile == tile) return;
            if (_marqueeSelected.Contains(tile)) return;
            tile.Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
        };
        tile.PointerExited += (_, _) =>
        {
            if (_selectedTile == tile) return;
            if (_marqueeSelected.Contains(tile)) return;
            tile.Background = Brushes.Transparent;
        };

        tile.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            var props = e.GetCurrentPoint(tile).Properties;

            // Ctrl+left-click: additive multi-select. Toggles the clicked
            // tile in/out of the marquee set without disturbing tiles that
            // are already in it. This is the standard convention every
            // shell file browser uses (Finder Cmd-click / Explorer Ctrl-
            // click) and it composes cleanly with the existing marquee:
            // any tile in _marqueeSelected, regardless of whether it got
            // there via rubber-band drag, Ctrl+A, or Ctrl-click, is a
            // valid target for the Copy/Cut/Delete batch operations
            // (CollectOperationTargets + CollectKeyboardTargets already
            // key off _marqueeSelected.Count > 1).
            //
            // The singular _selectedTile is also rolled into the set on
            // the first Ctrl-click so it doesn't visually "disappear"
            // when the user starts building a multi-selection from an
            // existing single click - matches every host OS.
            if (props.IsLeftButtonPressed &&
                e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                ToggleTileInMarquee(tile);
                // Don't arm a drag-out on Ctrl-click - the user is
                // building a selection, not preparing to drag.
                return;
            }

            // Right-click on a tile that's part of an existing marquee
            // multi-selection: PRESERVE the marquee so the user's intent
            // (right-click these N items) survives. The previous code
            // unconditionally collapsed back to a singular selection via
            // SelectTile, which calls ClearMarqueeSelection - so by the
            // time the ContextMenu.Opening handler ran, _marqueeSelected
            // was empty and the menu fell into its single-item branch.
            // That's the "mass select N tiles, right click, reverts to
            // 1 selected" bug. Mirror this by only running the singular
            // SelectTile path on left-click OR on a right-click that
            // landed OUTSIDE the marquee set.
            if (props.IsRightButtonPressed &&
                _marqueeSelected.Contains(tile) &&
                _marqueeSelected.Count > 1)
            {
                // Keep the marquee tint + selection intact; the per-tile
                // ContextMenu.Opening below will see the multi-state and
                // pluralise its labels accordingly.
                return;
            }
            SelectTile(tile, path, isDirectory);

            // Arm a potential drag-out (to a desktop layer or another
            // open explorer). Capture is deferred to the first PointerMoved
            // past the threshold so a simple click + a double-click both
            // resolve normally.
            ArmTileDragOut(tile, path, isDirectory, e);
        };

        tile.PointerMoved += (_, e) =>
        {
            if (_tileDragSource != tile || !_tileDragArmed) return;
            UpdateTileDragOut(e);
        };

        tile.PointerReleased += (_, e) =>
        {
            if (_tileDragSource != tile) return;
            FinishTileDragOut(e);
        };

        // Click directly on the label while THIS tile is already the
        // selected one starts an inline rename - same convention as the
        // desktop and as Windows/macOS Finder. The first click selects
        // the tile (handled by tile.PointerPressed); the next click on
        // the label hits this handler. Clicks on the icon never start
        // rename - reserved for activation / drag.
        label.PointerPressed += (_, e) =>
        {
            if (_selectedTile == tile)
            {
                e.Handled = true;
                BeginInlineRename(tile, stack, label, path, isDirectory);
            }
        };

        // DoubleTapped is Avalonia's built-in double-click detector - it
        // honours the platform's double-click time + distance thresholds
        // instead of the brittle 350-ms ad-hoc clock the previous version
        // used. That clock was randomly missing the second click on slower
        // pointers (especially in picker mode where the first click also
        // triggers the details-panel slide-in animation), which made
        // folders look un-navigable.
        tile.DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            ActivateTile(path, isDirectory);
        };

        return tile;
    }

    /// <summary>
    /// Swaps the tile's label TextBlock for a TextBox pre-filled with the
    /// current name. Commits on Enter / focus-loss, cancels on Escape.
    /// On commit the file/folder is renamed on disk and the explorer is
    /// refreshed (which rebuilds the tile with the new name in the
    /// correct sort position).
    /// </summary>
    private void BeginInlineRename(Border tile, StackPanel stack, TextBlock label,
                                   string path, bool isDirectory)
    {
        var labelIndex = stack.Children.IndexOf(label);
        if (labelIndex < 0) return;
        var oldName = Path.GetFileName(path);

        var editor = new TextBox
        {
            Text = oldName,
            FontSize = 11,
            Padding = new Thickness(4, 2),
            MinWidth = 92,
            MaxWidth = 96,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            AcceptsReturn = false,
            Margin = new Thickness(0, 6, 0, 0),
        };

        bool finished = false;
        void Restore() { stack.Children[labelIndex] = label; }

        async Task CommitAsync()
        {
            if (finished) return;
            finished = true;

            var newName = (editor.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(newName) || newName == oldName) { Restore(); return; }
            if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                Restore();
                if (Content is Panel host)
                    await DOSIDialog.Alert(host, "Invalid name",
                        "That name contains characters that aren't allowed.");
                return;
            }

            var parent = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(parent)) { Restore(); return; }
            var newPath = Path.Combine(parent, newName);

            if (File.Exists(newPath) || Directory.Exists(newPath))
            {
                Restore();
                if (Content is Panel host)
                    await DOSIDialog.Alert(host, "Name in use",
                        "An item with that name already exists in this folder.");
                return;
            }

            try
            {
                if (isDirectory) Directory.Move(path, newPath);
                else             File.Move(path, newPath);
                _statusSelection.Text = $"Renamed to: {newName}";
                Refresh();
            }
            catch (Exception ex)
            {
                Restore();
                if (Content is Panel host)
                    await DOSIDialog.Alert(host, "Couldn't rename", ex.Message);
            }
        }

        void Cancel()
        {
            if (finished) return;
            finished = true;
            Restore();
        }

        editor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; _ = CommitAsync(); }
            else if (e.Key == Key.Escape) { e.Handled = true; Cancel(); }
        };
        editor.LostFocus += (_, _) => _ = CommitAsync();

        stack.Children[labelIndex] = editor;
        // Pre-select just the stem so a straight type-over keeps the
        // file extension intact - same convention as Windows / macOS.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            editor.Focus();
            var stem = Path.GetFileNameWithoutExtension(oldName);
            editor.SelectionStart = 0;
            editor.SelectionEnd = string.IsNullOrEmpty(stem) ? (editor.Text ?? string.Empty).Length : stem.Length;
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void SelectTile(Border tile, string path, bool isDirectory)
    {
        // Any prior marquee tint is replaced by the singular selection -
        // a normal click should never leave ghost tints behind.
        ClearMarqueeSelection();

        if (_selectedTile != null && _selectedTile != tile)
            _selectedTile.Background = Brushes.Transparent;

        _selectedTile = tile;
        var a = Accents.AccentPrimary;
        tile.Background = new SolidColorBrush(Color.FromArgb(80, a.R, a.G, a.B));

        var info = isDirectory
            ? "Folder"
            : FormatSize(SafeFileSize(path));
        _statusSelection.Text = $"{Path.GetFileName(path)}  \u2022  {info}";

        ShowDetailsPanel(path, isDirectory);
        UpdatePickerActionState();
    }

    private void ClearSelection()
    {
        if (_selectedTile != null)
        {
            _selectedTile.Background = Brushes.Transparent;
            _selectedTile = null;
        }
        // Wipe rubber-band tint too - otherwise tiles selected by a
        // previous marquee stay highlighted forever and any subsequent
        // single-click only releases the singular selection while the
        // ghost marquee tints remain. This was the source of the
        // "glitchy" feel.
        ClearMarqueeSelection();
        _statusSelection.Text = "";
        HideDetailsPanel();
        UpdatePickerActionState();
    }

    private void ActivateTile(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            Navigate(path);
            return;
        }

        // Picker mode: hand the path back to the caller and close the
        // window. The caller is responsible for whatever happens next
        // (set as wallpaper, attach to email, etc.).
        if (_pickerCallback != null)
        {
            var cb = _pickerCallback;
            _pickerCallback = null; // guard against double-fire
            try { cb(path); }
            finally { _ = PlayCloseAnimationAsync().ContinueWith(_ => { }); }
            return;
        }

        // File-association routing: ask the loaded application registry
        // whether any installed app claims this extension. The IDE (which
        // used to be hard-coded here) now ships as a per-user application
        // and registers itself for .cs / .dosiform / .dosiapp / .json /
        // .txt / .md via IDOSIApp.CanOpenFile - so removing the IDE just
        // means the file falls through to the metadata fallback instead
        // of crashing.
        var ext = Path.GetExtension(path);
        var pluginApp = LoadedAppRegistry.FindForFile(ext);
        if (pluginApp != null)
        {
            if (pluginApp.Activate() is DOSIWindow appWindow)
            {
                pluginApp.OpenPath(appWindow, path);
                WindowManager.Instance?.OpenWindow(appWindow);
            }
            return;
        }

        // Image files open in the DOSIImageViewer. Built-in default app -
        // not migrated to a plug-in because it ships with the OS.
        if (IsImageExtension(ext))
        {
            var viewer = new DOSIImageViewer(path);
            WindowManager.Instance?.OpenWindow(viewer);
            return;
        }

        // Fallback: show a small info dialog with the file's metadata.
        var size = FormatSize(SafeFileSize(path));
        var modified = SafeLastWriteTime(path);
        var msg = $"Path:     {RelativeToRoot(path)}\nSize:     {size}\nModified: {modified:g}";

        if (Content is Panel panelHost)
            _ = DOSIDialog.Alert(panelHost, Path.GetFileName(path), msg);
    }

    /// <summary>
    /// Extensions the image viewer owns. Sourced from the viewer's own
    /// SupportedExtensions list so adding a format there picks up the
    /// double-click association here automatically.
    /// </summary>
    private static bool IsImageExtension(string ext) =>
        DOSIImageViewer.SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Switches this explorer into file-picker mode. Files whose extension
    /// is not in <paramref name="extensions"/> are hidden; double-clicking
    /// a visible file invokes <paramref name="onPicked"/> with its absolute
    /// path and closes the window. Cancelling (closing the window without
    /// picking) does not invoke the callback.
    /// </summary>
    /// <param name="prompt">Title shown in the window's chrome.</param>
    /// <param name="extensions">Whitelist of file extensions, each starting with '.' (e.g. <c>.png</c>).</param>
    /// <param name="onPicked">Invoked with the chosen file's absolute path.</param>
    public void EnablePickerMode(string prompt, string[] extensions, Action<string> onPicked)
    {
        _pickerExtensions = extensions ?? Array.Empty<string>();
        _pickerCallback = onPicked;
        Title = string.IsNullOrWhiteSpace(prompt) ? "Choose a file" : prompt;
        // Surface the Cancel + Choose action buttons in the status bar.
        // Choose stays disabled until the user selects a valid file -
        // see UpdatePickerActionState.
        if (_pickerActionStack != null) _pickerActionStack.IsVisible = true;
        UpdatePickerActionState();
        // Re-render so the extension filter takes effect immediately.
        PopulateItems();
    }

    /// <summary>
    /// Enables / disables the picker-mode "Choose" button based on the
    /// current singular selection. Folders are navigation targets in
    /// picker mode, never pickable, so the button stays disabled when a
    /// folder is selected; files outside the extension whitelist are
    /// hidden by PopulateItems so any visible file selection is valid.
    /// Called from SelectTile and ClearSelection so the button tracks
    /// the user's intent live.
    /// </summary>
    private void UpdatePickerActionState()
    {
        if (_pickerChooseButton == null) return;
        if (_pickerCallback == null) { _pickerChooseButton.IsEnabled = false; return; }

        bool canPick = false;
        if (_selectedTile?.Tag is string p && File.Exists(p))
        {
            canPick = _pickerExtensions == null ||
                      _pickerExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase);
        }
        _pickerChooseButton.IsEnabled = canPick;
    }

    private static long SafeFileSize(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    private static DateTime SafeLastWriteTime(string path)
    {
        try { return File.GetLastWriteTime(path); } catch { return DateTime.MinValue; }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024d;
        if (kb < 1024) return $"{kb:0.#} KB";
        double mb = kb / 1024d;
        if (mb < 1024) return $"{mb:0.##} MB";
        double gb = mb / 1024d;
        return $"{gb:0.##} GB";
    }

    // =====================================================================
    // New folder
    // =====================================================================

    private async System.Threading.Tasks.Task CreateNewFolderAsync()
    {
        if (Content is not Panel host) return;

        var input = new DOSITextBox
        {
            FontSize = 13,
            Padding = new Thickness(10, 8),
            Width = 260,
            UseRoundedEnds = false,
            Text = "New Folder"
        };

        // Build the dialog ourselves so we can attach explicit Cancel + OK
        // buttons. The static DOSIDialog.Custom helper omits buttons by
        // design (DialogType.Custom), which is what left this prompt with
        // just a textbox and no way to confirm or cancel from the UI.
        var dialog = new DOSIDialog("New folder",
            "Enter a name for the new folder:",
            DialogType.Custom,
            input);
        dialog.AddButton("Cancel", DialogResult.Cancel, false);
        dialog.AddButton("OK", DialogResult.OK, true);
        var result = await dialog.ShowAsync(host);

        if (result != DialogResult.OK) return;

        var name = (input.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            await DOSIDialog.Alert(host, "Invalid name",
                "That folder name contains characters that aren't allowed.");
            return;
        }

        try
        {
            var newPath = Path.Combine(_currentPath, name);
            Directory.CreateDirectory(newPath);
            Refresh();
        }
        catch (Exception ex)
        {
            await DOSIDialog.Alert(host, "Couldn't create folder", ex.Message);
        }
    }

    // =====================================================================
    // Context menus
    // =====================================================================

    /// <summary>
    /// Builds the per-tile right-click menu (Open / Copy / Cut / Rename /
    /// Delete). The menu operates on the multi-selection when the
    /// right-clicked tile is part of it; otherwise it operates on just
    /// the clicked tile (after promoting it to the singular selection).
    /// Rename is suppressed for multi-select because there's no sensible
    /// way to rename N items to one name.
    /// </summary>
    private DOSIContextMenu BuildTileContextMenu(Border tile, string path, bool isDirectory)
    {
        var menu = new DOSIContextMenu();

        var open = new MenuItem { Header = "Open" };
        open.Click += (_, _) => ActivateTile(path, isDirectory);

        // "Open in new window" is folder-only - opens a fresh DOSIFileExplorer
        // navigated to this folder, leaving the current window where it is.
        // Mirrors the convention every host shell uses for power-user
        // multi-pane workflows.
        var openInNew = new MenuItem { Header = "Open in new window" };
        openInNew.Click += (_, _) =>
        {
            if (!isDirectory) return;
            try
            {
                var fresh = new DOSIFileExplorer();
                fresh.RequestNavigate(path);
                WindowManager.Instance?.OpenWindow(fresh);
            }
            catch (Exception ex) { Debug.WriteLine($"[DOSIFileExplorer] open-in-new failed: {ex.Message}"); }
        };

        // "Reveal in containing folder" navigates THIS window to the
        // file's parent and selects the file. Useful when the user
        // arrived at the tile via a flat view (search match, recent
        // files) and wants to see siblings.
        var reveal = new MenuItem { Header = "Reveal in containing folder" };
        reveal.Click += (_, _) =>
        {
            var parent = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(parent) || !IsInsideRoot(parent)) return;
            Navigate(parent);
            // After PopulateItems lands the tile is selectable; defer the
            // selection so we run after layout.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var match = _itemsPanel.Children
                    .OfType<Border>()
                    .FirstOrDefault(b => b.Tag is string s &&
                                         string.Equals(Path.GetFullPath(s),
                                                       Path.GetFullPath(path),
                                                       StringComparison.OrdinalIgnoreCase));
                if (match?.Tag is string mp) SelectTile(match, mp, Directory.Exists(mp));
            }, Avalonia.Threading.DispatcherPriority.Loaded);
        };

        var copy = new MenuItem { Header = "Copy" };
        copy.Click += (_, _) => CopySelectionToClipboard(tile, path);

        var cut = new MenuItem { Header = "Cut" };
        cut.Click += (_, _) => CutSelectionToClipboard(tile, path);

        var rename = new MenuItem { Header = "Rename" };
        rename.Click += async (_, _) => await RenamePathAsync(path);

        var delete = new MenuItem { Header = "Delete" };
        delete.Click += async (_, _) => await DeleteSelectionAsync(tile, path, isDirectory);

        menu.Items.Add(open);
        if (isDirectory)
            menu.Items.Add(openInNew);
        menu.Items.Add(reveal);
        menu.Items.Add(new Separator());
        menu.Items.Add(copy);
        menu.Items.Add(cut);
        menu.Items.Add(new Separator());
        menu.Items.Add(rename);
        menu.Items.Add(delete);

        menu.Opening += (_, _) =>
        {
            // Multi-select branch: the right-click landed inside the
            // marquee-tinted set, so the menu should target every tile
            // in it. Pluralise labels + disable single-item-only ops
            // (Rename, Open) so the user can't accidentally trigger them
            // against N files at once.
            bool isMulti = _marqueeSelected.Contains(tile) && _marqueeSelected.Count > 1;
            if (isMulti)
            {
                var n = _marqueeSelected.Count;
                copy.Header   = $"Copy {n} items";
                cut.Header    = $"Cut {n} items";
                delete.Header = $"Delete {n} items";
                open.IsEnabled = false;
                openInNew.IsEnabled = false;
                reveal.IsEnabled = false;
                rename.IsEnabled = false;
                // Don't touch _selectedTile - leave the marquee tint as-is
                // so the user can see exactly what's about to be affected.
                return;
            }

            // Singular branch: reset labels and promote the clicked tile
            // to the singular selection (which clears any leftover
            // marquee tint from a previous drag).
            copy.Header   = "Copy";
            cut.Header    = "Cut";
            delete.Header = "Delete";
            open.IsEnabled = true;
            openInNew.IsEnabled = true;
            reveal.IsEnabled = true;
            rename.IsEnabled = true;
            if (_selectedTile != tile)
                SelectTile(tile, path, isDirectory);
        };

        return menu;
    }

    /// <summary>
    /// Returns the absolute paths of every tile currently in the
    /// marquee-selected set (multi-select) or, if the marquee is empty,
    /// a one-element list containing the right-clicked tile's path. The
    /// right-clicked tile is included even when not in the marquee set
    /// so single-right-click behaves the same as it always did.
    /// </summary>
    private List<string> CollectOperationTargets(Border clickedTile, string clickedPath)
    {
        if (_marqueeSelected.Contains(clickedTile) && _marqueeSelected.Count > 1)
        {
            return _marqueeSelected
                .Select(b => b.Tag as string)
                .Where(s => !string.IsNullOrEmpty(s))
                .Cast<string>()
                .ToList();
        }
        return new List<string> { clickedPath };
    }

    private void CopySelectionToClipboard(Border tile, string path)
    {
        var targets = CollectOperationTargets(tile, path);
        FileClipboard.CopyMany(targets);
        _statusSelection.Text = targets.Count == 1
            ? $"Copied: {Path.GetFileName(targets[0])}"
            : $"Copied {targets.Count} items";
    }

    private void CutSelectionToClipboard(Border tile, string path)
    {
        var targets = CollectOperationTargets(tile, path);
        FileClipboard.CutMany(targets);
        _statusSelection.Text = targets.Count == 1
            ? $"Cut: {Path.GetFileName(targets[0])}"
            : $"Cut {targets.Count} items";
    }

    /// <summary>
    /// Multi-aware delete: trashes (or permanently deletes, inside the
    /// trash) every tile in the multi-select, falling back to single-
    /// item delete when the right-click landed outside the marquee.
    /// Confirmation is shown ONCE for the whole batch so the user isn't
    /// hammered with N dialogs.
    /// </summary>
    private async Task DeleteSelectionAsync(Border tile, string path, bool isDirectory)
    {
        var targets = CollectOperationTargets(tile, path);
        if (targets.Count == 1)
        {
            await DeletePathAsync(targets[0], isDirectory);
            return;
        }

        if (Content is not Panel host) return;
        bool insideTrash = FileTrash.IsInsideTrash(targets[0]);
        if (insideTrash)
        {
            var confirm = await DOSIDialog.Confirm(host,
                "Delete permanently?",
                $"{targets.Count} items will be permanently deleted. This can't be undone.");
            if (confirm != DialogResult.OK) return;
        }

        int ok = 0, fail = 0;
        foreach (var p in targets)
        {
            try
            {
                if (FileTrash.IsInsideTrash(p))
                {
                    if (Directory.Exists(p)) Directory.Delete(p, recursive: true);
                    else if (File.Exists(p)) File.Delete(p);
                }
                else if (_user != null)
                {
                    if (FileTrash.Send(_user, p) == null) { fail++; continue; }
                }
                // The path left its original spot - tidy the layout JSON.
                DOSI.CORE.UIComponents.WindowManagement.DesktopIconLayout
                    .ForgetIfOnDesktop(p);
                ok++;
            }
            catch { fail++; }
        }

        _statusSelection.Text = fail == 0
            ? (insideTrash ? $"Deleted: {ok} items" : $"Moved to Trash: {ok} items")
            : $"{ok} succeeded, {fail} failed";
        Refresh();
    }

    /// <summary>
    /// Builds the right-click menu shown when the user clicks empty space
    /// in the items area (no tile under the cursor): Paste / New folder /
    /// Refresh.
    /// </summary>
    private DOSIContextMenu BuildEmptyAreaContextMenu()
    {
        var menu = new DOSIContextMenu();

        var paste = new MenuItem { Header = "Paste" };
        paste.Click += async (_, _) => await PasteFromClipboardAsync();

        var newFolder = new MenuItem { Header = "New folder" };
        newFolder.Click += async (_, _) => await CreateNewFolderAsync();

        var refresh = new MenuItem { Header = "Refresh" };
        refresh.Click += (_, _) => Refresh();

        // Open the current folder in a separate explorer window. Cheap
        // power-user affordance for side-by-side browsing without having
        // to backtrack through the sidebar in two windows separately.
        var openInNew = new MenuItem { Header = "Open in new window" };
        openInNew.Click += (_, _) =>
        {
            try
            {
                var fresh = new DOSIFileExplorer();
                fresh.RequestNavigate(_currentPath);
                WindowManager.Instance?.OpenWindow(fresh);
            }
            catch (Exception ex) { Debug.WriteLine($"[DOSIFileExplorer] open-in-new failed: {ex.Message}"); }
        };

        menu.Items.Add(paste);
        menu.Items.Add(new Separator());
        menu.Items.Add(newFolder);
        menu.Items.Add(openInNew);
        menu.Items.Add(new Separator());
        menu.Items.Add(refresh);

        menu.Opening += (_, _) =>
        {
            // Disable Paste when there's nothing on the clipboard, and
            // surface the staged name (or count, on a multi-paste) so the
            // user knows what they're about to paste.
            paste.IsEnabled = FileClipboard.HasContent;
            if (!FileClipboard.HasContent)
            {
                paste.Header = "Paste";
            }
            else if (FileClipboard.Count == 1)
            {
                paste.Header = $"Paste \u201C{Path.GetFileName((FileClipboard.Path ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar))}\u201D";
            }
            else
            {
                paste.Header = $"Paste {FileClipboard.Count} items";
            }
        };

        return menu;
    }

    /// <summary>
    /// Builds a tile for a single trash manifest entry. The on-disk path
    /// still drives selection/details, but the per-tile context menu is
    /// Restore / Delete Forever instead of the normal Copy / Cut / Rename
    /// (those make no sense inside the trash). Inline rename is also
    /// suppressed for the same reason.
    /// </summary>
    private Border BuildTrashTile(TrashEntry entry, string path)
    {
        var tile = BuildTile(path, isDirectory: entry.IsDirectory);
        tile.ContextMenu = BuildTrashTileContextMenu(entry);
        return tile;
    }

    /// <summary>
    /// Per-trash-entry right-click menu: Restore (move back to original
    /// path) and Delete Forever (permanent delete via FileTrash).
    /// <para>
    /// Multi-select aware: when the right-clicked tile is part of an
    /// active marquee selection (set up by Ctrl+A or rubber-band drag),
    /// the menu pluralises its labels and the click handlers walk every
    /// selected tile - so users can mass-restore or mass-delete-forever
    /// without N right-clicks. Same shape as <see cref="BuildTileContextMenu"/>'s
    /// multi-select handling for the regular folder view.
    /// </para>
    /// </summary>
    private DOSIContextMenu BuildTrashTileContextMenu(TrashEntry entry)
    {
        var menu = new DOSIContextMenu();

        var restore = new MenuItem();
        var deleteForever = new MenuItem();

        // Pulled lazily by the click handlers so the multi/single decision
        // reflects the marquee state AT click time, not at menu-build time.
        Border? clickedTileRef = null;
        List<TrashEntry> ResolveTargets()
        {
            if (clickedTileRef != null &&
                _marqueeSelected.Contains(clickedTileRef) &&
                _marqueeSelected.Count > 1)
            {
                var list = new List<TrashEntry>();
                foreach (var b in _marqueeSelected)
                {
                    if (b.Tag is not string p) continue;
                    var resolved = ResolveTrashEntryForPath(p);
                    if (resolved != null) list.Add(resolved);
                }
                return list;
            }
            return new List<TrashEntry> { entry };
        }

        restore.Click += (_, _) =>
        {
            if (_user == null) return;
            var targets = ResolveTargets();
            int ok = 0;
            string? lastDest = null;
            foreach (var t in targets)
            {
                var dest = FileTrash.Restore(_user, t.Id);
                if (dest != null) { ok++; lastDest = dest; }
            }
            _statusSelection.Text = ok switch
            {
                0 => "Nothing restored.",
                1 => $"Restored: {Path.GetFileName(lastDest ?? string.Empty)}",
                _ => $"Restored: {ok} items"
            };
            Refresh();
        };

        deleteForever.Click += async (_, _) =>
        {
            if (_user == null) return;
            if (Content is not Panel host) return;
            var targets = ResolveTargets();
            var confirm = await DOSIDialog.Confirm(host,
                "Delete permanently?",
                targets.Count == 1
                    ? $"\u201C{targets[0].Name}\u201D will be permanently deleted. This can't be undone."
                    : $"{targets.Count} items will be permanently deleted. This can't be undone.");
            if (confirm != DialogResult.OK) return;
            int ok = 0;
            foreach (var t in targets)
            {
                if (FileTrash.DeleteForever(_user, t.Id)) ok++;
            }
            _statusSelection.Text = ok == targets.Count
                ? (ok == 1 ? $"Deleted forever: {targets[0].Name}" : $"Deleted forever: {ok} items")
                : $"{ok} of {targets.Count} deleted";
            Refresh();
        };

        menu.Items.Add(restore);
        menu.Items.Add(new Separator());
        menu.Items.Add(deleteForever);

        menu.Opening += (_, _) =>
        {
            // The marquee set holds Border references but the trash entry
            // we were built for is fixed at construction. Resolve the
            // clicked tile by scanning _itemsPanel for the Border whose
            // Tag matches our entry's on-disk path (cheap - trash views
            // are small).
            clickedTileRef = FindTileForEntry(entry);

            bool isMulti = clickedTileRef != null &&
                           _marqueeSelected.Contains(clickedTileRef) &&
                           _marqueeSelected.Count > 1;
            if (isMulti)
            {
                var n = _marqueeSelected.Count;
                restore.Header = $"Restore {n} items";
                deleteForever.Header = $"Delete {n} items forever";
            }
            else
            {
                restore.Header = $"Restore to {Path.GetDirectoryName(entry.OriginalPath)}";
                deleteForever.Header = "Delete forever";
            }
        };

        return menu;
    }

    /// <summary>
    /// Maps a trash-view tile's path back to its <see cref="TrashEntry"/>.
    /// The on-disk layout is <c>&lt;trash&gt;/items/&lt;id&gt;/&lt;name&gt;</c>
    /// so the id is just the parent-directory name; we look the resulting
    /// id up against <see cref="FileTrash.List"/> to recover the full entry
    /// (which carries the OriginalPath needed for Restore).
    /// </summary>
    private TrashEntry? ResolveTrashEntryForPath(string path)
    {
        if (_user == null || string.IsNullOrEmpty(path)) return null;
        try
        {
            var parent = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(parent)) return null;
            var id = Path.GetFileName(parent);
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var e in FileTrash.List(_user))
            {
                if (string.Equals(e.Id, id, StringComparison.Ordinal)) return e;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Scans the live tiles for the one whose Tag matches the given trash entry's resolved path.</summary>
    private Border? FindTileForEntry(TrashEntry entry)
    {
        if (_user == null) return null;
        var p = FileTrash.ResolveItemPath(_user, entry);
        foreach (var tile in _itemsPanel.Children.OfType<Border>())
        {
            if (tile.Tag is string tp &&
                string.Equals(tp, p, StringComparison.OrdinalIgnoreCase))
                return tile;
        }
        return null;
    }

    /// <summary>
    /// Trash-view replacement for the regular empty-area context menu.
    /// Replaces Paste / New folder (neither makes sense in the bin) with
    /// Empty Trash + Refresh.
    /// </summary>
    private DOSIContextMenu BuildTrashEmptyAreaContextMenu()
    {
        var menu = new DOSIContextMenu();

        var empty = new MenuItem { Header = "Empty Trash" };
        empty.Click += async (_, _) =>
        {
            if (_user == null) return;
            if (Content is not Panel host) return;
            var count = FileTrash.List(_user).Count;
            if (count == 0)
            {
                _statusSelection.Text = "Trash is already empty.";
                return;
            }
            var confirm = await DOSIDialog.Confirm(host,
                "Empty Trash?",
                $"{count} item{(count == 1 ? "" : "s")} will be permanently deleted. This can't be undone.");
            if (confirm != DialogResult.OK) return;
            var removed = FileTrash.EmptyAll(_user);
            _statusSelection.Text = $"Emptied: {removed} item{(removed == 1 ? "" : "s")}";
            Refresh();
        };

        // Bulk restore: walks the manifest and Restores every entry. Each
        // restore lands at its original path (with the usual collision
        // suffix when something already exists there). No confirmation -
        // restoring is non-destructive by nature.
        var restoreAll = new MenuItem { Header = "Restore all" };
        restoreAll.Click += (_, _) =>
        {
            if (_user == null) return;
            var all = FileTrash.List(_user).ToList();
            if (all.Count == 0)
            {
                _statusSelection.Text = "Trash is already empty.";
                return;
            }
            int ok = 0;
            foreach (var e in all)
            {
                if (FileTrash.Restore(_user, e.Id) != null) ok++;
            }
            _statusSelection.Text = ok == all.Count
                ? $"Restored: {ok} item{(ok == 1 ? "" : "s")}"
                : $"Restored {ok} of {all.Count}";
            Refresh();
        };

        var refresh = new MenuItem { Header = "Refresh" };
        refresh.Click += (_, _) => Refresh();

        menu.Items.Add(empty);
        menu.Items.Add(restoreAll);
        menu.Items.Add(new Separator());
        menu.Items.Add(refresh);

        menu.Opening += (_, _) =>
        {
            bool hasItems = _user != null && FileTrash.List(_user).Count > 0;
            empty.IsEnabled = hasItems;
            restoreAll.IsEnabled = hasItems;
        };

        return menu;
    }

    /// <summary>
    /// Soft-deletes <paramref name="path"/> by moving it into the user's
    /// trash. Honours sandbox bounds (refuses to touch anything outside
    /// the user's root). If we're already INSIDE the trash, deletion is
    /// permanent (no recursive bin-of-bins). Surfaces errors via a dialog
    /// rather than crashing the explorer.
    /// </summary>
    private async Task DeletePathAsync(string path, bool isDirectory)
    {
        if (Content is not Panel host) return;
        if (!IsInsideRoot(path)) return;
        var name = Path.GetFileName(path);

        // Inside the trash view, "Delete" means permanently delete.
        // Confirm with the user because this branch is truly destructive.
        if (FileTrash.IsInsideTrash(path))
        {
            var confirm = await DOSIDialog.Confirm(host,
                "Delete permanently?",
                isDirectory
                    ? $"\u201C{name}\u201D will be permanently deleted along with everything in it. This can't be undone."
                    : $"\u201C{name}\u201D will be permanently deleted. This can't be undone.");
            if (confirm != DialogResult.OK) return;

            try
            {
                if (isDirectory) Directory.Delete(path, recursive: true);
                else             File.Delete(path);
                DOSI.CORE.UIComponents.WindowManagement.DesktopIconLayout
                    .ForgetIfOnDesktop(path);
                _statusSelection.Text = $"Deleted: {name}";
                Refresh();
            }
            catch (Exception ex)
            {
                await DOSIDialog.Alert(host, "Couldn't delete", ex.Message);
            }
            return;
        }

        // Soft-delete: move to trash. No confirm dialog - the action is
        // reversible from the Trash view, same as every modern OS.
        if (_user == null) return;
        var trashed = FileTrash.Send(_user, path);
        if (trashed == null)
        {
            await DOSIDialog.Alert(host, "Couldn't delete",
                "The item couldn't be moved to the trash.");
            return;
        }
        // The item left disk - drop its desktop-layout entry (no-op if
        // it wasn't on a desktop folder) so the JSON doesn't accumulate
        // stale rows and a same-name re-create auto-places fresh.
        DOSI.CORE.UIComponents.WindowManagement.DesktopIconLayout
            .ForgetIfOnDesktop(path);
        _statusSelection.Text = $"Moved to Trash: {name}";
        Refresh();
    }

    /// <summary>
    /// Prompts for a new name and renames the entry on disk. Bails on
    /// invalid characters or a collision so the user can re-attempt
    /// without a half-completed move.
    /// </summary>
    private async Task RenamePathAsync(string path)
    {
        if (Content is not Panel host) return;
        if (!IsInsideRoot(path)) return;

        var oldName = Path.GetFileName(path);
        var input = new DOSITextBox
        {
            FontSize = 13,
            Padding = new Thickness(10, 8),
            Width = 280,
            UseRoundedEnds = false,
            Text = oldName
        };

        var dialog = new DOSIDialog("Rename",
            "Enter a new name:",
            DialogType.Custom,
            input);
        dialog.AddButton("Cancel", DialogResult.Cancel, false);
        dialog.AddButton("OK", DialogResult.OK, true);
        var result = await dialog.ShowAsync(host);
        if (result != DialogResult.OK) return;

        var newName = (input.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(newName) || newName == oldName) return;
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            await DOSIDialog.Alert(host, "Invalid name",
                "That name contains characters that aren't allowed.");
            return;
        }

        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent)) return;
        var newPath = Path.Combine(parent, newName);

        if (File.Exists(newPath) || Directory.Exists(newPath))
        {
            await DOSIDialog.Alert(host, "Name in use",
                "An item with that name already exists in this folder.");
            return;
        }

        try
        {
            if (Directory.Exists(path)) Directory.Move(path, newPath);
            else                        File.Move(path, newPath);
            // Keep the desktop-icon layout JSON in sync when the renamed
            // entry lives on a desktop folder: carry its saved position
            // onto the new name. Pure no-op when the path isn't on a
            // desktop folder, so it's safe to call unconditionally.
            DOSI.CORE.UIComponents.WindowManagement.DesktopIconLayout
                .RenameIfOnDesktop(path, newPath);
            _statusSelection.Text = $"Renamed to: {newName}";
            Refresh();
        }
        catch (Exception ex)
        {
            await DOSIDialog.Alert(host, "Couldn't rename", ex.Message);
        }
    }

    // =====================================================================
    // Details panel
    // =====================================================================

    /// <summary>
    /// Builds the slide-in details panel that shows the selected item's icon,
    /// name, kind, size, modified date, and relative path. Lives at the left
    /// edge of the items area and is hidden off-screen via TranslateTransform
    /// until <see cref="ShowDetailsPanel"/> animates it in.
    /// </summary>
    private Border BuildDetailsPanel()
    {
        _detailsIconHost = new Panel
        {
            Width = 84,
            Height = 84,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 18, 0, 14)
        };

        _detailsName = new TextBlock
        {
            Text = string.Empty,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextPrimaryBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 3,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(14, 0, 14, 4)
        };

        _detailsKind = new TextBlock
        {
            Text = string.Empty,
            FontSize = 11,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.85,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(14, 0, 14, 16)
        };

        var divider = new Border
        {
            Height = 1,
            Margin = new Thickness(16, 4, 16, 12),
            Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255))
        };

        _detailsSizeRow = BuildDetailsRow();
        _detailsModifiedRow = BuildDetailsRow();
        _detailsPathRow = BuildDetailsRow(wrap: true);

        var rows = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 10,
            Margin = new Thickness(16, 0, 16, 16),
            Children =
            {
                BuildDetailsLabeled("Size", _detailsSizeRow),
                BuildDetailsLabeled("Modified", _detailsModifiedRow),
                BuildDetailsLabeled("Where", _detailsPathRow)
            }
        };

        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Children = { _detailsIconHost, _detailsName, _detailsKind, divider, rows }
        };

        var scroller = new DOSIScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            ShowScrollButtons = false
        };

        _detailsTranslate = new TranslateTransform(DetailsPanelWidth, 0);

        var panel = new Border
        {
            Width = DetailsPanelWidth,
            Background = Accents.WindowChromeBrush,
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(1, 0, 0, 0),
            // Square corners on all sides so the panel sits flush against
            // the surrounding chrome.
            CornerRadius = new CornerRadius(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsVisible = false,
            RenderTransform = _detailsTranslate,
            ClipToBounds = true,
            Child = scroller
        };

        // Swallow pointer presses so clicking inside the panel doesn't bubble
        // up to the items-area handler that would otherwise clear selection
        // and immediately animate the panel back out.
        panel.PointerPressed += (_, e) => e.Handled = true;

        return panel;
    }

    private static TextBlock BuildDetailsRow(bool wrap = false) => new()
    {
        FontSize = 11,
        Foreground = AccentManager.Instance.TextSecondaryBrush,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
        Opacity = 0.95
    };

    private static Control BuildDetailsLabeled(string label, TextBlock value)
    {
        var labelText = new TextBlock
        {
            Text = label.ToUpperInvariant(),
            FontSize = 9,
            FontWeight = FontWeight.SemiBold,
            Foreground = AccentManager.Instance.TextSecondaryBrush,
            Opacity = 0.55,
            LetterSpacing = 1,
            Margin = new Thickness(0, 0, 0, 3)
        };
        return new StackPanel
        {
            Orientation = Orientation.Vertical,
            Children = { labelText, value }
        };
    }

    private void ShowDetailsPanel(string path, bool isDirectory)
    {
        if (_detailsPanel == null) return;

        UpdateDetailsContent(path, isDirectory);

        if (_detailsOpen) return;
        _detailsOpen = true;
        _detailsPanel.IsVisible = true;
        // The items area already reserves the panel's width permanently
        // (set once in the ctor), so we don't toggle Padding here - that
        // used to reflow the WrapPanel on every click and make tiles
        // visibly jump around. The panel just slides in via its
        // TranslateTransform over the already-reserved strip.
        AnimateDetailsPanel(opening: true);
    }

    private void HideDetailsPanel()
    {
        if (!_detailsOpen) return;
        _detailsOpen = false;
        // No padding mutation - see ShowDetailsPanel. The reserved strip
        // stays clear when no tile is selected, same as Finder/Explorer.
        AnimateDetailsPanel(opening: false);
    }

    /// <summary>
    /// Quick Look preview overlay. Toggled by Space on the singular
    /// selection. Shows a centered card with the file's name + a
    /// kind-appropriate preview (bitmap for images, head-of-file text
    /// for plain-text formats, generic icon + size/path for anything
    /// else). Dismissed by Space / Escape / clicking the dimmed
    /// backdrop - same gestures every Quick Look in the wild ships.
    /// </summary>
    private void OpenQuickLook(string path, bool isDirectory)
    {
        CloseQuickLook();

        var nameText = new TextBlock
        {
            Text = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)),
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12)
        };

        Control previewBody;
        try
        {
            if (isDirectory)
            {
                int count = 0;
                try { count = Directory.EnumerateFileSystemEntries(path).Count(); } catch { }
                previewBody = new TextBlock
                {
                    Text = count == 1 ? "1 item" : $"{count} items",
                    FontSize = 14,
                    Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 24, 0, 24)
                };
            }
            else if (IsImageExtensionForQuickLook(Path.GetExtension(path)))
            {
                // Use the shared ImageCache so a Quick Look on a 24 MP
                // phone photo doesn't freeze the dispatcher for ~half a
                // second decoding the source - the popup is at most
                // 560x360 anyway, so we cap at 1600 px long edge for a
                // crisp preview that still composites cheaply.
                var img = new Image
                {
                    MaxWidth = 560,
                    MaxHeight = 360,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                DOSI.CORE.ImageManagement.ImageCache.LoadAsync(path, 1600, bmp =>
                {
                    // The overlay may have been dismissed before the
                    // decode finished - only assign if we're still
                    // attached to the visual tree.
                    if (bmp != null && img.Parent != null)
                        img.Source = bmp;
                });
                previewBody = img;
            }
            else if (IsTextExtensionForQuickLook(Path.GetExtension(path)))
            {
                // Head-of-file text preview. Cap at 8 KB so a large
                // log doesn't lock the UI thread - most "is this the
                // right file?" decisions are made from the first
                // screenful anyway.
                string head;
                using (var sr = new StreamReader(path))
                {
                    char[] buf = new char[8 * 1024];
                    int read = sr.Read(buf, 0, buf.Length);
                    head = new string(buf, 0, read);
                }
                previewBody = new TextBlock
                {
                    Text = head,
                    FontFamily = new FontFamily("Consolas, Menlo, monospace"),
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromArgb(235, 235, 235, 235)),
                    TextWrapping = TextWrapping.NoWrap,
                    MaxWidth = 560,
                    MaxHeight = 360
                };
            }
            else
            {
                long size = 0;
                try { size = new FileInfo(path).Length; } catch { }
                previewBody = new TextBlock
                {
                    Text = $"No preview available\n{FormatSize(size)}",
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 24, 0, 24)
                };
            }
        }
        catch (Exception ex)
        {
            previewBody = new TextBlock
            {
                Text = $"Preview failed: {ex.Message}",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 200, 200)),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 24, 0, 24)
            };
        }

        var hint = new TextBlock
        {
            Text = "Press Space or Esc to close",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 0)
        };

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 22, 26, 34)),
            BorderBrush = new SolidColorBrush(
                Color.FromArgb(180, Accents.AccentPrimary.R, Accents.AccentPrimary.G, Accents.AccentPrimary.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(28, 22),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 640,
            Child = new StackPanel
            {
                Spacing = 6,
                Children = { nameText, previewBody, hint }
            }
        };

        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = card
        };
        overlay.PointerPressed += (_, ev) =>
        {
            if (ReferenceEquals(ev.Source, overlay)) CloseQuickLook();
        };
        // Escape key while overlay is up dismisses. Routed through the
        // overlay so it doesn't trip explorer-level shortcuts.
        overlay.KeyDown += (_, ev) =>
        {
            if (ev.Key == Key.Escape || ev.Key == Key.Space)
            {
                ev.Handled = true;
                CloseQuickLook();
            }
        };
        card.PointerPressed += (_, ev) => ev.Handled = true;
        overlay.Focusable = true;

        _overlayHost.Children.Add(overlay);
        _quickLookOverlay = overlay;
        overlay.Focus();
    }

    private void CloseQuickLook()
    {
        if (_quickLookOverlay == null) return;
        var overlay = _quickLookOverlay;
        _quickLookOverlay = null;
        _overlayHost.Children.Remove(overlay);
    }

    private static bool IsImageExtensionForQuickLook(string ext) =>
        !string.IsNullOrEmpty(ext) && (
            ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".webp", StringComparison.OrdinalIgnoreCase));

    private static bool IsTextExtensionForQuickLook(string ext) =>
        !string.IsNullOrEmpty(ext) && (
            ext.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".log", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".xml", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".cs", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".yml", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".ini", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".cfg", StringComparison.OrdinalIgnoreCase));

    private void UpdateDetailsContent(string path, bool isDirectory)
    {
        if (_detailsIconHost == null || _detailsName == null || _detailsKind == null ||
            _detailsSizeRow == null || _detailsModifiedRow == null || _detailsPathRow == null)
            return;
        // header reads as a "preview" without us re-implementing geometry.
        // For image files, swap the generic icon for an actual thumbnail
        // of the file so the details panel doubles as a Quick-Look. Best-
        // effort: any IO/decode error falls back to the generic icon path.
        _detailsIconHost.Children.Clear();
        Control? icon = null;
        if (!isDirectory && IsImageExtension(Path.GetExtension(path)))
        {
            try
            {
                // Use the shared ImageCache so the details preview is
                // produced at a small thumbnail size (~320 px long edge)
                // off the UI thread. Without this, clicking a large
                // photo in the explorer's list view used to freeze the
                // dispatcher for several hundred ms while the full-res
                // JPEG decoded and then the result was scaled down to
                // a 100 px preview anyway.
                var img = new Image
                {
                    Stretch = Stretch.Uniform,
                    Width = 100,
                    Height = 100,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var pathSnapshot = path;
                DOSI.CORE.ImageManagement.ImageCache.LoadAsync(
                    path,
                    DOSI.CORE.ImageManagement.ImageCache.ThumbnailMaxDimension,
                    bmp =>
                    {
                        // The preview panel may have moved on to another
                        // file before the decode finished; only apply
                        // the bitmap if we're still showing the same one.
                        if (!ReferenceEquals(img.Parent, _detailsIconHost)) return;
                        if (bmp != null) img.Source = bmp;
                    });
                icon = img;
            }
            catch
            {
                // Corrupted / unreadable image - fall through to the
                // generic file icon.
                icon = null;
            }
        }
        icon ??= isDirectory ? BuildFolderIcon() : BuildFileIcon(Path.GetExtension(path));
        if (icon is Control c && c is not Image)
        {
            // Only scale the vector icon - the bitmap preview is already
            // sized to the panel, and scaling it further looks soft.
            c.RenderTransform = new ScaleTransform(1.45, 1.45);
            c.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        }
        _detailsIconHost.Children.Add(icon);

        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name)) name = path;
        _detailsName.Text = name;
        _detailsKind.Text = isDirectory
            ? "Folder"
            : DescribeFileKind(Path.GetExtension(path));

        if (isDirectory)
        {
            try
            {
                var count = Directory.EnumerateFileSystemEntries(path).Count();
                _detailsSizeRow.Text = $"{count} item{(count == 1 ? "" : "s")}";
            }
            catch { _detailsSizeRow.Text = "—"; }
        }
        else
        {
            _detailsSizeRow.Text = FormatSize(SafeFileSize(path));
        }

        var modified = SafeLastWriteTime(path);
        _detailsModifiedRow.Text = modified == DateTime.MinValue
            ? "—"
            : modified.ToString("g");

        _detailsPathRow.Text = RelativeToRoot(path);
    }

    private void AnimateDetailsPanel(bool opening)
    {
        if (_detailsPanel == null || _detailsTranslate == null) return;

        const double duration = 220;

        var startX = _detailsTranslate.X;
        var targetX = opening ? 0d : DetailsPanelWidth;
        var startTime = DateTime.UtcNow;

        _detailsAnimTimer?.Stop();
        _detailsAnimTimer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _detailsAnimTimer.Tick += (_, _) =>
        {
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            var t = Math.Clamp(elapsed / duration, 0d, 1d);
            // Ease-out cubic on open, ease-in quad on close - same feel as
            // the desktop apps menu animation.
            var eased = opening
                ? 1 - Math.Pow(1 - t, 3)
                : t * t;

            _detailsTranslate.X = startX + (targetX - startX) * eased;

            if (t >= 1d)
            {
                _detailsAnimTimer?.Stop();
                _detailsAnimTimer = null;
                if (!opening && _detailsPanel != null)
                    _detailsPanel.IsVisible = false;
            }
        };
        _detailsAnimTimer.Start();
    }

    private static string DescribeFileKind(string extension)
    {
        var ext = (extension ?? string.Empty).TrimStart('.').ToUpperInvariant();
        return ext switch
        {
            "" => "File",
            "TXT" => "Plain text document",
            "MD" => "Markdown document",
            "CS" => "C# source file",
            "JSON" => "JSON document",
            "XML" => "XML document",
            "HTML" or "HTM" => "HTML document",
            "CSS" => "Stylesheet",
            "JS" or "TS" => $"{ext} source file",
            "PNG" or "JPG" or "JPEG" or "GIF" or "BMP" or "WEBP" => $"{ext} image",
            "MP3" or "WAV" or "FLAC" or "OGG" => $"{ext} audio",
            "MP4" or "MKV" or "AVI" or "MOV" or "WEBM" => $"{ext} video",
            "PDF" => "PDF document",
            "ZIP" or "RAR" or "7Z" or "TAR" or "GZ" => $"{ext} archive",
            _ => $"{ext} file"
        };
    }

    // =====================================================================
    // Theming
    // =====================================================================

    private void OnAccentChanged(object? sender, EventArgs e)
    {
        // Chrome surfaces.
        if (_toolbar != null) _toolbar.Background = Accents.WindowChromeBrush;
        if (_sidebar != null) _sidebar.Background = Accents.WindowChromeBrush;
        if (_itemsArea != null) _itemsArea.Background = Accents.WindowContentBrush;
        if (_statusBar != null) _statusBar.Background = Accents.WindowChromeBrush;

        // Toolbar tool buttons - their glyph foreground was a captured brush.
        foreach (var (_, glyph) in _toolButtons)
            glyph.Foreground = Accents.TextPrimaryBrush;

        // Marquee + multi-select tint: the shared brushes get recoloured
        // in place so every tile currently tinted by them updates in one
        // assignment, with no visual-tree walk needed.
        var a = Accents.AccentPrimary;
        if (_marqueeSelectionFill != null)
            _marqueeSelectionFill.Color = Color.FromArgb(80, a.R, a.G, a.B);
        if (_marqueeRectFill != null)
            _marqueeRectFill.Color = Color.FromArgb(40, a.R, a.G, a.B);
        if (_marqueeRectStroke != null)
            _marqueeRectStroke.Color = Color.FromArgb(180, a.R, a.G, a.B);

        // Breadcrumb / status text. Breadcrumb is a StackPanel of
        // segments now, so re-tint per child rather than as a single
        // TextBlock.
        UpdateBreadcrumb();
        _statusItemCount.Foreground = Accents.TextSecondaryBrush;
        _statusSelection.Foreground = Accents.TextSecondaryBrush;

        // Rebuild sidebar to refresh accent-colored glyphs and labels.
        BuildSidebar();
        RefreshSidebarHighlight();

        // Repopulate items so folder icons / file extension labels re-tint.
        PopulateItems();

        // Re-tint the details panel chrome. Content (large icon + text rows)
        // gets refreshed naturally on the next selection.
        if (_detailsPanel != null)
        {
            _detailsPanel.Background = Accents.WindowChromeBrush;
            _detailsPanel.BorderBrush = new SolidColorBrush(
                Color.FromArgb(60, 255, 255, 255));
        }
        if (_detailsName != null) _detailsName.Foreground = Accents.TextPrimaryBrush;
        if (_detailsKind != null) _detailsKind.Foreground = Accents.TextSecondaryBrush;
        if (_detailsSizeRow != null) _detailsSizeRow.Foreground = Accents.TextSecondaryBrush;
        if (_detailsModifiedRow != null) _detailsModifiedRow.Foreground = Accents.TextSecondaryBrush;
        if (_detailsPathRow != null) _detailsPathRow.Foreground = Accents.TextSecondaryBrush;
    }

    // =====================================================================
    // Visual builders
    // =====================================================================

    private Border BuildToolButton(string glyph, string tooltip)
    {
        var text = new TextBlock
        {
            Text = glyph,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextPrimaryBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var b = new Border
        {
            Width = 30,
            Height = 28,
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
            Child = text,
            Tag = true // enabled state
        };

        ToolTip.SetTip(b, tooltip);

        b.PointerEntered += (_, _) =>
        {
            if (b.Tag is true)
                b.Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
        };
        b.PointerExited += (_, _) =>
        {
            if (b.Tag is true)
                b.Background = Brushes.Transparent;
        };

        _toolButtons.Add((b, text));
        return b;
    }

    private static void SetToolButtonEnabled(Border button, bool enabled)
    {
        button.Tag = enabled;
        button.Opacity = enabled ? 1.0 : 0.35;
        button.Cursor = enabled ? new Cursor(StandardCursorType.Hand) : new Cursor(StandardCursorType.Arrow);
        if (!enabled) button.Background = Brushes.Transparent;
    }

    private Control BuildFolderIcon()
    {
        var a = Accents.AccentPrimary;
        var b = Accents.AccentSecondary;

        var body = new Border
        {
            Width = 56,
            Height = 42,
            CornerRadius = new CornerRadius(4, 6, 6, 6),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(255, a.R, a.G, a.B), 0),
                    new GradientStop(Color.FromArgb(255, b.R, b.G, b.B), 1)
                }
            },
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var tab = new Border
        {
            Width = 22,
            Height = 6,
            Background = new SolidColorBrush(Color.FromArgb(255, a.R, a.G, a.B)),
            CornerRadius = new CornerRadius(2, 2, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(2, 0, 0, 0)
        };

        var grid = new Grid
        {
            Width = 56,
            Height = 48,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        grid.Children.Add(tab);
        grid.Children.Add(body);
        return grid;
    }

    private Control BuildFileIcon(string extension)
    {
        var ext = (extension ?? string.Empty).TrimStart('.').ToUpperInvariant();
        if (ext.Length > 4) ext = ext[..4];

        var page = new Border
        {
            Width = 44,
            Height = 52,
            Background = new SolidColorBrush(Color.FromArgb(245, 245, 247, 252)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom
        };

        var label = new TextBlock
        {
            Text = string.IsNullOrEmpty(ext) ? "FILE" : ext,
            FontSize = 9,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Accents.AccentPrimary),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 6)
        };

        var corner = new Polygon
        {
            Points = new Avalonia.Collections.AvaloniaList<Point>
            {
                new Point(44 - 12, 0),
                new Point(44, 0),
                new Point(44, 12)
            },
            Fill = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0))
        };

        var grid = new Grid
        {
            Width = 44,
            Height = 52,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        grid.Children.Add(page);
        grid.Children.Add(corner);
        grid.Children.Add(label);
        return grid;
    }

    private static Control CreateAppIcon()
    {
        var a = AccentManager.Instance.AccentPrimary;
        var border = new Border
        {
            Width = 16,
            Height = 12,
            CornerRadius = new CornerRadius(1, 2, 2, 2),
            Background = new SolidColorBrush(a),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var tab = new Border
        {
            Width = 7,
            Height = 2,
            Background = new SolidColorBrush(a),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = new CornerRadius(1, 1, 0, 0)
        };
        var grid = new Grid { Width = 16, Height = 14 };
        grid.Children.Add(tab);
        grid.Children.Add(border);
        return grid;
    }
}
