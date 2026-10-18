import test, { before } from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { mkdtemp, mkdir, writeFile, readFile, appendFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { NativeStatsService, reconcileNativeReadback } from '../services/native-stats.mjs';
import { buildNativeHelper } from '../native/build.mjs';

const native = process.platform === 'darwin' && process.arch === 'arm64';
let binary;
before(async () => { if (native) binary = await buildNativeHelper(); });
const identity = '76561198000000000';
const fixture = behavior => ({
  accountId: identity, behavior,
  achievements: [
    { id: 'LOCKED', name: 'First', description: 'Fixture only', hidden: false, unlocked: false, writable: true },
    { id: 'UNLOCKED', name: 'Second', description: '', hidden: true, unlocked: true, writable: true, globalPercent: 0 },
    { id: 'PROTECTED', name: 'Protected', description: '', hidden: false, unlocked: false, writable: false, reason: 'Protected by schema' },
  ],
  stats: [
    { id: 'count', type: 'int', value: 4, writable: true, min: 0, max: 100 },
    { id: 'distance', type: 'float', value: 1.5, writable: true, min: 0, max: 100 },
    { id: 'only_up', type: 'int', value: 5, writable: true, incrementOnly: true },
    { id: 'bounded', type: 'float', value: 10, writable: true, maxChange: 1 },
    { id: 'average', type: 'average', value: 3, writable: false, reason: 'Average-rate' },
  ],
});
function service(behavior) { return new NativeStatsService({ binaryPath: binary, fixture: fixture(behavior) }); }
function stage(store = { status: 'ok' }) {
  return { appId: 42, accountId: identity, source: 'steam-client-refreshed', store,
    items: [{ kind: 'achievement', id: 'LOCKED', requested: true, staging: 'accepted', status: 'pendingVerification' }] };
}
function readback(overrides = {}) {
  return { appId: 42, accountId: identity, achievements: [{ id: 'LOCKED', unlocked: true }], stats: [],
    capabilities: { refreshCallbackMatched: true }, ...overrides };
}
async function request(data) {
  return new Promise((resolve, reject) => {
    const child = spawn(binary); let output = '';
    child.stdout.on('data', c => { output += c; }); child.on('error', reject);
    child.on('close', code => { try { resolve({ code, ...JSON.parse(output) }); } catch (e) { reject(e); } });
    child.stdin.end(JSON.stringify(data));
  });
}

test('verification requires store callback and independently refreshed matching identity', () => {
  const result = reconcileNativeReadback(stage(), readback());
  assert.equal(result.status, 'verified');
  assert.equal(result.verification.independentServerRead, false);
  assert.equal(result.verification.scope, 'store-callback-and-separate-process-client-refresh');
});
test('matching local values cannot turn callback timeout into success', () => {
  const result = reconcileNativeReadback(stage({ status: 'unknown' }), readback());
  assert.equal(result.items[0].status, 'unknown');
});
test('batch callback rejection cannot claim every matching item failed or persisted', () => {
  const result = reconcileNativeReadback(stage({ status: 'failed', result: 8 }), readback());
  assert.equal(result.store.status, 'failed'); assert.equal(result.items[0].status, 'unknown'); assert.equal(result.items[0].observed, true);
});
test('batch rejection with a differing independent observation identifies the failed item', () => {
  const result = reconcileNativeReadback(stage({ status: 'failed', result: 8 }), readback({ achievements: [{ id: 'LOCKED', unlocked: false }] }));
  assert.equal(result.items[0].status, 'failed'); assert.equal(result.items[0].observed, false);
  assert.equal(reconcileNativeReadback(stage({ status: 'failed', result: 8 }), null).items[0].status, 'unknown');
});
test('missing, stale or wrong-account readback cannot verify a write', () => {
  for (const read of [null, readback({ accountId: '76561198000000001' }), readback({ appId: 43 }), readback({ capabilities: { refreshCallbackMatched: false } })]) {
    assert.equal(reconcileNativeReadback(stage(), read).items[0].status, 'unknown');
  }
});
test('refreshed mismatch reports failure and keeps requested/observed values', () => {
  const result = reconcileNativeReadback(stage(), readback({ achievements: [{ id: 'LOCKED', unlocked: false }] }));
  assert.equal(result.items[0].status, 'failed'); assert.equal(result.items[0].observed, false);
});
test('float comparison uses actual float32 storage precision', () => {
  const staged = stage(); staged.items = [{ kind: 'stat', id: 'distance', type: 'float', requested: 1.1, staging: 'accepted' }];
  assert.equal(reconcileNativeReadback(staged, readback({ stats: [{ id: 'distance', value: Math.fround(1.1) }] })).status, 'verified');
});
test('synthetic verification is always identified as fixture evidence', () => {
  const staged = stage(); staged.source = 'fixture';
  const result = reconcileNativeReadback(staged, readback());
  assert.equal(result.status, 'verified'); assert.equal(result.verification.steamVerified, false); assert.equal(result.verification.scope, 'fixture');
});
test('invalid AppID and identity are rejected before a helper can be launched', () => {
  const s = new NativeStatsService({ binaryPath: '/does/not/exist' });
  assert.throws(() => s.read({ appId: 0 }), /AppID/);
  assert.throws(() => s.apply({ appId: 42, expectedAccountId: 76561198000000000, changes: {} }), /SteamID/);
  s.close();
});

test('native fixture reads hidden/display fields and preserves unknown percentage', { skip: !native }, async () => {
  const s = service(); const result = await s.read({ appId: 42 });
  assert.equal(result.accountId, identity); assert.equal(result.achievements[1].hidden, true);
  assert.equal(result.achievements[1].globalPercent, 0);
  assert.equal('globalPercent' in result.achievements[0], false); assert.equal(result.source, 'fixture'); await s.close();
});
test('native batch performs unlock, relock, INT and FLOAT then a separate read process', { skip: !native }, async () => {
  const s = service(); await s.read({ appId: 42 });
  const result = await s.apply({ appId: 42, expectedAccountId: identity, changes: {
    achievements: [{ id: 'LOCKED', unlocked: true }, { id: 'UNLOCKED', unlocked: false }],
    stats: [{ id: 'count', type: 'int', value: 10 }, { id: 'distance', type: 'float', value: 1.1 }],
  } });
  assert.equal(result.items.length, 4); assert.ok(result.items.every(i => i.status === 'verified'));
  assert.equal(result.store.status, 'ok'); assert.equal(result.verification.independentProcess, true);
  assert.equal(result.verification.steamVerified, false);
  assert.equal(result.readback.stats.find(s => s.id === 'distance').value, Math.fround(1.1)); await s.close();
});
test('stale before and protected items fail without discarding independently valid items', { skip: !native }, async () => {
  const s = service(); await s.read({ appId: 42 });
  const result = await s.apply({ appId: 42, expectedAccountId: identity, changes: {
    achievements: [{ id: 'LOCKED', unlocked: true, before: true }, { id: 'PROTECTED', unlocked: true }],
    stats: [{ id: 'count', type: 'int', value: 9 }],
  } });
  assert.deepEqual(result.items.map(i => i.status), ['failed', 'failed', 'verified']);
  assert.equal(result.readback.achievements[0].unlocked, false); await s.close();
});
test('native validates integer range, fractional values, schema range, type and monotonicity', { skip: !native }, async () => {
  const s = service(); await s.read({ appId: 42 });
  const result = await s.apply({ appId: 42, expectedAccountId: identity, changes: { stats: [
    { id: 'count', type: 'int', value: 1.5 }, { id: 'distance', type: 'int', value: 2 },
    { id: 'only_up', type: 'int', value: 4 }, { id: 'bounded', type: 'float', value: 12 },
  ] } });
  assert.ok(result.items.every(i => i.status === 'failed')); assert.equal(result.mutationAttempted, false); assert.equal(result.store.status, 'notAttempted'); await s.close();
});
test('native callback matcher rejects wrong AppID/user, short payload and timeout', { skip: !native }, async () => {
  for (const mode of ['wrongApp', 'wrongUser', 'shortPayload', 'timeout', 'apiFalse']) {
    const s = service({ store: mode }); await s.read({ appId: 42 });
    const result = await s.apply({ appId: 42, expectedAccountId: identity, changes: { achievements: [{ id: 'LOCKED', unlocked: true }] } });
    assert.equal(result.store.status, 'unknown', mode); assert.equal(result.items[0].status, 'unknown', mode);
    assert.equal(result.readback.achievements[0].unlocked, true); await s.close();
  }
});
test('native Set rejection is per item and no store is called when all are rejected', { skip: !native }, async () => {
  const s = service({ rejectIds: ['LOCKED'] }); await s.read({ appId: 42 });
  const result = await s.apply({ appId: 42, expectedAccountId: identity, changes: { achievements: [{ id: 'LOCKED', unlocked: true }] } });
  assert.equal(result.items[0].status, 'failed'); assert.equal(result.store.status, 'notAttempted'); await s.close();
});
test('failed readback or account switch preserves unknown mutation outcome', { skip: !native }, async () => {
  for (const behavior of [{ readbackFailure: true }, { accountAfterStore: '76561198000000001' }]) {
    const s = service(behavior); await s.read({ appId: 42 });
    const result = await s.apply({ appId: 42, expectedAccountId: identity, changes: { achievements: [{ id: 'LOCKED', unlocked: true }] } });
    assert.equal(result.items[0].status, 'unknown'); await s.close();
  }
});
test('apply requires a matching preview and closed service cannot restart', { skip: !native }, async () => {
  const s = service();
  await assert.rejects(s.apply({ appId: 42, expectedAccountId: identity, changes: {} }), /Read this game/);
  await s.read({ appId: 42 });
  await assert.rejects(s.apply({ appId: 42, expectedAccountId: '76561198000000001', changes: {} }), /expected account/);
  await s.close(); await assert.rejects(s.read({ appId: 42 }), /closed/);
});
test('native account guard rejects before any staging and duplicate batch is rejected', { skip: !native }, async () => {
  const base = { operation: 'apply', appId: 42, fixture: fixture(), changes: { achievements: [{ id: 'LOCKED', unlocked: true, before: false }] } };
  const mismatch = await request({ ...base, expectedAccountId: '76561198000000001' });
  assert.equal(mismatch.ok, false); assert.match(mismatch.error, /account mismatch/); assert.equal(mismatch.mutationOutcome, 'notAttempted');
  const duplicate = await request({ ...base, expectedAccountId: identity, changes: { achievements: [...base.changes.achievements, ...base.changes.achievements] } });
  assert.equal(duplicate.ok, false); assert.match(duplicate.error, /Duplicate/); assert.equal(duplicate.mutationOutcome, 'notAttempted');
});
test('closing while synthetic native read is pending kills owned child and rejects late result', { skip: !native }, async () => {
  const s = service({ delayMs: 1000 }); const pending = s.read({ appId: 42 });
  await new Promise(resolve => setTimeout(resolve, 100));
  const rejected = assert.rejects(pending, /cancelled/); await s.close(); await rejected;
});

function kvString(key, value) { return Buffer.concat([Buffer.from([1]), Buffer.from(`${key}\0${value}\0`)]); }
function kvInt(key, value) { const v = Buffer.alloc(4); v.writeInt32LE(value); return Buffer.concat([Buffer.from([2]), Buffer.from(`${key}\0`), v]); }
function kvObject(key, entries) { return Buffer.concat([Buffer.from([0]), Buffer.from(`${key}\0`), ...entries, Buffer.from([8])]); }
test('bounded native binary schema parser enumerates INT/FLOAT/average and permissions', { skip: !native }, async () => {
  const schema = Buffer.concat([kvObject('42', [kvObject('stats', [
    kvObject('0', [kvString('name', 'count'), kvInt('type', 1), kvInt('min', 0)]),
    kvObject('1', [kvString('name', 'distance'), kvString('type', 'float'), kvInt('permission', 2)]),
    kvObject('2', [kvString('name', 'average'), kvInt('type', 3)]),
  ])]), Buffer.from([8])]);
  const result = await request({ operation: 'read', appId: 42, fixture: { accountId: identity, achievements: [], schemaBase64: schema.toString('base64'), statValues: { count: 4, distance: 1.25, average: 2 } } });
  assert.equal(result.ok, true); assert.deepEqual(result.data.stats.map(s => s.type), ['int', 'float', 'average']);
  assert.equal(result.data.stats[0].value, 4); assert.equal(result.data.stats[1].writable, false); assert.equal(result.data.stats[2].writable, false);
});
test('malformed schema permissions and constraints fail closed for achievements and stats', { skip: !native }, async () => {
  for (const invalid of ['not-an-integer', '0.5', '-1', '4', '0 trailing']) {
    const schema = Buffer.concat([kvObject('42', [kvObject('stats', [
      kvObject('0', [kvString('name', 'count'), kvInt('type', 1), kvString('permission', invalid)]),
      kvObject('1', [kvInt('type', 4), kvObject('bits', [kvObject('0', [kvString('name', 'LOCKED'), kvString('permission', invalid)])])]),
    ])]), Buffer.from([8])]);
    const response = await request({ operation: 'apply', appId: 42, expectedAccountId: identity,
      fixture: { ...fixture(), schemaBase64: schema.toString('base64'), statValues: { count: 4 } },
      changes: { achievements: [{ id: 'LOCKED', before: false, unlocked: true }], stats: [{ id: 'count', before: 4, type: 'int', value: 5 }] } });
    assert.equal(response.ok, true, invalid); assert.equal(response.data.mutationAttempted, false, invalid);
    assert.ok(response.data.items.every(item => item.status === 'failed'), invalid);
  }
  for (const [key, value] of [['min', 'not-numeric'], ['max', 'bad'], ['maxchange', '-1'], ['incrementonly', '2']]) {
    const schema = Buffer.concat([kvObject('42', [kvObject('stats', [kvObject('0', [kvString('name', 'count'), kvInt('type', 1), kvString(key, value)])])]), Buffer.from([8])]);
    const response = await request({ operation: 'read', appId: 42, fixture: { accountId: identity, achievements: [], schemaBase64: schema.toString('base64'), statValues: { count: 4 } } });
    assert.equal(response.ok, true); assert.equal(response.data.stats[0].writable, false); assert.match(response.data.stats[0].reason, /Malformed/);
  }
});
test('schema types require exact known values and legacy type_int fallback preserves int32 bounds', { skip: !native }, async () => {
  for (const invalid of ['1 trailing', '1.5', '2.5', '-1', '6', 'unknown']) {
    const schema = Buffer.concat([kvObject('42', [kvObject('stats', [kvObject('0', [kvString('name', 'count'), kvString('type', invalid)])])]), Buffer.from([8])]);
    const response = await request({ operation: 'apply', appId: 42, expectedAccountId: identity,
      fixture: { accountId: identity, achievements: [], schemaBase64: schema.toString('base64'), statValues: { count: 4 } },
      changes: { stats: [{ id: 'count', type: 'int', value: 5, before: 4 }] } });
    assert.equal(response.ok, true, invalid); assert.equal(response.data.mutationAttempted, false, invalid);
    assert.equal(response.data.items[0].status, 'failed', invalid);
  }
  const schema = Buffer.concat([kvObject('42', [kvObject('stats', [kvObject('0', [kvString('name', 'count'), kvInt('type', 0), kvInt('type_int', 1)])])]), Buffer.from([8])]);
  const response = await request({ operation: 'read', appId: 42, fixture: { accountId: identity, achievements: [], schemaBase64: schema.toString('base64'), statValues: { count: 4 } } });
  assert.equal(response.data.stats[0].type, 'int'); assert.equal(response.data.stats[0].max, 2147483647); assert.equal(response.data.stats[0].min, -2147483648);
});
test('malformed/deep schema never becomes writable data', { skip: !native }, async () => {
  for (const schema of [Buffer.from([0, 65, 0]), Buffer.concat([Buffer.from(Array(66).fill([0, 65, 0]).flat()), Buffer.alloc(67, 8)])]) {
    const result = await request({ operation: 'read', appId: 42, fixture: { accountId: identity, achievements: [], schemaBase64: schema.toString('base64'), statValues: {} } });
    assert.equal(result.ok, false); assert.equal(result.mutationOutcome, 'notAttempted');
  }
});

test('actual native dlopen/vtable/callback path works against a synthetic dylib, never Steam', { skip: !native }, async () => {
  const root = await mkdtemp(join(tmpdir(), 'sch-native-abi-'));
  const libraryPath = join(root, 'Steam.AppBundle/Steam/Contents/MacOS/steamclient.dylib');
  try {
    await mkdir(join(root, 'Steam.AppBundle/Steam/Contents/MacOS'), { recursive: true });
    await promisify(execFile)('clang++', ['-std=c++20', '-fobjc-arc', '-Wall', '-Wextra', '-Werror', '-dynamiclib', '-framework', 'Foundation',
      fileURLToPath(new URL('../native/synthetic-steamclient.mm', import.meta.url)), '-o', libraryPath]);
    await mkdir(join(root, 'appcache/stats'), { recursive: true });
    const schema = Buffer.concat([kvObject('42', [kvObject('stats', [
      kvObject('0', [kvString('name', 'count'), kvInt('type', 1), kvInt('min', 0)]),
      kvObject('1', [kvString('name', 'distance'), kvInt('type', 2)]),
      kvObject('2', [kvInt('type', 4), kvObject('bits', [kvObject('0', [kvString('name', 'ACH'), kvInt('permission', 0)])])]),
    ])]), Buffer.from([8])]);
    await writeFile(join(root, 'appcache/stats/UserGameStatsSchema_42.bin'), schema);
    for (const identityMode of [{ registerIdentityOnly: false }, { registerIdentityOnly: true }, { registerIdentityOnly: true, callResultRefresh: true }]) {
      await writeFile(join(root, 'synthetic-state.json'), JSON.stringify({ accountId: identity, count: 4, distance: 1.5, unlocked: false, ...identityMode }));
      const s = new NativeStatsService({ binaryPath: binary, steamLibraryPath: libraryPath });
      const preview = await s.read({ appId: 42 });
      assert.equal(preview.accountId, identity); assert.equal(preview.achievements[0].globalPercent, 7.5);
      assert.equal(preview.capabilities.architecture, 'arm64'); assert.equal(preview.capabilities.privateAbiLocallyVerified, false);
      const result = await s.apply({ appId: 42, expectedAccountId: identity, changes: {
        achievements: [{ id: 'ACH', unlocked: true }], stats: [{ id: 'count', type: 'int', value: 9 }, { id: 'distance', type: 'float', value: 3.25 }],
      } });
      assert.equal(result.store.callbackId, 1102); assert.ok(result.items.every(item => item.status === 'verified'));
      assert.equal(result.verification.independentServerRead, false);
      const persisted = JSON.parse(await readFile(join(root, 'synthetic-state.json'), 'utf8'));
      assert.equal(persisted.count, 9); assert.equal(persisted.unlocked, true); await s.close();
    }
    for (const flags of [{ wrongRefreshIdentity: true }, { truncatedRefresh: true }, { callResultRefresh: true, wrongRefreshIdentity: true }]) {
      await writeFile(join(root, 'synthetic-state.json'), JSON.stringify({ accountId: identity, count: 4, distance: 1.5, unlocked: false, ...flags }));
      const rejected = await request({ operation: 'read', appId: 42, steamLibraryPath: libraryPath, timeoutMs: 50 });
      assert.equal(rejected.ok, false); assert.match(rejected.error, /refresh callback/); assert.equal(rejected.mutationOutcome, 'notAttempted');
    }
    await writeFile(join(root, 'synthetic-state.json'), JSON.stringify({ accountId: identity, count: 4, distance: 1.5, unlocked: false }));
    const guarded = new NativeStatsService({ binaryPath: binary, steamLibraryPath: libraryPath });
    await guarded.read({ appId: 42 });
    await appendFile(libraryPath, Buffer.from([0]));
    await assert.rejects(guarded.apply({ appId: 42, expectedAccountId: identity, changes: { achievements: [{ id: 'ACH', unlocked: true }] } }), /changed since preview/);
    const unchanged = JSON.parse(await readFile(join(root, 'synthetic-state.json'), 'utf8'));
    assert.equal(unchanged.unlocked, false); await guarded.close();
  } finally { await rm(root, { recursive: true, force: true }); }
});
