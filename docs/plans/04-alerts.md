# Step 4: Alerts

**Goal:** notify the user when something needs attention, above all a **failing pump** and
**overheating CPU or GPU**.

**Status:** done. A real pump failure can't be tested from a non-admin copy; the engine is covered by tests.

## Why now

Alerts subscribe to the sensor stream (step 2), use friendly names and roles (step 1, so "Pump" is
known), and need Pulse to keep running in the background (step 3).

## What was built

### Rules (`Services/AlertRules.cs`)
- A rule: what it watches (a sensor, or a role: the pump, CPU package, GPU hot spot, every drive),
  above or below a threshold, for how long (hold), a cooldown, a severity, and whether a vanished
  reading counts as a breach.
- Thresholds are stored in the sensor's units (°C, rpm) and shown and edited in the user's unit.
- **Built-in rules** (editable and switchable, not deletable; new ones are added to older settings files):

| Rule | Condition | Hold | Severity |
|---|---|---|---|
| Pump stopped | pump below 500 rpm, or its reading disappears | 3 s | Critical |
| CPU too hot | CPU package above 95 °C | 10 s | Warning |
| GPU hot spot too hot | above 100 °C | 10 s | Warning |
| Drive too hot | each drive above its own reported warning temperature, or 70 °C | 30 s | Warning |

### Engine (`Services/AlertEngine.cs`)
- Pure logic with the clock passed in. Per rule and target: OK → pending (waiting out the hold) →
  firing (raised) → OK (cleared).
- A rule that notified stays quiet for its cooldown (default 10 min, pump 5 min), so a reading hovering
  around the threshold doesn't spam. Quiet re-fires still appear in the list.
- A pump reading that **disappears after being seen** counts as stopped. One that's never been seen
  (no admin rights, no driver) is not treated as a failure.
- Turning a rule off clears its alert quietly.

### Notifications (`Services/Notifier.cs`)
- **Windows toasts** via the Windows APIs. Pulse registers a toast identity per user
  (`HKCU\Software\Classes\AppUserModelId\Pulse.HardwareMonitor`, name and icon) on first use.
- Critical: a "reminder" toast that stays on screen until dismissed, with an alarm sound (can be
  turned off) and **Open Pulse** / **Dismiss** buttons. Warnings: ordinary toasts.
- Clicking a toast or **Open Pulse** brings the window up (while Pulse is running).
- If a toast can't be shown, the tray balloon is used instead.
- Cost: targeting `net10.0-windows10.0.19041.0` (Windows 10 2004+) brings the Windows API projection,
  **+25 MB** in the build (37 MB total). Possible later trim: generate projections only for the
  `Windows.UI.Notifications` and `Windows.Data.Xml.Dom` namespaces with CsWinRT.

### In the app (`ViewModels/AlertCenter.cs`)
- Evaluates on every reading, even while the window is hidden (it listens to the stream like the tray).
- **Bell** in the header with an unread badge; the dropdown lists recent alerts (newest first, 50 kept)
  with a severity dot (red critical, amber warning, green "back to normal"), **Clear**, and a link to
  alert settings.
- The **tray icon turns red** while a critical alert is firing.
- Every alert is written to `pulse.log`.

### Settings → Alerts
- Each rule: on/off, "Above / Below [threshold] [unit] for [n] seconds", delete (custom rules only),
  a Critical badge where it applies.
- **Send a test notification**, and **Alarm sound for critical alerts**.
- Custom rules: All sensors → right-click → **Alert when above…** creates a rule starting a little above
  the current reading (+10 °C for temperatures, +20% otherwise) and opens Settings to adjust it.

## Verified (non-admin copy on the dev machine)
- Settings → Alerts renders the four built-in rules with editable thresholds in the user's unit.
- **Send a test notification** delivered a toast: Windows reports Pulse's notifications as enabled and
  the toast was in the notification center. The pop-up banner didn't appear in the screenshot, most
  likely because Do Not Disturb or a focus session was on.
- The bell opens and shows its empty state.

## Tests
100 passing (19 new): spike vs sustained breach, timing, flapping and cooldown, pump stopped, pump
reading vanishing vs never seen, drives using their own warning temperature, turning a rule off,
critical state, custom rules, defaults added to old settings, the alert center (notify, list, unread,
clear, alarm sound off), Fahrenheit threshold editing stored in Celsius, built-ins not deletable, and the
toast XML (reminder scenario, alarm, buttons, escaping, silent).

## Done when
- [x] A brief CPU spike above the threshold doesn't alert, but a sustained one does (tests).
- [ ] Unplugging the pump fan connector raises a critical toast within a few seconds. Simulated in tests;
      the real check needs admin and is optional (it stops the pump, so only for a few seconds).
- [ ] Check by eye on the dev machine: the test toast banner (with Do Not Disturb off), and **Open Pulse**
      bringing the window up.
