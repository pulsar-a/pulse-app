# Step 7: Customizable dashboard ✅

**Goal:** let users shape the dashboard: pin any sensor as a tile, reorder cards, and hide the ones
they don't need.

## Why now

Pinned tiles are named through the catalog (step 1) and open the details panel (step 6). The layout
model built here is what step 8 reflows into responsive and mini layouts, so it has to exist first.

## Design

### Layout model
- Replace the hard-wired XAML layout in `DashboardView.xaml` with a list of **tiles** described in
  settings (`Settings.Dashboard.Tiles`): tile kind, size (small, medium, wide), and options.
- Tile kinds: the existing cards (Processor, Graphics, Memory, System power, Core strip, CPU cooling,
  Fans, Drives, Network), plus new **Sensor tile** (one pinned sensor: value, sparkline, min/max),
  **Group tile** (a few pinned sensors in one card).
- Each tile kind has its own view model and `DataTemplate`, which means splitting today's
  `DashboardViewModel` into smaller per-card view models. That refactor is the main work of this step
  and keeps later steps simple.
- Default layout = today's dashboard, so nothing changes for existing users.

### Editing
- An "Edit dashboard" button switches to edit mode: tiles get drag handles, a hide button and a size
  picker; a "+" tile adds hidden cards back or picks a sensor to pin.
- Drag and drop reorders tiles, with animated reflow.
- Pin from elsewhere: the details panel "Pin" action and right-click → "Pin to dashboard" in All sensors.
- "Reset layout" in Settings.

## What was built

### Layout model (`Services/DashboardLayout.cs`)
- `Settings.Dashboard.Tiles`: a list of tiles (id, kind, size, hidden, and for pinned tiles their sensors
  and an optional title). **Null means the default layout**, so users who never edit keep getting
  Pulse's default as it improves.
- Kind and size are saved as text and read leniently: a settings file with a tile kind this version
  doesn't know (say, from a newer Pulse) drops that tile instead of failing to load the whole file.
  Loading also repairs duplicates, empty pinned tiles and sizes a card can't take, and adds built-in
  cards that are missing.
- Sizes on a 12-column grid: S (3 columns), M (4), L (6), XL (8), Full (12). Each card offers only the
  sizes that suit it (a ring gauge never goes below S, the core strip never below L).
- All edits (move, hide, resize, pin, unpin, remove, reset) are pure functions on that list, applied
  through settings. The dashboard re-syncs from settings on every change, so pinning from the details
  panel or resetting in Settings updates the dashboard the same way an edit on the dashboard does.

### Tiles (`ViewModels/DashboardTiles.cs`, `Views/DashboardView.xaml`)
- `DashboardViewModel` now exposes `Tiles` (visible, in order) and `HiddenTiles`. Each tile wraps a card
  view model; the core strip, fans and drives got their own card view models (`CoresCardViewModel`,
  `FansCardViewModel`, `DrivesCardViewModel`) next to the existing gauge, cooling and network ones.
  The update logic stays in `DashboardViewModel`; the hard-wired XAML grid is gone.
- Every card is a `DataTemplate` chosen by its view model's type, so tiles can come in any order.
- **Default layout = the old dashboard:** four gauges, then cores (XL) and cooling (M), then fans,
  drives and network (M each). The one visible difference: drives used to be a little wider than fans
  and network, now all three are equal.
- **`TileFlowPanel`** (`Controls/`): lays tiles out left to right on the 12 columns, wrapping when the
  next tile doesn't fit. A row that isn't full is stretched so its tiles fill the width (hide Memory and
  the other three gauges widen instead of leaving a hole). Tiles in a row share its height. In edit mode
  tiles glide to their new place **and size** together (280 ms, re-arranged each frame only while
  something is moving), so a resize grows smoothly while the neighbours slide aside. Step 8 can change
  the column count for narrow windows.

### Pinned sensors
- **One sensor:** name, hardware, value in large type, session low and high, and a one-minute sparkline.
- **Two or more:** a group with a row per sensor (name, hardware, small sparkline, value) and an optional
  title.
- A pinned sensor that isn't in the current readings (unplugged, or Pulse started without admin rights)
  keeps its tile, says "Not detected right now", and can be removed.
- Clicking a pinned tile (or a row of a group) opens its details.

### Editing
- The pencil button in the header (dashboard only) turns on edit mode. Esc or **Done** turns it off.
- Each card is dimmed and gets a pill with its name, a size button, and hide (built-in cards) or remove
  (pinned tiles). Groups get a "Name this group" box.
- **The size button opens a menu** (Small, Medium, Large...; each with "a quarter of a row" and a little
  width preview, the current one checked). The first version cycled to the next size on every click; the
  card then resized and the button moved with it, so a quick second click landed somewhere else.
- **Drag a card** to move it. The tiles stay still while you drag: a copy of the card follows the
  pointer and a blue bar shows where it will land (before a card when over its left half, after it over
  the right half; the nearest card counts in gaps). On release the move happens once and the tiles glide
  into place. The view scrolls when the pointer nears the top or bottom.
  - The first version reordered tiles live as the pointer passed over them. While they glided, a
    different tile ended up under the pointer, the order flipped back, and the tiles trembled. The drop
    position is now worked out from the tiles' resting places only (`Controls/TileDrop.cs`), so the same
    pointer position always gives the same answer.
- A bar at the top: what you can do, **Hidden** cards to bring back (they return to their old spot),
  **Pin a sensor** (opens the Ctrl+K search in pin mode), **Reset layout**, **Done**.

### Color code
- A sensor's details chart (and a pinned tile) uses the color of the card that shows it: the Graphics
  card's temperature is green, Memory purple, System power yellow, cooling blue, fans teal, drives sky
  blue. Before, the chart colored by sensor kind, so the GPU temperature came out orange and memory
  load green.
- Sensors that aren't on a card follow what they belong to (anything on the graphics card is green,
  any power reading yellow...), and only shared chips like the motherboard fall back to the kind of
  reading (`Palette.ForSensor`).

### Pump header
- The CPU cooling card has a **Pump header** list whenever the motherboard has spinning fan headers:
  every header with its live speed, plus "Not on a motherboard header". It replaces both the
  "Change pump header" button and the separate list of "Set as pump" buttons. Until someone picks, it
  says "Choose a header"; a lone spinning header is still picked automatically. It's hidden when the
  pump was recognized by name (a cooler, a header called "Pump") and nobody picked one.

### Pinning from elsewhere
- **Details panel:** "Pin to dashboard", or "Unpin" when it's pinned. With pinned tiles already on the
  dashboard it asks: as a new tile, or added to one of them (that's how groups are made).
- **All sensors:** right-click → Pin to dashboard.
- **Settings → Dashboard:** Reset layout. Reset restores the built-in cards' order, sizes and visibility,
  and keeps pinned tiles (at the end).

## Tests
177 passing (27 new): the default layout fills three rows exactly, repairing a damaged or newer layout,
an unknown tile kind not losing other settings, move/resize/size cycling, pinning without duplicates and
into groups, unpinning and removing, the default dashboard saving nothing, **pinning Pump and Water In
next to the CPU card and getting the same layout after a restart**, live values in pinned tiles, a pinned
sensor that disappeared (shown, then removed), hiding and restoring a card in its old place, reset keeping
pins, the size menu (its choices, and a double click on a size applying it once), the search's pin
mode, the drop position (left/right half, end of the list, gaps, never moving next to itself, same
answer for the same pointer), and the pump header list (all headers listed after a pick, switching to
another header or to none, ticks never counting as a choice).

Rendered offscreen with a synthetic machine (default dashboard, pinned group, edit mode). The first edit
mode render had the drag label and size buttons covering each card's title, so the controls moved into
one pill in the middle of the dimmed card. A later render caught a resize mid-glide
(Processor widening while Graphics and System power slide to the next row) and the size menu. Renders also
showed a drag in progress (copy under the pointer, blue drop bar, the picked-up card faded) and the
cooling card's header list. The details colors were checked for the processor, graphics, memory and
power cards. Not yet tried by hand in the running app: dragging with a real mouse.

## Done when
- [x] A user can pin "Pump" and "Water In" next to the CPU card, restart, and see the same layout
  (covered by a test against the saved settings file).
- [x] The default dashboard looks like before for users who never edit (same cards, order and rows;
  the bottom row's three cards are now equal widths).
