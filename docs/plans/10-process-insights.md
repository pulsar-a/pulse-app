# Step 10: Process insights ("What's heating my PC")

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

## Tasks
- [ ] Process sampler (CPU, memory) with benchmarks.
- [ ] GPU per-process counters.
- [ ] Top processes tile and full page.
- [ ] Heat-linked highlight.
- [ ] Tests: delta calculation, process exit or PID reuse between samples.

## Done when
- Running a game shows it at the top of the GPU list within 2 seconds.
- Sampling doesn't show up as a noticeable cost in Pulse's own CPU usage.
