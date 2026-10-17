import test from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { createRequire } from 'node:module';
import { createLiveTransport } from '../live.mjs';
import { fixturePlan } from '../fixture.mjs';
import { ProbeError } from '../probe.mjs';

class FakeUser extends EventEmitter {
  static last;
  constructor(options) {
    super(); this.options = options; this.requests = []; this.playingState = { blocked: false };
    FakeUser.last = this;
  }
  logOn(details) {
    this.login = details;
    this.steamID = { getSteamID64: () => fixturePlan.expectedSteamId };
    queueMicrotask(() => this.emit('loggedOn'));
  }
  gamesPlayed(apps, force) { this.requests.push({ apps, force }); }
  async getUserOwnedApps(_id, options) {
    this.readOptions = options;
    return { apps: [{ appid: 1001, playtime_forever: 12 }] };
  }
  logOff() { this.steamID = null; this.emit('disconnected'); }
}

async function connect(SteamUserClass = FakeUser) {
  const controller = new AbortController();
  const faults = [];
  const transport = await createLiveTransport({ plan: fixturePlan, signal: controller.signal,
    askSecret: async () => ' password with spaces ', askAccount: async () => 'fixture-account',
    onFault: error => { faults.push(error.code); controller.abort(error); }, SteamUserClass });
  return { transport, controller, faults, user: FakeUser.last };
}

test('live adapter disables storage/reconnect and never forces another session off', async () => {
  const { transport, user, faults } = await connect();
  assert.equal(user.options.dataDirectory, null);
  assert.equal(user.options.autoRelogin, false);
  assert.equal(user.options.saveAppTickets, false);
  assert.equal(user.login.password, ' password with spaces ');
  transport.setPlaying([1001, 1002]);
  await transport.readOwnedApps(new AbortController().signal);
  transport.setPlaying([1002]);
  assert.deepEqual(user.requests, [{ apps: [1001, 1002], force: false }, { apps: [1002], force: false }]);
  assert.deepEqual(user.readOptions.filterAppids, [1001, 1002]);
  await transport.close();
  assert.deepEqual(faults, []);
});

test('wrong account logs off before a play request', async () => {
  class WrongUser extends FakeUser {
    logOn(details) {
      super.logOn(details);
      this.steamID = { getSteamID64: () => '76561198000000001' };
    }
  }
  await assert.rejects(connect(WrongUser), { code: 'account_mismatch' });
  assert.equal(FakeUser.last.steamID, null);
  assert.deepEqual(FakeUser.last.requests, []);
});

test('connection fault and real-game conflict abort without raw error leakage', async () => {
  for (const kind of ['error', 'disconnected', 'playingState']) {
    const { transport, user, faults } = await connect();
    if (kind === 'error') user.emit('error', new Error('secret upstream details'));
    if (kind === 'disconnected') user.emit('disconnected');
    if (kind === 'playingState') user.emit('playingState', true, 999);
    assert.throws(() => transport.setPlaying([1001]), ProbeError);
    assert.equal(faults.length, 1);
    assert.equal(faults[0].includes('secret'), false);
    await transport.close();
  }
});

test('installed patched dependency can construct a client and roundtrip used protobufs offline', async () => {
  const require = createRequire(import.meta.url);
  const SteamUser = require('steam-user');
  const client = new SteamUser({ dataDirectory: null, autoRelogin: false });
  assert.equal(client.packageVersion, '5.3.0');
  const schema = require('steam-user/protobufs/generated/_load.js');
  for (const [name, data] of [
    ['CMsgClientGamesPlayed', { games_played: [{ game_id: '1001' }, { game_id: '1002' }] }],
    ['CPlayer_GetOwnedGames_Response', { game_count: 1, games: [{ appid: 1001, playtime_forever: 10 }] }]
  ]) {
    const type = schema[name];
    assert.ok(type, name);
    const encoded = type.encode(type.fromObject(data)).finish();
    const decoded = type.toObject(type.decode(encoded), { longs: String });
    assert.deepEqual(decoded, data);
  }
  const ticket = require('steam-appticket/protobufs/generated/_load.js');
  const type = ticket.EncryptedAppTicket;
  assert.ok(type);
  const encoded = type.encode(type.create({ ticket_version_no: 1 })).finish();
  assert.equal(type.decode(encoded).ticket_version_no, 1);
});
