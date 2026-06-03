using System;
using System.Collections.Generic;
using System.Linq;
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

    // Process-wide tracker of every DOSIContextMenu currently open. Lets
    // a fresh open call dismiss any prior menu so a right-click on a
    // taskbar chip followed by a right-click on the desktop doesn't
    // leave both menus visible at once. We track at the DOSI layer
    // (not Avalonia's base ContextMenu) so legacy / non-DOSI menus
    // don't get force-closed by user-defined chrome.
    private static readonly HashSet<DOSIContextMenu> _openMenus = new();

    /// <summary>
    /// Closes every DOSIContextMenu currently shown. Call this at the
    /// start of any custom right-click handler that opens its own menu
    /// to prevent two menus rendering at the same time. Safe to call
    /// when nothing is open - it's a no-op.
    /// </summary>
    public static void CloseAllOpen()
    {
        // Snapshot first - Close() mutates _openMenus via the Closed
        // event handler we wire below.
        var snapshot = _openMenus.ToArray();
        foreach (var m in snapshot)
        {
            try { m.Close(); } catch { /* defensive: never let chrome close errors propagate */ }
        }
    }

    public DOSIContextMenu()
    {
        ApplyAccentSurfaces();

        Template = BuildTemplate();
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(4);
        MinWidth = 170;

        // Track open / close so CloseAllOpen() can hit every live menu.
        // Pairing on Opened/Closed (not Opening/Closing) ensures we only
        // count menus that actually rendered - avoids leaks if Opening
        // is cancelled by a handler.
        Opened += (_, _) => _openMenus.Add(this);
        Closed += (_, _) => _openMenus.Remove(this);

        // When THIS menu starts opening, dismiss every other DOSI menu
        // currently shown. Solves the "right-click taskbar chip, then
        // right-click desktop, see both menus at once" pile-up. Single
        // chokepoint here means every consumer (file explorer, desktop,
        // taskbar, code editor) gets the behaviour for free.
        Opening += (_, _) =>
        {
            foreach (var other in _openMenus.ToArray())
            {
                if (!ReferenceEquals(other, this))
                {
                    try { other.Close(); } catch { /* defensive */ }
                }
            }
        };

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

        // Belt-and-braces: a ContextMenu only lives in the visual tree
        // while its popup is open, so an accent change that lands while
        // the menu is closed never reaches OnAccentChanged. Refresh the
        // brushes + styles every time the menu opens so re-opens after a
        // closed-state accent flip always render the current accent.
        Opening += (_, _) => ApplyAccentSurfaces();
    }

    /// <summary>
    /// (Re-)applies every accent-derived surface on the menu: background
    /// gradient, accent border, item text colour, separator colour. Safe
    /// to call any number of times - idempotent.
    /// </summary>
    private void ApplyAccentSurfaces()
    {
        Background = BuildMenuBackground();
        BorderBrush = new SolidColorBrush(Accents.AccentSecondary);

        // Item / separator styles capture brushes at construction time,
        // so rebuild them so a live accent flip (especially Light <-> dark)
        // recolours menu-item text + dividers correctly.
        Styles.Clear();
        Styles.Add(BuildItemStyle());
        Styles.Add(BuildDisabledItemStyle());
        Styles.Add(BuildSeparatorStyle());
    }

    private void OnAccentChanged(object? sender, EventArgs e) => ApplyAccentSurfaces();

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

            // Two shadow recipes:
            // - Dark accents: a deep near-black halo (matches the apps menu
            //   / notification popover, which always open against the dark
            //   wallpaper).
            // - Light accent: a softer, cooler, lower-alpha shadow. A
            //   straight #96000000 with a 36px blur paints a muddy grey
            //   halo around the near-white menu surface that reads as
            //   "smudged" rather than "elevated". The lower alpha + slate
            //   tint gives proper depth without dirtying the chrome.
            // The apps menu / notification popover keep using their own
            // shadow because they open against the wallpaper, not over
            // window content - the visual budget is different there.
            var isLight = Accents.CurrentAccent == DOSIAccent.Light;
            var chrome = new Border
            {
                BoxShadow = new BoxShadows(new BoxShadow
                {
                    OffsetX = 0,
                    OffsetY = isLight ? 6 : 14,
                    Blur = isLight ? 20 : 36,
                    Spread = 0,
                    Color = isLight
                        ? Color.FromArgb(48, 28, 38, 60)
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
            // window's edges. ALL FOUR sides must be wide enough to fit the
            // blur radius (~36 px for dark, ~20 px for light) or the halo
            // gets sliced flush against the popup edge on that side. The
            // previous asymmetric gutter (16, 12, 32, 36) was sized for
            // bottom-right only - the LEFT side had just 16 px while the
            // dark-accent shadow's 36 px blur needed 32+, which is why the
            // left edge of every right-click menu rendered with a hard,
            // un-feathered shadow boundary while the other three sides
            // looked properly soft. Adding the downward OffsetY of 14 px
            // to the bottom gutter keeps the offset shadow bleed accounted
            // for without growing the top inset (the shadow has no upward
            // bleed beyond the blur).
            var blur = isLight ? 20 : 36;
            var offsetY = isLight ? 6 : 14;
            return new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(blur, blur, blur, blur + offsetY),
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

    /// <summary>
    /// Disabled-item style - foreground only. Items rendered in a
    /// disabled state (e.g. "Paste" with no clipboard content, "Open
    /// in new window" on a non-folder tile) get a softer foreground
    /// + reduced opacity so they read as "not actionable" without
    /// disappearing entirely. Picks accent-aware greys so the dim
    /// state stays legible on both Light and dark surfaces - the
    /// previous behaviour inherited Avalonia's default disabled
    /// brush which is nearly white-on-white under our light theme
    /// (the "hard on the eyes" complaint).
    /// </summary>
    private static Style BuildDisabledItemStyle() => new(s => s.OfType<MenuItem>().Class(":disabled"))
    {
        Setters =
        {
            new Setter(MenuItem.ForegroundProperty,
                Accents.CurrentAccent == DOSIAccent.Light
                    ? new SolidColorBrush(Color.FromRgb(140, 145, 158))
                    : new SolidColorBrush(Color.FromRgb(150, 155, 170))),
            new Setter(MenuItem.OpacityProperty, 0.75)
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
    /// Alpha is intentionally well below opaque (~210) so the menu reads as
    /// a soft glass panel rather than a heavy slab. Stops are also pulled
    /// closer together in luminance so the gradient is gentler - the older
    /// stronger gradient drew the eye to the menu chrome instead of the
    /// menu items themselves.
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
                    new GradientStop(Color.FromArgb(210, 250, 251, 254), 0),
                    new GradientStop(Color.FromArgb(210, 240, 243, 249), 1)
                }
            };
        }

        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(210, 30, 32, 40), 0),
                new GradientStop(Color.FromArgb(210, 22, 24, 32), 1)
            }
        };
    }
}
