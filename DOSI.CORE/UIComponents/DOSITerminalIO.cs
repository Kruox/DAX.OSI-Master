using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DOSI.CORE.AccentManagement;

namespace DOSI.CORE.UIComponents;

/// <summary>
/// A 100% custom-drawn command prompt style terminal control.
/// No inherited controls - everything is rendered manually.
/// </summary>
public class DOSITerminalIO : Control
{
    #region Fields

    private readonly List<TerminalLine> _lines = [];
    private readonly List<string> _commandHistory = [];
    private int _historyIndex = -1;
    private string _currentInput = string.Empty;
    private string _inputText = string.Empty;
    private int _caretPosition = 0;
    private bool _caretVisible = true;
    private DispatcherTimer? _caretTimer;
    private readonly DOSIScrollBar _scrollBar;
    private bool _isFocused = false;

    // Rendering constants
    private const double LineHeightPadding = 4;
    private const double Padding = 8;

    // Brushes (accent-aware)
    private IBrush _backgroundBrush = Brushes.Black;
    private IBrush _textBrush = Brushes.White;
    private readonly Typeface _typeface;

    private static AccentManager Accents => AccentManager.Instance;

    #endregion

    #region Properties

    public string Prompt { get; set; } = "C:\\>";
    public double FontSize { get; set; } = 14;
    public bool IsReadOnly { get; set; }
    public bool IsInputEnabled { get; set; } = true;

    private double LineHeight => FontSize + LineHeightPadding;
    private double _cachedDummyWidth;

    #endregion

    #region Events

    public event EventHandler<TerminalCommandEventArgs>? CommandSubmitted;

    #endregion

    #region Constructor

    static DOSITerminalIO()
    {
        // Suppress the default white focus rectangle on this DOSI control.
        FocusAdornerProperty.OverrideDefaultValue<DOSITerminalIO>(null);
    }

    public DOSITerminalIO()
    {
        Focusable = true;

        // Suppress the default Fluent focus rectangle (white outline). Setting this
        // as a local value overrides the accent style that re-applies it.
        FocusAdorner = null;

        ClipToBounds = true;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        _typeface = new Typeface(DOSIFonts.Mono);
        UpdateAccentBrushes();

        // Initialize scroll bar
        _scrollBar = new DOSIScrollBar
        {
            Orientation = Orientation.Vertical,
            SmallChange = LineHeight,
            LargeChange = LineHeight * 5,
            ShowButtons = false, // Cleaner look for terminal
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsVisible = false // Only show when needed
        };
        _scrollBar.Scroll += (s, e) => InvalidateVisual();

        VisualChildren.Add(_scrollBar);
        LogicalChildren.Add(_scrollBar);

        // Caret blink timer
        _caretTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _caretTimer.Tick += OnCaretTimerTick;

        // Focus handling
        GotFocus += OnGotFocus;
        LostFocus += OnLostFocus;

        // Subscribe/unsubscribe properly
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void OnCaretTimerTick(object? sender, EventArgs e)
    {
        _caretVisible = !_caretVisible;
        InvalidateVisual();
    }

    private void OnGotFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _isFocused = true;
        _caretVisible = true;
        _caretTimer?.Start();
        InvalidateVisual();
    }

    private void OnLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _isFocused = false;
        _caretTimer?.Stop();
        _caretVisible = false;
        InvalidateVisual();
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        Accents.AccentChanged += OnAccentChanged;
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        Accents.AccentChanged -= OnAccentChanged;
        _caretTimer?.Stop();
    }

    private void OnAccentChanged(object? sender, EventArgs e)
    {
        UpdateAccentBrushes();
        _cachedDummyWidth = 0; // Reset cached measurement
        InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        UpdateScrollBar();

        // Recalculate scroll position when the control is resized
        // This ensures the latest content is visible after maximize/restore
        if (e.NewSize.Height > 0)
        {
            ScrollToBottom();
            InvalidateVisual();
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_scrollBar.IsVisible)
        {
            _scrollBar.Arrange(new Rect(finalSize.Width - 14, 0, 14, finalSize.Height));
        }
        return base.ArrangeOverride(finalSize);
    }

    private void UpdateScrollBar()
    {
        var contentHeight = GetContentHeight();
        var viewportHeight = Bounds.Height;

        if (contentHeight > viewportHeight)
        {
            _scrollBar.IsVisible = true;
            _scrollBar.Maximum = contentHeight - viewportHeight;
            _scrollBar.ViewportSize = viewportHeight;
        }
        else
        {
            _scrollBar.IsVisible = false;
            _scrollBar.Value = 0;
        }

        InvalidateArrange();
    }

    private void UpdateAccentBrushes()
    {
        // Create a darker version of the window content for terminal background
        var bgColor = Accents.WindowContent;
        var terminalBg = Color.FromRgb(
            (byte)Math.Max(0, bgColor.R - 20),
            (byte)Math.Max(0, bgColor.G - 20),
            (byte)Math.Max(0, bgColor.B - 20));

        _backgroundBrush = new SolidColorBrush(terminalBg).ToImmutable();
        _textBrush = Accents.TextPrimaryBrush.ToImmutable();
    }

    #endregion

    #region Control Overrides

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_scrollBar.IsVisible)
        {
            var delta = e.Delta.Y * LineHeight * 3;
            _scrollBar.Value = Math.Clamp(_scrollBar.Value - delta, 0, _scrollBar.Maximum);
            InvalidateVisual();
        }
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (IsReadOnly || !IsInputEnabled) return;

        var handled = true;
        switch (e.Key)
        {
            case Key.Enter:
                SubmitCommand();
                break;
            case Key.Back when _caretPosition > 0:
                _inputText = _inputText.Remove(_caretPosition - 1, 1);
                _caretPosition--;
                break;
            case Key.Delete when _caretPosition < _inputText.Length:
                _inputText = _inputText.Remove(_caretPosition, 1);
                break;
            case Key.Left when _caretPosition > 0:
                _caretPosition--;
                break;
            case Key.Right when _caretPosition < _inputText.Length:
                _caretPosition++;
                break;
            case Key.Home:
                _caretPosition = 0;
                break;
            case Key.End:
                _caretPosition = _inputText.Length;
                break;
            case Key.Up:
                NavigateHistory(-1);
                break;
            case Key.Down:
                NavigateHistory(1);
                break;
            case Key.Escape:
                _inputText = string.Empty;
                _caretPosition = 0;
                _historyIndex = -1;
                break;
            case Key.L when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                Clear();
                break;
            case Key.C when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                _lines.Add(new TerminalLine(Prompt + " " + _inputText + "^C"));
                _inputText = string.Empty;
                _caretPosition = 0;
                ScrollToBottom();
                break;
            default:
                handled = false;
                break;
        }

        if (handled)
        {
            ResetCaretBlink();
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (IsReadOnly || !IsInputEnabled || string.IsNullOrEmpty(e.Text)) return;

        foreach (var c in e.Text.Where(c => !char.IsControl(c)))
        {
            _inputText = _inputText.Insert(_caretPosition, c.ToString());
            _caretPosition++;
        }

        ResetCaretBlink();
        InvalidateVisual();
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;

        // Background
        context.FillRectangle(_backgroundBrush, new Rect(bounds.Size));

        var lineHeight = LineHeight;
        double scrollOffset = _scrollBar.IsVisible ? _scrollBar.Value : 0;
        double y = Padding - scrollOffset;

        // Draw all output lines
        foreach (var line in _lines)
        {
            if (y + lineHeight > 0 && y < bounds.Height)
            {
                DrawText(context, line.Text, Padding, y);
            }
            y += lineHeight;
        }

        // Draw current input line
        if (!IsReadOnly)
        {
            if (y + lineHeight > 0 && y < bounds.Height)
            {
                var inputLine = Prompt + " " + _inputText;
                DrawText(context, inputLine, Padding, y);

                // Draw caret
                if (_isFocused && _caretVisible)
                {
                    // Measure actual text width up to caret position (include space after prompt)
                    var textBeforeCaret = Prompt + " " + _inputText.Substring(0, _caretPosition);
                    var caretX = Padding + MeasureTextWidth(textBeforeCaret);
                    var caretWidth = MeasureTextWidth("M"); // Use 'M' width for consistent caret size
                    context.FillRectangle(_textBrush, new Rect(caretX, y + 2, caretWidth, lineHeight - 4));
                }
            }
        }
    }

    #endregion

    #region Public Methods

    public void Write(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        
        // Append to last incomplete line or create new
        if (_lines.Count > 0 && !_lines[^1].IsComplete)
        {
            _lines[^1] = new TerminalLine(_lines[^1].Text + text, false);
        }
        else
        {
            _lines.Add(new TerminalLine(text, false));
        }
        
        ScrollToBottom();
        InvalidateVisual();
    }

    public void WriteLine(string text = "")
    {
        _lines.Add(new TerminalLine(text, true));
        ScrollToBottom();
        InvalidateVisual();
    }

    public void Clear()
    {
        _lines.Clear();
        if (_scrollBar.IsVisible)
        {
            _scrollBar.Value = 0;
            UpdateScrollBar();
        }
        InvalidateVisual();
    }

    public void FocusInput()
    {
        IsInputEnabled = true;
        _isFocused = true;
        _caretVisible = true;
        _caretTimer?.Start();
        Focus();
        InvalidateVisual();
    }

    public void UnfocusInput()
    {
        IsInputEnabled = false;
        _isFocused = false;
        _caretTimer?.Stop();
        _caretVisible = false;
        InvalidateVisual();
    }

    public void SetInput(string text)
    {
        _inputText = text;
        _caretPosition = text.Length;
        InvalidateVisual();
    }

    public void SetPrompt(string prompt)
    {
        Prompt = prompt;
        InvalidateVisual();
    }

    #endregion

    #region Private Methods

    private void DrawText(DrawingContext context, string text, double x, double y)
    {
        if (string.IsNullOrEmpty(text)) return;

        var formattedText = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _typeface,
            FontSize,
            _textBrush);

        context.DrawText(formattedText, new Point(x, y));
    }

    private double MeasureTextWidth(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        // FormattedText ignores trailing whitespace - append dummy char and subtract its width
        var formattedWithDummy = new FormattedText(
            text + "|",
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _typeface,
            FontSize,
            _textBrush);

        return formattedWithDummy.Width - GetDummyCharWidth();
    }

    private double GetDummyCharWidth()
    {
        if (_cachedDummyWidth == 0)
        {
            _cachedDummyWidth = new FormattedText(
                "|",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                _typeface,
                FontSize,
                _textBrush).Width;
        }
        return _cachedDummyWidth;
    }

    private void SubmitCommand()
    {
        var command = _inputText;

        // Don't add empty lines or fire event for empty input
        if (string.IsNullOrEmpty(command))
        {
            return;
        }

        _lines.Add(new TerminalLine(Prompt + " " + command, true));

        if (!string.IsNullOrWhiteSpace(command) && 
            (_commandHistory.Count == 0 || _commandHistory[^1] != command))
        {
            _commandHistory.Add(command);
        }

        _inputText = string.Empty;
        _caretPosition = 0;
        _historyIndex = -1;
        _currentInput = string.Empty;

        ScrollToBottom();
        CommandSubmitted?.Invoke(this, new TerminalCommandEventArgs(command));
    }

    private void NavigateHistory(int direction)
    {
        if (_commandHistory.Count == 0) return;

        if (_historyIndex == -1 && direction == -1)
            _currentInput = _inputText;

        _historyIndex = Math.Clamp(_historyIndex + direction, -1, _commandHistory.Count - 1);
        _inputText = _historyIndex == -1 ? _currentInput : _commandHistory[^(_historyIndex + 1)];
        _caretPosition = _inputText.Length;
    }

    private double GetContentHeight() => (_lines.Count + 1) * LineHeight + Padding * 2;

    private void ScrollToBottom()
    {
        UpdateScrollBar();

        // Don't adjust scroll if control hasn't been laid out yet
        if (Bounds.Height <= 0)
        {
            _scrollBar.Value = 0;
            return;
        }

        if (_scrollBar.IsVisible)
        {
            _scrollBar.Value = _scrollBar.Maximum;
        }
    }

    private void ResetCaretBlink()
    {
        _caretVisible = true;
        _caretTimer?.Stop();
        _caretTimer?.Start();
    }

    #endregion

    #region Nested Types

    private readonly record struct TerminalLine(string Text, bool IsComplete = true);

    #endregion
}

/// <summary>
/// Event arguments for terminal command submission.
/// </summary>
public class TerminalCommandEventArgs : EventArgs
{
    public string Command { get; }
    public TerminalCommandEventArgs(string command) => Command = command;
}
