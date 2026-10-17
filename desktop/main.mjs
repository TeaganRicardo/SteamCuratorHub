import { app, BrowserWindow, ipcMain, protocol, net, safeStorage, dialog, shell, clipboard, powerMonitor, Menu } from 'electron';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { mkdirSync, readFileSync, writeFileSync, statSync } from 'node:fs';
import { publicHttps } from './workspace.mjs';
import { openWorkspace } from './recovery.mjs';
import { SecretStore } from './secrets.mjs';
import { SteamService } from './services/steam.mjs';
import { NativeStatsService } from './services/native-stats.mjs';
import { nativeBinaryPath } from './native/build.mjs';
import { ResearchService } from './services/research.mjs';
import { DemoSteamService, DemoResearchService } from './services/demo.mjs';
import { PublishingService } from './publishing.mjs';
import { AppController } from './controller.mjs';
import { session } from 'electron';

protocol.registerSchemesAsPrivileged([{ scheme: 'hub', privileges: { standard: true, secure: true, supportFetchAPI: true } }]);
app.setName('Steam Curator Hub');
const fixture = process.argv.includes('--fixture');
const smoke = process.argv.includes('--smoke');
const fixtureDir = process.argv.find(value => value.startsWith('--fixture-data='))?.slice('--fixture-data='.length);
if (fixture && fixtureDir) { mkdirSync(fixtureDir, { recursive: true, mode: 0o700 }); app.setPath('userData', resolve(fixtureDir)); }
if (smoke && !fixture) throw new Error('Smoke tests require explicit fixture mode');
let window;
let controller;
let quitting = false;
let quitRequested = false;
function safeError(error) {
  const code = typeof error?.code === 'string' ? error.code : error?.message;
  return { ok: false, error: typeof code === 'string' && /^[a-z][a-z0-9_]{0,120}$/.test(code) ? code : 'operation_failed',
    ...(error?.mutationOutcome ? { mutationOutcome: error.mutationOutcome } : {}) };
}
function emit(event) { if (window && !window.isDestroyed()) window.webContents.send('hub:event', event); }
async function start() {
  const root = app.getAppPath();
  const assets = new Map(['index.html', 'app.mjs', 'style.css', 'bbcode.mjs'].map(name => [`/${name}`, join(root, 'desktop', 'renderer', name)]));
  protocol.handle('hub', request => {
    const url = new URL(request.url);
    const path = url.hostname === 'app' ? assets.get(url.pathname) : null;
    return path ? net.fetch(pathToFileURL(path).href) : new Response('Not found', { status: 404 });
  });
  const dataPath = app.getPath('userData');
  const workspace = await openWorkspace({ path: join(dataPath, fixture ? 'fixture-workspace.json' : 'workspace.json'), dialog });
  if (!workspace) { app.quit(); return; }
  const fixtureKeys = new Map();
  const secrets = fixture ? { status: () => ({ encryptionAvailable: false, aiKeyConfigured: false, webApiKeyConfigured: false, searchKeyConfigured: false }),
    read: key => fixtureKeys.get(key) ?? '', write: (key, value) => fixtureKeys.set(key, value) } : new SecretStore(join(dataPath, 'credentials.json'), safeStorage);
  let steam;
  const steamEvents = event => controller ? controller.steamEvent(event).catch(() => emit({ type: 'app.error', data: 'workspace_save_failed' })) : emit(event);
  steam = fixture ? new DemoSteamService({ onEvent: steamEvents }) : new SteamService({ onEvent: steamEvents });
  const native = new NativeStatsService({ binaryPath: nativeBinaryPath, fixture, onEvent: emit });
  const research = fixture ? new DemoResearchService() : new ResearchService();
  const publishing = new PublishingService({ BrowserWindow, session, workspace, onEvent: emit });
  controller = new AppController({ workspace, steam, native, research, secrets, publishing, onEvent: emit, fixture });
  if (fixture && !workspace.state.activeSteamId) {
    workspace.bindAccount('76561198000000000');
    const task = workspace.createTask({ appId: 1001, title: '示例：星海旅人', groupId: '45134494', targetMinutes: 120 });
    workspace.saveDraft({ taskId: task.id, language: 'schinese', kind: 'long', text: '[h2]在探索中寻找自己的节奏[/h2]\n\n这是一段可编辑的演示草稿。你可以体验资料整理、短评摘要和发布准备流程。\n\n[list]\n[*]示例优点：探索与叙事相互呼应\n[*]示例不足：后段节奏仍有打磨空间\n[/list]', expectedRevision: 0 });
    workspace.saveDraft({ taskId: task.id, language: 'schinese', kind: 'short', text: '一段以探索和叙事为核心的示例旅程，适合希望慢下来发掘细节的玩家。', expectedRevision: 0 });
  }
  window = new BrowserWindow({ width: 1440, height: 950, minWidth: 1050, minHeight: 720, title: fixture ? 'Steam Curator Hub · 演示模式' : 'Steam Curator Hub',
    backgroundColor: '#0e1420', titleBarStyle: 'hiddenInset', show: !smoke,
    webPreferences: { preload: join(root, 'desktop', 'preload.cjs'), nodeIntegration: false, contextIsolation: true, sandbox: true, webSecurity: true } });
  window.on('close', event => { if (!quitting) { event.preventDefault(); app.quit(); } });
  window.webContents.on('render-process-gone', () => {
    quitting = true; Promise.resolve(controller?.close()).finally(() => app.quit());
  });
  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  window.webContents.on('will-navigate', (event, url) => { if (url !== 'hub://app/index.html') event.preventDefault(); });
  session.defaultSession.setPermissionRequestHandler((_contents, _permission, callback) => callback(false));
  session.defaultSession.setPermissionCheckHandler(() => false);
  ipcMain.handle('hub:command', async (event, command, payload = {}) => {
    if (event.sender !== window?.webContents || event.senderFrame !== event.sender.mainFrame || event.senderFrame.url !== 'hub://app/index.html') return safeError(new Error('untrusted_sender'));
    try {
      if (typeof command !== 'string') throw new Error('invalid_command');
      let value;
      if (command === 'app.request-close') {
        app.quit(); value = { requested: true };
      } else if (command === 'app.close-ready') {
        if (!quitRequested || typeof payload.allow !== 'boolean') throw new Error('close_not_requested');
        quitRequested = false;
        if (payload.allow) { quitting = true; await controller.close(); app.quit(); }
        value = { closing: payload.allow };
      } else if (command === 'external.open') {
        const url = publicHttps(payload.url); await shell.openExternal(url); value = { opened: true };
      } else if (command === 'clipboard.write') {
        if (typeof payload.text !== 'string' || payload.text.length > 100000) throw new Error('invalid_text');
        clipboard.writeText(payload.text); value = { copied: true };
      } else if (command === 'backup.export') {
        const selection = await dialog.showSaveDialog(window, { title: '导出工作区备份（不含凭据）', defaultPath: `steam-curator-hub-${new Date().toISOString().slice(0, 10)}.json`, filters: [{ name: 'JSON', extensions: ['json'] }] });
        if (selection.canceled) value = { cancelled: true };
        else { writeFileSync(selection.filePath, JSON.stringify(workspace.exportBackup(), null, 2), { mode: 0o600 }); value = { exported: true }; }
      } else if (command === 'backup.import') {
        if (steam.runs().some(run => run.state === 'running') || controller.researchRequests.size || workspace.snapshot().tasks.some(task => task.operations?.some(op => op.state === 'applying'))) throw new Error('stop_operations_before_import');
        const selection = await dialog.showOpenDialog(window, { title: '导入工作区备份', properties: ['openFile'], filters: [{ name: 'JSON', extensions: ['json'] }] });
        if (selection.canceled) value = { cancelled: true };
        else {
          if (statSync(selection.filePaths[0]).size > 20_000_000) throw new Error('workspace_too_large');
          const raw = readFileSync(selection.filePaths[0], 'utf8'); if (Buffer.byteLength(raw) > 20_000_000) throw new Error('workspace_too_large');
          const decision = await dialog.showMessageBox(window, { type: 'question', buttons: ['取消', '导入并替换'], defaultId: 0, cancelId: 0,
            message: '用备份替换当前工作区？', detail: '当前数据保留为本机 previous 备份。凭据不会导入，发布成功记录需重新核实。' });
          if (decision.response !== 1) value = { cancelled: true };
          else {
            const release = controller.acquireMaintenance();
            try {
              await publishing.logout(); controller.assertIdle();
              workspace.importBackup(JSON.parse(raw)); emit({ type: 'workspace.changed' }); value = controller.snapshot();
            } finally { release(); }
          }
        }
      } else value = await controller.invoke(command, payload);
      return { ok: true, value };
    } catch (error) { return safeError(error); }
  });
  powerMonitor.on('suspend', () => { Promise.resolve(steam.suspend?.()).catch(() => {}); });
  await window.loadURL('hub://app/index.html');
  if (smoke) await runSmoke(window, controller);
}
async function runSmoke(win, application) {
  const output = process.argv.find(value => value.startsWith('--smoke-output='))?.slice('--smoke-output='.length);
  const result = { mode: 'fixture', packaged: app.isPackaged, checks: [], version: app.getVersion() };
  try {
    const value = await win.webContents.executeJavaScript(`(async () => {
      const initial = await window.hub.invoke('init');
      if (!initial.fixture || !initial.workspace.tasks.length) throw new Error('fixture_bootstrap_failed');
      const task = initial.workspace.tasks[0];
      const run = await window.hub.invoke('runs.start', {taskId:task.id,stopAfterMinutes:15});
      await window.hub.invoke('runs.stop', {appId:task.appId});
      const native = await window.hub.invoke('stats.read', {taskId:task.id});
      const research = await window.hub.invoke('research.run', {taskId:task.id,language:'schinese'});
      const attempt = await window.hub.invoke('publication.prepare', {taskId:task.id,language:'schinese',kind:'short'});
      const tabs = [...document.querySelectorAll('[data-tab]')].map(node => node.dataset.tab);
      const headings={library:'游戏库',tasks:'评测任务',runs:'运行与时长',achievements:'成就与统计',research:'资料研究',editor:'评测编辑',settings:'设置'};
      for(const name of Object.keys(headings)) {
        const button=document.querySelector('[data-tab="'+name+'"]'); if(!button)throw new Error('missing_tab_'+name);button.click();
        await new Promise(resolve=>setTimeout(resolve,10));
        if(document.querySelector('#main h1')?.textContent!==headings[name])throw new Error('tab_render_failed_'+name);
      }
      const state = await window.hub.invoke('init');
      return {tabs,runState:run.state,nativeSource:native.source,achievements:native.achievements.length,
        researchSources:research.sources.length,shortCount:Array.from(attempt.text).length,
        persistedTasks:state.workspace.tasks.length,hasNode:typeof process!=='undefined',
        title:document.title,bodyTextLength:document.body.textContent.length};
    })()`);
    if (value.hasNode || value.shortCount >= 200 || !value.achievements || !value.researchSources) throw new Error('smoke_assertion_failed');
    result.checks = value; result.status = 'passed';
  } catch (error) { result.status = 'failed'; result.error = safeError(error).error; process.exitCode = 1; }
  if (output) { mkdirSync(resolve(output, '..'), { recursive: true }); writeFileSync(output, JSON.stringify(result, null, 2)); }
  await application.close(); quitting = true; app.exit(result.status === 'passed' ? 0 : 1);
}
if (!app.requestSingleInstanceLock()) app.quit();
else {
  app.whenReady().then(async () => {
    Menu.setApplicationMenu(Menu.buildFromTemplate([
      ...(process.platform === 'darwin' ? [{ label: app.name, submenu: [{ role: 'about' }, { type: 'separator' }, { role: 'quit' }] }] : []),
      { label: '编辑', submenu: [{ role: 'undo' }, { role: 'redo' }, { type: 'separator' }, { role: 'cut' }, { role: 'copy' }, { role: 'paste' }, { role: 'selectAll' }] },
      { label: '视图', submenu: [{ role: 'resetZoom' }, { role: 'zoomIn' }, { role: 'zoomOut' }, { role: 'togglefullscreen' }] }
    ]));
    await start();
  }).catch(error => { console.error(safeError(error).error); app.exit(1); });
  app.on('before-quit', event => {
    if (quitting) return;
    event.preventDefault();
    if (!window || window.isDestroyed()) { quitting = true; Promise.resolve(controller?.close()).finally(() => app.quit()); return; }
    if (!quitRequested) { quitRequested = true; emit({ type: 'app.requestClose' }); }
  });
  app.on('window-all-closed', () => app.quit());
  app.on('second-instance', () => { if (window) { if (window.isMinimized()) window.restore(); window.focus(); } });
}
