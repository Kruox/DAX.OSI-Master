using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DOSI.CORE;
using DOSI.CORE.AccentManagement;
using DOSI.CORE.Animations;
using DOSI.CORE.UIComponents;
using DOSI.CORE.UserManagement;

namespace DAX.OSI.UI;

/// <summary>
/// Elegant full-screen "signing out" experience shown when a user signs out.
/// Mirrors a real OS sign-out: a soft accent-tinted backdrop with the user's
/// avatar, a friendly farewell line, and a spinner. The screen fades in,
/// holds, and then fades out so the host can crossfade back to the login
/// screen seamlessly.
/// </summary>
public class SignoutScreen : DOSIScreen, IDisposable
{
    public override string ScreenId => "signout";
    public override string ScreenName => "Sign out";

    private static AccentManager Accents => AccentManager.Instance;

    private readonly Grid _root;
    private readonly StackPanel _hero;
    private readonly Ellipse _avatarRing;
    private readonly TextBlock _avatarInitial;
    private readonly TextBlock _farewell;
    private readonly TextBlock _status;
    private readonly DOSILoadingAnim _spinner;

    private DispatcherTimer? _activeTimer;
    private bool _isDisposed;

    public SignoutScreen(DOSIUser? user)
    {
        // Don't override Desktop.Background - the DOSIScreen base already paints
        // the user's wallpaper (or accent vignette) behind the Desktop canvas.
        // We just blur it on entry so the sign-out UI stays readable.

        var displayName = user?.DisplayName
                          ?? user?.Username
                          ?? "User";
        var initial = string.IsNullOrWhiteSpace(displayName)
            ? "?"
            : char.ToUpperInvariant(displayName.Trim()[0]).ToString();

        _avatarRing = new Ellipse
        {
            Width = 88,
            Height = 88,
            Fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Accents.AccentPrimary, 0),
                    new GradientStop(Accents.AccentSecondary, 1)
                }
            },
            Stroke = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
            StrokeThickness = 1.5,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        _avatarInitial = new TextBlock
        {
            Text = initial,
            FontFamily = DOSIFonts.UI,
            FontSize = 40,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Accents.TextOnAccent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var avatar = new Grid
        {
            Width = 88,
            Height = 88,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { _avatarRing, _avatarInitial }
        };

        _farewell = new TextBlock
        {
            Text = $"See you soon, {displayName}",
            FontFamily = DOSIFonts.UI,
            FontSize = 26,
            FontWeight = FontWeight.Light,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 26, 0, 6),
            LetterSpacing = 1
        };

        _status = new TextBlock
        {
            Text = "Signing out...",
            FontFamily = DOSIFonts.UI,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center,
            LetterSpacing = 2
        };

        _spinner = new DOSILoadingAnim(LoadingSize.Small)
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 22, 0, 0)
        };

        _hero = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
            Children = { avatar, _farewell, _status, _spinner }
        };

        _root = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Children = { _hero }
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
    /// Plays the full sign-out sequence: fade in, hold so the user can read
    /// the farewell, then fade out so the host can crossfade to login.
    /// </summary>
    public async Task RunAsync()
    {
        // Phase 1: fade in the avatar / farewell / spinner (~600ms).
        await AnimateAsync(600, t => _hero.Opacity = EaseOutCubic(t));

        // Phase 2: hold so the user can read the farewell (~900ms).
        await Task.Delay(900);

        // Phase 3: gentle fade out (~450ms) before the host crossfades to login.
        await AnimateAsync(450, t => _hero.Opacity = 1 - EaseInOutCubic(t));
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
            catch { /* never let an animation glitch block sign-out */ }

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

