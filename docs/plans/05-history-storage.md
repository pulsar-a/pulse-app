# Step 5: History storage

**Goal:** keep sensor history beyond the 60-minute in-memory buffer, so users can look back hours or
days, and export it.

**Status:** done. Size and speed measured with a synthetic week; a live recording on the dev machine is
still to be checked (Settings → History shows the size).

## Why now

It extends the in-memory buffers from step 2. The details panel (step 6) and the web view (step 12)
read from it.

## What was built (`Services/History/`)

### Storage (`HistoryDatabase.cs`)
- SQLite (`Microsoft.Data.Sqlite`) at `%LOCALAPPDATA%\Pulse\history.db`, WAL mode, incremental vacuum.
- **One row per sensor per minute** (min, average, max), not every reading:
  - `sensor(id, key, type, name, hardware)`
  - `minute(sensor_id, ts, min, avg, max)`: `WITHOUT ROWID`, primary key `(sensor_id, ts)`; `ts` is minutes since year 1, UTC.
  - `session(id, start_ts, end_ts)`: when Pulse was running.
- **Compact values:** stored as whole hundredths (72.35 → 7235), which SQLite packs into 1–4 bytes
  instead of 8.
- **Unchanged minutes are skipped**, with a keyframe every 60 minutes. Reads carry the last value forward,
  but only inside a session and never across a **gap row** (NULL values), which is written once when a
  sensor stops reporting. So "unchanged", "sensor gone" and "Pulse wasn't running" are never confused.
- **Corrupt or unreadable file:** moved aside as `history.corrupt-<time>.db` and a fresh database
  started. `PRAGMA quick_check` runs on open.

### Recording (`MinuteAggregator.cs`, `HistoryRecorder.cs`)
- Minutes are aggregated on the polling thread (cheap) and written by a background task through a
  queue, so SQLite never blocks polling or the UI.
- The minute in progress is written on exit, after polling stops.
- Pruned hourly to the retention setting. A failed write is logged and recording carries on.

### Reading (`HistoryStore.cs`)
- `Query(sensor, from, to, maxPoints)`: recent data from the in-memory stream (full resolution, includes
  the minute in progress), older data from disk, merged.
- Downsampling into equal time buckets keeps each bucket's lowest min and highest max, so spikes survive.

### Export (`HistoryExport.cs`)
- CSV with one row per minute and one column per sensor (the minute's average). Headers use Pulse's names
  (renames included) with units; temperatures follow °C/°F. UTF-8 with BOM so Excel reads "°" correctly.

### Settings → History
- Keep history: Off / 1 day / 7 days (default) / 30 days, with the current size on disk.
- Export CSV for the last hour, 24 hours, 7 days or 30 days.
- Delete history (click again within 4 s to confirm).

## Measurements (synthetic week, 400 sensors)

The benchmark wrote 7 days of minutes for 400 sensors: 150 constant (timings, limits), 150 temperatures
drifting with noise, 100 noisy loads, clocks and rates.

| | Result |
|---|---|
| Size | **57 MB for 7 days, about 8 MB a day** (30 days ≈ 250 MB) |
| Write | 6.6 ms per minute batch, on the background thread |
| 24-hour query, 600 points | 3–6 ms |
| 7-day query, 600 points | 20–40 ms |

Within the plan's budget (under 200 MB at the default 7 days; under 50 ms for a 24-hour query).

## Tests
112 passing (12 new): minute aggregation, unchanged-minute skipping with complete reads, hourly keyframes,
gap rows, no carrying across sessions, pruning, corrupt-file recovery, merging disk with the live stream,
spike-preserving downsampling, CSV export (headers, units, °F), the recorder end to end (including the
partial minute on exit), and recording turned off.

## Done when
- [x] A 24-hour query returns in under 50 ms (3–6 ms with a synthetic week).
- [x] The database stays within the size budget at the default retention (~57 MB for 7 days of 400 sensors).
- [x] Killing Pulse mid-write never corrupts the database: SQLite's WAL makes each minute's write atomic,
      and a damaged file is detected on open and replaced.
- [ ] Check on the dev machine after a day: size in Settings → History, and an exported CSV opening in Excel.
