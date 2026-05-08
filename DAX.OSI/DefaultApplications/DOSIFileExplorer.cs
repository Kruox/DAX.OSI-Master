using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DOSI.CORE.AccentManagement;
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
    private readonly TextBlock _breadcrumb;
    private readonly Border _backButton;
    private readonly Border _forwardButton;
    private readonly Border _upButton;
    private readonly Border _refreshButton;
    private readonly Border _newFolderButton;

    private readonly StackPanel _sidebarItems;
    private readonly WrapPanel _itemsPanel;
    private readonly DOSIScrollViewer _itemsScroller;
    private readonly TextBlock _statusItemCount;
    private readonly TextBlock _statusSelection;

    // Themed chrome surfaces - kept as fields so OnAccentChanged can re-theme them.
    private Border? _toolbar;
    private Border? _sidebar;
    private Border? _itemsArea;
    private Border? _statusBar;
    private readonly List<(Border Button, TextBlock Glyph)> _toolButtons = new();

    private Border? _selectedTile;

    // ----- Picker mode (file-open dialog) -----
    // When non-null, the explorer behaves as a modal-style file picker:
    // files outside the extension whitelist are hidden, double-clicking a
    // file invokes the callback + closes the window, and the title bar
    // shows the supplied prompt instead of "Files". Folders stay clickable
    // so the user can still navigate. Set via EnablePickerMode.
    private string[]? _pickerExtensions;
    private Action<string>? _pickerCallback;

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

        var navGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _backButton, _forwardButton, _upButton, _refreshButton }
        };

        var toolbarGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
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
        toolbarGrid.Children.Add(_newFolderButton); Grid.SetColumn(_newFolderButton, 2);

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
        _breadcrumb = new TextBlock
        {
            Text = "",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextSecondaryBrush,
            Margin = new Thickness(14, 8, 14, 4),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
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

        var itemsArea = new Border
        {
            Background = Accents.WindowContentBrush,
            Child = _itemsScroller
        };
        _itemsArea = itemsArea;

        // Click on empty area clears selection.
        itemsArea.PointerPressed += (_, e) =>
        {
            if (e.Source == itemsArea || e.Source == _itemsPanel || e.Source == _itemsScroller)
                ClearSelection();
        };

        // Wrap the items area so the details panel can slide in over it
        // without disturbing the WrapPanel layout. The panel is anchored to
        // the right of the contentArea (and spans BOTH rows so its top
        // edge sits flush against the toolbar instead of leaving a gap
        // where the breadcrumb row peeks through).
        _detailsPanel = BuildDetailsPanel();

        var contentArea = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*")
        };
        contentArea.Children.Add(_breadcrumb); Grid.SetRow(_breadcrumb, 0);
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
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(14, 6)
        };
        statusGrid.Children.Add(_statusItemCount); Grid.SetColumn(_statusItemCount, 0);
        statusGrid.Children.Add(_statusSelection); Grid.SetColumn(_statusSelection, 1);

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

        Content = rootGrid;

        AttachedToVisualTree += (_, _) => Accents.AccentChanged += OnAccentChanged;
        DetachedFromVisualTree += (_, _) =>
        {
            Accents.AccentChanged -= OnAccentChanged;
            _detailsAnimTimer?.Stop();
            _detailsAnimTimer = null;
        };

        Navigate(_rootPath, recordHistory: false);
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
    }

    private string RelativeToRoot(string path)
    {
        var rel = Path.GetRelativePath(_rootPath, path);
        if (rel == "." || string.IsNullOrEmpty(rel)) return "~";
        return "~" + Path.DirectorySeparatorChar + rel;
    }

    private void UpdateBreadcrumb()
    {
        var rel = RelativeToRoot(_currentPath);
        _breadcrumb.Text = rel.Replace(Path.DirectorySeparatorChar.ToString(), "  \u203A  ");
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

    private void PopulateItems()
    {
        _itemsPanel.Children.Clear();
        _selectedTile = null;
        _statusSelection.Text = "";
        HideDetailsPanel();

        IEnumerable<string> dirs = Array.Empty<string>();
        IEnumerable<string> files = Array.Empty<string>();
        try
        {
            dirs = Directory.EnumerateDirectories(_currentPath)
                .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);
            files = Directory.EnumerateFiles(_currentPath)
                .Where(f => !Path.GetFileName(f).Equals(_user?.Username + ".json",
                                                        StringComparison.OrdinalIgnoreCase))
                // In picker mode, hide everything that doesn't match the
                // extension whitelist so the user can't accidentally pick
                // an incompatible file. Folders are always shown so they
                // can keep navigating.
                .Where(f => _pickerExtensions == null ||
                            _pickerExtensions.Contains(Path.GetExtension(f),
                                                       StringComparer.OrdinalIgnoreCase))
                .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);
        }
        catch { /* unreadable folder; show nothing */ }

        int dirCount = 0, fileCount = 0;
        foreach (var d in dirs) { _itemsPanel.Children.Add(BuildTile(d, isDirectory: true)); dirCount++; }
        foreach (var f in files) { _itemsPanel.Children.Add(BuildTile(f, isDirectory: false)); fileCount++; }

        _statusItemCount.Text = $"{dirCount + fileCount} item{((dirCount + fileCount) == 1 ? "" : "s")}";
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

        tile.PointerEntered += (_, _) =>
        {
            if (_selectedTile != tile)
                tile.Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
        };
        tile.PointerExited += (_, _) =>
        {
            if (_selectedTile != tile)
                tile.Background = Brushes.Transparent;
        };

        tile.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            SelectTile(tile, path, isDirectory);
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

    private void SelectTile(Border tile, string path, bool isDirectory)
    {
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
    }

    private void ClearSelection()
    {
        if (_selectedTile != null)
        {
            _selectedTile.Background = Brushes.Transparent;
            _selectedTile = null;
        }
        _statusSelection.Text = "";
        HideDetailsPanel();
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

        // File-association routing: source / project files open in the
        // DOSIIDE just like double-clicking them in a desktop OS would. We
        // launch the IDE through WindowManager so it gets the same chrome,
        // taskbar entry, and z-order behaviour as any other DOSI app.
        var ext = Path.GetExtension(path);
        if (IsIdeFileExtension(ext))
        {
            var ide = new DOSIIDE();
            ide.RequestOpen(path);
            DOSI.CORE.UIComponents.WindowManagement.WindowManager.Instance?.OpenWindow(ide);
            return;
        }

        // Image files open in the DOSIImageViewer. Same convention as the
        // IDE branch above - one entry point per file family, easy to extend.
        if (IsImageExtension(ext))
        {
            var viewer = new DOSIImageViewer(path);
            DOSI.CORE.UIComponents.WindowManagement.WindowManager.Instance?.OpenWindow(viewer);
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
    /// Extensions the IDE owns. Centralised so adding a new file type the
    /// IDE understands (say, .dosiapp) is a one-line change rather than a
    /// search across every potential opener.
    /// </summary>
    private static bool IsIdeFileExtension(string ext) =>
        ext.Equals(".cs",       StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".dosiform", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".dosiapp",  StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".json",     StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".txt",      StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".md",       StringComparison.OrdinalIgnoreCase);

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
        // Re-render so the extension filter takes effect immediately.
        PopulateItems();
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

        var result = await DOSIDialog.Custom(host, "New folder",
            "Enter a name for the new folder:", input);

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
        // Reserve the panel's width on the items area so tiles reflow
        // into the still-visible region. Without this the panel slides in
        // OVER the right-edge tiles, which means a second click on those
        // tiles lands on the panel (or empty space) instead of the tile -
        // and Avalonia's DoubleTapped requires both clicks on the same
        // visual, so the folder appeared un-navigable. Also gives the
        // tile under the cursor a stable hit-target during the slide-in.
        if (_itemsArea != null)
            _itemsArea.Padding = new Thickness(0, 0, DetailsPanelWidth, 0);
        AnimateDetailsPanel(opening: true);
    }

    private void HideDetailsPanel()
    {
        if (!_detailsOpen) return;
        _detailsOpen = false;
        // Release the reserved width so the items area goes back to
        // using the full content area.
        if (_itemsArea != null)
            _itemsArea.Padding = new Thickness(0);
        AnimateDetailsPanel(opening: false);
    }

    private void UpdateDetailsContent(string path, bool isDirectory)
    {
        if (_detailsIconHost == null || _detailsName == null || _detailsKind == null ||
            _detailsSizeRow == null || _detailsModifiedRow == null || _detailsPathRow == null)
            return;

        // Big icon: clone the tile-sized builder and scale it up so the
        // header reads as a "preview" without us re-implementing geometry.
        _detailsIconHost.Children.Clear();
        var icon = isDirectory ? BuildFolderIcon() : BuildFileIcon(Path.GetExtension(path));
        if (icon is Control c)
        {
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

        // Breadcrumb / status text.
        _breadcrumb.Foreground = Accents.TextSecondaryBrush;
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
