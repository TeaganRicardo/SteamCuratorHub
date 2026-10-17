import test from 'node:test';
import assert from 'node:assert/strict';
import { DemoSteamService } from '../services/demo.mjs';
import { Workspace } from '../workspace.mjs';
import { AppController } from '../controller.mjs';

const owner = '76561198000000000';
function setup() {
  let saved = null;
  const workspace = new Workspace({ load: () => saved, save(value) { saved = structuredClone(value); } });
  workspace.bindAccount(owner);
  const a = workspace.createTask({ appId: 1001, title: 'Synthetic A', groupId: '45134494', targetMinutes: 120 });
  const b = workspace.createTask({ appId: 1002, title: 'Synthetic B', groupId: '45525570' });
  const events = [], pending = []; let controller;
  const steam = new DemoSteamService({ onEvent(event) {
    events.push(structuredClone(event)); if (controller) pending.push(controller.steamEvent(event));
  } });
  controller = new AppController({ workspace, steam, native: { async close() {} }, research: {},
    secrets: { status: () => ({}) }, publishing: { async logout() {} }, fixture: true, onEvent() {} });
  return { workspace, a, b, steam, controller, events, async settle() { await Promise.all(pending); } };
}

test('demo logout publishes terminal runs before clearing and keeps fixture history through the actual controller', async () => {
  const h = setup();
  const first = await h.controller.invoke('runs.start', { taskId: h.a.id, stopAfterMinutes: 60 });
  const second = await h.controller.invoke('runs.start', { taskId: h.b.id, stopAfterMinutes: 15 });
  await h.controller.invoke('runs.stop', { appId: 1001 });
  assert.equal(h.steam.runs().find(run => run.id === second.id).state, 'running');
  h.events.length = 0;
  const status = await h.controller.invoke('steam.logout'); await h.settle();
  assert.equal(status.state, 'logged_out'); assert.equal(h.steam.status().state, 'logged_out');
  assert.deepEqual(h.steam.runs(), []);
  const terminal = h.events.findIndex(event => event.type === 'steam.runs' && event.data.some(run => run.id === second.id && run.state === 'stopped'));
  const cleared = h.events.findIndex(event => event.type === 'steam.runs' && !event.data.length);
  const disconnected = h.events.findIndex(event => event.type === 'steam.status' && event.data.state === 'logged_out');
  assert.ok(terminal >= 0 && terminal < cleared && cleared < disconnected);
  const history = h.workspace.snapshot().runHistory;
  assert.equal(history.length, 2);
  assert.equal(history.find(run => run.id === first.id).reason, 'user_stopped');
  assert.equal(history.find(run => run.id === second.id).reason, 'logout');
  for (const run of history) {
    assert.equal(run.fixture, true); assert.equal(run.recordSource, 'fixture'); assert.equal(run.elapsedSource, 'fixture');
    assert.equal(run.readbackStatus, 'fixture_only'); assert.equal(run.stopStatus, 'fixture_only');
    assert.equal(run.localElapsedSeconds, 0); assert.equal(run.steamMinutes, run.baselineSteamMinutes);
  }
  await assert.rejects(h.steam.ownedGames(), /steam_not_connected/);
  await assert.rejects(h.steam.startRun({ appId: 1001 }), /steam_not_connected/);
});

test('demo reconnect advances identity generation without restoring old runs', async () => {
  const h = setup(); const old = await h.steam.startRun({ appId: 1001 });
  await h.steam.logout(); const disconnected = h.steam.status();
  const connected = await h.steam.login({ accountName: 'ignored-synthetic-account', password: 'ignored-synthetic-password' });
  assert.equal(connected.state, 'connected'); assert.ok(connected.generation > disconnected.generation);
  assert.equal(connected.steamId, owner); assert.deepEqual(h.steam.runs(), []);
  const next = await h.steam.startRun({ appId: 1001 }); await h.settle();
  assert.notEqual(next.id, old.id); assert.ok(next.generation > old.generation);
  assert.doesNotMatch(JSON.stringify({ events: h.events, backup: h.workspace.exportBackup() }), /ignored-synthetic/);
  const outside = h.steam.status(); outside.state = 'closed'; assert.equal(h.steam.status().state, 'connected');
  await h.steam.close(); await h.settle();
});

test('demo close is terminal and idempotent; sleep pauses without pretending to resume', async () => {
  const h = setup(); await h.steam.startRun({ appId: 1002 });
  const suspended = await h.steam.suspend(); await h.settle();
  assert.equal(suspended.state, 'error'); assert.deepEqual(h.steam.runs(), []);
  assert.equal(h.workspace.snapshot().runHistory[0].state, 'paused');
  assert.equal(h.workspace.snapshot().runHistory[0].reason, 'system_sleep');
  await h.steam.login(); const run = await h.steam.startRun({ appId: 1001 });
  const closed = await h.steam.close(); await h.settle();
  assert.equal(closed.state, 'closed'); assert.equal(h.steam.status().state, 'closed');
  const terminal = h.workspace.snapshot().runHistory.find(item => item.id === run.id);
  assert.equal(terminal.state, 'stopped'); assert.equal(terminal.reason, 'service_closed');
  const count = h.events.length; assert.deepEqual(await h.steam.close(), closed); assert.equal(h.events.length, count);
  await assert.rejects(h.steam.login(), /service_closed/);
  await assert.rejects(h.steam.startRun({ appId: 1001 }), /service_closed/);
});

test('logout during synthetic preflight cannot create a late run and duplicate starts stay unique', async () => {
  const h = setup();
  const pending = h.steam.startRun({ appId: 1001 }); await h.steam.logout();
  await assert.rejects(pending, /account_changed/); assert.deepEqual(h.steam.runs(), []);
  await h.steam.login();
  const attempts = await Promise.allSettled([h.steam.startRun({ appId: 1001 }), h.steam.startRun({ appId: 1001 })]);
  assert.equal(attempts.filter(result => result.status === 'fulfilled').length, 1);
  assert.equal(attempts.filter(result => result.status === 'rejected').length, 1);
  assert.equal(h.steam.runs().length, 1);
  await h.steam.close(); await h.settle();
});

test('demo reached target is a stopped synthetic observation and public store lookup survives logout', async () => {
  const h = setup();
  const reached = await h.steam.startRun({ appId: 1001, totalTargetMinutes: 60, stopAfterMinutes: 15 }); await h.settle();
  assert.equal(reached.state, 'stopped'); assert.equal(reached.reason, 'total_target_already_reached');
  assert.equal(reached.startedAt, null); assert.equal(reached.requestStatus, 'not_requested');
  assert.equal(reached.deadline, null); assert.equal(reached.steamMinutes, 72);
  await h.steam.logout();
  assert.ok((await h.steam.searchStore('星海')).length); assert.equal((await h.steam.appDetails(1001)).appId, 1001);
  await h.steam.close(); await h.settle();
});
