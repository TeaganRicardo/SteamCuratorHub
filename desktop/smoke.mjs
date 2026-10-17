import { mkdtemp, readFile, rm, mkdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { spawn } from 'node:child_process';
import { createRequire } from 'node:module';
import { buildNativeHelper } from './native/build.mjs';
const require = createRequire(import.meta.url);
const packagedBinary = process.argv.find(value => value.startsWith('--binary='))?.slice('--binary='.length);
await buildNativeHelper();
const profile = await mkdtemp(join(tmpdir(), 'sch-smoke-'));
const output = resolve(packagedBinary ? '.local/packaged-smoke.json' : '.local/mvp-smoke.json'); await mkdir(resolve('.local'), { recursive: true });
const env = { ...process.env }; delete env.ELECTRON_RUN_AS_NODE;
try {
  const code = await new Promise((resolveCode, reject) => {
    const child = spawn(packagedBinary ? resolve(packagedBinary) : require('electron'), [...(packagedBinary ? [] : ['.']), '--fixture', '--smoke', `--fixture-data=${profile}`, `--smoke-output=${output}`], { env, stdio: 'inherit' });
    const timer = setTimeout(() => { child.kill('SIGKILL'); reject(new Error('Electron smoke exceeded 60 seconds')); }, 60000);
    child.once('error', reject); child.once('exit', code => { clearTimeout(timer); resolveCode(code); });
  });
  const result = JSON.parse(await readFile(output, 'utf8'));
  console.log(JSON.stringify(result, null, 2));
  if (code !== 0 || result.status !== 'passed') process.exitCode = 1;
} finally { await rm(profile, { recursive: true, force: true }); }
