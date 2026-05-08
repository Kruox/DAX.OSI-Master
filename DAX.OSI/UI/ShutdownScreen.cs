using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DOSI.CORE;
using DOSI.CORE.AccentManagement;
using DOSI.CORE.Animations;
using DOSI.CORE.UIComponents;

namespace DAX.OSI.UI;

/// <summary>
/// Full-screen "powering off" experience shown right before DAX.OSI exits.
/// Crossfades through a few phases:
///   1. Accent gradient with the DOSI logo, spinner, and status text.
///   2. Fades the entire stage to pure black.
///   3. Reveals a minimal white "Powering off..." line on black, then resolves.
/// </summary>
public class ShutdownScreen : DOSIScreen, IDisposable
{
    public override string ScreenId => "shutdown";
    public override string ScreenName => "Shutdown";

    private static AccentManager Accents => AccentManager.Instance;

    private readonly Grid _root;
    private readonly Border _stage;
    private readonly StackPanel _hero;
    private readonly TextBlock _brand;
    private readonly TextBlock _status;
    private readonly DOSILoadingAnim _spinner;
    private readonly Border _blackOverlay;
    private readonly TextBlock _finalText;

    private DispatcherTimer? _activeTimer;
    private bool _isDisposed;

    public ShutdownScreen()
    {
        // Don't override Desktop.Background - the DOSIScreen base already paints
        // the current wallpaper (or accent vignette) behind the Desktop canvas.
        // We blur it on entry so the shutdown UI stays readable on top.

        _brand = new TextBlock
        {
            Text = "DAX.OSI",
            FontFamily = DOSIFonts.UI,
            FontSize = 56,
            FontWeight = FontWeight.Light,
            Foreground = Brushes.White,
            Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Center,
            LetterSpacing = 6
        };

        _spinner = new DOSILoadingAnim(LoadingSize.Medium)
        {
            Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 28, 0, 18)
        };

        _status = new TextBlock
        {
            Text = "Shutting down...",
            FontFamily = DOSIFonts.UI,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
            Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Center,
            LetterSpacing = 2
        };

        _hero = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _brand, _spinner, _status }
        };

        _stage = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.Transparent,
            Child = _hero
        };

        _blackOverlay = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.Black,
            Opacity = 0,
            IsHitTestVisible = false
        };

        _finalText = new TextBlock
        {
            Text = "Powering off...",
            FontFamily = DOSIFonts.UI,
            FontSize = 14,
            FontWeight = FontWeight.Normal,
            Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
            Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _root = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Children = { _stage, _blackOverlay, _finalText }
        };

        Desktop.Children.Add(_root);
        Desktop.LayoutUpdated += OnDesktopLayoutUpdated;
    }

    private void OnDesktopLayoutUpdated(object? sender, EventArgs e)
    {
        _root.Width = Desktop.Bounds.Width;
        _root.Height = Desktop.Bounds.Height;
    }

    public override void OnNavigatedTo()
    {
        base.OnNavigatedTo();
        NotifyScreenReady();
    }

    /// <summary>
    /// Plays the full shutdown sequence and resolves once the final
    /// "Powering off..." moment has been on screen long enough to read.
    /// </summary>
    public async Task RunAsync()
    {
        // Phase 1: fade in the brand, spinner and status (~700ms).
        await AnimateAsync(700, t =>
        {
            var eased = EaseOutCubic(t);
            _brand.Opacity = eased;
            _spinner.Opacity = eased;
            _status.Opacity = eased;
        });

        // Phase 2: hold so the user can read it (~1.2s).
        await Task.Delay(1200);

        // Phase 3: fade the entire colored stage out to black (~900ms).
        await AnimateAsync(900, t =>
        {
            var eased = EaseInOutCubic(t);
            _blackOverlay.Opacity = eased;
            _hero.Opacity = 1 - eased;
        });

        // Phase 4: reveal the final white text on black (~500ms).
        await AnimateAsync(500, t => _finalText.Opacity = EaseOutCubic(t));

        // Phase 5: hold the final frame briefly (~700ms) before exit.
        await Task.Delay(700);
    }

    private Task AnimateAsync(double durationMs, Action<double> onProgress)
    {
        var tcs = new TaskCompletionSource<bool>();
        var startTime = DateTime.UtcNow;

        _activeTimer?.Stop();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _activeTimer = timer;

        timer.Tick += (_, _) =>
        {
            if (_isDisposed)
            {
                timer.Stop();
                tcs.TrySetResult(false);
                return;
            }

            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            var t = Math.Clamp(elapsed / Math.Max(1, durationMs), 0d, 1d);

            try { onProgress(t); }
            catch { /* never let an animation glitch block shutdown */ }

            if (t >= 1d)
            {
                timer.Stop();
                if (ReferenceEquals(_activeTimer, timer))
                    _activeTimer = null;
                tcs.TrySetResult(true);
            }
        };

        timer.Start();
        return tcs.Task;
    }

    private static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);

    private static double EaseInOutCubic(double t) =>
        t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Desktop.LayoutUpdated -= OnDesktopLayoutUpdated;

        _activeTimer?.Stop();
        _activeTimer = null;

        _spinner.Dispose();
        _hero.Children.Clear();
        _root.Children.Clear();
        Desktop.Children.Clear();

        GC.SuppressFinalize(this);
    }
}
