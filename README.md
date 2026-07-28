# TaskbarMonitor

A system-monitor strip that docks into the Windows 11 taskbar, just left of the tray:

```
↓   1.2 MB/s  ↑  88.0 KB/s │ CPU  14%  46°C
```

It has no background of its own. The text is drawn directly onto the taskbar's acrylic in your
Windows accent color, so it reads as part of the taskbar rather than as a window sitting on it.
One strip is docked per taskbar, so multi-monitor setups get one on each.

Every field is fixed-width, so the numbers never make the strip jitter as they change — that is
where the padding above comes from. Network and CPU are on by default; RAM and GPU are one
checkbox away in Settings. The default set is deliberately narrow, because all four fields is
about 640 px and reaches back into the centred taskbar buttons on a 1920-wide screen.

**Right-click the strip** for the menu.

## Install

See **[docs/INSTALL.md](docs/INSTALL.md)** for the full guide — requirements, updating and
troubleshooting. The short version, from an elevated PowerShell at the repository root:

```powershell
dotnet publish src\TaskbarMonitor -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
.\deploy\Install-Task.ps1 -StartNow
```

That publishes a self-contained binary and registers a scheduled task that starts it at logon.
If it is already running, `Stop-Process -Name TaskbarMonitor -Force` first — the running exe is
locked and the publish will otherwise fail.
CPU temperature additionally needs the [PawnIO](https://pawnio.eu/) driver; without it that one
field shows `--°C` and everything else works.

## What it shows

| Field | Source |
|---|---|
| Network ↓/↑ | 64-bit interface counters (`GetIfEntry2`) on the default-route adapter |
| CPU % | PDH `% Processor Utility` — the same counter Task Manager uses |
| CPU °C | LibreHardwareMonitor via the PawnIO driver (needs elevation) |
| RAM % | Physical memory in use (`GlobalMemoryStatusEx`) |
| GPU % / °C | LibreHardwareMonitor, falling back to `nvidia-smi` |

Network rates come from the adapter that actually carries default-route traffic, which means
WSL2 and Docker `vEthernet` adapters are excluded rather than double-counted.

## Configuration

Right-click the strip → **Settings…** for a settings window with live preview: every change shows
on the strip as you make it, **Save** writes the file and leaves the window open, and **Close**
discards anything unsaved (it asks first). The title shows `*` while there are unsaved changes. The
knobs most people want are there — which metrics to show, `theme` (`auto`/`light`/`dark`),
`textColorSource` (`accent` or `theme`), `backgroundAlpha` (`0` is fully transparent; raise it for
a translucent pill) and `textShadow`.

`publish\settings.json` sits next to the exe, is created with defaults on first run, and
hot-reloads when saved, so hand-editing still works. *Advanced → Open settings file* opens it in
Notepad — it is also the only way to reach `pollIntervalMs`, `sensorIntervalMs` and `logging`,
which the window deliberately does not expose. Full table in
[docs/INSTALL.md § Configuration](docs/INSTALL.md#configuration).

Left-clicks are swallowed so the strip never steals focus from the taskbar; the menu is
right-click only.

## Building from source

.NET 8 SDK, then:

```powershell
dotnet build TaskbarMonitor.sln
dotnet test TaskbarMonitor.sln
```

Never enable trimming — LibreHardwareMonitor resolves sensors by reflection.

`tools\NetValidation` is a harness for checking network-rate accuracy against a real download.
Note that its loopback mode is inconclusive by design: the Windows TCP loopback fast path
bypasses the interface counters the app reads.

## Project layout

| Path | Contents |
|---|---|
| `src\TaskbarMonitor\Positioning\` | Taskbar discovery, strip placement, the redock/topmost watchdog |
| `src\TaskbarMonitor\Sensors\` | Sampling threads; `Sensors\Network\` is the accuracy-critical part |
| `src\TaskbarMonitor\Rendering\` | Layered-window surface, text rendering, accent/theme colors |
| `src\TaskbarMonitor\Formatting\` | Fixed-width formatters, so the strip never changes width |
| `src\TaskbarMonitor\Settings\` | The settings model, its JSON store, and the editing/validation rules |
| `src\TaskbarMonitor\UI\` | The settings window |
| `deploy\` | Scheduled-task install/uninstall scripts |
| `tests\` | xunit tests |

`CLAUDE.md` holds the architecture notes and a list of hard-won platform gotchas worth reading
before changing the rendering or positioning code.
