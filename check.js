// Treemap layout check:  node check.js
// Backend checks live in server.ps1:  pwsh -File server.ps1 -SelfTest
const fs = require('fs'), assert = require('assert');

const html = fs.readFileSync(__dirname + '/ui.html', 'utf8');
const src = html.slice(html.indexOf('function squarify'), html.indexOf('function drawMap'));
const squarify = new Function(src + '; return squarify;')();

const items = [500, 300, 120, 60, 15, 4, 1].map(v => ({ value: v }));
const tiles = squarify(items, 0, 0, 800, 500);

assert.strictEqual(tiles.length, items.length, 'every item gets a tile');
const area = tiles.reduce((s, t) => s + t.w * t.h, 0);
assert.ok(Math.abs(area - 800 * 500) < 1, 'tiles cover the rect, area=' + area);
for (const t of tiles) {
  assert.ok(t.w > 0 && t.h > 0, 'no zero-size tile');
  assert.ok(t.x >= -1e-9 && t.y >= -1e-9 && t.x + t.w <= 800.001 && t.y + t.h <= 500.001, 'inside bounds');
}

// Real drive data: a few giants and a long tail of crumbs, in an odd-sized
// frame. This is what pushed tiles a pixel past the frame before clipping.
const real = [448.7, 96.8, 46.7, 45.6, 37.6, 36, 5.7, 3.1, .7, .5, .1, .02, .004]
  .map(v => ({ value: v * 1024 }));
for (const [w, h] of [[647, 513], [333, 901], [1920, 140]]) {
  for (const t of squarify(real, 0, 0, w, h)) {
    assert.ok(t.x + t.w <= w + 1e-6 && t.y + t.h <= h + 1e-6,
      `tile escapes the ${w}x${h} frame at ${t.x.toFixed(2)},${t.y.toFixed(2)}`);
  }
}
const worst = Math.max(...tiles.map(t => Math.max(t.w / t.h, t.h / t.w)));
assert.ok(worst < 30, 'aspect ratios stay clickable, worst=' + worst.toFixed(1));
assert.strictEqual(squarify([], 0, 0, 100, 100).length, 0, 'empty input');
assert.strictEqual(squarify([{ value: 0 }], 0, 0, 100, 100).length, 0, 'zero total');

console.log('treemap check OK  worst aspect ' + worst.toFixed(2));
