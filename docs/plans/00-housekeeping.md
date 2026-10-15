# Step 0: Housekeeping

**Goal:** a committed, tested baseline to build on, and the one open bug (missing SATA drives) solved.

**Status:** in progress. Code tasks are done; the remaining items need admin runs on the dev machine.

## Why first

Every later step changes shared files. A clean commit gives a known-good point to return to, and
tests on the view-model logic catch regressions as the dashboard grows.

## Tasks

### Commit the baseline
- [x] Baseline committed (`7352379 feat: first commit`).
- [ ] Optional: create the GitHub repository and push.

### Add a test project
- [x] `tests/Pulse.Tests` (xUnit, `net10.0-windows`, `win-x64`) referencing `src/Pulse`, added to `Pulse.slnx`.
- [x] `make test` runs `dotnet test` into `artifacts/test`, so tests also run while Pulse is open
      (a running Pulse locks `src/Pulse/bin`).
- [x] **Snapshot fixtures:** `Pulse.exe --dump-snapshot <file>` reads the hardware twice (rates need
      two reads) and writes a `HardwareSnapshot` as JSON (`Services/SnapshotDump.cs`, `Services/SnapshotJson.cs`).
  - [x] `Fixtures/non-admin.json` captured.
  - [ ] `Fixtures/admin.json`: run `make snapshot` (asks for admin rights; close Pulse first).
        `Real_snapshot_with_admin_fills_the_dashboard` is a no-op until this file exists.
- [x] Tests isolate themselves: settings and log go to a temporary folder (`SettingsService(folder)`,
      `Log.FilePath`), numbers are formatted with the invariant culture.
- [x] First tests (32 passing):
  - `SensorFormatTests`: units, GHz switch, rates, durations, missing/NaN values.
  - `DashboardViewModelTests`: package temperature vs load fallback, session min/max, DIMM limit
    sensors ignored, pump auto-pick (single header, cleared by user, several headers), drive letters
    and health, the real non-admin fixture.
  - `SensorsViewModelTests`: collapsed by default, expand, search inside collapsed groups and
    restore, no-match state, rows update only while the page is active.
  - `SnapshotJsonTests`: fixture round trip.

### Solve the missing SATA drives
LHM's own probes find all five drives, but Pulse sometimes showed only the three NVMe ones, even
after its re-scans (`HardwareService.RunAsync`).

**Findings so far:** every run that missed the SATA drives had a second hardware reader running at the
same time (a second Pulse, or a probe alongside Pulse). A solo admin run of Pulse found all 5
(`pulse.log`, 18:15: "Windows reports 5 internal fixed drive(s); LHM found 5"). Two processes doing
SMART reads on the same SATA controller at once is the leading suspect. ASUS Armoury Crate also runs on
this machine and may contribute.

- [x] Single instance (below) removes the Pulse-versus-Pulse case.
- [ ] Verify: launch Pulse as admin 10 times (`make start`, close, repeat) and check `pulse.log` each time
      for "LHM found 5". If it still misses drives, compare runs with Armoury Crate stopped.
- [ ] If misses remain: open storage before the other hardware groups, or re-scan with a longer delay.

### Single instance
- [x] `Services/SingleInstance.cs`: a named mutex allows one Pulse per user session. A second launch
      signals the running copy (named event) to restore and focus its window, then exits with code 2.
      Checked: launching twice leaves one process; the second exits with code 2.
- [ ] Check the bring-to-front behavior by eye (minimize Pulse, launch it again).

## Done when
- The baseline is committed and `make test` passes. ✅
- All five drives show up on ten consecutive admin launches.
- Launching Pulse twice focuses the existing window.
