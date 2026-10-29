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
const richSource = await readFile(new URL('../renderer/rich-text.mjs', import.meta.url), 'utf8');
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
  const modal = document.querySelector('#modal');
  modal.showModal = () => { modal.open = true; modal.setAttribute('open', ''); };
  modal.close = () => { modal.open = false; modal.removeAttribute('open'); };
  let closeCount = 0; window.close = () => { closeCount++; };
  const calls = [], events = new Set(), timers = fakeTimers();
  let steam = { state: 'connected', steamId: owner, displayName: 'Synthetic account' };
  let runs = [];
  const emit = event => { for (const listener of events) listener(copy(event)); };
  const changed = () => queueMicrotask(() => emit({ type: 'workspace.changed' }));
  const snapshot = () => ({ fixture: true, workspace: workspace.snapshot(), steam: copy(steam), runs: copy(runs),
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
      case 'stats.reconcile': return {readback:nativeSnapshot(workspace.task(payload.taskId).appId),reconciliationId:'fixture-reconciliation'};
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
      case 'runs.start': runs.push({id:`run-${payload.taskId}`,appId:workspace.task(payload.taskId).appId,state:'running'});return {started:true};
      case 'runs.stop': runs=runs.map(run=>run.appId===payload.appId?{...run,state:'stopped'}:run);emit({type:'steam.runs',data:runs});return {stopped:true};
      case 'publication.record': {const result=workspace.recordPublication(payload);changed();return result;}
      case 'publication.prepare': { const result = workspace.preparePublication(payload.taskId, payload.language, payload.kind); changed(); return result; }
      case 'clipboard.write': return { copied: true };
      case 'external.open': return { opened: true };
      case 'settings.save': { workspace.updateSettings(payload.settings || {}); changed(); return snapshot(); }
      case 'backup.export': case 'backup.preview': return { cancelled: true };
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
  const script = `${richSource.replace(/^export /gm, '')}\n${appSource.replace(/^import .*from '\.\/rich-text\.mjs';\s*/m, '')}`;
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
  async function nav(name) { if(name==='research'){await nav('editor');if(!document.querySelector('.sources-pane'))await click('查看资料与建议');return;} await click(document.querySelector(`[data-tab="${name}"]`)); }
  async function input(selector, value, event = 'input') {
    const node = typeof selector === 'string' ? document.querySelector(selector) : selector;
    assert.ok(node, `Missing field ${selector}`); if(node.getAttribute('contenteditable')==='true')node.textContent=value;else node.value = value;
    node.dispatchEvent(new window.Event(event, { bubbles: true })); await settle(); return node;
  }
  async function choose(id) { await input('#task-picker', id, 'change'); }
  async function lang(value) { await input('#language-picker', value, 'change'); }
  return { window, document, workspace, a, b, calls, timers, emit, click, nav, input, choose, lang, findButton,
    setSteam(value) { steam = copy(value); emit({ type: 'steam.status', data: steam }); },
    setRuns(value) { runs = copy(value); emit({ type: 'steam.runs', data: runs }); },
    closeCount: () => closeCount, dispose: () => { timers.clear(); events.clear(); } };
}

test('workbench retains game selection and parallel runs across game tools and global panels', async t => {
  const h = await harness(); t.after(h.dispose);
  assert.equal(h.document.querySelector('#main h1').textContent,'研究与写作');
  assert.equal(h.document.querySelector('.sources-pane'),null,'writing opens with sources collapsed');
  const expected = { library:'查找游戏',runs:'运行与时长',achievements:'成就与统计',editor:'研究与写作',publication:'发布准备',settings:'设置' };
  for(const [tab,title] of Object.entries(expected)){
    await h.nav(tab);assert.equal(h.document.querySelector('#main h1')?.textContent,title);
    assert.equal(h.document.querySelector('#task-picker').value,h.a.id);
    assert.equal(h.document.querySelectorAll('.game-choice').length,2);
    assert.ok(h.document.querySelector('#run-tray'));
  }
  await h.click(h.document.querySelectorAll('.game-choice')[1]);
  assert.equal(h.document.querySelector('#main h1').textContent,'研究与写作');
  assert.equal(h.document.querySelector('#task-picker').value,h.b.id);
  assert.equal(h.document.querySelector('#main img'),null);
});

for (const command of ['steam.search', 'steam.library']) test(`late ${command} cannot redraw settings or discard unsaved API inputs`, async t => {
  const response = deferred(); let calls = 0;
  const h = await harness({ handlers: { [command]: (payload, context) => {
    if (command === 'steam.library' && calls++ === 0) return context.defaults(command, payload); // Fixture bootstrap.
    return response.promise;
  } } }); t.after(h.dispose);
  await h.nav('library');
  if (command === 'steam.search') {
    await h.input('#main select', 'store', 'change'); await h.input('[aria-label="搜索游戏"]', 'Pending search'); await h.click('搜索');
  } else await h.click('刷新游戏库');
  await h.nav('settings'); await h.input('[aria-label="AI API 密钥"]', 'synthetic-unsaved-key');
  const input = h.document.querySelector('[aria-label="AI API 密钥"]');
  response.resolve([{ appId: 1003, name: 'Late catalog result', steamMinutes: null }]); await settle(); await settle();
  assert.equal(h.document.querySelector('#main h1').textContent, '设置');
  assert.equal(h.document.querySelector('[aria-label="AI API 密钥"]') === input, true, 'late catalog result must not replace the active page DOM');
  assert.equal(input.value, 'synthetic-unsaved-key');
});

test('newer store searches win and a mode change invalidates an earlier result', async t => {
  const old = deferred(), next = deferred(), obsolete = deferred();
  const responses = [old, next, obsolete]; let sequence = 0;
  const h = await harness({ handlers: { 'steam.search': () => responses[sequence++].promise } }); t.after(h.dispose);
  await h.nav('library'); await h.input('#main select', 'store', 'change');
  await h.input('[aria-label="搜索游戏"]', 'Old'); await h.click('搜索');
  await h.input('[aria-label="搜索游戏"]', 'New');
  const enter = new h.window.Event('keydown', { bubbles: true }); enter.key = 'Enter';
  h.document.querySelector('[aria-label="搜索游戏"]').dispatchEvent(enter); await settle();
  assert.equal(sequence, 2);
  next.resolve([{ appId: 1003, name: 'NEW RESULT', steamMinutes: null }]); await settle();
  old.resolve([{ appId: 1004, name: 'OLD RESULT', steamMinutes: null }]); await settle();
  assert.match(h.document.querySelector('#main').textContent, /NEW RESULT/);
  assert.doesNotMatch(h.document.querySelector('#main').textContent, /OLD RESULT/);
  await h.input('[aria-label="搜索游戏"]', 'Obsolete'); await h.click('搜索');
  await h.input('#main select', 'library', 'change');
  obsolete.resolve([{ appId: 1005, name: 'OBSOLETE STORE RESULT', steamMinutes: null }]); await settle();
  assert.doesNotMatch(h.document.querySelector('#main').textContent, /OBSOLETE STORE RESULT/);
  assert.equal(h.document.querySelector('#main select').value, 'library');
});

test('late automatic library after login cannot replace a newer store search', async t => {
  const owned = deferred(); let reads = 0, loggedIn = false;
  const h = await harness({ handlers: {
    init: (_payload, context) => ({ ...context.snapshot(), ...(loggedIn ? { steam: { state: 'connected', steamId: owner, generation: 2 } } : {}) }),
    'steam.library': (payload, context) => reads++ === 0 ? context.defaults('steam.library', payload) : owned.promise,
    'steam.login': () => { loggedIn = true; return { state: 'connected', steamId: owner, generation: 2 }; },
    'steam.search': () => [{ appId: 1003, name: 'NEW STORE RESULT', steamMinutes: null }],
  } }); t.after(h.dispose);
  await h.click(h.document.querySelector('#account-button')); await h.click('退出连接并停止运行'); await h.click('确认退出连接');
  await h.nav('library'); await h.click(h.document.querySelector('#account-button'));
  await h.input('input[autocomplete="username"]', 'synthetic-user'); await h.input('input[type="password"]', 'synthetic-password');
  await h.click('连接'); await settle(); await settle();
  assert.equal(reads, 2);
  await h.nav('library'); await h.input('#main select', 'store', 'change'); await h.input('[aria-label="搜索游戏"]', 'New title'); await h.click('搜索');
  assert.match(h.document.querySelector('#main').textContent, /NEW STORE RESULT/);
  owned.resolve([{ appId: 1004, name: 'OLD OWNED RESULT', steamMinutes: 20 }]); await settle(); await settle();
  assert.equal(h.document.querySelector('#main select').value, 'store');
  assert.match(h.document.querySelector('#main').textContent, /NEW STORE RESULT/);
  assert.doesNotMatch(h.document.querySelector('#main').textContent, /OLD OWNED RESULT/);
});

test('login completion preserves a store choice made while authentication was pending', async t => {
  const login = deferred(); let loggedIn = false, reads = 0;
  const h = await harness({ handlers: {
    init: (_payload, context) => ({ ...context.snapshot(), ...(loggedIn ? { steam: { state: 'connected', steamId: owner, generation: 2 } } : {}) }),
    'steam.library': (payload, context) => { reads++; return context.defaults('steam.library', payload); },
    'steam.login': async () => { await login.promise; loggedIn = true; return { state: 'connected', steamId: owner, generation: 2 }; },
    'steam.search': () => [{ appId: 1003, name: 'STORE CHOICE DURING LOGIN', steamMinutes: null }],
  } }); t.after(h.dispose);
  await h.click(h.document.querySelector('#account-button')); await h.click('退出连接并停止运行'); await h.click('确认退出连接');
  await h.nav('library'); await h.click(h.document.querySelector('#account-button')); await h.click('连接');
  await h.nav('library'); await h.input('#main select', 'store', 'change'); await h.input('[aria-label="搜索游戏"]', 'Store choice'); await h.click('搜索');
  await h.nav('settings'); await h.input('[aria-label="AI API 密钥"]', 'synthetic-unsaved-key');
  const input = h.document.querySelector('[aria-label="AI API 密钥"]');
  login.resolve(); await settle(); await settle();
  assert.equal(reads, 1, 'automatic login loading does not request a library over the selected store mode');
  assert.equal(h.document.querySelector('[aria-label="AI API 密钥"]'), input);
  assert.equal(input.value, 'synthetic-unsaved-key');
  await h.nav('library');
  assert.equal(h.document.querySelector('#main select').value, 'store');
  assert.match(h.document.querySelector('#main').textContent, /STORE CHOICE DURING LOGIN/);
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
  await h.choose(h.b.id); await h.input('[aria-label="个人长评正文"]', 'B English text'); await h.nav('runs');
  assert.equal(h.workspace.task(h.a.id).drafts.english.long.text, 'A English text');
  assert.equal(h.workspace.task(h.b.id).drafts.english.long.text, 'B English text');
  assert.equal(h.workspace.task(h.b.id).drafts.schinese.long.text, '');
  assert.match(h.document.querySelector('#main').textContent, /Beta/);
});

test('account switch rerenders task content while dirty buffers finish saving to their captured owner and language', async t => {
  const other = '76561198000000001'; let nextTask;
  const h = await harness({ configure(workspace) {
    workspace.bindAccount(other); nextTask = workspace.createTask({ appId: 2001, title: 'New owner task', groupId: '45525570' });
    workspace.saveDraft({ taskId: nextTask.id, language: 'english', kind: 'long', text: 'New owner English text', expectedRevision: 0 });
    workspace.bindAccount(owner);
  } }); t.after(h.dispose);
  await h.nav('editor'); await h.lang('english'); await h.input('[aria-label="个人长评正文"]', 'Old owner unsaved English text');
  h.workspace.bindAccount(other); h.emit({ type: 'workspace.changed' }); await settle(); await settle();
  await h.timers.fire(700);
  const old = h.workspace.exportBackup().tasks.find(task => task.id === h.a.id);
  assert.equal(old.drafts.english.long.text, 'Old owner unsaved English text'); assert.equal(old.drafts.schinese.long.text, '');
  assert.equal(h.workspace.snapshot().activeSteamId, other);
  assert.equal(h.workspace.task(nextTask.id).drafts.english.long.text, 'New owner English text');
  assert.equal(h.document.querySelector('#account-status .account-id').textContent, other);
  assert.equal(h.document.querySelector('[aria-label="个人长评正文"]').textContent, 'New owner English text');
  assert.equal(h.document.querySelector('#task-picker').value, nextTask.id);
  const save = h.calls.find(call => call.command === 'draft.save');
  assert.equal(save.payload.ownerSteamId, owner); assert.equal(save.payload.taskId, h.a.id); assert.equal(save.payload.language, 'english');
  assert.equal(h.calls.some(call => /^(?:account\.bind|steam\.(?:login|logout)|runs\.|stats\.)/.test(call.command)), false);
});

test('account refresh on settings preserves global unsaved API controls', async t => {
  const h = await harness(); t.after(h.dispose); await h.nav('settings');
  await h.input('[aria-label="AI API 密钥"]', 'synthetic-unsaved-key');
  const input = h.document.querySelector('[aria-label="AI API 密钥"]');
  h.workspace.bindAccount('76561198000000001'); h.emit({ type: 'workspace.changed' }); await settle(); await settle();
  assert.equal(h.document.querySelector('[aria-label="AI API 密钥"]') === input, true);
  assert.equal(input.value, 'synthetic-unsaved-key');
});

test('login completion preserves settings typed after leaving the connection panel', async t => {
  const login=deferred();const h=await harness({handlers:{'steam.login':()=>login.promise}});t.after(h.dispose);
  h.setSteam({state:'logged_out'});await h.click(h.document.querySelector('#account-button'));await h.click('连接');
  await h.nav('settings');await h.input('[aria-label="AI API 密钥"]','synthetic-unsaved-after-login');
  const input=h.document.querySelector('[aria-label="AI API 密钥"]');
  h.workspace.bindAccount('76561198000000001');login.resolve({state:'connected',steamId:'76561198000000001'});await settle();await settle();
  assert.equal(h.document.querySelector('[aria-label="AI API 密钥"]'),input);assert.equal(input.value,'synthetic-unsaved-after-login');
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
  await h.click('鉴赏家短评');
  const text = '中A ée\u0301 👩🏽‍💻\n';
  await h.input('[aria-label="鉴赏家短评正文"]', text);
  assert.match(h.document.querySelector('#main').textContent, new RegExp(`${Array.from(text).length} / 199 字`));
  await h.input('[aria-label="全文链接"]', `https://steamcommunity.com/profiles/${owner}/recommended/1001/`);
  await h.click('保存链接与推荐类型'); await h.click('立即保存');
  const draft = h.workspace.task(h.a.id).drafts.schinese;
  assert.equal(draft.short.text, text); assert.ok(draft.fullReviewUrl.includes('/1001/'));
  await h.input('[aria-label="鉴赏家短评正文"]', '字'.repeat(200)); await h.nav('publication'); await h.click('准备鉴赏家短评');
  assert.equal(h.workspace.task(h.a.id).publications.length, 0); assert.match(h.document.querySelector('#toast').textContent, /少于 200/);
});

test('editor retains a legacy wrong full link for repair and only freezes a corrected matching link', async t => {
  const ownReview = `https://steamcommunity.com/profiles/${owner}/recommended/1001/`;
  const wrongOwner = ownReview.replace(owner, '76561198000000001');
  const h = await harness({ configure(workspace, { a }) {
    workspace.saveDraft({ taskId: a.id, language: 'schinese', kind: 'short', text: 'Short body remains unchanged', expectedRevision: 0 });
    workspace.commit(state => { Object.assign(state.tasks[0].drafts.schinese, { fullReviewUrl: wrongOwner, fullReviewUrlNeedsRepair: true }); });
  } }); t.after(h.dispose);
  await h.nav('editor');await h.click('鉴赏家短评');
  assert.equal(h.document.querySelector('[aria-label="全文链接"]').value, wrongOwner);
  await h.nav('publication'); await h.click('准备鉴赏家短评'); assert.equal(h.workspace.task(h.a.id).publications.length, 0);await h.nav('editor');
  for (const link of [wrongOwner, ownReview.replace('/1001/', '/999/'), 'https://example.com/review']) {
    await h.input('[aria-label="全文链接"]', link); await h.click('保存链接与推荐类型');
    assert.equal(h.workspace.task(h.a.id).drafts.schinese.fullReviewUrl, wrongOwner);
    assert.match(h.document.querySelector('#toast').textContent, /对应账号、游戏/);
  }
  await h.input('[aria-label="全文链接"]', ownReview); await h.click('保存链接与推荐类型'); await h.nav('publication');await h.click('准备鉴赏家短评');
  const task = h.workspace.task(h.a.id);
  assert.equal(task.publications.length, 1); assert.equal(task.publications[0].fullReviewUrl, ownReview);
  assert.equal(task.drafts.schinese.fullReviewUrlNeedsRepair, false);
  assert.equal(task.drafts.schinese.short.text, 'Short body remains unchanged');
});

test('saved long review renders formatted input safely and HTTPS links require a modified click', async t => {
  const text='<img src=x onerror="alert(1)"><script>alert(2)</script>[b]bold[/b][url=javascript:alert(3)]bad[/url][url=https://example.com/review]good[/url]';
  const h=await harness({configure(workspace,{a}){workspace.saveDraft({taskId:a.id,language:'schinese',kind:'long',text,expectedRevision:0});}});t.after(h.dispose);
  const editor=h.document.querySelector('.rich-editor');assert.equal(editor.querySelector('img,script,iframe'),null);
  assert.match(editor.textContent,/<script>alert\(2\)<\/script>/);assert.equal(editor.querySelector('strong').textContent,'bold');
  const link=editor.querySelector('a[href="https://example.com/review"]');await h.click(link);
  assert.equal(h.calls.filter(c=>c.command==='external.open').length,0);
  const event=new h.window.Event('click',{bubbles:true});event.metaKey=true;link.dispatchEvent(event);await settle();
  assert.deepEqual(h.calls.filter(c=>c.command==='external.open').map(c=>c.payload.url),['https://example.com/review']);
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
  assert.equal(h.document.querySelector('#main h1').textContent, '研究与写作');
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
  await h.nav('achievements'); await h.click('重新读取未知结果');await h.click('已核对回读，解除重试门禁');
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
  assert.equal(h.document.querySelector('[aria-label="个人长评正文"]').textContent, 'unsaved conflict text');
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

test('connection panel carries credentials through Guard and success without retaining passwords or codes', async t => {
  const pending=deferred();const h=await harness({handlers:{'steam.login':()=>pending.promise}});t.after(h.dispose);
  h.setSteam({state:'logged_out'});await h.click(h.document.querySelector('#account-button'));const panel=h.document.querySelector('#main');
  await h.input('input[autocomplete="username"]','synthetic-account');const password=panel.querySelector('input[type="password"]');password.value='synthetic-only-password';
  await h.click('连接');assert.equal(password.value,'');assert.match(panel.textContent,/正在验证登录/);assert.equal(panel.querySelector('input[type="password"]'),null);
  h.setSteam({state:'guard_required',accountName:'synthetic-account',guardPrompt:{kind:'email',domain:'<img onerror=alert(1)>',lastCodeWrong:true}});
  assert.equal(panel.querySelector('img'),null);assert.match(panel.textContent,/上一个验证码无效/);
  const code=panel.querySelector('input[autocomplete="one-time-code"]');code.value='12345';await h.click('提交验证码');assert.equal(code.value,'');
  h.setSteam({state:'connected',steamId:owner,accountName:'synthetic-account',persistence:'encrypted'});pending.resolve({state:'connected'});await settle();await settle();
  assert.match(panel.textContent,/已连接/);assert.match(panel.textContent,/会话已加密保存/);
  assert.equal(panel.textContent.includes('synthetic-only-password'),false);assert.equal(panel.textContent.includes('12345'),false);
  assert.equal(h.document.querySelector('#modal').open,undefined);
});

test('authentication errors stay inline and saved-session recovery exposes credentials only by choice', async t => {
  const pending=deferred();const h=await harness({handlers:{'steam.login':()=>pending.promise}});t.after(h.dispose);
  h.setSteam({state:'logged_out'});await h.click(h.document.querySelector('#account-button'));await h.input('input[autocomplete="username"]','synthetic-account');await h.click('连接');
  h.setSteam({state:'error',accountName:'synthetic-account',error:'steam_credentials_rejected'});pending.reject(new Error('steam_credentials_rejected'));await settle();await settle();
  const panel=h.document.querySelector('#main');assert.match(panel.textContent,/Steam 拒绝了登录凭据/);
  assert.equal(panel.querySelector('input[autocomplete="username"]').value,'synthetic-account');assert.equal(panel.querySelector('input[type="password"]').value,'');
  await h.nav('editor');h.setSteam({state:'restoring',accountName:'saved-account'});assert.match(h.document.querySelector('#account-status').textContent,/saved-account.*正在恢复已保存会话/);
  await h.click(h.document.querySelector('#account-button'));assert.match(panel.textContent,/正在恢复已保存会话/);
  h.setSteam({state:'reconnecting',accountName:'saved-account',attempt:2,maxAttempts:3});assert.match(panel.textContent,/自动重连 2 \/ 3/);
  await h.click('取消登录');assert.ok(h.findButton('连接',panel));
  h.setSteam({state:'error',restoreAvailable:true,error:'steam_disconnected'});assert.equal(panel.querySelector('input[type="password"]'),null);
  assert.ok(h.findButton('再次尝试保存的会话'));await h.click('改用账号重新认证');assert.ok(panel.querySelector('input[type="password"]'));
});

test('logout explains its consequences and only a separate confirmation clears the connection', async t=>{
  const h=await harness();t.after(h.dispose);await h.click(h.document.querySelector('#account-button'));await h.click('退出连接并停止运行');
  assert.equal(h.calls.filter(c=>c.command==='steam.logout').length,0);assert.match(h.document.querySelector('#main').textContent,/清除已保存的会话/);
  await h.click('保留连接');assert.equal(h.calls.filter(c=>c.command==='steam.logout').length,0);
  await h.click('退出连接并停止运行');await h.click('确认退出连接');assert.equal(h.calls.filter(c=>c.command==='steam.logout').length,1);
});

test('a post-login library failure preserves successful authentication feedback',async t=>{
  let failed=false;const h=await harness({handlers:{'steam.login':()=>{h.setSteam({state:'connected',accountName:'synthetic-account',steamId:owner,persistence:'encrypted'});failed=true;return {state:'connected'};},'steam.library':(payload,context)=>{if(failed)throw new Error('steam_library_failed');return context.defaults('steam.library',payload);}}});t.after(h.dispose);
  h.setSteam({state:'logged_out'});await h.click(h.document.querySelector('#account-button'));await h.click('连接');await settle();await settle();
  const panel=h.document.querySelector('#main');assert.match(panel.textContent,/已连接/);assert.match(panel.textContent,/会话已加密保存/);assert.ok(h.findButton('退出连接并停止运行',panel));
});

test('achievement read/preview/apply controls remain usable while the same AppID is running', async t => {
  const h = await harness(); t.after(h.dispose);
  h.setRuns([{ id: 'running', appId: h.a.appId, steamId: owner, state: 'running' }]);
  await h.nav('achievements');
  assert.doesNotMatch(h.document.querySelector('#main').textContent, /请先停止协议运行/);
  await h.click('读取 / 刷新 Steam 状态');
  const checkbox = h.document.querySelector('#achievement-table input[type="checkbox"]'); checkbox.checked = true;
  checkbox.dispatchEvent(new h.window.Event('change', { bubbles: true }));
  await h.click('预览成就与统计更改');
  await h.click('提交合成更改');
  assert.deepEqual(h.calls.filter(call => /^stats\./.test(call.command)).map(call => call.command), ['stats.read', 'stats.preview', 'stats.apply']);
  assert.equal(h.calls.some(call => call.command === 'runs.stop'), false);
});

test('damaged credentials leave offline work usable and recovery requires an explicit settings action',async t=>{
  let corrupt=true;
  const h=await harness({handlers:{init:(_payload,context)=>({...context.snapshot(),credentials:{...context.snapshot().credentials,...(corrupt?{credentialStoreError:'credential_store_unreadable'}:{})}}),
    'credentials.reset':()=>{corrupt=false;return {reset:true};}}});t.after(h.dispose);
  await h.nav('editor');await h.input('[aria-label="个人长评正文"]','offline draft still usable');await h.timers.fire(700);
  await h.nav('settings');assert.match(h.document.querySelector('#main').textContent,/凭据文件需要恢复/);
  assert.ok(h.findButton('导出工作区备份'));assert.equal(h.calls.some(call=>call.command==='credentials.reset'),false);
  await h.click('恢复凭据存储…');assert.equal(h.calls.some(call=>call.command==='credentials.reset'),false);
  await h.click('归档损坏文件并重新配置密钥');
  assert.equal(h.calls.filter(call=>call.command==='credentials.reset').length,1);
  assert.equal(h.workspace.task(h.a.id).drafts.schinese.long.text,'offline draft still usable');
  assert.doesNotMatch(h.document.querySelector('#main').textContent,/凭据文件需要恢复/);
});

test('formatted input saves legacy Steam formatting and whole-copy combines the group footer once',async t=>{
  const footer='[url=https://example.com/curator]群组尾巴[/url]';
  const h=await harness({configure(workspace){workspace.updateGroups(workspace.snapshot().groups.map(group=>({...group,footers:{...group.footers,schinese:footer}})));}});t.after(h.dispose);
  const editor=h.document.querySelector('.rich-editor');editor.innerHTML='<h2>标题</h2><p>正文 <strong>加粗</strong> <em>斜体</em></p>';
  editor.dispatchEvent(new h.window.Event('input',{bubbles:true}));await h.click('立即保存');
  const saved=h.workspace.task(h.a.id).drafts.schinese.long.text;
  assert.match(saved,/\[h2\]标题\[\/h2\]/);assert.match(saved,/\[b\]加粗\[\/b\]/);assert.match(saved,/\[i\]斜体\[\/i\]/);
  assert.equal(saved.includes(footer),false,'footer is composed only at publication/copy boundary');
  await h.click('复制到 Steam');assert.equal(h.calls.at(-1).command,'clipboard.write');assert.equal(h.calls.at(-1).payload.text,`${saved.trim()}\n\n${footer}`);
  await h.choose(h.b.id);await h.choose(h.a.id);assert.equal(h.document.querySelector('.rich-editor strong').textContent,'加粗');
  await h.click('鉴赏家短评');await h.input('[aria-label="鉴赏家短评正文"]','独立短评');await h.click('复制到 Steam');
  assert.equal(h.calls.at(-1).payload.text,'独立短评');
});

test('sources drawer and keyboard width controls preserve the current formatted draft',async t=>{
  const h=await harness();t.after(h.dispose);await h.input('.rich-editor','写作中的正文');await h.click('查看资料与建议');
  assert.equal(h.workspace.task(h.a.id).drafts.schinese.long.text,'写作中的正文');assert.ok(h.document.querySelector('.sources-pane'));
  for(const [label,key,expected] of [['调整游戏列表宽度','ArrowRight','210'],['调整资料宽度','End','480']]){
    const bar=h.document.querySelector(`[aria-label="${label}"]`),event=new h.window.Event('keydown',{bubbles:true,cancelable:true});event.key=key;bar.dispatchEvent(event);
    assert.equal(bar.getAttribute('aria-valuenow'),expected);assert.equal(event.defaultPrevented,true);
  }
  await h.click('收起资料');assert.equal(h.document.querySelector('.sources-pane'),null);assert.equal(h.document.querySelector('.rich-editor').textContent,'写作中的正文');
});

test('persistent run tray stops one game while retaining the other across global panels',async t=>{
  const h=await harness();t.after(h.dispose);h.setRuns([{id:'alpha',appId:1001,state:'running'},{id:'beta',appId:1002,state:'running'}]);
  await h.nav('settings');assert.match(h.document.querySelector('#run-tray').textContent,/并行运行 · 2 款/);
  await h.click('单停',h.document.querySelector('#run-tray'));assert.deepEqual(h.calls.filter(c=>c.command==='runs.stop').map(c=>c.payload.appId),[1001]);
  assert.match(h.document.querySelector('#run-tray').textContent,/并行运行 · 1 款/);assert.match(h.document.querySelector('#run-tray').textContent,/Beta/);
  assert.equal(h.document.querySelector('#main h1').textContent,'设置');
});

test('search details preserve the selected draft and new tasks require an explicit unique group',async t=>{
  const h=await harness();t.after(h.dispose);await h.input('.rich-editor','在浏览游戏前保留');await h.nav('library');
  await h.input('#main select','store','change');await h.input('[aria-label="搜索游戏"]','Gamma');await h.click('搜索');await h.click('商店与介绍');
  assert.equal(h.document.querySelector('#task-picker').value,h.a.id);assert.equal(h.workspace.task(h.a.id).drafts.schinese.long.text,'在浏览游戏前保留');
  await h.click('选择群组建立任务');await h.click('建立任务并打开');assert.equal(h.calls.filter(c=>c.command==='task.create').length,0);
  const group=[...h.document.querySelectorAll('#main select')].find(node=>node.querySelector('option[value=""]'));await h.input(group,'45525570','change');await h.click('建立任务并打开');
  const task=h.workspace.snapshot().tasks.find(item=>item.appId===1003);assert.equal(task.groupId,'45525570');assert.equal(h.document.querySelector('#task-picker').value,task.id);assert.equal(h.document.querySelector('#main h1').textContent,'研究与写作');
});

test('unknown native result needs separate fresh readback and acknowledgement with its exact token',async t=>{
  const h=await harness({configure(workspace,{a}){workspace.recordOperation(a.id,owner,{id:'uncertain',state:'outcome_unknown',changes:{}});},handlers:{'stats.acknowledge':(payload,context)=>{
    assert.equal(payload.reconciliationId,'fixture-reconciliation');context.workspace.recordOperation(context.a.id,owner,{id:'uncertain',state:'operator_acknowledged',changes:{}});return nativeSnapshot(1001);
  }}});t.after(h.dispose);await h.nav('achievements');
  assert.equal(h.findButton('已核对回读，解除重试门禁'),undefined);await h.click('读取 / 刷新 Steam 状态');assert.equal(h.findButton('已核对回读，解除重试门禁'),undefined);
  await h.click('重新读取未知结果');assert.equal(h.workspace.task(h.a.id).operations[0].state,'outcome_unknown');assert.equal(h.calls.filter(c=>c.command==='stats.acknowledge').length,0);
  assert.ok(h.document.querySelector('[aria-label="选择 ACH_1001"]'));await h.click('已核对回读，解除重试门禁');assert.equal(h.workspace.task(h.a.id).operations[0].state,'operator_acknowledged');
});

test('backup compares both language drafts and stops runs before a separate replacement',async t=>{
  let preview;
  const h=await harness({configure(workspace,{a}){workspace.saveDraft({taskId:a.id,language:'schinese',kind:'long',text:'当前中文正文',expectedRevision:0});},handlers:{'backup.preview':(_payload,context)=>{
    const imported=context.workspace.snapshot();imported.tasks[0].drafts.schinese.long.text='备份中文正文';imported.tasks[0].drafts.english.short.text='Backup English short';preview={previewId:'reviewed-backup',workspace:imported,scope:{currentOtherTasks:2,importedOtherTasks:1,currentOtherRuns:4,importedOtherRuns:3}};return preview;
  },'backup.restore':(payload,context)=>{assert.equal(payload.previewId,'reviewed-backup');context.workspace.importBackup(preview.workspace);context.changed();return context.snapshot();}}});t.after(h.dispose);
  h.setRuns([{id:'alpha',appId:1001,state:'running'}]);await h.nav('settings');await h.click('选择备份并比较');
  const compare=h.document.querySelector('.backup-comparison');assert.match(compare.textContent,/当前中文正文/);assert.match(compare.textContent,/备份中文正文/);assert.match(compare.textContent,/Backup English short/);assert.match(compare.textContent,/其他账号也会被替换：任务 2 → 1 项/);
  assert.equal(h.calls.filter(c=>c.command==='backup.restore').length,0);assert.equal(h.findButton('确认用此备份替换工作区').disabled,true);
  await h.click('停止全部运行与研究');assert.equal(h.calls.filter(c=>c.command==='backup.restore').length,0);assert.equal(h.findButton('确认用此备份替换工作区').disabled,false);
  await h.click('确认用此备份替换工作区');assert.equal(h.workspace.task(h.a.id).drafts.schinese.long.text,'备份中文正文');assert.equal(h.document.querySelector('.backup-comparison'),null);
});

test('publication keeps frozen formatted history and blocks stale filling and unknown duplicate preparation independently',async t=>{
  const h=await harness();t.after(h.dispose);await h.input('.rich-editor','First frozen body');await h.nav('publication');await h.click('准备个人长评');
  const attempt=h.workspace.task(h.a.id).publications[0];assert.match(h.document.querySelector('.frozen-body').textContent,/First frozen body/);assert.equal(h.document.querySelector('[aria-label="发布结果链接"]').value,'');
  await h.nav('editor');await h.input('.rich-editor','New current body');await h.nav('publication');assert.equal(h.findButton('填入冻结内容（不提交）').disabled,true);assert.match(h.document.querySelector('.frozen-body').textContent,/First frozen body/);
  await h.click('记录已提交 / 结果待核实');assert.equal(h.findButton('准备个人长评').disabled,true);assert.equal(h.findButton('准备鉴赏家短评').disabled,false);
  await h.click('本人已核对此结果 URL');assert.equal(h.workspace.task(h.a.id).publications[0].state,'submitted_unknown');assert.match(h.document.querySelector('#toast').textContent,/实际发布结果链接/);
  assert.equal(h.workspace.task(h.a.id).publications[0].id,attempt.id);
});

for(const action of ['确认退出连接','取消登录'])test(`late ${action} completion preserves settings entered after leaving the connection panel`,async t=>{
  const logout=deferred();const h=await harness({handlers:{'steam.logout':()=>logout.promise}});t.after(h.dispose);
  if(action==='取消登录')h.setSteam({state:'reconnecting',attempt:1,maxAttempts:3});
  await h.click(h.document.querySelector('#account-button'));if(action==='确认退出连接')await h.click('退出连接并停止运行');await h.click(action);
  await h.nav('settings');await h.input('[aria-label="AI API 密钥"]','keep-on-delayed-logout');const input=h.document.querySelector('[aria-label="AI API 密钥"]');
  h.setSteam({state:'logged_out'});logout.resolve({state:'logged_out'});await settle();await settle();
  assert.equal(h.document.querySelector('#main h1').textContent,'设置');assert.equal(h.document.querySelector('[aria-label="AI API 密钥"]'),input);assert.equal(input.value,'keep-on-delayed-logout');
});

test('late Guard failure preserves settings after the user leaves the connection panel',async t=>{
  const guard=deferred();const h=await harness({handlers:{'steam.guard':()=>guard.promise}});t.after(h.dispose);h.setSteam({state:'guard_required',guardPrompt:{kind:'email'}});await settle();
  await h.click('提交验证码');await h.nav('settings');await h.input('[aria-label="AI API 密钥"]','keep-on-guard-error');const input=h.document.querySelector('[aria-label="AI API 密钥"]');
  guard.reject(new Error('guard_rejected'));await settle();await settle();assert.equal(h.document.querySelector('[aria-label="AI API 密钥"]'),input);assert.equal(input.value,'keep-on-guard-error');
});

test('backup preview and stop actions preserve settings controls and wait for active AI cancellation',async t=>{
  const ai=deferred();const h=await harness({handlers:{'ai.format':()=>ai.promise,'backup.preview':(_payload,context)=>({previewId:'ai-backup',workspace:context.workspace.snapshot()}),'research.cancel':()=>{ai.reject(new Error('research_cancelled'));return {cancelled:true};}}});t.after(h.dispose);
  await h.input('.rich-editor','AI source');await h.click('AI 格式处理');await h.nav('settings');await h.input('[aria-label="AI API 密钥"]','keep-while-comparing');const input=h.document.querySelector('[aria-label="AI API 密钥"]');
  await h.click('选择备份并比较');assert.equal(h.document.querySelector('[aria-label="AI API 密钥"]'),input);assert.equal(input.value,'keep-while-comparing');assert.equal(h.findButton('确认用此备份替换工作区').disabled,true);
  await h.click('停止全部运行与研究');await settle();assert.equal(h.calls.filter(c=>c.command==='research.cancel').length,1);assert.equal(h.calls.filter(c=>c.command==='backup.restore').length,0);
  assert.equal(h.findButton('确认用此备份替换工作区').disabled,false);assert.equal(h.document.querySelector('[aria-label="AI API 密钥"]'),input);assert.equal(input.value,'keep-while-comparing');
});

test('unknown and imported publication records can inspect only their frozen official target',async t=>{
  const h=await harness({configure(workspace,{a}){
    workspace.saveDraft({taskId:a.id,language:'schinese',kind:'long',text:'Frozen body',expectedRevision:0});const attempt=workspace.preparePublication(a.id,'schinese','long');workspace.recordPublication({taskId:a.id,attemptId:attempt.id,state:'submitted_unknown'});
    workspace.commit(state=>{const record=state.tasks[0].publications[0];record.imported=true;record.resultUrl='https://example.com/misdirection';});
  },handlers:{init:(_payload,context)=>({...context.snapshot(),fixture:false})}});t.after(h.dispose);await h.nav('publication');
  assert.equal(h.findButton('打开官方页面'),undefined);assert.equal(h.findButton('填入冻结内容（不提交）').disabled,true);await h.click('打开结果页核对');
  assert.equal(h.calls.filter(c=>c.command==='publication.open').length,0);assert.equal(h.calls.find(c=>c.command==='external.open').payload.url,`https://steamcommunity.com/profiles/${owner}/recommended/1001/`);
});

test('malformed imported publication metadata stays available for manual copy without breaking the workbench',async t=>{
  const h=await harness({configure(workspace){workspace.commit(state=>state.tasks[0].publications.push({id:'old',language:'unknown',kind:'long',text:'Preserved old body',state:'imported_unverified'}));}});t.after(h.dispose);await h.nav('publication');
  assert.match(h.document.querySelector('#main').textContent,/Preserved old body/);assert.ok(h.findButton('复制原始历史记录'));assert.equal(h.findButton('填入冻结内容（不提交）'),undefined);await h.click('复制原始历史记录');assert.match(h.calls.at(-1).payload.text,/Preserved old body/);
});

for(const command of ['stats.reconcile','stats.acknowledge'])test(`late ${command} completion preserves settings input while retaining its native result`,async t=>{
  const response=deferred();const h=await harness({configure(workspace,{a}){workspace.recordOperation(a.id,owner,{id:'pending-unknown',state:'outcome_unknown',changes:{}});},handlers:{[command]:()=>response.promise}});t.after(h.dispose);
  await h.nav('achievements');await h.click('重新读取未知结果');if(command==='stats.acknowledge')await h.click('已核对回读，解除重试门禁');
  await h.nav('settings');await h.input('[aria-label="AI API 密钥"]','keep-on-native-readback');const input=h.document.querySelector('[aria-label="AI API 密钥"]');
  response.resolve(command==='stats.reconcile'?{readback:nativeSnapshot(1001),reconciliationId:'late-native-result'}:nativeSnapshot(1001));await settle();await settle();
  assert.equal(h.document.querySelector('[aria-label="AI API 密钥"]'),input);assert.equal(input.value,'keep-on-native-readback');
  await h.nav('achievements');assert.ok(h.document.querySelector('[aria-label="选择 ACH_1001"]'));
});

for(const command of ['ai.format','research.run'])test(`${command} completion preserves the active editor and uncommitted IME text`,async t=>{
  const response=deferred();const h=await harness({handlers:{[command]:()=>response.promise}});t.after(h.dispose);
  await h.input('.rich-editor','已提交正文');if(command==='research.run')await h.click('查看资料与建议');
  const editor=h.document.querySelector('.rich-editor');await h.click(command==='ai.format'?'AI 格式处理':'搜集资料并生成长评初稿');assert.equal(h.document.querySelector('.rich-editor'),editor);
  editor.dispatchEvent(new h.window.Event('compositionstart',{bubbles:true}));editor.textContent='已提交正文与拼音输入中';editor.dispatchEvent(new h.window.Event('input',{bubbles:true}));
  response.resolve(command==='ai.format'?{text:'迟到建议'}:{draft:'迟到研究建议',origin:{taskId:h.a.id,ownerSteamId:owner,language:'schinese',baseRevision:1}});await settle();await settle();
  assert.equal(h.document.querySelector('.rich-editor'),editor);assert.equal(editor.textContent,'已提交正文与拼音输入中');assert.equal(h.workspace.task(h.a.id).drafts.schinese.long.text,'已提交正文');
  editor.dispatchEvent(new h.window.Event('compositionend',{bubbles:true}));await h.click('立即保存');assert.equal(h.workspace.task(h.a.id).drafts.schinese.long.text,'已提交正文与拼音输入中');
});

test('legacy formatted group footer remains separate during editing and appears once in copy and frozen publication',async t=>{
  const footer='[s]旧格式署名[/s]\n[url]https://example.com/group[/url]';
  const h=await harness({configure(workspace,{a}){workspace.updateGroups(workspace.snapshot().groups.map(group=>({...group,footers:{...group.footers,schinese:footer}})));workspace.saveDraft({taskId:a.id,language:'schinese',kind:'long',text:`原有正文\n\n${footer}`,expectedRevision:0});}});t.after(h.dispose);
  const editor=h.document.querySelector('.rich-editor');assert.equal(editor.textContent,'原有正文');assert.equal(editor.querySelector('s,a'),null,'group footer is separate from editable body');
  await h.click('AI 格式处理');assert.equal(h.calls.find(call=>call.command==='ai.format').payload.text,'原有正文','AI transforms receive body without embedded legacy footer');
  editor.append(h.document.createTextNode('，补充一句'));editor.dispatchEvent(new h.window.Event('input',{bubbles:true}));await h.click('立即保存');
  assert.equal(h.workspace.task(h.a.id).drafts.schinese.long.text,'原有正文，补充一句');await h.click('复制到 Steam');assert.equal(h.calls.at(-1).payload.text.split('旧格式署名').length-1,1);
  await h.nav('publication');await h.click('准备个人长评');const attempt=h.workspace.task(h.a.id).publications[0];assert.equal(attempt.text.split('旧格式署名').length-1,1);assert.equal(attempt.text,`原有正文，补充一句\n\n${footer}`);
});

test('pending publication result URL keeps its input node during unrelated updates and text across navigation',async t=>{
  const h=await harness({configure(workspace,{a}){workspace.saveDraft({taskId:a.id,language:'schinese',kind:'long',text:'Publication body',expectedRevision:0});workspace.preparePublication(a.id,'schinese','long');}});t.after(h.dispose);await h.nav('publication');
  const pending=`https://steamcommunity.com/profiles/${owner}/recommended/1001/`;const input=await h.input('[aria-label="发布结果链接"]',pending);
  h.workspace.saveDraft({taskId:h.b.id,language:'english',kind:'long',text:'background unrelated draft',expectedRevision:0});h.emit({type:'workspace.changed'});await settle();await settle();
  assert.equal(h.document.querySelector('[aria-label="发布结果链接"]'),input,'unrelated workspace refresh must retain the active URL input');assert.equal(input.value,pending);await h.nav('editor');await h.nav('publication');assert.equal(h.document.querySelector('[aria-label="发布结果链接"]').value,pending);
});

test('comment translation participates in backup cancellation and idle gating',async t=>{
  const translation=deferred();const h=await harness({configure(workspace,{a}){workspace.saveResearch(a.id,{appId:a.appId,sources:[],reviews:[{review:'A source comment'}],warnings:[]},owner);},handlers:{'ai.translate':()=>translation.promise,'backup.preview':(_payload,context)=>({previewId:'comment-backup',workspace:context.workspace.snapshot()}),'research.cancel':()=>{translation.reject(new Error('research_cancelled'));return {cancelled:true};}}});t.after(h.dispose);
  await h.click('查看资料与建议');await h.click('翻译此评论');await h.nav('settings');await h.click('选择备份并比较');assert.equal(h.findButton('确认用此备份替换工作区').disabled,true);
  await h.click('停止全部运行与研究');await settle();assert.equal(h.calls.filter(c=>c.command==='research.cancel').length,1);assert.equal(h.findButton('确认用此备份替换工作区').disabled,false);
});

test('short publication inspection rejects another curator URL and uses the frozen group target',async t=>{
  const h=await harness({configure(workspace,{a}){workspace.saveDraft({taskId:a.id,language:'schinese',kind:'short',text:'Short frozen body',expectedRevision:0});const attempt=workspace.preparePublication(a.id,'schinese','short');workspace.recordPublication({taskId:a.id,attemptId:attempt.id,state:'submitted_unknown',resultUrl:'https://store.steampowered.com/curator/99999999/'});},handlers:{init:(_payload,context)=>({...context.snapshot(),fixture:false})}});t.after(h.dispose);await h.nav('publication');await h.click('打开结果页核对');
  assert.equal(h.calls.find(c=>c.command==='external.open').payload.url,'https://store.steampowered.com/curator/45134494/');assert.equal(h.calls.filter(c=>c.command==='publication.open').length,0);
});
