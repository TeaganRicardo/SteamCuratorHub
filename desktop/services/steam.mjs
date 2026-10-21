import { randomInt, randomUUID } from 'node:crypto';
import { performance } from 'node:perf_hooks';
import { refreshTokenIdentity } from './steam-session.mjs';

const MAX_APPS = 100000;
const MAX_PARALLEL = 32;
const MAX_BODY = 2 * 1024 * 1024;
const READBACK_INTERVAL = 60000;
const GAP_LIMIT = 6000;
const RECONNECT_DELAYS = [1000, 3000, 10000];
const TRANSIENT_RESULTS = new Set([0, 2, 3, 10, 16, 20, 35, 36, 48]);
const AUTH_RESULTS = new Set([5, 15, 27, 63, 65, 66, 71, 74, 85, 88]);
const CONFLICT_RESULTS = new Set([6, 34, 50]);
const NETWORK_ERRORS = new Set(['ETIMEDOUT', 'ECONNRESET', 'ENOTFOUND', 'EAI_AGAIN', 'ECONNREFUSED', 'EHOSTUNREACH', 'ENETUNREACH']);
const SESSION_ERRORS = new Set(['credential_encryption_unavailable', 'credential_encryption_unavailable_use_session_only',
  'credential_decryption_failed', 'credential_store_unreadable', 'credential_store_write_failed', 'credential_store_changed_retry',
  'invalid_steam_session', 'steam_session_expired']);

function failure(code) { return Object.assign(new Error(code), { code }); }
function appIdOf(value) {
  if (!Number.isInteger(value) || value < 1 || value > 0xffffffff) throw failure('invalid_app_id');
  return value;
}
function minutesOf(value, code, allowZero = false) {
  if (value === undefined || value === null || value === '') return null;
  if (typeof value !== 'number' || !Number.isFinite(value) || value < 0 || (!allowZero && value === 0) || value > 5256000) throw failure(code);
  return value;
}
function text(value, limit = 500) { return typeof value === 'string' ? value.slice(0, limit) : ''; }
function plainText(value, limit = 50000) {
  return text(value, limit * 2).replace(/<br\s*\/?\s*>/gi, '\n').replace(/<[^>]*>/g, '')
    .replace(/&(?:amp|lt|gt|quot|#39|nbsp);/g, match => ({ '&amp;': '&', '&lt;': '<', '&gt;': '>',
      '&quot;': '"', '&#39;': "'", '&nbsp;': ' ' })[match]).slice(0, limit);
}
function imageUrl(value) {
  try { const url = new URL(value); return url.protocol === 'https:' ? url.href : ''; } catch { return ''; }
}

/** Steam protocol sessions are separate from browser publishing and native stats sessions. */
export class SteamService {
  constructor({ onEvent = () => {}, fetchImpl = globalThis.fetch, SteamUserClass, sessionStore, clock = {} } = {}) {
    this._onEvent = onEvent;
    this._fetch = fetchImpl;
    this._SteamUserClass = SteamUserClass;
    this._sessionStore = sessionStore;
    this._auth = null;
    this._loginTimer = null;
    this._retryTimer = null;
    this._stableTimer = null;
    this._reconnectAttempts = 0;
    this._clock = { now: Date.now, monotonicNow: () => performance.now(), setTimeout, clearTimeout,
      setInterval, clearInterval, ...clock };
    this._generation = 0;
    this._status = { state: 'logged_out', generation: 0 };
    this._client = null;
    this._session = new AbortController();
    this._pendingLogin = null;
    this._guard = null;
    this._runRecords = new Map();
    this._ownedRequest = null;
    this._queue = Promise.resolve();
    this._termination = Promise.resolve({ state: 'not_connected' });
    this._timer = null;
    this._nextReadback = Infinity;
    this._lastTick = null;
    this._closed = false;
    this._http = new AbortController();
  }

  status() { return structuredClone(this._status); }
  _emit(type, data) {
    try { this._onEvent({ type, data: structuredClone(data) }); } catch { /* Observers cannot own the session. */ }
  }
  _setStatus(value) {
    const auth = this._auth;
    this._status = { ...value, ...(auth ? { accountName: auth.accountName, persistence: auth.persistence,
      restoreAvailable: Boolean(auth.refreshToken), ...(auth.persistenceError ? { persistenceError: auth.persistenceError } : {}) } : {}),
      generation: this._generation };
    if (auth && !auth.persistenceError) delete this._status.persistenceError;
    this._emit('steam.status', this._status);
  }
  _check(generation = this._generation) {
    if (this._closed) throw failure('service_closed');
    if (generation !== this._generation) throw failure('account_changed');
    if (this._status.state !== 'connected' || !this._client?.steamID || this._session.signal.aborted) {
      throw failure('steam_not_connected');
    }
    if (this._client.playingState?.blocked) throw failure('playing_elsewhere');
  }

  async login({ accountName, password } = {}) {
    if (this._closed) throw failure('service_closed');
    if (typeof accountName !== 'string' || !accountName.trim() || accountName.length > 128 ||
        typeof password !== 'string' || !password || password.length > 1024) throw failure('invalid_credentials');
    // Replacing an account must not leave its reusable credentials on disk.
    this._sessionStore?.clearSteamSession();
    return this._beginLogin({ accountName: accountName.trim(), password }, {
      accountName: accountName.trim(), refreshToken: null, steamId: null, persistence: 'pending'
    }, 'logging_in');
  }

  async restore() {
    if (this._closed) throw failure('service_closed');
    if (this._status.state === 'connected') return this.status();
    if (this._pendingLogin || this._retryTimer || this._client) throw failure('steam_login_in_progress');
    let saved, persisted;
    try {
      persisted = this._sessionStore?.readSteamSession();
      saved = persisted ?? (this._auth?.refreshToken ? this._auth : null);
      if (!saved) return this.status();
      if (refreshTokenIdentity(saved.refreshToken, this._clock.now()) !== saved.steamId) throw failure('invalid_steam_session');
    } catch (error) {
      const clearError = ['invalid_steam_session', 'steam_session_expired'].includes(error.message) ? this._forgetReusable() : null;
      this._setStatus({ state: 'error', error: this._sessionError(error), ...(clearError ? { persistenceError: clearError } : {}) });
      throw failure(this._sessionError(error));
    }
    return this._beginLogin({ refreshToken: saved.refreshToken }, { ...saved, persistence: persisted ? 'encrypted' : 'session_only' }, 'restoring');
  }

  _beginLogin(credentials, auth, state) {
    const previous = this._client;
    this._freezeRuns('unknown', 'account_changed');
    this._emitRuns();
    const generation = ++this._generation;
    this._invalidate('account_changed');
    this._client = null;
    this._auth = auth;
    this._reconnectAttempts = 0;
    this._runRecords.clear();
    this._emitRuns();
    this._session = new AbortController();
    let resolve, reject;
    const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
    this._pendingLogin = { resolve, reject, promise };
    this._setStatus({ state });
    this._connect(credentials, generation, previous).catch(() => {
      if (generation === this._generation && !this._closed) this._fault('steam_client_unavailable');
    });
    return promise;
  }

  async _connect(credentials, generation, previous) {
    await this._closeClient(previous);
    if (generation !== this._generation || this._closed) return;
    let SteamUser;
    try { SteamUser = this._SteamUserClass ?? (await import('steam-user')).default; }
    catch { if (generation === this._generation) this._fault('steam_client_unavailable'); return; }
    if (generation !== this._generation || this._closed) return;
    let client;
    try {
      // The application owns the retry budget and never resumes gamesPlayed.
      client = new SteamUser({ dataDirectory: null, autoRelogin: false, saveAppTickets: false,
        renewRefreshTokens: true, enablePicsCache: false });
    } catch { this._fault('steam_client_unavailable'); return; }
    this._client = client;
    const current = () => this._client === client && generation === this._generation && !this._closed && !this._session.signal.aborted;
    this._loginTimer = this._clock.setTimeout(() => {
      if (current()) this._connectionFailure('login_timeout', 16);
    }, 120000);
    client.on('error', error => { if (current()) this._connectionFailure('steam_error', error?.eresult, error?.code); });
    client.on('disconnected', result => { if (current()) this._connectionFailure('steam_disconnected', result ?? 0); });
    client.on('refreshToken', token => {
      if (!current()) return;
      try {
        const steamId = refreshTokenIdentity(token, this._clock.now());
        if (this._auth.steamId && steamId !== this._auth.steamId) throw failure('invalid_steam_session');
        this._auth.steamId = steamId; this._auth.refreshToken = token;
        // Upstream may emit this before loggedOn; persist only after identity confirmation.
        if (this._status.state === 'connected') { this._persistSession(); this._setStatus({ ...this._status }); }
      } catch {
        this._forgetReusable(); this._fault('invalid_steam_session');
      }
    });
    client.on('playingState', blocked => { if (current() && blocked) this._fault('playing_elsewhere'); });
    client.on('steamGuard', (domain, callback, lastCodeWrong) => {
      if (!current()) return;
      this._guard = { callback, generation };
      const guardPrompt = { kind: domain ? 'email' : 'two_factor', lastCodeWrong: Boolean(lastCodeWrong) };
      // The account name is public progress context; codes and tokens never leave this owner.
      if (typeof domain === 'string') guardPrompt.domain = text(domain, 128);
      this._setStatus({ state: 'guard_required', guardPrompt });
      this._emit('steam.guard', guardPrompt);
    });
    client.on('accountInfo', name => {
      if (current() && this._status.state === 'connected') {
        this._setStatus({ ...this._status, displayName: text(name, 200) });
      }
    });
    client.on('loggedOn', () => {
      if (!current()) {
        // A late authentication response may arrive after cancellation. Never adopt it.
        try { client.gamesPlayed([], false); client.logOff(); } catch {}
        return;
      }
      const steamId = client.steamID?.getSteamID64?.();
      if (typeof steamId !== 'string' || !/^\d{17}$/.test(steamId)) return this._fault('invalid_steam_identity');
      if (this._auth.steamId && steamId !== this._auth.steamId) {
        this._forgetReusable(); return this._fault('invalid_steam_identity');
      }
      if (client.playingState?.blocked) return this._fault('playing_elsewhere');
      this._auth.steamId = steamId;
      this._persistSession();
      this._guard = null;
      const pending = this._pendingLogin;
      this._pendingLogin = null;
      this._clock.clearTimeout(this._loginTimer); this._loginTimer = null;
      this._setStatus({ state: 'connected', steamId });
      this._startTimer();
      this._stableTimer = this._clock.setTimeout(() => { if (current()) this._reconnectAttempts = 0; }, 60000);
      this._stableTimer?.unref?.();
      pending?.resolve(this.status());
    });
    try {
      client.logOn({ ...credentials, logonID: randomInt(1, 0x7fffffff),
        machineName: 'SteamCuratorHub' });
    } catch { this._fault('login_failed'); }
  }

  _sessionError(error) {
    const code = error?.code ?? error?.message;
    return SESSION_ERRORS.has(code) ? code : 'steam_session_save_failed';
  }
  _persistSession() {
    const auth = this._auth;
    if (!auth?.refreshToken) { if (auth) auth.persistence = 'session_only'; return; }
    try {
      if (!this._sessionStore) { auth.persistence = 'session_only'; return; }
      this._sessionStore.writeSteamSession({ accountName: auth.accountName, steamId: auth.steamId, refreshToken: auth.refreshToken });
      auth.persistence = 'encrypted'; delete auth.persistenceError;
    } catch (error) { auth.persistence = 'session_only'; auth.persistenceError = this._sessionError(error); }
  }
  _forgetReusable() {
    if (this._auth) { this._auth.refreshToken = null; this._auth.persistence = 'none'; delete this._auth.persistenceError; }
    try { this._sessionStore?.clearSteamSession(); }
    catch (error) {
      if (this._auth) this._auth.persistenceError = this._sessionError(error);
      return this._sessionError(error);
    }
  }
  _connectionFailure(code, result, networkCode) {
    if (AUTH_RESULTS.has(result)) {
      const reusable = Boolean(this._auth?.refreshToken);
      this._forgetReusable();
      return this._fault(reusable ? 'steam_auth_required' : 'steam_credentials_rejected');
    }
    if (CONFLICT_RESULTS.has(result)) return this._fault('steam_session_conflict');
    if (this._auth?.refreshToken && (TRANSIENT_RESULTS.has(result) || NETWORK_ERRORS.has(networkCode))) return this._scheduleReconnect(code);
    this._fault(code);
  }
  _scheduleReconnect(code) {
    if (this._reconnectAttempts >= RECONNECT_DELAYS.length) return this._fault('steam_reconnect_exhausted');
    const delay = RECONNECT_DELAYS[this._reconnectAttempts++];
    const client = this._client;
    this._freezeRuns('unknown', code);
    const generation = ++this._generation;
    this._invalidate(code, true);
    this._client = null;
    this._session = new AbortController();
    this._setStatus({ state: 'reconnecting', steamId: this._auth.steamId, attempt: this._reconnectAttempts,
      maxAttempts: RECONNECT_DELAYS.length, retryAt: new Date(this._clock.now() + delay).toISOString() });
    this._emitRuns();
    this._retryTimer = this._clock.setTimeout(() => {
      this._retryTimer = null;
      if (generation !== this._generation || this._closed) return;
      try { refreshTokenIdentity(this._auth.refreshToken, this._clock.now()); }
      catch { this._forgetReusable(); this._fault('steam_auth_required'); return; }
      this._connect({ refreshToken: this._auth.refreshToken }, generation).catch(() => {
        if (generation === this._generation && !this._closed) this._fault('steam_client_unavailable');
      });
    }, delay);
    this._closeClient(client);
  }

  submitGuard(code) {
    if (typeof code !== 'string' || !/^[a-zA-Z0-9]{4,12}$/.test(code.trim())) throw failure('invalid_guard_code');
    const guard = this._guard;
    if (!guard || guard.generation !== this._generation || this._status.state !== 'guard_required') {
      throw failure('guard_not_requested');
    }
    this._guard = null;
    this._setStatus({ state: 'logging_in' });
    try { guard.callback(code.trim()); } catch { this._fault('guard_failed'); throw failure('guard_failed'); }
    return this.status();
  }

  _invalidate(code, keepLogin = false) {
    this._session.abort(failure(code));
    this._guard = null;
    this._clock.clearTimeout(this._loginTimer); this._loginTimer = null;
    this._clock.clearTimeout(this._retryTimer); this._retryTimer = null;
    this._clock.clearTimeout(this._stableTimer); this._stableTimer = null;
    this._ownedRequest = null;
    this._clock.clearInterval(this._timer);
    this._timer = null;
    this._nextReadback = Infinity;
    if (!keepLogin) {
      const pending = this._pendingLogin;
      this._pendingLogin = null;
      pending?.reject(failure(code));
    }
  }

  async _endClient(client) {
    if (!client) return { state: 'not_connected' };
    let clearRequested = false;
    if (client.steamID) {
      try { client.gamesPlayed([], false); clearRequested = true; } catch {}
    }
    return new Promise(resolve => {
      let timer;
      let ended = false;
      const done = state => {
        if (ended) return;
        ended = true;
        this._clock.clearTimeout(timer);
        client.off('disconnected', disconnected);
        resolve({ state, clearRequested });
      };
      const disconnected = () => done('logged_off');
      client.once('disconnected', disconnected);
      timer = this._clock.setTimeout(() => done('unknown'), 5000);
      try { client.logOff(); } catch { done('unknown'); }
    });
  }

  _closeClient(client) {
    // Retain every outstanding shutdown, including across several rapid login replacements.
    this._termination = Promise.all([this._termination, this._endClient(client)]).then(([earlier, current]) =>
      current.state === 'not_connected' ? earlier : current);
    return this._termination;
  }

  _fault(code, runState = 'unknown') {
    const client = this._client;
    if (this._closed || (!client && !this._pendingLogin && !['logging_in', 'restoring', 'reconnecting', 'guard_required'].includes(this._status.state))) return;
    this._freezeRuns(runState, code);
    ++this._generation;
    this._invalidate(code);
    this._client = null;
    this._setStatus({ state: 'error', error: code });
    this._emitRuns();
    this._closeClient(client);
  }

  async _disconnect(forgetSession) {
    const client = this._client;
    ++this._generation;
    this._freezeRuns('stopped', 'logout');
    this._invalidate('logged_out');
    this._client = null;
    const clearError = forgetSession ? this._forgetReusable() : null;
    this._auth = null;
    this._setStatus({ state: this._closed ? 'closed' : 'logged_out' });
    this._emitRuns();
    const result = await this._closeClient(client);
    if (result.state === 'unknown') {
      for (const run of this._runRecords.values()) {
        if (run.reason === 'logout') { run.state = 'unknown'; run.stopStatus = 'cleanup_unknown'; }
      }
      this._emitRuns();
    }
    if (clearError) { this._setStatus({ state: this._closed ? 'closed' : 'logged_out', persistenceError: clearError }); throw failure(clearError); }
    return { ...this.status(), cleanup: result.state };
  }

  logout() { return this._disconnect(true); }

  /** Host powerMonitor can call this before OS sleep; there is no automatic resume. */
  suspend(reason = 'system_sleep') { this._fault(reason === 'system_sleep' ? reason : 'session_suspended', 'paused'); return this.status(); }

  async close() {
    if (this._closed) { await this._termination; return this.status(); }
    this._closed = true;
    this._http.abort(failure('service_closed'));
    await this._disconnect(false);
    return this.status();
  }

  async _bounded(promise, signal, timeoutCode = 'steam_request_timeout') {
    if (signal.aborted) throw signal.reason;
    let timer;
    let abort;
    const limit = new Promise((_, reject) => {
      timer = this._clock.setTimeout(() => reject(failure(timeoutCode)), 15000);
      abort = () => reject(signal.reason ?? failure('account_changed'));
      signal.addEventListener('abort', abort, { once: true });
    });
    try { return await Promise.race([promise, limit]); }
    finally { this._clock.clearTimeout(timer); signal.removeEventListener('abort', abort); }
  }

  async ownedGames() {
    const generation = this._generation;
    this._check(generation);
    if (this._ownedRequest?.generation === generation) return this._ownedRequest.promise;
    const client = this._client;
    const signal = this._session.signal;
    const began = this._clock.now();
    const promise = (async () => {
      let response;
      try {
        response = await this._bounded(client.getUserOwnedApps(client.steamID, { includeAppInfo: true,
          includePlayedFreeGames: true, includeFreeSub: true, skipUnvettedApps: false }), signal);
      } catch (error) {
        if (generation !== this._generation || signal.aborted) throw failure('account_changed');
        this._readbackFailure();
        throw failure(error?.code === 'steam_request_timeout' ? error.code : 'steam_library_failed');
      }
      this._check(generation);
      const elapsed = this._clock.now() - began;
      if (elapsed < 0 || elapsed > 15000) { this._fault('system_sleep_or_clock_gap'); throw failure('system_sleep_or_clock_gap'); }
      if (!Array.isArray(response?.apps) || response.apps.length > MAX_APPS) {
        this._readbackFailure(); throw failure('invalid_steam_library');
      }
      const seen = new Set();
      const games = [];
      for (const app of response.apps) {
        const appId = app?.appid;
        if (!Number.isInteger(appId) || appId < 1 || appId > 0xffffffff || seen.has(appId)) continue;
        seen.add(appId);
        const steamMinutes = Number.isSafeInteger(app.playtime_forever) && app.playtime_forever >= 0 ? app.playtime_forever : null;
        games.push({ appId, name: text(app.name, 500) || `App ${appId}`, steamMinutes });
      }
      this._applyReadback(games);
      this._check(generation);
      return games;
    })();
    this._ownedRequest = { generation, promise };
    try { return await promise; }
    finally { if (this._ownedRequest?.promise === promise) this._ownedRequest = null; }
  }

  _readbackFailure() {
    for (const run of this._runRecords.values()) run.readbackStatus = 'failed';
    if (this._activeIds().length) this._fault('steam_readback_failed');
    else { this._nextReadback = Infinity; this._emitRuns(); }
  }
  _applyReadback(games) {
    const byId = new Map(games.map(game => [game.appId, game]));
    const reached = [];
    let missingActive = false;
    for (const run of this._runRecords.values()) {
      const value = byId.get(run.appId)?.steamMinutes;
      if (value === null || value === undefined) {
        run.readbackStatus = 'missing';
        if (run.state === 'running') missingActive = true;
        continue;
      }
      const regressed = value < run.steamMinutes || value < run.baselineSteamMinutes;
      run.steamMinutes = value;
      run.lastReadbackAt = new Date(this._clock.now()).toISOString();
      run.readbackStatus = regressed ? 'inconsistent' : value > run.baselineSteamMinutes ?
        'increase_observed' : 'no_increase_observed';
      if (run.state === 'running' && run.totalTargetMinutes !== null && value >= run.totalTargetMinutes) reached.push(run.appId);
      if (run.state === 'running' && regressed) missingActive = true;
    }
    if (missingActive) { this._fault('steam_readback_unavailable'); return; }
    if (reached.length) this._stopIds(reached, 'total_target_reached');
    this._emitRuns();
  }

  _enqueue(operation) {
    const generation = this._generation;
    const result = this._queue.then(() => { this._check(generation); return operation(generation); });
    this._queue = result.catch(() => {});
    return result;
  }
  _activeIds() { return [...this._runRecords.values()].filter(run => run.state === 'running').map(run => run.appId); }
  _sendPlaying(appIds) {
    this._check();
    try { this._client.gamesPlayed(appIds, false); }
    catch { this._fault('steam_play_request_failed'); throw failure('steam_play_request_failed'); }
  }

  async startRun({ appId, totalTargetMinutes, stopAfterMinutes, generation: expectedGeneration } = {}) {
    appIdOf(appId);
    const total = minutesOf(totalTargetMinutes, 'invalid_total_target', true);
    const stop = minutesOf(stopAfterMinutes, 'invalid_session_duration');
    if (expectedGeneration !== undefined && expectedGeneration !== this._generation) return Promise.reject(failure('account_changed'));
    const generation = this._generation;
    this._check(generation);
    if (this._runRecords.get(appId)?.state === 'running') throw failure('already_running');
    // Do not hold the mutation queue across network IO: stopping another game stays immediate.
    const owned = await this.ownedGames();
    this._check(generation);
    return this._enqueue(() => {
      this._check(generation);
      if (this._runRecords.get(appId)?.state === 'running') throw failure('already_running');
      if (this._activeIds().length >= MAX_PARALLEL) throw failure('parallel_run_limit');
      const game = owned.find(item => item.appId === appId);
      if (!game) throw failure('game_not_owned');
      if (game.steamMinutes === null) throw failure('steam_playtime_unavailable');
      const now = this._clock.now();
      const reached = total !== null && game.steamMinutes >= total;
      const run = { id: randomUUID(), appId, name: game.name, state: reached ? 'stopped' : 'running', generation,
        steamId: this._status.steamId, startedAt: reached ? null : new Date(now).toISOString(),
        stoppedAt: reached ? new Date(now).toISOString() : null, baselineSteamMinutes: game.steamMinutes,
        steamMinutes: game.steamMinutes, totalTargetMinutes: total, stopAfterMinutes: stop,
        deadline: reached || stop === null ? null : new Date(now + stop * 60000).toISOString(),
        readbackStatus: 'observed', lastReadbackAt: new Date(now).toISOString(),
        reason: reached ? 'total_target_already_reached' : null,
        requestStatus: reached ? 'not_requested' : 'requested_without_acknowledgement',
        stopStatus: reached ? 'not_requested' : null, _elapsedMs: 0, _lastMono: this._clock.monotonicNow() };
      if (!reached) this._sendPlaying([...this._activeIds(), appId]);
      this._runRecords.set(appId, run);
      if (!reached) this._nextReadback = Math.min(this._nextReadback, now + READBACK_INTERVAL);
      this._emitRuns();
      return this.runs().find(item => item.appId === appId);
    });
  }

  stopRun(appId) {
    appIdOf(appId);
    return this._enqueue(() => {
      const run = this._runRecords.get(appId);
      if (!run) throw failure('run_not_found');
      if (run.state === 'running') this._stopIds([appId], 'user_stopped');
      return this.runs().find(item => item.appId === appId);
    });
  }
  _capture(run) {
    const now = this._clock.monotonicNow();
    const delta = now - run._lastMono;
    // Never claim time in an unobserved sleep/stall gap.
    if (delta >= 0 && delta <= GAP_LIMIT) run._elapsedMs += delta;
    run._lastMono = now;
  }
  _stopIds(ids, reason) {
    const selected = new Set(ids);
    this._sendPlaying(this._activeIds().filter(id => !selected.has(id)));
    for (const id of ids) {
      const run = this._runRecords.get(id);
      if (!run || run.state !== 'running') continue;
      this._capture(run);
      run.state = 'stopped'; run.reason = reason;
      run.stoppedAt = new Date(this._clock.now()).toISOString();
      run.stopStatus = 'requested_without_acknowledgement';
    }
    this._nextReadback = Math.min(this._nextReadback, this._clock.now() + 5000);
    this._emitRuns();
  }
  _freezeRuns(state, reason) {
    for (const run of this._runRecords.values()) {
      if (run.state !== 'running') continue;
      if (reason !== 'system_sleep_or_clock_gap') this._capture(run);
      run.state = state; run.reason = reason;
      run.stoppedAt = new Date(this._clock.now()).toISOString(); run.stopStatus = 'cleanup_unverified';
    }
  }

  runs() {
    return [...this._runRecords.values()].map(run => {
      const { _elapsedMs, _lastMono, ...value } = run;
      const delta = run.state === 'running' ? this._clock.monotonicNow() - _lastMono : 0;
      const elapsed = _elapsedMs + (delta >= 0 && delta <= GAP_LIMIT ? delta : 0);
      return { ...value, localElapsedSeconds: Math.floor(elapsed / 1000), localElapsedMinutes: elapsed / 60000 };
    });
  }
  _emitRuns() { this._emit('steam.runs', this.runs()); }
  _startTimer() {
    this._lastTick = { wall: this._clock.now(), mono: this._clock.monotonicNow() };
    this._timer = this._clock.setInterval(() => this._tick(), 1000);
    this._timer?.unref?.();
  }
  _tick() {
    if (this._status.state !== 'connected' || this._closed) return;
    const wall = this._clock.now();
    const mono = this._clock.monotonicNow();
    const wallDelta = wall - this._lastTick.wall;
    const monoDelta = mono - this._lastTick.mono;
    this._lastTick = { wall, mono };
    if (wallDelta < 0 || monoDelta < 0 || wallDelta > GAP_LIMIT || monoDelta > GAP_LIMIT ||
        Math.abs(wallDelta - monoDelta) > GAP_LIMIT) return this._fault('system_sleep_or_clock_gap', 'paused');
    for (const run of this._runRecords.values()) if (run.state === 'running') this._capture(run);
    const due = [...this._runRecords.values()].filter(run => run.state === 'running' && run.deadline && Date.parse(run.deadline) <= wall);
    try { if (due.length) this._stopIds(due.map(run => run.appId), 'session_deadline'); }
    catch { return; }
    if (wall >= this._nextReadback && !this._ownedRequest) {
      this._nextReadback = wall + READBACK_INTERVAL;
      // Unknown/failing readback interrupts active runs; the failed request is not retried.
      this.ownedGames().catch(() => {});
    }
    if (this._activeIds().length) this._emitRuns();
  }

  async _json(url) {
    if (this._closed) throw failure('service_closed');
    const controller = new AbortController();
    const signal = AbortSignal.any([controller.signal, this._http.signal]);
    const timer = this._clock.setTimeout(() => controller.abort(), 12000);
    try {
      const response = await this._fetch(url, { signal, redirect: 'error', credentials: 'omit',
        headers: { Accept: 'application/json' } });
      if (!response.ok) throw failure('store_request_failed');
      if (Number(response.headers?.get?.('content-length')) > MAX_BODY) throw failure('store_response_too_large');
      let body;
      if (response.body?.getReader) {
        const reader = response.body.getReader();
        const chunks = [];
        let size = 0;
        try {
          while (true) {
            const next = await reader.read();
            if (next.done) break;
            size += next.value.byteLength;
            if (size > MAX_BODY) { controller.abort(); throw failure('store_response_too_large'); }
            chunks.push(next.value);
          }
        } finally { reader.releaseLock(); }
        body = Buffer.concat(chunks).toString('utf8');
      } else {
        body = await response.text();
        if (Buffer.byteLength(body, 'utf8') > MAX_BODY) throw failure('store_response_too_large');
      }
      if (signal.aborted) throw failure('store_request_cancelled');
      return JSON.parse(body);
    } catch (error) {
      if (this._closed) throw failure('service_closed');
      if (error?.code === 'store_response_too_large') throw failure(error.code);
      throw failure(signal.aborted ? 'store_request_timeout' : 'store_request_failed');
    } finally { this._clock.clearTimeout(timer); controller.abort(); }
  }

  async searchStore(query) {
    if (typeof query !== 'string' || query.trim().length < 2 || query.length > 120) throw failure('invalid_search_query');
    const url = new URL('https://store.steampowered.com/api/storesearch/');
    url.searchParams.set('term', query.trim()); url.searchParams.set('l', 'schinese'); url.searchParams.set('cc', 'GB');
    const data = await this._json(url.href);
    if (!Array.isArray(data?.items) || data.items.length > 1000) throw failure('invalid_store_response');
    const seen = new Set();
    return data.items.filter(item => Number.isInteger(item?.id) && item.id > 0 && item.id <= 0xffffffff &&
      (!item.type || item.type === 'app') && !seen.has(item.id) && seen.add(item.id)).slice(0, 50).map(item => ({
      appId: item.id, name: text(item.name, 500), steamMinutes: null, headerImage: imageUrl(item.tiny_image),
      platforms: { windows: Boolean(item.platforms?.windows), mac: Boolean(item.platforms?.mac), linux: Boolean(item.platforms?.linux) },
      storeUrl: `https://store.steampowered.com/app/${item.id}/` }));
  }

  async appDetails(appId) {
    appIdOf(appId);
    const url = `https://store.steampowered.com/api/appdetails?appids=${appId}&l=schinese&cc=GB`;
    const response = await this._json(url);
    const data = response?.[appId]?.data;
    if (response?.[appId]?.success !== true || !data || Number(data.steam_appid) !== appId) throw failure('store_details_unavailable');
    return { appId, name: text(data.name, 500), description: plainText(data.detailed_description || data.short_description),
      headerImage: imageUrl(data.header_image), developers: Array.isArray(data.developers) ? data.developers.slice(0, 30).map(name => text(name, 300)) : [],
      releaseDate: text(data.release_date?.date, 100),
      platforms: { windows: Boolean(data.platforms?.windows), mac: Boolean(data.platforms?.mac), linux: Boolean(data.platforms?.linux) },
      storeUrl: `https://store.steampowered.com/app/${appId}/` };
  }
}
