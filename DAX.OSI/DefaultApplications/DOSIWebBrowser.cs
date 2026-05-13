using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DAX.OSI.Controls;
using DOSI.CORE;
using DOSI.CORE.AccentManagement;
using DOSI.CORE.UIComponents;
using DOSI.CORE.UIComponents.WindowManagement;
using System.IO;
using System.Net.Http;

namespace DAX.OSI.DefaultApplications;

/// <summary>
/// A modern web browser application for the DOSI operating system.
/// Features tabbed browsing, navigation controls, an address bar, and a
/// real native web renderer (WebView2 on Windows, WKWebView on macOS,
/// WebKitGTK on Linux) wrapped behind <see cref="WebViewWrapper"/>.
/// </summary>
public class DOSIWebBrowser : DOSIWindow
{
    private readonly DOSITextBox _addressBar;
    private Avalonia.Controls.Shapes.Path? _addressBarSiteIcon;
    private readonly Border _backButton;
    private readonly Border _forwardButton;
    private readonly Border _refreshButton;
    private readonly Border _homeButton;
    private readonly Border _contentArea;
    /// <summary>
    /// Hosts every tab's <see cref="BrowserTab.PageContent"/> simultaneously.
    /// Tab switching toggles each child's <see cref="Control.IsVisible"/>
    /// instead of reparenting, which keeps each tab's native WebView handle
    /// (WebView2 HWND / WKWebView / WebKitGTK widget) attached to the visual
    /// tree. Without this, switching tabs detaches the WebView, the renderer
    /// drops its native handle, and re-activating forces a full page reload
    /// (visible as YouTube starting over when the user flips back to a tab
    /// they were already watching).
    /// </summary>
    private readonly Grid _tabContentHost;
    private readonly Border _toolbarBorder;
    private readonly Border _statusBar;
    private readonly TextBlock _statusText;
    private Border? _loadProgress;
    private Avalonia.Threading.DispatcherTimer? _loadProgressTimer;
    private double _loadProgressPhase;
    private readonly List<string> _history = [];
    private int _historyIndex = -1;
    private string _currentUrl = "dosi://home";
    private WebViewWrapper? _webView;
    private bool _isExternalPage;
    private bool _isFullScreen;

    // ----- Tabbed browsing -----
    // Each BrowserTab snapshots the per-tab navigation state (url + history +
    // webview + rendered content). The instance fields above always mirror
    // _activeTab so every existing NavigateTo / GoBack / RenderPage path keeps
    // working unchanged - tabs simply swap state in/out around them.
    private sealed class BrowserTab
    {
        public string CurrentUrl = "dosi://home";
        public string TitleText = "New Tab";
        public bool IsExternalPage;
        public WebViewWrapper? WebView;
        public Control? PageContent;
        public readonly List<string> History = new();
        public int HistoryIndex = -1;
        public Border? Header;
        public TextBlock? HeaderText;
        public Image? HeaderIcon;
        public Border? HeaderCloseButton;
        public Border? HeaderActiveIndicator;
        public string? FaviconForUrl;
    }

    private readonly List<BrowserTab> _tabs = new();
    private BrowserTab? _activeTab;
    private readonly StackPanel _tabStrip;
    private readonly Border _tabStripContainer;
    private bool _isSwitchingTabs;

    // Single shared HttpClient for favicon fetches. Static + reused so we
    // don't socket-leak one client per tab and so the connection pool can be
    // reused across navigations.
    private static readonly HttpClient _faviconHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private static AccentManager Accents => AccentManager.Instance;

    public DOSIWebBrowser(string? initialUrl = null)
    {
        Title = "DOSI Browser";
        WindowWidth = 900;
        WindowHeight = 600;
        MinimumSize = new Size(500, 350);
        Icon = CreateBrowserIcon();

        // Handle keyboard shortcuts for fullscreen (F11) and exit (Escape)
        KeyDown += OnBrowserKeyDown;

        // Create navigation buttons (using PointerReleased for click behavior).
        // Every glyph is a Path so the entire toolbar (back / forward / refresh
        // / home / go) renders with the exact same stroke weight, footprint,
        // and drop shadow regardless of platform font fallback. Text-based
        // glyphs used to drift in size between fonts which made the toolbar
        // look like a mismatched icon set.
        _backButton = CreateNavButton(BuildArrowGlyph(pointsRight: false), "Back");
        _backButton.PointerReleased += (s, e) => { if (_backButton.Tag is true) GoBack(); };

        _forwardButton = CreateNavButton(BuildArrowGlyph(pointsRight: true), "Forward");
        _forwardButton.PointerReleased += (s, e) => { if (_forwardButton.Tag is true) GoForward(); };

        _refreshButton = CreateNavButton(BuildRefreshGlyph(), "Refresh");
        _refreshButton.PointerReleased += (s, e) => { if (_refreshButton.Tag is true) Refresh(); };

        _homeButton = CreateNavButton(BuildHomeGlyph(), "Home");
        _homeButton.PointerReleased += (s, e) =>
        {
            if (_homeButton.Tag is true)
                NavigateTo(BrowserPreferences.Current.HomeUrl);
        };

        // Navigation button panel
        var navButtonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };
        navButtonPanel.Children.Add(_backButton);
        navButtonPanel.Children.Add(_forwardButton);
        navButtonPanel.Children.Add(_refreshButton);
        navButtonPanel.Children.Add(_homeButton);

        // Address bar - using custom DOSITextBox with rounded pill-shaped ends.
        // Left padding is bumped to make room for the site-state icon (lock /
        // globe / sparkle) overlaid on the inside of the pill below.
        _addressBar = new DOSITextBox
        {
            PlaceholderText = "Enter URL or search...",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            UseRoundedEnds = true,  // Pill-shaped rounded corners
            Padding = new Thickness(36, 6, 14, 6),
            Height = 32
        };
        _addressBar.KeyDown += OnAddressBarKeyDown;
        _addressBar.GotFocus += (s, e) => _addressBar.SelectAll();
        // Keep the leading site-state icon in lockstep with whatever the
        // address bar shows (covers NavigateTo, GoBack/Forward, in-page
        // navigations from the WebView, etc. - any code that touches Text).
        _addressBar.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
                ApplyAddressBarSiteIcon((e.NewValue as string) ?? string.Empty);
        };

        // Leading site-state icon overlaid inside the pill, kept visually
        // associated with the address text. Path-based so it scales crisply
        // and follows the accent. RefreshAddressBarSiteIcon() swaps the
        // geometry whenever the URL changes (lock for HTTPS, globe for HTTP,
        // sparkle for dosi:// internal pages).
        _addressBarSiteIcon = new Avalonia.Controls.Shapes.Path
        {
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            Fill = Accents.TextSecondaryBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        var siteIconHost = new Border
        {
            Width = 22,
            Height = 22,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            Child = _addressBarSiteIcon,
            IsHitTestVisible = false
        };
        ApplyAddressBarSiteIcon("dosi://home");

        // Go button - shares the exact Path geometry used by Forward so the
        // entire toolbar reads as one consistent arrow family. Pixel-identical
        // to _forwardButton on every platform.
        var goButton = CreateNavButton(BuildArrowGlyph(pointsRight: true), "Go");
        goButton.PointerReleased += (s, e) => NavigateTo(_addressBar.Text ?? "");

        // Toolbar layout
        var toolbar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(8),
            Height = 38
        };

        toolbar.Children.Add(navButtonPanel);
        Grid.SetColumn(navButtonPanel, 0);

        var addressContainer = new Border { Margin = new Thickness(8, 0) };
        var addressOverlay = new Grid();
        addressOverlay.Children.Add(_addressBar);
        addressOverlay.Children.Add(siteIconHost);
        addressContainer.Child = addressOverlay;
        toolbar.Children.Add(addressContainer);
        Grid.SetColumn(addressContainer, 1);

        toolbar.Children.Add(goButton);
        Grid.SetColumn(goButton, 2);

        // Content area (where web pages are displayed)
        _contentArea = new Border
        {
            Background = Accents.WindowContentBrush,
            CornerRadius = new CornerRadius(0, 0, 6, 6),
            ClipToBounds = true
        };
        _tabContentHost = new Grid();
        _contentArea.Child = _tabContentHost;

        // Status bar
        _statusText = new TextBlock
        {
            Text = "Ready",
            FontSize = 12,
            Foreground = Accents.TextSecondaryBrush,
            Margin = new Thickness(12, 6),
            VerticalAlignment = VerticalAlignment.Center
        };

        // Thin accent-coloured strip drawn along the TOP edge of the status
        // bar that pulses while a page is loading. Indeterminate (the wrapped
        // WebView doesn't surface a load percentage) but the gentle
        // breathing motion gives users continuous "yes, work is happening"
        // feedback during slow navigations. Hidden by default.
        _loadProgress = new Border
        {
            Height = 2,
            Background = Accents.AccentPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsVisible = false,
            Opacity = 0
        };

        var statusContent = new Grid();
        statusContent.Children.Add(_statusText);
        statusContent.Children.Add(_loadProgress);

        _statusBar = new Border
        {
            Background = Accents.ControlBackgroundBrush,
            BorderBrush = new SolidColorBrush(Accents.ControlBorder),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Height = 28,
            Child = statusContent
        };

        // Main layout
        var mainGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto")
        };

        // ---------- Tab strip (above the toolbar) ----------
        _tabStrip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center
        };

        var newTabButton = BuildNewTabButton();

        var tabRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 4),
            Children = { _tabStrip, newTabButton }
        };

        var tabScroller = new DOSIScrollViewer
        {
            Content = tabRow,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            ShowScrollButtons = false
        };

        _tabStripContainer = new Border
        {
            Background = Accents.WindowChromeBrush,
            BorderBrush = new SolidColorBrush(Accents.ControlBorder),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Height = 36,
            Child = tabScroller
        };

        mainGrid.Children.Add(_tabStripContainer);
        Grid.SetRow(_tabStripContainer, 0);

        _toolbarBorder = new Border
        {
            Background = Accents.ControlBackgroundBrush,
            BorderBrush = new SolidColorBrush(Accents.ControlBorder),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = toolbar,
            // Soft shadow underneath separates chrome from page content. Kept
            // very subtle (low opacity, small blur) so it reads as a depth
            // cue rather than a heavy drop shadow.
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0,
                OffsetY = 2,
                Blur = 6,
                Spread = 0,
                Color = Color.FromArgb(40, 0, 0, 0)
            })
        };

        mainGrid.Children.Add(_toolbarBorder);
        Grid.SetRow(_toolbarBorder, 1);

        mainGrid.Children.Add(_contentArea);
        Grid.SetRow(_contentArea, 2);

        mainGrid.Children.Add(_statusBar);
        Grid.SetRow(_statusBar, 3);

        Content = mainGrid;

        // Subscribe to accent changes
        AttachedToVisualTree += (s, e) => Accents.AccentChanged += OnAccentChanged;
        DetachedFromVisualTree += (s, e) => Accents.AccentChanged -= OnAccentChanged;

        // Handle focus changes to show/hide WebView (fixes airspace z-order issue)
        FocusChanged += OnWindowFocusChanged;

        // Re-evaluate WebView visibility whenever any window opens, closes, or
        // moves on the desktop. With multiple browsers tiled side-by-side we
        // need to know if another window is actually overlapping us before
        // hiding the native surface - otherwise the unfocused tile would
        // pointlessly show the "Paused" card even though it's fully visible.
        AttachedToVisualTree += OnAttachedForOcclusionTracking;
        DetachedFromVisualTree += OnDetachedForOcclusionTracking;

        // Hide the native WebView while the user drags this window. The
        // WebView2 HWND lives on the OS compositor, not Avalonia's, so it
        // visibly trails the Avalonia chrome by a frame or two during a drag
        // - looks like the page is detached from the window. Hiding it for
        // the duration of the drag (and snapping it back on release) keeps
        // the window feeling solid.
        DragStateChanged += OnWindowDragStateChanged;

        // Dispose the WebView the instant shutdown / sign-out is initiated
        // - BEFORE the overlay screens animate in. Native WebView2 HWNDs
        // ignore Avalonia's z-order so they'd otherwise stay painted on top
        // of the shutdown / sign-out screen until WindowManager.CloseAllWindows
        // runs at the very end of the sequence.
        SystemShutdown.ShutdownStarting += OnSystemTeardownStarting;
        SystemSignOut.SignOutStarting += OnSystemTeardownStarting;

        // Handle window closing to dispose WebView properly
        Closing += OnWindowClosing;

        // React to live preference changes (e.g. user picked a new zoom in
        // the settings page): push the new zoom into every tab's WebView so
        // the change takes effect without needing a manual reload.
        BrowserPreferences.Changed += OnBrowserPrefsChanged;

        // Open the initial tab. This routes through the tab system so the
        // first tab gets a real header in the strip instead of being a
        // headless "hidden" navigation.
        OpenNewTab(initialUrl ?? "dosi://home", activate: true);
    }

    private void OnWindowClosing(object? sender, DOSI.CORE.UIComponents.WindowManagement.DOSIWindowClosingEventArgs e)
    {
        // Unhook the global teardown listeners so a closed-but-not-shutdown
        // browser doesn't keep its handler alive in the static event lists.
        SystemShutdown.ShutdownStarting -= OnSystemTeardownStarting;
        SystemSignOut.SignOutStarting -= OnSystemTeardownStarting;
        BrowserPreferences.Changed -= OnBrowserPrefsChanged;

        // Dispose every tab's WebView (not just the active one) so background
        // tabs don't leak their native HWND past the window's lifetime.
        DisposeAllTabWebViews();
        _webView = null;

        // Tear down the load-progress pulse timer so its dispatcher reference
        // doesn't keep the closed window's UI thread root alive.
        EndLoadProgress();
    }

    /// <summary>
    /// Pushes preference changes into every live tab's WebView (currently
    /// just zoom - everything else is consulted on demand). Called on the
    /// UI thread; the static <see cref="BrowserPreferences.Changed"/> event
    /// fires from <see cref="DOSITextBox"/> / button handlers which already
    /// run there.
    /// </summary>
    private void OnBrowserPrefsChanged(object? sender, EventArgs e)
    {
        var zoom = BrowserPreferences.Current.ZoomPercent;
        foreach (var tab in _tabs)
        {
            if (tab.WebView != null) tab.WebView.ZoomPercent = zoom;
        }
        // Re-render any open internal page so the settings card reflects the
        // freshly-loaded prefs (e.g. after a user-switch reload).
        if (!_isExternalPage && _currentUrl.StartsWith("dosi://", StringComparison.OrdinalIgnoreCase))
        {
            RenderPage(_currentUrl);
        }
    }

    /// <summary>
    /// Tears down the native WebView immediately when the OS begins to shut
    /// down or sign out, so its HWND stops painting before the full-screen
    /// overlay covers the desktop. Without this the WebView visibly survives
    /// on top of the shutdown / sign-out screen until window cleanup runs at
    /// the end of the sequence.
    /// </summary>
    private void OnSystemTeardownStarting()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try
            {
                DisposeAllTabWebViews();
                _webView = null;
            }
            catch { }
        });
    }

    /// <summary>
    /// Handles window focus changes to show/hide the WebView.
    /// This is necessary because native WebView controls have "airspace" issues
    /// and render above all Avalonia content regardless of z-order. We only
    /// hide when this window is actually occluded by another window above it -
    /// tiled / side-by-side browsers stay fully visible at the same time.
    /// </summary>
    private void OnWindowFocusChanged(object? sender, DOSI.CORE.UIComponents.WindowManagement.DOSIWindowFocusEventArgs e)
    {
        ReevaluateWebViewVisibility();
    }

    private DOSI.CORE.UIComponents.WindowManagement.WindowManager? _trackedManager;
    private Canvas? _trackedParent;

    private void OnAttachedForOcclusionTracking(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        // Manager-level events: any window opens / closes / changes z-order.
        _trackedManager = DOSI.CORE.UIComponents.WindowManagement.WindowManager.Instance;
        if (_trackedManager != null)
        {
            _trackedManager.WindowsChanged += OnWindowsChangedForOcclusion;
            _trackedManager.WindowFocusChanged += OnAnyFocusChangedForOcclusion;
        }
        // Canvas LayoutUpdated catches sibling moves / resizes (drag, snap,
        // resize-grip) that don't fire any WindowManager event.
        _trackedParent = Parent as Canvas;
        if (_trackedParent != null)
            _trackedParent.LayoutUpdated += OnDesktopLayoutUpdated;
        ReevaluateWebViewVisibility();
    }

    private void OnDetachedForOcclusionTracking(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (_trackedManager != null)
        {
            _trackedManager.WindowsChanged -= OnWindowsChangedForOcclusion;
            _trackedManager.WindowFocusChanged -= OnAnyFocusChangedForOcclusion;
            _trackedManager = null;
        }
        if (_trackedParent != null)
        {
            _trackedParent.LayoutUpdated -= OnDesktopLayoutUpdated;
            _trackedParent = null;
        }
    }

    private void OnWindowsChangedForOcclusion(object? sender, DOSI.CORE.UIComponents.WindowManagement.DOSIWindowEventArgs e)
        => ReevaluateWebViewVisibility();

    private void OnAnyFocusChangedForOcclusion(object? sender, DOSI.CORE.UIComponents.WindowManagement.DOSIWindowFocusEventArgs e)
        => ReevaluateWebViewVisibility();

    /// <summary>Cached last visibility so we only call SetVisible when the
    /// effective answer actually changes - LayoutUpdated fires every frame
    /// during a drag and we don't want to flap the placeholder card on / off.</summary>
    private bool? _lastOcclusionVisible;

    private void OnDesktopLayoutUpdated(object? sender, EventArgs e)
        => ReevaluateWebViewVisibility();

    /// <summary>
    /// Decide whether the native WebView surface should be visible right now.
    /// Rule: keep the surface visible unless another window with a higher
    /// z-order overlaps this window's rectangle. That way the focused window
    /// always shows, and unfocused windows only hide when something is
    /// actually painted over them - which is exactly when the airspace
    /// problem would otherwise bite.
    /// </summary>
    private void ReevaluateWebViewVisibility()
    {
        if (_webView == null || !_isExternalPage) return;
        if (WebViewWrapper.IsGloballyPaused) return;          // global pause owns the surface
        if (IsBeingDragged) return;                           // drag handler owns the surface

        var manager = _trackedManager ??
                      DOSI.CORE.UIComponents.WindowManagement.WindowManager.Instance;
        bool covered = false;
        if (manager != null)
        {
            var windows = manager.Windows;
            int myIdx = -1;
            for (int i = 0; i < windows.Count; i++)
                if (ReferenceEquals(windows[i], this)) { myIdx = i; break; }
            if (myIdx >= 0)
            {
                var myRect = new Avalonia.Rect(WindowX, WindowY, WindowWidth, WindowHeight);
                if (myRect.Width > 0 && myRect.Height > 0)
                {
                    // Only windows ABOVE us in z-order can occlude (Avalonia
                    // stacking matches manager order: index 0 = bottom, end = top).
                    for (int i = myIdx + 1; i < windows.Count; i++)
                    {
                        var other = windows[i];
                        if (other.WindowState == DOSI.CORE.UIComponents.WindowManagement.DOSIWindowState.Minimized) continue;
                        if (!other.IsVisible) continue;
                        var otherRect = new Avalonia.Rect(other.WindowX, other.WindowY,
                                                          other.WindowWidth, other.WindowHeight);
                        if (otherRect.Intersects(myRect)) { covered = true; break; }
                    }
                }
            }
        }

        var shouldBeVisible = !covered;
        if (_lastOcclusionVisible == shouldBeVisible) return;
        _lastOcclusionVisible = shouldBeVisible;
        _webView.SetVisible(shouldBeVisible, WebViewOverlayKind.Inactive);
    }

    /// <summary>
    /// Hides / re-shows the native WebView around a window drag so the page
    /// content doesn't appear to lag behind the Avalonia chrome (the native
    /// HWND can't keep up with the compositor at drag speed). The placeholder
    /// card shown by <see cref="WebViewWrapper"/> takes over for the drag
    /// duration so the window stays visually whole.
    /// </summary>
    private void OnWindowDragStateChanged(object? sender, bool isDragging)
    {
        if (_webView == null || !_isExternalPage) return;
        _webView.SetVisible(!isDragging, WebViewOverlayKind.Dragging);
    }

    /// <summary>
    /// Handles standard browser keyboard shortcuts. Bare keys (F11, F5, Escape)
    /// fire unconditionally; everything else requires Ctrl (or Alt for nav).
    /// Shortcuts that move focus into the address bar (Ctrl+L / Ctrl+E) are
    /// intentionally swallowed even when the address bar already has focus so
    /// the keystroke can't end up inserted as text.
    /// </summary>
    private void OnBrowserKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _isFullScreen)
        {
            ExitFullScreen();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F5)
        {
            Refresh();
            e.Handled = true;
            return;
        }

        // Accept either Ctrl (Windows / Linux convention) or Meta (macOS Cmd
        // convention) as the "command" modifier so browser shortcuts work
        // natively on every platform without OS branching. Avalonia maps
        // the macOS Command key to KeyModifiers.Meta.
        var cmd = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        var alt = (e.KeyModifiers & KeyModifiers.Alt) == KeyModifiers.Alt;
        var shift = (e.KeyModifiers & KeyModifiers.Shift) == KeyModifiers.Shift;

        if (alt && !cmd)
        {
            if (e.Key == Key.Left) { GoBack(); e.Handled = true; return; }
            if (e.Key == Key.Right) { GoForward(); e.Handled = true; return; }
        }

        if (!cmd) return;

        switch (e.Key)
        {
            case Key.T:
                OpenNewTab(null, activate: true);
                e.Handled = true;
                break;
            case Key.W:
                if (_activeTab != null) CloseTab(_activeTab);
                e.Handled = true;
                break;
            case Key.R:
                Refresh();
                e.Handled = true;
                break;
            case Key.L:
            case Key.E:
                _addressBar.Focus();
                _addressBar.SelectAll();
                e.Handled = true;
                break;
            case Key.Tab:
                CycleTab(forward: !shift);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Activates the next/previous tab in tab-strip order, wrapping at the ends.
    /// No-op when fewer than two tabs exist.
    /// </summary>
    private void CycleTab(bool forward)
    {
        if (_tabs.Count < 2 || _activeTab == null) return;
        var idx = _tabs.IndexOf(_activeTab);
        if (idx < 0) return;
        var next = forward
            ? (idx + 1) % _tabs.Count
            : (idx - 1 + _tabs.Count) % _tabs.Count;
        ActivateTab(_tabs[next]);
    }

    /// <summary>
    /// Toggles fullscreen mode for the browser.
    /// </summary>
    public void ToggleFullScreen()
    {
        if (_isFullScreen)
            ExitFullScreen();
        else
            EnterFullScreen();
    }

    /// <summary>
    /// Bridges the page's HTML5 Fullscreen API requests (e.g. clicking the
    /// fullscreen button on a YouTube video) into the browser's host-side
    /// fullscreen state. The wrapper's injected shim has already styled
    /// the target element to fill the WebView viewport - all we need to do
    /// here is hide our chrome and maximise the host window so the WebView
    /// itself takes the whole screen. Marshalled to the UI thread because
    /// the wrapper raises this from its message-receive handler which can
    /// arrive on a worker thread on some renderer backends.
    /// </summary>
    private void OnWebViewFullScreenChangeRequested(object? sender, bool enter)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (enter)
            {
                if (!_isFullScreen) EnterFullScreen();
            }
            else
            {
                if (_isFullScreen) ExitFullScreen(notifyPage: false);
            }
        });
    }

    /// <summary>
    /// Enters fullscreen mode - hides browser chrome and expands the host
    /// DOSI window to fill the entire desktop canvas.
    /// </summary>
    private void EnterFullScreen()
    {
        if (_isFullScreen) return;
        _isFullScreen = true;

        // Expand the inner DOSI window (NOT the host OS window - doing that
        // would make the entire DAX.OSI app go fullscreen on the user's
        // real Windows desktop, which is the wrong abstraction since
        // DAX.OSI IS the OS in this metaphor). EnterImmersiveFullScreen
        // also fires AnyWindowFullScreenChanged so DesktopScreen hides
        // its taskbar for the duration.
        EnterImmersiveFullScreen();

        // Hide the browser's own tab strip, toolbar and status bar so the
        // WebView takes the entire window content area edge-to-edge - matches
        // the chromeless experience of going fullscreen on a YouTube video
        // in a real browser.
        _tabStripContainer.IsVisible = false;
        _toolbarBorder.IsVisible = false;
        _statusBar.IsVisible = false;
    }

    /// <summary>
    /// Exits fullscreen mode - restores all chrome and window state.
    /// </summary>
    private void ExitFullScreen() => ExitFullScreen(notifyPage: true);

    private void ExitFullScreen(bool notifyPage)
    {
        if (!_isFullScreen) return;
        _isFullScreen = false;

        // Restore the inner DOSI window's geometry and chrome.
        ExitImmersiveFullScreen();

        // Show browser chrome again.
        _tabStripContainer.IsVisible = true;
        _toolbarBorder.IsVisible = true;
        _statusBar.IsVisible = true;

        // Drive the page out of HTML5 fullscreen too so YouTube etc. update
        // their player UI to match. notifyPage is false when the page itself
        // initiated the exit (the shim already cleared its own state) - in
        // that case calling back would be a redundant round-trip.
        if (notifyPage && _webView != null)
            _ = _webView.ExitPageFullscreenAsync();
    }

    private void OnAccentChanged(object? sender, EventArgs e)
    {
        // DOSITextBox handles its own theming, no need to update _addressBar

        // Update toolbar
        _toolbarBorder.Background = Accents.ControlBackgroundBrush;
        _toolbarBorder.BorderBrush = new SolidColorBrush(Accents.ControlBorder);

        // Update content area background (prevents white flash during drag)
        _contentArea.Background = Accents.WindowContentBrush;
        // _tabContentHost is transparent by design - the chrome behind it is
        // already painted by _contentArea, so no per-accent update needed.

        // Update status bar
        _statusBar.Background = Accents.ControlBackgroundBrush;
        _statusBar.BorderBrush = new SolidColorBrush(Accents.ControlBorder);
        _statusText.Foreground = Accents.TextSecondaryBrush;

        // Tab strip chrome + per-tab header colors track the live accent.
        _tabStripContainer.Background = Accents.WindowChromeBrush;
        _tabStripContainer.BorderBrush = new SolidColorBrush(Accents.ControlBorder);
        foreach (var tab in _tabs)
        {
            if (tab.HeaderText != null)
                tab.HeaderText.Foreground = Accents.TextPrimaryBrush;
        }
        UpdateAllTabHeaderVisuals();

        // Re-render internal pages to update accent colors
        if (!_isExternalPage && _currentUrl.StartsWith("dosi://", StringComparison.OrdinalIgnoreCase))
        {
            RenderPage(_currentUrl);
        }
    }

    private void OnAddressBarKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            NavigateTo(_addressBar.Text ?? "");
            e.Handled = true;
        }
    }

    private void NavigateTo(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        // Normalize URL
        url = url.Trim();
        if (!url.Contains("://"))
        {
            // Treat anything that looks like a host as a URL: a dotted name
            // (example.com), localhost, or localhost:port. Everything else
            // (with no dots, no colons, or containing whitespace) goes to
            // search.
            bool looksLikeHost =
                (url.Contains('.') && !url.Contains(' ')) ||
                url.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase);

            if (looksLikeHost)
                url = "https://" + url;
            else
                url = $"dosi://search?q={Uri.EscapeDataString(url)}";
        }

        // Resolve search redirects upfront. Letting CreateSearchPage do this
        // recursively from inside RenderPage caused the outer RenderPage to
        // continue and overwrite the freshly-mounted WebView with the temp
        // placeholder Border (Enter would search but the result page never
        // appeared - clicking Go worked because by then the address bar text
        // already held the resolved URL and skipped this branch).
        if (url.StartsWith("dosi://search?q=", StringComparison.OrdinalIgnoreCase))
        {
            var q = Uri.UnescapeDataString(url.Substring("dosi://search?q=".Length));
            if (!string.IsNullOrWhiteSpace(q))
                url = BrowserPreferences.Current.GetSearchUrl(q);
        }

        // Switching to an external URL means a fresh WebView for this tab.
        // Tear the previous one down here (instead of leaking it as the
        // "hidden" page behind the new content) so background tab webviews
        // don't pile up.
        if (_activeTab != null && _activeTab.WebView != null)
        {
            // Pull the old webview out of the shared content host BEFORE
            // disposing - leaving a disposed native WebView2/WKWebView in the
            // visual tree leaks its OS-level handle and corrupts the next
            // attach.
            RemoveTabContent(_activeTab.PageContent);
            try { _activeTab.WebView.Dispose(); } catch { }
            _activeTab.WebView = null;
            _activeTab.PageContent = null;
        }
        _webView = null;

        _currentUrl = url;
        _addressBar.Text = url;
        Title = GetPageTitle(url) + " - DOSI Browser";

        // Update history
        if (_historyIndex < _history.Count - 1)
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);

        _history.Add(url);
        _historyIndex = _history.Count - 1;

        UpdateNavigationButtons();
        RenderPage(url);
        // Refresh the tab title from the URL on every navigation. The WebView
        // will overwrite this with the page's real <title> as soon as it loads
        // (see TitleChanged below) - this just gives the tab a sensible label
        // immediately instead of leaving stale text from the previous URL.
        if (_activeTab != null)
        {
            _activeTab.TitleText = GetDisplayNameFromUrl(url);
            if (url.StartsWith("dosi://", StringComparison.OrdinalIgnoreCase))
            {
                // Internal page - clear any stale favicon left over from a
                // previous external navigation in this tab.
                _activeTab.FaviconForUrl = null;
                if (_activeTab.HeaderIcon != null)
                {
                    _activeTab.HeaderIcon.Source = null;
                    _activeTab.HeaderIcon.IsVisible = false;
                }
            }
            else
            {
                LoadFaviconAsync(_activeTab, url);
            }
        }
        SyncActiveTabState();
        UpdateTabHeaderVisuals(_activeTab);
    }

    private void GoBack()
    {
        if (_isExternalPage && _webView?.CanGoBack == true)
        {
            _webView.GoBack();
            return;
        }

        if (_historyIndex > 0)
        {
            _historyIndex--;
            var url = _history[_historyIndex];
            _currentUrl = url;
            _addressBar.Text = url;
            Title = GetPageTitle(url) + " - DOSI Browser";
            UpdateNavigationButtons();
            RenderPage(url);
        }
    }

    private void GoForward()
    {
        if (_isExternalPage && _webView?.CanGoForward == true)
        {
            _webView.GoForward();
            return;
        }

        if (_historyIndex < _history.Count - 1)
        {
            _historyIndex++;
            var url = _history[_historyIndex];
            _currentUrl = url;
            _addressBar.Text = url;
            Title = GetPageTitle(url) + " - DOSI Browser";
            UpdateNavigationButtons();
            RenderPage(url);
        }
    }

    private void Refresh()
    {
        if (_isExternalPage && _webView != null)
        {
            _webView.Refresh();
        }
        else
        {
            RenderPage(_currentUrl);
        }
    }

    private void UpdateNavigationButtons()
    {
        bool canGoBack, canGoForward;

        if (_isExternalPage && _webView != null)
        {
            canGoBack = _webView.CanGoBack || _historyIndex > 0;
            canGoForward = _webView.CanGoForward || _historyIndex < _history.Count - 1;
        }
        else
        {
            canGoBack = _historyIndex > 0;
            canGoForward = _historyIndex < _history.Count - 1;
        }

        // Use Tag to store enabled state (Border doesn't have IsEnabled)
        _backButton.Tag = canGoBack;
        _forwardButton.Tag = canGoForward;
        _backButton.Opacity = canGoBack ? 1.0 : 0.4;
        _forwardButton.Opacity = canGoForward ? 1.0 : 0.4;
    }

    private string GetPageTitle(string url)
    {
        if (url.StartsWith("dosi://home", StringComparison.OrdinalIgnoreCase)) return "Home";
        if (url.StartsWith("dosi://search", StringComparison.OrdinalIgnoreCase)) return "Search Results";
        if (url.StartsWith("dosi://settings", StringComparison.OrdinalIgnoreCase)) return "Settings";
        if (url.StartsWith("dosi://about", StringComparison.OrdinalIgnoreCase)) return "About DOSI Browser";
        if (url.StartsWith("dosi://error", StringComparison.OrdinalIgnoreCase)) return "Error";

        try
        {
            var uri = new Uri(url);
            return uri.Host;
        }
        catch
        {
            return "Page";
        }
    }

    private void RenderPage(string url)
    {
        _statusText.Text = $"Loading {url}...";

        Control pageContent;
        _isExternalPage = false;

        if (url.StartsWith("dosi://home", StringComparison.OrdinalIgnoreCase))
            pageContent = CreateHomePage();
        else if (url.StartsWith("dosi://search", StringComparison.OrdinalIgnoreCase))
            pageContent = CreateSearchPage(url);
        else if (url.StartsWith("dosi://about", StringComparison.OrdinalIgnoreCase))
            pageContent = CreateAboutPage();
        else if (url.StartsWith("dosi://settings", StringComparison.OrdinalIgnoreCase))
            pageContent = CreateSettingsPage();
        else if (url.StartsWith("dosi://", StringComparison.OrdinalIgnoreCase))
            pageContent = CreateErrorPage("Page Not Found", $"The page '{url}' could not be found.");
        else
        {
            // External URL - use real WebView
            pageContent = CreateWebViewPage(url);
            _isExternalPage = true;
            return; // WebView handles status updates
        }

        ShowTabContent(pageContent);
        _statusText.Text = "Done";
        if (_activeTab != null) _activeTab.PageContent = pageContent;
    }

    private Control CreateWebViewPage(string url)
    {
        _webView = new WebViewWrapper
        {
            // Honor the user's persisted zoom from the start so the first
            // paint of every page already lands at the right size instead of
            // flashing native-size first and then jumping.
            ZoomPercent = BrowserPreferences.Current.ZoomPercent
        };

        _webView.NavigationStarting += (s, navUrl) =>
        {
            _statusText.Text = $"Loading {navUrl}...";
            _addressBar.Text = navUrl;
            BeginLoadProgress();
        };

        _webView.NavigationCompleted += (s, navUrl) =>
        {
            _statusText.Text = "Done";
            _addressBar.Text = navUrl;
            _currentUrl = navUrl;
            UpdateNavigationButtons();
            SyncActiveTabState();
            EndLoadProgress();
        };

        _webView.TitleChanged += (s, title) =>
        {
            var resolved = string.IsNullOrEmpty(title) ? GetPageTitle(_currentUrl) : title;
            Title = resolved + " - DOSI Browser";
            if (_activeTab != null)
            {
                _activeTab.TitleText = resolved;
                UpdateTabHeaderVisuals(_activeTab);
            }
        };

        // Handle popup/new window requests - open in new DOSI browser window
        _webView.NewWindowRequested += (s, popupUrl) =>
        {
            var windowManager = WindowManager.Instance;
            if (windowManager != null)
            {
                var newBrowser = new DOSIWebBrowser(popupUrl);
                windowManager.OpenWindow(newBrowser);
            }
        };

        // Replace the renderer's built-in right-click menu (Chrome / WebKit
        // chrome) with a DOSIContextMenu. The wrapper has already injected
        // the JS bridge that suppresses the native menu and forwards click
        // context (link / image / selection) to us, so we just translate
        // that into menu items here.
        _webView.ContextMenuRequested += OnWebViewContextMenuRequested;

        // Hook the page-driven fullscreen requests so YouTube / HTML5 video
        // / fullscreen web games can actually expand to fill the window.
        // The wrapper's injected JS shim has already done the in-page work
        // (stretched the element to 100vw/100vh, faked document.fullscreenElement);
        // here we collapse the browser chrome and maximise the host window
        // so the WebView surface fills the OS window edge-to-edge.
        _webView.FullScreenChangeRequested += OnWebViewFullScreenChangeRequested;

        ShowTabContent(_webView);
        _webView.NavigateToUrl(url);
        if (_activeTab != null)
        {
            _activeTab.WebView = _webView;
            _activeTab.PageContent = _webView;
        }

        return _webView;
    }

    // ---- Tab content hosting helpers ---------------------------------------
    //
    // The shared _tabContentHost grid keeps every tab's PageContent in the
    // visual tree at the same time. Switching tabs only flips IsVisible so
    // native WebViews never get detached/reattached (which would force a
    // full page reload on every tab switch).

    /// <summary>
    /// Adds <paramref name="content"/> to the shared host if it isn't there
    /// yet, then makes it the only visible child. Idempotent: re-showing an
    /// already-visible page is a no-op cost-wise.
    /// </summary>
    private void ShowTabContent(Control content)
    {
        if (content == null) return;
        if (!_tabContentHost.Children.Contains(content))
            _tabContentHost.Children.Add(content);
        foreach (var child in _tabContentHost.Children)
            child.IsVisible = ReferenceEquals(child, content);
    }

    /// <summary>Removes a tab's content from the shared host (used when
    /// closing a tab or replacing its WebView).</summary>
    private void RemoveTabContent(Control? content)
    {
        if (content != null && _tabContentHost.Children.Contains(content))
            _tabContentHost.Children.Remove(content);
    }

    /// <summary>
    /// Builds and opens a context-aware <see cref="DOSIContextMenu"/> at the
    /// click position reported by the in-page JS bridge. Items are tailored
    /// to what was actually under the cursor (link, image, selection) so the
    /// menu mirrors what users expect from a real desktop browser instead of
    /// always showing the same generic list.
    /// </summary>
    private void OnWebViewContextMenuRequested(object? sender, WebViewContextMenuRequestedEventArgs e)
    {
        if (_webView == null) return;

        var menu = new DOSIContextMenu();

        if (!string.IsNullOrEmpty(e.LinkUrl))
        {
            var openInNewTab = new MenuItem { Header = "Open Link in New Tab" };
            openInNewTab.Click += (_, _) => OpenNewTab(e.LinkUrl, activate: true);
            menu.Items.Add(openInNewTab);

            var openInNew = new MenuItem { Header = "Open Link in New Window" };
            openInNew.Click += (_, _) =>
            {
                var wm = WindowManager.Instance;
                if (wm != null) wm.OpenWindow(new DOSIWebBrowser(e.LinkUrl));
            };
            menu.Items.Add(openInNew);

            menu.Items.Add(new Separator());
        }

        if (!string.IsNullOrEmpty(e.ImageUrl))
        {
            var openImageInTab = new MenuItem { Header = "Open Image in New Tab" };
            openImageInTab.Click += (_, _) => OpenNewTab(e.ImageUrl, activate: true);
            menu.Items.Add(openImageInTab);

            var openImage = new MenuItem { Header = "Open Image in New Window" };
            openImage.Click += (_, _) =>
            {
                var wm = WindowManager.Instance;
                if (wm != null) wm.OpenWindow(new DOSIWebBrowser(e.ImageUrl));
            };
            menu.Items.Add(openImage);

            menu.Items.Add(new Separator());
        }

        var back = new MenuItem { Header = "Back", IsEnabled = _webView.CanGoBack };
        back.Click += (_, _) => GoBack();
        menu.Items.Add(back);

        var forward = new MenuItem { Header = "Forward", IsEnabled = _webView.CanGoForward };
        forward.Click += (_, _) => GoForward();
        menu.Items.Add(forward);

        var refresh = new MenuItem { Header = "Refresh" };
        refresh.Click += (_, _) => Refresh();
        menu.Items.Add(refresh);

        // Note: Copy / Copy Link / Copy Image items intentionally omitted -
        // Avalonia 12 is mid-migration from DataObject/DataFormats to
        // DataTransfer/DataFormat with no stable convenience API for plain
        // text yet, and we'd rather show a smaller correct menu than ship
        // copy buttons that throw at runtime. Re-add once Avalonia.Input
        // exposes a non-deprecated SetTextAsync-equivalent.

        // Anchor the popup at the click point inside the WebView. The JS
        // bridge reports coordinates in CSS pixels relative to the WebView,
        // which lines up with the PlacementTarget's local coordinate space.
        menu.PlacementTarget = _webView;
        menu.Placement = PlacementMode.AnchorAndGravity;
        menu.PlacementAnchor = Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.TopLeft;
        menu.PlacementGravity = Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.BottomRight;
        menu.HorizontalOffset = e.X;
        menu.VerticalOffset = e.Y;
        menu.Open(_webView);
    }

    private Control CreateHomePage()
    {
        var container = new DOSIScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };

        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Spacing = 18,
            Margin = new Thickness(40, 80, 40, 60),
            MaxWidth = 760
        };

        // ---- Hero greeting -------------------------------------------------
        // Time-of-day aware so the home page feels alive across the day
        // instead of staring back with the same headline every time.
        var hour = DateTime.Now.Hour;
        var greeting = hour switch
        {
            >= 5 and < 12 => "Good morning",
            >= 12 and < 18 => "Good afternoon",
            >= 18 and < 22 => "Good evening",
            _ => "Hello"
        };

        var hero = new TextBlock
        {
            Text = greeting,
            FontSize = 44,
            FontWeight = FontWeight.Bold,
            Foreground = Accents.AccentPrimaryBrush,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        content.Children.Add(hero);

        var subhero = new TextBlock
        {
            Text = "Where would you like to go?",
            FontSize = 16,
            Foreground = Accents.TextSecondaryBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, -6, 0, 14)
        };
        content.Children.Add(subhero);

        // ---- Centered search bar ------------------------------------------
        // Functional - submitting routes through NavigateTo (which dispatches
        // to the user's chosen search engine, same as the address bar).
        var searchBox = new DOSITextBox
        {
            PlaceholderText = $"Search {BrowserPreferences.GetEngineLabel(BrowserPreferences.Current.SearchEngine)} or type a URL",
            FontSize = 14,
            UseRoundedEnds = true,
            Padding = new Thickness(20, 10),
            Height = 44,
            Width = 540,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        searchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                NavigateTo(searchBox.Text ?? string.Empty);
                e.Handled = true;
            }
        };
        var searchHost = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0, 6, 0, 6),
            Child = searchBox
        };
        content.Children.Add(searchHost);

        // ---- Quick links section (kind-grouped cards) ---------------------
        content.Children.Add(new TextBlock
        {
            Text = "QUICK LINKS",
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.7,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 26, 0, 4)
        });

        var linksPanel = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Orientation = Orientation.Horizontal,
            ItemWidth = 170
        };

        linksPanel.Children.Add(CreateQuickLink("Google",   "https://www.google.com",  Color.FromRgb(66, 133, 244)));
        linksPanel.Children.Add(CreateQuickLink("YouTube",  "https://www.youtube.com", Color.FromRgb(255, 0, 0)));
        linksPanel.Children.Add(CreateQuickLink("GitHub",   "https://github.com",      Color.FromRgb(36, 41, 47)));
        linksPanel.Children.Add(CreateQuickLink("Reddit",   "https://www.reddit.com",  Color.FromRgb(255, 69, 0)));
        linksPanel.Children.Add(CreateQuickLink("Settings", "dosi://settings",         Accents.AccentPrimary));
        linksPanel.Children.Add(CreateQuickLink("About",    "dosi://about",            Accents.AccentSecondary));

        content.Children.Add(linksPanel);

        // ---- Footer line --------------------------------------------------
        var footer = new TextBlock
        {
            Text = $"DOSI Browser · default search: {BrowserPreferences.GetEngineLabel(BrowserPreferences.Current.SearchEngine)}",
            FontSize = 11,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 28, 0, 0)
        };
        content.Children.Add(footer);

        container.Content = content;

        // Theme-aware backdrop so the home page reads cleanly in both light
        // and dark accents instead of glaring white when the rest of DOSI is
        // dark.
        return new Border
        {
            Background = Accents.WindowContentBrush,
            Child = container
        };
    }

    private Control CreateQuickLink(string text, string url, Color brand)
    {
        // Brand badge: solid circle in the site's brand color with the
        // first letter of its name. Avoids relying on emoji glyphs that
        // can mojibake on file re-save (the previous "??" boxes).
        var badge = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(brand),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = string.IsNullOrEmpty(text)
                    ? "\u003F"
                    : char.ToUpperInvariant(text[0]).ToString(),
                FontSize = 12,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var buttonContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                badge,
                new TextBlock
                {
                    Text = text,
                    FontSize = 14,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Accents.TextPrimaryBrush
                }
            }
        };

        var button = new Border
        {
            Background = Accents.ButtonBackgroundBrush,
            BorderBrush = new SolidColorBrush(Accents.ControlBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 8, 18, 8),
            Margin = new Thickness(5),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = buttonContent
        };

        // Hover effects using accent colors
        button.PointerEntered += (s, e) => button.Background = Accents.ButtonBackgroundHoverBrush;
        button.PointerExited += (s, e) => button.Background = Accents.ButtonBackgroundBrush;
        button.PointerPressed += (s, e) => button.Background = Accents.ButtonBackgroundPressedBrush;
        button.PointerReleased += (s, e) =>
        {
            button.Background = Accents.ButtonBackgroundHoverBrush;
            NavigateTo(url);
        };

        return button;
    }

    private Control CreateSearchPage(string url)
    {
        var query = "";
        if (url.Contains("?q="))
        {
            var queryStart = url.IndexOf("?q=") + 3;
            query = Uri.UnescapeDataString(url[queryStart..]);
        }

        // If there's a search query, redirect to the user's chosen search engine.
        // Note: NavigateTo() resolves search URLs upfront now, so this branch
        // only fires for direct dosi://search?q= renders that bypass NavigateTo
        // (e.g. back/forward through history). Kept as a safety net.
        if (!string.IsNullOrEmpty(query))
        {
            NavigateTo(BrowserPreferences.Current.GetSearchUrl(query));
            return new Border(); // Temporary, will be replaced by WebView
        }

        var content = new StackPanel
        {
            Margin = new Thickness(40, 30),
            Spacing = 15
        };

        var title = new TextBlock
        {
            Text = "Search",
            FontSize = 24,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextPrimaryBrush
        };
        content.Children.Add(title);

        var placeholder = new TextBlock
        {
            Text = "Enter a search query in the address bar above.",
            FontSize = 14,
            Foreground = Accents.TextSecondaryBrush
        };
        content.Children.Add(placeholder);

        return new DOSIScrollViewer { Content = content };
    }

    private Control CreateAboutPage()
    {
        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 15,
            Margin = new Thickness(40)
        };

        var logo = new TextBlock
        {
            Text = "DOSI Browser",
            FontSize = 36,
            FontWeight = FontWeight.Bold,
            Foreground = Accents.AccentPrimaryBrush,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        content.Children.Add(logo);

        var version = new TextBlock
        {
            Text = "Version 1.0.0",
            FontSize = 16,
            Foreground = Accents.TextSecondaryBrush,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        content.Children.Add(version);

        var description = new TextBlock
        {
            Text = "A modern, fast, and secure web browser\nbuilt for the DOSI operating system.\n\nPowered by the host platform's native web renderer\n(WebView2 on Windows, WKWebView on macOS, WebKitGTK on Linux).",
            FontSize = 14,
            Foreground = Accents.TextPrimaryBrush,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0)
        };
        content.Children.Add(description);

        var copyright = new TextBlock
        {
            Text = "\u00A9 2024 DOSI Corporation. All rights reserved.",
            FontSize = 12,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.7,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 20, 0, 0)
        };
        content.Children.Add(copyright);

        return new Border
        {
            Child = content,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private Control CreateSettingsPage()
    {
        var prefs = BrowserPreferences.Current;

        var content = new StackPanel
        {
            Margin = new Thickness(48, 36, 48, 48),
            Spacing = 18,
            MaxWidth = 880,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        // ---- Hero header -------------------------------------------------
        // A two-line header (eyebrow + title) plus a soft accent ribbon
        // anchors the page so the section cards feel like a list under it
        // rather than five floating tiles.
        var eyebrow = new TextBlock
        {
            Text = "DOSI BROWSER",
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.AccentPrimaryBrush,
            Opacity = 0.85,
            Margin = new Thickness(0, 0, 0, 2),
            // Faux letter-spacing - Avalonia TextBlock has no LetterSpacing
            // property, but a bit of padding around each glyph reads as
            // tracked uppercase to the eye.
            LineHeight = 14
        };
        var pageTitle = new TextBlock
        {
            Text = "Settings",
            FontSize = 32,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextPrimaryBrush
        };
        var pageSubtitle = new TextBlock
        {
            Text = "Personalize how the browser starts up, searches, protects you, and downloads.",
            FontSize = 13,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.9,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 640
        };
        var ribbon = new Border
        {
            Height = 3,
            Width = 56,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 14, 0, 18),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Accents.AccentPrimary, 0),
                    new GradientStop(Color.FromArgb(0,
                        Accents.AccentPrimary.R, Accents.AccentPrimary.G, Accents.AccentPrimary.B), 1)
                }
            },
            HorizontalAlignment = HorizontalAlignment.Left
        };

        content.Children.Add(eyebrow);
        content.Children.Add(pageTitle);
        content.Children.Add(pageSubtitle);
        content.Children.Add(ribbon);

        // ---- Functional section cards ------------------------------------
        content.Children.Add(BuildHomePageCard(prefs));
        content.Children.Add(BuildSearchEngineCard(prefs));
        content.Children.Add(BuildPrivacyCard(prefs));
        content.Children.Add(BuildAppearanceCard(prefs));
        content.Children.Add(BuildDownloadsCard(prefs));

        // Footer with file location + reset to defaults.
        content.Children.Add(BuildFooterRow(prefs));

        return new DOSIScrollViewer { Content = content };
    }

    /// <summary>
    /// Shared card chrome for a settings section. Renders a rounded surface
    /// with a circular accent-tinted icon, the section title + description,
    /// and the supplied control region beneath them.
    /// </summary>
    private Border CreateSettingsCard(Geometry icon, string title, string description, Control body)
    {
        var iconShape = new Avalonia.Controls.Shapes.Path
        {
            Data = icon,
            Fill = Accents.AccentPrimaryBrush,
            Stretch = Stretch.Uniform,
            Width = 18,
            Height = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var iconBubble = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(19),
            Background = new SolidColorBrush(Color.FromArgb(36,
                Accents.AccentPrimary.R, Accents.AccentPrimary.G, Accents.AccentPrimary.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(70,
                Accents.AccentPrimary.R, Accents.AccentPrimary.G, Accents.AccentPrimary.B)),
            BorderThickness = new Thickness(1),
            Child = iconShape,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 14, 0)
        };

        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextPrimaryBrush
        };

        var descText = new TextBlock
        {
            Text = description,
            FontSize = 12,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.95,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };

        var headerStack = new StackPanel { Spacing = 0 };
        headerStack.Children.Add(titleText);
        headerStack.Children.Add(descText);

        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*")
        };
        headerGrid.Children.Add(iconBubble);
        Grid.SetColumn(iconBubble, 0);
        headerGrid.Children.Add(headerStack);
        Grid.SetColumn(headerStack, 1);

        var stack = new StackPanel { Spacing = 14 };
        stack.Children.Add(headerGrid);
        stack.Children.Add(body);

        return new Border
        {
            Background = Accents.WindowChromeBrush,
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20, 16, 20, 18),
            Child = stack,
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0,
                OffsetY = 4,
                Blur = 18,
                Spread = 0,
                Color = Color.FromArgb(28, 0, 0, 0)
            })
        };
    }

    // ---- Section: Home Page -------------------------------------------------

    private Control BuildHomePageCard(BrowserPreferences prefs)
    {
        var input = new DOSITextBox
        {
            Text = prefs.HomeUrl,
            PlaceholderText = "https://example.com or dosi://home",
            Width = 420,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var save = new DOSIButton { Text = "Save" };
        var reset = new DOSIButton { Text = "Use DOSI Home" };
        var status = new TextBlock
        {
            FontSize = 11,
            Foreground = Accents.AccentPrimaryBrush,
            Opacity = 0,
            VerticalAlignment = VerticalAlignment.Center
        };

        save.Click += (_, _) =>
        {
            var v = (input.Text ?? "").Trim();
            if (string.IsNullOrEmpty(v)) v = "dosi://home";
            prefs.HomeUrl = v;
            prefs.Save();
            FlashStatus(status, "\u2713 Saved");
        };
        reset.Click += (_, _) =>
        {
            input.Text = "dosi://home";
            prefs.HomeUrl = "dosi://home";
            prefs.Save();
            FlashStatus(status, "\u2713 Reset to default");
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        row.Children.Add(save);
        row.Children.Add(reset);
        row.Children.Add(status);

        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(input);
        body.Children.Add(row);

        return CreateSettingsCard(BuildHomeIcon(),
            "Home page",
            "The page DOSI Browser opens when you click the home button or open a fresh window.",
            body);
    }

    // ---- Section: Search Engine ---------------------------------------------

    private Control BuildSearchEngineCard(BrowserPreferences prefs)
    {
        var grid = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 0,
            Margin = new Thickness(0, 4, 0, 0)
        };
        // Track the radio group locally so picking one updates the chrome
        // of the others without a full page rebuild.
        var rows = new System.Collections.Generic.List<Border>();
        var engines = new[]
        {
            BrowserSearchEngine.Google,
            BrowserSearchEngine.Bing,
            BrowserSearchEngine.DuckDuckGo,
            BrowserSearchEngine.Brave,
            BrowserSearchEngine.Startpage,
        };

        foreach (var engine in engines)
        {
            var row = BuildSearchEngineRow(engine, prefs.SearchEngine == engine);
            row.Tag = engine;
            row.PointerReleased += (_, _) =>
            {
                prefs.SearchEngine = engine;
                prefs.Save();
                foreach (var r in rows) StyleSearchEngineRow(r, (BrowserSearchEngine)r.Tag! == engine);
            };
            rows.Add(row);
            grid.Children.Add(row);
        }

        return CreateSettingsCard(BuildSearchIcon(),
            "Search engine",
            "Address bar searches and the built-in Search page route through your choice.",
            grid);
    }

    private Border BuildSearchEngineRow(BrowserSearchEngine engine, bool selected)
    {
        var name = new TextBlock
        {
            Text = BrowserPreferences.GetEngineLabel(engine),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextPrimaryBrush
        };
        var sub = new TextBlock
        {
            Text = BrowserPreferences.GetEngineTagline(engine),
            FontSize = 11,
            Foreground = Accents.TextSecondaryBrush,
            Margin = new Thickness(0, 1, 0, 0)
        };
        var labels = new StackPanel { Spacing = 0 };
        labels.Children.Add(name);
        labels.Children.Add(sub);

        var dot = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(7),
            BorderBrush = Accents.AccentPrimaryBrush,
            BorderThickness = new Thickness(2),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*")
        };
        grid.Children.Add(dot);
        Grid.SetColumn(dot, 0);
        grid.Children.Add(labels);
        Grid.SetColumn(labels, 1);

        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10),
            Margin = new Thickness(0, 0, 0, 6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = grid
        };
        StyleSearchEngineRow(card, selected);
        // Subtle hover wash that complements the picker selection.
        card.PointerEntered += (_, _) =>
        {
            if (card.Tag is BrowserSearchEngine eng &&
                BrowserPreferences.Current.SearchEngine != eng)
            {
                card.Background = new SolidColorBrush(Color.FromArgb(20,
                    Accents.AccentPrimary.R, Accents.AccentPrimary.G, Accents.AccentPrimary.B));
            }
        };
        card.PointerExited += (_, _) =>
        {
            if (card.Tag is BrowserSearchEngine eng)
                StyleSearchEngineRow(card, BrowserPreferences.Current.SearchEngine == eng);
        };
        return card;
    }

    private void StyleSearchEngineRow(Border card, bool selected)
    {
        card.Background = selected
            ? new SolidColorBrush(Color.FromArgb(40,
                Accents.AccentPrimary.R, Accents.AccentPrimary.G, Accents.AccentPrimary.B))
            : Accents.ControlBackgroundBrush;
        card.BorderBrush = selected
            ? Accents.AccentPrimaryBrush
            : new SolidColorBrush(Color.FromArgb(40, 128, 128, 128));
        card.BorderThickness = new Thickness(selected ? 1.5 : 1);

        // Update the radio dot fill based on selection.
        if (card.Child is Grid g && g.Children.Count > 0 && g.Children[0] is Border dot)
        {
            dot.Background = selected ? Accents.AccentPrimaryBrush : Brushes.Transparent;
        }
    }

    // ---- Section: Privacy ---------------------------------------------------

    private Control BuildPrivacyCard(BrowserPreferences prefs)
    {
        var dnt = BuildToggleRow(
            "Send Do Not Track",
            "Politely asks each site not to profile you. Honored at the site\u2019s discretion.",
            prefs.SendDoNotTrack,
            on => { prefs.SendDoNotTrack = on; prefs.Save(); });

        var clear = new DOSIButton { Text = "Clear other tabs" };
        var clearStatus = new TextBlock
        {
            FontSize = 11,
            Foreground = Accents.AccentPrimaryBrush,
            Opacity = 0,
            VerticalAlignment = VerticalAlignment.Center
        };
        clear.Click += (_, _) =>
        {
            // Close every tab except the currently active one (the settings
            // page itself) so the user keeps a navigable surface.
            var snapshot = _tabs.ToArray();
            int closed = 0;
            foreach (var t in snapshot)
            {
                if (t == _activeTab) continue;
                CloseTab(t);
                closed++;
            }
            FlashStatus(clearStatus,
                closed == 0 ? "No other tabs open" : $"\u2713 Closed {closed} tab" + (closed == 1 ? "" : "s"));
        };
        var clearRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        clearRow.Children.Add(clear);
        clearRow.Children.Add(clearStatus);

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(dnt);
        body.Children.Add(BuildSubtleDivider());
        body.Children.Add(new TextBlock
        {
            Text = "Browsing data",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextSecondaryBrush
        });
        body.Children.Add(clearRow);

        return CreateSettingsCard(BuildShieldIcon(),
            "Privacy & security",
            "Tracker hints, cookies, and the keys to clearing your local browsing trail.",
            body);
    }

    // ---- Section: Appearance ------------------------------------------------

    private Control BuildAppearanceCard(BrowserPreferences prefs)
    {
        var label = new TextBlock
        {
            Text = "Page zoom",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextSecondaryBrush
        };
        var current = new TextBlock
        {
            Text = $"{prefs.ZoomPercent}%",
            FontSize = 12,
            Foreground = Accents.AccentPrimaryBrush,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var labelRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        labelRow.Children.Add(label); Grid.SetColumn(label, 0);
        labelRow.Children.Add(current); Grid.SetColumn(current, 1);

        var presets = new[] { 75, 90, 100, 125, 150 };
        var pillRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var pills = new System.Collections.Generic.List<Border>();
        foreach (var z in presets)
        {
            var pill = BuildZoomPill(z, prefs.ZoomPercent == z);
            pill.Tag = z;
            pill.PointerReleased += (_, _) =>
            {
                prefs.ZoomPercent = z;
                prefs.Save();
                current.Text = $"{z}%";
                foreach (var p in pills) StyleZoomPill(p, (int)p.Tag! == z);
            };
            pills.Add(pill);
            pillRow.Children.Add(pill);
        }

        var hint = new TextBlock
        {
            Text = "Applied to web pages the next time they load.",
            FontSize = 11,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.85,
            Margin = new Thickness(0, 4, 0, 0)
        };

        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(labelRow);
        body.Children.Add(pillRow);
        body.Children.Add(hint);

        return CreateSettingsCard(BuildPaletteIcon(),
            "Appearance",
            "Control how dense the page reads. Browser chrome follows your DOSI accent automatically.",
            body);
    }

    private Border BuildZoomPill(int zoom, bool selected)
    {
        var label = new TextBlock
        {
            Text = $"{zoom}%",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var pill = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = label
        };
        StyleZoomPill(pill, selected);
        pill.PointerEntered += (_, _) =>
        {
            if (pill.Tag is int z && BrowserPreferences.Current.ZoomPercent != z)
                pill.Background = new SolidColorBrush(Color.FromArgb(28,
                    Accents.AccentPrimary.R, Accents.AccentPrimary.G, Accents.AccentPrimary.B));
        };
        pill.PointerExited += (_, _) =>
        {
            if (pill.Tag is int z) StyleZoomPill(pill, BrowserPreferences.Current.ZoomPercent == z);
        };
        return pill;
    }

    private void StyleZoomPill(Border pill, bool selected)
    {
        pill.Background = selected
            ? Accents.AccentPrimaryBrush
            : Accents.ControlBackgroundBrush;
        pill.BorderBrush = selected
            ? Accents.AccentPrimaryBrush
            : new SolidColorBrush(Color.FromArgb(50, 128, 128, 128));
        pill.BorderThickness = new Thickness(1);
        if (pill.Child is TextBlock t)
        {
            t.Foreground = selected
                ? new SolidColorBrush(Accents.TextOnAccent)
                : Accents.TextPrimaryBrush;
        }
    }

    // ---- Section: Downloads -------------------------------------------------

    private Control BuildDownloadsCard(BrowserPreferences prefs)
    {
        var input = new DOSITextBox
        {
            Text = prefs.DownloadFolder,
            // Show the host OS's actual default downloads path so the hint
            // matches what the user would type on their platform (Windows
            // backslash-rooted, POSIX forward-slash-rooted).
            PlaceholderText = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var save = new DOSIButton { Text = "Save" };
        var reset = new DOSIButton { Text = "Use system default" };
        var status = new TextBlock
        {
            FontSize = 11,
            Foreground = Accents.AccentPrimaryBrush,
            Opacity = 0,
            VerticalAlignment = VerticalAlignment.Center
        };
        save.Click += (_, _) =>
        {
            prefs.DownloadFolder = (input.Text ?? "").Trim();
            prefs.Save();
            FlashStatus(status, "\u2713 Saved");
        };
        reset.Click += (_, _) =>
        {
            var def = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");
            input.Text = def;
            prefs.DownloadFolder = def;
            prefs.Save();
            FlashStatus(status, "\u2713 Reset to default");
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(save);
        row.Children.Add(reset);
        row.Children.Add(status);

        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(input);
        body.Children.Add(row);

        return CreateSettingsCard(BuildDownloadIcon(),
            "Downloads",
            "Where files saved from web pages should land on disk.",
            body);
    }

    // ---- Footer / shared bits ----------------------------------------------

    private Control BuildFooterRow(BrowserPreferences prefs)
    {
        var location = new TextBlock
        {
            Text = $"Settings stored at {BrowserPreferences.FilePath}",
            FontSize = 11,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.75,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var resetAll = new DOSIButton { Text = "Reset all to defaults" };
        resetAll.Click += (_, _) =>
        {
            var fresh = new BrowserPreferences();
            prefs.HomeUrl = fresh.HomeUrl;
            prefs.SearchEngine = fresh.SearchEngine;
            prefs.SendDoNotTrack = fresh.SendDoNotTrack;
            prefs.ZoomPercent = fresh.ZoomPercent;
            prefs.DownloadFolder = fresh.DownloadFolder;
            prefs.Save();
            // Simplest visible feedback: re-render the settings page so every
            // control reflects the freshly-defaulted values in one pass.
            RenderPage("dosi://settings");
        };
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 6, 0, 0)
        };
        grid.Children.Add(location); Grid.SetColumn(location, 0);
        grid.Children.Add(resetAll); Grid.SetColumn(resetAll, 1);
        return grid;
    }

    private Border BuildSubtleDivider() => new()
    {
        Height = 1,
        Background = new SolidColorBrush(Color.FromArgb(36, 128, 128, 128)),
        Margin = new Thickness(0, 4, 0, 4)
    };

    /// <summary>
    /// Builds an inline label + accent-pill toggle. We don't have a
    /// DOSIToggleSwitch so this draws one inline: a rounded background and
    /// a small knob that animates left/right via a TranslateTransform.
    /// </summary>
    private Border BuildToggleRow(string title, string description, bool initial, Action<bool> onChanged)
    {
        var label = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextPrimaryBrush
        };
        var sub = new TextBlock
        {
            Text = description,
            FontSize = 11,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.95,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        };
        var labels = new StackPanel { Spacing = 0 };
        labels.Children.Add(label);
        labels.Children.Add(sub);

        var knob = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(2, 0, 0, 0),
            BoxShadow = new BoxShadows(new BoxShadow
            { OffsetX = 0, OffsetY = 1, Blur = 3, Color = Color.FromArgb(80, 0, 0, 0) }),
            RenderTransform = new TranslateTransform(initial ? 18 : 0, 0)
        };
        var track = new Border
        {
            Width = 40,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = knob
        };
        bool state = initial;
        void Apply()
        {
            track.Background = state
                ? Accents.AccentPrimaryBrush
                : new SolidColorBrush(Color.FromArgb(60, 128, 128, 128));
            if (knob.RenderTransform is TranslateTransform tt) tt.X = state ? 18 : 0;
        }
        Apply();
        track.PointerReleased += (_, _) =>
        {
            state = !state;
            Apply();
            onChanged(state);
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(labels); Grid.SetColumn(labels, 0);
        grid.Children.Add(track);  Grid.SetColumn(track, 1);

        return new Border
        {
            Padding = new Thickness(0),
            Child = grid
        };
    }

    /// <summary>Briefly fades the supplied label in and out so the user gets
    /// confirmation that their click did something without a popup.</summary>
    private static async void FlashStatus(TextBlock label, string text)
    {
        try
        {
            label.Text = text;
            label.Opacity = 1;
            await System.Threading.Tasks.Task.Delay(1500);
            label.Opacity = 0;
        }
        catch { }
    }

    // ---- Inline vector icons (replaces the old "??" placeholder emoji) -----
    // Tiny 24x24 path geometries, all centered in a 0..24 viewbox so
    // CreateSettingsCard's icon bubble can render them at any size.

    private static Geometry BuildHomeIcon() =>
        Geometry.Parse("M12 3 L2 12 H5 V21 H10 V14 H14 V21 H19 V12 H22 Z");

    private static Geometry BuildSearchIcon() =>
        Geometry.Parse("M10 2 A8 8 0 1 0 10 18 A8 8 0 1 0 10 2 M16 16 L22 22");

    private static Geometry BuildShieldIcon() =>
        Geometry.Parse("M12 2 L4 5 V12 C4 17 8 21 12 22 C16 21 20 17 20 12 V5 Z M9 12 L11 14 L15 10");

    private static Geometry BuildPaletteIcon() =>
        Geometry.Parse("M12 2 A10 10 0 1 0 12 22 C13 22 13 21 13 20 C13 19 12 19 12 18 C12 17 13 16 14 16 H17 A4 4 0 0 0 21 12 A10 10 0 0 0 12 2 M7 12 A1.5 1.5 0 1 1 7 12.01 M11 7 A1.5 1.5 0 1 1 11 7.01 M17 9 A1.5 1.5 0 1 1 17 9.01");

    private static Geometry BuildDownloadIcon() =>
        Geometry.Parse("M12 3 V15 M6 11 L12 17 L18 11 M4 21 H20");

    private Control CreateErrorPage(string title, string message)
    {
        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 15,
            Margin = new Thickness(40)
        };

        var icon = new TextBlock
        {
            // U+26A0 warning sign ⚠ - reads as a clear "something's wrong"
            // marker on every platform without depending on color emoji fonts.
            Text = "\u26A0",
            FontSize = 48,
            Foreground = Accents.AccentPrimaryBrush,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        content.Children.Add(icon);

        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 24,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.AccentPrimaryBrush,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        content.Children.Add(titleText);

        var messageText = new TextBlock
        {
            Text = message,
            FontSize = 14,
            Foreground = Accents.TextSecondaryBrush,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        content.Children.Add(messageText);

        var homeButton = new Button
        {
            Content = "Go to Home Page",
            Padding = new Thickness(20, 10),
            Background = Accents.AccentPrimaryBrush,
            Foreground = new SolidColorBrush(Colors.White),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 10, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        homeButton.Click += (s, e) => NavigateTo("dosi://home");
        content.Children.Add(homeButton);

        return new Border
        {
            Child = content,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    /// <summary>
    /// Path-based arrow glyph used by Back / Forward / Go so all three render
    /// identically (same stem length, same head angle, same stroke weight) on
    /// any platform / font fallback. Avoids the visual mismatch you get when
    /// \u2190 / \u2192 happen to resolve to different fonts.
    /// </summary>
    private static Control BuildArrowGlyph(bool pointsRight)
    {
        // Geometry: stem from x=4 to x=12 at y=8, head from (8,4)-(12,8)-(8,12).
        // Designed inside a 16x16 box that matches the prior FontSize=16 footprint.
        const string rightArrow = "M 4,8 L 12,8 M 8,4 L 12,8 L 8,12";
        var path = BuildGlyphPath(rightArrow);
        if (!pointsRight)
        {
            // Mirror horizontally for the back arrow so the geometry, stroke
            // weight, and head proportions stay perfectly consistent with the
            // forward / go variants - just reflected.
            path.RenderTransform = new ScaleTransform(-1, 1);
            path.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        }
        return path;
    }

    /// <summary>
    /// Reload glyph: two opposing horizontal arrows stacked vertically (top
    /// points right, bottom points left). Universal "sync / swap / reload"
    /// metaphor, drawn entirely from straight stems and straight-line
    /// arrowheads - the same drawing primitives the back / forward / home
    /// glyphs use.
    ///
    /// Why not a circular arc: a curved path inscribed in the SAME 4..12
    /// bounding box as a straight-edged glyph carries less visual weight at
    /// the corners (its ink only ever touches the box at four points), so it
    /// always reads as smaller and visually off-center next to the straight
    /// arrows even when its geometric midpoint matches theirs exactly. An
    /// angular two-arrow design sidesteps that whole class of optical-sizing
    /// problem by sharing the SAME ink distribution as its toolbar neighbours.
    /// </summary>
    private static Control BuildRefreshGlyph()
    {
        // Top arrow: horizontal stem at y=6 from (4,6) to (11,6), with a
        // right-pointing arrowhead at (11,6) drawn the same way as the
        // forward arrow's head.
        // Bottom arrow: horizontal stem at y=10 from (12,10) to (5,10), with
        // a left-pointing arrowhead at (5,10) drawn the same way as the back
        // arrow's head.
        // Both stems sit symmetrically about the (8,8) midpoint and the
        // combined geometry fills the 4..12 box edge-to-edge - matching the
        // straight-arrow glyphs pixel-for-pixel inside the 32x32 nav button.
        const string geometry =
            "M 4,6 L 11,6 M 8,4 L 11,6 L 8,8 " +     // top: right-pointing arrow
            "M 12,10 L 5,10 M 8,8 L 5,10 L 8,12";    // bottom: left-pointing arrow

        return BuildGlyphPath(geometry);
    }

    /// <summary>
    /// House glyph: a roof triangle on top of a small square body. Same 16x16
    /// footprint and stroke weight as the rest of the toolbar so it visually
    /// matches the arrows and refresh icon.
    /// </summary>
    private static Control BuildHomeGlyph()
    {
        // Roof: (3,8) -> (8,3) -> (13,8). Body: rectangle from (4.5,8) to
        // (11.5,13). Drawn as one continuous path so the stroke joins look
        // crisp at the corners.
        const string home =
            "M 3,8.5 L 8,3.5 L 13,8.5 " +
            "M 4.5,8 L 4.5,13 L 11.5,13 L 11.5,8";
        return BuildGlyphPath(home);
    }

    /// <summary>
    /// Shared Path factory for every nav-button glyph. Centralises stroke
    /// settings, the 16x16 footprint, and the live accent re-stroke so a
    /// fresh icon stays one line of geometry data instead of repeating the
    /// boilerplate.
    /// </summary>
    private static Avalonia.Controls.Shapes.Path BuildGlyphPath(string geometry)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(geometry),
            Stroke = Accents.TextPrimaryBrush,
            StrokeThickness = 1.6,
            StrokeJoin = PenLineJoin.Round,
            StrokeLineCap = PenLineCap.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 16,
            Height = 16
        };

        // Live-recolor on accent change so the glyph tracks the foreground
        // brush (e.g. flips dark <-> light when the user switches accents).
        EventHandler onAccent = (_, _) => path.Stroke = Accents.TextPrimaryBrush;
        path.AttachedToVisualTree += (_, _) => Accents.AccentChanged += onAccent;
        path.DetachedFromVisualTree += (_, _) => Accents.AccentChanged -= onAccent;

        return path;
    }

    /// <summary>
    /// Starts the indeterminate "breathing" pulse on the status-bar load
    /// strip. Idempotent: re-calling while already pulsing is a no-op.
    /// </summary>
    private void BeginLoadProgress()
    {
        if (_loadProgress == null) return;
        _loadProgress.IsVisible = true;
        if (_loadProgressTimer != null) return;

        _loadProgressPhase = 0;
        _loadProgressTimer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        _loadProgressTimer.Tick += (_, _) =>
        {
            if (_loadProgress == null) return;
            _loadProgressPhase += 0.08;
            // Cosine-eased breathing between 0.35 and 1.0 so the strip never
            // disappears (still reads as "loading") but visibly pulses.
            var eased = 0.675 + 0.325 * Math.Cos(_loadProgressPhase);
            _loadProgress.Opacity = eased;
        };
        _loadProgressTimer.Start();
    }

    /// <summary>
    /// Snaps the load strip back to invisible and tears down the pulse timer.
    /// Safe to call when no pulse is in flight.
    /// </summary>
    private void EndLoadProgress()
    {
        _loadProgressTimer?.Stop();
        _loadProgressTimer = null;
        if (_loadProgress != null)
        {
            _loadProgress.Opacity = 0;
            _loadProgress.IsVisible = false;
        }
    }

    /// <summary>
    /// Updates the leading site-state icon inside the address bar pill based
    /// on <paramref name="url"/>'s scheme. HTTPS gets a closed-padlock; plain
    /// HTTP gets an unlocked padlock tinted by the warning accent so the user
    /// notices an insecure page; <c>dosi://</c> internal pages get a four-
    /// pointed sparkle in the accent color so the home page / settings page
    /// feel native rather than borrowed.
    /// </summary>
    private void ApplyAddressBarSiteIcon(string url)
    {
        if (_addressBarSiteIcon == null) return;

        // Filled-shape geometries (Stretch.Uniform sizes them into the 14x14
        // host) - kept simple so they read instantly at icon size.
        const string lockGeometry =
            "M 5,8 L 5,5 A 3,3 0 0 1 11,5 L 11,8 L 4,8 L 4,15 L 12,15 L 12,8 Z " +
            "M 6.5,5 A 1.5,1.5 0 0 1 9.5,5 L 9.5,8 L 6.5,8 Z";
        const string sparkleGeometry =
            "M 8,1 L 9.6,6.4 L 15,8 L 9.6,9.6 L 8,15 L 6.4,9.6 L 1,8 L 6.4,6.4 Z";
        const string globeGeometry =
            "M 8,1 A 7,7 0 1 1 7.999,1 Z " +
            "M 1,8 L 15,8 M 8,1 C 4,4 4,12 8,15 M 8,1 C 12,4 12,12 8,15";

        IBrush fill;
        string data;
        if (url.StartsWith("dosi://", StringComparison.OrdinalIgnoreCase))
        {
            data = sparkleGeometry;
            fill = Accents.AccentPrimaryBrush;
        }
        else if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            data = lockGeometry;
            fill = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0x6E)); // calm green
        }
        else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            data = globeGeometry;
            fill = new SolidColorBrush(Color.FromRgb(0xE8, 0xA1, 0x4A)); // warning amber
        }
        else
        {
            data = globeGeometry;
            fill = Accents.TextSecondaryBrush;
        }

        _addressBarSiteIcon.Data = Geometry.Parse(data);
        _addressBarSiteIcon.Fill = fill;
    }

    private static Border CreateNavButton(Control glyph, string tooltip)
    {
        // Soft drop shadow on every glyph so the icons gently lift off the
        // toolbar surface. Applied here (instead of per-builder) so all five
        // nav buttons get the exact same shadow recipe automatically.
        glyph.Effect = new Avalonia.Media.DropShadowEffect
        {
            BlurRadius = 4,
            OffsetX = 0,
            OffsetY = 1,
            Color = Color.FromArgb(110, 0, 0, 0),
            Opacity = 1
        };

        var button = new Border
        {
            Width = 32,
            Height = 32,
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(16),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = glyph,
            // Store enabled state in Tag (default true)
            Tag = true,
            [ToolTip.TipProperty] = tooltip
        };

        button.PointerEntered += (s, e) =>
        {
            if (button.Tag is true)
                button.Background = Accents.ButtonBackgroundHoverBrush;
        };

        button.PointerExited += (s, e) =>
        {
            button.Background = Brushes.Transparent;
        };

        button.PointerPressed += (s, e) =>
        {
            if (button.Tag is true)
                button.Background = Accents.ButtonBackgroundPressedBrush;
        };

        button.PointerReleased += (s, e) =>
        {
            if (button.Tag is true)
                button.Background = button.IsPointerOver
                    ? Accents.ButtonBackgroundHoverBrush
                    : Brushes.Transparent;
        };

        return button;
    }

    private static Control CreateBrowserIcon()
    {
        var border = new Border
        {
            Width = 16,
            Height = 16,
            Background = Accents.AccentGradientBrush,
            CornerRadius = new CornerRadius(8)
        };

        var globe = new Ellipse
        {
            Width = 10,
            Height = 10,
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 1.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var grid = new Grid();
        grid.Children.Add(globe);
        border.Child = grid;

        return border;
    }

    // =====================================================================
    // Tabbed browsing
    // =====================================================================

    /// <summary>
    /// Builds the small "+" affordance at the right end of the tab strip
    /// that opens a fresh tab pointing at the home page.
    /// </summary>
    private Border BuildNewTabButton()
    {
        var plus = new TextBlock
        {
            Text = "+",
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            Foreground = Accents.TextPrimaryBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var btn = new Border
        {
            Width = 26,
            Height = 26,
            Background = Brushes.Transparent,
            // Fully circular hover so it visually echoes the round nav buttons
            // in the toolbar instead of looking like a tiny tab.
            CornerRadius = new CornerRadius(13),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = plus,
            [ToolTip.TipProperty] = "New tab"
        };

        btn.PointerEntered += (_, _) => btn.Background = Accents.ButtonBackgroundHoverBrush;
        btn.PointerExited += (_, _) => btn.Background = Brushes.Transparent;
        btn.PointerReleased += (_, _) => OpenNewTab("dosi://home", activate: true);

        return btn;
    }

    /// <summary>
    /// Creates a new browser tab, optionally navigating it to <paramref name="url"/>.
    /// When <paramref name="activate"/> is true the newly-opened tab becomes
    /// the foreground tab; otherwise it lives in the background until the
    /// user clicks its header (used by "Open Link in New Tab").
    /// </summary>
    public void OpenNewTab(string? url, bool activate)
    {
        var tab = new BrowserTab
        {
            CurrentUrl = url ?? "dosi://home",
            TitleText = GetDisplayNameFromUrl(url ?? "dosi://home")
        };
        BuildTabHeader(tab);
        _tabs.Add(tab);
        _tabStrip.Children.Add(tab.Header!);
        UpdateTabHeaderVisuals(tab);

        if (activate || _activeTab == null)
        {
            // Make this tab current first (without forcing a render - we are
            // about to NavigateTo which will render exactly once).
            ActivateTab(tab, navigateIfEmpty: false);
            if (!string.IsNullOrEmpty(url))
                NavigateTo(url);
        }
        else
        {
            // Background tab: don't navigate the WebView yet, but kick off
            // the favicon fetch + display-name resolution so the user sees a
            // proper tab label / icon while it sits idle in the strip.
            tab.PageContent = null;
            if (!string.IsNullOrEmpty(url))
                LoadFaviconAsync(tab, url);
        }
    }

    /// <summary>
    /// Switches the foreground tab. Saves the outgoing tab's mutable state,
    /// detaches its content from the host, then loads the incoming tab's
    /// state into the singular fields the rest of the browser reads.
    /// </summary>
    private void ActivateTab(BrowserTab tab, bool navigateIfEmpty = false)
    {
        if (ReferenceEquals(_activeTab, tab)) { UpdateAllTabHeaderVisuals(); return; }

        _isSwitchingTabs = true;
        try
        {
            // Persist current tab.
            SyncActiveTabState();

            _activeTab = tab;

            // Hydrate fields from the new active tab.
            _currentUrl = tab.CurrentUrl;
            _isExternalPage = tab.IsExternalPage;
            _webView = tab.WebView;
            _history.Clear();
            _history.AddRange(tab.History);
            _historyIndex = tab.HistoryIndex;
            _addressBar.Text = tab.CurrentUrl;
            Title = (string.IsNullOrEmpty(tab.TitleText) ? GetPageTitle(tab.CurrentUrl) : tab.TitleText) + " - DOSI Browser";
            UpdateNavigationButtons();

            if (tab.PageContent != null)
            {
                ShowTabContent(tab.PageContent);
            }
            else if (navigateIfEmpty || _history.Count == 0)
            {
                // Fresh tab without rendered content yet - render its page.
                RenderPage(tab.CurrentUrl);
            }

            UpdateAllTabHeaderVisuals();
        }
        finally
        {
            _isSwitchingTabs = false;
        }
    }

    /// <summary>
    /// Closes <paramref name="tab"/>, disposing its WebView. If the closed
    /// tab was active, focus moves to the nearest neighbor; closing the very
    /// last tab opens a fresh home tab so the browser is never blank.
    /// </summary>
    private void CloseTab(BrowserTab tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0) return;

        // Detach content if this tab is active so the dispose below doesn't
        // free a control that's still parented to the content area.
        if (ReferenceEquals(_activeTab, tab))
        {
            RemoveTabContent(tab.PageContent);
            _webView = null;
        }
        else
        {
            // Background tab: also remove its content so we don't keep a
            // disposed WebView wired into the host's visual tree.
            RemoveTabContent(tab.PageContent);
        }

        try { tab.WebView?.Dispose(); } catch { }
        tab.WebView = null;
        tab.PageContent = null;

        if (tab.Header != null)
            _tabStrip.Children.Remove(tab.Header);
        _tabs.RemoveAt(index);

        if (_tabs.Count == 0)
        {
            // Never let the window be tab-less - open a fresh home tab.
            _activeTab = null;
            OpenNewTab("dosi://home", activate: true);
            return;
        }

        if (ReferenceEquals(_activeTab, tab))
        {
            var next = _tabs[Math.Min(index, _tabs.Count - 1)];
            _activeTab = null; // force ActivateTab to swap
            ActivateTab(next);
        }
        else
        {
            UpdateAllTabHeaderVisuals();
        }
    }

    /// <summary>
    /// Mirrors the singular fields back into <see cref="_activeTab"/> so a
    /// subsequent tab switch can restore them verbatim.
    /// </summary>
    private void SyncActiveTabState()
    {
        if (_activeTab == null || _isSwitchingTabs) return;
        _activeTab.CurrentUrl = _currentUrl;
        _activeTab.IsExternalPage = _isExternalPage;
        _activeTab.WebView = _webView;
        _activeTab.History.Clear();
        _activeTab.History.AddRange(_history);
        _activeTab.HistoryIndex = _historyIndex;
        // PageContent is updated where it's actually assigned (RenderPage /
        // CreateWebViewPage) so this method stays cheap and idempotent.
    }

    /// <summary>
    /// Builds the per-tab header (title + close button) and wires its click
    /// handlers. The header is what the user actually clicks in the tab
    /// strip to switch between tabs.
    /// </summary>
    private void BuildTabHeader(BrowserTab tab)
    {
        var iconImage = new Image
        {
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            IsVisible = false
        };

        var titleText = new TextBlock
        {
            Text = tab.TitleText,
            FontSize = 12,
            Foreground = Accents.TextPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 140
        };

        var closeGlyph = new TextBlock
        {
            Text = "\u00D7", // multiplication sign - reads as a clean "x"
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            Foreground = Accents.TextSecondaryBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var closeBtn = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = closeGlyph,
            Margin = new Thickness(8, 0, 0, 0),
            // Hidden by default; revealed on tab hover or when the tab is
            // active. Reduces visual noise across many tabs and matches the
            // behaviour of every modern browser.
            Opacity = 0,
            IsHitTestVisible = false,
            [ToolTip.TipProperty] = "Close tab"
        };
        closeBtn.PointerEntered += (_, _) => closeBtn.Background = new SolidColorBrush(Accents.CloseButtonHover);
        closeBtn.PointerExited += (_, _) => closeBtn.Background = Brushes.Transparent;
        closeBtn.PointerReleased += (_, e) =>
        {
            e.Handled = true;
            CloseTab(tab);
        };

        // Thin accent strip drawn along the top edge of the active tab. Hidden
        // for inactive tabs; UpdateTabHeaderVisuals flips its opacity. Gives
        // the active tab a clear visual anchor (Edge / Chrome convention)
        // beyond the subtle background swap.
        var activeIndicator = new Border
        {
            Height = 2,
            Background = Accents.AccentPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(6, 0, 6, 0),
            CornerRadius = new CornerRadius(0, 0, 1, 1),
            Opacity = 0,
            IsHitTestVisible = false
        };

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { iconImage, titleText, closeBtn }
        };

        // Stack the indicator over the row contents using a Grid - the indicator
        // sits at the top edge regardless of how the row's contents shift.
        var headerRoot = new Grid();
        headerRoot.Children.Add(content);
        headerRoot.Children.Add(activeIndicator);

        var header = new Border
        {
            Padding = new Thickness(12, 4),
            CornerRadius = new CornerRadius(8, 8, 0, 0),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = headerRoot
        };
        header.PointerEntered += (_, _) =>
        {
            if (!ReferenceEquals(_activeTab, tab))
                header.Background = Accents.ButtonBackgroundHoverBrush;
            // Reveal close button on hover.
            closeBtn.Opacity = 1;
            closeBtn.IsHitTestVisible = true;
        };
        header.PointerExited += (_, _) =>
        {
            if (!ReferenceEquals(_activeTab, tab))
                header.Background = Brushes.Transparent;
            // Keep the close button visible while the tab is active so the
            // user can dismiss the foreground tab without first hovering it.
            if (!ReferenceEquals(_activeTab, tab))
            {
                closeBtn.Opacity = 0;
                closeBtn.IsHitTestVisible = false;
            }
        };
        header.PointerReleased += (_, e) =>
        {
            // Middle-click closes the tab (standard browser convention).
            // PointerReleased's InitialPressMouseButton tells us which button
            // started the gesture - using e.GetCurrentPoint here would always
            // report Released for every button.
            if (e.InitialPressMouseButton == MouseButton.Middle)
            {
                e.Handled = true;
                CloseTab(tab);
                return;
            }
            ActivateTab(tab);
        };

        tab.Header = header;
        tab.HeaderText = titleText;
        tab.HeaderIcon = iconImage;
        tab.HeaderCloseButton = closeBtn;
        tab.HeaderActiveIndicator = activeIndicator;
    }

    /// <summary>
    /// Refreshes the title text + selection-fill of a single tab header to
    /// reflect the tab's current state.
    /// </summary>
    private void UpdateTabHeaderVisuals(BrowserTab? tab)
    {
        if (tab?.Header == null || tab.HeaderText == null) return;

        var label = string.IsNullOrEmpty(tab.TitleText)
            ? GetPageTitle(tab.CurrentUrl)
            : tab.TitleText;
        tab.HeaderText.Text = label;

        var isActive = ReferenceEquals(_activeTab, tab);

        tab.Header.Background = isActive
            ? Accents.ControlBackgroundBrush
            : Brushes.Transparent;

        // Bold the active tab title slightly so the foreground tab also reads
        // by weight, not just by background fill - helps users with low
        // contrast settings track which tab is current.
        tab.HeaderText.FontWeight = isActive ? FontWeight.SemiBold : FontWeight.Normal;

        // Lift the active tab off the strip with a soft drop shadow so it
        // visually "comes forward" the way Edge / Chrome / VS Code do, then
        // strip it from inactive tabs to keep the row flat.
        tab.Header.BoxShadow = isActive
            ? new BoxShadows(new BoxShadow
            {
                OffsetX = 0,
                OffsetY = 1,
                Blur = 6,
                Spread = 0,
                Color = Color.FromArgb(55, 0, 0, 0)
            })
            : default;

        // Active indicator strip: visible only on the foreground tab.
        if (tab.HeaderActiveIndicator != null)
            tab.HeaderActiveIndicator.Opacity = isActive ? 1 : 0;

        // Close button persists for the active tab so it can always be
        // dismissed without first hovering; inactive tabs only reveal it on
        // hover (handled by the header's pointer events).
        if (tab.HeaderCloseButton != null)
        {
            if (isActive)
            {
                tab.HeaderCloseButton.Opacity = 1;
                tab.HeaderCloseButton.IsHitTestVisible = true;
            }
            else if (!tab.Header.IsPointerOver)
            {
                tab.HeaderCloseButton.Opacity = 0;
                tab.HeaderCloseButton.IsHitTestVisible = false;
            }
        }
    }

    /// <summary>Refreshes every tab header (used after activation / close).</summary>
    private void UpdateAllTabHeaderVisuals()
    {
        foreach (var tab in _tabs)
            UpdateTabHeaderVisuals(tab);
    }

    /// <summary>Disposes the native WebView for every tab (close / shutdown).</summary>
    private void DisposeAllTabWebViews()
    {
        foreach (var tab in _tabs)
        {
            try { tab.WebView?.Dispose(); } catch { }
            tab.WebView = null;
            tab.PageContent = null;
        }
    }

    /// <summary>
    /// Best-effort "official" name for a URL used as the initial tab label
    /// before the WebView reports the page's real <c>&lt;title&gt;</c>. For
    /// internal pages we reuse <see cref="GetPageTitle"/>; for external pages
    /// we strip the host down to its primary label and capitalize it (so
    /// <c>https://www.youtube.com/watch?v=...</c> becomes <c>Youtube</c>).
    /// Known brand names are spelled the way the brand actually writes them.
    /// </summary>
    private string GetDisplayNameFromUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "New Tab";
        if (url.StartsWith("dosi://", StringComparison.OrdinalIgnoreCase)) return GetPageTitle(url);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return GetPageTitle(url);

        var host = uri.Host;
        if (string.IsNullOrEmpty(host)) return GetPageTitle(url);
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            host = host.Substring(4);

        // Use the leftmost label of the host as the brand stem so subdomains
        // (mail.google.com) collapse to the recognizable name ("Google").
        var label = host.Split('.')[0];
        if (string.IsNullOrEmpty(label)) return host;

        return label.ToLowerInvariant() switch
        {
            "google"   => "Google",
            "youtube"  => "YouTube",
            "github"   => "GitHub",
            "reddit"   => "Reddit",
            "twitter"  => "Twitter",
            "x"        => "X",
            "facebook" => "Facebook",
            "stackoverflow" => "Stack Overflow",
            "microsoft"     => "Microsoft",
            "wikipedia"     => "Wikipedia",
            _ => char.ToUpperInvariant(label[0]) + label.Substring(1)
        };
    }

    /// <summary>
    /// Asynchronously fetches and applies a favicon for <paramref name="tab"/>.
    /// Uses Google's S2 favicons service so a single request resolves cleanly
    /// for any host (the canonical <c>/favicon.ico</c> on the origin often
    /// 404s or returns CORS-blocked responses on modern sites). All failures
    /// are silent - a missing favicon should never disrupt browsing.
    /// </summary>
    private async void LoadFaviconAsync(BrowserTab tab, string url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;

            // Coalesce: skip the network round-trip if we've already resolved
            // this exact host's favicon for this tab.
            if (string.Equals(tab.FaviconForUrl, uri.Host, StringComparison.OrdinalIgnoreCase))
                return;
            tab.FaviconForUrl = uri.Host;

            var faviconUrl = $"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(uri.Host)}&sz=32";
            var bytes = await _faviconHttp.GetByteArrayAsync(faviconUrl).ConfigureAwait(false);
            if (bytes == null || bytes.Length == 0) return;

            using var ms = new MemoryStream(bytes);
            var bitmap = new Bitmap(ms);

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (tab.HeaderIcon == null)
                {
                    bitmap.Dispose();
                    return;
                }
                // Only apply if the tab is still pointed at the same host - the
                // user may have navigated away while the request was in flight.
                if (!Uri.TryCreate(tab.CurrentUrl, UriKind.Absolute, out var stillAt) ||
                    !string.Equals(stillAt.Host, uri.Host, StringComparison.OrdinalIgnoreCase))
                {
                    bitmap.Dispose();
                    return;
                }
                // Dispose the previous favicon (if any) before swapping in the new
                // one - otherwise host changes leak one decoded bitmap per swap.
                (tab.HeaderIcon.Source as IDisposable)?.Dispose();
                tab.HeaderIcon.Source = bitmap;
                tab.HeaderIcon.IsVisible = true;
            });
        }
        catch
        {
            // Favicons are best-effort - any failure (DNS, timeout, decode)
            // just leaves the tab without an icon, which is fine.
        }
    }
}
