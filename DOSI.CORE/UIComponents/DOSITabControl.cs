using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DOSI.CORE.AccentManagement;

namespace DOSI.CORE.UIComponents;

/// <summary>
/// Where the tab strip is rendered relative to the tab content.
/// </summary>
public enum DOSITabPlacement
{
    Left,
    Top
}

/// <summary>
/// A single tab inside a <see cref="DOSITabControl"/>. Tabs hold a header
/// (title + optional icon glyph) and an arbitrary <see cref="Control"/> body
/// that is displayed when the tab is active.
/// </summary>
public sealed class DOSITabItem
{
    /// <summary>The text shown in the tab strip.</summary>
    public string Header { get; init; } = string.Empty;

    /// <summary>Optional small unicode glyph (e.g. emoji or symbol) shown to the left of the header.</summary>
    public string? Glyph { get; init; }

    /// <summary>Optional sub-text shown under the header in the tab strip (vertical placement only).</summary>
    public string? Subtitle { get; init; }

    /// <summary>The body of the tab. Lazily evaluated so heavy content can be deferred.</summary>
    public Func<Control> ContentFactory { get; init; } = () => new Panel();

    private Control? _cachedContent;

    /// <summary>Returns (and caches) the body control produced by <see cref="ContentFactory"/>.</summary>
    internal Control GetOrCreateContent() => _cachedContent ??= ContentFactory();

    /// <summary>
    /// Drops the cached body so the next <see cref="GetOrCreateContent"/> call
    /// re-runs the factory. Used by <see cref="DOSITabControl"/> on accent
    /// change so freshly-rebuilt content picks up live brushes - cached
    /// <c>TextBlock.Foreground</c> values from a prior accent are otherwise
    /// stale (SolidColorBrush snapshots, not bindings) and render as the wrong
    /// color (e.g. light text on a light surface) until the tab is rebuilt.
    /// </summary>
    internal void InvalidateContent() => _cachedContent = null;
}

/// <summary>
/// A 100% custom-drawn tab control for the DOSI virtual operating system.
/// Renders an accent-aware vertical (or top) tab strip with hover / selected
/// states, a sliding accent indicator, and a content area that swaps as the
/// user changes tabs.
///
/// Designed to mirror the look-and-feel of <see cref="DOSIButton"/>,
/// <see cref="DOSIWindow"/>, and the rest of the DOSI UI kit.
/// </summary>
public class DOSITabControl : Control
{
    #region Fields

    private static AccentManager Accents => AccentManager.Instance;

    private readonly Border _root;
    private readonly StackPanel _tabStrip;
    private readonly Border _stripContainer;
    private readonly Border _indicator;
    private readonly Border _contentArea;
    private readonly Grid _layoutGrid;

    private readonly Dictionary<DOSITabItem, Border> _tabHeaders = new();

    // Animated indicator state
    private double _indicatorCurrent;
    private double _indicatorTarget;
    private double _indicatorCurrentSize;
    private double _indicatorTargetSize;
    private DispatcherTimer? _indicatorTimer;

    private const double IndicatorAnimMs = 180;
    private const double SidebarWidth = 200;
    private const double TopStripHeight = 44;

    #endregion

    #region Styled Properties

    public static readonly StyledProperty<DOSITabPlacement> TabPlacementProperty =
        AvaloniaProperty.Register<DOSITabControl, DOSITabPlacement>(nameof(TabPlacement), defaultValue: DOSITabPlacement.Left);

    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<DOSITabControl, int>(nameof(SelectedIndex), defaultValue: -1);

    #endregion

    #region Properties

    public DOSITabPlacement TabPlacement
    {
        get => GetValue(TabPlacementProperty);
        set => SetValue(TabPlacementProperty, value);
    }

    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    /// <summary>The tabs displayed by this control. Add or remove items at any time.</summary>
    public AvaloniaList<DOSITabItem> Items { get; } = new();

    public DOSITabItem? SelectedItem =>
        SelectedIndex >= 0 && SelectedIndex < Items.Count ? Items[SelectedIndex] : null;

    #endregion

    #region Events

    public event EventHandler<DOSITabItem>? SelectionChanged;

    #endregion

    #region Constructor

    static DOSITabControl()
    {
        FocusAdornerProperty.OverrideDefaultValue<DOSITabControl>(null);

        TabPlacementProperty.Changed.AddClassHandler<DOSITabControl>((tc, _) => tc.RebuildLayout());
        SelectedIndexProperty.Changed.AddClassHandler<DOSITabControl>((tc, _) => tc.OnSelectionPropertyChanged());
    }

    public DOSITabControl()
    {
        FocusAdorner = null;
        ClipToBounds = true;

        _tabStrip = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            Margin = new Thickness(10)
        };

        _indicator = new Border
        {
            Width = 3,
            Height = 0,
            CornerRadius = new CornerRadius(2),
            Background = Accents.AccentGradientBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Margin = new Thickness(2, 0, 0, 0)
        };

        var stripHost = new Grid
        {
            Children = { _indicator, _tabStrip }
        };

        _stripContainer = new Border
        {
            Background = Accents.WindowChromeBrush,
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            Child = stripHost
        };

        _contentArea = new Border
        {
            Background = Accents.WindowContentBrush,
            Padding = new Thickness(0)
        };

        _layoutGrid = new Grid();

        _root = new Border
        {
            Child = _layoutGrid,
            ClipToBounds = true
        };

        VisualChildren.Add(_root);
        LogicalChildren.Add(_root);

        Items.CollectionChanged += OnItemsChanged;

        AttachedToVisualTree += (_, _) =>
        {
            Accents.AccentChanged += OnAccentChanged;
            RebuildLayout();
            if (SelectedIndex < 0 && Items.Count > 0)
                SelectedIndex = 0;
            else
                RefreshTabVisuals();
            UpdateIndicatorTarget(animate: false);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            Accents.AccentChanged -= OnAccentChanged;
            _indicatorTimer?.Stop();
            _indicatorTimer = null;
        };
    }

    #endregion

    #region Layout

    private void RebuildLayout()
    {
        _layoutGrid.Children.Clear();
        _layoutGrid.RowDefinitions.Clear();
        _layoutGrid.ColumnDefinitions.Clear();

        if (TabPlacement == DOSITabPlacement.Left)
        {
            _tabStrip.Orientation = Orientation.Vertical;
            _tabStrip.Margin = new Thickness(10);
            _stripContainer.Width = SidebarWidth;
            _stripContainer.Height = double.NaN;
            _stripContainer.BorderThickness = new Thickness(0, 0, 1, 0);

            _indicator.HorizontalAlignment = HorizontalAlignment.Left;
            _indicator.VerticalAlignment = VerticalAlignment.Top;
            _indicator.Width = 3;

            _layoutGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            _layoutGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            _layoutGrid.Children.Add(_stripContainer);
            Grid.SetColumn(_stripContainer, 0);
            _layoutGrid.Children.Add(_contentArea);
            Grid.SetColumn(_contentArea, 1);
        }
        else // Top
        {
            _tabStrip.Orientation = Orientation.Horizontal;
            _tabStrip.Margin = new Thickness(10, 6);
            _stripContainer.Width = double.NaN;
            _stripContainer.Height = TopStripHeight;
            _stripContainer.BorderThickness = new Thickness(0, 0, 0, 1);

            _indicator.HorizontalAlignment = HorizontalAlignment.Left;
            _indicator.VerticalAlignment = VerticalAlignment.Bottom;
            _indicator.Height = 3;
            _indicator.Width = 0;
            _indicator.Margin = new Thickness(0, 0, 0, 2);

            _layoutGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            _layoutGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));

            _layoutGrid.Children.Add(_stripContainer);
            Grid.SetRow(_stripContainer, 0);
            _layoutGrid.Children.Add(_contentArea);
            Grid.SetRow(_contentArea, 1);
        }

        RebuildHeaders();
        UpdateContentArea();
        Dispatcher.UIThread.Post(() => UpdateIndicatorTarget(animate: false), DispatcherPriority.Loaded);
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildHeaders();
        if (Items.Count == 0)
        {
            SelectedIndex = -1;
        }
        else if (SelectedIndex < 0 || SelectedIndex >= Items.Count)
        {
            SelectedIndex = 0;
        }
        else
        {
            UpdateContentArea();
        }
        RefreshTabVisuals();
        Dispatcher.UIThread.Post(() => UpdateIndicatorTarget(animate: false), DispatcherPriority.Loaded);
    }

    #endregion

    #region Tab Headers

    private void RebuildHeaders()
    {
        _tabStrip.Children.Clear();
        _tabHeaders.Clear();

        for (int i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            var header = BuildTabHeader(item);
            _tabHeaders[item] = header;
            _tabStrip.Children.Add(header);
        }
    }

    private Border BuildTabHeader(DOSITabItem item)
    {
        var glyphText = !string.IsNullOrEmpty(item.Glyph) ? new TextBlock
        {
            Text = item.Glyph,
            FontSize = 14,
            Foreground = Accents.TextPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 18,
            TextAlignment = TextAlignment.Center
        } : null;

        var titleText = new TextBlock
        {
            Text = item.Header,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center
        };

        Control labelHost;
        if (!string.IsNullOrEmpty(item.Subtitle) && TabPlacement == DOSITabPlacement.Left)
        {
            var subtitleText = new TextBlock
            {
                Text = item.Subtitle,
                FontSize = 11,
                Foreground = Accents.TextSecondaryBrush,
                Opacity = 0.85
            };
            labelHost = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 1,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { titleText, subtitleText }
            };
        }
        else
        {
            labelHost = titleText;
        }

        var contentStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (glyphText != null) contentStack.Children.Add(glyphText);
        contentStack.Children.Add(labelHost);

        var header = new Border
        {
            Padding = TabPlacement == DOSITabPlacement.Left
                ? new Thickness(12, 9)
                : new Thickness(14, 8),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = contentStack,
            HorizontalAlignment = TabPlacement == DOSITabPlacement.Left
                ? HorizontalAlignment.Stretch
                : HorizontalAlignment.Left
        };

        header.PointerEntered += (_, _) =>
        {
            if (Items.IndexOf(item) != SelectedIndex)
                header.Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
        };
        header.PointerExited += (_, _) =>
        {
            if (Items.IndexOf(item) != SelectedIndex)
                header.Background = Brushes.Transparent;
        };
        header.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            var idx = Items.IndexOf(item);
            if (idx >= 0) SelectedIndex = idx;
        };

        return header;
    }

    private void RefreshTabVisuals()
    {
        for (int i = 0; i < Items.Count; i++)
        {
            if (!_tabHeaders.TryGetValue(Items[i], out var header)) continue;

            if (i == SelectedIndex)
            {
                header.Background = new SolidColorBrush(
                    Color.FromArgb(60, Accents.AccentPrimary.R, Accents.AccentPrimary.G, Accents.AccentPrimary.B));
                ApplySelectedHeaderText(header, true);
            }
            else
            {
                header.Background = Brushes.Transparent;
                ApplySelectedHeaderText(header, false);
            }
        }
    }

    private void ApplySelectedHeaderText(Border header, bool selected)
    {
        if (header.Child is not StackPanel stack) return;

        foreach (var child in stack.Children)
        {
            switch (child)
            {
                case TextBlock tb:
                    tb.Foreground = selected ? Accents.AccentPrimaryBrush : Accents.TextPrimaryBrush;
                    tb.FontWeight = selected ? FontWeight.Bold : FontWeight.SemiBold;
                    break;
                case StackPanel inner:
                    if (inner.Children.Count > 0 && inner.Children[0] is TextBlock title)
                    {
                        title.Foreground = selected ? Accents.AccentPrimaryBrush : Accents.TextPrimaryBrush;
                        title.FontWeight = selected ? FontWeight.Bold : FontWeight.SemiBold;
                    }
                    break;
            }
        }
    }

    #endregion

    #region Selection / Content

    private void OnSelectionPropertyChanged()
    {
        if (SelectedIndex < -1) SelectedIndex = -1;
        if (SelectedIndex >= Items.Count) SelectedIndex = Items.Count - 1;

        UpdateContentArea();
        RefreshTabVisuals();
        UpdateIndicatorTarget(animate: true);

        if (SelectedItem != null)
            SelectionChanged?.Invoke(this, SelectedItem);
    }

    private void UpdateContentArea()
    {
        var item = SelectedItem;
        _contentArea.Child = item?.GetOrCreateContent();
    }

    #endregion

    #region Indicator Animation

    private void UpdateIndicatorTarget(bool animate)
    {
        if (SelectedIndex < 0 || SelectedIndex >= Items.Count)
        {
            _indicator.IsVisible = false;
            return;
        }
        _indicator.IsVisible = true;

        if (!_tabHeaders.TryGetValue(Items[SelectedIndex], out var header)) return;

        // Defer until layout has run so Bounds are valid.
        if (header.Bounds.Width <= 0 || header.Bounds.Height <= 0)
        {
            Dispatcher.UIThread.Post(() => UpdateIndicatorTarget(animate), DispatcherPriority.Loaded);
            return;
        }

        if (TabPlacement == DOSITabPlacement.Left)
        {
            _indicatorTarget = header.Bounds.Y + 6;
            _indicatorTargetSize = Math.Max(0, header.Bounds.Height - 12);
        }
        else
        {
            _indicatorTarget = header.Bounds.X + 8;
            _indicatorTargetSize = Math.Max(0, header.Bounds.Width - 16);
        }

        if (!animate)
        {
            _indicatorCurrent = _indicatorTarget;
            _indicatorCurrentSize = _indicatorTargetSize;
            ApplyIndicatorPosition();
            return;
        }

        _indicatorTimer?.Stop();

        var startPos = _indicatorCurrent;
        var startSize = _indicatorCurrentSize;
        var endPos = _indicatorTarget;
        var endSize = _indicatorTargetSize;
        var startTime = DateTime.UtcNow;

        _indicatorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _indicatorTimer.Tick += (s, _) =>
        {
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            var t = Math.Clamp(elapsed / IndicatorAnimMs, 0.0, 1.0);
            // Ease-out cubic
            var eased = 1 - Math.Pow(1 - t, 3);

            _indicatorCurrent = startPos + (endPos - startPos) * eased;
            _indicatorCurrentSize = startSize + (endSize - startSize) * eased;
            ApplyIndicatorPosition();

            if (t >= 1.0)
            {
                _indicatorTimer?.Stop();
                _indicatorTimer = null;
            }
        };
        _indicatorTimer.Start();
    }

    private void ApplyIndicatorPosition()
    {
        if (TabPlacement == DOSITabPlacement.Left)
        {
            _indicator.Margin = new Thickness(2, _indicatorCurrent, 0, 0);
            _indicator.Height = _indicatorCurrentSize;
            _indicator.Width = 3;
        }
        else
        {
            _indicator.Margin = new Thickness(_indicatorCurrent, 0, 0, 2);
            _indicator.Width = _indicatorCurrentSize;
            _indicator.Height = 3;
        }
    }

    #endregion

    #region Accent

    private bool _contentRebuildPending;

    private void OnAccentChanged(object? sender, EventArgs e)
    {
        _stripContainer.Background = Accents.WindowChromeBrush;
        _stripContainer.BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
        _contentArea.Background = Accents.WindowContentBrush;
        _indicator.Background = Accents.AccentGradientBrush;

        // Tab strip headers are owned by us, so rebuilding them here picks up
        // fresh TextPrimary / TextSecondary brushes for the current accent.
        RebuildHeaders();
        RefreshTabVisuals();
        Dispatcher.UIThread.Post(() => UpdateIndicatorTarget(animate: false), DispatcherPriority.Loaded);

        // Tab BODIES are produced by the consumer's ContentFactory and cached
        // on first display. Those cached visuals captured accent brushes at
        // build time, so they don't repaint on a live accent flip - the user
        // sees stale text colors until the window is reopened.
        //
        // ApplyAccentAnimated fires AccentChanged on EVERY interpolation tick
        // (~28 times over a 450ms transition). Rebuilding the tab body on each
        // tick (a) thrashes the layout system, (b) captures intermediate
        // palette values, and (c) starves the final correct rebuild. Coalesce
        // every burst of AccentChanged events into a single rebuild that runs
        // once the dispatcher is idle (i.e. when the animation has settled).
        if (_contentRebuildPending) return;
        _contentRebuildPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _contentRebuildPending = false;
            foreach (var item in Items)
                item.InvalidateContent();
            UpdateContentArea();
        }, DispatcherPriority.ContextIdle);
    }

    #endregion

    #region Layout overrides

    protected override Size MeasureOverride(Size availableSize)
    {
        _root.Measure(availableSize);
        return _root.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _root.Arrange(new Rect(finalSize));
        Dispatcher.UIThread.Post(() => UpdateIndicatorTarget(animate: false), DispatcherPriority.Background);
        return finalSize;
    }

    #endregion
}
