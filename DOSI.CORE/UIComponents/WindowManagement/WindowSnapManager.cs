using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using DOSI.CORE.AccentManagement;

namespace DOSI.CORE.UIComponents.WindowManagement;

/// <summary>
/// Aero / macOS Stage Manager-style window snapping for any
/// <see cref="WindowManager"/>. While a <see cref="DOSIWindow"/> is being
/// dragged, this manager paints an animated translucent preview rectangle
/// over the desktop showing where the window will land if released. On
/// drop, it animates the window smoothly into the snap target.
///
/// Snap zones (Windows-style):
///   * Top edge          -> Maximize (full work area, below the taskbar inset)
///   * Left / right edge -> Half screen
///   * Any of the four corners -> Quarter screen
///
/// The preview chip is rendered onto the desktop <see cref="Canvas"/>
/// underneath the active window so the user keeps full visual context of
/// the window they are dragging. All visuals are accent-aware and update
/// live when the user changes their accent color in Settings.
/// </summary>
public sealed class WindowSnapManager : IDisposable
{
    private enum SnapZone
    {
        None,
        // Halves
        Left,
        Right,
        Top,            // Maximize (full work area)
        TopHalf,        // Upper half across full width
        BottomHalf,     // Lower half across full width
        // Quarter corners
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
        // Thirds (Windows 11 / FancyZones-style)
        LeftThird,
        CenterThird,
        RightThird
    }

    /// <summary>How close the cursor must come to a screen edge before the
    /// edge snap zone activates. Matches the responsive feel of the
    /// Windows 11 snap layouts overlay - small enough to never trigger by
    /// accident, large enough that hitting it during a quick fling feels
    /// effortless.</summary>
    private const double EdgeThreshold = 8;

    /// <summary>Side length of the square "corner box" anchored at each
    /// screen corner. While the cursor is inside this box AND within the
    /// edge band, the snap target promotes from a half-screen to the
    /// matching quarter, so corners are easy to hit without pixel-perfect
    /// aim.</summary>
    private const double CornerSize = 90;

    /// <summary>How close (in pixels) the cursor must come to the bottom
    /// edge to trigger the bottom-half snap. Slightly larger than the
    /// generic edge threshold because the bottom of the desktop tends to
    /// be where users park the mouse between actions.</summary>
    private const double BottomEdgeThreshold = 12;

    /// <summary>Holding Shift while dragging promotes left / right edges
    /// from half-screen to a thirds layout (left third, center third, right
    /// third). Mirrors the Windows 11 PowerToys FancyZones gesture. Tracked
    /// off the most recent pointer move because Avalonia 11 doesn't expose
    /// a global keyboard-state singleton.</summary>
    private bool _shiftHeld;

    private static AccentManager Accents => AccentManager.Instance;

    private readonly Canvas _desktop;
    private readonly WindowManager _manager;
    private readonly Border _preview;

    /// <summary>Tracks each window's pre-snap rect so we could later restore
    /// it on un-snap. Populated lazily the first time a window is snapped.
    /// </summary>
    private readonly Dictionary<DOSIWindow, Rect> _preSnapBounds = new();

    private DOSIWindow? _activeWindow;
    private SnapZone _currentZone = SnapZone.None;
    private bool _previewVisible;

    // Lightweight DispatcherTimer-driven tween for the preview chip. Avoids
    // pulling in Avalonia.Animation for what is essentially a 4-property lerp.
    private DispatcherTimer? _animTimer;
    private Rect _animFromRect;
    private Rect _animToRect;
    private double _animFromOpacity;
    private double _animToOpacity;
    private DateTime _animStart;
    private static readonly TimeSpan PreviewAnimDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan SnapAnimDuration = TimeSpan.FromMilliseconds(220);
    private static readonly IEasing PreviewEasing = new CubicEaseOut();
    private static readonly IEasing SnapEasing = new CubicEaseInOut();

    public WindowSnapManager(Canvas desktop, WindowManager manager)
    {
        _desktop = desktop ?? throw new ArgumentNullException(nameof(desktop));
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));

        _preview = new Border
        {
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(2),
            IsHitTestVisible = false,
            IsVisible = false,
            Opacity = 0,
            Width = 0,
            Height = 0
        };
        ApplyAccent();
        Canvas.SetLeft(_preview, 0);
        Canvas.SetTop(_preview, 0);
        // Insert at index 0 so the preview always paints UNDER any open
        // window - the user keeps a clear view of the window they're
        // dragging while still seeing the snap target glow through.
        _desktop.Children.Insert(0, _preview);

        _manager.WindowOpened += OnWindowOpened;
        _manager.WindowClosed += OnWindowClosed;
        foreach (var w in _manager.Windows) Attach(w);

        Accents.AccentChanged += OnAccentChanged;
    }

    private void OnAccentChanged(object? sender, EventArgs e) => ApplyAccent();

    private void ApplyAccent()
    {
        var c = Accents.AccentPrimary;
        _preview.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(90, c.R, c.G, c.B), 0),
                new GradientStop(Color.FromArgb(40, c.R, c.G, c.B), 1)
            }
        };
        _preview.BorderBrush = new SolidColorBrush(Color.FromArgb(220, c.R, c.G, c.B));
        _preview.BoxShadow = new BoxShadows(new BoxShadow
        {
            OffsetX = 0,
            OffsetY = 8,
            Blur = 36,
            Spread = 0,
            Color = Color.FromArgb(140, c.R, c.G, c.B)
        });
    }

    private void OnWindowOpened(object? sender, DOSIWindowEventArgs e) => Attach(e.Window);
    private void OnWindowClosed(object? sender, DOSIWindowEventArgs e) => Detach(e.Window);

    private void Attach(DOSIWindow window)
    {
        window.DragStateChanged += OnWindowDragStateChanged;
    }

    private void Detach(DOSIWindow window)
    {
        window.DragStateChanged -= OnWindowDragStateChanged;
        window.PointerMoved -= OnDragPointerMoved;
        _preSnapBounds.Remove(window);
    }

    private void OnWindowDragStateChanged(object? sender, bool isDragging)
    {
        if (sender is not DOSIWindow w) return;

        if (isDragging)
        {
            _activeWindow = w;
            _currentZone = SnapZone.None;
            // Pointer is captured by the window's chrome during a drag, so
            // PointerMoved events bubble up through the window itself rather
            // than reaching the desktop canvas. Subscribe directly on the
            // window for the duration of the drag.
            w.PointerMoved += OnDragPointerMoved;
        }
        else
        {
            w.PointerMoved -= OnDragPointerMoved;
            CommitSnap(w);
            _activeWindow = null;
        }
    }

    private void OnDragPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_activeWindow == null) return;
        _shiftHeld = (e.KeyModifiers & KeyModifiers.Shift) == KeyModifiers.Shift;
        var p = e.GetPosition(_desktop);
        var zone = ResolveZone(p);
        if (zone == _currentZone) return;
        _currentZone = zone;
        if (zone == SnapZone.None) HidePreview();
        else ShowPreviewFor(zone);
    }

    private SnapZone ResolveZone(Point p)
    {
        var w = _desktop.Bounds.Width;
        var h = _desktop.Bounds.Height;
        if (w <= 0 || h <= 0) return SnapZone.None;
        var topInset = _manager.TopWorkAreaInset;

        bool nearTop = p.Y <= topInset + EdgeThreshold;
        bool nearBottom = p.Y >= h - BottomEdgeThreshold;
        bool nearLeft = p.X <= EdgeThreshold;
        bool nearRight = p.X >= w - EdgeThreshold;

        // Corner promotion: if the cursor is inside a corner box AND on the
        // adjacent edge band, snap to the quarter instead of the half. This
        // is what makes corners feel "sticky" the way Windows 11 does.
        bool inLeftCornerBox = p.X <= CornerSize;
        bool inRightCornerBox = p.X >= w - CornerSize;
        bool inTopCornerBox = p.Y <= topInset + CornerSize;
        bool inBottomCornerBox = p.Y >= h - CornerSize;

        if ((nearTop && inLeftCornerBox) || (nearLeft && inTopCornerBox)) return SnapZone.TopLeft;
        if ((nearTop && inRightCornerBox) || (nearRight && inTopCornerBox)) return SnapZone.TopRight;
        if ((nearBottom && inLeftCornerBox) || (nearLeft && inBottomCornerBox)) return SnapZone.BottomLeft;
        if ((nearBottom && inRightCornerBox) || (nearRight && inBottomCornerBox)) return SnapZone.BottomRight;

        // Top edge: full maximize unless the user explicitly asked for the
        // top half by holding Shift, which gives them a horizontal split.
        if (nearTop) return _shiftHeld ? SnapZone.TopHalf : SnapZone.Top;
        // Bottom edge: bottom half. Maximize doesn't make sense from the
        // bottom because the user already had to drag DOWN to reach it.
        if (nearBottom) return SnapZone.BottomHalf;

        // Side edges: half by default, thirds when Shift is held. Thirds
        // pick which column based on the cursor's vertical position so the
        // user can land on left / center / right without releasing Shift.
        if (nearLeft)
        {
            if (!_shiftHeld) return SnapZone.Left;
            return PickThird(p.Y, topInset, h);
        }
        if (nearRight)
        {
            if (!_shiftHeld) return SnapZone.Right;
            return PickThird(p.Y, topInset, h);
        }
        return SnapZone.None;
    }

    /// <summary>Maps a vertical cursor position inside the work area onto
    /// one of the three FancyZones-style thirds (top → left, middle → center,
    /// bottom → right). Keeps the gesture single-handed: hold Shift, slide
    /// up / down along the edge to choose the column, release.</summary>
    private static SnapZone PickThird(double cursorY, double topInset, double totalHeight)
    {
        var workH = Math.Max(1, totalHeight - topInset);
        var rel = (cursorY - topInset) / workH;
        if (rel < 1.0 / 3) return SnapZone.LeftThird;
        if (rel < 2.0 / 3) return SnapZone.CenterThird;
        return SnapZone.RightThird;
    }

    private Rect ZoneRect(SnapZone zone)
    {
        var w = _desktop.Bounds.Width;
        var h = _desktop.Bounds.Height;
        var top = _manager.TopWorkAreaInset;
        var workH = Math.Max(0, h - top);
        var halfW = w / 2.0;
        var halfH = workH / 2.0;
        var thirdW = w / 3.0;
        return zone switch
        {
            SnapZone.Top => new Rect(0, top, w, workH),
            SnapZone.TopHalf => new Rect(0, top, w, halfH),
            SnapZone.BottomHalf => new Rect(0, top + halfH, w, halfH),
            SnapZone.Left => new Rect(0, top, halfW, workH),
            SnapZone.Right => new Rect(halfW, top, halfW, workH),
            SnapZone.TopLeft => new Rect(0, top, halfW, halfH),
            SnapZone.TopRight => new Rect(halfW, top, halfW, halfH),
            SnapZone.BottomLeft => new Rect(0, top + halfH, halfW, halfH),
            SnapZone.BottomRight => new Rect(halfW, top + halfH, halfW, halfH),
            SnapZone.LeftThird => new Rect(0, top, thirdW, workH),
            SnapZone.CenterThird => new Rect(thirdW, top, thirdW, workH),
            SnapZone.RightThird => new Rect(thirdW * 2, top, thirdW, workH),
            _ => default
        };
    }

    private void ShowPreviewFor(SnapZone zone)
    {
        var rect = ZoneRect(zone);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        // Inset slightly so the chip reads as a hovering pane instead of a
        // flat fill against the screen edges. Matches the Windows 11 snap
        // overlay's breathing room.
        const double inset = 8;
        var target = new Rect(
            rect.X + inset,
            rect.Y + inset,
            Math.Max(0, rect.Width - inset * 2),
            Math.Max(0, rect.Height - inset * 2));

        if (!_previewVisible)
        {
            // Pop in from a slightly smaller rect centered on the target for
            // a satisfying "spring open" feel.
            _preview.IsVisible = true;
            var center = target.Center;
            const double popScale = 0.92;
            var startW = target.Width * popScale;
            var startH = target.Height * popScale;
            var startRect = new Rect(
                center.X - startW / 2,
                center.Y - startH / 2,
                startW,
                startH);
            BeginPreviewAnim(startRect, target, 0, 1);
            _previewVisible = true;
        }
        else
        {
            // Glide between zones - the chip morphs from the previous target
            // to the new one without ever fading out, which reads as one
            // continuous overlay.
            BeginPreviewAnim(CurrentPreviewRect(), target, _preview.Opacity, 1);
        }
    }

    private void HidePreview()
    {
        if (!_previewVisible) return;
        _previewVisible = false;
        BeginPreviewAnim(CurrentPreviewRect(), CurrentPreviewRect(), _preview.Opacity, 0);
    }

    private Rect CurrentPreviewRect()
    {
        var x = Canvas.GetLeft(_preview); if (double.IsNaN(x)) x = 0;
        var y = Canvas.GetTop(_preview); if (double.IsNaN(y)) y = 0;
        return new Rect(x, y, _preview.Width, _preview.Height);
    }

    private void BeginPreviewAnim(Rect from, Rect to, double fromOp, double toOp)
    {
        _animFromRect = from;
        _animToRect = to;
        _animFromOpacity = fromOp;
        _animToOpacity = toOp;
        _animStart = DateTime.UtcNow;
        if (_animTimer == null)
        {
            _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(8) };
            _animTimer.Tick += OnPreviewAnimTick;
        }
        if (!_animTimer.IsEnabled) _animTimer.Start();
    }

    private void OnPreviewAnimTick(object? sender, EventArgs e)
    {
        var elapsed = DateTime.UtcNow - _animStart;
        var t = Math.Min(1.0, elapsed.TotalMilliseconds / PreviewAnimDuration.TotalMilliseconds);
        var eased = PreviewEasing.Ease(t);
        Canvas.SetLeft(_preview, Lerp(_animFromRect.X, _animToRect.X, eased));
        Canvas.SetTop(_preview, Lerp(_animFromRect.Y, _animToRect.Y, eased));
        _preview.Width = Math.Max(0, Lerp(_animFromRect.Width, _animToRect.Width, eased));
        _preview.Height = Math.Max(0, Lerp(_animFromRect.Height, _animToRect.Height, eased));
        _preview.Opacity = Lerp(_animFromOpacity, _animToOpacity, eased);

        if (t >= 1.0)
        {
            _animTimer!.Stop();
            if (_preview.Opacity <= 0.01) _preview.IsVisible = false;
        }
    }

    private void CommitSnap(DOSIWindow window)
    {
        var zone = _currentZone;
        _currentZone = SnapZone.None;
        HidePreview();

        if (zone == SnapZone.None) return;
        var rect = ZoneRect(zone);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        // Remember the pre-snap rect (once) so a future "drag off the edge"
        // gesture could restore the original size.
        if (!_preSnapBounds.ContainsKey(window))
        {
            _preSnapBounds[window] = new Rect(
                window.WindowX, window.WindowY,
                window.WindowWidth, window.WindowHeight);
        }

        // Make sure we're starting from a Normal state. If the window happens
        // to be Maximized, the maximize-clamp would fight our animation.
        if (window.WindowState == DOSIWindowState.Maximized)
            window.WindowState = DOSIWindowState.Normal;

        _ = AnimateWindowAsync(window, rect);
    }

    private static async Task AnimateWindowAsync(DOSIWindow window, Rect target)
    {
        var startX = window.WindowX;
        var startY = window.WindowY;
        var startW = window.WindowWidth;
        var startH = window.WindowHeight;
        var t0 = DateTime.UtcNow;
        while (true)
        {
            var t = Math.Min(1.0, (DateTime.UtcNow - t0).TotalMilliseconds / SnapAnimDuration.TotalMilliseconds);
            var e = SnapEasing.Ease(t);
            window.WindowX = Lerp(startX, target.X, e);
            window.WindowY = Lerp(startY, target.Y, e);
            window.WindowWidth = Lerp(startW, target.Width, e);
            window.WindowHeight = Lerp(startH, target.Height, e);
            if (t >= 1.0) break;
            await Task.Delay(8);
        }
        window.WindowX = target.X;
        window.WindowY = target.Y;
        window.WindowWidth = target.Width;
        window.WindowHeight = target.Height;
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    public void Dispose()
    {
        _manager.WindowOpened -= OnWindowOpened;
        _manager.WindowClosed -= OnWindowClosed;
        Accents.AccentChanged -= OnAccentChanged;
        foreach (var w in _manager.Windows) Detach(w);
        _animTimer?.Stop();
        _animTimer = null;
        if (_preview.Parent is Canvas c) c.Children.Remove(_preview);
    }
}
