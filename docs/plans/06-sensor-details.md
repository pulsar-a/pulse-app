# Step 6: Sensor details

**Goal:** click any sensor (a dashboard ring, a core bar, a fan, a drive, or a row in All sensors) to
open a details panel with a large chart and statistics.

## Why now

The chart reads history from step 5 and names from step 1. Step 7's pinned tiles open this same panel,
so it comes first.

## Design

### Panel
- A slide-in panel from the right (overlaying the page, about 480 px wide), closed with Esc or a
  click outside. Several sensors can be compared by adding them to the same chart.
- **Header:** friendly name (renameable inline), original name, hardware, current value in large type.
- **Range tabs:** 5 min, 1 h, 24 h, 7 d.
- **Stats:** min, average, max, and time spent above a threshold (for temperatures).
- **Actions:** Pin to dashboard (wired up in step 7), Create alert (opens the step 4 rule editor
  pre-filled), Export CSV (step 5).

### Chart
- Use LiveCharts2 (`LiveChartsCore.SkiaSharpView.WPF`) for zoom, pan and tooltips. The dashboard
  keeps the lightweight `Sparkline` control.
- Style the chart with the theme tokens: accent line, gradient fill like the sparklines, min/max band.
- Live updates: the last point moves in real time from `SensorStream`; older points come from `HistoryStore`.

### Entry points
- Dashboard: ring cards, core tiles, fan rows, drive rows and the cooling card become clickable,
  with a hover state.
- All sensors: clicking a row opens the panel; right-click keeps the rename menu.
- A keyboard shortcut to search for any sensor (Ctrl+K) and open its details.

## Tasks
- [ ] Details panel shell with animation and keyboard support.
- [ ] LiveCharts2 chart with themed styling; ranges; live tail.
- [ ] Stats calculation.
- [ ] Entry points from the dashboard and All sensors; Ctrl+K sensor search.
- [ ] Compare mode (add a second sensor).
- [ ] Tests: stats over synthetic series; range-to-query mapping.

## Done when
- Clicking the CPU ring shows a 24-hour package temperature chart within a fraction of a second.
- The chart keeps moving live while the panel is open, with no UI stalls in the log.
