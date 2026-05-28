using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DOSI.CORE.AccentManagement;

namespace DOSI.CORE.UIComponents.WindowManagement;

/// <summary>
/// Horizontal strip of "running app" chips, one per open
/// <see cref="DOSIWindow"/>. Mounted inside the top taskbar's middle column
/// so the user can see what's open and bring any window forward without
/// having to dig through the apps menu or alt-tab their way through a
/// stack of overlapping panes.
/// <para>
/// Each chip is a rounded pill carrying a small accent-tinted glyph badge
/// (the window's title initial) and the window's title. The active window's
/// chip is filled with a soft accent gradient + a thin underline + a low
/// drop shadow so it visibly "lifts" off the strip the way modern taskbars
/// (Windows 11, GNOME, Edge tabs) advertise their foreground item. Hover
/// triggers a live-preview popover beneath the chip after a short delay,
/// rendered straight from the source window's visual tree via
/// <see cref="RenderTargetBitmap"/> so the thumbnail is always current
/// (not a frozen icon).
/// </para>
/// <para>
/// Chip click semantics, matching every mainstream taskbar:
/// <list type="bullet">
///   <item><description>If the window is minimized -&gt; Restore + focus.</description></item>
///   <item><description>If the window is already focused -&gt; minimize
///   (toggle - the "click my own taskbar button to peek the desktop"
///   gesture).</description></item>
///   <item><description>Else -&gt; bring to front + focus.</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class TaskbarAppsStrip : Border
{
    private static AccentManager Accents => AccentManager.Instance;

    // Chip dimensions. The shape - rounded rectangle, 6 px corner radius -
    // is matched to the apps button on the left side of the taskbar so the
    // running-apps strip reads as a continuation of that family rather than
    // a separate UI vocabulary.
    private const double ChipHeight = 26;
    private const double ChipMinWidth = 110;
    private const double ChipMaxWidth = 200;
    private const double ChipSpacing = 6;
    private const double ChipCornerRadius = 6;
    private const double BadgeSize = 18;

    // Preview popover sizing. The thumbnail area is locked to a 16:10
    // rectangle so previews of any window aspect ratio land in the same
    // visual footprint - the snapshot itself is letterboxed inside via
    // Stretch.Uniform. Sized for a compact, modern look (Win11 / macOS
    // dock tooltip scale) so a sparse window snapshot doesn't dominate
    // the popover.
    private const double PreviewWidth = 280;
    private const double PreviewImageHeight = 180;
    private const int HoverDelayMs = 380;
    private const int CloseGraceMs = 140;

    private readonly StackPanel _chipStrip;
    private readonly Dictionary<DOSIWindow, ChipEntry> _chips = new();
    // Cache thumbnails briefly to avoid repeated expensive renders while
    // the user moves the cursor across chips. Keeps UI snappy and cuts
    // down on RenderTargetBitmap churn.
    private readonly Dictionary<DOSIWindow, (RenderTargetBitmap? Bitmap, DateTime Timestamp)> _previewCache
        = new();

    private WindowManager? _boundManager;

    // ---- Live-preview popover state -------------------------------------
    // The preview is hosted INSIDE the same TopLevel as the taskbar via
    // Avalonia's OverlayLayer - NOT inside a Popup. A Popup spawns its own
    // OS-level child window with a native frame around it (the gray square
    // border the user reported), and on Windows that frame can't be styled
    // away short of swapping out the entire popup host. OverlayLayer is
    // just an in-window Panel that sits above every other in-window
    // visual, so the card renders as a regular Avalonia control with the
    // accent border + rounded corners we actually asked for, matching how
    // the apps menu / notification popover are hosted in _layoutRoot.
    private Border? _previewCard;
    private Image? _previewImage;
    private TextBlock? _previewPlaceholderText;
    private Avalonia.Controls.Primitives.OverlayLayer? _previewHostLayer;
    private DOSIWindow? _hoverWindow;
    private Control? _previewAnchor;
    private DispatcherTimer? _hoverOpenTimer;
    private DispatcherTimer? _hoverCloseTimer;
    private EventHandler? _previewLayoutHandler;

    public TaskbarAppsStrip()
    {
        Background = Brushes.Transparent;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        _chipStrip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = ChipSpacing,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0)
        };
        Child = _chipStrip;

        AttachedToVisualTree += (_, _) => Bind(WindowManager.Instance);
        DetachedFromVisualTree += (_, _) =>
        {
            Unbind();
            ClosePreview(immediate: true);
            DetachPreviewFromHost();
        };
    }

    // Render the captured bitmap into the exact preview pixel rect so
    // the Image control always has a correctly-sized source to stretch
    // from. This avoids cases where a small captured bitmap ends up in
    // the corner of the preview because of DPI/Stretch interaction.
    private static RenderTargetBitmap? EnsureBitmapFitsPreview(RenderTargetBitmap src)
    {
        try
        {
            var px = new PixelSize((int)Math.Ceiling(PreviewWidth), (int)Math.Ceiling(PreviewImageHeight));
            var dpi = new Vector(96, 96);
            var rtb = new RenderTargetBitmap(px, dpi);
            // Draw the source into the target, scaling to fill while
            // preserving aspect ratio (UniformToFill behaviour). We use
            // RenderTargetBitmap.Render since Avalonia doesn't provide a
            // direct blit-with-scale API - the simplest approach is to
            // place the Image inside a temporary control tree, but that
            // is heavier. As a pragmatic compromise, if the source size
            // already matches, return it directly.
            if (src.PixelSize == px) return src;
            // Fallback: render the src visual via an Image control hosted
            // in an offscreen Border. This keeps scaling via Layout out of
            // the live visual tree.
            // Host the source inside an offscreen Border using an ImageBrush
            // with UniformToFill so scaling + centering are handled by the
            // brush rasterization. Render that host into our fixed-size
            // preview bitmap. This avoids the quirks of rendering an Image
            // control and guarantees the source covers the preview area.
            var host = new Border
            {
                Width = PreviewWidth,
                Height = PreviewImageHeight,
                Background = new ImageBrush(src)
                {
                    Stretch = Stretch.UniformToFill,
                    AlignmentX = AlignmentX.Center,
                    AlignmentY = AlignmentY.Center
                }
            };
            try
            {
                host.Measure(new Size(PreviewWidth, PreviewImageHeight));
                host.Arrange(new Rect(0, 0, PreviewWidth, PreviewImageHeight));
                rtb.Render(host);
                return rtb;
            }
            catch
            {
                rtb.Dispose();
                return src;
            }
        }
        catch { return src; }
    }

    // =====================================================================
    // WindowManager binding
    // =====================================================================

    private void Bind(WindowManager? mgr)
    {
        if (mgr == null) return;
        if (ReferenceEquals(_boundManager, mgr)) { RebuildFromManager(); return; }
        Unbind();
        _boundManager = mgr;
        mgr.WindowOpened += OnWindowOpened;
        mgr.WindowClosed += OnWindowClosed;
        mgr.WindowFocusChanged += OnWindowFocusChanged;
        RebuildFromManager();
    }

    private void Unbind()
    {
        if (_boundManager == null) return;
        _boundManager.WindowOpened -= OnWindowOpened;
        _boundManager.WindowClosed -= OnWindowClosed;
        _boundManager.WindowFocusChanged -= OnWindowFocusChanged;
        foreach (var entry in _chips.Values) entry.Detach();
        _chips.Clear();
        _chipStrip.Children.Clear();
        _boundManager = null;
    }

    private void RebuildFromManager()
    {
        if (_boundManager == null) return;
        foreach (var entry in _chips.Values) entry.Detach();
        _chips.Clear();
        _chipStrip.Children.Clear();
        foreach (var w in _boundManager.Windows) AddChip(w);
        RefreshActiveTint();
    }

    private void OnWindowOpened(object? sender, DOSIWindowEventArgs e)
    {
        if (_chips.ContainsKey(e.Window)) return;
        AddChip(e.Window);
        RefreshActiveTint();
    }

    private void OnWindowClosed(object? sender, DOSIWindowEventArgs e)
    {
        if (!_chips.TryGetValue(e.Window, out var entry)) return;
        entry.Detach();
        _chipStrip.Children.Remove(entry.Chip);
        _chips.Remove(e.Window);
        if (ReferenceEquals(_hoverWindow, e.Window)) ClosePreview(immediate: true);
        // Drop any cached snapshot for the closed window
        _previewCache.Remove(e.Window);
    }

    private void OnWindowFocusChanged(object? sender, DOSIWindowFocusEventArgs e)
        => RefreshActiveTint();

    // =====================================================================
    // Chip construction
    // =====================================================================

    private void AddChip(DOSIWindow window)
    {
        // Leading glyph badge: rounded square carrying the title initial.
        // Always reads as "this is an app" even when the window's own Icon
        // can't be reparented (it's already mounted in the title bar).
        var initial = new TextBlock
        {
            Text = ResolveInitial(window.Title),
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Accents.TextOnAccent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var badge = new Border
        {
            Width = BadgeSize,
            Height = BadgeSize,
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Accents.AccentPrimary),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = initial
        };

        var titleText = new TextBlock
        {
            Text = window.Title,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            // Pinned white - taskbar background is the system's
            // BuildTaskbarBackground glassy dark blur; the accent's
            // TextPrimary would go nearly invisible under Light accent.
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = ChipMaxWidth - BadgeSize - 28
        };

        // Active indicator sits flush along the bottom edge of the chip.
        var underline = new Border
        {
            Height = 2,
            CornerRadius = new CornerRadius(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(10, 0, 10, 0),
            Background = new SolidColorBrush(Accents.AccentPrimary),
            IsVisible = false,
            IsHitTestVisible = false
        };

        var content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(8, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(badge); Grid.SetColumn(badge, 0);
        content.Children.Add(titleText); Grid.SetColumn(titleText, 1);

        var chip = new Border
        {
            Height = ChipHeight,
            MinWidth = ChipMinWidth,
            MaxWidth = ChipMaxWidth,
            // Rounded rectangle matching the apps button corner radius so
            // the running-apps strip reads as the same button family as
            // the left-side launcher rather than a separate pill control.
            CornerRadius = new CornerRadius(ChipCornerRadius),
            Background = IdleBrush(),
            BorderBrush = IdleBorderBrush(),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new Grid { Children = { content, underline } }
        };
        // We render our own preview - suppress the default tooltip so the
        // two don't fight for the user's attention.
        ToolTip.SetTip(chip, null);

        EventHandler<DOSIWindowStateChangedEventArgs> stateHandler = (_, _) => RefreshActiveTint();
        EventHandler<DOSIWindowFocusEventArgs> focusHandler = (_, _) => RefreshActiveTint();
        EventHandler? layoutHandler = (_, _) => _previewCache.Remove(window);
        window.StateChanged += stateHandler;
        window.FocusChanged += focusHandler;
        window.LayoutUpdated += layoutHandler;

        chip.PointerEntered += (_, _) =>
        {
            if (!IsActive(window)) chip.Background = HoverBrush();
            SchedulePreview(window, chip);
        };
        chip.PointerExited += (_, _) =>
        {
            if (!IsActive(window)) chip.Background = IdleBrush();
            ScheduleClosePreview();
        };
        chip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(chip).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            ClosePreview(immediate: true);
            ActivateChip(window);
        };

        _chips[window] = new ChipEntry(chip, titleText, badge, initial, underline,
            window, stateHandler, focusHandler, layoutHandler);
        _chipStrip.Children.Add(chip);
    }

    /// <summary>
    /// Routes a chip click to the right action based on the window's
    /// current state. See the class summary for the toggle rules.
    /// </summary>
    private void ActivateChip(DOSIWindow window)
    {
        var mgr = _boundManager;
        if (mgr == null) return;

        if (window.WindowState == DOSIWindowState.Minimized)
        {
            window.Restore();
            mgr.SetFocus(window);
            mgr.BringToFront(window);
            return;
        }

        if (window.IsFocused)
        {
            window.WindowState = DOSIWindowState.Minimized;
            return;
        }

        mgr.BringToFront(window);
        mgr.SetFocus(window);
    }

    private void RefreshActiveTint()
    {
        foreach (var (window, entry) in _chips)
        {
            // Keep the label + badge in lockstep with the window title.
            entry.Label.Text = window.Title;
            entry.Initial.Text = ResolveInitial(window.Title);

            bool active = IsActive(window);
            entry.Chip.Background = active ? ActiveBrush() : IdleBrush();
            entry.Chip.BorderBrush = active ? ActiveBorderBrush() : IdleBorderBrush();
            entry.Chip.BoxShadow = active ? ActiveShadow() : default;
            entry.Underline.IsVisible = active;
            entry.Badge.Background = new SolidColorBrush(Accents.AccentPrimary);
            entry.Initial.Foreground = new SolidColorBrush(Accents.TextOnAccent);
            // A minimized window stays in the strip but tinted down so the
            // user can see it's not currently on screen.
            entry.Chip.Opacity = window.WindowState == DOSIWindowState.Minimized ? 0.55 : 1.0;
        }
    }

    private bool IsActive(DOSIWindow w) =>
        _boundManager?.FocusedWindow == w && w.WindowState != DOSIWindowState.Minimized;

    private static string ResolveInitial(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "?";
        foreach (var c in title)
        {
            if (char.IsLetterOrDigit(c)) return char.ToUpperInvariant(c).ToString();
        }
        return "?";
    }

    // =====================================================================
    // Live preview popover
    // =====================================================================

    private void SchedulePreview(DOSIWindow window, Control anchor)
    {
        // If the user moves chip -> chip, swap targets without flashing the
        // popover closed; just retarget and re-snapshot.
        _hoverCloseTimer?.Stop();
        _hoverCloseTimer = null;
        if (_previewCard != null && _previewCard.IsVisible && !ReferenceEquals(_hoverWindow, window))
        {
            _hoverWindow = window;
            _previewAnchor = anchor;
            RefreshPreviewContent(window);
            UpdatePreviewPosition();
            return;
        }

        _hoverWindow = window;
        _previewAnchor = anchor;
        _hoverOpenTimer?.Stop();
        _hoverOpenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoverDelayMs) };
        _hoverOpenTimer.Tick += (_, _) =>
        {
            _hoverOpenTimer!.Stop();
            _hoverOpenTimer = null;
            if (!ReferenceEquals(_hoverWindow, window)) return;
            OpenPreview(window, anchor);
        };
        _hoverOpenTimer.Start();
    }

    private void ScheduleClosePreview()
    {
        _hoverOpenTimer?.Stop();
        _hoverOpenTimer = null;
        _hoverCloseTimer?.Stop();
        _hoverCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CloseGraceMs) };
        _hoverCloseTimer.Tick += (_, _) =>
        {
            _hoverCloseTimer!.Stop();
            _hoverCloseTimer = null;
            ClosePreview(immediate: false);
        };
        _hoverCloseTimer.Start();
    }

    private void OpenPreview(DOSIWindow window, Control anchor)
    {
        EnsurePreviewBuilt();
        if (_previewCard == null) return;

        _previewAnchor = anchor;
        RefreshPreviewContent(window);

        // Mount into the OverlayLayer (idempotent) and position under the
        // anchor chip. The layer is resolved from the anchor's TopLevel,
        // so this works correctly on both the primary monitor and any
        // secondary MonitorWindow.
        var layer = Avalonia.Controls.Primitives.OverlayLayer.GetOverlayLayer(anchor);
        if (layer == null) return;

        if (!ReferenceEquals(_previewHostLayer, layer))
        {
            // Layer changed (rare - happens if the taskbar gets reparented
            // between monitors). Detach from the old one first so we don't
            // leak a ghost card on the previous host.
            DetachPreviewFromHost();
            _previewHostLayer = layer;
        }

        if (_previewCard.Parent == null)
        {
            layer.Children.Add(_previewCard);
        }

        _previewCard.IsVisible = true;

        // Position now and on every subsequent layout pass so anchor
        // movement (window resize, taskbar reflow) keeps the card pinned.
        UpdatePreviewPosition();
        if (_previewLayoutHandler == null)
        {
            _previewLayoutHandler = (_, _) => UpdatePreviewPosition();
            layer.LayoutUpdated += _previewLayoutHandler;
        }
    }

    private void ClosePreview(bool immediate)
    {
        _hoverOpenTimer?.Stop();
        _hoverOpenTimer = null;
        if (immediate)
        {
            _hoverCloseTimer?.Stop();
            _hoverCloseTimer = null;
        }

        if (_previewCard != null) _previewCard.IsVisible = false;
        _hoverWindow = null;
        _previewAnchor = null;
    }

    /// <summary>
    /// Removes the preview card from its current OverlayLayer and detaches
    /// the layout-updated handler. Called when the strip is leaving the
    /// visual tree or when the OverlayLayer changes (reparenting between
    /// monitors).
    /// </summary>
    private void DetachPreviewFromHost()
    {
        if (_previewHostLayer != null && _previewLayoutHandler != null)
        {
            _previewHostLayer.LayoutUpdated -= _previewLayoutHandler;
        }
        _previewLayoutHandler = null;
        if (_previewCard?.Parent is Panel parent)
        {
            parent.Children.Remove(_previewCard);
        }
        _previewHostLayer = null;
    }

    /// <summary>
    /// Recomputes the preview card's position so it sits horizontally
    /// centred under <see cref="_previewAnchor"/> and a small gap below
    /// the taskbar. The card is clamped to the host layer's bounds so it
    /// can't bleed off-screen on narrow monitors. Skipped silently when
    /// any of the inputs aren't ready yet (no anchor, no layer, layout
    /// hasn't run) - the next LayoutUpdated tick retries with valid
    /// values.
    /// </summary>
    private void UpdatePreviewPosition()
    {
        if (_previewCard == null || _previewHostLayer == null || _previewAnchor == null) return;
        // Bail if anchor hasn't been laid out yet (no parent visual root means
        // TranslatePoint will throw or return null).
        if (((Visual)_previewAnchor).GetVisualParent() == null) return;

        Point anchorTopLeft;
        try
        {
            anchorTopLeft = _previewAnchor.TranslatePoint(new Point(0, 0), _previewHostLayer)
                            ?? new Point(0, 0);
        }
        catch
        {
            return;
        }

        var anchorWidth = _previewAnchor.Bounds.Width;
        var anchorHeight = _previewAnchor.Bounds.Height;
        // PreviewWidth is the card's fixed visible width. The card hasn't
        // measured yet on first show, so use the constant rather than
        // _previewCard.Bounds.Width (which would be 0 the first frame).
        var cardWidth = PreviewWidth;
        var desiredX = anchorTopLeft.X + (anchorWidth - cardWidth) / 2;
        var desiredY = anchorTopLeft.Y + anchorHeight + 10;

        // Clamp so the card stays inside the layer's bounds. 8 px breathing
        // room on each edge matches the apps-menu margin.
        var layerWidth = _previewHostLayer.Bounds.Width;
        if (layerWidth > cardWidth + 16)
        {
            desiredX = Math.Clamp(desiredX, 8, layerWidth - cardWidth - 8);
        }

        // OverlayLayer is a Panel - children honour Margin for placement
        // (it has no Canvas-style attached props). A left/top margin is
        // the cleanest way to position an absolute child inside it.
        _previewCard.HorizontalAlignment = HorizontalAlignment.Left;
        _previewCard.VerticalAlignment = VerticalAlignment.Top;
        _previewCard.Margin = new Thickness(desiredX, desiredY, 0, 0);
    }

    private void EnsurePreviewBuilt()
    {
        if (_previewCard != null) return;

        // ---- Thumbnail surface --------------------------------------
        // The popover now shows ONLY the live window snapshot - no header,
        // no badge, no title chrome. The window UI itself already carries
        // its own title bar / traffic-light buttons / accent border, so a
        // second header on top of that read as redundant chrome on chrome.
        // Stretch.Uniform keeps the captured aspect ratio intact and the
        // image is centred so square / portrait / ultrawide windows all
        // look intentional inside the fixed-size popover frame.
        _previewImage = new Image
        {
            // Show the entire window scaled to fit the preview area while
            // preserving aspect ratio (no cropping). This matches the
            // user's preference to always see the whole UI inside the
            // preview plate.
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.Both,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        RenderOptions.SetBitmapInterpolationMode(_previewImage, BitmapInterpolationMode.HighQuality);

        // Placeholder text shown when SnapshotWindow returns null - i.e.
        // when the window is minimized, hasn't been laid out yet, or
        // hosts a native surface (WebView2) that can't be captured into
        // a managed bitmap. Mounted on top of _previewImage in the same
        // plate; one of the two is always invisible.
        _previewPlaceholderText = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(20),
            IsVisible = false
        };

        // ---- Snapshot plate -----------------------------------------
        // The snapshot itself lives inside a small Border that gives it
        // its OWN rounded corners + hairline edge + soft drop shadow, so
        // it reads as a miniature floating window sitting on the popover
        // surface rather than a flat texture pasted onto the card. This
        // is the visual idiom Windows 11 / macOS Mission Control use for
        // window thumbnails and it's what makes the preview feel like a
        // real preview instead of a clipped screenshot.
        //
        // The plate's own corner radius (8) is intentionally smaller than
        // the card's (12) so the snapshot reads as nested INSIDE the
        // card - the way a polaroid sits on a desk - rather than fighting
        // the card's outer curvature.
        var snapshotPlate = new Border
        {
            CornerRadius = new CornerRadius(8),
            // 1 px hairline so the snapshot has a crisp edge against the
            // card surface even when the captured window's chrome happens
            // to match the card's gradient (dark accents on dark cards).
            BorderBrush = new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            // Dark plate so a captured window with transparent / glass
            // chrome has something to composite against - prevents the
            // snapshot from "dissolving into" the card gradient.
            // Make the inner plate transparent so the preview bitmap's
            // transparent pixels show the host desktop beneath the card
            // instead of compositing against a dark plate.
            Background = Brushes.Transparent,
            ClipToBounds = true,
            // Soft elevation shadow so the snapshot visibly floats above
            // the card surface. Tuned weaker than the card's own shadow
            // (8 px blur vs 36 px) so it reads as "snapshot above plate"
            // not "two cards at the same elevation".
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0,
                OffsetY = 4,
                Blur = 14,
                Spread = 0,
                Color = Color.FromArgb(110, 0, 0, 0)
            }),
            Child = new Grid { Children = { _previewImage, _previewPlaceholderText } }
        };

        var imageFrame = new Border
        {
            Width = PreviewWidth,
            Height = PreviewImageHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            // Generous padding around the snapshot plate so its own drop
            // shadow has room to fade out without being clipped against
            // the card's interior edge.
            Padding = new Thickness(14, 12, 14, 14),
            // Card uses ClipToBounds=false so its drop shadow halo can fade
            // out fully around all four edges (matching the apps menu).
            // The image frame picks up the corner-clipping responsibility
            // here so the inner plate + snapshot still respect the card's
            // 12 px rounded corners.
            CornerRadius = new CornerRadius(11),
            ClipToBounds = true,
            Child = snapshotPlate
        };

        // ---- Card surface -------------------------------------------
        // Visual parity with the apps menu / notification popover:
        //   * Soft two-stop dark gradient background (Light-accent branch
        //     keeps text readable in the Light theme).
        //   * 12 px rounded corners - same radius as the apps menu so all
        //     dropdown surfaces feel like one family.
        //   * Accent-coloured border so the popover visibly belongs to
        //     the user's chosen accent.
        //   * Drop shadow recipe lifted DIRECTLY from BuildAppsMenu:
        //     OffsetY=14, Blur=36, Spread=0, alpha 150/255. Because the
        //     card is in OverlayLayer (not a Popup with its own OS
        //     window), the 36 px blur halo renders fully on all four
        //     sides instead of being clipped at any native-window edge.
        //     ClipToBounds is left FALSE so the shadow halo has room to
        //     fade out around the card; the inner image frame does its
        //     own clipping so this doesn't leak any visible overflow.
        _previewCard = new Border
        {
            Width = PreviewWidth,
            CornerRadius = new CornerRadius(12),
            // Make the card background transparent so the area behind
            // the preview shows through instead of a filled gradient.
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Accents.AccentSecondary),
            BorderThickness = new Thickness(1),
            ClipToBounds = false,
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0,
                OffsetY = 14,
                Blur = 36,
                Spread = 0,
                Color = Color.FromArgb(150, 0, 0, 0)
            }),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = imageFrame,
            // Born invisible; OpenPreview flips this to true after mounting
            // into the OverlayLayer. Mounted-but-invisible costs nothing
            // and lets us avoid Add/Remove churn on every hover.
            IsVisible = false
        };

        // Keep the popover open while the cursor lives inside it (so the
        // user can move chip -> preview and click the implicit "bring
        // forward" affordance below).
        _previewCard.PointerEntered += (_, _) =>
        {
            _hoverCloseTimer?.Stop();
            _hoverCloseTimer = null;
        };
        _previewCard.PointerExited += (_, _) => ScheduleClosePreview();
        _previewCard.PointerPressed += (_, e) =>
        {
            if (_hoverWindow == null) return;
            e.Handled = true;
            var target = _hoverWindow;
            ClosePreview(immediate: true);
            ActivateChip(target);
        };
    }

    /// <summary>
    /// Builds the popover card's background gradient. Mirrors the apps menu
    /// / notification popover recipe so every DOSI dropdown surface reads
    /// as the same family of card. Light-accent branch keeps the surface
    /// pale enough for dark text to stay readable.
    /// </summary>
    private static IBrush BuildPreviewBackground()
    {
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

    /// <summary>
    /// Renders a fresh thumbnail of <paramref name="window"/> into the
    /// preview image. Uses <see cref="RenderTargetBitmap"/> on the window's
    /// chrome+content visual (excluding the shadow gutter) so the image is
    /// exactly the window UI the user would see if they pulled the window
    /// forward right now. Native WebView surfaces live on the OS compositor
    /// and won't show in a managed bitmap; the preview falls back to a
    /// styled placeholder card carrying the app title in that case, and
    /// also when the window is currently minimized (no visual to render).
    /// </summary>
    private void RefreshPreviewContent(DOSIWindow window)
    {
        if (_previewImage == null || _previewPlaceholderText == null) return;

        // Re-apply accent-aware surfaces every refresh so a flip mid-hover
        // re-tints without waiting for the popover to close + re-open.
        if (_previewCard != null)
        {
            _previewCard.Background = BuildPreviewBackground();
            _previewCard.BorderBrush = new SolidColorBrush(Accents.AccentSecondary);
        }

        // Try a short-lived cache first to avoid expensive re-renders while
        // the user moves across chips. Cached entries older than 1s are
        // dropped so rapid state changes still refresh in a timely way.
        var useCache = _previewCache.TryGetValue(window, out var cached) &&
                       cached.Bitmap != null &&
                       (DateTime.UtcNow - cached.Timestamp).TotalMilliseconds < 1000;
        RenderTargetBitmap? bmp = null;
        if (useCache)
        {
            bmp = cached.Bitmap;
        }
        else
        {
            try { bmp = SnapshotWindow(window); }
            catch { bmp = null; }
            _previewCache[window] = (bmp, DateTime.UtcNow);
        }

        if (bmp != null)
        {
            // Ensure the bitmap fills the preview area visually. Some
            // captures end up small or letterboxed; render the captured
            // bitmap into a fixed-size preview bitmap that exactly fits
            // the visible preview plate so Stretch/align quirks can't
            // leave the thumbnail stuck in a corner.
            var fitted = EnsureBitmapFitsPreview(bmp);
            _previewImage.Source = fitted ?? bmp;
            _previewImage.IsVisible = true;
            _previewPlaceholderText.IsVisible = false;
        }
        else
        {
            // Minimized / not-yet-laid-out / native-surface windows can't be
            // captured via RenderTargetBitmap. Show a graceful placeholder
            // instead of the garbled output the user would otherwise see
            // from rendering a 0x0 or invisible visual.
            _previewImage.Source = null;
            _previewImage.IsVisible = false;
            _previewPlaceholderText.Text = window.WindowState == DOSIWindowState.Minimized
                ? $"{(string.IsNullOrWhiteSpace(window.Title) ? "Window" : window.Title)}\nMinimized"
                : (string.IsNullOrWhiteSpace(window.Title) ? "Preview unavailable" : window.Title);
            _previewPlaceholderText.IsVisible = true;
        }
    }

    private static RenderTargetBitmap? SnapshotWindow(DOSIWindow window)
    {
        // Minimized windows have IsVisible=false on the entire control, so
        // the visual subtree never renders. Rendering one anyway produces
        // the "tiny dark fragment in the top-left of a black canvas" image
        // the user reported. Return null so the caller falls back to the
        // styled placeholder.
        if (window.WindowState == DOSIWindowState.Minimized) return null;
        if (!window.IsVisible) return null;

        // Snapshot the window UI itself (chrome + content + accent border
        // + rounded corners), NOT the outer container that includes the
        // 50 px shadow gutter. WindowVisual.Bounds is sized to
        // WindowWidth / WindowHeight when the visual has been laid out;
        // before first measure (window opened this frame, never shown
        // yet) we fall back to the public WindowWidth/WindowHeight which
        // are always populated.
        var visual = window.WindowVisual;

        // Prefer the window's logical size (WindowWidth/WindowHeight) as
        // the snapshot source dimensions so the captured thumbnail is
        // independent of the on-screen control's current rendered size.
        // This makes the taskbar preview behave like Windows: the full
        // window contents are shown scaled to the preview area regardless
        // of how the user sized the live window on the desktop.
        var width = window.WindowWidth;
        var height = window.WindowHeight;
        // Fall back to the visual's measured bounds only if the logical
        // dimensions are unavailable.
        if (double.IsNaN(width) || double.IsNaN(height) || width < 1 || height < 1)
        {
            width = visual.Bounds.Width;
            height = visual.Bounds.Height;
        }
        if (double.IsNaN(width) || double.IsNaN(height) || width < 1 || height < 1) return null;

        // Render the visual into a bitmap sized exactly to the preview
        // plate so the Image control always gets a source that fills the
        // preview area. Do NOT modify the live visual. Instead use a
        // VisualBrush that paints the live visual into an offscreen host
        // at the desired preview size and rasterize that host.
        var pixelW = Math.Max(1, (int)Math.Ceiling(PreviewWidth));
        var pixelH = Math.Max(1, (int)Math.Ceiling(PreviewImageHeight));
        var dpi = 96.0;

        var rtb = new RenderTargetBitmap(new PixelSize(pixelW, pixelH), new Vector(dpi, dpi));
        try
        {
            // Host with a solid background so transparent regions composite
            // against the window background rather than the card plate.
            var host = new Grid
            {
                Width = PreviewWidth,
                Height = PreviewImageHeight,
                // Keep the raster host transparent so the rendered visual
                // composite doesn't get an accent-coloured backing. The
                // snapshot should be the window UI only; any surrounding
                // card/background is provided by the popover visuals.
                Background = Brushes.Transparent
            };

            // Paint the live visual into a Rectangle using a VisualBrush so
            // we don't modify the live visual tree (no RenderTransform).
            var vb = new VisualBrush
            {
                Visual = visual,
                Stretch = Stretch.Uniform,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center
            };

            var rect = new Rectangle
            {
                Width = PreviewWidth,
                Height = PreviewImageHeight,
                Fill = vb,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            host.Children.Add(rect);

            host.Measure(new Size(PreviewWidth, PreviewImageHeight));
            host.Arrange(new Rect(0, 0, PreviewWidth, PreviewImageHeight));
            rtb.Render(host);
            return rtb;
        }
        catch
        {
            rtb.Dispose();
            return null;
        }
    }

    // =====================================================================
    // Brush helpers - re-built on every paint cycle because the accent
    // can flip at any time and chips re-tint via RefreshActiveTint.
    // =====================================================================

    private static SolidColorBrush IdleBrush() =>
        new(Color.FromArgb(28, 255, 255, 255));

    private static SolidColorBrush HoverBrush() =>
        new(Color.FromArgb(60, 255, 255, 255));

    private static SolidColorBrush IdleBorderBrush() =>
        new(Color.FromArgb(36, 255, 255, 255));

    private static IBrush ActiveBrush()
    {
        var a = Accents.AccentPrimary;
        // Diagonal accent wash so the active chip reads as a "lit" surface
        // rather than a flat colour block - matches the lift suggested by
        // its shadow + underline.
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(160, a.R, a.G, a.B), 0),
                new GradientStop(Color.FromArgb(110, a.R, a.G, a.B), 1)
            }
        };
    }

    private static SolidColorBrush ActiveBorderBrush()
    {
        var a = Accents.AccentPrimary;
        return new SolidColorBrush(Color.FromArgb(190, a.R, a.G, a.B));
    }

    private static BoxShadows ActiveShadow()
    {
        var a = Accents.AccentPrimary;
        return new BoxShadows(new BoxShadow
        {
            OffsetX = 0,
            OffsetY = 2,
            Blur = 10,
            Spread = -2,
            Color = Color.FromArgb(140, a.R, a.G, a.B)
        });
    }

    /// <summary>
    /// Bundles a chip's visual + the per-window event hooks so we can
    /// detach them in one shot when a window closes.
    /// </summary>
    private sealed class ChipEntry
    {
        public Border Chip { get; }
        public TextBlock Label { get; }
        public Border Badge { get; }
        public TextBlock Initial { get; }
        public Border Underline { get; }
        public DOSIWindow Window { get; }
        private readonly EventHandler<DOSIWindowStateChangedEventArgs> _stateHandler;
        private readonly EventHandler<DOSIWindowFocusEventArgs> _focusHandler;
        private readonly EventHandler? _layoutHandler;

        public ChipEntry(Border chip, TextBlock label, Border badge, TextBlock initial,
            Border underline, DOSIWindow window,
            EventHandler<DOSIWindowStateChangedEventArgs> stateHandler,
            EventHandler<DOSIWindowFocusEventArgs> focusHandler,
            EventHandler? layoutHandler)
        {
            Chip = chip;
            Label = label;
            Badge = badge;
            Initial = initial;
            Underline = underline;
            Window = window;
            _stateHandler = stateHandler;
            _focusHandler = focusHandler;
            _layoutHandler = layoutHandler;
        }

        public void Detach()
        {
            Window.StateChanged -= _stateHandler;
            Window.FocusChanged -= _focusHandler;
            if (_layoutHandler != null) Window.LayoutUpdated -= _layoutHandler;
        }
    }
}
