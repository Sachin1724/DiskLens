// UI contract checks:  node check-ui.js
// Treemap layout lives in check.js; backend checks in server.ps1 -SelfTest.
const fs = require('fs'), assert = require('assert');
const html = fs.readFileSync(__dirname + '/ui.html', 'utf8');

const lin = c => { c /= 255; return c <= .04045 ? c / 12.92 : Math.pow((c + .055) / 1.055, 2.4); };
const lum = hex => {
  const n = parseInt(hex.slice(1), 16);
  return .2126 * lin(n >> 16 & 255) + .7152 * lin(n >> 8 & 255) + .0722 * lin(n & 255);
};
const contrast = (a, b) => {
  const [hi, lo] = [lum(a), lum(b)].sort((x, y) => y - x);
  return (hi + .05) / (lo + .05);
};

// Every verdict block ships its own fill AND the text colour drawn on it.
// Tile labels are 13px, so the pair has to clear 4.5:1 — a loud palette is no
// excuse for a label nobody can read.
// Tokens come from the dark :root, then the light theme's overrides on top of
// a copy - both themes have to clear the bar, not just the one you looked at.
const block = sel => {
  const i = html.indexOf(sel);
  assert.ok(i > 0, 'missing token block ' + sel);
  return html.slice(i, html.indexOf('}', i));
};
const tokensIn = (text, prefix) => {
  const out = {};
  for (const m of text.matchAll(new RegExp('--' + prefix + '-([A-Z]+):\\s*(#[0-9a-f]{6})', 'g'))) out[m[1]] = m[2];
  return out;
};

const darkRoot = block(':root {');
const lightRoot = block(':root[data-theme="light"] {');
const themes = {
  dark: { fills: tokensIn(darkRoot, 'v'), tones: tokensIn(darkRoot, 'vt') },
  light: {
    fills: { ...tokensIn(darkRoot, 'v'), ...tokensIn(lightRoot, 'v') },
    tones: { ...tokensIn(darkRoot, 'vt'), ...tokensIn(lightRoot, 'vt') }
  }
};

let worst = Infinity, worstName = '';
for (const [theme, { fills, tones }] of Object.entries(themes)) {
  const names = Object.keys(fills);
  assert.strictEqual(names.length, 7, `${theme}: all seven verdict fills, found ${names.length}`);
  for (const n of names) {
    assert.ok(tones[n], `${theme}/${n} has no --vt- text colour`);
    const r = contrast(fills[n], tones[n]);
    assert.ok(r >= 4.5, `${theme}/${n}: ${tones[n]} on ${fills[n]} is only ${r.toFixed(2)}:1`);
    if (r < worst) { worst = r; worstName = theme + '/' + n; }
  }
}

// A verdict block has to be visible against the canvas it sits on, not just legible.
const canvas = { dark: '#07070a', light: '#f4f1e8' };
for (const [theme, { fills }] of Object.entries(themes)) {
  for (const [n, hex] of Object.entries(fills)) {
    const r = contrast(hex, canvas[theme]);
    assert.ok(r >= 1.25, `${theme}/${n}: ${hex} barely separates from the ${theme} canvas (${r.toFixed(2)}:1)`);
  }
}

// The loud surfaces outside the verdict set carry text too.
for (const [name, bg, fg] of [['lime chip', '#c6f135', '#07070a'], ['violet stat', '#6b4bff', '#ffffff'],
                              ['cyan card', '#16d9cf', '#07070a'], ['dark body', '#07070a', '#f4f4f7'],
                              ['dark muted', '#121218', '#9a9aa8'], ['ivory body', '#f4f1e8', '#15130d'],
                              ['ivory muted', '#fffdf8', '#585243'],
                              ['ivory lime', '#a8cf00', '#07070a']]) {
  const r = contrast(bg, fg);
  assert.ok(r >= 4.5, `${name}: ${fg} on ${bg} is only ${r.toFixed(2)}:1`);
}

assert.ok(/data-theme="light"/.test(html), 'a light theme exists');
assert.ok(/prefers-color-scheme: light/.test(html), 'the OS preference is the default');

assert.ok(/prefers-reduced-motion/.test(html), 'honours reduced motion');
assert.ok(/prefers-contrast/.test(html), 'honours a contrast preference');
assert.ok(/:focus-visible/.test(html), 'keyboard focus is visible');
assert.ok(/aria-sort/.test(html), 'the table reports its sort state');
assert.ok(/role="dialog"[\s\S]{0,120}aria-modal/.test(html), 'the confirm sheet is a modal dialog');
assert.ok(/aria-live/.test(html), 'toasts are announced');
assert.ok(!/[\u{1F300}-\u{1FAFF}\u{2700}-\u{27BF}]/u.test(html), 'no emoji standing in for icons');

// The file panel's two bits of real logic: how old a file reads, and what the
// extension chip says.
const helpers = html.slice(html.indexOf('function age('), html.indexOf('function filesHTML'));
const { age, extOf } = new Function(helpers + '; return { age, extOf };')();
const day = 86400000;
const ago = ms => new Date(Date.now() - ms).toISOString().slice(0, 10);

assert.strictEqual(age(ago(0)).text, 'today');
assert.strictEqual(age(ago(5 * day)).text, '5d old');
assert.strictEqual(age(ago(60 * day)).text, '2mo old');
assert.ok(!age(ago(300 * day)).stale, 'under a year is not stale');
assert.ok(age(ago(400 * day)).stale, 'past a year is stale');
assert.strictEqual(age(ago(400 * day)).text, '1.1y old');
assert.strictEqual(age(ago(1100 * day)).text, '3y old');
assert.strictEqual(age('not-a-date').text, 'not-a-date', 'an unparseable date falls back to itself');
assert.strictEqual(extOf('movie.final.MKV'), 'MKV');
assert.strictEqual(extOf('README'), 'FILE');
assert.strictEqual(extOf('.gitignore'), 'FILE', 'a dotfile has no extension');
assert.strictEqual(extOf('a.verylongextension'), 'FILE', 'absurd suffixes are not extensions');

// The keep/drop call: the one piece of judgement in the app, so it gets pinned
// down here rather than eyeballed in the browser.
const adviceSrc = html.slice(html.indexOf('const DAY = 86400000;'), html.indexOf('const callChip'));
const { advice } = new Function(
  'const VERDICT = ' + JSON.stringify({
    CACHE: 'rebuilds itself', MODELS: 're-downloadable', HEAVY: 'prune in its own tool',
    DATA: 'your files', SYSTEM: 'Windows needs it', UNKNOWN: 'unclassified', FILES: 'loose files'
  }) + '; const key = v => VERDICT[v] ? v : "UNKNOWN";' + adviceSrc + '; return { advice };')();

const daysAgo = d => new Date(Date.now() - d * 86400000).toISOString().slice(0, 10);
const GB = 1 << 30;
const call = (verdict, days, size = GB, removable = true) =>
  advice({ verdict, size, removable, fresh: days === null ? null : daysAgo(days) }).tag;

assert.strictEqual(call('CACHE', 2, GB, false), 'KEEP', 'a protected folder is never a drop');
assert.strictEqual(call('DATA', 900), 'KEEP', 'your own files stay, however old');
assert.strictEqual(call('SYSTEM', 900, GB, false), 'KEEP');
assert.strictEqual(call('HEAVY', 900), 'REVIEW', 'VM disks are shrunk, not deleted');
assert.strictEqual(call('CACHE', 2), 'REVIEW', 'a cache written this week is in use');
assert.strictEqual(call('CACHE', 60), 'DROP', 'a cache cold for two months is dead weight');
assert.strictEqual(call('MODELS', 10), 'REVIEW');
assert.strictEqual(call('MODELS', 120), 'DROP');
assert.strictEqual(call('UNKNOWN', 400, GB), 'DROP', 'big and a year cold');
assert.strictEqual(call('UNKNOWN', 400, 5 << 20), 'REVIEW', 'small and old is not worth the risk');
assert.strictEqual(call('UNKNOWN', null), 'REVIEW', 'no date means no opinion');
assert.strictEqual(call('CACHE', null), 'REVIEW', 'an unread cache is not assumed dead');
assert.ok(/untouched 2 months/.test(advice({ verdict: 'CACHE', size: GB, removable: true, fresh: daysAgo(60) }).why),
  'the reason names the evidence');

console.log(`ui checks OK  worst pair ${worstName} ${worst.toFixed(2)}:1`);
