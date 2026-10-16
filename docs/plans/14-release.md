# Step 14: Release and distribution

**Goal:** someone who has never heard of Pulse can download it, install it without scary warnings, get it
working (admin rights, PawnIO), receive updates, uninstall it without leftovers, and send a useful report
when something's wrong on their machine.

## Why now

Every feature step is built (13, fan control, is deliberately deferred). Until now Pulse has run from a build
folder on one PC. Most of what's left between that and other people using it isn't features: it's packaging,
trust and support. Some of it also changes code that exists (the data folder, the startup task, uninstall
hooks), so it's best done before more features pile on top.

## What Pulse already does to a system

These are what install, update and uninstall have to handle:

| What | Where | Written by |
|------|-------|------------|
| Settings, history database, log, toast icon | `%LOCALAPPDATA%\Pulse\` | `SettingsService.DefaultFolder` and everything using it |
| "Start with Windows" | Scheduled task `Pulse`, highest privileges, pointing at `Pulse.exe` | `StartupTask` (repaired at start if the exe moved) |
| Toast identity | `HKCU\Software\Classes\AppUserModelId\Pulse.HardwareMonitor` | `Notifier` |
| Tray icon kept visible | `HKCU\Control Panel\NotifyIconSettings\…` (`IsPromoted`) | `TrayPromotion` |
| Web view firewall rule | Windows Firewall rule "Pulse web view" (only if the user clicked) | `WebNetwork` |
| Elevation | `app.manifest`: `highestAvailable` (admins get the UAC prompt; standard users run with fewer sensors) | — |

## Design

The parts come in this order, so each builds on the one before.

### 1. Repository and CI
- The repository exists (`github.com/pulsar-a/pulse-app`, `main` pushed); it has no license and no CI yet.
- **License** (your decision, below), plus `THIRD-PARTY-NOTICES.md` for what Pulse ships:
  - LibreHardwareMonitorLib (MPL-2.0), HidSharp (Apache-2.0), WPF-UI (MIT), CommunityToolkit.Mvvm (MIT),
    H.NotifyIcon (MIT), Microsoft.Data.Sqlite (MIT), QRCoder (MIT).
  - PawnIO isn't shipped, only linked to (see 4).
  - The Corsair driver is an independent implementation of the protocol liquidctl documents; no GPL code is used.
    The notices say so.
- **CI** (GitHub Actions, `windows-latest`) on every push and pull request: build, then `dotnet test`.
  - The tests already suit a runner: real-machine tests (processes, GPU counters, desktop windows, the
    localhost web server) cope with what a VM has, and the admin-only fixture test skips itself.
  - Check: the GPU counters test on a runner without a GPU, and HttpListener on localhost there.
- **Versioning:** the version comes from the git tag (`v0.2.0` → 0.2.0), not hand-edited in the csproj. Settings → About
  already shows it.
- **Release workflow** on a `v*` tag: build, test, package (3), sign (6), and publish a GitHub Release with
  generated notes.

### 2. Copy diagnostics
First, because it pays off as soon as anyone else runs Pulse, even from a zip.
- Settings → About → **Copy diagnostics** (and a tray menu item), making a single text report:
  - Pulse's version, Windows build, admin or not, PawnIO installed or not, the render tier, and display scaling.
  - Every hardware device LHM found, with its type and identifier, and how many sensors it has. Plus the USB
    coolers with their state (`CoolerService.Statuses`), and drives found against what Windows reports.
  - The last 500 lines of `pulse.log`.
  - Settings, with the **web token removed**.
- **Privacy, as a rule:**
  - Replace the user profile path with `%USERPROFILE%`. Leave out the machine name, sensor values and history
    (temperatures say nothing useful for a bug report; hardware names do).
  - Show the report before copying (a preview dialog with Copy, and Save as file), so the user sees exactly
    what they share.
- Tests: redaction (token, profile path, machine name), and the report built from the admin and non-admin fixtures.

### 3. Installer and automatic updates
- **Velopack** (MIT): a one-click per-user installer (`Setup.exe`), delta updates, releases served straight
  from GitHub Releases (`GithubSource`), and update checks in-process. It's built for .NET desktop apps and needs
  no admin rights to install.
- **Runtime:** framework-dependent, with Velopack installing the .NET 10 Desktop Runtime as a prerequisite if it's
  missing (keeps updates small; the web view's choice of HttpListener means the Desktop runtime is enough).
  Self-contained is the fallback if prerequisites cause trouble (about 70 MB more per install, but deltas stay small).
- **Updates in the app:**
  - Settings → About gets "Check for updates" and "Install updates automatically" (on by default; checked at
    start and once a day).
  - An update downloads in the background and applies on the next start, or straight away from the button.
  - Never while a critical alert is firing.
- **Spikes first** (each could change the plan):
  - **Data folder collision.** Velopack installs to `%LOCALAPPDATA%\<packId>\`, and Pulse's data lives in
    `%LOCALAPPDATA%\Pulse\`. With packId "Pulse", installing would mix the app into the data folder, and
    **uninstalling would delete settings and history without asking**. Fix one way or the other:
    - packId `PulseApp` (the app goes to `%LOCALAPPDATA%\PulseApp\`), or
    - move the data to `%LOCALAPPDATA%\Pulse\data\` with a one-time migration.
    The first is simpler; decide in the spike.
  - **Restarting after an update, elevated.** With `highestAvailable`, Windows refuses to start Pulse from a
    non-elevated process (error 740) for admin users. Check how Velopack's restart launches the app; if it can't
    trigger the UAC prompt, restart through the scheduled task or ShellExecute.
  - **The startup task's path.** It must point at Velopack's stable `current\Pulse.exe`, not a versioned folder.
    `StartupTask.RepairPathAsync` already fixes a moved exe at start; confirm it survives an update.
  - **Toasts and the tray icon after install.** Check the toast identity and the Start menu shortcut Velopack
    creates. Windows also treats a new exe path as a new tray icon; check `TrayPromotion` still keeps it visible.
- The zip stays available for people who want a portable copy (with a note that it doesn't update itself).

### 4. First-run setup
Shown once, on the first start after install, and reachable later from Settings → About:
- **What Pulse is and isn't:** it reads sensors, never changes fan speeds, voltages or clocks, and sends nothing
  over the internet. The web view is off unless turned on; update checks go to GitHub.
- **Administrator rights:** what they unlock (CPU, motherboard, drives) and why Windows asks every start.
  Offer "Start with Windows", which starts Pulse without the prompt.
- **PawnIO:** whether it's installed and what it adds (CPU temperatures and power, motherboard fans). If it's
  missing, a button opens pawnio.eu. Check PawnIO's terms before considering bundling or a silent install;
  until then Pulse only links to it, like the existing banner. After installing, Pulse offers to restart itself.
- **The pump:** if the motherboard doesn't name its fan headers, point to the cooling card's header picker.
  If a supported USB cooler is managed by iCUE, say what "Read, except while iCUE runs" means.
- Keep it to two or three short pages; every page can be skipped.

### 5. Clean uninstall
Velopack runs the app with an uninstall hook (`VelopackApp.OnBeforeUninstallFastCallback`) before removing the
files. There, Pulse:
- deletes the `Pulse` scheduled task,
- removes the "Pulse web view" firewall rule if it exists,
- removes its toast identity key and its tray-promotion entry,
- and the data folder: **ask** (keep settings and history, the default, or remove everything). The hook can't
  show UI reliably, so the choice is set beforehand in Settings → About → "When uninstalling: keep my settings
  and history", read during uninstall.
- **Spike:** the scheduled task (created with highest privileges) and the firewall rule need admin rights to remove,
  and the uninstall hook runs in Velopack's unelevated context. Options: Pulse elevates for that step only
  (one UAC prompt during uninstall), or the task is created so the user can delete it unelevated. Decide in the spike.
- A manual fallback in the README: what to delete if the uninstaller couldn't.

### 6. Code signing
Unsigned, Windows SmartScreen warns on download and on the first run of every new version, and an unsigned app
asking for admin rights looks exactly like malware. The options (check current prices and eligibility before choosing):
- **SignPath Foundation:** free code signing for open-source projects, through their CI integration; requires a
  public repository and an OSI license, and they review the project.
- **Azure Trusted Signing (Microsoft):** a low monthly fee, signing from CI, but identity validation for
  individuals has been limited to some countries.
- **A standard (OV) certificate from a CA:** a yearly fee, and since 2023 the key must live on a hardware token or a
  cloud HSM, which complicates CI.
- EV certificates no longer skip SmartScreen's reputation building, so they aren't worth their extra cost here.
- Sign `Pulse.exe`, `Setup.exe` and the update packages (Velopack signs during `vpk pack` with a sign command).
  Reputation still builds over the first downloads; signing makes that per publisher, not per file.

## Progress

### CI and versioning (done)
- `.github/workflows/ci.yml`: on pushes to `main` and on pull requests, `windows-latest` restores, builds the
  solution in Release, runs the tests, and uploads the results (TRX) even when they fail. Full history is fetched
  for the version. A newer push cancels a running build for the same branch.
- The tests were checked for anything tied to this PC: the only system change is a temporary test key under
  `HKCU\Software\PulseTests`, which is removed again; nothing creates tasks, firewall rules or real toasts.
- MinVer sets the version from `v*` tags. It's a build-time package only, nothing is shipped. Settings → About shows it.

### Copy diagnostics (done)
- `Services/Diagnostics.cs` builds the report:
  - Pulse's version, Windows (name, feature update and build from the registry), admin, PawnIO, rendering tier,
    display scale, theme, culture and update interval.
  - Every device with its identifier and how many of its sensors answered (sub-hardware indented), then totals.
  - The USB coolers and their state; the settings with the web token removed; the last 500 lines of the log.
- Redaction runs over the whole report: the token wherever it appears (the log can hold a link), the profile
  folder as `%USERPROFILE%`, then the user and computer names as whole words (names under 3 letters are left,
  so "al" doesn't blank out "all"). No readings or history are included.
- `Views/DiagnosticsWindow` shows the report first, with Copy, Save as file and Close. Opened from Settings →
  About → "Copy diagnostics…" and from the tray menu.
- Checked on this PC: a real report has no trace of the user or computer name.
- Tests (7): what the report contains; nothing private left, from settings and from log lines; short names
  not over-redacted; settings losing only the token; reading the tail of a log being written; a report before
  the first reading; the Windows version line. 338 passing.

## Decisions for you
- **License:** MIT (simplest, most permissive) or GPL-3.0 (keeps forks open; also lets the Corsair driver be
  offered to LHM, which is MPL-2.0, either way). Needed before the repository is public.
- **Public repository?** Needed for SignPath's free signing and for GitHub Releases to serve updates without a token.
- **Signing route:** one of the three above.
- **Data folder fix:** `PulseApp` packId or moving the data (the spike decides if you don't mind which).

## Risks
- **Elevation and updates:** `highestAvailable` and per-user installers don't fit together naturally (spikes in 3 and 5).
- **SmartScreen after signing:** new publishers still see warnings until reputation builds. Say so on the download page.
- **PawnIO is a kernel driver from a third party:** Pulse must never look like it installs drivers behind the
  user's back. Link, explain, and let them choose.
- **Antivirus false positives:** hardware monitors talk to drivers and read other processes; some antivirus
  products flag that. Signing helps. Keep a short "if your antivirus complains" note, and submit false positives
  to the vendors.

## Tasks
- [ ] License, `THIRD-PARTY-NOTICES.md`, README install section (your decisions first).
- [x] CI: build and test on push and pull request (`.github/workflows/ci.yml`). Not run yet: it starts with the next push.
- [x] Version from the git tag (MinVer): between tags it's `0.1.0-alpha.0.<commits>`; tag `v0.2.0` makes it 0.2.0.
- [ ] Release workflow publishing a GitHub Release.
- [x] Copy diagnostics with redaction and a preview, from Settings → About and the tray menu.
- [ ] Spikes: data folder, elevated restart after update, startup task path, toasts and tray after install,
      uninstall rights.
- [ ] Velopack packaging in the release workflow; in-app update check and install.
- [ ] First-run setup pages.
- [ ] Uninstall hook: task, firewall rule, registry entries, data folder by the user's choice.
- [ ] Code signing in the release workflow.
- [ ] Tests: diagnostics redaction; the update policy (no update during a critical alert, daily check); uninstall
      cleanup against a fake system (task, rule and keys behind an interface).

## Done when
- On a clean Windows 11 VM: downloading `Setup.exe` from GitHub Releases and running it installs Pulse without
  a SmartScreen block (signed), with no runtime to install by hand.
- The first-run pages explain admin rights and PawnIO, and Pulse works fully after following them.
- Publishing `v0.x.1` updates the installed copy on its own; settings, history, "Start with Windows" and the
  tray icon survive the update.
- Uninstalling leaves no scheduled task, firewall rule or registry entry behind, and keeps or removes the data
  as chosen.
- "Copy diagnostics" on the VM gives a report that would let someone else debug a missing sensor, with no token,
  user name or machine name in it.
