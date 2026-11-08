// The "how it works" figure: a card-index bench.
//
// The concept is that DiskLens reads the drive ONCE and writes an index, and
// that every view afterwards reads the index rather than the disk. So: a
// platter on a turntable, a reader arm that makes a single pass across it, a
// chute dropping cards into an index drawer, and three outputs fed from the
// drawer - the mosaic tray (treemap), the dial (reclaimable) and the bin.
//
// Every number is real, measured on the machine this was built on:
//   3,240,377 files / 499,181 folders, 48 s cold walk, 56.4 MB index,
//   2.7 s warm start, 31.1 GB held by duplicates, 7.4 GB reclaimable.
import * as k from "../../../../.claude/skills/anatomy/kit/iso-kit.mjs";

export const TRUTH = {
  files: 3240377,
  folders: 499181,
  walkSeconds: 48,
  indexMb: 56.4,
  warmSeconds: 2.7,
  dupesGb: 31.1,
  reclaimGb: 7.4,
  usedGb: 705.2,
  freeGb: 247.5,
};

const WIDTH = 760;
const HEIGHT = 430;

// ---------------------------------------------------------------- the plan ---
const PLATE = { x: -170, y: -80, w: 346, d: 160, r: 9 };
const PLATE_H = 13;
const FOOT_H = 6;

const SPIN = [-104, 2];          // the platter: one drive
const SPIN_R = 50;
const SPIN_Z = 7;
const SPIN_H = 6;

const RAIL_Y = -46;              // the rail the reader arm rides
const RAIL_FROM = -156;
const RAIL_TO = -34;
const RAIL_Z = 62;
const POST_X = [-154, -36];

const HEAD_DROP = 30;            // how far the head hangs below the rail

const BOX = { x: -14, y: -34, w: 62, d: 62, r: 4 };   // the index drawer
const BOX_WALL = 4;
const BOX_H = 26;
const CARDS = 13;
const CARD_T = 1.5;

const TRAY = { x: 64, y: -46, w: 92, d: 86, r: 4 };   // the mosaic tray
const TRAY_H = 5;

// The blocks are the treemap: four folders, areas in proportion to real sizes.
const BLOCKS = [
  { x: 70, y: -40, w: 44, d: 40, h: 26, tone: "hi", label: "AppData" },
  { x: 118, y: -40, w: 32, d: 22, h: 15, tone: "mid", label: "Videos" },
  { x: 118, y: -14, w: 32, d: 14, h: 10, tone: "mid", label: "Downloads" },
  { x: 70, y: 4, w: 80, d: 28, h: 7, tone: "lo", label: "the rest" },
];

export const DIAL = [34, 44];    // the gauge: what the index says you can get back
export const DIAL_R = 21;
export const DIAL_Z = 54;
export const DIAL_H = 9;

const BIN = { x: -96, y: 46, w: 40, d: 28, r: 3 };    // the recycle bin
const BIN_H = 22;

export const P = k.fitProjection(
  [
    ...k.boxCorners(PLATE, -PLATE_H - FOOT_H, 0),
    [POST_X[0], RAIL_Y, RAIL_Z + 10],
    [POST_X[1], RAIL_Y, RAIL_Z + 10],
    [DIAL[0] + DIAL_R, DIAL[1], DIAL_Z + DIAL_H + 10],
    [TRAY.x + TRAY.w, TRAY.y + TRAY.d, TRAY_H + 30],
  ],
  WIDTH,
  HEIGHT,
  { pad: 26, azimuth: 46 },
);

const at = (point) => k.iso(point, P);
const S = (paths, style) => k.solidSvg(paths, style);
const L = (d, style) => k.lineSvg(d, style);
const D = (points, style) => k.dotsSvg(points, style);
const byDepth = (points) => [...points].sort((a, b) => k.depthOf([a[0], a[1], 0], P) - k.depthOf([b[0], b[1], 0], P));

export const HEAD_REACH = [RAIL_FROM + 16, RAIL_TO - 16];

// --------------------------------------------------------------- the parts ---
function base() {
  const out = [`<path class="iso-halo" d="${k.haloOf(PLATE, -PLATE_H - FOOT_H, PLATE_H, P)}"/>`];
  for (const [x, y] of byDepth(k.corners(PLATE, 24))) {
    out.push(S(k.cylinder(x, y, 7.4, -PLATE_H - FOOT_H, 1.8, P, 28), { tone: "lo" }));
    out.push(S(k.cylinder(x, y, 5.8, -PLATE_H - FOOT_H + 1.8, FOOT_H - 1.8, P, 28), { tone: "mid" }));
  }
  out.push(S(k.slabOf(PLATE, -PLATE_H, PLATE_H, P, 9, 1.8), { tone: "mid" }));
  out.push(L(k.planOutline(k.insetPlan(PLATE, 5), 0, P), { tone: "faint" }));
  out.push(L(k.planOutline(k.insetPlan(PLATE, 9), 0, P), { tone: "faint" }));

  const screws = k.corners(PLATE, 11);
  out.push(L(screws.map(([x, y]) => k.ring(x, y, 2.4, 0, P, 16)).join(""), { tone: "lo" }));
  out.push(D(screws.map(([x, y]) => at([x, y, 0])), { size: 0.5 }));

  // ruler strip along the front edge, a major tick every fifth
  const strip = { x: PLATE.x + 18, y: PLATE.y + PLATE.d - 15, w: PLATE.w - 36, d: 7, r: 1 };
  out.push(S(k.slabOf(strip, 0, 0.7, P, 3), { tone: "lo", crease: "none" }));
  const ticks = k.topTicks(strip.x + 4, strip.x + strip.w - 4, 5, 5, strip.y + 0.9, 0.7, [2, 3.8], P);
  out.push(L(ticks.minor, { tone: "lo" }), L(ticks.major, { tone: "mid" }));
  out.push(D([at([strip.x + 2, strip.y + 3.5, 0.7]), at([strip.x + strip.w - 2, strip.y + 3.5, 0.7])], { size: 0.45 }));

  // label plate: three engraved rule lines, no words in the drawing
  const plate = { x: PLATE.x + 16, y: PLATE.y + 8, w: 46, d: 20, r: 2 };
  out.push(S(k.slabOf(plate, 0, 1.1, P, 2, 0.4), { tone: "lo" }));
  for (let i = 0; i < 3; i++) {
    const y = plate.y + 5.5 + i * 4.6;
    out.push(L(k.lineOnTop([plate.x + 4, y], [plate.x + plate.w - (i === 2 ? 16 : 5), y], 1.1, P), { tone: "faint" }));
  }
  out.push(D(k.corners(plate, 3).map(([x, y]) => at([x, y, 1.1])), { size: 0.4 }));
  return out.join("");
}

function platter() {
  const out = [];
  // riser, platter, hub
  out.push(S(k.cylinder(SPIN[0], SPIN[1], SPIN_R - 6, 0, SPIN_Z, P, 48, 1), { tone: "lo" }));
  out.push(S(k.cylinder(SPIN[0], SPIN[1], SPIN_R, SPIN_Z, SPIN_H, P, 56, 1.2), { tone: "mid" }));
  const top = SPIN_Z + SPIN_H;

  // tracks: three turned grooves plus the radial sector ticks
  for (const r of [SPIN_R - 7, SPIN_R - 17, SPIN_R - 27]) {
    out.push(L(k.ring(SPIN[0], SPIN[1], r, top, P, 56), { tone: "faint" }));
  }
  const sectors = k.radialTicks(SPIN[0], SPIN[1], SPIN_R - 2, 48, 6, top, [3, 6], P);
  out.push(L(sectors.minor, { tone: "lo" }), L(sectors.major, { tone: "mid" }));

  out.push(S(k.cylinder(SPIN[0], SPIN[1], 11, top, 3.4, P, 32, 0.6), { tone: "hi" }));
  out.push(L(k.knurl(SPIN[0], SPIN[1], 11, top, top + 3.4, 28, P, 0.5), { tone: "lo" }));
  out.push(S(k.cylinder(SPIN[0], SPIN[1], 3.4, top + 3.4, 1.6, P, 20), { tone: "lit" }));
  out.push(D([at([SPIN[0], SPIN[1], top + 5])], { size: 0.6 }));
  return out.join("");
}

function rail() {
  const out = [];
  for (const x of byDepth(POST_X.map((px) => [px, RAIL_Y]))) {
    out.push(S(k.cylinder(x[0], x[1], 6.5, 0, 4, P, 28, 0.8), { tone: "lo" }));
    out.push(S(k.cylinder(x[0], x[1], 4.2, 4, RAIL_Z - 4, P, 24, 0.6), { tone: "mid" }));
    out.push(L(k.sideArc(x[0], x[1], 4.2, RAIL_Z - 9, P, 18), { tone: "faint" }));
    out.push(D([at([x[0], x[1], RAIL_Z])], { size: 0.5 }));
  }
  out.push(S(k.slabOf({ x: RAIL_FROM, y: RAIL_Y - 3, w: RAIL_TO - RAIL_FROM, d: 6, r: 1.5 }, RAIL_Z, 5, P, 3, 0.5), { tone: "mid" }));
  const stops = [RAIL_FROM + 3, RAIL_TO - 3];
  out.push(L(stops.map((x) => k.rise([x, RAIL_Y], RAIL_Z, RAIL_Z + 5, P)).join(""), { tone: "lo" }));
  out.push(D(stops.map((x) => at([x, RAIL_Y, RAIL_Z + 5])), { size: 0.45 }));
  // the travel scale engraved on the rail's top face
  const t = k.topTicks(RAIL_FROM + 10, RAIL_TO - 10, 6, 5, RAIL_Y - 2.4, RAIL_Z + 5, [1.6, 3], P);
  out.push(L(t.minor, { tone: "faint" }), L(t.major, { tone: "lo" }));
  return out.join("");
}

// The carriage and the head move together along x; built once at x = 0.
function carriage() {
  const out = [];
  const car = { x: -13, y: RAIL_Y - 9, w: 26, d: 18, r: 2 };
  out.push(S(k.slabOf(car, RAIL_Z + 5, 9, P, 2, 0.6), { tone: "hi" }));
  out.push(L(k.sideSeam(k.insetPlan(car, 2), RAIL_Z + 9, P), { tone: "faint" }));
  for (const [x, y] of k.corners(car, 4)) out.push(D([at([x, y, RAIL_Z + 14])], { size: 0.45 }));

  // two bushings gripping the rail, and the knurled thumb screw on top
  for (const dx of [-9, 9]) {
    out.push(S(k.cylinder(dx, RAIL_Y, 5.2, RAIL_Z + 2, 4, P, 24, 0.5), { tone: "mid" }));
    out.push(L(k.ring(dx, RAIL_Y, 3.4, RAIL_Z + 6, P, 20), { tone: "faint" }));
  }
  out.push(S(k.cylinder(0, RAIL_Y, 5, RAIL_Z + 14, 4.4, P, 26, 0.6), { tone: "mid" }));
  out.push(L(k.knurl(0, RAIL_Y, 5, RAIL_Z + 14, RAIL_Z + 18.4, 22, P, 0.45), { tone: "lo" }));
  out.push(D([at([0, RAIL_Y, RAIL_Z + 18.4])], { size: 0.5 }));

  // the arm reaching out over the platter, and the head on its end
  const armY = RAIL_Y + 9;
  out.push(S(k.slabOf({ x: -4, y: armY, w: 8, d: 44, r: 1.5 }, RAIL_Z + 1, 5, P, 2, 0.4), { tone: "mid" }));
  const headY = armY + 40;
  out.push(S(k.cylinder(0, headY, 7, RAIL_Z - HEAD_DROP + 6, 9, P, 26, 0.8), { tone: "hi" }));
  out.push(S(k.slabOf({ x: -3, y: headY - 3, w: 6, d: 6, r: 1 }, RAIL_Z - HEAD_DROP, 6, P, 2, 0.4), { tone: "mid" }));
  out.push(L(k.rise([0, headY], RAIL_Z - HEAD_DROP + 6, RAIL_Z + 1, P), { tone: "lo" }));
  out.push(S(k.cylinder(0, headY, 2.6, RAIL_Z - HEAD_DROP - 2.6, 2.6, P, 18), { tone: "lit" }));
  out.push(D([at([0, headY, RAIL_Z - HEAD_DROP - 2.6])], { size: 0.55 }));
  return out.join("");
}

// The chute that carries a read card from the head down into the drawer.
function chute() {
  const out = [];
  const y0 = RAIL_Y + 50;
  out.push(S(k.slabOf({ x: -34, y: y0 - 7, w: 26, d: 14, r: 2 }, 18, 3, P, 2, 0.4), { tone: "lo" }));
  out.push(S(k.slabOf({ x: -30, y: y0 - 4, w: 44, d: 8, r: 1.5 }, 21, 2, P, 2, 0.3), { tone: "mid" }));
  out.push(L(k.lineOnTop([-28, y0], [12, y0], 23, P), { tone: "faint" }));
  out.push(D([at([-30, y0, 23]), at([12, y0, 23])], { size: 0.45 }));
  for (const x of [-24, -14, -4, 6]) out.push(L(k.rise([x, y0 - 4], 21, 23, P), { tone: "faint" }));
  return out.join("");
}

function drawer() {
  const out = [];
  // an open box: floor, then four walls as slabs, back pair first
  out.push(S(k.slabOf(BOX, 0, 3, P, 4, 0.5), { tone: "lo" }));
  const walls = [
    { x: BOX.x, y: BOX.y, w: BOX.w, d: BOX_WALL, r: 1 },
    { x: BOX.x, y: BOX.y, w: BOX_WALL, d: BOX.d, r: 1 },
    { x: BOX.x + BOX.w - BOX_WALL, y: BOX.y, w: BOX_WALL, d: BOX.d, r: 1 },
    { x: BOX.x, y: BOX.y + BOX.d - BOX_WALL, w: BOX.w, d: BOX_WALL, r: 1 },
  ];
  const ordered = [...walls].sort(
    (a, b) => k.depthOf([a.x + a.w / 2, a.y + a.d / 2, 0], P) - k.depthOf([b.x + b.w / 2, b.y + b.d / 2, 0], P),
  );
  for (const wall of ordered) out.push(S(k.slabOf(wall, 3, BOX_H, P, 1, 0.4), { tone: "mid" }));
  out.push(L(k.planOutline(k.insetPlan(BOX, BOX_WALL), 3, P), { tone: "faint" }));
  // the index plate on the front wall, with its two screws
  const tag = { x: BOX.x + 14, y: BOX.y + BOX.d - BOX_WALL, w: 30, d: 1, r: 0 };
  out.push(D([at([tag.x + 2, tag.y, 3 + BOX_H - 6]), at([tag.x + 28, tag.y, 3 + BOX_H - 6])], { size: 0.45 }));
  return out.join("");
}

function tray() {
  const out = [];
  out.push(S(k.slabOf(TRAY, 0, TRAY_H, P, 4, 0.6), { tone: "lo" }));
  out.push(L(k.planOutline(k.insetPlan(TRAY, 4), TRAY_H, P), { tone: "faint" }));
  for (const [x, y] of k.corners(TRAY, 7)) {
    out.push(L(k.ring(x, y, 2, TRAY_H, P, 14), { tone: "faint" }));
    out.push(D([at([x, y, TRAY_H])], { size: 0.4 }));
  }
  return out.join("");
}

function dial() {
  const out = [];
  out.push(S(k.cylinder(DIAL[0], DIAL[1], 7, 0, 4, P, 26, 0.8), { tone: "lo" }));
  out.push(S(k.cylinder(DIAL[0], DIAL[1], 4, 4, DIAL_Z - 4, P, 22, 0.5), { tone: "mid" }));
  out.push(S(k.cylinder(DIAL[0], DIAL[1], DIAL_R, DIAL_Z, DIAL_H, P, 48, 1.2), { tone: "hi" }));
  out.push(L(k.knurl(DIAL[0], DIAL[1], DIAL_R, DIAL_Z, DIAL_Z + DIAL_H, 46, P, 0.5), { tone: "faint" }));
  const face = DIAL_Z + DIAL_H;
  out.push(S(k.cylinder(DIAL[0], DIAL[1], DIAL_R - 3, face, 0.6, P, 44), { tone: "lo", crease: "none" }));
  const ticks = k.radialTicks(DIAL[0], DIAL[1], DIAL_R - 4, 40, 5, face + 0.6, [2, 4.4], P);
  out.push(L(ticks.minor, { tone: "lo" }), L(ticks.major, { tone: "mid" }));
  out.push(L(k.ring(DIAL[0], DIAL[1], DIAL_R - 4, face + 0.6, P, 44), { tone: "faint" }));
  out.push(S(k.cylinder(DIAL[0], DIAL[1], 2.4, face + 0.6, 1.4, P, 18), { tone: "mid" }));
  return out.join("");
}

export function needleAt(turn) {
  const face = DIAL_Z + DIAL_H + 1.3;
  const angle = -Math.PI / 2 + turn * Math.PI * 2;
  const tip = [DIAL[0] + Math.cos(angle) * (DIAL_R - 7), DIAL[1] + Math.sin(angle) * (DIAL_R - 7)];
  const tail = [DIAL[0] - Math.cos(angle) * 4.5, DIAL[1] - Math.sin(angle) * 4.5];
  return k.lineOnTop(tail, tip, face, P);
}

function bin() {
  const out = [];
  out.push(S(k.slabOf(BIN, 0, BIN_H, P, 3, 0.6), { tone: "mid" }));
  out.push(L(k.sideSeam(k.insetPlan(BIN, 2), BIN_H - 4, P), { tone: "faint" }));
  out.push(L(k.planOutline(k.insetPlan(BIN, 3), BIN_H, P), { tone: "faint" }));
  // a hinged lid, open: hinge pins at the back, the lid tilted up on them
  const hinge = [BIN.x + 5, BIN.y + 1.5];
  const hinge2 = [BIN.x + BIN.w - 5, BIN.y + 1.5];
  out.push(D([at([...hinge, BIN_H]), at([...hinge2, BIN_H])], { size: 0.5 }));
  out.push(S(k.slabOf({ x: BIN.x + 1, y: BIN.y - 13, w: BIN.w - 2, d: 13, r: 2 }, BIN_H + 9, 2, P, 2, 0.4), { tone: "lo" }));
  out.push(L(k.segment([hinge[0], hinge[1], BIN_H], [BIN.x + 3, BIN.y - 6, BIN_H + 9], P), { tone: "lo" }));
  out.push(L(k.segment([hinge2[0], hinge2[1], BIN_H], [BIN.x + BIN.w - 3, BIN.y - 6, BIN_H + 9], P), { tone: "lo" }));
  return out.join("");
}

// ---------------------------------------------------------------- assembly ---
export function buildSvg() {
  const cards = [];
  for (let i = 0; i < CARDS; i++) {
    const plan = { x: BOX.x + BOX_WALL + 2, y: BOX.y + BOX_WALL + 2, w: BOX.w - BOX_WALL * 2 - 4, d: BOX.d - BOX_WALL * 2 - 4, r: 1 };
    cards.push(
      `<g data-card="${i}" style="display:none">${S(k.slabOf(plan, 3 + i * CARD_T, CARD_T, P, 1, 0.2), {
        tone: i === CARDS - 1 ? "hi" : "mid",
        crease: "faint",
      })}</g>`,
    );
  }

  const blocks = BLOCKS.map((b, i) => {
    const plan = { x: b.x, y: b.y, w: b.w, d: b.d, r: 2 };
    const body = [
      S(k.slabOf(plan, TRAY_H, b.h, P, 2, 0.5), { tone: b.tone, lit: i === 0 }),
      L(k.planOutline(k.insetPlan(plan, 3), TRAY_H + b.h, P), { tone: "faint" }),
      D(k.corners(plan, 4).map(([x, y]) => at([x, y, TRAY_H + b.h])), { size: 0.4 }),
    ].join("");
    return `<g data-block="${i}">${body}</g>`;
  });

  // Painter's order: floor and base, then back to front across the bench.
  const body = [
    base(),
    platter(),
    bin(),
    rail(),
    chute(),
    drawer(),
    cards.join(""),
    tray(),
    ...blocks,
    dial(),
    `<g data-part="needle-group">${L(needleAt(0), { tone: "lit" })}</g>`,
    `<g data-part="carriage">${carriage()}</g>`,
  ].join("");

  return k.figureSvg({ width: WIDTH, height: HEIGHT, label: "How DiskLens reads a drive", body });
}

export const LIVE_CONSTANTS = {
  P,
  REACH: HEAD_REACH,
  CARDS,
  TRUTH,
  BLOCKS: BLOCKS.map((b) => b.label),
};
