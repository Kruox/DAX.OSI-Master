using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using DOSI.CORE;
using DOSI.CORE.AccentManagement;

namespace DAX.OSI;

public class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Default;

        // Apply the universal UI-text drop shadow to every TextBlock in
        // the app. TextBlock is the underlying glyph-rendering primitive
        // for nearly every Avalonia text-displaying control (Buttons,
        // ContentPresenters, MenuItems, ListBoxItems, etc.), so a single
        // selector here gives the entire UI a consistent soft shadow.
        //
        // Intentionally NOT applied to monospaced / terminal text: the
        // exclusion happens naturally because DOSITerminalIO (and any
        // future DOSIFonts.Mono consumer) draws glyphs via
        // DrawingContext.DrawText directly instead of instantiating a
        // TextBlock, so this style never matches it. See DOSIFonts'
        // class doc-comment for the boundary contract.
        Styles.Add(new Style(s => s.OfType<TextBlock>())
        {
            Setters =
            {
                new Setter(TextBlock.EffectProperty, DOSIFonts.CreateUiTextDropShadow())
            }
        });

        // Initialize core system services and load settings
        SystemCore.Initialize();

        // Apply the accent from settings
        AccentManager.Instance.InitializeFromSettings();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow();

            // Apply fullscreen setting
            if (SystemCore.Settings.Fullscreen)
            {
                mainWindow.WindowState = WindowState.FullScreen;
            }

            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
