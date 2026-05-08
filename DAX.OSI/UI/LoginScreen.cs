using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DOSI.CORE;
using DOSI.CORE.AccentManagement;
using DOSI.CORE.Animations;
using DOSI.CORE.UIComponents;
using DOSI.CORE.UserManagement;
using DOSI.CORE.WallpaperManagement;

namespace DAX.OSI.UI;

/// <summary>
/// The login screen displayed after the boot sequence completes. Shows a tile
/// picker of all known users; selecting one previews their saved color accent,
/// deselecting reverts to the system default accent.
/// </summary>
public class LoginScreen : DOSIScreen
{
    public override string ScreenId => "login";
    public override string ScreenName => "Login";

    /// <summary>Raised once the celebration animation has finished after a successful sign-in.</summary>
    public event EventHandler<DOSIUser>? SignInCompleted;

    private static AccentManager Accents => AccentManager.Instance;

    // ----- Layout / chrome -----
    private readonly Grid _layoutRoot;
    private readonly Border _card;
    private readonly TextBlock _clockText;
    private readonly TextBlock _dateText;
    private readonly TextBlock _versionText;

    // ----- Picker mode -----
    private readonly StackPanel _pickerPanel;
    private readonly WrapPanel _userTilesWrap;
    private Border? _pickerDivider;

    // ----- Sign-in mode -----
    private readonly StackPanel _signInPanel;
    private readonly Grid _avatarGrid;
    private readonly ScaleTransform _avatarScale;
    private readonly Ellipse _avatarCircle;
    private readonly Ellipse _avatarGlow;
    private readonly TextBlock _avatarInitial;
    private readonly TextBlock _selectedDisplayName;
    private readonly TextBlock _selectedUsername;
    private readonly Border _signInDivider;
    private readonly DOSITextBox _passwordBox;
    private readonly DOSIButton _signInButton;
    private readonly DOSIButton _switchUserButton;
    private readonly TextBlock _statusText;

    // ----- State -----
    private readonly DispatcherTimer _clockTimer;
    private Tween? _entranceTween;
    private Tween? _avatarAnimTween;
    private Tween? _panelFadeTween;
    private Control? _panelFadeFrom;
    private Control? _panelFadeTo;
    private DOSIUser? _selectedUser;
    private DOSIAccent _systemDefaultAccent;

    public LoginScreen()
    {
        // ===== Ambient overlay (clock + date) =====
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
            Margin = new Thickness(36, 0, 0, 28),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Spacing = 2,
            Children = { _clockText, _dateText }
        };

        // ===== Picker mode =====
        var pickerTitle = new TextBlock
        {
            Text = "Choose your account",
            FontSize = 24,
            FontWeight = FontWeight.SemiBold,
            // Login text floats directly over the wallpaper, so it's always
            // pinned white (the accent's TextPrimary goes dark under the
            // Light accent and would disappear on dark wallpapers).
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var pickerSubtitle = new TextBlock
        {
            Text = "Select a profile to sign in",
            FontSize = 13,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0.85,
            Margin = new Thickness(0, 4, 0, 22)
        };

        _userTilesWrap = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            ItemWidth = 120,
            ItemHeight = 150
        };

        // Thin accent divider that sits between the subtitle and the tiles to
        // visually anchor the picker header instead of letting the tiles float.
        var pickerDivider = new Border
        {
            Height = 1,
            Width = 64,
            Margin = new Thickness(0, 0, 0, 18),
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 0),
                    new GradientStop(Color.FromArgb(120, Accents.AccentPrimary.R,
                                                        Accents.AccentPrimary.G,
                                                        Accents.AccentPrimary.B), 0.5),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 1)
                }
            }
        };
        _pickerDivider = pickerDivider;

        _pickerPanel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { pickerTitle, pickerSubtitle, pickerDivider, _userTilesWrap }
        };

        // ===== Sign-in mode =====
        // Soft accent glow that sits behind the avatar so the circle reads as
        // a focal point instead of a flat disc on the card.
        _avatarGlow = new Ellipse
        {
            Width = 132,
            Height = 132,
            Fill = BuildAvatarGlowBrush(),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };

        _avatarCircle = new Ellipse
        {
            Width = 96,
            Height = 96,
            Fill = BuildAvatarBrush(),
            Stroke = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            StrokeThickness = 1.5,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        _avatarInitial = new TextBlock
        {
            Text = "?",
            FontSize = 44,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Accents.TextOnAccent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var avatarGrid = new Grid
        {
            Width = 132,
            Height = 132,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { _avatarGlow, _avatarCircle, _avatarInitial }
        };
        _avatarGrid = avatarGrid;
        _avatarScale = new ScaleTransform(1, 1);
        _avatarGrid.RenderTransform = _avatarScale;
        _avatarGrid.RenderTransformOrigin = RelativePoint.Center;

        _selectedDisplayName = new TextBlock
        {
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
            // Pinned white for wallpaper legibility - same rule as the picker title.
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };

        _selectedUsername = new TextBlock
        {
            FontSize = 12,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Opacity = 0.85
        };

        _signInDivider = new Border
        {
            Height = 1,
            Width = 220,
            Margin = new Thickness(0, 14, 0, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = BuildDividerBrush()
        };

        _passwordBox = new DOSITextBox
        {
            PlaceholderText = "Password",
            FontSize = 14,
            Padding = new Thickness(14, 10),
            CornerRadius = new CornerRadius(8),
            Height = 40,
            Width = 300,
            UsePasswordChar = true
        };
        _passwordBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) OnSignInClicked(this, new Avalonia.Interactivity.RoutedEventArgs());
        };

        _signInButton = new DOSIButton
        {
            Text = "Sign In",
            FontSize = 14,
            Width = 300,
            Height = 42,
            CornerRadius = new CornerRadius(8)
        };
        _signInButton.Click += OnSignInClicked;

        _switchUserButton = new DOSIButton
        {
            Text = "Switch user",
            FontSize = 12,
            Width = 300,
            Height = 32,
            CornerRadius = new CornerRadius(8)
        };
        _switchUserButton.Click += (_, _) => DeselectUser();

        _statusText = new TextBlock
        {
            FontSize = 12,
            // The status line lives on top of the wallpaper, not on a chrome
            // surface, so we pin it to white. TextSecondary becomes a dark
            // gray under the Light accent and would disappear against a
            // dark wallpaper.
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Opacity = 0.85,
            Height = 16
        };

        _signInPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            // Both panels stay permanently in layout so the cardContent Grid
            // is always sized for the larger (sign-in) panel. That keeps the
            // picker pinned at the same Y coordinate before, during, and
            // after the cross-fade - otherwise hiding the sign-in panel
            // would shrink the card and the centered card would slide down,
            // which reads as the picker "animating downward".
            Opacity = 0,
            IsHitTestVisible = false,
            Children =
            {
                avatarGrid,
                _selectedDisplayName,
                _selectedUsername,
                _signInDivider,
                _passwordBox,
                _signInButton,
                _statusText,
                _switchUserButton
            }
        };

        // ===== Card =====
        // No background panel - content floats directly on the desktop so the
        // login screen feels lighter. _card stays as a transparent host purely
        // so the entrance animation and existing field references keep working.
        var cardContent = new Grid
        {
            Margin = new Thickness(36),
            Children = { _pickerPanel, _signInPanel }
        };

        _card = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            Width = 460,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = cardContent
        };

        // ===== Footer =====
        _versionText = new TextBlock
        {
            Text = "DAX.OSI  v1.0",
            FontSize = 11,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 24, 16)
        };

        _layoutRoot = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Children = { clockStack, _card, _versionText }
        };

        Desktop.Children.Add(_layoutRoot);
        Desktop.LayoutUpdated += (_, _) =>
        {
            _layoutRoot.Width = Desktop.Bounds.Width;
            _layoutRoot.Height = Desktop.Bounds.Height;
        };

        // ===== Clock =====
        UpdateClock();
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();

        AttachedToVisualTree += (_, _) =>
        {
            Accents.AccentChanged += OnAccentChanged;
            UserManager.UserCreated += OnUserCreated;
            _clockTimer.Start();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            Accents.AccentChanged -= OnAccentChanged;
            UserManager.UserCreated -= OnUserCreated;
            _clockTimer.Stop();
            // Snap any in-flight tweens to their final state. Critical for
            // the brief screen-manager reparent that happens at the end of
            // NavigateToWithCrossfadeAsync - without snap-to-end the panels /
            // avatar would freeze mid-animation when the reparent fires
            // DetachedFromVisualTree.
            _entranceTween?.Stop(snapToEnd: true);
            _entranceTween = null;
            _avatarAnimTween?.Stop(snapToEnd: true);
            _avatarAnimTween = null;
            _panelFadeTween?.Stop(snapToEnd: true);
            _panelFadeTween = null;
            _panelFadeFrom = null;
            _panelFadeTo = null;
        };
    }

    private void OnUserCreated(object? sender, DOSIUser newUser)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Only refresh while the picker is visible; when in sign-in mode the
            // tile list isn't being shown so we'll just rebuild on next picker entry.
            RebuildUserTiles();

            if (_pickerPanel.IsVisible)
            {
                AnimateTileFadeIn(newUser.Username);
            }
        });
    }

    private void AnimateTileFadeIn(string username)
    {
        Control? newTile = null;
        foreach (var child in _userTilesWrap.Children)
        {
            if (child is Control c && c.Tag is string tag && tag == username)
            {
                newTile = c;
                break;
            }
        }
        if (newTile == null) return;

        const double startOffset = 14;

        // Preserve the tile's existing RenderTransform (the hover ScaleTransform set
        // up in BuildUserTile). Compose it with a temporary TranslateTransform so the
        // slide-in doesn't clobber the hover/leave grow/shrink animation afterwards.
        var existing = newTile.RenderTransform as Transform;
        var translate = new TranslateTransform(0, startOffset);
        var group = new TransformGroup();
        if (existing != null) group.Children.Add(existing);
        group.Children.Add(translate);
        newTile.RenderTransform = group;
        newTile.Opacity = 0;

        Tween.Run(480, Easings.EaseOutCubic,
            apply: t =>
            {
                newTile.Opacity = t;
                translate.Y = startOffset * (1 - t);
            },
            onCompleted: () =>
            {
                // Restore the original transform so hover scaling keeps working.
                newTile.RenderTransform = existing;
            });
    }

    public override void OnNavigatedTo()
    {
        base.OnNavigatedTo();

        // Capture the system default accent so we can revert to it on deselect.
        _systemDefaultAccent = SystemCore.Settings.DefaultAccent;

        RebuildUserTiles();
        ShowPickerMode();
        PlayEntranceAnimation();

        // If we just arrived from a flow that previewed a different accent (e.g. the
        // setup wizard), animate back to the real system default so picker mode
        // always begins on the system accent.
        if (Accents.CurrentAccent != _systemDefaultAccent)
        {
            Accents.ApplyAccentAnimated(_systemDefaultAccent, TimeSpan.FromMilliseconds(550));
        }

        // Picker mode never shows a user-specific wallpaper - the system
        // accent vignette is the canonical "no one signed in" backdrop.
        // DOSIScreen handles the cross-fade for us.
        WallpaperManager.Instance.SetWallpaper(WallpaperManager.AccentOnlyKey);

        NotifyScreenReady();
    }

    // =====================================================================
    // User tiles
    // =====================================================================

    private void RebuildUserTiles()
    {
        _userTilesWrap.Children.Clear();

        var users = UserManager.GetAllUsers();
        if (users.Count == 0)
        {
            _userTilesWrap.Children.Add(new TextBlock
            {
                Text = "No accounts found.",
                FontSize = 13,
                Foreground = Accents.TextSecondaryBrush,
                Opacity = 0.85,
                Width = 320,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 12, 0, 0)
            });
            return;
        }

        foreach (var user in users)
            _userTilesWrap.Children.Add(BuildUserTile(user));
    }

    private Control BuildUserTile(DOSIUser user)
    {
        var (primary, secondary) = GetUserAccentColors(user);

        var ring = new Ellipse
        {
            Width = 64,
            Height = 64,
            Fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(primary, 0),
                    new GradientStop(secondary, 1)
                }
            },
            Stroke = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            StrokeThickness = 1.5,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var initial = new TextBlock
        {
            Text = GetInitial(user.DisplayName),
            FontSize = 28,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var avatar = new Grid
        {
            Width = 64,
            Height = 64,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { ring, initial }
        };

        var name = new TextBlock
        {
            Text = user.DisplayName,
            FontSize = 12,
            // Pinned to white because user tiles sit directly on the wallpaper,
            // not on a chrome surface. Using Accents.TextPrimaryBrush would
            // bind the label to whatever accent was active at build time -
            // signing out from a user with a Light accent would leave the
            // tile labels rendered as near-black text on the system's dark
            // login backdrop and they'd disappear. White matches the clock
            // and date labels which follow the same "lives on the wallpaper"
            // rule (see OnAccentChanged for the matching comment).
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 100,
            Margin = new Thickness(0, 8, 0, 0)
        };

        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { avatar, name }
        };

        // Wrap in a rounded "tile" border so hovering reveals a soft glass
        // highlight instead of the bare avatar floating on the card.
        var tile = new Border
        {
            Width = 108,
            Height = 130,
            Padding = new Thickness(6, 12),
            Margin = new Thickness(6),
            CornerRadius = new CornerRadius(14),
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = user.Username,
            Child = stack
        };

        // Smoothly animate scale on hover for a tactile pop effect.
        var scale = new ScaleTransform(1, 1);
        tile.RenderTransform = scale;
        tile.RenderTransformOrigin = RelativePoint.Center;

        Tween? hoverTween = null;
        void AnimateScaleTo(double target)
        {
            var startX = scale.ScaleX;
            var startY = scale.ScaleY;

            hoverTween?.Stop();
            hoverTween = Tween.Run(160, Easings.EaseOutCubic, t =>
            {
                scale.ScaleX = startX + (target - startX) * t;
                scale.ScaleY = startY + (target - startY) * t;
            });
        }

        tile.PointerEntered += (_, _) =>
        {
            AnimateScaleTo(1.06);
            tile.Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
            tile.BorderBrush = new SolidColorBrush(
                Color.FromArgb(80, primary.R, primary.G, primary.B));
        };
        tile.PointerExited += (_, _) =>
        {
            AnimateScaleTo(1.0);
            tile.Background = Brushes.Transparent;
            tile.BorderBrush = new SolidColorBrush(Color.FromArgb(0, 255, 255, 255));
        };
        tile.PointerPressed += (_, _) => SelectUser(user);
        return tile;
    }

    private static (Color Primary, Color Secondary) GetUserAccentColors(DOSIUser user)
    {
        var accent = UserManager.GetUserAccent(user) ?? SystemCore.Settings.DefaultAccent;
        return InitialStartup_AccentColors.Get(accent);
    }

    private static string GetInitial(string text)
    {
        var t = (text ?? string.Empty).Trim();
        return t.Length == 0 ? "?" : char.ToUpperInvariant(t[0]).ToString();
    }

    // =====================================================================
    // Mode switching
    // =====================================================================

    private void ShowPickerMode()
    {
        _selectedUser = null;
        // Both panels stay in the layout permanently - flip opacity + hit-test
        // instead of IsVisible so the card never resizes (see _signInPanel ctor).
        _pickerPanel.Opacity = 1;
        _pickerPanel.IsHitTestVisible = true;
        _signInPanel.Opacity = 0;
        _signInPanel.IsHitTestVisible = false;
        // Restore controls in case they were hidden by a prior sign-in attempt.
        _passwordBox.IsVisible = true;
        _signInButton.IsVisible = true;
        _switchUserButton.IsVisible = true;
        _statusText.Text = string.Empty;
        _passwordBox.Text = string.Empty;
    }

    private void SelectUser(DOSIUser user)
    {
        // If the user clicked a tile before the entrance animation finished,
        // snap the card to its end state. Otherwise the card's still-animating
        // opacity multiplies the panel cross-fade and the sign-in panel only
        // becomes visible once the card finishes - which reads as an instant
        // swap.
        FinishEntranceAnimation();

        _selectedUser = user;

        _avatarInitial.Text = GetInitial(user.DisplayName);
        _selectedDisplayName.Text = $"Welcome back, {user.DisplayName}";
        _selectedUsername.Text = $"@{user.Username}";

        _statusText.Text = string.Empty;
        _passwordBox.Text = string.Empty;

        // Cross-fade picker -> sign-in so the swap is always visibly animated,
        // not just when the accent change masks the snap.
        CrossFadePanels(_pickerPanel, _signInPanel, 280);

        // Animate to the user's preferred accent (or system default if none set).
        var targetAccent = UserManager.GetUserAccent(user) ?? _systemDefaultAccent;
        Accents.ApplyAccentAnimated(targetAccent, TimeSpan.FromMilliseconds(550));

        // Cross-fade in this user's chosen wallpaper. A null/missing pref
        // (or the explicit accent-only sentinel) leaves the accent vignette
        // as the backdrop.
        var targetWallpaper = UserManager.GetUserWallpaper(user) ?? WallpaperManager.Instance.DefaultWallpaperKey;
        WallpaperManager.Instance.SetWallpaper(targetWallpaper);

        // Pop the avatar in alongside the backdrop transition so it doesn't
        // snap in last when everything else is mid-animation.
        AnimateAvatarIn();

        Dispatcher.UIThread.Post(() => _passwordBox.Focus(), DispatcherPriority.Background);
    }

    private void AnimateAvatarIn()
    {
        _avatarGrid.Opacity = 0;
        _avatarScale.ScaleX = 0.6;
        _avatarScale.ScaleY = 0.6;

        _avatarAnimTween?.Stop();
        _avatarAnimTween = Tween.Run(420, Easings.EaseOutBack,
            apply: t =>
            {
                // Opacity ramps faster than the scale (clamped at 1) so the
                // glyph is fully visible before the back-out overshoot peaks.
                _avatarGrid.Opacity = Math.Clamp(t * 1.6, 0d, 1d);
                var s = 0.6 + (1.0 - 0.6) * t;
                _avatarScale.ScaleX = s;
                _avatarScale.ScaleY = s;
            },
            onCompleted: () =>
            {
                _avatarGrid.Opacity = 1;
                _avatarScale.ScaleX = 1;
                _avatarScale.ScaleY = 1;
                _avatarAnimTween = null;
            });
    }

    private void DeselectUser()
    {
        // Mirror SelectUser: snap any in-flight entrance animation so the
        // cross-fade isn't masked by the card still fading in.
        FinishEntranceAnimation();

        // Reset transient sign-in state without snapping the picker on - the
        // cross-fade below handles the visual swap.
        _selectedUser = null;
        _passwordBox.IsVisible = true;
        _signInButton.IsVisible = true;
        _switchUserButton.IsVisible = true;
        _statusText.Text = string.Empty;
        _passwordBox.Text = string.Empty;

        // Cross-fade sign-in -> picker (mirrors SelectUser).
        CrossFadePanels(_signInPanel, _pickerPanel, 280);

        // Animate back to the system default accent and accent-only backdrop.
        Accents.ApplyAccentAnimated(_systemDefaultAccent, TimeSpan.FromMilliseconds(550));
        WallpaperManager.Instance.SetWallpaper(WallpaperManager.AccentOnlyKey);
    }

    /// <summary>
    /// Pure opacity cross-fade from <paramref name="from"/> to <paramref name="to"/>.
    /// Both panels stay permanently in layout (only Opacity / IsHitTestVisible
    /// toggle), so the parent Grid never resizes mid-animation - the picker
    /// and sign-in panels share the same anchored position throughout.
    /// </summary>
    private void CrossFadePanels(Control from, Control to, int durationMs)
    {
        // Cancel any in-flight fade and reset start state.
        _panelFadeTween?.Stop();
        _panelFadeFrom = from;
        _panelFadeTo   = to;

        // Disable input on both panels for the duration of the fade so a
        // double-click can't trigger anything mid-animation.
        from.IsHitTestVisible = false;
        to.IsHitTestVisible   = false;

        // Defensive: clear any leftover transform from a prior animation so
        // neither panel can accidentally start off-position.
        from.RenderTransform = null;
        to.RenderTransform   = null;

        from.Opacity = 1;
        to.Opacity   = 0;

        _panelFadeTween = Tween.Run(Math.Max(1, durationMs), Easings.EaseInOutCubic,
            apply: t =>
            {
                from.Opacity = 1 - t;
                to.Opacity   = t;
            },
            onCompleted: () =>
            {
                from.Opacity = 0;
                to.Opacity   = 1;
                to.IsHitTestVisible = true;
                _panelFadeFrom = null;
                _panelFadeTo   = null;
                _panelFadeTween = null;
            });
    }

    // =====================================================================
    // Sign-in
    // =====================================================================

    private void OnSignInClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectedUser == null) return;

        var password = _passwordBox.Text ?? string.Empty;
        if (string.IsNullOrEmpty(password))
        {
            ShowError("Please enter your password.");
            return;
        }

        var authed = UserManager.Authenticate(_selectedUser.Username, password);
        if (authed == null)
        {
            ShowError("Incorrect password.");
            _passwordBox.Text = string.Empty;
            return;
        }

        // Hide the interactive controls so the celebration takes the spotlight
        // and the user can't double-submit while the animation is playing.
        _passwordBox.IsVisible = false;
        _signInButton.IsVisible = false;
        _switchUserButton.IsVisible = false;

        _statusText.Text = "Signing in...";
        _statusText.Foreground = Brushes.White;

        // Celebrate the successful sign-in, then hand control off to the host
        // (typically MainWindow) so it can crossfade into the desktop screen.
        DOSISuccessAnim.PlayOver(_layoutRoot, DOSISuccessAnim.SuccessSize.Large,
            onCompleted: () => SignInCompleted?.Invoke(this, authed));
    }

    private void ShowError(string message)
    {
        _statusText.Text = message;
        _statusText.Foreground = new SolidColorBrush(Color.FromRgb(232, 90, 90));
    }

    // =====================================================================
    // Theming / chrome refresh
    // =====================================================================

    private void OnAccentChanged(object? sender, EventArgs e)
    {
        _avatarCircle.Fill = BuildAvatarBrush();
        _avatarGlow.Fill = BuildAvatarGlowBrush();
        _avatarInitial.Foreground = new SolidColorBrush(Accents.TextOnAccent);

        // Picker / sign-in labels stay white - they sit on the wallpaper, not
        // on a chrome surface, so they should never re-tint with the accent
        // (would go dark + invisible under the Light accent).
        _statusText.Foreground = Brushes.White;

        _card.Background = Brushes.Transparent;
        if (_pickerDivider != null) _pickerDivider.Background = BuildPickerDividerBrush();
        _signInDivider.Background = BuildDividerBrush();

        // _clockText / _dateText are intentionally pinned to white - they
        // never re-tint with the accent (matches DesktopScreen's behavior).
        _versionText.Foreground = Accents.TextSecondaryBrush;
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        _clockText.Text = now.ToString("h:mm tt");
        _dateText.Text = now.ToString("dddd, MMMM d");
    }

    private LinearGradientBrush BuildAvatarBrush() => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Accents.AccentPrimary, 0),
            new GradientStop(Accents.AccentSecondary, 1)
        }
    };

    private RadialGradientBrush BuildAvatarGlowBrush()
    {
        var a = Accents.AccentPrimary;
        return new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(120, a.R, a.G, a.B), 0),
                new GradientStop(Color.FromArgb(40, a.R, a.G, a.B), 0.55),
                new GradientStop(Color.FromArgb(0, a.R, a.G, a.B), 1)
            }
        };
    }

    private LinearGradientBrush BuildDividerBrush() => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0, 255, 255, 255), 0),
            new GradientStop(Color.FromArgb(70, 255, 255, 255), 0.5),
            new GradientStop(Color.FromArgb(0, 255, 255, 255), 1)
        }
    };

    private LinearGradientBrush BuildPickerDividerBrush()
    {
        var a = Accents.AccentPrimary;
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 0),
                new GradientStop(Color.FromArgb(120, a.R, a.G, a.B), 0.5),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 1)
            }
        };
    }

    /// <summary>
    /// If the entrance animation is still mid-flight, snap the card to its
    /// final state (opacity 1) and cancel the tween. Called from SelectUser /
    /// DeselectUser so a fast click lands on a fully settled card and the
    /// cross-fade is the only animation in motion - otherwise the card's
    /// still-rising opacity multiplies the picker / sign-in opacities and
    /// the swap reads as instant.
    /// </summary>
    private void FinishEntranceAnimation()
    {
        _entranceTween?.Stop(snapToEnd: true);
        _entranceTween = null;
    }

    /// <summary>
    /// Pure opacity fade-in for the card on first navigation to the login
    /// screen. Matches the easing and "no slide" rule used by
    /// <see cref="CrossFadePanels"/> so the boot -> login appearance feels
    /// identical to picker &lt;-&gt; sign-in swaps and any later sign-out -> login
    /// return - everything stays anchored in place and dissolves.
    /// </summary>
    private void PlayEntranceAnimation()
    {
        _card.Opacity = 0;

        _entranceTween?.Stop();
        _entranceTween = Tween.Run(450, Easings.EaseInOutCubic,
            apply: t => _card.Opacity = t,
            onCompleted: () =>
            {
                _card.Opacity = 1;
                _entranceTween = null;
            });
    }
}

/// <summary>
/// Internal helper that mirrors the per-accent accent colors for avatar tile previews
/// without going through <see cref="AccentManager"/> (which would mutate the live accent).
/// </summary>
internal static class InitialStartup_AccentColors
{
    public static (Color Primary, Color Secondary) Get(DOSIAccent accent) => accent switch
    {
        DOSIAccent.DarkBlue => (Color.FromRgb(0, 122, 204), Color.FromRgb(0, 88, 156)),
        DOSIAccent.DarkPurple => (Color.FromRgb(138, 43, 226), Color.FromRgb(100, 30, 180)),
        DOSIAccent.DarkGreen => (Color.FromRgb(16, 185, 129), Color.FromRgb(10, 140, 100)),
        DOSIAccent.DarkOrange => (Color.FromRgb(255, 140, 0), Color.FromRgb(200, 100, 0)),
        DOSIAccent.DarkRed => (Color.FromRgb(220, 50, 70), Color.FromRgb(170, 30, 50)),
        DOSIAccent.DarkTeal => (Color.FromRgb(0, 188, 212), Color.FromRgb(0, 140, 160)),
        DOSIAccent.Light => (Color.FromRgb(0, 120, 215), Color.FromRgb(0, 90, 170)),
        DOSIAccent.Midnight => (Color.FromRgb(100, 100, 255), Color.FromRgb(70, 70, 200)),
        DOSIAccent.RoseGold => (Color.FromRgb(183, 110, 121), Color.FromRgb(150, 85, 95)),
        DOSIAccent.Coral => (Color.FromRgb(255, 127, 80), Color.FromRgb(210, 100, 60)),
        DOSIAccent.Lavender => (Color.FromRgb(180, 150, 210), Color.FromRgb(145, 115, 175)),
        DOSIAccent.Mint => (Color.FromRgb(152, 224, 186), Color.FromRgb(115, 185, 148)),
        DOSIAccent.Slate => (Color.FromRgb(112, 128, 144), Color.FromRgb(85, 100, 115)),
        DOSIAccent.Copper => (Color.FromRgb(184, 115, 81), Color.FromRgb(148, 88, 60)),
        DOSIAccent.Sapphire => (Color.FromRgb(15, 82, 186), Color.FromRgb(10, 60, 145)),
        DOSIAccent.Emerald => (Color.FromRgb(0, 155, 119), Color.FromRgb(0, 120, 90)),
        DOSIAccent.Ruby => (Color.FromRgb(155, 17, 50), Color.FromRgb(120, 12, 38)),
        DOSIAccent.Amber => (Color.FromRgb(255, 191, 0), Color.FromRgb(210, 155, 0)),
        DOSIAccent.Violet => (Color.FromRgb(143, 0, 255), Color.FromRgb(110, 0, 200)),
        DOSIAccent.Crimson => (Color.FromRgb(220, 20, 60), Color.FromRgb(170, 15, 45)),
        DOSIAccent.Forest => (Color.FromRgb(34, 139, 34), Color.FromRgb(25, 105, 25)),
        DOSIAccent.Ocean => (Color.FromRgb(0, 105, 148), Color.FromRgb(0, 80, 120)),
        DOSIAccent.Sunset => (Color.FromRgb(255, 94, 77), Color.FromRgb(210, 70, 55)),
        DOSIAccent.Storm => (Color.FromRgb(70, 90, 120), Color.FromRgb(50, 70, 95)),
        DOSIAccent.Bronze => (Color.FromRgb(205, 127, 50), Color.FromRgb(165, 100, 38)),
        DOSIAccent.Indigo => (Color.FromRgb(75, 0, 130), Color.FromRgb(55, 0, 100)),
        DOSIAccent.Magenta => (Color.FromRgb(255, 0, 255), Color.FromRgb(200, 0, 200)),
        DOSIAccent.Olive => (Color.FromRgb(128, 128, 0), Color.FromRgb(100, 100, 0)),
        DOSIAccent.Turquoise => (Color.FromRgb(64, 224, 208), Color.FromRgb(40, 180, 168)),
        DOSIAccent.Cyan => (Color.FromRgb(0, 220, 220), Color.FromRgb(0, 170, 175)),
        DOSIAccent.Aqua => (Color.FromRgb(130, 220, 230), Color.FromRgb(95, 180, 195)),
        DOSIAccent.Periwinkle => (Color.FromRgb(170, 175, 230), Color.FromRgb(135, 142, 195)),
        DOSIAccent.Plum => (Color.FromRgb(142, 69, 133), Color.FromRgb(108, 50, 102)),
        DOSIAccent.Fuchsia => (Color.FromRgb(255, 0, 200), Color.FromRgb(205, 0, 160)),
        DOSIAccent.Pink => (Color.FromRgb(255, 105, 180), Color.FromRgb(210, 80, 145)),
        DOSIAccent.Peach => (Color.FromRgb(255, 178, 130), Color.FromRgb(215, 142, 100)),
        DOSIAccent.Apricot => (Color.FromRgb(251, 175, 110), Color.FromRgb(208, 142, 85)),
        DOSIAccent.Tangerine => (Color.FromRgb(242, 133, 0), Color.FromRgb(200, 105, 0)),
        DOSIAccent.Goldenrod => (Color.FromRgb(218, 165, 32), Color.FromRgb(178, 132, 22)),
        DOSIAccent.Lime => (Color.FromRgb(146, 220, 50), Color.FromRgb(115, 178, 38)),
        DOSIAccent.Chartreuse => (Color.FromRgb(170, 220, 30), Color.FromRgb(135, 178, 22)),
        DOSIAccent.Sage => (Color.FromRgb(158, 188, 142), Color.FromRgb(122, 152, 110)),
        DOSIAccent.Pine => (Color.FromRgb(1, 121, 111), Color.FromRgb(0, 92, 85)),
        DOSIAccent.Jade => (Color.FromRgb(0, 168, 107), Color.FromRgb(0, 132, 84)),
        DOSIAccent.SeaGreen => (Color.FromRgb(46, 139, 87), Color.FromRgb(34, 108, 68)),
        DOSIAccent.Cerulean => (Color.FromRgb(0, 123, 167), Color.FromRgb(0, 95, 132)),
        DOSIAccent.SkyBlue => (Color.FromRgb(135, 206, 235), Color.FromRgb(98, 168, 200)),
        DOSIAccent.Cobalt => (Color.FromRgb(0, 71, 171), Color.FromRgb(0, 55, 135)),
        DOSIAccent.Navy => (Color.FromRgb(40, 60, 130), Color.FromRgb(28, 42, 95)),
        DOSIAccent.Burgundy => (Color.FromRgb(128, 0, 32), Color.FromRgb(95, 0, 22)),
        DOSIAccent.Maroon => (Color.FromRgb(128, 0, 0), Color.FromRgb(95, 0, 0)),
        DOSIAccent.Wine => (Color.FromRgb(114, 47, 55), Color.FromRgb(85, 35, 42)),
        DOSIAccent.Mocha => (Color.FromRgb(128, 92, 73), Color.FromRgb(98, 70, 55)),
        DOSIAccent.Chocolate => (Color.FromRgb(123, 63, 0), Color.FromRgb(92, 48, 0)),
        DOSIAccent.Sand => (Color.FromRgb(194, 178, 128), Color.FromRgb(155, 142, 100)),
        DOSIAccent.Charcoal => (Color.FromRgb(85, 92, 100), Color.FromRgb(62, 68, 75)),
        DOSIAccent.Steel => (Color.FromRgb(70, 130, 180), Color.FromRgb(52, 100, 142)),
        DOSIAccent.Onyx => (Color.FromRgb(80, 80, 85), Color.FromRgb(58, 58, 62)),
        _ => (Color.FromRgb(0, 122, 204), Color.FromRgb(0, 88, 156))
    };
}
