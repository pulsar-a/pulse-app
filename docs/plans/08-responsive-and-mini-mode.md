# Step 8: Responsive layout and mini mode ✅

**Goal:** the dashboard looks right at any window size, and a compact always-on-top mini window shows
chosen sensors while gaming or working.

## Why now

Both are views over the tile list from step 7. Doing this earlier would mean reflowing a hard-wired
layout and then redoing it once tiles exist.

## Design

### Responsive dashboard
- A custom panel that lays out tiles in a column grid: 4 columns above about 1400 px, 3 above 1100,
  2 above 760, 1 below. Tile sizes map to column spans.
- Lower the window's minimum width from 1120 px to about 480 px.
- Card internals adapt: at narrow widths the ring shrinks and stat rows move under it; the core strip
  wraps into two rows.
- Animate tile movement when the column count changes.

### Mini mode
- A separate small window (`MiniWindow`): borderless, rounded, always on top, draggable, with
  opacity (40–100%) and click-through options.
- Shows a vertical or horizontal list of tiles chosen in Settings → Mini mode, defaulting to CPU
  temperature, GPU temperature, pump speed and system power.
- Opened from a header button, the tray menu (step 3) and a global hotkey (default Ctrl+Shift+P).
- Remembers position per monitor. Hidden while the main window is focused, if the user wants.

## What was built

### Responsive dashboard
- **By card size, not fixed breakpoints.** Each card kind has a minimum width: ring gauges 250 px,
  core strip 360, cooling 320, fans 260, drives 330, network 280, a pinned sensor 220 (a group 300).
  When a tile's columns would be narrower than that, it takes more columns (`TileFlowPanel.Plan`), and
  rows still stretch to the full width. At the default layout that gives four gauges per row from about
  1000 px, three below that, two around 700 px and one at 480 px, with every card at least its minimum.
  Custom layouts reflow the same way.
- **Core strip** (`StripPanel`): one row of bars while they're at least 30 px wide, otherwise two or
  more even rows (24 cores → 2 × 12 in an 800 px window).
- **Window:** minimum width 1120 → 480 px (height 680 → 520).
  - Under 940 px the header buttons move below the machine name.
  - Under 640 px the page buttons show icons only (with tooltips) and the side margins shrink.
  - The details panel and the Ctrl+K search never get wider than the window.
- **Pages:**
  - All sensors drops the Min and Max columns under 680 px, so names keep their room; the search box
    shrinks instead of overlapping the sensor count.
  - Settings fills the width up to 820 px instead of having a 560 px minimum.
  - The alert rows ("Above [95] °C for [10] seconds") wrap onto two lines.
- Tile moves when the layout changes width use the step 7 glide only in edit mode; normal resizing
  reflows straight away (animating every resize step would make dragging the window edge sluggish).

### Mini window (`Views/MiniWindow`, `Views/MiniController`, `ViewModels/MiniViewModel`)
- **The window:** borderless and rounded, always on top, and deliberately plain for games (no
  animations or effects, values change once per reading).
  - It never takes focus, so clicking it doesn't pull you out of a game. It isn't in the taskbar or
    Alt+Tab.
  - Pulse puts it back on top every 5 seconds, since some games push themselves in front.
- **Shows** CPU temperature, GPU temperature, pump speed and system power by default. Those four are
  worked out from each reading by the mini window itself, so they work while the main window (and its
  dashboard) is hidden in the tray. The pump is the header picked in the cooling card, or a cooler's fan
  or a header named like a pump; power falls back to CPU package power without a PSU. Any sensor can be
  added in Settings → Mini window (through the Ctrl+K search), reordered or removed.
- **Layout:** stacked (label left, value right) or side by side (label over value).
- **Opacity:** 40–100%.
- **Click-through:** clicks reach the window underneath. Turned on from its menu or Settings, off from
  Settings or the tray menu ("Make the mini window clickable"), since its own menu can't be reached then.
- **Drag** it anywhere. Its position is saved **per monitor** (in physical pixels): after unplugging an
  external screen it opens top-right on the main screen, and plugging it back in restores its spot
  there. A saved spot off the edge is pulled back on screen.
- **Hidden while Pulse's own window is in front:** optional and off by default, as the plan said.
  Opening it yourself always shows it, even with this on, until Pulse's window next comes to the front.
- **Opened from:**
  - the header button
  - the tray menu (with the shortcut shown)
  - its own right-click menu (layout, opacity, click-through, settings, close)
  - a **global shortcut**
- It reopens at the next start if it was open.
- **Shortcut:** **Ctrl+Alt+P** by default, not the plan's Ctrl+Shift+P. A global shortcut takes the
  keys from every app, and Ctrl+Shift+P is VS Code's command palette and the browsers' private window.
  - Any shortcut can be set: Settings shows the current one, **Set** listens for the next keys pressed
    (the box shows what's held so far), **Reset** goes back to Ctrl + Alt + P. While listening, Esc
    cancels and Backspace turns the shortcut off.
  - It needs Ctrl, Alt or Win; Shift alone with a key would take capital letters away from typing.
  - While listening, the current shortcut is released, so pressing it again is recorded rather than
    swallowed. Leaving the page cancels.
  - If another app already holds it, Settings says so.
  - It's heard through a hidden message-only window, so it works while Pulse is only in the tray.
- While the mini window is showing, Pulse keeps reading at the normal rate even with the main window
  hidden (otherwise it slows to every 2 seconds when hidden).

## Fixes after first use
- **The mini window button did nothing.** The first build had "hide while Pulse is in front" on by
  default, so the button in Pulse's own window opened the mini window and hid it at once. The option is
  now off by default. Settings saved by that build are switched back once (settings schema 3), and an
  explicit open always shows it.
- **"Couldn't read sensors: … A device which does not exist was specified."** The Corsair PSU's USB
  connection dropped for a moment (it's shared with iCUE). The read threw, and the exception ended the
  polling loop, so every sensor stopped updating. Now (`DeviceHealth`, `HardwareService`):
  - each device is read on its own, and a failing one is skipped while every other device carries on
  - the failing device is retried after 1, 2, 4… up to 30 seconds
  - after three failures in a row its group is closed and reopened (PSU, USB coolers, drives, network,
    battery; never CPU, motherboard or memory), at most every 30 seconds
  - if it's still gone after reopening, Pulse keeps trying every 30 seconds until it's back
  - while it's out, its readings hold their last values for 10 seconds (step 6's `ReadingHold`), then
    show as missing
  - the log gets the first failure and then a count every 5 minutes, not a line a second
  - a failure listing drive letters no longer stops polling either

- **Mini window settings were laggy, the opacity slider worst; the log showed the UI frozen for 12 and 8
  seconds.**
  - Cause: the polling thread held a lock for the whole hardware read, including the PSU's USB reads
    (about 45 ms normally, seconds when it's fighting with iCUE). Every settings change made the UI
    thread take that lock, through the mini window telling polling to stay at full speed, so each slider
    step waited for a hardware read to finish. The ~0.8 s freezes when the mini window opened had the
    same cause.
  - Now the lock only covers choosing what to read; the reads happen without it.
  - Settings changes that don't concern them no longer touch polling, the mini window's window styles,
    or the tray icon (which used to redraw itself and talk to Explorer on every change).

## Tests
196 passing (19 new):
- **Layout:** the default layout at 1312 px is unchanged (4, 2, 3 per row); gauges reflow 3, 2, then 1
  per row; across 432–1900 px no tile is ever narrower than its minimum and every row fills the width;
  the core strip wraps evenly.
- **Mini window placement:** first opening goes top-right on the main screen; the saved spot comes back
  on the same monitor, falls back when that monitor is gone, and is pulled back on screen.
- **Shortcuts:** parsing, refusing a bare key, every offered choice readable.
- **Mini readouts:** the defaults on a real-looking PC; the fallbacks without a PSU or pump; chosen
  sensors with custom names and °F; layout and opacity following settings (never below 40%).
- **Settings file:** computed values stay out of settings.json. This also fixed step 7's tile kind and
  size helpers being written into the file.

After the fixes: 203 passing (7 more):
- a device that throws is skipped while the others still read, and is left without values for the hold
  to bridge
- the growing pause and its 30-second cap
- reopening after three failures, at most every 30 seconds
- a good read clearing the record
- the one-time switch-off of "hide while Pulse is in front", and a later deliberate choice being kept

After the shortcut recorder: 208 passing (5 more): recorded keys become a shortcut that displays and
registers as what was pressed (digits, F-keys, Win, numpad); waiting while only modifiers are held;
refusing Shift alone or no modifier; and the Set → keys / Esc / Backspace / Reset flow on the Settings
page.

Rendered offscreen at 480, 800, 1200 and 1920 px:
- At 480 px: the dashboard, All sensors and the Settings sections.
- The mini window stacked and side by side.

The renders caught two narrow-width problems, both fixed: the sensor count overlapping the search box,
and the alert rows running past the card edge.

## Done when
- [x] Resizing the window never clips or overlaps content (rendered at 480/800/1200/1920 px; the tile
  layout is checked at every width from 432 to 1900 px in steps of 7).
- [ ] Display scaling at 150% and 200% hasn't been checked: offscreen rendering is at 100%. Worth a look
  on a real screen.
- [ ] The mini window over a borderless game and its cost in FPS: to check on the real machine. It's
  built to be cheap (one small window, text updates once a second, no effects), but that's still a
  claim until measured in a game.
