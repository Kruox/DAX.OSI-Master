using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DOSI.CORE.AccentManagement;

namespace DOSI.CORE.UIComponents;

/// <summary>
/// A modal dialog that appears within the DOSI desktop environment.
/// Supports alert, confirm, and custom content dialogs.
/// </summary>
public class DOSIDialog : Control
{
    private readonly Border _overlay;
    private readonly Border _dialogBox;
    private readonly TextBlock _titleText;
    private readonly TextBlock _messageText;
    private readonly StackPanel _buttonPanel;
    private readonly StackPanel _contentPanel;
    private readonly Control? _customContent;
    
    private TaskCompletionSource<DialogResult>? _resultSource;
    private Panel? _parentContainer;

    private static AccentManager Accents => AccentManager.Instance;

    public string Title { get; set; } = "Dialog";
    public string Message { get; set; } = "";
    public DialogType Type { get; set; } = DialogType.Alert;

    static DOSIDialog()
    {
        // Suppress the default white focus rectangle on this DOSI control.
        FocusAdornerProperty.OverrideDefaultValue<DOSIDialog>(null);
    }

    public DOSIDialog(string title, string message, DialogType type = DialogType.Alert, Control? customContent = null)
    {
        // Suppress the default Fluent focus rectangle (white outline). Setting this
        // as a local value overrides the accent style that re-applies it.
        FocusAdorner = null;

        Title = title;
        Message = message;
        Type = type;
        _customContent = customContent;

        // Semi-transparent overlay
        _overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        // Title
        _titleText = new TextBlock
        {
            Text = title,
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextPrimaryBrush,
            Margin = new Thickness(0, 0, 0, 12)
        };

        // Message
        _messageText = new TextBlock
        {
            Text = message,
            FontSize = 14,
            Foreground = Accents.TextSecondaryBrush,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 350,
            Margin = new Thickness(0, 0, 0, 20)
        };

        // Button panel
        _buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };

        // Content panel
        _contentPanel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _contentPanel.Children.Add(_titleText);
        
        if (!string.IsNullOrEmpty(message))
            _contentPanel.Children.Add(_messageText);
        
        if (_customContent != null)
        {
            _customContent.Margin = new Thickness(0, 0, 0, 20);
            _contentPanel.Children.Add(_customContent);
        }
        
        _contentPanel.Children.Add(_buttonPanel);

        // Dialog box
        _dialogBox = new Border
        {
            Background = Accents.WindowContentBrush,
            BorderBrush = new SolidColorBrush(Accents.ControlBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(24),
            MinWidth = 300,
            MaxWidth = 450,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = _contentPanel,
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0,
                OffsetY = 8,
                Blur = 24,
                Color = Color.FromArgb(100, 0, 0, 0)
            })
        };

        CreateButtons();

        // Subscribe to accent changes
        AttachedToVisualTree += (s, e) => Accents.AccentChanged += OnAccentChanged;
        DetachedFromVisualTree += (s, e) => Accents.AccentChanged -= OnAccentChanged;
    }

    private void CreateButtons()
    {
        _buttonPanel.Children.Clear();

        switch (Type)
        {
            case DialogType.Alert:
                AddButton("OK", DialogResult.OK, true);
                break;
            case DialogType.Confirm:
                AddButton("Cancel", DialogResult.Cancel, false);
                AddButton("OK", DialogResult.OK, true);
                break;
            case DialogType.YesNo:
                AddButton("No", DialogResult.No, false);
                AddButton("Yes", DialogResult.Yes, true);
                break;
            case DialogType.YesNoCancel:
                AddButton("Cancel", DialogResult.Cancel, false);
                AddButton("No", DialogResult.No, false);
                AddButton("Yes", DialogResult.Yes, true);
                break;
            case DialogType.Custom:
                // Custom buttons should be added separately
                break;
        }
    }

    public void AddButton(string text, DialogResult result, bool isPrimary = false)
    {
        var button = new DOSIButton
        {
            Text = text,
            Padding = new Thickness(20, 8),
            MinWidth = 80
        };

        if (isPrimary)
        {
            button.Background = Accents.AccentPrimaryBrush;
            button.BackgroundHover = Accents.AccentSecondaryBrush;
            button.BackgroundPressed = Accents.AccentPrimaryBrush;
            button.Foreground = Brushes.White;
            button.BorderThickness = 0;
        }

        button.Click += (s, e) => Close(result);
        _buttonPanel.Children.Add(button);
    }

    private void OnAccentChanged(object? sender, EventArgs e)
    {
        _overlay.Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0));
        _dialogBox.Background = Accents.WindowContentBrush;
        _dialogBox.BorderBrush = new SolidColorBrush(Accents.ControlBorder);
        _titleText.Foreground = Accents.TextPrimaryBrush;
        _messageText.Foreground = Accents.TextSecondaryBrush;
    }

    public override void Render(DrawingContext context)
    {
        // Draw overlay
        context.FillRectangle(
            new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
            new Rect(Bounds.Size));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _dialogBox.Measure(availableSize);
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // Center the dialog
        var dialogSize = _dialogBox.DesiredSize;
        var x = (finalSize.Width - dialogSize.Width) / 2;
        var y = (finalSize.Height - dialogSize.Height) / 2;
        _dialogBox.Arrange(new Rect(x, y, dialogSize.Width, dialogSize.Height));
        return finalSize;
    }

    /// <summary>
    /// Shows the dialog and returns the result asynchronously.
    /// </summary>
    public Task<DialogResult> ShowAsync(Panel container)
    {
        _parentContainer = container;
        _resultSource = new TaskCompletionSource<DialogResult>();

        // Create a container grid for overlay + dialog
        var dialogContainer = new Grid
        {
            Name = "DOSIDialogContainer",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        dialogContainer.Children.Add(_overlay);
        dialogContainer.Children.Add(_dialogBox);

        // Handle overlay click to close (for alerts)
        _overlay.PointerPressed += (s, e) =>
        {
            if (Type == DialogType.Alert)
                Close(DialogResult.OK);
        };

        // Handle Escape key
        dialogContainer.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close(Type == DialogType.Alert ? DialogResult.OK : DialogResult.Cancel);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                Close(DialogResult.OK);
                e.Handled = true;
            }
        };

        container.Children.Add(dialogContainer);
        dialogContainer.Focus();

        return _resultSource.Task;
    }

    /// <summary>
    /// Closes the dialog with the specified result.
    /// </summary>
    public void Close(DialogResult result)
    {
        if (_parentContainer != null)
        {
            // Find and remove the dialog container
            for (int i = _parentContainer.Children.Count - 1; i >= 0; i--)
            {
                if (_parentContainer.Children[i] is Grid grid && grid.Name == "DOSIDialogContainer")
                {
                    _parentContainer.Children.RemoveAt(i);
                    break;
                }
            }
        }

        _resultSource?.TrySetResult(result);
    }

    #region Static Helper Methods

    /// <summary>
    /// Shows an alert dialog with a message.
    /// </summary>
    public static Task<DialogResult> Alert(Panel container, string title, string message)
    {
        var dialog = new DOSIDialog(title, message, DialogType.Alert);
        return dialog.ShowAsync(container);
    }

    /// <summary>
    /// Shows a confirmation dialog.
    /// </summary>
    public static Task<DialogResult> Confirm(Panel container, string title, string message)
    {
        var dialog = new DOSIDialog(title, message, DialogType.Confirm);
        return dialog.ShowAsync(container);
    }

    /// <summary>
    /// Shows a Yes/No dialog.
    /// </summary>
    public static Task<DialogResult> YesNo(Panel container, string title, string message)
    {
        var dialog = new DOSIDialog(title, message, DialogType.YesNo);
        return dialog.ShowAsync(container);
    }

    /// <summary>
    /// Shows a custom dialog.
    /// </summary>
    public static Task<DialogResult> Custom(Panel container, string title, string message, Control? customContent = null)
    {
        var dialog = new DOSIDialog(title, message, DialogType.Custom, customContent);
        return dialog.ShowAsync(container);
    }

    #endregion
}

/// <summary>
/// Types of dialogs available.
/// </summary>
public enum DialogType
{
    Alert,
    Confirm,
    YesNo,
    YesNoCancel,
    Custom
}

/// <summary>
/// Result returned from a dialog.
/// </summary>
public enum DialogResult
{
    None,
    OK,
    Cancel,
    Yes,
    No
}
