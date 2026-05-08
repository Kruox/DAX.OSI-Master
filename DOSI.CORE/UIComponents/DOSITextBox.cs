using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using DOSI.CORE.AccentManagement;

namespace DOSI.CORE.UIComponents;

/// <summary>
/// A 100% custom-drawn TextBox control for the DOSI operating system.
/// Supports rounded or square corners, custom theming, and full text editing.
/// </summary>
public class DOSITextBox : Control
{
    #region Fields

    private string _text = "";
    private int _cursorPosition;
    private int _selectionStart = -1;
    private int _selectionEnd = -1;
    private bool _isFocused;
    private bool _cursorVisible = true;
    private DispatcherTimer? _cursorTimer;
    private double _scrollOffset;
    private bool _isMouseSelecting;

    // Cached typeface - never changes, no need to allocate one per render or
    // per text measurement. FormattedText copies what it needs from this so
    // it's safe to share across all instances.
    private static readonly Typeface s_typeface = new(FontFamily.Default);

    // Cached selection-fill brush. Rebuilt only when the accent changes,
    // not on every render (Render fires once per cursor-blink interval).
    private IImmutableSolidColorBrush? _selectionFillBrush;

    private static AccentManager Accents => AccentManager.Instance;

    #endregion

    #region Styled Properties

    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<DOSITextBox, string>(nameof(Text), defaultValue: "");

    public static readonly StyledProperty<string> PlaceholderTextProperty =
        AvaloniaProperty.Register<DOSITextBox, string>(nameof(PlaceholderText), defaultValue: "");

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<DOSITextBox, double>(nameof(FontSize), defaultValue: 14.0);

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<DOSITextBox, CornerRadius>(nameof(CornerRadius), defaultValue: new CornerRadius(4));

    public static readonly StyledProperty<Thickness> PaddingProperty =
        AvaloniaProperty.Register<DOSITextBox, Thickness>(nameof(Padding), defaultValue: new Thickness(10, 6));

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<DOSITextBox, IBrush?>(nameof(Background));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<DOSITextBox, IBrush?>(nameof(Foreground));

    public static readonly StyledProperty<IBrush?> BorderBrushProperty =
        AvaloniaProperty.Register<DOSITextBox, IBrush?>(nameof(BorderBrush));

    public static readonly StyledProperty<double> BorderThicknessProperty =
        AvaloniaProperty.Register<DOSITextBox, double>(nameof(BorderThickness), defaultValue: 1.0);

    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<DOSITextBox, bool>(nameof(IsReadOnly), defaultValue: false);

    public static readonly StyledProperty<bool> UseRoundedEndsProperty =
        AvaloniaProperty.Register<DOSITextBox, bool>(nameof(UseRoundedEnds), defaultValue: false);

    public static readonly StyledProperty<bool> UsePasswordCharProperty =
        AvaloniaProperty.Register<DOSITextBox, bool>(nameof(UsePasswordChar), defaultValue: false);

    public static readonly StyledProperty<char> PasswordCharProperty =
        AvaloniaProperty.Register<DOSITextBox, char>(nameof(PasswordChar), defaultValue: '\u2022');

    #endregion

    #region Properties

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string PlaceholderText
    {
        get => GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public Thickness Padding
    {
        get => GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush? BorderBrush
    {
        get => GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    public double BorderThickness
    {
        get => GetValue(BorderThicknessProperty);
        set => SetValue(BorderThicknessProperty, value);
    }

    public bool IsReadOnly
    {
        get => GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    /// <summary>
    /// When true, corner radius is automatically set to half the height for pill-shaped ends.
    /// </summary>
    public bool UseRoundedEnds
    {
        get => GetValue(UseRoundedEndsProperty);
        set => SetValue(UseRoundedEndsProperty, value);
    }

    /// <summary>
    /// When true, the visible text is replaced with <see cref="PasswordChar"/>
    /// so the underlying value (still available via <see cref="Text"/>) is masked.
    /// Caret and selection positions remain index-accurate.
    /// </summary>
    public bool UsePasswordChar
    {
        get => GetValue(UsePasswordCharProperty);
        set => SetValue(UsePasswordCharProperty, value);
    }

    /// <summary>
    /// Character drawn in place of every text char when <see cref="UsePasswordChar"/>
    /// is true. Defaults to the bullet glyph (•).
    /// </summary>
    public char PasswordChar
    {
        get => GetValue(PasswordCharProperty);
        set => SetValue(PasswordCharProperty, value);
    }

    /// <summary>
    /// The text we actually render. Same length as <see cref="Text"/> so caret
    /// and selection indices line up regardless of masking.
    /// </summary>
    private string DisplayText => UsePasswordChar
        ? new string(PasswordChar, _text.Length)
        : _text;

    public int SelectionStartIndex => Math.Min(_selectionStart, _selectionEnd);
    public int SelectionEndIndex => Math.Max(_selectionStart, _selectionEnd);
    public bool HasSelection => _selectionStart >= 0 && _selectionEnd >= 0 && _selectionStart != _selectionEnd;
    public string SelectedText => HasSelection ? _text.Substring(SelectionStartIndex, SelectionEndIndex - SelectionStartIndex) : "";

    #endregion

    #region Events

    public event EventHandler<TextChangedEventArgs>? TextChanged;

    #endregion

    #region Constructor

    static DOSITextBox()
    {
        FocusableProperty.OverrideDefaultValue<DOSITextBox>(true);
        // Suppress the default white focus rectangle; we draw an accent-coloured
        // border in Render() when focused.
        FocusAdornerProperty.OverrideDefaultValue<DOSITextBox>(null);

        TextProperty.Changed.AddClassHandler<DOSITextBox>((tb, e) => tb.OnTextPropertyChanged(e));
        PlaceholderTextProperty.Changed.AddClassHandler<DOSITextBox>((tb, e) => tb.InvalidateVisual());
        FontSizeProperty.Changed.AddClassHandler<DOSITextBox>((tb, e) => tb.InvalidateVisual());
        UseRoundedEndsProperty.Changed.AddClassHandler<DOSITextBox>((tb, e) => tb.InvalidateVisual());
        UsePasswordCharProperty.Changed.AddClassHandler<DOSITextBox>((tb, e) => tb.InvalidateVisual());
        PasswordCharProperty.Changed.AddClassHandler<DOSITextBox>((tb, e) => tb.InvalidateVisual());
    }

    public DOSITextBox()
    {
        Cursor = new Cursor(StandardCursorType.Ibeam);

        // Suppress the default Fluent focus rectangle (white outline). Setting this
        // as a local value overrides the accent style that re-applies it.
        FocusAdorner = null;

        // Set default accent colors
        Background = Accents.ControlBackgroundBrush;
        Foreground = Accents.TextPrimaryBrush;
        BorderBrush = new SolidColorBrush(Accents.ControlBorder);

        // Focus handling via events
        GotFocus += OnGotFocusHandler;
        LostFocus += OnLostFocusHandler;

        // Subscribe to accent changes
        AttachedToVisualTree += (s, e) => Accents.AccentChanged += OnAccentChanged;
        DetachedFromVisualTree += (s, e) =>
        {
            Accents.AccentChanged -= OnAccentChanged;
            // Stop the cursor blink timer if the control is removed from the
            // visual tree while still focused - otherwise the timer's tick
            // closure pins this control alive indefinitely.
            StopCursorBlink();
        };
    }

    #endregion

    #region Property Changed Handlers

    private void OnTextPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        _text = e.NewValue as string ?? "";
        _cursorPosition = Math.Min(_cursorPosition, _text.Length);
        ClearSelection();
        InvalidateVisual();
        TextChanged?.Invoke(this, new TextChangedEventArgs());
    }

    private void OnAccentChanged(object? sender, EventArgs e)
    {
        Background = Accents.ControlBackgroundBrush;
        Foreground = Accents.TextPrimaryBrush;
        BorderBrush = new SolidColorBrush(Accents.ControlBorder);
        // Drop the cached selection brush - the accent's RGB just changed,
        // so the next Render will rebuild it from the new palette.
        _selectionFillBrush = null;
        InvalidateVisual();
    }

    private IImmutableSolidColorBrush GetOrBuildSelectionBrush()
    {
        var a = Accents.AccentPrimary;
        return (IImmutableSolidColorBrush)new SolidColorBrush(Color.FromArgb(100, a.R, a.G, a.B)).ToImmutable();
    }

    #endregion

    #region Rendering

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(0, 0, Bounds.Width, Bounds.Height);
        var cornerRadius = UseRoundedEnds ? new CornerRadius(bounds.Height / 2) : CornerRadius;
        var padding = Padding;

        // Draw background
        var backgroundBrush = Background ?? Accents.ControlBackgroundBrush;
        var geometry = CreateRoundedRectGeometry(bounds, cornerRadius);
        context.DrawGeometry(backgroundBrush, null, geometry);

        // Draw border
        var borderBrush = _isFocused 
            ? Accents.AccentPrimaryBrush 
            : (BorderBrush ?? new SolidColorBrush(Accents.ControlBorder));
        var borderPen = new Pen(borderBrush, BorderThickness);
        context.DrawGeometry(null, borderPen, geometry);

        // Calculate text area
        var textAreaLeft = padding.Left;
        var textAreaRight = bounds.Width - padding.Right;
        var textAreaWidth = textAreaRight - textAreaLeft;

        // Clip to text area
        using (context.PushClip(new Rect(textAreaLeft, 0, textAreaWidth, bounds.Height)))
        {
            var textY = (bounds.Height - FontSize * 1.2) / 2;
            var textX = textAreaLeft - _scrollOffset;
            var displayText = DisplayText;

            // Draw selection background
            if (HasSelection && _isFocused)
            {
                var selStart = GetTextWidth(displayText[..SelectionStartIndex]);
                var selEnd = GetTextWidth(displayText[..SelectionEndIndex]);
                var selRect = new Rect(
                    textX + selStart,
                    textY - 2,
                    selEnd - selStart,
                    FontSize * 1.2 + 4);

                _selectionFillBrush ??= GetOrBuildSelectionBrush();
                context.FillRectangle(_selectionFillBrush, selRect);
            }

            // Draw text or placeholder
            if (string.IsNullOrEmpty(_text) && !string.IsNullOrEmpty(PlaceholderText))
            {
                var placeholderText = CreateFormattedText(PlaceholderText, Accents.TextSecondaryBrush);
                context.DrawText(placeholderText, new Point(textX, textY));
            }
            else if (!string.IsNullOrEmpty(displayText))
            {
                var formattedText = CreateFormattedText(displayText, Foreground ?? Accents.TextPrimaryBrush);
                context.DrawText(formattedText, new Point(textX, textY));
            }

            // Draw cursor
            if (_isFocused && _cursorVisible && !IsReadOnly)
            {
                var cursorX = textX + GetTextWidth(displayText[.._cursorPosition]);
                var cursorPen = new Pen(Foreground ?? Accents.TextPrimaryBrush, 1.5);
                context.DrawLine(cursorPen,
                    new Point(cursorX, textY - 1),
                    new Point(cursorX, textY + FontSize * 1.2 + 1));
            }
        }
    }

    private StreamGeometry CreateRoundedRectGeometry(Rect rect, CornerRadius radius)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();

        var topLeft = Math.Min(radius.TopLeft, Math.Min(rect.Width / 2, rect.Height / 2));
        var topRight = Math.Min(radius.TopRight, Math.Min(rect.Width / 2, rect.Height / 2));
        var bottomRight = Math.Min(radius.BottomRight, Math.Min(rect.Width / 2, rect.Height / 2));
        var bottomLeft = Math.Min(radius.BottomLeft, Math.Min(rect.Width / 2, rect.Height / 2));

        ctx.BeginFigure(new Point(rect.Left + topLeft, rect.Top), true);
        
        ctx.LineTo(new Point(rect.Right - topRight, rect.Top));
        if (topRight > 0)
            ctx.ArcTo(new Point(rect.Right, rect.Top + topRight), new Size(topRight, topRight), 0, false, SweepDirection.Clockwise);
        
        ctx.LineTo(new Point(rect.Right, rect.Bottom - bottomRight));
        if (bottomRight > 0)
            ctx.ArcTo(new Point(rect.Right - bottomRight, rect.Bottom), new Size(bottomRight, bottomRight), 0, false, SweepDirection.Clockwise);
        
        ctx.LineTo(new Point(rect.Left + bottomLeft, rect.Bottom));
        if (bottomLeft > 0)
            ctx.ArcTo(new Point(rect.Left, rect.Bottom - bottomLeft), new Size(bottomLeft, bottomLeft), 0, false, SweepDirection.Clockwise);
        
        ctx.LineTo(new Point(rect.Left, rect.Top + topLeft));
        if (topLeft > 0)
            ctx.ArcTo(new Point(rect.Left + topLeft, rect.Top), new Size(topLeft, topLeft), 0, false, SweepDirection.Clockwise);

        ctx.EndFigure(true);
        return geometry;
    }

    private FormattedText CreateFormattedText(string text, IBrush brush)
    {
        return new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            s_typeface,
            FontSize,
            brush);
    }

    private double GetTextWidth(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        // FormattedText.Width strips trailing whitespace, which makes the caret
        // (and selection rectangle) appear to "stick" until the next non-space
        // character is typed. Measure the trimmed portion plus the trailing
        // spaces separately using a derived per-space width.
        int trailingSpaces = 0;
        int i = text.Length - 1;
        while (i >= 0 && text[i] == ' ')
        {
            trailingSpaces++;
            i--;
        }

        double width = 0;
        if (trailingSpaces < text.Length)
        {
            var head = text.Substring(0, text.Length - trailingSpaces);
            width += MeasureRaw(head);
        }

        if (trailingSpaces > 0)
        {
            // Difference between "x x" and "xx" yields the rendered space width
            // without being affected by trailing-whitespace trimming.
            var spaceWidth = MeasureRaw("x x") - MeasureRaw("xx");
            if (spaceWidth < 0) spaceWidth = 0;
            width += spaceWidth * trailingSpaces;
        }

        return width;
    }

    private double MeasureRaw(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var ft = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            s_typeface,
            FontSize,
            Brushes.Black);
        return ft.Width;
    }

    #endregion

    #region Focus Handling

    private void OnGotFocusHandler(object? sender, RoutedEventArgs e)
    {
        _isFocused = true;
        StartCursorBlink();
        InvalidateVisual();
    }

    private void OnLostFocusHandler(object? sender, RoutedEventArgs e)
    {
        _isFocused = false;
        StopCursorBlink();
        ClearSelection();
        InvalidateVisual();
    }

    private void StartCursorBlink()
    {
        _cursorVisible = true;
        _cursorTimer?.Stop();
        _cursorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _cursorTimer.Tick += (s, e) =>
        {
            _cursorVisible = !_cursorVisible;
            InvalidateVisual();
        };
        _cursorTimer.Start();
    }

    private void StopCursorBlink()
    {
        _cursorTimer?.Stop();
        _cursorTimer = null;
        _cursorVisible = true;
    }

    private void ResetCursorBlink()
    {
        if (_isFocused)
        {
            _cursorVisible = true;
            _cursorTimer?.Stop();
            _cursorTimer?.Start();
            InvalidateVisual();
        }
    }

    #endregion

    #region Mouse Handling

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        
        Focus();
        
        var point = e.GetPosition(this);
        var textPosition = GetCharacterIndexFromPoint(point);
        
        if (e.ClickCount == 2)
        {
            // Double-click: select word
            SelectWord(textPosition);
        }
        else if (e.ClickCount == 3)
        {
            // Triple-click: select all
            SelectAll();
        }
        else
        {
            // Single click: position cursor
            _cursorPosition = textPosition;
            
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _selectionStart >= 0)
            {
                _selectionEnd = _cursorPosition;
            }
            else
            {
                _selectionStart = _cursorPosition;
                _selectionEnd = _cursorPosition;
                _isMouseSelecting = true;
            }
        }
        
        ResetCursorBlink();
        EnsureCursorVisible();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        
        if (_isMouseSelecting)
        {
            var point = e.GetPosition(this);
            _cursorPosition = GetCharacterIndexFromPoint(point);
            _selectionEnd = _cursorPosition;
            EnsureCursorVisible();
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _isMouseSelecting = false;
    }

    private int GetCharacterIndexFromPoint(Point point)
    {
        var textX = Padding.Left - _scrollOffset;
        var clickX = point.X;
        var displayText = DisplayText;

        if (string.IsNullOrEmpty(displayText)) return 0;

        var targetWidth = clickX - textX;
        if (targetWidth <= 0) return 0;

        // Width of the full string is monotonically reached as we extend the
        // prefix, so we can binary-search the smallest prefix whose width
        // exceeds the click position. The previous linear scan called
        // GetTextWidth() N times per mouse-move event - each call allocates a
        // FormattedText - which made selection drags on long URLs measurably
        // sluggish. Binary search drops it to O(log N).
        int lo = 0;
        int hi = displayText.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            var w = GetTextWidth(displayText[..(mid + 1)]);
            if (w < targetWidth) lo = mid + 1;
            else hi = mid;
        }

        // lo is the first index whose prefix width >= targetWidth.
        // Snap to whichever side of the character is closer to the click.
        if (lo == 0) return 0;
        if (lo >= displayText.Length) return displayText.Length;

        var widthAtLo = GetTextWidth(displayText[..lo]);
        var widthAtNext = GetTextWidth(displayText[..(lo + 1)]);
        return (targetWidth - widthAtLo) < (widthAtNext - targetWidth) ? lo : lo + 1;
    }

    #endregion

    #region Keyboard Handling

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        
        switch (e.Key)
        {
            case Key.Left:
                MoveCursor(ctrl ? -GetWordBoundary(_cursorPosition, -1) : -1, shift);
                e.Handled = true;
                break;
                
            case Key.Right:
                MoveCursor(ctrl ? GetWordBoundary(_cursorPosition, 1) : 1, shift);
                e.Handled = true;
                break;
                
            case Key.Home:
                MoveCursorTo(0, shift);
                e.Handled = true;
                break;
                
            case Key.End:
                MoveCursorTo(_text.Length, shift);
                e.Handled = true;
                break;
                
            case Key.Back:
                if (!IsReadOnly)
                {
                    HandleBackspace(ctrl);
                    e.Handled = true;
                }
                break;
                
            case Key.Delete:
                if (!IsReadOnly)
                {
                    HandleDelete(ctrl);
                    e.Handled = true;
                }
                break;
                
            case Key.A when ctrl:
                SelectAll();
                e.Handled = true;
                break;
                
            case Key.C when ctrl:
                _ = CopyToClipboardAsync();
                e.Handled = true;
                break;

            case Key.X when ctrl:
                if (!IsReadOnly)
                {
                    _ = CutToClipboardAsync();
                    e.Handled = true;
                }
                break;

            case Key.V when ctrl:
                if (!IsReadOnly)
                {
                    _ = PasteFromClipboardAsync();
                    e.Handled = true;
                }
                break;
                
            case Key.Escape:
                ClearSelection();
                e.Handled = true;
                break;
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        
        if (IsReadOnly || string.IsNullOrEmpty(e.Text)) return;
        
        InsertText(e.Text);
        e.Handled = true;
    }

    #endregion

    #region Text Manipulation

    private void InsertText(string text)
    {
        if (HasSelection)
            DeleteSelection();
        
        _text = _text.Insert(_cursorPosition, text);
        _cursorPosition += text.Length;
        
        Text = _text;
        ResetCursorBlink();
        EnsureCursorVisible();
    }

    private void HandleBackspace(bool wholeWord)
    {
        if (HasSelection)
        {
            DeleteSelection();
        }
        else if (_cursorPosition > 0)
        {
            var deleteCount = wholeWord ? GetWordBoundary(_cursorPosition, -1) : 1;
            _text = _text.Remove(_cursorPosition - deleteCount, deleteCount);
            _cursorPosition -= deleteCount;
            Text = _text;
        }
        
        ResetCursorBlink();
        EnsureCursorVisible();
    }

    private void HandleDelete(bool wholeWord)
    {
        if (HasSelection)
        {
            DeleteSelection();
        }
        else if (_cursorPosition < _text.Length)
        {
            var deleteCount = wholeWord ? GetWordBoundary(_cursorPosition, 1) : 1;
            _text = _text.Remove(_cursorPosition, deleteCount);
            Text = _text;
        }
        
        ResetCursorBlink();
        EnsureCursorVisible();
    }

    private void DeleteSelection()
    {
        if (!HasSelection) return;
        
        _text = _text.Remove(SelectionStartIndex, SelectionEndIndex - SelectionStartIndex);
        _cursorPosition = SelectionStartIndex;
        ClearSelection();
        Text = _text;
    }

    private int GetWordBoundary(int position, int direction)
    {
        if (direction < 0)
        {
            if (position == 0) return 0;
            var i = position - 1;
            
            while (i > 0 && char.IsWhiteSpace(_text[i])) i--;
            while (i > 0 && !char.IsWhiteSpace(_text[i - 1])) i--;
            
            return position - i;
        }
        else
        {
            if (position >= _text.Length) return 0;
            var i = position;
            
            while (i < _text.Length && !char.IsWhiteSpace(_text[i])) i++;
            while (i < _text.Length && char.IsWhiteSpace(_text[i])) i++;
            
            return i - position;
        }
    }

    #endregion

    #region Cursor and Selection

    private void MoveCursor(int delta, bool extendSelection)
    {
        var newPosition = Math.Clamp(_cursorPosition + delta, 0, _text.Length);
        MoveCursorTo(newPosition, extendSelection);
    }

    private void MoveCursorTo(int position, bool extendSelection)
    {
        if (extendSelection)
        {
            if (_selectionStart < 0)
                _selectionStart = _cursorPosition;
            _selectionEnd = position;
        }
        else
        {
            ClearSelection();
        }
        
        _cursorPosition = position;
        ResetCursorBlink();
        EnsureCursorVisible();
        InvalidateVisual();
    }

    private void ClearSelection()
    {
        _selectionStart = -1;
        _selectionEnd = -1;
    }

    public void SelectAll()
    {
        _selectionStart = 0;
        _selectionEnd = _text.Length;
        _cursorPosition = _text.Length;
        InvalidateVisual();
    }

    private void SelectWord(int position)
    {
        if (string.IsNullOrEmpty(_text)) return;
        
        position = Math.Clamp(position, 0, _text.Length - 1);
        
        var start = position;
        while (start > 0 && !char.IsWhiteSpace(_text[start - 1])) start--;
        
        var end = position;
        while (end < _text.Length && !char.IsWhiteSpace(_text[end])) end++;
        
        _selectionStart = start;
        _selectionEnd = end;
        _cursorPosition = end;
        InvalidateVisual();
    }

    private void EnsureCursorVisible()
    {
        var cursorX = GetTextWidth(DisplayText[.._cursorPosition]);
        var visibleWidth = Bounds.Width - Padding.Left - Padding.Right;
        
        if (cursorX - _scrollOffset > visibleWidth - 10)
        {
            _scrollOffset = cursorX - visibleWidth + 10;
        }
        else if (cursorX - _scrollOffset < 0)
        {
            _scrollOffset = cursorX;
        }
        
        _scrollOffset = Math.Max(0, _scrollOffset);
        InvalidateVisual();
    }

    #endregion

    #region Clipboard

    // Avalonia 12 routes clipboard access through the hosting TopLevel and uses
    // the SetTextAsync / TryGetTextAsync extensions in Avalonia.Input.Platform.

    private async Task CopyToClipboardAsync()
    {
        if (!HasSelection) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;

        try { await clipboard.SetTextAsync(SelectedText); }
        catch { /* clipboard unavailable */ }
    }

    private async Task CutToClipboardAsync()
    {
        if (IsReadOnly || !HasSelection) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;

        var text = SelectedText;
        try { await clipboard.SetTextAsync(text); }
        catch { /* clipboard unavailable */ }

        DeleteSelection();
        ResetCursorBlink();
        EnsureCursorVisible();
    }

    private async Task PasteFromClipboardAsync()
    {
        if (IsReadOnly) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;

        string? text;
        try { text = await clipboard.TryGetTextAsync(); }
        catch { return; }

        if (string.IsNullOrEmpty(text)) return;

        // Single-line text box: strip embedded line breaks so a multi-line
        // paste collapses to a single line instead of corrupting the buffer.
        text = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        InsertText(text);
    }

    #endregion

    #region Layout

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = FontSize * 1.2 + Padding.Top + Padding.Bottom + 4;
        return new Size(availableSize.Width, Math.Min(height, availableSize.Height));
    }

    #endregion
}

/// <summary>
/// Event args for text changed event.
/// </summary>
public class TextChangedEventArgs : EventArgs
{
}
