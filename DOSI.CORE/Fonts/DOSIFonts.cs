using Avalonia.Media;

namespace DOSI.CORE;

/// <summary>
/// Single source of truth for every font shipped inside DOSI. The fonts are
/// embedded as <c>AvaloniaResource</c>s under <c>DOSI.CORE/Resources/Fonts/</c>
/// (see <c>DOSI.CORE.csproj</c>) and exposed here as well-known
/// <see cref="FontFamily"/> instances.
///
/// USAGE GUIDELINES
/// ----------------
///   * The application-wide default family is wired up in
///     <c>DAX.OSI.Program.BuildAvaloniaApp</c> via
///     <c>FontManagerOptions.DefaultFamilyName</c>. Every Avalonia control
///     that doesn't explicitly set <c>FontFamily</c> inherits Noto Sans
///     automatically - so individual DOSI controls do NOT need to set
///     <see cref="UI"/> by hand.
///   * Use <see cref="Mono"/> only for places that need a fixed-width font
///     (terminals, code editors, hex / log views). Currently consumed by
///     <c>DOSITerminalIO</c>.
///   * A subtle, universal text drop shadow is layered on top of every
///     <c>TextBlock</c> by <c>App.Initialize</c> via
///     <see cref="CreateUiTextDropShadow"/>. Because <c>DOSITerminalIO</c>
///     and any other <see cref="Mono"/>-using control draws its glyphs with
///     <c>DrawingContext.DrawText</c> directly (i.e. without instantiating
///     a <c>TextBlock</c>), the shadow style passes them by - which is the
///     intended exclusion boundary: the soft UI-text shadow stays on the
///     UI font, and monospaced terminal / code text remains crisp and
///     pixel-aligned.
/// </summary>
public static class DOSIFonts
{
    /// <summary>
    /// Avalonia resource URI of the bundled Noto Sans face, in the
    /// <c>avares://Assembly/Path#Family</c> form expected by
    /// <c>FontManagerOptions.DefaultFamilyName</c> and by
    /// <see cref="FontFamily"/> constructors. Kept as a string constant so
    /// it can be passed to APIs that take a raw URI without first
    /// constructing a <see cref="FontFamily"/>.
    /// </summary>
    public const string UIFamilyUri =
        "avares://DOSI.CORE/Resources/Fonts/NotoSans-Regular.ttf#Noto Sans";

    /// <summary>
    /// Avalonia resource URI of the bundled Cascadia Code face. Same form
    /// as <see cref="UIFamilyUri"/>.
    /// </summary>
    public const string MonoFamilyUri =
        "avares://DOSI.CORE/Resources/Fonts/CascadiaCode-Regular.ttf#Cascadia Code";

    /// <summary>
    /// The Noto Sans regular face shipped with DOSI. This is the universal
    /// UI font - registered as the application default in
    /// <c>BuildAvaloniaApp</c>, so every screen, window, and control
    /// inherits it without needing an explicit <c>FontFamily</c> setter.
    /// </summary>
    public static readonly FontFamily UI = new(UIFamilyUri);

    /// <summary>
    /// Cascadia Code regular - the canonical monospaced face for any
    /// character-cell-aligned content (terminals, code editors). Use this
    /// instead of falling back to system "Consolas" / "Courier New" so the
    /// monospace look stays identical across every host platform.
    /// </summary>
    public static readonly FontFamily Mono = new(MonoFamilyUri);

    /// <summary>
    /// Builds the canonical UI-text drop shadow applied globally to every
    /// <c>TextBlock</c> by the application style in <c>App.Initialize</c>.
    /// Kept intentionally subtle: a 1px downward offset, a small 3px blur,
    /// and a low-alpha black so the shadow reads as depth without smearing
    /// glyph outlines or breaking subpixel kerning.
    ///
    /// Returns a fresh instance per call because Avalonia's
    /// <c>DropShadowEffect</c> is mutable - sharing one instance across
    /// every <c>TextBlock</c> would couple their lifecycles and risk
    /// surprising rendering invalidations if a consumer ever tweaked it.
    /// The cost is one tiny allocation per text-displaying control (one-
    /// shot at style application).
    /// </summary>
    public static DropShadowEffect CreateUiTextDropShadow() => new()
    {
        OffsetX = 0,
        OffsetY = 1,
        BlurRadius = 3,
        Opacity = 0.45,
        Color = Avalonia.Media.Colors.Black
    };
}

