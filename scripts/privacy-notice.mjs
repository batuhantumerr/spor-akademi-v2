#!/usr/bin/env node
// P4-06 (NFR-PRV-06, DEC-543): the privacy notice has one source, docs/aydinlatma-metni.md. This script renders it to
// the static page both apps serve at /aydinlatma-metni.html (web/admin/public and web/parent/public), so the three can
// never drift apart:
//   node scripts/privacy-notice.mjs          writes both pages
//   node scripts/privacy-notice.mjs --check  fails when a page differs from the source (repo-checks job)
// Only the Markdown the notice uses is supported: headings, paragraphs, lists, tables, block quotes, **bold**,
// *italic*, `code` and [links](...) (rendered as their text). The page has no script and no inline style, so the apps'
// Content-Security-Policy applies unchanged.
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(fileURLToPath(new URL('.', import.meta.url)), '..');
const source = join(root, 'docs', 'aydinlatma-metni.md');
const targets = ['web/admin/public/aydinlatma-metni.html', 'web/parent/public/aydinlatma-metni.html'].map((p) =>
  join(root, p),
);

const escape = (text) =>
  text.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;');

function inline(text) {
  return escape(text)
    .replace(/`([^`]+)`/g, '<code>$1</code>')
    .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
    .replace(/\*([^*]+)\*/g, '<em>$1</em>')
    .replace(/\[([^\]]+)\]\([^)]+\)/g, '$1');
}

function render(markdown) {
  const lines = markdown.replace(/\r\n/g, '\n').split('\n');
  const out = [];
  let title = '';
  let i = 0;
  const isBlockStart = (line) => /^(#|- |\||>)/.test(line) || line.trim() === '';
  while (i < lines.length) {
    const line = lines[i];
    if (line.trim() === '') {
      i++;
    } else if (line.startsWith('# ')) {
      title = line.slice(2).trim();
      out.push(`<h1>${inline(title)}</h1>`);
      i++;
    } else if (line.startsWith('## ')) {
      out.push(`<h2>${inline(line.slice(3).trim())}</h2>`);
      i++;
    } else if (line.startsWith('>')) {
      const quote = [];
      while (i < lines.length && lines[i].startsWith('>')) {
        quote.push(lines[i].replace(/^> ?/, ''));
        i++;
      }
      out.push('<blockquote>');
      for (const paragraph of quote.join('\n').split(/\n\s*\n/)) {
        out.push(`<p>${inline(paragraph.replace(/\n/g, ' ').trim())}</p>`);
      }
      out.push('</blockquote>');
    } else if (line.startsWith('- ')) {
      out.push('<ul>');
      while (i < lines.length && lines[i].startsWith('- ')) {
        let item = lines[i].slice(2).trim();
        i++;
        while (i < lines.length && lines[i].startsWith('  ') && lines[i].trim() !== '') {
          item += ' ' + lines[i].trim();
          i++;
        }
        out.push(`<li>${inline(item)}</li>`);
      }
      out.push('</ul>');
    } else if (line.startsWith('|')) {
      const rows = [];
      while (i < lines.length && lines[i].startsWith('|')) {
        rows.push(lines[i]);
        i++;
      }
      const cells = (row) =>
        row
          .slice(1, -1)
          .split('|')
          .map((c) => inline(c.trim()));
      out.push('<table>');
      out.push(`<thead><tr>${cells(rows[0]).map((c) => `<th scope="col">${c}</th>`).join('')}</tr></thead>`);
      out.push('<tbody>');
      for (const row of rows.slice(2)) {
        out.push(`<tr>${cells(row).map((c) => `<td>${c}</td>`).join('')}</tr>`);
      }
      out.push('</tbody>');
      out.push('</table>');
    } else {
      const paragraph = [];
      while (i < lines.length && !isBlockStart(lines[i])) {
        paragraph.push(lines[i].trim());
        i++;
      }
      out.push(`<p>${inline(paragraph.join(' '))}</p>`);
    }
  }

  return [
    '<!doctype html>',
    '<html lang="tr">',
    '<head>',
    '<meta charset="utf-8">',
    '<meta name="viewport" content="width=device-width, initial-scale=1">',
    '<meta name="robots" content="noindex">',
    `<title>${inline(title)} · Spor Akademi</title>`,
    '</head>',
    '<!-- Generated from docs/aydinlatma-metni.md by scripts/privacy-notice.mjs; do not edit by hand (DEC-543). -->',
    '<body>',
    '<main>',
    ...out,
    '</main>',
    '</body>',
    '</html>',
    '',
  ].join('\n');
}

const html = render(readFileSync(source, 'utf8'));
if (process.argv.includes('--check')) {
  const stale = targets.filter((target) => {
    try {
      return readFileSync(target, 'utf8').replace(/\r\n/g, '\n') !== html;
    } catch {
      return true;
    }
  });
  if (stale.length > 0) {
    console.error(
      `privacy-notice: ${stale.map((t) => t.slice(root.length + 1)).join(', ')} differ from docs/aydinlatma-metni.md; run node scripts/privacy-notice.mjs`,
    );
    process.exit(1);
  }
  console.log('privacy-notice: OK');
} else {
  for (const target of targets) {
    writeFileSync(target, html);
  }
  console.log(`privacy-notice: wrote ${targets.length} pages`);
}
