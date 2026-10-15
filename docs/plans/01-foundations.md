# Step 1: Foundations

**Goal:** the shared building blocks later steps plug into: a Settings page, unit preferences,
sensor renaming, theme tokens and an app icon.

**Status:** done, except the manual checks at the bottom.

## Why now

- Steps 2, 3, 4, 5, 8 and 9 each add options. With a Settings page and a settings model in place,
  each of them only adds a section.
- Steps 4, 6, 7 and 12 show sensor names. Because renaming exists first, they use the friendly name
  from day one instead of being retrofitted.
- The views had 41 hard-coded colors. With named theme resources, the light theme in step 9 is a
  matter of adding a second palette.
- Step 3's tray icon needs the app icon.

## What was built

### Settings model (`Services/SettingsService.cs`)
- `Settings` schema v2: `SchemaVersion`, `Units`, `Sensors` (`Names`, `Roles`).
  Later steps add their own sections next to these.
- `SettingsService.Update(change)` applies a change, raises `Changed` right away, and saves
  500 ms later. Saves are atomic: write `settings.json.tmp`, then rename it over the old file.
  `Flush()` runs on exit.
- Migration: v1's `PumpSensorId` moves to `Sensors.Roles["Pump"]`. A corrupt file falls back to
  defaults and is logged.

### Sensor catalog (`Services/SensorCatalog.cs`)
- The single source of sensor names and roles. `DisplayName(id, original)` returns the custom name
  or the original; `Rename`, `ResetName`, `SetRole`, `ClearRole` and `ForgetRole` change them and raise
  `NamesChanged`.
- Roles: missing means "never chosen" (Pulse may auto-pick), an empty id means "the user cleared it".
  The pump is the first role.
- The dashboard (fans, cooling card, pump picker, core tooltips) and All sensors use display names.
  Pump and coolant detection matches the original name or the user's name, so renaming a header
  "… pump" also counts.

### Units (`Services/SensorFormat.cs`)
- `SensorFormat.TemperatureUnit` (set from settings) converts every temperature: `Format`,
  `Degrees("72°")`, `WholeTemperature("72 °C")`, `TemperatureDelta` (scaled without the 32° offset).
- View models no longer write their own `"°"` strings. Readings stay in °C internally, so gauge
  scales and heat colors are unchanged.
- A unit change shows up with the next reading (within a second).

### Rename UI (All sensors)
- Double-click a sensor name, or right-click → **Rename…**: inline box, Enter saves, Esc cancels,
  clicking away saves. Right-click → **Reset name** when renamed.
- Renamed rows show a pencil marker; the tooltip shows the original name. Search matches both names.

### Settings page (`Views/SettingsView.xaml`, `ViewModels/SettingsViewModel.cs`)
- Gear button next to Dashboard and All sensors.
- **Units:** °C / °F.
- **Sensors:** pump header (with "Detect again"), list of renamed sensors with Reset.
- **About:** version, admin and PawnIO status, log file with "Open log folder", settings file location.

### Theme tokens (`Themes/Dark.xaml`)
- Named brushes and colors: surfaces, category accents (solid and soft), status colors, brand, and
  instrument colors.
- No hex colors remain in the views. `RingGauge`, `PeakBar` and `Shimmer` got brush and color
  properties, set from tokens by implicit styles in `Themes/Styles.xaml`. `Palette` reads accents from
  the tokens (with built-in fallbacks for tests).
- The heat scale (blue → red) stays in `Palette` as code; it's a data scale, the same in any theme.

### App icon (`Resources/Pulse.ico`)
- The red heartbeat trace from the splash on a dark rounded square, drawn by `tools/IconGen`
  (`make icon`) at 16–256 px. Sizes up to 24 px use a simpler trace so it stays legible.
- Used for the exe (taskbar, Alt+Tab, Explorer), the window and the title bar.
- The generator replaces the planned SVG source: change the drawing code and run `make icon`.

### Tests
45 passing (13 new): unit conversion and deltas, the Fahrenheit dashboard keeping the dial scale,
settings migration, atomic save and change notification, corrupt-file fallback, catalog naming,
rename to blank or original, renamed fans and pump on the dashboard, search by either name, and
inline rename commit and cancel. Tests run one at a time because the unit and log path are global.

## Done when
- [x] No `#RRGGBB` or `#AARRGGBB` values remain in the XAML files.
- [x] Pulse has its own icon in the title bar (checked in a screenshot).
- [ ] Switching to °F updates every number, including gauges, min/max pills and All sensors, without
      a restart. Covered by tests; check by eye on the dev machine.
- [ ] Renaming "Fan #2" to "Pump" shows up on the dashboard, in All sensors and in search. Covered by
      tests; check the inline rename box by eye (it wasn't exercised in screenshots, to avoid writing to
      the real settings file).
- [ ] Taskbar and Alt+Tab icon: check by eye (needs a normal build and launch).
