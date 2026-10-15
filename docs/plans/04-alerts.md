# Step 4: Alerts

**Goal:** notify the user when something needs attention, above all a **failing pump** and
**overheating CPU or GPU**.

## Why now

Alerts subscribe to the sensor stream (step 2), use friendly names and roles (step 1, so "Pump" is
known), and need Pulse to keep running in the background (step 3).

## Design

### Rules
- A rule is: sensor (by id, or by role such as `Pump` or `CpuPackage`), condition (above / below),
  threshold, how long it must hold (debounce), cooldown, severity.
- **Built-in defaults** (editable, can be turned off):
  - Pump below 500 rpm for 3 s → critical.
  - CPU package above 95 °C for 10 s → warning.
  - GPU hot spot above 100 °C for 10 s → warning.
  - Any drive above its "Warning Temperature" sensor (LHM reports one for NVMe) → warning.
- Stored in `Settings.Alerts`.

### Engine
- `Services/AlertEngine.cs` subscribes to `SensorStream` on the polling thread, keeps per-rule state
  (ok → pending → firing → cooldown) and raises `AlertRaised` and `AlertCleared`.
- A missing sensor counts as "unknown", not "ok". For the pump, a vanished reading is itself worth
  a critical alert.
- Unit-aware: thresholds are stored in °C and shown in the user's unit.

### Notifications
- Windows toast notifications. Evaluate the Windows App SDK `AppNotificationManager` (works for
  unpackaged apps) against a tray balloon fallback. Toasts need an app identity, set up in this step.
- Critical alerts: the toast stays until dismissed; optional sound.
- In the app: a bell in the header with a list of recent alerts (time, sensor, value). The tray icon
  turns red while a critical alert is firing.

### Settings → Alerts
- List of rules with an on/off toggle, threshold editor and "Test notification" button.
- "Add rule" picks a sensor through the catalog.

## Tasks
- [ ] Rule model and defaults in settings.
- [ ] `AlertEngine` with debounce, cooldown and missing-sensor handling.
- [ ] Toast notifications with app identity; tray-balloon fallback.
- [ ] In-app alert list and tray red state.
- [ ] Settings → Alerts editor.
- [ ] Tests: the state machine with a fake clock and synthetic sensor streams (spike, sustained,
      flapping, sensor disappears).

## Done when
- Unplugging the pump fan connector (or simulating it in a test build) raises a critical toast
  within a few seconds.
- A brief CPU spike above the threshold doesn't alert, but a sustained one does.
