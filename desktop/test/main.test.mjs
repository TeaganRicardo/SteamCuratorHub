import test from 'node:test';
import assert from 'node:assert/strict';
import * as modules from 'node:module';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';

// The actual main/controller/repository/recovery/publishing modules execute in an isolated child.
// Electron, protocol playtime and native workers are synthetic; no userData/Steam/network is opened.
async function exercise(scenario) {
  const { registerHooks } = await import('node:module');
  const { EventEmitter } = await import('node:events');
  const { mkdtempSync, writeFileSync, rmSync } = await import('node:fs');
  const { tmpdir } = await import('node:os');
  const { join } = await import('node:path');
  const { pathToFileURL } = await import('node:url');
  const dir = mkdtempSync(join(tmpdir(), 'sch-main-fixture-'));
  const h = globalThis.__mainHarness = { windows: [], timeline: [], partitions: new Map(), delayStart: false, exited: false };
  let command; let invoke; let chooseFile; let selected = false;
  const app = new EventEmitter();
  Object.assign(app, { name: 'Synthetic', setName(name) { this.name = name; }, setPath() {}, getPath: () => dir,
    getAppPath: () => process.cwd(), requestSingleInstanceLock: () => true, whenReady: () => Promise.resolve(),
    getVersion: () => 'synthetic', exit() { h.exited = true; }, quit() {
      if (h.finalizing || h.exited) return;
      const event = { prevented: false, preventDefault() { this.prevented = true; } };
      app.emit('before-quit', event); if (event.prevented) return;
      h.finalizing = true;
      for (const win of [...h.windows]) if (!win.destroyed) win.close();
      if (h.windows.some(win => !win.destroyed)) { h.finalizing = false; return; }
      h.exited = true;
    } });
  class Window extends EventEmitter {
    constructor(options) {
      super(); this.options = options; this.destroyed = false; h.windows.push(this);
      this.webContents = new EventEmitter(); this.webContents.mainFrame = { url: 'hub://app/index.html' };
      this.webContents.setWindowOpenHandler = fn => { this.openHandler = fn; };
      this.webContents.getURL = () => this.url;
      this.webContents.send = (_channel, event) => {
        if (event.type === 'app.requestClose') h.closeRequests = (h.closeRequests ?? 0) + 1;
      };
      this.webContents.executeJavaScript = async () => true;
    }
    isDestroyed() { return this.destroyed; }
    async loadURL(url) { this.url = url; this.webContents.mainFrame.url = url; h.timeline.push('window.loaded'); }
    isMinimized() { return false; }
    focus() {}
    close() {
      if (this.destroyed) return;
      const event = { prevented: false, preventDefault() { this.prevented = true; } };
      this.emit('close', event); if (event.prevented) return;
      this.destroyed = true; this.emit('closed');
      if (h.windows.every(win => win.destroyed)) app.emit('window-all-closed');
    }
  }
  const createPartition = () => ({ setPermissionRequestHandler() {}, setPermissionCheckHandler() {},
    async clearStorageData() { if (h.blockClear) { h.clearStarted = true; await h.blockClear; } }, async clearCache() {} });
  const electron = { app, BrowserWindow: Window, ipcMain: { handle(_name, fn) { command = fn; } },
    protocol: { registerSchemesAsPrivileged() {}, handle() {} }, net: { fetch() { throw new Error('fixture_network_forbidden'); } },
    safeStorage: { isEncryptionAvailable: () => true }, dialog: { showOpenDialog() { selected = true; return new Promise(resolve => { chooseFile = resolve; }); },
      async showMessageBox(options) { return { response: options.title === '工作区需要恢复' ? 0 : 1 }; } },
    shell: { async openExternal() { h.externalOpened = true; } }, clipboard: { writeText() {} }, powerMonitor: new EventEmitter(),
    Menu: { setApplicationMenu() {}, buildFromTemplate(value) { return value; } },
    session: { defaultSession: createPartition(), fromPartition(name) {
      if (!h.partitions.has(name)) h.partitions.set(name, createPartition()); return h.partitions.get(name);
    } } };
  globalThis.__electron = electron;
  class DemoSteam {
    constructor({ sessionStore } = {}) {
      this.items = []; globalThis.__mainHarness.steam = this;
      globalThis.__mainHarness.hasSessionStore = typeof sessionStore?.readSteamSession === 'function' && typeof sessionStore?.writeSteamSession === 'function';
    }
    async restore() { globalThis.__mainHarness.timeline.push('steam.restore'); globalThis.__mainHarness.restores = (globalThis.__mainHarness.restores ?? 0) + 1; }
    status() { return { state: 'connected', steamId: '76561198000000000', generation: 0 }; }
    runs() { return this.items; }
    async startRun(value) {
      const h = globalThis.__mainHarness;
      if (h.delayStart) await new Promise(resolve => { h.releaseStart = resolve; });
      const run = { ...value, state: 'running' }; this.items.push(run); return run;
    }
    async close() { globalThis.__mainHarness.timeline.push('steam.close'); this.items = []; }
    async suspend() { return this.close(); }
  }
  class DemoResearch {}
  class Native {
    constructor() { globalThis.__mainHarness.native = this; }
    async close() { this.closed = true; globalThis.__mainHarness.timeline.push('native.close'); }
  }
  const electronSource = Object.keys(electron).map(name => `export const ${name}=globalThis.__electron.${name};`).join('\n');
  const demoSource = `export const DemoSteamService=${DemoSteam.toString()}; export const DemoResearchService=${DemoResearch.toString()};`;
  const nativeSource = `export const NativeStatsService=${Native.toString()};`;
  const dataUrl = value => 'data:text/javascript,' + encodeURIComponent(value);
  registerHooks({ resolve(specifier, context, next) {
    if (specifier === 'electron') return { url: dataUrl(electronSource), shortCircuit: true };
    if (specifier.endsWith('/services/demo.mjs')) return { url: dataUrl(demoSource), shortCircuit: true };
    if (scenario === 'auth_restore' && specifier.endsWith('/services/steam.mjs')) return { url: dataUrl(`export const SteamService=${DemoSteam.toString()};`), shortCircuit: true };
    if (specifier.endsWith('/services/native-stats.mjs')) return { url: dataUrl(nativeSource), shortCircuit: true };
    return next(specifier, context);
  } });
  if (scenario !== 'auth_restore') process.argv.push('--fixture', `--fixture-data=${dir}`);
  const flush = async predicate => { for (let i = 0; i < 100 && !predicate(); i++) await new Promise(setImmediate); };
  try {
    if (scenario === 'recovery_exit') writeFileSync(join(dir, 'fixture-workspace.json'), '{bad');
    await import(pathToFileURL(join(process.cwd(), 'desktop/main.mjs')).href);
    await flush(() => command || h.exited);
    if (scenario === 'recovery_exit') return { exited: h.exited, windows: h.windows.length };
    const sender = h.windows[0].webContents; const event = { sender, senderFrame: sender.mainFrame };
    invoke = (name, payload = {}) => command(event, name, payload);
    const initial = (await invoke('init')).value; const task = initial.workspace.tasks[0];
    if (scenario === 'auth_restore') return { restores: h.restores, hasSessionStore: h.hasSessionStore,
      loadedBeforeRestore: h.timeline.indexOf('window.loaded') < h.timeline.indexOf('steam.restore'), tasks: initial.workspace.tasks.length };
    if (scenario === 'ipc') {
      const foreign = await command({ sender: { mainFrame: {} }, senderFrame: {} }, 'task.update', { taskId: task.id, source: 'BAD' });
      const subframe = await command({ sender, senderFrame: { url: 'hub://app/index.html' } }, 'task.update', { taskId: task.id, source: 'BAD' });
      sender.mainFrame.url = 'https://attacker.example/';
      const foreignOrigin = await command(event, 'task.update', { taskId: task.id, source: 'BAD' });
      sender.mainFrame.url = 'hub://app/index.html';
      return { foreign: foreign.error, subframe: subframe.error, foreignOrigin: foreignOrigin.error,
        source: (await invoke('init')).value.workspace.tasks[0].source };
    }
    if (scenario === 'quit_publish_window') {
      const attempt = (await invoke('publication.prepare', { taskId: task.id, language: 'schinese', kind: 'long' })).value;
      await invoke('publication.open', { taskId: task.id, attemptId: attempt.id });
      await invoke('runs.start', { taskId: task.id, stopAfterMinutes: 15 });
      h.windows[0].close(); await flush(() => h.closeRequests);
      const beforeReady = { exited: h.exited, mainClosed: h.windows[0].destroyed,
        runs: h.steam.runs().length, nativeClosed: Boolean(h.native.closed), closeRequests: h.closeRequests };
      const ready = await invoke('app.close-ready', { allow: true }); await flush(() => h.exited);
      return { exited: h.exited, windowsClosed: h.windows.every(win => win.destroyed),
        runs: h.steam.runs().length, nativeClosed: h.native.closed, timeline: h.timeline, ready: ready.ok, beforeReady };
    }
    if (scenario === 'quit_cancel') {
      const unsolicited = await invoke('app.close-ready', { allow: true });
      h.windows[0].close(); app.quit(); h.windows[0].close(); await flush(() => h.closeRequests);
      const coalesced = h.closeRequests;
      const cancelled = await invoke('app.close-ready', { allow: false });
      const started = await invoke('runs.start', { taskId: task.id, stopAfterMinutes: 15 });
      const afterCancel = { exited: h.exited, nativeClosed: Boolean(h.native.closed), mainClosed: h.windows[0].destroyed,
        running: started.value?.state, closeRequests: h.closeRequests };
      await invoke('app.request-close'); await flush(() => h.closeRequests > coalesced);
      await invoke('app.close-ready', { allow: true }); await flush(() => h.exited);
      return { unsolicited: unsolicited.error, coalesced, cancelled: cancelled.value?.closing, afterCancel,
        closeRequests: h.closeRequests, exited: h.exited };
    }
    const backup = structuredClone(initial.workspace); backup.tasks = [];
    const backupPath = join(dir, 'backup.json'); writeFileSync(backupPath, JSON.stringify(backup));
    if (scenario === 'maintenance') {
      const attempt = (await invoke('publication.prepare', { taskId: task.id, language: 'schinese', kind: 'long' })).value;
      await invoke('publication.open', { taskId: task.id, attemptId: attempt.id });
      let release; h.blockClear = new Promise(resolve => { release = resolve; });
      const checking = invoke('backup.preview'); await flush(() => selected);
      chooseFile({ canceled: false, filePaths: [backupPath] }); const preview = await checking;
      const importing = invoke('backup.restore', { previewId: preview.value.previewId }); await flush(() => h.clearStarted);
      const blocked = await invoke('runs.start', { taskId: task.id });
      release(); const imported = await importing;
      return { blocked: blocked.error, imported: imported.ok, runs: h.steam.runs().length,
        tasks: (await invoke('init')).value.workspace.tasks.length };
    }
    h.delayStart = true;
    const running = invoke('runs.start', { taskId: task.id, stopAfterMinutes: 15 });
    await flush(() => h.releaseStart);
    const checking = invoke('backup.preview'); await flush(() => selected);
    if (scenario === 'backup_race') { h.releaseStart(); await running; }
    chooseFile({ canceled: false, filePaths: [backupPath] });
    const preview = await checking;
    const tasksBeforeRestore = (await invoke('init')).value.workspace.tasks.length;
    const outcome = await invoke('backup.restore', { previewId: preview.value.previewId });
    if (scenario === 'backup_pending') { h.releaseStart(); await running; }
    const final = (await invoke('init')).value;
    return { previewAccepted: preview.ok, tasksBeforeRestore, importAccepted: outcome.ok, error: outcome.error, tasks: final.workspace.tasks.length,
      runs: final.runs.filter(run => run.state === 'running').length };
  } finally {
    app.quit();
    if (invoke && !h.exited) await invoke('app.close-ready', { allow: true });
    await flush(() => h.exited); rmSync(dir, { recursive: true, force: true });
  }
}

function run(scenario) {
  const result = spawnSync(process.execPath, ['--input-type=module', '-'], {
    cwd: fileURLToPath(new URL('../../', import.meta.url)),
    input: `(${exercise.toString()})(${JSON.stringify(scenario)}).then(value=>console.log('SCH_MAIN_RESULT='+JSON.stringify(value))).catch(error=>{console.error(error.stack);process.exitCode=1;});`,
    encoding: 'utf8', timeout: 30000, maxBuffer: 1024 * 1024
  });
  assert.equal(result.status, 0, `${result.stderr}\n${result.stdout}`);
  const marker = result.stdout.split('\n').find(line => line.startsWith('SCH_MAIN_RESULT='));
  assert.ok(marker, result.stdout); return JSON.parse(marker.slice('SCH_MAIN_RESULT='.length));
}
const options = { skip: typeof modules.registerHooks !== 'function' };

test('actual main previews without writes and rejects restore if a pending run starts during selection', options, () => {
  const result = run('backup_race');
  assert.deepEqual(result, { previewAccepted: true, tasksBeforeRestore: 1, importAccepted: false, error: 'stop_operations_before_import', tasks: 1, runs: 1 });
});
test('actual main also rejects import while run preflight remains pending at the final gate', options, () => {
  const result = run('backup_pending');
  assert.deepEqual(result, { previewAccepted: true, tasksBeforeRestore: 1, importAccepted: false, error: 'stop_operations_before_import', tasks: 1, runs: 1 });
});
test('actual main maintenance lock rejects new business commands while publication storage clears', options, () => {
  const result = run('maintenance');
  assert.deepEqual(result, { blocked: 'workspace_maintenance_in_progress', imported: true, runs: 0, tasks: 0 });
});
test('actual main binds IPC to its main renderer origin and frame', options, () => {
  assert.deepEqual(run('ipc'), { foreign: 'untrusted_sender', subframe: 'untrusted_sender', foreignOrigin: 'untrusted_sender', source: '' });
});
test('closing main waits for renderer save approval, then cleans protocol/native owners and publishing windows', options, () => {
  const result = run('quit_publish_window');
  assert.deepEqual(result.beforeReady, { exited: false, mainClosed: false, runs: 1, nativeClosed: false, closeRequests: 1 });
  assert.equal(result.ready, true);
  assert.equal(result.exited, true); assert.equal(result.windowsClosed, true); assert.equal(result.runs, 0);
  assert.equal(result.nativeClosed, true); assert.ok(result.timeline.includes('steam.close'));
});
test('failed renderer save cancels quit without closing services and permits a fresh close handshake', options, () => {
  assert.deepEqual(run('quit_cancel'), { unsolicited: 'close_not_requested', coalesced: 1, cancelled: false,
    afterCancel: { exited: false, nativeClosed: false, mainClosed: false, running: 'running', closeRequests: 1 },
    closeRequests: 2, exited: true });
});
test('startup recovery exit does not create a window or start a Steam owner', options, () => {
  assert.deepEqual(run('recovery_exit'), { exited: true, windows: 0 });
});
test('normal startup injects the encrypted store and starts auth restore after the renderer loads without starting runs', options, () => {
  assert.deepEqual(run('auth_restore'), { restores: 1, hasSessionStore: true, loadedBeforeRestore: true, tasks: 0 });
});

function rendererDraftHarness(values, invoke) {
  const source = readFileSync(new URL('../renderer/app.mjs', import.meta.url), 'utf8');
  const draftCode = source.slice(source.indexOf('async function flushDraft(value) {'), source.indexOf('function edited(value,textarea){'));
  const closeCode = source.slice(source.indexOf('async function requestClose(){'), source.indexOf('window.hub.onEvent(event=>{'));
  const dialogs = []; const errors = [];
  const sandbox = { buffers: new Map(values.map(value => [value.key, value])),
    state: { workspace: { tasks: [] } }, taskId: null, language: 'english', clearTimeout,
    document: { body: { inert: false }, querySelector: () => null }, window: { hub: { invoke } },
    fail: error => errors.push(error), dialog: (...args) => dialogs.push(args), h: (...args) => args,
    button: (...args) => args, $: () => ({ close() {} }) };
  const functions = runInNewContext(`${draftCode}\n${closeCode}\n({flushDraft,flushAll,requestClose})`, sandbox);
  return { ...functions, dialogs, errors, body: sandbox.document.body };
}
function draftValue(key, text) {
  return { key, taskId: key, language: 'english', kind: 'long', text, revision: 0, dirty: true, saving: null };
}
function deferred() { let resolve; const promise = new Promise(done => { resolve = done; }); return { promise, resolve }; }
async function settle() { for (let i = 0; i < 8; i++) await Promise.resolve(); }

test('actual renderer shares one save drain while text changes during a pending autosave', async () => {
  const value = draftValue('first', 'First draft'); const calls = [];
  const renderer = rendererDraftHarness([value], async (command, payload) => {
    assert.equal(command, 'draft.save'); const pending = deferred(); calls.push({ payload, ...pending }); return pending.promise;
  });
  const first = renderer.flushDraft(value); value.text = 'Latest draft';
  const second = renderer.flushDraft(value);
  calls[0].resolve({ id: value.taskId, drafts: { english: { long: { revision: 1 } } } }); await settle();
  assert.equal(calls.length, 2); assert.deepEqual(calls.map(call => call.payload.expectedRevision), [0, 1]);
  calls[1].resolve({ id: value.taskId, drafts: { english: { long: { revision: 2 } } } });
  await Promise.all([first, second]); assert.equal(value.dirty, false); assert.equal(value.revision, 2);
});

test('actual renderer cancels the close handshake for an unresolved draft conflict and retains local text', async () => {
  const value = draftValue('first', 'Unsaved local text'); value.conflict = true; const commands = [];
  const renderer = rendererDraftHarness([value], async (command, payload) => { commands.push({ command, ...payload }); });
  await renderer.requestClose();
  assert.deepEqual(commands, [{ command: 'app.close-ready', allow: false }]);
  assert.equal(value.dirty, true); assert.equal(value.text, 'Unsaved local text');
  assert.equal(renderer.body.inert, false);
  assert.equal(renderer.dialogs.length, 1); assert.equal(renderer.errors.length, 1);
});

test('renderer close does not approve while an earlier buffer is edited during another buffer save', async () => {
  const first = draftValue('first', 'First draft'); const second = draftValue('second', 'Second draft');
  const wait = deferred(); const approvalStates = [];
  const renderer = rendererDraftHarness([first, second], async (command, payload) => {
    if (command === 'app.close-ready') { approvalStates.push({ allow: payload.allow, dirty: first.dirty || second.dirty }); return; }
    if (payload.taskId === second.taskId) await wait.promise;
    return { id: payload.taskId, drafts: { english: { long: { revision: payload.expectedRevision + 1 } } } };
  });
  const closing = renderer.requestClose(); await settle();
  assert.equal(renderer.body.inert, true);
  first.text = 'Edited while closing'; first.dirty = true; wait.resolve(); await closing;
  assert.equal(approvalStates.some(state => state.allow && state.dirty), false);
  assert.equal(renderer.body.inert, true);
});
