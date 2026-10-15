# Step 3: Tray icon and start with Windows

**Goal:** Pulse becomes an always-available background app: a live temperature in the tray, close to
tray, and automatic start at logon without an admin prompt.

**Status:** done, except checks that need admin rights (listed at the bottom).

## Why now

It needs the app icon and Settings page (step 1), the idle mode (step 2) so a hidden Pulse is cheap,
and the single-instance guard (step 0). Alerts (step 4) need Pulse running in the background, which
this step provides.

## What was built

### Tray library
**`H.NotifyIcon.Wpf` 2.4.1.** Compared with `WPF-UI.Tray` it supports updating the icon at runtime,
native notifications (`ShowNotification`) and click commands, and its menu is a normal WPF
`ContextMenu`, so it picks up the WPF-UI dark theme. Two things to know:
- `ForceCreate()` turns on Windows Efficiency Mode by default, which throttles the process. Pulse calls
  `ForceCreate(enablesEfficiencyMode: false)`.
- Its `ToIcon()` doesn't accept rendered bitmaps (`RenderTargetBitmap`), so `TrayIcon.ToIcon` converts
  the pixels to a GDI icon itself.

### Tray icon (`Views/TrayIcon.cs`, `Services/TrayText.cs`)
- **Live number:** drawn at the system's small-icon size (16 px at 100% scaling, 24 at 150%, 32 at 200%)
  as bold text on a dark rounded square. Temperatures and loads use the heat colors; other readings are white.
  It's only redrawn when the text or color changes. Two or three characters: "72", "146", "2.5k", "5.7".
- **What it shows:** automatic by default (CPU package temperature, or CPU load without the driver), or
  any choice from the tray menu, Settings, or right-click → **Show in tray icon** on the All sensors page.
- **Tooltip:** CPU temperature and load, GPU temperature and load, pump speed, system power
  (kept within Windows' 127-character limit).
- **Menu:** Open Pulse · Tray icon shows ▸ · Start with Windows · Exit Pulse.
- **Left-click** toggles the window.
- It listens to the sensor stream directly, so it keeps updating while the window is hidden.

### Window behavior (`App.xaml.cs`)
- Closing the window hides it to the tray (default), or exits if the user chose that or turned the
  tray icon off. The log records which happened.
- The first close to the tray shows a notification: "Pulse is still running".
- `--tray` (used by the startup task) or the "Start in the tray" setting starts Pulse hidden.
  Launching Pulse again brings the window back (single instance, step 0).
- Exit removes the tray icon first, so no ghost icon is left behind.

### Start with Windows (`Services/StartupTask.cs`)
- A scheduled task named **Pulse**: "At log on" of the current user (5 s delay), run with highest
  privileges, action `Pulse.exe --tray`. So Pulse starts elevated, with no admin prompt.
- Overrides Task Scheduler defaults that would hurt a monitor: no 3-day time limit, runs on battery,
  normal priority (5) instead of below-normal (7).
- Created with `schtasks /Create /XML` (no extra dependency). The task is the source of truth; settings
  don't store it.
- If Pulse has been moved since the task was made, startup re-points the task at the new location.
- Changing it needs admin rights; when Pulse isn't elevated, the toggle is disabled and says why.

### Settings → Startup and tray
Start with Windows · Show icon in the notification area · Tray icon shows · When I close the window
(keep running in the tray / exit) · Start in the tray.

## Verified (non-admin copy on the dev machine)
- The tray icon appears and renders the live number (seen in the overflow flyout). The tooltip read
  "Pulse | CPU 10% | GPU 37 °C · 34% | Power 264 W".
- Closing with the title bar button and with a plain window close both hide to the tray. A second
  launch exits with code 2 and brings the window back; live updates resume.
- `--tray` starts with no window and the tray icon present.

**Windows 11 note:** new tray icons go into the hidden overflow (the ^ arrow). Drag Pulse's icon onto
the taskbar, or turn it on in Settings → Personalization → Taskbar → Other system tray icons.
There's no supported way for an app to do this itself.

## Tests
81 passing (21 new): argument parsing, the task definition (logon trigger, highest privileges,
`--tray`, the overridden defaults, special characters in the path), tray number formatting
(units, k suffix, GHz), automatic choice and its load fallback, a chosen sensor and a missing one,
the tooltip text and limit, the menu choices, and startup settings defaults and migration.

## Done when
- [ ] After a reboot, Pulse appears in the tray with no UAC prompt and shows the CPU temperature.
      Needs admin: turn on Start with Windows, then sign out and back in.
- [x] Closing the window keeps Pulse in the tray, idling; clicking the icon or launching again restores it.
- [ ] Check by eye: tray menu, the "still running" notification, and Show in tray icon from All sensors.

## Later fix: keeping the icon next to the clock
Windows 11 hides every new tray icon under the ^ arrow, and it tells icons apart by the program's path.
Each build folder (the output path changed with the target framework), a published copy, or a test copy
counted as a new app, so "always show" never carried over. There's no API for this. Windows keeps the
choice in `HKCU\Control Panel\NotifyIconSettings`: one key per icon, with `ExecutablePath` and
`IsPromoted`, the same value the taskbar settings page writes.

- New setting, Settings → Startup and tray → **Always show it next to the clock** (on by default).
- At every start, and when the setting changes, Pulse finds Windows' record for its own exe and sets
  `IsPromoted` (`Services/TrayPromotion.cs`). Windows writes that record a moment after the icon first
  appears, so Pulse tries again after 1, 5 and 30 seconds. After a change it re-adds its icon so the
  taskbar picks it up.
- Turning the setting off hands the icon back to Windows (overflow). Hiding it in Windows' own settings
  while the Pulse setting is on is undone at the next start; the setting says so.
- Windows 10 keeps these choices in an opaque blob; there Pulse does nothing.
- Tests run against a scratch key under `HKCU\Software\PulseTests`: a new build's record gets
  promoted, other apps are untouched, turning it off, before Windows has a record, and no Windows 11
  settings.
- Not yet confirmed on the real taskbar: whether the icon moves out of the overflow straight away or
  only once Pulse restarts.
