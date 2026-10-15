# Step 9: Visual polish

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

### Small touches
- Smooth number transitions in All sensors rows (no flicker when values update).
- Hover elevation on clickable tiles.
- A refined splash: reuse the app icon's trace so splash and icon match.

## Tasks
- [ ] `RollingNumber` control; apply to gauges and tiles.
- [ ] Reduced-motion support across all animations.
- [ ] Heat-reactive borders.
- [ ] Light theme, theme switcher, follow-Windows mode, optional system accent.
- [ ] Contrast check in both themes.

## Done when
- Switching between Light and Dark is instant and every surface follows.
- With Windows animations turned off, Pulse shows no motion other than value changes.
