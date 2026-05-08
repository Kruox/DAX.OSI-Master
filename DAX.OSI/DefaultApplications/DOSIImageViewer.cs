using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DOSI.CORE.AccentManagement;
using DOSI.CORE.Animations;
using DOSI.CORE.UIComponents;
using DOSI.CORE.UIComponents.WindowManagement;
using Path = System.IO.Path;
using AvPath = Avalonia.Controls.Shapes.Path;

namespace DAX.OSI.DefaultApplications;

/// <summary>
/// Modern image viewer for the DOSI virtual operating system.
///
/// Features:
/// <list type="bullet">
///   <item><description>Zoom: mouse wheel (anchored at cursor), +/- keys, toolbar.
///   Smooth tween between zoom levels via <see cref="Tween"/>.</description></item>
///   <item><description>Pan: click-and-drag while zoomed in.</description></item>
///   <item><description>Fit-to-window / actual-size toggle (double-click image, or F / 1 keys).</description></item>
///   <item><description>Rotate 90° CW/CCW (R / Shift+R).</description></item>
///   <item><description>Sibling navigation: auto-discovers other images in the
///   same folder; Left / Right arrow keys cycle.</description></item>
///   <item><description>Open dialog backed by <see cref="DOSIFileExplorer"/> in
///   picker mode for visual consistency with the rest of the OS.</description></item>
///   <item><description>Status bar: filename, dimensions, zoom %, file size.</description></item>
/// </list>
/// </summary>
public sealed class DOSIImageViewer : DOSIWindow
{
    // File extensions the viewer is willing to load. Centralised so adding a
    // new format is a one-line change rather than hunting through the file.
    public static readonly string[] SupportedExtensions =
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".ico", ".tif", ".tiff"
    };

    private static AccentManager Accents => AccentManager.Instance;

    // ----- View / chrome -----
    private readonly Border _canvasBackdrop;
    private readonly Grid _canvasHost;
    private readonly Image _imageView;
    private readonly TranslateTransform _translate = new(0, 0);
    private readonly RotateTransform _rotate = new(0);
    private readonly TextBlock _statusName;
    private readonly TextBlock _statusDimensions;
    private readonly TextBlock _statusZoom;
    private readonly TextBlock _statusSize;
    private readonly TextBlock _emptyState;
    private readonly DOSIButton _btnPrev;
    private readonly DOSIButton _btnNext;
    private readonly DOSIButton _btnFit;
    private readonly DOSIButton _btnActual;
    private readonly DOSIButton _btnZoomIn;
    private readonly DOSIButton _btnZoomOut;
    private readonly DOSIButton _btnRotateLeft;
    private readonly DOSIButton _btnRotateRight;

    // ----- Image / navigation state -----
    private Bitmap? _bitmap;
    private string? _currentPath;
    private List<string> _siblings = new();
    private int _siblingIndex = -1;

    // ----- Zoom / pan state -----
    private const double MinZoom = 0.05;
    private const double MaxZoom = 32.0;
    private double _zoom = 1.0;
    private bool _fitMode = true;        // true => image is sized to fit the canvas
    private bool _isPanning;
    private Point _panStart;
    private double _panStartTx, _panStartTy;
    private Tween? _zoomTween;

    public DOSIImageViewer() : this(null) { }

    public DOSIImageViewer(string? initialImagePath)
    {
        Title = "Image Viewer";
        WindowWidth = 900;
        WindowHeight = 640;
        MinimumSize = new Size(420, 300);
        Icon = CreateIcon();

        // ===== Canvas (the image lives here) =====
        // We drive zoom through LAYOUT (explicit Width/Height) rather than
        // a ScaleTransform. RenderTransform-based scaling rasterizes the
        // bitmap at its natural size and then GPU-stretches the result with
        // a default low-quality filter, which produced visibly blurry zoomed
        // output even after RenderOptions.SetBitmapInterpolationMode(High).
        // Avalonia's high-quality interpolator only runs when the Image
        // control's own measure/arrange picks the target size - hence
        // Stretch.Uniform + ApplyImageSize() per zoom step. Rotate and
        // Translate stay as RenderTransforms because they're pure geometry
        // operations and don't trigger resampling.
        _imageView = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = RelativePoint.Center
        };
        RenderOptions.SetBitmapInterpolationMode(_imageView, BitmapInterpolationMode.HighQuality);
        _imageView.RenderTransform = new TransformGroup
        {
            Children = { _rotate, _translate }
        };

        _emptyState = new TextBlock
        {
            Text = "No image loaded.\nUse Open to choose one, or drop a file from the File Explorer.",
            FontSize = 13,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.85,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };

        _canvasHost = new Grid
        {
            ClipToBounds = true,
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Children = { _imageView, _emptyState }
        };

        // Subtle dark backdrop with a soft inner gradient for contrast - so a
        // light image doesn't bleed into the window chrome and a dark image
        // doesn't sit on a flat black void.
        _canvasBackdrop = new Border
        {
            Background = BuildCanvasBackdrop(),
            Child = _canvasHost
        };

        // Wheel zoom + drag pan + double-click toggle.
        _canvasHost.PointerWheelChanged += OnCanvasWheel;
        _canvasHost.PointerPressed += OnCanvasPressed;
        _canvasHost.PointerMoved += OnCanvasMoved;
        _canvasHost.PointerReleased += OnCanvasReleased;
        _canvasHost.DoubleTapped += (_, _) => ToggleFitActual();
        _canvasHost.SizeChanged += (_, _) => { if (_fitMode) ApplyFit(); };

        // ===== Toolbar =====
        var btnOpen = ToolbarButton("Open", () => OpenFromPicker());
        _btnPrev = ToolbarButton("\u2039 Prev", GoPrevious);
        _btnNext = ToolbarButton("Next \u203A", GoNext);
        _btnZoomOut = ToolbarButton("\u2212", () => ZoomBy(1 / 1.25));
        _btnZoomIn  = ToolbarButton("+", () => ZoomBy(1.25));
        _btnFit     = ToolbarButton("Fit", () => SetFitMode(true));
        _btnActual  = ToolbarButton("1:1", () => SetActualSize());
        _btnRotateLeft  = ToolbarButton("\u21BA", () => RotateBy(-90));
        _btnRotateRight = ToolbarButton("\u21BB", () => RotateBy(90));

        var toolbarLeft = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { btnOpen, ToolbarSeparator(), _btnPrev, _btnNext }
        };

        var toolbarRight = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children =
            {
                _btnRotateLeft, _btnRotateRight,
                ToolbarSeparator(),
                _btnZoomOut, _btnFit, _btnActual, _btnZoomIn
            }
        };

        var toolbarGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(10, 6)
        };
        toolbarGrid.Children.Add(toolbarLeft);   Grid.SetColumn(toolbarLeft, 0);
        toolbarGrid.Children.Add(toolbarRight);  Grid.SetColumn(toolbarRight, 1);

        var toolbar = new Border
        {
            Background = Accents.WindowChromeBrush,
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = toolbarGrid
        };

        // ===== Status bar =====
        _statusName       = StatusText(string.Empty, bold: true);
        _statusDimensions = StatusText(string.Empty);
        _statusZoom       = StatusText(string.Empty);
        _statusSize       = StatusText(string.Empty);

        var statusLeft = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _statusName, _statusDimensions }
        };
        var statusRight = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _statusSize, _statusZoom }
        };

        var statusGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(12, 4)
        };
        statusGrid.Children.Add(statusLeft);  Grid.SetColumn(statusLeft, 0);
        statusGrid.Children.Add(statusRight); Grid.SetColumn(statusRight, 1);

        var statusBar = new Border
        {
            Background = Accents.WindowChromeBrush,
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Height = 26,
            Child = statusGrid
        };

        // ===== Root =====
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        root.Children.Add(toolbar);          Grid.SetRow(toolbar, 0);
        root.Children.Add(_canvasBackdrop);  Grid.SetRow(_canvasBackdrop, 1);
        root.Children.Add(statusBar);        Grid.SetRow(statusBar, 2);

        Content = root;

        // ===== Wiring =====
        AttachedToVisualTree += (_, _) =>
        {
            Accents.AccentChanged += OnAccentChanged;
            // Keyboard shortcuts only fire when the window has focus.
            KeyDown += OnWindowKeyDown;
            Focusable = true;
            Dispatcher.UIThread.Post(() => Focus(), DispatcherPriority.Loaded);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            Accents.AccentChanged -= OnAccentChanged;
            KeyDown -= OnWindowKeyDown;
            _zoomTween?.Stop();
            _zoomTween = null;
        };

        UpdateUiState();

        if (!string.IsNullOrEmpty(initialImagePath) && File.Exists(initialImagePath))
        {
            // Defer until after the canvas has a size so fit-mode math works.
            Dispatcher.UIThread.Post(() => LoadImage(initialImagePath), DispatcherPriority.Loaded);
        }
    }

    // =====================================================================
    // Public API
    // =====================================================================

    /// <summary>
    /// Loads the image at <paramref name="path"/>, replaces whatever is on
    /// screen, and resets the view to fit-to-window. Silently no-ops if the
    /// file is missing or fails to decode.
    /// </summary>
    public void LoadImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        Bitmap? next;
        try
        {
            using var stream = File.OpenRead(path);
            next = new Bitmap(stream);
        }
        catch
        {
            // Decode failure - leave existing image (if any) untouched.
            return;
        }

        // Dispose the old bitmap so we don't accumulate decoded pixel data
        // when the user pages through a folder of large photos.
        _bitmap?.Dispose();
        _bitmap = next;
        _currentPath = path;
        _imageView.Source = _bitmap;
        _emptyState.IsVisible = false;

        // Reset transform state for the new image.
        _rotate.Angle = 0;
        _translate.X = 0;
        _translate.Y = 0;

        Title = $"{Path.GetFileName(path)} - Image Viewer";

        // Build the sibling list lazily on first navigation request rather
        // than eagerly here - opening a folder with thousands of images
        // shouldn't pay the directory-enumeration cost up front.
        _siblings = new List<string>();
        _siblingIndex = -1;

        SetFitMode(true);
        UpdateUiState();
    }

    // =====================================================================
    // Toolbar / status helpers
    // =====================================================================

    private DOSIButton ToolbarButton(string text, Action onClick)
    {
        var btn = new DOSIButton
        {
            Text = text,
            FontSize = 12,
            Padding = new Thickness(10, 4),
            CornerRadius = new CornerRadius(6),
            Height = 28
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private static Border ToolbarSeparator() => new()
    {
        Width = 1,
        Margin = new Thickness(2, 4),
        Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255))
    };

    private TextBlock StatusText(string text, bool bold = false) => new()
    {
        Text = text,
        FontSize = 11,
        FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
        Foreground = Accents.TextSecondaryBrush,
        VerticalAlignment = VerticalAlignment.Center
    };

    private void UpdateUiState()
    {
        bool hasImage = _bitmap != null;

        _emptyState.IsVisible = !hasImage;
        _btnPrev.IsEnabled        = hasImage;
        _btnNext.IsEnabled        = hasImage;
        _btnFit.IsEnabled         = hasImage;
        _btnActual.IsEnabled      = hasImage;
        _btnZoomIn.IsEnabled      = hasImage;
        _btnZoomOut.IsEnabled     = hasImage;
        _btnRotateLeft.IsEnabled  = hasImage;
        _btnRotateRight.IsEnabled = hasImage;

        if (!hasImage)
        {
            _statusName.Text = string.Empty;
            _statusDimensions.Text = string.Empty;
            _statusZoom.Text = string.Empty;
            _statusSize.Text = string.Empty;
            return;
        }

        _statusName.Text = _currentPath != null ? Path.GetFileName(_currentPath) : string.Empty;
        _statusDimensions.Text = $"{_bitmap!.PixelSize.Width} \u00D7 {_bitmap.PixelSize.Height}";
        _statusZoom.Text = $"{Math.Round(_zoom * 100)}%";

        try
        {
            if (_currentPath != null && File.Exists(_currentPath))
                _statusSize.Text = FormatSize(new FileInfo(_currentPath).Length);
            else
                _statusSize.Text = string.Empty;
        }
        catch { _statusSize.Text = string.Empty; }
    }

    // =====================================================================
    // Zoom / pan / fit
    // =====================================================================

    /// <summary>
    /// Multiplies the current zoom by <paramref name="factor"/> (e.g. 1.25
    /// for zoom-in, 0.8 for zoom-out). Animates smoothly and snaps out of
    /// fit-mode so the user's explicit zoom isn't immediately overridden by
    /// the next size-changed event.
    /// </summary>
    private void ZoomBy(double factor) => SetZoom(_zoom * factor, anchor: null);

    private void SetActualSize()
    {
        _fitMode = false;
        SetZoom(1.0, anchor: null);
        // Centre the image when going to actual size from fit - otherwise it
        // can hang off the corner if the prior zoom + pan left it offset.
        _translate.X = 0;
        _translate.Y = 0;
    }

    private void SetFitMode(bool fit)
    {
        _fitMode = fit;
        if (fit) ApplyFit();
        else UpdateUiState();
    }

    private void ToggleFitActual()
    {
        if (_bitmap == null) return;
        if (_fitMode) SetActualSize();
        else SetFitMode(true);
    }

    private void ApplyFit()
    {
        if (_bitmap == null) return;

        var bounds = _canvasHost.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        // Account for rotation: when the image is rotated 90/270 the
        // effective width/height swap from the layout's perspective.
        bool sideways = ((int)Math.Round(_rotate.Angle / 90)) % 2 != 0;
        var imgW = sideways ? _bitmap.PixelSize.Height : _bitmap.PixelSize.Width;
        var imgH = sideways ? _bitmap.PixelSize.Width  : _bitmap.PixelSize.Height;

        var fit = Math.Min(bounds.Width / imgW, bounds.Height / imgH);

        _translate.X = 0;
        _translate.Y = 0;
        SetZoom(fit, anchor: null, animate: false);
    }

    /// <summary>
    /// Pushes <paramref name="zoom"/> into the image's layout size. Driving
    /// the size through Width/Height (rather than a ScaleTransform) is what
    /// lets Avalonia's HighQuality bitmap interpolator actually run - the
    /// resampling happens during the Image control's draw, with the target
    /// size known up front, instead of as a post-render GPU stretch.
    /// </summary>
    private void ApplyImageSize(double zoom)
    {
        if (_bitmap == null) return;

        // Swap layout dims when rotated 90/270 so the rotated bitmap fits
        // its layout rect exactly (the rotation happens in RenderTransform
        // so the layout rect needs the post-rotation orientation).
        bool sideways = ((int)Math.Round(_rotate.Angle / 90)) % 2 != 0;
        var pxW = sideways ? _bitmap.PixelSize.Height : _bitmap.PixelSize.Width;
        var pxH = sideways ? _bitmap.PixelSize.Width  : _bitmap.PixelSize.Height;

        _imageView.Width  = Math.Max(1, pxW * zoom);
        _imageView.Height = Math.Max(1, pxH * zoom);
    }

    private void SetZoom(double targetZoom, Point? anchor, bool animate = true)
    {
        targetZoom = Math.Clamp(targetZoom, MinZoom, MaxZoom);
        if (Math.Abs(targetZoom - _zoom) < 0.0001) { ApplyImageSize(targetZoom); UpdateUiState(); return; }

        // If the user explicitly zoomed (anchor != null OR via toolbar/keys),
        // they're no longer in passive fit-mode. ApplyFit clears this flag
        // through its caller chain.
        if (anchor != null) _fitMode = false;

        // Anchored zoom: keep the world-space point under the cursor stationary
        // by adjusting the translate to compensate for the size change.
        if (anchor is { } cursor)
        {
            var cx = _canvasHost.Bounds.Width / 2;
            var cy = _canvasHost.Bounds.Height / 2;

            // Convert cursor from canvas coords to image-centre-relative
            // coords. The image is centered in the host, then offset by
            // _translate, so its centre is at (cx + tx, cy + ty).
            var dx = cursor.X - cx - _translate.X;
            var dy = cursor.Y - cy - _translate.Y;

            var ratio = targetZoom / _zoom;
            _translate.X -= dx * (ratio - 1);
            _translate.Y -= dy * (ratio - 1);
        }

        if (!animate)
        {
            _zoom = targetZoom;
            ApplyImageSize(targetZoom);
            UpdateUiState();
            return;
        }

        var startZoom = _zoom;
        _zoom = targetZoom;

        _zoomTween?.Stop();
        _zoomTween = Tween.Run(140, Easings.EaseOutCubic,
            apply: t =>
            {
                var z = startZoom + (targetZoom - startZoom) * t;
                ApplyImageSize(z);
            },
            onCompleted: () =>
            {
                ApplyImageSize(targetZoom);
                _zoomTween = null;
                UpdateUiState();
            });

        UpdateUiState();
    }

    private void RotateBy(double degrees)
    {
        if (_bitmap == null) return;
        _rotate.Angle = ((_rotate.Angle + degrees) % 360 + 360) % 360;
        // Re-fit so the rotated image is sized correctly to the canvas.
        if (_fitMode) ApplyFit();
        else ApplyImageSize(_zoom);
    }

    // =====================================================================
    // Pointer / keyboard input
    // =====================================================================

    private void OnCanvasWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_bitmap == null) return;
        var pos = e.GetPosition(_canvasHost);
        // Each wheel notch (delta 1) is ~1.15x. Negative direction zooms out.
        var factor = Math.Pow(1.15, e.Delta.Y);
        SetZoom(_zoom * factor, anchor: pos);
        e.Handled = true;
    }

    private void OnCanvasPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_bitmap == null) return;
        var props = e.GetCurrentPoint(_canvasHost).Properties;
        if (!props.IsLeftButtonPressed) return;

        _isPanning = true;
        _panStart = e.GetPosition(_canvasHost);
        _panStartTx = _translate.X;
        _panStartTy = _translate.Y;
        _canvasHost.Cursor = new Cursor(StandardCursorType.SizeAll);
        e.Pointer.Capture(_canvasHost);
    }

    private void OnCanvasMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning) return;
        var p = e.GetPosition(_canvasHost);
        _translate.X = _panStartTx + (p.X - _panStart.X);
        _translate.Y = _panStartTy + (p.Y - _panStart.Y);
    }

    private void OnCanvasReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isPanning) return;
        _isPanning = false;
        _canvasHost.Cursor = new Cursor(StandardCursorType.Arrow);
        e.Pointer.Capture(null);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:    GoPrevious();        e.Handled = true; break;
            case Key.Right:   GoNext();            e.Handled = true; break;
            case Key.Add:
            case Key.OemPlus: ZoomBy(1.25);        e.Handled = true; break;
            case Key.Subtract:
            case Key.OemMinus:ZoomBy(1 / 1.25);    e.Handled = true; break;
            case Key.D0:
            case Key.NumPad0: SetFitMode(true);    e.Handled = true; break;
            case Key.D1:
            case Key.NumPad1: SetActualSize();     e.Handled = true; break;
            case Key.F:       SetFitMode(true);    e.Handled = true; break;
            case Key.R:
                RotateBy(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -90 : 90);
                e.Handled = true; break;
            case Key.O:
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) { OpenFromPicker(); e.Handled = true; }
                break;
            case Key.Escape:
                if (!_fitMode) { SetFitMode(true); e.Handled = true; }
                break;
        }
    }

    // =====================================================================
    // Open / sibling navigation
    // =====================================================================

    private void OpenFromPicker()
    {
        var picker = new DOSIFileExplorer();
        picker.EnablePickerMode("Choose an image", SupportedExtensions, path =>
        {
            // EnablePickerMode closes its own window; just load the result.
            LoadImage(path);
        });
        WindowManager.Instance?.OpenWindow(picker);
    }

    private void EnsureSiblings()
    {
        if (_siblings.Count > 0 || _currentPath == null) return;
        try
        {
            var dir = Path.GetDirectoryName(_currentPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            _siblings = Directory.EnumerateFiles(dir)
                .Where(p => SupportedExtensions.Contains(
                    Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _siblingIndex = _siblings.FindIndex(p =>
                string.Equals(p, _currentPath, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            _siblings = new List<string>();
            _siblingIndex = -1;
        }
    }

    private void GoPrevious()
    {
        EnsureSiblings();
        if (_siblings.Count == 0 || _siblingIndex < 0) return;
        _siblingIndex = (_siblingIndex - 1 + _siblings.Count) % _siblings.Count;
        LoadImage(_siblings[_siblingIndex]);
    }

    private void GoNext()
    {
        EnsureSiblings();
        if (_siblings.Count == 0 || _siblingIndex < 0) return;
        _siblingIndex = (_siblingIndex + 1) % _siblings.Count;
        LoadImage(_siblings[_siblingIndex]);
    }

    // =====================================================================
    // Theming
    // =====================================================================

    private void OnAccentChanged(object? sender, EventArgs e)
    {
        _canvasBackdrop.Background = BuildCanvasBackdrop();
        _emptyState.Foreground = Accents.TextSecondaryBrush;
        _statusName.Foreground = Accents.TextSecondaryBrush;
        _statusDimensions.Foreground = Accents.TextSecondaryBrush;
        _statusZoom.Foreground = Accents.TextSecondaryBrush;
        _statusSize.Foreground = Accents.TextSecondaryBrush;
    }

    private static IBrush BuildCanvasBackdrop() => new RadialGradientBrush
    {
        Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
        GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
        RadiusX = new RelativeScalar(0.75, RelativeUnit.Relative),
        RadiusY = new RelativeScalar(0.75, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(255, 26, 26, 30), 0),
            new GradientStop(Color.FromArgb(255, 14, 14, 18), 1)
        }
    };

    // =====================================================================
    // Helpers
    // =====================================================================

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double v = bytes;
        string[] units = { "KB", "MB", "GB", "TB" };
        foreach (var u in units)
        {
            v /= 1024;
            if (v < 1024) return $"{v:0.##} {u}";
        }
        return $"{v:0.##} PB";
    }

    private static Control CreateIcon()
    {
        // Minimal "mountain + sun" pictogram drawn with shapes - keeps the
        // app self-contained (no PNG dependency) and follows the same style
        // as DOSITerminal's vector icon.
        var border = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.FromRgb(40, 80, 140)),
            ClipToBounds = true
        };
        var canvas = new Canvas { Width = 16, Height = 16 };
        // Sun
        canvas.Children.Add(new Ellipse
        {
            Width = 4, Height = 4,
            Fill = new SolidColorBrush(Color.FromRgb(255, 220, 110)),
            [Canvas.LeftProperty] = 10d, [Canvas.TopProperty] = 2d
        });
        // Mountain
        canvas.Children.Add(new Polygon
        {
            Points = new Avalonia.Collections.AvaloniaList<Point>
            {
                new(0, 14), new(6, 7), new(10, 11), new(14, 5), new(16, 7), new(16, 16), new(0, 16)
            },
            Fill = new SolidColorBrush(Color.FromRgb(220, 230, 240))
        });
        border.Child = canvas;
        return border;
    }
}
