import { readdirSync } from 'node:fs';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
function walk(path) {
  return readdirSync(path, { withFileTypes: true }).flatMap(entry => entry.name === 'build' ? [] :
    entry.isDirectory() ? walk(join(path, entry.name)) : /\.(?:mjs|cjs)$/.test(entry.name) ? [join(path, entry.name)] : []);
}
const files = walk('desktop');
for (const path of files) {
  const result = spawnSync(process.execPath, ['--check', path], { stdio: 'inherit' });
  if (result.status !== 0) process.exit(result.status ?? 1);
}
console.log(`Syntax checked ${files.length} desktop modules`);
