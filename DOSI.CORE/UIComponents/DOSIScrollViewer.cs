using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DOSI.CORE.AccentManagement;

namespace DOSI.CORE.UIComponents;

/// <summary>
/// A custom ScrollViewer that uses the DOSIScrollBar for beautiful accent-aware scrolling.
/// Supports smooth scrolling animations and accent integration.
/// </summary>
public class DOSIScrollViewer : Control
{
    #region Fields

    private readonly DOSIScrollBar _verticalScrollBar;
    private readonly DOSIScrollBar _horizontalScrollBar;
    private readonly Border _contentBorder;
    private double _horizontalOffset;
    private double _verticalOffset;
    private Size _extent;
    private Size _viewport;

    private static AccentManager Accents => AccentManager.Instance;

    #endregion

    #region Styled Properties

    public static readonly StyledProperty<Control?> ContentProperty =
        AvaloniaProperty.Register<DOSIScrollViewer, Control?>(nameof(Content));

    public static readonly StyledProperty<ScrollBarVisibility> VerticalScrollBarVisibilityProperty =
        AvaloniaProperty.Register<DOSIScrollViewer, ScrollBarVisibility>(nameof(VerticalScrollBarVisibility), 
            defaultValue: ScrollBarVisibility.Auto);

    public static readonly StyledProperty<ScrollBarVisibility> HorizontalScrollBarVisibilityProperty =
        AvaloniaProperty.Register<DOSIScrollViewer, ScrollBarVisibility>(nameof(HorizontalScrollBarVisibility), 
            defaultValue: ScrollBarVisibility.Disabled);

    public static readonly StyledProperty<bool> ShowScrollButtonsProperty =
        AvaloniaProperty.Register<DOSIScrollViewer, bool>(nameof(ShowScrollButtons), defaultValue: true);

    #endregion

    #region Properties

    public Control? Content
    {
        get => GetValue(ContentProperty);
        set => SetValue(ContentProperty, value);
    }

    public ScrollBarVisibility VerticalScrollBarVisibility
    {
        get => GetValue(VerticalScrollBarVisibilityProperty);
        set => SetValue(VerticalScrollBarVisibilityProperty, value);
    }

    public ScrollBarVisibility HorizontalScrollBarVisibility
    {
        get => GetValue(HorizontalScrollBarVisibilityProperty);
        set => SetValue(HorizontalScrollBarVisibilityProperty, value);
    }

    public bool ShowScrollButtons
    {
        get => GetValue(ShowScrollButtonsProperty);
        set => SetValue(ShowScrollButtonsProperty, value);
    }

    public double HorizontalOffset => _horizontalOffset;
    public double VerticalOffset => _verticalOffset;
    public Size Extent => _extent;
    public Size Viewport => _viewport;

    #endregion

    #region Constructor

    static DOSIScrollViewer()
    {
        // Suppress the default white focus rectangle on this DOSI control.
        FocusAdornerProperty.OverrideDefaultValue<DOSIScrollViewer>(null);

        ContentProperty.Changed.AddClassHandler<DOSIScrollViewer>((sv, e) => sv.OnContentChanged(e));
        ShowScrollButtonsProperty.Changed.AddClassHandler<DOSIScrollViewer>((sv, e) => sv.UpdateScrollBarSettings());
    }

    public DOSIScrollViewer()
    {
        ClipToBounds = true;

        // Suppress the default Fluent focus rectangle (white outline). Setting this
        // as a local value overrides the accent style that re-applies it.
        FocusAdorner = null;

        _contentBorder = new Border
        {
            ClipToBounds = true
        };

        _verticalScrollBar = new DOSIScrollBar
        {
            Orientation = Orientation.Vertical,
            SmallChange = 20,
            LargeChange = 100,
            ShowButtons = true
        };
        _verticalScrollBar.Scroll += OnVerticalScroll;

        _horizontalScrollBar = new DOSIScrollBar
        {
            Orientation = Orientation.Horizontal,
            SmallChange = 20,
            LargeChange = 100,
            ShowButtons = true
        };
        _horizontalScrollBar.Scroll += OnHorizontalScroll;

        // Add children
        VisualChildren.Add(_contentBorder);
        VisualChildren.Add(_verticalScrollBar);
        VisualChildren.Add(_horizontalScrollBar);

        LogicalChildren.Add(_contentBorder);
        LogicalChildren.Add(_verticalScrollBar);
        LogicalChildren.Add(_horizontalScrollBar);
    }

    #endregion

    #region Content Changed

    private void OnContentChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (e.OldValue is Control oldContent)
        {
            _contentBorder.Child = null;
        }

        if (e.NewValue is Control newContent)
        {
            _contentBorder.Child = newContent;
        }

        InvalidateMeasure();
    }

    private void UpdateScrollBarSettings()
    {
        _verticalScrollBar.ShowButtons = ShowScrollButtons;
        _horizontalScrollBar.ShowButtons = ShowScrollButtons;
    }

    #endregion

    #region Scrolling

    private void OnVerticalScroll(object? sender, ScrollEventArgs e)
    {
        _verticalOffset = e.NewValue;
        UpdateContentPosition();
    }

    private void OnHorizontalScroll(object? sender, ScrollEventArgs e)
    {
        _horizontalOffset = e.NewValue;
        UpdateContentPosition();
    }

    private void UpdateContentPosition()
    {
        if (_contentBorder.Child != null)
        {
            _contentBorder.Child.RenderTransform = new TranslateTransform(-_horizontalOffset, -_verticalOffset);
        }
    }

    public void ScrollToTop()
    {
        _verticalScrollBar.Value = 0;
    }

    public void ScrollToBottom()
    {
        _verticalScrollBar.Value = _verticalScrollBar.Maximum;
    }

    public void ScrollToHome()
    {
        _horizontalScrollBar.Value = 0;
    }

    public void ScrollToEnd()
    {
        _horizontalScrollBar.Value = _horizontalScrollBar.Maximum;
    }

    #endregion

    #region Layout

    protected override Size MeasureOverride(Size availableSize)
    {
        // Measure content with infinite size only on the axis that can scroll.
        // The other axis is constrained to the available space so panels like
        // WrapPanel know how wide/tall they may grow before wrapping.
        var content = _contentBorder.Child;
        if (content != null)
        {
            // Reserve scrollbar space on the constrained axis so wrapping math is correct.
            var measureWidth = HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled
                ? Math.Max(0, availableSize.Width - (VerticalScrollBarVisibility != ScrollBarVisibility.Disabled ? 14.0 : 0))
                : double.PositiveInfinity;

            var measureHeight = VerticalScrollBarVisibility == ScrollBarVisibility.Disabled
                ? Math.Max(0, availableSize.Height - (HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled ? 14.0 : 0))
                : double.PositiveInfinity;

            content.Measure(new Size(measureWidth, measureHeight));
            _extent = content.DesiredSize;
        }
        else
        {
            _extent = new Size(0, 0);
        }

        // Report a finite desired size so parents (StackPanel, etc.) lay us out correctly
        // even when they offer infinite space on a given axis.
        var desiredWidth = double.IsInfinity(availableSize.Width) ? _extent.Width : availableSize.Width;
        var desiredHeight = double.IsInfinity(availableSize.Height) ? _extent.Height : availableSize.Height;
        return new Size(desiredWidth, desiredHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _viewport = finalSize;

        // Calculate if scrollbars are needed
        var showVertical = ShouldShowScrollBar(VerticalScrollBarVisibility, _extent.Height, _viewport.Height);
        var showHorizontal = ShouldShowScrollBar(HorizontalScrollBarVisibility, _extent.Width, _viewport.Width);

        // Calculate dimensions
        var scrollBarWidth = showVertical ? 14.0 : 0;
        var scrollBarHeight = showHorizontal ? 14.0 : 0;
        var contentWidth = finalSize.Width - scrollBarWidth;
        var contentHeight = finalSize.Height - scrollBarHeight;

        // Arrange content
        _contentBorder.Arrange(new Rect(0, 0, contentWidth, contentHeight));

        // Update and arrange vertical scrollbar
        if (showVertical)
        {
            _verticalScrollBar.IsVisible = true;
            _verticalScrollBar.Maximum = Math.Max(0, _extent.Height - contentHeight);
            _verticalScrollBar.ViewportSize = contentHeight;
            _verticalScrollBar.Arrange(new Rect(contentWidth, 0, scrollBarWidth, contentHeight));
        }
        else
        {
            _verticalScrollBar.IsVisible = false;
            _verticalOffset = 0;
        }

        // Update and arrange horizontal scrollbar
        if (showHorizontal)
        {
            _horizontalScrollBar.IsVisible = true;
            _horizontalScrollBar.Maximum = Math.Max(0, _extent.Width - contentWidth);
            _horizontalScrollBar.ViewportSize = contentWidth;
            _horizontalScrollBar.Arrange(new Rect(0, contentHeight, contentWidth, scrollBarHeight));
        }
        else
        {
            _horizontalScrollBar.IsVisible = false;
            _horizontalOffset = 0;
        }

        // Clamp scroll values
        _verticalScrollBar.Value = Math.Clamp(_verticalScrollBar.Value, 0, _verticalScrollBar.Maximum);
        _horizontalScrollBar.Value = Math.Clamp(_horizontalScrollBar.Value, 0, _horizontalScrollBar.Maximum);

        UpdateContentPosition();

        return finalSize;
    }

    private bool ShouldShowScrollBar(ScrollBarVisibility visibility, double extent, double viewport)
    {
        return visibility switch
        {
            ScrollBarVisibility.Visible => true,
            ScrollBarVisibility.Hidden => false,
            ScrollBarVisibility.Disabled => false,
            ScrollBarVisibility.Auto => extent > viewport,
            _ => false
        };
    }

    #endregion

    #region Input Handling

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        if (_verticalScrollBar.IsVisible)
        {
            var delta = e.Delta.Y * 50;
            _verticalScrollBar.Value = Math.Clamp(_verticalScrollBar.Value - delta, 0, _verticalScrollBar.Maximum);
            e.Handled = true;
        }
        else if (_horizontalScrollBar.IsVisible)
        {
            var delta = e.Delta.X * 50;
            _horizontalScrollBar.Value = Math.Clamp(_horizontalScrollBar.Value - delta, 0, _horizontalScrollBar.Maximum);
            e.Handled = true;
        }
    }

    #endregion
}
