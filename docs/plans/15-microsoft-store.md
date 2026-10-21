# Step 15: Microsoft Store

**Goal:** Pulse in the Microsoft Store, the full version (administrator rights, CPU temperatures through PawnIO),
signed by Microsoft for free, possibly as a paid listing while the GitHub version stays free.

## Why the MSIX route

- **The Store signs MSIX packages itself**, so no code-signing certificate is needed (an individual in the EU can't
  use Azure Artifact Signing, and CA certificates in one's own name cost roughly $130–300 a year; see step 14).
- The other route, listing our own Setup.exe, needs that certificate, and it's unclear whether the Store can sell
  an EXE-installer app (Microsoft's distribution guide says payments work for both routes; older answers from
  Microsoft staff say MSIX only).
- Registering as an individual developer is free since 2025. The Store keeps 15% of sales through its payments.

## The two hurdles, and how we ask

1. **Administrator rights.** A packaged app needs the restricted capability `allowElevation` to elevate. Microsoft
   approves restricted capabilities case by case, and reportedly rarely this one. Pulse already elevates itself
   (`Elevation.cs`, manifest `asInvoker`), which is what the capability allows.
2. **PawnIO.** Store policy 10.2.4: "Generally, dependency on non-Microsoft provided drivers or NT services is not
   allowed but may be considered case by case for WHCP certified drivers." Pulse doesn't install PawnIO and works
   without it (GPU, memory, drives, network, power supply, Corsair coolers, processes); CPU temperatures and
   motherboard fans need it. The dependency must be disclosed at the start of the description and in the
   certification notes. Whether PawnIO counts as "WHCP certified" is worth asking its author.

Plan B, if either is refused: a Store edition that runs without administrator rights (no CPU temperatures or drive
health), pointing to the GitHub version for the full picture.

## What was built

- **One build for both.** `AppPackage.IsPackaged` (the package API) tells Pulse where it runs. In the package:
  - The startup task and elevated relaunches go through the `pulse.exe` **execution alias**, since a copy started
    straight from the WindowsApps folder has no package identity (and would see a different data folder).
  - Toasts use the package's identity instead of one Pulse registers.
  - Pulse's updater is off: Settings → About says the Store keeps it up to date.
- **`make msix`** (`tools/msix.ps1`): publishes Pulse self-contained (the Store can't install the .NET runtime
  first), lays it out with `packaging/msix/AppxManifest.xml` and the logos, and packs it with makeappx from the
  Windows SDK. Version from the latest release tag (`v0.2.0` → `0.2.0.0`; the Store wants four numbers, the last
  one 0). The first build: 85 MB, and makeappx's manifest check passed.
- **The identity** (`Name`, `Publisher`, display names) comes from `packaging/msix/identity.json`, which holds
  local-test values until it's filled from Partner Center.
- **Logos:** `make icon` now also renders the package's logos (44, 150, 310×150 and 50 px, plus the taskbar's
  exact sizes, plain and unplated) from the same drawing as the app icon.
- **`PRIVACY.md`**, the privacy policy the Store requires: Pulse collects and sends nothing; history stays on the
  PC; the phone view only when turned on, local network only.
- Tests: unpackaged Pulse launches itself from its exe; the manifest declares runFullTrust, allowElevation, the
  alias and the exe. 366 passing.

## Known limitation
- An MSIX package can't run code when it's uninstalled, so a "Start with Windows" task and the web view's firewall
  rule stay behind after uninstalling the Store version (the task then points at a missing alias and does
  nothing). Settings could offer to remove both before uninstalling; or the Store version could use the package's
  own startup entry (removed automatically, but without admin rights at sign-in, so a UAC prompt every time).

## Steps

### 1. Try it on this PC (needs you)
1. Settings → System → For developers → **Developer Mode** on (needed for unsigned packages; turn it off again after).
2. Exit Pulse, then in PowerShell: `Add-AppxPackage -Register artifacts\msix\layout\AppxManifest.xml`
3. Start Pulse from the Start menu. Check: the UAC prompt and full sensors; Settings → About says "Installed from the
   Microsoft Store"; a test alert's toast; "Start with Windows" (a task pointing at `pulse.exe` in WindowsApps);
   that a second launch focuses the window.
4. Remove it: `Get-AppxPackage *Pulse.LocalTest* | Remove-AppxPackage`.

### 2. Partner Center (needs you)
1. Sign up at [storedeveloper.microsoft.com](https://storedeveloper.microsoft.com) as an individual (free; ID check).
2. Reserve the name. "Pulse" alone is likely taken; for example "Pulse Hardware Monitor".
3. Product identity → copy `Package/Identity/Name`, `Publisher` and `PublisherDisplayName` into `identity.json`, then
   `make msix` again.
4. Submission: price (or free), category Utilities & tools, the screenshots in `docs/images`, the privacy policy URL
   (`https://github.com/pulsar-a/pulse-app/blob/main/PRIVACY.md`), and the texts below.

### Texts for the submission

**Description, first lines (policy 10.2.4 wants the dependency disclosed first):**
> CPU temperatures, clocks and power, and motherboard fans, need the free PawnIO driver (pawnio.eu), which Pulse
> links to but doesn't install. Everything else works without it: graphics card, memory, drives, network, power
> supply, Corsair coolers and processes.

**Restricted capability `allowElevation`:**
> Pulse is a hardware monitor. Windows only allows programs running as administrator to read CPU temperatures,
> motherboard sensor chips and drive health (SMART), so Pulse asks for administrator rights with a standard UAC
> prompt when it starts. If the user declines, it keeps running with the sensors available without them. It never
> elevates silently, and it only reads: it doesn't change fan speeds, voltages, clocks or any system setting.

**Restricted capability `runFullTrust`:**
> Pulse is a WPF desktop application (.NET 10). It reads hardware sensors through the open-source
> LibreHardwareMonitor library and USB HID devices, which the app container doesn't allow.

**Certification notes:**
> Pulse reads hardware sensors and changes nothing. It optionally uses the third-party PawnIO driver
> (https://pawnio.eu, open source, signed) for CPU and motherboard sensors; Pulse doesn't install it and works
> without it, showing a banner that links to it. Testing: start Pulse, accept the UAC prompt; the dashboard fills
> within seconds. Without PawnIO the Processor card shows load instead of temperature, by design. The optional
> phone view (Settings → Phone and web view, off by default) serves a read-only page on the local network only.
> Source: https://github.com/pulsar-a/pulse-app

## Done when
- [ ] The package installs and works on this PC (step 1 above).
- [ ] Submitted; the result of the allowElevation and PawnIO review is written here.
