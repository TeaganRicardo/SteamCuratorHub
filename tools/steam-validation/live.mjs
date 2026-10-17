import { randomInt } from 'node:crypto';
import { ProbeError } from './probe.mjs';

// Only loaded for an explicit --live invocation. No stored auth tokens, cookies or local Steam files.
export async function createLiveTransport({ plan, signal, askSecret, askAccount, onFault,
  SteamUserClass }) {
  const SteamUser = SteamUserClass ?? (await import('steam-user')).default;
  const user = new SteamUser({ dataDirectory: null, autoRelogin: false, saveAppTickets: false,
    renewRefreshTokens: false });
  let closing = false;
  let disconnected = false;
  let fault = null;
  let rejectLogin;
  const fail = code => {
    if (closing) return;
    fault ??= new ProbeError(code);
    rejectLogin?.(fault);
    onFault(fault);
  };
  user.on('error', () => fail('steam_error'));
  user.on('disconnected', () => {
    disconnected = true;
    fail('steam_disconnected');
  });
  user.on('playingState', blocked => { if (blocked) fail('playing_elsewhere'); });
  user.on('steamGuard', (_domain, callback) => {
    askSecret('Steam Guard code: ', signal).then(code => {
      if (!signal.aborted && !closing) callback(code.trim());
    }).catch(() => fail('guard_cancelled'));
  });
  const abortLogin = () => rejectLogin?.(signal.reason ?? new ProbeError('cancelled'));
  signal.addEventListener('abort', abortLogin, { once: true });
  const close = async () => {
    if (closing) return;
    closing = true;
    signal.removeEventListener('abort', abortLogin);
    const ended = new Promise(resolve => {
      if (disconnected) return resolve();
      let timer;
      const done = () => { clearTimeout(timer); user.off('disconnected', done); resolve(); };
      user.once('disconnected', done);
      timer = setTimeout(done, 5000);
    });
    user.logOff();
    await ended;
    if (!disconnected) throw new ProbeError('logout_unconfirmed');
  };
  try {
    const accountName = await askAccount('Steam account name: ', signal);
    const password = await askSecret('Steam password: ', signal);
    signal.throwIfAborted();
    await new Promise((resolve, reject) => {
      rejectLogin = reject;
      const timer = setTimeout(() => reject(new ProbeError('login_timeout')), 120000);
      user.once('loggedOn', () => { clearTimeout(timer); resolve(); });
      // Clear timer on login rejection as well.
      const failLogin = rejectLogin;
      rejectLogin = error => { clearTimeout(timer); failLogin(error); };
      try {
        user.logOn({ accountName, password, logonID: randomInt(1, 0x7FFFFFFF),
          machineName: 'SteamCuratorHub validation' });
      } catch { rejectLogin(new ProbeError('login_failed')); }
    });
    rejectLogin = null;
    signal.throwIfAborted();
    if (user.steamID?.getSteamID64() !== plan.expectedSteamId) throw new ProbeError('account_mismatch');
    const assertReady = () => {
      signal.throwIfAborted();
      if (fault) throw fault;
      if (!user.steamID) throw new ProbeError('not_connected');
      if (user.playingState?.blocked) throw new ProbeError('playing_elsewhere');
    };
    return {
      assertReady,
      setPlaying(appIds) {
        if (appIds.length) assertReady();
        if (user.steamID) user.gamesPlayed(appIds, false);
      },
      async readOwnedApps(abortSignal) {
        assertReady();
        const response = await abortable(user.getUserOwnedApps(user.steamID, {
          filterAppids: plan.targets.map(t => t.appId), includeAppInfo: false,
          includePlayedFreeGames: true, skipUnvettedApps: false
        }), abortSignal);
        return response.apps;
      },
      close
    };
  } catch (error) {
    try { await close(); } catch { /* Login failed; no play request was issued. */ }
    throw error instanceof ProbeError ? error : new ProbeError('login_failed');
  }
}

async function abortable(promise, signal) {
  signal.throwIfAborted();
  let onAbort;
  const aborted = new Promise((_, reject) => {
    onAbort = () => reject(signal.reason ?? new ProbeError('cancelled'));
    signal.addEventListener('abort', onAbort, { once: true });
  });
  try { return await Promise.race([promise, aborted]); }
  finally { signal.removeEventListener('abort', onAbort); }
}
