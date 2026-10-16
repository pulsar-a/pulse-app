# Step 13: Fan control (deferred)

**Goal, if ever:** safe, limited fan and pump curves from Pulse.

**Status:** deferred, possibly for good. Pulse reads; it doesn't control. This file records why, and what would
have to be true first.

## Why it's deferred
- **Cooling is safety-critical.** A wrong write can stop a pump or a fan. Reading badly shows a wrong number;
  writing badly can overheat a PC. Pulse runs on other people's machines, with hardware nobody here can test.
- **The vendors' software already does it**, and fights anyone else who tries. Step 11 showed how carefully
  Pulse has to share a controller with iCUE just to *read* it.
- **The protocols are fragile.** liquidctl marks Corsair's whole Commander family "broken" for fan control:
  curves fail on the Commander ST, and fans get stuck at 100% on the Core XT (issues #598, #705).
- **Motherboard fans** go through the Super I/O chip and PawnIO, where a wrong register can do more than set a speed.

## What would have to be true first
- Read support that has been solid on real users' machines for a long time (step 11), with alerts that
  catch a stopped pump (step 4).
- A failsafe that hands control back to the hardware: on exit, on a crash and when Pulse stops answering
  (a watchdog). Never leave a fan at a fixed low speed.
- Limits Pulse never crosses: a minimum pump speed, and full speed above a temperature, whatever the curve says.
- Testing on each controller it supports, not just the developer's PC.

## Builds on
- Step 4 (alerts): stopped pumps and high temperatures are already caught.
- Step 11 (USB coolers): the driver layer would gain write operations per device, off by default, device by device.
