// Pulse web view. Live from /api/stream (one summary a second), history from /api/history.
// Everything is written with textContent: names can be the user's own renames, never markup.
"use strict";

const $ = (id) => document.getElementById(id);
const svgNs = "http://www.w3.org/2000/svg";

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

function svg(tag, attributes) {
  const node = document.createElementNS(svgNs, tag);
  for (const [k, v] of Object.entries(attributes)) node.setAttribute(k, v);
  return node;
}

function heatClass(heat) {
  return heat === "Hot" ? "hot" : heat === "Warm" ? "warm" : "";
}

function rows(list) {
  const ul = el("ul", "rows");
  for (const row of list) {
    const li = el("li");
    li.append(el("span", "", row.label), el("span", "", row.value));
    ul.append(li);
  }
  return ul;
}

// A ring like the desktop's: a 270° arc, the reading's share of full scale.
function ring(card) {
  const box = el("div", "ring");
  const r = 52, length = 2 * Math.PI * r, arc = length * 0.75;
  const s = svg("svg", { viewBox: "0 0 120 120" });
  s.append(svg("circle", { cx: 60, cy: 60, r, fill: "none", "stroke-width": 9, class: "track",
    "stroke-dasharray": `${arc} ${length}`, "stroke-linecap": "round" }));
  const value = Math.max(0, Math.min(1, card.fraction)) * arc;
  s.append(svg("circle", { cx: 60, cy: 60, r, fill: "none", "stroke-width": 9, stroke: card.accent,
    "stroke-dasharray": `${value} ${length}`, "stroke-linecap": "round" }));
  const text = el("div", "value");
  text.append(el("div", "big", card.value), el("div", "caption", card.caption));
  box.append(s, text);
  return box;
}

// The last minute as a line, scaled to its own range (or 0–100 for percentages and temperatures).
function sparkline(values, color, fixedMax) {
  const points = values.map((v, i) => [i, v]).filter(([, v]) => v !== null);
  const s = svg("svg", { class: "spark", viewBox: "0 0 100 30", preserveAspectRatio: "none" });
  if (points.length < 2) return s;
  const vs = points.map(([, v]) => v);
  const max = fixedMax ?? Math.max(...vs) * 1.1;
  const min = fixedMax ? 0 : Math.min(...vs) * 0.9;
  const span = Math.max(max - min, 1e-6), n = Math.max(values.length - 1, 1);
  const d = points.map(([i, v], k) => `${k ? "L" : "M"}${(i / n) * 100},${30 - ((v - min) / span) * 28 - 1}`).join(" ");
  s.append(svg("path", { d, fill: "none", stroke: color, "stroke-width": 1.6, "vector-effect": "non-scaling-stroke" }));
  return s;
}

// onclick rather than addEventListener: the cooling card is the same element every second, and handlers would pile up.
function clickable(node, sensorId, title) {
  node.onclick = sensorId ? () => openHistory(sensorId, title) : null;
  node.classList.toggle("clickable", Boolean(sensorId));
}

function renderGauges(gauges) {
  const host = $("gauges");
  host.replaceChildren();
  for (const g of gauges) {
    const card = el("article", `card ${heatClass(g.heat)}`);
    const head = el("div", "card-head");
    head.append(el("h2", "", g.title));
    card.append(head, el("div", "device", g.device), ring(g), rows(g.rows));
    const percent = /%|°/.test(g.value) ? 100 : undefined;
    card.append(sparkline(g.history, g.accent, percent));
    clickable(card, g.sensorId, g.title);
    host.append(card);
  }
}

function renderCooling(c) {
  const host = $("cooling");
  host.className = `card ${heatClass(c.heat)}`;
  host.replaceChildren();
  const head = el("div", "card-head");
  head.append(el("h2", "", "CPU cooling"), el("span", "device", c.source));
  host.append(head);
  if (!c.hasData) {
    clickable(host, null, "");
    host.append(el("p", "", c.emptyTitle), el("p", "caption", c.emptyText));
    return;
  }
  host.append(el("p", "headline", c.headline), el("p", "caption", c.caption), rows(c.rows),
    sparkline(c.history, "#4fa8ff"));
  clickable(host, c.sensorId, "CPU cooling");
}

function renderList(id, title, items, render) {
  const host = $(id);
  host.hidden = items.length === 0;
  host.replaceChildren(el("h2", "", title));
  host.firstChild.style.marginBottom = "8px";
  for (const item of items) host.append(render(item));
}

function bar(fraction, color) {
  const b = el("div", "bar");
  const fill = el("i");
  fill.style.width = `${Math.round(Math.max(0, Math.min(1, fraction)) * 100)}%`;
  fill.style.background = color;
  b.append(fill);
  return b;
}

function renderFans(fans) {
  renderList("fans", "Fans", fans, (f) => {
    const wrap = el("div");
    const line = el("div", "list-row");
    line.append(el("span", "", f.name), el("b", "", f.speed));
    wrap.append(line, el("div", "caption", f.source), bar(f.fraction, "#2dd4bf"));
    return wrap;
  });
}

function renderDrives(drives) {
  renderList("drives", "Drives", drives, (d) => {
    const wrap = el("div");
    const line = el("div", "list-row");
    line.append(el("span", "", d.letters ? `${d.letters} ${d.name}` : d.name), el("b", "", d.temperature));
    wrap.append(line, el("div", "caption", d.details), bar(d.used, "#38bdf8"));
    clickable(wrap, d.sensorId, d.name);
    return wrap;
  });
}

function renderNetwork(n) {
  const items = n.adapter ? [n] : [];
  renderList("network", "Network", items, (x) => {
    const wrap = el("div");
    const line = el("div", "list-row");
    line.append(el("span", "", `↓ ${x.down}`), el("span", "", `↑ ${x.up}`));
    wrap.append(el("div", "caption", x.adapter), line, el("div", "caption", x.totals));
    return wrap;
  });
}

function renderAlerts(alerts) {
  const host = $("alerts");
  const recent = alerts.filter((a) => !a.cleared);
  host.hidden = recent.length === 0;
  host.replaceChildren();
  for (const a of recent) {
    const box = el("div", `alert ${a.severity === "Critical" ? "critical" : ""}`);
    box.append(el("strong", "", a.title), el("span", "caption", `${a.time} · ${a.message}`));
    host.append(box);
  }
}

function render(summary) {
  if (!summary) return;
  $("machine").textContent = summary.machine;
  document.title = `Pulse · ${summary.machine}`;
  $("status").textContent = `Live · updated ${summary.updated}`;
  $("dot").classList.add("live");
  renderAlerts(summary.alerts);
  renderGauges(summary.gauges);
  renderCooling(summary.cooling);
  renderFans(summary.fans);
  renderDrives(summary.drives);
  renderNetwork(summary.network);
}

// ---- Live updates ------------------------------------------------------------------------------

let lastMessage = 0;

function connect() {
  const source = new EventSource("api/stream");
  source.onmessage = (e) => {
    lastMessage = Date.now();
    render(JSON.parse(e.data));
  };
  source.onerror = () => {
    $("dot").classList.remove("live");
    $("status").textContent = "Reconnecting…";
    // EventSource retries by itself; a 401 (link no longer valid) ends it, so say so.
    if (source.readyState === EventSource.CLOSED) {
      fetch("api/summary").then((r) => {
        if (r.status === 401) $("status").textContent = "This link isn't valid any more. Scan the QR code in Pulse again.";
        else setTimeout(connect, 3000);
      }).catch(() => setTimeout(connect, 3000));
    }
  };
}

// No word for a while (the PC slept, Wi-Fi dropped): show that the numbers are stale.
setInterval(() => {
  if (lastMessage && Date.now() - lastMessage > 5000) {
    $("dot").classList.remove("live");
    $("status").textContent = "Waiting for Pulse…";
  }
}, 2000);

fetch("api/summary").then((r) => (r.ok ? r.json() : null)).then(render).catch(() => {});
connect();

// ---- History sheet -------------------------------------------------------------------------------

let historySensor = null;
let historyTitle = "";

function openHistory(sensorId, title) {
  historySensor = sensorId;
  historyTitle = title;
  $("sheet-title").textContent = title;
  $("sheet").hidden = false;
  loadHistory(document.querySelector(".ranges button.on").dataset.range);
}

function loadHistory(range) {
  for (const b of document.querySelectorAll(".ranges button")) b.classList.toggle("on", b.dataset.range === range);
  const chart = $("chart");
  chart.replaceChildren();
  $("sheet-stats").textContent = "Loading…";
  fetch(`api/history?sensor=${encodeURIComponent(historySensor)}&range=${range}`)
    .then((r) => r.json())
    .then((points) => drawHistory(chart, points))
    .catch(() => ($("sheet-stats").textContent = "Couldn't load the history."));
}

function drawHistory(chart, points) {
  if (points.length < 2) {
    $("sheet-stats").textContent = "No history yet for this range.";
    return;
  }
  const t0 = points[0].t, t1 = points[points.length - 1].t;
  const lo = Math.min(...points.map((p) => p.min)), hi = Math.max(...points.map((p) => p.max));
  const span = Math.max(hi - lo, 1e-6), pad = span * 0.1;
  const x = (t) => ((t - t0) / Math.max(t1 - t0, 1)) * 320;
  const y = (v) => 156 - ((v - lo + pad) / (span + 2 * pad)) * 152;
  const color = getComputedStyle(document.documentElement).getPropertyValue("--accent").trim() || "#4fa8ff";
  // The band between each moment's lowest and highest, then the average as a line.
  const band = points.map((p, i) => `${i ? "L" : "M"}${x(p.t)},${y(p.max)}`).join(" ") + " "
    + points.slice().reverse().map((p) => `L${x(p.t)},${y(p.min)}`).join(" ") + " Z";
  chart.append(svg("path", { d: band, fill: color, opacity: 0.18 }));
  chart.append(svg("path", {
    d: points.map((p, i) => `${i ? "L" : "M"}${x(p.t)},${y(p.avg)}`).join(" "),
    fill: "none", stroke: color, "stroke-width": 2, "vector-effect": "non-scaling-stroke",
  }));
  const fmt = (v) => (Math.abs(v) >= 100 ? v.toFixed(0) : v.toFixed(1));
  $("sheet-stats").textContent = `Low ${fmt(lo)} · high ${fmt(hi)} · ${points.length} points`;
}

for (const b of document.querySelectorAll(".ranges button")) b.addEventListener("click", () => loadHistory(b.dataset.range));
$("sheet-close").addEventListener("click", () => ($("sheet").hidden = true));
$("sheet").addEventListener("click", (e) => { if (e.target === $("sheet")) $("sheet").hidden = true; });
