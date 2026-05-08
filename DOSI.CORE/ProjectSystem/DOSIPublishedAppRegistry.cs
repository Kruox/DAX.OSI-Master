using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DOSI.CORE.AccentManagement;
using DOSI.CORE.Designer;
using DOSI.CORE.UIComponents;
using DOSI.CORE.UIComponents.WindowManagement;
using DOSI.CORE.UserManagement;

namespace DOSI.CORE.ProjectSystem;

/// <summary>
/// One published DOSI app, persisted in the user's app registry.
/// </summary>
public sealed class DOSIPublishedApp
{
    public string Name { get; set; } = string.Empty;
    public string ProjectFolderPath { get; set; } = string.Empty;
    public DateTime PublishedUtc { get; set; }
    public string? Description { get; set; }
}

/// <summary>
/// Per-user list of "published" DOSI apps, persisted at
/// <c>&lt;userFolder&gt;/published-apps.json</c>. Published apps are surfaced in
/// the desktop's Applications menu and re-compiled from source on every launch
/// so edits in the IDE go live the next time the user opens the app.
/// </summary>
public static class DOSIPublishedAppRegistry
{
    private const string RegistryFileName = "published-apps.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly object SyncRoot = new();

    /// <summary>Raised after any publish / unpublish so UI consumers can refresh.</summary>
    public static event EventHandler? AppsChanged;

    /// <summary>Returns the registry path for <paramref name="user"/>.</summary>
    public static string GetRegistryPath(DOSIUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Path.Combine(UserManager.GetUserFolder(user.Username), RegistryFileName);
    }

    /// <summary>
    /// Loads every published app for <paramref name="user"/>. Stale entries
    /// (whose project folder no longer exists) are filtered out automatically.
    /// </summary>
    public static IReadOnlyList<DOSIPublishedApp> GetAll(DOSIUser? user)
    {
        if (user == null) return Array.Empty<DOSIPublishedApp>();

        var path = GetRegistryPath(user);
        if (!File.Exists(path)) return Array.Empty<DOSIPublishedApp>();

        try
        {
            var json = File.ReadAllText(path);
            var list = JsonSerializer.Deserialize<List<DOSIPublishedApp>>(json, JsonOptions);
            if (list == null) return Array.Empty<DOSIPublishedApp>();

            return list
                .Where(a => !string.IsNullOrWhiteSpace(a.Name)
                         && !string.IsNullOrWhiteSpace(a.ProjectFolderPath)
                         && Directory.Exists(a.ProjectFolderPath))
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<DOSIPublishedApp>();
        }
    }

    /// <summary>
    /// Publishes <paramref name="project"/> for <paramref name="user"/>.
    /// If an app with the same name already exists, its entry is replaced
    /// (re-publishing simply refreshes the published-time stamp).
    /// </summary>
    public static bool Publish(DOSIProject project, DOSIUser? user)
    {
        if (project == null || user == null) return false;

        lock (SyncRoot)
        {
            var current = GetAll(user).ToList();
            current.RemoveAll(a => string.Equals(a.Name, project.Name, StringComparison.OrdinalIgnoreCase));

            current.Add(new DOSIPublishedApp
            {
                Name = project.Name,
                ProjectFolderPath = project.FolderPath,
                PublishedUtc = DateTime.UtcNow,
                Description = $"Published from {project.Name}.dosiproj"
            });

            if (!Save(user, current)) return false;
        }

        AppsChanged?.Invoke(null, EventArgs.Empty);
        return true;
    }

    /// <summary>Removes an app from the registry by name.</summary>
    public static bool Unpublish(string appName, DOSIUser? user)
    {
        if (user == null || string.IsNullOrWhiteSpace(appName)) return false;

        lock (SyncRoot)
        {
            var current = GetAll(user).ToList();
            var removed = current.RemoveAll(a => string.Equals(a.Name, appName, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;
            if (!Save(user, current)) return false;
        }

        AppsChanged?.Invoke(null, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Updates the registry entry whose old name matches <paramref name="oldName"/>
    /// (or whose path matches <paramref name="oldFolderPath"/>) so it points to the
    /// renamed project. Preserves <see cref="DOSIPublishedApp.PublishedUtc"/>.
    /// Returns <c>true</c> if an entry was updated.
    /// </summary>
    public static bool UpdateAfterRename(DOSIUser? user, string oldName, string? oldFolderPath,
                                         string newName, string newFolderPath)
    {
        if (user == null || string.IsNullOrWhiteSpace(newName) ||
            string.IsNullOrWhiteSpace(newFolderPath))
            return false;

        bool changed;
        lock (SyncRoot)
        {
            var current = GetAll(user).ToList();
            var match = current.FirstOrDefault(a =>
                string.Equals(a.Name, oldName, StringComparison.OrdinalIgnoreCase) ||
                (oldFolderPath != null &&
                 string.Equals(a.ProjectFolderPath, oldFolderPath, StringComparison.OrdinalIgnoreCase)));
            if (match == null) return false;

            match.Name = newName;
            match.ProjectFolderPath = newFolderPath;
            match.Description = $"Published from {newName}.dosiproj";

            if (!Save(user, current)) return false;
            changed = true;
        }

        if (changed) AppsChanged?.Invoke(null, EventArgs.Empty);
        return changed;
    }

    private static bool Save(DOSIUser user, List<DOSIPublishedApp> apps)
    {
        try
        {
            var path = GetRegistryPath(user);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(apps, JsonOptions));
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Compiles a <see cref="DOSIPublishedApp"/>'s project from disk and opens its
/// <see cref="Avalonia.Controls.Control"/> result inside a real <see cref="DOSIWindow"/>.
/// Always recompiles from source so edits in the IDE go live on the next launch.
/// </summary>
public static class DOSIPublishedAppLauncher
{
    private static AccentManager Accents => AccentManager.Instance;

    /// <summary>
    /// Builds and runs <paramref name="app"/>, opening the resulting Control as
    /// a new DOSIWindow on the active desktop. Returns the launched window, or
    /// <c>null</c> if the build failed or there was no active WindowManager.
    /// </summary>
    public static DOSIWindow? Launch(DOSIPublishedApp app, Action<string>? onOutput = null)
    {
        ArgumentNullException.ThrowIfNull(app);

        var manager = WindowManager.Instance;
        if (manager == null)
        {
            onOutput?.Invoke("[Launch] No active WindowManager - cannot open published app.");
            return null;
        }

        var project = DOSIProjectManager.Load(app.ProjectFolderPath);
        if (project == null)
        {
            ShowErrorWindow(manager, app.Name,
                $"The project for '{app.Name}' could not be loaded.\n\n" +
                $"Expected at: {app.ProjectFolderPath}");
            onOutput?.Invoke($"[Launch] {app.Name}: project not found.");
            return null;
        }

        var result = DOSIProjectCompiler.BuildAndRun(project);

        if (!result.Success)
        {
            var message = string.Join(Environment.NewLine, result.Diagnostics);
            if (string.IsNullOrWhiteSpace(message))
                message = "Build failed with no diagnostics.";
            ShowErrorWindow(manager, app.Name, message);
            onOutput?.Invoke($"[Launch] {app.Name}: build failed.");
            return null;
        }

        // Visual-only project fast-path: no .cs sources means BuildAndRun
        // produced no assembly and no Control, but the project may still be
        // a perfectly valid form-based app (a single .dosiform with handlers
        // edited via the designer). Mirror the IDE's Run path and instantiate
        // the form directly via DOSIFormLoader instead of demanding a
        // Program.Run() entry point that doesn't exist.
        if (result.Assembly == null && result.ReturnedControl == null)
        {
            var formPath = FindPrimaryFormFile(project.FolderPath);
            if (formPath != null)
            {
                try
                {
                    var formDoc = DOSIFormSerializer.Load(formPath);
                    var formWindow = DOSIFormLoader.Build(formDoc, out var handlerDiags);
                    foreach (var d in handlerDiags)
                        onOutput?.Invoke("[Handlers] " + d);

                    if (string.IsNullOrWhiteSpace(formWindow.Title) ||
                        formWindow.Title == "Form")
                    {
                        formWindow.Title = app.Name;
                    }

                    manager.OpenWindow(formWindow);
                    onOutput?.Invoke($"[Launch] {app.Name}: opened (visual form).");
                    return formWindow;
                }
                catch (Exception ex)
                {
                    ShowErrorWindow(manager, app.Name,
                        $"'{app.Name}' could not be launched from its visual form.\n\n" + ex.Message);
                    onOutput?.Invoke($"[Launch] {app.Name}: form load failed - {ex.Message}");
                    return null;
                }
            }
        }

        if (result.ReturnedControl == null)
        {
            ShowErrorWindow(manager, app.Name,
                $"'{app.Name}' built successfully but its entry method returned no Control.\n\n" +
                "Make sure Program.Run() returns a Control.");
            onOutput?.Invoke($"[Launch] {app.Name}: no Control returned.");
            return null;
        }

        var contentHost = new Border
        {
            Background = Accents.WindowContentBrush,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = result.ReturnedControl
        };

        var window = new DOSIWindow
        {
            Title = app.Name,
            WindowWidth = 720,
            WindowHeight = 480,
            MinimumSize = new Size(280, 180),
            Icon = BuildAppIcon(),
            Content = contentHost
        };

        manager.OpenWindow(window);
        onOutput?.Invoke($"[Launch] {app.Name}: opened.");
        return window;
    }

    private static void ShowErrorWindow(WindowManager manager, string title, string message)
    {
        var msg = new TextBlock
        {
            Text = message,
            FontSize = 12,
            Foreground = Accents.TextPrimaryBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20)
        };

        var scroll = new DOSIScrollViewer
        {
            Content = msg,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            ShowScrollButtons = false
        };

        var window = new DOSIWindow
        {
            Title = title + " - launch failed",
            WindowWidth = 560,
            WindowHeight = 320,
            MinimumSize = new Size(320, 180),
            Icon = BuildAppIcon(),
            Content = new Border
            {
                Background = Accents.WindowContentBrush,
                Child = scroll
            }
        };

        manager.OpenWindow(window);
    }

    /// <summary>
    /// Returns the project's "primary" .dosiform file (Form1.dosiform if present,
    /// otherwise the first .dosiform found at the project root) or null when
    /// the project has no visual form.
    /// </summary>
    private static string? FindPrimaryFormFile(string projectFolder)
    {
        if (string.IsNullOrWhiteSpace(projectFolder) || !Directory.Exists(projectFolder))
            return null;

        try
        {
            var preferred = Path.Combine(projectFolder, "Form1.dosiform");
            if (File.Exists(preferred)) return preferred;

            return Directory.EnumerateFiles(projectFolder, "*.dosiform", SearchOption.TopDirectoryOnly)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static Control BuildAppIcon()
    {
        var a = AccentManager.Instance.AccentPrimary;
        var bg = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(a)
        };
        var grid = new Grid { Width = 16, Height = 16 };
        grid.Children.Add(bg);
        return grid;
    }
}
