using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DOSI.CORE.AccentManagement;
using DOSI.CORE.ProjectSystem;
using DOSI.CORE.UIComponents;

namespace DAX.OSI.DefaultApplications;

/// <summary>
/// Inline editor for a <see cref="DOSIProject"/>'s manifest. Behaves like a
/// "Project Properties" tab inside DOSIIDE: form fields back the manifest
/// fields and the Save button persists via <see cref="DOSIProjectManager.SaveManifest"/>.
/// <para>
/// Renaming is intentionally NOT exposed here; that goes through the IDE's
/// existing Rename flow because it has to move the folder + manifest file too.
/// </para>
/// </summary>
internal sealed class ProjectPropertiesPanel : Border
{
    private static AccentManager Accents => AccentManager.Instance;

    private readonly DOSIProject _project;
    private readonly Action<DOSIProject>? _onSaved;

    private readonly DOSITextBox _nameLabel;
    private readonly DOSITextBox _descriptionBox;
    private readonly DOSITextBox _versionBox;
    private readonly DOSITextBox _authorBox;
    private readonly DOSITextBox _entryTypeBox;
    private readonly DOSITextBox _entryMethodBox;
    private readonly DOSITextBox _kindBox;
    private readonly TextBlock _statusText;
    private readonly DOSIButton _saveBtn;

    /// <summary>Raised when the panel detects unsaved changes (so the host tab can show the dirty mark).</summary>
    public event EventHandler? Modified;

    /// <summary>Raised after a successful save so the host tab can clear its dirty mark.</summary>
    public event EventHandler? Saved;

    public bool IsDirty { get; private set; }

    public ProjectPropertiesPanel(DOSIProject project, Action<DOSIProject>? onSaved = null)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _onSaved = onSaved;

        _nameLabel = BuildField(_project.Name, readOnly: true,
            placeholder: "Use the Rename button on the toolbar to change the name.");

        _descriptionBox = BuildField(_project.Manifest.Description,
            placeholder: "Short description shown in the Applications menu after Publish.");
        _versionBox = BuildField(_project.Manifest.Version, placeholder: "1.0.0");
        _authorBox = BuildField(_project.Manifest.Author, placeholder: "Optional");
        _entryTypeBox = BuildField(_project.Manifest.EntryType, placeholder: "Program");
        _entryMethodBox = BuildField(_project.Manifest.EntryMethod, placeholder: "Run");
        _kindBox = BuildField(_project.Manifest.Kind, placeholder: "DOSIControl / Console");

        foreach (var box in new[] { _descriptionBox, _versionBox, _authorBox,
                                    _entryTypeBox, _entryMethodBox, _kindBox })
        {
            box.PropertyChanged += OnFieldChanged;
        }

        _statusText = new TextBlock
        {
            FontSize = 11,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.7,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        _saveBtn = new DOSIButton
        {
            Text = "Save",
            Width = 100,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        _saveBtn.Click += (_, _) => Save();

        var actionsRow = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(_saveBtn, Dock.Right);
        actionsRow.Children.Add(_saveBtn);
        actionsRow.Children.Add(_statusText);

        var formStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 14,
            Margin = new Thickness(28, 24, 28, 24),
            Children =
            {
                BuildHeader(),
                BuildSection("Identity",
                    BuildLabeledRow("Name", _nameLabel),
                    BuildLabeledRow("Description", _descriptionBox),
                    BuildLabeledRow("Version", _versionBox),
                    BuildLabeledRow("Author", _authorBox)),
                BuildSection("Entry point",
                    BuildLabeledRow("Type", _entryTypeBox),
                    BuildLabeledRow("Method", _entryMethodBox),
                    BuildLabeledRow("Kind", _kindBox)),
                BuildLocationSection(),
                actionsRow
            }
        };

        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = formStack
        };

        Background = Accents.WindowContentBrush;
        Child = scroller;
    }

    /// <summary>Persists the in-memory edits to disk. Safe to call when nothing changed.</summary>
    public void Save()
    {
        _project.Manifest.Description = _descriptionBox.Text ?? string.Empty;
        _project.Manifest.Version = string.IsNullOrWhiteSpace(_versionBox.Text) ? "1.0.0" : _versionBox.Text!.Trim();
        _project.Manifest.Author = _authorBox.Text ?? string.Empty;
        _project.Manifest.EntryType = string.IsNullOrWhiteSpace(_entryTypeBox.Text) ? "Program" : _entryTypeBox.Text!.Trim();
        _project.Manifest.EntryMethod = string.IsNullOrWhiteSpace(_entryMethodBox.Text) ? "Run" : _entryMethodBox.Text!.Trim();
        _project.Manifest.Kind = string.IsNullOrWhiteSpace(_kindBox.Text) ? "DOSIControl" : _kindBox.Text!.Trim();

        if (DOSIProjectManager.SaveManifest(_project))
        {
            IsDirty = false;
            ShowStatus("Saved.", isError: false);
            Saved?.Invoke(this, EventArgs.Empty);
            _onSaved?.Invoke(_project);
        }
        else
        {
            ShowStatus("Save failed - check disk permissions.", isError: true);
        }
    }

    /// <summary>Marks the form as clean (e.g. after the host tab persists itself).</summary>
    public void MarkClean()
    {
        IsDirty = false;
        _statusText.Text = string.Empty;
    }

    private void OnFieldChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name != "Text") return;
        if (IsDirty) return;
        IsDirty = true;
        ShowStatus("Unsaved changes...", isError: false);
        Modified?.Invoke(this, EventArgs.Empty);
    }

    private void ShowStatus(string text, bool isError)
    {
        _statusText.Text = text;
        _statusText.Foreground = isError
            ? new SolidColorBrush(Color.FromRgb(255, 120, 120))
            : Accents.TextSecondaryBrush;
        _statusText.Opacity = 1;
    }

    private DOSITextBox BuildField(string initial, string? placeholder = null, bool readOnly = false)
    {
        return new DOSITextBox
        {
            Text = initial ?? string.Empty,
            PlaceholderText = placeholder ?? string.Empty,
            IsReadOnly = readOnly,
            Width = 420,
            HorizontalAlignment = HorizontalAlignment.Left
        };
    }

    private Control BuildHeader() => new StackPanel
    {
        Orientation = Orientation.Vertical,
        Children =
        {
            new TextBlock
            {
                Text = "Project Properties",
                FontSize = 22,
                FontWeight = FontWeight.SemiBold,
                Foreground = Accents.TextPrimaryBrush
            },
            new TextBlock
            {
                Text = $"Edits the manifest at {_project.ManifestPath}",
                FontSize = 11,
                Foreground = Accents.TextSecondaryBrush,
                Opacity = 0.7,
                Margin = new Thickness(0, 4, 0, 0)
            }
        }
    };

    private Control BuildSection(string title, params Control[] rows)
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical, Spacing = 10 };
        stack.Children.Add(new TextBlock
        {
            Text = title.ToUpperInvariant(),
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.7,
            Margin = new Thickness(0, 6, 0, 4)
        });
        foreach (var r in rows) stack.Children.Add(r);
        return stack;
    }

    private static Control BuildLabeledRow(string label, Control field)
    {
        var lbl = new TextBlock
        {
            Text = label,
            Width = 110,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = AccentManager.Instance.TextPrimaryBrush
        };
        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(lbl, Dock.Left);
        dock.Children.Add(lbl);
        dock.Children.Add(field);
        return dock;
    }

    private Control BuildLocationSection()
    {
        var pathBox = new DOSITextBox
        {
            Text = _project.FolderPath,
            IsReadOnly = true,
            Width = 420,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        return BuildSection("Location",
            BuildLabeledRow("Folder", pathBox));
    }
}
