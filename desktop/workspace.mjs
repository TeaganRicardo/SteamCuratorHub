import { createHash, randomUUID } from 'node:crypto';

export const LANGUAGES = ['schinese', 'english'];
const RECOMMENDATIONS = ['recommended', 'not_recommended', 'informational'];
const MAX_WORKSPACE_BYTES = 20_000_000;
const MAX_RUN_HISTORY = 10000;
const now = () => new Date().toISOString();
const clone = value => structuredClone(value);
export function appId(value) {
  if (!Number.isInteger(value) || value <= 0 || value > 0xFFFFFFFF) throw new Error('invalid_app_id');
  return value;
}
export function steamId(value) {
  if (typeof value !== 'string' || !/^7656119\d{10}$/.test(value)) throw new Error('invalid_steam_id');
  return value;
}
function text(value, max = 100000) {
  if (typeof value !== 'string' || value.length > max || value.includes('\0')) throw new Error('invalid_text');
  return value;
}
function language(value) {
  if (!LANGUAGES.includes(value)) throw new Error('invalid_language');
  return value;
}
export function publicHttps(value, allowedHosts) {
  if (typeof value !== 'string' || value.length > 2000) throw new Error('invalid_url');
  let url;
  try { url = new URL(value); } catch { throw new Error('invalid_url'); }
  if (url.protocol !== 'https:' || url.username || url.password || url.port ||
      !url.hostname.includes('.') || /^\d+\.\d+\.\d+\.\d+$/.test(url.hostname) ||
      url.hostname.includes(':') || /(?:^|\.)(?:localhost|local|internal|test|invalid)$/.test(url.hostname) ||
      (allowedHosts && !allowedHosts.includes(url.hostname))) throw new Error('invalid_url');
  return url.href;
}
export function validateFullReviewUrl(value, ownerSteamId, id) {
  steamId(ownerSteamId); appId(id);
  if (value === '') return '';
  let url;
  try { url = new URL(publicHttps(value, ['steamcommunity.com'])); }
  catch { throw new Error('long_review_result_url_mismatch'); }
  if (url.pathname.startsWith('/id/')) throw new Error('full_review_url_requires_numeric_profile');
  const path = `/profiles/${ownerSteamId}/recommended/${id}`;
  if (url.search || url.hash || url.pathname !== path && url.pathname !== `${path}/`) {
    throw new Error('long_review_result_url_mismatch');
  }
  return `https://steamcommunity.com${path}/`;
}
export function shortCount(value) { return Array.from(value).length; }
export function validateShort(value) {
  text(value, 10000);
  if (!value.trim()) throw new Error('empty_short_review');
  if (shortCount(value) >= 200) throw new Error('short_review_must_be_under_200');
  if (/https?:\/\//i.test(value) || /\[\/?(?:url|b|i|u|h[123]|list|quote|code|spoiler|table)(?:[=\]])/i.test(value)) {
    throw new Error('short_review_plain_text_only');
  }
  return value;
}
function optionalMinutes(value) {
  if (value === null || value === undefined || value === '') return null;
  if (typeof value !== 'number' || !Number.isFinite(value) || value < 0 || value > 10_000_000) throw new Error('invalid_minutes');
  return value;
}
function defaultDraft() {
  return { long: { text: '', revision: 0 }, short: { text: '', revision: 0 },
    fullReviewUrl: '', recommendation: 'recommended', revision: 0 };
}
function workspaceSize(value) {
  if (Buffer.byteLength(JSON.stringify(value, null, 2) + '\n') > MAX_WORKSPACE_BYTES) throw new Error('workspace_too_large');
}
function runDate(value) {
  if (value === undefined || value === null) return null;
  if (typeof value !== 'string' || value.length > 50 || !Number.isFinite(Date.parse(value))) throw new Error('invalid_run_date');
  return new Date(value).toISOString();
}
function runCode(value) {
  if (value === undefined || value === null) return null;
  if (typeof value !== 'string' || !/^[a-z][a-z0-9_]{0,99}$/.test(value)) throw new Error('invalid_run_status');
  return value;
}
function runRecord(run, { recover = false, importing = false } = {}) {
  if (!run || typeof run !== 'object' || Array.isArray(run) || typeof run.id !== 'string' || !/^[\w-]{1,100}$/.test(run.id)) {
    throw new Error('invalid_run');
  }
  const ownerSteamId = steamId(run.ownerSteamId ?? run.steamId);
  if (run.steamId !== undefined && steamId(run.steamId) !== ownerSteamId) throw new Error('run_identity_changed');
  if (!['running', 'stopped', 'paused', 'unknown', 'outcome_unknown'].includes(run.state)) throw new Error('invalid_run_state');
  const seconds = run.localElapsedSeconds ?? (run.localElapsedMinutes === undefined ? 0 : run.localElapsedMinutes * 60);
  const minutes = run.localElapsedMinutes ?? seconds / 60;
  if (typeof seconds !== 'number' || !Number.isFinite(seconds) || seconds < 0 || seconds > 600_000_000 ||
      typeof minutes !== 'number' || !Number.isFinite(minutes) || minutes < 0 || minutes > 10_000_000 ||
      Math.abs(seconds - minutes * 60) > 1) throw new Error('invalid_local_elapsed');
  if (run.generation !== undefined && (!Number.isSafeInteger(run.generation) || run.generation < 0)) throw new Error('invalid_run_generation');
  const interrupted = recover && run.state === 'running';
  // An imported record is historical evidence with unverified provenance. A
  // local timer never changes the independently observed Steam minute fields.
  const imported = importing || run.imported === true;
  return {
    id: run.id, ownerSteamId, appId: appId(run.appId), name: text(run.name ?? '', 300),
    state: interrupted ? 'outcome_unknown' : run.state, generation: run.generation ?? 0,
    startedAt: runDate(run.startedAt), stoppedAt: runDate(run.stoppedAt), deadline: runDate(run.deadline),
    localElapsedSeconds: seconds, localElapsedMinutes: minutes, fixture: run.fixture === true,
    elapsedSource: run.fixture === true ? 'fixture' : 'local_monotonic_timer',
    baselineSteamMinutes: optionalMinutes(run.baselineSteamMinutes), steamMinutes: optionalMinutes(run.steamMinutes),
    lastReadbackAt: runDate(run.lastReadbackAt), readbackStatus: runCode(run.readbackStatus),
    totalTargetMinutes: optionalMinutes(run.totalTargetMinutes), stopAfterMinutes: optionalMinutes(run.stopAfterMinutes),
    reason: interrupted ? 'process_ended_before_stop_confirmation' : runCode(run.reason),
    requestStatus: runCode(run.requestStatus), stopStatus: interrupted ? 'cleanup_unknown' : runCode(run.stopStatus),
    recordedAt: runDate(run.recordedAt) ?? now(), updatedAt: runDate(run.updatedAt) ?? now(),
    imported, recordSource: imported ? 'imported_backup' : run.fixture === true ? 'fixture' : 'steam_service',
  };
}
function defaults() {
  return { version: 1, activeSteamId: null, tasks: [], runHistory: [], groups: [
    { id: '45134494', name: '浮生评测', footers: { schinese: '[url=https://store.steampowered.com/curator/45134494/]浮生评测[/url]', english: '[url=https://store.steampowered.com/curator/45134494/]Curator group[/url]' } },
    { id: '45525570', name: '桃你欢心', footers: { schinese: '[url=https://store.steampowered.com/curator/45525570/]桃你欢心[/url]', english: '[url=https://store.steampowered.com/curator/45525570/]Curator group[/url]' } }
  ], settings: { aiBaseUrl: 'https://generativelanguage.googleapis.com/v1beta', aiModel: '', articleLanguage: 'schinese',
    hideHiddenAchievements: true, maxSources: 4, maxOutputTokens: 4000 } };
}

function sanitizeRecord(value, depth = 0) {
  if (depth > 30) throw new Error('backup_structure_too_deep');
  if (Array.isArray(value)) return value.map(item => sanitizeRecord(item, depth + 1));
  if (value && typeof value === 'object') return Object.fromEntries(Object.entries(value)
    .filter(([key]) => !/^(?:password|passphrase|api[_-]?key|aiKey|webApiKey|searchKey|searchApiKey|secret|clientSecret|authorization|cookie|cookies|set-cookie|refreshToken|accessToken|authToken|machineAuthToken|session|steamSession|requestHeaders|headers)$/i.test(key))
    .map(([key, item]) => [key, sanitizeRecord(item, depth + 1)]));
  return value;
}

// Normalize imports through explicit fields. Backups have no arbitrary class objects or secret slots.
export function normalizeWorkspace(input, { importing = true } = {}) {
  workspaceSize(input);
  if (!input || input.version !== 1 || !Array.isArray(input.tasks) || input.tasks.length > 2000 ||
      !Array.isArray(input.groups) || input.groups.length > 100) throw new Error('invalid_backup');
  const state = defaults();
  state.activeSteamId = input.activeSteamId ? steamId(input.activeSteamId) : null;
  if (input.runHistory !== undefined && (!Array.isArray(input.runHistory) || input.runHistory.length > MAX_RUN_HISTORY)) {
    throw new Error('invalid_run_history');
  }
  state.runHistory = (input.runHistory ?? []).map(run => runRecord(run, { recover: true, importing }));
  if (new Set(state.runHistory.map(run => run.id)).size !== state.runHistory.length) throw new Error('duplicate_run');
  state.groups = input.groups.map(group => {
    if (typeof group.id !== 'string' || !/^[1-9]\d{0,19}$/.test(group.id)) throw new Error('invalid_group');
    return { id: group.id, name: text(group.name, 100), footers: Object.fromEntries(LANGUAGES.map(lang =>
      [lang, text(group.footers?.[lang] ?? '', 10000)])) };
  });
  if (new Set(state.groups.map(g => g.id)).size !== state.groups.length) throw new Error('duplicate_group');
  state.tasks = input.tasks.map(task => {
    if (typeof task.id !== 'string' || !/^[\w-]{1,100}$/.test(task.id)) throw new Error('invalid_task');
    const drafts = {};
    for (const lang of LANGUAGES) {
      const source = task.drafts?.[lang] ?? defaultDraft();
      drafts[lang] = defaultDraft();
      for (const kind of ['long', 'short']) drafts[lang][kind] = {
        text: text(source[kind]?.text ?? ''), revision: Number.isSafeInteger(source[kind]?.revision) && source[kind].revision >= 0 ? source[kind].revision : 0
      };
      drafts[lang].fullReviewUrl = source.fullReviewUrl ? publicHttps(source.fullReviewUrl) : '';
      // Legacy URLs remain visible for repair; only publication boundaries
      // require a canonical review belonging to this exact account and AppID.
      try { drafts[lang].fullReviewUrl = validateFullReviewUrl(drafts[lang].fullReviewUrl, task.ownerSteamId, task.appId); }
      catch { drafts[lang].fullReviewUrlNeedsRepair = true; }
      drafts[lang].recommendation = RECOMMENDATIONS.includes(source.recommendation) ? source.recommendation : 'recommended';
      drafts[lang].revision = Number.isSafeInteger(source.revision) && source.revision >= 0 ? source.revision : 0;
    }
    if (!state.groups.some(g => g.id === task.groupId)) throw new Error('unknown_group');
    // Research and publication evidence are preserved as bounded JSON, never executed or trusted as verified imports.
    const research = task.research ? sanitizeRecord(JSON.parse(JSON.stringify(task.research))) : null;
    if (JSON.stringify(research).length > 2_000_000) throw new Error('research_too_large');
    const publications = (Array.isArray(task.publications) ? task.publications : []).slice(-100).map(attempt => ({
      ...sanitizeRecord(JSON.parse(JSON.stringify(attempt))), imported: true,
      state: ['verified', 'user_confirmed'].includes(attempt.state) ? 'imported_unverified' : 'submitted_unknown'
    }));
    const operations = (Array.isArray(task.operations) ? task.operations : []).slice(-100).map(operation => {
      const saved = sanitizeRecord(JSON.parse(JSON.stringify(operation)));
      if (saved.state === 'applying') saved.state = 'outcome_unknown';
      if (importing) {
        saved.imported = true; saved.evidenceSource = 'imported_backup';
        if (saved.state === 'verified') saved.state = 'imported_unverified';
        if (saved.result) {
          saved.result.imported = true;
          if (saved.result.status === 'verified') saved.result.status = 'imported_unverified';
          if (saved.result.verification) saved.result.verification = { ...saved.result.verification,
            scope: 'imported-unverified', steamVerified: false, imported: true };
          if (Array.isArray(saved.result.items)) saved.result.items = saved.result.items.map(item => item.status === 'verified' ? { ...item, status: 'imported_unverified' } : item);
        }
      }
      return saved;
    });
    return { id: task.id, appId: appId(task.appId), title: text(task.title, 300), ownerSteamId: steamId(task.ownerSteamId),
      groupId: task.groupId, source: text(task.source ?? '', 1000), targetMinutes: optionalMinutes(task.targetMinutes),
      status: ['active', 'completed', 'archived'].includes(task.status) ? task.status : 'active',
      createdAt: text(task.createdAt ?? now(), 50), updatedAt: text(task.updatedAt ?? now(), 50), drafts, research, publications, operations };
  });
  if (new Set(state.tasks.map(t => t.id)).size !== state.tasks.length ||
      new Set(state.tasks.map(t => `${t.ownerSteamId}:${t.appId}`)).size !== state.tasks.length) throw new Error('duplicate_task');
  const config = input.settings ?? {};
  state.settings.aiBaseUrl = config.aiBaseUrl ? publicHttps(config.aiBaseUrl) : state.settings.aiBaseUrl;
  state.settings.aiModel = text(config.aiModel ?? '', 200);
  state.settings.articleLanguage = LANGUAGES.includes(config.articleLanguage) ? config.articleLanguage : 'schinese';
  state.settings.hideHiddenAchievements = config.hideHiddenAchievements !== false;
  state.settings.maxSources = Math.min(6, Math.max(1, Math.floor(Number(config.maxSources) || 4)));
  state.settings.maxOutputTokens = Math.min(8000, Math.max(500, Math.floor(Number(config.maxOutputTokens) || 4000)));
  workspaceSize(state);
  return state;
}

export class Workspace {
  constructor(repository) {
    this.repository = repository;
    const loaded = repository.load();
    // Locally saved evidence retains its origin; importing a backup deliberately downgrades it.
    this.state = loaded ? normalizeWorkspace(loaded, { importing: false }) : defaults();
    if (loaded) for (const task of this.state.tasks) {
      const original = loaded.tasks.find(t => t.id === task.id);
      task.publications = original.publications ?? [];
    }
  }
  commit(update) {
    const next = clone(this.state);
    const result = update(next);
    workspaceSize(next);
    this.repository.save(next);
    this.state = next;
    return clone(result ?? this.snapshot());
  }
  snapshot() {
    return { ...clone(this.state), tasks: clone(this.state.tasks.filter(task => task.ownerSteamId === this.state.activeSteamId)),
      runHistory: clone(this.state.runHistory.filter(run => run.ownerSteamId === this.state.activeSteamId)) };
  }
  task(id) {
    const task = this.state.tasks.find(t => t.id === id && t.ownerSteamId === this.state.activeSteamId);
    if (!task) throw new Error('task_not_found');
    return clone(task);
  }
  bindAccount(id) { return this.commit(state => { state.activeSteamId = steamId(id); }); }
  createTask({ appId: id, title, groupId, source = '', targetMinutes = null }) {
    appId(id); text(title, 300); text(source, 1000); optionalMinutes(targetMinutes);
    if (!this.state.activeSteamId) throw new Error('bind_account_first');
    if (!this.state.groups.some(g => g.id === groupId)) throw new Error('unknown_group');
    if (this.state.tasks.some(t => t.appId === id && t.ownerSteamId === this.state.activeSteamId)) throw new Error('task_already_exists');
    return this.commit(state => {
      const task = { id: randomUUID(), appId: id, title, groupId, source, targetMinutes, ownerSteamId: state.activeSteamId,
        status: 'active', createdAt: now(), updatedAt: now(), drafts: Object.fromEntries(LANGUAGES.map(lang => [lang, defaultDraft()])),
        research: null, publications: [], operations: [] };
      state.tasks.push(task); return task;
    });
  }
  updateTask({ taskId, groupId, source, targetMinutes, status }) {
    this.task(taskId);
    return this.commit(state => {
      const task = state.tasks.find(t => t.id === taskId);
      if (groupId !== undefined) {
        if (!state.groups.some(g => g.id === groupId)) throw new Error('unknown_group');
        task.groupId = groupId;
      }
      if (source !== undefined) task.source = text(source, 1000);
      if (targetMinutes !== undefined) task.targetMinutes = optionalMinutes(targetMinutes);
      if (status !== undefined) {
        if (!['active', 'completed', 'archived'].includes(status)) throw new Error('invalid_task_status');
        task.status = status;
      }
      task.updatedAt = now(); return task;
    });
  }
  saveDraft({ taskId, language: lang, kind, text: value, expectedRevision }) {
    this.task(taskId); language(lang); text(value);
    if (!['long', 'short'].includes(kind)) throw new Error('invalid_draft_kind');
    return this.commit(state => {
      const task = state.tasks.find(t => t.id === taskId);
      const draft = task.drafts[lang][kind];
      if (draft.revision !== expectedRevision) throw new Error('draft_changed');
      draft.text = value; draft.revision++; task.drafts[lang].revision++; task.updatedAt = now(); return task;
    });
  }
  saveDraftOptions({ taskId, language: lang, fullReviewUrl, recommendation }) {
    const current = this.task(taskId); language(lang);
    if (!RECOMMENDATIONS.includes(recommendation)) throw new Error('invalid_recommendation');
    const url = validateFullReviewUrl(fullReviewUrl, current.ownerSteamId, current.appId);
    return this.commit(state => {
      const task = state.tasks.find(t => t.id === taskId);
      Object.assign(task.drafts[lang], { fullReviewUrl: url, fullReviewUrlNeedsRepair: false, recommendation });
      task.drafts[lang].revision++; task.updatedAt = now(); return task;
    });
  }
  saveResearch(taskId, result, expectedAccount) {
    this.task(taskId);
    if (this.state.activeSteamId !== expectedAccount || result.appId !== this.task(taskId).appId) throw new Error('research_context_changed');
    const safe = sanitizeRecord(JSON.parse(JSON.stringify(result)));
    if (JSON.stringify(safe).length > 2_000_000) throw new Error('research_too_large');
    return this.commit(state => {
      const task = state.tasks.find(t => t.id === taskId); task.research = safe; task.updatedAt = now(); return task;
    });
  }
  updateGroups(groups) {
    const normalized = normalizeWorkspace({ ...this.state, groups }).groups;
    return this.commit(state => { state.groups = normalized; });
  }
  updateSettings(settings) {
    const normalized = normalizeWorkspace({ ...this.state, settings: { ...this.state.settings, ...settings } }).settings;
    return this.commit(state => { state.settings = normalized; });
  }
  content(taskId, lang, kind) {
    const task = this.task(taskId); language(lang);
    const draft = task.drafts[lang];
    if (kind === 'short') return { text: validateShort(draft.short.text),
      fullReviewUrl: validateFullReviewUrl(draft.fullReviewUrl, task.ownerSteamId, task.appId), recommendation: draft.recommendation };
    if (kind !== 'long' || !draft.long.text.trim()) throw new Error('empty_long_review');
    const footer = this.state.groups.find(g => g.id === task.groupId)?.footers[lang]?.trim() ?? '';
    let body = draft.long.text.trim();
    const knownFooters = this.state.groups.flatMap(group => LANGUAGES.map(code => group.footers[code]?.trim()).filter(Boolean));
    let matched;
    do {
      matched = knownFooters.find(candidate => body.endsWith(candidate));
      if (matched) body = body.slice(0, -matched.length).trim();
    } while (matched);
    if (!body) throw new Error('empty_long_review');
    return { text: footer ? `${body}\n\n${footer}` : body, fullReviewUrl: '', recommendation: draft.recommendation };
  }
  publicationSnapshot(taskId, lang, kind) {
    const task = this.task(taskId); const content = this.content(taskId, lang, kind);
    const value = { taskId, ownerSteamId: task.ownerSteamId, appId: task.appId, groupId: task.groupId,
      language: lang, kind, draftRevision: task.drafts[lang].revision, ...content };
    return { ...value, digest: createHash('sha256').update(JSON.stringify(value)).digest('hex') };
  }
  preparePublication(taskId, lang, kind) {
    const snapshot = this.publicationSnapshot(taskId, lang, kind);
    const task = this.task(taskId);
    if (task.publications.some(p => p.kind === kind && p.language === lang && ['submitted_unknown'].includes(p.state))) {
      throw new Error('previous_publication_needs_verification');
    }
    return this.commit(state => {
      const attempt = { ...snapshot, id: randomUUID(), state: 'prepared', createdAt: now(), updatedAt: now(), resultUrl: '', evidenceSource: '' };
      state.tasks.find(t => t.id === taskId).publications.push(attempt); return attempt;
    });
  }
  assertCurrentAttempt(taskId, attemptId) {
    const attempt = this.task(taskId).publications.find(p => p.id === attemptId);
    if (!attempt || attempt.imported || attempt.digest !== this.publicationSnapshot(taskId, attempt.language, attempt.kind).digest) throw new Error('publication_changed');
    return attempt;
  }
  recordPublication({ taskId, attemptId, state: value, resultUrl = '', evidenceSource = '' }) {
    const task = this.task(taskId);
    const attempt = task.publications.find(p => p.id === attemptId);
    if (!attempt || !['filled', 'submitted_unknown', 'user_confirmed', 'verified', 'failed'].includes(value)) throw new Error('invalid_publication');
    if (['verified', 'user_confirmed'].includes(value) && !resultUrl) throw new Error('result_url_required');
    const url = resultUrl ? publicHttps(resultUrl, ['steamcommunity.com', 'store.steampowered.com']) : '';
    if (value === 'verified' && evidenceSource !== 'steam_api_readback') throw new Error('verification_evidence_required');
    return this.commit(state => {
      const saved = state.tasks.find(t => t.id === taskId).publications.find(p => p.id === attemptId);
      Object.assign(saved, { state: value, resultUrl: url, evidenceSource: text(evidenceSource, 100), updatedAt: now() }); return saved;
    });
  }
  recordOperation(taskId, ownerSteamId, operation) {
    const saved = sanitizeRecord(JSON.parse(JSON.stringify(operation)));
    if (JSON.stringify(saved).length > 2_000_000) throw new Error('operation_too_large');
    return this.commit(state => {
      const task = state.tasks.find(t => t.id === taskId && t.ownerSteamId === ownerSteamId);
      if (!task) throw new Error('operation_context_missing');
      task.operations ??= [];
      const index = task.operations.findIndex(item => item.id === saved.id);
      if (index < 0) task.operations.push(saved); else task.operations[index] = saved;
      task.operations = task.operations.slice(-100); return saved;
    });
  }
  recordRun(run) { return this.recordRuns([run])[0]; }
  recordRuns(runs) {
    if (!Array.isArray(runs) || runs.length > MAX_RUN_HISTORY) throw new Error('invalid_run_history');
    const records = runs.map(run => runRecord(run));
    if (new Set(records.map(run => run.id)).size !== records.length) throw new Error('duplicate_run');
    if (!records.length) return [];
    return this.commit(state => {
      const indexes = new Map(state.runHistory.map((item, index) => [item.id, index]));
      for (const saved of records) {
        const index = indexes.get(saved.id);
        if (index === undefined) {
          if (state.runHistory.length >= MAX_RUN_HISTORY) throw new Error('run_history_limit');
          indexes.set(saved.id, state.runHistory.length); state.runHistory.push(saved);
        } else {
          const previous = state.runHistory[index];
          if (previous.ownerSteamId !== saved.ownerSteamId || previous.appId !== saved.appId || previous.generation !== saved.generation || previous.fixture !== saved.fixture) {
            throw new Error('run_identity_changed');
          }
          if (previous.state !== 'running' && saved.state === 'running') throw new Error('run_history_cannot_resume');
          saved.recordedAt = previous.recordedAt;
          saved.updatedAt = now();
          saved.imported = previous.imported;
          saved.recordSource = previous.recordSource;
          state.runHistory[index] = saved;
        }
      }
      return records;
    });
  }
  exportBackup() { return clone(this.state); }
  importBackup(value) {
    const normalized = normalizeWorkspace(value);
    this.repository.save(normalized); this.state = normalized; return this.snapshot();
  }
}
