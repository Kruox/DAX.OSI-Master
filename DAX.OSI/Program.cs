using Avalonia;
using Avalonia.Media;
using DOSI.CORE;

namespace DAX.OSI;

internal class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Wire the embedded Noto Sans face (shipped under
            // DOSI.CORE/Resources/Fonts/) as the application-wide default
            // family. Every Avalonia control that doesn't set FontFamily
            // explicitly inherits this - so the UI uses the bundled font
            // uniformly across every host platform without each DOSI
            // control having to opt in. Controls that need a monospace
            // face (currently DOSITerminalIO) reach for DOSIFonts.Mono
            // explicitly.
            .With(new FontManagerOptions
            {
                DefaultFamilyName = DOSIFonts.UIFamilyUri
            })
            .LogToTrace();
}
