using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Reactive;
using DOSI.CORE.AccentManagement;
using AvPath = Avalonia.Controls.Shapes.Path;

namespace DOSI.CORE.UIComponents;

/// <summary>
/// Themed dropdown selector for the DOSI control family. A flat pill that
/// opens a popup list of items underneath it on click; selecting an item
/// closes the popup and raises <see cref="SelectionChanged"/>. Items are
/// plain strings (kept deliberately simple - the property panel + designer
/// don't need a templated item shape, just labels + a callback).
///
/// Why hand-roll one instead of using Avalonia's <c>ComboBox</c>: the
/// stock ComboBox drags in Fluent theme styles that don't match the rest
/// of the DOSI control set, and its popup is not theme-aware. Building it
/// from primitives keeps it visually consistent (same chrome / hover /
/// accent treatment as <c>DOSIButton</c> + <c>DOSITextBox</c>) and lets
/// us re-tint live on accent change like every other DOSI control.
/// </summary>
public class DOSIDropDown : ContentControl
{
    private static AccentManager Accents => AccentManager.Instance;

    private readonly Border _root;
    private readonly TextBlock _label;
    private readonly AvPath _chevron;
    private readonly Popup _popup;
    private readonly StackPanel _itemsHost;

    /// <summary>The items currently rendered in the popup.</summary>
    private List<string> _items = new();

    public static readonly StyledProperty<string?> PlaceholderProperty =
        AvaloniaProperty.Register<DOSIDropDown, string?>(nameof(Placeholder), "Select…");

    public string? Placeholder
    {
        get => GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    public static readonly StyledProperty<string?> SelectedItemProperty =
        AvaloniaProperty.Register<DOSIDropDown, string?>(nameof(SelectedItem));

    public string? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    /// <summary>Raised after the user picks an item from the popup.</summary>
    public event EventHandler<string>? SelectionChanged;

    public DOSIDropDown()
    {
        _label = new TextBlock
        {
            Text = Placeholder ?? "",
            FontSize = 12,
            Foreground = Accents.TextPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        // Down-chevron rendered as a Path so it scales cleanly with the
        // text and tints with the foreground brush.
        _chevron = new AvPath
        {
            Data = Geometry.Parse("M 0 0 L 4 4 L 8 0 Z"),
            Fill = Accents.TextSecondaryBrush,
            Width = 8,
            Height = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };

        var rowGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            VerticalAlignment = VerticalAlignment.Center
        };
        rowGrid.Children.Add(_label);   Grid.SetColumn(_label, 0);
        rowGrid.Children.Add(_chevron); Grid.SetColumn(_chevron, 1);

        _root = new Border
        {
            Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(4),
            // Accent-aware surfaces: translucent-white over a light
            // window surface is invisible (the "dropdown looks weird
            // on light theme" complaint). Use ControlBackground/Border
            // brushes so the trigger pill picks up the right contrast
            // automatically on every accent.
            Background = BuildIdleBackground(),
            BorderBrush = BuildIdleBorder(),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = rowGrid,
            MinHeight = 28
        };

        // Popup holds the items list. Placement under the trigger Border;
        // sized to match the trigger's width so long labels don't reflow.
        // Constructed BEFORE the _root pointer handlers so they can safely
        // capture _popup without nullable-warning gymnastics.
        _itemsHost = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 0
        };
        var popupChrome = new Border
        {
            Background = Accents.WindowChromeBrush,
            BorderBrush = new SolidColorBrush(Accents.ControlBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(2),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0, OffsetY = 4, Blur = 12, Spread = 0,
                Color = Accents.ShadowColor
            }),
            Child = _itemsHost
        };
        _popup = new Popup
        {
            PlacementTarget = _root,
            Placement = PlacementMode.Bottom,
            HorizontalOffset = 0,
            VerticalOffset = 4,
            IsLightDismissEnabled = true,
            Child = popupChrome
        };

        _root.PointerEntered += (_, _) =>
            _root.Background = BuildHoverBackground();
        _root.PointerExited += (_, _) =>
        {
            if (!_popup.IsOpen)
                _root.Background = BuildIdleBackground();
        };
        _root.PointerReleased += (_, e) =>
        {
            e.Handled = true;
            _popup.IsOpen = !_popup.IsOpen;
        };

        var hostGrid = new Grid { Children = { _root, _popup } };
        Content = hostGrid;

        // Keep label in sync with selection / placeholder.
        this.GetObservable(SelectedItemProperty).Subscribe(new AnonymousObserver<string?>(_ => RefreshLabel()));
        this.GetObservable(PlaceholderProperty).Subscribe(new AnonymousObserver<string?>(_ => RefreshLabel()));
        RefreshLabel();

        // Live-tint on accent change so a Light <-> dark flip while the
        // dropdown is mounted recolours its trigger + popup chrome.
        // Subscribe / unsubscribe in lockstep with the visual tree to
        // avoid leaking the static accent event handler.
        AttachedToVisualTree += (_, _) => Accents.AccentChanged += OnAccentChangedInternal;
        DetachedFromVisualTree += (_, _) => Accents.AccentChanged -= OnAccentChangedInternal;
    }

    private void OnAccentChangedInternal(object? sender, EventArgs e)
    {
        _root.Background = BuildIdleBackground();
        _root.BorderBrush = BuildIdleBorder();
        _label.Foreground = string.IsNullOrEmpty(SelectedItem)
            ? Accents.TextSecondaryBrush
            : Accents.TextPrimaryBrush;
        _chevron.Fill = Accents.TextSecondaryBrush;
    }

    /// <summary>
    /// The dropdown trigger's idle background. <see cref="AccentManager.ControlBackground"/>
    /// is theme-aware (dark surfaces under dark accents, light under
    /// the Light accent) so the pill stays visible on every theme.
    /// </summary>
    private static IBrush BuildIdleBackground()
        => new SolidColorBrush(Accents.ControlBackground);

    private static IBrush BuildHoverBackground()
        => new SolidColorBrush(Accents.ControlBackgroundHover);

    private static IBrush BuildIdleBorder()
        => new SolidColorBrush(Accents.ControlBorder);

    private void RefreshLabel()
    {
        var sel = SelectedItem;
        if (string.IsNullOrEmpty(sel))
        {
            _label.Text = Placeholder ?? "";
            _label.Foreground = Accents.TextSecondaryBrush;
        }
        else
        {
            _label.Text = sel;
            _label.Foreground = Accents.TextPrimaryBrush;
        }
    }

    /// <summary>
    /// Replace the popup's item list. Call this whenever the available
    /// options change; selection is preserved if the previous value is
    /// still in the new list, otherwise cleared.
    /// </summary>
    public void SetItems(IEnumerable<string> items)
    {
        _items = new List<string>(items);
        _itemsHost.Children.Clear();
        foreach (var item in _items)
        {
            var captured = item;
            var row = new Border
            {
                Padding = new Thickness(10, 6),
                CornerRadius = new CornerRadius(3),
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new TextBlock
                {
                    Text = captured,
                    FontSize = 12,
                    Foreground = Accents.TextPrimaryBrush
                }
            };
            row.PointerEntered += (_, _) => row.Background = Accents.ButtonBackgroundHoverBrush;
            row.PointerExited  += (_, _) => row.Background = Brushes.Transparent;
            row.PointerReleased += (_, e) =>
            {
                e.Handled = true;
                _popup.IsOpen = false;
                SelectedItem = captured;
                SelectionChanged?.Invoke(this, captured);
            };
            _itemsHost.Children.Add(row);
        }

        if (SelectedItem != null && !_items.Contains(SelectedItem))
            SelectedItem = null;
    }
}
