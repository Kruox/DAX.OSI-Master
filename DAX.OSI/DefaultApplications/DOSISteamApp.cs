using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DAX.OSI.Controls;
using DOSI.CORE.AccentManagement;
using DOSI.CORE.UIComponents;
using DOSI.CORE.UIComponents.WindowManagement;
// Path is ambiguous between System.IO.Path and Avalonia.Controls.Shapes.Path
// (the latter comes in via Avalonia.Controls.Shapes for icon work).
// Alias the IO type to keep the file-system code readable.
using IOPath = System.IO.Path;

namespace DAX.OSI.DefaultApplications;

/// <summary>
/// Steam launcher hosted inside a <see cref="DOSIWindow"/>. Reads the host's
/// local Steam install (if present), renders the user's library as a tile
/// grid, and launches games by handing the <c>steam://rungameid/&lt;appid&gt;</c>
/// URI to the OS shell - which boots them through the real Steam client.
///
/// Why we don't host the Steam client itself: the Steam desktop client is a
/// native top-level OS window with its own GPU surface and overlay. There is
/// no supported way to reparent another process's HWND/WKWebView/GTK widget
/// into an Avalonia canvas without breaking input routing, focus, DPI
/// scaling, and Steam's own embedded CEF browser. Same goes for the games
/// themselves - they own their own swap chain. So we host Steam's WEB
/// surfaces (store / community / news) inside a <see cref="WebViewWrapper"/>
/// and launch installed games through Steam's URI scheme. That's the
/// realistic 80% of the experience without pretending we can do the
/// impossible 20%.
/// </summary>
public class DOSISteamApp : DOSIWindow
{
    private readonly Border _libraryButton;
    private readonly Border _storeButton;
    private readonly Border _communityButton;
    private readonly Border _newsButton;
    private readonly Border _contentHost;
    private readonly TextBlock _statusText;

    /// <summary>Preserved across view switches so we don't rebuild the WebView every click.</summary>
    private WebViewWrapper? _webView;
    private string? _activeWebUrl;

    /// <summary>Cached library install root (e.g. <c>C:\Program Files (x86)\Steam</c>) or null when Steam isn't installed.</summary>
    private static readonly string? _steamRoot = ResolveSteamRoot();

    /// <summary>Single shared HttpClient for header-image fetches. Same rationale as the browser's favicon client.</summary>
    private static readonly HttpClient _imageHttp = new() { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>
    /// Process-wide cache of decoded header bitmaps keyed by Steam appid.
    /// Avoids refetching+redecoding on every Library reopen (the user can
    /// trivially open / close the Steam app several times per session, and
    /// each tile's PNG is ~30KB - small individually, but a 100-game library
    /// is ~3MB of needless network + CPU per reopen). Capped at 512 entries
    /// so an unusually huge library can't grow this unbounded; eviction is
    /// crude FIFO because access patterns here are bursty (open library →
    /// hit them all → close), not LRU-shaped.
    /// </summary>
    private static readonly Dictionary<string, Bitmap> _headerCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> _headerCacheOrder = new();
    private const int HeaderCacheMax = 512;
    private static readonly object _headerCacheLock = new();

    private static AccentManager Accents => AccentManager.Instance;

    public DOSISteamApp()
    {
        Title = "Steam";
        WindowWidth = 1000;
        WindowHeight = 640;
        MinimumSize = new Size(680, 440);
        Icon = CreateAppIcon();

        // ---------- Sidebar ----------
        // Vertical "tab" rail on the left (Steam-style). Each pill swaps the
        // content host instead of opening a new window so the user always
        // stays inside the same DOSIWindow.
        _libraryButton   = BuildSidebarButton("Library",   isActive: true);
        _storeButton     = BuildSidebarButton("Store",     isActive: false);
        _communityButton = BuildSidebarButton("Community", isActive: false);
        _newsButton      = BuildSidebarButton("News",      isActive: false);

        _libraryButton.PointerReleased   += (_, _) => ShowLibrary();
        _storeButton.PointerReleased     += (_, _) => ShowWeb("https://store.steampowered.com/", _storeButton);
        _communityButton.PointerReleased += (_, _) => ShowWeb("https://steamcommunity.com/", _communityButton);
        _newsButton.PointerReleased      += (_, _) => ShowWeb("https://store.steampowered.com/news/", _newsButton);

        var brand = new TextBlock
        {
            Text = "STEAM",
            FontFamily = DOSI.CORE.DOSIFonts.Mono,
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            LetterSpacing = 3.0,
            Foreground = Accents.AccentPrimaryBrush,
            Margin = new Thickness(18, 18, 18, 24),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        var sidebarStack = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(8, 0, 8, 0),
            Children = { _libraryButton, _storeButton, _communityButton, _newsButton }
        };

        var sidebarRoot = new StackPanel { Spacing = 0 };
        sidebarRoot.Children.Add(brand);
        sidebarRoot.Children.Add(sidebarStack);

        var sidebar = new Border
        {
            Width = 180,
            Background = Accents.WindowChromeBrush,
            BorderBrush = new SolidColorBrush(Accents.ControlBorder),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = sidebarRoot
        };

        // ---------- Content host ----------
        _contentHost = new Border
        {
            Background = Accents.WindowContentBrush,
            ClipToBounds = true
        };

        // ---------- Status bar ----------
        _statusText = new TextBlock
        {
            Text = ResolveStatusLine(),
            FontSize = 11,
            Foreground = Accents.TextSecondaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0)
        };
        var statusBar = new Border
        {
            Height = 26,
            Background = Accents.ControlBackgroundBrush,
            BorderBrush = new SolidColorBrush(Accents.ControlBorder),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = _statusText
        };

        // ---------- Layout ----------
        var bodyGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*")
        };
        bodyGrid.Children.Add(sidebar);   Grid.SetColumn(sidebar, 0);
        bodyGrid.Children.Add(_contentHost); Grid.SetColumn(_contentHost, 1);

        var rootGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto")
        };
        rootGrid.Children.Add(bodyGrid);  Grid.SetRow(bodyGrid, 0);
        rootGrid.Children.Add(statusBar); Grid.SetRow(statusBar, 1);

        Content = rootGrid;

        // Open on the Library by default - it's the panel users care about
        // most and works fully offline (no WebView needed).
        ShowLibrary();

        // Live accent re-style + WebView teardown on close.
        AttachedToVisualTree += (_, _) => Accents.AccentChanged += OnAccentChanged;
        DetachedFromVisualTree += (_, _) => Accents.AccentChanged -= OnAccentChanged;
        Closing += OnWindowClosing;
    }

    private void OnWindowClosing(object? sender, DOSIWindowClosingEventArgs e)
    {
        // Drop the native WebView's HWND/WKWebView before the window dies so
        // its OS-level handle doesn't outlive us (same pattern as the
        // browser's per-tab teardown).
        try { _webView?.Dispose(); } catch { /* best-effort */ }
        _webView = null;
    }

    private void OnAccentChanged(object? sender, EventArgs e)
    {
        // Re-render whatever is currently visible so the new accent is
        // picked up by the static colors baked into the cards / tiles.
        if (_activeWebUrl == null) ShowLibrary();
        // Sidebar pill highlight tracks the accent - simplest refresh is to
        // re-style the active button.
        StyleSidebarButton(_libraryButton,
            ReferenceEquals(GetActiveSidebarButton(), _libraryButton));
        StyleSidebarButton(_storeButton,
            ReferenceEquals(GetActiveSidebarButton(), _storeButton));
        StyleSidebarButton(_communityButton,
            ReferenceEquals(GetActiveSidebarButton(), _communityButton));
        StyleSidebarButton(_newsButton,
            ReferenceEquals(GetActiveSidebarButton(), _newsButton));
        _statusText.Foreground = Accents.TextSecondaryBrush;
    }

    private Border? GetActiveSidebarButton()
    {
        if (_activeWebUrl == null) return _libraryButton;
        if (_activeWebUrl.Contains("store.steampowered.com/news"))   return _newsButton;
        if (_activeWebUrl.Contains("store.steampowered.com"))        return _storeButton;
        if (_activeWebUrl.Contains("steamcommunity.com"))            return _communityButton;
        return null;
    }

    // =====================================================================
    // Library view (native, offline)
    // =====================================================================

    private void ShowLibrary()
    {
        // Tear down any active WebView when leaving the web views - keeping
        // it parked off-screen would still hold the native HWND, and the
        // library view can't host one anyway.
        DisposeActiveWebView();
        _activeWebUrl = null;

        StyleSidebarButton(_libraryButton, true);
        StyleSidebarButton(_storeButton, false);
        StyleSidebarButton(_communityButton, false);
        StyleSidebarButton(_newsButton, false);

        if (_steamRoot == null)
        {
            _contentHost.Child = BuildSteamMissingNotice();
            _statusText.Text = "Steam is not installed on this machine.";
            return;
        }

        // Show a placeholder immediately, then push the directory walk off
        // the UI thread. On a slow disk / huge library this scan can take a
        // hundred-plus milliseconds, which is enough to feel like a freeze
        // when clicking the Library tab. The previous synchronous version
        // also stalled the WebView teardown above on the same frame.
        _contentHost.Child = BuildLibraryLoadingPlaceholder();
        _statusText.Text = "Scanning Steam library ...";

        // Capture root locally because _steamRoot is non-null inside this
        // method (we checked above) but the lambda can't infer that across
        // the Task.Run boundary.
        var root = _steamRoot;
        _ = Task.Run(() =>
        {
            // Materialise on the background thread so the OrderBy + ToList
            // both happen off-UI. ScanInstalledGames is yield-based so the
            // ToList here is what actually drives the disk reads.
            List<SteamGame> games;
            try { games = ScanInstalledGames(root).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList(); }
            catch { games = new List<SteamGame>(); }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // Bail if the user navigated away (or signed out) while the
                // scan was running - dropping the result is fine, the next
                // ShowLibrary() call will just re-scan.
                if (_activeWebUrl != null) return;

                _statusText.Text = games.Count == 0
                    ? "Steam install detected, but no installed games were found."
                    : $"{games.Count} installed game" + (games.Count == 1 ? "" : "s") +
                      "  ·  Click a tile to launch through Steam.";

                if (games.Count == 0)
                {
                    _contentHost.Child = BuildEmptyLibraryNotice();
                    return;
                }

                var wrap = new WrapPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(20, 16, 20, 16)
                };
                foreach (var g in games)
                    wrap.Children.Add(BuildGameTile(g));

                _contentHost.Child = new DOSIScrollViewer
                {
                    Content = wrap,
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
                };
            });
        });
    }

    private Control BuildLibraryLoadingPlaceholder() => new Border
    {
        Padding = new Thickness(40),
        Child = new TextBlock
        {
            Text = "Loading library ...",
            FontSize = 14,
            Foreground = Accents.TextSecondaryBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        },
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch
    };

    private Control BuildGameTile(SteamGame g)
    {
        // 460x215 is Steam's canonical "header capsule" aspect; we render
        // tiles slightly smaller so a few fit per row without overwhelming
        // the window.
        const double tileWidth = 230;
        const double tileImageHeight = 107;

        var image = new Image
        {
            Width = tileWidth,
            Height = tileImageHeight,
            Stretch = Stretch.UniformToFill
        };

        // Fallback chrome shown until (or instead of) the header bitmap
        // arrives. A solid accent panel with the game's first letter reads
        // well on its own when the network is offline.
        var initial = string.IsNullOrEmpty(g.Name)
            ? "?"
            : char.ToUpperInvariant(g.Name[0]).ToString();
        var fallback = new Border
        {
            Background = Accents.AccentGradientBrush,
            Child = new TextBlock
            {
                Text = initial,
                FontFamily = DOSI.CORE.DOSIFonts.Mono,
                FontSize = 36,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Accents.TextOnAccent),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var imageHost = new Grid
        {
            Width = tileWidth,
            Height = tileImageHeight
        };
        imageHost.Children.Add(fallback);
        imageHost.Children.Add(image);
        image.IsVisible = false;

        // Async header-image fetch from Steam's CDN. Fire-and-forget; on
        // failure (offline / no such app) the fallback panel just stays.
        _ = LoadHeaderImageAsync(g.AppId, image, fallback);

        var nameText = new TextBlock
        {
            Text = g.Name,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextPrimaryBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(10, 8, 10, 0)
        };
        var sizeText = new TextBlock
        {
            Text = FormatSize(g.SizeOnDiskBytes),
            FontFamily = DOSI.CORE.DOSIFonts.Mono,
            FontSize = 10,
            Foreground = Accents.TextSecondaryBrush,
            Margin = new Thickness(10, 2, 10, 10),
            Opacity = 0.85
        };

        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(imageHost);
        stack.Children.Add(nameText);
        stack.Children.Add(sizeText);

        var tile = new Border
        {
            Width = tileWidth,
            Margin = new Thickness(8),
            CornerRadius = new CornerRadius(8),
            Background = Accents.ControlBackgroundBrush,
            BorderBrush = new SolidColorBrush(Accents.ControlBorder),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = stack
        };

        // Hover halo - intentionally accent-tinted (not white) so hover state
        // tracks whatever theme the user picked.
        tile.PointerEntered += (_, _) =>
        {
            tile.BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0,
                OffsetY = 4,
                Blur = 18,
                Spread = -2,
                Color = Color.FromArgb(140,
                    Accents.AccentPrimary.R,
                    Accents.AccentPrimary.G,
                    Accents.AccentPrimary.B)
            });
        };
        tile.PointerExited += (_, _) => tile.BoxShadow = default;
        tile.PointerPressed += (_, _) => tile.Opacity = 0.85;
        tile.PointerReleased += (_, _) =>
        {
            tile.Opacity = 1;
            LaunchGame(g);
        };

        ToolTip.SetTip(tile, $"{g.Name}\nApp ID: {g.AppId}\nClick to launch through Steam");
        return tile;
    }

    private void LaunchGame(SteamGame g)
    {
        try
        {
            // Prefer launching the steam executable directly with -applaunch
            // over the steam:// URI handler. The URI route triggers the
            // Windows / macOS shell's "open external app?" confirmation
            // dialog every time, even though the user just clicked our tile.
            // -applaunch is the same command Steam itself uses internally
            // (the URI handler ultimately just forwards to it), so cloud
            // saves / overlay / DRM / achievements still all wire up
            // correctly - we're just skipping the OS confirmation prompt.
            //
            // Falls back to the steam:// URI when we can't locate steam.exe
            // (rare; happens if the user moved Steam after install). The
            // fallback path will surface the OS prompt - that's acceptable
            // for the edge case.
            var launched = TryDirectLaunch(g);
            if (!launched)
            {
                Process.Start(new ProcessStartInfo($"steam://rungameid/{g.AppId}")
                {
                    UseShellExecute = true
                });
            }
            _statusText.Text = $"Launching {g.Name} through Steam ...";

            // Best-effort cross-platform borderless transform. Runs on a
            // background thread, polls for the game's window to appear, then
            // strips the host OS chrome so it doesn't visually fight DAX.OSI.
            // Result is purely cosmetic - if it fails (Wayland, missing
            // wmctrl, anti-cheat blocking, etc.) the game still runs fine,
            // we just leave its native chrome in place.
            _ = Task.Run(async () =>
            {
                var result = await BorderlessGameLauncher.MakeBorderlessAsync(g.Name);
                var msg = result switch
                {
                    BorderlessGameLauncher.Result.Applied     => $"{g.Name} running borderless",
                    BorderlessGameLauncher.Result.TimedOut    => $"{g.Name}: didn't detect a game window in time",
                    BorderlessGameLauncher.Result.ToolMissing => $"{g.Name}: borderless needs wmctrl on Linux",
                    BorderlessGameLauncher.Result.Unsupported => $"{g.Name} launched (borderless not supported on this platform)",
                    _ => $"{g.Name} launched"
                };
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => _statusText.Text = msg);
            });
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Couldn't launch {g.Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Launch the game by invoking the Steam executable directly with
    /// <c>-applaunch &lt;appid&gt;</c>. Returns true if the process actually
    /// started; false if we couldn't find the executable on disk (caller
    /// should then fall back to the steam:// URI handler). UseShellExecute
    /// is intentionally false so the OS doesn't show its "open external
    /// app?" confirmation dialog - that's the whole reason this code path
    /// exists.
    /// </summary>
    private static bool TryDirectLaunch(SteamGame g)
    {
        var exe = ResolveSteamExecutable();
        if (exe == null) return false;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"-applaunch {g.AppId}",
                UseShellExecute = false,
                CreateNoWindow = true,
                // Run in the steam install dir so steam.exe finds its
                // satellite DLLs (it's been picky about this historically).
                WorkingDirectory = IOPath.GetDirectoryName(exe) ?? string.Empty
            });
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Resolves the path to the Steam executable on the current platform,
    /// or null if Steam isn't installed / detectable. Mirrors the install-
    /// root resolution in <see cref="ResolveSteamRoot"/> but points at the
    /// runnable binary instead of the data directory.
    /// </summary>
    private static string? ResolveSteamExecutable()
    {
        if (_steamRoot == null) return null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var p = IOPath.Combine(_steamRoot, "steam.exe");
                return File.Exists(p) ? p : null;
            }
            if (OperatingSystem.IsMacOS())
            {
                // Steam.app's CLI launcher.
                var p = "/Applications/Steam.app/Contents/MacOS/steam_osx";
                return File.Exists(p) ? p : null;
            }
            if (OperatingSystem.IsLinux())
            {
                // Distro packages drop a 'steam' wrapper in /usr/bin; the
                // bundled Steam install ships a 'steam.sh' inside the root.
                string[] candidates =
                {
                    "/usr/bin/steam",
                    "/usr/local/bin/steam",
                    IOPath.Combine(_steamRoot, "steam.sh")
                };
                foreach (var c in candidates)
                    if (File.Exists(c)) return c;
            }
        }
        catch { /* fall through to null */ }
        return null;
    }

    private async Task LoadHeaderImageAsync(string appId, Image target, Control fallback)
    {
        if (string.IsNullOrEmpty(appId)) return;

        // Cache hit fast-path: skip the network + decode entirely. We still
        // marshal back to the UI thread because target.Source is a Visual
        // property and Avalonia enforces single-threaded access.
        Bitmap? cached;
        lock (_headerCacheLock)
        {
            _headerCache.TryGetValue(appId, out cached);
        }
        if (cached != null)
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                target.Source = cached;
                target.IsVisible = true;
                fallback.IsVisible = false;
            });
            return;
        }

        var url = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg";
        try
        {
            var bytes = await _imageHttp.GetByteArrayAsync(url).ConfigureAwait(false);
            if (bytes == null || bytes.Length == 0) return;
            using var ms = new MemoryStream(bytes);
            var bmp = new Bitmap(ms);

            lock (_headerCacheLock)
            {
                // Race-safe insert: another tile for the same appid could
                // have raced past us and populated the slot already. First
                // writer wins; we keep the bmp we decoded for THIS callsite
                // since it's already loaded into our target below.
                if (!_headerCache.ContainsKey(appId))
                {
                    _headerCache[appId] = bmp;
                    _headerCacheOrder.Enqueue(appId);
                    while (_headerCacheOrder.Count > HeaderCacheMax)
                    {
                        var evict = _headerCacheOrder.Dequeue();
                        _headerCache.Remove(evict);
                    }
                }
            }

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                target.Source = bmp;
                target.IsVisible = true;
                fallback.IsVisible = false;
            });
        }
        catch
        {
            // Offline or app has no header image - fallback panel stays.
        }
    }

    // =====================================================================
    // Web views (Store / Community / News)
    // =====================================================================

    private void ShowWeb(string url, Border navButton)
    {
        StyleSidebarButton(_libraryButton,   ReferenceEquals(navButton, _libraryButton));
        StyleSidebarButton(_storeButton,     ReferenceEquals(navButton, _storeButton));
        StyleSidebarButton(_communityButton, ReferenceEquals(navButton, _communityButton));
        StyleSidebarButton(_newsButton,      ReferenceEquals(navButton, _newsButton));

        // Reuse the existing WebView when switching between Store / Community /
        // News - the user expects sign-in cookies and scroll position to
        // survive the tab change.
        if (_webView == null)
        {
            _webView = new WebViewWrapper();
            _contentHost.Child = _webView;
        }
        else if (!ReferenceEquals(_contentHost.Child, _webView))
        {
            _contentHost.Child = _webView;
        }

        _activeWebUrl = url;
        _webView.NavigateToUrl(url);
        _statusText.Text = $"Loading {url} ...";
    }

    private void DisposeActiveWebView()
    {
        if (_webView == null) return;
        try { _webView.Dispose(); } catch { /* best-effort */ }
        _webView = null;
    }

    // =====================================================================
    // Empty / missing-Steam fallbacks
    // =====================================================================

    private Control BuildSteamMissingNotice() => BuildCenteredCard(
        title: "Steam isn't installed",
        body: "DOSI couldn't find a Steam install on this machine. Install Steam from steampowered.com, sign in once, and reopen this app to see your library here.",
        actionText: "Open Steam website",
        action: () => ShowWeb("https://store.steampowered.com/about/", _storeButton));

    private Control BuildEmptyLibraryNotice() => BuildCenteredCard(
        title: "No installed games found",
        body: "Steam is installed, but DOSI didn't find any installed games in any of its library folders. Install something through Steam (or move an existing library back online) and click Refresh.",
        actionText: "Open Steam Store",
        action: () => ShowWeb("https://store.steampowered.com/", _storeButton));

    private Control BuildCenteredCard(string title, string body, string actionText, Action action)
    {
        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextPrimaryBrush,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var bodyText = new TextBlock
        {
            Text = body,
            FontSize = 13,
            Foreground = Accents.TextSecondaryBrush,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 460,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var actionBtn = new DOSIButton
        {
            Text = actionText,
            Margin = new Thickness(0, 20, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        actionBtn.Click += (_, _) => action();

        var stack = new StackPanel
        {
            Spacing = 0,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { titleText, bodyText, actionBtn }
        };

        return new Border
        {
            Padding = new Thickness(40),
            Child = stack,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
    }

    // =====================================================================
    // Steam library scan
    // =====================================================================
    //
    // Steam stores its install list in two layers on every platform:
    //   1. <SteamRoot>\steamapps\libraryfolders.vdf  - a small VDF file that
    //      lists each library root the user has configured (Steam supports
    //      multiple library locations: SSD + HDD, internal + external, etc.).
    //   2. <Library>\steamapps\appmanifest_<appid>.acf  - one tiny VDF per
    //      installed app, containing its appid, display name, install dir,
    //      and size on disk.
    //
    // Both formats are key/value text. We don't need a full VDF parser - a
    // pair of regexes on the recognized fields is enough to pull what the
    // tile grid needs and skip everything else.

    private static IEnumerable<SteamGame> ScanInstalledGames(string steamRoot)
    {
        // Dedupe by appid: a game's manifest can appear under multiple
        // discovered library folders (e.g. the Steam install root is itself
        // library "0" in libraryfolders.vdf, so without a guard every game
        // installed in the default location would show up twice). Keep
        // first-found wins - nothing here depends on which copy "wins"
        // because the manifests are identical.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var libraryRoot in EnumerateLibraryFolders(steamRoot))
        {
            var steamapps = IOPath.Combine(libraryRoot, "steamapps");
            if (!Directory.Exists(steamapps)) continue;

            string[] manifests;
            try { manifests = Directory.GetFiles(steamapps, "appmanifest_*.acf"); }
            catch { continue; }

            foreach (var manifest in manifests)
            {
                SteamGame? g = TryParseManifest(manifest);
                if (g == null) continue;
                if (!seen.Add(g.AppId)) continue;
                yield return g;
            }
        }
    }

    private static IEnumerable<string> EnumerateLibraryFolders(string steamRoot)
    {
        // Track which library roots we've already yielded so callers always
        // get a deduped sequence (the install root is also referenced from
        // libraryfolders.vdf, and the same library can be listed under both
        // the new and old VDF paths).
        var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (seenRoots.Add(NormalizePath(steamRoot)))
            yield return steamRoot;

        // libraryfolders.vdf moved between two locations across Steam
        // versions - we check both to stay compatible with old + new clients.
        string[] candidates =
        {
            IOPath.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
            IOPath.Combine(steamRoot, "config", "libraryfolders.vdf"),
        };

        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            string text;
            try { text = File.ReadAllText(path); }
            catch { continue; }

            foreach (Match m in Regex.Matches(text,
                "\"path\"\\s*\"([^\"]+)\"",
                RegexOptions.IgnoreCase))
            {
                var p = m.Groups[1].Value.Replace("\\\\", "\\");
                if (!Directory.Exists(p)) continue;
                if (seenRoots.Add(NormalizePath(p)))
                    yield return p;
            }
        }
    }

    /// <summary>Canonicalize a directory path for HashSet comparisons (full path, no trailing separator).</summary>
    private static string NormalizePath(string p)
    {
        try { return IOPath.GetFullPath(p).TrimEnd(IOPath.DirectorySeparatorChar, IOPath.AltDirectorySeparatorChar); }
        catch { return p; }
    }

    private static SteamGame? TryParseManifest(string manifestPath)
    {
        string text;
        try { text = File.ReadAllText(manifestPath); }
        catch { return null; }

        var appId = ReadVdfString(text, "appid");
        var name = ReadVdfString(text, "name");
        var sizeStr = ReadVdfString(text, "SizeOnDisk");

        if (string.IsNullOrEmpty(appId)) return null;
        if (string.IsNullOrEmpty(name)) name = $"App {appId}";

        // Size is best-effort: missing or malformed entries leave SizeOnDiskBytes at 0.
        _ = long.TryParse(sizeStr, out var sizeBytes);

        return new SteamGame
        {
            AppId = appId,
            Name = name,
            SizeOnDiskBytes = sizeBytes
        };
    }

    private static string ReadVdfString(string vdf, string key)
    {
        // VDF lines look like:  "name"		"Half-Life 2"
        // A double-quoted key, whitespace, a double-quoted value. Match just
        // that shape - good enough for the small set of fields we need.
        var m = Regex.Match(vdf,
            "\"" + Regex.Escape(key) + "\"\\s*\"([^\"]*)\"",
            RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : string.Empty;
    }

    private static string? ResolveSteamRoot()
    {
        // Windows: registry first (most reliable across non-default installs),
        // then well-known Program Files locations. macOS / Linux use the
        // standard user-profile-rooted locations.
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                var path = key?.GetValue("SteamPath") as string;
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) return path;

                using var key64 = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
                var install = key64?.GetValue("InstallPath") as string;
                if (!string.IsNullOrEmpty(install) && Directory.Exists(install)) return install;

                string[] fallbackWin =
                {
                    @"C:\Program Files (x86)\Steam",
                    @"C:\Program Files\Steam"
                };
                foreach (var p in fallbackWin)
                    if (Directory.Exists(p)) return p;
            }
            else if (OperatingSystem.IsMacOS())
            {
                var p = IOPath.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Application Support", "Steam");
                if (Directory.Exists(p)) return p;
            }
            else if (OperatingSystem.IsLinux())
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string[] linuxCandidates =
                {
                    IOPath.Combine(home, ".steam", "steam"),
                    IOPath.Combine(home, ".local", "share", "Steam"),
                    IOPath.Combine(home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam") // Flatpak
                };
                foreach (var p in linuxCandidates)
                    if (Directory.Exists(p)) return p;
            }
        }
        catch { /* registry / IO errors -> treat Steam as not installed */ }

        return null;
    }

    private string ResolveStatusLine()
    {
        return _steamRoot == null
            ? "Steam not detected"
            : $"Steam: {_steamRoot}";
    }

    // =====================================================================
    // Sidebar styling
    // =====================================================================

    private Border BuildSidebarButton(string label, bool isActive)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0)
        };

        var btn = new Border
        {
            Height = 36,
            CornerRadius = new CornerRadius(8),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = text
        };
        StyleSidebarButton(btn, isActive);

        // Hover wash sits between transparent (inactive) and accent (active).
        btn.PointerEntered += (_, _) =>
        {
            if (!IsActiveSidebarButton(btn))
                btn.Background = new SolidColorBrush(Color.FromArgb(28,
                    Accents.AccentPrimary.R, Accents.AccentPrimary.G, Accents.AccentPrimary.B));
        };
        btn.PointerExited += (_, _) =>
        {
            StyleSidebarButton(btn, IsActiveSidebarButton(btn));
        };
        return btn;
    }

    private bool IsActiveSidebarButton(Border btn) => ReferenceEquals(GetActiveSidebarButton(), btn);

    private void StyleSidebarButton(Border btn, bool isActive)
    {
        btn.Background = isActive
            ? new SolidColorBrush(Color.FromArgb(60,
                Accents.AccentPrimary.R, Accents.AccentPrimary.G, Accents.AccentPrimary.B))
            : Brushes.Transparent;
        btn.BorderBrush = isActive
            ? Accents.AccentPrimaryBrush
            : Brushes.Transparent;
        btn.BorderThickness = new Thickness(isActive ? 1 : 0);
        if (btn.Child is TextBlock t)
            t.Foreground = isActive ? Accents.AccentPrimaryBrush : Accents.TextPrimaryBrush;
    }

    // =====================================================================
    // App icon (taskbar / window chrome)
    // =====================================================================

    private static Control CreateAppIcon()
    {
        // Stylized "S" inside an accent-coloured circle - intentionally
        // generic so we don't ship Valve trademarks in the chrome.
        var bg = new Border
        {
            Width = 16,
            Height = 16,
            Background = Accents.AccentGradientBrush,
            CornerRadius = new CornerRadius(8)
        };
        var s = new TextBlock
        {
            Text = "S",
            FontFamily = DOSI.CORE.DOSIFonts.Mono,
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Accents.TextOnAccent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var grid = new Grid();
        grid.Children.Add(s);
        bg.Child = grid;
        return bg;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "—";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{v:0.#} {units[u]}";
    }

    /// <summary>Snapshot of one installed Steam game pulled from its appmanifest.</summary>
    private sealed class SteamGame
    {
        public string AppId { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public long SizeOnDiskBytes { get; init; }
    }
}
