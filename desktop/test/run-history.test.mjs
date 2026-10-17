import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { Workspace } from '../workspace.mjs';
import { JsonRepository } from '../repository.mjs';

const account = '76561198000000000';
const otherAccount = '76561198000000001';
const startedAt = '2026-10-17T10:00:00.000Z';
const run = (overrides = {}) => ({ id: 'run-first', steamId: account, appId: 4444, name: 'Fixture Quest',
  generation: 2, state: 'running', startedAt, stoppedAt: null,
  deadline: '2026-10-17T11:00:00.000Z', totalTargetMinutes: 300, stopAfterMinutes: 60,
  baselineSteamMinutes: 120, steamMinutes: 120, readbackStatus: 'no_increase_observed',
  lastReadbackAt: startedAt, localElapsedSeconds: 900, localElapsedMinutes: 15,
  reason: null, requestStatus: 'requested_without_acknowledgement', stopStatus: null, ...overrides });
function memoryRepository() {
  let value = null;
  return { load: () => structuredClone(value), save(next) { value = structuredClone(next); } };
}

test('history updates a run by ID and preserves repeated sessions separately from tasks', () => {
  const workspace = new Workspace(memoryRepository()); workspace.bindAccount(account);
  workspace.recordRun(run());
  workspace.recordRun(run({ state: 'stopped', reason: 'user_stopped', stoppedAt: '2026-10-17T10:20:00.000Z',
    localElapsedSeconds: 1200, localElapsedMinutes: 20, steamMinutes: 130,
    readbackStatus: 'increase_observed', lastReadbackAt: '2026-10-17T10:21:00.000Z',
    stopStatus: 'requested_without_acknowledgement' }));
  workspace.recordRun(run({ id: 'run-second' }));
  const history = workspace.snapshot().runHistory;
  assert.equal(history.length, 2);
  assert.equal(history[0].state, 'stopped'); assert.equal(history[0].reason, 'user_stopped');
  assert.equal(history[0].deadline, '2026-10-17T11:00:00.000Z');
  assert.equal(history[0].localElapsedMinutes, 20);
  assert.equal(history[0].baselineSteamMinutes, 120); assert.equal(history[0].steamMinutes, 130);
  assert.equal(history[0].elapsedSource, 'local_monotonic_timer');
  assert.equal(history[0].lastReadbackAt, '2026-10-17T10:21:00.000Z');
  assert.equal(history[0].ownerSteamId, account);
  assert.equal(history[0].imported, false); assert.equal(history[0].recordSource, 'steam_service');
  assert.deepEqual(workspace.snapshot().tasks, []);
  history[0].name = 'A caller cannot mutate history';
  assert.equal(workspace.snapshot().runHistory[0].name, 'Fixture Quest');
});

test('active-account snapshots isolate history while the backup retains all owners including late old-account events', () => {
  const workspace = new Workspace(memoryRepository());
  assert.deepEqual(workspace.snapshot().runHistory, []);
  workspace.bindAccount(account); workspace.recordRun(run());
  workspace.bindAccount(otherAccount); workspace.recordRun(run({ id: 'run-other', steamId: otherAccount }));
  workspace.recordRun(run({ state: 'unknown', reason: 'steam_disconnected' }));
  assert.deepEqual(workspace.snapshot().runHistory.map(item => item.id), ['run-other']);
  assert.equal(workspace.exportBackup().runHistory.length, 2);
  workspace.bindAccount(account);
  assert.equal(workspace.snapshot().runHistory[0].state, 'unknown');
});

test('a multi-game checkpoint commits once and rejects a partially invalid batch atomically', () => {
  let saves = 0;
  const repository = memoryRepository(); const save = repository.save;
  repository.save = value => { saves++; save(value); };
  const workspace = new Workspace(repository); workspace.bindAccount(account);
  saves = 0;
  const records = workspace.recordRuns([run(), run({ id: 'run-second', appId: 5555 })]);
  assert.equal(saves, 1); assert.equal(records.length, 2);
  assert.throws(() => workspace.recordRuns([run({ state: 'stopped' }), run({ id: 'run-second', appId: 6666 })]), /run_identity_changed/);
  assert.equal(saves, 1); assert.ok(workspace.snapshot().runHistory.every(item => item.state === 'running'));
  assert.throws(() => workspace.recordRuns([run(), run()]), /duplicate_run/);
  assert.deepEqual(workspace.recordRuns([]), []); assert.equal(saves, 1);
});

test('real repository reload marks an interrupted run unknown and never restores a running session', () => {
  const dir = mkdtempSync(join(tmpdir(), 'sch-run-history-'));
  try {
    const repository = new JsonRepository(join(dir, 'workspace.json'));
    const workspace = new Workspace(repository); workspace.bindAccount(account);
    workspace.recordRun(run());
    workspace.recordRun(run({ id: 'run-stopped', state: 'stopped', reason: 'session_deadline',
      stoppedAt: '2026-10-17T11:00:00.000Z', stopStatus: 'requested_without_acknowledgement' }));
    const restored = new Workspace(repository);
    const [interrupted, stopped] = restored.snapshot().runHistory;
    assert.equal(interrupted.state, 'outcome_unknown');
    assert.equal(interrupted.reason, 'process_ended_before_stop_confirmation');
    assert.equal(interrupted.stopStatus, 'cleanup_unknown'); assert.equal(interrupted.stoppedAt, null);
    assert.equal(interrupted.localElapsedSeconds, 900); assert.equal(interrupted.steamMinutes, 120);
    assert.equal(interrupted.readbackStatus, 'no_increase_observed');
    assert.equal(interrupted.imported, false); assert.equal(stopped.state, 'stopped');
    assert.throws(() => restored.recordRun(run()), /run_history_cannot_resume/);
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test('imports preserve whitelisted observations, mark their origin and keep drafts untouched', () => {
  const workspace = new Workspace(memoryRepository()); workspace.bindAccount(account);
  const task = workspace.createTask({ appId: 4444, title: 'Fixture Quest', groupId: '45134494' });
  workspace.saveDraft({ taskId: task.id, language: 'english', kind: 'long', text: 'Existing user draft', expectedRevision: 0 });
  const backup = workspace.exportBackup();
  backup.runHistory = [run({ password: 'secret-password', session: { token: 'secret-token' },
    headers: { Authorization: 'secret-header' }, _elapsedMs: 999999, steamMinutesDelta: 900,
    verified: true, recordSource: 'verified_server', arbitrary: { apiKey: 'secret-key' } })];
  workspace.importBackup(backup);
  const saved = workspace.snapshot().runHistory[0];
  assert.equal(saved.state, 'outcome_unknown'); assert.equal(saved.imported, true);
  assert.equal(saved.recordSource, 'imported_backup'); assert.equal(saved.steamMinutes, 120);
  assert.equal(saved.localElapsedMinutes, 15);
  assert.equal('steamMinutesDelta' in saved, false); assert.equal('verified' in saved, false);
  assert.doesNotMatch(JSON.stringify(workspace.exportBackup()), /secret-|_elapsedMs|arbitrary/);
  assert.equal(workspace.task(task.id).drafts.english.long.text, 'Existing user draft');
  const restored = new Workspace({ load: () => workspace.exportBackup(), save() {} });
  assert.equal(restored.snapshot().runHistory[0].imported, true);
});

test('a run ID cannot change owner, AppID or generation and terminal records cannot resume', () => {
  const workspace = new Workspace(memoryRepository()); workspace.bindAccount(account); workspace.recordRun(run());
  for (const changes of [{ steamId: otherAccount }, { appId: 5555 }, { generation: 3 }, { ownerSteamId: otherAccount }]) {
    assert.throws(() => workspace.recordRun(run(changes)), /run_identity_changed/);
  }
  assert.equal(workspace.snapshot().runHistory.length, 1);
  workspace.recordRun(run({ state: 'paused', reason: 'system_sleep_or_clock_gap' }));
  assert.throws(() => workspace.recordRun(run()), /run_history_cannot_resume/);
  assert.equal(workspace.snapshot().runHistory[0].state, 'paused');
});

test('invalid observations and duplicate or excessive imported history fail before changing the workspace', () => {
  const workspace = new Workspace(memoryRepository()); workspace.bindAccount(account);
  for (const changes of [{ id: '' }, { steamId: 'wrong-account' }, { appId: 0 }, { state: 'verified' },
    { localElapsedSeconds: NaN }, { localElapsedMinutes: -1 }, { localElapsedSeconds: 600 },
    { deadline: 'invalid date' }, { readbackStatus: { state: 'verified' } }, { steamMinutes: -1 }]) {
    assert.throws(() => workspace.recordRun(run(changes)));
  }
  const backup = workspace.exportBackup();
  assert.throws(() => workspace.importBackup({ ...backup, runHistory: [run(), run()] }), /duplicate_run/);
  assert.throws(() => workspace.importBackup({ ...backup, runHistory: Array(10001).fill(run()) }), /invalid_run_history/);
  assert.deepEqual(workspace.snapshot().runHistory, []);
  delete backup.runHistory;
  workspace.importBackup(backup); assert.deepEqual(workspace.snapshot().runHistory, []);
});

test('the 20 MB UTF-8 backup bound and disk errors preserve the previous history', () => {
  let failing = false;
  const repository = memoryRepository(); const save = repository.save;
  repository.save = value => { if (failing) throw new Error('disk_full'); save(value); };
  const workspace = new Workspace(repository); workspace.bindAccount(account); workspace.recordRun(run());
  failing = true;
  assert.throws(() => workspace.recordRun(run({ state: 'stopped', reason: 'user_stopped' })), /disk_full/);
  assert.equal(workspace.snapshot().runHistory[0].state, 'running');
  failing = false;
  const backup = workspace.exportBackup();
  assert.throws(() => workspace.importBackup({ ...backup, padding: '中'.repeat(6_666_667) }), /workspace_too_large/);
  assert.equal(workspace.snapshot().runHistory[0].state, 'running');
});

test('fixture checkpoints retain their synthetic origin and cannot turn into Steam observations', () => {
  const workspace = new Workspace(memoryRepository()); workspace.bindAccount(account);
  workspace.recordRun(run({ fixture: true }));
  assert.equal(workspace.snapshot().runHistory[0].recordSource, 'fixture');
  assert.equal(workspace.snapshot().runHistory[0].elapsedSource, 'fixture');
  assert.throws(() => workspace.recordRun(run({ state: 'stopped' })), /run_identity_changed/);
  workspace.recordRun(run({ fixture: true, state: 'stopped' }));
  const restored = new Workspace({ load: () => workspace.exportBackup(), save() {} });
  assert.equal(restored.snapshot().runHistory[0].fixture, true);
  assert.equal(restored.snapshot().runHistory[0].recordSource, 'fixture');
});
