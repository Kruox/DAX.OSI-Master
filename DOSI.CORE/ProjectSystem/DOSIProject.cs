using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DOSI.CORE.ProjectSystem;

/// <summary>
/// Persisted manifest for a DOSI project. Stored as <c>&lt;projectFolder&gt;/&lt;name&gt;.dosiproj</c>.
/// </summary>
public sealed class DOSIProjectManifest
{
    /// <summary>Display name of the project (also the folder name and assembly name).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Project type/template identifier (e.g. "DOSIControl", "Console").</summary>
    public string Kind { get; set; } = "DOSIControl";

    /// <summary>Fully-qualified entry-point type, e.g. <c>"Program"</c>.</summary>
    public string EntryType { get; set; } = "Program";

    /// <summary>Static method on <see cref="EntryType"/> invoked when running.</summary>
    public string EntryMethod { get; set; } = "Run";

    /// <summary>Project file format version (for future migrations).</summary>
    public int FormatVersion { get; set; } = 1;

    /// <summary>UTC creation time.</summary>
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// In-memory wrapper around a <see cref="DOSIProjectManifest"/>. Knows where the
/// project lives on disk and can enumerate its source files.
/// </summary>
public sealed class DOSIProject
{
    public DOSIProjectManifest Manifest { get; }

    /// <summary>Absolute folder containing the project.</summary>
    public string FolderPath { get; }

    /// <summary>Absolute path to the manifest file.</summary>
    public string ManifestPath { get; }

    public string Name => Manifest.Name;

    public DOSIProject(DOSIProjectManifest manifest, string folderPath, string manifestPath)
    {
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        FolderPath = folderPath ?? throw new ArgumentNullException(nameof(folderPath));
        ManifestPath = manifestPath ?? throw new ArgumentNullException(nameof(manifestPath));
    }

    /// <summary>Returns every <c>*.cs</c> source file inside the project folder.</summary>
    public IEnumerable<string> EnumerateSourceFiles()
    {
        if (!Directory.Exists(FolderPath)) return Array.Empty<string>();

        try
        {
            var sep = Path.DirectorySeparatorChar;
            return Directory.EnumerateFiles(FolderPath, "*.cs", SearchOption.AllDirectories)
                .Where(p =>
                {
                    // bin/obj filter must operate on the path RELATIVE to the
                    // project, otherwise host paths like "...\bin\Debug\net9.0\..."
                    // would falsely exclude every project file.
                    var rel = Path.GetRelativePath(FolderPath, p);
                    if (rel.StartsWith("bin" + sep, StringComparison.OrdinalIgnoreCase)) return false;
                    if (rel.StartsWith("obj" + sep, StringComparison.OrdinalIgnoreCase)) return false;
                    if (rel.Contains(sep + "bin" + sep, StringComparison.OrdinalIgnoreCase)) return false;
                    if (rel.Contains(sep + "obj" + sep, StringComparison.OrdinalIgnoreCase)) return false;
                    return true;
                })
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}

/// <summary>
/// Creates and loads <see cref="DOSIProject"/> instances. Projects live as folders
/// under the user's home folder (typically <c>~/Projects/&lt;name&gt;/</c>) and are
/// identified by a single <c>*.dosiproj</c> manifest file at their root.
/// </summary>
public static class DOSIProjectManager
{
    public const string ManifestExtension = ".dosiproj";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Returns true if <paramref name="folder"/> contains a <c>*.dosiproj</c> file.</summary>
    public static bool IsProjectFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return false;
        try
        {
            return Directory.EnumerateFiles(folder, "*" + ManifestExtension, SearchOption.TopDirectoryOnly).Any();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Returns the manifest path inside <paramref name="folder"/>, or <c>null</c>.</summary>
    public static string? FindManifest(string folder)
    {
        if (!Directory.Exists(folder)) return null;
        try
        {
            return Directory.EnumerateFiles(folder, "*" + ManifestExtension, SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Loads the project rooted at <paramref name="folder"/>, or <c>null</c> if not a project.</summary>
    public static DOSIProject? Load(string folder)
    {
        var manifestPath = FindManifest(folder);
        if (manifestPath == null) return null;

        try
        {
            var json = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<DOSIProjectManifest>(json, JsonOptions);
            if (manifest == null) return null;
            if (string.IsNullOrWhiteSpace(manifest.Name))
                manifest.Name = Path.GetFileNameWithoutExtension(manifestPath);

            return new DOSIProject(manifest, folder, manifestPath);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Walks up from <paramref name="path"/> (file or folder), stopping before
    /// it leaves <paramref name="root"/>, and returns the first ancestor folder
    /// that contains a <c>.dosiproj</c> manifest. Returns <c>null</c> if none.
    /// </summary>
    public static DOSIProject? FindProjectFor(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return null;

        var rootFull = Path.GetFullPath(root);
        var dir = File.Exists(path) ? Path.GetDirectoryName(path) : path;

        while (!string.IsNullOrEmpty(dir) &&
               dir.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            if (IsProjectFolder(dir)) return Load(dir);
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>
    /// Returns every project folder found one level under <paramref name="projectsRoot"/>.
    /// </summary>
    public static IReadOnlyList<DOSIProject> ListProjects(string projectsRoot)
    {
        if (string.IsNullOrWhiteSpace(projectsRoot) || !Directory.Exists(projectsRoot))
            return Array.Empty<DOSIProject>();

        var result = new List<DOSIProject>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(projectsRoot))
            {
                if (!IsProjectFolder(dir)) continue;
                var p = Load(dir);
                if (p != null) result.Add(p);
            }
        }
        catch { }

        return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Creates a brand-new project folder at <paramref name="parentFolder"/>/<paramref name="name"/>,
    /// writes the manifest, and seeds it with a starter <c>Program.cs</c> using DOSI controls.
    /// Returns the loaded <see cref="DOSIProject"/> on success.
    /// </summary>
    public static DOSIProject? Create(string parentFolder, string name, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(parentFolder) || !Directory.Exists(parentFolder))
        {
            error = "Parent folder does not exist.";
            return null;
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Project name cannot be empty.";
            return null;
        }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "Project name contains characters that aren't allowed.";
            return null;
        }

        var safeName = name.Trim();
        var projectFolder = Path.Combine(parentFolder, safeName);
        if (Directory.Exists(projectFolder))
        {
            error = $"A folder named '{safeName}' already exists here.";
            return null;
        }

        try
        {
            Directory.CreateDirectory(projectFolder);

            var manifest = new DOSIProjectManifest
            {
                Name = safeName,
                Kind = "DOSIControl",
                EntryType = "Program",
                EntryMethod = "Run",
                CreatedUtc = DateTime.UtcNow
            };

            var manifestPath = Path.Combine(projectFolder, safeName + ManifestExtension);
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));

            var programPath = Path.Combine(projectFolder, "Program.cs");
            File.WriteAllText(programPath, BuildStarterProgram(safeName));

            return new DOSIProject(manifest, projectFolder, manifestPath);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Renames an existing project. Renames the folder, the manifest file, and
    /// updates the manifest's <see cref="DOSIProjectManifest.Name"/> field.
    /// Returns the reloaded <see cref="DOSIProject"/> at its new location.
    /// </summary>
    public static DOSIProject? Rename(DOSIProject project, string newName, out string? error)
    {
        error = null;
        if (project == null) { error = "No project to rename."; return null; }

        var trimmed = newName?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            error = "The new name cannot be empty.";
            return null;
        }
        if (trimmed!.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "The new name contains characters that aren't allowed.";
            return null;
        }

        if (string.Equals(project.Name, trimmed, StringComparison.Ordinal))
            return project;

        var parent = Path.GetDirectoryName(project.FolderPath);
        if (string.IsNullOrEmpty(parent))
        {
            error = "Cannot determine the parent folder.";
            return null;
        }

        var newFolder = Path.Combine(parent, trimmed);

        if (Directory.Exists(newFolder) &&
            !string.Equals(Path.GetFullPath(newFolder),
                           Path.GetFullPath(project.FolderPath),
                           StringComparison.OrdinalIgnoreCase))
        {
            error = $"A folder named '{trimmed}' already exists.";
            return null;
        }

        try
        {
            if (!string.Equals(project.FolderPath, newFolder, StringComparison.Ordinal))
            {
                if (string.Equals(project.FolderPath, newFolder, StringComparison.OrdinalIgnoreCase))
                {
                    // Case-only rename on a case-insensitive filesystem - go via temp.
                    var temp = newFolder + "__rename_" + Guid.NewGuid().ToString("N");
                    Directory.Move(project.FolderPath, temp);
                    Directory.Move(temp, newFolder);
                }
                else
                {
                    Directory.Move(project.FolderPath, newFolder);
                }
            }

            var oldManifestName = Path.GetFileName(project.ManifestPath);
            var movedManifestPath = Path.Combine(newFolder, oldManifestName);
            var newManifestPath = Path.Combine(newFolder, trimmed + ManifestExtension);

            if (!string.Equals(movedManifestPath, newManifestPath, StringComparison.Ordinal) &&
                File.Exists(movedManifestPath))
            {
                if (File.Exists(newManifestPath)) File.Delete(newManifestPath);
                File.Move(movedManifestPath, newManifestPath);
            }

            var manifest = project.Manifest;
            manifest.Name = trimmed;
            File.WriteAllText(newManifestPath, JsonSerializer.Serialize(manifest, JsonOptions));

            return new DOSIProject(manifest, newFolder, newManifestPath);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static string BuildStarterProgram(string projectName) =>
$@"using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using DOSI.CORE.AccentManagement;
using DOSI.CORE.UIComponents;

// Welcome to {projectName}!
//
// The IDE compiles this project with Roslyn and invokes Program.Run().
// Whatever Control you return is shown in the Output pane on the right,
// so you can build little DOSI mini-apps using our custom controls.
public static class Program
{{
    public static Control Run()
    {{
        var accents = AccentManager.Instance;

        var heading = new TextBlock
        {{
            Text = ""Hello from {projectName}!"",
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
            Foreground = accents.TextPrimaryBrush,
            HorizontalAlignment = HorizontalAlignment.Center
        }};

        var subhead = new TextBlock
        {{
            Text = ""Edit Program.cs and press Run to see this update."",
            FontSize = 12,
            Foreground = accents.TextSecondaryBrush,
            Opacity = 0.85,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 18)
        }};

        var clicks = 0;
        var counter = new TextBlock
        {{
            Text = ""Clicks: 0"",
            FontSize = 13,
            Foreground = accents.TextPrimaryBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0)
        }};

        var button = new DOSIButton
        {{
            Text = ""Click me"",
            Padding = new Thickness(20, 8),
            HorizontalAlignment = HorizontalAlignment.Center
        }};

        var stack = new StackPanel
        {{
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = {{ heading, subhead, button, counter }}
        }};

        var root = new Grid
        {{
            Children = {{ stack }}
        }};

        // Pop a quick toast when the app launches so it's obvious the run
        // succeeded. The toast is added to the highest-level Panel we can
        // reach (so it floats above this window on the desktop instead of
        // being clipped inside our small content area). Delete this block
        // if you don't want the launch notification.
        root.AttachedToVisualTree += (_, _) =>
        {{
            var top = TopLevel.GetTopLevel(root);
            var host = top?.GetVisualDescendants()
                          .OfType<Panel>()
                          .FirstOrDefault() ?? root;
            DOSIPopNotification.Show(host, $""{projectName} launched"");
        }};

        button.Click += (_, _) =>
        {{
            clicks++;
            counter.Text = $""Clicks: {{clicks}}"";
            Console.WriteLine($""Button clicked: {{clicks}} time(s)."");
        }};

        Console.WriteLine(""{projectName} started."");
        return root;
    }}
}}
";
}
