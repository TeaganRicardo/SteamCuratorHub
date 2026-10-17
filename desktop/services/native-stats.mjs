import { spawn } from 'node:child_process';
import { buildNativeHelper, nativeBinaryPath } from '../native/build.mjs';

function appId(value) {
  if (!Number.isInteger(value) || value < 1 || value > 0xffffffff) throw new TypeError('Invalid AppID');
  return value;
}
function accountId(value) {
  if (typeof value !== 'string' || !/^7656\d{13}$/.test(value)) throw new TypeError('Expected full SteamID64 string');
  return value;
}
function clone(value) { return structuredClone(value); }
function error(message, mutationOutcome = 'notAttempted') {
  return Object.assign(new Error(message), { mutationOutcome });
}
function fixtureDefaults() {
  return {
    accountId: '76561198000000000',
    achievements: [{ id: 'ACH_FIRST', name: 'First step', description: 'Synthetic achievement', hidden: false, unlocked: false, writable: true, globalPercent: 12.5 }],
    stats: [{ id: 'kills', type: 'int', value: 1, writable: true, min: 0, max: 1000 },
      { id: 'distance', type: 'float', value: 1.25, writable: true, min: 0, max: 100000 }],
  };
}
function snapshotValid(value, expectedAppId) {
  if (!value || value.appId !== expectedAppId || !Array.isArray(value.achievements) || !Array.isArray(value.stats)) {
    throw error('Native helper returned a malformed or wrong-AppID snapshot');
  }
  accountId(value.accountId);
  return value;
}

// This verifies only the documented scope. A local Steam callback plus a new
// process requesting stats is not an independent HTTP observation of the server.
export function reconcileNativeReadback(staged, readback, readbackError) {
  const fixture = staged.source === 'fixture';
  const matchedAccount = readback?.accountId === staged.accountId && readback?.appId === staged.appId;
  const refreshed = readback?.capabilities?.refreshCallbackMatched === true;
  const items = staged.items.map(item => {
    if (item.staging !== 'accepted') return { ...item, status: 'failed' };
    let status = 'unknown';
    let reason;
    const rows = matchedAccount && refreshed ? (item.kind === 'achievement' ? readback.achievements : readback.stats) : [];
    const row = rows.find(row => row.id === item.id);
    const observed = row?.[item.kind === 'achievement' ? 'unlocked' : 'value'];
    const expected = item.type === 'float' ? Math.fround(item.requested) : item.requested;
    const hasObservation = observed !== undefined && observed !== null;
    const valueMatches = observed === expected;
    if (staged.store?.status === 'failed') {
      // A rejected batch can contain both accepted and rejected changes. Keep
      // the batch failure, but do not infer that every individual item failed.
      status = hasObservation && !valueMatches ? 'failed' : 'unknown';
      reason = hasObservation && !valueMatches
        ? `Steam rejected the batch (result ${staged.store.result ?? 'unknown'}); refreshed value differs`
        : `Steam rejected the batch (result ${staged.store.result ?? 'unknown'}); per-item persistence cannot be established`;
    } else if (!readback || !matchedAccount || !refreshed) {
      reason = readbackError || (!matchedAccount ? 'Readback account/AppID differs from the write attempt' : 'No matching refresh callback for independent readback');
    } else {
      if (staged.store?.status !== 'ok') reason = 'Store callback is unknown; matching local values alone do not prove storage';
      else if (!hasObservation) reason = 'Item was unavailable in independent readback';
      else {
        status = valueMatches ? 'verified' : 'failed';
        if (status === 'failed') reason = 'Refreshed value does not match the requested value';
      }
    }
    return { ...item, status, ...(observed !== undefined ? { observed } : {}), ...(reason ? { reason } : {}) };
  });
  const status = items.length && items.every(i => i.status === 'verified') ? 'verified'
    : items.some(i => i.status === 'unknown') ? 'unknown' : 'failed';
  return {
    ...staged, items, status, readback: readback || null,
    verification: {
      scope: fixture ? 'fixture' : 'store-callback-and-separate-process-client-refresh',
      steamVerified: !fixture && status === 'verified', independentProcess: Boolean(readback),
      independentServerRead: false,
      note: fixture ? 'Synthetic transport only; no Steam capability has been proven.'
        : 'Matching StoreStats callback plus a separate helper requesting refreshed Steam client stats; no independent Web API confirmation.',
      ...(readbackError ? { error: readbackError } : {}),
    },
  };
}

export class NativeStatsService {
  #binaryPath; #steamLibraryPath; #fixture; #onEvent; #children = new Set();
  #closed = false; #generation = 0; #tail = Promise.resolve(); #previews = new Map(); #build;
  constructor({ binaryPath, steamLibraryPath, fixture, onEvent } = {}) {
    this.#binaryPath = binaryPath || nativeBinaryPath;
    this.#steamLibraryPath = steamLibraryPath;
    this.#fixture = fixture ? clone(fixture === true ? fixtureDefaults() : fixture) : null;
    this.#onEvent = onEvent;
    this.#build = binaryPath ? Promise.resolve(binaryPath) : null;
  }
  #emit(type, data) { if (this.#closed) return; try { this.#onEvent?.({ type, data }); } catch { /* UI listeners cannot erase a mutation result. */ } }
  #serial(operation) {
    if (this.#closed) return Promise.reject(error('Native stats service is closed'));
    const generation = this.#generation;
    const result = this.#tail.then(() => {
      if (this.#closed || generation !== this.#generation) throw error('Native operation cancelled before start');
      return operation(generation);
    });
    this.#tail = result.catch(() => {});
    return result;
  }
  async #invoke(operation, payload, generation, fixtureOverride) {
    if (this.#closed || generation !== this.#generation) throw error('Native operation cancelled before helper launch');
    if (!this.#build) this.#build = buildNativeHelper().catch(e => { this.#build = null; throw e; });
    await this.#build;
    if (this.#closed || generation !== this.#generation) throw error('Native operation cancelled before helper launch');
    const request = { operation, ...payload,
      ...(this.#steamLibraryPath ? { steamLibraryPath: this.#steamLibraryPath } : {}),
      ...(this.#fixture ? { fixture: fixtureOverride || this.#fixture } : {}) };
    const outcome = operation === 'apply' ? 'unknown' : 'notAttempted';
    return new Promise((resolve, reject) => {
      const env = Object.fromEntries(['PATH', 'HOME', 'TMPDIR', 'LANG', 'LC_ALL'].filter(k => process.env[k]).map(k => [k, process.env[k]]));
      const child = spawn(this.#binaryPath, [], { stdio: ['pipe', 'pipe', 'pipe'], env });
      this.#children.add(child);
      let output = '', diagnostics = '', settled = false;
      const finish = (err, data) => {
        if (settled) return; settled = true; clearTimeout(timer); this.#children.delete(child);
        if (err) reject(err); else resolve(data);
      };
      const timer = setTimeout(() => { child.kill('SIGKILL'); finish(error('Native helper timed out; do not retry pending writes without reading again', outcome)); }, 45000);
      child.on('error', e => finish(error(`Could not start native helper: ${e.message}`, 'notAttempted')));
      child.stdin.on('error', e => { if (e.code !== 'EPIPE') finish(error(`Native helper input failed: ${e.message}`, outcome)); });
      child.stdout.on('data', chunk => {
        output += chunk;
        if (Buffer.byteLength(output) > 16 * 1024 * 1024) { child.kill('SIGKILL'); finish(error('Native helper output exceeded limit', outcome)); }
      });
      child.stderr.on('data', chunk => { if (diagnostics.length < 4096) diagnostics += chunk; });
      child.on('close', (code, signal) => {
        if (settled) return;
        if (this.#closed || generation !== this.#generation) return finish(error('Native operation cancelled; refresh before retrying changes', outcome));
        let response;
        try { response = JSON.parse(output.trim()); } catch {
          return finish(error(`Native helper exited without a valid result (${signal || code}); ${operation === 'apply' ? 'write outcome may be unknown' : 'Steam client may be unavailable or its private ABI changed'}`, outcome));
        }
        if (!response.ok) return finish(error(response.error || 'Native helper failed', response.mutationOutcome || outcome));
        if (code !== 0) return finish(error('Native helper returned an unsuccessful exit status', outcome));
        finish(null, response.data);
      });
      try { child.stdin.end(JSON.stringify(request)); } catch (e) { child.kill('SIGKILL'); finish(error(e.message, outcome)); }
    });
  }
  read({ appId: id }) {
    appId(id);
    return this.#serial(async generation => {
      this.#emit('native.status', { state: 'reading', appId: id });
      try {
        const snapshot = snapshotValid(await this.#invoke('read', { appId: id }, generation), id);
        if (this.#closed || generation !== this.#generation) throw error('Native read was cancelled before delivery');
        this.#previews.set(id, clone(snapshot));
        this.#emit('native.status', { state: 'ready', appId: id });
        return snapshot;
      } catch (e) { this.#emit('native.status', { state: 'error', appId: id, error: e.message }); throw e; }
    });
  }
  apply({ appId: id, expectedAccountId, changes }) {
    appId(id); accountId(expectedAccountId);
    if (!changes || !Array.isArray(changes.achievements ?? []) || !Array.isArray(changes.stats ?? [])) throw new TypeError('Invalid changes');
    const copied = clone(changes);
    return this.#serial(async generation => {
      const preview = this.#previews.get(id);
      if (!preview || preview.accountId !== expectedAccountId) throw error('Read this game on the expected account before applying changes');
      for (const kind of ['achievements', 'stats']) for (const change of copied[kind] || []) {
        const row = preview[kind].find(row => row.id === change.id);
        if (change.before === undefined && row) change.before = row[kind === 'achievements' ? 'unlocked' : 'value'];
      }
      this.#previews.delete(id); // A preview authorizes one attempt; unknown outcomes cannot silently replay it.
      this.#emit('native.status', { state: 'applying', appId: id });
      let staged;
      try {
        staged = await this.#invoke('apply', {
          appId: id, expectedAccountId, changes: copied,
          expectedLibrarySha256: preview.capabilities?.librarySha256,
        }, generation);
        if (!staged || staged.appId !== id || staged.accountId !== expectedAccountId || !Array.isArray(staged.items)) {
          throw error('Native write response identity does not match this attempt', 'unknown');
        }
        if (this.#fixture && staged.fixtureState) this.#fixture = staged.fixtureState;
        delete staged.fixtureState;
        let readback, readbackError;
        try {
          let fixtureOverride;
          if (this.#fixture?.behavior?.readbackFailure) fixtureOverride = { ...this.#fixture, behavior: { ...this.#fixture.behavior, readFailure: true } };
          readback = snapshotValid(await this.#invoke('read', { appId: id, expectedLibrarySha256: preview.capabilities?.librarySha256 }, generation, fixtureOverride), id);
        } catch (e) { readbackError = e.message; }
        const result = reconcileNativeReadback(staged, readback, readbackError);
        if (this.#closed || generation !== this.#generation) {
          throw Object.assign(error('Native attempt cancelled after staging; refresh before retrying', 'unknown'), { operationResult: result });
        }
        this.#emit('native.result', result);
        this.#emit('native.status', { state: 'ready', appId: id });
        return result;
      } catch (e) { this.#emit('native.status', { state: 'error', appId: id, error: e.message, mutationOutcome: e.mutationOutcome || 'unknown' }); throw e; }
    });
  }
  close() {
    this.#closed = true; this.#generation += 1; this.#previews.clear();
    for (const child of this.#children) child.kill('SIGKILL');
    return this.#tail;
  }
}
