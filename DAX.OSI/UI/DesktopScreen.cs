using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DAX.OSI.DefaultApplications;
using DOSI.CORE;
using DOSI.CORE.AccentManagement;
using DOSI.CORE.ProjectSystem;
using DOSI.CORE.UIComponents;
using DOSI.CORE.UIComponents.WindowManagement;
using DOSI.CORE.UserManagement;
using DOSI.CORE.WallpaperManagement;

namespace DAX.OSI.UI;

/// <summary>
/// The desktop screen shown after a successful sign-in. Hosts open windows,
/// shows a transient welcome notification, and renders a thin top taskbar
/// with an Applications launcher and the signed-in user / live clock.
/// </summary>
public class DesktopScreen : DOSIScreen
{
    public override string ScreenId => "desktop";
    public override string ScreenName => "Desktop";

    private static AccentManager Accents => AccentManager.Instance;

    private const double TaskbarHeight = 28;
    // ----- Layout -----
    private readonly Grid _layoutRoot;
    private readonly Grid _ambientLayer;
    private readonly Border _taskbar;
    // Drives the taskbar slide-in / slide-out animations. The Border's Y
    // translation moves between -TaskbarHeight (off-screen above) and 0
    // (docked at the top of the desktop).
    private readonly TranslateTransform _taskbarSlide = null!;
    private readonly Border _appsButton;
    private readonly Border _appsButtonAccent;
    private readonly TextBlock _appsButtonLabel;
    private readonly TextBlock _taskbarUser;
    private readonly Border _userAvatarChip;
    private readonly TextBlock _userAvatarInitial;
    private readonly TextBlock _clockText;
    private readonly TextBlock _dateText;
    private readonly TextBlock _versionText;

    // ----- Apps menu -----
    private readonly Border _appsMenuBackdrop;
    private readonly Border _appsMenu;
    private readonly TranslateTransform _appsMenuTranslate;
    private DispatcherTimer? _appsMenuAnimTimer;
    private bool _appsMenuOpen;
    private StackPanel? _appsMenuItems;

    // ----- State -----
    private readonly DispatcherTimer _clockTimer;

    /// <summary>
    /// Tracks whether the desktop is currently displaying the soft (blurred)
    /// or sharp variant of the wallpaper. Mirrors the per-user
    /// <see cref="UserManager.WallpaperBlurPreferenceKey"/> preference and
    /// is consulted by <see cref="ResolveWallpaperBitmap"/> so the base
    /// <c>DOSIScreen</c> cross-fade machinery picks up the right variant.
    /// Defaults to <c>true</c> so the desktop's first frame matches every
    /// other DOSI screen.
    /// </summary>
    private bool _wallpaperBlurEnabled = true;

    public DesktopScreen()
    {
        // ===== Top taskbar =====
        // Modern 2x2 rounded-tile launcher glyph. Classic "apps" iconography
        // (Windows Start / macOS Launchpad), painted in the accent gradient
        // so it tracks theme changes. Each tile is parented to a UniformGrid
        // host so OnAccentAccentChanged can re-tint all four in one pass.
        var appsTileGrid = new UniformGrid
        {
            Rows = 2,
            Columns = 2,
            Width = 14,
            Height = 14
        };
        for (int i = 0; i < 4; i++)
        {
            appsTileGrid.Children.Add(new Border
            {
                Width = 5,
                Height = 5,
                CornerRadius = new CornerRadius(1.5),
                Background = Accents.AccentGradientBrush,
                Margin = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        _appsButtonAccent = new Border
        {
            Width = 14,
            Height = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Child = appsTileGrid
        };

        _appsButtonLabel = new TextBlock
        {
            Text = "Applications",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center
        };

        _appsButton = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { _appsButtonAccent, _appsButtonLabel }
            }
        };
        _appsButton.PointerEntered += (_, _) =>
        {
            if (!_appsMenuOpen)
                _appsButton.Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
        };
        _appsButton.PointerExited += (_, _) =>
        {
            if (!_appsMenuOpen)
                _appsButton.Background = Brushes.Transparent;
        };
        _appsButton.PointerPressed += OnAppsButtonPressed;

        // Compact clock + date are no longer in the taskbar - they live as an
        // ambient overlay in the bottom-left of the desktop (built below).
        _clockText = new TextBlock
        {
            FontSize = 42,
            FontWeight = FontWeight.Light,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };

        _dateText = new TextBlock
        {
            FontSize = 14,
            Foreground = Brushes.White,
            Opacity = 0.85,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };

        var clockStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(36, 0, 0, 28),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Spacing = 2,
            Children = { _clockText, _dateText }
        };

        // User chip (mini avatar + display name).
        _userAvatarInitial = new TextBlock
        {
            Text = "?",
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Accents.TextOnAccent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var avatarRing = new Ellipse
        {
            Width = 18,
            Height = 18,
            Fill = Accents.AccentGradientBrush
        };

        _userAvatarChip = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Grid
            {
                Width = 18,
                Height = 18,
                Children = { avatarRing, _userAvatarInitial }
            }
        };

        _taskbarUser = new TextBlock
        {
            Text = string.Empty,
            FontSize = 12,
            Foreground = Accents.TextPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center
        };

        var userStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 6,
            Children = { _userAvatarChip, _taskbarUser }
        };

        var rightStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 14, 0),
            Spacing = 18,
            Children = { userStack }
        };

        var taskbarGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto")
        };
        taskbarGrid.Children.Add(_appsButton);
        Grid.SetColumn(_appsButton, 0);
        taskbarGrid.Children.Add(new Border { Background = Brushes.Transparent });
        Grid.SetColumn(taskbarGrid.Children[1], 1);
        taskbarGrid.Children.Add(rightStack);
        Grid.SetColumn(rightStack, 2);

        _taskbar = new Border
        {
            Height = TaskbarHeight,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Background = BuildTaskbarBackground(),
            BorderBrush = BuildTaskbarBorderBrush(),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = taskbarGrid,
            // Start the bar parked above the screen so the slide-in animation
            // (kicked off after AttachedToVisualTree) has somewhere to slide
            // FROM. AnimateTaskbarInAsync drives Y back to 0.
            RenderTransform = _taskbarSlide = new TranslateTransform(0, -TaskbarHeight)
        };

        // ===== Apps menu (popup) =====
        _appsMenuBackdrop = new Border
        {
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsVisible = false
        };
        _appsMenuBackdrop.PointerPressed += (_, _) => CloseAppsMenu();

        _appsMenu = BuildAppsMenu();

        // ===== Bottom-right version label =====
        _versionText = new TextBlock
        {
            Text = "DAX.OSI  v1.0",
            FontSize = 11,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 24, 16)
        };

        _appsMenuTranslate = new TranslateTransform(0, -8);
        _appsMenu.RenderTransform = _appsMenuTranslate;
        _appsMenu.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);

        _layoutRoot = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            // Order matters: backdrop and menu sit above the taskbar so they
            // catch clicks anywhere on the screen. Only elements that MUST
            // render above DOSIWindows live here - the layout root is later
            // re-parented into MainWindow.PopupHost (above the windows canvas).
            Children = { _taskbar, _appsMenuBackdrop, _appsMenu }
        };

        // Ambient decorative layer (clock, date, version) lives in the desktop
        // canvas BELOW windows. With translucent windows enabled, drawing these
        // elements above windows would make them bleed through every window.
        // Keeping them on the desktop layer lets translucent windows obscure
        // them naturally while the wallpaper still shows through window bodies.
        _ambientLayer = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false,
            Children = { clockStack, _versionText }
        };

        Desktop.Children.Add(_ambientLayer);
        Desktop.Children.Add(_layoutRoot);
        Desktop.LayoutUpdated += (_, _) =>
        {
            _layoutRoot.Width = Desktop.Bounds.Width;
            _layoutRoot.Height = Desktop.Bounds.Height;
            _ambientLayer.Width = Desktop.Bounds.Width;
            _ambientLayer.Height = Desktop.Bounds.Height;
        };

        UpdateClock();
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();

        AttachedToVisualTree += (_, _) =>
        {
            Accents.AccentChanged += OnAccentAccentChanged;
            DOSIPublishedAppRegistry.AppsChanged += OnPublishedAppsChanged;
            DefaultApplications.DOSISettingsScreen.WallpaperBlurChanged += OnWallpaperBlurChanged;
            DOSIWindow.AnyWindowFullScreenChanged += OnAnyWindowFullScreenChanged;
            // Sync up immediately in case a window is already fullscreen when
            // the desktop attaches (e.g. fast user switch mid-video).
            ApplyFullScreenChromeVisibility(!DOSIWindow.IsAnyWindowFullScreen);
            _clockTimer.Start();

            // Reserve the taskbar's vertical space in the global window
            // manager so no DOSIWindow can be cascaded, dragged, or maximized
            // up under the taskbar.
            if (WindowManager.Instance is { } wm)
                wm.TopWorkAreaInset = TaskbarHeight;

            // Move the desktop chrome (taskbar / version / apps menu) into the
            // application-wide popup overlay so it always renders above any
            // persistent DOSIWindow living in MainWindow's window overlay.
            if (DAX.OSI.MainWindow.PopupHost is Canvas popup)
            {
                if (_layoutRoot.Parent is Panel oldParent)
                    oldParent.Children.Remove(_layoutRoot);
                popup.Children.Add(_layoutRoot);

                void SyncSize(object? _, EventArgs __)
                {
                    _layoutRoot.Width = popup.Bounds.Width;
                    _layoutRoot.Height = popup.Bounds.Height;
                }
                popup.LayoutUpdated += SyncSize;
                SyncSize(null, EventArgs.Empty);
            }

            // Slide the taskbar in from above on first attach. Posted at
            // Loaded priority so it runs AFTER the parent crossfade has
            // started and the popup overlay has measured itself - this way
            // the user sees a clean drop-down even if the desktop is being
            // crossfaded in from the login screen at the same time.
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => _ = AnimateTaskbarInAsync(),
                Avalonia.Threading.DispatcherPriority.Loaded);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            Accents.AccentChanged -= OnAccentAccentChanged;
            DOSIPublishedAppRegistry.AppsChanged -= OnPublishedAppsChanged;
            DefaultApplications.DOSISettingsScreen.WallpaperBlurChanged -= OnWallpaperBlurChanged;
            DOSIWindow.AnyWindowFullScreenChanged -= OnAnyWindowFullScreenChanged;
            _clockTimer.Stop();
            _appsMenuAnimTimer?.Stop();
            _appsMenuAnimTimer = null;
            _taskbarAnimTimer?.Stop();
            _taskbarAnimTimer = null;

            // Defensive: if we're being torn down while a window is
            // immersive-fullscreen, restore chrome visibility so the next
            // screen we navigate to (sign-out, shutdown) doesn't inherit
            // a hidden taskbar.
            ApplyFullScreenChromeVisibility(true);

            // Release the reserved top inset so screens without a taskbar
            // (login, signout, shutdown) get the full canvas back.
            if (WindowManager.Instance is { } wm)
                wm.TopWorkAreaInset = 0;

            // Pull desktop chrome back out of the popup overlay so it doesn't
            // leak when this DesktopScreen instance is removed.
            if (DAX.OSI.MainWindow.PopupHost is Panel popup &&
                _layoutRoot.Parent == popup)
            {
                popup.Children.Remove(_layoutRoot);
            }
        };
    }

    public override void OnNavigatedTo()
    {
        base.OnNavigatedTo();

        UpdateUserChip(UserManager.CurrentUser);

        // Apply the user's saved DOSIWindow opacity preference so newly-opened
        // (and currently-open) windows use the configured translucency.
        DOSIWindow.WindowOpacity = UserManager.CurrentUser != null
            ? UserManager.GetUserWindowOpacity(UserManager.CurrentUser)
            : UserManager.DefaultWindowOpacity;

        // Apply the user's saved wallpaper-blur preference. We only update
        // the field here - the actual cross-fade between the soft and
        // sharp variants is kicked off by DOSIScreen.OnTransitionComplete
        // AFTER the screen-level cross-fade settles and our backdrop is
        // visible. Doing the swap here would play the wallpaper animation
        // INVISIBLY behind the hidden backdrop, making the change look
        // like an instant snap when the desktop appears. Login,
        // sign-out, shutdown, and the setup wizard intentionally ignore
        // this preference and always render the soft variant.
        _wallpaperBlurEnabled = UserManager.CurrentUser == null ||
                                UserManager.GetUserWallpaperBlur(UserManager.CurrentUser);

        NotifyScreenReady();

        // Show welcome toast once the desktop is up.
        Dispatcher.UIThread.Post(() =>
        {
            var name = UserManager.CurrentUser?.DisplayName
                       ?? UserManager.CurrentUser?.Username
                       ?? "User";
            DOSIPopNotification.Show($"Welcome, {name}");
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Returns the wallpaper bitmap variant the desktop should currently
    /// show. Honors the per-user wallpaper-blur preference so toggling it
    /// in Settings cross-fades between the soft and sharp variants of the
    /// same underlying wallpaper. Falls back to the soft variant whenever
    /// the preference is enabled (or no preference is known).
    /// </summary>
    protected override Avalonia.Media.Imaging.Bitmap? ResolveWallpaperBitmap()
    {
        return WallpaperManager.Instance.GetCurrentBitmap(blurred: _wallpaperBlurEnabled);
    }

    /// <summary>
    /// Live-applies the wallpaper-blur preference when the user toggles it
    /// in Settings. Persists the change to the active user (already done
    /// by the Settings handler) and animates a cross-fade between the
    /// blurred and sharp variants of the same wallpaper.
    /// </summary>
    private void OnWallpaperBlurChanged(object? sender, bool enabled)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_wallpaperBlurEnabled == enabled) return;
            _wallpaperBlurEnabled = enabled;
            RefreshWallpaper();
        });
    }

    // =====================================================================
    // Apps menu
    // =====================================================================

    private Border BuildAppsMenu()
    {
        _appsMenuItems = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 2
        };

        RebuildAppsMenuItems();

        return new Border
        {
            Width = 280,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8, TaskbarHeight + 4, 0, 0),
            Padding = new Thickness(8),
            Background = BuildMenuBackground(),
            BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0,
                OffsetY = 14,
                Blur = 36,
                Spread = 0,
                Color = Color.FromArgb(150, 0, 0, 0)
            }),
            Opacity = 0,
            IsVisible = false,
            Child = _appsMenuItems
        };
    }

    /// <summary>
    /// (Re)builds the contents of the Applications menu. Called once at startup,
    /// every time the menu opens, and whenever the published-app registry changes.
    /// </summary>
    private void RebuildAppsMenuItems()
    {
        if (_appsMenuItems == null) return;
        var items = _appsMenuItems;
        items.Children.Clear();

        items.Children.Add(BuildAppsMenuHeader("Applications"));
        items.Children.Add(BuildAppsMenuItem("Terminal", "Command-line shell", BuildTerminalGlyph(),
            () => LaunchApplication(new DOSITerminal())));
        items.Children.Add(BuildAppsMenuItem("Browser", "Browse the web", BuildBrowserGlyph(),
            () => LaunchApplication(new DOSIWebBrowser())));
        items.Children.Add(BuildAppsMenuItem("Files", "Browse your files", BuildFilesGlyph(),
            () => LaunchApplication(new DOSIFileExplorer())));
        items.Children.Add(BuildAppsMenuItem("Image Viewer", "View pictures and screenshots", BuildImageViewerGlyph(),
            () => LaunchApplication(new DOSIImageViewer())));
        items.Children.Add(BuildAppsMenuItem("Code", "Edit and build code", BuildCodeGlyph(),
            () => LaunchApplication(new DOSIIDE())));

        // Published user apps (live-recompiled on every launch).
        var published = DOSIPublishedAppRegistry.GetAll(UserManager.CurrentUser);
        if (published.Count > 0)
        {
            items.Children.Add(BuildMenuDivider());
            items.Children.Add(BuildAppsMenuHeader("Published"));
            foreach (var app in published)
            {
                var captured = app;
                items.Children.Add(BuildAppsMenuItem(
                    captured.Name,
                    captured.Description ?? "Published DOSI app",
                    BuildPublishedAppGlyph(),
                    () =>
                    {
                        CloseAppsMenu();
                        DOSIPublishedAppLauncher.Launch(captured);
                    }));
            }
        }

        items.Children.Add(BuildMenuDivider());
        items.Children.Add(BuildAppsMenuHeader("System"));
        items.Children.Add(BuildAppsMenuItem("Settings", "Personalize and configure DOSI", BuildSettingsGlyph(),
            () => LaunchApplication(new DOSISettingsScreen())));
        items.Children.Add(BuildAppsMenuItem("Application Manager", "Uninstall installed apps", BuildAppManagerGlyph(),
            () => LaunchApplication(new DeregisterApplicationScreen())));
        items.Children.Add(BuildAppsMenuItem("Sign out", "Switch user / lock session", BuildSignOutGlyph(),
            () => SystemSignOut.Begin()));
        items.Children.Add(BuildAppsMenuItem("Shutdown", "Power off DAX.OSI", BuildShutdownGlyph(),
            () => SystemShutdown.Begin(0)));
    }

    private static Border BuildMenuDivider() => new()
    {
        Height = 1,
        Margin = new Thickness(8, 6, 8, 6),
        Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255))
    };

    private void OnPublishedAppsChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(RebuildAppsMenuItems);
    }

    /// <summary>
    /// Hides desktop chrome (taskbar + apps menu host + ambient clock /
    /// version label) while any DOSIWindow is in immersive fullscreen, and
    /// restores it on exit. Without this the taskbar - which lives in
    /// MainWindow.PopupHost above the windows canvas - would cover the
    /// top of a fullscreened YouTube video.
    /// </summary>
    private void OnAnyWindowFullScreenChanged(object? sender, bool fullscreen)
    {
        Dispatcher.UIThread.Post(() => ApplyFullScreenChromeVisibility(!fullscreen));
    }

    private void ApplyFullScreenChromeVisibility(bool chromeVisible)
    {
        // Close the apps menu if it's open - leaving it dangling on top of
        // a fullscreened video would defeat the purpose.
        if (!chromeVisible && _appsMenuOpen)
            CloseAppsMenu();

        _layoutRoot.IsVisible = chromeVisible;
        _ambientLayer.IsVisible = chromeVisible;
    }

    private static Control BuildPublishedAppGlyph() => new Border
    {
        Width = 26,
        Height = 26,
        CornerRadius = new CornerRadius(6),
        Background = new SolidColorBrush(Accents.AccentPrimary),
        Margin = new Thickness(0, 0, 12, 0),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        Child = new TextBlock
        {
            Text = "\u2756",
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Accents.TextOnAccent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    private static Control BuildAppsMenuHeader(string text) => new TextBlock
    {
        Text = text,
        FontSize = 10,
        FontWeight = FontWeight.SemiBold,
        Foreground = Accents.TextSecondaryBrush,
        Opacity = 0.75,
        Margin = new Thickness(10, 6, 10, 8),
        TextAlignment = TextAlignment.Left
    };

    private Control BuildAppsMenuItem(string title, string subtitle, Control glyph, Action onSelected)
    {
        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center
        };

        var subtitleText = new TextBlock
        {
            Text = subtitle,
            FontSize = 11,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.85,
            VerticalAlignment = VerticalAlignment.Center
        };

        var textStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 1,
            Children = { titleText, subtitleText }
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            VerticalAlignment = VerticalAlignment.Center
        };
        grid.Children.Add(glyph);
        Grid.SetColumn(glyph, 0);
        grid.Children.Add(textStack);
        Grid.SetColumn(textStack, 1);

        var row = new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = grid
        };

        row.PointerEntered += (_, _) =>
            row.Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
        row.PointerExited += (_, _) =>
            row.Background = Brushes.Transparent;
        row.PointerPressed += (_, e) =>
        {
            e.Handled = true; // don't bubble to backdrop
            CloseAppsMenu();
            // Defer launch so the close animation can begin first.
            Dispatcher.UIThread.Post(onSelected, DispatcherPriority.Background);
        };

        return row;
    }

    private static Control BuildTerminalGlyph() => new Border
    {
        Width = 26,
        Height = 26,
        CornerRadius = new CornerRadius(6),
        Background = new SolidColorBrush(Color.FromRgb(20, 20, 26)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
        BorderThickness = new Thickness(1),
        Margin = new Thickness(0, 0, 12, 0),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        Child = new TextBlock
        {
            Text = ">_",
            FontFamily = new FontFamily("Consolas"),
            FontWeight = FontWeight.Bold,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 210)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    private static Control BuildBrowserGlyph() => new Border
    {
        Width = 26,
        Height = 26,
        CornerRadius = new CornerRadius(13),
        Background = Accents.AccentGradientBrush,
        Margin = new Thickness(0, 0, 12, 0),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        Child = new TextBlock
        {
            Text = "\u25CC", // dotted circle - simple "globe" stand-in
            FontSize = 16,
            Foreground = new SolidColorBrush(Accents.TextOnAccent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    private static Control BuildFilesGlyph()
    {
        var tab = new Border
        {
            Width = 14,
            Height = 4,
            CornerRadius = new CornerRadius(1, 1, 0, 0),
            Background = Accents.AccentGradientBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(2, 4, 0, 0)
        };

        var body = new Border
        {
            Width = 22,
            Height = 16,
            CornerRadius = new CornerRadius(2, 4, 4, 4),
            Background = Accents.AccentGradientBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 2)
        };

        var grid = new Grid
        {
            Width = 26,
            Height = 26,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        grid.Children.Add(tab);
        grid.Children.Add(body);
        return grid;
    }

    private static Control BuildImageViewerGlyph()
    {
        // Mountain + sun pictogram - matches the icon used in the viewer's
        // title bar so the menu entry is visually self-identifying.
        var border = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromRgb(40, 80, 140)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            ClipToBounds = true
        };

        var canvas = new Canvas { Width = 26, Height = 26 };
        // Sun
        canvas.Children.Add(new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = new SolidColorBrush(Color.FromRgb(255, 220, 110)),
            [Canvas.LeftProperty] = 16d,
            [Canvas.TopProperty] = 4d
        });
        // Mountain silhouette
        canvas.Children.Add(new Avalonia.Controls.Shapes.Polygon
        {
            Points = new Avalonia.Collections.AvaloniaList<Point>
            {
                new(0, 22), new(9, 11), new(15, 17), new(22, 8), new(26, 11), new(26, 26), new(0, 26)
            },
            Fill = new SolidColorBrush(Color.FromRgb(220, 230, 240))
        });
        border.Child = canvas;
        return border;
    }

    private static Control BuildCodeGlyph() => new Border
    {
        Width = 26,
        Height = 26,
        CornerRadius = new CornerRadius(6),
        Background = new SolidColorBrush(Color.FromRgb(20, 20, 26)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
        BorderThickness = new Thickness(1),
        Margin = new Thickness(0, 0, 12, 0),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        Child = new TextBlock
        {
            Text = "{ }",
            FontFamily = new FontFamily("Consolas"),
            FontWeight = FontWeight.Bold,
            FontSize = 11,
            Foreground = new SolidColorBrush(Accents.AccentPrimary),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    private static Control BuildShutdownGlyph() => new Border
    {
        Width = 26,
        Height = 26,
        CornerRadius = new CornerRadius(13),
        Background = new SolidColorBrush(Color.FromRgb(60, 18, 18)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(180, 240, 90, 90)),
        BorderThickness = new Thickness(1),
        Margin = new Thickness(0, 0, 12, 0),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        Child = new TextBlock
        {
            Text = "\u23FB", // power symbol
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(255, 150, 150)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    private static Control BuildSignOutGlyph() => new Border
    {
        Width = 26,
        Height = 26,
        CornerRadius = new CornerRadius(13),
        Background = Accents.AccentGradientBrush,
        BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
        BorderThickness = new Thickness(1),
        Margin = new Thickness(0, 0, 12, 0),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        Child = new TextBlock
        {
            Text = "\u21AA", // rightwards arrow with hook - "leave"
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Accents.TextOnAccent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    private static Control BuildSettingsGlyph() => new Border
    {
        Width = 26,
        Height = 26,
        CornerRadius = new CornerRadius(6),
        Background = Accents.AccentGradientBrush,
        Margin = new Thickness(0, 0, 12, 0),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        Child = new TextBlock
        {
            Text = "\u2699", // gear
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Accents.TextOnAccent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    private static Control BuildAppManagerGlyph() => new Border
    {
        Width = 26,
        Height = 26,
        CornerRadius = new CornerRadius(6),
        Background = new SolidColorBrush(Color.FromRgb(60, 18, 18)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(180, 240, 90, 90)),
        BorderThickness = new Thickness(1),
        Margin = new Thickness(0, 0, 12, 0),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        Child = new TextBlock
        {
            Text = "\u2715", // multiplication X - "remove"
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(255, 170, 170)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    private void OnAppsButtonPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        if (_appsMenuOpen)
            CloseAppsMenu();
        else
            OpenAppsMenu();
    }

    private void OpenAppsMenu()
    {
        if (_appsMenuOpen) return;
        _appsMenuOpen = true;

        // Refresh in case the user just published / unpublished an app while
        // this DesktopScreen was alive.
        RebuildAppsMenuItems();

        _appsButton.Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
        _appsMenuBackdrop.IsVisible = true;
        _appsMenu.IsVisible = true;

        // Hide every native WebView surface so the menu isn't occluded by
        // the OS-level browser composition (airspace problem). Restored in
        // CloseAppsMenu below.
        DAX.OSI.Controls.WebViewWrapper.SetAllPaused(true);

        AnimateAppsMenu(opening: true);
    }

    private void CloseAppsMenu()
    {
        if (!_appsMenuOpen) return;
        _appsMenuOpen = false;
        _appsButton.Background = Brushes.Transparent;
        DAX.OSI.Controls.WebViewWrapper.SetAllPaused(false);
        AnimateAppsMenu(opening: false);
    }

    private void AnimateAppsMenu(bool opening)
    {
        const double duration = 180;
        const double startOffset = -8;

        var startOpacity = _appsMenu.Opacity;
        var targetOpacity = opening ? 1.0 : 0.0;
        var startY = _appsMenuTranslate.Y;
        var targetY = opening ? 0.0 : startOffset;

        var startTime = DateTime.UtcNow;

        _appsMenuAnimTimer?.Stop();
        _appsMenuAnimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _appsMenuAnimTimer.Tick += (_, _) =>
        {
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            var t = Math.Clamp(elapsed / duration, 0d, 1d);
            var eased = opening
                ? 1 - Math.Pow(1 - t, 3)   // ease-out for opening
                : t * t;                   // ease-in for closing

            _appsMenu.Opacity = startOpacity + (targetOpacity - startOpacity) * eased;
            _appsMenuTranslate.Y = startY + (targetY - startY) * eased;

            if (t >= 1d)
            {
                _appsMenuAnimTimer?.Stop();
                _appsMenuAnimTimer = null;

                if (!opening)
                {
                    _appsMenu.IsVisible = false;
                    _appsMenuBackdrop.IsVisible = false;
                }
            }
        };
        _appsMenuAnimTimer.Start();
    }

    // =====================================================================
    // Taskbar slide animation
    // =====================================================================

    private DispatcherTimer? _taskbarAnimTimer;

    /// <summary>
    /// Drops the taskbar in from above. Awaitable so callers can sequence
    /// other startup work after the chrome is in place. Safe to call when
    /// the bar is already on-screen (no-op then).
    /// </summary>
    public Task AnimateTaskbarInAsync(int durationMs = 380)
        => AnimateTaskbarAsync(targetY: 0, durationMs, easeOut: true);

    /// <summary>
    /// Slides the taskbar back up off-screen. Used by the sign-out and
    /// shutdown flows so the chrome retracts cleanly instead of just fading
    /// with everything else. Returns when the slide is complete.
    /// </summary>
    public Task AnimateTaskbarOutAsync(int durationMs = 280)
        => AnimateTaskbarAsync(targetY: -TaskbarHeight, durationMs, easeOut: false);

    private Task AnimateTaskbarAsync(double targetY, int durationMs, bool easeOut)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var startY = _taskbarSlide.Y;
        if (Math.Abs(startY - targetY) < 0.5)
        {
            _taskbarSlide.Y = targetY;
            tcs.TrySetResult(true);
            return tcs.Task;
        }

        _taskbarAnimTimer?.Stop();
        var startTime = DateTime.UtcNow;
        _taskbarAnimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _taskbarAnimTimer.Tick += (_, _) =>
        {
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            var t = Math.Clamp(elapsed / durationMs, 0d, 1d);
            var eased = easeOut
                ? 1 - Math.Pow(1 - t, 3)   // cubic ease-out for entrance
                : t * t * t;                // cubic ease-in for exit

            _taskbarSlide.Y = startY + (targetY - startY) * eased;

            if (t >= 1d)
            {
                _taskbarAnimTimer?.Stop();
                _taskbarAnimTimer = null;
                _taskbarSlide.Y = targetY;
                tcs.TrySetResult(true);
            }
        };
        _taskbarAnimTimer.Start();
        return tcs.Task;
    }

    private static void LaunchApplication(DOSIWindow window)
    {
        var manager = WindowManager.Instance;
        if (manager == null) return;

        manager.OpenWindow(window);
        window.BringToFront();
    }

    // =====================================================================
    // User chip / welcome / chrome
    // =====================================================================

    private void UpdateUserChip(DOSIUser? user)
    {
        var name = user?.DisplayName ?? user?.Username ?? string.Empty;
        _taskbarUser.Text = name;
        _userAvatarInitial.Text = string.IsNullOrWhiteSpace(name)
            ? "?"
            : char.ToUpperInvariant(name.Trim()[0]).ToString();
        _userAvatarChip.IsVisible = !string.IsNullOrWhiteSpace(name);
    }

    private void OnAccentAccentChanged(object? sender, EventArgs e)
    {
        _taskbar.Background = BuildTaskbarBackground();
        _taskbar.BorderBrush = BuildTaskbarBorderBrush();
        if (_appsButtonAccent.Child is UniformGrid appsTiles)
        {
            foreach (var tile in appsTiles.Children)
            {
                if (tile is Border b)
                    b.Background = Accents.AccentGradientBrush;
            }
        }
        _appsButtonLabel.Foreground = Accents.TextPrimaryBrush;
        _taskbarUser.Foreground = Accents.TextPrimaryBrush;
        _userAvatarInitial.Foreground = new SolidColorBrush(Accents.TextOnAccent);

        // _clockText / _dateText are intentionally pinned to white - they
        // never re-tint with the accent (matches LoginScreen's behavior).
        _versionText.Foreground = Accents.TextSecondaryBrush;

        _appsMenu.Background = BuildMenuBackground();
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        _clockText.Text = now.ToString("h:mm tt");
        _dateText.Text = now.ToString("dddd, MMMM d");
    }

    private static IBrush BuildTaskbarBackground()
    {
        // Light accent needs a light surface so the (dark) TextPrimaryBrush
        // labels in the taskbar stay readable. All other accents keep the
        // original deep navy so the chrome reads as "system bar".
        if (Accents.CurrentAccent == DOSIAccent.Light)
        {
            return new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(235, 248, 249, 252), 0),
                    new GradientStop(Color.FromArgb(225, 232, 236, 244), 1)
                }
            };
        }

        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(220, 22, 24, 30), 0),
                new GradientStop(Color.FromArgb(210, 14, 16, 22), 1)
            }
        };
    }

    /// <summary>
    /// Builds an accent-tinted gradient for the bottom border of the taskbar
    /// so the user's chosen accent is visible at all times.
    /// </summary>
    private static IBrush BuildTaskbarBorderBrush()
    {
        var a = Accents.AccentPrimary;
        var b = Accents.AccentSecondary;
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(180, a.R, a.G, a.B), 0),
                new GradientStop(Color.FromArgb(200, b.R, b.G, b.B), 1)
            }
        };
    }

    private static IBrush BuildMenuBackground()
    {
        // Match the taskbar: light surface under the Light accent so menu
        // item text (TextPrimaryBrush, dark in Light mode) stays readable.
        if (Accents.CurrentAccent == DOSIAccent.Light)
        {
            return new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(245, 250, 251, 254), 0),
                    new GradientStop(Color.FromArgb(238, 232, 236, 244), 1)
                }
            };
        }

        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(235, 28, 30, 38), 0),
                new GradientStop(Color.FromArgb(225, 16, 18, 26), 1)
            }
        };
    }
}
