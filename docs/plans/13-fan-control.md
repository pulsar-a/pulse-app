# Step 13: Fan control (deferred)

**Goal:** possibly, in the future, let users set fan curves from Pulse. **This step is deliberately
last and may never ship.** A bug here can reduce cooling, which is a much worse failure than a wrong
number on screen.

## Why last

- It needs alerts (step 4) as a safety net, so a mistake is noticed immediately.
- The pump on this machine is driven by the Corsair controller (step 11), not by the motherboard, and
  dedicated tools (for example FanControl, which also uses LibreHardwareMonitor) already do this well.
- Writing to the Super I/O chip conflicts with BIOS curves and with ASUS Armoury Crate's fan service,
  which runs on this machine.

## Preconditions before starting
- Steps 4 and 11 are done and stable.
- A clear user need that existing tools don't cover.

## Safety rules (non-negotiable)
- **Never control the pump** from Pulse, whatever header it's on.
- Minimum duty floor (for example 30%) that can't be lowered from the UI.
- If CPU or GPU temperature passes a critical threshold, or a sensor reading goes missing, immediately
  return every controlled fan to its default (BIOS) control.
- When Pulse exits or crashes, fans return to default control. LHM's `IControl.SetDefault()` is the
  mechanism; add a watchdog process if needed.
- Off by default; enabling it shows a clear warning.

## Sketch
- Curves: temperature source (any sensor) → duty points with hysteresis and smoothing.
- UI: a curve editor in the details panel of a fan.
- Uses LHM `ISensor.Control` (`IControl.SetSoftware(value)` / `SetDefault()`).

## Done when
- Not before the preconditions are met. Decide go or no-go at that time and record it here.
