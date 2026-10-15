# Step 1: Foundations

**Goal:** the shared building blocks later steps plug into: a Settings page, unit preferences,
sensor renaming, theme tokens and an app icon.

## Why now

- Steps 2, 3, 4, 5, 8 and 9 each add options. With a Settings page and a settings model in place,
  each of them only adds a section.
- Steps 4, 6, 7 and 12 show sensor names. If renaming exists first, they all use the friendly name
  from day one instead of being retrofitted.
- The views have about 40 hard-coded colors (`#14FFFFFF` and similar in `DashboardView.xaml`,
  `MainWindow.xaml`, `SensorsView.xaml`, `Styles.xaml`). Moving them to named resources now makes
  the light theme in step 9 a matter of adding a second palette.
- Step 3's tray icon needs the app icon.

## Design

### Settings model
- Grow `Settings` (`Services/SettingsService.cs`) into a versioned record:
  `SchemaVersion`, `Units`, `Sensors` (custom names, see below), `PumpSensorId`, plus room for later
  sections (`Startup`, `Alerts`, `History`, `Dashboard`, `Appearance`).
- `SettingsService` raises `Changed` after `Save()`, so view models can react without restarting.
- Saving is debounced (about 500 ms) and atomic: write to `settings.json.tmp`, then rename it over the old file.
- Unknown or invalid values fall back to defaults and are logged, never crash.

### Settings page
- A third tab next to Dashboard and All sensors, with a gear icon.
- `ViewModels/SettingsViewModel.cs` with one section view model per area, starting with:
  - **Units:** temperature °C/°F.
  - **Sensors:** list of renamed sensors, with reset.
  - **About:** version, log file location ("Open log folder" button), PawnIO status.

### Units
- `SensorFormat` takes a `UnitPreferences` object instead of hard-coding °C.
- Fahrenheit converts values, min, max and gauge scales. The CPU and GPU dials keep a 0–100 °C
  scale internally, so the arc still means the same thing.
- One place (`SensorFormat`) does the formatting; view models never write their own `"°"` strings.
  This means replacing the ad-hoc format lambdas in `DashboardViewModel` (for example
  `SetGauge("temperature", t, 100, v => $"{v:0}°")`).

### Sensor identity and custom names
- New `Services/SensorCatalog.cs`: given an LHM sensor identifier, it returns a `SensorInfo` with
  display name (custom or original), original name, type, unit and owning hardware.
- Custom names live in `Settings.Sensors.Names` as `identifier → name`.
- The pump setting becomes a special case of naming: it's stored as a role (`Pump`) on a sensor id,
  and the cooling card reads roles from the catalog.
- **All sensors page:** right-click a row and choose "Rename…" (an inline text box) or "Reset name".
  Renamed sensors show a small pencil marker and the original name in a tooltip.
- The dashboard, All sensors page and every later feature get names through the catalog.

### Theme tokens
- Add `Themes/Dark.xaml` with named brushes and colors: `CardBackground`, `CardBorder`, `Track`,
  `SubtleFill`, `Divider`, accent colors `Accent.Cpu`, `Accent.Gpu` and so on, and the heat scale.
- Replace every hex value in XAML with `{DynamicResource …}`.
- `Controls/Palette.cs` reads accents from resources, so the controls (`RingGauge`, `PeakBar`,
  `Sparkline`, `Shimmer`) follow the theme.

### App icon
- Design the Pulse mark: the red heartbeat trace from the splash, inside a rounded square.
- Export it as an `.ico` with 16, 20, 24, 32, 48 and 256 px sizes (`Resources/Pulse.ico`), set
  `<ApplicationIcon>` in `Pulse.csproj`, and set the window and title-bar icon.
- Keep an SVG source in `docs/assets/`.

## Tasks
- [ ] Settings model v2, migration from the current file, `Changed` event, atomic save.
- [ ] Settings page with Units, Sensors and About sections.
- [ ] Fahrenheit support through `SensorFormat`; remove ad-hoc unit strings.
- [ ] `SensorCatalog` and custom names; rename from All sensors; migrate `PumpSensorId` to a role.
- [ ] Theme token dictionary; replace all hex values in XAML; `Palette` reads resources.
- [ ] App icon in the taskbar, title bar and Alt+Tab.
- [ ] Tests: unit conversion, catalog naming and fallback, settings migration from the step 0 format.

## Done when
- Switching to °F updates every number, including gauges, min/max pills and All sensors, without a restart.
- Renaming "Fan #2" to "Pump" shows up on the dashboard, in All sensors and in search.
- No `#RRGGBB` or `#AARRGGBB` values remain in the XAML files.
- Pulse has its own icon in the taskbar and title bar.
