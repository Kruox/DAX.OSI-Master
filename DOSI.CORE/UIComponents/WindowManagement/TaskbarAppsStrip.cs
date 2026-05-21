using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
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
    // Stretch.Uniform.
    private const double PreviewWidth = 280;
    private const double PreviewImageHeight = 158;
    private const int HoverDelayMs = 380;
    private const int CloseGraceMs = 140;

    private readonly StackPanel _chipStrip;
    private readonly Dictionary<DOSIWindow, ChipEntry> _chips = new();

    private WindowManager? _boundManager;

    // ---- Live-preview popover state -------------------------------------
    // One popup reused for every chip; the popover's PlacementTarget is
    // swapped to the currently-hovered chip so we never juggle multiple
    // popups in flight. Built lazily on first hover.
    private Popup? _previewPopup;
    private Border? _previewCard;
    private Image? _previewImage;
    private TextBlock? _previewTitle;
    private TextBlock? _previewSubtitle;
    private Border? _previewBadge;
    private DOSIWindow? _hoverWindow;
    private DispatcherTimer? _hoverOpenTimer;
    private DispatcherTimer? _hoverCloseTimer;

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
        };
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
        window.StateChanged += stateHandler;
        window.FocusChanged += focusHandler;

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
            window, stateHandler, focusHandler);
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
        if (_previewPopup?.IsOpen == true && !ReferenceEquals(_hoverWindow, window))
        {
            _hoverWindow = window;
            _previewPopup.PlacementTarget = anchor;
            RefreshPreviewContent(window);
            return;
        }

        _hoverWindow = window;
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
        if (_previewPopup == null) return;

        _previewPopup.PlacementTarget = anchor;
        RefreshPreviewContent(window);
        if (!_previewPopup.IsOpen) _previewPopup.IsOpen = true;
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
        if (_previewPopup != null && _previewPopup.IsOpen)
            _previewPopup.IsOpen = false;
        _hoverWindow = null;
    }

    private void EnsurePreviewBuilt()
    {
        if (_previewPopup != null) return;

        // ---- Header --------------------------------------------------
        // Small accent badge carrying the window's title initial, mirroring
        // the chip badge for visual continuity, plus a stacked
        // title / status caption.
        var badgeInitial = new TextBlock
        {
            Text = "?",
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Accents.TextOnAccent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _previewBadge = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Accents.AccentPrimary),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Child = badgeInitial
        };

        _previewTitle = new TextBlock
        {
            Text = string.Empty,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        _previewSubtitle = new TextBlock
        {
            Text = string.Empty,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Opacity = 0.7,
            Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0)
        };

        var titleStack = new StackPanel
        {
            Spacing = 0,
            VerticalAlignment = VerticalAlignment.Center
        };
        titleStack.Children.Add(_previewTitle);
        titleStack.Children.Add(_previewSubtitle);

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(14, 12, 14, 12)
        };
        header.Children.Add(_previewBadge); Grid.SetColumn(_previewBadge, 0);
        header.Children.Add(titleStack); Grid.SetColumn(titleStack, 1);

        // ---- Thumbnail frame ----------------------------------------
        // The thumbnail itself gets a rounded 10 px corner radius and a
        // soft inset shadow + 1 px white hairline so it reads as a real
        // window snapshot dropped onto the card surface, not a flat
        // texture pasted onto the background.
        _previewImage = new Image
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.Both,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var imageFrame = new Border
        {
            Width = PreviewWidth - 28,
            Height = PreviewImageHeight,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Margin = new Thickness(14, 0, 14, 12),
            Child = _previewImage,
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0,
                OffsetY = 4,
                Blur = 12,
                Spread = 0,
                Color = Color.FromArgb(110, 0, 0, 0)
            })
        };

        var cardBody = new StackPanel { Spacing = 0 };
        cardBody.Children.Add(header);
        cardBody.Children.Add(imageFrame);

        _previewCard = new Border
        {
            Width = PreviewWidth,
            CornerRadius = new CornerRadius(14),
            // Vertical glassy gradient with the accent tint subtly
            // bleeding in at the top - reads as a piece of premium
            // taskbar chrome rather than a flat dialog. The base
            // colour stays near-black so the snapshot is always the
            // brightest element in the card.
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(240, 28, 32, 42), 0),
                    new GradientStop(Color.FromArgb(240, 18, 22, 30), 1)
                }
            },
            BorderBrush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(150,
                        Accents.AccentPrimary.R, Accents.AccentPrimary.G, Accents.AccentPrimary.B), 0),
                    new GradientStop(Color.FromArgb(40, 255, 255, 255), 1)
                }
            },
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            BoxShadow = new BoxShadows(
                new BoxShadow
                {
                    OffsetX = 0,
                    OffsetY = 14,
                    Blur = 36,
                    Spread = 0,
                    Color = Color.FromArgb(180, 0, 0, 0)
                },
                [
                    new BoxShadow
                    {
                        OffsetX = 0,
                        OffsetY = 0,
                        Blur = 22,
                        Spread = -6,
                        Color = Color.FromArgb(110,
                            Accents.AccentPrimary.R, Accents.AccentPrimary.G, Accents.AccentPrimary.B)
                    }
                ]),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = cardBody
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

        _previewPopup = new Popup
        {
            Placement = PlacementMode.AnchorAndGravity,
            PlacementAnchor = PopupAnchor.Bottom,
            PlacementGravity = PopupGravity.Bottom,
            HorizontalOffset = 0,
            VerticalOffset = 10,
            // Keep the popup focusless so hovering over a chip doesn't
            // steal focus from whatever the user is typing into.
            IsLightDismissEnabled = false,
            Child = _previewCard,
            OverlayDismissEventPassThrough = true
        };
    }

    /// <summary>
    /// Renders a fresh thumbnail of <paramref name="window"/> into the
    /// preview image. Uses <see cref="RenderTargetBitmap"/> on the window
    /// visual itself so the image is whatever the user would see if they
    /// pulled the window forward right now. Native WebView surfaces live
    /// on the OS compositor and won't show in a managed bitmap; the
    /// preview gracefully falls back to a title-only card in that case.
    /// </summary>
    private void RefreshPreviewContent(DOSIWindow window)
    {
        if (_previewTitle == null || _previewSubtitle == null ||
            _previewBadge == null || _previewImage == null ||
            _previewBadge.Child is not TextBlock badgeText) return;

        _previewTitle.Text = string.IsNullOrWhiteSpace(window.Title) ? "Window" : window.Title;
        _previewSubtitle.Text = window.WindowState == DOSIWindowState.Minimized
            ? "MINIMIZED \u00B7 CLICK TO RESTORE"
            : (IsActive(window)
                ? "ACTIVE \u00B7 CLICK TO MINIMIZE"
                : "CLICK TO BRING FORWARD");
        badgeText.Text = ResolveInitial(window.Title);
        _previewBadge.Background = new SolidColorBrush(Accents.AccentPrimary);
        badgeText.Foreground = new SolidColorBrush(Accents.TextOnAccent);

        try
        {
            var bmp = SnapshotWindow(window);
            _previewImage.Source = bmp;
            _previewImage.IsVisible = bmp != null;
        }
        catch
        {
            _previewImage.Source = null;
            _previewImage.IsVisible = false;
        }
    }

    private static RenderTargetBitmap? SnapshotWindow(DOSIWindow window)
    {
        // DOSIWindow embeds a 50 px shadow gutter on every side - its
        // actual visual size is Width / Height, not WindowWidth /
        // WindowHeight.
        var width = window.Width;
        var height = window.Height;
        if (double.IsNaN(width) || double.IsNaN(height) ||
            width <= 1 || height <= 1) return null;

        // CRITICAL: RenderTargetBitmap.Render(Visual) paints the visual
        // at its NATIVE pixel size into the bitmap's top-left corner -
        // it does NOT scale to fit. Sizing the bitmap to (width*scale,
        // height*scale) and rendering a 1000x700 window into it leaves
        // only the top-left ~360x320 visible and clips the rest, which
        // is the cut-off thumbnail the user keeps reporting.
        //
        // The supported way to downscale is via the bitmap's DPI: a 96
        // DPI render of a 1000x700 visual produces 1000x700 pixels; at
        // 48 DPI the SAME visual fills only 500x350 device pixels. We
        // pick a target DPI so the WHOLE window fits inside a ~480 px
        // longest-edge bitmap, then size the bitmap to the same scaled
        // dimensions - result: the entire window (chrome + content +
        // shadow gutter) ends up inside the bitmap with nothing clipped.
        const double maxDim = 480;
        var scale = Math.Min(1.0, maxDim / Math.Max(width, height));
        var pixelW = Math.Max(1, (int)Math.Ceiling(width * scale));
        var pixelH = Math.Max(1, (int)Math.Ceiling(height * scale));
        var dpi = 96 * scale;

        var rtb = new RenderTargetBitmap(new PixelSize(pixelW, pixelH), new Vector(dpi, dpi));
        try
        {
            rtb.Render(window);
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

        public ChipEntry(Border chip, TextBlock label, Border badge, TextBlock initial,
            Border underline, DOSIWindow window,
            EventHandler<DOSIWindowStateChangedEventArgs> stateHandler,
            EventHandler<DOSIWindowFocusEventArgs> focusHandler)
        {
            Chip = chip;
            Label = label;
            Badge = badge;
            Initial = initial;
            Underline = underline;
            Window = window;
            _stateHandler = stateHandler;
            _focusHandler = focusHandler;
        }

        public void Detach()
        {
            Window.StateChanged -= _stateHandler;
            Window.FocusChanged -= _focusHandler;
        }
    }
}
