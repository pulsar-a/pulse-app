# Step 8: Responsive layout and mini mode

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

## Tasks
- [ ] Responsive tile panel with breakpoints; lower minimum window size.
- [ ] Compact variants of card templates.
- [ ] Mini window with tile selection, opacity, click-through, position memory.
- [ ] Global hotkey and tray entry.
- [ ] Manual test pass at 480, 800, 1200 and 1920 px widths, at 100%, 150% and 200% display scaling.

## Done when
- Resizing the window never clips or overlaps content.
- The mini window stays on top of a borderless game window and costs no noticeable FPS.
