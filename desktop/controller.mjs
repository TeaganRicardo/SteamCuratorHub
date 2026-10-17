import { randomUUID } from 'node:crypto';
import { appId, steamId, normalizeWorkspace } from './workspace.mjs';

export class AppController {
  constructor({ workspace, steam, native, research, secrets, publishing, onEvent, fixture = false }) {
    Object.assign(this, { workspace, steam, native, research, secrets, publishing, onEvent, fixture });
    this.epoch = 0; this.researchRequests = new Map(); this.previews = new Map(); this.lastGeneration = null; this.nativeOperations = 0; this.maintenance = false; this.inFlight = new Map();
    this.runCheckpoints = new Map();
  }
  snapshot() { return { workspace: this.workspace.snapshot(), steam: this.steam.status(), runs: this.steam.runs(),
    credentials: this.secrets.status(), fixture: this.fixture, version: '0.1.0' }; }
  changed() { this.onEvent?.({ type: 'workspace.changed' }); }
  async steamEvent(event) {
    if (event.type === 'steam.runs' && this.workspace.recordRuns) {
      const checkpoints = event.data.filter(run => {
        const fingerprint = JSON.stringify([run.state, run.reason, run.stopStatus, run.readbackStatus, run.steamMinutes,
          run.lastReadbackAt, Math.floor((run.localElapsedSeconds ?? 0) / 30)]);
        if (this.runCheckpoints.get(run.id) === fingerprint) return false;
        return true;
      });
      if (checkpoints.length) {
        this.workspace.recordRuns(checkpoints);
        for (const run of checkpoints) this.runCheckpoints.set(run.id, JSON.stringify([run.state, run.reason, run.stopStatus,
          run.readbackStatus, run.steamMinutes, run.lastReadbackAt, Math.floor((run.localElapsedSeconds ?? 0) / 30)]));
      }
    }
    if (event.type === 'steam.status' && event.data.generation !== this.lastGeneration) {
      this.lastGeneration = event.data.generation; this.epoch++;
      for (const request of this.researchRequests.values()) request.abort(); this.researchRequests.clear(); this.previews.clear();
      this.publishing.logout().catch(() => {});
    }
    if (event.type === 'steam.status' && event.data.state === 'connected' && event.data.steamId &&
        this.workspace.state.activeSteamId !== event.data.steamId) {
      this.workspace.bindAccount(event.data.steamId); this.changed();
    }
    this.onEvent?.(event);
  }
  taskContext(taskId) { return { task: this.workspace.task(taskId), epoch: this.epoch, owner: this.workspace.state.activeSteamId }; }
  assertContext(context) {
    if (context.epoch !== this.epoch || context.owner !== this.workspace.state.activeSteamId ||
        this.workspace.task(context.task.id).appId !== context.task.appId) throw new Error('operation_context_changed');
  }
  assertNativeAvailable() {
    if (this.steam.runs().some(run => run.state === 'running') || [...this.inFlight.values()].includes('runs.start')) throw new Error('stop_runs_before_native_stats');
    if (this.nativeOperations) throw new Error('native_operation_in_progress');
  }
  async withNative(operation) {
    this.assertNativeAvailable();
    this.nativeOperations++;
    try { return await operation(); } finally { this.nativeOperations--; }
  }
  aiSettings() {
    const settings = this.workspace.state.settings;
    return { apiKey: this.secrets.read('aiKey', settings.aiBaseUrl), searchApiKey: this.secrets.read('searchKey'), baseUrl: settings.aiBaseUrl, model: settings.aiModel,
      maxSources: settings.maxSources, maxOutputTokens: settings.maxOutputTokens };
  }
  assertIdle() {
    if ([...this.inFlight.values()].some(command => command !== 'init') || this.nativeOperations ||
        this.researchRequests.size || this.steam.runs().some(run => run.state === 'running') ||
        this.workspace.state.tasks.some(task => task.operations?.some(operation => operation.state === 'applying'))) {
      throw new Error('stop_operations_before_import');
    }
  }
  acquireMaintenance() {
    if (this.maintenance) throw new Error('workspace_maintenance_in_progress');
    this.assertIdle(); this.maintenance = true; this.epoch++; this.previews.clear();
    return () => { this.maintenance = false; };
  }
  async invoke(command, payload = {}) {
    if (this.maintenance && command !== 'init') throw new Error('workspace_maintenance_in_progress');
    const ticket = Symbol(command); this.inFlight.set(ticket, command);
    try { return await this.dispatch(command, payload); } finally { this.inFlight.delete(ticket); }
  }
  async dispatch(command, payload = {}) {
    if (!payload || typeof payload !== 'object' || Array.isArray(payload) || JSON.stringify(payload).length > 2_000_000) throw new Error('invalid_request');
    switch (command) {
      case 'init': return this.snapshot();
      case 'account.bind': {
        const id = steamId(payload.steamId);
        if (this.steam.status().state === 'connected' && this.steam.status().steamId !== id) throw new Error('logout_before_binding_another_account');
        const bindingEpoch = ++this.epoch; this.previews.clear();
        for (const request of this.researchRequests.values()) request.abort();
        await this.publishing.logout();
        if (bindingEpoch !== this.epoch) throw new Error('operation_context_changed');
        if (this.steam.status().state === 'connected' && this.steam.status().steamId !== id) throw new Error('logout_before_binding_another_account');
        this.workspace.bindAccount(id); this.changed(); return this.snapshot();
      }
      case 'steam.login': return this.steam.login(payload);
      case 'steam.guard': return this.steam.submitGuard(payload.code);
      case 'steam.logout': {
        this.epoch++; for (const request of this.researchRequests.values()) request.abort(); this.previews.clear();
        const result = await this.steam.logout(); await this.publishing.logout(); this.changed(); return result;
      }
      case 'steam.library': return this.steam.ownedGames();
      case 'steam.search': return this.steam.searchStore(payload.query);
      case 'steam.details': return this.steam.appDetails(appId(payload.appId));
      case 'task.create': { const result = this.workspace.createTask(payload); this.changed(); return result; }
      case 'task.update': { const result = this.workspace.updateTask(payload); this.changed(); return result; }
      case 'draft.save': { const result = this.workspace.saveDraft(payload); this.changed(); return result; }
      case 'draft.options': { const result = this.workspace.saveDraftOptions(payload); this.changed(); return result; }
      case 'runs.start': {
        if (this.nativeOperations) throw new Error('native_operation_in_progress');
        const context = this.taskContext(payload.taskId);
        if (this.steam.status().steamId !== context.owner) throw new Error('steam_account_mismatch');
        const result = await this.steam.startRun({ appId: context.task.appId, totalTargetMinutes: context.task.targetMinutes,
          stopAfterMinutes: payload.stopAfterMinutes });
        this.assertContext(context); return result;
      }
      case 'runs.stop': return this.steam.stopRun(appId(payload.appId));
      case 'stats.read': {
        const context = this.taskContext(payload.taskId);
        const result = await this.withNative(() => this.native.read({ appId: context.task.appId })); this.assertContext(context);
        if (result.accountId !== context.owner) throw new Error('native_steam_account_mismatch');
        return result;
      }
      case 'stats.preview': {
        const context = this.taskContext(payload.taskId);
        if (context.task.operations?.some(op => ['applying', 'outcome_unknown'].includes(op.state))) throw new Error('native_unknown_result_needs_acknowledgement');
        const result = await this.withNative(() => this.native.read({ appId: context.task.appId })); this.assertContext(context);
        if (result.accountId !== context.owner) throw new Error('native_steam_account_mismatch');
        if (this.workspace.task(context.task.id).operations?.some(op => ['applying', 'outcome_unknown'].includes(op.state))) throw new Error('native_unknown_result_needs_acknowledgement');
        const changes = { achievements: [], stats: [] };
        if (!payload.changes || !Array.isArray(payload.changes.achievements) || !Array.isArray(payload.changes.stats) ||
            payload.changes.achievements.length + payload.changes.stats.length > 1000) throw new Error('invalid_changes');
        for (const item of payload.changes.achievements) {
          const row = result.achievements.find(a => a.id === item.id);
          if (!row || typeof item.unlocked !== 'boolean') throw new Error('invalid_achievement_change');
          changes.achievements.push({ id: row.id, unlocked: item.unlocked, before: row.unlocked });
        }
        for (const item of payload.changes.stats) {
          const row = result.stats.find(a => a.id === item.id);
          if (!row || !row.writable || !Number.isFinite(item.value) || (row.type === 'int' && !Number.isInteger(item.value))) throw new Error('invalid_stat_change');
          changes.stats.push({ id: row.id, type: row.type, value: item.value, before: row.value });
        }
        if (!changes.achievements.length && !changes.stats.length) throw new Error('no_changes_selected');
        const preview = { id: randomUUID(), taskId: context.task.id, owner: context.owner, appId: context.task.appId,
          epoch: context.epoch, changes, state: 'prepared', createdAt: new Date().toISOString() };
        this.previews.set(preview.id, preview); return preview;
      }
      case 'stats.apply': {
        const context = this.taskContext(payload.taskId);
        this.assertNativeAvailable();
        if (context.task.operations?.some(op => ['applying', 'outcome_unknown'].includes(op.state))) throw new Error('native_unknown_result_needs_acknowledgement');
        const preview = this.previews.get(payload.previewId);
        if (!preview || preview.taskId !== context.task.id || preview.owner !== context.owner || preview.epoch !== this.epoch) throw new Error('stats_preview_expired');
        for (const [id, candidate] of this.previews) if (candidate.taskId === context.task.id) this.previews.delete(id);
        const operation = { ...preview, state: 'applying', updatedAt: new Date().toISOString() };
        this.workspace.recordOperation(context.task.id, context.owner, operation); this.changed();
        try {
          const result = await this.withNative(() => this.native.apply({ appId: context.task.appId, expectedAccountId: context.owner, changes: preview.changes }));
          operation.result = result;
          operation.state = result.status === 'unknown' ? 'outcome_unknown' : result.status;
          operation.updatedAt = new Date().toISOString();
          this.workspace.recordOperation(context.task.id, context.owner, operation); this.changed();
          return result;
        } catch (error) {
          operation.state = error.mutationOutcome === 'notAttempted' ? 'failed' : 'outcome_unknown';
          operation.failure = operation.state; operation.updatedAt = new Date().toISOString();
          this.workspace.recordOperation(context.task.id, context.owner, operation); this.changed(); throw error;
        }
      }
      case 'stats.acknowledge': {
        const context = this.taskContext(payload.taskId);
        const operation = context.task.operations?.find(op => op.id === payload.operationId);
        if (!operation || operation.state !== 'outcome_unknown') throw new Error('unknown_operation_not_found');
        const readback = await this.withNative(() => this.native.read({ appId: context.task.appId })); this.assertContext(context);
        if (readback.accountId !== context.owner) throw new Error('native_steam_account_mismatch');
        this.workspace.recordOperation(context.task.id, context.owner, { ...operation, state: 'operator_acknowledged',
          acknowledgement: 'User requested fresh readback and acknowledged the uncertain prior result.', readback,
          updatedAt: new Date().toISOString() }); this.changed(); return readback;
      }
      case 'research.cancel':
        for (const [id, controller] of this.researchRequests) if (id === payload.taskId || id.startsWith(`${payload.taskId}:`)) controller.abort();
        return { cancelled: true };
      case 'research.run': {
        const context = this.taskContext(payload.taskId);
        const language = payload.language === 'english' ? 'english' : 'schinese';
        const baseRevision = context.task.drafts[language].long.revision;
        this.researchRequests.get(context.task.id)?.abort();
        const controller = new AbortController(); this.researchRequests.set(context.task.id, controller);
        try {
          const collected = await this.research.research({ appId: context.task.appId, name: context.task.title,
            language, requestId: randomUUID() }, this.aiSettings(), controller.signal);
          controller.signal.throwIfAborted(); this.assertContext(context);
          const result = { ...collected, origin: { taskId: context.task.id, ownerSteamId: context.owner, language, baseRevision } };
          this.workspace.saveResearch(context.task.id, result, context.owner); this.changed(); return result;
        } finally { if (this.researchRequests.get(context.task.id) === controller) this.researchRequests.delete(context.task.id); }
      }
      case 'ai.translate': case 'ai.format': case 'ai.shorten': {
        const context = this.taskContext(payload.taskId);
        const controller = new AbortController();
        const id = `${context.task.id}:${command}`; this.researchRequests.get(id)?.abort(); this.researchRequests.set(id, controller);
        try {
          if (typeof payload.text !== 'string' || payload.text.length > 100000) throw new Error('invalid_text');
          const method = command.split('.')[1];
          const result = await this.research[method]({ text: payload.text, language: payload.language }, this.aiSettings(), controller.signal);
          controller.signal.throwIfAborted(); this.assertContext(context);
          return { ...result, taskId: context.task.id, baseRevision: payload.baseRevision, kind: method === 'shorten' ? 'short' : payload.kind };
        } finally { if (this.researchRequests.get(id) === controller) this.researchRequests.delete(id); }
      }
      case 'publication.prepare': return this.workspace.preparePublication(payload.taskId, payload.language, payload.kind);
      case 'publication.open': return this.publishing.open(this.workspace.assertCurrentAttempt(payload.taskId, payload.attemptId));
      case 'publication.inspect': return this.publishing.fill(payload.taskId, payload.attemptId, true);
      case 'publication.fill': { const result = await this.publishing.fill(payload.taskId, payload.attemptId); this.changed(); return result; }
      case 'publication.record': {
        if (payload.state === 'verified') throw new Error('automatic_evidence_required');
        const result = this.workspace.recordPublication({ ...payload, evidenceSource: 'user_confirmation' }); this.changed(); return result;
      }
      case 'publication.verify': { const result = await this.publishing.verifyLong(payload.taskId, payload.attemptId, payload.resultUrl); this.changed(); return result; }
      case 'publication.copy': return this.workspace.assertCurrentAttempt(payload.taskId, payload.attemptId);
      case 'settings.save': {
        const normalized = normalizeWorkspace({ ...this.workspace.exportBackup(),
          settings: { ...this.workspace.state.settings, ...(payload.settings ?? {}) },
          groups: payload.groups ?? this.workspace.state.groups });
        const changes = Object.fromEntries(['aiKey', 'webApiKey', 'searchKey'].filter(key => payload.secrets?.[key] !== undefined).map(key => [key, payload.secrets[key]]));
        if (normalized.settings.aiBaseUrl !== this.workspace.state.settings.aiBaseUrl &&
            changes.aiKey === undefined && this.secrets.status().aiKeyConfigured) throw new Error('new_endpoint_requires_key');
        // Validate everything first. Credentials are bound to the target origin before config can change.
        if (this.secrets.patch) this.secrets.patch(changes, payload.persistSecrets !== false, normalized.settings.aiBaseUrl);
        else for (const [key, value] of Object.entries(changes)) this.secrets.write(key, value, payload.persistSecrets !== false, normalized.settings.aiBaseUrl);
        this.workspace.commit(state => { state.settings = normalized.settings; state.groups = normalized.groups; });
        this.changed(); return this.snapshot();
      }
      default: throw new Error('unknown_command');
    }
  }
  async close() {
    this.epoch++; for (const controller of this.researchRequests.values()) controller.abort(); this.previews.clear();
    await Promise.allSettled([this.steam.close(), this.native.close(), this.publishing.close()]);
  }
}
