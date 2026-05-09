using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DOSI.CORE.AccentManagement;

namespace DOSI.CORE.UIComponents;

/// <summary>
/// A 100% custom-drawn multi-line code editor for the DOSI operating system.
/// Renders a line-number gutter, monospace text, blinking caret, and scrollbar.
/// Supports basic editing (typing, backspace/delete, arrow / home / end / page up/down,
/// enter for new line, tab inserts spaces). No syntax highlighting yet.
/// </summary>
public class DOSICodeEditor : Control
{
    #region Fields

    private readonly List<string> _lines = new() { string.Empty };
    private int _caretLine;
    private int _caretCol;
    private bool _caretVisible = true;
    private DispatcherTimer? _caretTimer;

    // Selection anchor. The selection runs from (_anchorLine,_anchorCol) to
    // (_caretLine,_caretCol). When the two points are equal there is no
    // selection (just a caret).
    private int _anchorLine;
    private int _anchorCol;
    private bool _isMouseSelecting;

    // Undo / redo history. We snapshot the entire buffer + caret/anchor at the
    // start of each mutation so that Ctrl+Z restores the exact prior state.
    private readonly Stack<EditSnapshot> _undoStack = new();
    private readonly Stack<EditSnapshot> _redoStack = new();
    private bool _isApplyingHistory;
    private const int MaxUndoEntries = 200;

    private readonly record struct EditSnapshot(
        string[] Lines, int CaretLine, int CaretCol, int AnchorLine, int AnchorCol);

    private readonly DOSIScrollBar _vScrollBar;
    private double _cachedCharWidth;

    private readonly Typeface _typeface;

    private static AccentManager Accents => AccentManager.Instance;

    private const double GutterPaddingX = 12;
    private const double TextPaddingX = 10;
    private const double LineSpacing = 4;
    private const int TabSize = 4;

    #endregion

    #region Properties

    public double FontSize { get; set; } = 13;
    public bool IsReadOnly { get; set; }

    /// <summary>Language mode for syntax highlighting. <c>null</c> = plain text.</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets the full text of the editor. Setter resets caret to (0,0).</summary>
    public string Text
    {
        get => string.Join("\n", _lines);
        set
        {
            _lines.Clear();
            var raw = (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            _lines.AddRange(raw.Split('\n'));
            if (_lines.Count == 0) _lines.Add(string.Empty);
            _caretLine = 0;
            _caretCol = 0;
            _anchorLine = 0;
            _anchorCol = 0;
            _isDirty = false;
            _undoStack.Clear();
            _redoStack.Clear();
            UpdateScrollBar();
            InvalidateVisual();
            TextChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool _isDirty;
    /// <summary>True if the buffer has been modified since the last <see cref="Text"/> set or <see cref="MarkClean"/>.</summary>
    public bool IsDirty => _isDirty;

    public int LineCount => _lines.Count;
    public int CaretLine => _caretLine + 1;   // 1-based for status bar
    public int CaretColumn => _caretCol + 1;  // 1-based for status bar

    public double LineHeight => FontSize + LineSpacing;

    #endregion

    #region Events

    public event EventHandler? TextChanged;
    public event EventHandler? CaretChanged;

    #endregion

    #region Construction

    static DOSICodeEditor()
    {
        FocusableProperty.OverrideDefaultValue<DOSICodeEditor>(true);
        FocusAdornerProperty.OverrideDefaultValue<DOSICodeEditor>(null);
    }

    public DOSICodeEditor()
    {
        Focusable = true;
        FocusAdorner = null;

        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        _typeface = new Typeface("Consolas, Menlo, Courier New, monospace");

        _vScrollBar = new DOSIScrollBar
        {
            Orientation = Orientation.Vertical,
            ShowButtons = false,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsVisible = false
        };
        _vScrollBar.Scroll += (_, _) => { InvalidateVisual(); InvalidateArrange(); };
        VisualChildren.Add(_vScrollBar);
        LogicalChildren.Add(_vScrollBar);

        _caretTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _caretTimer.Tick += (_, _) => { _caretVisible = !_caretVisible; InvalidateVisual(); };

        GotFocus += (_, _) =>
        {
            _caretVisible = true;
            _caretTimer?.Start();
            InvalidateVisual();
        };
        LostFocus += (_, _) =>
        {
            _caretTimer?.Stop();
            _caretVisible = false;
            HideCompletion();
            InvalidateVisual();
        };

        AttachedToVisualTree += (_, _) => Accents.AccentChanged += OnAccentChanged;
        DetachedFromVisualTree += (_, _) =>
        {
            Accents.AccentChanged -= OnAccentChanged;
            _caretTimer?.Stop();
        };

        // Re-anchor on resize: when the host window toggles fullscreen <-> windowed
        // the viewport height changes, which can leave the caret off-screen and
        // make appended text in append-only buffers (output, terminal) appear
        // cut off behind chrome. Recompute scroll bounds and pull the caret
        // back into view.
        SizeChanged += (_, _) =>
        {
            UpdateScrollBar();
            EnsureCaretVisible();
        };

        BuildContextMenu();
    }

    /// <summary>
    /// Default right-click context menu: Cut / Copy / Paste / Select All
    /// plus Undo / Redo. Read-only editors only get Copy + Select All.
    /// </summary>
    private void BuildContextMenu()
    {
        var menu = new DOSIContextMenu();

        var undo = new MenuItem { Header = "Undo" };
        undo.Click += (_, _) => Undo();

        var redo = new MenuItem { Header = "Redo" };
        redo.Click += (_, _) => Redo();

        var cut = new MenuItem { Header = "Cut" };
        cut.Click += (_, _) => _ = CutAsync();

        var copy = new MenuItem { Header = "Copy" };
        copy.Click += (_, _) => _ = CopyAsync();

        var paste = new MenuItem { Header = "Paste" };
        paste.Click += (_, _) => _ = PasteFromClipboardAsync();

        var selectAll = new MenuItem { Header = "Select All" };
        selectAll.Click += (_, _) => SelectAll();

        // Disable mutations when read-only so the user gets a sensible menu.
        menu.Opening += (_, _) =>
        {
            undo.IsEnabled = !IsReadOnly;
            redo.IsEnabled = !IsReadOnly;
            cut.IsEnabled = !IsReadOnly && HasSelection;
            copy.IsEnabled = HasSelection;
            paste.IsEnabled = !IsReadOnly;
        };

        menu.Items.Add(undo);
        menu.Items.Add(redo);
        menu.Items.Add(new Separator());
        menu.Items.Add(cut);
        menu.Items.Add(copy);
        menu.Items.Add(paste);
        menu.Items.Add(new Separator());
        menu.Items.Add(selectAll);

        ContextMenu = menu;
    }

    private void OnAccentChanged(object? sender, EventArgs e)
    {
        _cachedCharWidth = 0;
        InvalidateVisual();
    }

    #endregion

    #region Public API

    public void MarkClean()
    {
        _isDirty = false;
        InvalidateVisual();
    }

    public void Focus() => base.Focus();

    /// <summary>Smallest font size accepted by <see cref="ZoomOut"/>.</summary>
    public const double MinFontSize = 8;

    /// <summary>Largest font size accepted by <see cref="ZoomIn"/>.</summary>
    public const double MaxFontSize = 32;

    private double _baseFontSize;

    /// <summary>Increases the editor font size by 1 pt (clamped to <see cref="MaxFontSize"/>).</summary>
    public void ZoomIn() => SetFontSizeInternal(FontSize + 1);

    /// <summary>Decreases the editor font size by 1 pt (clamped to <see cref="MinFontSize"/>).</summary>
    public void ZoomOut() => SetFontSizeInternal(FontSize - 1);

    /// <summary>Restores the editor font size to whatever it was when the control was first shown.</summary>
    public void ResetZoom()
    {
        if (_baseFontSize <= 0) _baseFontSize = 13;
        SetFontSizeInternal(_baseFontSize);
    }

    private void SetFontSizeInternal(double size)
    {
        var clamped = Math.Clamp(size, MinFontSize, MaxFontSize);
        if (Math.Abs(clamped - FontSize) < 0.01) return;
        if (_baseFontSize <= 0) _baseFontSize = FontSize;
        FontSize = clamped;
        UpdateScrollBar();
        EnsureCaretVisible();
        InvalidateVisual();
    }

    /// <summary>
    /// Duplicates the line containing the caret (or each fully-selected line).
    /// The new copy is inserted directly below; caret moves down with the
    /// duplicate so a repeated press makes consecutive copies.
    /// </summary>
    public void DuplicateLine()
    {
        if (IsReadOnly) return;
        BeginEdit();

        var (sl, _, el, _) = HasSelection
            ? GetNormalizedSelection()
            : (_caretLine, 0, _caretLine, 0);

        // Snapshot the affected slice so we can append it right after.
        var slice = new List<string>(el - sl + 1);
        for (int i = sl; i <= el; i++) slice.Add(_lines[i]);
        _lines.InsertRange(el + 1, slice);

        var spanCount = slice.Count;
        _caretLine = Math.Min(_lines.Count - 1, _caretLine + spanCount);
        _anchorLine = _caretLine;
        _anchorCol = _caretCol = Math.Min(_caretCol, _lines[_caretLine].Length);

        AfterStructuralEdit();
    }

    /// <summary>Swaps the current line (or selected lines) with the line(s) above.</summary>
    public void MoveLineUp()
    {
        if (IsReadOnly) return;
        var (sl, _, el, _) = HasSelection ? GetNormalizedSelection() : (_caretLine, 0, _caretLine, 0);
        if (sl <= 0) return;     // already at top

        BeginEdit();
        var line = _lines[sl - 1];
        _lines.RemoveAt(sl - 1);
        _lines.Insert(el, line);

        _caretLine--; _anchorLine--;
        AfterStructuralEdit();
    }

    /// <summary>Swaps the current line (or selected lines) with the line(s) below.</summary>
    public void MoveLineDown()
    {
        if (IsReadOnly) return;
        var (sl, _, el, _) = HasSelection ? GetNormalizedSelection() : (_caretLine, 0, _caretLine, 0);
        if (el >= _lines.Count - 1) return;     // already at bottom

        BeginEdit();
        var line = _lines[el + 1];
        _lines.RemoveAt(el + 1);
        _lines.Insert(sl, line);

        _caretLine++; _anchorLine++;
        AfterStructuralEdit();
    }

    private void AfterStructuralEdit()
    {
        _isDirty = true;
        UpdateScrollBar();
        EnsureCaretVisible();
        ResetCaretBlink();
        TextChanged?.Invoke(this, EventArgs.Empty);
        CaretChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>
    /// Programmatically sets the selection to the supplied range. Coordinates
    /// are 0-based; values are clamped to valid line/column bounds. Used by
    /// the IDE's Find feature to highlight the next match.
    /// </summary>
    public void SetSelection(int startLine, int startCol, int endLine, int endCol)
    {
        if (_lines.Count == 0) return;
        startLine = Math.Clamp(startLine, 0, _lines.Count - 1);
        endLine = Math.Clamp(endLine, 0, _lines.Count - 1);
        startCol = Math.Clamp(startCol, 0, _lines[startLine].Length);
        endCol = Math.Clamp(endCol, 0, _lines[endLine].Length);

        _anchorLine = startLine; _anchorCol = startCol;
        _caretLine = endLine;    _caretCol = endCol;

        EnsureCaretVisible();
        ResetCaretBlink();
        CaretChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>
    /// Finds the next occurrence of <paramref name="needle"/> at or after
    /// (<paramref name="fromLine"/>, <paramref name="fromCol"/>), wrapping
    /// from the top if not found by the end of the buffer. Returns
    /// <c>true</c> on a hit and outputs the match position + length.
    /// </summary>
    public bool FindNext(string needle, int fromLine, int fromCol, bool ignoreCase,
                         out int matchLine, out int matchCol, out int matchLength)
    {
        matchLine = matchCol = matchLength = 0;
        if (string.IsNullOrEmpty(needle)) return false;

        var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        fromLine = Math.Clamp(fromLine, 0, _lines.Count - 1);
        fromCol = Math.Clamp(fromCol, 0, _lines[fromLine].Length);

        // Search from the cursor to the end of the buffer.
        for (int i = fromLine; i < _lines.Count; i++)
        {
            var startCol = i == fromLine ? fromCol : 0;
            if (startCol > _lines[i].Length) continue;
            var idx = _lines[i].IndexOf(needle, startCol, cmp);
            if (idx >= 0)
            {
                matchLine = i; matchCol = idx; matchLength = needle.Length;
                return true;
            }
        }

        // Wrap: scan from the top up to the original starting position.
        for (int i = 0; i <= fromLine; i++)
        {
            var endCol = i == fromLine ? fromCol : _lines[i].Length;
            var slice = i == fromLine ? _lines[i].Substring(0, endCol) : _lines[i];
            var idx = slice.IndexOf(needle, cmp);
            if (idx >= 0)
            {
                matchLine = i; matchCol = idx; matchLength = needle.Length;
                return true;
            }
        }

        return false;
    }

    #endregion

    #region Layout

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateScrollBar();
        InvalidateVisual();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_vScrollBar.IsVisible)
            _vScrollBar.Arrange(new Rect(finalSize.Width - 14, 0, 14, finalSize.Height));

        if (_completionPopup != null && _completionOpen)
        {
            var charW = GetCharWidth();
            var scrollOffset = _vScrollBar.IsVisible ? _vScrollBar.Value : 0;
            const double topPad = 4d;

            // Where the caret line currently sits inside the editor's visible area.
            var caretLineTop = topPad + _completionPrefixLine * LineHeight - scrollOffset;
            var caretLineBottom = caretLineTop + LineHeight;
            var caretX = GutterWidth + TextPaddingX + _caretCol * charW;

            // How much space we have on each side of the caret line. We must
            // shrink the popup to this so it never overflows the editor (which
            // has ClipToBounds=true) and never gets clamped to (0,0).
            var roomBelow = Math.Max(0, finalSize.Height - caretLineBottom - 4);
            var roomAbove = Math.Max(0, caretLineTop - 4);

            // Prefer below the caret line; only flip above when there is
            // meaningfully more room above than below.
            bool showBelow = roomBelow >= 80 || roomBelow >= roomAbove;
            var availableHeight = Math.Max(40, showBelow ? roomBelow : roomAbove);
            var targetHeight = Math.Min(220d, availableHeight);

            _completionPopup.Measure(new Size(280, targetHeight));
            var desired = _completionPopup.DesiredSize;
            var w = desired.Width;
            var h = Math.Min(desired.Height, targetHeight);

            // Anchor x at the start of the word being completed (so the
            // suggestion list lines up with the prefix the user typed).
            var prefixOffset = (_caretCol - _completionPrefixStartCol) * charW;
            var x = caretX - prefixOffset;

            var y = showBelow ? caretLineBottom + 2 : caretLineTop - h - 2;

            // Clamp horizontally to the editor's text area so the popup never
            // crosses into the gutter or runs past the right edge.
            var minX = GutterWidth + 2;
            var maxX = Math.Max(minX, finalSize.Width - w - 2);
            if (x < minX) x = minX;
            if (x > maxX) x = maxX;
            if (y < 0) y = 0;

            _completionPopup.Arrange(new Rect(x, y, w, h));
        }

        return base.ArrangeOverride(finalSize);
    }

    private double GutterWidth
    {
        get
        {
            var maxDigits = Math.Max(2, _lines.Count.ToString().Length);
            return GutterPaddingX * 2 + maxDigits * GetCharWidth();
        }
    }

    private void UpdateScrollBar()
    {
        var contentHeight = _lines.Count * LineHeight + 8;
        var viewportHeight = Bounds.Height;
        if (contentHeight > viewportHeight && viewportHeight > 0)
        {
            _vScrollBar.IsVisible = true;
            _vScrollBar.Maximum = contentHeight - viewportHeight;
            _vScrollBar.ViewportSize = viewportHeight;
            _vScrollBar.SmallChange = LineHeight;
            _vScrollBar.LargeChange = LineHeight * 5;
        }
        else
        {
            _vScrollBar.IsVisible = false;
            _vScrollBar.Value = 0;
        }
        InvalidateArrange();
    }

    private void EnsureCaretVisible()
    {
        if (!_vScrollBar.IsVisible) return;

        var caretY = _caretLine * LineHeight;
        var top = _vScrollBar.Value;
        var bottom = top + Bounds.Height - LineHeight;

        if (caretY < top)
            _vScrollBar.Value = Math.Max(0, caretY);
        else if (caretY > bottom)
            _vScrollBar.Value = Math.Min(_vScrollBar.Maximum, caretY - Bounds.Height + LineHeight + 8);
    }

    /// <summary>
    /// Scrolls the viewport so the last line of text is in view. Useful for
    /// append-only buffers (build/run output, terminals, logs) where the
    /// freshly-added text would otherwise sit below the visible region and
    /// look cut off.
    /// </summary>
    public void ScrollToEnd()
    {
        // UpdateScrollBar runs on layout/text changes; force a recompute so
        // Maximum reflects the most recently appended lines before we jump.
        UpdateScrollBar();
        if (!_vScrollBar.IsVisible) return;
        _vScrollBar.Value = _vScrollBar.Maximum;
        InvalidateVisual();
    }

    /// <summary>
    /// Move the caret to the given 1-based line + column and scroll the
    /// viewport so the destination is visible. Used by the IDE when the
    /// user picks an event from the code-behind dropdown - much friendlier
    /// than dumping them at the top of a 60-line file every time.
    /// </summary>
    public void GoToLine(int line, int column = 1)
    {
        if (_lines.Count == 0) return;
        var l0 = Math.Clamp(line - 1, 0, _lines.Count - 1);
        var c0 = Math.Clamp(column - 1, 0, _lines[l0].Length);
        _caretLine = l0;
        _caretCol = c0;
        _anchorLine = l0;
        _anchorCol = c0;
        EnsureCaretVisible();
        // Take focus + restart the blink so the caret is immediately visible
        // at the new position. Without this, GoToLine moves the caret silently
        // and the user has no visual cue where their next keystroke will land.
        Focus();
        ResetCaretBlink();
        InvalidateVisual();
        CaretChanged?.Invoke(this, EventArgs.Empty);
    }

    #endregion

    #region Input

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        HideCompletion();

        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        var (line, col) = HitTest(point.Position);
        var extend = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        // Double-click selects the word under the caret. Triple-click selects
        // the whole line. Single click (optionally shift-extended) sets caret.
        if (e.ClickCount == 2)
        {
            _caretLine = line;
            _caretCol = col;
            SelectWordAtCaret();
        }
        else if (e.ClickCount >= 3)
        {
            SelectLine(line);
        }
        else
        {
            MoveCaretTo(line, col, extend);
            _isMouseSelecting = true;
            e.Pointer.Capture(this);
        }

        ResetCaretBlink();
        CaretChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_isMouseSelecting) return;

        var (line, col) = HitTest(e.GetPosition(this));
        if (line == _caretLine && col == _caretCol) return;

        // Extend selection by moving caret while leaving the anchor in place.
        _caretLine = line;
        _caretCol = col;
        ResetCaretBlink();
        EnsureCaretVisible();
        CaretChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_isMouseSelecting)
        {
            _isMouseSelecting = false;
            e.Pointer.Capture(null);
        }
    }

    private (int line, int col) HitTest(Point pos)
    {
        var scrollOffset = _vScrollBar.IsVisible ? _vScrollBar.Value : 0;
        var line = (int)((pos.Y - 4 + scrollOffset) / LineHeight);
        line = Math.Clamp(line, 0, _lines.Count - 1);

        var charW = GetCharWidth();
        var col = (int)Math.Round((pos.X - GutterWidth - TextPaddingX) / charW);
        col = Math.Clamp(col, 0, _lines[line].Length);
        return (line, col);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        // Ctrl+wheel: zoom font size instead of scrolling.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Delta.Y > 0) ZoomIn();
            else if (e.Delta.Y < 0) ZoomOut();
            e.Handled = true;
            return;
        }

        if (_vScrollBar.IsVisible)
        {
            var delta = e.Delta.Y * LineHeight * 3;
            _vScrollBar.Value = Math.Clamp(_vScrollBar.Value - delta, 0, _vScrollBar.Maximum);
            InvalidateVisual();
        }
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        // Clipboard / select-all shortcuts work even in read-only mode where
        // they don't mutate the buffer.
        if (ctrl && !shift)
        {
            switch (e.Key)
            {
                case Key.A: SelectAll(); e.Handled = true; return;
                case Key.C: _ = CopySelectionAsync(); e.Handled = true; return;
                case Key.X:
                    if (!IsReadOnly) { _ = CutSelectionAsync(); e.Handled = true; return; }
                    _ = CopySelectionAsync(); e.Handled = true; return;
                case Key.V:
                    if (!IsReadOnly) { _ = PasteAsync(); e.Handled = true; return; }
                    break;
                case Key.Insert:
                    _ = CopySelectionAsync(); e.Handled = true; return;
                case Key.Z:
                    if (!IsReadOnly) { Undo(); e.Handled = true; return; }
                    break;
                case Key.Y:
                    if (!IsReadOnly) { Redo(); e.Handled = true; return; }
                    break;
            }
        }
        // Ctrl+Shift+Z is a common alternate for redo (matches VS / VS Code).
        if (ctrl && shift && e.Key == Key.Z && !IsReadOnly)
        {
            Redo();
            e.Handled = true;
            return;
        }
        // Shift+Insert = paste (legacy).
        if (shift && !ctrl && e.Key == Key.Insert && !IsReadOnly)
        {
            _ = PasteAsync();
            e.Handled = true;
            return;
        }

        if (IsReadOnly && !IsNavigationKey(e.Key)) return;

        // Code completion: when the popup is open, intercept navigation/accept keys first.
        if (_completionOpen)
        {
            switch (e.Key)
            {
                case Key.Down: MoveCompletion(1); e.Handled = true; return;
                case Key.Up: MoveCompletion(-1); e.Handled = true; return;
                case Key.Enter:
                case Key.Tab:
                    AcceptCompletion();
                    e.Handled = true;
                    return;
                case Key.Escape:
                    HideCompletion();
                    e.Handled = true;
                    return;
            }
        }

        // Ctrl+Space: explicitly invoke completion.
        if (ctrl && e.Key == Key.Space && !IsReadOnly)
        {
            RefreshCompletion();
            e.Handled = true;
            return;
        }

        // Ctrl+/ : toggle line comment on the current line.
        if (ctrl && e.Key == Key.OemQuestion && !IsReadOnly)
        {
            ToggleLineComment();
            ResetCaretBlink();
            EnsureCaretVisible();
            CaretChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        // Ctrl+D : duplicate the current line (or selected lines) below.
        if (ctrl && !shift && e.Key == Key.D && !IsReadOnly)
        {
            DuplicateLine();
            e.Handled = true;
            return;
        }

        // Alt+Up / Alt+Down : move the current line up/down.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && !ctrl && !shift && !IsReadOnly)
        {
            if (e.Key == Key.Up)   { MoveLineUp();   e.Handled = true; return; }
            if (e.Key == Key.Down) { MoveLineDown(); e.Handled = true; return; }
        }

        // Ctrl+= / Ctrl++ : zoom in.   Ctrl+- : zoom out.   Ctrl+0 : reset zoom.
        if (ctrl && !shift)
        {
            if (e.Key == Key.OemPlus || e.Key == Key.Add)        { ZoomIn();    e.Handled = true; return; }
            if (e.Key == Key.OemMinus || e.Key == Key.Subtract)  { ZoomOut();   e.Handled = true; return; }
            if (e.Key == Key.D0 || e.Key == Key.NumPad0)         { ResetZoom(); e.Handled = true; return; }
        }

        // Tab on a snippet trigger word: expand it instead of inserting spaces.
        if (e.Key == Key.Tab && !IsReadOnly && !_completionOpen && TryExpandSnippet())
        {
            ResetCaretBlink();
            UpdateScrollBar();
            EnsureCaretVisible();
            CaretChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        var handled = true;
        var isNav = IsNavigationKey(e.Key);

        switch (e.Key)
        {
            case Key.Left:
                if (!shift && HasSelection)
                {
                    var (sl, sc, _, _) = GetNormalizedSelection();
                    _caretLine = sl; _caretCol = sc;
                }
                else if (_caretCol > 0) _caretCol--;
                else if (_caretLine > 0) { _caretLine--; _caretCol = _lines[_caretLine].Length; }
                break;
            case Key.Right:
                if (!shift && HasSelection)
                {
                    var (_, _, el, ec) = GetNormalizedSelection();
                    _caretLine = el; _caretCol = ec;
                }
                else if (_caretCol < _lines[_caretLine].Length) _caretCol++;
                else if (_caretLine < _lines.Count - 1) { _caretLine++; _caretCol = 0; }
                break;
            case Key.Up:
                if (_caretLine > 0)
                {
                    _caretLine--;
                    _caretCol = Math.Min(_caretCol, _lines[_caretLine].Length);
                }
                break;
            case Key.Down:
                if (_caretLine < _lines.Count - 1)
                {
                    _caretLine++;
                    _caretCol = Math.Min(_caretCol, _lines[_caretLine].Length);
                }
                break;
            case Key.Home:
                _caretCol = 0;
                break;
            case Key.End:
                _caretCol = _lines[_caretLine].Length;
                break;
            case Key.PageUp:
                {
                    var step = Math.Max(1, (int)(Bounds.Height / LineHeight));
                    _caretLine = Math.Max(0, _caretLine - step);
                    _caretCol = Math.Min(_caretCol, _lines[_caretLine].Length);
                }
                break;
            case Key.PageDown:
                {
                    var step = Math.Max(1, (int)(Bounds.Height / LineHeight));
                    _caretLine = Math.Min(_lines.Count - 1, _caretLine + step);
                    _caretCol = Math.Min(_caretCol, _lines[_caretLine].Length);
                }
                break;
            case Key.Back when !IsReadOnly:
                Backspace();
                break;
            case Key.Delete when !IsReadOnly:
                DeleteForward();
                break;
            case Key.Enter when !IsReadOnly:
                InsertNewLine();
                break;
            case Key.Tab when !IsReadOnly:
                InsertText(new string(' ', TabSize));
                break;
            default:
                handled = false;
                break;
        }

        // After navigation keys, either extend the selection (shift held) or
        // collapse any existing selection to the new caret position.
        if (handled && isNav)
        {
            if (!shift)
            {
                _anchorLine = _caretLine;
                _anchorCol = _caretCol;
            }
        }

        if (handled)
        {
            ResetCaretBlink();
            UpdateScrollBar();
            EnsureCaretVisible();
            if (_completionOpen && (e.Key == Key.Back || e.Key == Key.Delete))
                RefreshCompletion();
            else if (e.Key != Key.Back && e.Key != Key.Delete)
                HideCompletion();
            CaretChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (IsReadOnly || string.IsNullOrEmpty(e.Text)) return;

        var clean = new string(e.Text.Where(c => !char.IsControl(c) || c == '\t').ToArray());
        if (clean.Length == 0) return;

        // Auto-pair brackets / quotes: insert the closing char and keep the
        // caret between them. If the user types the closing char while the
        // caret already sits on it, just step over (don't double up).
        if (clean.Length == 1 && TryHandleAutoPair(clean[0]))
        {
            ResetCaretBlink();
            UpdateScrollBar();
            EnsureCaretVisible();
            RefreshCompletion();
            CaretChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        InsertText(clean);
        ResetCaretBlink();
        UpdateScrollBar();
        EnsureCaretVisible();
        RefreshCompletion();
        CaretChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        e.Handled = true;
    }

    private static bool IsNavigationKey(Key k) =>
        k is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown;

    #endregion

    #region Text mutation

    private void InsertText(string text)
    {
        BeginEdit();
        if (HasSelection) DeleteSelection();

        // Multi-line paste: split on newline so each piece goes onto its own line.
        if (text.IndexOf('\n') >= 0)
        {
            var pieces = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var line = _lines[_caretLine];
            var head = line.Substring(0, _caretCol);
            var tail = line.Substring(_caretCol);

            _lines[_caretLine] = head + pieces[0];
            for (int i = 1; i < pieces.Length; i++)
                _lines.Insert(_caretLine + i, pieces[i]);

            _caretLine += pieces.Length - 1;
            _caretCol = pieces[^1].Length;
            _lines[_caretLine] = _lines[_caretLine] + tail;
        }
        else
        {
            var line = _lines[_caretLine];
            _lines[_caretLine] = line.Substring(0, _caretCol) + text + line.Substring(_caretCol);
            _caretCol += text.Length;
        }

        _anchorLine = _caretLine;
        _anchorCol = _caretCol;
        MarkDirty();
    }

    private void InsertNewLine()
    {
        BeginEdit();
        if (HasSelection) DeleteSelection();

        var line = _lines[_caretLine];
        var head = line.Substring(0, _caretCol);
        var tail = line.Substring(_caretCol);

        // Auto-indent: copy leading whitespace from current line.
        var indent = new string(line.TakeWhile(c => c == ' ' || c == '\t').ToArray());

        _lines[_caretLine] = head;
        _lines.Insert(_caretLine + 1, indent + tail);
        _caretLine++;
        _caretCol = indent.Length;
        _anchorLine = _caretLine;
        _anchorCol = _caretCol;
        MarkDirty();
    }

    private void Backspace()
    {
        if (HasSelection) { BeginEdit(); DeleteSelection(); return; }

        if (_caretCol > 0)
        {
            BeginEdit();
            var line = _lines[_caretLine];
            _lines[_caretLine] = line.Remove(_caretCol - 1, 1);
            _caretCol--;
        }
        else if (_caretLine > 0)
        {
            BeginEdit();
            var prev = _lines[_caretLine - 1];
            var cur = _lines[_caretLine];
            _caretCol = prev.Length;
            _lines[_caretLine - 1] = prev + cur;
            _lines.RemoveAt(_caretLine);
            _caretLine--;
        }
        else
        {
            return;
        }
        _anchorLine = _caretLine;
        _anchorCol = _caretCol;
        MarkDirty();
    }

    private void DeleteForward()
    {
        if (HasSelection) { BeginEdit(); DeleteSelection(); return; }

        var line = _lines[_caretLine];
        if (_caretCol < line.Length)
        {
            BeginEdit();
            _lines[_caretLine] = line.Remove(_caretCol, 1);
        }
        else if (_caretLine < _lines.Count - 1)
        {
            BeginEdit();
            _lines[_caretLine] = line + _lines[_caretLine + 1];
            _lines.RemoveAt(_caretLine + 1);
        }
        else
        {
            return;
        }
        _anchorLine = _caretLine;
        _anchorCol = _caretCol;
        MarkDirty();
    }

    private void MarkDirty()
    {
        if (!_isDirty)
        {
            _isDirty = true;
        }
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    #endregion

    #region Selection / Clipboard

    /// <summary>True when the selection anchor differs from the caret position.</summary>
    public bool HasSelection => _anchorLine != _caretLine || _anchorCol != _caretCol;

    // ===== Undo / Redo =====

    private EditSnapshot CaptureSnapshot()
        => new(_lines.ToArray(), _caretLine, _caretCol, _anchorLine, _anchorCol);

    /// <summary>
    /// Push the current buffer state onto the undo stack and clear the redo
    /// stack. Called at the top of every text-mutating method so Ctrl+Z can
    /// step back to the exact state before each edit.
    /// </summary>
    private void BeginEdit()
    {
        if (_isApplyingHistory) return;

        _undoStack.Push(CaptureSnapshot());
        if (_undoStack.Count > MaxUndoEntries)
        {
            // Drop the oldest entry to cap memory usage.
            var arr = _undoStack.ToArray();
            _undoStack.Clear();
            for (int i = MaxUndoEntries - 1; i >= 0; i--)
                _undoStack.Push(arr[i]);
        }
        _redoStack.Clear();
    }

    /// <summary>Restore the buffer to the most recent pre-edit snapshot.</summary>
    public void Undo()
    {
        if (_undoStack.Count == 0) return;
        _redoStack.Push(CaptureSnapshot());
        ApplySnapshot(_undoStack.Pop());
    }

    /// <summary>Re-apply a snapshot previously rolled back by <see cref="Undo"/>.</summary>
    public void Redo()
    {
        if (_redoStack.Count == 0) return;
        _undoStack.Push(CaptureSnapshot());
        ApplySnapshot(_redoStack.Pop());
    }

    private void ApplySnapshot(EditSnapshot snap)
    {
        _isApplyingHistory = true;
        try
        {
            _lines.Clear();
            _lines.AddRange(snap.Lines);
            if (_lines.Count == 0) _lines.Add(string.Empty);

            _caretLine = Math.Clamp(snap.CaretLine, 0, _lines.Count - 1);
            _caretCol = Math.Clamp(snap.CaretCol, 0, _lines[_caretLine].Length);
            _anchorLine = Math.Clamp(snap.AnchorLine, 0, _lines.Count - 1);
            _anchorCol = Math.Clamp(snap.AnchorCol, 0, _lines[_anchorLine].Length);
        }
        finally
        {
            _isApplyingHistory = false;
        }

        _isDirty = true;
        HideCompletion();
        UpdateScrollBar();
        EnsureCaretVisible();
        ResetCaretBlink();
        TextChanged?.Invoke(this, EventArgs.Empty);
        CaretChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    // ===== Selection helpers =====

    /// <summary>
    /// Returns the selection bounds with start guaranteed to come before end
    /// in document order. When there is no selection, start == end == caret.
    /// </summary>
    public (int startLine, int startCol, int endLine, int endCol) GetNormalizedSelection()
    {
        if (_anchorLine < _caretLine ||
            (_anchorLine == _caretLine && _anchorCol <= _caretCol))
        {
            return (_anchorLine, _anchorCol, _caretLine, _caretCol);
        }
        return (_caretLine, _caretCol, _anchorLine, _anchorCol);
    }

    /// <summary>Returns the currently selected text, or an empty string.</summary>
    public string GetSelectedText()
    {
        if (!HasSelection) return string.Empty;
        var (sl, sc, el, ec) = GetNormalizedSelection();
        if (sl == el) return _lines[sl].Substring(sc, ec - sc);

        var sb = new System.Text.StringBuilder();
        sb.Append(_lines[sl], sc, _lines[sl].Length - sc).Append('\n');
        for (int i = sl + 1; i < el; i++)
            sb.Append(_lines[i]).Append('\n');
        sb.Append(_lines[el], 0, ec);
        return sb.ToString();
    }

    /// <summary>Selects all text in the editor.</summary>
    public void SelectAll()
    {
        _anchorLine = 0;
        _anchorCol = 0;
        _caretLine = _lines.Count - 1;
        _caretCol = _lines[_caretLine].Length;
        ResetCaretBlink();
        CaretChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>
    /// Move the caret to (<paramref name="line"/>,<paramref name="col"/>).
    /// When <paramref name="extendSelection"/> is false the anchor follows
    /// the caret (collapsing any selection); when true the anchor is left in
    /// place so the selection grows.
    /// </summary>
    private void MoveCaretTo(int line, int col, bool extendSelection)
    {
        line = Math.Clamp(line, 0, _lines.Count - 1);
        col = Math.Clamp(col, 0, _lines[line].Length);
        _caretLine = line;
        _caretCol = col;
        if (!extendSelection)
        {
            _anchorLine = line;
            _anchorCol = col;
        }
    }

    private void SelectWordAtCaret()
    {
        var line = _lines[_caretLine];
        if (line.Length == 0) { _anchorLine = _caretLine; _anchorCol = _caretCol; return; }

        int start = Math.Min(_caretCol, line.Length - 1);
        int end = start;
        bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

        if (IsWord(line[start]))
        {
            while (start > 0 && IsWord(line[start - 1])) start--;
            while (end < line.Length && IsWord(line[end])) end++;
        }
        else
        {
            // Non-word: just select that single character.
            end = Math.Min(line.Length, start + 1);
        }

        _anchorLine = _caretLine; _anchorCol = start;
        _caretCol = end;
    }

    private void SelectLine(int line)
    {
        line = Math.Clamp(line, 0, _lines.Count - 1);
        _anchorLine = line; _anchorCol = 0;
        _caretLine = line; _caretCol = _lines[line].Length;
    }

    /// <summary>
    /// Removes the currently selected range from the buffer, leaving the
    /// caret at the start of the deletion and clearing the selection.
    /// </summary>
    private void DeleteSelection()
    {
        if (!HasSelection) return;
        var (sl, sc, el, ec) = GetNormalizedSelection();

        if (sl == el)
        {
            _lines[sl] = _lines[sl].Remove(sc, ec - sc);
        }
        else
        {
            var head = _lines[sl].Substring(0, sc);
            var tail = _lines[el].Substring(ec);
            _lines[sl] = head + tail;
            _lines.RemoveRange(sl + 1, el - sl);
        }

        _caretLine = sl;
        _caretCol = sc;
        _anchorLine = sl;
        _anchorCol = sc;
        MarkDirty();
    }

    private async System.Threading.Tasks.Task CopySelectionAsync()
    {
        var clipboard = Avalonia.Controls.TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;

        // When nothing is selected, copy the current line (matches VS / VS Code).
        var text = HasSelection
            ? GetSelectedText()
            : _lines[_caretLine] + "\n";
        if (string.IsNullOrEmpty(text)) return;

        try { await clipboard.SetTextAsync(text); } catch { /* clipboard unavailable */ }
    }

    private async System.Threading.Tasks.Task CutSelectionAsync()
    {
        if (IsReadOnly) return;
        var clipboard = Avalonia.Controls.TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;

        BeginEdit();
        string text;
        if (HasSelection)
        {
            text = GetSelectedText();
            DeleteSelection();
        }
        else
        {
            // No selection: cut the whole current line.
            text = _lines[_caretLine] + "\n";
            if (_lines.Count == 1)
            {
                _lines[0] = string.Empty;
            }
            else
            {
                _lines.RemoveAt(_caretLine);
                if (_caretLine >= _lines.Count) _caretLine = _lines.Count - 1;
            }
            _caretCol = Math.Min(_caretCol, _lines[_caretLine].Length);
            _anchorLine = _caretLine;
            _anchorCol = _caretCol;
            MarkDirty();
        }

        try { await clipboard.SetTextAsync(text); } catch { /* clipboard unavailable */ }

        ResetCaretBlink();
        EnsureCaretVisible();
        UpdateScrollBar();
        HideCompletion();
        CaretChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private async System.Threading.Tasks.Task PasteAsync()
    {
        if (IsReadOnly) return;
        var clipboard = Avalonia.Controls.TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;

        string? text;
        try { text = await clipboard.TryGetTextAsync(); }
        catch { return; }

        if (string.IsNullOrEmpty(text)) return;

        InsertText(text);
        ResetCaretBlink();
        EnsureCaretVisible();
        UpdateScrollBar();
        HideCompletion();
        CaretChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>Public copy hook for context menus / toolbar buttons.</summary>
    public System.Threading.Tasks.Task CopyAsync() => CopySelectionAsync();

    /// <summary>Public cut hook for context menus / toolbar buttons.</summary>
    public System.Threading.Tasks.Task CutAsync() => CutSelectionAsync();

    /// <summary>Public paste hook for context menus / toolbar buttons.</summary>
    public System.Threading.Tasks.Task PasteFromClipboardAsync() => PasteAsync();

    #endregion

    #region Render

    public override void Render(DrawingContext context)
    {
        var bg = Accents.WindowContentBrush;
        context.FillRectangle(bg, new Rect(Bounds.Size));

        var gutterW = GutterWidth;
        var gutterColor = new SolidColorBrush(Color.FromArgb(50, 0, 0, 0));
        context.FillRectangle(gutterColor, new Rect(0, 0, gutterW, Bounds.Height));

        var dividerBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255));
        context.FillRectangle(dividerBrush, new Rect(gutterW - 1, 0, 1, Bounds.Height));

        var fg = Accents.TextPrimaryBrush;
        var dim = Accents.TextSecondaryBrush;
        var accent = new SolidColorBrush(Accents.AccentPrimary);

        var scrollOffset = _vScrollBar.IsVisible ? _vScrollBar.Value : 0;
        var firstVisible = Math.Max(0, (int)(scrollOffset / LineHeight));
        var lastVisible = Math.Min(_lines.Count - 1, firstVisible + (int)(Bounds.Height / LineHeight) + 1);

        var charWidth = GetCharWidth();
        var topPad = 4d;

        // Caret line highlight (only when there is no active selection so the
        // selection rectangles aren't visually muddied).
        if (IsFocused && !HasSelection)
        {
            var hlY = topPad + _caretLine * LineHeight - scrollOffset;
            if (hlY > -LineHeight && hlY < Bounds.Height)
            {
                var hl = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
                context.FillRectangle(hl, new Rect(gutterW, hlY, Bounds.Width - gutterW, LineHeight));
            }
        }

        // Bracket matching: when the caret sits adjacent to a bracket, find
        // its mate and outline both. Skipped during active selection so it
        // doesn't fight the selection highlight visually.
        if (IsFocused && !HasSelection && TryFindBracketPair(out var b1L, out var b1C, out var b2L, out var b2C))
        {
            var matchPen = new Pen(new SolidColorBrush(
                Color.FromArgb(180, Accents.AccentPrimary.R, Accents.AccentPrimary.G, Accents.AccentPrimary.B)),
                thickness: 1);
            void DrawBracketBox(int line, int col)
            {
                var x = gutterW + TextPaddingX + col * charWidth;
                var y = topPad + line * LineHeight - scrollOffset;
                if (y < -LineHeight || y > Bounds.Height) return;
                context.DrawRectangle(null, matchPen,
                    new Rect(x - 0.5, y + 1, charWidth + 1, LineHeight - 2), 2, 2);
            }
            DrawBracketBox(b1L, b1C);
            DrawBracketBox(b2L, b2C);
        }

        // Selection highlight - drawn underneath the text so glyphs remain
        // crisp on top of the accent-tinted background.
        if (HasSelection)
        {
            var (sl, sc, el, ec) = GetNormalizedSelection();
            var selBrush = new SolidColorBrush(
                Color.FromArgb(90,
                    Accents.AccentPrimary.R,
                    Accents.AccentPrimary.G,
                    Accents.AccentPrimary.B));

            for (int i = Math.Max(sl, firstVisible); i <= Math.Min(el, lastVisible); i++)
            {
                int colStart = (i == sl) ? sc : 0;
                int colEnd = (i == el) ? ec : _lines[i].Length;
                var rectX = gutterW + TextPaddingX + colStart * charWidth;
                var rectY = topPad + i * LineHeight - scrollOffset;
                var rectW = Math.Max(charWidth * 0.5, (colEnd - colStart) * charWidth);
                // For lines that wrap into the next line via newline, extend a
                // little so the user sees the line-break is selected too.
                if (i < el) rectW += charWidth * 0.5;
                context.FillRectangle(selBrush, new Rect(rectX, rectY, rectW, LineHeight));
            }
        }

        for (int i = firstVisible; i <= lastVisible; i++)
        {
            var y = topPad + i * LineHeight - scrollOffset;

            // Line number
            var numText = (i + 1).ToString(CultureInfo.InvariantCulture);
            var lineNumBrush = i == _caretLine ? accent : (IBrush)dim;
            DrawText(context, numText, gutterW - GutterPaddingX - charWidth * numText.Length, y, lineNumBrush);

            // Source line
            DrawSourceLine(context, _lines[i], gutterW + TextPaddingX, y, fg, charWidth);
        }

        // Caret
        if (IsFocused && _caretVisible)
        {
            var caretX = gutterW + TextPaddingX + _caretCol * charWidth;
            var caretY = topPad + _caretLine * LineHeight - scrollOffset;
            if (caretY > -LineHeight && caretY < Bounds.Height)
            {
                context.FillRectangle(fg, new Rect(caretX, caretY + 1, 1.5, LineHeight - 2));
            }
        }
    }

    private void DrawText(DrawingContext context, string text, double x, double y, IBrush brush)
    {
        if (string.IsNullOrEmpty(text)) return;
        var ft = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _typeface,
            FontSize,
            brush);
        context.DrawText(ft, new Point(x, y));
    }

    private void DrawSourceLine(DrawingContext context, string line, double x, double y, IBrush defaultBrush, double charWidth)
    {
        if (string.IsNullOrEmpty(line)) return;

        if (!string.Equals(Language, "csharp", StringComparison.OrdinalIgnoreCase))
        {
            DrawText(context, line, x, y, defaultBrush);
            return;
        }

        // Paint the whole line first in the default colour so punctuation /
        // operators (which the C# tokenizer skips) are still rendered. Then
        // overdraw recognised tokens (keywords, types, strings, ...) in their
        // accent colours on top.
        DrawText(context, line, x, y, defaultBrush);

        var tokens = TokenizeCSharp(line);
        foreach (var tk in tokens)
        {
            if (tk.Kind == CSharpTokenKind.Plain) continue;
            var brush = BrushForToken(tk.Kind, defaultBrush);
            DrawText(context, line.Substring(tk.Start, tk.Length), x + tk.Start * charWidth, y, brush);
        }
    }

    private IBrush BrushForToken(CSharpTokenKind kind, IBrush defaultBrush) => kind switch
    {
        CSharpTokenKind.Keyword => new SolidColorBrush(Color.FromRgb(86, 156, 214)),     // blue
        CSharpTokenKind.Type => new SolidColorBrush(Color.FromRgb(78, 201, 176)),        // teal
        CSharpTokenKind.String => new SolidColorBrush(Color.FromRgb(214, 157, 133)),     // brown-orange
        CSharpTokenKind.Number => new SolidColorBrush(Color.FromRgb(181, 206, 168)),     // green
        CSharpTokenKind.Comment => new SolidColorBrush(Color.FromRgb(106, 153, 85)),     // green
        CSharpTokenKind.Preprocessor => new SolidColorBrush(Color.FromRgb(155, 155, 155)),
        _ => defaultBrush
    };

    private enum CSharpTokenKind { Plain, Keyword, Type, String, Number, Comment, Preprocessor }
    private readonly record struct CSharpToken(int Start, int Length, CSharpTokenKind Kind);

    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract","as","async","await","base","bool","break","byte","case","catch","char","checked",
        "class","const","continue","decimal","default","delegate","do","double","else","enum","event",
        "explicit","extern","false","finally","fixed","float","for","foreach","goto","if","implicit",
        "in","int","interface","internal","is","lock","long","namespace","new","null","object","operator",
        "out","override","params","private","protected","public","readonly","record","ref","return","sbyte",
        "sealed","short","sizeof","stackalloc","static","string","struct","switch","this","throw","true",
        "try","typeof","uint","ulong","unchecked","unsafe","ushort","using","var","virtual","void","volatile",
        "while","with","yield","nameof","global","partial","get","set","init","required","file"
    };

    private static List<CSharpToken> TokenizeCSharp(string line)
    {
        var tokens = new List<CSharpToken>();
        int i = 0;
        int n = line.Length;

        while (i < n)
        {
            char c = line[i];

            // Line comment
            if (c == '/' && i + 1 < n && line[i + 1] == '/')
            {
                tokens.Add(new CSharpToken(i, n - i, CSharpTokenKind.Comment));
                break;
            }

            // Preprocessor (#region, #if, ...)
            if (c == '#' && (i == 0 || IsAllWhitespace(line, 0, i)))
            {
                tokens.Add(new CSharpToken(i, n - i, CSharpTokenKind.Preprocessor));
                break;
            }

            // String / interpolated / verbatim - simplified single-line scan
            if (c == '"' || (c == '@' && i + 1 < n && line[i + 1] == '"') ||
                (c == '$' && i + 1 < n && line[i + 1] == '"'))
            {
                int start = i;
                if (c == '@' || c == '$') i++;
                i++; // opening quote
                while (i < n)
                {
                    if (line[i] == '\\' && i + 1 < n) { i += 2; continue; }
                    if (line[i] == '"') { i++; break; }
                    i++;
                }
                tokens.Add(new CSharpToken(start, i - start, CSharpTokenKind.String));
                continue;
            }

            // Char literal
            if (c == '\'')
            {
                int start = i;
                i++;
                while (i < n)
                {
                    if (line[i] == '\\' && i + 1 < n) { i += 2; continue; }
                    if (line[i] == '\'') { i++; break; }
                    i++;
                }
                tokens.Add(new CSharpToken(start, i - start, CSharpTokenKind.String));
                continue;
            }

            // Number
            if (char.IsDigit(c))
            {
                int start = i;
                while (i < n && (char.IsLetterOrDigit(line[i]) || line[i] == '.' || line[i] == '_'))
                    i++;
                tokens.Add(new CSharpToken(start, i - start, CSharpTokenKind.Number));
                continue;
            }

            // Identifier / keyword / type
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < n && (char.IsLetterOrDigit(line[i]) || line[i] == '_'))
                    i++;
                var word = line.Substring(start, i - start);
                CSharpTokenKind kind;
                if (CSharpKeywords.Contains(word))
                    kind = CSharpTokenKind.Keyword;
                else if (word.Length > 0 && char.IsUpper(word[0]))
                    kind = CSharpTokenKind.Type;
                else
                    kind = CSharpTokenKind.Plain;
                tokens.Add(new CSharpToken(start, i - start, kind));
                continue;
            }

            i++;
        }

        return tokens;
    }

    private static bool IsAllWhitespace(string s, int start, int endExclusive)
    {
        for (int k = start; k < endExclusive; k++)
            if (!char.IsWhiteSpace(s[k])) return false;
        return true;
    }

    private double GetCharWidth()
    {
        if (_cachedCharWidth > 0) return _cachedCharWidth;
        var ft = new FormattedText(
            "M",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _typeface,
            FontSize,
            Brushes.White);
        _cachedCharWidth = ft.Width;
        return _cachedCharWidth;
    }

    private void ResetCaretBlink()
    {
        _caretVisible = true;
        _caretTimer?.Stop();
        _caretTimer?.Start();
    }

    #endregion

    #region Completion / IntelliSense

    private Border? _completionPopup;
    private StackPanel? _completionList;
    private readonly List<string> _completionItems = new();
    private int _completionSelected;
    private bool _completionOpen;
    private int _completionPrefixStartCol;
    private int _completionPrefixLine;
    private const int CompletionMaxItems = 8;

    private void EnsureCompletionPopup()
    {
        if (_completionPopup != null) return;

        _completionList = new StackPanel { Orientation = Orientation.Vertical };
        _completionPopup = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(245, 30, 30, 30)),
            BorderBrush = new SolidColorBrush(Accents.AccentPrimary),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            MinWidth = 120,
            MaxWidth = 240,
            MaxHeight = 200,
            IsVisible = false,
            IsHitTestVisible = false,
            Child = _completionList
        };
        VisualChildren.Add(_completionPopup);
        LogicalChildren.Add(_completionPopup);
    }

    private (int startCol, string prefix) GetCurrentPrefix()
    {
        var line = _lines[_caretLine];
        int start = _caretCol;
        while (start > 0 && (char.IsLetterOrDigit(line[start - 1]) || line[start - 1] == '_'))
            start--;
        return (start, line.Substring(start, _caretCol - start));
    }

    private IEnumerable<string> CollectCandidates()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (string.Equals(Language, "csharp", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var kw in CSharpKeywords)
                if (seen.Add(kw)) yield return kw;

            // Built-in DOSI + Avalonia vocabulary so users get useful suggestions
            // even before they've typed a symbol themselves.
            foreach (var sym in DosiVocabulary)
                if (seen.Add(sym)) yield return sym;
        }

        // Harvest identifiers from the current document so the user gets
        // suggestions for symbols they've already typed (variables, methods, types).
        foreach (var ln in _lines)
        {
            int i = 0;
            while (i < ln.Length)
            {
                if (char.IsLetter(ln[i]) || ln[i] == '_')
                {
                    int s = i;
                    while (i < ln.Length && (char.IsLetterOrDigit(ln[i]) || ln[i] == '_'))
                        i++;
                    var w = ln.Substring(s, i - s);
                    if (w.Length > 1 && seen.Add(w)) yield return w;
                }
                else
                {
                    i++;
                }
            }
        }
    }

    private void RefreshCompletion()
    {
        var (startCol, prefix) = GetCurrentPrefix();
        if (prefix.Length < 1) { HideCompletion(); return; }

        var matches = CollectCandidates()
            .Where(w => w.Length > prefix.Length &&
                        w.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(w => w, StringComparer.OrdinalIgnoreCase)
            .Take(CompletionMaxItems)
            .ToList();

        if (matches.Count == 0) { HideCompletion(); return; }

        EnsureCompletionPopup();
        _completionItems.Clear();
        _completionItems.AddRange(matches);
        _completionSelected = 0;
        _completionPrefixLine = _caretLine;
        _completionPrefixStartCol = startCol;
        _completionOpen = true;
        _completionPopup!.IsVisible = true;
        RebuildCompletionList();
        InvalidateArrange();
    }

    private void RebuildCompletionList()
    {
        if (_completionList == null) return;
        _completionList.Children.Clear();
        for (int i = 0; i < _completionItems.Count; i++)
        {
            bool selected = i == _completionSelected;
            var tb = new TextBlock
            {
                Text = _completionItems[i],
                FontSize = FontSize,
                FontFamily = new FontFamily("Consolas, Menlo, Courier New, monospace"),
                Foreground = selected
                    ? new SolidColorBrush(Accents.TextOnAccent)
                    : Accents.TextPrimaryBrush,
                Padding = new Thickness(8, 2)
            };
            var row = new Border
            {
                Background = selected
                    ? new SolidColorBrush(Accents.AccentPrimary)
                    : Brushes.Transparent,
                CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = tb
            };
            _completionList.Children.Add(row);
        }
    }

    private void HideCompletion()
    {
        if (!_completionOpen) return;
        _completionOpen = false;
        if (_completionPopup != null) _completionPopup.IsVisible = false;
    }

    private void AcceptCompletion()
    {
        if (!_completionOpen ||
            _completionSelected < 0 ||
            _completionSelected >= _completionItems.Count ||
            _completionPrefixLine != _caretLine)
        {
            HideCompletion();
            return;
        }

        var word = _completionItems[_completionSelected];
        var line = _lines[_caretLine];
        if (_completionPrefixStartCol < 0 || _caretCol > line.Length)
        {
            HideCompletion();
            return;
        }

        BeginEdit();
        _lines[_caretLine] = line.Substring(0, _completionPrefixStartCol) + word + line.Substring(_caretCol);
        _caretCol = _completionPrefixStartCol + word.Length;
        HideCompletion();
        MarkDirty();
        EnsureCaretVisible();
        CaretChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private void MoveCompletion(int delta)
    {
        if (!_completionOpen || _completionItems.Count == 0) return;
        _completionSelected = (_completionSelected + delta + _completionItems.Count) % _completionItems.Count;
        RebuildCompletionList();
    }

    #endregion

    #region Snippets / auto-pair / comment toggle

    /// <summary>
    /// Common DOSI + Avalonia symbols surfaced in completion so users discover
    /// the API without having to type each name first.
    /// </summary>
    private static readonly string[] DosiVocabulary =
    {
        // Avalonia layout / controls
        "StackPanel", "Grid", "Border", "TextBlock", "TextBox", "Button", "Image",
        "Canvas", "DockPanel", "WrapPanel", "ScrollViewer", "ContentControl",
        "Orientation", "HorizontalAlignment", "VerticalAlignment", "Thickness",
        "CornerRadius", "RowDefinitions", "ColumnDefinitions", "Margin", "Padding",
        "Background", "Foreground", "BorderBrush", "BorderThickness", "Width",
        "Height", "MinWidth", "MinHeight", "MaxWidth", "MaxHeight", "Children",
        "Content", "Brushes", "Color", "SolidColorBrush", "LinearGradientBrush",
        "FontWeight", "FontSize", "FontFamily", "TextAlignment", "TextWrapping",

        // DOSI core
        "DOSIWindow", "DOSIScreen", "DOSIButton", "DOSITextBox", "DOSIDialog",
        "DOSIScrollViewer", "DOSIScrollBar", "DOSICodeEditor", "DOSIPopNotification",
        "DOSILoadingAnim", "DOSISuccessAnim", "AccentManager", "WindowManager",
        "UserManager", "ScreenManager", "DialogResult", "DialogType", "LoadingSize",

        // Frequently-used members
        "Title", "WindowWidth", "WindowHeight", "MinimumSize", "Icon",
        "AccentPrimary", "AccentSecondary", "WindowChromeBrush", "WindowContentBrush",
        "TextPrimaryBrush", "TextSecondaryBrush", "AccentPrimaryBrush", "TextOnAccent",
        "Instance", "Show", "ShowAsync", "Close", "OpenWindow", "CloseWindow",
        "Alert", "Confirm", "Custom"
    };

    /// <summary>
    /// Snippet triggers expanded when the user presses Tab right after the word.
    /// Use "$|" inside the body to mark where the caret should land. Newlines are
    /// indented to match the trigger's own leading whitespace.
    /// </summary>
    private static readonly Dictionary<string, string> Snippets = new(StringComparer.Ordinal)
    {
        ["dwin"] =
            "var window = new DOSIWindow\n{\n    Title = \"$|\",\n    WindowWidth = 480,\n    WindowHeight = 320,\n    Content = null\n};",
        ["stack"] =
            "var stack = new StackPanel\n{\n    Orientation = Orientation.Vertical,\n    Spacing = 8,\n    Children = { $| }\n};",
        ["grid"] =
            "var grid = new Grid\n{\n    RowDefinitions = new RowDefinitions(\"Auto,*\"),\n    ColumnDefinitions = new ColumnDefinitions(\"*\")\n};$|",
        ["border"] =
            "var border = new Border\n{\n    Background = AccentManager.Instance.WindowContentBrush,\n    Padding = new Thickness(12),\n    Child = $|\n};",
        ["text"] =
            "new TextBlock { Text = \"$|\", Foreground = AccentManager.Instance.TextPrimaryBrush }",
        ["btn"] =
            "var button = new DOSIButton { Content = \"$|\" };\nbutton.Click += (_, _) => { };",
        ["dlg"] =
            "await DOSIDialog.Alert(host, \"$|\", \"\");",
        ["acc"] =
            "AccentManager.Instance.$|",
        ["using"] =
            "using Avalonia;\nusing Avalonia.Controls;\nusing Avalonia.Layout;\nusing Avalonia.Media;\nusing DOSI.CORE.AccentManagement;\nusing DOSI.CORE.UIComponents;$|",
        ["main"] =
            "public static class Program\n{\n    public static Control Run()\n    {\n        $|\n        return new TextBlock { Text = \"Hello\" };\n    }\n}"
    };

    private bool TryExpandSnippet()
    {
        var (start, prefix) = GetCurrentPrefix();
        if (prefix.Length == 0 || !Snippets.TryGetValue(prefix, out var body)) return false;

        BeginEdit();
        // Indent every line of the snippet to match the trigger's leading whitespace.
        var line = _lines[_caretLine];
        var indent = new string(line.TakeWhile(c => c == ' ' || c == '\t').ToArray());

        var caretMarker = body.IndexOf("$|", StringComparison.Ordinal);
        var clean = body.Replace("$|", string.Empty);
        var indented = clean.Replace("\n", "\n" + indent);

        // Replace the trigger word with the snippet body.
        var head = line.Substring(0, start);
        var tail = line.Substring(_caretCol);
        var combined = head + indented + tail;
        var pieces = combined.Split('\n');

        _lines[_caretLine] = pieces[0];
        for (int i = 1; i < pieces.Length; i++)
            _lines.Insert(_caretLine + i, pieces[i]);

        // Position the caret at $| (or end of snippet if none specified).
        if (caretMarker < 0) caretMarker = clean.Length;
        var beforeCaret = (head + indented[..Math.Min(caretMarker, indented.Length)]);
        var beforeLines = beforeCaret.Split('\n');
        _caretLine = _caretLine + beforeLines.Length - 1;
        _caretCol = beforeLines[^1].Length;

        MarkDirty();
        UpdateScrollBar();
        return true;
    }

    /// <summary>
    /// If the caret is adjacent to a bracket character, locates its matching
    /// counterpart and outputs the positions of both. Returns <c>false</c>
    /// when there's no bracket near the caret or no match is found.
    /// </summary>
    private bool TryFindBracketPair(out int aLine, out int aCol, out int bLine, out int bCol)
    {
        aLine = aCol = bLine = bCol = 0;
        if (_caretLine < 0 || _caretLine >= _lines.Count) return false;

        var line = _lines[_caretLine];
        // Prefer the bracket immediately to the LEFT of the caret (matches VS).
        char? candidate = null;
        int candidateCol = -1;
        if (_caretCol > 0 && IsBracket(line[_caretCol - 1]))
        {
            candidate = line[_caretCol - 1]; candidateCol = _caretCol - 1;
        }
        else if (_caretCol < line.Length && IsBracket(line[_caretCol]))
        {
            candidate = line[_caretCol]; candidateCol = _caretCol;
        }
        if (candidate == null) return false;

        var c = candidate.Value;
        var (open, close, forward) = c switch
        {
            '(' => ('(', ')', true),
            '[' => ('[', ']', true),
            '{' => ('{', '}', true),
            ')' => ('(', ')', false),
            ']' => ('[', ']', false),
            '}' => ('{', '}', false),
            _ => ('\0', '\0', true)
        };
        if (open == '\0') return false;

        int depth = 1;
        if (forward)
        {
            for (int li = _caretLine; li < _lines.Count; li++)
            {
                var s = _lines[li];
                int colStart = li == _caretLine ? candidateCol + 1 : 0;
                for (int ci = colStart; ci < s.Length; ci++)
                {
                    if (s[ci] == open) depth++;
                    else if (s[ci] == close) { depth--; if (depth == 0) { aLine = _caretLine; aCol = candidateCol; bLine = li; bCol = ci; return true; } }
                }
            }
        }
        else
        {
            for (int li = _caretLine; li >= 0; li--)
            {
                var s = _lines[li];
                int colStart = li == _caretLine ? candidateCol - 1 : s.Length - 1;
                for (int ci = colStart; ci >= 0; ci--)
                {
                    if (s[ci] == close) depth++;
                    else if (s[ci] == open) { depth--; if (depth == 0) { aLine = _caretLine; aCol = candidateCol; bLine = li; bCol = ci; return true; } }
                }
            }
        }
        return false;
    }

    private static bool IsBracket(char c) =>
        c == '(' || c == ')' || c == '[' || c == ']' || c == '{' || c == '}';

    private bool TryHandleAutoPair(char c)
    {
        var line = _lines[_caretLine];

        // Step over an existing closer (so typing ')' on top of ')' just moves on).
        if ((c == ')' || c == ']' || c == '}' || c == '"' || c == '\'') &&
            _caretCol < line.Length && line[_caretCol] == c)
        {
            _caretCol++;
            return true;
        }
        char close = c switch
        {
            '(' => ')',
            '[' => ']',
            '{' => '}',
            '"' => '"',
            '\'' => '\'',
            _ => '\0'
        };
        if (close == '\0') return false;

        // For quotes: don't auto-pair when the caret sits next to a word char
        // (probably typing a contraction or a string suffix).
        if ((c == '"' || c == '\'') &&
            _caretCol > 0 &&
            (char.IsLetterOrDigit(line[_caretCol - 1]) || line[_caretCol - 1] == '_'))
        {
            return false;
        }

        BeginEdit();
        _lines[_caretLine] = line.Substring(0, _caretCol) + c + close + line.Substring(_caretCol);
        _caretCol++;
        MarkDirty();
        return true;
    }

    private void ToggleLineComment()
    {
        if (_caretLine < 0 || _caretLine >= _lines.Count) return;
        BeginEdit();
        var line = _lines[_caretLine];
        var leading = line.TakeWhile(c => c == ' ' || c == '\t').Count();
        var trimmed = line.Substring(leading);

        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            // Uncomment - drop the leading "// " or "//".
            var removeLen = trimmed.StartsWith("// ", StringComparison.Ordinal) ? 3 : 2;
            _lines[_caretLine] = line.Substring(0, leading) + trimmed.Substring(removeLen);
            _caretCol = Math.Max(0, _caretCol - removeLen);
        }
        else
        {
            _lines[_caretLine] = line.Substring(0, leading) + "// " + trimmed;
            _caretCol += 3;
        }
        MarkDirty();
    }

    #endregion
}
