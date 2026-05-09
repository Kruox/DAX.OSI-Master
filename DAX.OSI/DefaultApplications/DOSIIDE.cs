using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DOSI.CORE.AccentManagement;
using DOSI.CORE.ProjectSystem;
using DOSI.CORE.Security;
using DOSI.CORE.UIComponents;
using DOSI.CORE.UIComponents.WindowManagement;
using DOSI.CORE.UserManagement;
using IOPath = System.IO.Path;

namespace DAX.OSI.DefaultApplications;

/// <summary>
/// A Visual Studio-style integrated development environment for DOSI.
/// Sandboxed to the signed-in user's home folder. Includes:
///   - Toolbar  : New / Open / Save / Save All
///   - Sidebar  : "Solution Explorer" tree of the user's home folder
///   - Tabs     : multi-file editing with dirty markers and close buttons
///   - Editor   : custom DOSICodeEditor (line numbers, caret, scrollbar)
///   - Status   : caret position, encoding, file path
/// </summary>
public class DOSIIDE : DOSIWindow
{
    private static AccentManager Accents => AccentManager.Instance;

    private readonly DOSIUser? _user;
    private readonly string _rootPath;

    // Chrome surfaces (kept as fields so OnAccentChanged can re-theme them).
    private Border? _toolbar;
    private Border? _sidebar;
    private Border? _tabsBar;
    private Border? _editorArea;
    private Border? _statusBar;
    private Border? _sidebarHeader;
    private TextBlock? _sidebarHeaderText;
    private readonly List<(Border Button, TextBlock Glyph, TextBlock Label)> _toolButtons = new();

    // Sidebar (file tree)
    private readonly StackPanel _treeRoot;
    private readonly Dictionary<string, StackPanel> _treeFolderChildren = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _expandedFolders = new(StringComparer.OrdinalIgnoreCase);

    // Tabs / editor
    private readonly StackPanel _tabStrip;
    private readonly Grid _editorHost;
    private readonly TextBlock _placeholder;
    private readonly List<EditorTab> _tabs = new();
    private EditorTab? _activeTab;

    // Status bar
    private readonly TextBlock _statusFilePath;
    private readonly TextBlock _statusCaret;
    private readonly TextBlock _statusEncoding;

    // Active project (the one whose folder owns the most-recently-focused file).
    private DOSIProject? _activeProject;
    private readonly TextBlock _statusProject;

    // Output pane (build messages, stdout, run preview)
    private Border? _outputPane;
    private Border? _outputHeaderBar;
    private TextBlock? _outputHeader;
    private DOSICodeEditor? _outputLog;
    private Border? _runPreviewHost;
    private Grid? _runPreviewContent;

    // Standalone DOSIWindow that hosts the most recent run (so the user's
    // returned Control behaves like a real OS window: drag, resize, focus).
    private DOSIWindow? _runWindow;

    // ---- Command bar (Find / Go-to-line) ----
    // Single overlay strip at the top of the editor area used for both Find
    // (Ctrl+F) and Go-to-line (Ctrl+G). Mode is tracked by _commandMode;
    // hidden when null.
    private Border? _commandBar;
    private TextBlock? _commandLabel;
    private DOSITextBox? _commandInput;
    private TextBlock? _commandHint;
    private string? _commandMode;     // "find" or "goto" or null

    // ---- Closed-tab stack (Ctrl+Shift+T to reopen) ----
    private readonly Stack<string> _recentlyClosed = new();
    private const int MaxRecentlyClosed = 16;

    // ---- Session persistence ----
    // Sidecar JSON at <userHome>/.dosi-ide-session.json. Records the open
    // tab paths + active tab so the IDE re-opens where you left off.
    private bool _sessionRestoring;
    private string SessionFilePath =>
        IOPath.Combine(_rootPath, ".dosi-ide-session.json");

    private sealed class IdeSessionState
    {
        public List<string> OpenPaths { get; set; } = new();
        public string? ActivePath { get; set; }
    }

    // ---- Recent projects ----
    private string RecentProjectsFilePath =>
        IOPath.Combine(_rootPath, ".dosi-ide-recent.json");
    private const int MaxRecentProjects = 10;

    private sealed class RecentProjectEntry
    {
        public string Path { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public DateTime LastOpenedUtc { get; set; }
    }

    // ---- Build status spinner ----
    private Border? _spinnerHost;
    private TextBlock? _spinnerGlyph;
    private RotateTransform? _spinnerRotate;
    private Avalonia.Threading.DispatcherTimer? _spinnerTimer;

    // ---- Format-document chord state (Ctrl+K then Ctrl+D) ----
    private DateTime _ctrlKDownUtc;
    private static readonly TimeSpan ChordWindow = TimeSpan.FromSeconds(1.5);

    // ---- Quick file switcher (Ctrl+,) ----
    private Border? _switcherOverlay;
    private DOSITextBox? _switcherInput;
    private StackPanel? _switcherResults;
    private List<string> _switcherFiles = new();
    private List<(string Path, Border Row)> _switcherVisible = new();
    private int _switcherSelectedIndex;

    // ---- Tab drag-reorder state ----
    private EditorTab? _draggingTab;
    private Point _dragStartPoint;
    private bool _dragActive;
    private int _dragOriginalIndex;
    private Border? _dragInsertionIndicator;

    public DOSIIDE()
    {
        Title = "Code";
        WindowWidth = 1080;
        WindowHeight = 660;
        MinimumSize = new Size(720, 420);
        Icon = CreateAppIcon();

        _user = UserManager.CurrentUser;
        if (_user != null)
        {
            UserManager.EnsureUserSubfolders(_user);
            _rootPath = UserManager.GetUserFolder(_user.Username);
        }
        else
        {
            _rootPath = AppContext.BaseDirectory;
        }

        // ---------- Toolbar ----------
        var newProjectBtn = BuildToolButton("\u2756", "New project");
        newProjectBtn.PointerReleased += async (_, _) => await CreateNewProjectAsync();

        var newBtn = BuildToolButton("\u002B", "New file");
        newBtn.PointerReleased += async (_, _) => await CreateNewFileAsync();

        var openBtn = BuildToolButton("\u2922", "Open project");
        openBtn.PointerReleased += async (_, _) => await OpenProjectAsync();

        var saveBtn = BuildToolButton("\u2913", "Save");
        saveBtn.PointerReleased += (_, _) => SaveActive();

        var saveAllBtn = BuildToolButton("\u29C9", "Save all");
        saveAllBtn.PointerReleased += (_, _) => SaveAll();

        var buildBtn = BuildToolButton("\u2692", "Build");
        buildBtn.PointerReleased += (_, _) => RunBuildOrRun(runAfter: false);

        var runBtn = BuildToolButton("\u25B6", "Run");
        runBtn.PointerReleased += (_, _) => RunBuildOrRun(runAfter: true);

        var publishBtn = BuildToolButton("\u2191", "Publish");
        publishBtn.PointerReleased += async (_, _) => await PublishActiveProjectAsync();

        var renameBtn = BuildToolButton("\u270E", "Rename");
        renameBtn.PointerReleased += async (_, _) => await RenameActiveProjectAsync();

        var propertiesBtn = BuildToolButton("\u2699", "Properties");
        propertiesBtn.PointerReleased += (_, _) => OpenActiveProjectProperties();

        var toolGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { newProjectBtn, newBtn, openBtn, saveBtn, saveAllBtn,
                         BuildToolDivider(), buildBtn, runBtn, BuildSpinner(), publishBtn, renameBtn, propertiesBtn }
        };

        var toolbarGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(10, 6)
        };
        toolbarGrid.Children.Add(toolGroup); Grid.SetColumn(toolGroup, 0);

        var toolbar = new Border
        {
            Background = Accents.WindowChromeBrush,
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = toolbarGrid
        };
        _toolbar = toolbar;

        // ---------- Sidebar (Solution Explorer) ----------
        _treeRoot = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 0,
            Margin = new Thickness(0, 6, 0, 8)
        };
        BuildTree();

        var treeScroller = new DOSIScrollViewer
        {
            Content = _treeRoot,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            ShowScrollButtons = false
        };

        var sidebarHeaderText = new TextBlock
        {
            Text = "SOLUTION EXPLORER",
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextSecondaryBrush,
            LetterSpacing = 1.2
        };
        _sidebarHeaderText = sidebarHeaderText;
        var sidebarHeader = new Border
        {
            Padding = new Thickness(12, 8),
            Background = Accents.WindowChromeBrush,
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = sidebarHeaderText
        };
        _sidebarHeader = sidebarHeader;

        var sidebarContent = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*")
        };
        sidebarContent.Children.Add(sidebarHeader); Grid.SetRow(sidebarHeader, 0);
        sidebarContent.Children.Add(treeScroller); Grid.SetRow(treeScroller, 1);

        var sidebar = new Border
        {
            Width = 260,
            Background = Accents.WindowChromeBrush,
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = sidebarContent
        };
        _sidebar = sidebar;

        // ---------- Tab strip ----------
        _tabStrip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0
        };

        var tabsScroller = new DOSIScrollViewer
        {
            Content = _tabStrip,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            ShowScrollButtons = false,
            Height = 32
        };

        var tabsBar = new Border
        {
            Background = Accents.WindowChromeBrush,
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Height = 32,
            Child = tabsScroller
        };
        _tabsBar = tabsBar;

        // ---------- Editor host ----------
        _placeholder = new TextBlock
        {
            Text = "Open a file from the Solution Explorer to start editing.",
            FontSize = 13,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.7,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _editorHost = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _editorHost.Children.Add(_placeholder);

        // Build the Find / Go-to overlay strip once and stack it above the
        // editor host. Hidden by default; shown via ShowFindBar / ShowGotoBar.
        var commandBar = BuildCommandBar();

        var editorStack = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        editorStack.Children.Add(_editorHost);
        editorStack.Children.Add(commandBar);

        var editorArea = new Border
        {
            Background = Accents.WindowContentBrush,
            Child = editorStack
        };
        _editorArea = editorArea;

        // ---------- Status bar ----------
        _statusFilePath = new TextBlock
        {
            Text = "No file open",
            FontSize = 11,
            Foreground = new SolidColorBrush(Accents.TextOnAccent),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _statusCaret = new TextBlock
        {
            Text = "Ln 1, Col 1",
            FontSize = 11,
            Foreground = new SolidColorBrush(Accents.TextOnAccent),
            VerticalAlignment = VerticalAlignment.Center
        };
        _statusEncoding = new TextBlock
        {
            Text = "UTF-8",
            FontSize = 11,
            Foreground = new SolidColorBrush(Accents.TextOnAccent),
            VerticalAlignment = VerticalAlignment.Center
        };

        _statusProject = new TextBlock
        {
            Text = "No project",
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Accents.TextOnAccent),
            VerticalAlignment = VerticalAlignment.Center
        };

        var statusGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
            Margin = new Thickness(14, 4)
        };
        var projectWrap = new Border { Margin = new Thickness(0, 0, 16, 0), Child = _statusProject };
        statusGrid.Children.Add(projectWrap); Grid.SetColumn(projectWrap, 0);
        statusGrid.Children.Add(_statusFilePath); Grid.SetColumn(_statusFilePath, 1);

        var caretWrap = new Border { Margin = new Thickness(16, 0, 0, 0), Child = _statusCaret };
        statusGrid.Children.Add(caretWrap); Grid.SetColumn(caretWrap, 2);

        var encWrap = new Border { Margin = new Thickness(16, 0, 0, 0), Child = _statusEncoding };
        statusGrid.Children.Add(encWrap); Grid.SetColumn(encWrap, 3);

        var statusBar = new Border
        {
            Height = 24,
            Background = new SolidColorBrush(Accents.AccentPrimary),
            Child = statusGrid
        };
        _statusBar = statusBar;

        // ---------- Output / Run preview pane ----------
        _outputHeader = new TextBlock
        {
            Text = "OUTPUT",
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextSecondaryBrush,
            LetterSpacing = 1.2,
            VerticalAlignment = VerticalAlignment.Center
        };
        var hideOutputBtn = new TextBlock
        {
            Text = "\u2715",
            FontSize = 11,
            Foreground = Accents.TextSecondaryBrush,
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center
        };
        hideOutputBtn.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            if (_outputPane != null) _outputPane.IsVisible = false;
        };

        var outputHeaderGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(12, 8)
        };
        outputHeaderGrid.Children.Add(_outputHeader); Grid.SetColumn(_outputHeader, 0);
        outputHeaderGrid.Children.Add(hideOutputBtn); Grid.SetColumn(hideOutputBtn, 1);

        _outputLog = new DOSICodeEditor
        {
            FontSize = 12,
            IsReadOnly = true,
            Text = string.Empty
        };

        _runPreviewContent = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Accents.WindowContentBrush
        };
        _runPreviewHost = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Background = Accents.WindowContentBrush,
            IsVisible = false,
            Child = _runPreviewContent
        };

        var outputBody = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto")
        };
        outputBody.Children.Add(_outputLog); Grid.SetRow(_outputLog, 0);
        outputBody.Children.Add(_runPreviewHost); Grid.SetRow(_runPreviewHost, 1);

        var outputContent = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*")
        };
        var outputHeaderBorder = new Border
        {
            Background = Accents.WindowChromeBrush,
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            BorderThickness = new Thickness(0, 1, 0, 1),
            Child = outputHeaderGrid
        };
        _outputHeaderBar = outputHeaderBorder;
        outputContent.Children.Add(outputHeaderBorder); Grid.SetRow(outputHeaderBorder, 0);
        outputContent.Children.Add(outputBody); Grid.SetRow(outputBody, 1);

        _outputPane = new Border
        {
            Height = 200,
            Background = Accents.WindowContentBrush,
            IsVisible = false,
            // Clip child content (the code-editor render) so long output
            // doesn't bleed past the pane and overpaint the status bar.
            ClipToBounds = true,
            Child = outputContent
        };

        // ---------- Layout ----------
        var rightStack = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto")
        };
        rightStack.Children.Add(tabsBar); Grid.SetRow(tabsBar, 0);
        rightStack.Children.Add(editorArea); Grid.SetRow(editorArea, 1);
        rightStack.Children.Add(_outputPane); Grid.SetRow(_outputPane, 2);

        var bodyGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*")
        };
        bodyGrid.Children.Add(sidebar); Grid.SetColumn(sidebar, 0);
        bodyGrid.Children.Add(rightStack); Grid.SetColumn(rightStack, 1);

        var rootGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto")
        };
        rootGrid.Children.Add(toolbar); Grid.SetRow(toolbar, 0);
        rootGrid.Children.Add(bodyGrid); Grid.SetRow(bodyGrid, 1);
        rootGrid.Children.Add(statusBar); Grid.SetRow(statusBar, 2);

        Content = rootGrid;

        AttachedToVisualTree += (_, _) =>
        {
            Accents.AccentChanged += OnAccentChanged;
            // Restore tabs from the previous session at Loaded priority so the
            // tab strip is fully wired up before any OpenFile call runs.
            Avalonia.Threading.Dispatcher.UIThread.Post(LoadSession,
                Avalonia.Threading.DispatcherPriority.Loaded);
        };
        DetachedFromVisualTree += (_, _) => Accents.AccentChanged -= OnAccentChanged;

        // Tunnel keyboard shortcuts so they fire even when the code editor has focus.
        AddHandler(KeyDownEvent, OnIdeShortcut,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private async void OnIdeShortcut(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        // Ctrl+S       -> Save active file
        // Ctrl+Shift+S -> Save all
        // Ctrl+W       -> Close active tab
        // Ctrl+B       -> Build
        // F5           -> Build & run
        // Ctrl+N       -> New file
        // Ctrl+Shift+N -> New project
        // Ctrl+O       -> Open project
        // Ctrl+P       -> Publish
        if (ctrl && e.Key == Key.S && shift) { SaveAll(); e.Handled = true; }
        else if (ctrl && e.Key == Key.S)     { SaveActive(); e.Handled = true; }
        else if (ctrl && e.Key == Key.W)     { if (_activeTab != null) CloseTab(_activeTab); e.Handled = true; }
        else if (ctrl && e.Key == Key.B)     { RunBuildOrRun(runAfter: false); e.Handled = true; }
        else if (e.Key == Key.F5)            { RunBuildOrRun(runAfter: true); e.Handled = true; }
        else if (ctrl && e.Key == Key.N && shift) { await CreateNewProjectAsync(); e.Handled = true; }
        else if (ctrl && e.Key == Key.N)     { await CreateNewFileAsync(); e.Handled = true; }
        else if (ctrl && e.Key == Key.O)     { await OpenProjectAsync(); e.Handled = true; }
        else if (ctrl && e.Key == Key.P)     { await PublishActiveProjectAsync(); e.Handled = true; }
        else if (ctrl && e.Key == Key.F)     { ShowFindBar(); e.Handled = true; }
        else if (ctrl && e.Key == Key.G)     { ShowGotoBar(); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.T) { ReopenLastClosedTab(); e.Handled = true; }
        else if (ctrl && e.Key == Key.OemComma) { ShowQuickSwitcher(); e.Handled = true; }
        else if (ctrl && e.Key == Key.K)
        {
            // First half of the Ctrl+K, Ctrl+D format-document chord.
            _ctrlKDownUtc = DateTime.UtcNow;
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.D &&
                 (DateTime.UtcNow - _ctrlKDownUtc) < ChordWindow)
        {
            _ctrlKDownUtc = DateTime.MinValue;
            FormatActiveDocument();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _commandMode != null) { HideCommandBar(); e.Handled = true; }
        else if (e.Key == Key.Escape && _switcherOverlay?.IsVisible == true) { HideQuickSwitcher(); e.Handled = true; }
        else if (e.Key == Key.Escape && _draggingTab != null) { EndTabDrag(commit: false); e.Handled = true; }
    }

    // =====================================================================
    // Tree (project-scoped Solution Explorer)
    // =====================================================================

    private void BuildTree()
    {
        _treeRoot.Children.Clear();
        _treeFolderChildren.Clear();

        if (_activeProject == null)
        {
            _treeRoot.Children.Add(BuildEmptyState());
            return;
        }

        // Project root header (clicking does nothing - it's just the label).
        var rootHeader = BuildTreeRow(
            _activeProject.Name,
            _activeProject.FolderPath,
            isDirectory: true,
            depth: 0,
            isRoot: true);
        _treeRoot.Children.Add(rootHeader);

        var rootChildren = new StackPanel { Orientation = Orientation.Vertical };
        _treeRoot.Children.Add(rootChildren);
        _treeFolderChildren[_activeProject.FolderPath] = rootChildren;

        // Auto-expand the project root so files are visible immediately.
        _expandedFolders.Add(_activeProject.FolderPath);
        PopulateFolderInTree(_activeProject.FolderPath, rootChildren, depth: 1);
    }

    private Control BuildEmptyState()
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 8,
            Margin = new Thickness(16, 24, 16, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        stack.Children.Add(new TextBlock
        {
            Text = "No project open.",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            TextAlignment = TextAlignment.Center,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.85
        });
        stack.Children.Add(new TextBlock
        {
            Text = "Use New project to create one,\nor Open project to pick an existing one.",
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.7
        });

        var recents = LoadRecentProjects();
        if (recents.Count > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "RECENT",
                FontSize = 10,
                FontWeight = FontWeight.SemiBold,
                Foreground = Accents.TextSecondaryBrush,
                Opacity = 0.6,
                Margin = new Thickness(8, 18, 0, 4)
            });
            foreach (var entry in recents)
            {
                var row = BuildRecentProjectRow(entry);
                if (row != null) stack.Children.Add(row);
            }
        }
        return stack;
    }

    private Border? BuildRecentProjectRow(RecentProjectEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Path) || !Directory.Exists(entry.Path)) return null;

        var nameText = new TextBlock
        {
            Text = string.IsNullOrEmpty(entry.Name) ? IOPath.GetFileName(entry.Path) : entry.Name,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextPrimaryBrush
        };
        var pathText = new TextBlock
        {
            Text = entry.Path,
            FontSize = 10,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.7,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var stack = new StackPanel { Orientation = Orientation.Vertical, Children = { nameText, pathText } };

        var row = new Border
        {
            Padding = new Thickness(8, 6),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = stack
        };
        row.PointerEntered += (_, _) =>
            row.Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
        row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
        row.PointerReleased += (_, _) =>
        {
            try
            {
                var loaded = DOSIProjectManager.Load(entry.Path);
                if (loaded != null) SetActiveProject(loaded);
            }
            catch { /* ignore - row will silently no-op if the project went stale */ }
        };
        return row;
    }

    private void RefreshTree()
    {
        // Preserve expansion across rebuilds.
        BuildTree();
    }

    private void PopulateFolderInTree(string folder, StackPanel container, int depth)
    {
        container.Children.Clear();
        if (!Directory.Exists(folder)) return;

        IEnumerable<string> dirs = Array.Empty<string>();
        IEnumerable<string> files = Array.Empty<string>();
        try
        {
            dirs = Directory.EnumerateDirectories(folder)
                .Where(d =>
                {
                    var n = IOPath.GetFileName(d);
                    return !n.Equals("bin", StringComparison.OrdinalIgnoreCase)
                        && !n.Equals("obj", StringComparison.OrdinalIgnoreCase);
                })
                .OrderBy(p => IOPath.GetFileName(p), StringComparer.OrdinalIgnoreCase);
            files = Directory.EnumerateFiles(folder)
                .Where(f =>
                {
                    var n = IOPath.GetFileName(f);
                    // Hide the manifest itself - it's surfaced by the project root row.
                    if (n.EndsWith(DOSIProjectManager.ManifestExtension, StringComparison.OrdinalIgnoreCase))
                        return false;
                    return true;
                })
                .OrderBy(p => IOPath.GetFileName(p), StringComparer.OrdinalIgnoreCase);
        }
        catch { return; }

        foreach (var d in dirs)
        {
            container.Children.Add(BuildTreeRow(IOPath.GetFileName(d), d, true, depth));
            var childHost = new StackPanel { Orientation = Orientation.Vertical, IsVisible = false };
            container.Children.Add(childHost);
            _treeFolderChildren[d] = childHost;

            if (_expandedFolders.Contains(d))
            {
                childHost.IsVisible = true;
                PopulateFolderInTree(d, childHost, depth + 1);
            }
        }

        foreach (var f in files)
        {
            container.Children.Add(BuildTreeRow(IOPath.GetFileName(f), f, false, depth));
        }
    }

    private Border BuildTreeRow(string label, string path, bool isDirectory, int depth, bool isRoot = false)
    {
        var indent = depth * 14 + 6;
        var isExpanded = isDirectory && _expandedFolders.Contains(path);
        var isProjectFolder = isDirectory && DOSIProjectManager.IsProjectFolder(path);

        var twirly = new TextBlock
        {
            Text = isDirectory ? (isExpanded ? "\u25BE" : "\u25B8") : " ",
            FontSize = 10,
            Width = 12,
            Foreground = Accents.TextSecondaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };

        var glyph = new TextBlock
        {
            Text = isDirectory ? (isProjectFolder ? "\u2756" : "\u25A0") : GlyphForFile(path),
            FontSize = 11,
            Foreground = isDirectory
                ? new SolidColorBrush(isProjectFolder ? Accents.AccentSecondary : Accents.AccentPrimary)
                : Accents.TextSecondaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 6, 0)
        };

        var name = new TextBlock
        {
            Text = label,
            FontSize = 12,
            FontWeight = (isRoot || isProjectFolder) ? FontWeight.SemiBold : FontWeight.Normal,
            Foreground = Accents.TextPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(indent, 0, 8, 0),
            Children = { twirly, glyph, name }
        };

        var row = new Border
        {
            Padding = new Thickness(0, 4),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = stack,
            Tag = path
        };

        row.PointerEntered += (_, _) =>
            row.Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
        row.PointerExited += (_, _) =>
            row.Background = Brushes.Transparent;

        DateTime lastClick = DateTime.MinValue;
        row.PointerPressed += (_, e) =>
        {
            // Right-click should pop the context menu, not toggle / open.
            var props = e.GetCurrentPoint(row).Properties;
            if (props.IsRightButtonPressed) return;

            e.Handled = true;
            if (isDirectory)
            {
                ToggleFolder(path, twirly);
            }
            else
            {
                var now = DateTime.UtcNow;
                if ((now - lastClick).TotalMilliseconds < 350)
                {
                    OpenFile(path);
                    lastClick = DateTime.MinValue;
                }
                else
                {
                    OpenFile(path);
                    lastClick = now;
                }
            }
        };

        AttachTreeContextMenu(row, path, isDirectory, isRoot);

        return row;
    }

    private void ToggleFolder(string folder, TextBlock twirly)
    {
        if (!_treeFolderChildren.TryGetValue(folder, out var container)) return;

        if (_expandedFolders.Contains(folder))
        {
            _expandedFolders.Remove(folder);
            container.IsVisible = false;
            twirly.Text = "\u25B8";
        }
        else
        {
            _expandedFolders.Add(folder);
            PopulateFolderInTree(folder, container, GetDepth(folder) + 1);
            container.IsVisible = true;
            twirly.Text = "\u25BE";
        }
    }

    private int GetDepth(string folder)
    {
        var baseDir = _activeProject?.FolderPath ?? _rootPath;
        var rel = IOPath.GetRelativePath(baseDir, folder);
        if (rel == "." || string.IsNullOrEmpty(rel)) return 0;
        return rel.Count(c => c == IOPath.DirectorySeparatorChar) + 1;
    }

    private static string GlyphForFile(string path)
    {
        var ext = IOPath.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".cs" or ".js" or ".ts" or ".py" or ".rb" or ".go" or ".rs" or ".java" or ".c" or ".cpp" or ".h" => "\u25C6",
            ".dosiform" => "\u25A0",
            ".md" or ".txt" or ".log" => "\u25A1",
            ".json" or ".xml" or ".yml" or ".yaml" or ".toml" => "\u25A3",
            ".html" or ".htm" or ".css" or ".scss" => "\u25C8",
            _ => "\u25CB"
        };
    }

    // =====================================================================
    // Tabs / files
    // =====================================================================

    private sealed class EditorTab
    {
        public required string Path;
        public required Border TabBorder;
        public required TextBlock TabLabel;
        public required TextBlock DirtyMark;
        // Exactly one of Editor or Designer is non-null. Editor is used for
        // .cs / text files (DOSICodeEditor); Designer is used for .dosiform
        // visual forms. The host content for the tab is whichever is set.
        public DOSICodeEditor? Editor;
        public DOSI.CORE.Designer.DOSIDesigner? Designer;
        // Project Properties tab for editing the .dosiproj manifest. When set,
        // SaveActive/SaveAll route through the panel's own persistence path.
        public ProjectPropertiesPanel? Properties;
        // When this tab is a code-behind view for a designer tab, points back
        // to that designer tab. Save reroutes through the form parser instead
        // of writing the editor text to disk.
        public EditorTab? CodeBehindFor;
        // Optional wrapper around Editor (e.g. an events-dropdown header bar
        // above a code-behind editor). When set, this is what gets shown
        // instead of Editor directly so the header travels with the tab.
        public Control? HostShell;
        public Control HostContent => HostShell ?? (Control?)Editor ?? Designer ?? (Control)Properties!;
    }

    /// <summary>
    /// Public entry point for external launchers (e.g. the file explorer
    /// double-clicking a .dosiform / .cs file) to ask the IDE to open a
    /// specific file. The open is deferred until the IDE has been attached
    /// to the visual tree so the tab strip is fully wired up.
    /// </summary>
    public void RequestOpen(string path)
    {
        if (string.IsNullOrEmpty(path)) return;

        // Defer at Loaded priority - by the time Loaded-priority work runs,
        // any pending AttachedToVisualTree has already fired and the IDE's
        // tab strip is wired up. Works whether the caller adds the IDE to
        // the visual tree before or after this call.
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => OpenFile(path),
            Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void OpenFile(string path)
    {
        if (!File.Exists(path)) return;

        // If already open, just activate.
        var existing = _tabs.FirstOrDefault(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) { ActivateTab(existing); return; }

        // Visual form? Open the dedicated designer instead of the code editor.
        // Round-trips a .dosiform JSON document; the IDE's Run path knows how
        // to launch it via DOSIFormLoader.
        if (path.EndsWith(".dosiform", StringComparison.OrdinalIgnoreCase))
        {
            OpenDesignerFile(path);
            return;
        }

        // Project manifest? Open the Properties form instead of raw JSON.
        if (path.EndsWith(DOSIProjectManager.ManifestExtension, StringComparison.OrdinalIgnoreCase))
        {
            OpenActiveProjectProperties();
            return;
        }

        string text;
        try { text = UserVault.ReadAllText(path); }
        catch { return; }

        var editor = new DOSICodeEditor
        {
            Text = text,
            Language = IOPath.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase)
                ? "csharp" : null
        };
        editor.MarkClean();
        editor.TextChanged += (_, _) => UpdateDirtyState();
        editor.CaretChanged += (_, _) => UpdateCaretStatus();

        var label = new TextBlock
        {
            Text = IOPath.GetFileName(path),
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center
        };

        var dirtyMark = new TextBlock
        {
            Text = "",
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Accents.AccentPrimary),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0)
        };

        var closeBtn = new TextBlock
        {
            Text = "\u2715",
            FontSize = 11,
            Foreground = Accents.TextSecondaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        var contentStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { label, dirtyMark, closeBtn }
        };

        var tabBorder = new Border
        {
            Padding = new Thickness(14, 0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = contentStack
        };

        var tab = new EditorTab
        {
            Path = path,
            TabBorder = tabBorder,
            TabLabel = label,
            DirtyMark = dirtyMark,
            Editor = editor
        };

        tabBorder.PointerPressed += (_, e) =>
        {
            if (e.Source == closeBtn) { CloseTab(tab); e.Handled = true; return; }
            ActivateTab(tab);
            e.Handled = true;
        };
        closeBtn.PointerPressed += (_, e) => { CloseTab(tab); e.Handled = true; };

        _tabs.Add(tab);
        _tabStrip.Children.Add(tabBorder);
        WireTabInteraction(tab);
        ActivateTab(tab);
    }

    /// <summary>
    /// Opens a <c>.dosiform</c> visual form in the dedicated designer view
    /// (in place of the code editor). Wires the designer's Modified event to
    /// the same dirty-tracking pipeline the editor uses.
    /// </summary>
    private void OpenDesignerFile(string path)
    {
        var doc = DOSI.CORE.Designer.DOSIFormSerializer.Load(path);
        var designer = new DOSI.CORE.Designer.DOSIDesigner(doc);
        designer.MarkClean();
        designer.Modified += (_, _) => UpdateDirtyState();
        // Wire the designer's edit-handler request to the IDE so double-click
        // opens a real code-behind tab (full editor) instead of a modal.
        designer.EditHandlerRequested += (_, e) => OpenCodeBehindFor(path, e.ControlName, e.EventName);

        var label = new TextBlock
        {
            Text = IOPath.GetFileName(path),
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        var dirtyMark = new TextBlock
        {
            Text = "",
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Accents.AccentPrimary),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0)
        };
        var closeBtn = new TextBlock
        {
            Text = "\u2715",
            FontSize = 11,
            Foreground = Accents.TextSecondaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        var contentStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { label, dirtyMark, closeBtn }
        };
        var tabBorder = new Border
        {
            Padding = new Thickness(14, 0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = contentStack
        };

        var tab = new EditorTab
        {
            Path = path,
            TabBorder = tabBorder,
            TabLabel = label,
            DirtyMark = dirtyMark,
            Designer = designer
        };

        tabBorder.PointerPressed += (_, e) =>
        {
            if (e.Source == closeBtn) { CloseTab(tab); e.Handled = true; return; }
            ActivateTab(tab);
            e.Handled = true;
        };
        closeBtn.PointerPressed += (_, e) => { CloseTab(tab); e.Handled = true; };

        _tabs.Add(tab);
        _tabStrip.Children.Add(tabBorder);
        WireTabInteraction(tab);
        ActivateTab(tab);
    }

    /// <summary>
    /// If the requested handler isn't already present in the code-behind
    /// editor's buffer, append a fresh stub for it before the closing brace
    /// of the static class. Handles the "user added a new control AFTER
    /// opening the code-behind tab" path - without it, double-clicking the
    /// new control silently activates the existing tab and the user thinks
    /// the IDE forgot to generate the handler. We can't fully regenerate
    /// the buffer because that would clobber any unsaved edits.
    /// </summary>
    private static void EnsureHandlerStubInEditor(EditorTab tab,
                                                  DOSI.CORE.Designer.DOSIFormDocument doc,
                                                  string controlName, string eventName)
    {
        if (tab.Editor == null) return;

        var methodName = $"{controlName}_{eventName}";
        var current = tab.Editor.Text ?? string.Empty;
        if (current.Contains("void " + methodName + "(", StringComparison.Ordinal))
            return; // already there

        var stub = DOSI.CORE.Designer.DOSIFormCodeBehind.GenerateStub(doc, controlName, eventName);
        if (string.IsNullOrEmpty(stub)) return;

        // Insert before the LAST '}' (closing brace of the static class).
        var lastBrace = current.LastIndexOf('}');
        var updated = lastBrace < 0
            ? current + Environment.NewLine + stub + Environment.NewLine
            : current.Substring(0, lastBrace) + Environment.NewLine + stub + Environment.NewLine + current.Substring(lastBrace);

        tab.Editor.Text = updated;
    }

    /// <summary>
    /// Opens (or focuses) the code-behind tab for a given visual form. The
    /// code-behind is a synthesised C# view of all the form's event handlers
    /// in one file - dead-on what VB / WinForms have done for decades. The
    /// virtual path is "&lt;form&gt;.code" so it never collides with a real
    /// file on disk; saving routes through the form parser instead of writing
    /// the editor text out verbatim.
    /// </summary>
    private void OpenCodeBehindFor(string formPath, string controlName, string eventName)
    {
        // Find the source designer tab.
        var sourceTab = _tabs.FirstOrDefault(t =>
            t.Designer != null &&
            string.Equals(t.Path, formPath, StringComparison.OrdinalIgnoreCase));
        if (sourceTab?.Designer == null) return;

        var virtualPath = formPath + ".code";

        // If a code-behind tab is already open for this form, just focus it.
        var existing = _tabs.FirstOrDefault(t =>
            t.CodeBehindFor == sourceTab &&
            string.Equals(t.Path, virtualPath, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            // Make sure the requested handler exists in the buffer. The user
            // may have added a new control to the form AFTER opening this
            // tab, so the freshly-clicked control's stub won't be there yet.
            // We append a minimal stub if missing - we can't full-regenerate
            // without losing the user's in-flight edits.
            EnsureHandlerStubInEditor(existing, sourceTab.Designer!.Document, controlName, eventName);
            ActivateTab(existing);
            NavigateToHandler(existing, controlName, eventName);
            return;
        }

        // Generate the code-behind text from the live document so any
        // handlers the user has previously written show up immediately.
        var initialCode = DOSI.CORE.Designer.DOSIFormCodeBehind.Generate(sourceTab.Designer.Document);
        var editor = new DOSICodeEditor
        {
            Text = initialCode,
            Language = "csharp"
        };
        editor.MarkClean();
        editor.TextChanged += (_, _) => UpdateDirtyState();
        editor.CaretChanged += (_, _) => UpdateCaretStatus();

        var label = new TextBlock
        {
            Text = IOPath.GetFileName(formPath) + " [Code]",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        var dirtyMark = new TextBlock
        {
            Text = "",
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Accents.AccentPrimary),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0)
        };
        var closeBtn = new TextBlock
        {
            Text = "\u2715",
            FontSize = 11,
            Foreground = Accents.TextSecondaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        var contentStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { label, dirtyMark, closeBtn }
        };
        var tabBorder = new Border
        {
            Padding = new Thickness(14, 0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = contentStack
        };

        var tab = new EditorTab
        {
            Path = virtualPath,
            TabBorder = tabBorder,
            TabLabel = label,
            DirtyMark = dirtyMark,
            Editor = editor,
            CodeBehindFor = sourceTab
        };

        // Events dropdown: list every form-level event + every per-control
        // event the form currently exposes. Picking one jumps the caret to
        // that handler's body (auto-generating a stub first if it doesn't
        // exist yet). Sits in a thin header strip above the editor so it
        // doesn't compete with the designer's property panel.
        var eventsDropdown = new DOSI.CORE.UIComponents.DOSIDropDown
        {
            Placeholder = "Jump to event…",
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 260
        };
        RefreshCodeBehindEventsDropdown(eventsDropdown, sourceTab.Designer.Document);
        eventsDropdown.SelectionChanged += (_, label) =>
        {
            // Items are formatted "<owner>.<event>" - parse back out.
            var dot = label.IndexOf('.');
            if (dot < 0) return;
            var owner = label.Substring(0, dot);
            var ev = label.Substring(dot + 1);
            EnsureHandlerStubInEditor(tab, sourceTab.Designer!.Document, owner, ev);
            NavigateToHandler(tab, owner, ev);
            eventsDropdown.SelectedItem = null;
        };
        var headerBar = new Border
        {
            Background = Accents.WindowChromeBrush,
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 6),
            Child = eventsDropdown
        };
        var hostShell = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*")
        };
        hostShell.Children.Add(headerBar); Grid.SetRow(headerBar, 0);
        hostShell.Children.Add(editor);    Grid.SetRow(editor, 1);
        tab.HostShell = hostShell;

        // Refresh dropdown items every time the user activates this tab -
        // they may have added new controls to the designer in between.
        editor.AttachedToVisualTree += (_, _) =>
            RefreshCodeBehindEventsDropdown(eventsDropdown, sourceTab.Designer!.Document);

        tabBorder.PointerPressed += (_, e) =>
        {
            if (e.Source == closeBtn) { CloseTab(tab); e.Handled = true; return; }
            ActivateTab(tab);
            e.Handled = true;
        };
        closeBtn.PointerPressed += (_, e) => { CloseTab(tab); e.Handled = true; };

        _tabs.Add(tab);
        _tabStrip.Children.Add(tabBorder);
        WireTabInteraction(tab);
        ActivateTab(tab);

        // Land the caret on the freshly-requested handler instead of dumping
        // the user at the top of a 60-line file.
        NavigateToHandler(tab, controlName, eventName);
    }

    /// <summary>
    /// Searches <paramref name="tab"/>'s editor for the first occurrence of
    /// <c>void &lt;controlName&gt;_&lt;eventName&gt;(</c>, then advances past the
    /// opening brace + comment line so the caret lands inside the method
    /// body where the user actually writes code.
    /// </summary>
    private static void NavigateToHandler(EditorTab tab, string controlName, string eventName)
    {
        if (tab.Editor == null) return;
        var text = tab.Editor.Text ?? string.Empty;
        var needle = "void " + controlName + "_" + eventName + "(";
        var idx = text.IndexOf(needle, StringComparison.Ordinal);
        if (idx < 0) return;

        // Convert char index -> 1-based line number.
        int line = 1;
        for (int i = 0; i < idx; i++) if (text[i] == '\n') line++;

        // Method declaration is at `line`; body opens on the next line ('{'),
        // and the user-edit comment lives one line below that. Land the
        // caret on the comment line so they can immediately type / replace.
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => tab.Editor.GoToLine(line + 2, column: 9 /* indent past 8 spaces */),
            Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Repopulates <paramref name="dropdown"/> with every form-level + per-
    /// control event the supplied document exposes. Items are labelled
    /// <c>&lt;owner&gt;.&lt;event&gt;</c> so the selection handler can split
    /// them back into the (controlName, eventName) pair the rest of the
    /// IDE expects.
    /// </summary>
    private static void RefreshCodeBehindEventsDropdown(
        DOSI.CORE.UIComponents.DOSIDropDown dropdown,
        DOSI.CORE.Designer.DOSIFormDocument doc)
    {
        var items = new List<string>
        {
            "Form.Load",
            "Form.Closing",
            "Form.Closed"
        };
        foreach (var def in doc.Controls)
        {
            var entry = DOSI.CORE.Designer.DOSIDesignerControlCatalog.Find(def.Type);
            if (entry?.PrimaryEvent == null || string.IsNullOrWhiteSpace(def.Name)) continue;
            var events = entry.Events ?? new[] { entry.PrimaryEvent };
            foreach (var ev in events)
                items.Add(def.Name + "." + ev);
        }
        dropdown.SetItems(items);
    }

    private void ActivateTab(EditorTab tab)
    {
        _activeTab = tab;

        foreach (var t in _tabs)
        {
            var isActive = t == tab;
            var a = Accents.AccentPrimary;
            t.TabBorder.Background = isActive
                ? Accents.WindowContentBrush
                : Brushes.Transparent;
            t.TabBorder.BorderBrush = isActive
                ? new SolidColorBrush(a)
                : new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
            t.TabBorder.BorderThickness = isActive
                ? new Thickness(0, 2, 1, 0)
                : new Thickness(0, 0, 1, 0);
        }

        _editorHost.Children.Clear();
        _editorHost.Children.Add(tab.HostContent);
        tab.HostContent.Focus();

        SaveSession();

        // Returning to a designer tab from a code-behind tab used to leave
        // the previously-selected control still highlighted (and still
        // driving the property panel). That was confusing - the user just
        // came back from editing handler code, the designer should look
        // pristine. Clear the selection so the property panel resets to
        // form-level properties.
        tab.Designer?.ClearSelection();

        UpdateActiveProjectFor(tab.Path);
        UpdateStatusBar();
    }

    private void UpdateActiveProjectFor(string filePath)
    {
        var project = DOSIProjectManager.FindProjectFor(filePath, _rootPath);

        // No project at all? Don't disturb the currently-rooted project so the
        // user doesn't lose their tree by opening an unrelated file.
        if (project == null)
        {
            _statusProject.Text = _activeProject != null
                ? "Project: " + _activeProject.Name
                : "No project";
            return;
        }

        // Same project: just refresh the status text.
        if (_activeProject != null &&
            string.Equals(project.FolderPath, _activeProject.FolderPath, StringComparison.OrdinalIgnoreCase))
        {
            _statusProject.Text = "Project: " + _activeProject.Name;
            return;
        }

        // Switch to the new project's solution explorer.
        SetActiveProject(project);
    }

    /// <summary>Sets the project that owns the Solution Explorer view.</summary>
    private void SetActiveProject(DOSIProject? project)
    {
        _activeProject = project;
        _expandedFolders.Clear();
        if (project != null)
        {
            _expandedFolders.Add(project.FolderPath);
            TouchRecentProject(project);
        }
        BuildTree();
        _statusProject.Text = project != null ? "Project: " + project.Name : "No project";
    }

    private async System.Threading.Tasks.Task OpenProjectAsync()
    {
        if (Content is not Panel host) return;

        var projectsRoot = IOPath.Combine(_rootPath, "Projects");
        Directory.CreateDirectory(projectsRoot);
        var available = DOSIProjectManager.ListProjects(projectsRoot);

        if (available.Count == 0)
        {
            await DOSIDialog.Alert(host, "No projects found",
                "There are no projects in your Projects folder yet. " +
                "Use New project to create one.");
            return;
        }

        var list = new StackPanel { Orientation = Orientation.Vertical, Spacing = 4 };
        DOSIProject? chosen = null;
        DOSIDialog? dlg = null;

        foreach (var p in available)
        {
            var row = new Border
            {
                Padding = new Thickness(12, 8),
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
                Width = 320,
                Child = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = p.Name,
                            FontSize = 13,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = Accents.TextPrimaryBrush
                        },
                        new TextBlock
                        {
                            Text = "~" + IOPath.DirectorySeparatorChar +
                                   IOPath.GetRelativePath(_rootPath, p.FolderPath),
                            FontSize = 11,
                            Foreground = Accents.TextSecondaryBrush,
                            Opacity = 0.8
                        }
                    }
                }
            };
            row.PointerEntered += (_, _) =>
                row.Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
            row.PointerExited += (_, _) =>
                row.Background = Brushes.Transparent;
            row.PointerPressed += (_, _) =>
            {
                chosen = p;
                dlg?.Close(DialogResult.OK);
            };
            list.Children.Add(row);
        }

        dlg = new DOSIDialog("Open project", string.Empty, DialogType.Custom, list);
        dlg.AddButton("Cancel", DialogResult.Cancel);
        var result = await dlg.ShowAsync(host);

        if (result == DialogResult.OK && chosen != null)
        {
            SetActiveProject(chosen);
            var program = IOPath.Combine(chosen.FolderPath, "Program.cs");
            if (File.Exists(program)) OpenFile(program);
        }
    }

    private void CloseTab(EditorTab tab)
    {
        // TODO: prompt to save when dirty. Discarding for now.
        var idx = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        _tabStrip.Children.Remove(tab.TabBorder);

        // Remember the closed file so Ctrl+Shift+T can reopen it. Don't
        // remember code-behind tabs (auto-spawned from designers) or
        // Properties tabs (not real source files).
        if (tab.CodeBehindFor == null && tab.Properties == null &&
            !string.IsNullOrEmpty(tab.Path) && File.Exists(tab.Path))
        {
            _recentlyClosed.Push(tab.Path);
            while (_recentlyClosed.Count > MaxRecentlyClosed)
            {
                var arr = _recentlyClosed.ToArray();
                _recentlyClosed.Clear();
                for (int i = MaxRecentlyClosed - 1; i >= 0; i--) _recentlyClosed.Push(arr[i]);
                break;
            }
        }

        if (_activeTab == tab)
        {
            _activeTab = null;
            _editorHost.Children.Clear();
            if (_tabs.Count > 0)
                ActivateTab(_tabs[Math.Min(idx, _tabs.Count - 1)]);
            else
            {
                _editorHost.Children.Add(_placeholder);
                UpdateStatusBar();
            }
        }

        SaveSession();
    }

    private void UpdateDirtyState()
    {
        if (_activeTab == null) return;
        var dirty = _activeTab.Editor?.IsDirty ?? _activeTab.Designer?.IsDirty ?? _activeTab.Properties?.IsDirty ?? false;
        _activeTab.DirtyMark.Text = dirty ? "\u25CF" : "";
    }

    private void UpdateCaretStatus()
    {
        if (_activeTab == null)
        {
            _statusCaret.Text = "Ln 1, Col 1";
            return;
        }
        if (_activeTab.Editor != null)
        {
            _statusCaret.Text = $"Ln {_activeTab.Editor.CaretLine}, Col {_activeTab.Editor.CaretColumn}";
        }
        else
        {
            _statusCaret.Text = "Design view";
        }
    }

    private void UpdateStatusBar()
    {
        if (_activeTab == null)
        {
            _statusFilePath.Text = "No file open";
            _statusCaret.Text = "Ln 1, Col 1";
            return;
        }
        _statusFilePath.Text = "~" + IOPath.DirectorySeparatorChar +
            IOPath.GetRelativePath(_rootPath, _activeTab.Path);
        UpdateCaretStatus();
    }

    private void SaveActive()
    {
        if (_activeTab == null) return;

        // Project Properties tab: route through the panel's own save path.
        if (_activeTab.Properties != null)
        {
            _activeTab.Properties.Save();
            UpdateDirtyState();
            return;
        }

        try
        {
            if (_activeTab.CodeBehindFor != null && _activeTab.Editor != null)
            {
                // Code-behind save: parse the editor text, push handler bodies
                // back into the form document, then persist the .dosiform.
                var sourceTab = _activeTab.CodeBehindFor;
                var designer = sourceTab.Designer;
                if (designer != null)
                {
                    var diags = DOSI.CORE.Designer.DOSIFormCodeBehind.Parse(
                        _activeTab.Editor.Text ?? string.Empty, designer.Document);
                    if (diags.Count > 0)
                    {
                        ShowOutput();
                        foreach (var d in diags) AppendOutput("[CodeBehind] " + d);
                    }
                    UserVault.WriteAllText(sourceTab.Path,
                        DOSI.CORE.Designer.DOSIFormSerializer.Serialize(designer.Document));
                    designer.MarkClean();
                    designer.RefreshSelectedProperties();
                    _activeTab.Editor.MarkClean();
                    UpdateDirtyState();
                    return;
                }
            }

            if (_activeTab.Editor != null)
            {
                UserVault.WriteAllText(_activeTab.Path, _activeTab.Editor.Text);
                _activeTab.Editor.MarkClean();
            }
            else if (_activeTab.Designer != null)
            {
                UserVault.WriteAllText(_activeTab.Path, _activeTab.Designer.GetSerialized());
                _activeTab.Designer.MarkClean();
            }
            UpdateDirtyState();
        }
        catch { /* best-effort */ }
    }

    private void SaveAll()
    {
        foreach (var t in _tabs)
        {
            try
            {
                if (t.Editor != null && t.Editor.IsDirty)
                {
                    UserVault.WriteAllText(t.Path, t.Editor.Text);
                    t.Editor.MarkClean();
                }
                else if (t.Designer != null && t.Designer.IsDirty)
                {
                    UserVault.WriteAllText(t.Path, t.Designer.GetSerialized());
                    t.Designer.MarkClean();
                }
            }
            catch { }
        }
        UpdateDirtyState();
    }

    private async System.Threading.Tasks.Task CreateNewFileAsync()
    {
        if (Content is not Panel host) return;

        // If a project is active, default the new file into its folder so .cs
        // files are picked up by the build automatically. Otherwise fall back
        // to the user's Documents folder.
        var defaultDir = _activeProject?.FolderPath
            ?? IOPath.Combine(_rootPath, "Documents");
        var defaultName = _activeProject != null ? "NewClass.cs" : "untitled.txt";

        var name = await PromptTextAsync(host, "New file",
            $"Create a new file in '{IOPath.GetFileName(defaultDir)}'.", defaultName);
        if (name == null) return;
        if (string.IsNullOrEmpty(name)) return;
        if (name.IndexOfAny(IOPath.GetInvalidFileNameChars()) >= 0)
        {
            await DOSIDialog.Alert(host, "Invalid name",
                "That file name contains characters that aren't allowed.");
            return;
        }

        try
        {
            Directory.CreateDirectory(defaultDir);
            var fullPath = IOPath.Combine(defaultDir, name);
            if (!File.Exists(fullPath))
                UserVault.WriteAllText(fullPath, BuildNewFileContent(fullPath));

            RefreshTree();
            OpenFile(fullPath);
        }
        catch (Exception ex)
        {
            await DOSIDialog.Alert(host, "Couldn't create file", ex.Message);
        }
    }

    // =====================================================================
    // Project lifecycle
    // =====================================================================

    private async System.Threading.Tasks.Task CreateNewProjectAsync()
    {
        if (Content is not Panel host) return;

        var input = new DOSITextBox
        {
            FontSize = 13,
            Padding = new Thickness(10, 8),
            Width = 280,
            Text = "MyDosiApp"
        };

        // Project-type choice: code-only scaffolds the Program.cs entry point
        // the compiler expects; visual-form scaffolds Form1.dosiform instead
        // (which Run can launch directly via DOSIFormLoader, no compile needed).
        var codeRadio = new RadioButton
        {
            GroupName = "DosiProjectKind",
            Content = "Code project (Program.cs entry point)",
            IsChecked = true,
            Foreground = Accents.TextPrimaryBrush
        };
        var visualRadio = new RadioButton
        {
            GroupName = "DosiProjectKind",
            Content = "Visual form project (drag-and-drop designer)",
            Foreground = Accents.TextPrimaryBrush
        };

        var content = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 8,
            Children =
            {
                input,
                new TextBlock
                {
                    Text = "Project type:",
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Accents.TextSecondaryBrush,
                    Margin = new Thickness(0, 8, 0, 0)
                },
                codeRadio,
                visualRadio
            }
        };

        // Build the dialog manually so we can attach explicit Cancel / Create
        // buttons (DialogType.Custom doesn't add any by default).
        var dialog = new DOSIDialog("New project",
            "Enter a name. The project will be created in your Projects folder:",
            DialogType.Custom, content);
        dialog.AddButton("Cancel", DialogResult.Cancel, false);
        dialog.AddButton("Create", DialogResult.OK, true);

        // Convenience: pressing Enter inside the textbox submits as Create.
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                dialog.Close(DialogResult.OK);
                e.Handled = true;
            }
        };

        var result = await dialog.ShowAsync(host);
        if (result != DialogResult.OK) return;

        var name = (input.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name)) return;

        var projectsRoot = IOPath.Combine(_rootPath, "Projects");
        Directory.CreateDirectory(projectsRoot);

        var project = DOSIProjectManager.Create(projectsRoot, name, out var error);
        if (project == null)
        {
            await DOSIDialog.Alert(host, "Couldn't create project", error ?? "Unknown error.");
            return;
        }

        SetActiveProject(project);

        if (visualRadio.IsChecked == true)
        {
            // Visual form template: scaffold Form1.dosiform and drop the
            // Program.cs the manager auto-created (so the project doesn't
            // accidentally contain BOTH a .cs entry point and a .dosiform,
            // which is what the user noticed earlier - "looks like 2 projects
            // are in one solution"). Visual projects are pure-form by design;
            // if the user wants C# files later they can still add them.
            var autoProgram = IOPath.Combine(project.FolderPath, "Program.cs");
            try { if (File.Exists(autoProgram)) File.Delete(autoProgram); } catch { /* best-effort */ }

            var formPath = IOPath.Combine(project.FolderPath, "Form1.dosiform");
            UserVault.WriteAllText(formPath, BuildNewFileContent(formPath));
            RefreshTree();
            OpenFile(formPath);
        }
        else
        {
            var programPath = IOPath.Combine(project.FolderPath, "Program.cs");
            if (File.Exists(programPath)) OpenFile(programPath);
        }
    }

    /// <summary>
    /// Publishes the currently-active project to the user's app registry so it
    /// shows up in the desktop's Applications menu. Re-publishing an existing
    /// app simply refreshes its entry.
    /// </summary>
    private async System.Threading.Tasks.Task PublishActiveProjectAsync()
    {
        ShowOutput();

        if (Content is not Panel host) return;
        if (_user == null)
        {
            await DOSIDialog.Alert(host, "Can't publish",
                "You need to be signed in to publish apps.");
            return;
        }
        if (_activeProject == null)
        {
            await DOSIDialog.Alert(host, "No project",
                "Open or create a project before publishing.");
            return;
        }

        // Save first so the next launch picks up everything in the editor.
        SaveAll();

        // Sanity-check: build before publishing so we don't add a broken app.
        AppendOutput($"[Publish] {_activeProject.Name}: verifying build...");
        var build = DOSIProjectCompiler.Build(_activeProject);
        foreach (var d in build.Diagnostics) AppendOutput(d);
        if (!build.Success)
        {
            AppendOutput($"[Publish] {_activeProject.Name}: aborted (build failed).");
            await DOSIDialog.Alert(host, "Publish failed",
                "Fix the build errors shown in the Output pane and try again.");
            return;
        }

        if (!DOSIPublishedAppRegistry.Publish(_activeProject, _user))
        {
            AppendOutput($"[Publish] {_activeProject.Name}: failed to write app registry.");
            await DOSIDialog.Alert(host, "Publish failed",
                "Could not write the app registry to disk.");
            return;
        }

        AppendOutput($"[Publish] {_activeProject.Name}: added to the Applications menu.");
        await DOSIDialog.Alert(host, "Published",
            $"'{_activeProject.Name}' is now available from the Applications menu. " +
            $"It will recompile from source on every launch, so future edits go live automatically.");
    }

    /// <summary>
    /// Renames the active project. Renames the folder + manifest on disk,
    /// closes any open editor tabs from the project (to release file locks),
    /// reopens them at their new paths, and updates the published-app registry
    /// entry if this project was previously published.
    /// </summary>
    private async System.Threading.Tasks.Task RenameActiveProjectAsync()
    {
        if (Content is not Panel host) return;
        if (_activeProject == null)
        {
            await DOSIDialog.Alert(host, "No project",
                "Open or create a project before renaming.");
            return;
        }

        var newName = await PromptTextAsync(host, "Rename project",
            "Enter a new name. The project folder and manifest will be renamed.",
            _activeProject.Name);
        if (newName == null) return;
        if (string.IsNullOrEmpty(newName) ||
            string.Equals(newName, _activeProject.Name, StringComparison.Ordinal))
            return;

        // Snapshot the project state we'll need after the rename completes.
        var oldName = _activeProject.Name;
        var oldFolder = _activeProject.FolderPath;

        // Save first, then close every editor tab from this project so we don't
        // hold file handles open during Directory.Move.
        SaveAll();

        var openInProject = _tabs
            .Where(t => t.Path.StartsWith(oldFolder + IOPath.DirectorySeparatorChar,
                                          StringComparison.OrdinalIgnoreCase))
            .ToList();
        var relativePathsToReopen = openInProject
            .Select(t => IOPath.GetRelativePath(oldFolder, t.Path))
            .ToList();
        var lastFocused = _activeTab != null && openInProject.Contains(_activeTab)
            ? IOPath.GetRelativePath(oldFolder, _activeTab.Path)
            : null;

        foreach (var t in openInProject) CloseTab(t);

        var renamed = DOSIProjectManager.Rename(_activeProject, newName, out var error);
        if (renamed == null)
        {
            await DOSIDialog.Alert(host, "Couldn't rename project",
                error ?? "Unknown error.");
            // Re-open the closed tabs at their original paths so the user
            // doesn't lose their context.
            foreach (var rel in relativePathsToReopen)
            {
                var p = IOPath.Combine(oldFolder, rel);
                if (File.Exists(p)) OpenFile(p);
            }
            return;
        }

        // Keep the registry in sync if the project was published.
        DOSIPublishedAppRegistry.UpdateAfterRename(_user, oldName, oldFolder,
                                                   renamed.Name, renamed.FolderPath);

        SetActiveProject(renamed);

        // Reopen previously-open files at their new paths.
        foreach (var rel in relativePathsToReopen)
        {
            var p = IOPath.Combine(renamed.FolderPath, rel);
            if (File.Exists(p)) OpenFile(p);
        }
        if (lastFocused != null)
        {
            var focusPath = IOPath.Combine(renamed.FolderPath, lastFocused);
            var focusTab = _tabs.FirstOrDefault(t =>
                string.Equals(t.Path, focusPath, StringComparison.OrdinalIgnoreCase));
            if (focusTab != null) ActivateTab(focusTab);
        }

        ShowOutput();
        AppendOutput($"[Rename] {oldName} -> {renamed.Name}");
    }

    private void RunBuildOrRun(bool runAfter)
    {
        ShowOutput();
        StartBuildSpinner(runAfter ? "Running" : "Building");
        bool success = false;
        try
        {
            success = RunBuildOrRunCore(runAfter);
        }
        finally
        {
            StopBuildSpinner(success);
        }
    }

    private bool RunBuildOrRunCore(bool runAfter)
    {
        // Tear down any previous standalone run window before starting again.
        CloseRunWindow();
        if (_runPreviewContent != null) _runPreviewContent.Children.Clear();
        if (_runPreviewHost != null) _runPreviewHost.IsVisible = false;

        // Save dirty buffers from this project so the compiler reads the latest source.
        SaveAll();

        // Code-behind tab? Treat it as if its source designer were active.
        // SaveAll above has already parsed the editor buffer back into the
        // form document, so the source designer is current. This lets users
        // hit Build / F5 from the code-behind editor without having to flip
        // back to the designer tab first.
        var effectiveTab = _activeTab?.CodeBehindFor ?? _activeTab;

        // Visual form fast-path: if the active tab (or its source) is a
        // .dosiform document, skip the project compile and just instantiate
        // the form via the runtime loader. Lets users iterate on visual-only
        // apps without needing a Program.cs entry point.
        if (runAfter && effectiveTab?.Designer != null)
        {
            AppendOutput($"[Run] Launching visual form '{IOPath.GetFileName(effectiveTab.Path)}'...");
            try
            {
                var window = DOSI.CORE.Designer.DOSIFormLoader.Build(
                    effectiveTab.Designer.Document, out var handlerDiags);
                foreach (var d in handlerDiags) AppendOutput("[Handlers] " + d);
                LaunchPrebuiltWindow(window);
                return true;
            }
            catch (Exception ex)
            {
                AppendOutput($"[Run] Failed: {ex.Message}");
                return false;
            }
        }

        // Build button on a designer (or its code-behind) tab: compile the
        // form's handlers in isolation so the user gets early feedback on
        // syntax errors without having to actually launch the window.
        if (!runAfter && effectiveTab?.Designer != null)
        {
            AppendOutput($"[Build] Compiling form '{IOPath.GetFileName(effectiveTab.Path)}'...");
            var formCompile = DOSI.CORE.Designer.DOSIFormHandlerCompiler.Compile(
                effectiveTab.Designer.Document);
            foreach (var d in formCompile.Diagnostics) AppendOutput("[Handlers] " + d);
            AppendOutput(formCompile.Success
                ? $"[Build] '{IOPath.GetFileName(effectiveTab.Path)}': succeeded."
                : $"[Build] '{IOPath.GetFileName(effectiveTab.Path)}': FAILED.");
            return formCompile.Success;
        }

        if (_activeProject == null)
        {
            AppendOutput("[Build] No active project. Open a file inside a project first, " +
                         "or create one with the New Project button.");
            return false;
        }

        AppendOutput($"[Build] {_activeProject.Name}: starting...");

        var result = runAfter
            ? DOSIProjectCompiler.BuildAndRun(_activeProject)
            : DOSIProjectCompiler.Build(_activeProject);

        foreach (var d in result.Diagnostics)
            AppendOutput(d);

        if (!result.Success)
        {
            AppendOutput($"[Build] {_activeProject.Name}: FAILED.");
            return false;
        }

        AppendOutput($"[Build] {_activeProject.Name}: succeeded.");

        if (!runAfter) return true;

        if (!string.IsNullOrEmpty(result.Output))
        {
            AppendOutput("--- stdout ---");
            AppendOutput(result.Output.TrimEnd());
        }

        if (result.ReturnedControl != null)
        {
            LaunchRunWindow(result.ReturnedControl);
        }
        else
        {
            AppendOutput("[Run] Entry point returned no Control to display.");
        }
        return true;
    }

    /// <summary>
    /// Wraps the user's returned <see cref="Control"/> in a real <see cref="DOSIWindow"/>
    /// and registers it with the active <see cref="WindowManager"/>, so it behaves
    /// like any other DOSI app window (drag, resize, focus, close).
    /// </summary>
    private void LaunchRunWindow(Control userControl)
    {
        var manager = WindowManager.Instance;
        if (manager == null)
        {
            // No active desktop manager - fall back to the inline preview pane.
            if (_runPreviewContent != null && _runPreviewHost != null)
            {
                _runPreviewContent.Children.Clear();
                _runPreviewContent.Children.Add(userControl);
                _runPreviewHost.IsVisible = true;
                AppendOutput("[Run] No active WindowManager; preview shown inline.");
            }
            return;
        }

        // Some user controls don't bring their own background; give them the
        // standard window content surface so they look like a real DOSI app.
        // The wrapper Border sits BEHIND the user's control, so if the user
        // hard-codes a Background on what they return from Run(), their colour
        // covers this surface and remains exactly what they specified. When
        // they leave it transparent (the common case), this surface shows
        // through and we keep it in sync with the system accent below.
        var contentHost = new Border
        {
            Background = Accents.WindowContentBrush,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = userControl
        };

        EventHandler accentHandler = (_, _) =>
        {
            // Re-fetch the brush so the wrapper picks up the new accent's
            // window-content colour. The user's control is unaffected.
            contentHost.Background = Accents.WindowContentBrush;
        };
        Accents.AccentChanged += accentHandler;

        var window = new DOSIWindow
        {
            Title = _activeProject?.Name ?? "Run",
            WindowWidth = 640,
            WindowHeight = 420,
            MinimumSize = new Size(280, 180),
            Icon = CreateAppIcon(),
            Content = contentHost
        };

        // Forget the reference once the user closes it so the next Run starts
        // fresh and we don't try to close an already-disposed window.
        window.DetachedFromVisualTree += (_, _) =>
        {
            Accents.AccentChanged -= accentHandler;
            if (ReferenceEquals(_runWindow, window))
                _runWindow = null;
        };

        _runWindow = window;
        manager.OpenWindow(window);

        AppendOutput($"[Run] Launched '{window.Title}' in a new window.");
    }

    private void CloseRunWindow()
    {
        if (_runWindow == null) return;
        try { WindowManager.Instance?.CloseWindow(_runWindow); }
        catch { /* best-effort */ }
        _runWindow = null;
    }

    /// <summary>
    /// Registers a fully-built <see cref="DOSIWindow"/> with the active
    /// <see cref="WindowManager"/> as the IDE's current run target. Used by
    /// the visual-form path where the loader already returns a complete
    /// window (no UserControl wrapping needed).
    /// </summary>
    private void LaunchPrebuiltWindow(DOSIWindow window)
    {
        var manager = WindowManager.Instance;
        if (manager == null)
        {
            AppendOutput("[Run] No active WindowManager; cannot launch visual form.");
            return;
        }

        window.DetachedFromVisualTree += (_, _) =>
        {
            if (ReferenceEquals(_runWindow, window))
                _runWindow = null;
        };

        _runWindow = window;
        manager.OpenWindow(window);
        AppendOutput($"[Run] Launched '{window.Title}' in a new window.");
    }

    private void ShowOutput()
    {
        if (_outputPane != null) _outputPane.IsVisible = true;
    }

    private void AppendOutput(string line)
    {
        if (_outputLog == null) return;
        var current = _outputLog.Text;
        _outputLog.Text = string.IsNullOrEmpty(current)
            ? line
            : current + Environment.NewLine + line;
        _outputLog.MarkClean();
        // Keep the freshest line in view so long build/run logs don't appear
        // cut off behind the status bar in either windowed or fullscreen mode.
        _outputLog.ScrollToEnd();
    }

    private static Border BuildToolDivider() => new()
    {
        Width = 1,
        Height = 18,
        Margin = new Thickness(6, 0),
        Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
        VerticalAlignment = VerticalAlignment.Center
    };

    // =====================================================================
    // Solution Explorer context menu (per-row file / folder operations)
    // =====================================================================

    /// <summary>
    /// Attaches a right-click context menu to a tree row, with options that
    /// vary by node kind (file vs folder vs project root).
    /// </summary>
    private void AttachTreeContextMenu(Border row, string path, bool isDirectory, bool isRoot)
    {
        var menu = new DOSIContextMenu();

        if (isDirectory)
        {
            var newFile = new MenuItem { Header = "New file..." };
            newFile.Click += async (_, _) => await AddFileToFolderAsync(path);

            var newForm = new MenuItem { Header = "New visual form..." };
            newForm.Click += async (_, _) => await AddVisualFormToFolderAsync(path);

            var newFolder = new MenuItem { Header = "New folder..." };
            newFolder.Click += async (_, _) => await AddFolderToFolderAsync(path);

            menu.Items.Add(newFile);
            menu.Items.Add(newForm);
            menu.Items.Add(newFolder);
            menu.Items.Add(new Separator());
        }
        else
        {
            var open = new MenuItem { Header = "Open" };
            open.Click += (_, _) => OpenFile(path);
            menu.Items.Add(open);
            menu.Items.Add(new Separator());
        }

        var rename = new MenuItem { Header = isRoot ? "Rename project..." : "Rename..." };
        rename.Click += async (_, _) =>
        {
            if (isRoot) await RenameActiveProjectAsync();
            else await RenameNodeAsync(path, isDirectory);
        };

        var delete = new MenuItem { Header = isRoot ? "Delete project..." : "Delete..." };
        delete.Click += async (_, _) => await DeleteNodeAsync(path, isDirectory, isRoot);

        menu.Items.Add(rename);
        menu.Items.Add(delete);

        row.ContextMenu = menu;
    }

    /// <summary>
    /// Returns the initial contents to write to a brand-new file. For .cs
    /// files created inside a project, scaffolds a class skeleton with the
    /// standard DOSI usings so the file is immediately usable from Program.cs
    /// (no namespace, matching the project template's convention). All other
    /// file types start empty.
    /// </summary>
    private string BuildNewFileContent(string filePath)
    {
        // Visual form template: an empty document the designer can immediately
        // round-trip. Saved as JSON so it's human-readable + diff-friendly.
        if (IOPath.GetExtension(filePath).Equals(".dosiform", StringComparison.OrdinalIgnoreCase))
        {
            return DOSI.CORE.Designer.DOSIFormSerializer.Serialize(
                new DOSI.CORE.Designer.DOSIFormDocument
                {
                    Title = IOPath.GetFileNameWithoutExtension(filePath),
                    Width = 480,
                    Height = 320
                });
        }

        if (!IOPath.GetExtension(filePath).Equals(".cs", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        var project = DOSIProjectManager.FindProjectFor(filePath, _rootPath);
        if (project == null) return string.Empty;

        var className = SanitizeClassName(IOPath.GetFileNameWithoutExtension(filePath));
        return
$@"using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DOSI.CORE.AccentManagement;
using DOSI.CORE.UIComponents;

// {className} is part of the {project.Name} project.
// All .cs files in this folder are picked up automatically when you
// Build or Run, so you can use {className} from Program.cs right away.
public class {className}
{{
}}
";
    }

    /// <summary>
    /// Coerces an arbitrary file name into a valid C# identifier:
    /// strips invalid chars, prefixes a leading underscore if the first
    /// char is a digit, and falls back to "NewClass" if nothing usable remains.
    /// </summary>
    private static string SanitizeClassName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "NewClass";
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
        }
        if (sb.Length == 0) return "NewClass";
        if (char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }

    /// <summary>
    /// Modal text-input dialog with explicit Cancel + OK buttons (Enter
    /// submits, Escape cancels via <see cref="DOSIDialog"/> built-in handling).
    /// Returns the trimmed entry, or null if the user cancelled.
    /// </summary>
    private static async System.Threading.Tasks.Task<string?> PromptTextAsync(
        Panel host, string title, string message, string initial)
    {
        var input = new DOSITextBox
        {
            FontSize = 13,
            Padding = new Thickness(10, 8),
            Width = 280,
            Text = initial
        };

        var dialog = new DOSIDialog(title, message, DialogType.Custom, input);
        dialog.AddButton("Cancel", DialogResult.Cancel, false);
        dialog.AddButton("OK", DialogResult.OK, true);

        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                dialog.Close(DialogResult.OK);
                e.Handled = true;
            }
        };

        var result = await dialog.ShowAsync(host);
        if (result != DialogResult.OK) return null;
        return (input.Text ?? string.Empty).Trim();
    }

    private async System.Threading.Tasks.Task AddFileToFolderAsync(string folder)
    {
        if (Content is not Panel host) return;

        var name = await PromptTextAsync(host, "New file",
            $"Create a new file in '{IOPath.GetFileName(folder)}'.", "untitled.txt");
        if (name == null || string.IsNullOrEmpty(name)) return;
        if (name.IndexOfAny(IOPath.GetInvalidFileNameChars()) >= 0)
        {
            await DOSIDialog.Alert(host, "Invalid name",
                "That file name contains characters that aren't allowed.");
            return;
        }

        try
        {
            Directory.CreateDirectory(folder);
            var fullPath = IOPath.Combine(folder, name);
            if (!File.Exists(fullPath))
                UserVault.WriteAllText(fullPath, BuildNewFileContent(fullPath));

            _expandedFolders.Add(folder);
            RefreshTree();
            OpenFile(fullPath);
        }
        catch (Exception ex)
        {
            await DOSIDialog.Alert(host, "Couldn't create file", ex.Message);
        }
    }

    /// <summary>
    /// Convenience wrapper around <see cref="AddFileToFolderAsync"/> that
    /// pre-fills the new-file prompt with a <c>.dosiform</c> name. Created
    /// files open straight into the visual designer because OpenFile branches
    /// on the extension.
    /// </summary>
    private async System.Threading.Tasks.Task AddVisualFormToFolderAsync(string folder)
    {
        if (Content is not Panel host) return;

        var name = await PromptTextAsync(host, "New visual form",
            $"Create a new visual form in '{IOPath.GetFileName(folder)}'.", "Form1.dosiform");
        if (name == null || string.IsNullOrEmpty(name)) return;
        if (name.IndexOfAny(IOPath.GetInvalidFileNameChars()) >= 0)
        {
            await DOSIDialog.Alert(host, "Invalid name",
                "That file name contains characters that aren't allowed.");
            return;
        }
        // Append the extension if the user left it off so the IDE knows to
        // open it in the designer.
        if (!name.EndsWith(".dosiform", StringComparison.OrdinalIgnoreCase))
            name += ".dosiform";

        try
        {
            Directory.CreateDirectory(folder);
            var fullPath = IOPath.Combine(folder, name);
            if (!File.Exists(fullPath))
                UserVault.WriteAllText(fullPath, BuildNewFileContent(fullPath));

            _expandedFolders.Add(folder);
            RefreshTree();
            OpenFile(fullPath);
        }
        catch (Exception ex)
        {
            await DOSIDialog.Alert(host, "Couldn't create form", ex.Message);
        }
    }

    private async System.Threading.Tasks.Task AddFolderToFolderAsync(string parent)
    {
        if (Content is not Panel host) return;

        var name = await PromptTextAsync(host, "New folder",
            $"Create a new folder inside '{IOPath.GetFileName(parent)}'.", "NewFolder");
        if (name == null || string.IsNullOrEmpty(name)) return;
        if (name.IndexOfAny(IOPath.GetInvalidFileNameChars()) >= 0)
        {
            await DOSIDialog.Alert(host, "Invalid name",
                "That folder name contains characters that aren't allowed.");
            return;
        }

        try
        {
            var fullPath = IOPath.Combine(parent, name);
            Directory.CreateDirectory(fullPath);

            _expandedFolders.Add(parent);
            _expandedFolders.Add(fullPath);
            RefreshTree();
        }
        catch (Exception ex)
        {
            await DOSIDialog.Alert(host, "Couldn't create folder", ex.Message);
        }
    }

    private async System.Threading.Tasks.Task RenameNodeAsync(string path, bool isDirectory)
    {
        if (Content is not Panel host) return;

        var oldName = IOPath.GetFileName(path);
        var parent = IOPath.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent)) return;

        var newName = await PromptTextAsync(host,
            isDirectory ? "Rename folder" : "Rename file",
            $"Enter a new name for '{oldName}'.", oldName);
        if (newName == null || string.IsNullOrEmpty(newName)) return;
        if (string.Equals(newName, oldName, StringComparison.Ordinal)) return;
        if (newName.IndexOfAny(IOPath.GetInvalidFileNameChars()) >= 0)
        {
            await DOSIDialog.Alert(host, "Invalid name",
                "That name contains characters that aren't allowed.");
            return;
        }

        var newPath = IOPath.Combine(parent, newName);
        if (File.Exists(newPath) || Directory.Exists(newPath))
        {
            await DOSIDialog.Alert(host, "Already exists",
                $"'{newName}' already exists in this folder.");
            return;
        }

        // Save + close any tabs whose paths sit inside the rename target so we
        // don't hold open file handles during Move and so reopened tabs end up
        // pointing at the new locations.
        SaveAll();
        var sep = IOPath.DirectorySeparatorChar;
        var affected = _tabs
            .Where(t => isDirectory
                ? t.Path.StartsWith(path + sep, StringComparison.OrdinalIgnoreCase)
                : string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var relativePaths = affected
            .Select(t => isDirectory ? IOPath.GetRelativePath(path, t.Path) : oldName)
            .ToList();
        var lastFocused = _activeTab != null && affected.Contains(_activeTab);
        foreach (var t in affected) CloseTab(t);

        try
        {
            if (isDirectory) Directory.Move(path, newPath);
            else File.Move(path, newPath);
        }
        catch (Exception ex)
        {
            await DOSIDialog.Alert(host, "Couldn't rename", ex.Message);
            // Best-effort: reopen what we closed at the original paths.
            foreach (var rel in relativePaths)
            {
                var p = isDirectory ? IOPath.Combine(path, rel) : path;
                if (File.Exists(p)) OpenFile(p);
            }
            return;
        }

        // Tree expansion uses absolute paths; remap so the renamed folder
        // (and its children) stay expanded.
        if (isDirectory) RemapExpandedFolders(path, newPath);

        RefreshTree();

        // Reopen previously-open tabs at their new locations.
        EditorTab? toFocus = null;
        if (isDirectory)
        {
            foreach (var rel in relativePaths)
            {
                var p = IOPath.Combine(newPath, rel);
                if (File.Exists(p))
                {
                    OpenFile(p);
                    if (lastFocused && toFocus == null)
                        toFocus = _tabs.LastOrDefault(t =>
                            string.Equals(t.Path, p, StringComparison.OrdinalIgnoreCase));
                }
            }
        }
        else if (File.Exists(newPath))
        {
            OpenFile(newPath);
            if (lastFocused)
                toFocus = _tabs.LastOrDefault(t =>
                    string.Equals(t.Path, newPath, StringComparison.OrdinalIgnoreCase));
        }
        if (toFocus != null) ActivateTab(toFocus);
    }

    private async System.Threading.Tasks.Task DeleteNodeAsync(string path, bool isDirectory, bool isProjectRoot)
    {
        if (Content is not Panel host) return;

        var name = IOPath.GetFileName(path);
        var prompt = isProjectRoot
            ? $"Permanently delete the project '{name}' and all of its files? This can't be undone."
            : isDirectory
                ? $"Permanently delete the folder '{name}' and everything inside it? This can't be undone."
                : $"Permanently delete '{name}'? This can't be undone.";

        var confirm = await DOSIDialog.YesNo(host, "Delete", prompt);
        if (confirm != DialogResult.Yes) return;

        // Close any tabs that point at the path or any file inside it.
        var sep = IOPath.DirectorySeparatorChar;
        var affected = _tabs
            .Where(t => isDirectory
                ? (string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase)
                    || t.Path.StartsWith(path + sep, StringComparison.OrdinalIgnoreCase))
                : string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var t in affected) CloseTab(t);

        try
        {
            if (isDirectory) Directory.Delete(path, recursive: true);
            else File.Delete(path);
        }
        catch (Exception ex)
        {
            await DOSIDialog.Alert(host, "Couldn't delete", ex.Message);
            return;
        }

        // If we deleted the active project folder, drop the project context.
        if (isProjectRoot && _activeProject != null &&
            string.Equals(_activeProject.FolderPath, path, StringComparison.OrdinalIgnoreCase))
        {
            DOSIPublishedAppRegistry.Unpublish(_activeProject.Name, _user);
            SetActiveProject(null);
            return;
        }

        RefreshTree();
    }

    private void RemapExpandedFolders(string oldPath, string newPath)
    {
        var sep = IOPath.DirectorySeparatorChar;
        var snapshot = _expandedFolders.ToList();
        foreach (var folder in snapshot)
        {
            if (string.Equals(folder, oldPath, StringComparison.OrdinalIgnoreCase))
            {
                _expandedFolders.Remove(folder);
                _expandedFolders.Add(newPath);
            }
            else if (folder.StartsWith(oldPath + sep, StringComparison.OrdinalIgnoreCase))
            {
                _expandedFolders.Remove(folder);
                _expandedFolders.Add(newPath + folder[oldPath.Length..]);
            }
        }
    }

    // =====================================================================
    // Theming
    // =====================================================================

    private void OnAccentChanged(object? sender, EventArgs e)
    {
        if (_toolbar != null) _toolbar.Background = Accents.WindowChromeBrush;
        if (_sidebar != null) _sidebar.Background = Accents.WindowChromeBrush;
        if (_tabsBar != null) _tabsBar.Background = Accents.WindowChromeBrush;
        if (_editorArea != null) _editorArea.Background = Accents.WindowContentBrush;
        if (_statusBar != null) _statusBar.Background = new SolidColorBrush(Accents.AccentPrimary);
        if (_outputPane != null) _outputPane.Background = Accents.WindowContentBrush;
        if (_outputHeaderBar != null) _outputHeaderBar.Background = Accents.WindowChromeBrush;
        if (_runPreviewHost != null) _runPreviewHost.Background = Accents.WindowContentBrush;
        if (_runPreviewContent != null) _runPreviewContent.Background = Accents.WindowContentBrush;
        if (_outputHeader != null) _outputHeader.Foreground = Accents.TextSecondaryBrush;

        foreach (var (_, glyph, label) in _toolButtons)
        {
            glyph.Foreground = Accents.TextPrimaryBrush;
            label.Foreground = Accents.TextPrimaryBrush;
        }

        _statusFilePath.Foreground = new SolidColorBrush(Accents.TextOnAccent);
        _statusCaret.Foreground = new SolidColorBrush(Accents.TextOnAccent);
        _statusEncoding.Foreground = new SolidColorBrush(Accents.TextOnAccent);
        _statusProject.Foreground = new SolidColorBrush(Accents.TextOnAccent);
        _placeholder.Foreground = Accents.TextSecondaryBrush;
        if (_sidebarHeader != null) _sidebarHeader.Background = Accents.WindowChromeBrush;
        if (_sidebarHeaderText != null) _sidebarHeaderText.Foreground = Accents.TextSecondaryBrush;

        // Rebuild tree (icons / colors derive from accent).
        BuildTree();

        // Re-tint tabs.
        if (_activeTab != null) ActivateTab(_activeTab);
    }

    // =====================================================================
    // Visual builders
    // =====================================================================

    private Border BuildToolButton(string glyph, string tooltip)
    {
        var glyphText = new TextBlock
        {
            Text = glyph,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        var labelText = new TextBlock
        {
            Text = tooltip,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0)
        };
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { glyphText, labelText }
        };

        var b = new Border
        {
            Padding = new Thickness(10, 5),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
            Child = stack
        };

        ToolTip.SetTip(b, tooltip);

        b.PointerEntered += (_, _) =>
            b.Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
        b.PointerExited += (_, _) =>
            b.Background = Brushes.Transparent;

        _toolButtons.Add((b, glyphText, labelText));
        return b;
    }

    private static Control CreateAppIcon()
    {
        var a = AccentManager.Instance.AccentPrimary;
        var bg = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(a),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var label = new TextBlock
        {
            Text = "{ }",
            FontSize = 9,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(AccentManager.Instance.TextOnAccent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var grid = new Grid { Width = 16, Height = 16 };
        grid.Children.Add(bg);
        grid.Children.Add(label);
        return grid;
    }

    // =====================================================================
    // Find / Go-to-line command bar
    // =====================================================================

    private Border BuildCommandBar()
    {
        _commandLabel = new TextBlock
        {
            Text = "Find:",
            FontSize = 12,
            Foreground = new SolidColorBrush(AccentManager.Instance.TextOnAccent),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0)
        };

        _commandInput = new DOSITextBox
        {
            Width = 280,
            VerticalAlignment = VerticalAlignment.Center
        };
        _commandInput.KeyDown += OnCommandInputKeyDown;

        _commandHint = new TextBlock
        {
            Text = "",
            FontSize = 11,
            Opacity = 0.7,
            Foreground = new SolidColorBrush(AccentManager.Instance.TextOnAccent),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };

        var closeBtn = new TextBlock
        {
            Text = "\u2715",
            FontSize = 14,
            Foreground = new SolidColorBrush(AccentManager.Instance.TextOnAccent),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(8, 0, 8, 0),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
        };
        closeBtn.PointerReleased += (_, _) => HideCommandBar();

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _commandLabel, _commandInput, _commandHint }
        };

        var dock = new DockPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            LastChildFill = false
        };
        DockPanel.SetDock(closeBtn, Dock.Right);
        dock.Children.Add(closeBtn);
        dock.Children.Add(row);

        _commandBar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(220, 22, 28, 60)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            IsVisible = false,
            Child = dock
        };
        return _commandBar;
    }

    private void ShowFindBar()
    {
        if (_commandBar == null || _commandInput == null || _commandLabel == null || _commandHint == null) return;
        _commandMode = "find";
        _commandLabel.Text = "Find:";
        _commandHint.Text = "Enter \u2192 next match    Esc \u2192 close";

        // Pre-fill with the current selection if there is one.
        if (_activeTab?.Editor is { } ed && ed.HasSelection)
        {
            var sel = ed.GetSelectedText();
            if (!string.IsNullOrEmpty(sel) && !sel.Contains('\n')) _commandInput.Text = sel;
        }

        _commandBar.IsVisible = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _commandInput.Focus(),
            Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void ShowGotoBar()
    {
        if (_commandBar == null || _commandInput == null || _commandLabel == null || _commandHint == null) return;
        _commandMode = "goto";
        _commandLabel.Text = "Go to line:";
        _commandHint.Text = $"of {_activeTab?.Editor?.LineCount ?? 0}";
        _commandInput.Text = string.Empty;
        _commandBar.IsVisible = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _commandInput.Focus(),
            Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void HideCommandBar()
    {
        if (_commandBar == null) return;
        _commandBar.IsVisible = false;
        _commandMode = null;
        _activeTab?.Editor?.Focus();
    }

    private void OnCommandInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { HideCommandBar(); e.Handled = true; return; }
        if (e.Key != Key.Enter) return;
        e.Handled = true;

        var text = _commandInput?.Text ?? string.Empty;
        if (_commandMode == "find" && _activeTab?.Editor is { } ed && text.Length > 0)
        {
            // Start search just past the current selection so repeat-Enter
            // walks through subsequent matches.
            var (sl, sc, el, ec) = ed.HasSelection
                ? ed.GetNormalizedSelection()
                : (ed.CaretLine - 1, ed.CaretColumn - 1, ed.CaretLine - 1, ed.CaretColumn - 1);
            int startLine = el, startCol = ec;
            if (ed.FindNext(text, startLine, startCol, ignoreCase: true,
                            out var ml, out var mc, out var len))
            {
                ed.SetSelection(ml, mc, ml, mc + len);
                if (_commandHint != null) _commandHint.Text = "Enter \u2192 next match    Esc \u2192 close";
            }
            else
            {
                if (_commandHint != null) _commandHint.Text = "No match.";
            }
        }
        else if (_commandMode == "goto" && _activeTab?.Editor is { } ed2)
        {
            if (int.TryParse(text, out var lineNum))
            {
                ed2.GoToLine(lineNum);
                HideCommandBar();
            }
            else if (_commandHint != null) _commandHint.Text = "Enter a number.";
        }
    }

    // =====================================================================
    // Session persistence + reopen-closed-tab
    // =====================================================================

    private void SaveSession()
    {
        if (_sessionRestoring) return;
        if (_user == null) return;
        try
        {
            var state = new IdeSessionState
            {
                OpenPaths = _tabs
                    .Where(t => t.CodeBehindFor == null && t.Properties == null && !string.IsNullOrEmpty(t.Path))
                    .Select(t => t.Path)
                    .ToList(),
                ActivePath = _activeTab?.Path
            };
            File.WriteAllText(SessionFilePath,
                JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort */ }
    }

    private void LoadSession()
    {
        if (_user == null) return;
        if (!File.Exists(SessionFilePath)) return;

        IdeSessionState? state;
        try { state = JsonSerializer.Deserialize<IdeSessionState>(File.ReadAllText(SessionFilePath)); }
        catch { return; }
        if (state == null || state.OpenPaths.Count == 0) return;

        _sessionRestoring = true;
        try
        {
            foreach (var path in state.OpenPaths)
            {
                if (File.Exists(path)) OpenFile(path);
            }
            if (!string.IsNullOrEmpty(state.ActivePath))
            {
                var match = _tabs.FirstOrDefault(t =>
                    string.Equals(t.Path, state.ActivePath, StringComparison.OrdinalIgnoreCase));
                if (match != null) ActivateTab(match);
            }
        }
        finally { _sessionRestoring = false; }
    }

    private void ReopenLastClosedTab()
    {
        while (_recentlyClosed.Count > 0)
        {
            var path = _recentlyClosed.Pop();
            if (!File.Exists(path)) continue;
            // Skip if it's already open.
            if (_tabs.Any(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            OpenFile(path);
            return;
        }
    }

    // =====================================================================
    // Project Properties tab
    // =====================================================================

    private void OpenActiveProjectProperties()
    {
        if (_activeProject == null)
        {
            ShowOutput();
            AppendOutput("[Properties] No active project. Open one first.");
            return;
        }

        var manifestPath = _activeProject.ManifestPath;

        // If the Properties tab for this project is already open, just activate it.
        var existing = _tabs.FirstOrDefault(t =>
            t.Properties != null &&
            string.Equals(t.Path, manifestPath, StringComparison.OrdinalIgnoreCase));
        if (existing != null) { ActivateTab(existing); return; }

        var panel = new ProjectPropertiesPanel(_activeProject, savedProject =>
        {
            // Refresh the tree label / status bar in case the manifest changed
            // anything user-visible (description, version, author, ...).
            _statusProject.Text = "Project: " + savedProject.Name;
        });

        // Build a minimal tab border that matches the existing tab pattern.
        var label = new TextBlock
        {
            Text = "Properties \u2014 " + _activeProject.Name,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Accents.TextOnAccent)
        };
        var dirtyMark = new TextBlock
        {
            Text = "",
            FontSize = 14,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Accents.TextOnAccent)
        };
        var closeBtn = new TextBlock
        {
            Text = "\u2715",
            FontSize = 12,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Accents.TextOnAccent),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        var contentStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { label, dirtyMark, closeBtn }
        };
        var tabBorder = new Border
        {
            Padding = new Thickness(14, 0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = contentStack
        };

        var tab = new EditorTab
        {
            Path = manifestPath,
            TabBorder = tabBorder,
            TabLabel = label,
            DirtyMark = dirtyMark,
            Properties = panel
        };

        tabBorder.PointerPressed += (_, e) =>
        {
            if (e.Source == closeBtn) { CloseTab(tab); e.Handled = true; return; }
            ActivateTab(tab);
            e.Handled = true;
        };
        closeBtn.PointerPressed += (_, e) => { CloseTab(tab); e.Handled = true; };

        panel.Modified += (_, _) => UpdateDirtyState();
        panel.Saved += (_, _) => UpdateDirtyState();

        _tabs.Add(tab);
        _tabStrip.Children.Add(tabBorder);
        WireTabInteraction(tab);
        ActivateTab(tab);
    }

    // =====================================================================
    // Format document (Ctrl+K, Ctrl+D)
    // =====================================================================

    private void FormatActiveDocument()
    {
        var ed = _activeTab?.Editor;
        if (ed == null) return;
        if (!_activeTab!.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            var src = ed.Text;
            var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(src);
            var root = tree.GetRoot();
            using var workspace = new Microsoft.CodeAnalysis.AdhocWorkspace();
            var formatted = Microsoft.CodeAnalysis.Formatting.Formatter
                .Format(root, workspace).ToFullString();

            if (formatted != src)
            {
                var line = ed.CaretLine;
                var col = ed.CaretColumn;
                ed.Text = formatted;
                ed.GoToLine(line, col);
            }
        }
        catch (Exception ex)
        {
            ShowOutput();
            AppendOutput($"[Format] Failed: {ex.Message}");
        }
    }

    // =====================================================================
    // Build status spinner
    // =====================================================================

    private Border BuildSpinner()
    {
        _spinnerGlyph = new TextBlock
        {
            Text = "\u25D2",
            FontSize = 14,
            Foreground = new SolidColorBrush(AccentManager.Instance.TextOnAccent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _spinnerRotate = new RotateTransform(0);
        _spinnerGlyph.RenderTransform = _spinnerRotate;
        _spinnerGlyph.RenderTransformOrigin = RelativePoint.Center;

        _spinnerHost = new Border
        {
            Width = 18,
            Height = 18,
            Margin = new Thickness(8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
            Child = _spinnerGlyph
        };
        return _spinnerHost;
    }

    private DateTime _spinnerStartedUtc;

    private void StartBuildSpinner(string label)
    {
        if (_spinnerHost == null || _spinnerGlyph == null) return;
        _spinnerStartedUtc = DateTime.UtcNow;
        _spinnerGlyph.Text = "\u25D2";   // half-circle (rotates)
        _spinnerGlyph.Foreground = new SolidColorBrush(AccentManager.Instance.TextOnAccent);
        ToolTip.SetTip(_spinnerHost, label);
        _spinnerHost.IsVisible = true;
        _spinnerTimer?.Stop();
        _spinnerTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _spinnerTimer.Tick += (_, _) =>
        {
            if (_spinnerRotate != null) _spinnerRotate.Angle = (_spinnerRotate.Angle + 18) % 360;
        };
        _spinnerTimer.Start();
    }

    private void StopBuildSpinner(bool? success = null)
    {
        if (_spinnerHost == null || _spinnerGlyph == null) return;
        _spinnerTimer?.Stop();
        _spinnerTimer = null;
        if (_spinnerRotate != null) _spinnerRotate.Angle = 0;

        if (success.HasValue)
        {
            // Flash a success/failure glyph for ~1.5s before hiding.
            var elapsed = DateTime.UtcNow - _spinnerStartedUtc;
            _spinnerGlyph.Text = success.Value ? "\u2713" : "\u2717";
            _spinnerGlyph.Foreground = success.Value
                ? new SolidColorBrush(Color.FromRgb(120, 220, 140))
                : new SolidColorBrush(Color.FromRgb(240, 110, 110));
            ToolTip.SetTip(_spinnerHost,
                $"{(success.Value ? "Succeeded" : "Failed")} in {elapsed.TotalSeconds:0.0}s");

            var hideTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            hideTimer.Tick += (_, _) =>
            {
                hideTimer.Stop();
                if (_spinnerHost != null) _spinnerHost.IsVisible = false;
            };
            hideTimer.Start();
            return;
        }

        _spinnerHost.IsVisible = false;
    }

    // =====================================================================
    // Quick file switcher (Ctrl+,)
    // =====================================================================

    private void ShowQuickSwitcher()
    {
        if (_activeProject == null) return;
        if (Content is not Panel host) return;

        if (_switcherOverlay == null) BuildQuickSwitcherOverlay(host);
        if (_switcherOverlay == null || _switcherInput == null) return;

        // Refresh the file index every time so newly added files appear.
        _switcherFiles = EnumerateProjectFiles(_activeProject.FolderPath).ToList();
        _switcherInput.Text = string.Empty;
        RefreshQuickSwitcherResults();

        _switcherOverlay.IsVisible = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _switcherInput.Focus();
            // SelectAll isn't on DOSITextBox public API; clearing first then
            // refocusing is the simplest equivalent.
            _switcherInput.Text = string.Empty;
            RefreshQuickSwitcherResults();
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void HideQuickSwitcher()
    {
        if (_switcherOverlay == null) return;
        _switcherOverlay.IsVisible = false;
        _activeTab?.Editor?.Focus();
    }

    private void BuildQuickSwitcherOverlay(Panel host)
    {
        _switcherInput = new DOSITextBox
        {
            Width = 480,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _switcherInput.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == "Text") RefreshQuickSwitcherResults();
        };
        _switcherInput.KeyDown += OnQuickSwitcherKey;

        _switcherResults = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 8, 0, 0)
        };

        var footerHint = new TextBlock
        {
            Text = "\u2191 \u2193 navigate    Enter open    Esc close",
            FontSize = 10,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0)
        };

        var card = new Border
        {
            Width = 520,
            Background = new SolidColorBrush(Color.FromArgb(235, 22, 28, 60)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 80, 0, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Children = { _switcherInput, _switcherResults, footerHint }
            }
        };

        _switcherOverlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsVisible = false,
            Child = card
        };
        _switcherOverlay.PointerPressed += (_, e) =>
        {
            // Click outside the card dismisses.
            if (e.Source == _switcherOverlay) HideQuickSwitcher();
        };
        host.Children.Add(_switcherOverlay);
    }

    private static IEnumerable<string> EnumerateProjectFiles(string projectFolder)
    {
        if (!Directory.Exists(projectFolder)) return Array.Empty<string>();
        try
        {
            return Directory.EnumerateFiles(projectFolder, "*", SearchOption.AllDirectories)
                .Where(p =>
                {
                    var name = IOPath.GetFileName(p);
                    if (name.EndsWith(DOSIProjectManager.ManifestExtension, StringComparison.OrdinalIgnoreCase)) return false;
                    var rel = p.Substring(projectFolder.Length).Replace('\\', '/');
                    if (rel.Contains("/bin/", StringComparison.OrdinalIgnoreCase)) return false;
                    if (rel.Contains("/obj/", StringComparison.OrdinalIgnoreCase)) return false;
                    var ext = IOPath.GetExtension(name).ToLowerInvariant();
                    return ext is ".cs" or ".dosiform" or ".json" or ".txt" or ".md";
                })
                .OrderBy(IOPath.GetFileName, StringComparer.OrdinalIgnoreCase);
        }
        catch { return Array.Empty<string>(); }
    }

    private void RefreshQuickSwitcherResults()
    {
        if (_switcherResults == null || _switcherInput == null) return;
        _switcherResults.Children.Clear();
        _switcherVisible.Clear();

        var query = _switcherInput.Text ?? string.Empty;
        var matches = (string.IsNullOrEmpty(query)
            ? _switcherFiles.Take(20)
            : _switcherFiles
                .Where(p => FuzzyMatch(IOPath.GetFileName(p), query) || FuzzyMatch(p, query))
                .Take(20)).ToList();

        if (matches.Count == 0)
        {
            _switcherResults.Children.Add(new TextBlock
            {
                Text = "No matching files in this project.",
                FontSize = 12,
                Foreground = Accents.TextSecondaryBrush,
                Opacity = 0.7,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 12, 0, 6)
            });
            return;
        }

        int idx = 0;
        foreach (var path in matches)
        {
            var capturedIdx = idx;
            var capturedPath = path;
            var name = new TextBlock
            {
                Text = IOPath.GetFileName(path),
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = Accents.TextPrimaryBrush
            };
            var rel = _activeProject != null && path.StartsWith(_activeProject.FolderPath, StringComparison.OrdinalIgnoreCase)
                ? path.Substring(_activeProject.FolderPath.Length).TrimStart('\\', '/')
                : path;
            var sub = new TextBlock
            {
                Text = rel,
                FontSize = 10,
                Foreground = Accents.TextSecondaryBrush,
                Opacity = 0.7,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var row = new Border
            {
                Padding = new Thickness(8, 5),
                CornerRadius = new CornerRadius(4),
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new StackPanel { Orientation = Orientation.Vertical, Children = { name, sub } }
            };
            row.PointerEntered += (_, _) => { _switcherSelectedIndex = capturedIdx; HighlightQuickSwitcherSelection(); };
            row.PointerReleased += (_, _) => { OpenFile(capturedPath); HideQuickSwitcher(); };
            _switcherResults.Children.Add(row);
            _switcherVisible.Add((path, row));
            idx++;
        }
        _switcherSelectedIndex = 0;
        HighlightQuickSwitcherSelection();
    }

    private void HighlightQuickSwitcherSelection()
    {
        for (int i = 0; i < _switcherVisible.Count; i++)
        {
            _switcherVisible[i].Row.Background = i == _switcherSelectedIndex
                ? new SolidColorBrush(Color.FromArgb(60, AccentManager.Instance.AccentPrimary.R,
                    AccentManager.Instance.AccentPrimary.G, AccentManager.Instance.AccentPrimary.B))
                : Brushes.Transparent;
        }
    }

    private void OnQuickSwitcherKey(object? sender, KeyEventArgs e)
    {
        if (_switcherVisible.Count == 0) return;
        switch (e.Key)
        {
            case Key.Down:
                _switcherSelectedIndex = Math.Min(_switcherSelectedIndex + 1, _switcherVisible.Count - 1);
                HighlightQuickSwitcherSelection();
                e.Handled = true;
                return;
            case Key.Up:
                _switcherSelectedIndex = Math.Max(_switcherSelectedIndex - 1, 0);
                HighlightQuickSwitcherSelection();
                e.Handled = true;
                return;
            case Key.Enter:
                var path = _switcherVisible[_switcherSelectedIndex].Path;
                OpenFile(path);
                HideQuickSwitcher();
                e.Handled = true;
                return;
            case Key.Escape:
                HideQuickSwitcher();
                e.Handled = true;
                return;
        }
    }

    /// <summary>
    /// Order-preserving substring fuzzy match: every char in <paramref name="query"/>
    /// must appear in <paramref name="haystack"/> in order (case-insensitive).
    /// </summary>
    private static bool FuzzyMatch(string haystack, string query)
    {
        if (string.IsNullOrEmpty(query)) return true;
        int hi = 0, qi = 0;
        while (hi < haystack.Length && qi < query.Length)
        {
            if (char.ToLowerInvariant(haystack[hi]) == char.ToLowerInvariant(query[qi])) qi++;
            hi++;
        }
        return qi == query.Length;
    }

    // =====================================================================
    // Tab interaction (middle-click close + drag-reorder)
    // =====================================================================

    private void WireTabInteraction(EditorTab tab)
    {
        var border = tab.TabBorder;

        // Middle-click closes the tab.
        border.PointerPressed += (_, e) =>
        {
            var props = e.GetCurrentPoint(border).Properties;
            if (props.IsMiddleButtonPressed)
            {
                CloseTab(tab);
                e.Handled = true;
                return;
            }
            if (props.IsLeftButtonPressed)
            {
                _draggingTab = tab;
                _dragStartPoint = e.GetPosition(_tabStrip);
                _dragOriginalIndex = _tabs.IndexOf(tab);
                _dragActive = false;
            }
        };

        border.PointerMoved += (_, e) =>
        {
            if (_draggingTab != tab || !e.GetCurrentPoint(border).Properties.IsLeftButtonPressed) return;
            var pos = e.GetPosition(_tabStrip);
            if (!_dragActive)
            {
                if (Math.Abs(pos.X - _dragStartPoint.X) < 6) return;
                _dragActive = true;
                tab.TabBorder.Opacity = 0.55;     // ghost the dragging tab
            }
            ReorderDraggingTab(pos.X);
        };

        border.PointerReleased += (_, _) => EndTabDrag(commit: true);
    }

    private void EndTabDrag(bool commit)
    {
        if (_draggingTab != null)
        {
            _draggingTab.TabBorder.Opacity = 1d;
            if (!commit)
            {
                // Esc cancel: restore the tab to its original index.
                var current = _tabs.IndexOf(_draggingTab);
                if (current >= 0 && current != _dragOriginalIndex && _dragOriginalIndex >= 0 && _dragOriginalIndex <= _tabs.Count)
                {
                    var t = _draggingTab;
                    _tabs.RemoveAt(current);
                    _tabStrip.Children.RemoveAt(current);
                    var safeIdx = Math.Min(_dragOriginalIndex, _tabs.Count);
                    _tabs.Insert(safeIdx, t);
                    _tabStrip.Children.Insert(safeIdx, t.TabBorder);
                }
            }
        }
        ClearDragInsertionIndicator();
        _draggingTab = null;
        _dragActive = false;
    }

    private void ShowDragInsertionIndicator(int targetIdx)
    {
        if (_dragInsertionIndicator == null)
        {
            _dragInsertionIndicator = new Border
            {
                Width = 2,
                Background = new SolidColorBrush(Accents.AccentPrimary),
                IsHitTestVisible = false
            };
        }
        // Re-insert at the target index inside the tab strip.
        _tabStrip.Children.Remove(_dragInsertionIndicator);
        var safeIdx = Math.Clamp(targetIdx, 0, _tabStrip.Children.Count);
        _tabStrip.Children.Insert(safeIdx, _dragInsertionIndicator);
    }

    private void ClearDragInsertionIndicator()
    {
        if (_dragInsertionIndicator != null)
            _tabStrip.Children.Remove(_dragInsertionIndicator);
    }

    private void ReorderDraggingTab(double mouseX)
    {
        if (_draggingTab == null) return;
        var currentIdx = _tabs.IndexOf(_draggingTab);
        if (currentIdx < 0) return;

        // Find the tab the cursor is hovering over by walking the strip.
        double accum = 0;
        int targetIdx = _tabs.Count - 1;
        for (int i = 0; i < _tabs.Count; i++)
        {
            var w = _tabs[i].TabBorder.Bounds.Width;
            if (mouseX < accum + w / 2) { targetIdx = i; break; }
            accum += w;
        }

        if (targetIdx == currentIdx) return;

        // Move in both the model and the visual strip.
        var moving = _draggingTab;
        _tabs.RemoveAt(currentIdx);
        _tabStrip.Children.RemoveAt(currentIdx);
        _tabs.Insert(targetIdx, moving);
        _tabStrip.Children.Insert(targetIdx, moving.TabBorder);
        ShowDragInsertionIndicator(targetIdx + 1);
        SaveSession();
    }

    // =====================================================================
    // Recent projects
    // =====================================================================

    private List<RecentProjectEntry> LoadRecentProjects()
    {
        if (!File.Exists(RecentProjectsFilePath)) return new List<RecentProjectEntry>();
        try
        {
            var list = JsonSerializer.Deserialize<List<RecentProjectEntry>>(
                File.ReadAllText(RecentProjectsFilePath)) ?? new();
            return list
                .Where(e => Directory.Exists(e.Path))
                .OrderByDescending(e => e.LastOpenedUtc)
                .Take(MaxRecentProjects)
                .ToList();
        }
        catch { return new List<RecentProjectEntry>(); }
    }

    private void TouchRecentProject(DOSIProject project)
    {
        try
        {
            var list = LoadRecentProjects();
            list.RemoveAll(e => string.Equals(e.Path, project.FolderPath, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, new RecentProjectEntry
            {
                Path = project.FolderPath,
                Name = project.Name,
                LastOpenedUtc = DateTime.UtcNow
            });
            if (list.Count > MaxRecentProjects) list = list.Take(MaxRecentProjects).ToList();
            File.WriteAllText(RecentProjectsFilePath,
                JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort */ }
    }
}
