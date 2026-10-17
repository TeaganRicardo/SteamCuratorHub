// Offline DOM integration only: executes the shipped renderer against a fake
// preload bridge and in-memory Workspace. No Electron, Steam, network or visuals.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import { parseHTML } from 'linkedom';
import { Workspace } from '../workspace.mjs';

const html = await readFile(new URL('../renderer/index.html', import.meta.url), 'utf8');
const appSource = await readFile(new URL('../renderer/app.mjs', import.meta.url), 'utf8');
const bbcodeSource = await readFile(new URL('../renderer/bbcode.mjs', import.meta.url), 'utf8');
const owner = '76561198000000000';
const copy = value => structuredClone(value);
const settle = () => new Promise(resolve => setImmediate(resolve));
function deferred() { let resolve, reject; const promise = new Promise((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
function nativeSnapshot(appId) {
  return { appId, accountId: owner, source: 'fixture', capabilities: { privateAbiLocallyVerified: false },
    achievements: [{ id: `ACH_${appId}`, name: `Achievement ${appId}`, description: 'Synthetic', unlocked: false, hidden: false, writable: true, globalPercent: 5 }],
    stats: [{ id: `COUNT_${appId}`, type: 'int', value: 1, writable: true }] };
}
function fakeTimers() {
  let sequence = 0; const scheduled = new Map();
  return {
    setTimeout(fn, ms) { const id = ++sequence; scheduled.set(id, { fn, ms }); return id; },
    clearTimeout(id) { scheduled.delete(id); },
    async fire(ms) {
      const due = [...scheduled].filter(([, timer]) => timer.ms <= ms);
      for (const [id, timer] of due) { scheduled.delete(id); await timer.fn(); }
      await settle();
    },
    clear() { scheduled.clear(); },
  };
}
async function harness({ configure, handlers = {} } = {}) {
  const repository = { load: () => null, save() {} };
  const workspace = new Workspace(repository); workspace.bindAccount(owner);
  const a = workspace.createTask({ appId: 1001, title: 'Alpha <img src=x onerror=alert(1)>', groupId: '45134494' });
  const b = workspace.createTask({ appId: 1002, title: 'Beta', groupId: '45525570' });
  configure?.(workspace, { a, b });
  const { window, document } = parseHTML(html);
  // linkedom intentionally omits these browser APIs. Polyfill browser semantics
  // in the harness, without changing app source or injecting test-only hooks.
  Object.defineProperty(window.HTMLSelectElement.prototype, 'value', {
    configurable: true,
    get() { return this.querySelector('option[selected]')?.getAttribute('value') ?? this.querySelector('option')?.getAttribute('value') ?? ''; },
    set(value) { for (const option of this.querySelectorAll('option')) option.toggleAttribute('selected', option.getAttribute('value') === String(value)); },
  });
  window.HTMLTextAreaElement.prototype.setRangeText = function(text, start, end) {
    this.value = this.value.slice(0, start) + text + this.value.slice(end);
    this.selectionStart = start; this.selectionEnd = start + text.length;
  };
  const modal = document.querySelector('#modal');
  modal.showModal = () => { modal.open = true; modal.setAttribute('open', ''); };
  modal.close = () => { modal.open = false; modal.removeAttribute('open'); };
  let closeCount = 0; window.close = () => { closeCount++; };
  const calls = [], events = new Set(), timers = fakeTimers();
  let steam = { state: 'connected', steamId: owner, displayName: 'Synthetic account' };
  const emit = event => { for (const listener of events) listener(copy(event)); };
  const changed = () => queueMicrotask(() => emit({ type: 'workspace.changed' }));
  const snapshot = () => ({ fixture: true, workspace: workspace.snapshot(), steam: copy(steam), runs: [],
    credentials: { encryptionAvailable: false, aiKeyConfigured: true, searchKeyConfigured: true, webApiKeyConfigured: false } });
  const defaults = async (command, payload) => {
    switch (command) {
      case 'init': return snapshot();
      case 'steam.library': return workspace.snapshot().tasks.map(t => ({ appId: t.appId, name: t.title, steamMinutes: 20 }));
      case 'steam.details': return { appId: payload.appId, name: 'Synthetic details', description: '<script>not executable</script>', platforms: { windows: true }, developers: ['Fixture'] };
      case 'steam.search': return [{ appId: 1003, name: 'Gamma', steamMinutes: null }];
      case 'task.update': { const result = workspace.updateTask(payload); changed(); return result; }
      case 'task.create': { const result = workspace.createTask(payload); changed(); return result; }
      case 'draft.save': { const result = workspace.saveDraft(payload); changed(); return result; }
      case 'draft.options': { const result = workspace.saveDraftOptions(payload); changed(); return result; }
      case 'stats.read': case 'stats.acknowledge': return nativeSnapshot(workspace.task(payload.taskId).appId);
      case 'stats.preview': {
        const task = workspace.task(payload.taskId);
        return { id: 'preview', changes: { achievements: payload.changes.achievements.map(row => ({ ...row, before: false })),
          stats: payload.changes.stats.map(row => ({ ...row, type: 'int', before: 1 })) }, taskId: task.id };
      }
      case 'stats.apply': return { status: 'verified', source: 'fixture', readback: nativeSnapshot(workspace.task(payload.taskId).appId), items: [] };
      case 'ai.format': case 'ai.translate': case 'ai.shorten': return { text: `SUGGESTION ${payload.taskId} ${payload.language}` };
      case 'research.run': {
        const task = workspace.task(payload.taskId);
        const result = { appId: task.appId, requestId: 'research', draft: `RESEARCH ${payload.language}`, summary: 'Fixture summary', sources: [], reviews: [], warnings: [], language: payload.language,
          origin: { taskId: task.id, ownerSteamId: task.ownerSteamId, language: payload.language, baseRevision: task.drafts[payload.language].long.revision } };
        workspace.saveResearch(task.id, result, owner); changed(); return result;
      }
      case 'research.cancel': return { cancelled: true };
      case 'publication.prepare': { const result = workspace.preparePublication(payload.taskId, payload.language, payload.kind); changed(); return result; }
      case 'clipboard.write': return { copied: true };
      case 'external.open': return { opened: true };
      case 'settings.save': { workspace.updateSettings(payload.settings || {}); changed(); return snapshot(); }
      case 'backup.export': case 'backup.import': return { cancelled: true };
      case 'app.close-ready': return { closing: payload.allow };
      case 'app.request-close': queueMicrotask(() => emit({ type: 'app.requestClose' })); return { requested: true };
      case 'steam.login': return { state: 'logging_in' };
      case 'steam.logout': steam = { state: 'logged_out' }; emit({ type: 'steam.status', data: steam }); return steam;
      case 'steam.guard': return { accepted: true };
      default: throw new Error(`Unexpected fake command: ${command}`);
    }
  };
  window.hub = {
    async invoke(command, payload = {}) {
      calls.push({ command, payload: copy(payload) });
      const result = await (handlers[command] ? handlers[command](payload, { workspace, a, b, defaults, changed, snapshot }) : defaults(command, payload));
      return copy(result);
    },
    onEvent(fn) { events.add(fn); return () => events.delete(fn); },
  };
  const context = vm.createContext({ window, document, Node: window.Node, Event: window.Event, URL,
    setTimeout: timers.setTimeout, clearTimeout: timers.clearTimeout, console });
  // Execute the actual sources in one isolated browser realm. Only static ESM
  // import/export syntax is adapted; no functions or guards are rewritten.
  const script = `${bbcodeSource.replace(/^export /gm, '')}\n${appSource.replace(/^import .*from '\.\/bbcode\.mjs';\s*/m, '')}`;
  await vm.runInContext(`(async () => {\n${script}\n})()`, context, { filename: 'shipped-renderer.mjs' });
  await settle();
  function findButton(label, root = document) {
    return [...root.querySelectorAll('button')].find(node => node.textContent.trim() === label);
  }
  async function click(label, root = document) {
    const node = typeof label === 'string' ? findButton(label, root) : label;
    assert.ok(node, `Missing button: ${label}`); assert.notEqual(node.disabled, true, `Disabled button: ${label}`);
    node.dispatchEvent(new window.Event('click', { bubbles: true })); await settle(); return node;
  }
  async function nav(name) { await click(document.querySelector(`[data-tab="${name}"]`)); }
  async function input(selector, value, event = 'input') {
    const node = typeof selector === 'string' ? document.querySelector(selector) : selector;
    assert.ok(node, `Missing field ${selector}`); node.value = value;
    node.dispatchEvent(new window.Event(event, { bubbles: true })); await settle(); return node;
  }
  async function choose(id) { await input('#task-picker', id, 'change'); }
  async function lang(value) { await input('#language-picker', value, 'change'); }
  return { window, document, workspace, a, b, calls, timers, emit, click, nav, input, choose, lang, findButton,
    closeCount: () => closeCount, dispose: () => { timers.clear(); events.clear(); } };
}

test('actual renderer exposes seven usable pages and task context across navigation', async t => {
  const h = await harness(); t.after(h.dispose);
  const expected = { library: '游戏库', tasks: '评测任务', runs: '运行与时长', achievements: '成就与统计', research: '资料研究', editor: '评测编辑', settings: '设置' };
  assert.equal(h.document.querySelectorAll('[data-tab]').length, 7);
  for (const [tab, title] of Object.entries(expected)) {
    await h.nav(tab); assert.equal(h.document.querySelector('#main h1')?.textContent, title);
    assert.equal(h.document.querySelector('#task-picker').value, h.a.id);
  }
  assert.equal(h.document.querySelector('#main img'), null, 'game titles are text, never HTML');
});

test('saved run history shows only active owner and retains its synthetic evidence label', async t => {
  const h = await harness({ configure(workspace) {
    const run = { id: 'historical-fixture', steamId: owner, appId: 1001, name: 'Owned <img src=x onerror=alert(1)>',
      state: 'stopped', generation: 0, fixture: true, localElapsedSeconds: 600, localElapsedMinutes: 10,
      steamMinutes: 120, baselineSteamMinutes: 120, readbackStatus: 'fixture_only', reason: 'user_stopped',
      startedAt: '2026-10-17T10:00:00.000Z', stoppedAt: '2026-10-17T10:10:00.000Z', stopStatus: 'fixture_only' };
    workspace.recordRuns([run, { ...run, id: 'another-owner', steamId: '76561198000000001', name: 'Other account private title' }]);
  } }); t.after(h.dispose);
  await h.nav('runs');
  const content = h.document.querySelector('#main');
  assert.match(content.textContent, /已保存的运行历史/); assert.match(content.textContent, /合成历史/);
  assert.match(content.textContent, /10 分钟/); assert.match(content.textContent, /2 小时/);
  assert.doesNotMatch(content.textContent, /Other account private title/); assert.equal(content.querySelector('img'), null);
});

test('editing autosaves to original task/language and navigation flushes remaining buffers', async t => {
  const h = await harness(); t.after(h.dispose); await h.nav('editor');
  await h.input('[aria-label="个人长评正文"]', 'A Chinese text'); await h.timers.fire(700);
  assert.equal(h.workspace.task(h.a.id).drafts.schinese.long.text, 'A Chinese text');
  await h.lang('english'); await h.input('[aria-label="个人长评正文"]', 'A English text');
  await h.choose(h.b.id); await h.input('[aria-label="个人长评正文"]', 'B English text'); await h.nav('tasks');
  assert.equal(h.workspace.task(h.a.id).drafts.english.long.text, 'A English text');
  assert.equal(h.workspace.task(h.b.id).drafts.english.long.text, 'B English text');
  assert.equal(h.workspace.task(h.b.id).drafts.schinese.long.text, '');
  assert.match(h.document.querySelector('#main').textContent, /Beta/);
});

test('language change retains its captured target across a save and status refresh', async t => {
  const save = deferred(); let pending, first = true;
  const h = await harness({ handlers: { 'draft.save': (payload, context) => {
    if (first) { first = false; pending = { payload, context }; return save.promise; }
    return context.defaults('draft.save', payload);
  } } }); t.after(h.dispose);
  await h.nav('editor'); await h.input('[aria-label="个人长评正文"]', 'Chinese before switch');
  await h.lang('english'); assert.ok(pending);
  h.emit({ type: 'steam.status', data: { state: 'connected', steamId: owner } });
  save.resolve(await pending.context.defaults('draft.save', pending.payload)); await settle(); await settle();
  assert.equal(h.document.querySelector('#language-picker').value, 'english', 'topbar must agree with the rendered editor language');
  assert.match(h.document.querySelector('#statusbar').textContent, /文章：English/);
  await h.input('[aria-label="个人长评正文"]', 'English after switch'); await h.click('立即保存');
  assert.equal(h.workspace.task(h.a.id).drafts.schinese.long.text, 'Chinese before switch');
  assert.equal(h.workspace.task(h.a.id).drafts.english.long.text, 'English after switch');
});

test('Unicode short counter includes spaces/newlines/combined codepoints while link stays separate', async t => {
  const h = await harness(); t.after(h.dispose); await h.nav('editor');
  const text = '中A ée\u0301 👩🏽‍💻\n';
  await h.input('[aria-label="鉴赏家短评正文"]', text);
  assert.match(h.document.querySelector('#main').textContent, new RegExp(`${Array.from(text).length} / 199 字`));
  await h.input('[aria-label="全文链接"]', `https://steamcommunity.com/profiles/${owner}/recommended/1001/`);
  await h.click('保存链接与推荐类型'); await h.click('立即保存');
  const draft = h.workspace.task(h.a.id).drafts.schinese;
  assert.equal(draft.short.text, text); assert.ok(draft.fullReviewUrl.includes('/1001/'));
  await h.input('[aria-label="鉴赏家短评正文"]', '字'.repeat(200)); await h.click('准备鉴赏家短评');
  assert.equal(h.workspace.task(h.a.id).publications.length, 0); assert.match(h.document.querySelector('#toast').textContent, /少于 200/);
});

test('BBCode preview treats HTML as text and only opens explicit HTTPS links', async t => {
  const h = await harness(); t.after(h.dispose); await h.nav('editor');
  const text = '<img src=x onerror="alert(1)"><script>alert(2)</script>[b]bold[/b][url=javascript:alert(3)]bad[/url][url=https://example.com/review]good[/url]';
  await h.input('[aria-label="个人长评正文"]', text);
  const preview = h.document.querySelector('.preview');
  assert.equal(preview.querySelector('img,script,iframe'), null); assert.match(preview.textContent, /<script>alert\(2\)<\/script>/);
  assert.equal(preview.querySelector('strong').textContent, 'bold');
  const links = [...preview.querySelectorAll('a')]; await h.click(links[0]); await h.click(links[1]);
  assert.deepEqual(h.calls.filter(c => c.command === 'external.open').map(c => c.payload.url), ['https://example.com/review']);
});

test('late AI suggestion belongs to its original task and language without overwriting drafts', async t => {
  const response = deferred();
  const h = await harness({ handlers: { 'ai.format': () => response.promise } }); t.after(h.dispose);
  await h.nav('editor'); await h.input('[aria-label="个人长评正文"]', 'original A'); await h.click('立即保存');
  await h.click('AI 格式处理'); await h.choose(h.b.id); await h.lang('english');
  response.resolve({ text: 'SUGGESTION FOR A CHINESE' }); await settle();
  assert.doesNotMatch(h.document.querySelector('#main').textContent, /SUGGESTION FOR A CHINESE/);
  assert.equal(h.workspace.task(h.a.id).drafts.schinese.long.text, 'original A');
  await h.choose(h.a.id); await h.lang('schinese');
  assert.match(h.document.querySelector('#main').textContent, /SUGGESTION FOR A CHINESE/);
  await h.click('将此建议保存到草稿');
  assert.equal(h.workspace.task(h.a.id).drafts.schinese.long.text, 'SUGGESTION FOR A CHINESE');
  assert.equal(h.workspace.task(h.b.id).drafts.english.long.text, '');
});

test('pending save and task switch cannot supply another task revision to AI operation', async t => {
  const save = deferred(); let pendingSave;
  const h = await harness({ configure(workspace, { b }) {
    for (let i = 0; i < 7; i++) workspace.saveDraft({ taskId: b.id, language: 'schinese', kind: 'long', text: `B ${i}`, expectedRevision: i });
  }, handlers: { 'draft.save': (payload, context) => { pendingSave = { payload, context }; return save.promise; } } }); t.after(h.dispose);
  await h.nav('editor'); await h.input('[aria-label="个人长评正文"]', 'new A'); await h.click('AI 格式处理');
  assert.ok(pendingSave); await h.choose(h.b.id);
  const saved = await pendingSave.context.defaults('draft.save', pendingSave.payload); save.resolve(saved); await settle(); await settle();
  assert.equal(h.calls.filter(call => call.command === 'ai.format').length, 0, 'a context changed before AI starts must cancel that operation');
});

test('late native refresh cannot erase another task snapshot or pending selection', async t => {
  const old = deferred(); let aReads = 0;
  const h = await harness({ handlers: { 'stats.read': (payload, { a, b }) => payload.taskId === a.id && aReads++ === 0 ? old.promise : nativeSnapshot(payload.taskId === a.id ? a.appId : b.appId) } }); t.after(h.dispose);
  await h.nav('achievements'); await h.click('读取 / 刷新 Steam 状态'); await h.choose(h.b.id);
  await h.click('读取 / 刷新 Steam 状态');
  const selected = h.document.querySelector('[aria-label="选择 ACH_1002"]'); assert.ok(selected); selected.checked = true;
  selected.dispatchEvent(new h.window.Event('change', { bubbles: true }));
  old.resolve(nativeSnapshot(1001)); await settle();
  assert.ok(h.document.querySelector('[aria-label="选择 ACH_1002"]'), 'late A refresh must preserve B table');
  assert.equal(h.document.querySelector('[aria-label="选择 ACH_1002"]').checked, true);
});

test('saved research of unknown origin cannot overwrite current language draft after reopen', async t => {
  const h = await harness({ configure(workspace, { a }) {
    workspace.saveDraft({ taskId: a.id, language: 'schinese', kind: 'long', text: 'User newer draft', expectedRevision: 0 });
    workspace.saveResearch(a.id, { appId: a.appId, draft: 'OLD RESEARCH SUGGESTION', sources: [], reviews: [], warnings: [] }, owner);
  } }); t.after(h.dispose);
  await h.nav('research'); await h.lang('english');
  const apply = h.findButton('将建议稿保存为当前语言长评');
  if (apply && !apply.disabled) await h.click(apply);
  assert.equal(h.workspace.task(h.a.id).drafts.english.long.text, '', 'unknown-source-language research cannot become current English text');
  await h.lang('schinese'); await h.nav('research');
  const old = h.findButton('将建议稿保存为当前语言长评'); if (old && !old.disabled) await h.click(old);
  assert.equal(h.workspace.task(h.a.id).drafts.schinese.long.text, 'User newer draft', 'unknown base revision must not overwrite newer user edits');
});

test('research with known origin applies only to its captured language and revision', async t => {
  const h = await harness(); t.after(h.dispose);
  await h.nav('editor'); await h.input('[aria-label="个人长评正文"]', 'Original Chinese'); await h.click('立即保存');
  await h.nav('research'); await h.click('搜集资料并生成长评初稿');
  await h.lang('english');
  assert.equal(h.findButton('将建议稿保存为当前语言长评'), undefined, 'Chinese research cannot apply through the English view');
  await h.lang('schinese'); await h.nav('editor'); await h.input('[aria-label="个人长评正文"]', 'Newer author revision'); await h.click('立即保存');
  await h.nav('research'); await h.click('将建议稿保存为当前语言长评');
  assert.equal(h.workspace.task(h.a.id).drafts.schinese.long.text, 'Newer author revision');
  assert.match(h.document.querySelector('#toast').textContent, /草稿已发生变化/);
  assert.equal(h.workspace.task(h.a.id).drafts.english.long.text, '');
  await h.click('搜集资料并生成长评初稿'); await h.click('将建议稿保存为当前语言长评');
  assert.equal(h.workspace.task(h.a.id).drafts.schinese.long.text, 'RESEARCH schinese', 'fresh matching-origin research remains usable');
  assert.equal(h.document.querySelector('#main h1').textContent, '评测编辑');
});

test('failed native items retain their selected changes for inspection', async t => {
  const h = await harness({ handlers: { 'stats.apply': () => ({ status: 'failed', source: 'fixture', readback: nativeSnapshot(1001),
    items: [{ kind: 'achievement', id: 'ACH_1001', status: 'failed', requested: true }] }) } }); t.after(h.dispose);
  await h.nav('achievements'); await h.click('读取 / 刷新 Steam 状态');
  const selected = h.document.querySelector('[aria-label="选择 ACH_1001"]'); selected.checked = true;
  selected.dispatchEvent(new h.window.Event('change', { bubbles: true }));
  await h.click('预览成就与统计更改'); await h.click('提交合成更改');
  assert.equal(h.document.querySelector('[aria-label="选择 ACH_1001"]').checked, true, 'failed change remains visible rather than losing unfinished intent');
});

test('late native apply cannot replace the newly selected task snapshot', async t => {
  const result = deferred();
  const h = await harness({ handlers: { 'stats.apply': () => result.promise } }); t.after(h.dispose);
  await h.nav('achievements'); await h.click('读取 / 刷新 Steam 状态');
  const selected = h.document.querySelector('[aria-label="选择 ACH_1001"]'); selected.checked = true;
  selected.dispatchEvent(new h.window.Event('change', { bubbles: true }));
  await h.click('预览成就与统计更改'); await h.click('提交合成更改');
  await h.choose(h.b.id); await h.click('读取 / 刷新 Steam 状态');
  result.resolve({ status: 'verified', readback: nativeSnapshot(1001), items: [] }); await settle(); await settle();
  assert.ok(h.document.querySelector('[aria-label="选择 ACH_1002"]'));
  assert.equal(h.document.querySelector('[aria-label="选择 ACH_1001"]'), null);
});

test('late native unknown-result acknowledgement cannot replace another task snapshot', async t => {
  const result = deferred();
  const h = await harness({ configure(workspace, { a }) {
    workspace.recordOperation(a.id, owner, { id: 'unknown-operation', state: 'outcome_unknown', changes: {} });
  }, handlers: { 'stats.acknowledge': () => result.promise } }); t.after(h.dispose);
  await h.nav('achievements'); await h.click('刷新并确认已检查未知结果');
  await h.choose(h.b.id); await h.click('读取 / 刷新 Steam 状态');
  result.resolve(nativeSnapshot(1001)); await settle(); await settle();
  assert.ok(h.document.querySelector('[aria-label="选择 ACH_1002"]'));
  assert.equal(h.document.querySelector('[aria-label="选择 ACH_1001"]'), null);
});

test('ordinary close handshake flushes pending text before allowing main to close', async t => {
  const h = await harness(); t.after(h.dispose); await h.nav('editor');
  await h.input('[aria-label="个人长评正文"]', 'must survive close');
  const event = new h.window.Event('beforeunload', { cancelable: true }); h.window.dispatchEvent(event); await settle();
  assert.equal(event.defaultPrevented, true);
  h.emit({ type: 'app.requestClose' }); await settle();
  assert.equal(h.workspace.task(h.a.id).drafts.schinese.long.text, 'must survive close');
  assert.deepEqual(h.calls.filter(c => c.command === 'app.close-ready').map(c => c.payload.allow), [true]);
  assert.ok(h.calls.findIndex(c => c.command === 'draft.save') < h.calls.findIndex(c => c.command === 'app.close-ready'));
  assert.equal(h.closeCount(), 0, 'renderer uses the main-process close handshake');
});

test('a conflicted unsaved draft must block close instead of silently discarding text', async t => {
  const h = await harness({ handlers: { 'draft.save': () => { throw Object.assign(new Error('draft_changed'), { code: 'draft_changed' }); } } }); t.after(h.dispose);
  await h.nav('editor'); await h.input('[aria-label="个人长评正文"]', 'unsaved conflict text'); await h.timers.fire(700);
  h.emit({ type: 'app.requestClose' }); await settle();
  assert.equal(h.closeCount(), 0, 'conflicted dirty buffer is not successfully flushed');
  assert.deepEqual(h.calls.filter(c => c.command === 'app.close-ready').map(c => c.payload.allow), [false]);
  assert.equal(h.document.querySelector('#modal').open, true);
  assert.match(h.document.querySelector('#modal').textContent, /unsaved conflict text/);
  await h.click('复制全部未保存文字');
  assert.match(h.calls.find(c => c.command === 'clipboard.write').payload.text, /unsaved conflict text/);
  assert.equal(h.document.querySelector('[aria-label="个人长评正文"]').value, 'unsaved conflict text');
  await h.click('放弃本次未保存修改并退出'); await settle();
  assert.deepEqual(h.calls.filter(c => c.command === 'app.close-ready').map(c => c.payload.allow), [false, true]);
  assert.equal(h.workspace.task(h.a.id).drafts.schinese.long.text, '', 'only explicit discard may lose the local conflict buffer');
});

test('autosave and close share one drain and persist edits made during an in-flight save', async t => {
  const save = deferred(); let pending, first = true;
  const h = await harness({ handlers: { 'draft.save': (payload, context) => {
    if (first) { first = false; pending = { payload, context }; return save.promise; }
    return context.defaults('draft.save', payload);
  } } }); t.after(h.dispose);
  await h.nav('editor'); await h.input('[aria-label="个人长评正文"]', 'first');
  const autosave = h.timers.fire(700); await settle(); assert.ok(pending);
  await h.input('[aria-label="个人长评正文"]', 'second while saving'); h.emit({ type: 'app.requestClose' }); await settle();
  assert.equal(h.calls.filter(c => c.command === 'draft.save').length, 1, 'close must await the existing save rather than issue a competing write');
  assert.equal(h.calls.filter(c => c.command === 'app.close-ready').length, 0);
  save.resolve(await pending.context.defaults('draft.save', pending.payload)); await autosave; await settle();
  assert.equal(h.workspace.task(h.a.id).drafts.schinese.long.text, 'second while saving');
  assert.deepEqual(h.calls.filter(c => c.command === 'draft.save').map(c => c.payload.expectedRevision), [0, 1]);
  assert.deepEqual(h.calls.filter(c => c.command === 'app.close-ready').map(c => c.payload.allow), [true]);
});

test('connection and Guard dialogs retain no password in their controls and render prompts as text', async t => {
  const h = await harness(); t.after(h.dispose);
  h.emit({ type: 'steam.status', data: { state: 'logged_out' } }); await h.click(h.document.querySelector('#account-button'));
  assert.equal(h.document.querySelector('#modal').open, true);
  const password = h.document.querySelector('#modal input[type="password"]'); password.value = 'synthetic-only-password';
  await h.click('连接', h.document.querySelector('#modal')); assert.equal(password.value, ''); assert.equal(h.document.querySelector('#modal').open, false);
  h.emit({ type: 'steam.guard', data: { kind: 'email', domain: '<img onerror=alert(1)>' } });
  assert.equal(h.document.querySelector('#modal').open, true); assert.equal(h.document.querySelector('#modal img'), null);
  assert.match(h.document.querySelector('#modal').textContent, /<img onerror=alert\(1\)>/);
  await h.click('取消登录'); assert.equal(h.document.querySelector('#modal').open, false);
});
