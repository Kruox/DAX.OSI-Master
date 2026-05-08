using System;
using System.Linq;
using DOSI.CORE.AccentManagement;
using DOSI.CORE.UIComponents;
using DOSI.CORE.UIComponents.WindowManagement;
using DOSI.CORE.UserManagement;

namespace DOSI.CORE.DOSITerminalManagement;

/// <summary>
/// Manages terminal command processing and output for DOSITerminal instances.
/// Centralizes all terminal logic including command handling and welcome messages.
/// </summary>
public class DOSITerminalManager
{
    private readonly DOSITerminalIO _terminal;
    private readonly Action? _closeWindowAction;
    private readonly Action<string, string?>? _openApplicationAction;

    // When non-null, the next submitted line is routed here instead of being parsed
    // as a command. Used by interactive multi-step flows like `useradd`.
    private Action<string>? _pendingInputHandler;

    public const string Version = "1.0.0";
    public const string DefaultPrompt = "C:\\DOSI>";

    /// <summary>
    /// Creates a new terminal manager for the specified terminal control.
    /// </summary>
    /// <param name="terminal">The terminal IO control to manage.</param>
    /// <param name="closeWindowAction">Optional action to close the parent window.</param>
    /// <param name="openApplicationAction">Optional action to open applications (appName, args).</param>
    public DOSITerminalManager(DOSITerminalIO terminal, Action? closeWindowAction = null, Action<string, string?>? openApplicationAction = null)
    {
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        _closeWindowAction = closeWindowAction;
        _openApplicationAction = openApplicationAction;

        _terminal.SetPrompt(DefaultPrompt);
        _terminal.CommandSubmitted += OnCommandSubmitted;
    }

    /// <summary>
    /// Displays the welcome message when the terminal opens.
    /// </summary>
    public void ShowWelcome()
    {
        var now = DateTime.Now;
        var greeting = now.Hour switch
        {
            < 12 => "Good morning",
            < 17 => "Good afternoon",
            _ => "Good evening"
        };

        _terminal.WriteLine();
        _terminal.WriteLine($"  DOSI Terminal v{Version}");
        _terminal.WriteLine($"  ---------------------------------");
        _terminal.WriteLine($"  {greeting}, {Environment.UserName}");
        _terminal.WriteLine($"  {now:dddd, MMMM d, yyyy} • {now:h:mm tt}");
        _terminal.WriteLine();
        _terminal.WriteLine($"  Host: {Environment.MachineName} | Accent: {AccentManager.GetAccentDisplayName(AccentManager.Instance.CurrentAccent)}");
        _terminal.WriteLine();
    }

    private void OnCommandSubmitted(object? sender, TerminalCommandEventArgs e)
    {
        // Interactive mode: route the line to the pending handler.
        if (_pendingInputHandler != null)
        {
            var handler = _pendingInputHandler;
            _pendingInputHandler = null;
            handler(e.Command ?? string.Empty);
            return;
        }

        if (string.IsNullOrWhiteSpace(e.Command))
            return;

        var parts = e.Command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant();
        var args = parts.Skip(1).ToArray();

        ExecuteCommand(command, args, e.Command);
        _terminal.WriteLine();
    }

    private void ExecuteCommand(string command, string[] args, string fullCommand)
    {
        switch (command)
        {
            case "help":
            case "?":
                ShowHelp();
                break;

            case "cls":
            case "clear":
                _terminal.Clear();
                break;

            case "echo":
                _terminal.WriteLine(string.Join(" ", args));
                break;

            case "date":
                _terminal.WriteLine($"The current date is: {DateTime.Now:ddd MM/dd/yyyy}");
                break;

            case "time":
                _terminal.WriteLine($"The current time is: {DateTime.Now:HH:mm:ss.ff}");
                break;

            case "ver":
            case "version":
                _terminal.WriteLine();
                _terminal.WriteLine($"DOSI Terminal [Version {Version}]");
                break;

            case "whoami":
                _terminal.WriteLine($"dosi\\{Environment.UserName.ToLowerInvariant()}");
                break;

            case "hostname":
                _terminal.WriteLine(Environment.MachineName);
                break;

            case "systeminfo":
                ShowSystemInfo();
                break;

            case "accent":
                HandleAccentCommand(args);
                break;

            case "color":
                HandleColorCommand(args);
                break;

            case "dir":
            case "ls":
                ShowFakeDir();
                break;

            case "browser":
            case "web":
                OpenBrowser(args);
                break;

            case "useradd":
            case "adduser":
            case "createuser":
                StartUserAddWizard();
                break;

            case "exit":
            case "quit":
                _closeWindowAction?.Invoke();
                break;

            case "shutdown":
                HandleShutdownCommand(args);
                break;

            case "signout":
            case "logout":
                HandleSignOutCommand();
                break;

            default:
                _terminal.WriteLine($"'{fullCommand}' is not recognized as an internal or external command,");
                _terminal.WriteLine("operable program or batch file.");
                break;
        }
    }

    private void ShowHelp()
    {
        _terminal.WriteLine("Available commands:");
        _terminal.WriteLine();
        _terminal.WriteLine("  BROWSER      Opens the DOSI web browser.");
        _terminal.WriteLine("  CLS          Clears the screen.");
        _terminal.WriteLine("  COLOR        Sets the terminal colors.");
        _terminal.WriteLine("  DATE         Displays the date.");
        _terminal.WriteLine("  DIR          Displays a list of files.");
        _terminal.WriteLine("  ECHO         Displays messages.");
        _terminal.WriteLine("  EXIT         Exits the terminal.");
        _terminal.WriteLine("  HELP         Provides help information.");
        _terminal.WriteLine("  HOSTNAME     Prints the machine name.");
        _terminal.WriteLine("  SYSTEMINFO   Displays system information.");
        _terminal.WriteLine("  ACCENT       Lists or changes the accent color (saved per-user when signed in,");
        _terminal.WriteLine("               otherwise saved to SystemSettings.json).");
        _terminal.WriteLine("  SHUTDOWN     Shuts down DAX.OSI. Use 'shutdown -t <sec>' to schedule,");
        _terminal.WriteLine("               or 'shutdown -a' to abort a pending shutdown.");
        _terminal.WriteLine("  SIGNOUT      Signs out the current user and returns to the login screen.");
        _terminal.WriteLine("  TIME         Displays the time.");
        _terminal.WriteLine("  USERADD      Creates a new user account (interactive).");
        _terminal.WriteLine("  VER          Displays the version.");
        _terminal.WriteLine("  WHOAMI       Displays the current user.");
    }

    private void ShowSystemInfo()
    {
        _terminal.WriteLine();
        _terminal.WriteLine($"Host Name:                 {Environment.MachineName}");
        _terminal.WriteLine($"OS Name:                   {Environment.OSVersion.Platform}");
        _terminal.WriteLine($"OS Version:                {Environment.OSVersion.Version}");
        _terminal.WriteLine($"System Type:               {(Environment.Is64BitOperatingSystem ? "x64-based PC" : "x86-based PC")}");
        _terminal.WriteLine($"Processor(s):              {Environment.ProcessorCount} Processor(s) Installed.");
        _terminal.WriteLine($".NET Version:              {Environment.Version}");
        _terminal.WriteLine($"Current Accent:            {AccentManager.GetAccentDisplayName(AccentManager.Instance.CurrentAccent)}");
    }

    private void HandleAccentCommand(string[] args)
    {
        var accents = AccentManager.Instance;
        var signedIn = UserManager.CurrentUser;

        if (args.Length == 0)
        {
            _terminal.WriteLine("Available accent colors:");
            _terminal.WriteLine();
            foreach (var accent in AccentManager.GetAvailableAccents())
            {
                var marker = accent == accents.CurrentAccent ? " (current)" : "";
                _terminal.WriteLine($"  {AccentManager.GetAccentDisplayName(accent)}{marker}");
            }
            _terminal.WriteLine();
            _terminal.WriteLine("Usage: ACCENT <name>");
            _terminal.WriteLine(signedIn != null
                ? $"  Saves to user profile: {signedIn.Username}"
                : "  Not signed in - saves to SystemSettings.json (system default).");
            return;
        }

        var requested = string.Join("", args).ToLowerInvariant().Replace(" ", "");
        DOSIAccent? matched = null;
        foreach (var accent in AccentManager.GetAvailableAccents())
        {
            if (accent.ToString().ToLowerInvariant() == requested ||
                AccentManager.GetAccentDisplayName(accent).ToLowerInvariant().Replace(" ", "") == requested)
            {
                matched = accent;
                break;
            }
        }

        if (matched is null)
        {
            _terminal.WriteLine($"Unknown accent: '{string.Join(" ", args)}'");
            _terminal.WriteLine("Type ACCENT to see available accent colors.");
            return;
        }

        var target = matched.Value;
        var displayName = AccentManager.GetAccentDisplayName(target);

        // Animate the live UI the same way the login screen does on user-select.
        accents.ApplyAccentAnimated(target, TimeSpan.FromMilliseconds(550));

        if (signedIn != null)
        {
            // Persist as a per-user preference (same path used at user creation).
            if (UserManager.SetUserAccent(signedIn, target))
                _terminal.WriteLine($"Accent saved to profile '{signedIn.Username}': {displayName}");
            else
                _terminal.WriteLine($"Accent applied ({displayName}), but failed to save to profile.");
        }
        else
        {
            // Persist as the system-wide default.
            SystemCore.Settings.DefaultAccent = target;
            SystemCore.SaveSettings();
            _terminal.WriteLine($"Accent saved to SystemSettings.json: {displayName}");
            _terminal.WriteLine($"  ({SystemCore.SettingsFilePath})");
        }
    }

    private void HandleColorCommand(string[] args)
    {
        _terminal.WriteLine("Sets the default terminal foreground and background colors.");
        _terminal.WriteLine();
        _terminal.WriteLine("COLOR [attr]");
        _terminal.WriteLine();
        _terminal.WriteLine("  attr    Specifies color attribute of console output.");
        _terminal.WriteLine();
        _terminal.WriteLine("Note: Color customization not yet implemented.");
    }

    private void ShowFakeDir()
    {
        _terminal.WriteLine(" Volume in drive C is DOSI_SYSTEM");
        _terminal.WriteLine(" Volume Serial Number is D051-0000");
        _terminal.WriteLine();
        _terminal.WriteLine(" Directory of C:\\DOSI");
        _terminal.WriteLine();
        _terminal.WriteLine($"{DateTime.Now:MM/dd/yyyy  hh:mm tt}    <DIR>          .");
        _terminal.WriteLine($"{DateTime.Now:MM/dd/yyyy  hh:mm tt}    <DIR>          ..");
        _terminal.WriteLine($"{DateTime.Now:MM/dd/yyyy  hh:mm tt}    <DIR>          Applications");
        _terminal.WriteLine($"{DateTime.Now:MM/dd/yyyy  hh:mm tt}    <DIR>          System");
        _terminal.WriteLine($"{DateTime.Now:MM/dd/yyyy  hh:mm tt}             1,024 readme.txt");
        _terminal.WriteLine("               1 File(s)          1,024 bytes");
        _terminal.WriteLine("               4 Dir(s)   1,000,000,000 bytes free");
    }

    private void OpenBrowser(string[] args)
    {
        var url = args.Length > 0 ? string.Join(" ", args) : null;

        if (_openApplicationAction != null)
        {
            _openApplicationAction("browser", url);
            _terminal.WriteLine("Opening DOSI Browser...");
        }
        else
        {
            _terminal.WriteLine("Cannot open browser: Application launcher not available.");
        }
    }

    #region Sign Out

    private void HandleSignOutCommand()
    {
        var user = UserManager.CurrentUser;
        if (user == null)
        {
            _terminal.WriteLine("No user is currently signed in.");
            return;
        }

        _terminal.WriteLine($"Signing out {user.DisplayName}...");
        SystemSignOut.Begin();
    }

    #endregion

    #region Shutdown

    // Live tick subscriptions owned by this terminal so they unhook cleanly.
    private Action<int>? _shutdownTickHandler;
    private Action? _shutdownCancelHandler;
    private Action? _shutdownExecHandler;

    private void HandleShutdownCommand(string[] args)
    {
        if (args.Length == 0)
        {
            _terminal.WriteLine("Shutting down DAX.OSI...");
            SystemShutdown.Begin(0);
            return;
        }

        var first = args[0].ToLowerInvariant();

        if (first is "-a" or "/a" or "--abort")
        {
            if (SystemShutdown.IsShutdownPending)
            {
                SystemShutdown.Cancel();
                // Cancel handler below prints the abort message and unhooks.
            }
            else
            {
                _terminal.WriteLine("No shutdown is currently scheduled.");
            }
            return;
        }

        if (first is "-t" or "/t")
        {
            if (args.Length < 2 || !int.TryParse(args[1], out var seconds) || seconds < 0)
            {
                _terminal.WriteLine("Usage: shutdown -t <seconds>");
                return;
            }

            if (seconds == 0)
            {
                _terminal.WriteLine("Shutting down DAX.OSI...");
                SystemShutdown.Begin(0);
                return;
            }

            // Detach any previous countdown handlers from this terminal first.
            UnhookShutdownHandlers();

            _terminal.WriteLine($"DAX.OSI will shut down in {seconds} second(s).");
            _terminal.WriteLine("Type 'shutdown -a' to cancel.");

            _shutdownTickHandler = remaining =>
            {
                // Avoid one line per second. Print only at meaningful
                // milestones plus the final 5-second countdown.
                if (remaining == seconds || remaining == 60 || remaining == 30 ||
                    remaining == 20 || remaining == 10 || remaining <= 5)
                {
                    _terminal.WriteLine(
                        $"  Shutdown in {remaining} second{(remaining == 1 ? "" : "s")}...");
                }
            };

            _shutdownCancelHandler = () =>
            {
                _terminal.WriteLine("Scheduled shutdown cancelled.");
                UnhookShutdownHandlers();
            };

            _shutdownExecHandler = () => UnhookShutdownHandlers();

            SystemShutdown.CountdownTick += _shutdownTickHandler;
            SystemShutdown.CountdownCancelled += _shutdownCancelHandler;
            SystemShutdown.ShuttingDown += _shutdownExecHandler;

            SystemShutdown.Begin(seconds);
            return;
        }

        _terminal.WriteLine("Usage: shutdown [-t <seconds>] [-a]");
    }

    private void UnhookShutdownHandlers()
    {
        if (_shutdownTickHandler != null)
        {
            SystemShutdown.CountdownTick -= _shutdownTickHandler;
            _shutdownTickHandler = null;
        }
        if (_shutdownCancelHandler != null)
        {
            SystemShutdown.CountdownCancelled -= _shutdownCancelHandler;
            _shutdownCancelHandler = null;
        }
        if (_shutdownExecHandler != null)
        {
            SystemShutdown.ShuttingDown -= _shutdownExecHandler;
            _shutdownExecHandler = null;
        }
    }

    #endregion

    #region Interactive USERADD

    /// <summary>State accumulated as the user walks through the wizard prompts.</summary>
    private sealed class UserAddState
    {
        public string Username = string.Empty;
        public string DisplayName = string.Empty;
        public string Password = string.Empty;
        public DOSIAccent? Accent;
        public bool IsAdministrator;
        public DOSIAccent[] AvailableAccents = Array.Empty<DOSIAccent>();
    }

    /// <summary>
    /// Prompts the user for a single line of input. <paramref name="handler"/> receives
    /// the entered text on the next Enter; it can call <see cref="AskNext"/> again to
    /// chain prompts together.
    /// </summary>
    private void AskNext(string promptText, Action<string> handler)
    {
        _terminal.SetPrompt(promptText);
        _pendingInputHandler = handler;
    }

    /// <summary>Returns control to the regular command prompt.</summary>
    private void EndInteractive()
    {
        _pendingInputHandler = null;
        _terminal.SetPrompt(DefaultPrompt);
        _terminal.WriteLine();
    }

    /// <summary>
    /// Returns true and prints a cancel notice if <paramref name="input"/> is the magic
    /// abort word. The caller should stop the flow when this returns true.
    /// </summary>
    private bool HandleCancel(string input)
    {
        if (!string.Equals(input?.Trim(), "cancel", StringComparison.OrdinalIgnoreCase))
            return false;

        _terminal.WriteLine("useradd: cancelled.");
        EndInteractive();
        return true;
    }

    private void StartUserAddWizard()
    {
        UserManager.Initialize();

        _terminal.WriteLine();
        _terminal.WriteLine("=== Create new user account ===");
        _terminal.WriteLine("Type 'cancel' at any prompt to abort.");
        _terminal.WriteLine();

        var state = new UserAddState();
        AskUsername(state);
    }

    // ---- Step 1: Username ----
    private void AskUsername(UserAddState state)
    {
        AskNext("Username:", input =>
        {
            if (HandleCancel(input)) return;

            var name = (input ?? string.Empty).Trim();
            if (!UserManager.IsValidUsername(name))
            {
                _terminal.WriteLine("  Invalid username. Must start with a letter and be 3-32 chars (a-z, 0-9, _ -).");
                AskUsername(state);
                return;
            }
            if (UserManager.UserExists(name))
            {
                _terminal.WriteLine($"  A user named '{name}' already exists.");
                AskUsername(state);
                return;
            }

            state.Username = name.ToLowerInvariant();
            AskDisplayName(state);
        });
    }

    // ---- Step 2: Display name (defaults to username) ----
    private void AskDisplayName(UserAddState state)
    {
        AskNext($"Display name [{state.Username}]:", input =>
        {
            if (HandleCancel(input)) return;

            var trimmed = (input ?? string.Empty).Trim();
            state.DisplayName = string.IsNullOrEmpty(trimmed) ? state.Username : trimmed;
            AskPassword(state);
        });
    }

    // ---- Step 3: Password ----
    private void AskPassword(UserAddState state)
    {
        _terminal.WriteLine("  (Note: password is shown as you type in this terminal.)");
        AskNext("Password:", input =>
        {
            if (HandleCancel(input)) return;

            var pwd = input ?? string.Empty;
            if (!UserManager.IsValidPassword(pwd))
            {
                _terminal.WriteLine("  Password must be at least 4 characters and not start or end with a space.");
                AskPassword(state);
                return;
            }

            state.Password = pwd;
            AskConfirmPassword(state);
        });
    }

    // ---- Step 4: Confirm password ----
    private void AskConfirmPassword(UserAddState state)
    {
        AskNext("Confirm password:", input =>
        {
            if (HandleCancel(input)) return;

            if ((input ?? string.Empty) != state.Password)
            {
                _terminal.WriteLine("  Passwords don't match. Let's try the password again.");
                state.Password = string.Empty;
                AskPassword(state);
                return;
            }

            AskAccent(state);
        });
    }

    // ---- Step 5: Accent picker ----
    private void AskAccent(UserAddState state)
    {
        state.AvailableAccents = AccentManager.GetAvailableAccents().ToArray();

        _terminal.WriteLine();
        _terminal.WriteLine("Available accent colors:");
        for (int i = 0; i < state.AvailableAccents.Length; i++)
        {
            var t = state.AvailableAccents[i];
            _terminal.WriteLine($"  [{i + 1,2}] {AccentManager.GetAccentDisplayName(t)}");
        }
        _terminal.WriteLine("  [ 0] Use system default");

        AskNext("Accent number:", input =>
        {
            if (HandleCancel(input)) return;

            var raw = (input ?? string.Empty).Trim();
            if (!int.TryParse(raw, out var idx) || idx < 0 || idx > state.AvailableAccents.Length)
            {
                _terminal.WriteLine($"  Please enter a number from 0 to {state.AvailableAccents.Length}.");
                AskAccent(state);
                return;
            }

            state.Accent = idx == 0 ? null : state.AvailableAccents[idx - 1];
            AskAdministrator(state);
        });
    }

    // ---- Step 6: Administrator ----
    private void AskAdministrator(UserAddState state)
    {
        AskNext("Make this account an administrator? (y/N):", input =>
        {
            if (HandleCancel(input)) return;

            var v = (input ?? string.Empty).Trim().ToLowerInvariant();
            state.IsAdministrator = v is "y" or "yes";
            AskConfirm(state);
        });
    }

    // ---- Step 7: Confirm ----
    private void AskConfirm(UserAddState state)
    {
        var accentName = state.Accent.HasValue
            ? AccentManager.GetAccentDisplayName(state.Accent.Value)
            : "system default";

        _terminal.WriteLine();
        _terminal.WriteLine("Review:");
        _terminal.WriteLine($"  Username:      {state.Username}");
        _terminal.WriteLine($"  Display name:  {state.DisplayName}");
        _terminal.WriteLine($"  Accent:        {accentName}");
        _terminal.WriteLine($"  Administrator: {(state.IsAdministrator ? "yes" : "no")}");

        AskNext("Create this account? (Y/n):", input =>
        {
            if (HandleCancel(input)) return;

            var v = (input ?? string.Empty).Trim().ToLowerInvariant();
            if (v is "n" or "no")
            {
                _terminal.WriteLine("useradd: cancelled.");
                EndInteractive();
                return;
            }

            CommitUser(state);
        });
    }

    private void CommitUser(UserAddState state)
    {
        var result = UserManager.CreateUser(
            state.Username,
            state.Password,
            out var user,
            displayName: state.DisplayName,
            isAdministrator: state.IsAdministrator);

        if (result != UserCreationResult.Success || user == null)
        {
            _terminal.WriteLine(result switch
            {
                UserCreationResult.UsernameAlreadyExists => $"  A user named '{state.Username}' already exists.",
                UserCreationResult.InvalidUsername => "  Invalid username.",
                UserCreationResult.InvalidPassword => "  Invalid password.",
                UserCreationResult.IOError => "  Couldn't write the account file. Check disk permissions.",
                _ => "  Failed to create the account."
            });
            EndInteractive();
            return;
        }

        if (state.Accent.HasValue)
            UserManager.SetUserAccent(user, state.Accent.Value);

        _terminal.WriteLine();
        _terminal.WriteLine($"  Account created at: {UserManager.GetUserFilePath(user.Username)}");
        _terminal.WriteLine($"  Welcome to DAX.OSI, {user.DisplayName}.");
        EndInteractive();
    }

    #endregion
}
