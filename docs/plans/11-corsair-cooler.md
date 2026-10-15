# Step 11: Corsair cooler over USB

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

## Tasks
- [ ] Confirm the device model and firmware.
- [ ] Probe tool and go/no-go decision, written into this file.
- [ ] Reader integrated into the polling pipeline with throttling and recovery.
- [ ] iCUE detection and behavior.
- [ ] Tests: packet parsing against captured responses.

## Done when
- The CPU cooling card shows coolant temperature and pump rpm from the Corsair controller, matching
  iCUE within ±1 °C and ±50 rpm.
- Closing Pulse (or killing it) leaves the cooler running normally on its own curve.
