# Step 10: Process insights ("What's heating my PC") ✅

**Goal:** show which processes use the CPU and GPU right now, next to the temperatures they cause.

## Why now

It only needs the polling pipeline (step 2) to sample processes on the same schedule. It's
independent of the UI-heavy steps, so it can move earlier if wanted.

## Design

### Data
- **CPU per process:** sample `Process.TotalProcessorTime` for all processes every 2 s and compute
  deltas. Cheaper alternatives: `NtQuerySystemInformation(SystemProcessInformation)` in a single call.
  Measure both; pick whichever keeps sampling under about 5 ms.
- **GPU per process:** the Windows "GPU Engine" performance counters (`\GPU Engine(*)\Utilization Percentage`,
  instance names contain `pid_<id>`), summed per process. The same data Task Manager shows.
- **Memory per process:** working set, from the same sampling.
- Sample only while the card or page is visible, or when an alert rule needs it.

### UI
- A dashboard tile (step 7) "Top processes": top 5 by CPU and top 5 by GPU, with icon, name, a small bar
  and percentage. Clicking opens a full page with sorting and search.
- Link to temperature: when CPU temperature is high, the tile highlights and is titled
  "What's heating your CPU".
- Action: "End task" with a confirmation, only for non-system processes.

## What was built

### Sampling (`Services/Processes/`)
- **CPU and memory:** one `NtQuerySystemInformation(SystemProcessInformation)` call returns every process
  with its CPU time, private working set (Task Manager's Memory column), start time and session. Measured
  on this PC (365 processes): **2.4 ms** per sample. The `Process` class took 12 ms and, without admin
  rights, could read only 204 of them, because it opens each process. So the native call it is.
- **CPU %** is the share of the whole CPU over the interval, like Task Manager: CPU time used ÷ (wall time ×
  logical processors). A process is known by its PID *and* start time, so a PID that Windows reuses never
  inherits the old process's time; a process that started since the last sample counts all its time.
- **GPU per process:** the "GPU Engine" counters through PDH with a wildcard (new processes are picked up),
  about **4–5 ms** per read. Per process, the busiest engine counts (a game at 80% of 3D and 10% of Copy is at
  80%), as in Task Manager. Without the counters the GPU columns go away.
- **Programs, not processes:** processes are grouped by executable name, so Chrome's 15 processes are one
  row. Each program's description ("Google Chrome") and icon are looked up once and kept, a few per sample,
  the ones on the dashboard tile first.
- **Only while visible:** a background thread samples every 2 s while the Top processes tile is on the
  dashboard (and the dashboard is showing) or the Processes page is open, with the window up. Otherwise it
  sleeps. The first sample after waking is only a baseline, so the next one comes after 1 s.
- `pulse.log` gets a once-a-minute summary of what sampling costs, split into processes and GPU counters.

### Top processes tile
- A new built-in card, full width at the end of the dashboard (existing layouts get it at the end too):
  the top 5 programs by **CPU**, **GPU** and **memory**, each with its icon, a bar and the figure. A narrower
  tile shows as many lists as fit (`Controls/ColumnsPanel`): CPU and GPU at half width, CPU alone below that.
- Clicking it opens the Processes page.
- **Heat link:** when the Processor card turns warm or hot (the step 9 heat rules), the tile takes the same
  border and becomes "What's heating your CPU · Processor at 96°", with the Processor list's heading in the
  warning color. The same for the graphics card ("What's heating your GPU"), when GPU figures exist.

### Processes page
- A fourth page, **Processes**, between All sensors and Settings: every program with CPU (and a small bar),
  GPU and memory, sorted busiest first. Click a column to sort by it, again to flip; search matches the
  friendly name or the file name. Rows are moved, not rebuilt, so the list doesn't flicker.
- **End task**, on hover, only for the user's own programs: not services (session 0), not other users', not
  Windows' own (dwm, csrss, svchost and the like), and not Pulse. It always asks first, says how many
  processes will close, and checks each process's start time before ending it, so a reused PID is never hit.

## Tests
270 passing (32 new):
- **CPU deltas:** the first sample is a baseline; share of all cores over the interval; a newly started process;
  a reused PID; exited processes; readings out of range; reset after a pause.
- **GPU:** busiest engine per process, duplicates adding up, idle and malformed instances ignored.
- **Programs:** grouping and totals; who can be ended (services, other sessions, Windows, Pulse).
- **Tile:** top 5 per metric; CPU and memory only without GPU counters; waiting for real figures and keeping
  them through a pause; the "What's heating" titles and heading colors; the dashboard noticing the tile is hidden (which pauses sampling).
- **Page:** sorting, search, rows kept between updates, only updating while open, End task asking first and
  refusing system programs, the message when Windows refuses.
- **Real machine:** the native sample against the `Process` class (it must find this test process with the right
  start time and stay under 50 ms), the GPU counters, and ending a real child process only with its real start time.

Rendered offscreen in dark and light with real processes: the full-width tile, a half-width "What's heating
your CPU" tile, and the Processes page.

## Done when
- [ ] Running a game shows it at the top of the GPU list within 2 seconds. Not tried with a game yet; the
      sampling interval is 2 s and the GPU counters update per sample.
- [x] Sampling doesn't show up as a noticeable cost in Pulse's own CPU usage: about 7 ms every 2 s, and only
      while a tile or page shows it. Worth confirming from the once-a-minute line in `pulse.log`.
- [ ] Not seen yet in the running app: the tile, the page, End task's confirmation dialog.
