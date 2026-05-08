using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DOSI.CORE.UserManagement;

namespace DAX.OSI.DefaultApplications;

/// <summary>
/// Choices the user can pick from in the DOSI Browser settings page for the
/// default search engine. Each value maps to a query template inside
/// <see cref="BrowserPreferences.GetSearchUrl"/>.
/// </summary>
public enum BrowserSearchEngine
{
    Google,
    Bing,
    DuckDuckGo,
    Brave,
    Startpage
}

/// <summary>
/// Persistent per-machine preferences for the DOSI Browser - home page URL,
/// default search engine, privacy toggles, page zoom, and the chosen
/// download folder. Stored as a small JSON file alongside the rest of DOSI's
/// settings so it survives app restarts.
/// </summary>
public sealed class BrowserPreferences
{
    private const string FileName = "Settings.json";
    /// <summary>Subfolder inside each user's home directory where browser
    /// state (settings, history, etc.) lives. Mirrors how real desktop
    /// browsers tuck their per-profile data under a single named folder.</summary>
    public const string PerUserFolderName = "Browser";
    /// <summary>File name used when no user is signed in - settings live
    /// alongside the rest of DOSI's machine-wide files in that case.</summary>
    private const string MachineWideFileName = "DOSIBrowserSettings.json";

    /// <summary>Default landing page when the user clicks the home button or
    /// opens a fresh tab. Accepts any URL the address bar would accept,
    /// including the built-in <c>dosi://home</c> page.</summary>
    public string HomeUrl { get; set; } = "dosi://home";

    /// <summary>Search engine the address bar should use when the user types
    /// a non-URL query.</summary>
    public BrowserSearchEngine SearchEngine { get; set; } = BrowserSearchEngine.Google;

    /// <summary>When true, every external page load gets a JS bridge that
    /// sets <c>navigator.doNotTrack = "1"</c> and adds a <c>DNT</c> hint.
    /// (Real DNT requires HTTP header support - this is the best-effort
    /// JS-side complement.)</summary>
    public bool SendDoNotTrack { get; set; } = true;

    /// <summary>Page zoom applied to external pages, expressed as a percent
    /// (100 = native size). Applied via injected CSS so it works across
    /// every supported renderer.</summary>
    public int ZoomPercent { get; set; } = 100;

    /// <summary>Folder downloads should be saved to. Defaults to the active
    /// DOSI user's per-user Downloads folder (provisioned by
    /// <see cref="UserManager.EnsureUserSubfolders"/>) so each account has
    /// its own download bucket. Falls back to the host OS Downloads folder
    /// when no user is signed in (for example during the first-run wizard).
    /// </summary>
    public string DownloadFolder { get; set; } = ResolveDefaultDownloadFolder();

    /// <summary>
    /// Computes the best-fit default for <see cref="DownloadFolder"/>.
    /// Prefers the active DOSI user's <c>Downloads</c> subfolder so each
    /// account has its own download bucket; falls back to the host OS
    /// Downloads folder when nobody is signed in yet.
    /// </summary>
    private static string ResolveDefaultDownloadFolder()
    {
        try
        {
            var user = UserManager.CurrentUser;
            if (user != null)
            {
                var perUser = UserManager.GetUserSubfolder(user, "Downloads");
                // Self-heal: ensure the folder exists for accounts created
                // before the standard subfolder set added "Downloads".
                Directory.CreateDirectory(perUser);
                return perUser;
            }
        }
        catch { /* fall through to system default */ }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? "" : Path.Combine(home, "Downloads");
    }

    [JsonIgnore]
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Resolves where the browser's prefs JSON should live right now.
    /// Per-user (<c>&lt;userhome&gt;/Browser/Settings.json</c>) when a user
    /// is signed in so each account keeps its own home page, search engine,
    /// zoom level, and download bucket; machine-wide fallback otherwise so
    /// the very first launch (before any user exists) still has somewhere
    /// to persist defaults.
    /// </summary>
    [JsonIgnore]
    public static string FilePath
    {
        get
        {
            try
            {
                var user = UserManager.CurrentUser;
                if (user != null)
                {
                    var folder = Path.Combine(UserManager.GetUserFolder(user.Username), PerUserFolderName);
                    Directory.CreateDirectory(folder);
                    return Path.Combine(folder, FileName);
                }
            }
            catch { /* fall back to machine-wide */ }
            return Path.Combine(AppContext.BaseDirectory, MachineWideFileName);
        }
    }

    private static BrowserPreferences? _current;

    /// <summary>The active preferences singleton. Lazily loaded from disk on
    /// first access; subsequent reads return the same instance.</summary>
    public static BrowserPreferences Current
    {
        get
        {
            if (_current != null) return _current;
            _current = Load();
            // Stay in sync with the active user's Downloads folder. Subscribed
            // exactly once at first access so we don't double-hook on reload.
            UserManager.CurrentUserChanged += OnCurrentUserChanged;
            return _current;
        }
    }

    /// <summary>
    /// Reloads <see cref="DownloadFolder"/> and the rest of the prefs to
    /// the freshly signed-in user's per-user store. Each user's settings
    /// file lives under their own home folder, so swapping accounts swaps
    /// the entire prefs payload (home page, search engine, zoom, downloads).
    /// </summary>
    private static void OnCurrentUserChanged(object? sender, DOSIUser? user)
    {
        try
        {
            // Reload the full prefs object from the new user's file (or
            // create defaults for them if it doesn't exist yet). We then
            // self-heal the download folder against the per-user Downloads
            // bucket UserManager.EnsureUserSubfolders provisioned.
            _current = Load();
            if (user != null)
            {
                var perUser = UserManager.GetUserSubfolder(user, "Downloads");
                Directory.CreateDirectory(perUser);

                var stored = _current.DownloadFolder ?? string.Empty;
                bool isUsersRoot = stored.StartsWith(UserManager.UsersRootPath,
                                    StringComparison.OrdinalIgnoreCase);
                if (string.IsNullOrWhiteSpace(stored) || !Directory.Exists(stored) || isUsersRoot)
                {
                    _current.DownloadFolder = perUser;
                    _current.Save();
                }
            }
        }
        catch { /* best-effort migration */ }
        // Notify subscribers so any open browser windows re-render with the
        // new user's home page, search engine, zoom, etc.
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Raised after <see cref="Save"/> commits to disk so any open
    /// browser windows can re-render their internal pages with the new
    /// values without needing to restart.</summary>
    public static event EventHandler? Changed;

    /// <summary>Reads the JSON file if present; falls back to defaults
    /// otherwise. Never throws - a corrupt file produces defaults so the
    /// browser always opens.</summary>
    public static BrowserPreferences Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<BrowserPreferences>(json, JsonOptions);
                if (loaded != null) return loaded;
            }
        }
        catch { /* fall back to defaults */ }
        return new BrowserPreferences();
    }

    /// <summary>Writes the current values to disk and notifies subscribers.</summary>
    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(FilePath, json);
        }
        catch { /* silent - prefs simply don't persist this run */ }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Builds the search URL for the current engine and a raw user
    /// query. The query is URL-encoded internally so callers can pass plain
    /// text straight from the address bar.</summary>
    public string GetSearchUrl(string query) => SearchEngine switch
    {
        BrowserSearchEngine.Bing       => $"https://www.bing.com/search?q={Uri.EscapeDataString(query)}",
        BrowserSearchEngine.DuckDuckGo => $"https://duckduckgo.com/?q={Uri.EscapeDataString(query)}",
        BrowserSearchEngine.Brave      => $"https://search.brave.com/search?q={Uri.EscapeDataString(query)}",
        BrowserSearchEngine.Startpage  => $"https://www.startpage.com/do/search?query={Uri.EscapeDataString(query)}",
        _                              => $"https://www.google.com/search?q={Uri.EscapeDataString(query)}",
    };

    /// <summary>Friendly display label for the given search engine.</summary>
    public static string GetEngineLabel(BrowserSearchEngine e) => e switch
    {
        BrowserSearchEngine.Bing       => "Bing",
        BrowserSearchEngine.DuckDuckGo => "DuckDuckGo",
        BrowserSearchEngine.Brave      => "Brave Search",
        BrowserSearchEngine.Startpage  => "Startpage",
        _                              => "Google",
    };

    /// <summary>One-line description shown under the engine name in the
    /// settings picker.</summary>
    public static string GetEngineTagline(BrowserSearchEngine e) => e switch
    {
        BrowserSearchEngine.Bing       => "Microsoft\u2019s search engine.",
        BrowserSearchEngine.DuckDuckGo => "Privacy-first, no tracking by default.",
        BrowserSearchEngine.Brave      => "Independent index, no profiling.",
        BrowserSearchEngine.Startpage  => "Anonymized Google results.",
        _                              => "The default. Fast and familiar.",
    };
}
