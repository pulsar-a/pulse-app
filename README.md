# Pulse

A good-looking hardware monitor for Windows: live gauges, per-core temperatures, fans,
drives, network and power. Pulse is built with WPF on .NET 10, styled with
[WPF-UI](https://github.com/lepoco/wpfui), and reads sensors through
[LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor).

## Requirements

- Windows 10 or 11 (x64)
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- **Administrator rights** to read CPU, motherboard and drive sensors
- **[PawnIO](https://pawnio.eu)** driver for CPU temperatures, clocks and power,
  and for motherboard fans and voltages. Without it you still get GPU, memory,
  network, PSU and drive data. Pulse shows a banner if it's missing.

## Run it

### JetBrains Rider

1. Start Rider **as administrator**. The app asks for admin rights, and Rider can't
   launch it otherwise ("Process creation exception", Windows error 740).
2. Open `Pulse.slnx`.
3. Pick the **Pulse** run configuration and press Run or Debug.

### Command line

```powershell
make start
```

This builds Pulse and launches it, and Windows shows the admin prompt. With MinGW the
command is `mingw32-make start`. `make build` and `make clean` are also available.

To run it without `make`, use an elevated terminal:

```powershell
dotnet run --project src/Pulse
```

## Roadmap

The development plan lives in [docs/plans](docs/plans/README.md). It's split into steps that can be
built one at a time, ordered so each step builds on the previous ones instead of reworking them.

## Project layout

```
src/Pulse/
  Services/HardwareService.cs   Polls LHM on a background thread and publishes immutable snapshots
  Services/SensorFormat.cs      Units and number formatting per sensor type
  Models/HardwareSnapshot.cs    Snapshot records handed to the UI thread
  ViewModels/                   Dashboard, All sensors and main window state (CommunityToolkit.Mvvm)
  Views/                        XAML for the Dashboard and All sensors pages
  Controls/                     RingGauge, Sparkline and the color palette
  Themes/Styles.xaml            Card, text and navigation styles
```

All LibreHardwareMonitor calls happen in `HardwareService` on one background thread. Each
tick it copies every sensor into a `HardwareSnapshot`, and the view models read only from
that snapshot on the UI thread.
