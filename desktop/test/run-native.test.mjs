// Real controller + protocol service, injected network/native boundaries only.
import test from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { AppController } from '../controller.mjs';
import { SteamService } from '../services/steam.mjs';
import { Workspace } from '../workspace.mjs';

const ACCOUNT = '76561198000000000', OTHER = '76561198000000001';
function deferred() { let resolve; const promise = new Promise(done => { resolve = done; }); return { promise, resolve }; }
async function harness(t) {
  const clients = [];
  class User extends EventEmitter {
    constructor() { super(); this.requests = []; this.playingState = { blocked: false }; clients.push(this); }
    logOn() { this.steamID = { getSteamID64: () => ACCOUNT }; queueMicrotask(() => this.emit('loggedOn')); }
    gamesPlayed(ids, force) { this.requests.push({ ids: [...ids], force }); }
    async getUserOwnedApps() {
      if (this.readGate) await this.readGate;
      return { apps: [1001, 1002, 1003].map(appid => ({ appid, name: `Fixture ${appid}`, playtime_forever: 10 })) };
    }
    logOff() { this.steamID = null; this.emit('disconnected'); }
  }
  const workspace = new Workspace({ load: () => null, save() {} }); workspace.bindAccount(ACCOUNT);
  const tasks = [1001, 1002, 1003].map(appId => workspace.createTask({ appId, title: `Fixture ${appId}`, groupId: '45134494' }));
  let controller;
  const steam = new SteamService({ SteamUserClass: User, clock: { now: () => 1700000000000, monotonicNow: () => 0 },
    onEvent: event => controller.steamEvent(event) });
  const rows = new Map(tasks.map(task => [task.appId, { appId: task.appId, accountId: ACCOUNT, source: 'fixture',
    achievements: [{ id: 'ACH', unlocked: false }], stats: [
      { id: 'count', type: 'int', value: 0, writable: true }, { id: 'distance', type: 'float', value: 1.5, writable: true }] }]));
  const native = { calls: [], async read({ appId }) { this.calls.push(['read', appId]); return structuredClone(rows.get(appId)); },
    async apply({ appId, changes, expectedAccountId }) {
      this.calls.push(['apply', appId]); assert.equal(expectedAccountId, ACCOUNT);
      if (this.applyGate) await this.applyGate;
      const row = rows.get(appId);
      for (const change of changes.achievements) row.achievements.find(item => item.id === change.id).unlocked = change.unlocked;
      for (const change of changes.stats) row.stats.find(item => item.id === change.id).value = change.value;
      return { status: 'verified', appId, accountId: ACCOUNT, verification: { scope: 'fixture', steamVerified: false },
        readback: structuredClone(row), items: [] };
    }, async close() {} };
  const publishing = { async logout() {}, async close() {} };
  controller = new AppController({ workspace, steam, native, publishing, secrets: { status: () => ({}) }, research: {}, onEvent() {} });
  t.after(() => controller.close());
  await steam.login({ accountName: 'synthetic-account', password: 'synthetic-password' });
  const invoke = (command, task = tasks[0], payload = {}) => controller.invoke(command, { taskId: task.id, ...payload });
  const changes = { achievements: [{ id: 'ACH', unlocked: true }], stats: [{ id: 'count', value: 7 }, { id: 'distance', value: 2.25 }] };
  return { workspace, steam, native, controller, clients, rows, tasks, invoke, changes };
}

for (const nativeAppId of [1001, 1003]) test(`native read/preview/apply for AppID ${nativeAppId} preserves the complete protocol run set`, async t => {
  const h = await harness(t);
  await h.invoke('runs.start', h.tasks[0]); await h.invoke('runs.start', h.tasks[1]);
  const before = h.steam.runs(), requests = structuredClone(h.clients[0].requests);
  const target = h.tasks.find(task => task.appId === nativeAppId);
  const read = await h.invoke('stats.read', target); assert.equal(read.appId, nativeAppId);
  const preview = await h.invoke('stats.preview', target, { changes: h.changes });
  const result = await h.invoke('stats.apply', target, { previewId: preview.id });
  assert.equal(result.readback.achievements[0].unlocked, true);
  assert.deepEqual(result.readback.stats.map(row => row.value), [7, 2.25]);
  assert.deepEqual(h.steam.runs(), before); assert.deepEqual(h.clients[0].requests, requests);
  assert.equal(h.workspace.task(target.id).operations[0].state, 'verified');
  await h.invoke('runs.stop', h.tasks[0], { appId: 1001 });
  assert.deepEqual(h.clients[0].requests.at(-1), { ids: [1002], force: false });
  assert.equal(h.steam.runs().find(run => run.appId === 1002).state, 'running');
});

test('a native write permits independent protocol starts/stops while excluding another native worker', async t => {
  const h = await harness(t);
  await h.invoke('runs.start', h.tasks[0]); await h.invoke('runs.start', h.tasks[1]);
  const preview = await h.invoke('stats.preview', h.tasks[0], { changes: h.changes });
  const gate = deferred(); h.native.applyGate = gate.promise;
  const applying = h.invoke('stats.apply', h.tasks[0], { previewId: preview.id });
  assert.equal(h.controller.nativeOperations, 1);
  await assert.rejects(h.invoke('stats.read', h.tasks[1]), /native_operation_in_progress/);
  await h.invoke('runs.start', h.tasks[2]); await h.invoke('runs.stop', h.tasks[0], { appId: 1001 });
  assert.deepEqual(h.clients[0].requests.at(-1), { ids: [1002, 1003], force: false });
  const requests = structuredClone(h.clients[0].requests);
  gate.resolve(); await applying;
  assert.deepEqual(h.clients[0].requests, requests);
  assert.deepEqual(h.steam.runs().filter(run => run.state === 'running').map(run => run.appId), [1002, 1003]);
});

test('native operations can enter during run preflight while protocol/native account conflicts still reject', async t => {
  const h = await harness(t), gate = deferred();
  h.clients[0].readGate = gate.promise;
  const starting = h.invoke('runs.start');
  assert.equal((await h.invoke('stats.read')).accountId, ACCOUNT);
  gate.resolve(); await starting;
  const requests = structuredClone(h.clients[0].requests);
  h.rows.get(1001).accountId = OTHER;
  await assert.rejects(h.invoke('stats.preview', h.tasks[0], { changes: h.changes }), /native_steam_account_mismatch/);
  assert.equal(h.controller.previews.size, 0); assert.deepEqual(h.clients[0].requests, requests);
  const count = h.native.calls.length; h.workspace.bindAccount(OTHER);
  const foreignTask = h.workspace.createTask({ appId: 1001, title: 'Other account task', groupId: '45134494' });
  await assert.rejects(h.invoke('stats.read', foreignTask), /steam_account_mismatch/);
  assert.equal(h.native.calls.length, count); assert.deepEqual(h.clients[0].requests, requests);
});
