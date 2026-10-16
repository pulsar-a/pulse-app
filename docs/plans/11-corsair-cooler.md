# Step 11: Corsair cooler over USB (built; read from the real device)

**Goal:** read coolant temperature and pump speed directly from the Corsair cooler controller, the
only way to get coolant temperature on this machine.

## Background

- LibreHardwareMonitor 0.9.6 supports NZXT Kraken, Aqua Computer, Arctic, AeroCool, MSI, Razer and older
  fan controllers, but **no Corsair cooler controllers**. On this PC, the Corsair device with USB product
  ID `1B1C:0C32` is most likely a Commander ST (shipped with iCUE Elite LCD coolers). **Confirm the exact
  model first.**
- The open-source tool [liquidctl](https://github.com/liquidctl/liquidctl) documents the Commander Core
  family's USB HID protocol (`liquidctl/driver/commander_core.py`). That's the reference.
- `HidSharp` is already available (LHM depends on it).

## Risks and rules
- **Read-only.** No speed or lighting commands in this step.
- **Don't fight iCUE.** If iCUE (or another tool) is running, it owns the device. Detect it and either
  stay off the device or read in a way known to coexist. Document what was tested.
- The protocol puts the device into a "software mode" while talking to it. Make sure Pulse always
  returns it to "hardware mode" (including on crash, via a guard) so the cooler keeps its own fan and
  pump curves.
- Throttle reads (for example every 2 s) and handle disconnects gracefully.

## Plan
1. **Spike (time-boxed):** a console tool in `tools/CorsairProbe` that identifies the device,
   wakes it, reads firmware, speeds and temperatures, then puts it back to sleep. Compare readings with
   iCUE. Decide go or no-go.
2. If go: a `Services/Coolers/CorsairCommanderReader.cs` that produces readings in the same shape as
   LHM hardware (a `HardwareReading` of type Cooler), merged into each snapshot by the polling pipeline.
3. The cooling card picks them up automatically: Cooler hardware is already treated as pump and
   coolant data in `DashboardViewModel.UpdateCooling`.
4. Consider contributing the reader upstream to LibreHardwareMonitor, so it's maintained there.

## Findings (2026-10-16)
- **The device:** USB `1B1C:0C32`, a composite device with two HID interfaces (`MI_00`, `MI_01`).
  liquidctl lists `0x0c32` as the **Corsair Commander ST**. Also on this PC: `1B1C:1C07` (the Corsair PSU,
  which LHM already reads) and `1B1C:0C39` (a consumer-control HID device, not part of the cooler protocol).
  Firmware 2.0.19 (read by the probe, below).
- **iCUE is resident:** `Corsair.Service` runs in the background even with the iCUE window closed, so iCUE
  owns the controller unless that service is stopped.
- **Protocol** (liquidctl `commander_core.py`): HID interface 0, 96-byte reports, byte 1 always `0x08`.
  - Wake `01 03 00 02` and sleep `01 03 00 01`, around every operation (sleep in a `finally`).
  - Firmware: `02 13`, then `res[3].res[4].res[5]`.
  - A read opens an endpoint with `0d 00 <mode>`, reads `08 00 01/02/03`, then closes with `05 01 00`.
  - Speeds: mode `17`, type `06 00`; a count, then u16 LE rpm.
  - Temperatures: mode `21`, type `10 00`; 3 bytes per sensor: connected flag, then u16 LE in tenths of a °C.
- **Status in liquidctl:** the whole family is marked "(broken)" (issues #520, #583, #598, #623, #705).
  The reports are about **fan control** (curves failing on the ST, fans stuck at 100% on the XT), not
  reading. A 2026 fork (not merged) found the ST needs a different packet length per device, so the
  read path may need that fix too.
- **The risk for Pulse:** the wake and sleep commands are writes that hand control between software and the
  cooler's own curves. Sent while iCUE is driving the cooler, a sleep from Pulse could drop iCUE's curves
  until iCUE takes it back. The probe has to be run with that in mind.

- **The ST's reports are 64 bytes, not 96** (`make probe`: 65 in and out, with the report ID). liquidctl
  assumes 96 for the whole family, which likely explains the ST being "broken" there. Pulse takes the size
  from the device.

## Probe on the real Commander ST: go (2026-10-16)
Run with Pulse, iCUE and `Corsair.Service` closed (`make probe` first confirmed nothing else was using it).
- **Firmware 2.0.19**, 64-byte reports, and the data types are the documented ones (`09 00`, `06 00`,
  `10 00`): the type quirk from liquidctl's issue doesn't occur on this firmware.
- **All 7 speed channels connected:** pump about 2,560–2,590 rpm, fans 1–3 about 1,820–2,010 rpm, fans 4–6 about
  1,230–1,380 rpm. **Coolant 40.7–41.6 °C**, climbing slightly over the minutes with iCUE's fan curve gone.
  The second temperature channel reports "not connected". Readings were consistent over five runs.
- **On this device, chunks 2 and 3 repeat chunk 1**, because everything fits in one 64-byte report. The driver
  now reads more chunks only when the data needs them: 14 exchanges per read instead of 26.
- **Timing:** the data reads take about 1 ms each. The mode switches are the cost: **wake about 30 ms, sleep
  about 138 ms** (the controller taking its own curves back). The cooler waits on software for about 10 ms
  per read; each read costs about 175 ms on Pulse's cooler thread, every 2 s.
- **Every run ended with the sleep acknowledged**, leaving the cooler on its own curves.
- The capture is in `tests/Pulse.Tests/Fixtures/commander-st.txt`, and a test replays it byte for byte.
- **Still to do by eye:** compare with iCUE. Open iCUE right after a probe run (Pulse pauses for it
  automatically) and check the pump within ±50 rpm and the coolant within ±1 °C.

## What was built

### Many devices, not one (`Services/Coolers/`)
Pulse is meant for other people's PCs too, so this is a small driver layer rather than a Corsair special case:
- `KnownCoolers`: the registry. Each entry is a USB vendor and product ID, the interface the protocol runs on,
  a driver factory, and the apps known to own that device. Supporting another controller means a driver and a
  line here. Devices LibreHardwareMonitor already reads (NZXT Kraken, Aqua Computer, Arctic) stay with LHM.
- `ICoolerDriver` reads one device into an ordinary `HardwareReading` of type Cooler, so the cooling card,
  Fans card, alerts, history, tray, mini window and sensor details all take it as they come. `ICoolerLink`
  is the HID transport behind it (`HidCoolerLink`, on the HidSharp that LHM ships), so drivers are tested
  against a simulated device.
- `CorsairCommander`: the Commander Core, Core XT and ST. Read-only by design: no speed, curve or lighting
  commands exist in it. An independent implementation of the protocol liquidctl documents (liquidctl is GPL;
  none of its code is used).

### Reading safely
- Every read is wake → read → sleep, a few milliseconds every 2 s. The sleep goes out even when the read
  fails (and a failing sleep never hides the original error). The first read after opening also starts with a
  sleep, in case an earlier run was killed mid-read.
- Report sizes come from the device. Replies that don't echo the command (late replies after a timeout) are
  skipped. Payloads are checked before they're trusted (counts must fit the data), so an unknown format is
  rejected, not misread. A data-type mismatch, which the ST reportedly has, is logged once and the payload
  checks decide.
- Failing devices back off (5 s, growing to a minute). Unplugging and replugging is handled by a rescan every 15 s.

### Sharing with other software
- **Settings → USB coolers**, three choices:
  - **Read, except while other cooler software runs** (the default): Pulse stays off a cooler while iCUE
    (the app, or its `Corsair.Service` background service), SignalRGB, OpenRGB, FanControl, HWiNFO, AIDA64,
    Argus Monitor, liquidctl or CoolerControl is running, and starts again 10 s after they quit.
  - **Always read**: alongside them, with a warning that they may briefly lose the cooler.
  - **Don't read USB coolers.**
  The section lists each cooler that's plugged in and what Pulse is doing with it.
- **The cooling card explains a paused cooler** ("Corsair Commander ST isn't being read: left alone while
  iCUE is running...") instead of showing nothing.
- **No false "Pump stopped":** a cooler left alone on purpose is marked in the snapshot (`HardwareSnapshot.Unread`).
  The pump alert counts a vanished pump reading as a stopped pump, but not when Pulse chose not to read it.
  A pump that disappears for any other reason still alarms, as before.

### On the dashboard
- The Commander's channel 0 is the pump, the rest are fans: the pump and coolant go to the cooling card, the
  fans to the Fans card. (Before, every fan on any cooler counted as a pump. That rule is kept for coolers
  that don't name a pump.)
- A USB cooler's pump becomes the pump unless the user picked a motherboard header, so the pump alert, the
  tray and the mini window follow it. The header picker is hidden then.

### Probe (`tools/CorsairProbe`)
- `make probe`: lists supported coolers (path, serial, report sizes) and any cooler software running. Sends nothing.
- `make probe-read`: one read, every exchange in hex with its reply time (also saved to `artifacts/probe/exchanges.txt`, for test
  fixtures). Refuses while iCUE or similar (or Pulse) is running; `ARGS=--force` overrides.

## Tasks
- [x] Confirm the device model (Commander ST, `1B1C:0C32`, 64-byte reports, firmware 2.0.19).
- [x] Driver layer for several devices, Corsair Commander driver (read-only).
- [x] Reader integrated into the polling pipeline with throttling and recovery.
- [x] iCUE (and other cooler software) detection and behavior.
- [x] Tests against a simulated controller at 64 and 96 bytes, and liquidctl's documented reply layouts.
- [x] Run `make probe-read` on the real ST with iCUE fully closed, and save the exchanges as a test fixture: go.
- [ ] Compare the readings with iCUE (±1 °C, ±50 rpm).
- [ ] See it in Pulse itself: the cooling card with the Corsair pump and coolant, and pausing when iCUE starts.
- [ ] Optional: offer the driver to LibreHardwareMonitor upstream.

## Tests
299 passing (22 new): packets; replies parsed in liquidctl's documented layouts; malformed replies rejected;
a full read at 64 and 96 bytes (sleep first, wake, ..., sleep last; unplugged fans and probes left out);
later reads skipping firmware; sleep still sent after a timeout; the ST's data-type quirk; the pumpless XT;
the registry; recognizing iCUE's processes and other tools (and not, say, ASUS's fan service); the policy
(pause, resume after 10 s, always, off) and backoff; cooler readings joining snapshots and paused ones marked;
no pump alert for a pump left to iCUE, but still one for a pump that vanishes; the cooling card with a cooler
(pump and coolant there, fans in Fans, the pump role taken unless the user picked a header); and the card
explaining a paused cooler. Plus a byte-for-byte replay of the real Commander ST capture.

## Done when
- The CPU cooling card shows coolant temperature and pump rpm from the Corsair controller, matching
  iCUE within ±1 °C and ±50 rpm.
- Closing Pulse (or killing it) leaves the cooler running normally on its own curve.
