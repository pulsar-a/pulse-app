# Step 6: Sensor details ✅

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

## What was built

### Panel (`Views/SensorDetailsView.xaml`, `ViewModels/SensorDetailsViewModel.cs`)
- Slides in from the right (540 px) over a dimmed page; closes with Esc, the close button, or a click on
  the dimmed part. Opening another sensor while it's open just swaps the content.
- **Header:** the friendly name with a pencil to rename inline (Enter saves, Esc cancels, clicking away
  saves), "Reset name" when renamed, the original name and hardware, and the live value in large type.
- **Ranges:** 5 min, 1 hour (default), 24 hours, 7 days. The range you pick is kept for the next sensor.
- **Stats** for the range: lowest, average, highest. For temperatures with an alert rule (step 4), the
  time spent above that rule's limit: "12 min above the 95° alert limit (8% of this range)" or "Stayed
  below the 95° alert limit the whole time".
- **Compare with:** any other sensor of the same kind, drawn in purple on the same chart.
- **Actions:** Create alert (adds a rule for this sensor and opens Settings → Alerts, as in step 4),
  Export CSV (this sensor's points for the range: time, min, average, max, in °C or °F). "Pin to
  dashboard" comes with step 7.
- **Live:** ranges up to an hour refresh with every reading from the in-memory stream; 24 hours and
  7 days re-read from disk every 30 seconds, off the UI thread. Stale loads are dropped if a newer one
  started (another range or sensor).

### Chart (`Controls/HistoryChart.cs`)
- **A custom lightweight control instead of LiveCharts2.** It only needs a band, a line, gridlines and a
  hover readout, and drawing it directly keeps the app free of SkiaSharp (about 10 MB of native code)
  and in step with the existing gauges and sparklines. No zoom or pan; the range buttons cover that.
- Min–max band with the average line on top, in the sensor kind's accent color.
- Gridlines on round values (1, 2, 2.5, 5 × 10ⁿ) and time labels on round times (every minute, 10 minutes,
  hour, day...), spaced to the panel's width.
- The line **breaks where there's no data** (Pulse wasn't running or the sensor was gone) instead of
  drawing a straight line across the hole.
- Hover: a crosshair and a tooltip with the time, average, and min–max for that point (and the compared
  sensor's value).
- At most 600 points per series, downsampled by `HistoryStore` with spikes kept.

### Entry points
- **Dashboard:** the four gauge cards, core tiles, fan rows, drive rows (temperature), the cooling card
  and the network card open details on click, with a hover state.
- **All sensors:** clicking a row opens details. Rename stays in the right-click menu (and the pencil in
  the panel), so double-click is no longer needed.
- **Ctrl+K** (or the search button in the header): a search palette. Every word must match the custom
  name, original name, hardware or sensor kind; names starting with the query come first. ↑/↓ to choose,
  Enter or a click to open, Esc to close. Each result shows its current value.

## Tests
139 passing (27 new): nice axis steps and ranges, time labels (5 minutes, an hour, a week), lines
breaking at gaps, range stats including time above the limit and its wording, CSV export in °C and °F,
axis labels following the unit, alert limits from the built-in and disabled rules, search matching and
ranking, the catalog's full sensor list, and dashboard cards knowing which sensor they show.

Verified offscreen with synthetic data (a probe that renders the panel and the main window to PNG): the
whole window's XAML loads, the chart, compare overlay, rename box and search palette render as intended.
Not yet checked in the running app on real hardware: the slide animation, keyboard handling, and the
live tail (Pulse was already running, so a second copy wasn't started).

## Done when
- [x] Clicking the CPU ring shows a 24-hour package temperature chart within a fraction of a second
  (a 24-hour query takes 3–6 ms, step 5).
- [ ] The chart keeps moving live while the panel is open, with no UI stalls in the log: to confirm in
  the running app.
