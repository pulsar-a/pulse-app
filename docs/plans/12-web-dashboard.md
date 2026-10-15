# Step 12: Phone and web view

**Goal:** check the PC from a phone or another computer on the same network, in a browser.

## Why now

It's a consumer of the sensor stream (step 2) for live values and of history storage (step 5) for
charts. It doesn't depend on the WPF UI work, so it can move earlier if wanted.

## Design

### Server
- ASP.NET Core minimal API (Kestrel) hosted inside Pulse, via a `FrameworkReference` to
  `Microsoft.AspNetCore.App`.
- **Off by default.** Settings → Remote access: enable, port (default 8085), and a generated access
  token shown as a QR code (URL with the token) for the phone to scan.
- Binds to the local network only; no internet exposure, no UPnP.
- Endpoints:
  - `GET /api/snapshot`: current values, with friendly names from the catalog.
  - `GET /api/history?sensor=…&range=24h`: data from `HistoryStore`.
  - `GET /api/stream`: Server-Sent Events, pushing a compact update every second.
  - `/`: a static single-page dashboard embedded in the app.
- Opening the port: when enabled, offer to add a Windows Firewall rule for the private profile only.

### Web page
- Mobile-first, dark, mirroring the desktop look: CPU, GPU, memory and power rings, pump,
  drives, alerts.
- No external CDNs; everything embedded so it works offline on the LAN.

## Security checklist
- Token required on every request (header or cookie after the first visit); constant-time comparison.
- Read-only API; no actions that change the PC.
- Rate limiting; request logging into `pulse.log`.

## Tasks
- [ ] Kestrel hosting with start/stop from Settings.
- [ ] API endpoints and SSE stream.
- [ ] Embedded web dashboard.
- [ ] Token, QR code, firewall rule offer.
- [ ] Tests: API with an in-memory test server; token enforcement.

## Done when
- Scanning the QR code on a phone opens a live dashboard updating every second.
- With the feature off, no port is open (check with `netstat`).
