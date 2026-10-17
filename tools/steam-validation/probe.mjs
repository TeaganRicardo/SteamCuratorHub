import { setTimeout as sleep } from 'node:timers/promises';

export class ProbeError extends Error {
  constructor(code) { super(code); this.code = code; }
}

export function validatePlan(plan) {
  if (plan?.version !== 1 || typeof plan.expectedSteamId !== 'string' || !/^7656119\d{10}$/.test(plan.expectedSteamId)) {
    throw new ProbeError('invalid_plan_identity');
  }
  if (!Array.isArray(plan.targets) || plan.targets.length !== 2) {
    throw new ProbeError('two_targets_required');
  }
  const targets = plan.targets.map(target => {
    if (!Number.isInteger(target.appId) || target.appId <= 0 || target.appId > 0xFFFFFFFF ||
        !Number.isInteger(target.stopAfterSeconds) || target.stopAfterSeconds < 60 ||
        target.stopAfterSeconds > 3600 || typeof target.uninstalled !== 'boolean' ||
        typeof target.windowsOnly !== 'boolean') throw new ProbeError('invalid_target');
    return { appId: target.appId, stopAfterSeconds: target.stopAfterSeconds,
      uninstalled: target.uninstalled, windowsOnly: target.windowsOnly };
  }).sort((a, b) => a.stopAfterSeconds - b.stopAfterSeconds);
  if (targets[0].appId === targets[1].appId ||
      !targets.some(t => t.uninstalled && t.windowsOnly)) throw new ProbeError('invalid_target_coverage');
  const readbackDelaySeconds = plan.readbackDelaySeconds ?? 30;
  if (!Number.isInteger(readbackDelaySeconds) || readbackDelaySeconds < 1 ||
      readbackDelaySeconds > 120 ||
      targets[1].stopAfterSeconds - targets[0].stopAfterSeconds <= readbackDelaySeconds + 15) {
    throw new ProbeError('invalid_schedule');
  }
  return { version: 1, expectedSteamId: plan.expectedSteamId, targets, readbackDelaySeconds };
}

export function playtimeSnapshot(apps, targets) {
  if (!Array.isArray(apps)) throw new ProbeError('invalid_snapshot');
  return targets.map(({ appId }) => {
    const matches = apps.filter(app => app.appid === appId);
    const minutes = matches[0]?.playtime_forever;
    if (matches.length !== 1 || !Number.isSafeInteger(minutes) || minutes < 0) {
      throw new ProbeError('missing_or_invalid_playtime');
    }
    return { appId, steamMinutes: minutes };
  });
}

export function comparePlaytime(before, after, mode) {
  return before.map(initial => {
    const final = after?.find(item => item.appId === initial.appId);
    const deltaMinutes = final ? final.steamMinutes - initial.steamMinutes : null;
    return { appId: initial.appId, beforeMinutes: initial.steamMinutes,
      afterMinutes: final?.steamMinutes ?? null, deltaMinutes,
      status: mode === 'fixture' ? 'fixture_only' : deltaMinutes === null ? 'unavailable' :
        deltaMinutes > 0 ? 'increase_observed' : deltaMinutes === 0 ? 'no_increase_observed' : 'inconsistent' };
  });
}

// Wall-clock gaps include system sleep/event-loop stalls. Never count the gap as proof of Steam time.
export async function waitChecked(ms, signal, { now = Date.now, sleepFn = sleep } = {}) {
  const deadline = now() + ms;
  let remaining = ms;
  while (remaining > 0) {
    signal.throwIfAborted();
    const chunk = Math.min(1000, remaining);
    const before = now();
    await sleepFn(chunk, undefined, { signal });
    const elapsed = now() - before;
    if (elapsed < 0 || elapsed > chunk + 5000) throw new ProbeError('clock_gap');
    remaining = deadline - now();
  }
  signal.throwIfAborted();
}

export function createReport(plan, mode, environment = {}) {
  return { version: 1, mode, environment, startedAt: new Date().toISOString(),
    state: 'not_started', targets: plan.targets, targetEvidenceSource: 'operator_assertion',
    events: [], snapshots: [], observations: [],
    acceptance: 'pending_user_verification', achievementsAndStats: 'not_tested' };
}

export async function runProbe({ plan, transport, report, signal, save = () => {},
  wait = waitChecked, now = Date.now }) {
  const started = now();
  const event = (type, details = {}) => {
    report.events.push({ type, elapsedMs: now() - started, ...details });
    try { save(report); }
    catch { report.persistence = 'failed'; throw new ProbeError('evidence_save_failed'); }
  };
  let requested = [];
  const request = appIds => {
    signal.throwIfAborted();
    transport.assertReady();
    // gamesPlayed has no server acknowledgement. Record only the local request.
    transport.setPlaying(appIds);
    requested = [...appIds];
    event('playing_requested', { appIds: [...appIds], acknowledgement: 'unavailable' });
  };
  const snapshot = async phase => {
    signal.throwIfAborted();
    const readStarted = now();
    const apps = await transport.readOwnedApps(signal);
    signal.throwIfAborted();
    const readElapsed = now() - readStarted;
    // The library request timeout is 10s; a larger gap means the deadline was not observable.
    if (readElapsed < 0 || readElapsed > 15000) throw new ProbeError('clock_gap');
    const values = playtimeSnapshot(apps, plan.targets);
    report.snapshots.push({ phase, elapsedMs: now() - started, values });
    event('playtime_read', { phase });
    return values;
  };
  try {
    report.state = 'preflight';
    transport.assertReady();
    const before = await snapshot('before');
    request(plan.targets.map(t => t.appId));
    report.state = 'running';
    const runStarted = now();
    for (const target of plan.targets) {
      const stopAt = runStarted + target.stopAfterSeconds * 1000;
      await wait(Math.max(0, stopAt - now()), signal);
      if (now() - stopAt > 5000) throw new ProbeError('clock_gap');
      request(requested.filter(id => id !== target.appId));
      await wait(plan.readbackDelaySeconds * 1000, signal);
      await snapshot(`after_stop_${target.appId}`);
    }
    report.observations = comparePlaytime(before, report.snapshots.at(-1).values, report.mode);
    report.state = 'collected';
    event('collection_completed');
  } catch (error) {
    report.state = 'interrupted';
    report.failure = error instanceof ProbeError ? error.code :
      signal.aborted && signal.reason instanceof ProbeError ? signal.reason.code : 'probe_failed';
    try { event('collection_interrupted', { code: report.failure }); } catch {}
  } finally {
    // Even preflight failure may follow a previous request. Clear this session, then log off.
    try {
      transport.setPlaying([]);
      report.cleanupClear = 'requested_without_acknowledgement';
    } catch {
      report.cleanupClear = 'request_failed';
    }
    try {
      await transport.close();
      report.cleanup = 'logged_off';
    } catch {
      report.cleanup = 'unknown';
    }
    try { event('cleanup_completed', { state: report.cleanup }); } catch {}
  }
  return report;
}
