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
