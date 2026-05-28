using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DOSI.CORE;
using DOSI.CORE.AccentManagement;
using DOSI.CORE.UIComponents;
using DOSI.CORE.UIComponents.WindowManagement;
using DOSI.CORE.UserManagement;
using DOSI.CORE.WallpaperManagement;

namespace DAX.OSI.DefaultApplications;

/// <summary>
/// The DOSI settings application. A windowed app that uses
/// <see cref="DOSITabControl"/> to organise system, personalisation, and
/// per-user account settings.
/// </summary>
public class DOSISettingsScreen : DOSIWindow
{
    private static AccentManager Accents => AccentManager.Instance;

    private readonly DOSIUser? _user;

    public DOSISettingsScreen()
    {
        Title = "Settings";
        WindowWidth = 880;
        WindowHeight = 560;
        MinimumSize = new Size(640, 420);
        Icon = CreateAppIcon();

        _user = UserManager.CurrentUser;

        var tabs = new DOSITabControl
        {
            TabPlacement = DOSITabPlacement.Left
        };

        tabs.Items.Add(new DOSITabItem
        {
            Header = "Profile",
            Subtitle = "Account & password",
            Glyph = "\u25CF",
            ContentFactory = BuildProfileTab
        });

        tabs.Items.Add(new DOSITabItem
        {
            Header = "Personalization",
            Subtitle = "Accents & wallpaper",
            Glyph = "\u25C6",
            ContentFactory = BuildPersonalizationTab
        });

        tabs.Items.Add(new DOSITabItem
        {
            Header = "System",
            Subtitle = "Window & startup",
            Glyph = "\u25A0",
            ContentFactory = BuildSystemTab
        });

        tabs.Items.Add(new DOSITabItem
        {
            Header = "About",
            Subtitle = $"DOSI {SystemCore.Version}",
            Glyph = "\u24D8",
            ContentFactory = BuildAboutTab
        });

        Content = tabs;
    }

    // =====================================================================
    // Shared helpers
    // =====================================================================

    private static Border BuildSection(string title, string? subtitle, params Control[] children)
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 10
        };

        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            Foreground = Accents.TextPrimaryBrush
        });

        if (!string.IsNullOrEmpty(subtitle))
        {
            stack.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 12,
                Foreground = Accents.TextSecondaryBrush,
                Opacity = 0.85,
                Margin = new Thickness(0, -6, 0, 4)
            });
        }

        foreach (var c in children)
            stack.Children.Add(c);

        return new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18),
            Margin = new Thickness(0, 0, 0, 14),
            Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Child = stack
        };
    }

    private static Control BuildLabel(string text, double fontSize = 12, FontWeight weight = FontWeight.SemiBold) =>
        new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            FontWeight = weight,
            Foreground = Accents.TextPrimaryBrush,
            Margin = new Thickness(0, 4, 0, 4)
        };

    private static Control BuildHelp(string text) =>
        new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.85,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        };

    private static Control BuildStatusText(out TextBlock target)
    {
        target = new TextBlock
        {
            Text = string.Empty,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextSecondaryBrush,
            Margin = new Thickness(0, 6, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        return target;
    }

    private static Border BuildToggle(bool initial, Action<bool> onChanged)
    {
        var on = initial;

        var thumb = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = Brushes.White,
            HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2)
        };

        var track = new Border
        {
            Width = 44,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = on
                ? Accents.AccentPrimaryBrush
                : new SolidColorBrush(Color.FromArgb(120, 100, 100, 100)),
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
            Child = thumb
        };

        track.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            on = !on;
            thumb.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            track.Background = on
                ? Accents.AccentPrimaryBrush
                : new SolidColorBrush(Color.FromArgb(120, 100, 100, 100));
            onChanged(on);
        };

        return track;
    }

    private static Control BuildToggleRow(string title, string description, bool initial, Action<bool> onChanged)
    {
        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextPrimaryBrush
        };
        var descText = new TextBlock
        {
            Text = description,
            FontSize = 11,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.85,
            TextWrapping = TextWrapping.Wrap
        };

        var labelStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { titleText, descText }
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 6, 0, 6)
        };
        grid.Children.Add(labelStack); Grid.SetColumn(labelStack, 0);
        var toggle = BuildToggle(initial, onChanged);
        grid.Children.Add(toggle); Grid.SetColumn(toggle, 1);

        return grid;
    }

    private static DOSIScrollViewer WrapInScroller(Control content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        ShowScrollButtons = false
    };

    // =====================================================================
    // Profile tab
    // =====================================================================

    private Control BuildProfileTab()
    {
        var sections = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(24, 20, 24, 20)
        };

        if (_user == null)
        {
            sections.Children.Add(BuildSection(
                "Not signed in",
                "Sign in to manage your account.",
                BuildHelp("Profile settings require an active user session.")));
            return WrapInScroller(sections);
        }

        sections.Children.Add(BuildAccountSection());
        sections.Children.Add(BuildDisplayNameSection());
        sections.Children.Add(BuildPasswordSection());

        return WrapInScroller(sections);
    }

    private Border BuildAccountSection()
    {
        var avatar = new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(28),
            Background = Accents.AccentGradientBrush,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Text = (_user!.DisplayName.FirstOrDefault().ToString().ToUpperInvariant()),
                FontSize = 24,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Accents.TextOnAccent),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var displayName = new TextBlock
        {
            Text = _user!.DisplayName,
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            Foreground = Accents.TextPrimaryBrush
        };

        var username = new TextBlock
        {
            Text = "@" + _user.Username + (_user.IsAdministrator ? "  \u2022  Administrator" : string.Empty),
            FontSize = 12,
            Foreground = Accents.TextSecondaryBrush
        };

        var lastLogin = new TextBlock
        {
            Text = _user.LastLoginUtc.HasValue
                ? "Last login: " + _user.LastLoginUtc.Value.ToLocalTime().ToString("g")
                : "Last login: never",
            FontSize = 11,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.85
        };

        var info = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { displayName, username, lastLogin }
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*")
        };
        grid.Children.Add(avatar); Grid.SetColumn(avatar, 0);
        avatar.Margin = new Thickness(0, 0, 16, 0);
        grid.Children.Add(info); Grid.SetColumn(info, 1);

        return BuildSection("Account", null, grid);
    }

    private Border BuildDisplayNameSection()
    {
        var nameBox = new DOSITextBox
        {
            Text = _user!.DisplayName,
            FontSize = 13,
            Padding = new Thickness(10, 6),
            Height = 30,
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 220
        };

        var status = new TextBlock
        {
            Text = string.Empty,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextSecondaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };

        var saveBtn = new DOSIButton
        {
            Text = "Save",
            Padding = new Thickness(18, 6),
            CornerRadius = new CornerRadius(6)
        };
        saveBtn.Click += (_, _) =>
        {
            var trimmed = (nameBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                status.Text = "Display name cannot be empty.";
                status.Foreground = new SolidColorBrush(Color.FromRgb(232, 80, 80));
                return;
            }

            _user!.DisplayName = trimmed;
            if (UserManager.SaveUser(_user))
            {
                status.Text = "Saved.";
                status.Foreground = Accents.AccentPrimaryBrush;
            }
            else
            {
                status.Text = "Failed to save.";
                status.Foreground = new SolidColorBrush(Color.FromRgb(232, 80, 80));
            }
        };

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto")
        };
        row.Children.Add(nameBox); Grid.SetColumn(nameBox, 0);
        row.Children.Add(saveBtn); Grid.SetColumn(saveBtn, 1);
        saveBtn.Margin = new Thickness(10, 0, 0, 0);
        row.Children.Add(status); Grid.SetColumn(status, 2);

        return BuildSection(
            "Display Name",
            "How your name appears across DOSI. Your sign-in username cannot be changed.",
            row);
    }

    private Border BuildPasswordSection()
    {
        var currentBox = new DOSITextBox
        {
            PlaceholderText = "Current password",
            UsePasswordChar = true,
            FontSize = 13,
            Padding = new Thickness(10, 6),
            Height = 30,
            CornerRadius = new CornerRadius(6)
        };
        var newBox = new DOSITextBox
        {
            PlaceholderText = "New password (min. 4 chars)",
            UsePasswordChar = true,
            FontSize = 13,
            Padding = new Thickness(10, 6),
            Height = 30,
            CornerRadius = new CornerRadius(6)
        };
        var confirmBox = new DOSITextBox
        {
            PlaceholderText = "Confirm new password",
            UsePasswordChar = true,
            FontSize = 13,
            Padding = new Thickness(10, 6),
            Height = 30,
            CornerRadius = new CornerRadius(6)
        };

        var status = new TextBlock
        {
            Text = string.Empty,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextSecondaryBrush,
            Margin = new Thickness(0, 6, 0, 0)
        };

        var changeBtn = new DOSIButton
        {
            Text = "Change password",
            Padding = new Thickness(18, 6),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0)
        };
        changeBtn.Click += (_, _) =>
        {
            var currentPwd = currentBox.Text ?? string.Empty;
            var newPwd = newBox.Text ?? string.Empty;
            var confirmPwd = confirmBox.Text ?? string.Empty;

            if (!UserManager.ValidatePassword(_user!.Username, currentPwd))
            {
                status.Text = "Current password is incorrect.";
                status.Foreground = new SolidColorBrush(Color.FromRgb(232, 80, 80));
                return;
            }
            if (!UserManager.IsValidPassword(newPwd))
            {
                status.Text = "New password must be at least 4 characters.";
                status.Foreground = new SolidColorBrush(Color.FromRgb(232, 80, 80));
                return;
            }
            if (newPwd != confirmPwd)
            {
                status.Text = "New passwords do not match.";
                status.Foreground = new SolidColorBrush(Color.FromRgb(232, 80, 80));
                return;
            }

            if (UserManager.UpdatePassword(_user.Username, newPwd))
            {
                status.Text = "Password updated.";
                status.Foreground = Accents.AccentPrimaryBrush;
                currentBox.Text = string.Empty;
                newBox.Text = string.Empty;
                confirmBox.Text = string.Empty;
            }
            else
            {
                status.Text = "Failed to update password.";
                status.Foreground = new SolidColorBrush(Color.FromRgb(232, 80, 80));
            }
        };

        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 8,
            Children = { currentBox, newBox, confirmBox, changeBtn, status }
        };

        return BuildSection(
            "Password",
            "Change your sign-in password. You must enter your current password to confirm.",
            stack);
    }

    // =====================================================================
    // Personalization tab
    // =====================================================================

    private Control BuildPersonalizationTab()
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(24, 20, 24, 20)
        };

        stack.Children.Add(BuildAccentSection());
        stack.Children.Add(BuildWallpaperSection());
        stack.Children.Add(BuildWallpaperFitSection());
        stack.Children.Add(BuildWallpaperBlurSection());
        stack.Children.Add(BuildWindowOpacitySection());

        return WrapInScroller(stack);
    }

    /// <summary>
    /// Raised whenever the user toggles the desktop wallpaper-blur
    /// preference. <c>DesktopScreen</c> subscribes so the change applies
    /// live (animated cross-fade between the sharp and blurred variants)
    /// without requiring a sign-out. Login / sign-out / shutdown / setup
    /// screens ignore this event and always render the blurred variant.
    /// </summary>
    public static event EventHandler<bool>? WallpaperBlurChanged;

    private Border BuildWallpaperBlurSection()
    {
        var initial = _user == null || UserManager.GetUserWallpaperBlur(_user);
        var row = BuildToggleRow(
            "Blur wallpaper",
            "Soften the desktop wallpaper so windows and overlays read more clearly. Toggling cross-fades between the soft and sharp variants in real time. Affects the desktop only - the login, sign-out, shutdown, and setup screens always use the soft variant.",
            initial,
            on =>
            {
                if (_user != null) UserManager.SetUserWallpaperBlur(_user, on);
                WallpaperBlurChanged?.Invoke(this, on);
            });

        return BuildSection(
            "Wallpaper Blur",
            "Saved to your profile. Re-applied automatically every time you sign in.",
            row);
    }

    /// <summary>
    /// Builds the live window-transparency control. Reads the current user's
    /// preference, drives <see cref="DOSIWindow.WindowOpacity"/> live as the
    /// user drags, and persists the final value to the user's profile.
    /// </summary>
    private Border BuildWindowOpacitySection()
    {
        var initial = _user != null
            ? UserManager.GetUserWindowOpacity(_user)
            : UserManager.DefaultWindowOpacity;

        var valueText = new TextBlock
        {
            Text = FormatOpacityPercent(initial),
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Foreground = Accents.AccentPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 48,
            TextAlignment = TextAlignment.Right
        };

        var slider = new DOSISlider
        {
            Minimum = UserManager.MinWindowOpacity,
            Maximum = 1.0,
            Step = 0.01,
            Value = initial,
            ValueFormat = "0%",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 12, 4)
        };

        slider.ValueChanged += (_, v) =>
        {
            // Live preview: every active DOSIWindow listens for this change.
            DOSIWindow.WindowOpacity = v;
            valueText.Text = FormatOpacityPercent(v);
            if (_user != null) UserManager.SetUserWindowOpacity(_user, v);
        };

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 6, 0, 0)
        };
        row.Children.Add(slider); Grid.SetColumn(slider, 0);
        row.Children.Add(valueText); Grid.SetColumn(valueText, 1);

        return BuildSection(
            "Window Transparency",
            "Subtly let the desktop wallpaper bleed through every DOSI window. " +
            "Changes preview live and are saved to your profile.",
            row);
    }

    private static string FormatOpacityPercent(double v) =>
        ((int)Math.Round(v * 100)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";

    private Border BuildAccentSection()
    {
        var swatchPanel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 8,
            LineSpacing = 8
        };

        var current = UserManager.GetUserAccent(_user!) ?? Accents.CurrentAccent;
        var swatchBorders = new Dictionary<DOSIAccent, Border>();

        foreach (var accent in AccentManager.GetAvailableAccents())
        {
            var preview = BuildAccentSwatch(accent, accent == current, useLiveGradientForCurrent: true);
            swatchBorders[accent] = preview;
            preview.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                Accents.ApplyAccentAnimated(accent, TimeSpan.FromMilliseconds(450));
                if (_user != null) UserManager.SetUserAccent(_user, accent);

                // Update selection ring on all swatches.
                foreach (var (a, b) in swatchBorders)
                    UpdateAccentSwatchSelected(b, a == accent);
            };
            swatchPanel.Children.Add(preview);
        }

        // Re-paint selection state when the accent changes externally (e.g.
        // another tab, or a future signed-in account being switched).
        // The handler is scoped to the section's visual-tree lifetime - a
        // bare `Accents.AccentChanged += ...` here would never unsubscribe
        // and the static event would retain this settings-screen instance
        // (and every captured swatch border) for the life of the process.
        EventHandler accentRefresh = (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var (a, b) in swatchBorders)
                    UpdateAccentSwatchSelected(b, a == Accents.CurrentAccent);
            });
        };
        swatchPanel.AttachedToVisualTree += (_, _) => Accents.AccentChanged += accentRefresh;
        swatchPanel.DetachedFromVisualTree += (_, _) => Accents.AccentChanged -= accentRefresh;

        return BuildSection(
            "Accent Color",
            "Pick a color theme for your account. Saved to your profile and re-applied next time you sign in. " +
            "This is independent of the system accent shown on the login screen.",
            swatchPanel);
    }

    private static Border BuildAccentSwatch(DOSIAccent accent, bool selected, bool useLiveGradientForCurrent)
    {
        // For the user-accent picker we render the currently-applied accent
        // with the live gradient brush so it matches the rest of the UI.
        // For the system-accent picker we always use a flat preview color
        // because the system accent is independent of the live theme.
        var brush = useLiveGradientForCurrent && AccentManager.Instance.CurrentAccent == accent
            ? (IBrush)AccentManager.Instance.AccentGradientBrush
            : new SolidColorBrush(GetAccentPreviewColor(accent));

        var swatch = new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(10),
            Background = brush,
            BorderBrush = selected
                ? new SolidColorBrush(Colors.White)
                : new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
            BorderThickness = new Thickness(selected ? 2.5 : 1),
            Cursor = new Cursor(StandardCursorType.Hand),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0,
                OffsetY = 2,
                Blur = 8,
                Color = Color.FromArgb(80, 0, 0, 0)
            })
        };

        var label = new TextBlock
        {
            Text = AccentManager.GetAccentDisplayName(accent),
            FontSize = 9,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 4),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 50,
            TextAlignment = TextAlignment.Center
        };

        var grid = new Grid { Width = 56, Height = 56 };
        grid.Children.Add(label);

        swatch.Child = grid;

        return swatch;
    }

    private static void UpdateAccentSwatchSelected(Border swatch, bool selected)
    {
        swatch.BorderBrush = selected
            ? new SolidColorBrush(Colors.White)
            : new SolidColorBrush(Color.FromArgb(80, 255, 255, 255));
        swatch.BorderThickness = new Thickness(selected ? 2.5 : 1);
    }

    /// <summary>
    /// Returns a representative solid preview color for a given accent so the
    /// settings swatch grid can show every accent at once without applying them.
    /// Uses a curated palette that mirrors the dominant accent of each theme.
    /// </summary>
    private static Color GetAccentPreviewColor(DOSIAccent accent) => accent switch
    {
        DOSIAccent.DarkBlue => Color.FromRgb(0, 122, 204),
        DOSIAccent.DarkPurple => Color.FromRgb(138, 43, 226),
        DOSIAccent.DarkGreen => Color.FromRgb(16, 185, 129),
        DOSIAccent.DarkOrange => Color.FromRgb(255, 140, 0),
        DOSIAccent.DarkRed => Color.FromRgb(220, 50, 70),
        DOSIAccent.DarkTeal => Color.FromRgb(0, 188, 212),
        DOSIAccent.Light => Color.FromRgb(0, 120, 215),
        DOSIAccent.Midnight => Color.FromRgb(100, 100, 255),
        DOSIAccent.RoseGold => Color.FromRgb(183, 110, 121),
        DOSIAccent.Coral => Color.FromRgb(255, 127, 80),
        DOSIAccent.Lavender => Color.FromRgb(180, 150, 210),
        DOSIAccent.Mint => Color.FromRgb(152, 224, 186),
        DOSIAccent.Slate => Color.FromRgb(112, 128, 144),
        DOSIAccent.Copper => Color.FromRgb(184, 115, 51),
        DOSIAccent.Sapphire => Color.FromRgb(15, 82, 186),
        DOSIAccent.Emerald => Color.FromRgb(80, 200, 120),
        DOSIAccent.Ruby => Color.FromRgb(224, 17, 95),
        DOSIAccent.Amber => Color.FromRgb(255, 191, 0),
        DOSIAccent.Violet => Color.FromRgb(143, 0, 255),
        DOSIAccent.Crimson => Color.FromRgb(220, 20, 60),
        DOSIAccent.Forest => Color.FromRgb(34, 139, 34),
        DOSIAccent.Ocean => Color.FromRgb(0, 105, 148),
        DOSIAccent.Sunset => Color.FromRgb(253, 94, 83),
        DOSIAccent.Storm => Color.FromRgb(99, 110, 114),
        DOSIAccent.Bronze => Color.FromRgb(205, 127, 50),
        DOSIAccent.Indigo => Color.FromRgb(75, 0, 130),
        DOSIAccent.Magenta => Color.FromRgb(255, 0, 255),
        DOSIAccent.Olive => Color.FromRgb(128, 128, 0),
        DOSIAccent.Turquoise => Color.FromRgb(64, 224, 208),
        DOSIAccent.Cyan => Color.FromRgb(0, 255, 255),
        DOSIAccent.Aqua => Color.FromRgb(127, 255, 212),
        DOSIAccent.Periwinkle => Color.FromRgb(204, 204, 255),
        DOSIAccent.Plum => Color.FromRgb(142, 69, 133),
        DOSIAccent.Fuchsia => Color.FromRgb(255, 0, 255),
        DOSIAccent.Pink => Color.FromRgb(255, 105, 180),
        DOSIAccent.Peach => Color.FromRgb(255, 218, 185),
        DOSIAccent.Apricot => Color.FromRgb(251, 206, 177),
        DOSIAccent.Tangerine => Color.FromRgb(242, 133, 0),
        DOSIAccent.Goldenrod => Color.FromRgb(218, 165, 32),
        DOSIAccent.Lime => Color.FromRgb(191, 255, 0),
        DOSIAccent.Chartreuse => Color.FromRgb(127, 255, 0),
        DOSIAccent.Sage => Color.FromRgb(176, 195, 145),
        DOSIAccent.Pine => Color.FromRgb(1, 121, 111),
        DOSIAccent.Jade => Color.FromRgb(0, 168, 107),
        DOSIAccent.SeaGreen => Color.FromRgb(46, 139, 87),
        DOSIAccent.Cerulean => Color.FromRgb(0, 123, 167),
        DOSIAccent.SkyBlue => Color.FromRgb(135, 206, 235),
        DOSIAccent.Cobalt => Color.FromRgb(0, 71, 171),
        DOSIAccent.Navy => Color.FromRgb(0, 0, 128),
        DOSIAccent.Burgundy => Color.FromRgb(128, 0, 32),
        DOSIAccent.Maroon => Color.FromRgb(128, 0, 0),
        DOSIAccent.Wine => Color.FromRgb(114, 47, 55),
        DOSIAccent.Mocha => Color.FromRgb(150, 113, 90),
        DOSIAccent.Chocolate => Color.FromRgb(123, 63, 0),
        DOSIAccent.Sand => Color.FromRgb(194, 178, 128),
        DOSIAccent.Charcoal => Color.FromRgb(54, 69, 79),
        DOSIAccent.Steel => Color.FromRgb(70, 130, 180),
        DOSIAccent.Onyx => Color.FromRgb(53, 56, 57),
        _ => Color.FromRgb(120, 120, 120)
    };

    /// <summary>
    /// Renders the "Wallpaper Fit" section: a row of pill buttons letting
    /// the user pick how the wallpaper sizes into the screen (Fill / Fit /
    /// Stretch / Center / Tile). Each pill carries a tiny preview that
    /// mimics what that mode does to the bitmap. The picked mode is
    /// applied live (DOSIScreen reacts to WallpaperFitChanged) and
    /// persisted on the active user.
    /// </summary>
    private Border BuildWallpaperFitSection()
    {
        var wm = WallpaperManager.Instance;

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10
        };

        var pills = new List<(WallpaperFitMode Mode, Border Pill)>();

        var modes = new (WallpaperFitMode Mode, string Label, string Hint)[]
        {
            (WallpaperFitMode.Fill,    "Fill",    "Cover the whole screen, crop edges"),
            (WallpaperFitMode.Fit,     "Fit",     "Show the whole image, may letterbox"),
            (WallpaperFitMode.Stretch, "Stretch", "Stretch to exactly fill (may distort)"),
            (WallpaperFitMode.Center,  "Center",  "Native size, centred"),
            (WallpaperFitMode.Tile,    "Tile",    "Repeat at native size to cover")
        };

        foreach (var entry in modes)
        {
            var captured = entry;
            var isActive = wm.CurrentFitMode == captured.Mode;
            var pill = BuildFitModePill(captured.Mode, captured.Label, captured.Hint, isActive);
            pill.PointerReleased += (_, e) =>
            {
                e.Handled = true;
                wm.SetFitMode(captured.Mode);
                if (_user != null)
                    UserManager.SetUserWallpaperFit(_user, captured.Mode.ToString());
                // Refresh pill highlights.
                foreach (var (m, p) in pills)
                    StyleFitPill(p, m == captured.Mode);
            };
            pills.Add((captured.Mode, pill));
            row.Children.Add(pill);
        }

        return BuildSection(
            "Wallpaper Fit",
            "How your wallpaper sizes into the screen.",
            row);
    }

    private Border BuildFitModePill(WallpaperFitMode mode, string label, string hint, bool active)
    {
        // Mini schematic - a small "monitor" rectangle with a dashed area
        // representing how the photo lays out under this mode. Drawn with
        // primitives so it renders identically on every accent.
        var schematic = BuildFitSchematic(mode);

        var caption = new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0)
        };

        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { schematic, caption }
        };

        var pill = new Border
        {
            Width = 84,
            Height = 78,
            Padding = new Thickness(8, 6),
            CornerRadius = new CornerRadius(8),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = stack
        };
        ToolTip.SetTip(pill, hint);
        StyleFitPill(pill, active);
        // Subtle hover to confirm it's clickable.
        pill.PointerEntered += (_, _) => { if (pill.BorderThickness.Top < 2) pill.Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)); };
        pill.PointerExited  += (_, _) => { if (pill.BorderThickness.Top < 2) pill.Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)); };
        return pill;
    }

    private static void StyleFitPill(Border pill, bool active)
    {
        pill.Background = active
            ? Accents.AccentPrimaryBrush
            : new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
        pill.BorderBrush = active
            ? Accents.AccentPrimaryBrush
            : new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
        pill.BorderThickness = new Thickness(active ? 2 : 1);
        if (pill.Child is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is TextBlock t)
        {
            t.Foreground = active
                ? new SolidColorBrush(Accents.TextOnAccent)
                : Accents.TextPrimaryBrush;
        }
    }

    /// <summary>
    /// Tiny visual hint for what each fit mode does. A 56x36 "monitor"
    /// frame with an inner shape (or pattern) showing the photo's behaviour.
    /// Pure shapes - no bitmap dependency - so it stays sharp on any DPI.
    /// </summary>
    private static Border BuildFitSchematic(WallpaperFitMode mode)
    {
        var photoColor = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255));
        var monitorBg = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0));

        Control inner = mode switch
        {
            // Fill: photo covers the whole monitor, slight crop hint via inset
            WallpaperFitMode.Fill => new Border
            {
                Background = photoColor,
                Margin = new Thickness(-4) // overflows clip area to imply "crop"
            },
            // Fit: smaller centred photo with letterbox bands
            WallpaperFitMode.Fit => new Border
            {
                Background = photoColor,
                Margin = new Thickness(0, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            },
            // Stretch: full coverage, no margins (looks identical to Fill at
            // this preview size; that's accurate)
            WallpaperFitMode.Stretch => new Border
            {
                Background = photoColor,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            },
            // Center: small centred square
            WallpaperFitMode.Center => new Border
            {
                Background = photoColor,
                Width = 18, Height = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            // Tile: 3x2 grid of mini photos
            WallpaperFitMode.Tile => BuildTileSchematic(photoColor),
            _ => new Border()
        };

        return new Border
        {
            Width = 52,
            Height = 30,
            Background = monitorBg,
            CornerRadius = new CornerRadius(3),
            ClipToBounds = true,
            BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = inner
        };
    }

    private static Control BuildTileSchematic(IBrush photoColor)
    {
        var g = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*"),
            RowDefinitions = new RowDefinitions("*,*")
        };
        for (int r = 0; r < 2; r++)
        for (int c = 0; c < 3; c++)
        {
            var cell = new Border
            {
                Background = photoColor,
                Margin = new Thickness(1)
            };
            Grid.SetRow(cell, r); Grid.SetColumn(cell, c);
            g.Children.Add(cell);
        }
        return g;
    }

    private Border BuildWallpaperSection()
    {
        var wm = WallpaperManager.Instance;

        // Inner host so we can rebuild the tile grid in-place when the user
        // adds a custom wallpaper - no need to rebuild the whole section
        // header / hint text.
        var grid = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 12,
            LineSpacing = 12
        };

        void Rebuild()
        {
            grid.Children.Clear();
            var current = wm.CurrentWallpaperKey;
            var tiles = new Dictionary<string, Border>();

            // Accent-only tile
            var accentTile = BuildWallpaperTile(
                "Accent only",
                null,
                string.IsNullOrEmpty(current) || current == WallpaperManager.AccentOnlyKey);
            accentTile.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                wm.SetWallpaper(WallpaperManager.AccentOnlyKey);
                if (_user != null) UserManager.SetUserWallpaper(_user, WallpaperManager.AccentOnlyKey);
                UpdateWallpaperSelection(tiles, WallpaperManager.AccentOnlyKey);
            };
            tiles[WallpaperManager.AccentOnlyKey] = accentTile;
            grid.Children.Add(accentTile);

            foreach (var w in wm.AvailableWallpapers)
            {
                var captured = w;
                // Use the thumbnail cache, NOT LoadBitmap - LoadBitmap
                // returns the full-resolution desktop bitmap (potentially
                // tens of MB per image as a live GPU texture). With even a
                // handful of custom wallpapers that pinned the compositor's
                // texture budget hard enough to lag the whole UI - which is
                // exactly what users hit after picking phone-shot photos.
                var bmp = wm.LoadThumbnail(captured.Key);
                var tile = BuildWallpaperTile(
                    captured.DisplayName,
                    bmp,
                    string.Equals(captured.Key, current, StringComparison.OrdinalIgnoreCase));
                tile.PointerPressed += (_, e) =>
                {
                    e.Handled = true;
                    wm.SetWallpaper(captured.Key);
                    if (_user != null) UserManager.SetUserWallpaper(_user, captured.Key);
                    UpdateWallpaperSelection(tiles, captured.Key);
                };
                tiles[captured.Key] = tile;
                grid.Children.Add(tile);
            }

            // "Add..." tile - opens the OS file picker, accepts every common
            // image format, registers the picked file as a custom wallpaper,
            // then activates it. The same code path the shipped wallpapers
            // use takes care of downscale + blur-bake + caching.
            grid.Children.Add(BuildAddWallpaperTile(tiles));
        }

        // Re-render whenever the catalog grows (custom wallpaper added) so
        // the new tile appears without the user having to leave + re-enter
        // the settings screen.
        EventHandler onCatalogChanged = (_, _) =>
            Dispatcher.UIThread.Post(Rebuild);
        grid.AttachedToVisualTree += (_, _) => wm.WallpapersChanged += onCatalogChanged;
        grid.DetachedFromVisualTree += (_, _) => wm.WallpapersChanged -= onCatalogChanged;

        Rebuild();

        return BuildSection(
            "Wallpaper",
            "Choose a desktop wallpaper, or pick any image file from your disk.",
            grid);
    }

    /// <summary>
    /// Builds the "+ Add..." picker tile. Visually matches the wallpaper
    /// tiles next to it (same outer dimensions, same caption styling) but
    /// shows a centred plus glyph instead of an image preview.
    /// </summary>
    private Border BuildAddWallpaperTile(Dictionary<string, Border> tiles)
    {
        var plus = new TextBlock
        {
            Text = "+",
            FontSize = 32,
            FontWeight = FontWeight.Light,
            Foreground = Accents.TextPrimaryBrush,
            Opacity = 0.7,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var imageHost = new Border
        {
            Width = 140,
            Height = 84,
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            Child = plus
        };
        var caption = new TextBlock
        {
            Text = "Add image...",
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextPrimaryBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
            TextAlignment = TextAlignment.Center
        };
        var tile = new Border
        {
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(10),
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Children = { imageHost, caption }
            }
        };
        tile.PointerPressed += async (_, e) =>
        {
            e.Handled = true;
            await PickAndApplyWallpaperAsync(tiles);
        };
        return tile;
    }

    /// <summary>
    /// Opens the DOSI file explorer in picker mode (sandboxed to the user's
    /// home folder, filtered to common image formats), registers the chosen
    /// file with the wallpaper manager, persists it to the active user, and
    /// switches to it. The catalog-changed event fires in
    /// <c>RegisterCustomWallpaper</c> which causes the settings grid to
    /// rebuild and pick up the new tile.
    /// </summary>
    private System.Threading.Tasks.Task PickAndApplyWallpaperAsync(Dictionary<string, Border> tiles)
    {
        var explorer = new DOSIFileExplorer();
        explorer.EnablePickerMode(
            prompt: "Choose a wallpaper image",
            extensions: new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tiff", ".tif" },
            onPicked: path =>
            {
                var wm = WallpaperManager.Instance;
                var key = wm.RegisterCustomWallpaper(path);
                if (key == null) return;

                wm.SetWallpaper(key);
                if (_user != null) UserManager.SetUserWallpaper(_user, key);
                // The grid Rebuild triggered by WallpapersChanged refreshes
                // the selection state; nothing else to do here.
            });

        DOSI.CORE.UIComponents.WindowManagement.WindowManager.Instance?.OpenWindow(explorer);
        return System.Threading.Tasks.Task.CompletedTask;
    }

    private static Border BuildWallpaperTile(string label, Bitmap? bitmap, bool selected)
    {
        Control inner;
        if (bitmap != null)
        {
            inner = new Image
            {
                Source = bitmap,
                Stretch = Stretch.UniformToFill
            };
        }
        else
        {
            // The accent-only tile must follow the live accent gradient so it
            // animates alongside the rest of the OS when the user picks a new
            // accent. Subscribe / unsubscribe with the visual tree to avoid
            // leaking the handler when the settings window closes.
            var accentBorder = new Border
            {
                Background = Accents.DesktopBackgroundBrush,
                Child = new TextBlock
                {
                    Text = "Accent",
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = 0.9
                }
            };

            EventHandler onAccent = (_, _) =>
                Dispatcher.UIThread.Post(() => accentBorder.Background = Accents.DesktopBackgroundBrush);

            accentBorder.AttachedToVisualTree += (_, _) => Accents.AccentChanged += onAccent;
            accentBorder.DetachedFromVisualTree += (_, _) => Accents.AccentChanged -= onAccent;

            inner = accentBorder;
        }

        var imageHost = new Border
        {
            Width = 140,
            Height = 84,
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Child = inner
        };

        var captionText = new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextPrimaryBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 140,
            TextAlignment = TextAlignment.Center
        };

        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 0,
            Children = { imageHost, captionText }
        };

        return new Border
        {
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(10),
            Background = Brushes.Transparent,
            BorderBrush = selected
                ? Accents.AccentPrimaryBrush
                : new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
            BorderThickness = new Thickness(selected ? 2 : 1),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = stack
        };
    }

    private static void UpdateWallpaperSelection(Dictionary<string, Border> tiles, string activeKey)
    {
        foreach (var (key, tile) in tiles)
        {
            var sel = string.Equals(key, activeKey, StringComparison.OrdinalIgnoreCase);
            tile.BorderBrush = sel
                ? Accents.AccentPrimaryBrush
                : new SolidColorBrush(Color.FromArgb(50, 255, 255, 255));
            tile.BorderThickness = new Thickness(sel ? 2 : 1);
        }
    }

    // =====================================================================
    // System tab
    // =====================================================================

    private Border BuildSystemAccentSection()
    {
        var swatchPanel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 8,
            LineSpacing = 8
        };

        var current = SystemCore.Settings.DefaultAccent;
        var swatchBorders = new Dictionary<DOSIAccent, Border>();

        foreach (var accent in AccentManager.GetAvailableAccents())
        {
            var captured = accent;
            var preview = BuildAccentSwatch(captured, captured == current, useLiveGradientForCurrent: false);
            swatchBorders[captured] = preview;
            preview.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                SystemCore.Settings.DefaultAccent = captured;
                SystemCore.SaveSettings();

                foreach (var (a, b) in swatchBorders)
                    UpdateAccentSwatchSelected(b, a == captured);
            };
            swatchPanel.Children.Add(preview);
        }

        return BuildSection(
            "System Accent",
            "The accent color used by the boot screen, login screen, and any " +
            "context where no user is signed in. Independent of your personal accent.",
            swatchPanel);
    }

    private Control BuildSystemTab()
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(24, 20, 24, 20)
        };

        stack.Children.Add(BuildSystemAccentSection());

        var startupRow = BuildToggleRow(
            "Launch in fullscreen",
            "When enabled, DAX.OSI starts in fullscreen mode. Disable to launch in a resizable desktop window.",
            SystemCore.Settings.Fullscreen,
            on =>
            {
                SystemCore.Settings.Fullscreen = on;
                SystemCore.SaveSettings();
            });

        stack.Children.Add(BuildSection(
            "Startup",
            "Settings applied the next time DAX.OSI starts.",
            startupRow));

        // Settings file location
        var pathText = new TextBlock
        {
            Text = SystemCore.SettingsFilePath,
            FontSize = 11,
            Foreground = Accents.TextSecondaryBrush,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas")
        };

        stack.Children.Add(BuildSection(
            "Settings File",
            "DOSI persists system-wide settings to disk in JSON format.",
            pathText));

        var usersText = new TextBlock
        {
            Text = UserManager.UsersRootPath,
            FontSize = 11,
            Foreground = Accents.TextSecondaryBrush,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas")
        };

        stack.Children.Add(BuildSection(
            "User Data Folder",
            "Each user account, their files, and their preferences live here.",
            usersText));

        return WrapInScroller(stack);
    }

    // =====================================================================
    // About tab
    // =====================================================================

    private Control BuildAboutTab()
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(24, 20, 24, 20),
            Spacing = 8
        };

        var hero = new Border
        {
            Width = 80,
            Height = 80,
            CornerRadius = new CornerRadius(16),
            Background = Accents.AccentGradientBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 16),
            Child = new TextBlock
            {
                Text = "OS",
                FontSize = 26,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Accents.TextOnAccent),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        stack.Children.Add(hero);

        stack.Children.Add(new TextBlock
        {
            Text = "DAX.OSI",
            FontSize = 24,
            FontWeight = FontWeight.Bold,
            Foreground = Accents.TextPrimaryBrush
        });

        stack.Children.Add(new TextBlock
        {
            Text = $"{SystemCore.Name}  \u2022  v{SystemCore.Version}",
            FontSize = 12,
            Foreground = Accents.TextSecondaryBrush
        });

        stack.Children.Add(new TextBlock
        {
            Text =
                "DAX.OSI is a virtual operating system built entirely with Avalonia and a custom UI " +
                "kit (DOSI). All controls - windows, buttons, scrollbars, dialogs, code editors - are " +
                "drawn from scratch and themed by the central accent system.",
            FontSize = 12,
            Foreground = Accents.TextPrimaryBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 16, 0, 0),
            MaxWidth = 540
        });

        return WrapInScroller(stack);
    }

    // =====================================================================
    // App icon (titlebar)
    // =====================================================================

    private static Control CreateAppIcon()
    {
        var bg = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(3),
            Background = Accents.AccentGradientBrush
        };

        var gear = new TextBlock
        {
            Text = "\u2699", // gear
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Accents.TextOnAccent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var grid = new Grid { Width = 16, Height = 16 };
        grid.Children.Add(bg);
        grid.Children.Add(gear);
        return grid;
    }
}
