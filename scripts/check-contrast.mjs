#!/usr/bin/env node
// P3-11 (DEC-440): contrast of the theme tokens in both themes, checked by the repo-checks job.
// Reads the `theme-tokens` block of web/admin/src/styles.css (the parent app uses a byte-for-byte copy,
// scripts/check-theme-tokens.sh) and checks every foreground/background pair the components use:
//   - text: 4.5:1 (WCAG 1.4.3); the 12px badges and small labels are normal-size text;
//   - focus rings, form control borders and chart series: 3:1 against the surfaces they sit on (WCAG 1.4.11).
// Translucent backgrounds (`bg-success/10` and the like) are blended over the surface first, as the browser does.
// axe checks the rendered text of every audited screen as well (e2e/support/wcag.ts); this catches a token change
// before any screen is rendered, including colours only some states use.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(fileURLToPath(new URL('.', import.meta.url)), '..');
const css = readFileSync(join(root, 'web/admin/src/styles.css'), 'utf8');
const block = css.slice(css.indexOf('theme-tokens:start'), css.indexOf('theme-tokens:end'));

function tokens(selector) {
  const start = block.indexOf(`${selector} {`);
  const body = block.slice(start, block.indexOf('}', start));
  return Object.fromEntries(
    [...body.matchAll(/--([\w-]+):\s*(#[0-9a-fA-F]{6})\s*;/g)].map((m) => [m[1], m[2]]),
  );
}

const light = tokens(':root');
const dark = { ...light, ...tokens('.dark') };

const rgb = (hex) => [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16));
const channel = (c) => {
  const s = c / 255;
  return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
};
const luminance = ([r, g, b]) => 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
const ratio = (a, b) => {
  const [x, y] = [luminance(a), luminance(b)].sort((p, q) => q - p);
  return (x + 0.05) / (y + 0.05);
};
/** `color` at `alpha` over `surface` (Tailwind's `/10`, `/50`, ... modifiers). */
const blend = (color, alpha, surface) => color.map((c, i) => Math.round(c * alpha + surface[i] * (1 - alpha)));

/**
 * [foreground, background, minimum, background alpha?]. A background written `token/alpha` is blended over `card`.
 */
const pairs = [
  // Body text and secondary text on every surface.
  ['foreground', 'background', 4.5],
  ['foreground', 'card', 4.5],
  ['muted-foreground', 'background', 4.5],
  ['muted-foreground', 'card', 4.5],
  ['muted-foreground', 'muted', 4.5],
  ['secondary-foreground', 'secondary', 4.5],
  ['sidebar-foreground', 'sidebar', 4.5],
  // Links, active menu items and status text on cards.
  ['primary', 'card', 4.5],
  ['primary', 'background', 4.5],
  ['destructive', 'card', 4.5],
  ['success', 'card', 4.5],
  ['warning', 'card', 4.5],
  ['info', 'card', 4.5],
  // Filled buttons.
  ['primary-foreground', 'primary', 4.5],
  ['destructive-foreground', 'destructive', 4.5],
  // Badges and notices: tinted background (10%) over the card.
  ['success', 'success/0.1', 4.5],
  ['warning', 'warning/0.1', 4.5],
  ['destructive', 'destructive/0.1', 4.5],
  ['info', 'info/0.1', 4.5],
  ['primary', 'primary/0.1', 4.5],
  // Non-text: focus ring, form control borders, chart series.
  ['ring', 'card', 3],
  ['ring', 'background', 3],
  ['control-border', 'card', 3],
  ['control-border', 'background', 3],
  ['chart-1', 'card', 3],
  ['chart-2', 'card', 3],
  ['chart-3', 'card', 3],
  ['chart-4', 'card', 3],
  ['chart-5', 'card', 3],
];

let failures = 0;
for (const [theme, t] of [
  ['light', light],
  ['dark', dark],
]) {
  for (const [fg, bg, min] of pairs) {
    if (!t[fg]) {
      console.error(`${theme}: token --${fg} is missing`);
      failures++;
      continue;
    }
    const [bgName, alpha] = bg.split('/');
    const background = alpha ? blend(rgb(t[bgName]), Number(alpha), rgb(t.card)) : rgb(t[bgName]);
    const value = ratio(rgb(t[fg]), background);
    if (value < min) {
      console.error(`${theme}: --${fg} on ${bg} is ${value.toFixed(2)}:1, needs ${min}:1`);
      failures++;
    } else if (process.argv.includes('--verbose')) {
      console.log(`${theme}: --${fg} on ${bg} ${value.toFixed(2)}:1`);
    }
  }
}
if (failures > 0) {
  console.error(`check-contrast: ${failures} pair(s) below the WCAG 2.2 AA minimum.`);
  process.exit(1);
}
console.log(`check-contrast: ${pairs.length * 2} token pairs meet WCAG 2.2 AA in the light and dark themes.`);
