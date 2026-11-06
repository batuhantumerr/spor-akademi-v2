// Resolves every conflict hunk in the given files by keeping the current branch ("ours") side.
const fs = require('fs');
for (const path of process.argv.slice(2)) {
  const text = fs.readFileSync(path, 'utf8');
  fs.writeFileSync(path, text.replace(/<<<<<<< [^\n]*\n([\s\S]*?)=======\n[\s\S]*?>>>>>>> [^\n]*\n/g, '$1'));
}
