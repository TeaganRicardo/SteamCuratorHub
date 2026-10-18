import test from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { SteamService } from '../services/steam.mjs';

const STEAM_ID = '76561198000000000';
const NEXT_ID = '76561198000000001';
const settle = async () => { for (let i = 0; i < 10; i++) await Promise.resolve(); };

function fakeClock() {
  let wall = 1700000000000;
  let mono = 0;
  let nextId = 0;
  const timers = new Map();
  const clock = {
    now: () => wall, monotonicNow: () => mono,
    setTimeout(fn, ms) { const id = ++nextId; timers.set(id, { fn, due: mono + ms }); return id; },
    clearTimeout(id) { timers.delete(id); },
    setInterval(fn, ms) { const id = ++nextId; timers.set(id, { fn, due: mono + ms, interval: ms }); return id; },
    clearInterval(id) { timers.delete(id); },
    async advance(ms) {
      const end = mono + ms;
      while (true) {
        const next = [...timers.entries()].filter(([, timer]) => timer.due <= end).sort((a, b) => a[1].due - b[1].due)[0];
        if (!next) break;
        const delta = next[1].due - mono;
        mono += delta; wall += delta;
        if (next[1].interval) next[1].due += next[1].interval;
        else timers.delete(next[0]);
        next[1].fn();
        await settle();
      }
      wall += end - mono; mono = end;
      await settle();
    },
    jump(wallMs, monoMs = wallMs) { wall += wallMs; mono += monoMs; },
    heartbeat() { for (const timer of [...timers.values()]) if (timer.interval) timer.fn(); },
    get timerCount() { return timers.size; }
  };
  return clock;
}

function tokenFor(steamId = STEAM_ID, claims = {}) {
  return `eyJhbGciOiJub25lIn0.${Buffer.from(JSON.stringify({ iss: 'steam', aud: ['client'], sub: steamId, exp: 4102444800, ...claims })).toString('base64url')}.synthetic`;
}
function sessionStore(saved = null) {
  return { saved, writes: [], clears: 0,
    readSteamSession() { return structuredClone(this.saved); },
    writeSteamSession(value) { this.saved = structuredClone(value); this.writes.push(this.saved); },
    clearSteamSession() { this.saved = null; this.clears++; } };
}
function setup({ requireGuard = false, autoLogin = true, fetchImpl, issueToken = false, store } = {}) {
  const clock = fakeClock();
  const clients = [];
  const events = [];
  class FakeUser extends EventEmitter {
    constructor(options) {
      super(); this.options = options; this.playingState = { blocked: false }; this.requests = [];
      this.apps = [{ appid: 1001, name: 'Windows Only', playtime_forever: 10 },
        { appid: 1002, name: 'Mac Game', playtime_forever: 20 }];
      clients.push(this);
    }
    authenticate(steamId = STEAM_ID) {
      if (issueToken) this.emit('refreshToken', tokenFor(steamId));
      this.steamID = { getSteamID64: () => steamId }; this.emit('loggedOn');
    }
    logOn(credentials) {
      this.credentials = credentials;
      if (requireGuard) queueMicrotask(() => this.emit('steamGuard', 'mail.invalid', code => {
        this.submittedCode = code; this.authenticate();
      }, false));
      else if (autoLogin) queueMicrotask(() => this.authenticate());
    }
    gamesPlayed(apps, force) { this.requests.push({ apps: [...apps], force }); }
    async getUserOwnedApps(id, options) {
      this.readId = id.getSteamID64(); this.readOptions = options;
      return this.readImpl ? this.readImpl() : { apps: structuredClone(this.apps) };
    }
    logOff() { this.logoffs = (this.logoffs || 0) + 1; this.steamID = null; this.emit('disconnected'); }
  }
  const service = new SteamService({ SteamUserClass: FakeUser, clock, fetchImpl, sessionStore: store,
    onEvent: event => events.push(event) });
  const login = () => service.login({ accountName: 'synthetic-account', password: ' private password ' });
  return { clock, service, clients, events, login };
}

test('login and Guard retain account progress while passwords/codes stay ephemeral and retry remains application-owned', async () => {
  const s = setup({ requireGuard: true });
  const pending = s.login();
  await settle();
  assert.equal(s.service.status().state, 'guard_required');
  assert.equal(s.service.status().accountName, 'synthetic-account');
  assert.deepEqual(s.service.status().guardPrompt, { kind: 'email', lastCodeWrong: false, domain: 'mail.invalid' });
  s.service.submitGuard('12345');
  assert.equal((await pending).steamId, STEAM_ID);
  assert.deepEqual(s.clients[0].options, { dataDirectory: null, autoRelogin: false, saveAppTickets: false,
    renewRefreshTokens: true, enablePicsCache: false });
  assert.equal(s.clients[0].credentials.password, ' private password ');
  assert.equal(JSON.stringify(s.events).includes('private password'), false);
  assert.equal(JSON.stringify(s.events).includes('12345'), false);
  assert.throws(() => s.service.submitGuard('12345'), { code: 'guard_not_requested' });
  await s.service.close();
  assert.equal(s.clock.timerCount, 0);
});

test('Guard cancellation and timeout reject login; late authentication is logged off', async () => {
  const s = setup({ requireGuard: true });
  const pending = s.login();
  const rejected = assert.rejects(pending, { code: 'login_timeout' });
  await settle();
  await s.clock.advance(120000);
  await rejected;
  assert.equal(s.service.status().error, 'login_timeout');
  assert.throws(() => s.service.submitGuard('12345'), { code: 'guard_not_requested' });
  s.clients[0].authenticate();
  assert.equal(s.service.status().state, 'error');
  assert.ok(s.clients[0].logoffs >= 2);
  assert.ok(s.clients[0].requests.every(request => request.apps.length === 0));
  await s.service.close();
});

test('tokens are persisted only after matching login, renewed safely, restored on restart and cleared on explicit logout', async () => {
  const store = sessionStore(), s = setup({ store, autoLogin: false });
  const pending = s.login(); await settle();
  const token = tokenFor(); s.clients[0].emit('refreshToken', token);
  assert.equal(store.saved, null); assert.equal(store.writes.length, 0);
  s.clients[0].authenticate(); await pending;
  assert.deepEqual(store.saved, { accountName: 'synthetic-account', steamId: STEAM_ID, refreshToken: token });
  const renewed = tokenFor(STEAM_ID, { exp: 4102444900 });
  s.clients[0].emit('refreshToken', renewed);
  assert.equal(store.saved.refreshToken, renewed);
  assert.equal(s.service.status().persistence, 'encrypted');
  assert.equal(JSON.stringify(s.events).includes(renewed), false);
  assert.equal(JSON.stringify(s.events).includes(token), false);
  await s.service.close();
  assert.equal(store.saved.refreshToken, renewed);
  const next = setup({ store });
  assert.equal((await next.service.restore()).steamId, STEAM_ID);
  assert.equal(next.clients[0].credentials.refreshToken, renewed);
  assert.equal(next.clients[0].credentials.password, undefined);
  assert.equal(next.clients[0].credentials.accountName, undefined);
  assert.deepEqual(next.service.runs(), []);
  await next.service.logout(); assert.equal(store.saved, null);
  await next.service.close(); assert.equal(next.clock.timerCount, 0);
});

test('expired, malformed, wrong-audience and identity-mismatched saved tokens are cleared before upstream login', async () => {
  for (const token of ['not-a-token', tokenFor(STEAM_ID, { exp: 1 }), tokenFor(STEAM_ID, { aud: ['web'] }),
    tokenFor(STEAM_ID, { iss: 'other' }), tokenFor(NEXT_ID)]) {
    const store = sessionStore({ accountName: 'saved-account', steamId: STEAM_ID, refreshToken: token });
    const s = setup({ store });
    await assert.rejects(s.service.restore(), /(?:invalid_steam_session|steam_session_expired)/);
    assert.equal(store.saved, null); assert.equal(s.clients.length, 0);
    assert.equal(s.service.status().state, 'error');
    assert.equal(JSON.stringify(s.events).includes(token), false);
    await s.service.close();
  }
});

test('revoked authentication never loops or retains a rejected reusable session', async () => {
  for (const eresult of [5, 15, 27]) {
    const store = sessionStore({ accountName: 'saved-account', steamId: STEAM_ID, refreshToken: tokenFor() });
    const s = setup({ store, autoLogin: false });
    const pending = s.service.restore(), rejected = assert.rejects(pending, { code: 'steam_auth_required' });
    await settle(); s.clients[0].emit('error', Object.assign(new Error('private upstream token detail'), { eresult }));
    await rejected; assert.equal(store.saved, null); assert.equal(s.service.status().restoreAvailable, false);
    await s.clock.advance(15000); assert.equal(s.clients.length, 1);
    assert.equal(JSON.stringify(s.events).includes('private upstream'), false);
    await s.service.close();
  }
});

test('transient disconnect retries only authentication with a bounded budget and never resumes prior runs', async () => {
  const store = sessionStore(), s = setup({ store, issueToken: true }); await s.login();
  await s.service.startRun({ appId: 1001 }); await s.service.startRun({ appId: 1002 });
  const ids = s.service.runs().map(run => run.id);
  for (const [index, delay] of [1000, 3000, 10000].entries()) {
    const client = s.clients.at(-1);
    client.emit('error', Object.assign(new Error('private network detail'), { eresult: 3 }));
    assert.equal(s.service.status().state, 'reconnecting');
    assert.equal(s.service.status().attempt, index + 1);
    assert.ok(s.service.runs().every(run => run.state === 'unknown'));
    await s.clock.advance(delay);
    assert.equal(s.service.status().state, 'connected');
    assert.equal(s.clients.length, index + 2);
    assert.equal(s.clients.at(-1).credentials.password, undefined);
    assert.ok(s.clients.at(-1).credentials.refreshToken);
    assert.deepEqual(s.clients.at(-1).requests, []);
    assert.deepEqual(s.service.runs().map(run => run.id), ids);
  }
  s.clients.at(-1).emit('disconnected', 0);
  assert.equal(s.service.status().error, 'steam_reconnect_exhausted');
  await s.clock.advance(20000); assert.equal(s.clients.length, 4);
  assert.equal(JSON.stringify(s.events).includes('private network'), false);
  await s.service.close(); assert.ok(store.saved); assert.equal(s.clock.timerCount, 0);
});

test('a token issued before the first loggedOn can recover a transient failure without rejecting its login caller', async () => {
  const s = setup({ autoLogin: false });
  const pending = s.login(); await settle();
  s.clients[0].emit('refreshToken', tokenFor()); s.clients[0].emit('error', Object.assign(new Error('network'), { eresult: 3 }));
  await s.clock.advance(1000); s.clients[1].authenticate();
  assert.equal((await pending).state, 'connected');
  assert.equal(s.clients[1].credentials.password, undefined);
  await s.service.close();
});

test('logout, close and account replacement cancel retry timers and ignore late auth/token events', async () => {
  for (const action of ['logout', 'close', 'replace']) {
    const store = sessionStore(), s = setup({ store, issueToken: true }); await s.login();
    const old = s.clients[0]; old.emit('disconnected', 0);
    if (action === 'replace') await s.service.login({ accountName: 'replacement', password: 'replacement-password' });
    else await s.service[action]();
    const saved = structuredClone(store.saved), count = s.clients.length;
    old.emit('refreshToken', tokenFor(NEXT_ID)); old.authenticate(NEXT_ID);
    await s.clock.advance(20000);
    assert.deepEqual(store.saved, saved); assert.equal(s.clients.length, count);
    if (action === 'logout') assert.equal(store.saved, null);
    if (action === 'close') assert.ok(store.saved);
    if (action === 'replace') assert.equal(s.service.status().accountName, 'replacement');
    await s.service.close(); assert.equal(s.clock.timerCount, 0);
  }
});

test('a healthy minute resets the recovery budget while authentication timeouts remain bounded', async () => {
  const s = setup({ issueToken: true }); await s.login();
  s.clients[0].emit('disconnected', 0); await s.clock.advance(1000);
  await s.clock.advance(60000);
  s.clients[1].emit('disconnected', 0); assert.equal(s.service.status().attempt, 1);
  await s.clock.advance(1000);
  await s.service.close();
  const stalled = setup({ autoLogin: false });
  const pending = stalled.login(), rejected = assert.rejects(pending, { code: 'steam_reconnect_exhausted' });
  await settle(); stalled.clients[0].emit('refreshToken', tokenFor());
  await stalled.clock.advance(500000); await rejected;
  assert.equal(stalled.clients.length, 4); await stalled.service.close();
});

test('encryption/save failures leave a visible session-only connection without leaking provider details', async () => {
  const store = sessionStore(), write = store.writeSteamSession;
  store.writeSteamSession = () => { throw new Error('credential_private_machine_details'); };
  const s = setup({ store, issueToken: true }); await s.login();
  assert.equal(s.service.status().state, 'connected'); assert.equal(s.service.status().persistence, 'session_only');
  assert.equal(s.service.status().persistenceError, 'steam_session_save_failed'); assert.equal(store.saved, null);
  assert.equal(JSON.stringify(s.events).includes('credential_private_machine_details'), false);
  s.clients[0].emit('disconnected', 0); await s.clock.advance(1000);
  assert.equal(s.service.status().state, 'connected');
  store.writeSteamSession = write; s.clients[1].emit('refreshToken', tokenFor());
  assert.equal(s.service.status().persistence, 'encrypted'); assert.equal(s.service.status().persistenceError, undefined);
  await s.service.close();
});

test('failure to clear saved credentials still disconnects owned runs and reports the retained disk error', async () => {
  const store = sessionStore(), s = setup({ store, issueToken: true }); await s.login();
  await s.service.startRun({ appId: 1001 });
  store.clearSteamSession = () => { throw new Error('credential_store_write_failed'); };
  await assert.rejects(s.service.logout(), { code: 'credential_store_write_failed' });
  assert.equal(s.service.status().state, 'logged_out'); assert.equal(s.clients[0].steamID, null);
  assert.equal(s.service.status().persistenceError, 'credential_store_write_failed');
  assert.ok(s.service.runs().every(run => run.state === 'stopped'));
  await s.service.close(); assert.equal(s.clock.timerCount, 0);
});

test('restored Guard and mismatched native identity stay bound to the original reusable account', async () => {
  const saved = { accountName: 'saved-account', steamId: STEAM_ID, refreshToken: tokenFor() };
  const s = setup({ store: sessionStore(saved), requireGuard: true });
  const pending = s.service.restore(); await settle();
  assert.equal(s.service.status().state, 'guard_required'); assert.equal(s.service.status().accountName, 'saved-account');
  s.service.submitGuard('12345'); await pending; await s.service.close();
  const store = sessionStore(saved), mismatch = setup({ store, autoLogin: false });
  const restoring = mismatch.service.restore(), rejected = assert.rejects(restoring, { code: 'invalid_steam_identity' });
  await settle(); mismatch.clients[0].authenticate(NEXT_ID); await rejected;
  assert.equal(store.saved, null); await mismatch.service.close();
});

test('owned games preserve missing Steam minutes and sanitize names without raw error leakage', async () => {
  const s = setup(); await s.login();
  s.clients[0].apps.push({ appid: 1003, name: 'Unplayed' });
  const games = await s.service.ownedGames();
  assert.equal(games.find(game => game.appId === 1003).steamMinutes, null);
  assert.equal(s.clients[0].readOptions.includePlayedFreeGames, true);
  assert.equal(s.clients[0].readOptions.skipUnvettedApps, false);
  await assert.rejects(s.service.startRun({ appId: 1003 }), { code: 'steam_playtime_unavailable' });
  await assert.rejects(s.service.startRun({ appId: 999 }), { code: 'game_not_owned' });
  s.clients[0].readImpl = async () => { throw new Error('private server details password=secret'); };
  await assert.rejects(s.service.ownedGames(), { code: 'steam_library_failed' });
  assert.equal(JSON.stringify(s.events).includes('password=secret'), false);
  await s.service.close();
});

test('concurrent starts send complete AppID sets and stopping one keeps the other', async () => {
  const s = setup(); await s.login();
  await Promise.all([s.service.startRun({ appId: 1001, totalTargetMinutes: 50, stopAfterMinutes: 2 }),
    s.service.startRun({ appId: 1002, totalTargetMinutes: 100, stopAfterMinutes: 5 })]);
  assert.deepEqual(s.clients[0].requests.map(request => request.apps), [[1001], [1001, 1002]]);
  await s.clock.advance(30000);
  assert.equal(s.service.runs()[0].localElapsedSeconds, 30);
  assert.equal(s.service.runs()[0].steamMinutes, 10);
  await s.service.stopRun(1001);
  assert.deepEqual(s.clients[0].requests.at(-1), { apps: [1002], force: false });
  assert.equal(s.service.runs()[0].stopStatus, 'requested_without_acknowledgement');
  assert.equal(s.service.runs()[1].state, 'running');
  assert.equal(s.service.runs()[0].totalTargetMinutes, 50);
  assert.equal(s.service.runs()[0].stopAfterMinutes, 2);
  assert.ok(s.events.some(event => event.type === 'steam.runs'));
  await s.service.close();
});

test('a pending preflight for another game never blocks an independent stop', async () => {
  const s = setup(); await s.login();
  await s.service.startRun({ appId: 1001 });
  let resolveRead;
  s.clients[0].readImpl = () => new Promise(resolve => { resolveRead = resolve; });
  const starting = s.service.startRun({ appId: 1002 });
  await settle();
  await s.service.stopRun(1001);
  assert.deepEqual(s.clients[0].requests.at(-1).apps, []);
  resolveRead({ apps: structuredClone(s.clients[0].apps) });
  await starting;
  assert.deepEqual(s.clients[0].requests.at(-1).apps, [1002]);
  await s.service.close();
});

test('already reached cumulative targets never send a run request or conflate the session timer', async () => {
  const s = setup(); await s.login();
  for (const totalTargetMinutes of [0, 10]) {
    const run = await s.service.startRun({ appId: 1001, totalTargetMinutes, stopAfterMinutes: 30 });
    assert.equal(run.state, 'stopped');
    assert.equal(run.reason, 'total_target_already_reached');
    assert.equal(run.startedAt, null);
    assert.equal(run.deadline, null);
    assert.equal(run.requestStatus, 'not_requested');
  }
  assert.equal(s.clients[0].requests.length, 0);
  await s.service.close();
});

test('individual session deadline stops only its game while Steam total remains observed data', async () => {
  const s = setup(); await s.login();
  await s.service.startRun({ appId: 1001, totalTargetMinutes: 100, stopAfterMinutes: 1 });
  await s.service.startRun({ appId: 1002, stopAfterMinutes: 3 });
  await s.clock.advance(60000);
  const [first, second] = s.service.runs();
  assert.equal(first.state, 'stopped'); assert.equal(first.reason, 'session_deadline');
  assert.equal(first.localElapsedSeconds, 60); assert.equal(first.steamMinutes, 10);
  assert.equal(second.state, 'running');
  assert.deepEqual(s.clients[0].requests.at(-1).apps, [1002]);
  await s.service.close();
});

test('only server readback reaches cumulative target; local elapsed cannot invent Steam minutes', async () => {
  const s = setup(); await s.login();
  await s.service.startRun({ appId: 1001, totalTargetMinutes: 11 });
  await s.clock.advance(60000);
  assert.equal(s.service.runs()[0].state, 'running');
  assert.equal(s.service.runs()[0].steamMinutes, 10);
  s.clients[0].apps[0].playtime_forever = 11;
  await s.service.ownedGames();
  assert.equal(s.service.runs()[0].reason, 'total_target_reached');
  assert.equal(s.service.runs()[0].readbackStatus, 'increase_observed');
  assert.deepEqual(s.clients[0].requests.at(-1).apps, []);
  await s.service.close();
});

test('disconnect and real-game conflict clear the session, preserve unknown and never reconnect', async () => {
  for (const kind of ['disconnected', 'playingState', 'error']) {
    const s = setup(); await s.login();
    await s.service.startRun({ appId: 1001 });
    s.clients[0].emit(kind, kind === 'playingState' ? true : new Error('private upstream detail'));
    await settle();
    assert.equal(s.service.status().state, 'error');
    assert.equal(s.service.runs()[0].state, 'unknown');
    assert.deepEqual(s.clients[0].requests.at(-1).apps, []);
    await assert.rejects(s.service.startRun({ appId: 1002 }), { code: 'steam_not_connected' });
    assert.equal(s.clients.length, 1);
    assert.equal(JSON.stringify(s.events).includes('private upstream'), false);
    assert.ok(s.clients[0].requests.every(request => request.force === false));
    await s.service.close();
  }
});

test('sleep/clock gaps pause runs and exclude the unobserved gap; host suspend also cleans up', async () => {
  for (const gap of [[3600000, 3600000], [-1000, 1000], [3600000, 0]]) {
    const s = setup(); await s.login(); await s.service.startRun({ appId: 1001 });
    await s.clock.advance(2000);
    s.clock.jump(...gap); s.clock.heartbeat();
    assert.equal(s.service.runs()[0].state, 'paused');
    assert.equal(s.service.runs()[0].localElapsedSeconds, 2);
    assert.equal(s.service.status().error, 'system_sleep_or_clock_gap');
    await s.service.close();
  }
  const s = setup(); await s.login(); await s.service.startRun({ appId: 1001 });
  s.service.suspend();
  assert.equal(s.service.runs()[0].reason, 'system_sleep');
  assert.deepEqual(s.clients[0].requests.at(-1).apps, []);
  await s.service.close();
});

test('host sleep before login or after logout does not invent an authentication error', async () => {
  const s = setup();
  assert.equal(s.service.suspend().state, 'logged_out');
  await s.login(); await s.service.logout();
  assert.equal(s.service.suspend().state, 'logged_out');
  await s.service.close();
});

test('stale owned reads and queued starts cannot cross logout or the account generation', async () => {
  const s = setup(); await s.login();
  const oldGeneration = s.service.status().generation;
  let resolveRead;
  s.clients[0].readImpl = () => new Promise(resolve => { resolveRead = resolve; });
  const starting = s.service.startRun({ appId: 1001 });
  const rejected = assert.rejects(starting, { code: 'account_changed' });
  await settle();
  await s.service.logout();
  await rejected;
  const nextLogin = s.login(); await settle();
  await nextLogin;
  resolveRead({ apps: [{ appid: 1001, playtime_forever: 999 }] });
  await settle();
  assert.deepEqual(s.service.runs(), []);
  assert.deepEqual(s.clients[1].requests, []);
  await assert.rejects(s.service.startRun({ appId: 1001, generation: oldGeneration }), { code: 'account_changed' });
  await s.service.close();
});

test('new account rejects old Guard callbacks and late old-client events', async () => {
  const s = setup({ requireGuard: true });
  const first = s.login();
  const rejected = assert.rejects(first, { code: 'account_changed' });
  await settle();
  const second = s.login(); await settle();
  await rejected;
  s.clients[0].authenticate(NEXT_ID);
  assert.equal(s.service.status().state, 'guard_required');
  s.service.submitGuard('12345');
  assert.equal((await second).steamId, STEAM_ID);
  assert.equal(s.clients[0].submittedCode, undefined);
  await s.service.close();
});

test('unknown/missing/regressing readback interrupts active runs and does not schedule retries', async () => {
  for (const mode of ['missing', 'regressed', 'failed']) {
    const s = setup(); await s.login(); await s.service.startRun({ appId: 1001 });
    if (mode === 'missing') s.clients[0].apps = [];
    if (mode === 'regressed') s.clients[0].apps[0].playtime_forever = 9;
    if (mode === 'failed') s.clients[0].readImpl = async () => { throw new Error('secret'); };
    await assert.rejects(s.service.ownedGames());
    assert.equal(s.service.runs()[0].state, 'unknown');
    assert.equal(s.service.runs()[0].readbackStatus, mode === 'missing' ? 'missing' : mode === 'regressed' ? 'inconsistent' : 'failed');
    assert.equal(s.service.status().state, 'error');
    await s.clock.advance(120000);
    assert.equal(s.clients.length, 1);
    await s.service.close();
  }
});

test('owned-game request timeout while running clears all requested games', async () => {
  const s = setup(); await s.login(); await s.service.startRun({ appId: 1001 });
  s.clients[0].readImpl = () => new Promise(() => {});
  const reading = s.service.ownedGames();
  const rejected = assert.rejects(reading, { code: 'steam_request_timeout' });
  await settle(); await s.clock.advance(15000); await rejected;
  assert.equal(s.service.runs()[0].state, 'unknown');
  assert.deepEqual(s.clients[0].requests.at(-1).apps, []);
  await s.service.close();
});

test('unconfirmed logout stays unknown rather than claiming a stopped Steam session', async () => {
  const s = setup(); await s.login(); await s.service.startRun({ appId: 1001 });
  s.clients[0].logOff = () => {};
  const loggingOut = s.service.logout();
  await s.clock.advance(5000);
  const result = await loggingOut;
  assert.equal(result.cleanup, 'unknown');
  assert.equal(s.service.runs()[0].state, 'unknown');
  assert.equal(s.service.runs()[0].stopStatus, 'cleanup_unknown');
  await s.service.close();
});

test('store search and app details are anonymous bounded requests with validated target identity', async () => {
  const urls = [];
  const s = setup({ fetchImpl: async (url, options) => {
    urls.push({ url, options });
    return new Response(JSON.stringify(url.includes('storesearch') ? { items: [
      { id: 1001, type: 'app', name: 'Game', tiny_image: 'https://cdn.invalid/a.png', platforms: { windows: true } },
      { id: 1001, type: 'app' }, { id: 42, type: 'sub' }, { id: -1 } ] } : {
      1001: { success: true, data: { steam_appid: 1001, name: 'Game', detailed_description: '<p>Text &amp; more</p>',
        header_image: 'https://cdn.invalid/a.png', platforms: { windows: true }, developers: ['Studio'], release_date: { date: '2026' } } }
    }));
  } });
  const games = await s.service.searchStore(' game ');
  assert.equal(games.length, 1); assert.equal(games[0].appId, 1001); assert.equal(games[0].steamMinutes, null);
  const details = await s.service.appDetails(1001);
  assert.equal(details.description, 'Text & more'); assert.equal(details.platforms.mac, false);
  assert.equal(details.storeUrl, 'https://store.steampowered.com/app/1001/');
  assert.ok(urls[0].url.includes('term=game'));
  assert.ok(urls.every(call => call.options.credentials === 'omit' && call.options.redirect === 'error'));
  assert.equal(s.clients.length, 0);
  await s.service.close();
});

test('store failures/oversize/mismatched identity never expose upstream details', async () => {
  const cases = [
    async () => { throw new Error('secret upstream password'); },
    async () => new Response('{}', { status: 500 }),
    async () => new Response('x'.repeat(2 * 1024 * 1024 + 1)),
    async () => new Response('{}', { headers: { 'content-length': String(3 * 1024 * 1024) } }),
    async () => new Response('{not json'),
    async () => new Response(JSON.stringify({ 1001: { success: true, data: { steam_appid: 1002 } } }))
  ];
  for (const fetchImpl of cases) {
    const s = setup({ fetchImpl });
    await assert.rejects(s.service.appDetails(1001), error => !error.message.includes('secret'));
    await s.service.close();
  }
});

test('store timeout and close cancel in-flight requests; malformed IDs and bounds are refused', async () => {
  const s = setup({ fetchImpl: async (_url, { signal }) => new Promise((_, reject) => {
    signal.addEventListener('abort', () => reject(new Error('upstream abort detail')), { once: true });
  }) });
  const searching = s.service.searchStore('game');
  const timeout = assert.rejects(searching, { code: 'store_request_timeout' });
  await s.clock.advance(12000); await timeout;
  await assert.rejects(s.service.appDetails('1001'), { code: 'invalid_app_id' });
  await assert.rejects(s.service.searchStore('x'), { code: 'invalid_search_query' });
  await assert.rejects(s.service.startRun({ appId: 1001, stopAfterMinutes: -1 }), { code: 'invalid_session_duration' });
  const another = s.service.searchStore('game');
  const closed = assert.rejects(another, { code: 'service_closed' });
  await s.service.close(); await closed;
  await assert.rejects(s.service.login({ accountName: 'x', password: 'x' }), { code: 'service_closed' });
});
