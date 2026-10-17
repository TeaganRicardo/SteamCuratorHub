import { spawn } from 'node:child_process';
import { mkdir, stat, rename, rm } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
export const nativeBinaryPath = join(here, 'build', 'steam-stats-helper');
export async function buildNativeHelper({ force = false } = {}) {
  if (process.platform !== 'darwin' || process.arch !== 'arm64') {
    throw new Error('Native stats helper currently requires macOS arm64; no Steam operation was attempted.');
  }
  const source = join(here, 'steam-stats-helper.mm');
  if (!force) {
    const [src, bin] = await Promise.all([stat(source), stat(nativeBinaryPath).catch(() => null)]);
    if (bin && bin.mtimeMs >= src.mtimeMs) return nativeBinaryPath;
  }
  await mkdir(dirname(nativeBinaryPath), { recursive: true });
  const temporary = `${nativeBinaryPath}.${process.pid}.${Date.now()}.tmp`;
  try {
    await new Promise((resolve, reject) => {
    const child = spawn('clang++', ['-std=c++20', '-fobjc-arc', '-Wall', '-Wextra', '-Werror',
      '-O2', '-framework', 'Foundation', source, '-o', temporary], { stdio: ['ignore', 'pipe', 'pipe'] });
    let diagnostics = '';
    child.stdout.on('data', c => { diagnostics += c; });
    child.stderr.on('data', c => { diagnostics += c; });
    child.on('error', reject);
    child.on('exit', code => code === 0 ? resolve() : reject(new Error(`Native helper build failed (${code}): ${diagnostics}`)));
  });
    await rename(temporary, nativeBinaryPath);
  } finally { await rm(temporary, { force: true }); }
  return nativeBinaryPath;
}
if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  buildNativeHelper({ force: true }).then(path => process.stdout.write(`${path}\n`), error => {
    process.stderr.write(`${error.message}\n`); process.exitCode = 1;
  });
}
