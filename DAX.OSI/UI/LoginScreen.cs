using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    // Bottom-corner chrome stacks - kept as fields so OnSignInClicked can
    // fade them out cleanly before raising SignInCompleted. Without that,
    // the corner shutdown button + version label POP out of existence at
    // the screen-manager handoff because DesktopScreen doesn't render
    // anything in the corners that would cover them.
    private readonly StackPanel _clockStack;
    private readonly StackPanel _bottomRightStack;

    // ----- Picker mode -----
    private readonly StackPanel _pickerPanel;
    private readonly WrapPanel _userTilesWrap;
    private Border? _pickerDivider;
    // Picker title is dynamic so we can swap "Choose your account" for a
    // time-of-day greeting ("Good morning, Tyler") when there's exactly
    // one user - small warmth touch, no cost when there's >1 user since
    // the original string falls through.
    private TextBlock? _pickerTitle;
    private TextBlock? _pickerSubtitle;

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
    // Tiny "CAPS LOCK" warning chip rendered just below the password row.
    // Kept hidden by default; flipped visible whenever a key arriving at
    // the password box reports KeyModifiers.None for Shift but produces a
    // Caps lock state. Same affordance every native login screen ships.
    private readonly Border _capsLockChip;
    // Eye glyph button overlaid on the password box that toggles the
    // DOSITextBox's UsePasswordChar mask. Pure QoL - users juggling long
    // generated passwords appreciate being able to verify what they
    // pasted/typed without typing it again into a plain-text field.
    private readonly TextBlock _passwordRevealGlyph;
    // The eye button itself - kept as a field so the success path can
    // hide it in lockstep with the password box. Without this, the eye
    // glyph stays painted over the "Welcome back" message during the
    // crossfade to the desktop.
    private readonly Border _passwordRevealButton;

    // ----- State -----
    private readonly DispatcherTimer _clockTimer;
    private Tween? _entranceTween;
    private Tween? _avatarAnimTween;
    private Tween? _panelFadeTween;
    private Control? _panelFadeFrom;
    private Control? _panelFadeTo;
    private DOSIUser? _selectedUser;
    private DOSIAccent _systemDefaultAccent;

    /// <summary>
    /// Longer-than-default wallpaper cross-fade for the login screen so
    /// selecting a user reads as a cinematic "dissolve into your world"
    /// moment rather than a quick swap. The base class uses 550 ms which
    /// is tuned for desktop blur toggles and rapid screen handoffs; here
    /// the wallpaper is changing FROM the accent vignette TO the user's
    /// chosen photo, which is the most emotionally loaded transition in
    /// the whole sign-in flow.
    /// </summary>
    protected override TimeSpan WallpaperTransitionDuration => TimeSpan.FromMilliseconds(900);

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

        _clockStack = new StackPanel
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
        _pickerTitle = pickerTitle;

        var pickerSubtitle = new TextBlock
        {
            Text = "Select a profile to sign in",
            FontSize = 13,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0.85,
            Margin = new Thickness(0, 4, 0, 22)
        };
        _pickerSubtitle = pickerSubtitle;

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
            Padding = new Thickness(14, 10, 38, 10), // extra right padding for the eye toggle
            CornerRadius = new CornerRadius(8),
            Height = 40,
            Width = 300,
            UsePasswordChar = true
        };
        _passwordBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) OnSignInClicked(this, new Avalonia.Interactivity.RoutedEventArgs());
            else if (e.Key == Key.CapsLock)
            {
                // Toggle now - KeyDown reports the PRE-toggle state. Defer
                // the visual update to the next tick so the modifier read
                // happens after the OS has actually flipped CapsLock.
                _capsLockTracked = !_capsLockTracked;
                Dispatcher.UIThread.Post(UpdateCapsLockChip, DispatcherPriority.Background);
            }
            else
            {
                // Any other keypress refreshes the chip - covers the case
                // where the user pressed CapsLock while focus was elsewhere.
                UpdateCapsLockChip();
            }
        };
        _passwordBox.GotFocus += (_, _) => UpdateCapsLockChip();

        // Reveal-password eye glyph, overlaid on the right edge of the
        // password box. Toggles UsePasswordChar between true/false; glyph
        // swap (eye <-> crossed-eye) telegraphs the current state. Stays
        // visible only while focus is in the password row to keep the
        // sign-in card visually quiet at rest.
        //
        // FONT NOTE: previously used U+1F441 (eye emoji) + U+1F576
        // (sunglasses emoji). Both are emoji-presentation code points,
        // which means the renderer falls through to whatever system
        // emoji font is available - so the same glyph could render as
        // a flat blue silhouette on one machine and a full-colour pixel
        // emoji on another, and the two STATES rendered with visibly
        // different weights/sizes because the eye and sunglasses come
        // from different emoji fonts. Switched to a pair of plain
        // monochrome BMP code points (U+25CF filled circle for "masked"
        // and U+25CB hollow circle for "revealed") and pinned the
        // glyph's FontFamily to the bundled DOSIFonts.UIFamily so both
        // states are guaranteed to be rendered by the same face at the
        // same metrics - no more "the eye looks different at certain
        // times".
        _passwordRevealGlyph = new TextBlock
        {
            Text = "\u25CF", // ● filled - masked
            FontFamily = DOSIFonts.UI,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsHitTestVisible = false
        };
        _passwordRevealButton = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(16),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            Child = _passwordRevealGlyph
        };
        ToolTip.SetTip(_passwordRevealButton, "Show password");
        _passwordRevealButton.PointerEntered += (_, _) =>
            _passwordRevealButton.Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
        _passwordRevealButton.PointerExited += (_, _) =>
            _passwordRevealButton.Background = Brushes.Transparent;
        _passwordRevealButton.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            _passwordBox.UsePasswordChar = !_passwordBox.UsePasswordChar;
            _passwordRevealGlyph.Text = _passwordBox.UsePasswordChar
                ? "\u25CF"   // ● filled circle (masked)
                : "\u25CB";  // ○ hollow circle (revealed) - same font, same metrics
            ToolTip.SetTip(_passwordRevealButton, _passwordBox.UsePasswordChar ? "Show password" : "Hide password");
            // Keep focus on the password field so typing continues.
            _passwordBox.Focus();
        };

        var passwordRow = new Grid { Width = 300, Height = 40 };
        passwordRow.Children.Add(_passwordBox);
        passwordRow.Children.Add(_passwordRevealButton);

        // Caps-lock warning chip - sits in the layout permanently (so the
        // card doesn't reflow when it appears) but is invisible by default.
        // ShowDelta = Opacity tween instead of IsVisible flip so the chip
        // doesn't jitter the layout when CapsLock toggles repeatedly.
        var capsGlyph = new TextBlock
        {
            Text = "\u21EA  CAPS LOCK", // ⇪
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            LetterSpacing = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _capsLockChip = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(170, 200, 90, 30)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 3),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
            Height = 20,
            Opacity = 0,
            IsHitTestVisible = false,
            Child = capsGlyph
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
                passwordRow,
                _capsLockChip,
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
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0)
        };

        // ===== Bottom-right: Shutdown button + version stack =====
        // Mirrors the apps-menu Shutdown entry (DesktopScreen.
        // BuildShutdownGlyph) but presented as a standalone circular
        // power button so the user can power off without signing in.
        // Stacked vertically with the version label underneath - keeps
        // them as a single visual unit pinned to the bottom-right corner,
        // out of the way of the bottom-left clock / date and the centred
        // sign-in card.
        var shutdownButton = BuildShutdownButton();
        _bottomRightStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 24, 16),
            Spacing = 0,
            Children = { shutdownButton, _versionText }
        };

        _layoutRoot = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Children = { _clockStack, _card, _bottomRightStack }
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
        // Suppress the card-only fade-in when the SCREEN itself is being
        // crossfaded in by ScreenManager.NavigateToWithCrossfadeAsync
        // (Opacity is set to 0 before OnNavigatedTo fires and tweened back
        // to 1 over the next several hundred ms). Without this guard, BOTH
        // animations run simultaneously - the screen fades 0->1 AND the
        // card fades 0->1 - so their opacities multiply (0.3 * 0.3 ~= 9%)
        // and the card stays nearly invisible for the entire crossfade,
        // then snaps to full opacity mid-transition. Most visible on the
        // InitialStartup -> LoginScreen handoff because the setup wizard's
        // success animation runs immediately before, drawing the user's
        // eye to exactly where the card is supposed to appear.
        // The screen-level crossfade is already a perfectly good entrance;
        // an extra nested fade serves no purpose during it.
        if (Opacity >= 0.999)
            PlayEntranceAnimation();
        else
            _card.Opacity = 1;

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
        UpdatePickerGreeting(users);
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

        // Pre-warm each user's preferred wallpaper bitmap off-thread so
        // selecting a tile doesn't pay the decode + downscale + Skia
        // blur-bake cost on click. Without this, the user clicks their
        // tile and the wallpaper visibly takes a couple of seconds to
        // resolve (most painful with large custom photos): the
        // OnWallpaperChanged async resolve runs only AFTER SelectUser
        // fires WallpaperChanged, so the cross-fade waits on the decode.
        // By kicking the decode here, the cache is hot by the time the
        // user actually clicks and the transition starts immediately.
        PrewarmUserWallpapers(users);
    }

    /// <summary>
    /// Best-effort: kick a background decode of every known user's
    /// preferred wallpaper so the bitmap cache is hot before any tile
    /// is clicked. Custom file-path wallpapers are auto-registered with
    /// <see cref="WallpaperManager"/> so the subsequent
    /// <see cref="WallpaperManager.SetWallpaper"/> call from
    /// <see cref="SelectUser"/> hits the cache instead of starting a
    /// fresh decode. Safe to call repeatedly; cache hits are no-ops.
    /// </summary>
    private static void PrewarmUserWallpapers(IReadOnlyList<DOSIUser> users)
    {
        if (users == null || users.Count == 0) return;
        var wm = WallpaperManager.Instance;
        foreach (var u in users)
        {
            var k = UserManager.GetUserWallpaper(u);
            if (string.IsNullOrWhiteSpace(k)) continue;
            // WallpaperManager.Prewarm handles the AccentOnlyKey + custom
            // file-path registration internally and runs the decode on a
            // worker thread. Cache hits are no-ops, so re-prewarming
            // every time the picker rebuilds is cheap.
            try { wm.Prewarm(k!); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Swaps the picker title for a time-of-day greeting when there's
    /// exactly one user (cheap warmth - the system feels like it knows
    /// them). With multiple users we keep the neutral "Choose your
    /// account" so we don't have to pick a favourite. The subtitle is
    /// updated in lockstep so the visual weight balances.
    /// </summary>
    private void UpdatePickerGreeting(IReadOnlyList<DOSIUser> users)
    {
        if (_pickerTitle == null || _pickerSubtitle == null) return;
        if (users.Count == 1)
        {
            var u = users[0];
            var name = !string.IsNullOrWhiteSpace(u.DisplayName)
                ? u.DisplayName
                : u.Username;
            var hour = DateTime.Now.Hour;
            string greeting =
                hour < 5 ? "Up late" :
                hour < 12 ? "Good morning" :
                hour < 17 ? "Good afternoon" :
                hour < 22 ? "Good evening" : "Welcome back";
            _pickerTitle.Text = $"{greeting}, {name}";
            _pickerSubtitle.Text = "Click your profile to sign in";
        }
        else
        {
            _pickerTitle.Text = "Choose your account";
            _pickerSubtitle.Text = "Select a profile to sign in";
        }
    }

    /// <summary>
    /// Reads the live Caps Lock toggle state and animates the warning chip
    /// in/out. Called whenever a key arrives at the password field or the
    /// field gains focus. Uses Opacity (not IsVisible) so the chip's
    /// footprint stays reserved in the layout - the card never reflows
    /// when CapsLock toggles, which is the difference between "subtle
    /// warning" and "jarring flicker".
    /// </summary>
    private void UpdateCapsLockChip()
    {
        if (_capsLockChip == null) return;
        bool capsOn = IsCapsLockOn();
        _capsLockChip.Opacity = capsOn ? 1 : 0;
    }

    /// <summary>
    /// Best-effort CapsLock detector. On Windows we ask user32 directly
    /// (instant, accurate even when CapsLock was toggled outside the
    /// app); on any other host we fall back to the locally-tracked
    /// _capsLockTracked flag which is flipped each time we observe a
    /// Key.CapsLock keypress in the password field. The fallback can
    /// miss state changes that happen while the field doesn't have
    /// focus, which is the standard tradeoff every cross-platform login
    /// chip makes - good enough as a warning, never trusted as a lock.
    /// </summary>
    private bool _capsLockTracked;
    private bool IsCapsLockOn()
    {
        try
        {
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Windows))
                return (NativeGetKeyState(0x14) & 1) != 0;
        }
        catch { /* fall through */ }
        return _capsLockTracked;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetKeyState")]
    private static extern short NativeGetKeyState(int nVirtKey);

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

        // Personal touch: if the user has picked a wallpaper, paint its
        // thumbnail UNDER the gradient ring so each tile is a tiny window
        // into that user's world. The ring stays on top so the accent
        // gradient still dominates - we just bleed a soft preview through.
        // Async-loaded from the shared thumbnail cache (warmed at boot)
        // so this is free at picker time. Skipped for accent-only users
        // since there's nothing to preview.
        TryAddWallpaperHalo(avatar, user, ring);

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

    /// <summary>
    /// Inserts a circular, soft-edged wallpaper preview under the user's
    /// gradient ring so each picker tile reads as "your space." Async
    /// thumbnail load via WallpaperManager so the picker doesn't pay
    /// decode cost; if the user has no wallpaper (or the decode fails),
    /// the ring just stays gradient-only - same as before. The preview
    /// is clipped to a circle via OpacityMask so the ring's circular
    /// outline keeps reading cleanly.
    /// </summary>
    private static void TryAddWallpaperHalo(Grid avatar, DOSIUser user, Ellipse ring)
    {
        var key = UserManager.GetUserWallpaper(user);
        if (string.IsNullOrWhiteSpace(key)) return;
        if (string.Equals(key, WallpaperManager.AccentOnlyKey, StringComparison.OrdinalIgnoreCase)) return;

        // Image rendered inside an Ellipse-clipped Border so the bitmap
        // composites as a circle exactly the same diameter as the
        // gradient ring above it.
        var image = new Image
        {
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.55  // muted so the accent ring still dominates
        };
        var halo = new Border
        {
            Width = 64,
            Height = 64,
            CornerRadius = new CornerRadius(32),
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Child = image
        };

        // Insert BEFORE the existing children so the halo sits beneath the
        // gradient ring and initial. Ring renders on top, halo behind it.
        avatar.Children.Insert(0, halo);

        WallpaperManager.Instance.LoadThumbnailAsync(key!, bmp =>
        {
            // Sanity: the avatar may have been detached before the
            // decode landed (rapid sign-in/out). Skip the assign in
            // that case so we don't pin the bitmap unnecessarily.
            if (bmp != null && halo.Parent != null)
                image.Source = bmp;
        });
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
        _passwordRevealButton.IsVisible = true;
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
        // not just when the accent change masks the snap. Run the panel
        // and accent durations a touch longer than the default so they
        // breathe in sync with the new (longer) wallpaper dissolve below -
        // everything finishes within ~50 ms of each other for a cohesive
        // "the screen rearranges itself into your space" feel.
        CrossFadePanels(_pickerPanel, _signInPanel, 520);

        // Animate to the user's preferred accent (or system default if none set).
        var targetAccent = UserManager.GetUserAccent(user) ?? _systemDefaultAccent;
        Accents.ApplyAccentAnimated(targetAccent, TimeSpan.FromMilliseconds(850));

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
        _passwordRevealButton.IsVisible = true;
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
        _passwordRevealButton.IsVisible = false;
        _signInButton.IsVisible = false;
        _switchUserButton.IsVisible = false;

        _statusText.Text = "Signing in...";
        _statusText.Foreground = Brushes.White;

        // Celebrate the successful sign-in, then fade out the corner chrome
        // (clock + shutdown button + version label) BEFORE handing control
        // off to the host. The screen manager's crossfade only fades the
        // INCOMING screen in over the still-opaque outgoing one (so most
        // covered regions transition smoothly), but the corners aren't
        // covered by DesktopScreen, so without this pre-fade the user sees
        // the shutdown button vanish at the swap. The central success card
        // is left at full opacity intentionally - it's what the desktop
        // fades in over.
        DOSISuccessAnim.PlayOver(_layoutRoot, DOSISuccessAnim.SuccessSize.Large,
            onCompleted: () => FadeCornerChromeAndSignIn(authed));
    }

    private void FadeCornerChromeAndSignIn(DOSIUser authed)
    {
        const int FadeMs = 220;

        // Both corner stacks fade in lock-step. We complete the sign-in
        // handoff from the clock-stack tween's onCompleted because both
        // tweens have identical durations - using either is equivalent.
        Tween.Run(FadeMs, Easings.EaseOutCubic,
            apply: t => _bottomRightStack.Opacity = 1 - t);
        Tween.Run(FadeMs, Easings.EaseOutCubic,
            apply: t => _clockStack.Opacity = 1 - t,
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

    /// <summary>
    /// Builds the standalone shutdown power button shown in the bottom-left
    /// corner of the login screen. Visually echoes the apps-menu Shutdown
    /// glyph (DesktopScreen.BuildShutdownGlyph) - same red-tinted circular
    /// chip with a power symbol - but enlarged and elevated to a primary
    /// affordance so the user can power off without signing in. Pointer
    /// hover gently brightens both the fill and the glyph; click invokes
    /// the same SystemShutdown.Begin pipeline the apps menu uses.
    /// </summary>
    private static Control BuildShutdownButton()
    {
        var glyph = new TextBlock
        {
            Text = "\u23FB", // power symbol
            FontSize = 22,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(255, 150, 150)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Idle / hover brushes captured here so the pointer handlers below
        // can swap between them without having to re-create brushes every
        // event - cheaper, and keeps the colour palette declarative.
        var idleFill = new SolidColorBrush(Color.FromRgb(60, 18, 18));
        var hoverFill = new SolidColorBrush(Color.FromRgb(95, 24, 24));
        var idleBorder = new SolidColorBrush(Color.FromArgb(180, 240, 90, 90));
        var hoverBorder = new SolidColorBrush(Color.FromArgb(255, 255, 120, 120));
        var idleGlyph = new SolidColorBrush(Color.FromRgb(255, 150, 150));
        var hoverGlyph = new SolidColorBrush(Color.FromRgb(255, 200, 200));

        var button = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(22),
            Background = idleFill,
            BorderBrush = idleBorder,
            BorderThickness = new Thickness(1.5),
            // Layout owned by the parent StackPanel - centre the chip
            // horizontally inside the stack so the smaller version label
            // underneath sits visually centred under it.
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = glyph,
            // Soft red glow under the chip - same vibe as the apps-menu
            // Shutdown row's hover state, but always-on since this is the
            // single power affordance on the login screen and we want it
            // to read as "primary destructive action available".
            Effect = new Avalonia.Media.DropShadowEffect
            {
                BlurRadius = 14,
                Color = Color.FromRgb(255, 60, 60),
                Opacity = 0.35,
                OffsetX = 0,
                OffsetY = 0
            }
        };

        button.PointerEntered += (_, _) =>
        {
            button.Background = hoverFill;
            button.BorderBrush = hoverBorder;
            glyph.Foreground = hoverGlyph;
        };
        button.PointerExited += (_, _) =>
        {
            button.Background = idleFill;
            button.BorderBrush = idleBorder;
            glyph.Foreground = idleGlyph;
        };
        button.PointerPressed += (_, e) =>
        {
            // Left-click only; ignore middle / right so a stray right-click
            // doesn't accidentally power off the system.
            if (e.GetCurrentPoint(button).Properties.IsLeftButtonPressed)
            {
                try { SystemShutdown.Begin(0); } catch { /* best-effort */ }
            }
        };

        return button;
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
