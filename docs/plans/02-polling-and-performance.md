# Step 2: Polling pipeline and performance

**Goal:** turn "read everything every second and push a snapshot" into a small data pipeline that
every later feature subscribes to, and make Pulse start faster and idle cheaply.

## Why now

Tray (3), alerts (4), history (5), details (6), process insights (10), the Corsair cooler (11) and
the web view (12) all consume sensor data. If each of them reached into `HardwareService` its own
way, they'd duplicate work and fight over timing. Building the pipeline once means each feature is
just another subscriber.

## Design

### Smart polling
- Split polling by hardware group, each with its own interval:
  - CPU, GPU, memory, motherboard, PSU: 1 s (the user setting; default 1 s).
  - Network: 1 s (its rates are per-second anyway).
  - Storage: 5 s. SMART reads are slow and drive temperatures change slowly.
- One polling thread; each tick updates only the groups that are due, then publishes a snapshot.
- **Idle mode:** when the window is minimized or hidden in the tray, the UI stops applying
  snapshots. Polling continues at a slower rate (for example 2 s, configurable), unless alerts or
  history need the normal rate.

### Sensor stream and in-memory history
- New `Services/SensorStream.cs`: after each tick it appends every numeric sensor value to a
  per-sensor ring buffer (last 60 minutes at the polling rate) and raises `SnapshotPublished`.
- About 400 sensors × 3600 samples × 4 bytes ≈ 6 MB. That's acceptable; measure it.
- The dashboard sparklines read from these buffers instead of keeping their own queues
  (`GaugeCardViewModel._history`, `NetworkViewModel`, `CoolingViewModel`), so history survives
  page switches and is shared.
- Consumers subscribe with a filter (sensor ids or a predicate) and get callbacks on a background
  thread. UI consumers marshal to the dispatcher themselves, using the "only apply the newest
  snapshot" pattern in `MainViewModel.OnSnapshotReady`.

### Startup speed
- **Lazy All sensors page:** create `SensorsView` the first time it's opened, not at startup.
- **ReadyToRun:** publish with `PublishReadyToRun=true` (precompiled code, so less work at launch).
  Add `make publish` producing a folder build.
- Measure with the UI watchdog and a `Stopwatch` from process start to the first rendered frame
  (`ContentRendered`); log it. The current baseline is about 0.5 s of UI-thread work while the
  window is built.
- Check whether WPF-UI's `ControlsDictionary` is the main cost; if so, merge only the styles Pulse
  uses.

### Update interval setting
- Settings → General → "Update every": 0.5 s, 1 s, 2 s, 5 s. The polling thread picks it up live.

## Tasks
- [ ] Per-group polling intervals in `HardwareService`.
- [ ] `SensorStream` with ring buffers and subscriptions; move dashboard sparklines onto it.
- [ ] Idle mode when minimized; resume instantly on restore with the latest values.
- [ ] Lazy All sensors page.
- [ ] `make publish` with ReadyToRun; record startup time in the log.
- [ ] Update-interval setting.
- [ ] Tests: ring buffer wrap-around, subscription filters, interval scheduling (with a fake clock).

## Done when
- Minimized Pulse uses close to 0% CPU on the UI thread (check in Task Manager and the log).
- Startup to first frame is measurably faster than the baseline; the number is in the log.
- Switching pages keeps sparkline history intact.
