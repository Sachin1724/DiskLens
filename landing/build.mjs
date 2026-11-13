// Builds ../landing.html - one self-contained page, no framework, no runtime CDN.
// The concept and the system it is built to are in DESIGN.md.
//
//   node landing/build.mjs
import { writeFileSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import * as k from "../../../../.claude/skills/anatomy/kit/iso-kit.mjs";
import { kitScript } from "../../../../.claude/skills/anatomy/scripts/inline-kit.mjs";
import { buildSvg, needleAt, HEAD_REACH, P, TRUTH, DIAL, DIAL_R, DIAL_Z, DIAL_H } from "./figure.mjs";

const HERE = dirname(fileURLToPath(import.meta.url));
const n = (value) => value.toLocaleString("en-US");
const WINDOW_SHOT = "data:image/jpeg;base64," + readFileSync(join(HERE, "window.jpg")).toString("base64");
const RELEASE = "https://github.com/Sachin1724/DiskLens/releases/latest/download/DiskLens-Setup.exe";

// Every byte count below was measured by DiskLens on the machine it was built on.
// GB, because that is how the app prints them.
const LEVELS = [
  {
    path: "C:\\",
    note: "952.7 GB formatted, 705.2 GB of it spoken for",
    blocks: [
      ["Users", 448.7, "data"],
      ["Program Files", 96.8, "system"],
      ["Windows", 46.7, "system"],
      ["Program Files (x86)", 45.6, "system"],
      ["files here", 36.0, "files"],
      ["ProgramData", 37.6, "system"],
      ["Android", 5.7, "unknown"],
      ["src", 3.1, "unknown"],
      ["the rest", 0.8, "unknown"],
    ],
  },
  {
    path: "C:\\Users\\sachi",
    note: "one profile, 448.7 GB of it",
    blocks: [
      ["AppData", 191.9, "unknown"],
      ["Videos", 60.1, "data"],
      ["Downloads", 33.0, "data"],
      ["Pictures", 17.5, "data"],
      [".gemini", 16.6, "unknown"],
      ["odia-lm", 16.0, "unknown"],
      ["Music", 14.4, "data"],
      ["VirtualBox VMs", 13.3, "heavy"],
      ["anaconda3", 11.6, "unknown"],
      [".android", 8.5, "unknown"],
      ["Documents", 6.0, "data"],
      ["113 smaller", 8.2, "unknown"],
    ],
  },
  {
    path: "C:\\Users\\sachi\\AppData\\Local",
    note: "the folder nobody opens, 191.9 GB of it",
    blocks: [
      ["Docker", 33.2, "heavy"],
      ["wsl", 26.5, "heavy"],
      ["hermes", 23.9, "unknown"],
      ["Programs", 17.7, "unknown"],
      ["Android", 17.5, "unknown"],
      ["npm-cache", 16.1, "cache"],
      ["uv", 15.9, "cache"],
      ["Google", 13.6, "unknown"],
      ["Arduino15", 9.5, "unknown"],
      ["Microsoft", 7.7, "system"],
      ["the rest", 10.3, "unknown"],
    ],
  },
];

const FEATURES = [
  ["Map", "A squarified treemap of the folder you are in. Loose files get a block of their own, so a 36 GB pagefile stops hiding behind a folder list.", "lime", "wide", "Browse"],
  ["Clean, by project", "A node_modules is judged by the repo around it, not by itself. Cold for a month and the project dead too, it is marked drop. Edited yesterday, it stays.", "violet", "wide", "Clean"],
  ["Copies", `Size buckets first, then only the collisions are read: 3.2M files seen, 462 hashed, ${TRUTH.dupesGb} GB of the same bytes in more than one place.`, "cyan", "tall", "Find"],
  ["Files", "The 300 biggest single files with the date each was last written. A 40 GB ISO from 2021 shows up here, not hidden in a folder total.", "plain", "", "Find"],
  ["Apps", "Every installed program by size, with the install date and when its folder last changed. Uninstall runs the vendor's own uninstaller.", "plain", "", "Remove"],
  ["System", "Hibernation file, Windows.old, update cache, WSL and Docker disks, the Recycle Bin. DiskLens never deletes these; it shows the size and the one command that shrinks each.", "plain", "", "Explain"],
  ["Hotspots", "The biggest folders anywhere on the drive, with pure containers filtered out so you get the culprit, not its parent.", "plain", "", "Find"],
  ["Table", "The same numbers, sortable and keyboard-navigable. A treemap alone is a poor experience without a mouse.", "plain", "", "Browse"],
  ["Recycle Bin, always", "Nothing is deleted outright, and every removal is logged with its size. Deletes patch the index in place, so numbers update in 0.3 s.", "plain", "", "Safe"],
];

const STEPS = [
  ["Sweep once", `${n(TRUTH.files)} files, ${n(TRUTH.folders)} folders, sized in ${TRUTH.walkSeconds} s across every core.`],
  ["Keep the drawer", `The index is saved, ${TRUTH.indexMb} MB. Tomorrow it opens in ${TRUTH.warmSeconds} s.`],
  ["Read, never re-read", "Every view answers from the index, so drilling is instant."],
  ["Patch, never rescan", "Delete something and the index is corrected in place."],
];

const figure = k.plateHtml({
  fig: "Fig 1",
  title: "The card-index bench",
  hint: "Drag the reader arm across the platter",
  readout: "pass 0% · drawer empty",
  keys: [
    { mark: "lit", label: "The head on the arm: the single pass over the drive" },
    { mark: "raised", label: "The drawer fills with one card per folder. The tray, the gauge and the bin read the drawer, never the platter" },
  ],
  caption:
    `One sweep reads every track and drops a card in the drawer for each folder it finds: ` +
    `${n(TRUTH.files)} files and ${n(TRUTH.folders)} folders in ${TRUTH.walkSeconds} seconds. ` +
    `The drawer is saved as a ${TRUTH.indexMb} MB index, so the next start opens from the drawer in ` +
    `${TRUTH.warmSeconds} seconds instead of sweeping again.`,
  body: `<div class="iso-stage" tabindex="0" role="slider"
      aria-label="Reader arm: arrow keys sweep it across the platter"
      aria-valuemin="0" aria-valuemax="100" aria-valuenow="0">${buildSvg()}</div>`,
});

// --------------------------------------------------------------------- live ---
const live = `
${kitScript()}

const LEVELS = ${JSON.stringify(LEVELS)};
const REDUCED = matchMedia("(prefers-reduced-motion: reduce)");
const EASE = (t) => 1 - Math.pow(1 - t, 3);
const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));
const lerp = (a, b, t) => a + (b - a) * t;

/* ---------------------------------------------------------------- one clock ---
   Everything on this page moves off a single rAF: the treemap, the reveals and
   the Fig 1 figure. No element owns a loop of its own. */
const jobs = new Set();
let running = false, last = 0;
function frame(now) {
  const dt = last ? Math.min((now - last) / 1000, 1 / 30) : 1 / 60;
  last = now;
  for (const job of jobs) {
    try { job(dt, now); }
    catch (e) { jobs.delete(job); console.error(e); }   // one broken job, not a dead page
  }
  running = jobs.size > 0;
  if (running) requestAnimationFrame(frame);
  else last = 0;
}
function join(job) {
  jobs.add(job);
  if (!running) { running = true; last = 0; requestAnimationFrame(frame); }
}
const leave = (job) => jobs.delete(job);

/* --------------------------------------------------------------- the treemap ---
   The same squarified layout the app ships, so the page is drawn by the product
   rather than illustrated to look like it. */
function squarify(items, x, y, w, h) {
  const out = [];
  const total = items.reduce((s, i) => s + i.value, 0);
  if (total <= 0 || w <= 0 || h <= 0) return out;
  const scale = (w * h) / total;
  let rest = items.slice(), rx = x, ry = y, rw = w, rh = h;
  const worst = (row, side) => {
    const sum = row.reduce((s, i) => s + i.value * scale, 0);
    const max = Math.max(...row.map((i) => i.value * scale));
    const min = Math.min(...row.map((i) => i.value * scale));
    const s2 = side * side;
    return Math.max((s2 * max) / (sum * sum), (sum * sum) / (s2 * min));
  };
  while (rest.length) {
    const horizontal = rw >= rh;
    const side = horizontal ? rh : rw;
    const row = [rest[0]];
    let i = 1;
    while (i < rest.length && worst(row.concat(rest[i]), side) <= worst(row, side)) { row.push(rest[i]); i++; }
    const sum = row.reduce((s, it) => s + it.value * scale, 0);
    const thick = side > 0 ? sum / side : 0;
    let off = 0;
    for (const it of row) {
      const len = thick > 0 ? (it.value * scale) / thick : 0;
      out.push(horizontal ? { it, x: rx, y: ry + off, w: thick, h: len } : { it, x: rx + off, y: ry, w: len, h: thick });
      off += len;
    }
    if (horizontal) { rx += thick; rw -= thick; } else { ry += thick; rh -= thick; }
    rest = rest.slice(i);
    if (rw <= 0.5 || rh <= 0.5) break;
  }
  for (const o of out) { o.w = Math.max(0, Math.min(o.w, x + w - o.x)); o.h = Math.max(0, Math.min(o.h, y + h - o.y)); }
  return out;
}

const stage = document.getElementById("stage");
const field = document.getElementById("field");
const crumb = document.getElementById("crumb");
const note = document.getElementById("note");
const pager = [...document.querySelectorAll("[data-level-dot]")];
const GAP = 6;

// One pool of tiles, reused across levels: a block travels to its new rectangle
// instead of fading out and a different one fading in.
const POOL = Math.max(...LEVELS.map((l) => l.blocks.length));
const tiles = [];
for (let i = 0; i < POOL; i++) {
  const el = document.createElement("button");
  el.className = "tile";
  el.type = "button";
  el.tabIndex = -1;
  el.innerHTML = '<b></b><i></i>';
  field.appendChild(el);
  tiles.push({ el, now: null, to: null, from: null, t: 1 });
}

let levelAt = 0;          // fractional: 0 -> 1 -> 2 as you scroll the stage
let shown = -1;
let breathe = 0;

function layoutFor(level, w, h, swap) {
  const blocks = LEVELS[level].blocks.map(([name, gb, kind], i) => ({ name, gb, kind, i, value: gb }));
  // The idle breath re-orders the two smallest-but-one blocks, so the picture
  // keeps settling at rest without anything jumping.
  if (swap && blocks.length > 3) {
    const a = blocks.length - 2, b = blocks.length - 3;
    [blocks[a].value, blocks[b].value] = [blocks[b].value, blocks[a].value];
  }
  return squarify(blocks.map((b) => ({ value: b.value, b })), 0, 0, w, h);
}

function place(tile, rect, w, h) {
  const { el } = tile;
  el.style.width = Math.max(0, rect.w - GAP) + "px";
  el.style.height = Math.max(0, rect.h - GAP) + "px";
  el.style.transform = "translate3d(" + (rect.x + GAP / 2) + "px," + (rect.y + GAP / 2) + "px,0)";
  const small = rect.w < 96 || rect.h < 52;
  el.dataset.small = small ? "1" : "0";
}

function paint(level, swap, w, h) {
  const rects = layoutFor(level, w, h, swap);
  const blocks = LEVELS[level].blocks;
  tiles.forEach((tile, i) => {
    const rect = rects[i];
    if (!rect) { tile.el.style.opacity = "0"; tile.el.tabIndex = -1; tile.el.setAttribute("aria-hidden", "true"); return; }
    const [name, gb, kind] = blocks[rect.it.b.i];
    tile.el.style.opacity = "1";
    tile.el.removeAttribute("aria-hidden");
    tile.el.tabIndex = 0;
    tile.el.dataset.kind = kind;
    tile.el.querySelector("b").textContent = name;
    tile.el.querySelector("i").textContent = gb >= 100 ? gb.toFixed(0) + " GB" : gb.toFixed(1) + " GB";
    tile.el.setAttribute("aria-label", name + ", " + gb + " gigabytes, in " + LEVELS[level].path);
    place(tile, rect, w, h);
  });
  if (shown !== level) {
    shown = level;
    crumb.textContent = LEVELS[level].path;
    note.textContent = LEVELS[level].note;
    pager.forEach((dot, i) => dot.setAttribute("aria-current", String(i === level)));
  }
}

function size() {
  const box = field.getBoundingClientRect();
  return [box.width, box.height];
}

let lastSwap = false;
function drawTreemap(dt, now) {
  const [w, h] = size();
  if (w < 4 || h < 4) return;
  breathe += dt;
  const swap = REDUCED.matches ? false : Math.floor(breathe / 5.5) % 2 === 1;
  const level = Math.round(levelAt);
  if (level !== shown || swap !== lastSwap || field.dataset.dirty === "1") {
    lastSwap = swap;
    field.dataset.dirty = "0";
    paint(level, swap, w, h);
  }
}

// Scroll drives the drill: the stage is pinned for two extra screens, one per level.
let lastY = 0;
function navDirection() {
  const y = Math.max(0, window.scrollY);
  if (Math.abs(y - lastY) > 6) {
    document.body.dataset.nav = y > lastY && y > 240 ? "down" : "up";
    lastY = y;
  }
}

function onScroll() {
  navDirection();
  const box = stage.getBoundingClientRect();
  const span = stage.offsetHeight - window.innerHeight;
  const done = span > 0 ? clamp(-box.top / span, 0, 1) : 0;
  levelAt = done * (LEVELS.length - 1);
  document.documentElement.style.setProperty("--drill", done.toFixed(4));
}

addEventListener("scroll", onScroll, { passive: true });
addEventListener("resize", () => { field.dataset.dirty = "1"; onScroll(); }, { passive: true });
onScroll();

new IntersectionObserver((entries) => {
  for (const e of entries) {
    if (e.isIntersecting) join(drawTreemap);
    else leave(drawTreemap);
  }
}, { rootMargin: "200px 0px" }).observe(stage);

field.addEventListener("click", (event) => {
  const tile = event.target.closest(".tile");
  if (!tile) return;
  const next = Math.min(LEVELS.length - 1, Math.round(levelAt) + 1);
  const span = stage.offsetHeight - window.innerHeight;
  window.scrollTo({ top: stage.offsetTop + (span * next) / (LEVELS.length - 1), behavior: REDUCED.matches ? "auto" : "smooth" });
});

/* ------------------------------------------------------------------ reveals ---
   One treatment, used once per section: the section rises into place as it
   enters, then never animates again. */
const revealed = new WeakSet();
new IntersectionObserver((entries) => {
  for (const e of entries) {
    if (!e.isIntersecting || revealed.has(e.target)) continue;
    revealed.add(e.target);
    e.target.dataset.in = "1";
  }
}, { rootMargin: "0px 0px -12% 0px" }).forEach;
const revealObserver = new IntersectionObserver((entries, obs) => {
  for (const e of entries) {
    if (!e.isIntersecting) continue;
    e.target.dataset.in = "1";
    obs.unobserve(e.target);
  }
}, { rootMargin: "0px 0px -12% 0px" });
document.querySelectorAll("[data-reveal]").forEach((el) => revealObserver.observe(el));

/* -------------------------------------------------------------- the figure ---
   The anatomy figure, driven by the page's ticker rather than its own loop. */
const P = ${JSON.stringify(P)};
const REACH = ${JSON.stringify(HEAD_REACH)};
const CARDS = 13;
const TRUTH = ${JSON.stringify(TRUTH)};
// needleAt is lifted out of the figure module, so the constants it closes over
// have to travel with it.
const DIAL = ${JSON.stringify(DIAL)}, DIAL_R = ${DIAL_R}, DIAL_Z = ${DIAL_Z}, DIAL_H = ${DIAL_H};
const needleAt = ${needleAt.toString().replace(/\bk\./g, "")};

const figStage = document.querySelector(".iso-stage");
const figSvg = figStage.querySelector("svg");
const carriage = figSvg.querySelector('[data-part="carriage"]');
const needleGroup = figSvg.querySelector('[data-part="needle-group"]');
const needle = needleGroup.querySelector("path");
const cards = [...figSvg.querySelectorAll("[data-card]")];
const fBlocks = [...figSvg.querySelectorAll("[data-block]")];
const readout = document.querySelector("[data-readout]");

const shareOf = (x) => clamp((x - REACH[0]) / (REACH[1] - REACH[0]), 0, 1);
let fx = REACH[0], fTarget = REACH[0], touring = true, clock = 0, idle = 0;

function drawFigure() {
  const done = shareOf(fx);
  carriage.setAttribute("transform", translateAlong(P, [fx, 0, 0]));
  const filled = Math.round(done * CARDS);
  cards.forEach((card, i) => { card.style.display = i < filled ? "" : "none"; });
  fBlocks.forEach((block, i) => {
    const from = 0.45 + i * 0.12;
    const up = clamp((done - from) / 0.18, 0, 1);
    block.setAttribute("transform", translateAlong(P, [0, 0, (1 - up) * -26]));
    block.style.opacity = up < 0.04 ? "0" : "1";
  });
  needle.setAttribute("d", needleAt(done * 0.78));
  needleGroup.style.opacity = done < 0.45 ? "0" : "1";
  const folders = Math.round(done * TRUTH.folders);
  const text = done < 1
    ? "pass " + Math.round(done * 100) + "% · " + folders.toLocaleString("en-US") + " folders carded"
    : "pass complete · " + TRUTH.folders.toLocaleString("en-US") + " folders · index " + TRUTH.indexMb + " MB";
  if (readout.textContent !== text) readout.textContent = text;
  figStage.setAttribute("aria-valuenow", String(Math.round(done * 100)));
  figStage.setAttribute("aria-valuetext", text);
}

function stepFigure(dt) {
  const calm = REDUCED.matches;
  if (touring) {
    clock += dt;
    const t = (clock % 7) / 5.2;
    fTarget = REACH[0] + clamp(t, 0, 1) * (REACH[1] - REACH[0]);
  }
  fx = calm ? fTarget : fx + (fTarget - fx) * (1 - Math.exp(-dt / 0.3));
  if (Math.abs(fTarget - fx) < 0.02) fx = fTarget;
  drawFigure();
}

new IntersectionObserver((entries) => {
  for (const e of entries) {
    if (e.isIntersecting) join(stepFigure);
    else leave(stepFigure);
  }
}, { rootMargin: "160px 0px" }).observe(figStage);

const steer = (next) => {
  touring = false;
  fTarget = clamp(next, REACH[0], REACH[1]);
  clearTimeout(idle);
  idle = setTimeout(() => { touring = !REDUCED.matches; clock = 0; }, 4000);
  join(stepFigure);
};
figStage.addEventListener("pointermove", (event) => {
  const box = figSvg.getBoundingClientRect();
  const along = clamp(((event.clientX - box.left) / box.width - 0.1) / 0.55, 0, 1);
  steer(REACH[0] + along * (REACH[1] - REACH[0]));
});
figStage.addEventListener("keydown", (event) => {
  const step = (REACH[1] - REACH[0]) / 12;
  const keys = { ArrowRight: step, ArrowUp: step, ArrowLeft: -step, ArrowDown: -step };
  if (event.key in keys) { event.preventDefault(); steer(fTarget + keys[event.key]); }
  if (event.key === "Home") { event.preventDefault(); steer(REACH[0]); }
  if (event.key === "End") { event.preventDefault(); steer(REACH[1]); }
});
if (REDUCED.matches) touring = false;
drawFigure();
`;

// --------------------------------------------------------------------- page ---
const page = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>DiskLens - see where 705 GB went</title>
<meta name="description" content="DiskLens is a desktop app for Windows and macOS that reads a drive once, keeps the index, and answers from memory: treemap, big files, duplicates, apps and a cleaner that says what is safe to drop, and why.">
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link rel="stylesheet" onerror="this.remove()"
  href="https://fonts.googleapis.com/css2?family=Space+Grotesk:wght@500;600;700&display=swap">
<style>
${k.ISO_CSS}
:root {
  color-scheme: dark;
  --ground: #07070a; --panel: #121218; --panel-2: #1b1b23; --line: #2a2a35;
  --ink: #f4f4f7; --muted: #9a9aa8; --faint: #6b6b7a;
  --lime: #c6f135; --violet: #6b4bff; --cyan: #16d9cf;
  --k-data: #e4e4ec; --k-system: #ff4fd8; --k-cache: #c6f135;
  --k-heavy: #6b4bff; --k-files: #16d9cf; --k-unknown: #2f2f3d;
  --ease: cubic-bezier(.32,.72,0,1);
  --anatomy-page: #07070a; --anatomy-card: #16161d; --anatomy-paper: #16161d;
  --anatomy-face: #1f1f28; --anatomy-top: #23232d;
  --anatomy-shade-0: #141419; --anatomy-shade-1: #191920; --anatomy-shade-2: #1e1e26; --anatomy-shade-3: #23232c;
  --anatomy-hi: #e2e2ea; --anatomy-mid: #8b8b99; --anatomy-lo: #55555f; --anatomy-faint: #3a3a45;
  --anatomy-lit: #c6f135; --anatomy-dot: #e2e2ea; --anatomy-text: #9a9aa8; --anatomy-strong: #f4f4f7;
  --gut: 16px;
  --drill: 0;
}
* { box-sizing: border-box; }
html { scroll-behavior: smooth; }
html, body { margin: 0; background: var(--ground); }
body {
  font: 500 17px/1.5 "Space Grotesk", "Segoe UI Variable Text", system-ui, sans-serif;
  color: var(--ink); -webkit-font-smoothing: antialiased; overflow-x: hidden;
  font-variant-numeric: tabular-nums;
}
a { color: inherit; text-decoration: none; }
* { scrollbar-width: thin; scrollbar-color: #33333f transparent; }
::-webkit-scrollbar { width: 11px; height: 11px; }
::-webkit-scrollbar-track { background: transparent; }
::-webkit-scrollbar-thumb { background: #33333f; border-radius: 99px;
  border: 3px solid transparent; background-clip: content-box; }
::-webkit-scrollbar-thumb:hover { background: #4a4a5a; background-clip: content-box; }
p { margin: 0; }
h1, h2, h3 { margin: 0; font-weight: 700; letter-spacing: -.045em; line-height: .92; }
h1 { font-size: clamp(2.4rem, 7.2vw, 6rem); }
h2 { font-size: clamp(2.2rem, 5.2vw, 4.2rem); }
h3 { font-size: 1.375rem; letter-spacing: -.03em; line-height: 1.05; }
:focus-visible { outline: 3px solid var(--lime); outline-offset: 3px; border-radius: 6px; }
.skip { position: fixed; top: -100px; left: 16px; z-index: 99; background: var(--lime);
  color: var(--ground); padding: 12px 18px; border-radius: 12px; font-weight: 700; }
.skip:focus { top: 16px; }

.wrap { width: min(1180px, 100% - 2 * var(--gut)); margin: 0 auto; }
.lbl { font-size: .6875rem; font-weight: 700; letter-spacing: .16em; text-transform: uppercase; color: var(--faint); }
[data-reveal] { opacity: 0; transform: translate3d(0, 26px, 0); }
[data-reveal][data-in="1"] { opacity: 1; transform: none;
  transition: opacity .7s var(--ease), transform .8s var(--ease); }
@media (prefers-reduced-motion: reduce) { [data-reveal] { opacity: 1; transform: none; } html { scroll-behavior: auto; } }

/* ------------------------------------------------------------------- chrome --- */
.top { position: fixed; inset: 0 0 auto 0; z-index: 40; padding: 14px 0;
  background: linear-gradient(var(--ground) 55%, rgba(7,7,10,0));
  transition: transform .45s var(--ease); }
body[data-nav="down"] .top { transform: translate3d(0, -130%, 0); }
.top .wrap { display: flex; align-items: center; gap: 14px; }
.word { display: flex; align-items: baseline; gap: 8px; padding: 8px 14px; border-radius: 11px;
  background: var(--lime); color: var(--ground); }
.word b { font-size: 1.1rem; font-weight: 700; letter-spacing: -.045em; }
.word span { font-size: .625rem; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; opacity: .7; }
.top nav { margin-left: auto; display: flex; gap: 6px; align-items: center; }
.top nav a { padding: 10px 14px; border-radius: 10px; font-size: .75rem; font-weight: 700;
  letter-spacing: .1em; text-transform: uppercase; color: var(--muted);
  transition: color .2s var(--ease), background .2s var(--ease); }
.top nav a:hover { color: var(--ink); background: var(--panel-2); }

.btn { display: inline-flex; align-items: center; gap: 10px; padding: 16px 22px; border-radius: 13px;
  font: inherit; font-size: .8125rem; font-weight: 700; letter-spacing: .1em; text-transform: uppercase;
  background: var(--panel-2); color: var(--ink); border: 0; cursor: pointer; position: relative;
  overflow: hidden; isolation: isolate; }
.btn span { position: relative; z-index: 1; }
/* the house hover: a block fills the button the way a tile fills its rectangle */
.btn::after { content: ""; position: absolute; inset: 0; background: var(--ink); z-index: 0;
  transform: translate3d(0, 101%, 0); transition: transform .5s var(--ease); }
.btn:hover::after, .btn:focus-visible::after { transform: none; }
.btn:hover span, .btn:focus-visible span { color: var(--ground); }
.btn.lime { background: var(--lime); color: var(--ground); }
.btn.lime::after { background: var(--ground); }
.btn.lime:hover span, .btn.lime:focus-visible span { color: var(--lime); }
.btn.ghost { background: transparent; box-shadow: inset 0 0 0 1.5px var(--line); }
.btn:active { transform: translateY(1px); }
@media (prefers-reduced-motion: reduce) { .btn::after { transition: none; } }

/* -------------------------------------------------------------------- stage --- */
.stage { position: relative; height: 320vh; }
/* exactly one screen, never more: the headline and the treemap share it */
.sticky { position: sticky; top: 0; height: 100vh; height: 100svh; overflow: hidden;
  display: flex; flex-direction: column; padding: 84px 0 20px; }
.sticky .wrap { display: flex; flex-direction: column; flex: 1; min-height: 0; gap: 12px; }
.headline, .meta { flex: none; }
.headline { display: grid; grid-template-columns: minmax(0, 7fr) minmax(0, 5fr); gap: var(--gut);
  align-items: end; }
.headline h1 em { font-style: normal; color: var(--lime); }
.headline p { color: var(--muted); font-size: 1rem; max-width: 42ch; }
.headline .acts { display: flex; gap: 8px; flex-wrap: wrap; margin-top: 18px; }

.meta { display: flex; align-items: baseline; gap: 14px; flex-wrap: wrap; }
.meta code { font: inherit; font-size: .875rem; font-weight: 700; color: var(--lime); }
.meta .dots { margin-left: auto; display: flex; gap: 6px; }
.meta .dots button { width: 26px; height: 6px; border-radius: 99px; border: 0; padding: 0;
  background: var(--line); cursor: pointer; transition: background .3s var(--ease); }
.meta .dots button[aria-current="true"] { background: var(--lime); }

#field { position: relative; flex: 1; min-height: 180px; }
.tile { position: absolute; left: 0; top: 0; border: 0; padding: 12px 14px; border-radius: 13px;
  text-align: left; color: var(--ground); background: var(--k-unknown); cursor: pointer;
  font: inherit; overflow: hidden; will-change: transform;
  transition: transform .9s var(--ease), width .9s var(--ease), height .9s var(--ease),
    opacity .45s var(--ease), filter .25s var(--ease); }
.tile b { display: block; font-size: .9375rem; font-weight: 700; letter-spacing: -.02em;
  white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.tile i { font-style: normal; font-size: .75rem; font-weight: 600; opacity: .72; }
.tile[data-small="1"] { padding: 7px 9px; }
.tile[data-small="1"] i { display: none; }
.tile[data-small="1"] b { font-size: .75rem; }
.tile:hover, .tile:focus-visible { filter: brightness(1.12); }
.tile[data-kind="data"] { background: var(--k-data); }
.tile[data-kind="system"] { background: var(--k-system); }
.tile[data-kind="cache"] { background: var(--k-cache); }
.tile[data-kind="heavy"] { background: var(--k-heavy); color: #fff; }
.tile[data-kind="files"] { background: var(--k-files); }
.tile[data-kind="unknown"] { background: var(--k-unknown); color: var(--ink); }
@media (prefers-reduced-motion: reduce) { .tile { transition: opacity .2s linear; } }

/* ------------------------------------------------------------------ section --- */
section { padding: clamp(72px, 11vw, 150px) 0; border-top: 1px solid var(--line); scroll-margin-top: 72px; }
.head { display: grid; grid-template-columns: minmax(0, 6fr) minmax(0, 5fr); gap: var(--gut);
  align-items: end; margin-bottom: clamp(32px, 5vw, 64px); }
.head p { color: var(--muted); }

.rail { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 8px; margin-top: 10px; }
.stat { border-radius: 18px; padding: 20px; min-height: 142px; display: flex; flex-direction: column;
  justify-content: space-between; }
.stat .v { font-size: clamp(1.8rem, 3.6vw, 2.9rem); font-weight: 700; letter-spacing: -.05em; line-height: .9; }
.stat .v span { font-size: .38em; letter-spacing: 0; margin-left: .18em; }
.stat small { font-size: .75rem; font-weight: 600; opacity: .7; }
.s-lime { background: var(--lime); color: var(--ground); }
.s-violet { background: var(--violet); color: #fff; }
.s-cyan { background: var(--cyan); color: var(--ground); }
.s-plain { background: var(--panel); }
.s-lime .lbl, .s-violet .lbl, .s-cyan .lbl { color: inherit; opacity: .62; }

.figwrap { background: var(--panel); border-radius: 22px; padding: clamp(18px, 3vw, 34px); }
.steps { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 8px; margin-top: 8px; }
.step { background: var(--panel-2); border-radius: 16px; padding: 20px; }
.step i { font-style: normal; display: inline-grid; place-items: center; width: 28px; height: 28px;
  border-radius: 9px; background: var(--lime); color: var(--ground); font-size: .8125rem; font-weight: 700; }
.step b { display: block; margin: 12px 0 5px; font-size: 1rem; letter-spacing: -.02em; }
.step p { font-size: .8125rem; color: var(--muted); }

.bento { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr));
  grid-auto-rows: minmax(210px, auto); gap: 8px; }
.cell { border-radius: 20px; padding: clamp(20px, 2.4vw, 30px); background: var(--panel);
  display: flex; flex-direction: column; justify-content: space-between; gap: 18px; }
.cell p { font-size: .9375rem; color: var(--muted); }
.cell.wide { grid-column: span 2; }
.cell.tall { grid-row: span 2; }
.cell.lime { background: var(--lime); color: var(--ground); }
.cell.violet { background: var(--violet); color: #fff; }
.cell.cyan { background: var(--cyan); color: var(--ground); }
.cell.lime p, .cell.violet p, .cell.cyan p { color: inherit; opacity: .76; }
.cell .lbl { opacity: .6; }
.cell.lime .lbl, .cell.violet .lbl, .cell.cyan .lbl { color: inherit; }

.shot { margin: 0; background: var(--panel); border-radius: 22px; padding: clamp(10px, 1.6vw, 18px); }
.shot img { display: block; width: 100%; height: auto; border-radius: 14px; }
.shot figcaption { margin: 14px 6px 4px; }
.get { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px; }
.card { background: var(--panel); border-radius: 20px; padding: clamp(22px, 2.6vw, 34px); }
.card ol { margin: 16px 0 0; padding-left: 1.1rem; color: var(--muted); font-size: .9375rem; }
.card li { margin-bottom: 10px; }
code { font-family: ui-monospace, "Cascadia Code", Consolas, monospace; font-size: .8125rem;
  background: var(--panel-2); padding: 2px 7px; border-radius: 6px; color: var(--lime); }
pre { margin: 16px 0 0; padding: 20px; border-radius: 14px; background: var(--ground);
  overflow-x: auto; font-size: .8125rem; line-height: 1.7; }
pre code { background: none; padding: 0; color: var(--muted); }
.fine { margin-top: 18px; color: var(--faint); font-size: .8125rem; }

footer { padding: 48px 0 72px; border-top: 1px solid var(--line); color: var(--faint); font-size: .8125rem; }
footer .wrap { display: flex; gap: 16px; flex-wrap: wrap; align-items: baseline; }
footer a { color: var(--muted); }
footer a:hover { color: var(--ink); }

@media (max-width: 1000px) {
  .headline, .head, .get { grid-template-columns: minmax(0, 1fr); }
  .rail, .steps { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .bento { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .cell.tall { grid-row: span 1; }
}
@media (max-width: 620px) {
  body { font-size: 16px; }
  .stage { height: 300vh; }
  .sticky { padding-top: 76px; }
  .top nav a { padding: 10px 8px; }
  .bento, .rail, .steps { grid-template-columns: minmax(0, 1fr); }
  .cell.wide { grid-column: span 1; }
}
</style>
</head>
<body class="iso" data-theme="dark">
<a class="skip" href="#how">Skip to how it works</a>

<div class="top">
  <div class="wrap">
    <a class="word" href="#top"><b>DiskLens</b><span>disk atlas</span></a>
    <nav>
      <a href="#how">How it works</a>
      <a href="#app">The app</a>
      <a href="#what">What it does</a>
      <a href="#get">Get it</a>
    </nav>
  </div>
</div>

<main id="top">
  <div class="stage" id="stage">
    <div class="sticky">
      <div class="wrap">
        <div class="headline">
          <h1>See where<br><em>705 GB</em> went.</h1>
          <div>
            <p>This is a real Windows drive, drawn by the thing you are about to install.
               Every block is a folder, sized by the bytes it actually holds. Scroll to drill in.</p>
            <div class="acts">
              <a class="btn lime" href="${RELEASE}"><span>Download for Windows</span></a>
              <a class="btn ghost" href="#get"><span>macOS and source</span></a>
            </div>
          </div>
        </div>

        <div class="meta">
          <code id="crumb">C:\\</code>
          <span class="lbl" id="note">952.7 GB formatted, 705.2 GB of it spoken for</span>
          <span class="dots">
            ${LEVELS.map((l, i) => `<button data-level-dot type="button" aria-current="${i === 0}" aria-label="Show ${l.path}"></button>`).join("")}
          </span>
        </div>

        <div id="field" role="img" aria-label="Treemap of a 952.7 gigabyte Windows drive. The largest blocks are Users at 448.7 GB, Program Files at 96.8 GB and Windows at 46.7 GB."></div>
      </div>
    </div>
  </div>

  <section id="how">
    <div class="wrap">
      <div class="head" data-reveal>
        <h2>One sweep, then never again.</h2>
        <p>Most disk tools re-read the drive every time you open them. DiskLens reads it once,
           writes the index down, and lives off that. The machine below is the whole idea.</p>
      </div>

      <div class="rail" data-reveal>
        <div class="stat s-lime"><span class="lbl">Cold sweep</span>
          <div class="v">${TRUTH.walkSeconds}<span>s</span></div><small>3.2M files, every core</small></div>
        <div class="stat s-violet"><span class="lbl">Next start</span>
          <div class="v">${TRUTH.warmSeconds}<span>s</span></div><small>from the saved index</small></div>
        <div class="stat s-cyan"><span class="lbl">Duplicates found</span>
          <div class="v">${TRUTH.dupesGb}<span>GB</span></div><small>same bytes, other places</small></div>
        <div class="stat s-plain"><span class="lbl">After a delete</span>
          <div class="v">0.3<span>s</span></div><small>index patched, not rescanned</small></div>
      </div>

      <div class="figwrap" style="margin-top:8px" data-reveal>
        ${figure}
      </div>

      <div class="steps" data-reveal>
        ${STEPS.map(([title, body], i) => `<div class="step"><i>${i + 1}</i><b>${title}</b><p>${body}</p></div>`).join("")}
      </div>
    </div>
  </section>

  <section id="app">
    <div class="wrap">
      <div class="head" data-reveal>
        <h2>A window,<br>not a tab.</h2>
        <p>DiskLens is a real desktop app: one file, its own window and icon, no server and no
           browser. It runs on Windows and macOS, and it tells you when a newer version is out.</p>
      </div>
      <figure class="shot" data-reveal>
        <img src="${WINDOW_SHOT}" width="1200" height="783" loading="lazy"
             alt="The DiskLens window showing a treemap of drive C: with Users, Program Files, Windows and ProgramData as the largest blocks, and the tab bar for map, table, hotspots, clean, copies, files, apps and system.">
        <figcaption class="fine">The app itself, scanning the same drive as the hero above.</figcaption>
      </figure>
    </div>
  </section>

  <section id="what">
    <div class="wrap">
      <div class="head" data-reveal>
        <h2>Eight views<br>over one index.</h2>
        <p>Drill the map, find the big files and the copies, check what your apps and the system
           are holding, then clear what the cleaner marks. Nothing leaves the machine and nothing is
           deleted outright.</p>
      </div>
      <div class="bento" data-reveal>
        ${FEATURES.map(([title, body, tone, span, tag]) =>
          `<div class="cell ${tone} ${span}"><span class="lbl">${tag}</span>
            <div><h3>${title}</h3><p style="margin-top:10px">${body}</p></div></div>`).join("")}
      </div>
    </div>
  </section>

  <section id="get">
    <div class="wrap">
      <div class="head" data-reveal>
        <h2>Get it.</h2>
        <p>A per-user install, no elevation, nothing written outside your profile. Nothing to
           install first.</p>
      </div>
      <div class="get" data-reveal>
        <div class="card">
          <span class="lbl">Windows</span>
          <ol>
            <li><a href="${RELEASE}">Download <code>DiskLens-Setup.exe</code></a> (about 65 MB) and run it. It installs for you only, into
                <code>%LOCALAPPDATA%\\Programs\\DiskLens</code>.</li>
            <li>DiskLens lands in the Start menu, and on the desktop if you leave the box ticked.</li>
            <li>It checks for a newer release when it opens and offers <b>Update now</b>. One click, and it reopens updated.</li>
            <li>Remove it from Add/Remove Programs. The saved index stays in
                <code>%LOCALAPPDATA%\\DiskLens</code> until you delete it.</li>
          </ol>
          <p class="fine">Needs Windows 10 or 11 with WebView2, which ships with Edge.</p>
        </div>
        <div class="card">
          <span class="lbl">macOS, or from source</span>
          <pre><code># needs the .NET 8 SDK
git clone https://github.com/Sachin1724/DiskLens
cd DiskLens

dotnet publish app -c Release -r osx-arm64
sh app/bundle-mac.sh        # -> DiskLens.app

# the checks that guard it
dotnet run --project app -- --selftest
node check.js &amp;&amp; node check-ui.js</code></pre>
          <p class="fine">No prebuilt Mac app is published yet, and the Mac build has not been tested on real
             hardware. The scanner, rules and window are the same code as on Windows.</p>
        </div>
      </div>
    </div>
  </section>
</main>

<footer>
  <div class="wrap">
    <span>DiskLens, a local disk analyser for Windows and macOS.</span>
    <a href="https://github.com/Sachin1724/DiskLens">Source on GitHub</a>
    <span>Every number here was measured, not estimated.</span>
    <a href="#top" style="margin-left:auto">Back to the top</a>
  </div>
</footer>

<script type="module">${live
  // The inlined kit carries a page-template string that mentions a script tag. Nothing that looks like a
  // script tag, open or close, may appear inside this script, so spell both with unicode escapes: they
  // mean the same to JS (in a string or a regex) and are invisible to any HTML parser or sanitiser.
  .replace(/<(\\?)(\/?)script/gi, (m, bs, sl) => "<" + (sl ? "\\u002f" : "") + "\\u0073cript")}</script>
</body>
</html>`;

writeFileSync(join(HERE, "..", "index.html"), page);
console.log("wrote index.html");
