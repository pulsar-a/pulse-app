# Step 7: Customizable dashboard

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

## Tasks
- [ ] Tile model and persistence; migrate to the default layout.
- [ ] Split `DashboardViewModel` into per-card view models behind a tile interface.
- [ ] Sensor tile and group tile.
- [ ] Edit mode: reorder, hide, resize, add.
- [ ] Pin entry points from the details panel and All sensors.
- [ ] Tests: layout persistence round-trip; migration; removing a tile whose sensor disappeared.

## Done when
- A user can pin "Pump" and "Water In" next to the CPU card, restart, and see the same layout.
- The default dashboard looks exactly like before for users who never edit.
