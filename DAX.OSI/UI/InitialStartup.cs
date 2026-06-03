using System;
using System.Collections.Generic;
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
using DOSI.CORE.WallpaperManagement;

namespace DAX.OSI.UI;

/// <summary>
/// First-run setup wizard. Walks the user through:
/// 1. Welcome
/// 2. Account creation (username + password)
/// 3. Display name + avatar color preview
/// 4. Color accent selection (live preview)
/// 5. Finish (creates the account, saves accent, sets system default)
/// </summary>
public class InitialStartup : DOSIScreen
{
    public override string ScreenId => "initial-startup";
    public override string ScreenName => "Welcome";

    /// <summary>Raised once setup is complete and the new user has been created.</summary>
    public event EventHandler<DOSIUser>? SetupCompleted;

    private static AccentManager Accents => AccentManager.Instance;

    // ----- Wizard state -----
    private readonly List<WizardStep> _steps = new();
    private int _currentStepIndex;

    private string _username = string.Empty;
    private string _password = string.Empty;
    private string _passwordConfirm = string.Empty;
    private string _displayName = string.Empty;
    private DOSIAccent _selectedAccent = DOSIAccent.DarkBlue;
    private readonly DOSIAccent _originalAccent;
    private string? _selectedWallpaperKey; // null = nothing chosen yet, WallpaperManager.AccentOnlyKey = opted out
    private readonly string? _originalWallpaperKey;

    // ----- UI -----
    private readonly Grid _layoutRoot;
    private readonly Border _card;
    private readonly StackPanel _stepDots;
    private readonly TextBlock _stepTitle;
    private readonly TextBlock _stepSubtitle;
    private readonly ContentControl _stepContent;
    private readonly DOSIButton _backButton;
    private readonly DOSIButton _nextButton;
    private readonly TextBlock _errorText;

    private DispatcherTimer? _entranceTimer;

    public InitialStartup()
    {
        _originalAccent = Accents.CurrentAccent;
        _selectedAccent = _originalAccent;
        _originalWallpaperKey = WallpaperManager.Instance.CurrentWallpaperKey;

        // ===== Header =====
        // Animated dot strip: one Ellipse per step, the active dot grows
        // into an accent-tinted pill while the rest stay as small grey
        // discs. RebuildStepDots wires the per-dot tween whenever the
        // step changes - reads as a live progress indicator instead of
        // the old static "● ○ ○" glyph string.
        _stepDots = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8)
        };

        _stepTitle = new TextBlock
        {
            FontSize = 28,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextPrimaryBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };

        _stepSubtitle = new TextBlock
        {
            FontSize = 13,
            Foreground = Accents.TextSecondaryBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 4, 0, 18),
            Opacity = 0.85,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460
        };

        _stepContent = new ContentControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            MinHeight = 240
        };

        _errorText = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(232, 90, 90)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
            Height = 16
        };

        // ===== Buttons =====
        // Back uses the universal DOSIButton style (matches every other button
        // in the OS). Next is filled with the accent gradient and uses the
        // on-accent text color so it visually wins as the primary action.
        _backButton = new DOSIButton
        {
            Text = "Back",
            Width = 160,
            Height = 42,
            CornerRadius = new CornerRadius(8),
            FontSize = 14
        };
        _backButton.Click += (_, _) => GoBack();

        _nextButton = new DOSIButton
        {
            Text = "Next",
            Width = 220,
            Height = 42,
            CornerRadius = new CornerRadius(8),
            FontSize = 14,
            Background = Accents.AccentPrimaryBrush,
            BackgroundHover = Accents.AccentSecondaryBrush,
            BackgroundPressed = Accents.AccentPrimaryBrush,
            Foreground = new SolidColorBrush(Accents.TextOnAccent),
            BorderThickness = 0
        };
        _nextButton.Click += (_, _) => GoNext();

        var buttonRow = new DockPanel
        {
            LastChildFill = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 24, 0, 0)
        };
        DockPanel.SetDock(_backButton, Dock.Left);
        DockPanel.SetDock(_nextButton, Dock.Right);
        buttonRow.Children.Add(_backButton);
        buttonRow.Children.Add(_nextButton);

        // ===== Card =====
        // No dark background panel - content floats directly on the accent
        // backdrop so the wizard feels light and matches the LoginScreen.
        var cardContent = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(40),
            Children =
            {
                _stepDots,
                _stepTitle,
                _stepSubtitle,
                _stepContent,
                _errorText,
                buttonRow
            }
        };

        _card = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            Width = 580,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = cardContent
        };

        _layoutRoot = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Children = { _card }
        };

        Desktop.Children.Add(_layoutRoot);
        Desktop.LayoutUpdated += (_, _) =>
        {
            _layoutRoot.Width = Desktop.Bounds.Width;
            _layoutRoot.Height = Desktop.Bounds.Height;
        };

        BuildSteps();

        AttachedToVisualTree += (_, _) => Accents.AccentChanged += OnAccentChanged;
        DetachedFromVisualTree += (_, _) =>
        {
            Accents.AccentChanged -= OnAccentChanged;
            _entranceTimer?.Stop();
            _entranceTimer = null;
        };
    }

    public override void OnNavigatedTo()
    {
        base.OnNavigatedTo();
        ShowStep(0, animate: true, direction: 1);
        NotifyScreenReady();
    }

    // =====================================================================
    // Step infrastructure
    // =====================================================================

    private sealed class WizardStep
    {
        public required string Title { get; init; }
        public required string Subtitle { get; init; }
        public required Func<Control> Build { get; init; }
        public Func<bool>? Validate { get; init; }
        public string NextLabel { get; init; } = "Next";
    }

    private void BuildSteps()
    {
        _steps.Clear();
        _steps.Add(new WizardStep
        {
            Title = "Welcome to DAX.OSI",
            Subtitle = "Let's set up your account. This only takes a minute.",
            Build = BuildWelcomeStep,
            NextLabel = "Get Started"
        });
        _steps.Add(new WizardStep
        {
            Title = "Create your account",
            Subtitle = "Pick a username and password for signing in.",
            Build = BuildAccountStep,
            Validate = ValidateAccountStep
        });
        _steps.Add(new WizardStep
        {
            Title = "How should we greet you?",
            Subtitle = "Your display name appears across DAX.OSI.",
            Build = BuildAvatarStep,
            Validate = ValidateAvatarStep
        });
        _steps.Add(new WizardStep
        {
            Title = "Pick your color accent",
            Subtitle = "The accent color is applied across the OS instantly. You can change this later.",
            Build = BuildAccentStep
        });
        _steps.Add(new WizardStep
        {
            Title = "Choose a wallpaper",
            Subtitle = "Pick a backdrop for your desktop, or stick with your accent color.",
            Build = BuildWallpaperStep,
            NextLabel = "Finish Setup"
        });
        _steps.Add(new WizardStep
        {
            Title = "You're all set",
            Subtitle = "Your account has been created and your preferences saved.",
            Build = BuildDoneStep,
            NextLabel = "Sign In"
        });
    }

    private void ShowStep(int index, bool animate, int direction = 1)
    {
        _currentStepIndex = Math.Clamp(index, 0, _steps.Count - 1);
        var step = _steps[_currentStepIndex];

        _errorText.Text = string.Empty;
        RebuildStepDots(_currentStepIndex, _steps.Count);
        _stepTitle.Text = step.Title;
        _stepSubtitle.Text = step.Subtitle;
        _stepContent.Content = step.Build();

        _backButton.IsVisible = _currentStepIndex > 0 && _currentStepIndex < _steps.Count - 1;
        _nextButton.Text = step.NextLabel;

        // When the back button is hidden, center the next button across the
        // full row instead of leaving it docked to the right.
        if (_backButton.IsVisible)
        {
            DockPanel.SetDock(_nextButton, Dock.Right);
            _nextButton.HorizontalAlignment = HorizontalAlignment.Right;
        }
        else
        {
            DockPanel.SetDock(_nextButton, Dock.Top);
            _nextButton.HorizontalAlignment = HorizontalAlignment.Center;
        }

        if (animate) PlayCardEntrance(direction);
    }

    /// <summary>
    /// Rebuilds the animated step-dot strip for <paramref name="currentIndex"/>
    /// out of <paramref name="total"/>. The active dot grows from a 6 px
    /// circle into a 22 px accent-tinted pill via a tween so the user
    /// sees the progress visibly advance step-to-step. Past steps stay
    /// as small accent discs (so progress reads as a filled trail), and
    /// future steps stay as faint grey discs. Idempotent - safe to call
    /// from accent / language changes too.
    /// </summary>
    private void RebuildStepDots(int currentIndex, int total)
    {
        if (_stepDots == null) return;
        _stepDots.Children.Clear();
        var accent = Accents.AccentPrimaryBrush;
        var inactive = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255));
        for (int i = 0; i < total; i++)
        {
            bool isActive = i == currentIndex;
            bool isPast = i < currentIndex;
            var dot = new Border
            {
                Width = isActive ? 22 : 6,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = (isActive || isPast) ? (IBrush)accent : inactive,
                VerticalAlignment = VerticalAlignment.Center,
                // Subtle accent halo on the active pill so the eye lands on
                // it without effort - the rest of the strip stays visually
                // quiet.
                Effect = isActive
                    ? new Avalonia.Media.DropShadowEffect
                    {
                        BlurRadius = 12,
                        Color = Accents.AccentPrimary,
                        Opacity = 0.55,
                        OffsetX = 0,
                        OffsetY = 0
                    }
                    : null
            };
            _stepDots.Children.Add(dot);
        }
    }

    [Obsolete("Replaced by RebuildStepDots; kept temporarily for any external callers.")]
    private static string BuildStepDots(int currentIndex, int total)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < total; i++)
        {
            sb.Append(i == currentIndex ? "\u25CF" : "\u25CB");
            if (i < total - 1) sb.Append("  ");
        }
        return sb.ToString();
    }

    private void GoBack()
    {
        if (_currentStepIndex == 0) return;
        ShowStep(_currentStepIndex - 1, animate: true, direction: -1);
    }

    private void GoNext()
    {
        var step = _steps[_currentStepIndex];
        if (step.Validate != null && !step.Validate())
        {
            // Validate already populated _errorText.
            return;
        }

        // Last step => finalize and exit.
        if (_currentStepIndex == _steps.Count - 1)
        {
            // Celebrate the new account, then hand off to the login screen.
            DOSISuccessAnim.PlayOver(_layoutRoot, DOSISuccessAnim.SuccessSize.Large,
                onCompleted: () => SetupCompleted?.Invoke(this, _createdUser!));
            return;
        }

        // Step before "Done" => commit the account.
        if (_currentStepIndex == _steps.Count - 2)
        {
            if (!CommitAccount()) return;
        }

        ShowStep(_currentStepIndex + 1, animate: true, direction: 1);
    }

    // =====================================================================
    // Step 1: Welcome
    // =====================================================================

    private Control BuildWelcomeStep()
    {
        var icon = new Border
        {
            Width = 96,
            Height = 96,
            CornerRadius = new CornerRadius(48),
            Background = Accents.AccentGradientBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = "\u2728",
                FontSize = 44,
                Foreground = new SolidColorBrush(Accents.TextOnAccent),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var copy = new TextBlock
        {
            Text = "We'll create your user profile, set your color accent, and hand you off to the sign-in screen.",
            FontSize = 13,
            Foreground = Accents.TextSecondaryBrush,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 24, 0, 0),
            Opacity = 0.9
        };

        return new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 0),
            Children = { icon, copy }
        };
    }

    // =====================================================================
    // Step 2: Account
    // =====================================================================

    private DOSITextBox? _usernameField;
    private DOSITextBox? _passwordField;
    private DOSITextBox? _passwordConfirmField;

    private Control BuildAccountStep()
    {
        _usernameField = new DOSITextBox
        {
            PlaceholderText = "Username (lowercase letters, numbers, _ -)",
            Width = 360,
            Height = 40,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10),
            Text = _username
        };
        _usernameField.TextChanged += (_, _) => _username = _usernameField!.Text ?? string.Empty;

        _passwordField = new DOSITextBox
        {
            PlaceholderText = "Password (4+ characters)",
            Width = 360,
            Height = 40,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10),
            Text = _password,
            UsePasswordChar = true
        };
        _passwordField.TextChanged += (_, _) => _password = _passwordField!.Text ?? string.Empty;

        _passwordConfirmField = new DOSITextBox
        {
            PlaceholderText = "Confirm password",
            Width = 360,
            Height = 40,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10),
            Text = _passwordConfirm,
            UsePasswordChar = true
        };
        _passwordConfirmField.TextChanged += (_, _) => _passwordConfirm = _passwordConfirmField!.Text ?? string.Empty;

        var stack = new StackPanel
        {
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { _usernameField, _passwordField, _passwordConfirmField }
        };

        Dispatcher.UIThread.Post(() => _usernameField.Focus(), DispatcherPriority.Background);
        return stack;
    }

    private bool ValidateAccountStep()
    {
        if (!UserManager.IsValidUsername(_username))
        {
            ShowError("Username must start with a letter and be 3-32 chars (a-z, 0-9, _ -).");
            return false;
        }
        if (UserManager.UserExists(_username))
        {
            ShowError("That username is already taken.");
            return false;
        }
        if (!UserManager.IsValidPassword(_password))
        {
            ShowError("Password must be at least 4 characters and not start or end with a space.");
            return false;
        }
        if (_password != _passwordConfirm)
        {
            ShowError("Passwords don't match.");
            return false;
        }
        return true;
    }

    // =====================================================================
    // Step 3: Avatar / display name
    // =====================================================================

    private DOSITextBox? _displayNameField;
    private TextBlock? _avatarInitial;
    private Ellipse? _avatarCircle;

    private Control BuildAvatarStep()
    {
        if (string.IsNullOrWhiteSpace(_displayName))
            _displayName = _username;

        _avatarCircle = new Ellipse
        {
            Width = 120,
            Height = 120,
            Fill = BuildAvatarBrush(),
            Stroke = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            StrokeThickness = 1.5,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        _avatarInitial = new TextBlock
        {
            Text = GetInitial(_displayName),
            FontSize = 56,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Accents.TextOnAccent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var avatar = new Grid
        {
            Width = 120,
            Height = 120,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { _avatarCircle, _avatarInitial }
        };

        _displayNameField = new DOSITextBox
        {
            PlaceholderText = "Display name",
            Width = 360,
            Height = 40,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10),
            Text = _displayName
        };
        _displayNameField.TextChanged += (_, _) =>
        {
            _displayName = _displayNameField!.Text ?? string.Empty;
            if (_avatarInitial != null) _avatarInitial.Text = GetInitial(_displayName);
        };

        return new StackPanel
        {
            Spacing = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { avatar, _displayNameField }
        };
    }

    private bool ValidateAvatarStep()
    {
        if (string.IsNullOrWhiteSpace(_displayName))
        {
            ShowError("Please enter a display name.");
            return false;
        }
        return true;
    }

    private static string GetInitial(string text)
    {
        var t = (text ?? string.Empty).Trim();
        return t.Length == 0 ? "?" : char.ToUpperInvariant(t[0]).ToString();
    }

    // =====================================================================
    // Step 4: Color accent
    // =====================================================================

    private readonly Dictionary<DOSIAccent, Ellipse> _accentRings = new();

    private Control BuildAccentStep()
    {
        _accentRings.Clear();

        var wrap = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            ItemWidth = 78,
            ItemHeight = 88
        };

        foreach (var accent in AccentManager.GetAvailableAccents())
        {
            wrap.Children.Add(BuildAccentSwatch(accent));
        }

        var scroller = new DOSIScrollViewer
        {
            Content = wrap,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            MaxHeight = 280
        };

        return scroller;
    }

    private Control BuildAccentSwatch(DOSIAccent accent)
    {
        var colors = GetAccentPreviewColors(accent);

        var ring = new Ellipse
        {
            Width = 56,
            Height = 56,
            Fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(colors.Primary, 0),
                    new GradientStop(colors.Secondary, 1)
                }
            },
            Stroke = accent == _selectedAccent
                ? new SolidColorBrush(Colors.White)
                : new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            StrokeThickness = accent == _selectedAccent ? 3 : 1.5,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _accentRings[accent] = ring;

        var label = new TextBlock
        {
            Text = AccentManager.GetAccentDisplayName(accent),
            FontSize = 10,
            Foreground = Accents.TextSecondaryBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 70,
            Margin = new Thickness(0, 6, 0, 0)
        };

        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4),
            Children = { ring, label },
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
        };

        stack.PointerPressed += (_, _) =>
        {
            if (_selectedAccent == accent) return;
            _selectedAccent = accent;
            // Live, smooth preview applied to the entire OS.
            Accents.ApplyAccentAnimated(accent, TimeSpan.FromMilliseconds(450));
            // Update selection rings in place so the scroll position is preserved.
            foreach (var (a, r) in _accentRings)
            {
                var sel = a == accent;
                r.Stroke = sel
                    ? new SolidColorBrush(Colors.White)
                    : new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
                r.StrokeThickness = sel ? 3 : 1.5;
            }
        };

        return stack;
    }

    private static (Color Primary, Color Secondary) GetAccentPreviewColors(DOSIAccent accent)
    {
        // Snapshot the accent colors by applying then immediately reading - but we don't
        // want to disrupt the live accent. Instead, mirror the lookup table by name with a
        // small known map of representative colors. Falls back to current accent if unknown.
        return accent switch
        {
            DOSIAccent.DarkBlue => (Color.FromRgb(0, 122, 204), Color.FromRgb(0, 88, 156)),
            DOSIAccent.DarkPurple => (Color.FromRgb(138, 43, 226), Color.FromRgb(100, 30, 180)),
            DOSIAccent.DarkGreen => (Color.FromRgb(16, 185, 129), Color.FromRgb(10, 140, 100)),
            DOSIAccent.DarkOrange => (Color.FromRgb(255, 140, 0), Color.FromRgb(200, 100, 0)),
            DOSIAccent.DarkRed => (Color.FromRgb(220, 50, 70), Color.FromRgb(170, 30, 50)),
            DOSIAccent.DarkTeal => (Color.FromRgb(0, 188, 212), Color.FromRgb(0, 140, 160)),
            DOSIAccent.Light => (Color.FromRgb(0, 120, 215), Color.FromRgb(0, 90, 170)),
            DOSIAccent.Dark => (Color.FromRgb(120, 160, 220), Color.FromRgb(85, 120, 175)),
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

    // =====================================================================
    // Step 5: Wallpaper
    // =====================================================================

    private Border? _accentOnlyTile;
    private readonly Dictionary<string, Border> _wallpaperTiles = new();

    private Control BuildWallpaperStep()
    {
        // Default to accent-only the first time the user reaches this step
        // so the live preview matches what they're already seeing.
        if (string.IsNullOrEmpty(_selectedWallpaperKey))
        {
            _selectedWallpaperKey = WallpaperManager.AccentOnlyKey;
            WallpaperManager.Instance.SetWallpaper(WallpaperManager.AccentOnlyKey);
        }

        _wallpaperTiles.Clear();

        var wrap = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            ItemWidth = 168,
            ItemHeight = 132
        };

        _accentOnlyTile = BuildWallpaperTile(
            key: WallpaperManager.AccentOnlyKey,
            displayName: "Accent color",
            preview: BuildAccentPreview());
        wrap.Children.Add(_accentOnlyTile);

        foreach (var w in WallpaperManager.Instance.AvailableWallpapers)
        {
            var tile = BuildWallpaperTile(w.Key, w.DisplayName, BuildWallpaperPreview(w.Key));
            _wallpaperTiles[w.Key] = tile;
            wrap.Children.Add(tile);
        }

        var scroller = new DOSIScrollViewer
        {
            Content = wrap,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            MaxHeight = 280
        };

        return scroller;
    }

    private Border BuildWallpaperTile(string key, string displayName, Control preview)
    {
        var isSelected = string.Equals(_selectedWallpaperKey, key, StringComparison.OrdinalIgnoreCase);

        var label = new TextBlock
        {
            Text = displayName,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextSecondaryBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 140,
            Margin = new Thickness(0, 6, 0, 0)
        };

        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Children = { preview, label }
        };

        var tile = new Border
        {
            Width = 152,
            Height = 116,
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(10),
            Background = Brushes.Transparent,
            BorderBrush = isSelected
                ? new SolidColorBrush(Colors.White)
                : new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(isSelected ? 2 : 1),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Child = stack
        };

        tile.PointerPressed += (_, _) =>
        {
            if (string.Equals(_selectedWallpaperKey, key, StringComparison.OrdinalIgnoreCase)) return;
            _selectedWallpaperKey = key;
            WallpaperManager.Instance.SetWallpaper(key);
            // Update selection state in place so the scroll position is preserved.
            UpdateWallpaperTileSelection();
        };

        return tile;
    }

    private void UpdateWallpaperTileSelection()
    {
        void Apply(Border? t, bool sel)
        {
            if (t == null) return;
            t.BorderBrush = sel
                ? new SolidColorBrush(Colors.White)
                : new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
            t.BorderThickness = new Thickness(sel ? 2 : 1);
        }

        Apply(_accentOnlyTile,
            string.Equals(_selectedWallpaperKey, WallpaperManager.AccentOnlyKey, StringComparison.OrdinalIgnoreCase));
        foreach (var (k, t) in _wallpaperTiles)
            Apply(t, string.Equals(_selectedWallpaperKey, k, StringComparison.OrdinalIgnoreCase));
    }

    private Control BuildAccentPreview()
    {
        return new Border
        {
            Width = 132,
            Height = 78,
            CornerRadius = new CornerRadius(6),
            Background = Accents.AccentGradientBrush,
            HorizontalAlignment = HorizontalAlignment.Center
        };
    }

    private Control BuildWallpaperPreview(string key)
    {
        var bmp = WallpaperManager.Instance.LoadBitmap(key);
        if (bmp == null) return BuildAccentPreview();

        return new Border
        {
            Width = 132,
            Height = 78,
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Background = new ImageBrush(bmp)
            {
                Stretch = Stretch.UniformToFill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center
            },
            HorizontalAlignment = HorizontalAlignment.Center
        };
    }

    // =====================================================================
    // Step 6: Done
    // =====================================================================

    private DOSIUser? _createdUser;

    private bool CommitAccount()
    {
        var result = UserManager.CreateUser(
            _username,
            _password,
            out var user,
            displayName: _displayName,
            isAdministrator: true); // first user is admin

        if (result != UserCreationResult.Success || user == null)
        {
            ShowError(result switch
            {
                UserCreationResult.UsernameAlreadyExists => "That username is already taken.",
                UserCreationResult.InvalidUsername => "Invalid username.",
                UserCreationResult.InvalidPassword => "Invalid password.",
                _ => "Couldn't create the account. Please try again."
            });
            return false;
        }

        UserManager.SetUserAccent(user, _selectedAccent);

        // Persist the wallpaper choice. AccentOnlyKey is stored explicitly so
        // we can distinguish "user opted out" from "never picked one".
        if (!string.IsNullOrEmpty(_selectedWallpaperKey))
            UserManager.SetUserWallpaper(user, _selectedWallpaperKey);

        // Note: SystemCore.Settings.DefaultAccent is intentionally NOT overwritten here.
        // The system default (used by the boot/login screens and as the "deselected" accent
        // on LoginScreen) is independent from any user's personal accent.

        _createdUser = user;
        return true;
    }

    private Control BuildDoneStep()
    {
        var checkmark = new Border
        {
            Width = 96,
            Height = 96,
            CornerRadius = new CornerRadius(48),
            Background = Accents.AccentGradientBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = "\u2713",
                FontSize = 52,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Accents.TextOnAccent),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var wallpaperLine = string.IsNullOrEmpty(_selectedWallpaperKey) ||
                            string.Equals(_selectedWallpaperKey, WallpaperManager.AccentOnlyKey, StringComparison.OrdinalIgnoreCase)
            ? "the accent color"
            : (WallpaperManager.Instance.TryGetWallpaper(_selectedWallpaperKey!, out var w)
                ? $"the \u2018{w.DisplayName}\u2019 wallpaper"
                : "your wallpaper");

        var summary = new TextBlock
        {
            Text = $"Welcome, {_displayName}! Your account \u2018{_username}\u2019 is ready, the {AccentManager.GetAccentDisplayName(_selectedAccent)} accent has been applied, and your desktop is set to {wallpaperLine}.",
            FontSize = 13,
            Foreground = Accents.TextSecondaryBrush,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 440,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 24, 0, 0),
            Opacity = 0.9
        };

        return new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { checkmark, summary }
        };
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private void ShowError(string message)
    {
        _errorText.Text = message;
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

    private void OnAccentChanged(object? sender, EventArgs e)
    {
        _stepTitle.Foreground = Accents.TextPrimaryBrush;
        _stepSubtitle.Foreground = Accents.TextSecondaryBrush;
        // Re-tint every dot so the active pill picks up the new accent.
        RebuildStepDots(_currentStepIndex, _steps.Count);

        // Re-tint the Next button so it always tracks the live accent. The
        // Back button uses default DOSIButton styling and re-themes itself.
        _nextButton.Background = Accents.AccentPrimaryBrush;
        _nextButton.BackgroundHover = Accents.AccentSecondaryBrush;
        _nextButton.BackgroundPressed = Accents.AccentPrimaryBrush;
        _nextButton.Foreground = new SolidColorBrush(Accents.TextOnAccent);

        if (_avatarCircle != null) _avatarCircle.Fill = BuildAvatarBrush();
        if (_avatarInitial != null) _avatarInitial.Foreground = new SolidColorBrush(Accents.TextOnAccent);
    }

    private void PlayCardEntrance(int direction = 1)
    {
        const double duration = 320;
        const double slideDistance = 28;
        var startOffset = slideDistance * direction;

        _stepContent.Opacity = 0;
        var translate = new TranslateTransform(startOffset, 0);
        _stepContent.RenderTransform = translate;

        _entranceTimer?.Stop();
        _entranceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        var startTime = DateTime.UtcNow;

        _entranceTimer.Tick += (_, _) =>
        {
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            var t = Math.Clamp(elapsed / duration, 0d, 1d);
            var eased = 1 - Math.Pow(1 - t, 3);

            _stepContent.Opacity = eased;
            translate.X = startOffset * (1 - eased);

            if (t >= 1d)
            {
                _entranceTimer?.Stop();
                _entranceTimer = null;
                _stepContent.RenderTransform = null;
            }
        };
        _entranceTimer.Start();
    }
}

