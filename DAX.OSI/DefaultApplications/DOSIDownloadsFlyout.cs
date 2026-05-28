using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DAX.OSI.Controls;
using DOSI.CORE;
using DOSI.CORE.AccentManagement;
using DOSI.CORE.UIComponents;
using DOSI.CORE.UIComponents.WindowManagement;
using DOSI.CORE.UserManagement;
using IOPath = System.IO.Path;

namespace DAX.OSI.DefaultApplications;

/// <summary>
/// A compact, modern downloads flyout that visually mirrors the Edge / Chrome
/// download popover. Anchored under the browser toolbar's right edge, holds a
/// running list of in-flight and recently-completed downloads for the lifetime
/// of the host <see cref="DOSIWebBrowser"/> window.
///
/// <para>
/// The flyout owns the entire download pipeline that the JS bridge in
/// <see cref="WebViewWrapper"/> feeds it: it picks the destination folder
/// from <see cref="BrowserPreferences.DownloadFolder"/> (always rooted in the
/// signed-in user's <c>~/Downloads</c>), streams the bytes via
/// <see cref="HttpClient"/>, updates a per-row progress bar, and surfaces
/// completion / error states inline. No native save-as dialog is ever shown
/// and no file is ever written outside the user's own download bucket.
/// </para>
/// </summary>
internal sealed class DOSIDownloadsFlyout
{
    private static AccentManager Accents => AccentManager.Instance;

    // ---- Public surface ------------------------------------------------------

    /// <summary>The Avalonia control to drop into the browser's overlay
    /// grid. Always present in the visual tree; only its visibility flips.</summary>
    public Control Root => _root;

    /// <summary>Bell button hosted in the toolbar that toggles the flyout.
    /// Carries an accent-coloured badge while downloads are active.</summary>
    public Control ToolbarButton => _toolbarButton;

    // ---- Layout root ---------------------------------------------------------

    private readonly Border _root;
    private readonly Border _card;
    private readonly TranslateTransform _cardSlide;
    private readonly StackPanel _itemsStack;
    private readonly TextBlock _emptyState;
    private bool _isOpen;
    private DispatcherTimer? _animTimer;

    // ---- Accent-tracked controls (re-tinted on AccentChanged) ----------------
    // These references are stored so the live accent re-application pass can
    // walk a known set of controls without a brittle visual-tree search.
    // Anything that uses an accent-derived colour at build time also lives
    // in this list so the picker tile, badge, progress bars, and divider all
    // re-tint instantly when the user picks a new DOSI accent.
    private readonly TextBlock _headerTitle;
    private readonly Border _headerGlyph;
    private readonly TextBlock _headerGlyphText;
    private readonly Border _divider;
    private EventHandler? _accentHandler;

    // ---- Toolbar button ------------------------------------------------------

    private readonly Border _toolbarButton;
    private readonly Border _toolbarBadge;

    // ---- State ---------------------------------------------------------------

    private readonly HttpClient _http;
    // FIFO of in-flight + completed entries. New downloads append at the
    // bottom so the most recent download is always visible without
    // scrolling - same UX as the Edge / Chrome flyout.
    private readonly List<DownloadEntry> _entries = new();
    private int _activeCount;

    public DOSIDownloadsFlyout()
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (DOSI Browser) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36");

        // ---- Empty-state placeholder shown when the list is, well, empty.
        _emptyState = new TextBlock
        {
            Text = "No downloads yet",
            FontSize = 12,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.65,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 24, 0, 24)
        };

        _itemsStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 2,
            Children = { _emptyState }
        };

        var scroller = new DOSIScrollViewer
        {
            Content = _itemsStack,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            MaxHeight = 280
        };

        // ---- Header (accent chip + title + action icons) -----------------
        // Small rounded accent chip carrying the download glyph mirrors the
        // notification popover's header styling - one of the easy wins for
        // "feels like a DOSI surface" rather than a generic dark card.
        _headerGlyphText = new TextBlock
        {
            Text = "\u2913", // downwards arrow to bar - "download" pictogram
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Accents.TextOnAccent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _headerGlyph = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(6),
            Background = Accents.AccentGradientBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Child = _headerGlyphText
        };

        _headerTitle = new TextBlock
        {
            Text = "Downloads",
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center
        };

        var titleStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _headerGlyph, _headerTitle }
        };

        var openFolderBtn = BuildHeaderIcon("\uD83D\uDCC1", "Open downloads folder");
        openFolderBtn.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            OpenDownloadsFolder();
        };

        // Trash glyph - clears completed entries (kept distinct from Close
        // so the user can wipe the list without dismissing the popover).
        var clearBtn = BuildHeaderIcon("\uD83D\uDDD1", "Clear completed");
        clearBtn.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            ClearCompleted();
        };

        // Close - the actual dismiss affordance. Previously the X glyph in
        // this slot called ClearCompleted, which was both confusing and the
        // reason there was no visible "close" button at all. Now Close
        // dispatches Close() so clicking the X dismisses the popover and
        // releases the WebView pause in one go.
        var closeBtn = BuildHeaderIcon("\u2715", "Close");
        closeBtn.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            Close();
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { openFolderBtn, clearBtn, closeBtn }
        };

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(14, 10, 8, 8)
        };
        header.Children.Add(titleStack); Grid.SetColumn(titleStack, 0);
        header.Children.Add(actions); Grid.SetColumn(actions, 1);

        // Accent-tinted divider - matches the gradient strip the notification
        // popover uses to separate header from list. Built fresh on each
        // AccentChanged so the live tint follows the user's accent.
        _divider = new Border
        {
            Height = 1,
            Background = BuildDividerBrush(),
            Margin = new Thickness(10, 0, 10, 6)
        };

        var cardBody = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Children = { header, _divider, scroller }
        };

        _cardSlide = new TranslateTransform(0, -8);

        _card = new Border
        {
            Width = 340,
            CornerRadius = new CornerRadius(10),
            Background = BuildCardBackground(),
            // Accent-tinted border so the popover visually belongs to the
            // active accent rather than reading as a generic dark card.
            BorderBrush = new SolidColorBrush(Accents.AccentSecondary),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0,
                OffsetY = 12,
                Blur = 28,
                Spread = 0,
                Color = Color.FromArgb(170, 0, 0, 0)
            }),
            Child = cardBody,
            RenderTransform = _cardSlide,
            RenderTransformOrigin = new RelativePoint(1, 0, RelativeUnit.Relative),
            Opacity = 0,
            IsVisible = false,
            // Anchor under the toolbar's right edge, matching Edge's flyout.
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 12, 0)
        };

        // ---- Outer hit-catching root. Click outside the card -> close.
        _root = new Border
        {
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false, // becomes true only while open
            IsVisible = false,
            Child = new Grid { Children = { _card } }
        };
        _root.PointerPressed += (_, e) =>
        {
            // Walk the source ancestry up to the card - if the press never
            // touched the card (or any descendant), treat it as an outside
            // click and dismiss. Using the source walk instead of an
            // _card.Bounds geometry probe avoids two long-standing bugs:
            //   * Bounds is empty (0x0) for one frame after the card first
            //     becomes visible, which would let the very first click on
            //     the card itself slip through and close the popover.
            //   * Bounds is the card's RENDER rect, which doesn't include
            //     the drop shadow region - clicks landing on the shadow
            //     halo would dismiss even though they visually look like
            //     they're on the card edge.
            var src = e.Source as Visual;
            while (src != null)
            {
                if (ReferenceEquals(src, _card)) return; // hit inside card
                src = src.GetVisualParent();
            }
            Close();
        };

        // Hook accent changes whenever the flyout root is attached to the
        // visual tree, so the popover's accent-derived surfaces (card border,
        // header chip, divider gradient, badge, progress bars on live rows)
        // re-tint the instant the user picks a new accent - even while the
        // popover is open. Detached when the host browser closes so the
        // static AccentManager doesn't keep a reference to a dead instance.
        _accentHandler = (_, _) => Dispatcher.UIThread.Post(ApplyAccent);
        _root.AttachedToVisualTree += (_, _) =>
        {
            Accents.AccentChanged += _accentHandler;
            ApplyAccent();
        };
        _root.DetachedFromVisualTree += (_, _) =>
        {
            Accents.AccentChanged -= _accentHandler;
        };

        // ---- Toolbar button (download glyph + badge) -------------------
        var glyph = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M12 3 V15 M6 11 L12 17 L18 11 M4 21 H20"),
            Stroke = Accents.TextPrimaryBrush,
            StrokeThickness = 1.5,
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        _toolbarBadge = new Border
        {
            Width = 7,
            Height = 7,
            CornerRadius = new CornerRadius(3.5),
            Background = Accents.AccentGradientBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 2, 0),
            IsVisible = false
        };

        var btnContent = new Grid
        {
            Width = 22,
            Height = 22,
            Children = { glyph, _toolbarBadge }
        };

        _toolbarButton = new Border
        {
            Width = 32,
            Height = 28,
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
            Child = btnContent
        };
        _toolbarButton.PointerEntered += (_, _) =>
        {
            if (!_isOpen)
                _toolbarButton.Background = new SolidColorBrush(Color.FromArgb(
                    35, Accents.AccentPrimary.R, Accents.AccentPrimary.G, Accents.AccentPrimary.B));
        };
        _toolbarButton.PointerExited += (_, _) =>
        {
            if (!_isOpen) _toolbarButton.Background = Brushes.Transparent;
        };
        _toolbarButton.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            Toggle();
        };
    }

    private static Border BuildHeaderIcon(string glyph, string tooltip)
    {
        var label = new TextBlock
        {
            Text = glyph,
            FontSize = 13,
            Foreground = Accents.TextPrimaryBrush,
            Opacity = 0.85,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var border = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = label
        };
        ToolTip.SetTip(border, tooltip);
        // Accent-tinted hover wash (matches the apps-menu / notification
        // popover header icons so all dropdown chrome feels unified).
        border.PointerEntered += (_, _) =>
        {
            border.Background = new SolidColorBrush(Color.FromArgb(
                50, Accents.AccentPrimary.R, Accents.AccentPrimary.G, Accents.AccentPrimary.B));
            label.Opacity = 1;
        };
        border.PointerExited += (_, _) =>
        {
            border.Background = Brushes.Transparent;
            label.Opacity = 0.85;
        };
        border.PointerPressed += (_, _) =>
        {
            border.Background = new SolidColorBrush(Color.FromArgb(
                90, Accents.AccentPrimary.R, Accents.AccentPrimary.G, Accents.AccentPrimary.B));
        };
        return border;
    }

    // =====================================================================
    // Public API used by DOSIWebBrowser
    // =====================================================================

    /// <summary>
    /// Entry point called by <see cref="DOSIWebBrowser"/> when the WebView's
    /// JS bridge flags a click as a download. Opens the flyout (so the user
    /// sees the new entry land), appends a row, and kicks off the async
    /// fetch. Safe to call from any thread.
    /// </summary>
    public void BeginDownload(WebViewDownloadRequestedEventArgs e)
    {
        Dispatcher.UIThread.Post(() => StartDownloadCore(e));
    }

    public void Toggle()
    {
        if (_isOpen) Close();
        else Open();
    }

    public void Open()
    {
        if (_isOpen) return;
        _isOpen = true;
        _toolbarButton.Background = new SolidColorBrush(Color.FromArgb(
            50, Accents.AccentPrimary.R, Accents.AccentPrimary.G, Accents.AccentPrimary.B));
        _root.IsHitTestVisible = true;
        _root.IsVisible = true;
        _card.IsVisible = true;
        // Bring the root to the TOP of its parent's z-order so a host that
        // added other overlay layers after construction (e.g. a sibling
        // tooltip or modal) can't end up rendered above us. Idempotent if
        // we're already on top.
        BringRootToTop();
        // Hide every WebView surface so the OS-level browser composition
        // doesn't paint OVER our flyout (the classic "airspace" problem).
        // SetAllPaused snapshots the per-wrapper visibility and restores it
        // on the matching SetAllPaused(false) - so a navigation that hides
        // the WebView for a moment doesn't end up shown again behind us.
        WebViewWrapper.SetAllPaused(true);
        Animate(opening: true);
    }

    public void Close()
    {
        if (!_isOpen) return;
        _isOpen = false;
        _toolbarButton.Background = Brushes.Transparent;
        WebViewWrapper.SetAllPaused(false);
        Animate(opening: false);
    }

    /// <summary>
    /// Re-parents <see cref="_root"/> to the end of its parent's children
    /// list so it renders ABOVE every sibling. Avalonia uses document order
    /// for z-ranking inside a Grid / Panel, and the host browser's overlay
    /// grid is built in a fixed order at construction time - but if any
    /// future addition to the overlay (status toast, modal scrim, etc.)
    /// gets appended after the flyout root, it would visually obscure the
    /// popover. Doing this on every open is cheap (no-op when already last)
    /// and guarantees the contract "the downloads popover is always the
    /// topmost in-window layer".
    /// </summary>
    private void BringRootToTop()
    {
        if (_root.Parent is Panel parent)
        {
            var children = parent.Children;
            if (children.Count > 0 && !ReferenceEquals(children[children.Count - 1], _root))
            {
                children.Remove(_root);
                children.Add(_root);
            }
        }
    }

    // =====================================================================
    // Internals
    // =====================================================================

    /// <summary>
    /// Re-applies the current DOSI accent to every accent-aware surface on
    /// the flyout. Called once on attach (so the popover always boots in
    /// the live accent) and on every <c>AccentChanged</c> notification so a
    /// flip mid-session immediately propagates - header chip, card border,
    /// divider gradient, badge, toolbar button fill, and every live row's
    /// file-icon chip + progress bar all re-tint in one pass.
    /// </summary>
    private void ApplyAccent()
    {
        _card.Background = BuildCardBackground();
        _card.BorderBrush = new SolidColorBrush(Accents.AccentSecondary);
        _headerGlyph.Background = Accents.AccentGradientBrush;
        _headerGlyphText.Foreground = new SolidColorBrush(Accents.TextOnAccent);
        _headerTitle.Foreground = Accents.TextPrimaryBrush;
        _divider.Background = BuildDividerBrush();
        _toolbarBadge.Background = Accents.AccentGradientBrush;
        _emptyState.Foreground = Accents.TextSecondaryBrush;

        // If the popover happens to be open while the accent flips, also
        // refresh the toolbar button's open-state highlight so it stays in
        // the new accent (closed state is transparent, so no work needed).
        if (_isOpen)
        {
            _toolbarButton.Background = new SolidColorBrush(Color.FromArgb(
                50, Accents.AccentPrimary.R, Accents.AccentPrimary.G, Accents.AccentPrimary.B));
        }

        // Re-tint every live row: file-icon chip + (active) progress fill
        // pick up the new accent. Failed rows keep their red so the error
        // state remains visually distinct from a regular accent-coloured row.
        foreach (var entry in _entries)
        {
            if (entry.IconChip != null)
                entry.IconChip.Background = Accents.AccentGradientBrush;
            if (entry.IconText != null)
                entry.IconText.Foreground = new SolidColorBrush(Accents.TextOnAccent);
            if (entry.State != DownloadState.Failed && entry.ProgressFill != null)
                entry.ProgressFill.Background = Accents.AccentGradientBrush;
            if (entry.NameText != null)
                entry.NameText.Foreground = Accents.TextPrimaryBrush;
        }
    }

    /// <summary>
    /// Builds the popover card's background brush. Mirrors the deep two-stop
    /// gradient used by the notification popover so both popovers feel like
    /// the same family of surface, while staying dark enough that the
    /// accent-coloured chip and badge pop visually against it.
    /// </summary>
    private static IBrush BuildCardBackground()
    {
        // Light accent gets a light card so the (dark) TextPrimaryBrush
        // labels stay readable - same pattern DesktopScreen uses for the
        // taskbar / apps-menu surfaces under the Light accent.
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
                new GradientStop(Color.FromArgb(245, 22, 24, 32), 0),
                new GradientStop(Color.FromArgb(238, 14, 16, 24), 1)
            }
        };
    }

    /// <summary>
    /// Accent-tinted gradient strip used as the divider between the popover
    /// header and the list. Reuses the recipe from the notification popover
    /// for visual consistency across DOSI dropdowns.
    /// </summary>
    private static IBrush BuildDividerBrush()
    {
        var a = Accents.AccentPrimary;
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 0),
                new GradientStop(Color.FromArgb(70, a.R, a.G, a.B), 0.5),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 1)
            }
        };
    }

    private void Animate(bool opening)
    {
        const double duration = 160;
        var startOpacity = _card.Opacity;
        var targetOpacity = opening ? 1.0 : 0.0;
        var startY = _cardSlide.Y;
        var targetY = opening ? 0.0 : -8.0;
        var startTime = DateTime.UtcNow;

        _animTimer?.Stop();
        _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _animTimer.Tick += (_, _) =>
        {
            var t = Math.Clamp((DateTime.UtcNow - startTime).TotalMilliseconds / duration, 0d, 1d);
            var eased = opening ? 1 - Math.Pow(1 - t, 3) : t * t;
            _card.Opacity = startOpacity + (targetOpacity - startOpacity) * eased;
            _cardSlide.Y = startY + (targetY - startY) * eased;
            if (t >= 1d)
            {
                _animTimer?.Stop();
                _animTimer = null;
                if (!opening)
                {
                    _card.IsVisible = false;
                    _root.IsVisible = false;
                    _root.IsHitTestVisible = false;
                }
            }
        };
        _animTimer.Start();
    }

    private string ResolveDownloadFolder()
    {
        // Priority order:
        //   1. BrowserPreferences.DownloadFolder (per-user, user-editable
        //      in Settings, already auto-rooted to <UserHome>/Downloads on
        //      sign-in by OnCurrentUserChanged).
        //   2. UserManager.GetUserSubfolder(user, "Downloads") if signed in
        //      but the pref is somehow empty / pointing at a missing dir.
        //   3. Environment.SpecialFolder.UserProfile + /Downloads as a last
        //      resort so a not-yet-signed-in test session still works.
        //
        // Result is guaranteed to exist on disk (Directory.CreateDirectory)
        // so callers don't need to defend against a missing destination.
        try
        {
            var prefs = BrowserPreferences.Current;
            var configured = prefs?.DownloadFolder;
            if (!string.IsNullOrWhiteSpace(configured))
            {
                Directory.CreateDirectory(configured);
                return configured;
            }
        }
        catch { /* fall through */ }

        var user = UserManager.CurrentUser;
        if (user != null)
        {
            try
            {
                UserManager.EnsureUserSubfolders(user);
                var dir = UserManager.GetUserSubfolder(user, "Downloads");
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch { /* fall through */ }
        }

        var fallback = IOPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static string ChooseUniqueDestination(string desired)
    {
        if (!File.Exists(desired)) return desired;
        var dir = IOPath.GetDirectoryName(desired) ?? "";
        var stem = IOPath.GetFileNameWithoutExtension(desired);
        var ext = IOPath.GetExtension(desired);
        for (int i = 2; i < 10000; i++)
        {
            var candidate = IOPath.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return desired;
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "download";
        foreach (var c in IOPath.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        // Strip query strings / fragments that snuck in through the URL
        // fallback path (the JS layer already does this, but defence-in-depth).
        var q = name.IndexOf('?');
        if (q > 0) name = name.Substring(0, q);
        var h = name.IndexOf('#');
        if (h > 0) name = name.Substring(0, h);
        return string.IsNullOrWhiteSpace(name) ? "download" : name.Trim();
    }

    private void StartDownloadCore(WebViewDownloadRequestedEventArgs e)
    {
        var folder = ResolveDownloadFolder();
        var sanitized = SanitizeFileName(e.SuggestedFileName);
        var finalPath = ChooseUniqueDestination(IOPath.Combine(folder, sanitized));

        var entry = new DownloadEntry(e.Url, finalPath);
        _entries.Add(entry);

        if (_itemsStack.Children.Contains(_emptyState))
            _itemsStack.Children.Remove(_emptyState);

        var row = BuildRow(entry);
        entry.Row = row;
        // New entry at the TOP for parity with the Edge flyout.
        _itemsStack.Children.Insert(0, row);

        Interlocked.Increment(ref _activeCount);
        UpdateBadge();
        // Open() is idempotent, but we also need to re-assert the WebView
        // pause when the popover is ALREADY open: if a previous BeginDownload
        // opened the flyout, then the WebView fired a navigation that
        // briefly re-showed itself (some renderer backends ignore the
        // SetVisible flip during navigation transitions), the next download
        // arriving while we're "open" would otherwise let the WebView keep
        // painting over us. Force-pause every time.
        if (_isOpen)
        {
            WebViewWrapper.SetAllPaused(true);
            BringRootToTop();
        }
        else
        {
            Open();
        }

        // Fire-and-forget the network work. The continuation marshals every
        // UI mutation back to the dispatcher.
        _ = Task.Run(async () =>
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, e.Url);
                if (!string.IsNullOrEmpty(e.Referer))
                {
                    try { req.Headers.Referrer = new Uri(e.Referer); } catch { }
                }
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                // If the server hands back a Content-Disposition filename,
                // prefer it over the URL-derived guess (matches what every
                // other browser does).
                var serverName = resp.Content.Headers.ContentDisposition?.FileNameStar
                              ?? resp.Content.Headers.ContentDisposition?.FileName;
                if (!string.IsNullOrWhiteSpace(serverName))
                {
                    var cleaned = SanitizeFileName(serverName.Trim('"'));
                    var swapped = ChooseUniqueDestination(IOPath.Combine(folder, cleaned));
                    entry.DestinationPath = swapped;
                    Dispatcher.UIThread.Post(() => entry.NameText.Text = IOPath.GetFileName(swapped));
                }

                long? total = resp.Content.Headers.ContentLength;
                using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var dst = File.Create(entry.DestinationPath);
                var buffer = new byte[81920];
                long received = 0;
                int read;
                var lastUiPush = DateTime.UtcNow;
                while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                    received += read;

                    // Throttle UI updates to ~30 Hz - more than that just
                    // burns dispatcher time without any visual benefit.
                    if ((DateTime.UtcNow - lastUiPush).TotalMilliseconds > 33)
                    {
                        lastUiPush = DateTime.UtcNow;
                        var capturedReceived = received;
                        var capturedTotal = total;
                        Dispatcher.UIThread.Post(() => UpdateRowProgress(entry, capturedReceived, capturedTotal));
                    }
                }

                Dispatcher.UIThread.Post(() =>
                {
                    UpdateRowProgress(entry, received, total);
                    MarkRowComplete(entry);
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => MarkRowFailed(entry, ex.Message));
            }
            finally
            {
                Interlocked.Decrement(ref _activeCount);
                Dispatcher.UIThread.Post(UpdateBadge);
            }
        });
    }

    private static Border BuildRow(DownloadEntry entry)
    {
        // File-icon glyph - a tinted document with the extension in the
        // bottom-right corner. Cheap, accent-aware, and resolution-
        // independent (no bitmaps to load per row).
        var ext = (IOPath.GetExtension(entry.DestinationPath) ?? "").TrimStart('.').ToUpperInvariant();
        if (ext.Length > 4) ext = ext.Substring(0, 4);

        var iconText = new TextBlock
        {
            Text = string.IsNullOrEmpty(ext) ? "FILE" : ext,
            FontSize = ext.Length >= 4 ? 8 : 9,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Accents.TextOnAccent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var iconBg = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(5),
            Background = Accents.AccentGradientBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Child = iconText
        };
        entry.IconChip = iconBg;
        entry.IconText = iconText;

        var nameText = new TextBlock
        {
            Text = IOPath.GetFileName(entry.DestinationPath),
            FontSize = 12.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextPrimaryBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        entry.NameText = nameText;

        var statusText = new TextBlock
        {
            Text = "Starting\u2026",
            FontSize = 10.5,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.8,
            Margin = new Thickness(0, 2, 0, 0)
        };
        entry.StatusText = statusText;

        // Hairline accent-coloured progress bar that runs along the bottom
        // of the row (cleaner than a tall standalone bar, and the row
        // already has the file metadata above it).
        var progressTrack = new Border
        {
            Height = 3,
            CornerRadius = new CornerRadius(1.5),
            Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var progressFill = new Border
        {
            Height = 3,
            CornerRadius = new CornerRadius(1.5),
            Background = Accents.AccentGradientBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0
        };
        var progressGrid = new Grid { Children = { progressTrack, progressFill } };
        entry.ProgressFill = progressFill;
        entry.ProgressTrack = progressTrack;

        var textStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { nameText, statusText, progressGrid }
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            VerticalAlignment = VerticalAlignment.Center
        };
        grid.Children.Add(iconBg); Grid.SetColumn(iconBg, 0);
        grid.Children.Add(textStack); Grid.SetColumn(textStack, 1);

        var row = new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = grid
        };
        row.PointerEntered += (_, _) =>
            row.Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
        row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
        row.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            if (entry.State == DownloadState.Complete)
                TryOpenFile(entry.DestinationPath);
        };

        return row;
    }

    private static void UpdateRowProgress(DownloadEntry entry, long received, long? total)
    {
        if (entry.ProgressFill == null || entry.ProgressTrack == null || entry.StatusText == null) return;
        if (total is long t && t > 0)
        {
            var ratio = Math.Clamp((double)received / t, 0d, 1d);
            entry.ProgressFill.Width = entry.ProgressTrack.Bounds.Width * ratio;
            entry.StatusText.Text = $"{FormatBytes(received)} of {FormatBytes(t)}";
        }
        else
        {
            // Unknown content length - indeterminate state, no bar but a
            // running byte counter so the user sees progress.
            entry.ProgressFill.Width = 0;
            entry.StatusText.Text = $"{FormatBytes(received)} received";
        }
    }

    private static void MarkRowComplete(DownloadEntry entry)
    {
        entry.State = DownloadState.Complete;
        if (entry.StatusText != null) entry.StatusText.Text = "Open file";
        if (entry.ProgressFill != null && entry.ProgressTrack != null)
            entry.ProgressFill.Width = entry.ProgressTrack.Bounds.Width;
    }

    private static void MarkRowFailed(DownloadEntry entry, string reason)
    {
        entry.State = DownloadState.Failed;
        if (entry.StatusText != null)
        {
            entry.StatusText.Text = $"Failed - {reason}";
            entry.StatusText.Foreground = new SolidColorBrush(Color.FromRgb(232, 110, 110));
        }
        if (entry.ProgressFill != null)
            entry.ProgressFill.Background = new SolidColorBrush(Color.FromRgb(232, 90, 90));
    }

    private void UpdateBadge()
    {
        var active = Interlocked.CompareExchange(ref _activeCount, 0, 0);
        _toolbarBadge.IsVisible = active > 0;
    }

    private void ClearCompleted()
    {
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            var e = _entries[i];
            if (e.State == DownloadState.Complete || e.State == DownloadState.Failed)
            {
                if (e.Row != null && _itemsStack.Children.Contains(e.Row))
                    _itemsStack.Children.Remove(e.Row);
                _entries.RemoveAt(i);
            }
        }
        if (_entries.Count == 0 && !_itemsStack.Children.Contains(_emptyState))
            _itemsStack.Children.Add(_emptyState);
    }

    private void OpenDownloadsFolder()
    {
        var dir = ResolveDownloadFolder();
        try
        {
            var wm = WindowManager.Instance;
            if (wm != null)
            {
                var explorer = new DOSIFileExplorer();
                explorer.RequestNavigate(dir);
                wm.OpenWindow(explorer);
            }
        }
        catch { /* best effort */ }
    }

    private static void TryOpenFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { /* tolerate - the user can still find it in Files */ }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double v = bytes / 1024d;
        if (v < 1024) return $"{v:0.#} KB";
        v /= 1024;
        if (v < 1024) return $"{v:0.#} MB";
        v /= 1024;
        return $"{v:0.##} GB";
    }

    // =====================================================================
    // Per-row state
    // =====================================================================

    private enum DownloadState { Active, Complete, Failed }

    private sealed class DownloadEntry
    {
        public string Url { get; }
        public string DestinationPath { get; set; }
        public DownloadState State { get; set; } = DownloadState.Active;
        public Border? Row { get; set; }
        public TextBlock NameText { get; set; } = null!;
        public TextBlock? StatusText { get; set; }
        public Border? ProgressFill { get; set; }
        public Border? ProgressTrack { get; set; }
        // Accent-aware surfaces tracked on the entry so ApplyAccent can
        // re-tint a live row's file-icon chip + label without re-walking
        // the row's visual tree on every accent flip.
        public Border? IconChip { get; set; }
        public TextBlock? IconText { get; set; }

        public DownloadEntry(string url, string destinationPath)
        {
            Url = url;
            DestinationPath = destinationPath;
        }
    }
}
