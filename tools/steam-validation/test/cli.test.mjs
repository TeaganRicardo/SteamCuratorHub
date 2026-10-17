import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, statSync } from 'node:fs';
import os from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const cli = fileURLToPath(new URL('../cli.mjs', import.meta.url));
test('CLI fixture writes private evidence and refuses to overwrite', () => {
  const dir = mkdtempSync(join(os.tmpdir(), 'sch-probe-'));
  try {
    const path = join(dir, 'report.json');
    const result = JSON.parse(execFileSync(process.execPath, [cli, '--fixture', '--output', path], { encoding: 'utf8' }));
    assert.equal(result.state, 'collected');
    const report = readFileSync(path, 'utf8');
    assert.equal(JSON.parse(report).mode, 'fixture');
    assert.equal(report.includes('76561198000000000'), false);
    if (process.platform !== 'win32') assert.equal(statSync(path).mode & 0o777, 0o600);
    assert.notEqual(spawnSync(process.execPath, [cli, '--fixture', '--output', path]).status, 0);
    assert.equal(readFileSync(path, 'utf8'), report);
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test('CLI requires an explicit single mode and a local live plan', () => {
  for (const args of [[], ['--live'], ['--fixture', '--live'], ['--fixture', '--plan', 'x'], ['--unknown']]) {
    const result = spawnSync(process.execPath, [cli, ...args], { encoding: 'utf8' });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /invalid_arguments/);
  }
});
