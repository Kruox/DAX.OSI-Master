using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DOSI.CORE.AccentManagement;

namespace DOSI.CORE;

/// <summary>
/// System settings that are persisted to SystemSettings.json.
/// </summary>
public class SystemSettings
{
    /// <summary>
    /// Whether the application should launch in fullscreen mode.
    /// </summary>
    public bool Fullscreen { get; set; } = true;

    /// <summary>
    /// The default accent accent to use on startup.
    /// </summary>
    public DOSIAccent DefaultAccent { get; set; } = DOSIAccent.DarkBlue;
}

/// <summary>
/// Core system services for the DAX Virtual Operating System.
/// </summary>
public static class SystemCore
{
    public static string Name => "DOSI.CORE";
    public static string Version => "1.0.0.0";

    private static readonly string SettingsFileName = "SystemSettings.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// The current system settings.
    /// </summary>
    public static SystemSettings Settings { get; private set; } = new();

    /// <summary>
    /// Gets the path to the settings file (next to the executable).
    /// </summary>
    public static string SettingsFilePath => Path.Combine(AppContext.BaseDirectory, SettingsFileName);

    /// <summary>
    /// Initializes the core system services and loads settings.
    /// </summary>
    public static void Initialize()
    {
        LoadSettings();
    }

    /// <summary>
    /// Loads settings from SystemSettings.json. Creates default settings if file doesn't exist.
    /// </summary>
    public static void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                Settings = JsonSerializer.Deserialize<SystemSettings>(json, JsonOptions) ?? new SystemSettings();
            }
            else
            {
                // Create default settings file
                Settings = new SystemSettings();
                SaveSettings();
            }
        }
        catch
        {
            // If loading fails, use defaults
            Settings = new SystemSettings();
        }
    }

    /// <summary>
    /// Saves the current settings to SystemSettings.json.
    /// </summary>
    public static void SaveSettings()
    {
        try
        {
            var json = JsonSerializer.Serialize(Settings, JsonOptions);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch
        {
            // Silently fail if we can't save settings
        }
    }
}
