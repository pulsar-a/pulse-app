# Step 2: Polling pipeline and performance

**Goal:** turn "read everything every second and push a snapshot" into a small data pipeline that
every later feature subscribes to, and make Pulse start faster and idle cheaply.

**Status:** done. Measurements are below; a few items were changed or dropped based on what the
measurements showed.

## Why now

Tray (3), alerts (4), history (5), details (6), process insights (10), the Corsair cooler (11) and
the web view (12) all consume sensor data. With the pipeline in place, each of them is just another
subscriber to `SensorStream`.

## What was built

### Smart polling (`Services/PollSchedule.cs`, `Services/HardwareService.cs`)
- One polling thread. Each tick reads only the hardware that's due: everything at the user's interval,
  drives every 5 s (or slower, if the interval is slower). Hardware not read this tick keeps its
  previous readings in the snapshot.
- Ticks keep a steady rhythm (the time a tick took is subtracted from the wait).
- Drive letters refresh every 30 s and drive re-scans run 5 s apart, now by time rather than by tick
  count, so they behave the same at every interval.

### Sensor stream (`Services/SensorStream.cs`)
- `HardwareService` publishes every snapshot to `SensorStream`, which records each sensor in a ring
  buffer (3600 samples, about an hour at 1 s, roughly 6 MB) and raises `SnapshotPublished`.
- `Recent(id, count)` and `Since(id, from)` read history from any thread. Missing readings are gaps (NaN).
- The dashboard sparklines read from the stream instead of keeping their own queues. History is
  shared and keeps recording while the window is hidden or another page is open. Derived lines, such
  as CPU + GPU power when there's no PSU sensor, are computed from the stream.
- Sparklines draw gaps as breaks and always span about a minute, whatever the interval.

### Idle mode
- When the window is minimized or hidden, the UI stops applying snapshots and polling slows to every
  2 s (or the user's interval, if slower). The stream keeps recording; on restore, the newest snapshot
  is applied immediately.
- The page on screen is the only one doing UI work: the dashboard pauses while All sensors or
  Settings is open (All sensors already did) and catches up when opened.

### Rendering cost
Measuring per-thread CPU showed the cost was drawing, not data processing (applying a snapshot
averages 1.6 ms):
- **Live dot:** it looped forever, which kept the whole window redrawing 20+ times a second. It now
  pulses once per reading, which also makes it meaningful: a pulse means fresh data.
- **Instrument animations** (gauges, bars, peaks, shimmer) are capped at 30 fps (`Controls/Motion.cs`)
  and take 0.45 s instead of 0.7 s, so the window is idle between readings.

### Startup
- **Lazy pages:** All sensors and Settings are built the first time they're opened (8–10 ms, logged
  as "Built SensorsPage"), so startup only builds the dashboard.
- **First-frame time** is logged: "First frame N ms after the process started".
- **ReadyToRun:** `make publish` produces a precompiled Release build in `artifacts/publish`.

### Update interval
- Settings → General → "Update every": 0.5 s, 1 s, 2 s, 5 s, applied from the next reading.

### Diagnostics
- Once a minute the log records how many snapshots were applied and their average and slowest
  UI-thread time.

## Measurements (dev machine, Pulse without admin)

| | Before | After |
|---|---|---|
| Dashboard visible, render thread | 2.3 s per 20 s (11% of a core) | 0.83 s (4%) |
| Dashboard visible, UI thread | 1.8 s per 20 s (9%) | 0.84 s (4%) |
| Settings page visible, render + UI | 1.45 s per 20 s (7%) | 0.3 s (1.5%) |
| Minimized, everything | — | about 2.5% of a core, mostly the polling thread |
| Polling thread (1 s interval) | — | about 2.5% of a core |
| All sensors, first open | ~190 ms block | 8–10 ms |
| First frame | 0.74–1.47 s (Debug) | 0.72–0.81 s (ReadyToRun) |

In Task Manager terms (32 threads), the visible dashboard is about 0.3% of total CPU.

### What didn't pan out
- **ReadyToRun barely changes startup.** The ~0.5 s of UI-thread work before the first frame is WPF
  and WPF-UI loading their resources and building the window, not compiling code. `make publish` stays
  for release builds, but it isn't a startup fix.
- **Trimming WPF-UI's `ControlsDictionary`** was not attempted: Pulse uses enough of its controls
  (buttons, text boxes, combo boxes, radio buttons, expanders, context menus, title bar) that a
  hand-picked subset would be fragile. Revisit only if startup becomes a real complaint.

## Tests
60 passing (15 new): poll schedule (every tick, drives every 5 s, slow intervals, idle mode, drift
tolerance), sensor stream (order, wrap-around, gaps, timed reads, subscribers), dashboard history from
the stream (including while paused), the CPU + GPU power sum, sparkline length per interval, and the
interval fallback.

## Done when
- [x] Minimized Pulse uses close to 0% CPU on the UI thread.
- [x] Startup to first frame is logged; ReadyToRun and lazy pages measured.
- [x] Switching pages keeps sparkline history intact (it lives in the stream).
