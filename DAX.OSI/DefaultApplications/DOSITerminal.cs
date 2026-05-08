using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DOSI.CORE.Animations;
using DOSI.CORE.DOSITerminalManagement;
using DOSI.CORE.UIComponents;
using DOSI.CORE.UIComponents.WindowManagement;
using DOSI.CORE.UserManagement;

namespace DAX.OSI.DefaultApplications;

/// <summary>
/// Terminal application for the DOSI virtual operating system.
/// Classic command prompt style interface.
/// </summary>
public class DOSITerminal : DOSIWindow
{
    private readonly DOSITerminalIO _terminal;
    private readonly DOSITerminalManager _manager;
    private readonly Grid _contentRoot;
    private readonly Grid _overlayHost;

    public DOSITerminal()
    {
        Title = "DOSI Terminal";
        WindowWidth = 680;
        WindowHeight = 400;
        MinimumSize = new Size(400, 200);
        Icon = CreateTerminalIcon();

        // Create the terminal control
        _terminal = new DOSITerminalIO();

        // Create the terminal manager with command handling
        _manager = new DOSITerminalManager(_terminal, Close, OpenApplication);

        // Wrap the terminal in a grid so we can overlay celebration animations
        // (e.g. successful useradd) without disturbing the IO control's focus.
        _overlayHost = new Grid
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            IsHitTestVisible = false
        };

        _contentRoot = new Grid
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
        };
        _contentRoot.Children.Add(_terminal);
        _contentRoot.Children.Add(_overlayHost);

        Content = _contentRoot;

        // Handle window focus changes to sync terminal focus
        FocusChanged += OnWindowFocusChanged;

        // Focus terminal when window is attached to visual tree.
        // A freshly opened terminal should always grab the caret so the user
        // can start typing immediately (Ctrl+T behavior).
        AttachedToVisualTree += (s, e) =>
        {
            UserManager.UserCreated += OnUserCreated;
            // Defer focus to allow layout to complete.
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => _terminal.FocusInput(),
                Avalonia.Threading.DispatcherPriority.Loaded);
        };

        DetachedFromVisualTree += (s, e) =>
        {
            UserManager.UserCreated -= OnUserCreated;
        };

        // Show welcome message
        _manager.ShowWelcome();
    }

    private void OnUserCreated(object? sender, DOSIUser user)
    {
        // Celebrate over the terminal window whenever a useradd succeeds.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            DOSISuccessAnim.PlayOver(_overlayHost, DOSISuccessAnim.SuccessSize.Medium));
    }

    private void OnWindowFocusChanged(object? sender, DOSIWindowFocusEventArgs e)
    {
        if (e.IsFocused)
            _terminal.FocusInput();
        else
            _terminal.UnfocusInput();
    }

    private void OpenApplication(string appName, string? args)
    {
        var windowManager = WindowManager.Instance;
        if (windowManager == null) return;

        switch (appName.ToLowerInvariant())
        {
            case "browser":
            case "web":
                var browser = new DOSIWebBrowser(args);
                windowManager.OpenWindow(browser);
                break;
        }
    }

    private static Control CreateTerminalIcon()
    {
        // Simple black square with white prompt
        var border = new Border
        {
            Width = 16,
            Height = 16,
            Background = new SolidColorBrush(Color.FromRgb(12, 12, 12)),
            CornerRadius = new CornerRadius(2),
            BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
            BorderThickness = new Thickness(1)
        };

        var text = new TextBlock
        {
            Text = ">_",
            Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)),
            FontSize = 8,
            FontWeight = FontWeight.Bold,
            FontFamily = new FontFamily("Consolas"),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        border.Child = text;
        return border;
    }
}
