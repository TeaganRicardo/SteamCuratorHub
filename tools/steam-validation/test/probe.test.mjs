import test from 'node:test';
import assert from 'node:assert/strict';
import { createReport, runProbe, validatePlan, waitChecked, comparePlaytime, ProbeError } from '../probe.mjs';
import { createFixture, fixturePlan } from '../fixture.mjs';

function setup() {
  const plan = validatePlan(structuredClone(fixturePlan));
  const fixture = createFixture();
  const controller = new AbortController();
  const report = createReport(plan, 'fixture');
  return { plan, ...fixture, controller, signal: controller.signal, report };
}

test('parallel requests retain the second AppID after stopping the first; fixture cannot pass acceptance', async () => {
  const s = setup();
  await runProbe(s);
  assert.deepEqual(s.commands, [[1001, 1002], [1002], [], []]);
  assert.equal(s.report.state, 'collected');
  assert.equal(s.report.cleanup, 'logged_off');
  assert.equal(s.report.acceptance, 'pending_user_verification');
  assert.deepEqual(s.report.snapshots.map(s => s.values.map(v => v.steamMinutes)), [[10, 20], [12, 22], [12, 24]]);
  assert.ok(s.report.observations.every(o => o.status === 'fixture_only'));
});

test('invalid target ownership/readback blocks all nonempty playing requests', async () => {
  const s = setup();
  s.transport.readOwnedApps = async () => [{ appid: 1001, playtime_forever: 0 }];
  await runProbe(s);
  assert.equal(s.report.failure, 'missing_or_invalid_playtime');
  assert.deepEqual(s.commands, [[]]);
});

test('disconnect aborts, preserves baseline, clears requests, and never reconnects/retries', async () => {
  const s = setup();
  s.wait = async () => {
    s.controller.abort(new ProbeError('steam_disconnected'));
    s.signal.throwIfAborted();
  };
  await runProbe(s);
  assert.equal(s.report.failure, 'steam_disconnected');
  assert.equal(s.report.snapshots.length, 1);
  assert.equal(s.report.cleanup, 'logged_off');
  assert.deepEqual(s.commands, [[1001, 1002], []]);
});

test('late readback after cancellation does not get added as evidence', async () => {
  const s = setup();
  s.transport.readOwnedApps = async () => {
    s.controller.abort(new ProbeError('operator_cancelled'));
    return [{ appid: 1001, playtime_forever: 0 }, { appid: 1002, playtime_forever: 0 }];
  };
  await runProbe(s);
  assert.equal(s.report.snapshots.length, 0);
  assert.equal(s.report.failure, 'operator_cancelled');
  assert.deepEqual(s.commands, [[]]);
});

test('evidence disk failure still clears the session and logs off', async () => {
  const s = setup();
  s.save = () => { throw new Error('disk full'); };
  await runProbe(s);
  assert.equal(s.report.failure, 'evidence_save_failed');
  assert.equal(s.report.persistence, 'failed');
  assert.equal(s.report.cleanup, 'logged_off');
  assert.deepEqual(s.commands, [[]]);
});

test('failed clear request does not prevent logoff, and failed logoff stays unknown', async () => {
  const s = setup();
  s.transport.setPlaying = () => { throw new Error('socket closed'); };
  s.transport.close = async () => { throw new Error('logout unconfirmed'); };
  await runProbe(s);
  assert.equal(s.report.cleanupClear, 'request_failed');
  assert.equal(s.report.cleanup, 'unknown');
  assert.equal(s.report.state, 'interrupted');
});

test('blocked session is rejected before running', async () => {
  const s = setup();
  s.transport.assertReady = () => { throw new ProbeError('playing_elsewhere'); };
  await runProbe(s);
  assert.equal(s.report.failure, 'playing_elsewhere');
  assert.deepEqual(s.commands, [[]]);
});

test('readback compares Steam counters and keeps no change, missing and regression distinct', () => {
  const before = [1, 2, 3, 4].map(appId => ({ appId, steamMinutes: 10 }));
  const after = [{ appId: 1, steamMinutes: 12 }, { appId: 2, steamMinutes: 10 }, { appId: 3, steamMinutes: 9 }];
  assert.deepEqual(comparePlaytime(before, after, 'live').map(x => x.status),
    ['increase_observed', 'no_increase_observed', 'inconsistent', 'unavailable']);
});

test('sleep or backwards clock interrupts rather than inventing elapsed Steam time', async () => {
  for (const delta of [10000, -1]) {
    let now = 0;
    await assert.rejects(waitChecked(1000, new AbortController().signal,
      { now: () => now, sleepFn: async () => { now += delta; } }), { code: 'clock_gap' });
  }
});

test('reject ambiguous, duplicate, uncovered and unsupported plans', () => {
  const changes = [
    p => { p.expectedSteamId = 'account-name'; },
    p => { p.targets[0].appId = '1001'; },
    p => { p.targets[0].appId = p.targets[1].appId; },
    p => { p.targets[0].windowsOnly = false; },
    p => { p.targets[0].stopAfterSeconds = 0; },
    p => { p.targets[0].stopAfterSeconds = p.targets[1].stopAfterSeconds; },
    p => { p.readbackDelaySeconds = 10000; },
    p => { p.targets.push(p.targets[0]); }
  ];
  for (const change of changes) {
    const plan = structuredClone(fixturePlan); change(plan);
    assert.throws(() => validatePlan(plan), ProbeError);
  }
});
