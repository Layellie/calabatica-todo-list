<p align="center">
  <img src="docs/icon.png" width="96" alt="CALABATICA icon" />
</p>

<h1 align="center">CALABATICA</h1>

<p align="center">
  A neon HUD-style to-do list and activity tracker for Windows.
</p>

<p align="center">
  <img alt="Platform" src="https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-00F0FF?style=flat-square" />
  <img alt=".NET" src="https://img.shields.io/badge/.NET-10-FCEE0A?style=flat-square" />
  <img alt="UI" src="https://img.shields.io/badge/UI-WPF-00F0FF?style=flat-square" />
  <img alt="License" src="https://img.shields.io/badge/license-MIT-FCEE0A?style=flat-square" />
</p>

> **Fan project.** CALABATICA's interface is inspired by the UI aesthetic of **Cyberpunk 2077**.
> It is a non-commercial fan project and is **not affiliated with, endorsed by, or connected to CD PROJEKT RED**.
> Cyberpunk 2077 is a registered trademark of CD PROJEKT S.A. No game assets (images, logos, sounds or fonts)
> are used in this project; all graphics are original.

![Main screen](docs/screenshots/main.png)

## Features

**Tasks**
- Natural-language quick add: `Write report #work @tomorrow @14:00 ! *weekly ~2`
- Priorities, categories, due dates and times, notes, subtasks with automatic progress
- Repeat daily, weekly, monthly or on **selected weekdays** (e.g. Mon/Wed/Fri, weekdays, weekend)
- Reminders (on time, 15 min, 1 h or 1 day before)
- Search, smart lists (Today / Upcoming / Overdue), category filters
- Sort by creation, due date or priority, or reorder by **drag and drop**
- Templates: save the visible tasks as a reusable set
- Undo delete (`Ctrl+Z`)

**Focus and insights**
- Built-in focus (Pomodoro) timer per task; tracked time goes into your daily activity
- Activity log and a 30 / 7-day chart (hover a bar to see that day)
- Stats: current and longest streak, weekly comparison, peak hour, average by weekday
- Month calendar with recurring tasks projected onto future days

**App**
- Turkish and English interface
- Five color themes (Neon, Synthwave, Matrix, Amber, Arctic) and three UI scales
- Runs in the system tray with Windows notifications; optional start with Windows
- Checks GitHub Releases for new versions
- Data stays on your computer (`%LOCALAPPDATA%\CyberTodo\cyber_data.json`), no account and no telemetry

| Calendar | Stats |
|---|---|
| ![Calendar](docs/screenshots/calendar.png) | ![Stats](docs/screenshots/stats.png) |

| Settings | Setup |
|---|---|
| ![Settings](docs/screenshots/settings.png) | ![Setup](docs/screenshots/setup.png) |

## Quick add syntax

Type a task in the "new objective" box and press `Enter`. Commands start with a symbol, so normal words in a title are never mistaken for a date.

| Command | Meaning | Example |
|---|---|---|
| `#name` | Category | `#work` |
| `!` | High priority | `!` |
| `@today` `@tomorrow` `@friday` `@25.11` | Due date | `@friday` |
| `@HH:MM` | Due time (today if no date) | `@14:00` |
| `*daily` `*weekly` `*monthly` | Repeat | `*weekly` |
| `*weekdays` `*weekend` `*mon,wed,fri` | Repeat on selected days | `*mon,wed,fri` |
| `~hours` | Estimated hours | `~1.5` |

Turkish words work too (`@yarın`, `*haftalık`, `*pzt,çar,cum`, …).

## Keyboard shortcuts

| Keys | Action |
|---|---|
| `Ctrl+N` | Focus the new task box |
| `Ctrl+F` | Search |
| `Ctrl+Z` | Undo delete |
| `Ctrl+,` | Settings |
| `Enter` | Add / save |
| `Esc` | Close dialog / clear search |

## Install

Releases are not published yet. For now, build from source.

The installer (`CALABATICA-Setup-x.y.z.exe`, under 1 MB) installs per user without admin rights. If the
**.NET 10 Desktop Runtime** is missing, it downloads Microsoft's official installer, verifies its Microsoft
signature and installs it. The installer UI follows the Windows display language (Turkish or English).

## Build from source

Requirements: Windows 10/11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
# Run the app
dotnet run --project CyberTodo/CyberTodo.csproj

# Build the installer -> artifacts/CALABATICA-Setup-1.0.0.exe
./build-installer.ps1 -Version 1.0.0
```

See [RELEASING.md](RELEASING.md) for versioning and the release workflow.

## Project structure

```
CyberTodo/              WPF app (MVVM, CommunityToolkit.Mvvm)
  Models/               Tasks, days, settings, themes, templates
  Services/             JSON storage, localization, update check, Windows startup
  ViewModels/           Main view model, editor, quick-add parser, stats/calendar builder
  MainWindow.xaml       UI (no third-party UI or chart libraries)
  TrayIconHost.cs       System tray icon and notifications
Installer/              Themed setup program (.NET Framework 4.8)
build-installer.ps1     Builds the app and the installer
.github/workflows/      Release workflow (runs on version tags)
```

## Tech notes

- C# 14, .NET 10, WPF, MVVM with `[ObservableProperty]` / `[RelayCommand]` source generators
- The chart, calendar and HUD frames are drawn with plain WPF elements; no chart library
- Glow effects are limited to two static elements to keep list rendering fast
- Saves are serialized on the UI thread and written atomically (temp file + move)

## License

[MIT](LICENSE) © 2026 SAMET KAŞMER
