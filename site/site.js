/*
  The project site's only script. It runs in <head>, so the theme is set
  before first paint, then draws everything once the DOM exists.

  Every number is generated from a fixed seed: identical on every load, and
  nobody's connection. Formats, colours and rules follow the app's own
  src/SpeedMeter.Core/View/Format.cs, Theme/Palette.cs, Meter/MeterPalette.cs,
  Meter/PixelFont.cs, Charts/*.cs and Views/*.cs. The live readouts tick once
  a second from a random walk, as the meter samples once a second.
*/

/* ------------------------------------------------------------------ theme */

const THEME_KEY = 'ism-site-theme';

function systemTheme() {
  return window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
}

function storedTheme() {
  try {
    const t = localStorage.getItem(THEME_KEY);
    return t === 'light' || t === 'dark' ? t : null;
  } catch { return null; }
}

function applyTheme(t) {
  document.documentElement.dataset.theme = t;
  const meta = document.querySelector('meta[name="theme-color"]');
  if (meta) meta.setAttribute('content', t === 'light' ? '#f4f5f7' : '#000000');
}

applyTheme(storedTheme() ?? systemTheme());

const isLight = () => document.documentElement.dataset.theme === 'light';
const css = (name) => getComputedStyle(document.documentElement).getPropertyValue(name).trim();

/* ------------------------------------------------------------------ format (Format.cs) */

let units = 'bits';
const BYTE_RATE = ['B/s', 'KB/s', 'MB/s', 'GB/s', 'TB/s'];
const BIT_RATE = ['bps', 'Kbps', 'Mbps', 'Gbps', 'Tbps'];
const BYTE_SHORT = ['B', 'K', 'M', 'G', 'T'];
const BIT_SHORT = ['b', 'K', 'M', 'G', 'T'];
const VOLUME = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
const WEEKDAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

function scale(value, step, n) {
  let i = 0, s = value;
  while (Math.abs(s) >= step && i < n - 1) { s /= step; i++; }
  return [s, i];
}
/** Bytes per second in the chosen units: bits are decimal, bytes binary, as Windows reports them. */
function scaledRate(bps) {
  const bits = units === 'bits';
  return scale(bits ? bps * 8 : bps, bits ? 1000 : 1024, 5);
}
const decimals = (v) => (Math.abs(v) >= 100 ? 0 : Math.abs(v) >= 10 ? 1 : 2);
/** "93.1" and "Mbps", so the unit can be set smaller than the number. */
function splitRate(bps) {
  const [s, i] = scaledRate(bps);
  return [s.toFixed(decimals(s)), (units === 'bits' ? BIT_RATE : BYTE_RATE)[i]];
}
const rate = (bps) => splitRate(bps).join(' ');
/** The taskbar text, the panel and the tooltip: "9.42 MB/s". */
function speedLong(bps) {
  const [s, i] = scaledRate(bps);
  const d = i === 0 ? 0 : s < 10 ? 2 : s < 100 ? 1 : 0;
  return `${s.toFixed(d)} ${(units === 'bits' ? BIT_RATE : BYTE_RATE)[i]}`;
}
/** The tray icon's compact value: "9.4" and "M". */
function speedShort(bps) {
  const [s, i] = scaledRate(bps);
  const v = s < 0.05 ? '0' : s < 10 && i > 0 ? s.toFixed(1) : s.toFixed(0);
  return [v, (units === 'bits' ? BIT_SHORT : BYTE_SHORT)[i]];
}
/** An axis tick: whole numbers where they suffice. */
function rateTick(bps) {
  if (bps <= 0) return '0';
  const [s, i] = scaledRate(bps);
  const t = Math.abs(s - Math.round(s)) < 0.05 ? String(Math.round(s)) : String(+s.toFixed(1));
  return `${t} ${(units === 'bits' ? BIT_RATE : BYTE_RATE)[i]}`;
}
function bytes(b) {
  const [s, i] = scale(b, 1024, VOLUME.length);
  return `${s.toFixed(decimals(s))} ${VOLUME[i]}`;
}
/** A session total in the panel: "4.70 GB". */
function total(b) {
  const [s, i] = scale(b, 1024, VOLUME.length);
  const d = i === 0 ? 0 : s < 10 ? 2 : s < 100 ? 1 : 0;
  return `${s.toFixed(d)} ${VOLUME[i]}`;
}
function duration(sec) {
  sec = Math.max(0, Math.floor(sec));
  if (sec >= 3600) return `${Math.floor(sec / 3600)}h ${Math.floor((sec % 3600) / 60)}m`;
  if (sec >= 60) return `${Math.floor(sec / 60)}m ${sec % 60}s`;
  return `${sec}s`;
}
const pad2 = (n) => String(n).padStart(2, '0');
/** The app's stamps: "8 Oct, 10:02". Dates here are built in UTC and shown as local. */
const stamp = (d) => `${d.getUTCDate()} ${MONTHS[d.getUTCMonth()]}, ${pad2(d.getUTCHours())}:${pad2(d.getUTCMinutes())}`;
const dayShort = (d) => `${d.getUTCDate()} ${MONTHS[d.getUTCMonth()]}`;
const dayLong = (d) => `${WEEKDAYS[d.getUTCDay()]} ${d.getUTCDate()} ${MONTHS[d.getUTCMonth()]} ${d.getUTCFullYear()}`;
const count = (n) => Math.round(n).toLocaleString('en-US');
const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
/** C#'s Math.Round: halves go to the even neighbour. */
function roundEven(x) {
  const r = Math.round(x);
  return Math.abs(x % 1) === 0.5 && r % 2 !== 0 ? r - 1 : r;
}

/* ------------------------------------------------------------------ data */

function mulberry32(seed) {
  return function () {
    seed |= 0; seed = (seed + 0x6d2b79f5) | 0;
    let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const DAY_MS = 86400000;
const MB = 1024 * 1024;
const FIRST = Date.UTC(2026, 5, 11); // the screenshots' history starts here
const END = Date.UTC(2026, 9, 8); // and ends here
const UPGRADE = Date.UTC(2026, 7, 31); // a 50 Mbps line becomes a 100 Mbps one
const mbps = (m) => (m * 1e6) / 8;
const downLine = (t) => (t >= UPGRADE ? mbps(100) : mbps(50));
const UP_LINE = mbps(20);

/** The history: one entry per day, null where the meter never ran. */
const HISTORY = (() => {
  const rnd = mulberry32(20261008);
  const days = [];
  const tripStart = Date.UTC(2026, 6, 18);
  for (let t = FIRST; t <= END; t += DAY_MS) {
    const away = (t >= tripStart && t < tripStart + 4 * DAY_MS) || (t !== END && rnd() < 0.04);
    if (away) { days.push({ day: new Date(t), stats: null }); continue; }
    const line = downLine(t);
    const at = (h, m) => new Date(t + h * 3600000 + m * 60000);
    const down = line * (0.68 + 0.36 * rnd() ** 0.55);
    const up = UP_LINE * (rnd() < 0.2 ? 0.75 + rnd() * 0.22 : 0.12 + 0.3 * rnd() ** 2);
    days.push({
      day: new Date(t),
      stats: {
        down: { bps: down, at: at(9 + Math.floor(rnd() * 14), Math.floor(rnd() * 60)) },
        up: { bps: up, at: at(9 + Math.floor(rnd() * 14), Math.floor(rnd() * 60)) },
        downBytes: (2 + rnd() * 12) * 1024 * MB * (line / mbps(100) + 0.4),
        upBytes: (0.15 + rnd() * 1.3) * 1024 * MB,
        seconds: (6 + rnd() * 10) * 3600,
      },
    });
  }
  return days;
})();

const RECORDS = (() => {
  const on = HISTORY.filter((d) => d.stats);
  const best = (k) => on.reduce((a, d) => (d.stats[k].bps > a.bps ? d.stats[k] : a), { bps: 0, at: null });
  const today = HISTORY[HISTORY.length - 1].stats;
  // The sustained windows need raw seconds, which are kept 14 days.
  const recent = on.filter((d) => d.day.getTime() > END - 14 * DAY_MS);
  const top = recent.reduce((a, d) => (d.stats.down.bps > a.stats.down.bps ? d : a), recent[0]);
  const base = top.stats.down;
  const later = (m) => new Date(base.at.getTime() + m * 60000);
  return {
    todayDown: today.down, todayUp: today.up, bestDown: best('down'), bestUp: best('up'),
    sustained: [
      { label: '10 s', bps: base.bps * 0.967, at: base.at },
      { label: '1 min', bps: base.bps * 0.951, at: base.at },
      { label: '5 min', bps: base.bps * 0.78, at: later(15) },
    ],
    days: on.length, first: on[0].day,
  };
})();

const PROFILES = [
  { id: 'standard', label: 'Standard', down: 100 * MB, up: 25 * MB, streams: 6, upStreams: 3 },
  { id: 'thorough', label: 'Thorough', down: 250 * MB, up: 60 * MB, streams: 6, upStreams: 4 },
  { id: 'heavy', label: 'Heavy', down: 500 * MB, up: 120 * MB, streams: 6, upStreams: 4 },
];

/** One finished run, with the rates a run at this line and setting would get. */
function makeRun(rnd, at, profile, opts = {}) {
  const line = downLine(at.getTime());
  const single = opts.single;
  const down = line * (single ? 0.52 + rnd() * 0.14 : 0.875 + rnd() * 0.08);
  const up = UP_LINE * (single ? 0.55 + rnd() * 0.12 : 0.8 + rnd() * 0.15);
  // A leg is cut at 20 s and keeps what moved.
  const downSeconds = Math.min(20, (profile.down * 8) / (down * 8) + 0.4 + rnd() * 0.3);
  const upSeconds = Math.min(20, profile.up / up + 0.4);
  const idle = 18 + rnd() * 8;
  // Whole pings lost of those sent in each phase (ten a second).
  const lost = (sent) => Math.floor(rnd() * rnd() * sent * 0.025);
  const run = {
    at, profile: opts.profileId ?? profile.id, connections: opts.device ? null : single ? 1 : profile.streams, device: opts.device ?? null,
    downBps: down, upBps: up,
    peakDownBps: down * (1.04 + rnd() * 0.05), peakUpBps: up * (1.03 + rnd() * 0.06),
    downSeconds: opts.short ?? downSeconds, upSeconds,
    downBytes: Math.min(profile.down, down * (opts.short ?? downSeconds)), upBytes: Math.min(profile.up, up * upSeconds),
    latency: idle, loadedDown: opts.old ? null : idle + 30 + rnd() * 85, loadedUp: opts.old ? null : idle + 40 + rnd() * 140,
    jitter: 0.8 + rnd() * 2, loadedDownJitter: 4 + rnd() * 8, loadedUpJitter: 6 + rnd() * 12,
    lost: opts.old ? null : { idle: lost(20), down: lost(90), up: lost(70) },
  };
  if (run.lost) {
    const l = run.lost;
    run.loss = { idle: (l.idle / 20) * 100, down: (l.down / 90) * 100, up: (l.up / 70) * 100 };
    run.lossTotal = ((l.idle + l.down + l.up) / 180) * 100;
  } else run.loss = run.lossTotal = null;
  run.meterSaw = run.device ? null : run.peakDownBps * (0.99 + rnd() * 0.05);
  run.tooShort = run.downSeconds < 3;
  return run;
}

const RUNS = (() => {
  const rnd = mulberry32(46);
  const runs = [];
  const from = Date.UTC(2026, 6, 26, 18, 13), to = Date.UTC(2026, 9, 8, 10, 3);
  for (let i = 0; i < 46; i++) {
    // Spread over the weeks, at waking hours.
    const day = Math.floor((from + (i / 45) * (to - from)) / DAY_MS) * DAY_MS;
    const t = i === 45 ? to : day + (9 + rnd() * 13) * 3600000;
    const at = new Date(Math.floor(t / 60000) * 60000);
    const profile = rnd() < 0.25 ? PROFILES[1] : PROFILES[0];
    // A few that show what the table copes with: the oldest lines predate
    // loaded latency and loss, one used the retired "light" size and ran too
    // short to trust, one was single-connection, one was taken on a phone.
    const opts = i < 3 ? { old: true } : i === 3 ? { old: true, profileId: 'light', short: 2.4 } : i === 17 ? { single: true } : i === 29 ? { device: 'phone' } : {};
    runs.push(makeRun(rnd, at, opts.profileId ? { ...PROFILES[0], down: 10 * MB, up: 5 * MB } : profile, opts));
  }
  return runs.reverse();
})();

/* ------------------------------------------------------------------ helpers */

/** Round axis ticks from zero, n intervals, in display units (ChartKit.NiceTicks). */
function niceTicks(max, n = 4) {
  const f = units === 'bits' ? 8 : 1;
  const m = max * f;
  if (m <= 0) return [...Array(n + 1)].map((_, i) => i / f);
  const raw = m / n;
  const p = 10 ** Math.floor(Math.log10(raw));
  let step = 10 * p;
  for (const k of [1, 2, 2.5, 3, 5, 10]) if (k * p >= raw) { step = k * p; break; }
  return [...Array(n + 1)].map((_, i) => (i * step) / f);
}

function yAxis(ticks, x0, x1, y) {
  return ticks.map((v) => `<line x1="${x0}" x2="${x1}" y1="${y(v)}" y2="${y(v)}" stroke="var(--grid)"/>`
    + `<text x="${x0 - 10}" y="${y(v) + 4}" text-anchor="end">${esc(rateTick(v))}</text>`).join('');
}

/** Monotone cubic (Fritsch-Carlson): a curve that never overshoots the data, as ChartKit.MonotoneFigure. */
function curve(pts) {
  const n = pts.length;
  if (n < 2) return n ? `M${pts[0][0]},${pts[0][1]}` : '';
  const dx = [], m = [], t = [];
  for (let i = 0; i < n - 1; i++) { dx[i] = pts[i + 1][0] - pts[i][0]; m[i] = (pts[i + 1][1] - pts[i][1]) / dx[i]; }
  t[0] = m[0]; t[n - 1] = m[n - 2];
  for (let i = 1; i < n - 1; i++) t[i] = m[i - 1] * m[i] <= 0 ? 0 : (m[i - 1] + m[i]) / 2;
  for (let i = 0; i < n - 1; i++) {
    if (m[i] === 0) { t[i] = 0; t[i + 1] = 0; continue; }
    const a = t[i] / m[i], b = t[i + 1] / m[i], h = a * a + b * b;
    if (h > 9) { const k = 3 / Math.sqrt(h); t[i] = k * a * m[i]; t[i + 1] = k * b * m[i]; }
  }
  const f = (v) => v.toFixed(1);
  let d = `M${f(pts[0][0])},${f(pts[0][1])}`;
  for (let i = 0; i < n - 1; i++) {
    const h = dx[i] / 3;
    d += `C${f(pts[i][0] + h)},${f(pts[i][1] + h * t[i])} ${f(pts[i + 1][0] - h)},${f(pts[i + 1][1] - h * t[i + 1])} ${f(pts[i + 1][0])},${f(pts[i + 1][1])}`;
  }
  return d;
}

const tipRow = (color, label, value) => `<div class="row"><i style="background:${color}"></i>${esc(label)}<span>${esc(value)}</span></div>`;

const tipEl = () => document.getElementById('tip');
function showTip(html, x, y) {
  const tip = tipEl();
  tip.innerHTML = html;
  tip.hidden = false;
  const r = tip.getBoundingClientRect();
  let left = x + 14, top = y + 14;
  if (left + r.width > window.innerWidth - 8) left = x - r.width - 14;
  if (top + r.height > window.innerHeight - 8) top = y - r.height - 14;
  tip.style.left = `${Math.max(8, left)}px`;
  tip.style.top = `${Math.max(8, top)}px`;
}
const hideTip = () => { tipEl().hidden = true; };

/** Pointer x inside an SVG, in its viewBox units. */
function svgX(svg, e) {
  const r = svg.getBoundingClientRect();
  return ((e.clientX - r.left) / r.width) * svg.viewBox.baseVal.width;
}

const reducedMotion = () => window.matchMedia('(prefers-reduced-motion: reduce)').matches;

/* ------------------------------------------------------------------ the live meter */

/*
  A random walk through what a connection does over a minute: mostly idle,
  a video's steady trickle, now and then a download that fills the line.
*/
const live = (() => {
  const rnd = mulberry32(714);
  let mode = 'idle', left = 6, down = 0, up = 0;
  const hist = { down: [], up: [] };
  const session = { down: 4.21 * 1024 * MB, up: 538 * MB, start: Date.now() - (73 * 60 + 12) * 1000, peak: mbps(48.8) };
  function step() {
    if (--left <= 0) {
      const r = rnd();
      mode = r < 0.35 ? 'idle' : r < 0.75 ? 'stream' : r < 0.92 ? 'download' : 'upload';
      left = mode === 'download' ? 6 + Math.floor(rnd() * 10) : 5 + Math.floor(rnd() * 14);
    }
    const j = () => 0.75 + rnd() * 0.5;
    const target = {
      idle: [mbps(0.05) + rnd() * mbps(0.4), mbps(0.02) + rnd() * mbps(0.1)],
      stream: [mbps(4) * j() + (rnd() < 0.3 ? mbps(14) * rnd() : 0), mbps(0.25) * j()],
      download: [mbps(86) * (0.88 + rnd() * 0.16), mbps(1.4) * j()],
      upload: [mbps(1.2) * j(), mbps(17) * (0.8 + rnd() * 0.2)],
    }[mode];
    down = down * 0.35 + target[0] * 0.65;
    up = up * 0.35 + target[1] * 0.65;
    hist.down.push(down); hist.up.push(up);
    if (hist.down.length > 60) { hist.down.shift(); hist.up.shift(); }
    session.down += down; session.up += up;
    session.peak = Math.max(session.peak, down);
  }
  for (let i = 0; i < 60; i++) step();
  return { step, hist, session, get down() { return down; }, get up() { return up; } };
})();

/* The taskbar's own palette, by the TASKBAR's theme (MeterPalette.cs). */
const METER = {
  dark: { down: '#6EE7A8', up: '#FFC46B', bg: '#202024' },
  light: { down: '#0B6B35', up: '#9A5400', bg: '#EEF0F4' },
};

const meterState = { tbTheme: 'dark', side: 'left', layout: 'both', unitLetters: true, upTop: false };

function clockHtml() {
  const now = new Date();
  return `<div class="tb-clock">${now.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}<br>${now.toLocaleDateString()}</div>`;
}

function readoutHtml() {
  return `<div class="readout" data-tip="${esc(`<b>Click</b>the details panel<br><b>Double-click</b>the dashboard<br><b>Right-click</b>the menu`)}"><i class="d"></i><span>${speedLong(live.down)}</span><i class="u"></i><span>${speedLong(live.up)}</span></div>`;
}

function taskbarHtml(side, withIcon) {
  const left = side === 'left' ? readoutHtml() : '';
  const right = side === 'right' ? readoutHtml() : '';
  return `${left}<div class="tb-search"></div><div class="tb-apps"><span class="win"></span><span></span><span></span><span></span><span></span></div>`
    + `${right}<div class="tb-tray"><span>⌃</span>${withIcon ? '<canvas width="16" height="16" class="tb-icon"></canvas>' : ''}<span>ENG</span></div>${clockHtml()}`;
}

function drawTaskbars() {
  const hero = document.getElementById('hero-taskbar');
  hero.innerHTML = taskbarHtml('left', false);
  document.getElementById('hero-flyout').innerHTML = flyoutHtml(METER.dark);
  const v = document.getElementById('v-taskbar');
  v.className = `tb tb-${meterState.tbTheme}`;
  v.innerHTML = taskbarHtml(meterState.side, true);
  v.querySelectorAll('.tb-icon').forEach((c) => paintIcon(c, METER[meterState.tbTheme], true));
}

/* ------------------------------------------------------------------ the tray icon (PixelFont.cs) */

// Row-major bits, top row first, '1' = lit; three pixels wide, seven rows tall.
const PIXEL_FONT = {
  '0': '111101101101101101111', '1': '010110010010010010111', '2': '111001001111100100111', '3': '111001001111001001111',
  '4': '101101101111001001001', '5': '111100100111001001111', '6': '111100100111101101111', '7': '111001001010010010010',
  '8': '111101101111101101111', '9': '111101101111001001111', '.': '0000001', ',': '0000011',
  K: '101101101110101101101', M: '101111111101101101101', G: '111100100101101101111', T: '111010010010010010010',
  B: '110101101110101101110', b: '100100100110101101110',
};
const glyphWidth = (c) => PIXEL_FONT[c].length / 7;
function pixelWidth(text) {
  let w = 0;
  for (let i = 0; i < text.length; i++) { if (!PIXEL_FONT[text[i]]) return -1; w += (i ? 1 : 0) + glyphWidth(text[i]); }
  return w;
}

/** One row of the 16 px icon, as IconRenderer.DrawRow with a 16 px icon. */
function drawRow(ctx, text, color, y0, rowH) {
  const gap = rowH > 12 ? 2 : 1;
  const ink = rowH - gap;
  const sy = Math.max(1, Math.floor(ink / 7));
  const w = pixelWidth(text);
  ctx.fillStyle = color;
  if (w < 0 || w > 16) {
    // Too wide for the pixel font (a four-digit value with its unit): real type, squeezed.
    ctx.save();
    ctx.font = `${ink + 2}px Tahoma, 'Segoe UI', sans-serif`;
    ctx.textBaseline = 'middle';
    const m = ctx.measureText(text).width;
    ctx.translate(8, y0 + rowH / 2);
    ctx.scale(Math.min(1, 16 / m), 1);
    ctx.textAlign = 'center';
    ctx.fillText(text, 0, 0);
    ctx.restore();
    return;
  }
  let x = roundEven((16 - w) / 2);
  const y = roundEven(y0 + (rowH - 7 * sy) / 2);
  for (const c of text) {
    const g = PIXEL_FONT[c], gw = glyphWidth(c);
    for (let r = 0; r < 7; r++) for (let col = 0; col < gw; col++) if (g[r * gw + col] === '1') ctx.fillRect(x + col, y + r * sy, 1, sy);
    x += gw + 1;
  }
}

function paintIcon(canvas, pal, transparent) {
  const ctx = canvas.getContext('2d');
  ctx.clearRect(0, 0, 16, 16);
  if (!transparent) { ctx.fillStyle = pal.bg; ctx.fillRect(0, 0, 16, 16); }
  const [dv, du] = speedShort(live.down), [uv, uu] = speedShort(live.up);
  const dt = meterState.unitLetters ? dv + du : dv, ut = meterState.unitLetters ? uv + uu : uv;
  if (meterState.layout === 'down') return drawRow(ctx, dt, pal.down, 0, 16);
  if (meterState.layout === 'up') return drawRow(ctx, ut, pal.up, 0, 16);
  const [a, b] = meterState.upTop ? [[ut, pal.up], [dt, pal.down]] : [[dt, pal.down], [ut, pal.up]];
  drawRow(ctx, a[0], a[1], 0, 8);
  drawRow(ctx, b[0], b[1], 8, 8);
}

function drawIcon() {
  const pal = METER[isLight() ? 'light' : 'dark'];
  paintIcon(document.getElementById('v-icon-big'), pal, false);
  paintIcon(document.getElementById('v-icon-1x'), pal, true);
  document.querySelector('.tray-chip').style.background = pal.bg;
  document.getElementById('v-icon-tip').textContent = `↓ ${speedLong(live.down)}   ↑ ${speedLong(live.up)}\nWi-Fi`;
}

/* ------------------------------------------------------------------ the details panel (FlyoutForm.cs) */

function flyoutHtml(pal) {
  const h = live.hist;
  const W = 312, H = 70;
  const max = Math.max(...h.down, ...h.up, 1) * 1.08;
  const x = (i) => (i / (h.down.length - 1)) * W;
  const y = (v) => H - (v / max) * (H - 4);
  const pts = (arr) => arr.map((v, i) => `${x(i).toFixed(1)},${y(v).toFixed(1)}`).join(' ');
  const [dv, du] = splitLong(live.down), [uv, uu] = splitLong(live.up);
  return `<div class="fl-body">
      <div class="fl-head"><span>Wi-Fi</span><small>${duration((Date.now() - live.session.start) / 1000)}</small></div>
      <div class="fl-rate"><span class="tri d"></span><b>${dv}</b><span class="u">${du}</span></div>
      <div class="fl-rate"><span class="tri u"></span><b>${uv}</b><span class="u">${uu}</span></div>
      <div class="fl-graph"><svg viewBox="0 0 ${W} ${H}" preserveAspectRatio="none">
        <polygon points="0,${H} ${pts(h.down)} ${W},${H}" fill="${pal.down}" fill-opacity="0.38"/>
        <polyline points="${pts(h.down)}" fill="none" stroke="${pal.down}" stroke-width="1.2"/>
        <polyline points="${pts(h.up)}" fill="none" stroke="${pal.up}" stroke-width="1.2"/>
      </svg></div>
      <div class="fl-foot"><span>Session&nbsp;&nbsp; ▼ ${total(live.session.down)}&nbsp;&nbsp;&nbsp; ▲ ${total(live.session.up)}</span><span>peak ${speedLong(live.session.peak)}</span></div>
    </div>
    <div class="fl-links"><span>Open dashboard</span><span>Run a speed test</span></div>`;
}
function splitLong(bps) {
  const s = speedLong(bps);
  const i = s.lastIndexOf(' ');
  return [s.slice(0, i), s.slice(i + 1)];
}
function drawFlyouts() {
  document.getElementById('v-flyout-dark').innerHTML = flyoutHtml(METER.dark);
  document.getElementById('v-flyout-light').innerHTML = flyoutHtml(METER.light);
}

/* ------------------------------------------------------------------ menus */

/*
  Items: [label, opts] where opts may hold check (bool), bold, sub (items),
  kb (shortcut text), act (what a click does here), off (disabled). '-' is a
  separator. Submenus open inline, so they also work at phone width.
*/
function menuHtml(items, path = 'm') {
  return items.map((it, i) => {
    if (it === '-') return '<hr>';
    const [label, o = {}] = it;
    const id = `${path}-${i}`;
    const ck = o.check === undefined ? '' : o.check ? '✓' : '';
    const cls = ['mi', o.sub ? 'sub' : '', o.bold ? 'bold' : '', o.off ? 'off' : ''].join(' ');
    const open = menuOpen.has(id);
    const head = `<div class="${cls}" ${o.sub ? `data-sub="${id}" aria-expanded="${open}" role="button" tabindex="0"` : ''}${o.act ? ` data-act="${o.act}"` : ''}>`
      + `<span class="ck">${ck}</span>${esc(label)}${o.kb ? `<span class="kb">${o.kb}</span>` : ''}${o.sub ? '<span class="ar">▶</span>' : ''}</div>`;
    return head + (o.sub ? `<div class="subm" ${open ? '' : 'hidden'}>${menuHtml(o.sub, id)}</div>` : '');
  }).join('');
}
const menuOpen = new Set(['tray-4', 'gear-0']);

function trayMenu() {
  const m = meterState;
  return [
    ['Open dashboard', { bold: true }], ['Run a speed test…', { act: 'test' }], ['Details…'], '-',
    ['Show speeds on', { sub: [
      ['Taskbar text (clock size)', { check: true }], ['Tray icon numbers', { check: true }], '-',
      ['Taskbar text position', { sub: [['Left end', { check: m.side === 'left', act: 'side:left' }], ['Right, beside the clock', { check: m.side === 'right', act: 'side:right' }]] }],
    ] }],
    ['Adapter', { sub: [['Automatic (busiest adapter)', { check: true }], ['All adapters (combined)', { check: false }], '-', ['Wi-Fi', { check: false }], ['Ethernet', { check: false }]] }],
    ['Units', { sub: [['Bytes (KB/s, MB/s)', { check: units === 'bytes', act: 'units:bytes' }], ['Bits (Kbps, Mbps)', { check: units === 'bits', act: 'units:bits' }]] }],
    ['Icon', { sub: [
      ['Layout', { sub: [['Download and upload', { check: m.layout === 'both', act: 'layout:both' }], ['Download only (bigger text)', { check: m.layout === 'down', act: 'layout:down' }], ['Upload only (bigger text)', { check: m.layout === 'up', act: 'layout:up' }]] }],
      '-', ['Show unit letters', { check: m.unitLetters, act: 'unitletters' }], ['Upload on top', { check: m.upTop, act: 'uptop' }], '-',
      ['Text colour', { sub: [['Match taskbar', { check: true }], ['Light text (dark taskbar)', { check: false }], ['Dark text (light taskbar)', { check: false }]] }],
    ] }],
    '-', ['Start with Windows', { check: true }], ['Reset session counters', { act: 'reset' }], '-', ['Exit'],
  ];
}

function gearMenu() {
  return [
    ['Theme', { sub: [['Dark', { check: !isLight(), act: 'theme:dark' }], ['Light', { check: isLight(), act: 'theme:light' }], ['Use Windows setting', { check: false, act: 'theme:system' }]] }],
    ['Units', { sub: [['Bits (Kbps, Mbps)', { check: units === 'bits', act: 'units:bits' }], ['Bytes (KB/s, MB/s)', { check: units === 'bytes', act: 'units:bytes' }]] }],
    '-', ['Start with Windows', { check: true }], ['Notify when recording stops', { check: true }], ['Notify when a test ends while closed', { check: true }], '-',
    ['Zoom in', { kb: 'Ctrl+Plus' }], ['Zoom out', { kb: 'Ctrl+Minus' }], ['Reset zoom (100%)', { kb: 'Ctrl+0' }], '-',
    ['Export history as CSV…'], ['Open history folder'], ['Open raw samples folder'], ['Open settings folder'], '-', ['Exit'],
  ];
}

function drawMenus() {
  document.getElementById('v-traymenu').innerHTML = menuHtml(trayMenu(), 'tray');
  document.getElementById('v-gearmenu').innerHTML = menuHtml(gearMenu(), 'gear');
}

/* ------------------------------------------------------------------ Speed page: figure cards */

function figHtml(label, valueBps, sub, color, size) {
  const [v, u] = splitRate(valueBps);
  return `<div class="fig"><div class="stat-label">${esc(label)}</div>
    <div class="v"><b style="color:${color};font-size:${size}">${v}</b><small>${u}</small></div>
    ${sub.map((s, i) => `<div class="w${i ? ' faint' : ''}">${esc(s)}</div>`).join('')}</div>`;
}

function drawFigs(skeleton = false) {
  const el = document.getElementById('v-figs');
  const r = RECORDS;
  if (skeleton) {
    // Skeleton.Apply: the page's own build, its text turned into bars.
    el.classList.add('skel');
    el.innerHTML = [...Array(8)].map(() => `<div class="fig"><span class="skel-bar" style="width:62%;height:11px"></span>
      <span class="skel-bar" style="width:46%;height:34px;margin-top:14px"></span><span class="skel-bar" style="width:38%;height:12px;margin-top:12px"></span></div>`).join('');
    return;
  }
  el.classList.remove('skel');
  const down = 'var(--down)', up = 'var(--up)';
  const when = (p) => (p.at ? stamp(p.at) : 'never observed');
  el.innerHTML = [
    figHtml("Today's fastest second down", r.todayDown.bps, [when(r.todayDown)], down, '2.3rem'),
    figHtml("Today's fastest second up", r.todayUp.bps, [when(r.todayUp)], up, '2.3rem'),
    figHtml('Fastest second down', r.bestDown.bps, [when(r.bestDown)], down, '2.3rem'),
    figHtml('Fastest second up', r.bestUp.bps, [when(r.bestUp)], up, '2.3rem'),
    ...r.sustained.map((s) => figHtml(`Best sustained ${s.label}`, s.bps, [stamp(s.at)], down, '2rem')),
    `<div class="fig"><div class="stat-label">On record</div><div class="v"><b style="font-size:2rem">${count(r.days)}</b><small>days</small></div>
      <div class="w">since ${dayLong(r.first)}</div><div class="w faint">recorded at the adapter once a second</div></div>`,
  ].join('');
  el.querySelectorAll('.fig').forEach((f, i) => { f.style.animationDelay = `${(i % 4) * 60 + Math.floor(i / 4) * 60}ms`; });
}

/* ------------------------------------------------------------------ Speed page: daily peak (DailyPeakChart.cs) */

let peakRange = 30;
function drawPeak() {
  const W = 1000, H = 290, L = 86, R = 8, T = 8, B = 30;
  const start = Math.max(FIRST, END - (peakRange - 1) * DAY_MS);
  const days = HISTORY.filter((d) => d.day.getTime() >= start);
  const max = Math.max(...days.map((d) => (d.stats ? Math.max(d.stats.down.bps, d.stats.up.bps) : 0)));
  const ticks = niceTicks(max);
  const top = ticks[ticks.length - 1];
  const pb = H - B;
  const y = (v) => pb - (v / top) * (pb - T);
  const band = (W - L - R) / days.length;
  // Two bars and the gap between them; a bar never wider than 46.
  const bw = Math.max(1, Math.min(46, (band * 0.8) / 2));
  const pairW = bw * 2 + Math.min(4, band * 0.06);
  const rTop = (x, yy, w) => {
    const h = pb - yy;
    if (h <= 0.2) return '';
    const r = Math.min(3, w / 2, h);
    return `M${x},${pb}V${yy + r}Q${x},${yy} ${x + r},${yy}H${x + w - r}Q${x + w},${yy} ${x + w},${yy + r}V${pb}Z`;
  };
  let bars = '', labels = '';
  const every = Math.max(1, Math.ceil(days.length / 15));
  days.forEach((d, i) => {
    const x0 = L + i * band;
    if (d.stats) {
      const left = x0 + (band - pairW) / 2;
      bars += `<path d="${rTop(left, y(d.stats.down.bps), bw)}" fill="var(--down)"/><path d="${rTop(left + pairW - bw, y(d.stats.up.bps), bw)}" fill="var(--up)"/>`;
    }
    if ((days.length - 1 - i) % every === 0) labels += `<text x="${x0 + band / 2}" y="${H - 8}" text-anchor="middle">${dayShort(d.day)}</text>`;
  });
  const el = document.getElementById('v-peak');
  el.innerHTML = `<svg viewBox="0 0 ${W} ${H}" role="img" aria-label="The fastest second of each day, down and up">
    ${yAxis(ticks, L, W - R, y)}<rect id="peak-wash" y="${T}" height="${pb - T}" fill="var(--hover-wash)" visibility="hidden"/>
    <g class="draw">${bars}</g>${labels}</svg>`;
  const svg = el.querySelector('svg'), wash = el.querySelector('#peak-wash');
  svg.addEventListener('pointermove', (e) => {
    const i = Math.floor((svgX(svg, e) - L) / band);
    if (i < 0 || i >= days.length) { wash.setAttribute('visibility', 'hidden'); return hideTip(); }
    wash.setAttribute('x', L + i * band); wash.setAttribute('width', band); wash.setAttribute('visibility', 'visible');
    const d = days[i], s = d.stats;
    showTip(`<b>${dayLong(d.day)}</b>` + (s
      ? tipRow('var(--down)', 'Peak down', rate(s.down.bps)) + tipRow('var(--up)', 'Peak up', rate(s.up.bps))
        + `<small style="margin-top:6px">Moved ${bytes(s.downBytes)} down · ${bytes(s.upBytes)} up</small><small>Recorded ${duration(s.seconds)}</small>`
      : 'Nothing recorded'), e.clientX, e.clientY);
  });
  svg.addEventListener('pointerleave', () => { wash.setAttribute('visibility', 'hidden'); hideTip(); });
}

/* ------------------------------------------------------------------ Test page: the gauge (Gauge.cs) */

const G_MIN = 1e4, G_MAX = 1e8, G_START = 135, G_SWEEP = 270, GX = 120, GY = 120, GR = 92;
const gFrac = (bps) => (!(bps > 0) ? 0 : Math.min(1, Math.max(0, (Math.log10(bps) - 4) / (Math.log10(G_MAX) - Math.log10(G_MIN)))));
const gFromFrac = (f) => (f <= 0 ? 0 : 10 ** (4 + f * 4));
const gPoint = (deg, r) => [GX + r * Math.cos((deg * Math.PI) / 180), GY + r * Math.sin((deg * Math.PI) / 180)];
function gArc(f) {
  const [x0, y0] = gPoint(G_START, GR), [x1, y1] = gPoint(G_START + G_SWEEP * f, GR);
  return `M${x0.toFixed(2)},${y0.toFixed(2)}A${GR},${GR} 0 ${G_SWEEP * f > 180 ? 1 : 0} 1 ${x1.toFixed(2)},${y1.toFixed(2)}`;
}

function makeGauge(el) {
  let target = 0, shown = 0, up = false, raf = 0, last = 0;
  function build() {
    let s = `<svg viewBox="0 0 264 222" role="img" aria-label="Live rate dial, logarithmic"><g transform="translate(12 10)">`
      + `<path d="${gArc(1)}" fill="none" stroke="var(--border-strong)" stroke-width="14" stroke-linecap="round"/>`
      + `<path d="${gArc(1)}" fill="none" stroke="var(--text-faint)" stroke-width="1" opacity="0.35"/>`;
    for (const dec of [1e4, 1e5, 1e6, 1e7]) for (const m of [dec * 2, dec * 5]) {
      const a = G_START + G_SWEEP * gFrac(m);
      const [ix, iy] = gPoint(a, GR - 6), [ox, oy] = gPoint(a, GR + 6);
      s += `<line x1="${ix}" y1="${iy}" x2="${ox}" y2="${oy}" stroke="var(--text-faint)" opacity="0.55"/>`;
    }
    s += '<path class="g-val" fill="none" stroke-width="14" stroke-linecap="round"/>';
    for (const t of [1e4, 1e5, 1e6, 1e7, 1e8]) {
      const a = G_START + G_SWEEP * gFrac(t);
      const [ix, iy] = gPoint(a, GR - 12), [ox, oy] = gPoint(a, GR + 12), [lx, ly] = gPoint(a, GR + 26);
      s += `<line x1="${ix}" y1="${iy}" x2="${ox}" y2="${oy}" stroke="var(--text-faint)" stroke-width="1.5"/><text x="${lx}" y="${ly + 3}" text-anchor="middle">${esc(rateTick(t))}</text>`;
    }
    s += `<line class="g-needle" x1="${GX}" y1="${GY}" stroke-width="3" stroke-linecap="round"/><circle class="g-hub" cx="${GX}" cy="${GY}" r="7" fill="var(--bg-inset)" stroke-width="2.5"/>`
      + `<text class="g-num" x="${GX}" y="${GY + 51}" text-anchor="middle">0</text><text class="g-unit" x="${GX}" y="${GY + 66}" text-anchor="middle"></text></g></svg>`;
    el.innerHTML = s;
    paint();
  }
  function paint() {
    const color = up ? 'var(--up)' : 'var(--down)';
    const f = gFrac(shown);
    const val = el.querySelector('.g-val');
    // Omitted at rest: a round cap would turn a zero-length arc into a dot.
    if (shown > 0) { val.setAttribute('d', gArc(Math.max(f, 0.004))); val.setAttribute('stroke', color); } else val.removeAttribute('d');
    const [nx, ny] = gPoint(G_START + G_SWEEP * f, GR - 20);
    const needle = el.querySelector('.g-needle');
    needle.setAttribute('x2', nx); needle.setAttribute('y2', ny); needle.setAttribute('stroke', color);
    el.querySelector('.g-hub').setAttribute('stroke', color);
    const [v, u] = shown > 0 ? splitRate(shown) : ['0', units === 'bits' ? 'bps' : 'B/s'];
    el.querySelector('.g-num').textContent = v;
    el.querySelector('.g-unit').textContent = u;
  }
  function frame(t) {
    const dt = Math.max(1, t - last);
    last = t;
    // Ease in log space, so 1 to 100 Mbps sweeps as evenly as 10 to 1000 Kbps.
    const from = gFrac(shown), to = gFrac(target);
    const next = from + (to - from) * (1 - Math.exp(-dt / 70));
    shown = Math.abs(to - next) < 0.002 ? target : gFromFrac(next);
    if (target === 0 && next < 0.003) shown = 0;
    paint();
    raf = shown === target ? 0 : requestAnimationFrame(frame);
  }
  build();
  return {
    set(bps, isUp) {
      target = Math.max(0, bps);
      up = isUp;
      if (reducedMotion() || (target === 0 && shown === 0)) { shown = target; cancelAnimationFrame(raf); raf = 0; paint(); return; }
      if (!raf) { last = performance.now(); raf = requestAnimationFrame(frame); }
    },
    rebuild: build,
  };
}

/* ------------------------------------------------------------------ Test page: a leg (SpeedChart.cs) */

function legSvg(points, isUp, average, span) {
  const W = 640, H = 180, L = 70, R = 4, T = 8, B = 24;
  const pb = H - B;
  const color = isUp ? 'var(--up)' : 'var(--down)';
  if (points.length < 2) return { html: `<svg viewBox="0 0 ${W} ${H}"></svg>` };
  const max = Math.max(...points.map((p) => p[1]), average ?? 0);
  const ticks = niceTicks(max, 3);
  const top = ticks[3];
  span = Math.max(1, span ?? points[points.length - 1][0]);
  const x = (t) => L + (t / span) * (W - L - R);
  const y = (v) => pb - (v / top) * (pb - T);
  const step = span > 30 ? 10 : span > 12 ? 5 : 2;
  let marks = '';
  for (let s = 0; s <= span + 0.01; s += step) marks += `<text x="${x(s)}" y="${H - 5}" text-anchor="middle">${Math.round(s)}s</text>`;
  const line = curve(points.map(([t, v]) => [x(t), y(v)]));
  const last = points[points.length - 1];
  const id = `g${Math.random().toString(36).slice(2, 8)}`;
  const avg = average > 0 ? `<line x1="${L}" x2="${W - R}" y1="${y(average)}" y2="${y(average)}" stroke="var(--text-dim)" stroke-dasharray="4 4"/>` : '';
  return {
    html: `<svg viewBox="0 0 ${W} ${H}" role="img" aria-label="${isUp ? 'Upload' : 'Download'} rate over the leg">
      <defs><linearGradient id="${id}" x1="0" x2="0" y1="0" y2="1"><stop offset="0" stop-color="${color}" stop-opacity="0.28"/><stop offset="1" stop-color="${color}" stop-opacity="0.02"/></linearGradient></defs>
      ${yAxis(ticks, L, W - R, y)}${marks}
      <path d="${line}L${x(last[0])},${pb}L${x(points[0][0])},${pb}Z" fill="url(#${id})"/>
      <path d="${line}" fill="none" stroke="${color}" stroke-width="2" stroke-linejoin="round"/>${avg}
      <line class="cur" y1="${T}" y2="${pb}" stroke="var(--text-faint)" stroke-dasharray="3 3" visibility="hidden"/>
      <circle class="dot" r="4.5" fill="${color}" stroke="var(--bg-panel)" stroke-width="2" visibility="hidden"/></svg>`,
    x, y, L, W, R, span,
  };
}

function drawLeg(el, points, isUp, average, span) {
  const g = legSvg(points, isUp, average, span);
  el.innerHTML = g.html;
  if (!g.x) return;
  const svg = el.querySelector('svg'), cur = svg.querySelector('.cur'), dot = svg.querySelector('.dot');
  svg.addEventListener('pointermove', (e) => {
    const t = Math.min(g.span, Math.max(0, ((svgX(svg, e) - g.L) / (g.W - g.L - g.R)) * g.span));
    const near = points.reduce((a, p) => (Math.abs(p[0] - t) < Math.abs(a[0] - t) ? p : a), points[0]);
    cur.setAttribute('x1', g.x(near[0])); cur.setAttribute('x2', g.x(near[0])); cur.setAttribute('visibility', 'visible');
    dot.setAttribute('cx', g.x(near[0])); dot.setAttribute('cy', g.y(near[1])); dot.setAttribute('visibility', 'visible');
    showTip(`<b>${near[0].toFixed(1)}s</b>${tipRow(isUp ? 'var(--up)' : 'var(--down)', isUp ? 'Upload' : 'Download', rate(near[1]))}`, e.clientX, e.clientY);
  });
  svg.addEventListener('pointerleave', () => { cur.setAttribute('visibility', 'hidden'); dot.setAttribute('visibility', 'hidden'); hideTip(); });
}

/** The shape of a leg: a ramp through the first second, a plateau with a sag, noise throughout. */
function legSeries(rnd, avg, seconds) {
  const pts = [];
  const n = Math.max(24, Math.round(seconds * 6));
  const sagAt = seconds * (0.4 + rnd() * 0.3);
  for (let i = 0; i <= n; i++) {
    const t = (i / n) * seconds;
    const ramp = t < 1 ? 0.25 + 0.75 * t ** 0.7 : 1;
    const sag = Math.abs(t - sagAt) < seconds * 0.06 ? 0.86 : 1;
    pts.push([t, avg * ramp * sag * (0.95 + rnd() * 0.1)]);
  }
  return pts;
}

const SAMPLE = (() => {
  const rnd = mulberry32(99);
  const r = RUNS[0];
  return { down: legSeries(rnd, r.downBps, r.downSeconds), up: legSeries(rnd, r.upBps, r.upSeconds), run: r };
})();

function drawLegs() {
  drawLeg(document.getElementById('v-leg-down'), SAMPLE.down, false, SAMPLE.run.downBps);
  drawLeg(document.getElementById('v-leg-up'), SAMPLE.up, true, SAMPLE.run.upBps);
}

/* ------------------------------------------------------------------ Test page: the run (TestPage.cs, simulated) */

const test = {
  profile: PROFILES[0], parallel: true, phase: 'ready', result: null,
  down: [], up: [], live: 0, moved: 0, target: 0, timer: 0, rnd: mulberry32(2026),
};
const PHASES = {
  ready: ['Ready', ' '],
  latency: ['Warming up', 'Timing small requests and counting lost pings.'],
  download: ['Downloading', 'Pulling from speed.cloudflare.com.'],
  upload: ['Uploading', 'Pushing to speed.cloudflare.com.'],
  saving: ['Saving', 'Writing the result beside the history.'],
  done: ['Done', 'The result is saved below with the other runs.'],
};
let testGauge, dialGauge;
const busy = () => ['latency', 'download', 'upload', 'saving'].includes(test.phase);

const ICON_DOWN = '<svg viewBox="0 0 24 24" stroke="var(--down)"><path d="M12 4v13M6 11l6 6 6-6M5 21h14"/></svg>';
const ICON_UP = '<svg viewBox="0 0 24 24" stroke="var(--up)"><path d="M12 20V7M6 13l6-6 6 6M5 3h14"/></svg>';

function legCard(isUp) {
  const r = test.phase === 'done' ? test.result : null;
  const [v, u] = r ? splitRate(isUp ? r.upBps : r.downBps) : ['-', ''];
  const peak = r ? `peak second ${rate(isUp ? r.peakUpBps : r.peakDownBps)}` : ' ';
  const moved = r ? `${bytes(isUp ? r.upBytes : r.downBytes)} over ${(isUp ? r.upSeconds : r.downSeconds).toFixed(1)}s` : ' ';
  return `<div class="leg-head"><span class="stat-label" style="margin:0">${isUp ? 'Upload' : 'Download'}</span>${isUp ? ICON_UP : ICON_DOWN}</div>
    <div class="leg-val"><div class="v"><b style="color:var(--${isUp ? 'up' : 'down'})">${v}</b><small>${u}</small></div><div class="lines"><div>${peak}</div><div>${moved}</div></div></div>
    <div class="chart"></div>`;
}

function drawTestLegs() {
  for (const isUp of [false, true]) {
    const el = document.getElementById(isUp ? 't-up' : 't-down');
    el.innerHTML = legCard(isUp);
    const r = test.phase === 'done' ? test.result : null;
    const pts = isUp ? test.up : test.down;
    const span = busy() && pts.length ? (isUp ? test.upSpan : test.downSpan) : undefined;
    drawLeg(el.querySelector('.chart'), pts, isUp, r ? (isUp ? r.upBps : r.downBps) : null, span);
  }
}

function latencyCards() {
  const r = test.phase === 'done' ? test.result : null;
  const ms = (v, d = 0) => `${v.toFixed(d)} ms`;
  const rise = (v) => `${v > 0 ? '+' : ''}${v.toFixed(0)} ms`;
  const pct = (f) => (f === 0 ? '0%' : `${f < 10 ? f.toFixed(1) : f.toFixed(0)}%`);
  const card = (label, value, unit, lines, icon) => `<div class="tcard"><div class="leg-head"><span class="stat-label" style="margin:0">${label}</span>${icon}</div>
    <div class="v"><b>${value}</b>${unit ? `<small>${unit}</small>` : ''}</div>${lines.map((l) => `<div class="w">${l ?? ' '}</div>`).join('')}</div>`;
  const i = (p) => `<svg viewBox="0 0 24 24" stroke="var(--text-faint)" style="width:17px;height:17px;fill:none;stroke-width:1.8">${p}</svg>`;
  const L = r?.loss, sent = { idle: 20, down: 90, up: 70 };
  const line = (k, label) => `${label} ${pct(L[k])} (${r.lost[k]} of ${sent[k]})`;
  document.getElementById('t-lat').innerHTML = [
    card('Latency', r ? r.latency.toFixed(0) : '-', r ? 'ms' : '', r ? ['idle', `download ${ms(r.loadedDown)} (${rise(r.loadedDown - r.latency)})`, `upload ${ms(r.loadedUp)} (${rise(r.loadedUp - r.latency)})`] : [null, null, null], i('<circle cx="12" cy="13" r="8"/><path d="M12 9v4l2 2M9 2h6"/>')),
    card('Jitter', r ? r.jitter.toFixed(1) : '-', r ? 'ms' : '', r ? ['idle', `download ${ms(r.loadedDownJitter, 1)}`, `upload ${ms(r.loadedUpJitter, 1)}`] : [null, null, null], i('<rect x="3" y="3" width="18" height="18" rx="3"/><path d="M6 13h3l2-5 2 9 2-4h3"/>')),
    card('Packet loss', r ? pct(r.lossTotal) : '-', '', r ? [line('idle', 'idle'), line('down', 'download'), line('up', 'upload')] : [null, null, null], i('<path d="M12 3 2 20h20L12 3zM12 10v4M12 17h.01"/>')),
  ].join('');
}

function drawNotes() {
  const r = test.phase === 'done' ? test.result : null;
  let html = '';
  const bloat = r ? Math.max(r.loadedDown, r.loadedUp) - r.latency : 0;
  if (r && bloat >= 60) {
    html += `<div class="tnote"><b>Latency rose ${bloat.toFixed(0)} ms while the link was busy.</b> That is bufferbloat: something between here and Cloudflare, usually the router, sometimes the ISP, queues packets instead of dropping them when it runs out of capacity. It is why a call breaks up when someone starts a download, and it is invisible to a latency test on an idle connection.</div>`;
  }
  document.getElementById('t-notes').innerHTML = html;
}

function drawRunCard() {
  const [title, sub] = PHASES[test.phase];
  document.getElementById('t-title').textContent = title;
  document.getElementById('t-sub').textContent = sub;
  const sizes = document.getElementById('t-sizes');
  sizes.innerHTML = PROFILES.map((p) => `<button type="button" class="chip-btn" role="radio" aria-checked="${p === test.profile}" data-p="${p.id}" ${busy() ? 'disabled' : ''}>${p.label} · ${bytes(p.down + p.up)}</button>`).join('');
  sizes.querySelectorAll('[data-p]').forEach((b) => b.addEventListener('click', () => { test.profile = PROFILES.find((p) => p.id === b.dataset.p); drawRunCard(); }));
  const conns = document.getElementById('t-conns');
  conns.innerHTML = `<button type="button" class="chip-btn" role="radio" aria-checked="${test.parallel}" data-c="1" ${busy() ? 'disabled' : ''} data-tip="Several transfers at once: how fast the line is">Parallel ×${test.profile.streams}</button>`
    + `<button type="button" class="chip-btn" role="radio" aria-checked="${!test.parallel}" data-c="0" ${busy() ? 'disabled' : ''} data-tip="One transfer, as a single download gets: often well below the line, limited by window size and round trip">Single</button>`;
  conns.querySelectorAll('[data-c]').forEach((b) => b.addEventListener('click', () => { test.parallel = b.dataset.c === '1'; drawRunCard(); }));
  const go = document.getElementById('t-go');
  go.textContent = busy() ? 'Stop' : 'Start test';
  go.classList.toggle('stop', busy());
  go.disabled = test.phase === 'saving';
  drawProgress();
}

function drawProgress() {
  const on = busy() && test.phase !== 'saving' && test.phase !== 'latency';
  document.getElementById('t-progress').classList.toggle('on', on);
  const f = test.target > 0 ? Math.min(1, test.moved / test.target) : 0;
  const fill = document.getElementById('t-pfill');
  fill.style.width = `${f * 100}%`;
  fill.style.background = test.phase === 'upload' ? 'var(--up)' : 'var(--down)';
  document.getElementById('t-ptext').textContent = on ? `${bytes(test.moved)} of ${bytes(test.target)} transferred` : ' ';
  testGauge.set(busy() ? test.live : 0, test.phase === 'upload');
}

function drawTest() { drawRunCard(); drawTestLegs(); latencyCards(); drawNotes(); }

function enter(phase) { test.phase = phase; drawTest(); }

/*
  The simulation: a real run's figures (makeRun), revealed over a few
  seconds, twenty steps a second on a timer (animation frames stop in a
  background tab, and a run should still finish there). Each leg's chart is drawn against its real duration, so the x-axis
  reads as the app's would; only the clock is sped up.
*/
function startTest() {
  // Stamps are read as UTC fields, so the visitor's local clock is shifted into them.
  const now = new Date();
  const r = makeRun(test.rnd, new Date(now.getTime() - now.getTimezoneOffset() * 60000), test.profile, { single: !test.parallel });
  r.downBytes = Math.min(test.profile.down, r.downBps * r.downSeconds);
  r.upBytes = Math.min(test.profile.up, r.upBps * r.upSeconds);
  test.result = r;
  test.down = []; test.up = [];
  const downAll = legSeries(test.rnd, r.downBps, r.downSeconds), upAll = legSeries(test.rnd, r.upBps, r.upSeconds);
  test.downSpan = r.downSeconds; test.upSpan = r.upSeconds;
  test.downAll = downAll; test.upAll = upAll;
  enter('latency');
  const plan = [['latency', 1400], ['download', 4200], ['upload', 3400], ['saving', 500]];
  let k = 0, t0 = performance.now();
  const tick = () => {
    const [phase, ms] = plan[k];
    const p = Math.min(1, (performance.now() - t0) / ms);
    if (phase === 'download' || phase === 'upload') {
      const all = phase === 'download' ? downAll : upAll;
      const shown = all.slice(0, Math.max(2, Math.ceil(p * all.length)));
      if (phase === 'download') test.down = shown; else test.up = shown;
      test.live = shown[shown.length - 1][1];
      test.target = phase === 'download' ? test.profile.down : test.profile.up;
      test.moved = p * (phase === 'download' ? r.downBytes : r.upBytes);
      drawProgress();
      const el = document.getElementById(phase === 'download' ? 't-down' : 't-up').querySelector('.chart');
      drawLeg(el, shown, phase === 'upload', null, phase === 'download' ? r.downSeconds : r.upSeconds);
    }
    if (p >= 1) {
      k++;
      t0 = performance.now();
      if (k >= plan.length) { finishTest(); return; }
      test.live = 0;
      enter(plan[k][0]);
    }
    test.timer = setTimeout(tick, 50);
  };
  test.timer = setTimeout(tick, 50);
}

function stopTest() {
  clearTimeout(test.timer);
  test.down = []; test.up = []; test.live = 0;
  enter('ready');
}

function finishTest() {
  test.down = test.downAll; test.up = test.upAll;
  test.live = 0;
  const r = test.result;
  r.simulated = true;
  r.meterSaw = null;
  RUNS.unshift(r);
  runsPage = 1;
  enter('done');
  drawRuns(true);
}

/* ------------------------------------------------------------------ Test page: previous runs (and the pager, Pages.cs) */

const PAGE_SIZE = 25;
let runsPage = 1;

function pageItems(page, n, around = 2) {
  if (n <= 1) return [1];
  const cur = Math.min(Math.max(page, 1), n);
  const shown = new Set([1, n]);
  for (let p = cur - around; p <= cur + around; p++) if (p >= 1 && p <= n) shown.add(p);
  const items = [];
  let prev = null;
  for (const p of [...shown].sort((a, b) => a - b)) {
    // A gap never stands in for a single page: that page is shown instead.
    if (prev !== null) { if (p - prev === 2) items.push(prev + 1); else if (p - prev > 2) items.push(null); }
    items.push(p);
    prev = p;
  }
  return items;
}

function drawRuns(fresh = false) {
  const pages = Math.max(1, Math.ceil(RUNS.length / PAGE_SIZE));
  runsPage = Math.min(Math.max(runsPage, 1), pages);
  const slice = RUNS.slice((runsPage - 1) * PAGE_SIZE, runsPage * PAGE_SIZE);
  const dash = (v) => (v == null ? '–' : v.toFixed(0));
  const pct = (f) => `${f.toFixed(1)}%`;
  const body = slice.map((r, i) => {
    const size = r.profile + (r.connections ? ` · ${r.connections === 1 ? 'single' : `×${r.connections}`}` : '') + (r.device ? ` · ${r.device === 'phone' ? 'phone' : 'other device'}` : '');
    const lat = r.loadedDown == null && r.loadedUp == null ? `${r.latency.toFixed(0)} ms` : `${r.latency.toFixed(0)} / ${dash(r.loadedDown)} / ${dash(r.loadedUp)} ms`;
    const loss = r.loss ? `<td class="num dim" data-tip="${esc(`idle ${pct(r.loss.idle)} · download ${pct(r.loss.down)} · upload ${pct(r.loss.up)} · ${r.lost.idle + r.lost.down + r.lost.up} of 180 pings lost`)}">${pct(r.lossTotal)}</td>` : '<td class="num dim">—</td>';
    const ran = r.tooShort ? `<td class="num warn" data-tip="Too short to be reliable">${r.downSeconds.toFixed(1)}s ⚠</td>` : `<td class="num dim">${r.downSeconds.toFixed(1)}s</td>`;
    const saw = r.device ? '<td class="num dim" data-tip="Run on another device; the meter only sees this PC">—</td>'
      : r.simulated ? '<td class="num dim" data-tip="Simulated on this page, so there was nothing for a meter to see. In the app: a dash until every minute the run touched is written.">—</td>'
        : `<td class="num dim">${rate(r.meterSaw)}</td>`;
    return `<tr${fresh && i === 0 && runsPage === 1 ? ' class="fresh"' : ''}><td class="mono">${stamp(r.at)}</td><td class="dim">${esc(size)}</td>
      <td class="num down">${rate(r.downBps)}</td><td class="num up">${rate(r.upBps)}</td><td class="num dim">${lat}</td>${loss}${ran}${saw}<td class="num dim">${bytes(r.downBytes + r.upBytes)}</td></tr>`;
  }).join('');
  const info = (t) => `<span class="info" data-tip="${esc(t)}">i</span>`;
  const step = (label, to, enabled, tip) => `<button type="button" class="chip-btn" data-go="${to}" ${enabled ? '' : 'disabled'} data-tip="${tip}">${label}</button>`;
  const pager = pages <= 1 ? '' : `<div class="pager">${step('Newest', 1, runsPage > 1, 'First page: the newest runs')}${step('← Newer', runsPage - 1, runsPage > 1, 'Previous page')}
      ${pageItems(runsPage, pages).map((n) => (n === null ? '<span class="gap">…</span>' : `<button type="button" class="chip-btn" role="radio" aria-checked="${n === runsPage}" data-go="${n}" aria-label="Page ${n}">${n}</button>`)).join('')}
      ${step('Older →', runsPage + 1, runsPage < pages, 'Next page')}${step('Oldest', pages, runsPage < pages, 'Last page: the oldest runs')}
      <span class="jump">Go to page <input type="number" min="1" max="${pages}" value="${runsPage}" aria-label="Page number, 1 to ${pages}"> of ${pages} <button type="button" class="chip-btn" data-jump>Go</button></span></div>`;
  const el = document.getElementById('v-runs');
  el.innerHTML = `<div class="table-wrap"><table><thead><tr><th>When</th><th>Size</th><th class="num">Download</th><th class="num">Upload</th>
      <th class="num">Latency${info('<b>Latency</b>Idle / during download / during upload, each in its fixed place; a dash for a figure the run does not have.')}</th>
      <th class="num">Loss</th><th class="num">Ran for</th>
      <th class="num">Meter saw${info('<b>Meter saw</b>The fastest second the meter logged at the adapter during the run: a completely separate measurement. A dash until every minute the run touched has been written (the meter writes once a minute), or when the meter was not running.')}</th>
      <th class="num">Data spent</th></tr></thead><tbody>${body}</tbody></table></div>${pager}
    <div class="note">Results are kept in <code>speedtests.jsonl</code> beside the history, one line per run, and survive anything that keeps the history.</div>`;
  const first = (runsPage - 1) * PAGE_SIZE + 1, last = Math.min(runsPage * PAGE_SIZE, RUNS.length);
  document.getElementById('v-runs-count').textContent = `${first}–${last} of ${count(RUNS.length)}`;
  el.querySelectorAll('[data-go]').forEach((b) => b.addEventListener('click', () => { runsPage = +b.dataset.go; drawRuns(); }));
  const input = el.querySelector('.jump input');
  const submit = () => { const v = parseInt(input.value, 10); if (v >= 1) { runsPage = Math.min(v, pages); drawRuns(); } };
  el.querySelector('[data-jump]')?.addEventListener('click', submit);
  input?.addEventListener('keydown', (e) => { if (e.key === 'Enter') submit(); });
}

/* ------------------------------------------------------------------ the rest */

function drawConn() {
  // Documentation values only (RFC 5737's address, RFC 5398's AS number): never a real address.
  document.getElementById('v-conn').innerHTML = `
    <div><div class="stat-label">Your network</div><div class="v">Example Broadband</div><div class="f">AS64500 · Springfield, Example Region</div></div>
    <div><div class="stat-label">Public address</div><div class="v mono">203.0.113.24</div><div class="f">as seen from outside your router</div></div>
    <div><div class="stat-label">Serving datacentre</div><div class="v">Example City (EXA)</div><div class="f">Example Region · over HTTP/1.1</div></div>
    <div class="conn-foot">The datacentre is whichever one Cloudflare routes you to, so a result says as much about the path to it as about your line. A distant one is itself a finding.</div>`;
}

function drawDashTop() {
  document.getElementById('v-dashtop').innerHTML = `<span class="tab on">Speed</span><span class="tab">Test</span>
    <span class="src">Wi-Fi</span><span class="d">↓ ${speedLong(live.down)}</span><span class="u">↑ ${speedLong(live.up)}</span><span class="gear" aria-hidden="true">⚙</span>`;
}

function drawToasts() {
  const r = RUNS.find((x) => !x.simulated) ?? RUNS[0];
  const toast = (title, text, warn) => `<div class="toast${warn ? ' warn' : ''}"><div class="app"><img src="favicon.svg" alt="">Internet Speed Meter</div><b>${esc(title)}</b><span>${esc(text)}</span></div>`;
  document.getElementById('v-toasts').innerHTML = [
    toast('Speed test finished', `↓ ${rate(r.downBps)}   ↑ ${rate(r.upBps)}   ${r.latency.toFixed(0)} ms`),
    toast('Speed history is not being recorded', 'The history folder is not reachable. Details are in error.log beside settings.ini.', true),
    toast('Internet Speed Meter is still running', 'The meter keeps recording, and the dashboard opens again from the taskbar text or the tray icon. Right-click either to exit.'),
  ].join('');
}

/** Everything that shows a rate: redrawn on a units or theme change. */
function drawAll() {
  drawTaskbars(); drawIcon(); drawFlyouts(); drawMenus(); drawFigs(); drawPeak(); drawLegs();
  testGauge.rebuild(); dialGauge.rebuild(); setDial(); drawTest(); drawConn(); drawRuns(); drawDashTop(); drawToasts();
}

function setDial() {
  const v = +document.getElementById('dial-range').value / 1000;
  dialGauge.set(gFromFrac(v), false);
}

/** Once a second, as the meter samples. */
function tickLive() {
  live.step();
  document.querySelectorAll('.readout').forEach((el) => {
    const spans = el.querySelectorAll('span');
    spans[0].textContent = speedLong(live.down);
    spans[1].textContent = speedLong(live.up);
  });
  document.querySelectorAll('.tb-icon').forEach((c) => paintIcon(c, METER[meterState.tbTheme], true));
  document.getElementById('hero-flyout').innerHTML = flyoutHtml(METER.dark);
  drawIcon(); drawFlyouts(); drawDashTop();
}

/* ------------------------------------------------------------------ wiring */

function wireTooltip() {
  document.addEventListener('pointermove', (e) => {
    if (e.target.closest && e.target.closest('.chart svg') && !e.target.closest('[data-tip]')) return; // charts run their own
    const t = e.target.closest && e.target.closest('[data-tip]');
    if (!t) return hideTip();
    showTip(t.getAttribute('data-tip'), e.clientX, e.clientY);
  });
  document.addEventListener('scroll', hideTip, { passive: true });
}

/** Snapshot.ps1 writes the Speed and Test pages dark and `-light`; the panel shows both taskbars in one. */
function shotSrc(btn) {
  return `screenshots/${btn.dataset.shot}${btn.dataset.themed === '1' && isLight() ? '-light' : ''}.webp`;
}
function selectShot(btn) {
  document.querySelectorAll('.tabs [role="tab"]').forEach((b) => b.setAttribute('aria-selected', String(b === btn)));
  const img = document.getElementById('shot-img');
  img.src = shotSrc(btn);
  img.alt = btn.dataset.cap;
  const scroller = document.querySelector('.shot-scroll');
  scroller.classList.toggle('fit', btn.dataset.themed === '0');
  scroller.scrollTop = 0;
}

function pickRadio(group, btn) {
  group.querySelectorAll('[role="radio"]').forEach((x) => x.setAttribute('aria-checked', String(x === btn)));
}

function setUnits(u) {
  units = u;
  pickRadio(document.getElementById('units'), document.querySelector(`#units [data-units="${u}"]`));
  drawAll();
}

function setTheme(t) {
  if (t === 'system') { try { localStorage.removeItem(THEME_KEY); } catch { /* still switches */ } applyTheme(systemTheme()); }
  else { applyTheme(t); try { localStorage.setItem(THEME_KEY, t); } catch { /* private window: still switches */ } }
  drawAll();
  const tab = document.querySelector('.tabs [aria-selected="true"]');
  if (tab) document.getElementById('shot-img').src = shotSrc(tab);
  heroShot();
}

function heroShot() {
  document.getElementById('hero-shot').src = `screenshots/speed${isLight() ? '-light' : ''}.webp`;
}

function menuAction(act) {
  const [k, v] = act.split(':');
  if (k === 'units') return setUnits(v);
  if (k === 'theme') return setTheme(v);
  if (k === 'side') { meterState.side = v; pickRadio(document.getElementById('tb-side'), document.querySelector(`#tb-side [data-v="${v}"]`)); }
  if (k === 'layout') { meterState.layout = v; pickRadio(document.getElementById('ico-layout'), document.querySelector(`#ico-layout [data-v="${v}"]`)); }
  if (k === 'unitletters') { meterState.unitLetters = !meterState.unitLetters; document.getElementById('ico-unit').checked = meterState.unitLetters; }
  if (k === 'uptop') { meterState.upTop = !meterState.upTop; document.getElementById('ico-uptop').checked = meterState.upTop; }
  if (k === 'reset') { live.session.down = 0; live.session.up = 0; live.session.peak = 0; live.session.start = Date.now(); live.hist.down.fill(0); live.hist.up.fill(0); }
  if (k === 'test') { document.querySelector('#t-run').scrollIntoView({ block: 'center' }); return; }
  drawTaskbars(); drawIcon(); drawFlyouts(); drawMenus();
}

document.addEventListener('DOMContentLoaded', () => {
  testGauge = makeGauge(document.getElementById('t-gauge'));
  dialGauge = makeGauge(document.getElementById('v-dial'));

  document.getElementById('theme-btn').addEventListener('click', () => setTheme(isLight() ? 'dark' : 'light'));
  window.matchMedia('(prefers-color-scheme: light)').addEventListener('change', () => {
    if (!storedTheme()) setTheme('system');
  });

  const tabs = document.querySelectorAll('.tabs [role="tab"]');
  tabs.forEach((b) => b.addEventListener('click', () => selectShot(b)));
  selectShot(tabs[0]);
  heroShot();

  document.querySelectorAll('#units [role="radio"]').forEach((b) => b.addEventListener('click', () => setUnits(b.dataset.units)));
  const seg = (id, fn) => {
    const g = document.getElementById(id);
    g.querySelectorAll('[role="radio"]').forEach((b) => b.addEventListener('click', () => { pickRadio(g, b); fn(b); }));
  };
  seg('tb-theme', (b) => { meterState.tbTheme = b.dataset.v; drawTaskbars(); });
  seg('tb-side', (b) => { meterState.side = b.dataset.v; drawTaskbars(); drawMenus(); });
  seg('ico-layout', (b) => { meterState.layout = b.dataset.v; drawIcon(); drawTaskbars(); drawMenus(); });
  seg('peak-range', (b) => { peakRange = b.dataset.range === 'all' ? 36500 : +b.dataset.range; drawPeak(); });
  document.getElementById('ico-unit').addEventListener('change', (e) => { meterState.unitLetters = e.target.checked; drawIcon(); drawTaskbars(); drawMenus(); });
  document.getElementById('ico-uptop').addEventListener('change', (e) => { meterState.upTop = e.target.checked; drawIcon(); drawTaskbars(); drawMenus(); });

  document.getElementById('cards-reload').addEventListener('click', () => {
    drawFigs(true);
    setTimeout(() => drawFigs(false), 900);
  });
  document.getElementById('dial-range').addEventListener('input', setDial);
  document.getElementById('t-go').addEventListener('click', () => (busy() ? stopTest() : startTest()));

  // Menus: a submenu row toggles its list; an action row does what it says.
  document.addEventListener('click', (e) => {
    const sub = e.target.closest('[data-sub]');
    if (sub) {
      const id = sub.dataset.sub;
      if (menuOpen.has(id)) menuOpen.delete(id); else menuOpen.add(id);
      drawMenus();
      return;
    }
    const act = e.target.closest('[data-act]');
    if (act) menuAction(act.dataset.act);
  });
  document.addEventListener('keydown', (e) => {
    if ((e.key === 'Enter' || e.key === ' ') && e.target.matches?.('[data-sub]')) { e.preventDefault(); e.target.click(); }
  });

  wireTooltip();
  drawAll();
  setInterval(tickLive, 1000);
  // The taskbar clocks move on the minute; once a minute is close enough.
  setInterval(drawTaskbars, 60000);
});
