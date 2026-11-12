#!/usr/bin/env node
// P3-10 (DEC-429): user-facing text lives only in the dictionaries (web/admin/src/app/core/i18n, web/parent/src/i18n).
// A simple scanner, run by the repo-checks job, that flags text written straight into components:
//   1. Turkish letters anywhere outside comments (catches most Turkish text, in templates and in code);
//   2. literal text nodes in Angular inline templates (`template: \`...\``) and in JSX;
//   3. literal aria-label / title / placeholder / alt attributes.
// Tests, generated API clients, test fixtures and the dictionaries themselves are skipped. A line that really needs
// a literal (for example a Turkish-only slug rule) ends with the comment `i18n-ignore` and a reason.
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(fileURLToPath(new URL('.', import.meta.url)), '..');
const sources = [
  { dir: 'web/admin/src/app', exts: ['.ts', '.html'] },
  { dir: 'web/parent/src', exts: ['.ts', '.tsx'] },
];
const skip = [
  /\.spec\.ts$/,
  /\.test\.tsx?$/,
  /[\\/]i18n[\\/]/,
  /^web[\\/]admin[\\/]src[\\/]app[\\/]api[\\/]/,
  /^web[\\/]parent[\\/]src[\\/]api[\\/]schema\.gen\.ts$/,
  /^web[\\/]parent[\\/]src[\\/]test[\\/]/,
];

const TURKISH = /[çğıİöşüÇĞÖŞÜ]/;
const LITERAL_ATTRIBUTE = /(?<![\w.[-])(aria-label|title|placeholder|alt)="([^"{}]*[A-Za-z][^"{}]*)"/g;

function* walk(dir) {
  for (const name of readdirSync(dir)) {
    const path = join(dir, name);
    if (statSync(path).isDirectory()) yield* walk(path);
    else yield path;
  }
}

/** Blanks out comments but keeps line numbers (strings with comment markers are rare enough here). */
function withoutComments(source) {
  return source
    .replace(/\/\*[\s\S]*?\*\//g, (m) => m.replace(/[^\n]/g, ' '))
    .replace(/<!--[\s\S]*?-->/g, (m) => m.replace(/[^\n]/g, ' '))
    .replace(/(^|[^:'"`\\])\/\/.*$/gm, (m, before) => before + ' '.repeat(m.length - before.length));
}

function lineOf(source, index) {
  return source.slice(0, index).split('\n').length;
}

/** Text between tags, after removing interpolations and control-flow blocks. */
function textNodes(template) {
  const cleaned = template
    .replace(/\{\{[\s\S]*?\}\}/g, (m) => ' '.repeat(m.length))
    .replace(/@(?:if|else if|for|switch|case|defer)\b[^{\n]*\{|@(?:else|default|empty|placeholder|loading)\s*\{|\}/g, (m) =>
      ' '.repeat(m.length),
    )
    .replace(/@let\b[^;]*;/g, (m) => ' '.repeat(m.length))
    .replace(/&[a-z]+;|&#\d+;/g, (m) => ' '.repeat(m.length));
  const found = [];
  for (const match of cleaned.matchAll(/>([^<>]*)</g)) {
    const text = match[1].trim();
    if (/[A-Za-zÇĞİÖŞÜçğıöşü]{2,}/.test(text)) found.push({ index: match.index + 1, text });
  }
  return found;
}

const problems = [];
for (const { dir, exts } of sources) {
  for (const absolute of walk(join(root, dir))) {
    const file = relative(root, absolute);
    if (!exts.some((ext) => file.endsWith(ext)) || skip.some((pattern) => pattern.test(file))) continue;
    const original = readFileSync(absolute, 'utf8');
    const ignored = new Set(
      original.split('\n').flatMap((line, i) => (line.includes('i18n-ignore') ? [i + 1] : [])),
    );
    const source = withoutComments(original);
    const report = (line, message) => {
      if (!ignored.has(line)) problems.push(`${file.split(sep).join('/')}:${line}: ${message}`);
    };

    source.split('\n').forEach((line, i) => {
      if (TURKISH.test(line)) report(i + 1, `Turkish text outside the dictionary: ${line.trim()}`);
    });

    const templates = [];
    if (file.endsWith('.html')) templates.push({ start: 0, body: source });
    for (const match of source.matchAll(/template:\s*`([\s\S]*?)`/g)) {
      templates.push({ start: match.index + match[0].indexOf('`') + 1, body: match[1] });
    }
    for (const { start, body } of templates) {
      for (const node of textNodes(body)) {
        report(lineOf(source, start + node.index), `literal template text: ${node.text}`);
      }
    }
    if (file.endsWith('.tsx')) {
      for (const node of source.matchAll(/>\s*([A-Za-z][^<>{}=;()]*?)\s*<\//g)) {
        report(lineOf(source, node.index), `literal JSX text: ${node[1]}`);
      }
    }
    if (file.endsWith('.html') || file.endsWith('.tsx') || templates.length > 0) {
      for (const match of source.matchAll(LITERAL_ATTRIBUTE)) {
        report(lineOf(source, match.index), `literal ${match[1]}: ${match[2]}`);
      }
    }
  }
}

if (problems.length > 0) {
  console.error(`check-i18n: ${problems.length} text(s) outside the dictionaries:`);
  for (const problem of problems) console.error(`  ${problem}`);
  console.error('Move the text to core/i18n (admin) or src/i18n (parent), or mark a deliberate literal with i18n-ignore.');
  process.exit(1);
}
console.log('check-i18n: OK');
