# TaskbarMonitor

User-facing docs: [README.md](README.md) and [docs/INSTALL.md](docs/INSTALL.md) (install, config
reference, troubleshooting). Keep them in sync when settings keys or the deploy flow change.

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
- `Rendering\LayeredSurface.cs` — the strip has **no background of its own**: it is a per-pixel-alpha layered window (`WS_EX_LAYERED` + `UpdateLayeredWindow`), so the taskbar acrylic shows through and the text looks painted onto it. Owns a cached top-down 32bpp `CreateDIBSection` + memory DC wrapped as a `Format32bppPArgb` `Bitmap`, reallocated only on size change.
- `Rendering\StripRenderer.cs` — GDI+ `DrawString` (never `TextRenderer`, see gotchas). Measure and draw live together so `ContentSize` and the glyphs can't disagree; the draw origin is centered off a measurement cached in `ApplyAppearance`, never re-measured per frame.
- `Rendering\ThemeWatcher.cs` — text color follows the Windows accent, contrast-corrected per theme (`EnsureReadable`). `appearance.textColorSource` = `accent` | `theme`; `textOverride` beats both.
- `UI\SettingsForm.cs` — field-by-field editor opened from the strip menu. Hand-coded, no designer/`.resx` (matching `OverlayWindow`). Live preview via `ISettingsHost.ApplyLive`; disk write only on Save; Cancel/Esc/X re-apply the as-of-open snapshot. Exposes everything **except** `pollIntervalMs`, `sensorIntervalMs` and `logging` — those are file-only.
- `Settings\` — `SettingsStore` (the only `JsonSerializer` calls: `Load`/`Parse`/`Serialize`/`Clone`/`SaveAndCapture`), `SettingsDraft` (draft + snapshot + dirty tracking), `SettingsFieldRules` (validation/normalization), `ISettingsHost` (the seam `PositionController` implements, so the form never sees strips or timers). The last three are Form-free precisely so they can be unit-tested — no test in this repo constructs a `Form`.

## Hard-won gotchas (do not re-learn these)

- **Z-order**: the taskbar is also TOPMOST and can raise itself above the strip *within* the topmost band without touching the strip's `WS_EX_TOPMOST` bit. The watchdog must re-assert `HWND_TOPMOST` every tick unconditionally (`SWP_NOMOVE|NOSIZE|NOACTIVATE` when not drifted) — a style-bit check is not sufficient.
- **CPU %**: use PDH `\Processor Information(_Total)\% Processor Utility` via `PdhAddEnglishCounterW` (Task Manager's counter). `% Processor Time` diverges badly on the boosting/parking 14900KF.
- **CPU temp driver**: LHM 0.9.6 uses the signed PawnIO driver (already installed on this machine). Never ship WinRing0 — Defender flags it as Trojan:Win32/Vigorf.A.
- **TaskbarCreated**: UIPI blocks the broadcast to elevated processes — `ChangeWindowMessageFilterEx` is required or explorer restarts orphan the strip.
- **Elevation**: manifest stays `asInvoker`; elevation comes from the scheduled task's RunLevel. Unelevated runs work, CPU temp shows `--°C`.
- **Layered-window hit-testing**: a per-pixel-alpha window is click-through wherever alpha is exactly 0, so a fully transparent strip would only receive the right-click menu on the glyphs themselves. The whole rect is washed at **alpha 1** (`StripRenderer.MinBackgroundAlpha`) — imperceptible, and hit-testable. Verify with `WindowFromPoint` over a grid, not by eye.
- **`CompositingQuality.HighQuality` destroys that wash**: its gamma-corrected blending rounds an alpha-1 `SourceOver` fill back to 0 and the strip silently stops taking clicks. The wash is written with `CompositingMode.SourceCopy` + `Graphics.Clear` (exact bytes, also erases the previous frame) under `CompositingQuality.Default`.
- **Never draw the strip with `TextRenderer`/GDI or ClearType**: GDI text APIs don't write the alpha byte, so glyphs come out invisible or black-boxed on a layered surface, and subpixel AA fringes. Use GDI+ `DrawString` with `TextRenderingHint.AntiAliasGridFit` and `StringFormat.GenericTypographic`.
- **Never set `Opacity`, `TransparencyKey` or `AllowTransparency`** on `OverlayWindow`: WinForms would drive the layer via `SetLayeredWindowAttributes`, which cannot be mixed with `UpdateLayeredWindow` — the strip goes blank. `WS_EX_LAYERED` is added in `CreateParams`.
- **`UpdateLayeredWindow` must be sized from the live window rect, not `ContentSize`** — `TaskbarLocator.ComputeStripRect` only uses `ContentSize.Width` and stretches the strip to the full taskbar height. Render is driven from `OnResize`, and `pptDst` is always NULL so positioning stays with the watchdog's `SetWindowPos`.
- **`FileSystemWatcher` and `SystemEvents` fire on background threads**, and a `System.Windows.Forms.Timer` started from one *never ticks* (its `WM_TIMER` has no message loop to pump). `PositionController` keeps a handle-bearing `Control _sync` created on the UI thread; the watcher gets it as `SynchronizingObject` and `ReapplyAppearance` marshals through it. Settings hot-reload was silently dead before this.
- **The settings window must be modeless and ownerless.** `SyncStrips` disposes strips whose taskbar hwnd disappears, so `Show(ownerStrip)` would destroy the owner out from under it on any explorer restart. Showing it must also be posted through `_sync.BeginInvoke` — inside the `ContextMenuStrip` click handler the dropdown still holds capture, and an activatable window loses the activation race and opens behind the taskbar.
- **Live preview must stay debounced (150 ms).** `ApplyLive` reassigns `Timer.Interval`, which is `KillTimer`+`SetTimer` — dragging the `watchdogIntervalMs` spinner un-debounced means the watchdog never ticks for the length of the drag. It also runs `SensorEngine.ApplySettings`, which re-registers `NotifyRouteChange2` and re-baselines the sampler whenever `adapterOverride` changes; that is why the adapter combo commits on `SelectionChangeCommitted`/`Leave` and never on `TextChanged`.
- **Self-write suppression is content-based, not time-based.** `TrySave` keeps the exact text `SaveAndCapture` wrote and the watcher-driven reload skips only when the file matches it byte-for-byte; `_lastWrittenJson` is cleared on every load and after any failed write. A tick window would be wrong in both directions — the 500 ms debounce restarts on every `Changed` event, so the reload's timing is not ours to predict.
- **`NumericUpDown.Value` throws outside `[Minimum, Maximum]`** and `settings.json` is documented as hand-editable, so every seed goes through `SettingsForm.Seed`. `SettingsForm` also uses `AutoScaleMode.Font` with an explicit `AutoScaleDimensions`, deliberately unlike `OverlayWindow`'s `AutoScaleMode.None`.
- **`AccentPalette`** (`HKCU\...\Explorer\Accent`) is 8 four-byte **RGBA** entries — *not* BGRA. Indices 0-6 are a lightest-to-darkest ramp (index 3 is the base, matching `AccentColorMenu` = `0xAABBGGRR`); **index 7 is an unrelated complement color** and must never be used.
- Cloudflare's `speed.cloudflare.com/__down` 403s non-browser TLS fingerprints; the harness defaults to `https://proof.ovh.net/files/100Mb.dat`.
