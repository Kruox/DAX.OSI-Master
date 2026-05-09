# DAX.OSI

> A Virtual Operating System experience built on **.NET 9** and **Avalonia UI**.

![.NET](https://img.shields.io/badge/.NET-9.0-512BD4?style=flat-square&logo=dotnet)
![Avalonia](https://img.shields.io/badge/Avalonia-UI-8B5CF6?style=flat-square)
![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux%20%7C%20macOS-informational?style=flat-square)
![Status](https://img.shields.io/badge/Status-In%20Development-orange?style=flat-square)

---

## Table of Contents

- [What is DAX.OSI?](#what-is-daxosi)
- [What is DOSI.CORE?](#what-is-dosicore)
- [Architecture Overview](#architecture-overview)
- [Project Layout](#project-layout)
- [Getting Started](#getting-started)
- [Tutorial](#tutorial)
- [Building Apps in DOSIIDE](#building-apps-in-dosiide)
  - [The `.dosiproj` Project Format](#the-dosiproj-project-format)
  - [Entry Points & The `Run` Convention](#entry-points--the-run-convention)
  - [Script-Style Files](#script-style-files)
  - [Multi-File Projects](#multi-file-projects)
  - [Building, Running & The Output Pane](#building-running--the-output-pane)
  - [The Visual Form Designer (`.dosiform`)](#the-visual-form-designer-dosiform)
  - [Wiring Event Handlers](#wiring-event-handlers)
  - [Docking & Responsive Layout](#docking--responsive-layout)
  - [Publishing Apps](#publishing-apps)
- [Roadmap](#roadmap)
- [License](#license)

---

## What is DAX.OSI?

**DAX.OSI** (the *DAX Open System Interface*) is a **virtual desktop operating system** that runs as a single cross-platform application. Instead of replacing your real OS, it boots inside its own window — complete with a login screen, a desktop, a window manager, default applications, and a settings hub — providing a sandboxed, themable, OS-like experience.

DAX.OSI is the **shell**: the boot screen, login flow, desktop environment, and the bundled "out-of-the-box" applications such as:

| Application | Purpose |
|---|---|
| `DOSITerminal` | Interactive command-line shell |
| `DOSIFileExplorer` | Browse and manage virtual files |
| `DOSIWebBrowser` | Embedded WebView-powered browser |
| `DOSIImageViewer` | View images |
| `DOSIIDE` | Lightweight code editor / IDE for building DOSI projects |
| `DOSISettingsScreen` | System settings (accent color, fullscreen, etc.) |

Every visible pixel — the boot animation, login screen, desktop wallpaper, taskbar, and windows — is rendered with Avalonia and powered by the components exposed by **DOSI.CORE**.

---

## What is DOSI.CORE?

**DOSI.CORE** is the **engine and SDK** behind DAX.OSI. It is a separate class library that provides every reusable system service and UI primitive needed to build a DOSI experience or a DOSI application.

Think of DOSI.CORE as the *kernel + standard library* of the virtual OS:

### 🧠 System Services
- `SystemCore` — central system bootstrapper, persists `SystemSettings.json`
- `SystemShutdown` / `SystemSignOut` — clean teardown lifecycle
- `UserManager` — user accounts, login, and session state
- `WallpaperManager` — desktop background management
- `AccentManager` — runtime accent/theming (e.g. `DarkBlue`, etc.)

### 🪟 Window & Screen Management
- `WindowManager` — owns and tracks every open `DOSIWindow`
- `WindowSnapManager` — Aero-style edge snapping
- `ScreenManager` — switching between full-screen surfaces (boot → login → desktop)

### 🎨 UI Component Library (`DOSI.CORE.UIComponents`)
A complete suite of OS-styled controls:
`DOSIWindow`, `DOSIButton`, `DOSITextBox`, `DOSILabel`, `DOSIDropDown`,
`DOSIScrollViewer`, `DOSIScrollBar`, `DOSITabControl`, `DOSISlider`,
`DOSIDialog`, `DOSIPopNotification`, `DOSIContextMenu`, `DOSICodeEditor`,
`DOSITerminalIO`.

### 🎬 Animations
`Tween`, `Easings`, `DOSILoadingAnim`, `DOSISuccessAnim` — the motion layer used throughout the shell.

### 🛠 Project & Designer System
- `DOSIProject`, `DOSIProjectCompiler`, `DOSIPublishedAppRegistry` — define, compile, and register third-party DOSI apps
- `DOSIDesigner`, `DOSIFormCodeBehind`, `DOSIFormHandlerCompiler` — visual form designer & code-behind compilation, used by `DOSIIDE`

### 💻 Terminal Subsystem
- `DOSITerminalManager` — coordinates terminal sessions and command dispatch

> In short: **DAX.OSI is the operating system you see. DOSI.CORE is what makes it tick — and what you'd reference to build your own DOSI apps.**

---

## Architecture Overview

```
┌──────────────────────────────────────────────────────────────┐
│                          DAX.OSI                             │
│  (Avalonia App: BootScreen → LoginScreen → DesktopScreen)    │
│                                                              │
│   Default Apps:  Terminal · FileExplorer · Browser · IDE …   │
└────────────────────────────┬─────────────────────────────────┘
                             │  references
                             ▼
┌──────────────────────────────────────────────────────────────┐
│                         DOSI.CORE                            │
│                                                              │
│   SystemCore   │  WindowManager  │  UserManager              │
│   AccentMgr    │  ScreenManager  │  WallpaperManager         │
│   UIComponents │  Animations     │  ProjectSystem · Designer │
└──────────────────────────────────────────────────────────────┘
                             │
                             ▼
                       Avalonia · .NET 9
```

The boot flow:

1. `Program.Main` builds the Avalonia `App`.
2. `App.Initialize` registers themes, calls `SystemCore.Initialize()`, and applies the saved accent.
3. `MainWindow` opens and the `ScreenManager` shows `BootScreen` → `LoginScreen` → `DesktopScreen`.
4. `WindowManager` hosts every launched application as a `DOSIWindow` on the desktop.

---

## Project Layout

```
DAX.OSI-Master/
├── DAX.OSI/                    # The shell application (entry point)
│   ├── Program.cs · App.cs · MainWindow.cs
│   ├── UI/                     # BootScreen, LoginScreen, DesktopScreen, …
│   ├── DefaultApplications/    # Terminal, FileExplorer, Browser, IDE, …
│   └── Controls/               # WebViewWrapper, etc.
│
├── DOSI.CORE/                  # The reusable engine / SDK
│   ├── SystemCore.cs · SystemShutdown.cs · SystemSignOut.cs
│   ├── UIComponents/           # All DOSI* controls
│   │   └── WindowManagement/   # WindowManager, WindowSnapManager, …
│   ├── Animations/             # Tween, Easings, loading/success anims
│   ├── ProjectSystem/          # DOSIProject + compiler + registry
│   ├── Designer/               # Visual form designer
│   ├── UserManagement/         # UserManager
│   ├── WallpaperManagement/    # WallpaperManager
│   ├── AccentManagement/       # AccentManager + DOSIAccent enum
│   └── DOSITerminalManagement/ # DOSITerminalManager
│
└── tutorial.html               # Quick-start interactive tutorial
```

---

## Getting Started

### Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Visual Studio 2026 (or any IDE with .NET 9 support)

### Build & Run

```powershell
git clone https://github.com/Kruox/DAX.OSI-Master.git
cd DAX.OSI-Master
dotnet build
dotnet run --project DAX.OSI
```

On first launch DAX.OSI will:

1. Create `SystemSettings.json` next to the executable.
2. Play the boot animation.
3. Drop you at the login screen.

---

## Tutorial

A friendly, interactive walkthrough is included in **[`tutorial.html`](./tutorial.html)** — open it in any browser to see how DAX.OSI boots, how DOSI.CORE plugs in, and how to write your first DOSI app.

---

## Building Apps in DOSIIDE

DAX.OSI ships with **DOSIIDE** — a Visual-Studio-style environment that lives *inside* the OS. It's a fully sandboxed code workspace rooted at the signed-in user's home folder, backed by an in-memory **Roslyn** compiler and a custom visual designer.

```
┌──────────────────────────────────────────────────────────────────────┐
│ DOSIIDE                                                              │
│ ┌──────────┬───────────────────────────────────────────────────────┐ │
│ │ Toolbar  │ New ▪ Open ▪ Save ▪ Save All ▪ Build ▪ Run ▪ Publish  │ │
│ ├──────────┼───────────────────────────────────────────────────────┤ │
│ │ Solution │ Tabs (multi-file, dirty markers, close)               │ │
│ │ Explorer │ ┌───────────────────────────────────────────────────┐ │ │
│ │  ▾ MyApp │ │ DOSICodeEditor   │   DOSIDesigner (.dosiform)     │ │ │
│ │   Prog.cs│ │ line numbers     │   toolbox + canvas + props     │ │ │
│ │   Form.. │ └───────────────────────────────────────────────────┘ │ │
│ │          │ Output pane: build log + run preview                  │ │
│ ├──────────┴───────────────────────────────────────────────────────┤ │
│ │ Status:  caret · encoding · file path · active project           │ │
│ └──────────────────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────────────┘
```

### The `.dosiproj` Project Format

A DOSI project is just a **folder** containing a single `*.dosiproj` JSON manifest. Every project lives under `~/Projects/<name>/` in the signed-in user's sandbox.

```jsonc
{
  "Name":         "MyCoolApp",
  "Kind":         "DOSIControl",   // or "Console"
  "EntryType":    "Program",       // class containing the entry method
  "EntryMethod":  "Run",           // static method invoked on Run
  "FormatVersion": 1,
  "CreatedUtc":   "2025-01-01T00:00:00Z"
}
```

The IDE's toolbar **"New project"** button scaffolds this for you and adds a starter `Program.cs`. `DOSIProjectManager` is the API you'd use programmatically (`Create`, `Load`, `ListProjects`, `FindProjectFor`).

### Entry Points & The `Run` Convention

When you press **Run**, `DOSIProjectCompiler.BuildAndRun` reflects on the manifest's `EntryType` + `EntryMethod` and invokes it as a `static` method. The return value decides what happens next:

| `Run()` returns         | What DOSIIDE does                                                  |
|-------------------------|--------------------------------------------------------------------|
| `void`                  | Executes the code; captures `Console.Out` into the Output pane.    |
| `Avalonia.Controls.Control` | Hosts the returned control inside a real `DOSIWindow` on the desktop. |

```csharp
// Program.cs
using Avalonia.Controls;
using DOSI.CORE.UIComponents;

public static class Program
{
    public static Control Run()
    {
        var stack = new StackPanel { Spacing = 8, Margin = new(16) };
        stack.Children.Add(new DOSILabel  { Text = "Hello from a DOSI app!" });
        stack.Children.Add(new DOSIButton { Content = "OK" });
        return stack; // ← becomes the window's Content
    }
}
```

### Script-Style Files

For quick experiments, DOSIIDE supports **top-level statements** without a class or namespace. The compiler auto-wraps them into a `Program.Run` shape:

```csharp
using DOSI.CORE.UIComponents;
var btn = new DOSIButton { Content = "Click me!" };
btn.Click += (_, _) => System.Console.WriteLine("clicked");
return btn;
```

### Multi-File Projects

Add as many `.cs` files as you like — every file in the project folder (excluding `bin/` and `obj/`) is compiled together as a single assembly. Reference any DOSI control or BCL type freely; the compiler injects every assembly already loaded in the host AppDomain plus the runtime TPA list.

```
MyCoolApp/
├── MyCoolApp.dosiproj
├── Program.cs            // contains `static Run()`
├── Models/User.cs
├── Views/MainView.cs     // your custom DOSIWindow
└── MainForm.dosiform     // visual-designer file (optional)
```

### Building, Running & The Output Pane

- **Build (⚒)** — compile only; warnings + errors stream to the Output pane.
- **Run (▶)** — build, invoke the entry point, capture stdout, and host any returned `Control` in a live preview window.
- **Save / Save All** — flush dirty tabs.
- **Status bar** — shows caret position, encoding, file path, and the active project (auto-detected from the focused tab).

Diagnostics are formatted with `(file:line,col): severity CSxxxx: message` so you can click straight to the offending line.

### The Visual Form Designer (`.dosiform`)

Files with the `.dosiform` extension open in **DOSIDesigner** instead of the code editor. Each form is a JSON document describing a `DOSIWindow` and its placed controls.

**Designer features:**
- Toolbox of every DOSI control (drag onto the canvas)
- Click-to-select, arrow-key nudging, drag-to-move, corner resizing
- 8 px snap-to-grid with grid overlay
- Live property grid for the selected control
- Dirty tracking + JSON save/load

At run time, `DOSIFormLoader` materialises the JSON into a real `DOSIWindow` — so a pure designer-only project (no `.cs`) can still launch.

### Wiring Event Handlers

Each control in the designer has a `Handlers` dictionary mapping **event name → C# code body**. The handler compiler synthesises one method per entry, compiles them into a single class, and wires them to the live instance.

```csharp
// Click handler body for `button1`:
DOSIPopNotification.Show("Hi", "You clicked " + button1.Content);
```

Form-level events (`Load`, `Closing`) are stored on the form itself under the synthetic name `Form` and bound to the host `DOSIWindow`.

### Docking & Responsive Layout

Every designed control has a `Dock` property powering responsive re-flow:

| `DOSIDock` | Behaviour                                       |
|------------|-------------------------------------------------|
| `None`     | Honors the absolute X/Y/Width/Height.           |
| `Top`      | Pinned to the top edge, full width.             |
| `Bottom`   | Pinned to the bottom edge, full width.          |
| `Left`     | Pinned to the left edge, full height.           |
| `Right`    | Pinned to the right edge, full height.          |
| `Fill`     | Fills the remaining space.                      |

Designed-once forms automatically adapt when the user resizes the running window.

### Publishing Apps

Once a project builds and runs cleanly, hit **Publish (↑)** in the toolbar. DOSIIDE registers it with `DOSIPublishedAppRegistry`, making it launchable from the desktop just like any built-in app — your custom DOSI app becomes a first-class citizen of the OS.

---

## Roadmap

- [ ] Expanded default app suite
- [ ] Plugin marketplace via `DOSIPublishedAppRegistry`
- [ ] Multi-user profiles and permissions
- [ ] Theming beyond accent colors
- [ ] Networking / virtual filesystem layer

---

## License

See repository for license information.

---

<p align="center">
  <strong>DAX.OSI</strong> — an OS that lives inside a window. <br/>
  Powered by <strong>DOSI.CORE</strong>, .NET 9, and Avalonia.
</p>
