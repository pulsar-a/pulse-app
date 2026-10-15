# Step 5: History storage

**Goal:** keep sensor history beyond the 60-minute in-memory buffer, so users can look back hours or
days, and export it.

## Why now

It extends the in-memory buffers from step 2. The details panel (step 6) and the web view (step 12)
read from it, so it has to exist before them.

## Design

### Storage
- SQLite via `Microsoft.Data.Sqlite`, database at `%LOCALAPPDATA%\Pulse\history.db`.
- **Don't store every reading.** About 400 sensors at 1 Hz is roughly 35 million rows a day. Instead:
  - Full-rate data stays in memory only (the step 2 buffers, last hour).
  - On disk, store **1-minute aggregates**: min, average, max per sensor per minute.
  - Skip sensors that never report a value, and those that haven't changed during the minute (store
    the first unchanged minute, then gaps mean "unchanged").
- Schema sketch:
  - `sensor(id INTEGER PRIMARY KEY, key TEXT UNIQUE, type INTEGER, unit TEXT)`
  - `minute(sensor_id, ts_minute, min REAL, avg REAL, max REAL, PRIMARY KEY(sensor_id, ts_minute)) WITHOUT ROWID`
- Retention: 7 days by default (Settings → History: 1, 7, 30 days, or off). Prune once an hour.
- Measure the database size on this machine during the step, and adjust the defaults to keep it
  under about 200 MB.

### Writer
- `Services/HistoryRecorder.cs` subscribes to `SensorStream`, aggregates in memory, and every minute
  writes one batch in a single transaction on a background thread. Use WAL mode.
- On shutdown it flushes the partial minute.
- If the database is locked or corrupt: log it, rename the file, start fresh. Never block the UI or polling.

### Query API
- `HistoryStore.Query(sensorKey, from, to, maxPoints)` merges disk minutes with the in-memory
  full-rate buffer for the most recent hour, and downsamples to `maxPoints` (largest-triangle or min/max
  buckets), so charts stay fast.

### Export
- Settings → History → "Export CSV…": choose sensors and a time range; writes one column per sensor.

## Tasks
- [ ] Schema, migrations, WAL mode.
- [ ] `HistoryRecorder` with per-minute aggregation and batched writes.
- [ ] Retention pruning and settings.
- [ ] `HistoryStore.Query` with downsampling and in-memory merge.
- [ ] CSV export.
- [ ] Tests: aggregation correctness, gap handling, downsampling, a corrupt-file recovery path.

## Done when
- After a day of use, a 24-hour query for a sensor returns in under 50 ms.
- The database stays within the size budget at the default retention.
- Killing Pulse mid-write never corrupts the database.
