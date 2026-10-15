# Step 0: Housekeeping

**Goal:** a committed, tested baseline to build on, and the one open bug (missing SATA drives) solved.

## Why first

Every later step changes shared files. A clean commit gives a known-good point to return to, and
tests on the view-model logic catch regressions as the dashboard grows.

## Tasks

### Commit the baseline
- [ ] Review `git status` in `G:\code\Pulse`, make sure `bin/` and `obj/` are ignored, commit everything.
- [ ] Optional: create the GitHub repository and push.

### Add a test project
- [ ] Add `tests/Pulse.Tests` (xUnit), targeting `net10.0-windows`, referencing `src/Pulse`.
- [ ] Add a `make test` target that runs `dotnet test`.
- [ ] **Snapshot fixtures:** add a hidden debug option (for example `Pulse.exe --dump-snapshot <file>`)
      that writes one `HardwareSnapshot` to JSON. Capture one as admin with PawnIO and one without,
      and commit both under `tests/Pulse.Tests/Fixtures/`. They let tests run against real sensor
      layouts from this machine without needing the hardware.
- [ ] First tests:
  - `SensorFormat`: units, clock in GHz, rates, durations.
  - `DashboardViewModel.Apply` with each fixture: CPU card picks package temperature, memory card
    ignores DIMM limit sensors, the pump is auto-selected when exactly one header spins,
    drives map to the right letters.
  - `SensorsViewModel`: groups start collapsed; search shows matches inside collapsed groups;
    clearing the search restores the previous view.

### Solve the missing SATA drives
LHM's own probes find all five drives, but Pulse sometimes shows only the three NVMe ones, even
after its re-scans (`HardwareService.RunAsync`).
- [ ] Run Pulse as admin a few times and read `%LOCALAPPDATA%\Pulse\pulse.log`: the "Hardware found",
      "Windows reports N drives" and "Drive re-scan" lines show what LHM returned.
- [ ] Check whether it correlates with something else polling the disks (ASUS Armoury Crate is
      running on this machine), with two Pulse instances running at once, or with time since boot.
- [ ] Fix accordingly. Candidates: open storage on a separate thread before other hardware groups;
      re-scan with a longer delay; prevent a second Pulse instance (see below).

### Single instance
- [ ] Allow only one Pulse at a time (named mutex). A second launch brings the first window to the
      front instead. Two instances polling the same hardware double the load and can make drivers
      and controllers return errors. Step 3 relies on this when Pulse lives in the tray.

## Done when
- The baseline is committed and `make test` passes.
- All five drives show up on ten consecutive admin launches.
- Launching Pulse twice focuses the existing window.
