import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, writeFileSync, rmSync, readdirSync, existsSync, statSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { openWorkspace } from '../recovery.mjs';
import { Workspace } from '../workspace.mjs';
import { JsonRepository } from '../repository.mjs';

const ACCOUNT = '76561198000000000';
function fixture() {
  const dir = mkdtempSync(join(tmpdir(), 'sch-recovery-'));
  const path = join(dir, 'workspace.json');
  const workspace = new Workspace({ load: () => null, save() {} }); workspace.bindAccount(ACCOUNT);
  const task = workspace.createTask({ appId: 1001, title: 'Synthetic', groupId: '45134494' });
  workspace.saveDraft({ taskId: task.id, language: 'english', kind: 'long', text: 'Saved review', expectedRevision: 0 });
  const attempt = workspace.preparePublication(task.id, 'english', 'long');
  workspace.recordPublication({ taskId: task.id, attemptId: attempt.id, state: 'verified',
    resultUrl: `https://steamcommunity.com/profiles/${ACCOUNT}/recommended/1001/`, evidenceSource: 'steam_api_readback' });
  const backup = workspace.exportBackup();
  return { dir, path, task, backup, cleanup() { rmSync(dir, { recursive: true, force: true }); } };
}
function dialogs(decisions, files = []) {
  const calls = [];
  return { calls, async showMessageBox(options) {
    calls.push(options);
    if (options.type === 'error') return { response: 0 };
    if (!decisions.length) throw new Error('unexpected recovery dialog');
    return { response: decisions.shift() };
  }, async showOpenDialog(options) {
    calls.push(options);
    const value = files.shift();
    return value ? { canceled: false, filePaths: [value] } : { canceled: true, filePaths: [] };
  } };
}

test('normal workspace and a new missing workspace open without a dialog or evidence downgrade', async () => {
  const f = fixture();
  try {
    const dialog = dialogs([]);
    const fresh = await openWorkspace({ path: f.path, dialog });
    assert.equal(fresh.snapshot().tasks.length, 0); assert.equal(dialog.calls.length, 0);
    new JsonRepository(f.path).save(f.backup);
    const restored = await openWorkspace({ path: f.path, dialog });
    assert.equal(restored.task(f.task.id).publications[0].state, 'verified');
    assert.equal(restored.recovery, undefined); assert.equal(dialog.calls.length, 0);
  } finally { f.cleanup(); }
});

test('exit after corruption leaves the exact original bytes and existing backup untouched', async () => {
  const f = fixture();
  try {
    writeFileSync(f.path, '{corrupt\n'); writeFileSync(`${f.path}.previous`, JSON.stringify(f.backup));
    const before = readFileSync(f.path); const previous = readFileSync(`${f.path}.previous`);
    assert.equal(await openWorkspace({ path: f.path, dialog: dialogs([0]) }), null);
    assert.deepEqual(readFileSync(f.path), before); assert.deepEqual(readFileSync(`${f.path}.previous`), previous);
    assert.equal(readdirSync(f.dir).some(name => name.includes('.corrupt-')), false);
  } finally { f.cleanup(); }
});

test('recovering previous validates and archives corruption, preserves previous, and downgrades imported publication evidence', async () => {
  const f = fixture();
  try {
    writeFileSync(f.path, '{broken'); writeFileSync(`${f.path}.previous`, JSON.stringify(f.backup));
    const previous = readFileSync(`${f.path}.previous`);
    const recovered = await openWorkspace({ path: f.path, dialog: dialogs([1]) });
    assert.equal(recovered.recovery.source, 'previous');
    assert.equal(readFileSync(recovered.recovery.archivePath, 'utf8'), '{broken');
    assert.deepEqual(readFileSync(`${f.path}.previous`), previous);
    assert.equal(recovered.task(f.task.id).publications[0].state, 'imported_unverified');
    assert.equal(recovered.task(f.task.id).publications[0].imported, true);
    const disk = JSON.parse(readFileSync(f.path, 'utf8'));
    assert.equal(disk.tasks[0].publications[0].state, 'imported_unverified');
    if (process.platform !== 'win32') {
      assert.equal(statSync(f.path).mode & 0o777, 0o600);
      assert.equal(statSync(recovered.recovery.archivePath).mode & 0o777, 0o600);
    }
    assert.equal(readdirSync(f.dir).some(name => name.includes('.recovery-')), false);
  } finally { f.cleanup(); }
});

test('invalid JSON and structurally invalid selected backups cannot overwrite or archive the original', async () => {
  for (const invalid of ['not json', JSON.stringify({ version: 1, tasks: [{}], groups: [] })]) {
    const f = fixture();
    try {
      writeFileSync(f.path, '{broken');
      const source = join(f.dir, 'chosen.json'); writeFileSync(source, invalid);
      const dialog = dialogs([2, 0], [source]);
      assert.equal(await openWorkspace({ path: f.path, dialog }), null);
      assert.equal(readFileSync(f.path, 'utf8'), '{broken');
      assert.equal(readFileSync(source, 'utf8'), invalid);
      assert.equal(readdirSync(f.dir).some(name => name.includes('.corrupt-') || name.includes('.recovery-')), false);
      assert.ok(dialog.calls.some(call => call.type === 'error'));
    } finally { f.cleanup(); }
  }
});

test('oversized selected backup is rejected before recovery and invalid previous can fall back to another backup', async () => {
  const f = fixture();
  try {
    writeFileSync(f.path, '{broken');
    const large = join(f.dir, 'large.json'); writeFileSync(large, ' '.repeat(20 * 1024 * 1024 + 1));
    const good = join(f.dir, 'good.json'); writeFileSync(good, JSON.stringify(f.backup));
    const dialog = dialogs([1, 2, 2], [large, good]);
    const recovered = await openWorkspace({ path: f.path, dialog });
    assert.equal(recovered.recovery.source, 'selected_backup');
    assert.equal(recovered.task(f.task.id).title, 'Synthetic');
    assert.equal(readFileSync(recovered.recovery.archivePath, 'utf8'), '{broken');
    assert.equal(readFileSync(good, 'utf8'), JSON.stringify(f.backup));
    assert.equal(dialog.calls.filter(call => call.type === 'error').length, 2);
  } finally { f.cleanup(); }
});

test('cancelled backup selection returns to recovery options without changing any file', async () => {
  const f = fixture();
  try {
    writeFileSync(f.path, 'broken');
    assert.equal(await openWorkspace({ path: f.path, dialog: dialogs([2, 0], [null]) }), null);
    assert.equal(readFileSync(f.path, 'utf8'), 'broken');
    assert.deepEqual(readdirSync(f.dir), ['workspace.json']);
  } finally { f.cleanup(); }
});

test('a structurally invalid current file enters recovery and imported credentials are stripped', async () => {
  const f = fixture();
  try {
    writeFileSync(f.path, JSON.stringify({ version: 1, tasks: [{}], groups: [] }));
    f.backup.settings.apiKey = 'synthetic-secret';
    f.backup.tasks[0].research = { password: 'synthetic-secret', sources: [] };
    const source = join(f.dir, 'selected.json'); writeFileSync(source, JSON.stringify(f.backup));
    const recovered = await openWorkspace({ path: f.path, dialog: dialogs([2], [source]) });
    assert.equal(JSON.stringify(recovered.exportBackup()).includes('synthetic-secret'), false);
    assert.equal(readFileSync(f.path, 'utf8').includes('synthetic-secret'), false);
  } finally { f.cleanup(); }
});

test('external repair during the recovery dialog is not overwritten by an older backup', async () => {
  const f = fixture();
  try {
    writeFileSync(f.path, '{broken'); writeFileSync(`${f.path}.previous`, JSON.stringify(f.backup));
    const repaired = structuredClone(f.backup); repaired.tasks[0].title = 'Externally repaired';
    let count = 0;
    const dialog = { async showMessageBox(options) {
      if (options.type === 'error') return { response: 0 };
      count++; writeFileSync(f.path, JSON.stringify(repaired)); return { response: 1 };
    }, async showOpenDialog() { throw new Error('not expected'); } };
    const recovered = await openWorkspace({ path: f.path, dialog });
    assert.equal(recovered.task(f.task.id).title, 'Externally repaired');
    assert.equal(recovered.recovery, undefined); assert.equal(count, 1);
    assert.equal(readdirSync(f.dir).some(name => name.includes('.corrupt-') || name.includes('.recovery-')), false);
  } finally { f.cleanup(); }
});

test('a current file larger than the read bound enters recovery without being silently reset', async () => {
  const f = fixture();
  try {
    writeFileSync(f.path, ' '.repeat(20 * 1024 * 1024 + 1));
    assert.equal(await openWorkspace({ path: f.path, dialog: dialogs([0]) }), null);
    assert.equal(statSync(f.path).size, 20 * 1024 * 1024 + 1);
    assert.equal(existsSync(`${f.path}.previous`), false);
  } finally { f.cleanup(); }
});
