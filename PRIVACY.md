# Privacy policy

*Pulse, the hardware monitor for Windows. Last updated 16 October 2026.*

**Short version: Pulse doesn't collect, send or share anything about you.** Everything it records stays on your PC.

## What Pulse reads

To show your PC's state, Pulse reads hardware sensors (temperatures, fan and pump speeds, power, load, clocks,
drive health and space, network throughput) and, on its Processes page, which programs are running and how much
CPU, GPU and memory they use. It reads these only to show them to you.

## What Pulse stores, and where

- **Settings** (your choices, sensor names you gave, alert rules) and **sensor history** (each sensor's lowest, average
  and highest value per minute, kept for a week by default, adjustable in Settings → History) are saved in `%LOCALAPPDATA%\Pulse` on
  your PC, or in the app's own data folder when installed from the Microsoft Store.
- **A log file** of what Pulse did (for troubleshooting) is kept there too.
- Nothing of this leaves your PC.

## What goes over the network

- **No accounts, analytics, ads or telemetry.** Pulse never sends your readings, settings or history anywhere.
- **Update checks** (the version from GitHub only, on by default): once a day Pulse asks GitHub whether a newer version exists and,
  if so, downloads it. GitHub sees the request like any web request (your IP address); Pulse sends nothing else.
  You can turn automatic updates off in Settings → About. The Microsoft Store version doesn't do this: the Store
  updates it.
- **The phone and web view**, only if you turn it on in Settings: Pulse then serves a read-only page with your
  sensor readings to devices on your local network that have your private link. It refuses connections from
  outside your local network, and you can replace the link at any time to shut out every device that had it.
- **Links you click** (for example to the PawnIO driver's website) open in your browser.

## Diagnostics

**Settings → About → Copy diagnostics** creates a report for a bug report, but only when you ask, and only you
decide where it goes. It leaves out your web view link, user name, computer name and readings, and you see all of
it before it's copied.

## Children

Pulse isn't directed at children and collects no personal information from anyone.

## Changes and contact

Changes to this policy are published here, in the project's repository. Questions:
[open an issue](https://github.com/pulsar-a/pulse-app/issues).
