using Avalonia.Media;
using Avalonia.Threading;

namespace DOSI.CORE.AccentManagement;

/// <summary>
/// Manages color accents and accents for the DOSI virtual operating system.
/// Provides centralized theming support for all UI components.
/// </summary>
public sealed class AccentManager
{
    private static AccentManager? _instance;
    public static AccentManager Instance => _instance ??= new AccentManager();

    public DOSIAccent CurrentAccent { get; private set; }

    public event EventHandler? AccentChanged;

    #region Color Properties

    public Color AccentPrimary { get; private set; }
    public Color AccentSecondary { get; private set; }
    public Color AccentTertiary { get; private set; }

    public Color WindowBackground { get; private set; }
    public Color WindowBorderFocused { get; private set; }
    public Color WindowBorderUnfocused { get; private set; }
    public Color WindowChrome { get; private set; }
    public Color WindowChromeUnfocused { get; private set; }
    public Color WindowContent { get; private set; }

    public Color TextPrimary { get; private set; }
    public Color TextSecondary { get; private set; }
    public Color TextDisabled { get; private set; }
    public Color TextOnAccent { get; private set; }

    public Color ControlBackground { get; private set; }
    public Color ControlBackgroundHover { get; private set; }
    public Color ControlBackgroundPressed { get; private set; }
    public Color ControlBorder { get; private set; }

    public Color ButtonBackground { get; private set; }
    public Color ButtonBackgroundHover { get; private set; }
    public Color ButtonBackgroundPressed { get; private set; }
    public Color CloseButtonHover { get; private set; }

    public Color ListBoxBackground { get; private set; }
    public Color ListBoxItemHover { get; private set; }
    public Color ListBoxItemSelected { get; private set; }
    public Color ListBoxItemSelectedUnfocused { get; private set; }

    public Color DesktopBackground1 { get; private set; }
    public Color DesktopBackground2 { get; private set; }
    public Color DesktopBackground3 { get; private set; }

    public Color ShadowColor { get; private set; }

    #endregion

    #region Brush Properties

    /// <summary>
    /// Global multiplier (0.5 - 1.0) applied to the alpha channel of every
    /// "container-style" brush returned by this manager (window background,
    /// chrome, content, control, button, listbox). Text and accent brushes
    /// are NOT affected so foreground content stays crisp and legible.
    /// Setting this fires <see cref="AccentChanged"/> so every subscribed
    /// control repaints with the new translucency in a single global pass.
    /// </summary>
    public double WindowOpacity
    {
        get => _windowOpacity;
        set
        {
            var clamped = Math.Clamp(value, 0.5, 1.0);
            if (Math.Abs(_windowOpacity - clamped) < double.Epsilon) return;
            _windowOpacity = clamped;
            // Mutate the cached brushes in place so every Border/TextBlock
            // already bound to WindowChromeBrush / WindowContentBrush /
            // etc. repaints automatically. We DO NOT fire AccentChanged
            // here - that signal means "the accent palette identity
            // changed", and consumers like DOSITabControl interpret it as
            // a cue to rebuild their cached tab bodies. Rebuilding mid-
            // drag (e.g. the transparency slider on the Settings User
            // tab) destroys the slider that's currently capturing the
            // pointer, so the drag scrolls the parent ScrollViewer back
            // to the top instead of moving the thumb. The brush mutation
            // alone is sufficient for live transparency preview.
            RefreshCachedBrushes();
        }
    }
    private double _windowOpacity = 1.0;

    private Color WithWindowOpacity(Color c) =>
        _windowOpacity >= 1.0
            ? c
            : Color.FromArgb((byte)(c.A * _windowOpacity), c.R, c.G, c.B);

    public SolidColorBrush AccentPrimaryBrush => _accentPrimaryBrush ??= new(AccentPrimary);
    public SolidColorBrush AccentSecondaryBrush => _accentSecondaryBrush ??= new(AccentSecondary);
    public SolidColorBrush WindowBackgroundBrush => _windowBackgroundBrush ??= new(WithWindowOpacity(WindowBackground));
    public SolidColorBrush WindowBorderFocusedBrush => _windowBorderFocusedBrush ??= new(WindowBorderFocused);
    public SolidColorBrush WindowBorderUnfocusedBrush => _windowBorderUnfocusedBrush ??= new(WindowBorderUnfocused);
    public SolidColorBrush WindowChromeBrush => _windowChromeBrush ??= new(WithWindowOpacity(WindowChrome));
    public SolidColorBrush WindowChromeUnfocusedBrush => _windowChromeUnfocusedBrush ??= new(WithWindowOpacity(WindowChromeUnfocused));
    public SolidColorBrush WindowContentBrush => _windowContentBrush ??= new(WithWindowOpacity(WindowContent));
    public SolidColorBrush TextPrimaryBrush => _textPrimaryBrush ??= new(TextPrimary);
    public SolidColorBrush TextSecondaryBrush => _textSecondaryBrush ??= new(TextSecondary);
    public SolidColorBrush TextDisabledBrush => _textDisabledBrush ??= new(TextDisabled);
    public SolidColorBrush ControlBackgroundBrush => _controlBackgroundBrush ??= new(WithWindowOpacity(ControlBackground));
    public SolidColorBrush ControlBackgroundHoverBrush => _controlBackgroundHoverBrush ??= new(WithWindowOpacity(ControlBackgroundHover));
    public SolidColorBrush ControlBackgroundPressedBrush => _controlBackgroundPressedBrush ??= new(WithWindowOpacity(ControlBackgroundPressed));
    public SolidColorBrush ButtonBackgroundBrush => _buttonBackgroundBrush ??= new(WithWindowOpacity(ButtonBackground));
    public SolidColorBrush ButtonBackgroundHoverBrush => _buttonBackgroundHoverBrush ??= new(WithWindowOpacity(ButtonBackgroundHover));
    public SolidColorBrush ButtonBackgroundPressedBrush => _buttonBackgroundPressedBrush ??= new(WithWindowOpacity(ButtonBackgroundPressed));
    public SolidColorBrush CloseButtonHoverBrush => _closeButtonHoverBrush ??= new(CloseButtonHover);
    public SolidColorBrush ListBoxBackgroundBrush => _listBoxBackgroundBrush ??= new(WithWindowOpacity(ListBoxBackground));
    public SolidColorBrush ListBoxItemHoverBrush => _listBoxItemHoverBrush ??= new(WithWindowOpacity(ListBoxItemHover));
    public SolidColorBrush ListBoxItemSelectedBrush => _listBoxItemSelectedBrush ??= new(ListBoxItemSelected);

    // Cached brush instances. Each public *Brush getter returns the SAME
    // instance for the life of the process; on accent / opacity change we
    // mutate the brush's Color field instead of allocating a new one. Avalonia
    // raises invalidation automatically when SolidColorBrush.Color changes,
    // so every consumer that previously assigned the brush as a Background /
    // Foreground keeps repainting correctly without any code changes.
    //
    // Why this matters: ApplyAccentAnimated fires AccentChanged ~28 times
    // over a 450 ms transition, and each subscribed control's OnAccentChanged
    // typically reads a handful of these getters. Caching turns ~hundreds of
    // throwaway SolidColorBrush allocations per animation into zero.
    private SolidColorBrush? _accentPrimaryBrush;
    private SolidColorBrush? _accentSecondaryBrush;
    private SolidColorBrush? _windowBackgroundBrush;
    private SolidColorBrush? _windowBorderFocusedBrush;
    private SolidColorBrush? _windowBorderUnfocusedBrush;
    private SolidColorBrush? _windowChromeBrush;
    private SolidColorBrush? _windowChromeUnfocusedBrush;
    private SolidColorBrush? _windowContentBrush;
    private SolidColorBrush? _textPrimaryBrush;
    private SolidColorBrush? _textSecondaryBrush;
    private SolidColorBrush? _textDisabledBrush;
    private SolidColorBrush? _controlBackgroundBrush;
    private SolidColorBrush? _controlBackgroundHoverBrush;
    private SolidColorBrush? _controlBackgroundPressedBrush;
    private SolidColorBrush? _buttonBackgroundBrush;
    private SolidColorBrush? _buttonBackgroundHoverBrush;
    private SolidColorBrush? _buttonBackgroundPressedBrush;
    private SolidColorBrush? _closeButtonHoverBrush;
    private SolidColorBrush? _listBoxBackgroundBrush;
    private SolidColorBrush? _listBoxItemHoverBrush;
    private SolidColorBrush? _listBoxItemSelectedBrush;

    /// <summary>
    /// Pushes the current Color values onto every cached SolidColorBrush so
    /// already-bound consumers (Background = TextPrimaryBrush, etc.) repaint
    /// with the live palette. Cheap - just up to ~21 Color assignments, no
    /// allocations.
    /// </summary>
    private void RefreshCachedBrushes()
    {
        if (_accentPrimaryBrush != null) _accentPrimaryBrush.Color = AccentPrimary;
        if (_accentSecondaryBrush != null) _accentSecondaryBrush.Color = AccentSecondary;
        if (_windowBackgroundBrush != null) _windowBackgroundBrush.Color = WithWindowOpacity(WindowBackground);
        if (_windowBorderFocusedBrush != null) _windowBorderFocusedBrush.Color = WindowBorderFocused;
        if (_windowBorderUnfocusedBrush != null) _windowBorderUnfocusedBrush.Color = WindowBorderUnfocused;
        if (_windowChromeBrush != null) _windowChromeBrush.Color = WithWindowOpacity(WindowChrome);
        if (_windowChromeUnfocusedBrush != null) _windowChromeUnfocusedBrush.Color = WithWindowOpacity(WindowChromeUnfocused);
        if (_windowContentBrush != null) _windowContentBrush.Color = WithWindowOpacity(WindowContent);
        if (_textPrimaryBrush != null) _textPrimaryBrush.Color = TextPrimary;
        if (_textSecondaryBrush != null) _textSecondaryBrush.Color = TextSecondary;
        if (_textDisabledBrush != null) _textDisabledBrush.Color = TextDisabled;
        if (_controlBackgroundBrush != null) _controlBackgroundBrush.Color = WithWindowOpacity(ControlBackground);
        if (_controlBackgroundHoverBrush != null) _controlBackgroundHoverBrush.Color = WithWindowOpacity(ControlBackgroundHover);
        if (_controlBackgroundPressedBrush != null) _controlBackgroundPressedBrush.Color = WithWindowOpacity(ControlBackgroundPressed);
        if (_buttonBackgroundBrush != null) _buttonBackgroundBrush.Color = WithWindowOpacity(ButtonBackground);
        if (_buttonBackgroundHoverBrush != null) _buttonBackgroundHoverBrush.Color = WithWindowOpacity(ButtonBackgroundHover);
        if (_buttonBackgroundPressedBrush != null) _buttonBackgroundPressedBrush.Color = WithWindowOpacity(ButtonBackgroundPressed);
        if (_closeButtonHoverBrush != null) _closeButtonHoverBrush.Color = CloseButtonHover;
        if (_listBoxBackgroundBrush != null) _listBoxBackgroundBrush.Color = WithWindowOpacity(ListBoxBackground);
        if (_listBoxItemHoverBrush != null) _listBoxItemHoverBrush.Color = WithWindowOpacity(ListBoxItemHover);
        if (_listBoxItemSelectedBrush != null) _listBoxItemSelectedBrush.Color = ListBoxItemSelected;

        // Gradient brushes are cached too - mutate their stops in place so
        // any pre-bound DesktopBackgroundBrush / AccentGradientBrush keeps
        // tracking the live palette.
        RefreshCachedGradients();
    }

    /// <summary>
    /// Radial gradient for desktop - bright center fading to dark edges (vignette effect).
    /// Cached and mutated in place on accent change (see <see cref="RefreshCachedBrushes"/>).
    /// </summary>
    public RadialGradientBrush DesktopBackgroundBrush
    {
        get
        {
            if (_desktopBackgroundBrush != null) return _desktopBackgroundBrush;

            _desktopBackgroundBrush = new RadialGradientBrush
            {
                Center = new Avalonia.RelativePoint(0.5, 0.5, Avalonia.RelativeUnit.Relative),
                GradientOrigin = new Avalonia.RelativePoint(0.5, 0.5, Avalonia.RelativeUnit.Relative),
                RadiusX = new Avalonia.RelativeScalar(1.0, Avalonia.RelativeUnit.Relative),
                RadiusY = new Avalonia.RelativeScalar(1.0, Avalonia.RelativeUnit.Relative),
                GradientStops =
                {
                    new(BrightenedDesktopCenter(), 0),
                    new(DesktopBackground2, 0.3),
                    new(DesktopBackground3, 0.6),
                    new(DesktopBackground1, 1)
                }
            };
            return _desktopBackgroundBrush;
        }
    }

    public LinearGradientBrush AccentGradientBrush
    {
        get
        {
            if (_accentGradientBrush != null) return _accentGradientBrush;
            _accentGradientBrush = CreateGradient(AccentPrimary, AccentSecondary);
            return _accentGradientBrush;
        }
    }

    private RadialGradientBrush? _desktopBackgroundBrush;
    private LinearGradientBrush? _accentGradientBrush;

    private Color BrightenedDesktopCenter() => Color.FromRgb(
        (byte)Math.Min(255, DesktopBackground2.R + 35),
        (byte)Math.Min(255, DesktopBackground2.G + 35),
        (byte)Math.Min(255, DesktopBackground2.B + 35));

    /// <summary>
    /// Mutates the cached desktop / accent gradient brushes' GradientStops in
    /// place so already-bound consumers repaint with the live palette without
    /// the brush being re-allocated.
    /// </summary>
    private void RefreshCachedGradients()
    {
        if (_desktopBackgroundBrush != null && _desktopBackgroundBrush.GradientStops.Count >= 4)
        {
            _desktopBackgroundBrush.GradientStops[0].Color = BrightenedDesktopCenter();
            _desktopBackgroundBrush.GradientStops[1].Color = DesktopBackground2;
            _desktopBackgroundBrush.GradientStops[2].Color = DesktopBackground3;
            _desktopBackgroundBrush.GradientStops[3].Color = DesktopBackground1;
        }
        if (_accentGradientBrush != null && _accentGradientBrush.GradientStops.Count >= 2)
        {
            _accentGradientBrush.GradientStops[0].Color = AccentPrimary;
            _accentGradientBrush.GradientStops[^1].Color = AccentSecondary;
        }
    }

    #endregion

    private AccentManager()
    {
        // accent will be applied after SystemCore.Initialize() is called
        // Default to DarkBlue temporarily until settings are loaded
        ApplyAccent(DOSIAccent.DarkBlue);
    }

    /// <summary>
    /// Initializes the accent manager with the accent from system settings.
    /// Should be called after SystemCore.Initialize().
    /// </summary>
    public void InitializeFromSettings()
    {
        ApplyAccent(SystemCore.Settings.DefaultAccent);
    }

    public void ApplyAccent(DOSIAccent accent)
    {
        CurrentAccent = accent;
        var t = GetAccentColors(accent);
        var isDark = accent != DOSIAccent.Light;

        AccentPrimary = t.Accent;
        AccentSecondary = t.AccentDark;
        AccentTertiary = WithAlpha(t.Accent, 80);

        // Auto-tint window colors with the accent for a cohesive look
        var tinted = CreateAccentTintedWindowColors(t.Accent, GetBaseLightness(accent), isDark);
        WindowBackground = tinted.WinBg;
        WindowBorderFocused = SoftenColor(t.Accent, 0.45); // More muted accent for softer border
        WindowBorderUnfocused = TintWithAccent(t.WinBorderUnfocused, t.Accent, 0.35); // Strong accent tint for unfocused
        WindowChrome = tinted.Chrome;
        WindowChromeUnfocused = tinted.ChromeUnfocused;
        WindowContent = tinted.Content;

        TextPrimary = t.TextPrimary;
        // Secondary text used to be blended TOWARD the accent which made
        // labels like "Account & password" or "Window & startup" collapse
        // into the surrounding chrome on saturated accents (DarkPurple,
        // DarkRed, etc.). Lift it toward white on dark accents so it stays
        // readable everywhere; on Light accent we still apply a faint accent
        // tint because the surface is already bright.
        TextSecondary = isDark
            ? Color.FromRgb(
                (byte)Math.Min(255, t.TextSecondary.R + 45),
                (byte)Math.Min(255, t.TextSecondary.G + 45),
                (byte)Math.Min(255, t.TextSecondary.B + 45))
            : TintWithAccent(t.TextSecondary, t.Accent, 0.05);
        TextDisabled = t.TextDisabled;
        TextOnAccent = t.TextOnAccent;

        ControlBackground = TintWithAccent(t.CtrlBg, t.Accent, 0.25);
        ControlBackgroundHover = TintWithAccent(t.CtrlHover, t.Accent, 0.30);
        ControlBackgroundPressed = TintWithAccent(t.CtrlPressed, t.Accent, 0.25);
        ControlBorder = TintWithAccent(t.CtrlBorder, t.Accent, 0.30);

        ButtonBackground = TintWithAccent(t.BtnBg, t.Accent, 0.1);
        ButtonBackgroundHover = tinted.BtnHover;
        ButtonBackgroundPressed = TintWithAccent(t.BtnPressed, t.Accent, 0.1);
        CloseButtonHover = Rgb(232, 17, 35);

        ListBoxBackground = TintWithAccent(t.ListBg, t.Accent, 0.08);
        ListBoxItemHover = TintWithAccent(t.ListHover, t.Accent, 0.12);
        ListBoxItemSelected = t.Accent;
        ListBoxItemSelectedUnfocused = TintWithAccent(t.ListSelectedUnfocused, t.Accent, 0.15);

        DesktopBackground1 = t.Desktop1;
        DesktopBackground2 = t.Desktop2;
        DesktopBackground3 = t.Desktop3;

        ShadowColor = t.Shadow;

        RefreshCachedBrushes();

        if (!_suppressAccentChanged)
            AccentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Gets the base lightness value for a accent's window colors.
    /// </summary>
    private static int GetBaseLightness(DOSIAccent accent) => accent switch
    {
        DOSIAccent.Midnight => 12,
        DOSIAccent.DarkBlue => 22,
        DOSIAccent.DarkPurple => 28,
        DOSIAccent.DarkGreen => 25,
        DOSIAccent.DarkOrange => 32,
        DOSIAccent.DarkRed => 30,
        DOSIAccent.DarkTeal => 25,
        DOSIAccent.Light => 240,
        _ => 25
    };

    /// <summary>
    /// Sets a custom accent color and re-tints all UI elements accordingly.
    /// </summary>
    public void SetCustomAccent(Color primary, Color secondary)
    {
        AccentPrimary = primary;
        AccentSecondary = secondary;
        AccentTertiary = WithAlpha(primary, 80);
        WindowBorderFocused = primary;
        ListBoxItemSelected = primary;

        // Re-tint window colors with the new accent
        var isDark = CurrentAccent != DOSIAccent.Light;
        var tinted = CreateAccentTintedWindowColors(primary, GetBaseLightness(CurrentAccent), isDark);
        WindowBackground = tinted.WinBg;
        WindowChrome = tinted.Chrome;
        WindowChromeUnfocused = tinted.ChromeUnfocused;
        WindowContent = tinted.Content;
        ButtonBackgroundHover = tinted.BtnHover;

        RefreshCachedBrushes();

        AccentChanged?.Invoke(this, EventArgs.Empty);
    }

    public static IEnumerable<DOSIAccent> GetAvailableAccents() => Enum.GetValues<DOSIAccent>();

    public static string GetAccentDisplayName(DOSIAccent accent) => accent switch
    {
        DOSIAccent.DarkBlue => "Dark Blue",
        DOSIAccent.DarkPurple => "Dark Purple",
        DOSIAccent.DarkGreen => "Dark Green",
        DOSIAccent.DarkOrange => "Dark Orange",
        DOSIAccent.DarkRed => "Dark Red",
        DOSIAccent.DarkTeal => "Dark Teal",
        DOSIAccent.Light => "Light",
        DOSIAccent.Midnight => "Midnight",
        DOSIAccent.RoseGold => "Rose Gold",
        DOSIAccent.Coral => "Coral",
        DOSIAccent.Lavender => "Lavender",
        DOSIAccent.Mint => "Mint",
        DOSIAccent.Slate => "Slate",
        DOSIAccent.Copper => "Copper",
        DOSIAccent.Sapphire => "Sapphire",
        DOSIAccent.Emerald => "Emerald",
        DOSIAccent.Ruby => "Ruby",
        DOSIAccent.Amber => "Amber",
        DOSIAccent.Violet => "Violet",
        DOSIAccent.Crimson => "Crimson",
        DOSIAccent.Forest => "Forest",
        DOSIAccent.Ocean => "Ocean",
        DOSIAccent.Sunset => "Sunset",
        DOSIAccent.Storm => "Storm",
        DOSIAccent.Bronze => "Bronze",
        DOSIAccent.Indigo => "Indigo",
        DOSIAccent.Magenta => "Magenta",
        DOSIAccent.Olive => "Olive",
        DOSIAccent.Turquoise => "Turquoise",
        DOSIAccent.Cyan => "Cyan",
        DOSIAccent.Aqua => "Aqua",
        DOSIAccent.Periwinkle => "Periwinkle",
        DOSIAccent.Plum => "Plum",
        DOSIAccent.Fuchsia => "Fuchsia",
        DOSIAccent.Pink => "Pink",
        DOSIAccent.Peach => "Peach",
        DOSIAccent.Apricot => "Apricot",
        DOSIAccent.Tangerine => "Tangerine",
        DOSIAccent.Goldenrod => "Goldenrod",
        DOSIAccent.Lime => "Lime",
        DOSIAccent.Chartreuse => "Chartreuse",
        DOSIAccent.Sage => "Sage",
        DOSIAccent.Pine => "Pine",
        DOSIAccent.Jade => "Jade",
        DOSIAccent.SeaGreen => "Sea Green",
        DOSIAccent.Cerulean => "Cerulean",
        DOSIAccent.SkyBlue => "Sky Blue",
        DOSIAccent.Cobalt => "Cobalt",
        DOSIAccent.Navy => "Navy",
        DOSIAccent.Burgundy => "Burgundy",
        DOSIAccent.Maroon => "Maroon",
        DOSIAccent.Wine => "Wine",
        DOSIAccent.Mocha => "Mocha",
        DOSIAccent.Chocolate => "Chocolate",
        DOSIAccent.Sand => "Sand",
        DOSIAccent.Charcoal => "Charcoal",
        DOSIAccent.Steel => "Steel",
        DOSIAccent.Onyx => "Onyx",
        _ => accent.ToString()
    };

    #region accent Data

    private static AccentColors GetAccentColors(DOSIAccent accent) => accent switch
    {
        // Base colors are neutral grays - the ApplyAccent method auto-tints them with the accent
        DOSIAccent.DarkBlue => new(
            Accent: Rgb(0, 122, 204), AccentDark: Rgb(0, 88, 156),
            WinBg: Rgb(32, 32, 32), WinBorderUnfocused: Rgb(55, 55, 55),
            Chrome: Rgb(28, 28, 28), ChromeUnfocused: Rgb(35, 35, 35), Content: Rgb(22, 22, 22),
            TextPrimary: Rgb(240, 245, 250), TextSecondary: Rgb(160, 165, 175),
            TextDisabled: Rgb(90, 95, 100), TextOnAccent: Rgb(255, 255, 255),
            CtrlBg: Rgb(42, 42, 42), CtrlHover: Rgb(55, 55, 55),
            CtrlPressed: Rgb(35, 35, 35), CtrlBorder: Rgb(70, 70, 70),
            BtnBg: Rgb(40, 40, 40), BtnHover: Rgb(55, 55, 55), BtnPressed: Rgb(32, 32, 32),
            ListBg: Rgb(20, 20, 20), ListHover: Rgb(45, 45, 45), ListSelectedUnfocused: Rgb(55, 55, 55),
            Desktop1: Rgb(8, 28, 58), Desktop2: Rgb(18, 52, 98), Desktop3: Rgb(12, 42, 78),
            Shadow: Rgba(0, 0, 0, 100)),

        DOSIAccent.DarkPurple => new(
            Accent: Rgb(138, 43, 226), AccentDark: Rgb(100, 30, 180),
            WinBg: Rgb(35, 35, 35), WinBorderUnfocused: Rgb(58, 58, 58),
            Chrome: Rgb(32, 32, 32), ChromeUnfocused: Rgb(38, 38, 38), Content: Rgb(28, 28, 28),
            TextPrimary: Rgb(248, 245, 255), TextSecondary: Rgb(175, 170, 185),
            TextDisabled: Rgb(100, 95, 110), TextOnAccent: Rgb(255, 255, 255),
            CtrlBg: Rgb(45, 45, 45), CtrlHover: Rgb(58, 58, 58),
            CtrlPressed: Rgb(38, 38, 38), CtrlBorder: Rgb(72, 72, 72),
            BtnBg: Rgb(42, 42, 42), BtnHover: Rgb(58, 58, 58), BtnPressed: Rgb(35, 35, 35),
            ListBg: Rgb(25, 25, 25), ListHover: Rgb(48, 48, 48), ListSelectedUnfocused: Rgb(58, 58, 58),
            Desktop1: Rgb(28, 15, 52), Desktop2: Rgb(50, 28, 85), Desktop3: Rgb(35, 20, 65),
            Shadow: Rgba(0, 0, 0, 100)),

        DOSIAccent.DarkGreen => new(
            Accent: Rgb(16, 185, 129), AccentDark: Rgb(10, 140, 100),
            WinBg: Rgb(32, 32, 32), WinBorderUnfocused: Rgb(55, 55, 55),
            Chrome: Rgb(28, 28, 28), ChromeUnfocused: Rgb(35, 35, 35), Content: Rgb(22, 22, 22),
            TextPrimary: Rgb(245, 255, 250), TextSecondary: Rgb(165, 175, 170),
            TextDisabled: Rgb(95, 105, 100), TextOnAccent: Rgb(255, 255, 255),
            CtrlBg: Rgb(42, 42, 42), CtrlHover: Rgb(55, 55, 55),
            CtrlPressed: Rgb(35, 35, 35), CtrlBorder: Rgb(70, 70, 70),
            BtnBg: Rgb(40, 40, 40), BtnHover: Rgb(55, 55, 55), BtnPressed: Rgb(32, 32, 32),
            ListBg: Rgb(20, 20, 20), ListHover: Rgb(45, 45, 45), ListSelectedUnfocused: Rgb(55, 55, 55),
            Desktop1: Rgb(12, 38, 28), Desktop2: Rgb(20, 62, 45), Desktop3: Rgb(15, 50, 35),
            Shadow: Rgba(0, 0, 0, 100)),

        DOSIAccent.DarkOrange => new(
            Accent: Rgb(255, 140, 0), AccentDark: Rgb(200, 100, 0),
            WinBg: Rgb(38, 38, 38), WinBorderUnfocused: Rgb(62, 62, 62),
            Chrome: Rgb(35, 35, 35), ChromeUnfocused: Rgb(42, 42, 42), Content: Rgb(30, 30, 30),
            TextPrimary: Rgb(255, 250, 245), TextSecondary: Rgb(185, 175, 165),
            TextDisabled: Rgb(115, 105, 95), TextOnAccent: Rgb(0, 0, 0),
            CtrlBg: Rgb(48, 48, 48), CtrlHover: Rgb(62, 62, 62),
            CtrlPressed: Rgb(40, 40, 40), CtrlBorder: Rgb(78, 78, 78),
            BtnBg: Rgb(45, 45, 45), BtnHover: Rgb(62, 62, 62), BtnPressed: Rgb(38, 38, 38),
            ListBg: Rgb(28, 28, 28), ListHover: Rgb(50, 50, 50), ListSelectedUnfocused: Rgb(62, 62, 62),
            Desktop1: Rgb(45, 28, 12), Desktop2: Rgb(70, 45, 20), Desktop3: Rgb(55, 35, 15),
            Shadow: Rgba(0, 0, 0, 100)),

        DOSIAccent.DarkRed => new(
            Accent: Rgb(220, 50, 70), AccentDark: Rgb(170, 30, 50),
            WinBg: Rgb(36, 36, 36), WinBorderUnfocused: Rgb(60, 60, 60),
            Chrome: Rgb(32, 32, 32), ChromeUnfocused: Rgb(40, 40, 40), Content: Rgb(28, 28, 28),
            TextPrimary: Rgb(255, 248, 250), TextSecondary: Rgb(180, 170, 172),
            TextDisabled: Rgb(110, 100, 102), TextOnAccent: Rgb(255, 255, 255),
            CtrlBg: Rgb(46, 46, 46), CtrlHover: Rgb(60, 60, 60),
            CtrlPressed: Rgb(38, 38, 38), CtrlBorder: Rgb(75, 75, 75),
            BtnBg: Rgb(44, 44, 44), BtnHover: Rgb(60, 60, 60), BtnPressed: Rgb(36, 36, 36),
            ListBg: Rgb(26, 26, 26), ListHover: Rgb(48, 48, 48), ListSelectedUnfocused: Rgb(60, 60, 60),
            Desktop1: Rgb(45, 18, 25), Desktop2: Rgb(68, 30, 40), Desktop3: Rgb(55, 22, 32),
            Shadow: Rgba(0, 0, 0, 100)),

        DOSIAccent.DarkTeal => new(
            Accent: Rgb(0, 188, 212), AccentDark: Rgb(0, 140, 160),
            WinBg: Rgb(32, 32, 32), WinBorderUnfocused: Rgb(55, 55, 55),
            Chrome: Rgb(28, 28, 28), ChromeUnfocused: Rgb(35, 35, 35), Content: Rgb(22, 22, 22),
            TextPrimary: Rgb(245, 252, 255), TextSecondary: Rgb(165, 175, 180),
            TextDisabled: Rgb(95, 105, 110), TextOnAccent: Rgb(0, 0, 0),
            CtrlBg: Rgb(42, 42, 42), CtrlHover: Rgb(55, 55, 55),
            CtrlPressed: Rgb(35, 35, 35), CtrlBorder: Rgb(70, 70, 70),
            BtnBg: Rgb(40, 40, 40), BtnHover: Rgb(55, 55, 55), BtnPressed: Rgb(32, 32, 32),
            ListBg: Rgb(20, 20, 20), ListHover: Rgb(45, 45, 45), ListSelectedUnfocused: Rgb(55, 55, 55),
            Desktop1: Rgb(12, 32, 42), Desktop2: Rgb(18, 55, 70), Desktop3: Rgb(15, 45, 58),
            Shadow: Rgba(0, 0, 0, 100)),

        DOSIAccent.Light => new(
            // Slightly deeper accent so it stays readable on white surfaces
            // (the old 0,120,215 washed out against pure-white chrome).
            Accent: Rgb(0, 95, 184), AccentDark: Rgb(0, 70, 145),
            // Off-white window/chrome with a faint cool tint so the chrome
            // visually separates from the content area instead of every
            // surface collapsing into one flat sheet of white.
            WinBg: Rgb(238, 240, 244), WinBorderUnfocused: Rgb(195, 200, 210),
            Chrome: Rgb(248, 249, 252), ChromeUnfocused: Rgb(236, 238, 242), Content: Rgb(252, 252, 254),
            // Darker, less-tinted text for proper contrast against light surfaces.
            TextPrimary: Rgb(20, 22, 28), TextSecondary: Rgb(85, 90, 100),
            TextDisabled: Rgb(150, 155, 165), TextOnAccent: Rgb(255, 255, 255),
            CtrlBg: Rgb(248, 249, 252), CtrlHover: Rgb(232, 236, 244),
            CtrlPressed: Rgb(218, 224, 234), CtrlBorder: Rgb(190, 195, 205),
            BtnBg: Rgb(248, 249, 252), BtnHover: Rgb(232, 236, 244), BtnPressed: Rgb(218, 224, 234),
            ListBg: Rgb(252, 253, 255), ListHover: Rgb(235, 240, 248), ListSelectedUnfocused: Rgb(208, 215, 228),
            Desktop1: Rgb(180, 200, 225), Desktop2: Rgb(155, 180, 215), Desktop3: Rgb(168, 190, 220),
            Shadow: Rgba(0, 0, 0, 50)),

        DOSIAccent.Midnight => new(
            Accent: Rgb(100, 100, 255), AccentDark: Rgb(70, 70, 200),
            WinBg: Rgb(18, 18, 18), WinBorderUnfocused: Rgb(40, 40, 40),
            Chrome: Rgb(15, 15, 15), ChromeUnfocused: Rgb(22, 22, 22), Content: Rgb(12, 12, 12),
            TextPrimary: Rgb(230, 230, 240), TextSecondary: Rgb(140, 140, 155),
            TextDisabled: Rgb(80, 80, 90), TextOnAccent: Rgb(255, 255, 255),
            CtrlBg: Rgb(25, 25, 25), CtrlHover: Rgb(38, 38, 38),
            CtrlPressed: Rgb(20, 20, 20), CtrlBorder: Rgb(50, 50, 50),
            BtnBg: Rgb(28, 28, 28), BtnHover: Rgb(42, 42, 42), BtnPressed: Rgb(22, 22, 22),
            ListBg: Rgb(10, 10, 10), ListHover: Rgb(30, 30, 30), ListSelectedUnfocused: Rgb(40, 40, 40),
            Desktop1: Rgb(5, 5, 15), Desktop2: Rgb(15, 15, 35), Desktop3: Rgb(8, 8, 22),
            Shadow: Rgba(0, 0, 0, 120)),

        // Rose Gold - elegant pink-gold
        DOSIAccent.RoseGold => new(
            Accent: Rgb(183, 110, 121), AccentDark: Rgb(150, 85, 95),
            WinBg: Rgb(38, 34, 35), WinBorderUnfocused: Rgb(60, 55, 56),
            Chrome: Rgb(35, 31, 32), ChromeUnfocused: Rgb(42, 38, 39), Content: Rgb(30, 26, 27),
            TextPrimary: Rgb(255, 245, 247), TextSecondary: Rgb(180, 170, 172),
            TextDisabled: Rgb(110, 100, 102), TextOnAccent: Rgb(255, 255, 255),
            CtrlBg: Rgb(48, 44, 45), CtrlHover: Rgb(62, 57, 58),
            CtrlPressed: Rgb(40, 36, 37), CtrlBorder: Rgb(78, 72, 73),
            BtnBg: Rgb(45, 41, 42), BtnHover: Rgb(62, 57, 58), BtnPressed: Rgb(38, 34, 35),
            ListBg: Rgb(28, 24, 25), ListHover: Rgb(50, 45, 46), ListSelectedUnfocused: Rgb(62, 57, 58),
            Desktop1: Rgb(45, 32, 38), Desktop2: Rgb(70, 50, 58), Desktop3: Rgb(55, 40, 48),
            Shadow: Rgba(0, 0, 0, 100)),

        // Coral - warm coral pink
        DOSIAccent.Coral => new(
            Accent: Rgb(255, 127, 80), AccentDark: Rgb(210, 100, 60),
            WinBg: Rgb(38, 35, 34), WinBorderUnfocused: Rgb(62, 58, 56),
            Chrome: Rgb(35, 32, 31), ChromeUnfocused: Rgb(42, 39, 38), Content: Rgb(30, 27, 26),
            TextPrimary: Rgb(255, 250, 248), TextSecondary: Rgb(185, 178, 175),
            TextDisabled: Rgb(115, 108, 105), TextOnAccent: Rgb(0, 0, 0),
            CtrlBg: Rgb(48, 45, 44), CtrlHover: Rgb(62, 58, 56),
            CtrlPressed: Rgb(40, 37, 36), CtrlBorder: Rgb(78, 74, 72),
            BtnBg: Rgb(45, 42, 41), BtnHover: Rgb(62, 58, 56), BtnPressed: Rgb(38, 35, 34),
            ListBg: Rgb(28, 25, 24), ListHover: Rgb(50, 46, 44), ListSelectedUnfocused: Rgb(62, 58, 56),
            Desktop1: Rgb(50, 35, 30), Desktop2: Rgb(75, 52, 45), Desktop3: Rgb(60, 42, 36),
            Shadow: Rgba(0, 0, 0, 100)),

        // Lavender - soft purple
        DOSIAccent.Lavender => new(
            Accent: Rgb(180, 150, 210), AccentDark: Rgb(145, 115, 175),
            WinBg: Rgb(36, 35, 40), WinBorderUnfocused: Rgb(58, 56, 65),
            Chrome: Rgb(33, 32, 38), ChromeUnfocused: Rgb(40, 38, 45), Content: Rgb(28, 27, 33),
            TextPrimary: Rgb(250, 248, 255), TextSecondary: Rgb(178, 175, 188),
            TextDisabled: Rgb(108, 105, 118), TextOnAccent: Rgb(30, 30, 30),
            CtrlBg: Rgb(46, 44, 52), CtrlHover: Rgb(58, 56, 66),
            CtrlPressed: Rgb(38, 36, 44), CtrlBorder: Rgb(74, 72, 82),
            BtnBg: Rgb(43, 41, 50), BtnHover: Rgb(58, 56, 66), BtnPressed: Rgb(36, 34, 42),
            ListBg: Rgb(26, 24, 30), ListHover: Rgb(48, 45, 55), ListSelectedUnfocused: Rgb(58, 56, 66),
            Desktop1: Rgb(35, 30, 50), Desktop2: Rgb(55, 48, 78), Desktop3: Rgb(42, 38, 62),
            Shadow: Rgba(0, 0, 0, 100)),

        // Mint - fresh green
        DOSIAccent.Mint => new(
            Accent: Rgb(152, 224, 186), AccentDark: Rgb(115, 185, 148),
            WinBg: Rgb(32, 38, 35), WinBorderUnfocused: Rgb(52, 62, 56),
            Chrome: Rgb(28, 35, 32), ChromeUnfocused: Rgb(35, 42, 38), Content: Rgb(22, 30, 26),
            TextPrimary: Rgb(245, 255, 250), TextSecondary: Rgb(165, 180, 172),
            TextDisabled: Rgb(95, 110, 102), TextOnAccent: Rgb(30, 30, 30),
            CtrlBg: Rgb(42, 50, 46), CtrlHover: Rgb(52, 62, 56),
            CtrlPressed: Rgb(35, 42, 38), CtrlBorder: Rgb(68, 78, 72),
            BtnBg: Rgb(40, 48, 44), BtnHover: Rgb(52, 62, 56), BtnPressed: Rgb(32, 40, 36),
            ListBg: Rgb(20, 28, 24), ListHover: Rgb(42, 52, 46), ListSelectedUnfocused: Rgb(52, 62, 56),
            Desktop1: Rgb(18, 42, 32), Desktop2: Rgb(30, 65, 50), Desktop3: Rgb(22, 52, 40),
            Shadow: Rgba(0, 0, 0, 100)),

        // Slate - cool gray-blue
        DOSIAccent.Slate => new(
            Accent: Rgb(112, 128, 144), AccentDark: Rgb(85, 100, 115),
            WinBg: Rgb(35, 38, 42), WinBorderUnfocused: Rgb(55, 60, 68),
            Chrome: Rgb(32, 35, 40), ChromeUnfocused: Rgb(38, 42, 48), Content: Rgb(28, 30, 35),
            TextPrimary: Rgb(240, 245, 250), TextSecondary: Rgb(160, 168, 180),
            TextDisabled: Rgb(90, 98, 110), TextOnAccent: Rgb(255, 255, 255),
            CtrlBg: Rgb(45, 50, 56), CtrlHover: Rgb(55, 62, 70),
            CtrlPressed: Rgb(38, 42, 48), CtrlBorder: Rgb(70, 78, 88),
            BtnBg: Rgb(42, 48, 54), BtnHover: Rgb(55, 62, 70), BtnPressed: Rgb(35, 40, 46),
            ListBg: Rgb(25, 28, 32), ListHover: Rgb(45, 50, 58), ListSelectedUnfocused: Rgb(55, 62, 70),
            Desktop1: Rgb(25, 32, 42), Desktop2: Rgb(40, 52, 68), Desktop3: Rgb(32, 42, 55),
            Shadow: Rgba(0, 0, 0, 100)),

        // Copper - warm metallic
        DOSIAccent.Copper => new(
            Accent: Rgb(184, 115, 81), AccentDark: Rgb(148, 88, 60),
            WinBg: Rgb(40, 36, 34), WinBorderUnfocused: Rgb(65, 58, 54),
            Chrome: Rgb(38, 34, 32), ChromeUnfocused: Rgb(45, 40, 38), Content: Rgb(32, 28, 26),
            TextPrimary: Rgb(255, 248, 245), TextSecondary: Rgb(188, 178, 172),
            TextDisabled: Rgb(118, 108, 102), TextOnAccent: Rgb(255, 255, 255),
            CtrlBg: Rgb(52, 46, 44), CtrlHover: Rgb(66, 58, 54),
            CtrlPressed: Rgb(44, 38, 36), CtrlBorder: Rgb(82, 74, 70),
            BtnBg: Rgb(48, 44, 42), BtnHover: Rgb(66, 58, 54), BtnPressed: Rgb(40, 36, 34),
            ListBg: Rgb(30, 26, 24), ListHover: Rgb(52, 46, 42), ListSelectedUnfocused: Rgb(66, 58, 54),
            Desktop1: Rgb(48, 35, 28), Desktop2: Rgb(72, 55, 44), Desktop3: Rgb(58, 44, 35),
            Shadow: Rgba(0, 0, 0, 100)),

        // Sapphire - deep blue
        DOSIAccent.Sapphire => new(
            Accent: Rgb(15, 82, 186), AccentDark: Rgb(10, 60, 145),
            WinBg: Rgb(30, 32, 40), WinBorderUnfocused: Rgb(48, 52, 65),
            Chrome: Rgb(26, 28, 38), ChromeUnfocused: Rgb(32, 35, 45), Content: Rgb(20, 22, 32),
            TextPrimary: Rgb(235, 242, 255), TextSecondary: Rgb(155, 165, 190),
            TextDisabled: Rgb(85, 95, 120), TextOnAccent: Rgb(255, 255, 255),
            CtrlBg: Rgb(40, 44, 55), CtrlHover: Rgb(50, 55, 70),
            CtrlPressed: Rgb(32, 36, 48), CtrlBorder: Rgb(62, 68, 85),
            BtnBg: Rgb(38, 42, 52), BtnHover: Rgb(50, 55, 70), BtnPressed: Rgb(30, 34, 44),
            ListBg: Rgb(18, 20, 28), ListHover: Rgb(38, 42, 55), ListSelectedUnfocused: Rgb(50, 55, 70),
            Desktop1: Rgb(8, 20, 50), Desktop2: Rgb(15, 38, 82), Desktop3: Rgb(10, 28, 65),
            Shadow: Rgba(0, 0, 0, 110)),

        // Emerald - rich green
        DOSIAccent.Emerald => new(
            Accent: Rgb(0, 155, 119), AccentDark: Rgb(0, 120, 90),
            WinBg: Rgb(30, 38, 35), WinBorderUnfocused: Rgb(48, 62, 56),
            Chrome: Rgb(26, 35, 32), ChromeUnfocused: Rgb(32, 42, 38), Content: Rgb(20, 30, 26),
            TextPrimary: Rgb(240, 255, 250), TextSecondary: Rgb(158, 182, 172),
            TextDisabled: Rgb(88, 112, 102), TextOnAccent: Rgb(255, 255, 255),
            CtrlBg: Rgb(40, 52, 48), CtrlHover: Rgb(50, 65, 58),
            CtrlPressed: Rgb(32, 44, 40), CtrlBorder: Rgb(62, 80, 72),
            BtnBg: Rgb(38, 50, 45), BtnHover: Rgb(50, 65, 58), BtnPressed: Rgb(30, 42, 38),
            ListBg: Rgb(18, 28, 24), ListHover: Rgb(38, 52, 46), ListSelectedUnfocused: Rgb(50, 65, 58),
            Desktop1: Rgb(10, 40, 32), Desktop2: Rgb(18, 65, 52), Desktop3: Rgb(14, 52, 42),
            Shadow: Rgba(0, 0, 0, 100)),

        // Ruby - deep red
        DOSIAccent.Ruby => new(
            Accent: Rgb(155, 17, 50), AccentDark: Rgb(120, 12, 38),
            WinBg: Rgb(38, 32, 34), WinBorderUnfocused: Rgb(62, 50, 54),
            Chrome: Rgb(35, 28, 30), ChromeUnfocused: Rgb(42, 35, 38), Content: Rgb(30, 24, 26),
            TextPrimary: Rgb(255, 242, 245), TextSecondary: Rgb(188, 168, 175),
            TextDisabled: Rgb(118, 98, 105), TextOnAccent: Rgb(255, 255, 255),
            CtrlBg: Rgb(50, 42, 45), CtrlHover: Rgb(64, 52, 56),
            CtrlPressed: Rgb(42, 34, 38), CtrlBorder: Rgb(80, 68, 72),
            BtnBg: Rgb(48, 40, 42), BtnHover: Rgb(64, 52, 56), BtnPressed: Rgb(40, 32, 35),
            ListBg: Rgb(28, 22, 24), ListHover: Rgb(50, 40, 44), ListSelectedUnfocused: Rgb(64, 52, 56),
            Desktop1: Rgb(45, 15, 25), Desktop2: Rgb(72, 28, 42), Desktop3: Rgb(58, 20, 32),
            Shadow: Rgba(0, 0, 0, 100)),

        // Amber - warm golden yellow
        DOSIAccent.Amber => new(
            Accent: Rgb(255, 191, 0), AccentDark: Rgb(210, 155, 0),
            WinBg: Rgb(40, 38, 32), WinBorderUnfocused: Rgb(65, 62, 52),
            Chrome: Rgb(38, 36, 28), ChromeUnfocused: Rgb(45, 42, 35), Content: Rgb(32, 30, 24),
            TextPrimary: Rgb(255, 252, 240), TextSecondary: Rgb(192, 188, 168),
            TextDisabled: Rgb(122, 118, 98), TextOnAccent: Rgb(30, 30, 30),
            CtrlBg: Rgb(52, 50, 42), CtrlHover: Rgb(68, 64, 52),
            CtrlPressed: Rgb(44, 42, 34), CtrlBorder: Rgb(85, 80, 68),
            BtnBg: Rgb(50, 48, 40), BtnHover: Rgb(68, 64, 52), BtnPressed: Rgb(42, 40, 32),
            ListBg: Rgb(30, 28, 22), ListHover: Rgb(52, 48, 40), ListSelectedUnfocused: Rgb(68, 64, 52),
            Desktop1: Rgb(50, 42, 18), Desktop2: Rgb(78, 68, 30), Desktop3: Rgb(62, 54, 24),
            Shadow: Rgba(0, 0, 0, 100)),

        // Violet - purple-blue
        DOSIAccent.Violet => new(
            Accent: Rgb(127, 90, 180), AccentDark: Rgb(98, 68, 145),
            WinBg: Rgb(35, 33, 42), WinBorderUnfocused: Rgb(56, 52, 68),
            Chrome: Rgb(32, 30, 40), ChromeUnfocused: Rgb(38, 36, 48), Content: Rgb(28, 26, 35),
            TextPrimary: Rgb(248, 245, 255), TextSecondary: Rgb(178, 172, 195),
            TextDisabled: Rgb(108, 102, 125), TextOnAccent: Rgb(255, 255, 255),
            CtrlBg: Rgb(46, 43, 56), CtrlHover: Rgb(58, 54, 72),
            CtrlPressed: Rgb(38, 35, 48), CtrlBorder: Rgb(74, 70, 90),
            BtnBg: Rgb(44, 41, 54), BtnHover: Rgb(58, 54, 72), BtnPressed: Rgb(36, 33, 46),
            ListBg: Rgb(26, 24, 32), ListHover: Rgb(46, 42, 58), ListSelectedUnfocused: Rgb(58, 54, 72),
            Desktop1: Rgb(32, 25, 52), Desktop2: Rgb(52, 42, 85), Desktop3: Rgb(42, 32, 68),
            Shadow: Rgba(0, 0, 0, 100)),

        // Crimson - vivid red
        DOSIAccent.Crimson => new(
            Accent: Rgb(220, 20, 60), AccentDark: Rgb(178, 15, 48),
            WinBg: Rgb(40, 32, 34), WinBorderUnfocused: Rgb(65, 50, 55),
            Chrome: Rgb(38, 28, 30), ChromeUnfocused: Rgb(45, 35, 38), Content: Rgb(32, 24, 26),
            TextPrimary: Rgb(255, 240, 245), TextSecondary: Rgb(195, 168, 178),
            TextDisabled: Rgb(125, 98, 108), TextOnAccent: Rgb(255, 255, 255),
            CtrlBg: Rgb(52, 42, 46), CtrlHover: Rgb(68, 52, 58),
            CtrlPressed: Rgb(44, 34, 38), CtrlBorder: Rgb(85, 68, 75),
            BtnBg: Rgb(50, 40, 44), BtnHover: Rgb(68, 52, 58), BtnPressed: Rgb(42, 32, 36),
            ListBg: Rgb(30, 22, 25), ListHover: Rgb(52, 40, 46), ListSelectedUnfocused: Rgb(68, 52, 58),
            Desktop1: Rgb(52, 18, 28), Desktop2: Rgb(82, 32, 48), Desktop3: Rgb(65, 24, 38),
            Shadow: Rgba(0, 0, 0, 100)),

        // Forest - deep forest green
        DOSIAccent.Forest => new(
            Accent: Rgb(34, 139, 34), AccentDark: Rgb(24, 108, 24),
            WinBg: Rgb(30, 36, 32), WinBorderUnfocused: Rgb(48, 58, 52),
            Chrome: Rgb(26, 33, 28), ChromeUnfocused: Rgb(32, 40, 35), Content: Rgb(20, 28, 24),
            TextPrimary: Rgb(242, 255, 245), TextSecondary: Rgb(162, 182, 168),
            TextDisabled: Rgb(92, 112, 98), TextOnAccent: Rgb(255, 255, 255),
            CtrlBg: Rgb(40, 48, 44), CtrlHover: Rgb(50, 62, 55),
            CtrlPressed: Rgb(32, 40, 36), CtrlBorder: Rgb(62, 78, 68),
            BtnBg: Rgb(38, 46, 42), BtnHover: Rgb(50, 62, 55), BtnPressed: Rgb(30, 38, 34),
            ListBg: Rgb(18, 26, 22), ListHover: Rgb(38, 48, 42), ListSelectedUnfocused: Rgb(50, 62, 55),
            Desktop1: Rgb(15, 38, 22), Desktop2: Rgb(28, 62, 38), Desktop3: Rgb(20, 50, 30),
            Shadow: Rgba(0, 0, 0, 100)),

        // Ocean - deep ocean blue
        DOSIAccent.Ocean => new(
            Accent: Rgb(0, 105, 148), AccentDark: Rgb(0, 80, 118),
            WinBg: Rgb(28, 34, 40), WinBorderUnfocused: Rgb(45, 55, 65),
            Chrome: Rgb(24, 30, 38), ChromeUnfocused: Rgb(30, 38, 46), Content: Rgb(18, 26, 34),
            TextPrimary: Rgb(235, 248, 255), TextSecondary: Rgb(155, 175, 192),
            TextDisabled: Rgb(85, 105, 122), TextOnAccent: Rgb(255, 255, 255),
            CtrlBg: Rgb(38, 46, 55), CtrlHover: Rgb(48, 58, 70),
            CtrlPressed: Rgb(30, 38, 48), CtrlBorder: Rgb(58, 72, 88),
            BtnBg: Rgb(36, 44, 52), BtnHover: Rgb(48, 58, 70), BtnPressed: Rgb(28, 36, 44),
            ListBg: Rgb(16, 22, 30), ListHover: Rgb(36, 45, 56), ListSelectedUnfocused: Rgb(48, 58, 70),
            Desktop1: Rgb(8, 28, 48), Desktop2: Rgb(15, 48, 78), Desktop3: Rgb(10, 38, 62),
            Shadow: Rgba(0, 0, 0, 110)),

        // Sunset - warm orange-pink
        DOSIAccent.Sunset => new(
            Accent: Rgb(250, 128, 114), AccentDark: Rgb(205, 100, 88),
            WinBg: Rgb(42, 36, 36), WinBorderUnfocused: Rgb(68, 58, 58),
            Chrome: Rgb(40, 33, 33), ChromeUnfocused: Rgb(48, 40, 40), Content: Rgb(35, 28, 28),
            TextPrimary: Rgb(255, 248, 248), TextSecondary: Rgb(198, 182, 182),
            TextDisabled: Rgb(128, 112, 112), TextOnAccent: Rgb(30, 30, 30),
            CtrlBg: Rgb(55, 48, 48), CtrlHover: Rgb(72, 62, 62),
            CtrlPressed: Rgb(46, 40, 40), CtrlBorder: Rgb(88, 78, 78),
            BtnBg: Rgb(52, 45, 45), BtnHover: Rgb(72, 62, 62), BtnPressed: Rgb(44, 38, 38),
            ListBg: Rgb(32, 26, 26), ListHover: Rgb(55, 46, 46), ListSelectedUnfocused: Rgb(72, 62, 62),
            Desktop1: Rgb(58, 32, 32), Desktop2: Rgb(92, 52, 52), Desktop3: Rgb(75, 42, 42),
            Shadow: Rgba(0, 0, 0, 100)),

        // Storm - dark gray-blue
        DOSIAccent.Storm => new(
            Accent: Rgb(95, 108, 128), AccentDark: Rgb(72, 82, 100),
            WinBg: Rgb(32, 34, 38), WinBorderUnfocused: Rgb(50, 54, 62),
            Chrome: Rgb(28, 30, 35), ChromeUnfocused: Rgb(35, 38, 44), Content: Rgb(24, 26, 30),
            TextPrimary: Rgb(230, 235, 245), TextSecondary: Rgb(150, 158, 175),
            TextDisabled: Rgb(85, 92, 108), TextOnAccent: Rgb(255, 255, 255),
            CtrlBg: Rgb(42, 46, 52), CtrlHover: Rgb(52, 58, 68),
            CtrlPressed: Rgb(35, 38, 45), CtrlBorder: Rgb(65, 72, 85),
            BtnBg: Rgb(40, 44, 50), BtnHover: Rgb(52, 58, 68), BtnPressed: Rgb(32, 36, 42),
            ListBg: Rgb(22, 24, 28), ListHover: Rgb(42, 46, 55), ListSelectedUnfocused: Rgb(52, 58, 68),
            Desktop1: Rgb(22, 28, 38), Desktop2: Rgb(38, 48, 65), Desktop3: Rgb(28, 38, 52),
            Shadow: Rgba(0, 0, 0, 110)),

        // Bronze - warm metallic brown
        DOSIAccent.Bronze => new(
            Accent: Rgb(205, 127, 50), AccentDark: Rgb(165, 100, 38),
            WinBg: Rgb(42, 38, 34), WinBorderUnfocused: Rgb(68, 62, 55),
            Chrome: Rgb(40, 36, 30), ChromeUnfocused: Rgb(48, 44, 38), Content: Rgb(35, 32, 26),
            TextPrimary: Rgb(255, 250, 242), TextSecondary: Rgb(198, 188, 175),
            TextDisabled: Rgb(128, 118, 105), TextOnAccent: Rgb(30, 30, 30),
            CtrlBg: Rgb(55, 50, 44), CtrlHover: Rgb(72, 65, 56),
            CtrlPressed: Rgb(46, 42, 36), CtrlBorder: Rgb(88, 80, 70),
            BtnBg: Rgb(52, 48, 42), BtnHover: Rgb(72, 65, 56), BtnPressed: Rgb(44, 40, 34),
            ListBg: Rgb(32, 28, 24), ListHover: Rgb(55, 50, 42), ListSelectedUnfocused: Rgb(72, 65, 56),
            Desktop1: Rgb(52, 42, 28), Desktop2: Rgb(82, 68, 48), Desktop3: Rgb(68, 55, 38),
            Shadow: Rgba(0, 0, 0, 100)),

        // Indigo - deep blue-violet
        DOSIAccent.Indigo => new(
            Accent: Rgb(75, 0, 130), AccentDark: Rgb(55, 0, 100),
            WinBg: Rgb(32, 30, 40), WinBorderUnfocused: Rgb(52, 48, 66),
            Chrome: Rgb(28, 26, 38), ChromeUnfocused: Rgb(35, 32, 46), Content: Rgb(24, 22, 34),
            TextPrimary: Rgb(242, 238, 255), TextSecondary: Rgb(168, 162, 195),
            TextDisabled: Rgb(98, 92, 125), TextOnAccent: Rgb(255, 255, 255),
            CtrlBg: Rgb(42, 40, 54), CtrlHover: Rgb(54, 50, 70),
            CtrlPressed: Rgb(34, 32, 46), CtrlBorder: Rgb(68, 64, 88),
            BtnBg: Rgb(40, 38, 52), BtnHover: Rgb(54, 50, 70), BtnPressed: Rgb(32, 30, 44),
            ListBg: Rgb(22, 20, 30), ListHover: Rgb(42, 38, 55), ListSelectedUnfocused: Rgb(54, 50, 70),
            Desktop1: Rgb(25, 15, 48), Desktop2: Rgb(42, 28, 78), Desktop3: Rgb(32, 20, 62),
            Shadow: Rgba(0, 0, 0, 110)),

        // Magenta - vivid pink-purple
        DOSIAccent.Magenta => new(
            Accent: Rgb(255, 0, 144), AccentDark: Rgb(205, 0, 115),
            WinBg: Rgb(40, 32, 38), WinBorderUnfocused: Rgb(66, 50, 62),
            Chrome: Rgb(38, 28, 35), ChromeUnfocused: Rgb(46, 35, 42), Content: Rgb(34, 24, 30),
            TextPrimary: Rgb(255, 242, 252), TextSecondary: Rgb(198, 168, 188),
            TextDisabled: Rgb(128, 98, 118), TextOnAccent: Rgb(255, 255, 255),
            CtrlBg: Rgb(54, 42, 50), CtrlHover: Rgb(70, 54, 65),
            CtrlPressed: Rgb(46, 34, 42), CtrlBorder: Rgb(88, 68, 82),
            BtnBg: Rgb(52, 40, 48), BtnHover: Rgb(70, 54, 65), BtnPressed: Rgb(44, 32, 40),
            ListBg: Rgb(32, 22, 28), ListHover: Rgb(55, 42, 50), ListSelectedUnfocused: Rgb(70, 54, 65),
            Desktop1: Rgb(52, 18, 42), Desktop2: Rgb(85, 32, 68), Desktop3: Rgb(68, 24, 55),
            Shadow: Rgba(0, 0, 0, 100)),

        // Olive - earthy green
        DOSIAccent.Olive => new(
            Accent: Rgb(128, 128, 0), AccentDark: Rgb(100, 100, 0),
            WinBg: Rgb(38, 38, 32), WinBorderUnfocused: Rgb(62, 62, 52),
            Chrome: Rgb(35, 35, 28), ChromeUnfocused: Rgb(42, 42, 35), Content: Rgb(30, 30, 24),
            TextPrimary: Rgb(252, 252, 242), TextSecondary: Rgb(185, 185, 168),
            TextDisabled: Rgb(115, 115, 98), TextOnAccent: Rgb(30, 30, 30),
            CtrlBg: Rgb(50, 50, 42), CtrlHover: Rgb(65, 65, 54),
            CtrlPressed: Rgb(42, 42, 34), CtrlBorder: Rgb(80, 80, 68),
            BtnBg: Rgb(48, 48, 40), BtnHover: Rgb(65, 65, 54), BtnPressed: Rgb(40, 40, 32),
            ListBg: Rgb(28, 28, 22), ListHover: Rgb(50, 50, 42), ListSelectedUnfocused: Rgb(65, 65, 54),
            Desktop1: Rgb(38, 42, 18), Desktop2: Rgb(62, 68, 32), Desktop3: Rgb(50, 55, 25),
            Shadow: Rgba(0, 0, 0, 100)),

        // Turquoise - bright teal-cyan
        DOSIAccent.Turquoise => new(
            Accent: Rgb(64, 224, 208), AccentDark: Rgb(40, 180, 168),
            WinBg: Rgb(30, 38, 38), WinBorderUnfocused: Rgb(48, 62, 62),
            Chrome: Rgb(26, 35, 35), ChromeUnfocused: Rgb(32, 42, 42), Content: Rgb(20, 30, 30),
            TextPrimary: Rgb(240, 255, 255), TextSecondary: Rgb(160, 185, 185),
            TextDisabled: Rgb(90, 115, 115), TextOnAccent: Rgb(15, 30, 35),
            CtrlBg: Rgb(40, 52, 52), CtrlHover: Rgb(52, 66, 66),
            CtrlPressed: Rgb(34, 44, 44), CtrlBorder: Rgb(64, 82, 82),
            BtnBg: Rgb(38, 50, 50), BtnHover: Rgb(52, 66, 66), BtnPressed: Rgb(32, 42, 42),
            ListBg: Rgb(20, 28, 28), ListHover: Rgb(40, 52, 52), ListSelectedUnfocused: Rgb(52, 66, 66),
            Desktop1: Rgb(8, 42, 42), Desktop2: Rgb(15, 70, 68), Desktop3: Rgb(12, 55, 55),
            Shadow: Rgba(0, 0, 0, 100)),

        // Cyan - vivid pure cyan
        DOSIAccent.Cyan => new(
            Accent: Rgb(0, 220, 220), AccentDark: Rgb(0, 170, 175),
            WinBg: Rgb(28, 36, 38), WinBorderUnfocused: Rgb(46, 60, 62),
            Chrome: Rgb(24, 32, 35), ChromeUnfocused: Rgb(30, 40, 42), Content: Rgb(18, 28, 30),
            TextPrimary: Rgb(238, 254, 255), TextSecondary: Rgb(155, 180, 188),
            TextDisabled: Rgb(85, 110, 118), TextOnAccent: Rgb(10, 30, 35),
            CtrlBg: Rgb(38, 50, 52), CtrlHover: Rgb(50, 64, 68),
            CtrlPressed: Rgb(32, 42, 46), CtrlBorder: Rgb(60, 78, 84),
            BtnBg: Rgb(36, 48, 50), BtnHover: Rgb(50, 64, 68), BtnPressed: Rgb(30, 40, 42),
            ListBg: Rgb(18, 26, 28), ListHover: Rgb(38, 50, 54), ListSelectedUnfocused: Rgb(50, 64, 68),
            Desktop1: Rgb(8, 38, 42), Desktop2: Rgb(12, 65, 72), Desktop3: Rgb(10, 50, 56),
            Shadow: Rgba(0, 0, 0, 110)),

        // Aqua - light watery cyan
        DOSIAccent.Aqua => new(
            Accent: Rgb(130, 220, 230), AccentDark: Rgb(95, 180, 195),
            WinBg: Rgb(30, 38, 40), WinBorderUnfocused: Rgb(48, 60, 65),
            Chrome: Rgb(26, 33, 36), ChromeUnfocused: Rgb(32, 40, 44), Content: Rgb(20, 28, 32),
            TextPrimary: Rgb(238, 252, 255), TextSecondary: Rgb(160, 180, 188),
            TextDisabled: Rgb(90, 110, 118), TextOnAccent: Rgb(15, 35, 40),
            CtrlBg: Rgb(40, 50, 54), CtrlHover: Rgb(52, 64, 68),
            CtrlPressed: Rgb(32, 42, 46), CtrlBorder: Rgb(62, 78, 84),
            BtnBg: Rgb(38, 48, 52), BtnHover: Rgb(52, 64, 68), BtnPressed: Rgb(30, 40, 44),
            ListBg: Rgb(20, 26, 30), ListHover: Rgb(40, 50, 54), ListSelectedUnfocused: Rgb(52, 64, 68),
            Desktop1: Rgb(15, 42, 50), Desktop2: Rgb(28, 70, 80), Desktop3: Rgb(20, 55, 65),
            Shadow: Rgba(0, 0, 0, 100)),

        // Periwinkle - soft blue-violet
        DOSIAccent.Periwinkle => new(
            Accent: Rgb(170, 175, 230), AccentDark: Rgb(135, 142, 195),
            WinBg: Rgb(34, 35, 42), WinBorderUnfocused: Rgb(54, 56, 68),
            Chrome: Rgb(31, 32, 40), ChromeUnfocused: Rgb(38, 39, 47), Content: Rgb(26, 27, 34),
            TextPrimary: Rgb(246, 248, 255), TextSecondary: Rgb(172, 178, 198),
            TextDisabled: Rgb(102, 108, 128), TextOnAccent: Rgb(20, 25, 50),
            CtrlBg: Rgb(44, 46, 56), CtrlHover: Rgb(56, 60, 72),
            CtrlPressed: Rgb(36, 38, 48), CtrlBorder: Rgb(70, 74, 90),
            BtnBg: Rgb(42, 44, 54), BtnHover: Rgb(56, 60, 72), BtnPressed: Rgb(34, 36, 46),
            ListBg: Rgb(24, 25, 32), ListHover: Rgb(44, 46, 58), ListSelectedUnfocused: Rgb(56, 60, 72),
            Desktop1: Rgb(28, 30, 58), Desktop2: Rgb(48, 52, 92), Desktop3: Rgb(36, 40, 72),
            Shadow: Rgba(0, 0, 0, 100)),

        // Plum - dark purple-pink
        DOSIAccent.Plum => new(
            Accent: Rgb(142, 69, 133), AccentDark: Rgb(108, 50, 102),
            WinBg: Rgb(38, 32, 38), WinBorderUnfocused: Rgb(60, 50, 60),
            Chrome: Rgb(34, 28, 34), ChromeUnfocused: Rgb(42, 35, 42), Content: Rgb(30, 24, 30),
            TextPrimary: Rgb(252, 244, 252), TextSecondary: Rgb(186, 170, 184),
            TextDisabled: Rgb(116, 100, 114), TextOnAccent: Rgb(255, 255, 255),
            CtrlBg: Rgb(50, 42, 50), CtrlHover: Rgb(64, 54, 64),
            CtrlPressed: Rgb(42, 35, 42), CtrlBorder: Rgb(80, 68, 80),
            BtnBg: Rgb(48, 40, 48), BtnHover: Rgb(64, 54, 64), BtnPressed: Rgb(40, 32, 40),
            ListBg: Rgb(28, 22, 28), ListHover: Rgb(50, 40, 50), ListSelectedUnfocused: Rgb(64, 54, 64),
            Desktop1: Rgb(45, 22, 42), Desktop2: Rgb(72, 38, 68), Desktop3: Rgb(58, 30, 55),
            Shadow: Rgba(0, 0, 0, 100)),

        // Fuchsia - hot pink-purple
        DOSIAccent.Fuchsia => new(
            Accent: Rgb(255, 0, 200), AccentDark: Rgb(205, 0, 160),
            WinBg: Rgb(40, 32, 40), WinBorderUnfocused: Rgb(66, 50, 64),
            Chrome: Rgb(38, 28, 36), ChromeUnfocused: Rgb(46, 35, 44), Content: Rgb(34, 24, 32),
            TextPrimary: Rgb(255, 240, 252), TextSecondary: Rgb(200, 168, 192),
            TextDisabled: Rgb(128, 98, 122), TextOnAccent: Rgb(255, 255, 255),
            CtrlBg: Rgb(54, 42, 52), CtrlHover: Rgb(70, 54, 66),
            CtrlPressed: Rgb(46, 34, 44), CtrlBorder: Rgb(88, 68, 84),
            BtnBg: Rgb(52, 40, 50), BtnHover: Rgb(70, 54, 66), BtnPressed: Rgb(44, 32, 42),
            ListBg: Rgb(32, 22, 30), ListHover: Rgb(55, 42, 52), ListSelectedUnfocused: Rgb(70, 54, 66),
            Desktop1: Rgb(54, 16, 48), Desktop2: Rgb(88, 28, 78), Desktop3: Rgb(70, 22, 62),
            Shadow: Rgba(0, 0, 0, 100)),

        // Pink - soft hot pink
        DOSIAccent.Pink => new(
            Accent: Rgb(255, 105, 180), AccentDark: Rgb(210, 80, 145),
            WinBg: Rgb(40, 34, 38), WinBorderUnfocused: Rgb(64, 54, 60),
            Chrome: Rgb(36, 30, 34), ChromeUnfocused: Rgb(44, 37, 41), Content: Rgb(32, 26, 30),
            TextPrimary: Rgb(255, 244, 250), TextSecondary: Rgb(196, 174, 188),
            TextDisabled: Rgb(126, 104, 118), TextOnAccent: Rgb(40, 15, 28),
            CtrlBg: Rgb(52, 44, 50), CtrlHover: Rgb(68, 56, 64),
            CtrlPressed: Rgb(44, 36, 42), CtrlBorder: Rgb(86, 70, 80),
            BtnBg: Rgb(50, 42, 48), BtnHover: Rgb(68, 56, 64), BtnPressed: Rgb(42, 34, 40),
            ListBg: Rgb(30, 24, 28), ListHover: Rgb(52, 42, 50), ListSelectedUnfocused: Rgb(68, 56, 64),
            Desktop1: Rgb(56, 28, 48), Desktop2: Rgb(88, 48, 72), Desktop3: Rgb(72, 38, 60),
            Shadow: Rgba(0, 0, 0, 100)),

        // Peach - soft warm peach
        DOSIAccent.Peach => new(
            Accent: Rgb(255, 178, 130), AccentDark: Rgb(215, 142, 100),
            WinBg: Rgb(40, 36, 32), WinBorderUnfocused: Rgb(64, 58, 52),
            Chrome: Rgb(36, 32, 28), ChromeUnfocused: Rgb(44, 39, 35), Content: Rgb(32, 28, 24),
            TextPrimary: Rgb(255, 250, 244), TextSecondary: Rgb(195, 182, 168),
            TextDisabled: Rgb(124, 112, 100), TextOnAccent: Rgb(50, 25, 10),
            CtrlBg: Rgb(52, 46, 42), CtrlHover: Rgb(68, 60, 54),
            CtrlPressed: Rgb(44, 38, 34), CtrlBorder: Rgb(86, 76, 68),
            BtnBg: Rgb(50, 44, 40), BtnHover: Rgb(68, 60, 54), BtnPressed: Rgb(42, 36, 32),
            ListBg: Rgb(30, 26, 22), ListHover: Rgb(52, 46, 40), ListSelectedUnfocused: Rgb(68, 60, 54),
            Desktop1: Rgb(58, 38, 24), Desktop2: Rgb(92, 62, 42), Desktop3: Rgb(72, 50, 32),
            Shadow: Rgba(0, 0, 0, 100)),

        // Apricot - golden peach
        DOSIAccent.Apricot => new(
            Accent: Rgb(251, 175, 110), AccentDark: Rgb(208, 142, 85),
            WinBg: Rgb(40, 36, 30), WinBorderUnfocused: Rgb(64, 58, 50),
            Chrome: Rgb(36, 32, 26), ChromeUnfocused: Rgb(44, 39, 33), Content: Rgb(32, 28, 22),
            TextPrimary: Rgb(255, 248, 240), TextSecondary: Rgb(192, 180, 165),
            TextDisabled: Rgb(120, 110, 96), TextOnAccent: Rgb(45, 25, 10),
            CtrlBg: Rgb(52, 46, 40), CtrlHover: Rgb(68, 60, 52),
            CtrlPressed: Rgb(44, 38, 32), CtrlBorder: Rgb(86, 76, 66),
            BtnBg: Rgb(50, 44, 38), BtnHover: Rgb(68, 60, 52), BtnPressed: Rgb(42, 36, 30),
            ListBg: Rgb(30, 26, 22), ListHover: Rgb(52, 46, 40), ListSelectedUnfocused: Rgb(68, 60, 52),
            Desktop1: Rgb(56, 38, 20), Desktop2: Rgb(90, 62, 35), Desktop3: Rgb(72, 50, 28),
            Shadow: Rgba(0, 0, 0, 100)),

        // Tangerine - vivid orange
        DOSIAccent.Tangerine => new(
            Accent: Rgb(242, 133, 0), AccentDark: Rgb(200, 105, 0),
            WinBg: Rgb(40, 35, 30), WinBorderUnfocused: Rgb(64, 56, 48),
            Chrome: Rgb(36, 31, 26), ChromeUnfocused: Rgb(44, 38, 32), Content: Rgb(32, 27, 22),
            TextPrimary: Rgb(255, 248, 240), TextSecondary: Rgb(190, 178, 162),
            TextDisabled: Rgb(120, 108, 94), TextOnAccent: Rgb(40, 20, 5),
            CtrlBg: Rgb(52, 45, 38), CtrlHover: Rgb(68, 58, 50),
            CtrlPressed: Rgb(44, 38, 32), CtrlBorder: Rgb(86, 74, 64),
            BtnBg: Rgb(50, 43, 36), BtnHover: Rgb(68, 58, 50), BtnPressed: Rgb(42, 35, 30),
            ListBg: Rgb(30, 25, 20), ListHover: Rgb(52, 44, 38), ListSelectedUnfocused: Rgb(68, 58, 50),
            Desktop1: Rgb(55, 32, 12), Desktop2: Rgb(88, 55, 22), Desktop3: Rgb(70, 42, 16),
            Shadow: Rgba(0, 0, 0, 100)),

        // Goldenrod - warm muted gold
        DOSIAccent.Goldenrod => new(
            Accent: Rgb(218, 165, 32), AccentDark: Rgb(178, 132, 22),
            WinBg: Rgb(40, 37, 30), WinBorderUnfocused: Rgb(64, 60, 50),
            Chrome: Rgb(36, 33, 26), ChromeUnfocused: Rgb(44, 40, 33), Content: Rgb(32, 29, 22),
            TextPrimary: Rgb(255, 250, 235), TextSecondary: Rgb(192, 184, 162),
            TextDisabled: Rgb(122, 114, 95), TextOnAccent: Rgb(35, 25, 5),
            CtrlBg: Rgb(52, 48, 40), CtrlHover: Rgb(68, 62, 52),
            CtrlPressed: Rgb(44, 40, 32), CtrlBorder: Rgb(86, 78, 66),
            BtnBg: Rgb(50, 46, 38), BtnHover: Rgb(68, 62, 52), BtnPressed: Rgb(42, 38, 30),
            ListBg: Rgb(30, 27, 20), ListHover: Rgb(52, 46, 38), ListSelectedUnfocused: Rgb(68, 62, 52),
            Desktop1: Rgb(50, 40, 14), Desktop2: Rgb(80, 62, 24), Desktop3: Rgb(64, 50, 18),
            Shadow: Rgba(0, 0, 0, 100)),

        // Lime - bright yellow-green
        DOSIAccent.Lime => new(
            Accent: Rgb(146, 220, 50), AccentDark: Rgb(115, 178, 38),
            WinBg: Rgb(34, 38, 30), WinBorderUnfocused: Rgb(54, 62, 50),
            Chrome: Rgb(30, 35, 26), ChromeUnfocused: Rgb(38, 42, 32), Content: Rgb(26, 30, 22),
            TextPrimary: Rgb(248, 255, 240), TextSecondary: Rgb(170, 185, 158),
            TextDisabled: Rgb(100, 115, 92), TextOnAccent: Rgb(20, 35, 5),
            CtrlBg: Rgb(44, 50, 40), CtrlHover: Rgb(56, 64, 50),
            CtrlPressed: Rgb(36, 42, 32), CtrlBorder: Rgb(70, 80, 64),
            BtnBg: Rgb(42, 48, 38), BtnHover: Rgb(56, 64, 50), BtnPressed: Rgb(34, 40, 30),
            ListBg: Rgb(22, 28, 20), ListHover: Rgb(44, 50, 38), ListSelectedUnfocused: Rgb(56, 64, 50),
            Desktop1: Rgb(28, 50, 12), Desktop2: Rgb(48, 78, 22), Desktop3: Rgb(36, 62, 16),
            Shadow: Rgba(0, 0, 0, 100)),

        // Chartreuse - vibrant green-yellow
        DOSIAccent.Chartreuse => new(
            Accent: Rgb(170, 220, 30), AccentDark: Rgb(135, 178, 22),
            WinBg: Rgb(36, 38, 30), WinBorderUnfocused: Rgb(58, 62, 50),
            Chrome: Rgb(32, 35, 26), ChromeUnfocused: Rgb(40, 42, 32), Content: Rgb(28, 30, 22),
            TextPrimary: Rgb(252, 255, 240), TextSecondary: Rgb(178, 188, 158),
            TextDisabled: Rgb(108, 118, 92), TextOnAccent: Rgb(25, 35, 5),
            CtrlBg: Rgb(46, 50, 40), CtrlHover: Rgb(58, 64, 50),
            CtrlPressed: Rgb(38, 42, 32), CtrlBorder: Rgb(74, 80, 64),
            BtnBg: Rgb(44, 48, 38), BtnHover: Rgb(58, 64, 50), BtnPressed: Rgb(36, 40, 30),
            ListBg: Rgb(24, 28, 20), ListHover: Rgb(46, 50, 38), ListSelectedUnfocused: Rgb(58, 64, 50),
            Desktop1: Rgb(34, 50, 10), Desktop2: Rgb(56, 78, 20), Desktop3: Rgb(44, 62, 14),
            Shadow: Rgba(0, 0, 0, 100)),

        // Sage - soft muted green
        DOSIAccent.Sage => new(
            Accent: Rgb(158, 188, 142), AccentDark: Rgb(122, 152, 110),
            WinBg: Rgb(34, 38, 34), WinBorderUnfocused: Rgb(54, 62, 54),
            Chrome: Rgb(30, 35, 30), ChromeUnfocused: Rgb(38, 42, 38), Content: Rgb(26, 30, 26),
            TextPrimary: Rgb(248, 252, 245), TextSecondary: Rgb(170, 180, 168),
            TextDisabled: Rgb(100, 110, 98), TextOnAccent: Rgb(25, 35, 20),
            CtrlBg: Rgb(44, 50, 44), CtrlHover: Rgb(56, 62, 54),
            CtrlPressed: Rgb(36, 42, 36), CtrlBorder: Rgb(70, 78, 68),
            BtnBg: Rgb(42, 48, 42), BtnHover: Rgb(56, 62, 54), BtnPressed: Rgb(34, 40, 34),
            ListBg: Rgb(22, 28, 22), ListHover: Rgb(44, 50, 42), ListSelectedUnfocused: Rgb(56, 62, 54),
            Desktop1: Rgb(28, 42, 26), Desktop2: Rgb(48, 68, 44), Desktop3: Rgb(38, 55, 35),
            Shadow: Rgba(0, 0, 0, 100)),

        // Pine - deep evergreen
        DOSIAccent.Pine => new(
            Accent: Rgb(1, 121, 111), AccentDark: Rgb(0, 92, 85),
            WinBg: Rgb(28, 36, 34), WinBorderUnfocused: Rgb(45, 58, 55),
            Chrome: Rgb(24, 32, 30), ChromeUnfocused: Rgb(30, 40, 38), Content: Rgb(18, 28, 26),
            TextPrimary: Rgb(238, 252, 248), TextSecondary: Rgb(155, 178, 172),
            TextDisabled: Rgb(85, 108, 102), TextOnAccent: Rgb(255, 255, 255),
            CtrlBg: Rgb(38, 50, 46), CtrlHover: Rgb(48, 62, 58),
            CtrlPressed: Rgb(30, 42, 38), CtrlBorder: Rgb(58, 78, 72),
            BtnBg: Rgb(36, 48, 44), BtnHover: Rgb(48, 62, 58), BtnPressed: Rgb(28, 40, 36),
            ListBg: Rgb(16, 26, 24), ListHover: Rgb(36, 50, 46), ListSelectedUnfocused: Rgb(48, 62, 58),
            Desktop1: Rgb(8, 38, 34), Desktop2: Rgb(14, 60, 55), Desktop3: Rgb(10, 48, 44),
            Shadow: Rgba(0, 0, 0, 110)),

        // Jade - bright green stone
        DOSIAccent.Jade => new(
            Accent: Rgb(0, 168, 107), AccentDark: Rgb(0, 132, 84),
            WinBg: Rgb(30, 38, 34), WinBorderUnfocused: Rgb(48, 62, 56),
            Chrome: Rgb(26, 35, 31), ChromeUnfocused: Rgb(32, 42, 38), Content: Rgb(20, 30, 26),
            TextPrimary: Rgb(240, 255, 248), TextSecondary: Rgb(160, 184, 172),
            TextDisabled: Rgb(90, 114, 102), TextOnAccent: Rgb(255, 255, 255),
            CtrlBg: Rgb(40, 52, 46), CtrlHover: Rgb(50, 65, 58),
            CtrlPressed: Rgb(32, 44, 38), CtrlBorder: Rgb(62, 80, 70),
            BtnBg: Rgb(38, 50, 44), BtnHover: Rgb(50, 65, 58), BtnPressed: Rgb(30, 42, 36),
            ListBg: Rgb(18, 28, 24), ListHover: Rgb(38, 52, 46), ListSelectedUnfocused: Rgb(50, 65, 58),
            Desktop1: Rgb(10, 42, 30), Desktop2: Rgb(18, 68, 50), Desktop3: Rgb(14, 55, 40),
            Shadow: Rgba(0, 0, 0, 100)),

        // Sea Green - cool seafoam
        DOSIAccent.SeaGreen => new(
            Accent: Rgb(46, 139, 87), AccentDark: Rgb(34, 108, 68),
            WinBg: Rgb(30, 36, 33), WinBorderUnfocused: Rgb(48, 60, 54),
            Chrome: Rgb(26, 33, 30), ChromeUnfocused: Rgb(32, 40, 36), Content: Rgb(20, 28, 25),
            TextPrimary: Rgb(240, 252, 246), TextSecondary: Rgb(160, 180, 170),
            TextDisabled: Rgb(90, 110, 100), TextOnAccent: Rgb(255, 255, 255),
            CtrlBg: Rgb(40, 50, 45), CtrlHover: Rgb(50, 62, 56),
            CtrlPressed: Rgb(32, 42, 38), CtrlBorder: Rgb(62, 78, 70),
            BtnBg: Rgb(38, 48, 43), BtnHover: Rgb(50, 62, 56), BtnPressed: Rgb(30, 40, 36),
            ListBg: Rgb(18, 26, 22), ListHover: Rgb(38, 50, 44), ListSelectedUnfocused: Rgb(50, 62, 56),
            Desktop1: Rgb(14, 40, 28), Desktop2: Rgb(24, 65, 46), Desktop3: Rgb(18, 52, 38),
            Shadow: Rgba(0, 0, 0, 100)),

        // Cerulean - rich sky blue
        DOSIAccent.Cerulean => new(
            Accent: Rgb(0, 123, 167), AccentDark: Rgb(0, 95, 132),
            WinBg: Rgb(28, 34, 40), WinBorderUnfocused: Rgb(45, 55, 65),
            Chrome: Rgb(24, 30, 38), ChromeUnfocused: Rgb(30, 38, 46), Content: Rgb(18, 26, 34),
            TextPrimary: Rgb(238, 248, 255), TextSecondary: Rgb(158, 178, 195),
            TextDisabled: Rgb(88, 108, 128), TextOnAccent: Rgb(255, 255, 255),
            CtrlBg: Rgb(38, 46, 55), CtrlHover: Rgb(48, 58, 70),
            CtrlPressed: Rgb(30, 38, 48), CtrlBorder: Rgb(58, 72, 88),
            BtnBg: Rgb(36, 44, 52), BtnHover: Rgb(48, 58, 70), BtnPressed: Rgb(28, 36, 44),
            ListBg: Rgb(16, 22, 30), ListHover: Rgb(36, 45, 56), ListSelectedUnfocused: Rgb(48, 58, 70),
            Desktop1: Rgb(8, 35, 55), Desktop2: Rgb(14, 58, 88), Desktop3: Rgb(10, 45, 70),
            Shadow: Rgba(0, 0, 0, 110)),

        // Sky Blue - light airy blue
        DOSIAccent.SkyBlue => new(
            Accent: Rgb(135, 206, 235), AccentDark: Rgb(98, 168, 200),
            WinBg: Rgb(30, 36, 42), WinBorderUnfocused: Rgb(48, 58, 68),
            Chrome: Rgb(26, 32, 38), ChromeUnfocused: Rgb(32, 40, 47), Content: Rgb(20, 28, 34),
            TextPrimary: Rgb(238, 248, 255), TextSecondary: Rgb(160, 178, 195),
            TextDisabled: Rgb(90, 108, 125), TextOnAccent: Rgb(15, 35, 50),
            CtrlBg: Rgb(40, 48, 56), CtrlHover: Rgb(52, 62, 72),
            CtrlPressed: Rgb(32, 40, 48), CtrlBorder: Rgb(62, 75, 88),
            BtnBg: Rgb(38, 46, 54), BtnHover: Rgb(52, 62, 72), BtnPressed: Rgb(30, 38, 46),
            ListBg: Rgb(20, 26, 32), ListHover: Rgb(40, 48, 58), ListSelectedUnfocused: Rgb(52, 62, 72),
            Desktop1: Rgb(20, 45, 65), Desktop2: Rgb(38, 75, 105), Desktop3: Rgb(28, 58, 85),
            Shadow: Rgba(0, 0, 0, 100)),

        // Cobalt - intense pure blue
        DOSIAccent.Cobalt => new(
            Accent: Rgb(0, 71, 171), AccentDark: Rgb(0, 55, 135),
            WinBg: Rgb(28, 30, 40), WinBorderUnfocused: Rgb(46, 50, 65),
            Chrome: Rgb(24, 26, 38), ChromeUnfocused: Rgb(30, 33, 46), Content: Rgb(18, 20, 32),
            TextPrimary: Rgb(232, 240, 255), TextSecondary: Rgb(152, 165, 195),
            TextDisabled: Rgb(82, 95, 125), TextOnAccent: Rgb(255, 255, 255),
            CtrlBg: Rgb(38, 42, 55), CtrlHover: Rgb(48, 54, 70),
            CtrlPressed: Rgb(30, 34, 48), CtrlBorder: Rgb(60, 68, 88),
            BtnBg: Rgb(36, 40, 52), BtnHover: Rgb(48, 54, 70), BtnPressed: Rgb(28, 32, 44),
            ListBg: Rgb(16, 18, 28), ListHover: Rgb(36, 40, 55), ListSelectedUnfocused: Rgb(48, 54, 70),
            Desktop1: Rgb(5, 18, 55), Desktop2: Rgb(10, 32, 88), Desktop3: Rgb(8, 25, 70),
            Shadow: Rgba(0, 0, 0, 120)),

        // Navy - traditional dark blue
        DOSIAccent.Navy => new(
            Accent: Rgb(40, 60, 130), AccentDark: Rgb(28, 42, 95),
            WinBg: Rgb(26, 28, 36), WinBorderUnfocused: Rgb(44, 48, 60),
            Chrome: Rgb(22, 24, 32), ChromeUnfocused: Rgb(28, 30, 40), Content: Rgb(16, 18, 28),
            TextPrimary: Rgb(228, 235, 250), TextSecondary: Rgb(148, 158, 185),
            TextDisabled: Rgb(78, 88, 115), TextOnAccent: Rgb(255, 255, 255),
            CtrlBg: Rgb(36, 40, 50), CtrlHover: Rgb(46, 50, 64),
            CtrlPressed: Rgb(28, 32, 42), CtrlBorder: Rgb(58, 64, 82),
            BtnBg: Rgb(34, 38, 48), BtnHover: Rgb(46, 50, 64), BtnPressed: Rgb(26, 30, 40),
            ListBg: Rgb(14, 16, 24), ListHover: Rgb(34, 38, 50), ListSelectedUnfocused: Rgb(46, 50, 64),
            Desktop1: Rgb(5, 10, 35), Desktop2: Rgb(10, 22, 60), Desktop3: Rgb(8, 16, 48),
            Shadow: Rgba(0, 0, 0, 120)),

        // Burgundy - rich dark wine red
        DOSIAccent.Burgundy => new(
            Accent: Rgb(128, 0, 32), AccentDark: Rgb(95, 0, 22),
            WinBg: Rgb(36, 30, 32), WinBorderUnfocused: Rgb(58, 48, 50),
            Chrome: Rgb(32, 26, 28), ChromeUnfocused: Rgb(40, 33, 35), Content: Rgb(28, 22, 24),
            TextPrimary: Rgb(252, 240, 244), TextSecondary: Rgb(188, 168, 174),
            TextDisabled: Rgb(118, 98, 104), TextOnAccent: Rgb(255, 255, 255),
            CtrlBg: Rgb(48, 40, 42), CtrlHover: Rgb(62, 52, 54),
            CtrlPressed: Rgb(40, 32, 35), CtrlBorder: Rgb(78, 66, 70),
            BtnBg: Rgb(46, 38, 40), BtnHover: Rgb(62, 52, 54), BtnPressed: Rgb(38, 30, 32),
            ListBg: Rgb(26, 20, 22), ListHover: Rgb(48, 38, 42), ListSelectedUnfocused: Rgb(62, 52, 54),
            Desktop1: Rgb(40, 12, 20), Desktop2: Rgb(64, 22, 32), Desktop3: Rgb(50, 16, 25),
            Shadow: Rgba(0, 0, 0, 110)),

        // Maroon - deep brownish red
        DOSIAccent.Maroon => new(
            Accent: Rgb(128, 0, 0), AccentDark: Rgb(95, 0, 0),
            WinBg: Rgb(36, 30, 30), WinBorderUnfocused: Rgb(58, 48, 48),
            Chrome: Rgb(32, 26, 26), ChromeUnfocused: Rgb(40, 33, 33), Content: Rgb(28, 22, 22),
            TextPrimary: Rgb(252, 240, 240), TextSecondary: Rgb(188, 168, 168),
            TextDisabled: Rgb(118, 98, 98), TextOnAccent: Rgb(255, 255, 255),
            CtrlBg: Rgb(48, 40, 40), CtrlHover: Rgb(62, 52, 52),
            CtrlPressed: Rgb(40, 32, 32), CtrlBorder: Rgb(78, 66, 66),
            BtnBg: Rgb(46, 38, 38), BtnHover: Rgb(62, 52, 52), BtnPressed: Rgb(38, 30, 30),
            ListBg: Rgb(26, 20, 20), ListHover: Rgb(48, 38, 38), ListSelectedUnfocused: Rgb(62, 52, 52),
            Desktop1: Rgb(38, 12, 12), Desktop2: Rgb(62, 22, 22), Desktop3: Rgb(48, 16, 16),
            Shadow: Rgba(0, 0, 0, 110)),

        // Wine - dusky red-purple
        DOSIAccent.Wine => new(
            Accent: Rgb(114, 47, 55), AccentDark: Rgb(85, 35, 42),
            WinBg: Rgb(36, 30, 32), WinBorderUnfocused: Rgb(58, 48, 50),
            Chrome: Rgb(32, 26, 28), ChromeUnfocused: Rgb(40, 33, 35), Content: Rgb(28, 22, 24),
            TextPrimary: Rgb(250, 240, 242), TextSecondary: Rgb(184, 166, 170),
            TextDisabled: Rgb(116, 96, 102), TextOnAccent: Rgb(255, 255, 255),
            CtrlBg: Rgb(48, 40, 42), CtrlHover: Rgb(62, 52, 54),
            CtrlPressed: Rgb(40, 32, 35), CtrlBorder: Rgb(78, 66, 70),
            BtnBg: Rgb(46, 38, 40), BtnHover: Rgb(62, 52, 54), BtnPressed: Rgb(38, 30, 32),
            ListBg: Rgb(26, 20, 22), ListHover: Rgb(48, 38, 42), ListSelectedUnfocused: Rgb(62, 52, 54),
            Desktop1: Rgb(38, 18, 22), Desktop2: Rgb(60, 30, 36), Desktop3: Rgb(48, 24, 28),
            Shadow: Rgba(0, 0, 0, 110)),

        // Mocha - warm coffee brown
        DOSIAccent.Mocha => new(
            Accent: Rgb(128, 92, 73), AccentDark: Rgb(98, 70, 55),
            WinBg: Rgb(38, 34, 32), WinBorderUnfocused: Rgb(60, 54, 50),
            Chrome: Rgb(34, 30, 28), ChromeUnfocused: Rgb(42, 38, 35), Content: Rgb(30, 26, 24),
            TextPrimary: Rgb(252, 246, 240), TextSecondary: Rgb(188, 178, 168),
            TextDisabled: Rgb(118, 108, 100), TextOnAccent: Rgb(255, 255, 255),
            CtrlBg: Rgb(50, 44, 40), CtrlHover: Rgb(64, 56, 52),
            CtrlPressed: Rgb(42, 36, 34), CtrlBorder: Rgb(80, 72, 66),
            BtnBg: Rgb(48, 42, 38), BtnHover: Rgb(64, 56, 52), BtnPressed: Rgb(40, 34, 32),
            ListBg: Rgb(28, 24, 22), ListHover: Rgb(50, 44, 40), ListSelectedUnfocused: Rgb(64, 56, 52),
            Desktop1: Rgb(40, 30, 22), Desktop2: Rgb(64, 50, 38), Desktop3: Rgb(50, 40, 30),
            Shadow: Rgba(0, 0, 0, 100)),

        // Chocolate - rich dark cocoa
        DOSIAccent.Chocolate => new(
            Accent: Rgb(123, 63, 0), AccentDark: Rgb(92, 48, 0),
            WinBg: Rgb(38, 33, 30), WinBorderUnfocused: Rgb(60, 52, 48),
            Chrome: Rgb(34, 30, 26), ChromeUnfocused: Rgb(42, 36, 33), Content: Rgb(30, 25, 22),
            TextPrimary: Rgb(252, 244, 235), TextSecondary: Rgb(188, 175, 162),
            TextDisabled: Rgb(118, 105, 92), TextOnAccent: Rgb(255, 255, 255),
            CtrlBg: Rgb(50, 42, 38), CtrlHover: Rgb(64, 54, 48),
            CtrlPressed: Rgb(42, 35, 32), CtrlBorder: Rgb(80, 68, 60),
            BtnBg: Rgb(48, 40, 36), BtnHover: Rgb(64, 54, 48), BtnPressed: Rgb(40, 32, 30),
            ListBg: Rgb(28, 22, 20), ListHover: Rgb(50, 42, 38), ListSelectedUnfocused: Rgb(64, 54, 48),
            Desktop1: Rgb(38, 22, 10), Desktop2: Rgb(60, 38, 18), Desktop3: Rgb(48, 30, 14),
            Shadow: Rgba(0, 0, 0, 110)),

        // Sand - warm pale neutral
        DOSIAccent.Sand => new(
            Accent: Rgb(194, 178, 128), AccentDark: Rgb(155, 142, 100),
            WinBg: Rgb(40, 38, 32), WinBorderUnfocused: Rgb(64, 60, 52),
            Chrome: Rgb(36, 34, 28), ChromeUnfocused: Rgb(44, 41, 35), Content: Rgb(32, 30, 24),
            TextPrimary: Rgb(255, 252, 245), TextSecondary: Rgb(192, 184, 168),
            TextDisabled: Rgb(122, 114, 98), TextOnAccent: Rgb(40, 32, 12),
            CtrlBg: Rgb(52, 48, 40), CtrlHover: Rgb(68, 62, 52),
            CtrlPressed: Rgb(44, 40, 32), CtrlBorder: Rgb(86, 78, 66),
            BtnBg: Rgb(50, 46, 38), BtnHover: Rgb(68, 62, 52), BtnPressed: Rgb(42, 38, 30),
            ListBg: Rgb(30, 28, 22), ListHover: Rgb(52, 48, 40), ListSelectedUnfocused: Rgb(68, 62, 52),
            Desktop1: Rgb(48, 42, 26), Desktop2: Rgb(78, 68, 42), Desktop3: Rgb(62, 54, 32),
            Shadow: Rgba(0, 0, 0, 100)),

        // Charcoal - cool dark gray
        DOSIAccent.Charcoal => new(
            Accent: Rgb(85, 92, 100), AccentDark: Rgb(62, 68, 75),
            WinBg: Rgb(28, 30, 32), WinBorderUnfocused: Rgb(46, 50, 54),
            Chrome: Rgb(25, 27, 30), ChromeUnfocused: Rgb(32, 34, 38), Content: Rgb(20, 22, 25),
            TextPrimary: Rgb(232, 235, 240), TextSecondary: Rgb(150, 158, 168),
            TextDisabled: Rgb(85, 92, 102), TextOnAccent: Rgb(255, 255, 255),
            CtrlBg: Rgb(38, 42, 46), CtrlHover: Rgb(48, 52, 58),
            CtrlPressed: Rgb(30, 34, 38), CtrlBorder: Rgb(60, 66, 72),
            BtnBg: Rgb(36, 40, 44), BtnHover: Rgb(48, 52, 58), BtnPressed: Rgb(28, 32, 36),
            ListBg: Rgb(18, 20, 22), ListHover: Rgb(38, 42, 46), ListSelectedUnfocused: Rgb(48, 52, 58),
            Desktop1: Rgb(18, 22, 26), Desktop2: Rgb(32, 38, 44), Desktop3: Rgb(24, 30, 35),
            Shadow: Rgba(0, 0, 0, 120)),

        // Steel - cool industrial blue
        DOSIAccent.Steel => new(
            Accent: Rgb(70, 130, 180), AccentDark: Rgb(52, 100, 142),
            WinBg: Rgb(30, 34, 40), WinBorderUnfocused: Rgb(48, 56, 65),
            Chrome: Rgb(26, 30, 36), ChromeUnfocused: Rgb(33, 38, 44), Content: Rgb(20, 25, 32),
            TextPrimary: Rgb(238, 245, 255), TextSecondary: Rgb(160, 175, 190),
            TextDisabled: Rgb(90, 105, 122), TextOnAccent: Rgb(255, 255, 255),
            CtrlBg: Rgb(40, 46, 54), CtrlHover: Rgb(50, 58, 68),
            CtrlPressed: Rgb(32, 38, 46), CtrlBorder: Rgb(62, 72, 86),
            BtnBg: Rgb(38, 44, 52), BtnHover: Rgb(50, 58, 68), BtnPressed: Rgb(30, 36, 44),
            ListBg: Rgb(18, 22, 28), ListHover: Rgb(38, 46, 54), ListSelectedUnfocused: Rgb(50, 58, 68),
            Desktop1: Rgb(18, 32, 50), Desktop2: Rgb(34, 55, 80), Desktop3: Rgb(25, 42, 65),
            Shadow: Rgba(0, 0, 0, 110)),

        // Onyx - deep neutral black
        DOSIAccent.Onyx => new(
            Accent: Rgb(80, 80, 85), AccentDark: Rgb(58, 58, 62),
            WinBg: Rgb(22, 22, 24), WinBorderUnfocused: Rgb(40, 40, 44),
            Chrome: Rgb(18, 18, 20), ChromeUnfocused: Rgb(25, 25, 28), Content: Rgb(14, 14, 16),
            TextPrimary: Rgb(228, 228, 232), TextSecondary: Rgb(148, 148, 155),
            TextDisabled: Rgb(82, 82, 88), TextOnAccent: Rgb(255, 255, 255),
            CtrlBg: Rgb(32, 32, 35), CtrlHover: Rgb(44, 44, 48),
            CtrlPressed: Rgb(24, 24, 26), CtrlBorder: Rgb(54, 54, 58),
            BtnBg: Rgb(30, 30, 32), BtnHover: Rgb(44, 44, 48), BtnPressed: Rgb(22, 22, 24),
            ListBg: Rgb(12, 12, 14), ListHover: Rgb(32, 32, 35), ListSelectedUnfocused: Rgb(44, 44, 48),
            Desktop1: Rgb(8, 8, 10), Desktop2: Rgb(20, 20, 24), Desktop3: Rgb(14, 14, 17),
            Shadow: Rgba(0, 0, 0, 130)),

        _ => GetAccentColors(DOSIAccent.DarkBlue)
    };

    private record AccentColors(
        Color Accent, Color AccentDark,
        Color WinBg, Color WinBorderUnfocused,
        Color Chrome, Color ChromeUnfocused, Color Content,
        Color TextPrimary, Color TextSecondary, Color TextDisabled, Color TextOnAccent,
        Color CtrlBg, Color CtrlHover, Color CtrlPressed, Color CtrlBorder,
        Color BtnBg, Color BtnHover, Color BtnPressed,
        Color ListBg, Color ListHover, Color ListSelectedUnfocused,
        Color Desktop1, Color Desktop2, Color Desktop3,
        Color Shadow);

    #endregion

    #region Helpers

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
    private static Color Rgba(byte r, byte g, byte b, byte a) => Color.FromArgb(a, r, g, b);
    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    /// <summary>
    /// Softens a color by reducing its saturation slightly while maintaining brightness.
    /// </summary>
    private static Color SoftenColor(Color c, double amount = 0.15)
    {
        // Blend toward a gray of similar brightness
        var gray = (byte)((c.R + c.G + c.B) / 3);
        return Color.FromRgb(
            (byte)(c.R + (gray - c.R) * amount),
            (byte)(c.G + (gray - c.G) * amount),
            (byte)(c.B + (gray - c.B) * amount));
    }

    /// <summary>
    /// Tints a base color with the accent color for a more cohesive look.
    /// </summary>
    /// <param name="baseColor">The base color to tint (typically a gray).</param>
    /// <param name="accent">The accent color to blend in.</param>
    /// <param name="amount">Amount of accent to blend (0.0 = no tint, 1.0 = full accent). Recommended: 0.08-0.15</param>
    private static Color TintWithAccent(Color baseColor, Color accent, double amount = 0.1)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)(baseColor.R + (accent.R - baseColor.R) * amount),
            (byte)(baseColor.G + (accent.G - baseColor.G) * amount),
            (byte)(baseColor.B + (accent.B - baseColor.B) * amount));
    }

    /// <summary>
    /// Creates accent-tinted window colors from base gray values.
    /// This ensures all accents have cohesive accent-tinted UI elements.
    /// </summary>
    /// <param name="accent">The primary accent color.</param>
    /// <param name="baseLightness">Base lightness level (0-255) for the darkest element.</param>
    /// <param name="isDark">Whether this is a dark accent.</param>
    private static (Color WinBg, Color Chrome, Color ChromeUnfocused, Color Content, Color BtnHover) 
        CreateAccentTintedWindowColors(Color accent, int baseLightness, bool isDark = true)
    {
        const double tintAmount = 0.35; // Strong accent tint for visible color

        if (isDark)
        {
            var winBg = TintWithAccent(Rgb((byte)(baseLightness + 15), (byte)(baseLightness + 15), (byte)(baseLightness + 15)), accent, tintAmount);
            var chrome = TintWithAccent(Rgb((byte)(baseLightness + 10), (byte)(baseLightness + 10), (byte)(baseLightness + 10)), accent, tintAmount * 0.9);
            var chromeUnfocused = TintWithAccent(Rgb((byte)(baseLightness + 18), (byte)(baseLightness + 18), (byte)(baseLightness + 18)), accent, tintAmount * 0.7);
            // Content area uses darker base (baseLightness - 5) for better contrast
            var content = TintWithAccent(Rgb((byte)Math.Max(0, baseLightness - 5), (byte)Math.Max(0, baseLightness - 5), (byte)Math.Max(0, baseLightness - 5)), accent, tintAmount * 0.6);
            var btnHover = TintWithAccent(Rgb((byte)(baseLightness + 35), (byte)(baseLightness + 35), (byte)(baseLightness + 35)), accent, tintAmount * 1.2);
            return (winBg, chrome, chromeUnfocused, content, btnHover);
        }
        else
        {
            // Light accent - moderate tinting
            var winBg = TintWithAccent(Rgb(243, 243, 243), accent, 0.08);
            var chrome = TintWithAccent(Rgb(255, 255, 255), accent, 0.06);
            var chromeUnfocused = TintWithAccent(Rgb(240, 240, 240), accent, 0.05);
            var content = TintWithAccent(Rgb(250, 250, 250), accent, 0.04); // Slightly darker for light accent
            var btnHover = TintWithAccent(Rgb(229, 229, 229), accent, 0.12);
            return (winBg, chrome, chromeUnfocused, content, btnHover);
        }
    }

    private static LinearGradientBrush CreateGradient(Color start, Color end) => new()
    {
        StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
        EndPoint = new Avalonia.RelativePoint(1, 1, Avalonia.RelativeUnit.Relative),
        GradientStops = [new(start, 0), new(end, 1)]
    };

    private static LinearGradientBrush CreateGradient(Color start, Color mid, Color end) => new()
    {
        StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
        EndPoint = new Avalonia.RelativePoint(1, 1, Avalonia.RelativeUnit.Relative),
        GradientStops = [new(start, 0), new(mid, 0.5), new(end, 1)]
    };

    #endregion

    #region Animated accent Transitions

    private DispatcherTimer? _accentAnimTimer;
    private bool _suppressAccentChanged;

    /// <summary>
    /// Smoothly transitions the active accent to <paramref name="target"/> by interpolating
    /// every color in the palette (accent, chrome, text, controls, desktop, shadow) over
    /// <paramref name="duration"/>. Fires <see cref="AccentChanged"/> on every tick so the
    /// entire UI fades together.
    /// </summary>
    public void ApplyAccentAnimated(DOSIAccent target, TimeSpan duration)
    {
        // Cancel any in-flight animation.
        _accentAnimTimer?.Stop();
        _accentAnimTimer = null;

        if (duration <= TimeSpan.Zero)
        {
            ApplyAccent(target);
            return;
        }

        // 1. Snapshot the current (start) palette.
        var startSnapshot = CapturePalette();

        // 2. Compute the target palette by applying the target accent silently,
        //    snapshotting, then restoring the start palette so visuals don't jump.
        _suppressAccentChanged = true;
        ApplyAccent(target);
        var endSnapshot = CapturePalette();
        ApplyPalette(startSnapshot);
        _suppressAccentChanged = false;

        // CurrentAccent was set to target by the silent ApplyAccent above; that's what
        // we want at the end of the animation, so leave it.

        var totalMs = duration.TotalMilliseconds;
        var startTime = DateTime.UtcNow;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _accentAnimTimer = timer;

        timer.Tick += (_, _) =>
        {
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            var t = Math.Clamp(elapsed / totalMs, 0d, 1d);
            // Ease-in-out cubic.
            var eased = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

            var lerped = LerpPalette(startSnapshot, endSnapshot, eased);
            ApplyPalette(lerped);
            AccentChanged?.Invoke(this, EventArgs.Empty);

            if (t >= 1d)
            {
                timer.Stop();
                if (ReferenceEquals(_accentAnimTimer, timer)) _accentAnimTimer = null;

                // Snap to exact target palette to eliminate any rounding drift.
                ApplyPalette(endSnapshot);
                AccentChanged?.Invoke(this, EventArgs.Empty);
            }
        };
        timer.Start();
    }

    /// <summary>Captures every animatable color in the current palette.</summary>
    private PaletteSnapshot CapturePalette() => new(
        AccentPrimary, AccentSecondary, AccentTertiary,
        WindowBackground, WindowBorderFocused, WindowBorderUnfocused,
        WindowChrome, WindowChromeUnfocused, WindowContent,
        TextPrimary, TextSecondary, TextDisabled, TextOnAccent,
        ControlBackground, ControlBackgroundHover, ControlBackgroundPressed, ControlBorder,
        ButtonBackground, ButtonBackgroundHover, ButtonBackgroundPressed, CloseButtonHover,
        ListBoxBackground, ListBoxItemHover, ListBoxItemSelected, ListBoxItemSelectedUnfocused,
        DesktopBackground1, DesktopBackground2, DesktopBackground3,
        ShadowColor);

    /// <summary>Writes a palette snapshot back onto every color property.</summary>
    private void ApplyPalette(PaletteSnapshot p)
    {
        AccentPrimary = p.AccentPrimary;
        AccentSecondary = p.AccentSecondary;
        AccentTertiary = p.AccentTertiary;
        WindowBackground = p.WindowBackground;
        WindowBorderFocused = p.WindowBorderFocused;
        WindowBorderUnfocused = p.WindowBorderUnfocused;
        WindowChrome = p.WindowChrome;
        WindowChromeUnfocused = p.WindowChromeUnfocused;
        WindowContent = p.WindowContent;
        TextPrimary = p.TextPrimary;
        TextSecondary = p.TextSecondary;
        TextDisabled = p.TextDisabled;
        TextOnAccent = p.TextOnAccent;
        ControlBackground = p.ControlBackground;
        ControlBackgroundHover = p.ControlBackgroundHover;
        ControlBackgroundPressed = p.ControlBackgroundPressed;
        ControlBorder = p.ControlBorder;
        ButtonBackground = p.ButtonBackground;
        ButtonBackgroundHover = p.ButtonBackgroundHover;
        ButtonBackgroundPressed = p.ButtonBackgroundPressed;
        CloseButtonHover = p.CloseButtonHover;
        ListBoxBackground = p.ListBoxBackground;
        ListBoxItemHover = p.ListBoxItemHover;
        ListBoxItemSelected = p.ListBoxItemSelected;
        ListBoxItemSelectedUnfocused = p.ListBoxItemSelectedUnfocused;
        DesktopBackground1 = p.DesktopBackground1;
        DesktopBackground2 = p.DesktopBackground2;
        DesktopBackground3 = p.DesktopBackground3;
        ShadowColor = p.ShadowColor;
        RefreshCachedBrushes();
    }

    private static PaletteSnapshot LerpPalette(PaletteSnapshot a, PaletteSnapshot b, double t) => new(
        LerpColor(a.AccentPrimary, b.AccentPrimary, t),
        LerpColor(a.AccentSecondary, b.AccentSecondary, t),
        LerpColor(a.AccentTertiary, b.AccentTertiary, t),
        LerpColor(a.WindowBackground, b.WindowBackground, t),
        LerpColor(a.WindowBorderFocused, b.WindowBorderFocused, t),
        LerpColor(a.WindowBorderUnfocused, b.WindowBorderUnfocused, t),
        LerpColor(a.WindowChrome, b.WindowChrome, t),
        LerpColor(a.WindowChromeUnfocused, b.WindowChromeUnfocused, t),
        LerpColor(a.WindowContent, b.WindowContent, t),
        LerpColor(a.TextPrimary, b.TextPrimary, t),
        LerpColor(a.TextSecondary, b.TextSecondary, t),
        LerpColor(a.TextDisabled, b.TextDisabled, t),
        LerpColor(a.TextOnAccent, b.TextOnAccent, t),
        LerpColor(a.ControlBackground, b.ControlBackground, t),
        LerpColor(a.ControlBackgroundHover, b.ControlBackgroundHover, t),
        LerpColor(a.ControlBackgroundPressed, b.ControlBackgroundPressed, t),
        LerpColor(a.ControlBorder, b.ControlBorder, t),
        LerpColor(a.ButtonBackground, b.ButtonBackground, t),
        LerpColor(a.ButtonBackgroundHover, b.ButtonBackgroundHover, t),
        LerpColor(a.ButtonBackgroundPressed, b.ButtonBackgroundPressed, t),
        LerpColor(a.CloseButtonHover, b.CloseButtonHover, t),
        LerpColor(a.ListBoxBackground, b.ListBoxBackground, t),
        LerpColor(a.ListBoxItemHover, b.ListBoxItemHover, t),
        LerpColor(a.ListBoxItemSelected, b.ListBoxItemSelected, t),
        LerpColor(a.ListBoxItemSelectedUnfocused, b.ListBoxItemSelectedUnfocused, t),
        LerpColor(a.DesktopBackground1, b.DesktopBackground1, t),
        LerpColor(a.DesktopBackground2, b.DesktopBackground2, t),
        LerpColor(a.DesktopBackground3, b.DesktopBackground3, t),
        LerpColor(a.ShadowColor, b.ShadowColor, t));

    private static Color LerpColor(Color a, Color b, double t)
    {
        return Color.FromArgb(
            (byte)Math.Clamp(a.A + (b.A - a.A) * t, 0, 255),
            (byte)Math.Clamp(a.R + (b.R - a.R) * t, 0, 255),
            (byte)Math.Clamp(a.G + (b.G - a.G) * t, 0, 255),
            (byte)Math.Clamp(a.B + (b.B - a.B) * t, 0, 255));
    }

    /// <summary>
    /// A frozen snapshot of every animatable color in the palette, used to interpolate
    /// between two accents during <see cref="ApplyAccentAnimated"/>.
    /// </summary>
    private sealed record PaletteSnapshot(
        Color AccentPrimary, Color AccentSecondary, Color AccentTertiary,
        Color WindowBackground, Color WindowBorderFocused, Color WindowBorderUnfocused,
        Color WindowChrome, Color WindowChromeUnfocused, Color WindowContent,
        Color TextPrimary, Color TextSecondary, Color TextDisabled, Color TextOnAccent,
        Color ControlBackground, Color ControlBackgroundHover, Color ControlBackgroundPressed, Color ControlBorder,
        Color ButtonBackground, Color ButtonBackgroundHover, Color ButtonBackgroundPressed, Color CloseButtonHover,
        Color ListBoxBackground, Color ListBoxItemHover, Color ListBoxItemSelected, Color ListBoxItemSelectedUnfocused,
        Color DesktopBackground1, Color DesktopBackground2, Color DesktopBackground3,
        Color ShadowColor);

    #endregion
}

/// <summary>
/// Available accents for the DOSI operating system.
/// </summary>
public enum DOSIAccent
{
    DarkBlue,
    DarkPurple,
    DarkGreen,
    DarkOrange,
    DarkRed,
    DarkTeal,
    Light,
    Midnight,
    // New accents
    RoseGold,
    Coral,
    Lavender,
    Mint,
    Slate,
    Copper,
    Sapphire,
    Emerald,
    Ruby,
    Amber,
    Violet,
    Crimson,
    Forest,
    Ocean,
    Sunset,
    Storm,
    Bronze,
    Indigo,
    Magenta,
    Olive,
    // Expanded palette - 30 additional accents
    Turquoise,
    Cyan,
    Aqua,
    Periwinkle,
    Plum,
    Fuchsia,
    Pink,
    Peach,
    Apricot,
    Tangerine,
    Goldenrod,
    Lime,
    Chartreuse,
    Sage,
    Pine,
    Jade,
    SeaGreen,
    Cerulean,
    SkyBlue,
    Cobalt,
    Navy,
    Burgundy,
    Maroon,
    Wine,
    Mocha,
    Chocolate,
    Sand,
    Charcoal,
    Steel,
    Onyx
}
