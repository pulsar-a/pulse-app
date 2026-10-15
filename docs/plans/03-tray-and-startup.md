# Step 3: Tray icon and start with Windows

**Goal:** Pulse becomes an always-available background app: a live temperature in the tray, close to
tray, and automatic start at logon without an admin prompt.

## Why now

It needs the app icon and Settings page (step 1), the idle mode (step 2) so a hidden Pulse is cheap,
and the single-instance guard (step 0). Alerts (step 4) need Pulse running in the background, which
this step provides.

## Design

### Tray icon
- Library: evaluate `WPF-UI.Tray` (same family as the UI library), `H.NotifyIcon.Wpf`, or WinForms
  `NotifyIcon` via `UseWindowsForms`. Pick the one that supports dynamic icons and a WPF context menu
  without pulling in much. Record the decision in this file.
- **Live number icon:** draw the chosen value (default: CPU package temperature) as text onto a
  16/32 px bitmap each update, colored by the heat scale, and swap the tray icon. The old Open Hardware
  Monitor did exactly this in `G:\code\openhardwaremonitor\GUI\SensorNotifyIcon.cs`, which is useful
  reference (MPL-2.0; if code is copied rather than re-implemented, keep its license header).
- **Tooltip:** CPU temperature and load, GPU temperature and load, pump speed, system power.
- **Menu:** Open Pulse, a pick list for which value the icon shows (any sensor through the step 1
  catalog), Start with Windows (toggle), Exit.
- Left-click toggles the window.

### Window behaviour
- Settings → Startup and tray:
  - Close button: "Minimize to tray" (default) or "Exit".
  - Start minimized to tray.
  - Show tray icon.
- First time the window is closed to the tray, show a one-time notification ("Pulse is still running
  in the tray") so it doesn't look like it quit.

### Start with Windows without UAC
- A normal Run-key autostart triggers the admin prompt on every logon because of the manifest.
  Instead, register a **scheduled task**: trigger "At logon" for the current user, "Run with highest
  privileges", action `Pulse.exe --tray`.
- Use the Task Scheduler COM API (or the `TaskScheduler` NuGet package). The old app's
  `GUI\StartupManager.cs` and `GUI\TaskScheduler.cs` show the approach.
- Creating the task requires admin; Pulse already runs elevated. If it isn't, the toggle explains
  why it's disabled.
- The task should allow running on battery and have no time limit (the defaults stop it after 3 days).
- `--tray` starts minimized to the tray.

## Tasks
- [ ] Choose a tray library; implement the dynamic number icon.
- [ ] Tray menu and tooltip; left-click toggles the window.
- [ ] Close-to-tray and start-minimized settings; first-close notification.
- [ ] Scheduled-task autostart with toggle in Settings and the tray menu; `--tray` argument.
- [ ] Remove the task on uninstall or if the toggle is turned off; handle a moved exe (re-point the task).
- [ ] Tests: settings round-trip; command-line parsing.

## Done when
- After a reboot, Pulse appears in the tray with no UAC prompt and shows the CPU temperature.
- Closing the window keeps Pulse in the tray using next to no CPU; clicking the icon restores it instantly.
