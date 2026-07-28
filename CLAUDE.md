# TaskbarMonitor

Windows 11 taskbar system-monitor strip: `↓ rate ↑ rate │ CPU % °C │ RAM % │ GPU % °C`, one borderless always-on-top window docked left of the tray on each taskbar. C#/.NET 8 WinForms, single package dependency (LibreHardwareMonitorLib, pinned `[0.9.6]`).

## Build / test / publish

Fresh shells may not have `dotnet` on PATH — prepend it first:

```powershell
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')
dotnet build TaskbarMonitor.sln
dotnet test TaskbarMonitor.sln
dotnet publish src\TaskbarMonitor -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

- Never enable trimming: LHM uses reflection.
- The app is a WinExe, so `> file` redirection captures nothing from `--console` mode; use `Start-Process -RedirectStandardOutput -Wait`.
- Deployed as scheduled task "TaskbarMonitor" (AtLogOn, 15 s delay, RunLevel Highest). After republishing: `Stop-ScheduledTask TaskbarMonitor; Start-ScheduledTask TaskbarMonitor`. Install/uninstall scripts in `deploy\`.
- `publish\settings.json` hot-reloads (FileSystemWatcher, 500 ms debounce). Delete it after changing defaults in `AppSettings.cs` so it regenerates.
- Validation harness: `tools\NetValidation` (`--seconds --bytes --mode internet|loopback --adapter --url`). Internet mode is the real assertion; loopback is INCONCLUSIVE by design (Windows TCP loopback fast path bypasses interface MIB counters). Keep download defaults small — user is on a bandwidth-constrained connection.

## Architecture

- `Program.cs` — single-instance mutex, `--console [seconds]` diagnostic mode, then `SensorEngine` + `PositionController` (an `ApplicationContext`).
- `Positioning\TaskbarLocator.cs` — finds `Shell_TrayWnd`/`TrayNotifyWnd` + all `Shell_SecondaryTrayWnd`; AppBar and work-area fallbacks. Overlay windows only — never `SetParent` into the taskbar (unsupported on the Win11 XAML taskbar).
- `Positioning\PositionController.cs` — owns one `OverlayWindow` per taskbar hwnd; 1 s UI timer, 2 s watchdog, 2 s redock delay after explorer restart/display change.
- `Sensors\SensorEngine.cs` — background thread, two cadences: fast lane 1 s (network/CPU%/RAM), slow lane 2.5 s (LHM temps + GPU). Publishes immutable `SensorSnapshot`. GPU falls back permanently to nvidia-smi after 5 null LHM reads.
- `Sensors\Network\` — the accuracy-critical part. `DefaultRouteResolver` (GetBestInterfaceEx toward 8.8.8.8 — inherently excludes WSL2/Docker vEthernet adapters), `IfEntry2CounterSource` (GetIfEntry2 64-bit octets), `NetRateSampler` (delta ÷ measured elapsed; discard + re-baseline on negative delta, ifIndex change, or gap > 5 s).
- `Rendering\` + `Formatting\UnitFormatter.cs` — Consolas monospace, fixed-width fields (GDI has no OpenType tnum). Every formatter returns constant-length strings; tests enforce it.

## Hard-won gotchas (do not re-learn these)

- **Z-order**: the taskbar is also TOPMOST and can raise itself above the strip *within* the topmost band without touching the strip's `WS_EX_TOPMOST` bit. The watchdog must re-assert `HWND_TOPMOST` every tick unconditionally (`SWP_NOMOVE|NOSIZE|NOACTIVATE` when not drifted) — a style-bit check is not sufficient.
- **CPU %**: use PDH `\Processor Information(_Total)\% Processor Utility` via `PdhAddEnglishCounterW` (Task Manager's counter). `% Processor Time` diverges badly on the boosting/parking 14900KF.
- **CPU temp driver**: LHM 0.9.6 uses the signed PawnIO driver (already installed on this machine). Never ship WinRing0 — Defender flags it as Trojan:Win32/Vigorf.A.
- **TaskbarCreated**: UIPI blocks the broadcast to elevated processes — `ChangeWindowMessageFilterEx` is required or explorer restarts orphan the strip.
- **Elevation**: manifest stays `asInvoker`; elevation comes from the scheduled task's RunLevel. Unelevated runs work, CPU temp shows `--°C`.
- Cloudflare's `speed.cloudflare.com/__down` 403s non-browser TLS fingerprints; the harness defaults to `https://proof.ovh.net/files/100Mb.dat`.
