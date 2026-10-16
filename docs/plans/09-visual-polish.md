# Step 9: Visual polish ✅

**Goal:** the extra delight on top of a working app: animated numbers, cards that react to heat, and a
light theme that follows Windows.

## Why now

The theme tokens from step 1 make the light theme a palette exercise rather than a rewrite, and doing
polish after step 7 means it's applied once to the final tile templates, not twice.

## Design

### Rolling numbers
- A `RollingNumber` control: each digit slides vertically to its new value (an odometer effect).
  Used for the big gauge values and tile values.
- Respect Windows "Show animations" (`SystemParameters.ClientAreaAnimation`); if off, values change
  instantly. This setting applies to every animation in Pulse, including splash, transitions, peaks and gauges.

### Heat-reactive cards
- When a card's main reading passes warning or critical thresholds (shared with the step 4 alert
  defaults), its border picks up the heat color, with a slow, subtle pulse at critical.
- Must stay readable: border only, never behind the text.

### Light theme and Windows accent
- `Themes/Light.xaml` with the same token names as `Dark.xaml`.
- Settings → Appearance: Dark, Light, or follow Windows (`SystemThemeWatcher` from WPF-UI).
- Optional: use the Windows accent color for neutral UI accents (navigation pill, buttons), keeping
  the per-category colors and the red Pulse brand.
- Check contrast: text on cards at least 4.5:1 in both themes.

### GPU acceleration
- No setting needed. WPF already draws through DirectX on the graphics card; this PC reports render
  tier 2 (full hardware acceleration). A setting could only switch to software rendering, which helps
  with broken graphics drivers, not speed. Pulse's own costs are on the CPU side (reading sensors,
  layout), which earlier steps already trimmed.
- Pulse now logs the render tier at startup, to spot a machine that falls back to software.

### Small touches
- Smooth number transitions in All sensors rows (no flicker when values update).
- Hover elevation on clickable tiles.
- A refined splash: reuse the app icon's trace so splash and icon match.

## What was built

### Windows animation setting (`Controls/Motion.cs`)
- One switch for every animation in Pulse, following Windows' **Animation effects** setting (and
  updating live when it changes). With it off:
  - gauges, bars and peak markers jump straight to their values
  - the details panel and search appear without sliding or scaling
  - pages appear without rising in
  - the splash shows its whole trace and title, standing still
  - the live dot doesn't ripple and loading placeholders don't shimmer
  - dashboard tiles move without gliding
  - hot cards don't pulse, and cards don't lift on hover
- In code everything goes through `Motion.Instrument`, `Motion.Ui` or `Motion.Enabled`; XAML binds to
  `Motion.Current.AnimationsEnabled`. The splash heartbeat moved from a XAML trigger to code so it can
  stay still.

### Rolling numbers: built, then removed
- Digits rolled to new values like an odometer on the gauges, cooling, network, pinned tiles and the
  details panel. In use it was distracting for numbers that change every second, so it's gone and the
  values change plainly again.

### Splash (`Views/SplashView`)
- Rebuilt as a cardiac monitor:
  - a white-hot point sweeps across a faint oscilloscope grid, drawing a glowing ECG trace (P wave,
    spike, T wave) that fades out behind it like a comet tail
  - on the spike, two shockwave rings burst out, the red glow behind everything flares, and the monitor
    and title thump
  - "Pulse" assembles letter by letter, then the status line fades in
  - it beats until the first readings arrive, then zooms past while one last shockwave spreads across
    the window, and the dashboard rises in
- **Timing:** at least two beats (a beat is 1.6 s), counted from when the splash first appears, and it
  leaves just after a beat's spike so the farewell goes out with the heartbeat. If the readings are
  late it waits for the next spike: at most one more beat. The first version counted its minimum from
  when the window was created; WPF's startup used up much of that, so on screen it often showed less
  than one beat and felt too quick.
- The trace and all timing come from one list of points, so the head follows the line exactly: its x
  moves steadily, and its y passes each point at the moment its x does.
- With Windows animations off it's a still picture: the full trace and the title.
- The splash background is now opaque; the dashboard showing through faintly added noise.

### Heat-reactive cards
- When a card's main reading passes warning or critical thresholds (shared with the step 4 alert
  defaults), its border picks up the heat color, with a slow, subtle pulse at critical.
- Must stay readable: border only, never behind the text.

### Light theme and Windows accent
- `Themes/Light.xaml` with the same token names as `Dark.xaml`.
- Settings → Appearance: Dark, Light, or follow Windows (`SystemThemeWatcher` from WPF-UI).
- Optional: use the Windows accent color for neutral UI accents (navigation pill, buttons), keeping
  the per-category colors and the red Pulse brand.
- Check contrast: text on cards at least 4.5:1 in both themes.

### Small touches
- Smooth number transitions in All sensors rows (no flicker when values update).
- Hover elevation on clickable tiles.
- A refined splash: reuse the app icon's trace so splash and icon match.

## What was built

### Windows animation setting (`Controls/Motion.cs`)
- One switch for every animation in Pulse, following Windows' **Animation effects** setting (and
  updating live when it changes). With it off:
  - gauges, bars and peak markers jump straight to their values
  - the details panel and search appear without sliding or scaling
  - pages appear without rising in
  - the splash shows its whole trace and title, standing still
  - the live dot doesn't ripple and loading placeholders don't shimmer
  - dashboard tiles move without gliding
  - numbers change without rolling, hot cards don't pulse, and cards don't lift on hover
- In code everything goes through `Motion.Instrument`, `Motion.Ui` or `Motion.Enabled`; XAML binds to
  `Motion.Current.AnimationsEnabled`. The splash heartbeat moved from a XAML trigger to code so it can
  stay still.

### Rolling numbers (`Controls/RollingNumber.cs`)
- Digits roll to their new value like an odometer: up when the digit grows, down when it shrinks. Units
  and separators just change; places line up from the right, so "99°" → "100°" rolls the two shared
  digits. Only cells whose digit changed animate (320 ms, 30 fps cap), once per reading.
- Used for:
  - the four gauge values
  - the cooling headline
  - network download and upload
  - a pinned sensor's value
  - the details panel's live value
- The mini window keeps plain text, for games.

### Heat-reactive cards (`ViewModels/Heat.cs`)
- A card's border turns **orange** within 10 °C of its limit and **red** at it, with a slow pulse
  (1.1 s, 30 fps; no pulse with animations off). Border only, never behind the text.
- The limits are the user's alert rules (step 4), so moving the CPU alert to 100 °C also moves where the
  Processor card turns red:
  - **Processor:** package temperature against the CPU alert.
  - **Graphics:** hot spot (or core) temperature against the GPU hot spot alert.
  - **Drives:** the hottest drive against the drive alert.
  - **CPU cooling:** red when the picked pump header runs below the pump alert.
  - **System power:** no alert, so warm from 75% of the PSU's rating and hot from 90%.
  - **Pinned sensors:** temperature tiles with an alert limit.
- A 2 °C margin (50 rpm for the pump) keeps a reading that hovers at a boundary from flickering the
  border.

### Light theme (`Themes/Light.xaml`, `Services/ThemeService.cs`)
- Same token names as the dark theme: white cards over light Mica, deeper status colors (they're also
  used for text), lighter gauge tracks and a light band behind the ring range. The category accents
  are the same in both themes, so a color means the same thing everywhere.
- Settings → Appearance: **Match Windows** (default), **Dark**, **Light**. Switching is instant:
  WPF-UI's resources and Pulse's token file swap together, and every view uses DynamicResource.
  Match Windows follows Windows as it changes (WPF-UI's watcher, with Pulse's tokens following it).
- A new caption text color for both themes (`Pulse.Text.Caption`): WPF-UI's light "tertiary" text is
  about 3.3:1 on a card, below 4.5:1. Error text got its own lighter red in the dark theme for the same
  reason.
- The Windows accent color was already used for Pulse's buttons and toggles (WPF-UI default), so the
  plan's optional "use the Windows accent" needed nothing more.

### Small touches
- Clickable cards lift 2 px on hover (with animations on).
- Not done:
  - **All sensors "smooth transitions":** the rows already update in place without flicker.
  - **The splash/icon trace match:** the splash already uses the icon's heartbeat shape.

## Tests
236 passing (24 new; the rolling-number tests went with the feature):
- **Splash:** the beat runs left to right with one spike; each beat lasts long enough for the tail to fade
  before the next one starts; quick readings still get two beats and the exit comes after a spike;
  slow readings never wait more than one extra beat. (238 passing in all.)
- **Animation setting:** with animations off every duration is zero.
- **Heat:** levels and their hysteresis; the pump rule; cards following the user's alert limits
  (raising the CPU limit cools the card); the hottest drive warming the drive card.
- **Themes:** the choice resolving against Windows (including high contrast); Match Windows being the
  default; both theme files defining exactly the same tokens.
- **Contrast**, from the real theme files: primary, secondary, caption, warning, info and error text are
  at least 4.5:1 on a card in both themes, and heat borders at least 3:1.

Rendered offscreen in dark and light with a hot Processor card and a warm Graphics card, plus the light
Settings page. The splash was captured at moments through a beat and on its way out. The first capture
showed the trace missing: its fading reveal wasn't moving, because the animation pointed straight at a
brush's transform. It's now animated through each line's own property path.

## Done when
- [x] Switching between Light and Dark is instant and every surface follows (token swap, renders of
  both).
- [x] With Windows animations turned off, Pulse shows no motion other than value changes. Covered by the
  switch above and a test of the durations. Worth a quick look with the Windows setting actually
  turned off.
- [ ] Not seen yet in motion on the real screen: the splash and the heat pulse (offscreen captures are
  stills).
