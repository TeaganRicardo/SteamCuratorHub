import { randomUUID } from 'node:crypto';
// Explicit demonstration mode: synthetic state, no Steam/network/client-library access.
export class DemoSteamService {
  constructor({ onEvent } = {}) {
    this.onEvent = onEvent; this._runs = []; this._generation = 0; this._closed = false;
    this._status = this._connectedStatus();
  }
  _connectedStatus() { return { state: 'connected', steamId: '76561198000000000', displayName: '演示账号', generation: this._generation, fixture: true }; }
  status() { return structuredClone(this._status); }
  _emit(type, data) { try { this.onEvent?.({ type, data: structuredClone(data) }); } catch { /* Observers do not own the synthetic session. */ } }
  _emitRuns() { this._emit('steam.runs', this.runs()); }
  _check(generation = this._generation) {
    if (this._closed) throw new Error('service_closed');
    if (generation !== this._generation) throw new Error('account_changed');
    if (this._status.state !== 'connected') throw new Error('steam_not_connected');
  }
  _endRuns(state, reason) {
    for (const run of this._runs) if (run.state === 'running') {
      run.state = state; run.reason = reason; run.stoppedAt = new Date().toISOString(); run.stopStatus = 'fixture_only';
    }
    this._emitRuns();
  }
  _disconnect(state, reason, runState = 'stopped') {
    this._endRuns(runState, reason); // Persist terminal evidence before clearing the live list.
    this._generation++; this._runs = []; this._emitRuns();
    this._status = { state, generation: this._generation, fixture: true, ...(state === 'error' ? { error: reason } : {}) };
    this._emit('steam.status', this._status); return this.status();
  }
  async login() {
    if (this._closed) throw new Error('service_closed');
    this._endRuns('stopped', 'account_changed'); this._generation++; this._runs = []; this._emitRuns();
    this._status = this._connectedStatus(); this._emit('steam.status', this._status); return this.status();
  }
  submitGuard() { this._check(); return this.status(); }
  async logout() { return this._closed ? this.status() : this._disconnect('logged_out', 'logout'); }
  _games() { return [
    { appId: 1001, name: '示例：星海旅人', steamMinutes: 72 },
    { appId: 1002, name: '示例：钟楼之下', steamMinutes: 12 },
    { appId: 1003, name: '示例：远方来信', steamMinutes: 0 }
  ]; }
  async ownedGames() { this._check(); return this._games(); }
  async searchStore(query) { return this._games().filter(g => g.name.includes(query)); }
  async appDetails(appId) { return { appId, name: this._games().find(g => g.appId === appId)?.name ?? '示例游戏',
    description: '这是可交互的合成数据，用于检查工作流。它不表示已取得 Steam 实机结果。', developers: ['Example studio'], platforms: { windows: true, mac: false }, releaseDate: '示例日期', storeUrl: `https://store.steampowered.com/app/${appId}/` }; }
  runs() { return structuredClone(this._runs); }
  async startRun(value) {
    this._check(); const generation = this._generation;
    if (this._runs.some(r => r.appId === value.appId && r.state === 'running')) throw new Error('already_running');
    const game = (await this.ownedGames()).find(g => g.appId === value.appId);
    this._check(generation); if (!game) throw new Error('game_not_owned');
    if (this._runs.some(r => r.appId === value.appId && r.state === 'running')) throw new Error('already_running');
    const reached = value.totalTargetMinutes != null && game.steamMinutes >= value.totalTargetMinutes;
    const now = new Date().toISOString();
    const run = { ...value, id: randomUUID(), name: game.name, generation, steamId: this.status().steamId,
      startedAt: reached ? null : now, stoppedAt: reached ? now : null, state: reached ? 'stopped' : 'running', steamMinutes: game.steamMinutes,
      localElapsedSeconds: 0, localElapsedMinutes: 0, baselineSteamMinutes: game.steamMinutes,
      deadline: !reached && value.stopAfterMinutes ? new Date(Date.now() + value.stopAfterMinutes * 60000).toISOString() : null,
      reason: reached ? 'total_target_already_reached' : null, requestStatus: reached ? 'not_requested' : 'fixture_only',
      stopStatus: reached ? 'not_requested' : null, readbackStatus: 'fixture_only', fixture: true };
    this._runs = this._runs.filter(r => r.appId !== value.appId); this._runs.push(run);
    this._emitRuns(); return structuredClone(run);
  }
  async stopRun(appId) {
    const run = this._runs.find(r => r.appId === appId); if (!run) throw new Error('run_not_found');
    if (run.state === 'running') { run.state = 'stopped'; run.stoppedAt = new Date().toISOString(); run.reason = 'user_stopped'; run.stopStatus = 'fixture_only'; }
    this._emitRuns(); return structuredClone(run);
  }
  async suspend() { return this._closed ? this.status() : this._disconnect('error', 'system_sleep', 'paused'); }
  async close() { if (this._closed) return this.status(); this._closed = true; return this._disconnect('closed', 'service_closed'); }
}
export class DemoResearchService {
  async research({ appId, name, requestId }) {
    const content = `这是 ${name} 的演示资料片段，描述探索、节奏和叙事。此来源是合成样本，不是已读取的媒体文章。`;
    return { appId, requestId, sources: [{ id: 'fixture-source', title: '演示来源', url: 'https://example.com/review',
      kind: 'fixture', status: 'fixture_only', content, identity: 'synthetic' }], reviews: [{ review: '示例评论：探索很有趣。', language: 'schinese' }],
      summary: '合成资料显示该示例围绕探索与叙事展开。', draft: '本篇为演示草稿。示例作品以探索为主线，叙事和节奏可以作为评测的两个切入点。此段不代表作者游玩经历。',
      warnings: ['fixture_only：未检索或调用 AI'], usage: { calls: [] } };
  }
  async translate({ text }) { return { text: `[演示翻译] ${text}` }; }
  async shorten({ text }) { return { text: Array.from(text).slice(0, 150).join('') }; }
  async format({ text }) { return { text: `[h2]评测[/h2]\n${text}` }; }
}
