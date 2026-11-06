// Resolves merge conflicts in docs/roadmap.md by keeping, line by line, the checked ([x]) variant.
const fs = require('fs');
const path = process.argv[2] || 'docs/roadmap.md';
const text = fs.readFileSync(path, 'utf8');
const resolved = text.replace(/<<<<<<< [^\n]*\n([\s\S]*?)=======\n([\s\S]*?)>>>>>>> [^\n]*\n/g, (_, ours, theirs) => {
  const a = ours.split('\n'); const b = theirs.split('\n');
  const n = Math.max(a.length, b.length);
  const out = [];
  for (let i = 0; i < n; i++) {
    const x = a[i] ?? ''; const y = b[i] ?? '';
    out.push(x.includes('[x]') ? x : y.includes('[x]') ? y : x || y);
  }
  return out.join('\n');
});
fs.writeFileSync(path, resolved);
if (/<<<<<<<|>>>>>>>/.test(resolved)) { console.error('unresolved'); process.exit(1); }
