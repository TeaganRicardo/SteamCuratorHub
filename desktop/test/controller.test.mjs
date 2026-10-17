import test from 'node:test';
import assert from 'node:assert/strict';
import { Workspace } from '../workspace.mjs';
import { AppController } from '../controller.mjs';

const ACCOUNT = '76561198000000000';
const OTHER = '76561198000000001';
const settle = async () => { for (let i = 0; i < 8; i++) await Promise.resolve(); };
function deferred() { let resolve, reject; const promise = new Promise((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; }
function setup() {
  const workspace = new Workspace({ load: () => null, save() {} }); workspace.bindAccount(ACCOUNT);
  const task = workspace.createTask({ appId: 1001, title: 'Synthetic', groupId: '45134494' });
  let steamStatus = { state: 'connected', steamId: ACCOUNT, generation: 0 };
  const steam = { status: () => steamStatus, runs: () => [], async close() {}, async logout() { steamStatus = { state: 'logged_out', generation: 1 }; } };
  const snapshot = { appId: 1001, accountId: ACCOUNT,
    achievements: [{ id: 'ACH', unlocked: false, writable: true }], stats: [{ id: 'count', type: 'int', value: 0, writable: true }] };
  const native = { applies: [], async read() { return structuredClone(snapshot); }, async apply(payload) {
    this.applies.push(payload); return { status: 'verified', appId: payload.appId, accountId: payload.expectedAccountId, items: [] };
  }, async close() {} };
  const research = { async research({ appId, requestId }) { return { appId, requestId, sources: [], reviews: [], draft: 'Generated' }; },
    async translate({ text }) { return { text }; } };
  const keys = new Map([['aiKey', 'synthetic-old-key']]);
  const secrets = { read: key => keys.get(key) ?? '', write(key, value) { keys.set(key, value); }, status: () => ({ aiKeyConfigured: keys.has('aiKey') }) };
  const publishing = { logouts: 0, async logout() { this.logouts++; }, async close() {} };
  const events = [];
  const controller = new AppController({ workspace, steam, native, research, secrets, publishing, onEvent: e => events.push(e) });
  const change = { achievements: [{ id: 'ACH', unlocked: true }], stats: [] };
  return { workspace, task, steam, native, research, secrets, publishing, controller, events, keys, snapshot, change,
    setSteamStatus(value) { steamStatus = value; } };
}

test('controller native preview binds account and before-values; journal exists before apply and remains per-item evidence', async () => {
  const s = setup();
  const preview = await s.controller.invoke('stats.preview', { taskId: s.task.id, changes: s.change });
  assert.equal(preview.owner, ACCOUNT); assert.equal(preview.changes.achievements[0].before, false);
  s.native.apply = async payload => {
    assert.equal(s.workspace.task(s.task.id).operations[0].state, 'applying');
    assert.equal(payload.expectedAccountId, ACCOUNT);
    return { status: 'verified', items: [{ id: 'ACH', status: 'verified' }] };
  };
  await s.controller.invoke('stats.apply', { taskId: s.task.id, previewId: preview.id });
  assert.equal(s.workspace.task(s.task.id).operations[0].state, 'verified');
  await assert.rejects(s.controller.invoke('stats.apply', { taskId: s.task.id, previewId: preview.id }), /preview_expired/);
});

test('Steam checkpoints persist once per interval or state change and late events retain the original owner', async () => {
  const s = setup(); let saves=0; s.workspace.repository.save=()=>{saves++;};
  const run={id:'checkpoint',appId:1001,steamId:ACCOUNT,state:'running',generation:0,localElapsedSeconds:0,steamMinutes:20,readbackStatus:'observed'};
  await s.controller.steamEvent({type:'steam.runs',data:[run]});
  for(let seconds=1;seconds<30;seconds++)await s.controller.steamEvent({type:'steam.runs',data:[{...run,localElapsedSeconds:seconds}]});
  assert.equal(saves,1);
  await s.controller.steamEvent({type:'steam.runs',data:[{...run,localElapsedSeconds:30}]});assert.equal(saves,2);
  s.workspace.bindAccount(OTHER);
  await s.controller.steamEvent({type:'steam.runs',data:[{...run,state:'unknown',reason:'steam_disconnected',localElapsedSeconds:31}]});
  assert.equal(s.workspace.snapshot().runHistory.length,0);
  assert.equal(s.workspace.exportBackup().runHistory[0].ownerSteamId,ACCOUNT);
  assert.equal(s.workspace.exportBackup().runHistory[0].state,'unknown');
});

test('failed run checkpoint is surfaced and retried without replacing the durable previous observation',async()=>{
  const s=setup();const run={id:'checkpoint',appId:1001,steamId:ACCOUNT,state:'running',localElapsedSeconds:0,steamMinutes:20};
  await s.controller.steamEvent({type:'steam.runs',data:[run]});
  s.workspace.repository.save=()=>{throw new Error('disk_full');};
  await assert.rejects(s.controller.steamEvent({type:'steam.runs',data:[{...run,state:'unknown'}]}),/disk_full/);
  assert.equal(s.workspace.snapshot().runHistory[0].state,'running');
  s.workspace.repository.save=()=>{};
  await s.controller.steamEvent({type:'steam.runs',data:[{...run,state:'unknown'}]});
  assert.equal(s.workspace.snapshot().runHistory[0].state,'unknown');
});

test('controller refuses a wrong native account before producing any writable preview', async () => {
  const s = setup(); s.snapshot.accountId = OTHER;
  await assert.rejects(s.controller.invoke('stats.preview', { taskId: s.task.id, changes: s.change }), /account_mismatch/);
  assert.equal(s.controller.previews.size, 0); assert.equal(s.native.applies.length, 0);
});

test('unknown native outcome blocks another previously prepared preview until explicit reconciliation', async () => {
  const s = setup();
  const first = await s.controller.invoke('stats.preview', { taskId: s.task.id, changes: s.change });
  const second = await s.controller.invoke('stats.preview', { taskId: s.task.id, changes: s.change });
  s.native.apply = async payload => { s.native.applies.push(payload); return { status: 'unknown', items: [] }; };
  await s.controller.invoke('stats.apply', { taskId: s.task.id, previewId: first.id });
  assert.equal(s.workspace.task(s.task.id).operations[0].state, 'outcome_unknown');
  await assert.rejects(s.controller.invoke('stats.apply', { taskId: s.task.id, previewId: second.id }),
    /(?:unknown_result|preview_expired|operation_in_progress)/);
  assert.equal(s.native.applies.length, 1);
});

test('a native preview read completing after a new unknown operation cannot issue a usable preview', async () => {
  const s = setup(); const read = deferred(); s.native.read = () => read.promise;
  const pending = s.controller.invoke('stats.preview', { taskId: s.task.id, changes: s.change });
  const rejected = assert.rejects(pending, /unknown_result/);
  s.workspace.recordOperation(s.task.id, ACCOUNT, { id: 'other-write', state: 'outcome_unknown' });
  read.resolve(structuredClone(s.snapshot)); await rejected;
  assert.equal(s.controller.previews.size, 0);
});

test('account binding cannot commit after another Steam account generation has superseded it', async () => {
  const s = setup(); const clearing = deferred(); s.publishing.logout = () => clearing.promise;
  s.setSteamStatus({ state: 'logged_out', generation: 0 });
  const pending = s.controller.invoke('account.bind', { steamId: OTHER });
  const rejected = assert.rejects(pending, /(?:context_changed|bind_superseded|logout_before_binding)/);
  s.setSteamStatus({ state: 'connected', steamId: ACCOUNT, generation: 1 });
  await s.controller.steamEvent({ type: 'steam.status', data: s.steam.status() });
  clearing.resolve(); await rejected;
  assert.equal(s.workspace.state.activeSteamId, ACCOUNT);
});

test('research cancellation and replacement reject late responses without replacing the newer task research', async () => {
  const s = setup(); const calls = [];
  s.research.research = ({ appId, requestId }, _settings, signal) => {
    const pending = deferred(); calls.push({ ...pending, appId, requestId, signal }); return pending.promise;
  };
  const old = s.controller.invoke('research.run', { taskId: s.task.id, language: 'english' });
  const rejected = assert.rejects(old);
  const current = s.controller.invoke('research.run', { taskId: s.task.id, language: 'english' });
  assert.equal(calls[0].signal.aborted, true);
  calls[1].resolve({ appId: 1001, requestId: calls[1].requestId, draft: 'New result' }); await current;
  calls[0].resolve({ appId: 1001, requestId: calls[0].requestId, draft: 'Old late result' }); await rejected;
  assert.equal(s.workspace.task(s.task.id).research.draft, 'New result');
  const cancelled = s.controller.invoke('research.run', { taskId: s.task.id, language: 'english' });
  const cancelRejected = assert.rejects(cancelled);
  await s.controller.invoke('research.cancel', { taskId: s.task.id });
  assert.equal(calls[2].signal.aborted, true);
  calls[2].resolve({ appId: 1001, requestId: calls[2].requestId, draft: 'Cancelled result' }); await cancelRejected;
  assert.equal(s.workspace.task(s.task.id).research.draft, 'New result');
});

test('Steam generation change cancels research and draft transforms, clears previews, and invalidates late results', async () => {
  const s = setup(); const translating = deferred(); let signal;
  s.research.translate = (_value, _settings, inputSignal) => { signal = inputSignal; return translating.promise; };
  const pending = s.controller.invoke('ai.translate', { taskId: s.task.id, text: 'original', kind: 'long', language: 'english', baseRevision: 0 });
  const rejected = assert.rejects(pending);
  s.controller.previews.set('old', { owner: ACCOUNT });
  s.setSteamStatus({ state: 'logged_out', generation: 1 });
  await s.controller.steamEvent({ type: 'steam.status', data: s.steam.status() });
  assert.equal(signal.aborted, true); assert.equal(s.controller.previews.size, 0); assert.equal(s.publishing.logouts, 1);
  translating.resolve({ text: 'late translation' }); await rejected;
  assert.equal(s.workspace.task(s.task.id).drafts.english.long.text, '');
});

test('unknown operation requires a fresh matching-account readback before user acknowledgement', async () => {
  const s = setup();
  s.workspace.recordOperation(s.task.id, ACCOUNT, { id: 'uncertain', state: 'outcome_unknown' });
  s.snapshot.accountId = OTHER;
  await assert.rejects(s.controller.invoke('stats.acknowledge', { taskId: s.task.id, operationId: 'uncertain' }), /account_mismatch/);
  assert.equal(s.workspace.task(s.task.id).operations[0].state, 'outcome_unknown');
  s.snapshot.accountId = ACCOUNT;
  await s.controller.invoke('stats.acknowledge', { taskId: s.task.id, operationId: 'uncertain' });
  assert.equal(s.workspace.task(s.task.id).operations[0].state, 'operator_acknowledged');
});

test('failed credential update does not pair a newly saved provider endpoint with the old API key', async () => {
  const s = setup(); const previous = s.controller.aiSettings();
  s.secrets.write = () => { throw new Error('credential_encryption_unavailable'); };
  await assert.rejects(s.controller.invoke('settings.save', {
    settings: { aiBaseUrl: 'https://new-provider.example/v1', aiModel: 'new-model' },
    secrets: { aiKey: 'synthetic-new-key' }, persistSecrets: true
  }), /credential_encryption_unavailable/);
  assert.deepEqual(s.controller.aiSettings(), previous);
});

test('renderer cannot assert automatic publication verification or persist secrets in the workspace', async () => {
  const s = setup();
  s.workspace.saveDraft({ taskId: s.task.id, language: 'english', kind: 'long', text: 'Review', expectedRevision: 0 });
  const attempt = await s.controller.invoke('publication.prepare', { taskId: s.task.id, language: 'english', kind: 'long' });
  await assert.rejects(s.controller.invoke('publication.record', { taskId: s.task.id, attemptId: attempt.id,
    state: 'verified', resultUrl: `https://steamcommunity.com/profiles/${ACCOUNT}/recommended/1001/` }), /automatic_evidence_required/);
  await s.controller.invoke('settings.save', { settings: { apiKey: 'DO_NOT_SAVE' }, secrets: { aiKey: 'synthetic-new-key' } });
  assert.equal(JSON.stringify(s.workspace.exportBackup()).includes('DO_NOT_SAVE'), false);
  assert.equal(JSON.stringify(s.workspace.exportBackup()).includes('synthetic-new-key'), false);
});

test('maintenance refuses pending run preflight and all-account applying journals, then blocks new commands until released', async () => {
  const s = setup(); const start = deferred();
  s.steam.startRun = () => start.promise;
  const pending = s.controller.invoke('runs.start', { taskId: s.task.id });
  assert.throws(() => s.controller.acquireMaintenance(), /stop_operations_before_import/);
  start.resolve({ appId: 1001, state: 'stopped' }); await pending;
  s.workspace.recordOperation(s.task.id, ACCOUNT, { id: 'old-account-write', state: 'applying' });
  s.workspace.bindAccount(OTHER);
  assert.throws(() => s.controller.acquireMaintenance(), /stop_operations_before_import/);
  s.workspace.recordOperation(s.task.id, ACCOUNT, { id: 'old-account-write', state: 'failed' });
  const release = s.controller.acquireMaintenance();
  assert.equal((await s.controller.invoke('init')).workspace.activeSteamId, OTHER);
  await assert.rejects(s.controller.invoke('account.bind', { steamId: ACCOUNT }), /workspace_maintenance_in_progress/);
  release();
  await s.controller.invoke('settings.save', { settings: { maxSources: 2 } });
  assert.equal(s.workspace.state.settings.maxSources, 2);
});

test('pending protocol start reserves the native seam before its asynchronous Steam preflight completes', async () => {
  const s = setup(); const start = deferred(); let nativeReads = 0;
  s.steam.startRun = () => start.promise;
  s.native.read = async () => { nativeReads++; return s.snapshot; };
  const pending = s.controller.invoke('runs.start', { taskId: s.task.id });
  await assert.rejects(s.controller.invoke('stats.read', { taskId: s.task.id }), /(?:stop_runs_before_native_stats|run_start_in_progress)/);
  assert.equal(nativeReads, 0);
  start.resolve({ appId: 1001, state: 'running' }); await pending;
});

test('a pending native read excludes another native worker and a new protocol start', async () => {
  const s = setup(); const read = deferred(); let reads = 0; let starts = 0;
  s.native.read = () => { reads++; return read.promise; };
  s.steam.startRun = async () => { starts++; return { state: 'running' }; };
  const pending = s.controller.invoke('stats.read', { taskId: s.task.id });
  await assert.rejects(s.controller.invoke('stats.read', { taskId: s.task.id }), /native_operation_in_progress/);
  await assert.rejects(s.controller.invoke('runs.start', { taskId: s.task.id }), /native_operation_in_progress/);
  assert.equal(reads, 1); assert.equal(starts, 0);
  read.resolve(structuredClone(s.snapshot)); await pending;
  assert.equal(s.controller.nativeOperations, 0);
});

test('native apply blocked before entering its worker does not create an unknown journal or consume preview', async () => {
  const s = setup();
  const preview = await s.controller.invoke('stats.preview', { taskId: s.task.id, changes: s.change });
  const read = deferred(); s.native.read = () => read.promise;
  const pending = s.controller.invoke('stats.read', { taskId: s.task.id });
  await assert.rejects(s.controller.invoke('stats.apply', { taskId: s.task.id, previewId: preview.id }), /native_operation_in_progress/);
  assert.equal(s.workspace.task(s.task.id).operations.length, 0);
  assert.equal(s.controller.previews.has(preview.id), true); assert.equal(s.native.applies.length, 0);
  read.resolve(structuredClone(s.snapshot)); await pending;
  await s.controller.invoke('stats.apply', { taskId: s.task.id, previewId: preview.id });
  assert.equal(s.workspace.task(s.task.id).operations[0].state, 'verified');
});
