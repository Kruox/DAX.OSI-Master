using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.Styling;
using DOSI.CORE.AccentManagement;

namespace DOSI.CORE.UIComponents;

/// <summary>
/// Right-click menu styled to match the desktop's Applications menu: gradient
/// background, accent-tinted border, rounded corners, and a real BoxShadow
/// that renders OUTSIDE the chrome.
///
/// Why a custom Template: Avalonia's <see cref="ContextMenu"/> opens inside a
/// popup top-level whose bounds come from the menu's layout size. A
/// <c>DropShadowEffect</c> or BoxShadow on the menu itself therefore gets
/// clipped at the popup edges. We work around that by retemplating the menu
/// with a transparent outer gutter (Padding contributes to layout), and the
/// visible chrome with the BoxShadow sits inside that gutter - so the shadow
/// has real room to fade out on all four sides.
///
/// Accent integration: the chrome's Background and BorderBrush are bound to
/// the menu's own properties, so updating <see cref="ContextMenu.BorderBrush"/>
/// on accent change propagates without rebuilding the menu. We hook
/// <see cref="AccentManager.AccentChanged"/> in the constructor and detach
/// when the menu is removed from the visual tree.
/// </summary>
public class DOSIContextMenu : ContextMenu
{
    private static AccentManager Accents => AccentManager.Instance;

    public DOSIContextMenu()
    {
        Background = BuildMenuBackground();
        BorderBrush = new SolidColorBrush(Accents.AccentSecondary);
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(4);
        MinWidth = 170;

        Template = BuildTemplate();
        Styles.Add(BuildItemStyle());
        Styles.Add(BuildSeparatorStyle());

        // Live-update the accent border. The template binds the chrome's
        // BorderBrush to ours, so reassigning BorderBrush is enough - no
        // need to walk into the templated visual tree.
        //
        // Subscribing on each AttachedToVisualTree (and unsubscribing on
        // each detach) keeps the AccentChanged invocation list balanced if
        // the menu's popup is shown / hidden / shown again. Subscribing in
        // the constructor used to break that pairing: after the first close,
        // the menu detached and unsubscribed, but the constructor never ran
        // again so re-opens stopped tracking accent changes.
        AttachedToVisualTree += (_, _) => Accents.AccentChanged += OnAccentChanged;
        DetachedFromVisualTree += (_, _) => Accents.AccentChanged -= OnAccentChanged;
    }

    private void OnAccentChanged(object? sender, EventArgs e)
    {
        BorderBrush = new SolidColorBrush(Accents.AccentSecondary);
        // Refresh the background too so live accent switches between Light
        // and a dark accent flip the menu surface accordingly.
        Background = BuildMenuBackground();

        // Item / separator styles capture brushes at construction time, so
        // rebuild them so a live accent flip (especially Light <-> dark)
        // recolors menu-item text + dividers correctly.
        Styles.Clear();
        Styles.Add(BuildItemStyle());
        Styles.Add(BuildSeparatorStyle());
    }

    /// <summary>
    /// Custom template: outer transparent gutter (so the popup window has real
    /// layout room for the shadow), inner Border with the visible chrome +
    /// BoxShadow, ItemsPresenter for the menu rows. Background and BorderBrush
    /// are template-bound so they react to accent changes on the live menu.
    /// </summary>
    private static FuncControlTemplate<DOSIContextMenu> BuildTemplate() =>
        new((parent, ns) =>
        {
            var presenter = new ItemsPresenter { Name = "PART_ItemsPresenter" };
            ns.Register("PART_ItemsPresenter", presenter);

            // Light surfaces get a softer, slightly cool shadow so we don't
            // paint a heavy black halo around a near-white menu. Dark accents
            // keep the original deep shadow for proper depth on dark wallpapers.
            var isLight = Accents.CurrentAccent == DOSIAccent.Light;
            var chrome = new Border
            {
                BoxShadow = new BoxShadows(new BoxShadow
                {
                    OffsetX = 0,
                    OffsetY = isLight ? 8 : 14,
                    Blur = isLight ? 24 : 36,
                    Spread = 0,
                    Color = isLight
                        ? Color.FromArgb(55, 30, 45, 75)
                        : Color.FromArgb(150, 0, 0, 0)
                }),
                Child = presenter
            };

            // Bind chrome chrome chrome -> menu so accent / theme updates
            // flow without rebuilding the popup.
            chrome[!Border.BackgroundProperty] = parent[!BackgroundProperty];
            chrome[!Border.BorderBrushProperty] = parent[!BorderBrushProperty];
            chrome[!Border.BorderThicknessProperty] = parent[!BorderThicknessProperty];
            chrome[!Border.CornerRadiusProperty] = parent[!CornerRadiusProperty];
            chrome[!Border.PaddingProperty] = parent[!PaddingProperty];

            // Gutter sizes match the BoxShadow's spread on each side so the
            // halo fades out naturally instead of being clipped at the popup
            // window's edges. Top/left are smaller than bottom/right because
            // the shadow has a downward OffsetY=14, so most of the bleed is
            // below + to the right. The chrome still opens close to the
            // cursor (the popup origin) - just nudged in by ~14px.
            return new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(16, 12, 32, 36),
                Child = chrome
            };
        });

    /// <summary>
    /// Tighten and crispen each row so the menu feels like the apps menu
    /// rather than the chunky default ContextMenu chrome. Foreground is
    /// resolved at style-build time, so callers should rebuild this style
    /// on accent change to flip between dark/light text.
    /// </summary>
    private static Style BuildItemStyle() => new(s => s.OfType<MenuItem>())
    {
        Setters =
        {
            new Setter(MenuItem.FontSizeProperty, 12d),
            new Setter(MenuItem.FontWeightProperty, FontWeight.Normal),
            new Setter(MenuItem.PaddingProperty, new Thickness(10, 5)),
            new Setter(MenuItem.MinHeightProperty, 26d),
            new Setter(MenuItem.CornerRadiusProperty, new CornerRadius(6)),
            // Pin to a strong dark/light value rather than TextPrimaryBrush
            // so MenuItem's templated header presenter doesn't fall back to
            // Avalonia's default theme foreground (which renders as faint
            // light-grey on our light surface).
            new Setter(MenuItem.ForegroundProperty,
                Accents.CurrentAccent == DOSIAccent.Light
                    ? new SolidColorBrush(Color.FromRgb(20, 22, 28))
                    : new SolidColorBrush(Color.FromRgb(240, 242, 248)))
        }
    };

    private static Style BuildSeparatorStyle() => new(s => s.OfType<Separator>())
    {
        Setters =
        {
            new Setter(Separator.HeightProperty, 1d),
            new Setter(Separator.MarginProperty, new Thickness(8, 4)),
            // Fully opaque divider colours - a translucent black on light
            // mode lets the page underneath bleed through (especially over
            // video frames) and makes the separator look like a smudged
            // gradient instead of a clean line.
            new Setter(Separator.BackgroundProperty,
                Accents.CurrentAccent == DOSIAccent.Light
                    ? new SolidColorBrush(Color.FromRgb(205, 210, 220))
                    : new SolidColorBrush(Color.FromRgb(70, 75, 90)))
        }
    };

    /// <summary>
    /// Same gradient surface used by the desktop's Applications menu so every
    /// popup feels visually consistent. Switches to a light surface under the
    /// Light accent so the (dark) menu item text stays readable.
    ///
    /// All stops are fully opaque (alpha = 255). Earlier revisions used
    /// alphas in the 230s for a faint frost effect, but that let busy page
    /// content (video frames, accent-coloured banners) bleed through the
    /// menu and made dividers / text look ghostly. A real desktop right-click
    /// menu always sits on a solid surface, so we do the same.
    /// </summary>
    private static IBrush BuildMenuBackground()
    {
        if (Accents.CurrentAccent == DOSIAccent.Light)
        {
            return new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromRgb(250, 251, 254), 0),
                    new GradientStop(Color.FromRgb(232, 236, 244), 1)
                }
            };
        }

        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(28, 30, 38), 0),
                new GradientStop(Color.FromRgb(16, 18, 26), 1)
            }
        };
    }
}
