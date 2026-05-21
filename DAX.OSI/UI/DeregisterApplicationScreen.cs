using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DAX.OSI.DefaultApplications;
using DOSI.CORE.AccentManagement;
using DOSI.CORE.Apps;
using DOSI.CORE.ProjectSystem;
using DOSI.CORE.UIComponents;
using DOSI.CORE.UserManagement;
// Disambiguate System.IO.Path from Avalonia.Controls.Shapes.Path - both are
// reachable via the using directives above and we only ever want the IO one.
using Path = System.IO.Path;

namespace DAX.OSI.UI;

/// <summary>
/// "Application Manager" window for uninstalling user-registered DOSI apps
/// (those published through the IDE into <see cref="DOSIPublishedAppRegistry"/>).
/// Default system applications (Terminal, Browser, Files, Code) are NOT shown
/// here because they live in code, not in the registry, and therefore cannot
/// be removed.
///
/// Each row shows the app's icon, name, description, and published date plus a
/// destructive "Uninstall" action. A header toggle lets the user choose whether
/// to also delete the app's project folder from disk on uninstall.
/// </summary>
public class DeregisterApplicationScreen : DOSIWindow
{
    private static AccentManager Accents => AccentManager.Instance;

    private readonly DOSIUser? _user;
    private readonly StackPanel _appList;
    private readonly TextBlock _emptyState;
    private readonly TextBlock _headerSubtitle;
    private readonly Border _headerBorder;
    private readonly Border _toggleBox;
    private readonly TextBlock _toggleCheck;
    private bool _alsoDeleteFiles = true;

    public DeregisterApplicationScreen()
    {
        Title = "Application Manager";
        WindowWidth = 720;
        WindowHeight = 520;
        MinimumSize = new Size(520, 360);
        Icon = BuildAppIcon();

        _user = UserManager.CurrentUser;

        // ===== Header =====
        var headerTitle = new TextBlock
        {
            Text = "Installed Applications",
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextPrimaryBrush
        };

        _headerSubtitle = new TextBlock
        {
            Text = "Uninstall apps you no longer need. Default system applications cannot be removed.",
            FontSize = 12,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.85,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };

        // Toggle for "also delete project files from disk".
        _toggleCheck = new TextBlock
        {
            Text = "\u2713",
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Accents.TextOnAccent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _toggleBox = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(4),
            Background = Accents.AccentGradientBrush,
            BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
            Child = _toggleCheck
        };
        _toggleBox.PointerPressed += (_, _) => SetAlsoDeleteFiles(!_alsoDeleteFiles);

        var toggleLabel = new TextBlock
        {
            Text = "Also delete project files from disk",
            FontSize = 12,
            Foreground = Accents.TextPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        toggleLabel.PointerPressed += (_, _) => SetAlsoDeleteFiles(!_alsoDeleteFiles);

        var toggleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 14, 0, 0),
            Children = { _toggleBox, toggleLabel }
        };

        var headerStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(20, 18, 20, 14),
            Children = { headerTitle, _headerSubtitle, toggleRow, BuildOpenApplicationsFolderRow() }
        };

        var headerBorder = new Border
        {
            Background = Accents.WindowChromeBrush,
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = headerStack
        };
        _headerBorder = headerBorder;

        // ===== App list =====
        _appList = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 8,
            Margin = new Thickness(20, 16, 20, 20)
        };

        _emptyState = new TextBlock
        {
            Text = "No installed applications.",
            FontSize = 14,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.85,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 60, 0, 0),
            IsVisible = false
        };

        var listGrid = new Grid
        {
            Children = { _appList, _emptyState }
        };

        var scroller = new DOSIScrollViewer
        {
            Content = listGrid,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };

        // ===== Layout =====
        var rootGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*")
        };
        rootGrid.Children.Add(headerBorder); Grid.SetRow(headerBorder, 0);
        rootGrid.Children.Add(scroller); Grid.SetRow(scroller, 1);

        Content = rootGrid;

        SetAlsoDeleteFiles(true);

        AttachedToVisualTree += (_, _) =>
        {
            DOSIPublishedAppRegistry.AppsChanged += OnAppsChanged;
            // Per-user installed plug-ins also surface in this window so the
            // user can uninstall them without hand-editing their Applications
            // folder. Refresh the list on registry changes (uninstall, sign-in
            // re-scan, etc.).
            LoadedAppRegistry.AppsChanged += OnAppsChanged;
            Accents.AccentChanged += OnAccentChanged;
            RebuildList();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            DOSIPublishedAppRegistry.AppsChanged -= OnAppsChanged;
            LoadedAppRegistry.AppsChanged -= OnAppsChanged;
            Accents.AccentChanged -= OnAccentChanged;
        };
    }

    private void OnAppsChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(RebuildList);
    }

    private void OnAccentChanged(object? sender, EventArgs e)
    {
        // Re-theme every accent-driven surface so accent changes show through
        // immediately without requiring the user to reopen the window.
        _toggleBox.Background = _alsoDeleteFiles
            ? Accents.AccentGradientBrush
            : new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
        _toggleCheck.Foreground = new SolidColorBrush(Accents.TextOnAccent);
        _headerSubtitle.Foreground = Accents.TextSecondaryBrush;

        if (_headerBorder != null)
            _headerBorder.Background = Accents.WindowChromeBrush;

        // Title-bar icon uses the accent gradient - rebuild so the new accent
        // is reflected in the chrome immediately.
        Icon = BuildAppIcon();

        RebuildList();
    }

    private void SetAlsoDeleteFiles(bool value)
    {
        _alsoDeleteFiles = value;
        _toggleCheck.IsVisible = value;
        _toggleBox.Background = value
            ? Accents.AccentGradientBrush
            : new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
    }

    /// <summary>
    /// Builds the "Open Applications folder" affordance shown in the
    /// header. Clicking opens a <see cref="DOSIFileExplorer"/> already
    /// rooted at the signed-in user's <c>Applications/</c> folder so the
    /// user can sideload an application DLL (or its per-app folder)
    /// without having to navigate there manually. Hidden when there's no
    /// signed-in user (defensive - this window shouldn't be reachable
    /// without one anyway).
    /// </summary>
    private Control BuildOpenApplicationsFolderRow()
    {
        if (_user == null)
            return new StackPanel { IsVisible = false };

        var openButton = new DOSIButton
        {
            Text = "Open Applications folder",
            FontSize = 12,
            Height = 30,
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(12, 0)
        };
        openButton.Click += (_, _) =>
        {
            try
            {
                var path = AppLoader.GetApplicationsFolderPath(_user);
                Directory.CreateDirectory(path); // first-time-create safety net

                var explorer = new DOSIFileExplorer();
                explorer.RequestNavigate(path);
                DOSI.CORE.UIComponents.WindowManagement.WindowManager
                    .Instance?.OpenWindow(explorer);
            }
            catch
            {
                // If the explorer can't be opened (rare - usually the
                // window manager is gone) silently no-op rather than
                // crashing the Application Manager.
            }
        };

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { openButton }
        };
    }

    // =====================================================================
    // List rendering
    // =====================================================================

    private void RebuildList()
    {
        _appList.Children.Clear();

        // ----- Per-user installed plug-ins (DLLs in <UserHome>/Applications/) -----
        // Enumerate the actual files on disk rather than walking
        // LoadedAppRegistry, because (a) one DLL can publish multiple
        // IDOSIApp entries and the user thinks of "the plug-in" as the
        // file, and (b) a DLL that failed to load still needs to be
        // uninstallable through this screen.
        var pluginPaths = EnumerateInstalledPluginPaths();
        var publishedApps = DOSIPublishedAppRegistry.GetAll(_user);

        if (pluginPaths.Count == 0 && publishedApps.Count == 0)
        {
            _emptyState.IsVisible = true;
            return;
        }
        _emptyState.IsVisible = false;

        if (pluginPaths.Count > 0)
        {
            _appList.Children.Add(BuildSectionHeader("Installed plug-ins"));
            foreach (var p in pluginPaths)
                _appList.Children.Add(BuildPluginRow(p));
        }

        if (publishedApps.Count > 0)
        {
            if (pluginPaths.Count > 0)
                _appList.Children.Add(new Border { Height = 8 }); // visual spacer between sections
            _appList.Children.Add(BuildSectionHeader("Published apps"));
            foreach (var app in publishedApps)
                _appList.Children.Add(BuildAppRow(app));
        }
    }

    /// <summary>
    /// Returns absolute paths of every application DLL <see cref="AppLoader"/>
    /// would (or did) try to load for the signed-in user. Supports both the
    /// per-app folder layout and the legacy flat layout - we just pass
    /// through whatever AppLoader's own discovery returns so the two stay
    /// in lock-step.
    /// </summary>
    private List<string> EnumerateInstalledPluginPaths()
    {
        if (_user == null) return new List<string>();
        try
        {
            var dir = AppLoader.GetApplicationsFolderPath(_user);
            return AppLoader.EnumerateAppDlls(dir)
                .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            // Unreadable Applications folder shouldn't crash this screen;
            // surface an empty list and let the user retry.
            return new List<string>();
        }
    }

    private Control BuildSectionHeader(string text) => new TextBlock
    {
        Text = text,
        FontSize = 11,
        FontWeight = FontWeight.SemiBold,
        Foreground = Accents.TextSecondaryBrush,
        Opacity = 0.85,
        Margin = new Thickness(2, 4, 0, 4),
        LetterSpacing = 1.2
    };

    private Control BuildAppRow(DOSIPublishedApp app)
    {
        // Glyph (matches the Applications menu's published-app glyph for consistency).
        var glyph = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(8),
            Background = Accents.AccentGradientBrush,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
            Child = new TextBlock
            {
                Text = "\u2756",
                FontSize = 18,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Accents.TextOnAccent),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var name = new TextBlock
        {
            Text = app.Name,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextPrimaryBrush
        };

        var description = new TextBlock
        {
            Text = app.Description ?? "Published DOSI app",
            FontSize = 12,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.85,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 1, 0, 0)
        };

        var published = new TextBlock
        {
            Text = $"Installed {app.PublishedUtc.ToLocalTime():MMM d, yyyy h:mm tt}",
            FontSize = 11,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.65,
            Margin = new Thickness(0, 4, 0, 0)
        };

        var textStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { name, description, published }
        };

        var uninstallBtn = new DOSIButton
        {
            Text = "Uninstall",
            FontSize = 12,
            Width = 110,
            Height = 32,
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(60, 18, 18)),
            BackgroundHover = new SolidColorBrush(Color.FromRgb(90, 24, 24)),
            BackgroundPressed = new SolidColorBrush(Color.FromRgb(45, 14, 14)),
            Foreground = new SolidColorBrush(Color.FromRgb(255, 170, 170)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(180, 240, 90, 90)),
            BorderThickness = 1
        };
        uninstallBtn.Click += async (_, _) => await ConfirmAndUninstallAsync(app);

        var rowGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            VerticalAlignment = VerticalAlignment.Center
        };
        rowGrid.Children.Add(glyph); Grid.SetColumn(glyph, 0);
        rowGrid.Children.Add(textStack); Grid.SetColumn(textStack, 1);
        rowGrid.Children.Add(uninstallBtn); Grid.SetColumn(uninstallBtn, 2);

        return new Border
        {
            Padding = new Thickness(14, 12),
            CornerRadius = new CornerRadius(10),
            Background = Accents.ControlBackgroundBrush,
            BorderBrush = new SolidColorBrush(
                Color.FromArgb(110,
                    Accents.AccentPrimary.R,
                    Accents.AccentPrimary.G,
                    Accents.AccentPrimary.B)),
            BorderThickness = new Thickness(1),
            Child = rowGrid
        };
    }

    /// <summary>
    /// Renders a single installed plug-in DLL as an uninstallable row. The
    /// display name is the file's basename (matches what the user dropped
    /// into the folder); the description is whatever any registered
    /// IDOSIApp from this DLL reports, falling back to "Installed plug-in"
    /// for DLLs that failed to load and therefore have no registry entry.
    /// </summary>
    private Control BuildPluginRow(string dllPath)
    {
        var fileName = Path.GetFileName(dllPath);
        var displayName = Path.GetFileNameWithoutExtension(dllPath);

        // Best-effort lookup: any IDOSIApp registered out of this DLL gives
        // us a friendlier name + description. We can't always know which
        // app came from which DLL (the registry stores instances, not
        // their origin path), so we just show the first registry entry's
        // metadata if any plug-in is loaded; otherwise fall back to the
        // file name. This mirrors how Windows Settings shows installed
        // programs - file-system truth first, decorated where possible.
        var loaded = LoadedAppRegistry.All.FirstOrDefault();
        string description;
        if (string.Equals(fileName, "DAX.OSI.IDE.Plugin.dll", StringComparison.OrdinalIgnoreCase))
            description = "DAX.OSI IDE (default)";
        else if (loaded != null && loaded.Title.Equals(displayName, StringComparison.OrdinalIgnoreCase))
            description = loaded.Description;
        else
            description = "Installed plug-in";

        var glyph = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromRgb(20, 20, 26)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
            Child = new TextBlock
            {
                Text = "{ }",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Accents.AccentPrimary),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var name = new TextBlock
        {
            Text = displayName,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextPrimaryBrush
        };

        var descText = new TextBlock
        {
            Text = description,
            FontSize = 12,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.85,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 1, 0, 0)
        };

        var pathText = new TextBlock
        {
            Text = fileName,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.65,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 4, 0, 0)
        };

        var textStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { name, descText, pathText }
        };

        var uninstallBtn = new DOSIButton
        {
            Text = "Uninstall",
            FontSize = 12,
            Width = 110,
            Height = 32,
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(60, 18, 18)),
            BackgroundHover = new SolidColorBrush(Color.FromRgb(90, 24, 24)),
            BackgroundPressed = new SolidColorBrush(Color.FromRgb(45, 14, 14)),
            Foreground = new SolidColorBrush(Color.FromRgb(255, 170, 170)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(180, 240, 90, 90)),
            BorderThickness = 1
        };
        uninstallBtn.Click += async (_, _) => await ConfirmAndUninstallPluginAsync(dllPath, displayName);

        var rowGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            VerticalAlignment = VerticalAlignment.Center
        };
        rowGrid.Children.Add(glyph); Grid.SetColumn(glyph, 0);
        rowGrid.Children.Add(textStack); Grid.SetColumn(textStack, 1);
        rowGrid.Children.Add(uninstallBtn); Grid.SetColumn(uninstallBtn, 2);

        return new Border
        {
            Padding = new Thickness(14, 12),
            CornerRadius = new CornerRadius(10),
            Background = Accents.ControlBackgroundBrush,
            BorderBrush = new SolidColorBrush(
                Color.FromArgb(110,
                    Accents.AccentPrimary.R,
                    Accents.AccentPrimary.G,
                    Accents.AccentPrimary.B)),
            BorderThickness = new Thickness(1),
            Child = rowGrid
        };
    }

    /// <summary>
    /// Confirms and removes an installed application. For per-app-folder
    /// installs (<c>Applications/&lt;AppName&gt;/&lt;AppName&gt;.dll</c>)
    /// the WHOLE folder is deleted so private dependencies and stamp
    /// files don't get left behind. For legacy flat installs
    /// (<c>Applications/&lt;AppName&gt;.dll</c>) only the DLL and matching
    /// .pdb are removed.
    /// <para>
    /// Because <see cref="AppLoader"/> loads each DLL through a
    /// <c>MemoryStream</c> rather than a memory-mapped file path, the
    /// file is NOT locked at runtime and the delete succeeds immediately.
    /// We also drop the app from <see cref="LoadedAppRegistry"/> so the
    /// apps menu updates the moment the dialog closes - no sign-out
    /// required.
    /// </para>
    /// </summary>
    private async Task ConfirmAndUninstallPluginAsync(string dllPath, string displayName)
    {
        var container = FindDialogContainer();
        if (container == null) return;

        var result = await DOSIDialog.YesNo(container, "Uninstall application",
            $"Uninstall '{displayName}'?\n\nThe application will be removed from your " +
            "Applications folder and disappear from the menu immediately.");
        if (result != DialogResult.Yes) return;

        try
        {
            // Decide whether this install lives in its own per-app folder
            // (preferred layout) or sits flat at the Applications root
            // (legacy / hand-installed). If the parent folder is named
            // exactly after the DLL's base name, treat it as a per-app
            // folder and remove the whole thing.
            var dllDir = Path.GetDirectoryName(dllPath);
            var dllBase = Path.GetFileNameWithoutExtension(dllPath);
            var appsRoot = _user != null ? AppLoader.GetApplicationsFolderPath(_user) : null;

            var isPerAppFolder =
                dllDir != null && appsRoot != null &&
                !string.Equals(Path.GetFullPath(dllDir).TrimEnd(Path.DirectorySeparatorChar),
                               Path.GetFullPath(appsRoot).TrimEnd(Path.DirectorySeparatorChar),
                               StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFileName(dllDir), dllBase, StringComparison.OrdinalIgnoreCase);

            if (isPerAppFolder && Directory.Exists(dllDir))
            {
                Directory.Delete(dllDir!, recursive: true);
            }
            else
            {
                if (File.Exists(dllPath))
                    File.Delete(dllPath);

                var pdb = Path.ChangeExtension(dllPath, ".pdb");
                if (File.Exists(pdb))
                    File.Delete(pdb);
            }
        }
        catch (Exception ex)
        {
            await DOSIDialog.Alert(container, "Uninstall failed",
                $"Could not delete '{displayName}':\n\n{ex.Message}");
            return;
        }

        // Drop the app from the live registry so the apps menu loses its
        // tile right away. The underlying assembly stays loaded for the
        // session (non-collectible context), but with no IDOSIApp in the
        // registry the host has no way to launch it again.
        LoadedAppRegistry.UnregisterByDllPath(dllPath);

        RebuildList();

        await DOSIDialog.Alert(container, "Uninstalled",
            $"'{displayName}' was removed.");
    }

    // =====================================================================
    // Uninstall flow
    // =====================================================================

    private async Task ConfirmAndUninstallAsync(DOSIPublishedApp app)
    {
        var container = FindDialogContainer();
        if (container == null) return;

        var message = _alsoDeleteFiles
            ? $"Uninstall '{app.Name}' and permanently delete its project files from disk?\n\nThis cannot be undone."
            : $"Uninstall '{app.Name}'?\n\nThe app will be removed from the Applications menu. Its project files on disk will be left intact.";

        var result = await DOSIDialog.YesNo(container, "Uninstall application", message);
        if (result != DialogResult.Yes) return;

        // IMPORTANT: unregister BEFORE deleting the folder.
        // DOSIPublishedAppRegistry.Unpublish internally calls GetAll, which
        // filters out entries whose ProjectFolderPath no longer exists on disk.
        // If we delete the folder first, GetAll hides the entry, RemoveAll
        // matches nothing, and Unpublish returns false ("not found") even
        // though the entry is still sitting in the registry file.
        var folderToDelete = (_alsoDeleteFiles
                              && !string.IsNullOrWhiteSpace(app.ProjectFolderPath)
                              && Directory.Exists(app.ProjectFolderPath))
            ? app.ProjectFolderPath
            : null;

        if (!DOSIPublishedAppRegistry.Unpublish(app.Name, _user))
        {
            await DOSIDialog.Alert(container, "Uninstall failed",
                $"Could not remove '{app.Name}' from the application registry.");
            return;
        }

        var deletedFiles = false;
        if (folderToDelete != null)
        {
            try
            {
                Directory.Delete(folderToDelete, recursive: true);
                deletedFiles = true;
            }
            catch (Exception ex)
            {
                await DOSIDialog.Alert(container, "Files not deleted",
                    $"'{app.Name}' was unregistered, but its project folder could not " +
                    $"be deleted:\n\n{ex.Message}");
                return;
            }
        }

        var summary = deletedFiles
            ? $"'{app.Name}' was uninstalled and its files were deleted."
            : $"'{app.Name}' was uninstalled.";
        await DOSIDialog.Alert(container, "Uninstalled", summary);
    }

    /// <summary>
    /// Walks up the visual tree to find the topmost <see cref="Panel"/> we can
    /// host a <see cref="DOSIDialog"/> in. Falls back to the window itself.
    /// </summary>
    private Panel? FindDialogContainer()
    {
        Visual? current = this;
        Panel? topMostPanel = null;
        while (current != null)
        {
            if (current is Panel p) topMostPanel = p;
            current = current.GetVisualParent();
        }
        return topMostPanel;
    }

    // =====================================================================
    // Icon
    // =====================================================================

    private static Control BuildAppIcon() => new Border
    {
        Width = 16,
        Height = 16,
        CornerRadius = new CornerRadius(4),
        Background = Accents.AccentGradientBrush,
        BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
        BorderThickness = new Thickness(1),
        Child = new TextBlock
        {
            Text = "\u2715",
            FontSize = 9,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Accents.TextOnAccent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };
}

