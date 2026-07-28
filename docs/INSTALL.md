# Installing TaskbarMonitor

TaskbarMonitor runs as a per-user scheduled task that starts at logon. There is no MSI or
setup wizard — you publish a self-contained binary, then register the task with the script in
`deploy\`.

## Requirements

| | |
|---|---|
| OS | Windows 11 (Win10 may work; the taskbar lookup targets the Win11 XAML taskbar) |
| Build-time | [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) |
| Run-time | None — the published build is self-contained |
| CPU temperature | [PawnIO](https://pawnio.eu/) driver, plus an elevated run |

PawnIO is optional. Without it everything else works and the CPU temperature field shows `--°C`.
It is not bundled, and **WinRing0 must never be substituted** — Defender flags it as
`Trojan:Win32/Vigorf.A`.

## 1. Build

From the repository root:

```powershell
dotnet publish src\TaskbarMonitor -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

This produces `publish\TaskbarMonitor.exe`. Do not add `-p:PublishTrimmed=true` —
LibreHardwareMonitor resolves sensors by reflection and trimming breaks it.

> If TaskbarMonitor is already installed and running, **stop it first** or the publish fails with
> `GenerateBundle ... The process cannot access the file` — the running exe is locked. See
> [Updating to a new build](#updating-to-a-new-build).

If `dotnet` is not on PATH in a fresh shell:

```powershell
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')
```

## 2. Register the scheduled task

From an **elevated** PowerShell:

```powershell
.\deploy\Install-Task.ps1 -StartNow
```

The task must be registered elevated because it uses `RunLevel Highest`. That is also what gives
the app the privileges it needs for CPU temperature — the manifest itself stays `asInvoker`, so
launching the exe directly just runs unelevated.

`Install-Task.ps1` registers a task named `TaskbarMonitor` that:

- triggers **at logon** of the current user, with a **15 s delay** so explorer, the taskbar and the
  network stack exist first,
- runs with **highest privileges** (no UAC prompt at logon),
- has no execution time limit, starts on battery, and ignores a second instance.

It also checks for the PawnIO service and warns if it is missing or stopped.

Useful parameters:

| Parameter | Purpose |
|---|---|
| `-ExePath <path>` | Register a binary somewhere other than `publish\TaskbarMonitor.exe` |
| `-StartNow` | Start the task immediately instead of waiting for the next logon |

## 3. Confirm it is running

The strip should appear on each taskbar, just left of the tray:

```
↓ 1.2 MB/s ↑ 88 KB/s │ CPU 14% 46°C │ RAM 38% │ GPU 12% 51°C
```

It has no background of its own — the text is drawn straight onto the taskbar's acrylic, in your
Windows accent color. **Right-click the strip** for the menu (settings, open settings file, reload
settings, exit). If the menu does not open, see [Troubleshooting](#troubleshooting).

To check from a shell:

```powershell
Get-Process TaskbarMonitor
Get-Content publish\monitor.log -Tail 20
```

A healthy start logs `PositionController up: N strip(s)`.

## Updating to a new build

The running process holds a lock on the exe, so stop it before republishing:

```powershell
Stop-Process -Name TaskbarMonitor -Force
dotnet publish src\TaskbarMonitor -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
Start-ScheduledTask -TaskName TaskbarMonitor
```

`Stop-ScheduledTask` alone does not reliably kill it — use `Stop-Process`.

If you changed defaults in `AppSettings.cs`, delete `publish\settings.json` first so it regenerates
with the new values; an existing file keeps its old keys and only picks up genuinely new ones.

## Uninstalling

From an elevated PowerShell:

```powershell
.\deploy\Uninstall-Task.ps1
```

This stops the process and unregisters the task. Delete the `publish` folder to remove the binary,
`settings.json` and `monitor.log`.

## Configuration

Settings live in `publish\settings.json`, next to the exe. The file is created with defaults on
first run and **hot-reloads** — save it and the strip re-renders within about a second (500 ms
debounce).

### The settings window

Right-click the strip → **Settings…**. Every field is a normal control, and **every change previews
live on the strip immediately** — nothing is written to `settings.json` until you press **Save**.
**Cancel**, `Esc` and the X button all revert the strip to how it looked when the window opened.
*Reset to defaults* restores the groups the window shows, but does not save on its own.

A few things worth knowing:

- **`pollIntervalMs`, `sensorIntervalMs` and the whole `logging` section are file-only** — the
  window does not show them, and *Reset to defaults* leaves them untouched. Use *Open settings file*
  for those. (`logging` in particular only takes effect at startup.)
- **External edits are ignored while the window is open**, since its draft is already live on the
  strip. Close it, or use *Reload settings*, to pick them up. If the file changed behind its back,
  Save asks before overwriting.
- **Saving normalizes.** Values outside the window's ranges are clamped, and an unrecognised `theme`
  or `textColorSource` becomes `auto` / `accent` — which is already how the renderer treated them.

`settings.json` stays authoritative, and hand-editing still works exactly as before.

### Appearance keys

These are the ones most people want; all of them are in the window's *Appearance* group.

| Key | Default | Meaning |
|---|---|---|
| `fontName` | `Consolas` | Must be monospace, or the strip will jitter as values change |
| `fontSizePt` | `12` | Scaled by the monitor's DPI |
| `theme` | `auto` | `auto` follows Windows, or force `light` / `dark` |
| `textColorSource` | `accent` | `accent` follows your Windows accent color; `theme` uses a plain neutral |
| `backgroundAlpha` | `0` | `0` is fully transparent. Raise it (e.g. `40`) for a translucent pill behind the text |
| `textShadow` | `true` | 1 px shadow; helps legibility over bright wallpapers |
| `backgroundOverride` | `null` | `#RRGGBB` or `#AARRGGBB` — wins over `backgroundAlpha` |
| `textOverride` | `null` | `#RRGGBB` — wins over the accent color |

Other sections cover which metrics are shown (`metrics`), placement relative to the tray
(`positioning`), fullscreen/auto-hide behaviour (`behavior`), the network adapter
(`network`) and logging (`logging`).

A corrupt `settings.json` is not overwritten — the app logs a warning and runs on defaults, so your
file is safe to hand-edit.

## Troubleshooting

**`dotnet publish` fails with `GenerateBundle` / `The process cannot access the file`.** A running
TaskbarMonitor holds a lock on `publish\TaskbarMonitor.exe`. Stop it and republish:

```powershell
Stop-Process -Name TaskbarMonitor -Force
dotnet publish src\TaskbarMonitor -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
Start-ScheduledTask -TaskName TaskbarMonitor
```

**CPU temperature shows `--°C`.** Either PawnIO is not installed, or the app is running
unelevated. Check `monitor.log` — the startup line reports both:
`Elevated=True, PawnIO=installed`. Launching `TaskbarMonitor.exe` by double-clicking runs it
unelevated; start it through the scheduled task instead.

**Right-clicking the strip does nothing.** The strip is a transparent layered window, and layered
windows are click-through wherever the alpha is exactly zero. It is washed at alpha 1 to stay
hit-testable, so this should not happen — if it does, set `backgroundAlpha` to a visible value to
confirm where the strip actually is, and file it as a bug.

**I hand-edited `settings.json` and nothing happened.** The settings window was open — external
changes are deliberately ignored while it is, so it cannot be clobbered mid-edit. `monitor.log`
records this as `settings.json changed on disk; ignored while the settings window is open`. Close
the window and save again, or use *Reload settings*.

**The strip vanished after restarting explorer.** It should redock automatically within ~2 s.
If it does not, check `monitor.log` for `TaskbarCreated received`; if that line is missing, the
UIPI message filter failed and the app needs a restart.

**The strip sits on top of something it shouldn't.** The taskbar is also topmost and can raise
itself above the strip, so the watchdog re-asserts topmost every 2 s. That interval is
`behavior.watchdogIntervalMs`.

**Nothing appears at all.** Confirm the process is alive (`Get-Process TaskbarMonitor`) and read
`publish\monitor.log`. To see the sensor values without any windows involved:

```powershell
Start-Process .\publish\TaskbarMonitor.exe -ArgumentList '--console 10' -RedirectStandardOutput out.txt -Wait
```

The app is a `WinExe`, so plain `> file` redirection captures nothing — `Start-Process
-RedirectStandardOutput` is required.
