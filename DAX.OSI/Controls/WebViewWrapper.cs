using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DOSI.CORE.AccentManagement;
using DOSI.CORE.UIComponents;

namespace DAX.OSI.Controls;

/// <summary>
/// Payload for <see cref="WebViewWrapper.ContextMenuRequested"/>. Carries the
/// information the right-click JS bridge captured inside the page so the host
/// can build a context-aware DOSI menu (link / selection / page actions).
/// </summary>
public sealed class WebViewContextMenuRequestedEventArgs : EventArgs
{
    /// <summary>X coordinate of the click in WebView-local (CSS) pixels.</summary>
    public double X { get; init; }
    /// <summary>Y coordinate of the click in WebView-local (CSS) pixels.</summary>
    public double Y { get; init; }
    /// <summary>Resolved href of the closest anchor under the cursor, or null.</summary>
    public string? LinkUrl { get; init; }
    /// <summary>Image src under the cursor, or null when not over an image.</summary>
    public string? ImageUrl { get; init; }
    /// <summary>Currently selected text inside the page, or empty.</summary>
    public string? SelectedText { get; init; }
}

/// <summary>
/// Payload for <see cref="WebViewWrapper.DownloadRequested"/>. Carries the
/// information the page's JS bridge captured at the click site so the host
/// can show a custom DOSI download flyout, route the bytes to the user's
/// Downloads folder, and skip the renderer's own (un-themed) save dialog.
/// </summary>
public sealed class WebViewDownloadRequestedEventArgs : EventArgs
{
    /// <summary>Absolute URL of the resource the user asked to download.</summary>
    public required string Url { get; init; }
    /// <summary>Filename suggested by the page (<c>&lt;a download="..."&gt;</c>
    /// attribute, the URL's last path segment, or a generated fallback).</summary>
    public required string SuggestedFileName { get; init; }
    /// <summary>Page URL that initiated the download - used as the HTTP
    /// <c>Referer</c> header so sites that require a same-origin referrer
    /// (Steam, CDNs, attachment endpoints) still serve the file.</summary>
    public string? Referer { get; init; }
}

/// <summary>
/// Why the WebView is currently hidden behind the placeholder card. Drives
/// the chip color, headline, and copy so an inactive / dragging window reads
/// as a friendly status message instead of a hard error page.
/// </summary>
public enum WebViewOverlayKind
{
    /// <summary>Window is open but doesn't have focus right now.</summary>
    Inactive,
    /// <summary>User is dragging the window by its chrome.</summary>
    Dragging,
    /// <summary>A DOSI popup / menu is open above the windows. The native
    /// renderer surface is hidden so the popup can render without being
    /// occluded by the OS-level WebView surface ("airspace" problem).</summary>
    MenuOpen,
    /// <summary>A real navigation / platform error - shows the red chip.</summary>
    Error
}

/// <summary>
/// WebView wrapper with placeholder support for window focus handling.
///
/// Cross-platform note: <see cref="NativeWebView"/> is provided by the
/// Avalonia.Controls.WebView package, which targets WebView2 on Windows,
/// WKWebView on macOS, and WebKitGTK on Linux. We attempt construction on
/// every platform and only fall back to the styled placeholder card if the
/// runtime can't load the native renderer (e.g. missing WebKitGTK on a bare
/// Linux distro). All navigation APIs become safe no-ops in that fallback
/// mode so the rest of DAX.OSI keeps running.
/// </summary>
public class WebViewWrapper : UserControl, IDisposable
{
    /// <summary>
    /// True when the host successfully loaded the native WebView renderer.
    /// Set to false if construction throws (e.g. missing WebKitGTK on Linux
    /// or an unexpected platform host). Inspect <see cref="PlatformLoadError"/>
    /// for the underlying exception message.
    /// </summary>
    public static bool IsWebViewSupported { get; private set; } = true;

    /// <summary>Last error captured when constructing <see cref="NativeWebView"/>.</summary>
    public static string? PlatformLoadError { get; private set; }

    // ---- Global pause registry ---------------------------------------------
    //
    // The native renderer surface (WebView2 HWND on Windows, WKWebView on
    // macOS, GtkWidget on Linux) is composited by the OS *above* every
    // Avalonia-drawn pixel - the classic "airspace" problem. That means any
    // popup / menu / context menu the host paints with Avalonia will be
    // occluded by an open WebView, no matter how high its Z-index is.
    //
    // We solve it by exposing a static SetAllPaused toggle: when something
    // like the Applications menu opens, the host calls SetAllPaused(true),
    // every live wrapper hides its native surface and shows the polished
    // "paused" card in its place, and the popup renders unobstructed.
    // SetAllPaused(false) restores the prior visibility per wrapper.
    private static readonly System.Collections.Generic.HashSet<WebViewWrapper> _liveInstances = new();
    private static bool _globalPaused;

    /// <summary>Whether <see cref="SetAllPaused"/> currently has every live
    /// WebView hidden. Useful for the host to query before opening a popup.</summary>
    public static bool IsGloballyPaused => _globalPaused;

    /// <summary>
    /// Hide / restore every live <see cref="WebViewWrapper"/>'s native
    /// surface. Call <c>SetAllPaused(true)</c> right before opening a popup
    /// menu / context menu / overlay that needs to render above WebView
    /// content and <c>SetAllPaused(false)</c> when it closes. Idempotent and
    /// safe to call when no wrappers exist.
    /// </summary>
    public static void SetAllPaused(bool paused)
    {
        if (_globalPaused == paused) return;
        _globalPaused = paused;
        // Snapshot - per-wrapper apply may detach itself during the call (e.g.
        // window closing inside a focus handler reentry).
        WebViewWrapper[] snapshot;
        lock (_liveInstances)
        {
            snapshot = new WebViewWrapper[_liveInstances.Count];
            _liveInstances.CopyTo(snapshot);
        }
        foreach (var w in snapshot) w.ApplyGlobalPause(paused);
    }

    /// <summary>True while this specific wrapper has its native surface
    /// hidden as part of <see cref="SetAllPaused"/>. Tracked so the
    /// per-window <see cref="SetVisible(bool, WebViewOverlayKind)"/> calls
    /// driven by focus / drag don't fight the global pause.</summary>
    private bool _pausedByGlobal;
    /// <summary>The visibility we should restore the native surface to when
    /// the global pause is released. Captured at the moment the pause begins
    /// so a window that was already inactive stays inactive afterwards.</summary>
    private bool _restoreVisibleAfterGlobalPause = true;
    /// <summary>The overlay copy we should restore when the pause ends.</summary>
    private WebViewOverlayKind _restoreOverlayKindAfterGlobalPause = WebViewOverlayKind.Inactive;

    private readonly NativeWebView? _webView;
    private readonly Grid _container;
    private readonly DOSIScrollBar _vScrollBar;
    private readonly DOSIScrollBar _hScrollBar;
    /// <summary>
    /// True while we are pushing scroll state received from the page back
    /// onto the DOSIScrollBar Value properties. Suppresses the
    /// scrollbar->page echo so we don't fight the user's wheel/keyboard.
    /// </summary>
    private bool _isUpdatingScrollFromPage;
    private readonly Border _placeholder;
    private readonly Border _placeholderCard;
    private readonly TextBlock _placeholderIcon;
    private readonly TextBlock _placeholderTitle;
    private readonly TextBlock _placeholderUrl;
    private readonly TextBlock _placeholderHint;
    private readonly Border _errorCodeChip;
    private readonly TextBlock _errorCodeText;
    private readonly StackPanel _suggestionsList;
    private string _currentUrl = "";
    private string _currentTitle = "";
    private WebViewOverlayKind _currentOverlayKind = WebViewOverlayKind.Inactive;

    private static AccentManager Accents => AccentManager.Instance;

    public event EventHandler<string>? NavigationStarting;
    public event EventHandler<string>? NavigationCompleted;
    public event EventHandler<string>? TitleChanged;
    public event EventHandler<string>? NewWindowRequested;
    /// <summary>
    /// Raised when the user right-clicks inside the page. The native context
    /// menu has already been suppressed by an injected script bridge, so the
    /// host is responsible for showing its own menu (typically a DOSIContextMenu).
    /// Cross-platform: works wherever the wrapped renderer (WebView2, WKWebView,
    /// WebKitGTK) supports a host-page postMessage channel.
    /// </summary>
    public event EventHandler<WebViewContextMenuRequestedEventArgs>? ContextMenuRequested;

    /// <summary>
    /// Raised when the user clicks something the in-page JS bridge classifies
    /// as a download (an <c>&lt;a download&gt;</c> anchor, a link to a known
    /// downloadable file extension, etc). The wrapper has already cancelled
    /// the renderer's own download flow, so the host owns the entire UX:
    /// show a popup, fetch the bytes, write them to the user's Downloads
    /// folder, and surface progress / completion. No native save-as dialog
    /// ever appears.
    /// </summary>
    public event EventHandler<WebViewDownloadRequestedEventArgs>? DownloadRequested;

    /// <summary>
    /// Raised when the page (or host F11 logic) requests a fullscreen state
    /// change. <c>true</c> means a page element has entered fullscreen via
    /// the JS Fullscreen API (e.g. YouTube's player); <c>false</c> means the
    /// page (or user) has exited it. Hosts typically respond by hiding their
    /// own chrome and maximising the OS window so the WebView fills the
    /// screen.
    /// </summary>
    public event EventHandler<bool>? FullScreenChangeRequested;

    /// <summary>
    /// Page zoom (percent, 100 = native size) applied to every page that
    /// loads in this wrapper. Setting the property re-applies immediately so
    /// the user sees the change without needing to reload. Implemented as
    /// CSS <c>zoom</c> on <c>documentElement</c> because it's the only
    /// approach supported uniformly by Chromium (WebView2 / WebKitGTK) and
    /// WKWebView, and it survives SPA route changes since we re-inject on
    /// every NavigationCompleted.
    /// </summary>
    private int _zoomPercent = 100;
    public int ZoomPercent
    {
        get => _zoomPercent;
        set
        {
            var clamped = Math.Clamp(value, 25, 500);
            if (_zoomPercent == clamped) return;
            _zoomPercent = clamped;
            _ = TryApplyZoomAsync();
        }
    }

    public WebViewWrapper()
    {
        // Attempt to spin up the native WebView on EVERY platform. The
        // Avalonia.Controls.WebView package handles WebView2 (Windows),
        // WKWebView (macOS), and WebKitGTK (Linux). If the runtime can't
        // load the native bits (e.g. WebKitGTK isn't installed on this
        // Linux host), we capture the exception, leave _webView null, and
        // let the placeholder card explain the situation.
        try
        {
            _webView = new NativeWebView();
            _webView.NavigationStarted += OnNavigationStarted;
            _webView.NavigationCompleted += OnNavigationCompleted;
            _webView.PropertyChanged += OnPropertyChanged;
            _webView.NewWindowRequested += OnNewWindowRequested;

            // Wire up the JS->host postMessage bridge. We use this both to
            // intercept right-clicks (so the native context menu can be
            // replaced with our DOSIContextMenu) and to keep the door open
            // for future page<->host integrations. Wrapped in a try block
            // because the property/event names are wrapper-specific and we
            // don't want a missing API on a future package version to take
            // the whole browser offline.
            TryEnablePostMessageBridge();

            IsWebViewSupported = true;
            PlatformLoadError = null;
        }
        catch (Exception ex)
        {
            _webView = null;
            IsWebViewSupported = false;
            PlatformLoadError = ex.Message;
        }

        // ---- Polished "can't load this page" card --------------------------
        // Modeled after Chrome / Edge's error pages: large glyph, headline,
        // requested URL, error code chip, and a short list of actionable
        // suggestions. Used for both the unsupported-platform case and any
        // navigation failure surfaced via ShowError(...).
        _placeholderIcon = new TextBlock
        {
            // U+1F310 (🌐) globe - 8-digit Unicode escape since it's outside the BMP.
            Text = "\U0001F310",
            FontSize = 56,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8)
        };

        _placeholderTitle = new TextBlock
        {
            Text = IsWebViewSupported
                ? "This page can\u2019t be displayed"
                : "DOSI Browser isn\u2019t available on this platform",
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextPrimaryBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 480
        };

        _placeholderUrl = new TextBlock
        {
            FontSize = 12,
            Foreground = Accents.TextSecondaryBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 520,
            Margin = new Thickness(0, 6, 0, 0),
            Opacity = 0.9
        };

        _errorCodeText = new TextBlock
        {
            Text = IsWebViewSupported ? "PAUSED" : "UNAVAILABLE",
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Accents.TextOnAccent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _errorCodeChip = new Border
        {
            Background = BuildStatusChipBrush(WebViewOverlayKind.Inactive),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 3),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 14),
            Child = _errorCodeText
        };

        _placeholderHint = new TextBlock
        {
            Text = IsWebViewSupported
                ? "Click anywhere on the browser to bring it back into focus and resume the page."
                : BuildUnsupportedPlatformHint(),
            FontSize = 13,
            Foreground = Accents.TextSecondaryBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 480,
            Opacity = 0.9
        };

        _suggestionsList = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            Margin = new Thickness(0, 16, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MaxWidth = 460
        };
        FillDefaultSuggestions();

        var divider = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 18, 0, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MaxWidth = 460,
            Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128))
        };

        var cardContent = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                _placeholderIcon,
                _placeholderTitle,
                _placeholderUrl,
                _errorCodeChip,
                _placeholderHint,
                divider,
                _suggestionsList
            }
        };

        _placeholderCard = new Border
        {
            Background = Accents.WindowChromeBrush,
            BorderBrush = new SolidColorBrush(Color.FromArgb(50, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(36, 32),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 620,
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0,
                OffsetY = 12,
                Blur = 32,
                Spread = 0,
                Color = Color.FromArgb(60, 0, 0, 0)
            }),
            Child = cardContent
        };

        _placeholder = new Border
        {
            Background = Accents.WindowContentBrush,
            Padding = new Thickness(24),
            Child = _placeholderCard,
            // Keep the placeholder up permanently when the native view is
            // unavailable; otherwise it starts hidden and only shows during
            // the focus-handling defocus state.
            IsVisible = !IsWebViewSupported
        };

        // Two-column / two-row grid: the WebView occupies cell (0,0) and the
        // DOSIScrollBars sit in the right column / bottom row. We DON'T
        // overlay the scrollbars on top of the WebView because the native
        // renderer (WebView2 HWND on Windows, WKWebView on macOS, GtkWidget
        // on Linux) is hosted as a true OS-level surface and would always
        // paint over Avalonia content (the classic "airspace" problem).
        // Reserving a thin column/row instead works on every platform.
        _container = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            RowDefinitions = new RowDefinitions("*,Auto")
        };

        _vScrollBar = new DOSIScrollBar
        {
            Orientation = Orientation.Vertical,
            ShowButtons = false,
            IsVisible = false
        };
        Grid.SetColumn(_vScrollBar, 1);
        Grid.SetRow(_vScrollBar, 0);
        _vScrollBar.Scroll += OnVerticalScrollBarChanged;

        _hScrollBar = new DOSIScrollBar
        {
            Orientation = Orientation.Horizontal,
            ShowButtons = false,
            IsVisible = false
        };
        Grid.SetColumn(_hScrollBar, 0);
        Grid.SetRow(_hScrollBar, 1);
        _hScrollBar.Scroll += OnHorizontalScrollBarChanged;

        if (_webView != null)
        {
            Grid.SetColumn(_webView, 0);
            Grid.SetRow(_webView, 0);
            _container.Children.Add(_webView);
        }
        _container.Children.Add(_vScrollBar);
        _container.Children.Add(_hScrollBar);

        // Placeholder card spans the whole grid so it covers both the
        // WebView and the reserved scrollbar gutters when shown.
        Grid.SetColumn(_placeholder, 0);
        Grid.SetRow(_placeholder, 0);
        Grid.SetColumnSpan(_placeholder, 2);
        Grid.SetRowSpan(_placeholder, 2);
        _container.Children.Add(_placeholder);
        Content = _container;

        // Subscribe to accent changes
        AttachedToVisualTree += (s, e) =>
        {
            Accents.AccentChanged += OnAccentChanged;
            lock (_liveInstances) _liveInstances.Add(this);
            // Newly-attached wrappers should obey an already-active global
            // pause - otherwise opening the apps menu, then opening a new
            // browser, would leave that browser visible and re-cover the menu.
            if (_globalPaused) ApplyGlobalPause(true);
        };
        DetachedFromVisualTree += (s, e) =>
        {
            Accents.AccentChanged -= OnAccentChanged;
            lock (_liveInstances) _liveInstances.Remove(this);
        };
    }

    private void OnAccentChanged(object? sender, EventArgs e)
    {
        _placeholder.Background = Accents.WindowContentBrush;
        _placeholderCard.Background = Accents.WindowChromeBrush;
        _placeholderTitle.Foreground = Accents.TextPrimaryBrush;
        _placeholderUrl.Foreground = Accents.TextSecondaryBrush;
        _placeholderHint.Foreground = Accents.TextSecondaryBrush;
        _errorCodeChip.Background = BuildStatusChipBrush(_currentOverlayKind);
        _errorCodeText.Foreground = new SolidColorBrush(Accents.TextOnAccent);
        foreach (var child in _suggestionsList.Children)
        {
            if (child is Grid g && g.Children.Count >= 2 && g.Children[1] is TextBlock t)
                t.Foreground = Accents.TextSecondaryBrush;
        }
    }

    /// <summary>
    /// Seeds the suggestions list with the defaults for the current scenario
    /// (platform-unsupported when WebView2 isn't available, generic browser
    /// guidance otherwise). Callers can replace it via <see cref="ShowError"/>.
    /// </summary>
    private void FillDefaultSuggestions()
    {
        _suggestionsList.Children.Clear();
        if (!IsWebViewSupported)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                AddSuggestion("Install WebKitGTK so DOSIWebBrowser can render real web pages: 'sudo apt install libwebkit2gtk-4.1-0' (Debian/Ubuntu) or your distro's equivalent.");
                AddSuggestion("Restart DAX.OSI after installing - the native renderer is loaded once at startup.");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                AddSuggestion("macOS ships WKWebView with the system, so this usually means the app entitlements need refreshing - rebuild with the latest project file.");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                AddSuggestion("Install the Microsoft Edge WebView2 Runtime (free) from https://developer.microsoft.com/microsoft-edge/webview2/.");
            }
            AddSuggestion("Use the rest of DOSI normally - Files, Code, Settings, and Terminal don\u2019t depend on the web renderer.");
            AddSuggestion("Open external URLs in your host operating system\u2019s native browser instead.");
            return;
        }

        switch (_currentOverlayKind)
        {
            case WebViewOverlayKind.Dragging:
                AddSuggestion("Release the title bar to drop the window. The page will pop right back in.");
                AddSuggestion("This isn\u2019t a load failure - it\u2019s how DOSI keeps the page in lockstep with the window during a move.");
                break;
            case WebViewOverlayKind.MenuOpen:
                AddSuggestion("Pick something from the menu, or click outside it, to dismiss the popup.");
                AddSuggestion("This is a workaround for the OS \u201cairspace\u201d rule - native browser surfaces always paint above app-drawn menus.");
                break;
            case WebViewOverlayKind.Inactive:
            default:
                AddSuggestion("Click anywhere inside the browser window to resume the page.");
                AddSuggestion("Other DOSI apps stay fully interactive in the meantime.");
                break;
        }
    }

    /// <summary>
    /// Builds the platform-specific intro line shown in the placeholder card
    /// when the native renderer failed to load. Mentions which native engine
    /// the host expects and surfaces the captured load error if any.
    /// </summary>
    private static string BuildUnsupportedPlatformHint()
    {
        string engine;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) engine = "Microsoft Edge WebView2";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) engine = "WKWebView";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) engine = "WebKitGTK";
        else engine = "a native web renderer";

        var baseLine =
            $"DOSIWebBrowser couldn\u2019t load {engine} on this host, so it\u2019s running in degraded mode. " +
            "The rest of DAX.OSI stays fully usable.";

        return string.IsNullOrEmpty(PlatformLoadError)
            ? baseLine
            : baseLine + $" (Underlying error: {PlatformLoadError})";
    }

    private void AddSuggestion(string text)
    {
        var bullet = new TextBlock
        {
            Text = "\u2022",
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            Foreground = Accents.AccentPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 8, 0)
        };

        var body = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = Accents.TextSecondaryBrush,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(0, 2, 0, 2)
        };
        row.Children.Add(bullet);  Grid.SetColumn(bullet, 0);
        row.Children.Add(body);    Grid.SetColumn(body, 1);
        _suggestionsList.Children.Add(row);
    }

    /// <summary>
    /// Shows the styled error placeholder over the (possibly missing) WebView
    /// with the given title, error code, and suggestion list. Use this when
    /// a navigation fails (no connection, DNS failure, etc.) so the user sees
    /// a polished page instead of WebView2\u2019s raw chrome error.
    /// </summary>
    public void ShowError(string title, string url, string errorCode, string description, params string[] suggestions)
    {
        _currentOverlayKind = WebViewOverlayKind.Error;
        _placeholderTitle.Text = title;
        _placeholderUrl.Text = url ?? string.Empty;
        _errorCodeText.Text = string.IsNullOrEmpty(errorCode) ? "ERR_UNKNOWN" : errorCode;
        _placeholderHint.Text = description ?? string.Empty;
        _errorCodeChip.Background = BuildStatusChipBrush(WebViewOverlayKind.Error);

        _suggestionsList.Children.Clear();
        if (suggestions != null && suggestions.Length > 0)
        {
            foreach (var s in suggestions)
                if (!string.IsNullOrWhiteSpace(s)) AddSuggestion(s);
        }
        else
        {
            FillDefaultSuggestions();
        }

        _placeholder.IsVisible = true;
        if (_webView != null) _webView.IsVisible = false;
    }

    private void OnNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e)
    {
        // Try to get URL from various possible property names
        string? url = null;
        var eventType = e.GetType();

        foreach (var propName in new[] { "Url", "Uri", "Source", "NavigateUri", "TargetUrl", "NewWindowUri" })
        {
            var prop = eventType.GetProperty(propName);
            if (prop != null)
            {
                var value = prop.GetValue(e);
                url = value?.ToString();
                if (!string.IsNullOrEmpty(url)) break;
            }
        }

        // Only handle if we have a URL and a handler, otherwise let native popup through
        if (!string.IsNullOrEmpty(url) && NewWindowRequested != null)
        {
            e.Handled = true;
            NewWindowRequested.Invoke(this, url);
        }
        // If no handler or no URL, let the native popup appear (e.Handled remains false)
    }

    public void SetVisible(bool visible)
    {
        SetVisible(visible, WebViewOverlayKind.Inactive);
    }

    /// <summary>
    /// Hides / re-shows the native WebView and, when hiding, customises the
    /// placeholder card for the supplied <paramref name="kind"/> so the user
    /// sees a friendly status ("Paused" while unfocused, "Moving" while the
    /// window is being dragged) instead of an error-flavoured chrome.
    /// </summary>
    public void SetVisible(bool visible, WebViewOverlayKind kind)
    {
        // On unsupported platforms the placeholder is the only thing we have,
        // so it stays visible regardless of the focus-handling request.
        if (_webView == null)
        {
            _placeholder.IsVisible = true;
            return;
        }

        // Don't fight the global pause - record what the host *wanted* and
        // apply it once SetAllPaused(false) lifts the pause.
        if (_pausedByGlobal)
        {
            _restoreVisibleAfterGlobalPause = visible;
            _restoreOverlayKindAfterGlobalPause = kind;
            return;
        }

        _webView.IsVisible = visible;
        _placeholder.IsVisible = !visible;

        if (!visible)
        {
            ApplyOverlayKind(kind);
        }
    }

    /// <summary>
    /// Per-instance side of <see cref="SetAllPaused"/>. Snapshots the
    /// current visibility so it can be restored on un-pause and swaps the
    /// native surface for the polished "menu open" placeholder. Wrappers in
    /// an Error state are left alone so the user keeps seeing the failure
    /// page they were already looking at.
    /// </summary>
    private void ApplyGlobalPause(bool paused)
    {
        if (_webView == null) return;                // unsupported platform
        if (_currentOverlayKind == WebViewOverlayKind.Error) return; // don't disturb error UI

        if (paused)
        {
            if (_pausedByGlobal) return;
            _pausedByGlobal = true;
            _restoreVisibleAfterGlobalPause = _webView.IsVisible;
            _restoreOverlayKindAfterGlobalPause = _currentOverlayKind;
            _webView.IsVisible = false;
            _placeholder.IsVisible = true;
            ApplyOverlayKind(WebViewOverlayKind.MenuOpen);
        }
        else
        {
            if (!_pausedByGlobal) return;
            _pausedByGlobal = false;
            _webView.IsVisible = _restoreVisibleAfterGlobalPause;
            _placeholder.IsVisible = !_restoreVisibleAfterGlobalPause;
            if (!_restoreVisibleAfterGlobalPause)
                ApplyOverlayKind(_restoreOverlayKindAfterGlobalPause);
        }
    }

    /// <summary>
    /// Restyles the placeholder card for the given status. The Error kind is
    /// reserved for actual failures (raised via <see cref="ShowError"/>); the
    /// other kinds use a soft accent chip and informational copy so the user
    /// doesn't read a transient pause as a crash.
    /// </summary>
    private void ApplyOverlayKind(WebViewOverlayKind kind)
    {
        _currentOverlayKind = kind;

        // Page identity stays the same regardless of why we're paused.
        _placeholderTitle.Foreground = Accents.TextPrimaryBrush;
        _placeholderUrl.Text = _currentUrl;
        _placeholderUrl.Foreground = Accents.TextSecondaryBrush;
        _placeholder.Background = Accents.WindowContentBrush;

        switch (kind)
        {
            case WebViewOverlayKind.Dragging:
                _placeholderTitle.Text = string.IsNullOrEmpty(_currentTitle)
                    ? "Holding the page while you move the window"
                    : _currentTitle;
                _errorCodeText.Text = "MOVING";
                _placeholderHint.Text =
                    "DOSI keeps the page parked while the window is in motion so it doesn\u2019t trail behind the chrome. Drop the window to continue.";
                break;

            case WebViewOverlayKind.MenuOpen:
                _placeholderTitle.Text = string.IsNullOrEmpty(_currentTitle)
                    ? "Paused while a menu is open"
                    : _currentTitle;
                _errorCodeText.Text = "MENU OPEN";
                _placeholderHint.Text =
                    "DOSI hides the browser surface while a menu or popup is showing so it doesn\u2019t paint over the menu. Close the menu to resume the page.";
                break;

            case WebViewOverlayKind.Inactive:
            default:
                _placeholderTitle.Text = string.IsNullOrEmpty(_currentTitle)
                    ? "Paused while another window is active"
                    : _currentTitle;
                _errorCodeText.Text = "PAUSED";
                _placeholderHint.Text =
                    "Click anywhere on the browser to bring it back into focus and resume the page.";
                break;
        }

        _errorCodeChip.Background = BuildStatusChipBrush(kind);
        FillDefaultSuggestions();
    }

    /// <summary>
    /// Picks the chip background for a given overlay kind. Status kinds get a
    /// soft accent surface so they read as informational; the Error kind keeps
    /// the bold red chip used by real failure pages.
    /// </summary>
    private static IBrush BuildStatusChipBrush(WebViewOverlayKind kind) => kind switch
    {
        WebViewOverlayKind.Error =>
            new SolidColorBrush(Color.FromArgb(235, 200, 60, 70)),
        _ => new SolidColorBrush(Color.FromArgb(
            70,
            Accents.AccentPrimary.R,
            Accents.AccentPrimary.G,
            Accents.AccentPrimary.B))
    };

    private void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        _currentUrl = _webView?.Source?.ToString() ?? _currentUrl;
        NavigationStarting?.Invoke(this, _currentUrl);

        // Eagerly re-inject the context-menu suppression script the moment
        // navigation begins. NavigationCompleted alone leaves a window where
        // the user can right-click on the partially-rendered page (or right
        // after Refresh resets the JS context) and catch the renderer's
        // native menu. Injecting on Started installs the capture-phase
        // contextmenu listener as soon as the new document object exists,
        // which on every navigation after the first one happens before the
        // page paints anything interactive.
        TryInjectContextMenuBridge();
    }

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        _currentUrl = _webView?.Source?.ToString() ?? _currentUrl;
        NavigationCompleted?.Invoke(this, _currentUrl);

        // Re-inject the context-menu suppression / bridge script into every
        // page that finishes loading. Doing it on NavigationCompleted (instead
        // of once at construction) means SPA route changes, iframes that
        // refresh, and back/forward navigations all pick the bridge up
        // automatically without the page-author needing to know it exists.
        TryInjectContextMenuBridge();
    }

    // ---- Cross-platform context-menu replacement bridge --------------------
    //
    // The native renderer (WebView2 on Windows, WKWebView on macOS, WebKitGTK
    // on Linux) ships its own right-click menu that ignores DOSI's chrome
    // entirely. We swap it out by injecting a tiny script that:
    //   1. Cancels the page's `contextmenu` event so the native menu never
    //      gets a chance to render.
    //   2. Posts the click coordinates plus link / selection context back to
    //      the host via whichever postMessage channel the host renderer
    //      provides (chrome.webview, webkit.messageHandlers, or this
    //      wrapper's own bridge function if it injected one).
    // The host then opens a DOSIContextMenu via ContextMenuRequested.
    //
    // Everything is best-effort: if the wrapper version doesn't expose
    // EnablePostMessage / WebMessageReceived / InvokeScriptAsync under those
    // exact names, we degrade to "no native menu, no replacement" rather
    // than crash - the page still works, it just falls back to the original
    // behaviour on right-click.

    private const string ContextMenuBridgeScript = @"
(function() {
    function send(payload) {
        var msg;
        try { msg = JSON.stringify(payload); } catch (e) { return; }
        try { if (window.chrome && window.chrome.webview && window.chrome.webview.postMessage) { window.chrome.webview.postMessage(msg); return; } } catch (e) {}
        try {
            if (window.webkit && window.webkit.messageHandlers) {
                for (var k in window.webkit.messageHandlers) {
                    var h = window.webkit.messageHandlers[k];
                    if (h && typeof h.postMessage === 'function') { h.postMessage(msg); return; }
                }
            }
        } catch (e) {}
        try { if (typeof window.PostMessageBridge === 'function') { window.PostMessageBridge(msg); return; } } catch (e) {}
    }
    function handler(e) {
        e.preventDefault();
        e.stopPropagation();
        var anchor = e.target && e.target.closest ? e.target.closest('a') : null;
        var image = e.target && e.target.closest ? e.target.closest('img') : null;
        var sel = '';
        try { sel = window.getSelection ? String(window.getSelection()) : ''; } catch (e2) {}
        send({
            type: 'dosi-contextmenu',
            x: e.clientX,
            y: e.clientY,
            href: anchor ? anchor.href : null,
            src: image ? image.src : null,
            text: sel
        });
        return false;
    }
    function install() {
        if (window.__dosiCtxInstalled) return;
        // document is null inside script-execution-on-document-created on
        // some platforms; bail and try again once DOMContentLoaded runs.
        if (typeof document === 'undefined' || !document) return;
        window.__dosiCtxInstalled = true;
        // Capture phase + non-passive so we beat any page-level handler and
        // can call preventDefault to stop the renderer's own menu.
        document.addEventListener('contextmenu', handler, { capture: true, passive: false });
        // Belt-and-braces: also listen on window, since some sites stop
        // propagation on document early in the bubble phase.
        window.addEventListener('contextmenu', handler, { capture: true, passive: false });
    }
    install();
    if (!window.__dosiCtxInstalled) {
        // Document didn't exist yet (rare). Retry on the next two safe
        // milestones so we don't miss the bridge for any path.
        try { document.addEventListener('readystatechange', install); } catch (e) {}
        try { document.addEventListener('DOMContentLoaded', install); } catch (e) {}
    }
})();
";

    // =====================================================================
    // Download interception bridge
    //
    // Captures clicks the page would otherwise turn into a renderer download
    // (anchor with a 'download' attribute, link with a downloadable file
    // extension, etc), cancels the click, and posts the resolved URL +
    // suggested filename back to the host. The host owns the actual fetch
    // and writes the bytes into the signed-in user's Downloads folder via
    // a DOSI flyout - so the user never sees WebView2's chrome-styled save
    // dialog or its bottom-bar download shelf.
    //
    // Heuristic (matches Chrome / Edge behaviour):
    //   * <a download[="name"]>           -> always treated as a download
    //   * href with a known file extension -> treated as a download
    //   * everything else                  -> normal navigation, untouched
    //
    // Modifier keys (Ctrl, Shift, Meta, middle-click) are explicitly NOT
    // hijacked - those still flow into the new-window bridge so power
    // users keep their open-in-new-tab muscle memory.
    //
    // Extension list is intentionally lean: archives, installers, media,
    // documents, fonts. False positives are cheap (user still gets a
    // download popup with the file) and false negatives are easy to add.
    // =====================================================================

    private const string DownloadBridgeScript = @"
(function() {
    if (window.__dosiDlInstalled) return;
    window.__dosiDlInstalled = true;
    var DL_EXT = /\.(zip|rar|7z|tar|gz|tgz|bz2|xz|iso|dmg|pkg|deb|rpm|appimage|exe|msi|msix|apk|ipa|jar|war|aar|nupkg|whl|crx|xpi|cab|mp3|wav|flac|ogg|opus|m4a|mp4|m4v|mov|mkv|avi|webm|wmv|3gp|pdf|epub|mobi|azw3|djvu|psd|ai|svgz|sketch|fig|blend|fbx|obj|stl|gltf|glb|usdz|csv|xls|xlsx|xlsm|doc|docx|ppt|pptx|odt|ods|odp|rtf|txt|json|xml|yaml|yml|sql|db|sqlite|bak|log|ttf|otf|woff|woff2|eot|bin|img|vhd|vhdx|ova|ovf)(\?.*)?$/i;
    // Cache HEAD probes by URL so a re-click on the same link doesn't
    // re-issue the network probe. Bounded to 256 entries so a long
    // browsing session can't grow this unbounded; oldest entries are
    // evicted in insertion order (good-enough LRU - we don't need
    // true recency on a probe cache).
    var headCache = Object.create(null);
    var headOrder = [];
    var HEAD_CACHE_MAX = 256;
    function headPut(key, value) {
        if (headCache[key] === undefined) {
            headOrder.push(key);
            if (headOrder.length > HEAD_CACHE_MAX) {
                var drop = headOrder.shift();
                if (drop !== undefined) delete headCache[drop];
            }
        }
        headCache[key] = value;
    }
    function send(payload) {
        var msg;
        try { msg = JSON.stringify(payload); } catch (e) { return; }
        try { if (window.chrome && window.chrome.webview && window.chrome.webview.postMessage) { window.chrome.webview.postMessage(msg); return; } } catch (e) {}
        try {
            if (window.webkit && window.webkit.messageHandlers) {
                for (var k in window.webkit.messageHandlers) {
                    var h = window.webkit.messageHandlers[k];
                    if (h && typeof h.postMessage === 'function') { h.postMessage(msg); return; }
                }
            }
        } catch (e) {}
        try { if (typeof window.PostMessageBridge === 'function') { window.PostMessageBridge(msg); return; } } catch (e) {}
    }
    function resolveUrl(href) {
        if (!href) return null;
        try { return new URL(href, document.baseURI || location.href).href; }
        catch (e) { return String(href); }
    }
    function fileNameFromUrl(u) {
        try {
            var url = new URL(u, document.baseURI || location.href);
            var seg = url.pathname.split('/').filter(function(s){ return s.length; }).pop();
            return seg ? decodeURIComponent(seg) : null;
        } catch (e) { return null; }
    }
    function fileNameFromDisposition(cd) {
        if (!cd) return null;
        // RFC 6266 filename* (UTF-8) takes precedence over filename=
        var star = /filename\*=(?:UTF-8''|)([^;]+)/i.exec(cd);
        if (star && star[1]) {
            try { return decodeURIComponent(star[1].trim().replace(/^""|""$/g, '')); }
            catch (e) {}
        }
        var basic = /filename=""?([^"";]+)""?/i.exec(cd);
        if (basic && basic[1]) return basic[1].trim();
        return null;
    }
    function isDownloadableByUrl(anchor, href) {
        if (!anchor || !href) return false;
        // Explicit download attribute -> always a download.
        if (anchor.hasAttribute && anchor.hasAttribute('download')) return true;
        // Known file extension -> treat as download.
        try {
            var url = new URL(href, document.baseURI || location.href);
            if (!/^https?:$/i.test(url.protocol)) return false;
            return DL_EXT.test(url.pathname);
        } catch (e) { return false; }
    }
    // Async fallback: when the URL doesn't match the extension list,
    // fire a HEAD probe and let the response tell us whether the server
    // intends this URL to be saved (Content-Disposition: attachment, or
    // a non-displayable Content-Type like application/octet-stream).
    // Same-origin probes only - cross-origin would 403/CORS most of
    // the time and we don't want to spend time waiting on doomed
    // requests. The fallback is gated to http(s) protocols.
    function probeDownloadable(href) {
        if (headCache[href] !== undefined) return Promise.resolve(headCache[href]);
        var url;
        try { url = new URL(href, document.baseURI || location.href); }
        catch (e) { return Promise.resolve(null); }
        if (!/^https?:$/i.test(url.protocol)) return Promise.resolve(null);
        if (url.origin !== location.origin) return Promise.resolve(null);

        return fetch(href, { method: 'HEAD', credentials: 'include', redirect: 'follow' })
            .then(function (resp) {
                if (!resp || !resp.ok) { headPut(href, null); return null; }
                var cd = resp.headers.get('content-disposition') || '';
                var ct = (resp.headers.get('content-type') || '').toLowerCase();
                var looksAttachment = /attachment/i.test(cd);
                var binaryType =
                    ct.indexOf('application/octet-stream') === 0 ||
                    ct.indexOf('application/zip') === 0 ||
                    ct.indexOf('application/pdf') === 0 ||
                    ct.indexOf('application/x-msdownload') === 0 ||
                    ct.indexOf('application/x-msi') === 0 ||
                    ct.indexOf('application/vnd.android.package-archive') === 0 ||
                    ct.indexOf('application/x-apple-diskimage') === 0;
                if (!looksAttachment && !binaryType) { headPut(href, null); return null; }
                var name = fileNameFromDisposition(cd) || fileNameFromUrl(href) || 'download';
                var result = { name: name };
                headPut(href, result);
                return result;
            })
            .catch(function () { headPut(href, null); return null; });
    }
    function handler(e) {
        // Skip non-primary buttons and any modifier - those go through
        // new-window / context-menu paths.
        if (e.button !== 0) return;
        if (e.ctrlKey || e.metaKey || e.shiftKey || e.altKey) return;
        var anchor = e.target && e.target.closest ? e.target.closest('a') : null;
        if (!anchor) return;
        var href = anchor.href;
        if (!href) return;
        // Fast path: URL pattern says download.
        if (isDownloadableByUrl(anchor, href)) {
            e.preventDefault();
            e.stopPropagation();
            var resolved = resolveUrl(href);
            var dlAttr = anchor.getAttribute && anchor.getAttribute('download');
            var suggested = (dlAttr && dlAttr.trim()) ? dlAttr.trim() : fileNameFromUrl(resolved);
            send({
                type: 'dosi-download',
                url: resolved,
                filename: suggested || 'download',
                referer: location.href
            });
            return false;
        }
        // Slow path: probe the server. We can't synchronously cancel
        // and then asynchronously decide - the engine will have already
        // started the navigation. So if the probe is unresolved, just
        // let the navigation proceed; if a previous probe of the same
        // href came back positive we suppress and forward to the host.
        var cached = headCache[anchor.href];
        if (cached && cached.name) {
            e.preventDefault();
            e.stopPropagation();
            send({
                type: 'dosi-download',
                url: resolveUrl(href),
                filename: cached.name,
                referer: location.href
            });
            return false;
        }
        // Warm the cache so a second click hits the fast path above.
        try { probeDownloadable(href); } catch (ignore) {}
    }
    try { document.addEventListener('click', handler, { capture: true, passive: false }); }
    catch (e) { document.addEventListener('click', handler, true); }

    // Mouse-over warm-up: when the user hovers a link for >120 ms,
    // pre-probe it so by the time they click we already know whether
    // the server wants it downloaded. Massively improves the hit rate
    // for sites whose download URLs don't carry a file extension.
    var hoverTimer = null;
    var hoverHref = null;
    document.addEventListener('mouseover', function (e) {
        var a = e.target && e.target.closest ? e.target.closest('a[href]') : null;
        if (!a) return;
        var href = a.href;
        if (!href || isDownloadableByUrl(a, href)) return;
        if (headCache[href] !== undefined) return;
        if (hoverTimer) clearTimeout(hoverTimer);
        hoverHref = href;
        hoverTimer = setTimeout(function () {
            if (hoverHref === href) {
                try { probeDownloadable(href); } catch (ignore) {}
            }
        }, 120);
    }, true);
    document.addEventListener('mouseout', function () {
        if (hoverTimer) { clearTimeout(hoverTimer); hoverTimer = null; }
        hoverHref = null;
    }, true);
})();
";

    /// <summary>
    /// Best-effort: subscribe to the wrapper's JS-&gt;host postMessage channel.
    /// Wrapped in a try block because the event names belong to the
    /// third-party Avalonia.Controls.WebView package and we don't want a
    /// future rename to crash the browser.
    /// </summary>
    private void TryEnablePostMessageBridge()
    {
        if (_webView == null) return;
        try
        {
            // Subscribing is enough on this wrapper; the underlying WebView2 /
            // WKWebView / WebKitGTK adapter installs its own bridge on first
            // attach and routes incoming messages through this single event.
            _webView.WebMessageReceived += OnWebMessageReceived;
        }
        catch { /* tolerate API drift across package versions */ }
    }

    /// <summary>
    /// Re-asserts the context-menu suppression script on the freshly loaded
    /// page. Idempotent (the script tags itself with __dosiCtxInstalled) so
    /// repeated injections during rapid navigation are harmless.
    /// </summary>
    private async void TryInjectContextMenuBridge()
    {
        if (_webView == null) return;
        try
        {
            // NativeWebView.InvokeScript returns Task<string?> (see decompiled
            // package source); the result is the script's last expression value
            // which we don't need here.
            await _webView.InvokeScript(ContextMenuBridgeScript);
            await _webView.InvokeScript(ScrollBridgeScript);
            await _webView.InvokeScript(NewWindowBridgeScript);
            await _webView.InvokeScript(FullScreenBridgeScript);
            await _webView.InvokeScript(DownloadBridgeScript);
            await TryApplyZoomAsync();
        }
        catch { /* page may not be ready yet, or script API unavailable */ }
    }

    /// <summary>
    /// Asks the page to exit any active HTML5 fullscreen by calling the
    /// shim's <c>__dosiExitFs</c> entry point. Used by the host when the
    /// user presses Escape from C# - keeps the page's fullscreen state
    /// machine in sync with the host's chrome state so YouTube doesn't
    /// keep showing its in-fullscreen UI after we've already restored
    /// the toolbar.
    /// </summary>
    public async Task ExitPageFullscreenAsync()
    {
        if (_webView == null) return;
        try
        {
            await _webView.InvokeScript("try{window.__dosiExitFs && window.__dosiExitFs();}catch(e){}");
        }
        catch { /* page navigated away or bridge not yet installed */ }
    }

    /// <summary>Pushes the current <see cref="ZoomPercent"/> into the live
    /// page via CSS <c>zoom</c>. Cheap enough to call on every navigation
    /// completion and on every property change.</summary>
    private async Task TryApplyZoomAsync()
    {
        if (_webView == null) return;
        try
        {
            var script = string.Format(
                CultureInfo.InvariantCulture,
                "(function(){{try{{var z={0:0.###}/100;if(document&&document.documentElement){{document.documentElement.style.zoom=z;}}if(document&&document.body){{document.body.style.zoom=z;}}}}catch(e){{}}}})();",
                _zoomPercent);
            await _webView.InvokeScript(script);
        }
        catch { /* page navigated away or bridge not yet installed */ }
    }

    // ---- Cross-platform popup / new-window interception --------------------
    //
    // The Avalonia.Controls.WebView NewWindowRequested event is unreliable
    // across engines: WebView2 only raises it for certain popup paths, and by
    // the time it fires the native OS window is sometimes already mid-creation
    // (visible in the screenshot the user reported). Intercepting in JS gives
    // us a single chokepoint that fires BEFORE the engine asks the OS to
    // create a window:
    //   1. Override window.open so it posts a message and returns a stub
    //      (sites that test the return value to decide whether the popup was
    //      "blocked" still see a non-null result).
    //   2. Capture-phase click + auxclick handler that catches anchors with
    //      target=_blank (or target=_new / target=anything-else) and
    //      ctrl/middle-click navigation, prevents default, and posts the
    //      resolved URL back to the host.
    // The host then raises NewWindowRequested, which DOSIWebBrowser already
    // turns into a new DOSI window.
    private const string NewWindowBridgeScript = @"
(function() {
    if (window.__dosiNewWinInstalled) return;
    window.__dosiNewWinInstalled = true;
    function send(payload) {
        var msg;
        try { msg = JSON.stringify(payload); } catch (e) { return; }
        try { if (window.chrome && window.chrome.webview && window.chrome.webview.postMessage) { window.chrome.webview.postMessage(msg); return; } } catch (e) {}
        try {
            if (window.webkit && window.webkit.messageHandlers) {
                for (var k in window.webkit.messageHandlers) {
                    var h = window.webkit.messageHandlers[k];
                    if (h && typeof h.postMessage === 'function') { h.postMessage(msg); return; }
                }
            }
        } catch (e) {}
        try { if (typeof window.PostMessageBridge === 'function') { window.PostMessageBridge(msg); return; } } catch (e) {}
    }
    function resolveUrl(href) {
        if (!href) return null;
        try { return new URL(href, document.baseURI || location.href).href; }
        catch (e) { return String(href); }
    }
    // 1. window.open hijack. Returns a minimal stub so calling code that
    //    pokes at the result (focus(), closed, postMessage) doesn't crash.
    try {
        var origOpen = window.open;
        window.open = function(url, target, features) {
            var resolved = resolveUrl(url);
            if (resolved) send({ type: 'dosi-newwindow', url: resolved });
            // Return a no-op stub object - many sites only check for truthy.
            var stub = {
                closed: false,
                focus: function(){}, blur: function(){},
                close: function(){ this.closed = true; },
                postMessage: function(){},
                location: { href: resolved || '' },
                document: null, opener: window
            };
            return stub;
        };
        window.open.__dosiPatched = true;
    } catch (e) {}
    // 2. Click / auxclick / mousedown interception for target=_blank anchors
    //    and modifier-key new-tab gestures. Capture phase + non-passive so we
    //    beat any page-level handler.
    function shouldIntercept(e, anchor) {
        if (!anchor || !anchor.href) return false;
        // Only http(s) / about / file links - leave mailto:, javascript:,
        // etc. to the engine so the user's mail client still launches.
        var p = (anchor.protocol || '').toLowerCase();
        if (p && p !== 'http:' && p !== 'https:' && p !== 'about:' && p !== 'file:') return false;
        var t = (anchor.target || '').toLowerCase();
        if (t === '_blank' || (t && t !== '_self' && t !== '_top' && t !== '_parent')) return true;
        // Middle-click / Ctrl+click / Cmd+click / Shift+click = new tab
        if (e.button === 1) return true;
        if (e.button === 0 && (e.ctrlKey || e.metaKey || e.shiftKey)) return true;
        return false;
    }
    function clickHandler(e) {
        try {
            // Bail on right-button events. auxclick fires for both middle
            // (button 1) and right (button 2); without this guard a
            // right-click on a target=_blank anchor would simultaneously
            // raise dosi-contextmenu (from the separate contextmenu
            // handler) AND dosi-newwindow here, so the host opens a
            // new browser window every time the user tries to bring up
            // the right-click menu over a link/image - the
            // 'right-click sometimes auto-opens a new window for no
            // reason' bug. Right-click is contextmenu-only; new-window
            // is left-click + modifier, middle-click, or a programmatic
            // window.open (handled separately above).
            if (e.button === 2) return;
            var anchor = e.target && e.target.closest ? e.target.closest('a[href]') : null;
            if (!shouldIntercept(e, anchor)) return;
            e.preventDefault();
            e.stopPropagation();
            var resolved = resolveUrl(anchor.getAttribute('href'));
            if (resolved) send({ type: 'dosi-newwindow', url: resolved });
        } catch (err) {}
    }
    try { document.addEventListener('click', clickHandler, { capture: true, passive: false }); }
    catch (e) { document.addEventListener('click', clickHandler, true); }
    try { document.addEventListener('auxclick', clickHandler, { capture: true, passive: false }); }
    catch (e) { document.addEventListener('auxclick', clickHandler, true); }
    // 3. <form target=_blank> submissions. Rewrite to _self so the form
    //    submits in the current page rather than spawning an OS window.
    function formHandler(e) {
        try {
            var f = e.target;
            if (!f || f.tagName !== 'FORM') return;
            var t = (f.target || '').toLowerCase();
            if (t === '_blank' || (t && t !== '_self' && t !== '_top' && t !== '_parent')) {
                f.target = '_self';
            }
        } catch (err) {}
    }
    try { document.addEventListener('submit', formHandler, { capture: true, passive: false }); }
    catch (e) { document.addEventListener('submit', formHandler, true); }
})();
";

    // ---- Cross-platform HTML5 fullscreen bridge ----------------------------
    //
    // The HTML5 Fullscreen API (Element.requestFullscreen / document.exitFullscreen)
    // is what YouTube, Vimeo, native HTML5 <video> controls, fullscreen web
    // games etc. all call. The Avalonia.Controls.WebView wrapper doesn't
    // bubble that request out as an OS-level fullscreen for us, and even if
    // it did, "true" OS fullscreen would lose the rest of the DOSI desktop
    // chrome. Instead we shim the API entirely in JS:
    //
    //   * Override every requestFullscreen / *RequestFullScreen prototype so
    //     it stretches the target element to fill the WebView viewport via
    //     CSS (position:fixed; 100vw/100vh; max z-index).
    //   * Patch document.fullscreenElement (and the vendor-prefixed aliases)
    //     to return the active element so YouTube's player state machine
    //     agrees we're fullscreen and shows the right controls.
    //   * Fire a synthetic 'fullscreenchange' event each transition so any
    //     listener the page registered (analytics, keyboard re-bindings)
    //     fires too.
    //   * postMessage 'dosi-fullscreen' to the host so DOSIWebBrowser can
    //     hide its own toolbar / status bar / window chrome and maximise
    //     the OS window for a true edge-to-edge experience.
    //   * Expose window.__dosiExitFs() as a back-channel the host can call
    //     when the user presses Escape from C#.
    //
    // The script is idempotent (window.__dosiFsInstalled guard) so the
    // re-injection that fires on every NavigationCompleted is harmless.
    private const string FullScreenBridgeScript = @"
(function() {
    if (window.__dosiFsInstalled) return;
    window.__dosiFsInstalled = true;

    function send(payload) {
        var msg;
        try { msg = JSON.stringify(payload); } catch (e) { return; }
        try { if (window.chrome && window.chrome.webview && window.chrome.webview.postMessage) { window.chrome.webview.postMessage(msg); return; } } catch (e) {}
        try {
            if (window.webkit && window.webkit.messageHandlers) {
                for (var k in window.webkit.messageHandlers) {
                    var h = window.webkit.messageHandlers[k];
                    if (h && typeof h.postMessage === 'function') { h.postMessage(msg); return; }
                }
            }
        } catch (e) {}
        try { if (typeof window.PostMessageBridge === 'function') { window.PostMessageBridge(msg); return; } } catch (e) {}
    }

    var STYLE_ID = '__dosi_fs_style';
    var ACTIVE_CLASS = '__dosi_fs_active';
    var LOCK_CLASS = '__dosi_fs_lock';
    var fsElement = null;

    function injectStyle() {
        if (document.getElementById(STYLE_ID)) return;
        var s = document.createElement('style');
        s.id = STYLE_ID;
        s.textContent =
            '.' + ACTIVE_CLASS + '{position:fixed!important;top:0!important;left:0!important;right:0!important;bottom:0!important;width:100vw!important;height:100vh!important;max-width:none!important;max-height:none!important;margin:0!important;padding:0!important;z-index:2147483647!important;background:black!important;}' +
            'body.' + LOCK_CLASS + '{overflow:hidden!important;}';
        (document.head || document.documentElement).appendChild(s);
    }

    function fire(name) {
        try { document.dispatchEvent(new Event(name, { bubbles: true })); } catch (e) {}
    }

    function enter(el) {
        if (!el) return Promise.reject(new Error('no element'));
        injectStyle();
        if (fsElement && fsElement !== el) {
            fsElement.classList.remove(ACTIVE_CLASS);
        }
        fsElement = el;
        try { el.classList.add(ACTIVE_CLASS); } catch (e) {}
        try { document.body && document.body.classList.add(LOCK_CLASS); } catch (e) {}
        fire('fullscreenchange');
        fire('webkitfullscreenchange');
        fire('mozfullscreenchange');
        fire('MSFullscreenChange');
        send({ type: 'dosi-fullscreen', enter: true });
        return Promise.resolve();
    }

    function exit() {
        if (!fsElement) {
            send({ type: 'dosi-fullscreen', enter: false });
            return Promise.resolve();
        }
        try { fsElement.classList.remove(ACTIVE_CLASS); } catch (e) {}
        try { document.body && document.body.classList.remove(LOCK_CLASS); } catch (e) {}
        fsElement = null;
        fire('fullscreenchange');
        fire('webkitfullscreenchange');
        fire('mozfullscreenchange');
        fire('MSFullscreenChange');
        send({ type: 'dosi-fullscreen', enter: false });
        return Promise.resolve();
    }

    // Patch every vendor-prefixed requestFullscreen we know about. Any of
    // them may be the one a given site/player feature-detects on first.
    function patchRequest(proto, name) {
        try { proto[name] = function() { return enter(this); }; } catch (e) {}
    }
    patchRequest(Element.prototype, 'requestFullscreen');
    patchRequest(Element.prototype, 'webkitRequestFullscreen');
    patchRequest(Element.prototype, 'webkitRequestFullScreen');
    patchRequest(Element.prototype, 'mozRequestFullScreen');
    patchRequest(Element.prototype, 'msRequestFullscreen');

    function patchExit(name) {
        try { document[name] = exit; } catch (e) {}
    }
    patchExit('exitFullscreen');
    patchExit('webkitExitFullscreen');
    patchExit('webkitCancelFullScreen');
    patchExit('mozCancelFullScreen');
    patchExit('msExitFullscreen');

    // Make document.fullscreenElement (and aliases) reflect our shim state
    // so YouTube's player code that checks `document.fullscreenElement`
    // agrees we are in fullscreen and renders the correct UI.
    function defineFsElement(name) {
        try {
            Object.defineProperty(document, name, {
                get: function() { return fsElement; },
                configurable: true
            });
        } catch (e) {}
    }
    defineFsElement('fullscreenElement');
    defineFsElement('webkitFullscreenElement');
    defineFsElement('webkitCurrentFullScreenElement');
    defineFsElement('mozFullScreenElement');
    defineFsElement('msFullscreenElement');

    // fullscreenEnabled must be true for some sites to even SHOW their
    // fullscreen button in the first place.
    function defineFsEnabled(name) {
        try {
            Object.defineProperty(document, name, {
                get: function() { return true; },
                configurable: true
            });
        } catch (e) {}
    }
    defineFsEnabled('fullscreenEnabled');
    defineFsEnabled('webkitFullscreenEnabled');
    defineFsEnabled('mozFullScreenEnabled');
    defineFsEnabled('msFullscreenEnabled');

    // Back-channel for the host to drive an exit (e.g. user pressed Escape
    // from C#). Calling exit() here mirrors the JS exit path so the page
    // sees a normal fullscreenchange event and doesn't get stuck in the
    // 'currently fullscreen' UI state.
    window.__dosiExitFs = exit;

    // Mirror the spec: Escape inside the page exits fullscreen. The native
    // engine's Escape handling can't see our shim so we wire it ourselves.
    document.addEventListener('keydown', function(e) {
        if (fsElement && (e.key === 'Escape' || e.keyCode === 27)) {
            exit();
            try { e.preventDefault(); e.stopPropagation(); } catch (err) {}
        }
    }, true);

    // Double-click on a <video> toggles fullscreen. Native YouTube does this
    // via Element.requestFullscreen() which our shim already handles, but
    // some embedded players (and YT in some UA-detected modes) skip the
    // API and just toggle their own CSS - leaving DOSI's simulated taskbar
    // hovering over the player. We intercept dblclick globally and route
    // it through enter()/exit() so the host C# side ALWAYS gets the
    // dosi-fullscreen message and hides its chrome accordingly.
    document.addEventListener('dblclick', function(e) {
        try {
            var t = e.target;
            // Walk up a few levels in case the click landed on an overlay
            // child of the <video> (player controls, captions overlay).
            var v = null;
            for (var n = t, hops = 0; n && hops < 5; n = n.parentNode, hops++) {
                if (n.tagName === 'VIDEO') { v = n; break; }
            }
            if (!v && fsElement && fsElement.tagName === 'VIDEO') v = fsElement;
            if (!v) return;

            if (fsElement) {
                exit();
            } else {
                // Prefer fullscreening the player container (so player controls
                // remain interactive), falling back to the <video> itself.
                var target = v;
                for (var p = v.parentElement, hops2 = 0; p && hops2 < 4; p = p.parentElement, hops2++) {
                    var cls = (p.className || '') + '';
                    if (cls.indexOf('player') >= 0 || cls.indexOf('html5-video') >= 0) { target = p; break; }
                }
                enter(target);
            }
            try { e.preventDefault(); e.stopPropagation(); } catch (err) {}
        } catch (err) {}
    }, true);
})();
";

    // ---- Cross-platform scrollbar replacement bridge -----------------------
    //
    // Hides the renderer's native scrollbars with CSS that works across all
    // three engines we target:
    //   * Chromium (WebView2 / WebKitGTK)  -> ::-webkit-scrollbar { display:none }
    //   * Gecko-style fallback             -> scrollbar-width: none
    //   * Legacy IE/Edge fallback          -> -ms-overflow-style: none
    // Then installs a passive scroll listener that posts the current scroll
    // metrics back to the host so the DOSIScrollBars can mirror them. Wheel,
    // keyboard, touch and trackpad scrolling all keep working natively - we
    // only suppress the visual track / thumb that the engine paints.
    private const string ScrollBridgeScript = @"
(function() {
    if (window.__dosiScrollInstalled) return;
    window.__dosiScrollInstalled = true;
    function send(payload) {
        var msg;
        try { msg = JSON.stringify(payload); } catch (e) { return; }
        try { if (window.chrome && window.chrome.webview && window.chrome.webview.postMessage) { window.chrome.webview.postMessage(msg); return; } } catch (e) {}
        try {
            if (window.webkit && window.webkit.messageHandlers) {
                for (var k in window.webkit.messageHandlers) {
                    var h = window.webkit.messageHandlers[k];
                    if (h && typeof h.postMessage === 'function') { h.postMessage(msg); return; }
                }
            }
        } catch (e) {}
        try { if (typeof window.PostMessageBridge === 'function') { window.PostMessageBridge(msg); return; } } catch (e) {}
    }
    function injectCss() {
        try {
            if (document.getElementById('__dosi_scrollbar_hide__')) return;
            var s = document.createElement('style');
            s.id = '__dosi_scrollbar_hide__';
            s.textContent =
                'html,body{scrollbar-width:none !important;-ms-overflow-style:none !important;}' +
                'html::-webkit-scrollbar,body::-webkit-scrollbar,*::-webkit-scrollbar{width:0 !important;height:0 !important;display:none !important;background:transparent !important;}';
            (document.head || document.documentElement || document.body).appendChild(s);
        } catch (e) {}
    }
    function metrics() {
        var de = document.documentElement || {};
        var bd = document.body || {};
        return {
            type: 'dosi-scroll',
            top: window.pageYOffset || de.scrollTop || bd.scrollTop || 0,
            left: window.pageXOffset || de.scrollLeft || bd.scrollLeft || 0,
            scrollHeight: Math.max(de.scrollHeight || 0, bd.scrollHeight || 0),
            scrollWidth: Math.max(de.scrollWidth || 0, bd.scrollWidth || 0),
            clientHeight: window.innerHeight || de.clientHeight || 0,
            clientWidth: window.innerWidth || de.clientWidth || 0
        };
    }
    var pending = false;
    function post() { pending = false; send(metrics()); }
    function schedule() {
        if (pending) return;
        pending = true;
        var raf = window.requestAnimationFrame || function(cb){ return setTimeout(cb, 16); };
        raf(post);
    }
    function install() {
        injectCss();
        try { window.addEventListener('scroll', schedule, { passive: true, capture: true }); } catch (e) { window.addEventListener('scroll', schedule, true); }
        try { window.addEventListener('resize', schedule, { passive: true }); } catch (e) { window.addEventListener('resize', schedule); }
        // Periodic resync catches SPAs that mutate document height without
        // firing scroll (lazy-loaded feeds, infinite scroll, etc.).
        setInterval(schedule, 750);
        setTimeout(post, 60);
    }
    // Allow the host (Avalonia side) to drive the page programmatically.
    window.__dosiScrollTo = function(y, x) {
        try { window.scrollTo({ top: y || 0, left: x || 0, behavior: 'auto' }); }
        catch (e) { try { window.scrollTo(x || 0, y || 0); } catch (e2) {} }
    };
    if (document.readyState === 'loading') {
        try { document.addEventListener('DOMContentLoaded', install, { once: true }); }
        catch (e) { document.addEventListener('DOMContentLoaded', install); }
    } else {
        install();
    }
})();
";

    /// <summary>
    /// User dragged / wheeled the vertical DOSIScrollBar. Push the new
    /// position into the page via the JS bridge. No-op when the change
    /// originated from the page itself (avoids a feedback loop).
    /// </summary>
    private void OnVerticalScrollBarChanged(object? sender, ScrollEventArgs e)
    {
        if (_isUpdatingScrollFromPage) return;
        _ = TryRunScrollToAsync(_hScrollBar.Value, e.NewValue);
    }

    private void OnHorizontalScrollBarChanged(object? sender, ScrollEventArgs e)
    {
        if (_isUpdatingScrollFromPage) return;
        _ = TryRunScrollToAsync(e.NewValue, _vScrollBar.Value);
    }

    private async Task TryRunScrollToAsync(double x, double y)
    {
        if (_webView == null) return;
        try
        {
            var script = string.Format(
                CultureInfo.InvariantCulture,
                "window.__dosiScrollTo && window.__dosiScrollTo({0:0.###},{1:0.###});",
                y, x);
            await _webView.InvokeScript(script);
        }
        catch { /* page navigated away or bridge not yet installed */ }
    }

    /// <summary>
    /// Applies a scroll metrics payload posted by the page onto the
    /// DOSIScrollBars. Toggles their visibility based on whether the
    /// document is actually overflowing in each axis.
    /// </summary>
    private void ApplyScrollMetrics(double top, double left, double scrollHeight, double scrollWidth, double clientHeight, double clientWidth)
    {
        _isUpdatingScrollFromPage = true;
        try
        {
            var vMax = Math.Max(0, scrollHeight - clientHeight);
            _vScrollBar.Maximum = vMax;
            _vScrollBar.ViewportSize = Math.Max(1, clientHeight);
            _vScrollBar.LargeChange = Math.Max(1, clientHeight * 0.9);
            _vScrollBar.SmallChange = 40;
            _vScrollBar.Value = Math.Clamp(top, 0, vMax);
            _vScrollBar.IsVisible = vMax > 0.5;

            var hMax = Math.Max(0, scrollWidth - clientWidth);
            _hScrollBar.Maximum = hMax;
            _hScrollBar.ViewportSize = Math.Max(1, clientWidth);
            _hScrollBar.LargeChange = Math.Max(1, clientWidth * 0.9);
            _hScrollBar.SmallChange = 40;
            _hScrollBar.Value = Math.Clamp(left, 0, hMax);
            _hScrollBar.IsVisible = hMax > 0.5;
        }
        finally
        {
            _isUpdatingScrollFromPage = false;
        }
    }

    /// <summary>
    /// Decodes the JSON payload posted by the injected context-menu script
    /// and republishes it as a strongly-typed <see cref="ContextMenuRequested"/>
    /// event. Silently ignores anything that isn't our own message so we
    /// coexist with other JS bridges on the page.
    /// <para>
    /// THREADING: <c>WebMessageReceived</c> is delivered on the WebView2
    /// COM apartment thread, NOT the Avalonia UI thread. Every downstream
    /// event raised here ends up touching Avalonia visuals (opening
    /// context menus, opening new browser windows, updating scroll
    /// state), and those mutations MUST happen on the UI thread or we
    /// hit "The calling thread cannot access this object because a
    /// different thread owns it." Worse, when that exception unwinds
    /// inside the bridge it can leave the message queue in a state
    /// where the next right-click silently routes its payload to the
    /// NewWindowRequested branch instead of the contextmenu branch -
    /// which is the "right-click sometimes auto-opens a new window for
    /// no reason" symptom. Marshalling the entire decode + dispatch
    /// onto the UI thread fixes both bugs in one place.
    /// </para>
    /// </summary>
    private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        var payload = e.Body;
        if (string.IsNullOrEmpty(payload)) return;

        // Capture the payload string (a value type for our purposes) and
        // post the heavy work onto the UI thread. Post (not InvokeAsync)
        // because we don't need to await completion - the JS side
        // doesn't wait for an ack.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => HandleWebMessage(payload));
    }

    private void HandleWebMessage(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (!root.TryGetProperty("type", out var typeProp) ||
                typeProp.ValueKind != JsonValueKind.String) return;
            var msgType = typeProp.GetString();

            if (msgType == "dosi-scroll")
            {
                static double D(JsonElement r, string name) =>
                    r.TryGetProperty(name, out var p) && p.TryGetDouble(out var v) ? v : 0;
                ApplyScrollMetrics(
                    top: D(root, "top"),
                    left: D(root, "left"),
                    scrollHeight: D(root, "scrollHeight"),
                    scrollWidth: D(root, "scrollWidth"),
                    clientHeight: D(root, "clientHeight"),
                    clientWidth: D(root, "clientWidth"));
                return;
            }

            if (msgType == "dosi-newwindow")
            {
                if (root.TryGetProperty("url", out var urlProp) &&
                    urlProp.ValueKind == JsonValueKind.String)
                {
                    var url = urlProp.GetString();
                    if (!string.IsNullOrEmpty(url))
                        NewWindowRequested?.Invoke(this, url);
                }
                return;
            }

            if (msgType == "dosi-fullscreen")
            {
                bool enter = root.TryGetProperty("enter", out var enterProp) &&
                             enterProp.ValueKind == JsonValueKind.True;
                FullScreenChangeRequested?.Invoke(this, enter);
                return;
            }

            if (msgType == "dosi-download")
            {
                string? dlUrl = root.TryGetProperty("url", out var dlUrlProp) &&
                                dlUrlProp.ValueKind == JsonValueKind.String
                    ? dlUrlProp.GetString() : null;
                if (string.IsNullOrEmpty(dlUrl)) return;
                string? dlName = root.TryGetProperty("filename", out var dlNameProp) &&
                                 dlNameProp.ValueKind == JsonValueKind.String
                    ? dlNameProp.GetString() : null;
                string? dlReferer = root.TryGetProperty("referer", out var dlRefProp) &&
                                    dlRefProp.ValueKind == JsonValueKind.String
                    ? dlRefProp.GetString() : null;
                DownloadRequested?.Invoke(this, new WebViewDownloadRequestedEventArgs
                {
                    Url = dlUrl,
                    SuggestedFileName = string.IsNullOrWhiteSpace(dlName) ? "download" : dlName,
                    Referer = dlReferer
                });
                return;
            }

            if (msgType != "dosi-contextmenu") return;

            double x = root.TryGetProperty("x", out var xp) && xp.TryGetDouble(out var xv) ? xv : 0;
            double y = root.TryGetProperty("y", out var yp) && yp.TryGetDouble(out var yv) ? yv : 0;
            string? href = root.TryGetProperty("href", out var hp) && hp.ValueKind == JsonValueKind.String ? hp.GetString() : null;
            string? src = root.TryGetProperty("src", out var sp) && sp.ValueKind == JsonValueKind.String ? sp.GetString() : null;
            string? text = root.TryGetProperty("text", out var tp) && tp.ValueKind == JsonValueKind.String ? tp.GetString() : null;

            ContextMenuRequested?.Invoke(this, new WebViewContextMenuRequestedEventArgs
            {
                X = x,
                Y = y,
                LinkUrl = string.IsNullOrEmpty(href) ? null : href,
                ImageUrl = string.IsNullOrEmpty(src) ? null : src,
                SelectedText = text
            });
        }
        catch (JsonException) { /* not our message format - ignore */ }
    }

    private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name == "PageTitle" && e.NewValue is string title && !string.IsNullOrEmpty(title))
        {
            _currentTitle = title;
            TitleChanged?.Invoke(this, _currentTitle);
        }
    }

    public void NavigateToUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return;

        _currentUrl = url;
        if (_webView == null) return; // browser unavailable on this platform

        try
        {
            _webView.Source = new Uri(url);
        }
        catch (UriFormatException) when (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _webView.Source = new Uri("https://" + url);
                _currentUrl = "https://" + url;
            }
            catch { }
        }
    }

    public void GoBack() { if (_webView?.CanGoBack == true) _webView.GoBack(); }
    public void GoForward() { if (_webView?.CanGoForward == true) _webView.GoForward(); }
    public void Refresh() => _webView?.Refresh();

    public bool CanGoBack => _webView?.CanGoBack ?? false;
    public bool CanGoForward => _webView?.CanGoForward ?? false;
    public string CurrentUrl => _currentUrl;
    public string Title => _currentTitle;

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_webView == null)
        {
            GC.SuppressFinalize(this);
            return;
        }

        // Unsubscribe events
        _webView.NavigationStarted -= OnNavigationStarted;
        _webView.NavigationCompleted -= OnNavigationCompleted;
        _webView.PropertyChanged -= OnPropertyChanged;
        _webView.NewWindowRequested -= OnNewWindowRequested;
        try { _webView.WebMessageReceived -= OnWebMessageReceived; } catch { }
        _vScrollBar.Scroll -= OnVerticalScrollBarChanged;
        _hScrollBar.Scroll -= OnHorizontalScrollBarChanged;

        // Navigate away and dispose the WebView
        try
        {
            _webView.Source = new Uri("about:blank");
        }
        catch { }

        // Remove from visual tree
        _container.Children.Remove(_webView);

        // Dispose the WebView if it implements IDisposable
        if (_webView is IDisposable disposable)
        {
            disposable.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
