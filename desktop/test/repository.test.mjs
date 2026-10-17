import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { syncBuiltinESMExports } from 'node:module';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { JsonRepository } from '../repository.mjs';

function setup(t) {
  const dir = fs.mkdtempSync(join(tmpdir(), 'sch-repository-fixture-'));
  t.after(() => {
    t.mock.restoreAll(); syncBuiltinESMExports(); fs.rmSync(dir, { recursive: true, force: true });
  });
  const path = join(dir, 'workspace.json');
  return { dir, path, repository: new JsonRepository(path) };
}

test('atomic repository flushes the same exclusively created writable handle before publishing', t => {
  const s = setup(t); const handles = new Map(); const calls = [];
  const { openSync, writeFileSync, fsyncSync, closeSync, renameSync } = fs;
  t.mock.method(fs, 'openSync', (path, flags, mode) => {
    const fd = openSync(path, flags, mode); handles.set(fd, { flags, mode }); calls.push(['open', fd]); return fd;
  });
  t.mock.method(fs, 'writeFileSync', (fd, content, ...options) => {
    assert.equal(typeof fd, 'number'); assert.equal(handles.get(fd)?.flags, 'wx');
    calls.push(['write', fd]); return writeFileSync(fd, content, ...options);
  });
  t.mock.method(fs, 'fsyncSync', fd => {
    // Reproduce Windows' writable-handle requirement on every test host. The
    // former openSync(temp, 'r') implementation fails here without any test skip.
    const handle = handles.get(fd);
    if (!handle || !/[wa+]/.test(handle.flags)) throw Object.assign(new Error('read-only flush'), { code: 'EPERM' });
    assert.equal(handle.mode, 0o600); calls.push(['flush', fd]); return fsyncSync(fd);
  });
  t.mock.method(fs, 'closeSync', fd => { calls.push(['close', fd]); return closeSync(fd); });
  t.mock.method(fs, 'renameSync', (from, to) => { calls.push(['rename']); return renameSync(from, to); });
  syncBuiltinESMExports();
  s.repository.save({ text: 'Synthetic first draft' });
  assert.deepEqual(s.repository.load(), { text: 'Synthetic first draft' });
  assert.deepEqual(calls.map(call => call[0]), ['open', 'write', 'flush', 'close', 'rename']);
  assert.equal(new Set(calls.filter(call => call[1] !== undefined).map(call => call[1])).size, 1);
  assert.deepEqual(fs.readdirSync(s.dir), ['workspace.json']);
});

test('a failed disk flush closes and removes its stage without changing current or previous bytes', t => {
  const s = setup(t); s.repository.save({ revision: 1 }); s.repository.save({ revision: 2 });
  const current = fs.readFileSync(s.path); const previous = fs.readFileSync(`${s.path}.previous`);
  let fd;
  t.mock.method(fs, 'fsyncSync', descriptor => { fd = descriptor; throw Object.assign(new Error('synthetic flush failed'), { code: 'EIO' }); });
  syncBuiltinESMExports();
  assert.throws(() => s.repository.save({ revision: 3 }), { code: 'EIO' });
  assert.deepEqual(fs.readFileSync(s.path), current); assert.deepEqual(fs.readFileSync(`${s.path}.previous`), previous);
  assert.deepEqual(fs.readdirSync(s.dir).sort(), ['workspace.json', 'workspace.json.previous']);
  assert.throws(() => fs.fstatSync(fd), { code: 'EBADF' });
});

test('a failed staged write also closes the writable handle and leaves the current workspace intact', t => {
  const s = setup(t); s.repository.save({ revision: 1 }); const current = fs.readFileSync(s.path); let fd;
  t.mock.method(fs, 'writeFileSync', descriptor => { fd = descriptor; throw Object.assign(new Error('synthetic disk full'), { code: 'ENOSPC' }); });
  syncBuiltinESMExports();
  assert.throws(() => s.repository.save({ revision: 2 }), { code: 'ENOSPC' });
  assert.deepEqual(fs.readFileSync(s.path), current); assert.deepEqual(fs.readdirSync(s.dir), ['workspace.json']);
  assert.throws(() => fs.fstatSync(fd), { code: 'EBADF' });
});

test('successful replacement preserves the exact previous bytes and rejects oversize UTF-8 before staging', t => {
  const s = setup(t); s.repository.save({ text: 'Synthetic draft' }); const first = fs.readFileSync(s.path);
  s.repository.save({ text: 'Synthetic edited draft' });
  assert.deepEqual(fs.readFileSync(`${s.path}.previous`), first);
  const current = fs.readFileSync(s.path);
  assert.throws(() => s.repository.save({ text: '评'.repeat(7_000_000) }), /workspace_too_large/);
  assert.deepEqual(fs.readFileSync(s.path), current); assert.deepEqual(fs.readFileSync(`${s.path}.previous`), first);
  assert.deepEqual(fs.readdirSync(s.dir).sort(), ['workspace.json', 'workspace.json.previous']);
});
