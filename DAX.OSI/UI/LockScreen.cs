using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DOSI.CORE.AccentManagement;
using DOSI.CORE.Security;
using DOSI.CORE.UIComponents;
using DOSI.CORE.UserManagement;

namespace DAX.OSI.UI;

/// <summary>
/// Full-window opaque overlay that obscures the entire desktop until the
/// signed-in user re-enters their password. Used by
/// <see cref="DOSI.CORE.Security.SessionLockManager"/> after the configured
/// idle timeout. Distinct from <see cref="LoginScreen"/>: there is no user
/// picker - only the currently signed-in user can unlock.
/// </summary>
public sealed class LockScreen : Border, IDOSILockScreen
{
    private static AccentManager Accents => AccentManager.Instance;

    private readonly DOSIUser _user;
    private readonly DOSITextBox _passwordBox;
    private readonly DOSIButton _unlockButton;
    private readonly DOSIButton _signOutButton;
    private readonly TextBlock _statusText;
    private readonly DispatcherTimer _clockTimer;
    private readonly TextBlock _clockText;
    private readonly TextBlock _dateText;

    /// <summary>Raised once the user has unlocked successfully.</summary>
    public event EventHandler? Unlocked;

    /// <summary>Raised when the user clicks "Sign out" instead of unlocking.</summary>
    public event EventHandler? SignOutRequested;

    public LockScreen(DOSIUser user)
    {
        _user = user ?? throw new ArgumentNullException(nameof(user));

        Background = BuildBackgroundBrush();
        IsHitTestVisible = true;
        Focusable = true;

        // ----- Avatar -----
        var avatarCircle = new Ellipse
        {
            Width = 96,
            Height = 96,
            Fill = new SolidColorBrush(Accents.AccentPrimary),
            Stroke = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            StrokeThickness = 1.5,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var initial = string.IsNullOrWhiteSpace(_user.DisplayName)
            ? (_user.Username.Length > 0 ? _user.Username[..1] : "?")
            : _user.DisplayName[..1];

        var avatarInitial = new TextBlock
        {
            Text = initial.ToUpperInvariant(),
            FontSize = 44,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Accents.TextOnAccent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var avatarGrid = new Grid
        {
            Width = 96,
            Height = 96,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { avatarCircle, avatarInitial }
        };

        // ----- Display name + lock label -----
        var displayName = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(_user.DisplayName) ? _user.Username : _user.DisplayName,
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 2)
        };

        var lockedLabel = new TextBlock
        {
            Text = "Session locked",
            FontSize = 12,
            Foreground = Brushes.White,
            Opacity = 0.7,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 18)
        };

        // ----- Password + buttons -----
        _passwordBox = new DOSITextBox
        {
            Width = 280,
            HorizontalAlignment = HorizontalAlignment.Center,
            UsePasswordChar = true,
            PlaceholderText = "Password"
        };
        _passwordBox.KeyDown += OnPasswordKeyDown;

        _unlockButton = new DOSIButton
        {
            Text = "Unlock",
            Width = 140,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0)
        };
        _unlockButton.Click += (_, _) => TryUnlock();

        _signOutButton = new DOSIButton
        {
            Text = "Sign out",
            Width = 140,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0)
        };
        _signOutButton.Click += (_, _) => SignOutRequested?.Invoke(this, EventArgs.Empty);

        _statusText = new TextBlock
        {
            Text = "",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0),
            Opacity = 0
        };

        // ----- Card -----
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(180, 22, 28, 60)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(36, 32),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 380,
            Child = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Children =
                {
                    avatarGrid, displayName, lockedLabel,
                    _passwordBox, _unlockButton, _signOutButton, _statusText
                }
            }
        };

        // ----- Clock corner -----
        _clockText = new TextBlock
        {
            FontSize = 42,
            FontWeight = FontWeight.Light,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        _dateText = new TextBlock
        {
            FontSize = 14,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Opacity = 0.85
        };
        var clockStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(36, 36, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Spacing = 2,
            Children = { _clockText, _dateText }
        };

        Child = new Grid
        {
            Children = { clockStack, card }
        };

        UpdateClock();
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();

        // Auto-focus the password box once the visual tree is attached.
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => _passwordBox.Focus());

        DetachedFromVisualTree += (_, _) => _clockTimer.Stop();
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        _clockText.Text = now.ToString("h:mm tt");
        _dateText.Text = now.ToString("dddd, MMMM d");
    }

    private static IBrush BuildBackgroundBrush() =>
        new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(245, 8, 12, 36), 0),
                new GradientStop(Color.FromArgb(245, 18, 22, 56), 1)
            }
        };

    private void OnPasswordKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; TryUnlock(); }
    }

    private void TryUnlock()
    {
        var pwd = _passwordBox.Text ?? string.Empty;
        if (pwd.Length == 0)
        {
            ShowStatus("Enter your password.");
            return;
        }

        // Lockout check first - re-uses the same throttle as LoginScreen.
        var locked = UserManager.GetLockoutSecondsRemaining(_user.Username);
        if (locked > 0)
        {
            ShowStatus($"Too many attempts. Try again in {locked}s.");
            return;
        }

        // ValidatePassword does NOT touch CurrentUser, but it DOES go through
        // the same fail-counter path indirectly when called via Authenticate.
        // We use Authenticate here so the lockout machinery applies.
        var prev = UserManager.CurrentUser;
        var result = UserManager.Authenticate(_user.Username, pwd);
        if (result == null)
        {
            // Authenticate cleared CurrentUser only on success; on failure it
            // never touched it. We're safe.
            var remaining = UserManager.GetLockoutSecondsRemaining(_user.Username);
            ShowStatus(remaining > 0
                ? $"Locked out. Try again in {remaining}s."
                : "Incorrect password.");
            _passwordBox.Text = string.Empty;
            _passwordBox.Focus();
            return;
        }

        // Success - audit + raise.
        SecurityAuditLog.AppendForUser(_user.Username, SecurityAuditEventType.SessionUnlocked, null);
        Unlocked?.Invoke(this, EventArgs.Empty);
    }

    private void ShowStatus(string text)
    {
        _statusText.Text = text;
        _statusText.Opacity = 1;
        _ = FadeOutStatusLater();
    }

    private async Task FadeOutStatusLater()
    {
        await Task.Delay(3500);
        if (!string.IsNullOrEmpty(_statusText.Text)) _statusText.Opacity = 0.55;
    }
}
