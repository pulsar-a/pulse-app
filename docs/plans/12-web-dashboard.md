# Step 12: Phone and web view ✅

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

## What was built

### One deliberate change from the design: HttpListener, not Kestrel
Pulse is going to other people's PCs. Kestrel would need the ASP.NET Core runtime on top of the .NET Desktop
runtime, a second download for every user. `HttpListener` ships with .NET and runs on Windows' own HTTP service
(http.sys), whose request parser lives in the kernel and is hardened. Binding to the network through http.sys
needs administrator rights, which Pulse already runs with. The trade-off is fewer niceties (no middleware), so
the few checks needed are written out in `WebServer` and tested directly.

### Server (`Services/Web/`)
- **Off by default.** Settings → Phone and web view turns it on; with it off, no port is open.
- `WebServer` routes: `/` (the page), `/app.js`, `/app.css`, `/api/summary`, `/api/history?sensor=…&range=1h|24h|7d`,
  `/api/stream` (Server-Sent Events, a summary each second, a ping every 15 s).
- **Same numbers as the window.** `WebFeed` keeps a dashboard view model of its own (never shown) that applies the
  newest snapshot once a second on the UI thread and is turned into JSON there: the gauges with their heat
  levels, rows and last-minute sparklines, cooling (with a paused USB cooler's explanation), fans, drives,
  network and recent alerts. The web threads only read that finished string, so no UI state is touched from
  another thread, and the phone shows exactly what the desktop shows.
- With a phone watching, polling stays at full speed even while the window is hidden (like the mini window).

### Security
- **The local network only, in code:** loopback, 10/8, 172.16/12, 192.168/16, link-local and IPv6 unique-local.
  Anything else is refused whatever the firewall says. Carrier-grade NAT (100.64/10, Tailscale) counts as remote.
- **A token on every request:** 128 random bits. The QR code's link carries it once (`/?t=…`); Pulse checks it,
  sets an `HttpOnly; SameSite=Strict` cookie and redirects to `/`, so the token leaves the address bar.
  After that, the cookie or an `Authorization: Bearer` header. Compared in constant time.
- **New link** replaces the token: the old link, and every phone using it, stop working at once.
- **Guessing is slowed:** 10 wrong tokens from one address within a minute lock it out for 5 minutes.
- **Read-only:** GET only (405 otherwise); nothing on the page or the API changes the PC.
- **Headers:** a strict CSP (`default-src 'none'`, scripts and styles from the page itself only, no inline code,
  `frame-ancestors 'none'`), `no-store`, `nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`.
- At most 8 live streams at once.
- **Logging:** `pulse.log` records start and stop, each new address that connects, refused addresses and lockouts,
  but not every request (a phone polls every second).
- **The page** writes every value with `textContent`, never as HTML, so a sensor renamed to markup stays text.

### Firewall and network
- **Allow in Windows Firewall** (Settings) adds an inbound rule for the port, **private networks only**. It runs
  only when clicked; Pulse never changes the firewall by itself. Whether the rule exists is checked in the background.
- If Windows treats the connection as **Public**, Settings says that phones can't connect and where to change it.
- The link uses the PC's address on its real network: virtual switches (Hyper-V, WSL, Docker) and VPNs come last,
  and an adapter with a gateway beats one without.

### The page (`src/Pulse/Web/`, embedded)
- Mobile-first, dark or light following the phone; the desktop's cards: four rings with sparklines, CPU cooling,
  fans, drives, network, and active alerts at the top. No CDNs, so it works on a LAN without internet.
- Tap a card for its history (1 hour, 24 hours, 7 days) from the same history store as the details panel: the
  range between each moment's low and high, and the average as a line.
- "Reconnecting…" while the stream is down, "Waiting for Pulse…" when updates stop (the PC slept), and a clear
  message when the link is no longer valid.

### Settings → Phone and web view
- On/off, the port (1024–65535), the link with its QR code (QRCoder, MIT, no dependencies), Copy link, New link,
  and the firewall button and network hints.

## Tests
331 passing (32 new):
- **Access:** tokens (format, uniqueness); exact matching only, and no token means nobody gets in; the local-network
  rule over 15 addresses (IPv4, IPv6, mapped, CGNAT); link formatting; the lockout (10 in a minute, 5 minutes,
  slow mistakes and a right link resetting it); picking the real adapter over virtual ones; off by default;
  turning it on making a token, and New link replacing it.
- **Summary:** the dashboard's cards and alerts in the JSON from a real snapshot; turning off forgets it.
- **The real server on a localhost port:** nothing without the token (page, script, API, stream, history); the QR
  link's redirect and its strict cookie; the security headers; the page and script served; the summary; POST
  refused; unknown paths and bad ranges; the stream sending the summary as it changes; guessing locking out even
  the right token afterwards; a new link shutting out the old one at once; stopping closing the port.

Checked in a browser at phone size (dark and light), against a scratch server fed with the test fixture plus the
Commander ST's real readings: the link-to-cookie flow, live updates, the cooling card with the Corsair pump and
coolant, and the history sheet drawing from the live stream. Found and fixed there: click handlers piling up on
the cooling card, and row labels wrapping at phone width.

## Done when
- [ ] Scanning the QR code on a phone opens a live dashboard updating every second. Checked in a phone-sized
      browser on this PC; not yet on a real phone over Wi-Fi (needs the firewall rule, and a Private network).
- [x] With the feature off, no port is open: Stop closes the listener (tested: the port refuses connections), and
      it's off by default. Worth a `netstat -an | findstr 8085` in the real app too.
