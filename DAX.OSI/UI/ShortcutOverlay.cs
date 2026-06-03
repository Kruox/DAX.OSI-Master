using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using DOSI.CORE.AccentManagement;
using DOSI.CORE.UIComponents;

namespace DAX.OSI.UI;

/// <summary>
/// Modal "press F1 / ? for help" overlay that lists every global
/// keyboard shortcut DAX.OSI exposes, grouped by category. Lives in
/// the global overlay canvas so it's available at any lifecycle stage
/// (boot, login, desktop, post-signout). One-shot: opens, captures
/// Esc / outside-click / X to dismiss, and self-removes from the host.
///
/// The catalog is intentionally a plain static array - we don't try
/// to discover shortcuts dynamically, because half of them are
/// host-routed (Ctrl+T to MainWindow.OnGlobalKeyDown) and half are
/// per-control (Ctrl+S in the IDE editor). A static catalog stays
/// honest about what's actually wired, and adding a new shortcut is
/// a one-line edit here next to the wiring change.
/// </summary>
internal static class ShortcutOverlay
{
    private static AccentManager Accents => AccentManager.Instance;

    private static readonly (string Category, string Keys, string Description)[] Catalog =
    {
        ("Global", "F1  /  Shift+?",     "Show this shortcut overlay"),
        ("Global", "Ctrl+T",             "Open a terminal"),
        ("Global", "Esc",                "Close the active overlay / popup"),

        ("Window", "Drag title bar",     "Move the window"),
        ("Window", "Drag to top edge",   "Maximize"),
        ("Window", "Drag to side edge",  "Snap to half"),
        ("Window", "Drag to corner",     "Snap to quarter"),
        ("Window", "Double-click title", "Maximize / restore"),
        ("Window", "Alt+F4",             "Close window"),

        ("File Explorer", "Backspace",   "Navigate up"),
        ("File Explorer", "Enter",       "Open selected"),
        ("File Explorer", "Delete",      "Move to Trash"),
        ("File Explorer", "F2",          "Rename"),
        ("File Explorer", "Ctrl+A",      "Select all"),

        ("Code Editor", "Ctrl+S",        "Save"),
        ("Code Editor", "Ctrl+Z / Y",    "Undo / Redo"),
        ("Code Editor", "F5",            "Run"),

        ("Image Viewer", "Wheel",        "Zoom (anchored at cursor)"),
        ("Image Viewer", "Left / Right", "Previous / Next image"),
        ("Image Viewer", "Double-click", "Toggle Fit / 1:1"),
        ("Image Viewer", "+ / -",        "Zoom in / out"),

        ("Designer (IDE)", "Drag controls",  "Snap to peer edges + form centerlines (hold Alt to disable snap)"),
        ("Designer (IDE)", "Shift+Click",    "Add to selection / multi-select"),
        ("Designer (IDE)", "Arrow keys",     "Nudge selection 1 px"),
        ("Designer (IDE)", "Shift+Arrows",   "Nudge selection by grid step"),
        ("Designer (IDE)", "Delete",         "Remove selected control(s)"),
        ("Designer (IDE)", "Double-click",   "Open code-behind for primary event")
    };

    /// <summary>
    /// Mounts the overlay into <paramref name="host"/> and returns. Idempotent:
    /// if the overlay is already shown, this is a no-op.
    /// </summary>
    public static void Show(Panel host)
    {
        if (host == null) return;
        // Don't double-mount.
        foreach (var c in host.Children)
            if (c is Border b && b.Name == OverlayName) return;

        var dim = new Border
        {
            Name = OverlayName,
            Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Width = host.Bounds.Width,
            Height = host.Bounds.Height
        };
        // Track host size: the overlay lives inside a Canvas (no
        // intrinsic stretch behaviour), so we have to resize the dim
        // border manually as the canvas changes size. Cleaner than
        // pulling System.Reactive in just for a one-line bind.
        EventHandler<SizeChangedEventArgs>? sizeHandler = null;
        sizeHandler = (_, _) =>
        {
            dim.Width = host.Bounds.Width;
            dim.Height = host.Bounds.Height;
        };
        host.SizeChanged += sizeHandler;
        dim.DetachedFromVisualTree += (_, _) =>
        {
            if (sizeHandler != null) host.SizeChanged -= sizeHandler;
        };

        var card = BuildCard(host);
        dim.Child = new Grid { Children = { card } };

        // Click outside the card dismisses the overlay.
        dim.PointerPressed += (s, e) =>
        {
            if (e.Source == dim || ReferenceEquals(e.Source, dim.Child))
            {
                Dismiss(host, dim);
                e.Handled = true;
            }
        };

        // Esc dismisses too. Subscribing on the dim border alone misses
        // the keypress because focus has likely settled on a control
        // outside the overlay (the desktop, the apps menu search box,
        // a focused window). Hook the TopLevel's tunneled KeyDown so
        // we see Esc regardless of focus, but only handle it while
        // OUR overlay is the topmost DOSIShortcutOverlay in the host.
        var top = Avalonia.Controls.TopLevel.GetTopLevel(host);
        EventHandler<KeyEventArgs>? topKeyHandler = null;
        if (top != null)
        {
            topKeyHandler = (_, e) =>
            {
                if (e.Key != Key.Escape) return;
                // Only the most-recently-added shortcut overlay should
                // react - if a future feature stacks one over another,
                // the bottom one ignores Esc.
                Border? topmost = null;
                foreach (var ch in host.Children)
                    if (ch is Border bb && bb.Name == OverlayName) topmost = bb;
                if (!ReferenceEquals(topmost, dim)) return;
                Dismiss(host, dim);
                e.Handled = true;
            };
            top.AddHandler(InputElement.KeyDownEvent, topKeyHandler,
                RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
                handledEventsToo: true);
            dim.DetachedFromVisualTree += (_, _) =>
            {
                if (topKeyHandler != null)
                    top.RemoveHandler(InputElement.KeyDownEvent, topKeyHandler);
            };
        }

        host.Children.Add(dim);
        Canvas.SetLeft(dim, 0);
        Canvas.SetTop(dim, 0);
        dim.Focus();

        // Soft fade-in to match the rest of the OS chrome.
        dim.Opacity = 0;
        var fade = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(140),
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, 0.0) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, 1.0) } }
            }
        };
        _ = fade.RunAsync(dim);
    }

    private const string OverlayName = "DOSIShortcutOverlay";

    private static Border BuildCard(Panel host)
    {
        var title = new TextBlock
        {
            Text = "Keyboard Shortcuts",
            FontSize = 22,
            FontWeight = FontWeight.Bold,
            Foreground = Accents.TextPrimaryBrush
        };
        var subtitle = new TextBlock
        {
            Text = "Press Esc to close",
            FontSize = 12,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.85,
            Margin = new Thickness(0, 4, 0, 16)
        };

        // Build per-category sections. Use the same SectionHeader +
        // grid-of-rows pattern the rest of DOSI uses so the overlay
        // looks native instead of bolted-on.
        var listStack = new StackPanel { Orientation = Orientation.Vertical, Spacing = 14 };
        string? lastCategory = null;
        StackPanel? currentSection = null;
        foreach (var (cat, keys, desc) in Catalog)
        {
            if (cat != lastCategory)
            {
                lastCategory = cat;
                var header = new TextBlock
                {
                    Text = cat.ToUpperInvariant(),
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Accents.AccentPrimaryBrush,
                    Margin = new Thickness(0, 0, 0, 4)
                };
                currentSection = new StackPanel { Orientation = Orientation.Vertical, Spacing = 4 };
                var sectionWrap = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Spacing = 4,
                    Children = { header, currentSection }
                };
                listStack.Children.Add(sectionWrap);
            }
            currentSection!.Children.Add(BuildShortcutRow(keys, desc));
        }

        var scroller = new DOSIScrollViewer
        {
            Content = listStack,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            ShowScrollButtons = false,
            MaxHeight = 460
        };

        var inner = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 0,
            Children = { title, subtitle, scroller }
        };

        var card = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 620,
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(28, 24),
            Background = Accents.WindowBackgroundBrush,
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0,
                OffsetY = 18,
                Blur = 60,
                Spread = -2,
                Color = Color.FromArgb(180, 0, 0, 0)
            }),
            Child = inner
        };
        return card;
    }

    private static Control BuildShortcutRow(string keys, string description)
    {
        var keyChip = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 2),
            Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = keys,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                FontFamily = new FontFamily("Consolas"),
                Foreground = Accents.TextPrimaryBrush
            }
        };
        var descText = new TextBlock
        {
            Text = description,
            FontSize = 12,
            Foreground = Accents.TextPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("180,*"),
            Margin = new Thickness(0, 2, 0, 2)
        };
        grid.Children.Add(keyChip); Grid.SetColumn(keyChip, 0);
        grid.Children.Add(descText); Grid.SetColumn(descText, 1);
        descText.Margin = new Thickness(12, 0, 0, 0);
        return grid;
    }

    private static void Dismiss(Panel host, Border overlay)
    {
        var fade = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(120),
            Easing = new CubicEaseIn(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, 1.0) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, 0.0) } }
            }
        };
        _ = fade.RunAsync(overlay).ContinueWith(_ =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (host.Children.Contains(overlay)) host.Children.Remove(overlay);
            });
        });
    }
}
