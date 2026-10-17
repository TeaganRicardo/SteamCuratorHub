import { renderBBCode } from './bbcode.mjs';

const $ = selector => document.querySelector(selector);
const main = $('#main');
const tabs = [ ['library','▦','游戏库'], ['tasks','▤','评测任务'], ['runs','▷','运行与时长'], ['achievements','◇','成就与统计'], ['research','⌕','资料研究'], ['editor','✎','评测编辑'], ['settings','⚙','设置'] ];
const labels = { connected:'已连接', logged_out:'未连接', logging_in:'正在连接', guard_required:'等待 Steam Guard', error:'连接中断', closed:'已关闭',
  running:'运行请求中', stopped:'已请求停止', paused:'已暂停', unknown:'待核实', outcome_unknown:'结果未知', verified:'已回读核实', failed:'失败',
  prepared:'已准备', filled:'已填入页面', submitted_unknown:'提交结果待核实', user_confirmed:'用户已确认', imported_unverified:'导入记录待核实',
  active:'进行中', completed:'已完成', archived:'已归档', collected:'已采集', operator_acknowledged:'已人工检查', fixture_only:'仅演示数据' };
const errors = {
  bind_account_first:'请先连接 Steam，或在设置中绑定 SteamID64。绑定 ID 只确定数据归属。',
  task_already_exists:'此账号已有该游戏的任务，请切换到已有任务。', draft_changed:'草稿已发生变化，当前文字已保留。请检查最新版本后重新保存。',
  short_review_must_be_under_200:'短评需少于 200 字；空格、换行和 Unicode 码点均计数。', short_review_plain_text_only:'短评只保留纯文字，链接请放在独立全文链接字段。',
  empty_short_review:'请先填写短评正文。', empty_long_review:'请先填写长评正文。', ai_not_configured:'请在设置中配置 AI API 密钥和可用模型。',
  previous_publication_needs_verification:'上次发布结果仍未知。请先核对结果或明确记录失败，避免重复发布。',
  publication_changed:'草稿、群组或账号已改变。请根据新内容重新准备发布。', steam_not_connected:'请先登录 Steam 协议会话。',
  steam_account_mismatch:'运行账号与任务归属不一致。请检查 Steam 账号。', native_steam_account_mismatch:'本机 Steam 客户端账号与任务不一致，已停止操作。',
  credential_encryption_unavailable_use_session_only:'系统加密不可用。可取消“持久保存密钥”，仅在本次应用会话中使用。',
  native_unknown_result_needs_acknowledgement:'有结果未知的原生写入。请先刷新并检查历史记录中的该次操作。',
  stats_preview_expired:'更改预览已失效，请重新读取并准备。', no_changes_selected:'请先选择成就更改或修改统计值。',
  operation_context_changed:'操作期间账号或任务上下文改变，结果没有写入当前任务。',
  operation_failed:'操作未完成。请检查 Steam 客户端、账号与配置；未知写入应先回读核对。', invalid_url:'请输入有效的 HTTPS 链接。',
  stop_operations_before_import:'请先停止运行、研究和写入操作，再导入备份。', logout_before_binding_another_account:'请先退出当前 Steam 协议账号，再绑定其他 SteamID64。',
  long_review_result_url_mismatch:'请填写对应账号、游戏的 Steam 个人评测链接。', publication_unknown:'该次发布结果未知，先核对结果后再继续。'
};
let state;
let activeTab = 'library';
let taskId = null;
let language = 'schinese';
let library = [];
let catalogMode = 'library';
let librarySort = 'name';
let libraryQuery = '';
let nativeSnapshot = null;
let nativeTaskId = null;
let selectedAchievements = new Set();
let statChanges = new Map();
let achievementQuery = '';
let achievementFilter = 'all';
let achievementSort = 'global';
let hideHidden = true;
let achievementAction = true;
let toastTimer;
let refreshSequence = 0;
let uiEpoch = 0;
let nativeReadSequence = 0;
const buffers = new Map();
const suggestions = new Map();
const busyTasks = new Set();
function researchWarning(value){
  const messages={ai_not_configured:'尚未配置 Gemini 密钥或模型，已保存取得的资料。',missing_search_configuration:'尚未配置 Brave 搜索密钥，本次没有站外媒体资料。',media_coverage_incomplete:'媒体资料覆盖不足，生成内容仅可依据已取得的来源。',steam_reviews_empty:'未取得 Steam 评论样本。',steam_review_sample_limited:'Steam 评论仅为有界样本，不代表所有玩家观点。',insufficient_evidence:'可用资料不足，未生成初稿。',media_discovery_no_sources:'本次检索未找到可读取的媒体来源。',steam_review_context_truncated:'Steam 评论样本已按上下文上限截取。'};
  if(messages[value])return messages[value];
  if(value.startsWith('media_read_failed:'))return `来源 ${value.split(':')[1]} 未取得正文。`;
  if(value.startsWith('media_identity_'))return `作品身份或版本尚待核对：${value.split(':')[1]??''}，未混入已确认资料。`;
  if(value.startsWith('media_edition_unverified:'))return `来源 ${value.split(':')[1]} 名称匹配，但版本仍待核对。`;
  if(value.startsWith('media_discovery_'))return `站外搜索未完成（${value.slice(16)}），已取得的资料仍保留。`;
  if(value.startsWith('ai_'))return `AI 生成未完成（${value}），原文和已有草稿仍保留。`;
  return value;
}

function h(tag, attrs = {}, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs)) {
    if (value === undefined || value === null) continue;
    if (key === 'class') node.className = value;
    else if (key.startsWith('on')) node.addEventListener(key.slice(2).toLowerCase(), value);
    else if (key === 'checked' || key === 'disabled' || key === 'readOnly') node[key] = Boolean(value);
    else if (key === 'value') node.value = value;
    else node.setAttribute(key, value);
  }
  for (const child of children.flat(Infinity)) if (child !== undefined && child !== null) node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  return node;
}
function toast(value, failed = false) {
  const node = $('#toast'); node.textContent = value; node.classList.add('show'); node.classList.toggle('error', failed);
  clearTimeout(toastTimer); toastTimer = setTimeout(() => node.classList.remove('show'), failed ? 9000 : 4500);
}
function fail(error) { toast(errors[error.code ?? error.message] ?? `操作未完成：${error.code ?? error.message}`, true); }
function button(label, action, className = '') {
  const node = h('button', { class: className, type: 'button' }, label);
  if (action) node.addEventListener('click', async () => {
    node.disabled = true;
    try { await action(); } catch (error) { fail(error); } finally { node.disabled = false; }
  });
  return node;
}
function pill(text, tone = '') { return h('span', { class: `pill ${tone}` }, labels[text] ?? text); }
function note(value, info = false) { return h('div', { class: `banner${info ? ' info' : ''}` }, value); }
function fmtMinutes(value) { return Number.isFinite(value) ? `${(value / 60).toFixed(value % 60 ? 1 : 0)} 小时` : '暂无回读'; }
function field(title, node, hint) { return h('label', {}, title, node, hint ? h('span', { class: 'inline-note' }, hint) : null); }
function select(options, value, onChange) {
  const node = h('select');
  for (const option of options) node.append(h('option', { value: Array.isArray(option) ? option[0] : option }, Array.isArray(option) ? option[1] : option));
  node.value = value;
  if (onChange) node.addEventListener('change', onChange);
  return node;
}
function heading(title, subtitle, ...actions) {
  return h('div', { class: 'page-heading' }, h('div', {}, h('h1', {}, title), h('p', { class: 'subtitle' }, subtitle)), h('div', { class: 'actions' }, actions));
}
function empty(title, text, action) { return h('div', { class: 'empty' }, h('div', { class: 'symbol' }, '◇'), h('h2', {}, title), h('p', {}, text), action); }
function task() { return state?.workspace.tasks.find(item => item.id === taskId); }
function group(item = task()) { return state.workspace.groups.find(value => value.id === item?.groupId); }
function draft(item = task()) { return item?.drafts[language]; }
function captureContext(current = task()) { return { taskId: current?.id, owner: state.workspace.activeSteamId, language, epoch: uiEpoch }; }
function isCurrent(context) { return context.taskId === taskId && context.owner === state.workspace.activeSteamId && context.epoch === uiEpoch && context.language === language; }
function openLink(url) { return window.hub.invoke('external.open', { url }); }
async function refresh(renderView = true) {
  const sequence = ++refreshSequence;
  const next = await window.hub.invoke('init');
  if (sequence !== refreshSequence) return;
  if (state?.workspace.activeSteamId !== next.workspace.activeSteamId) { uiEpoch++; nativeSnapshot = null; nativeTaskId = null; suggestions.clear(); library = []; }
  state = next;
  if (!state.workspace.tasks.some(item => item.id === taskId)) taskId = state.workspace.tasks.find(item => item.status === 'active')?.id ?? state.workspace.tasks[0]?.id ?? null;
  renderTop(); if (renderView) render();
}
async function navigate(tab) { await flushAll(); activeTab = tab; renderTop(); render(); }
async function chooseTask(id) {
  await flushAll();
  if (taskId && taskId !== id) window.hub.invoke('research.cancel', { taskId }).catch(() => {});
  uiEpoch++; taskId = id; nativeSnapshot = null; nativeTaskId = null; selectedAchievements.clear(); statChanges.clear(); renderTop(); render();
}
function renderTop() {
  $('#navigation').replaceChildren(...tabs.map(([id, icon, title]) => {
    const node = button('', () => navigate(id), `nav-item${activeTab === id ? ' active' : ''}`);
    node.dataset.tab = id; node.append(h('span', { class: 'nav-icon' }, icon), title); return node;
  }));
  const status = state.steam;
  $('#account-status').replaceChildren(h('div', { class: 'account-name' }, h('span', { class: `status-dot ${status.state}` }), state.fixture ? '演示工作区' : status.displayName || labels[status.state] || status.state),
    h('div', { class: 'account-id' }, state.workspace.activeSteamId || '未绑定 SteamID'));
  $('#account-button').textContent = status.state === 'connected' ? '账号与连接' : '连接 Steam';
  $('#mode-label').textContent = state.fixture ? '演示模式 · 合成数据，不连接 Steam' : '个人鉴赏家工作台';
  $('#task-picker').replaceChildren(h('option', { value: '' }, '选择评测任务'), ...state.workspace.tasks.map(item => h('option', { value: item.id }, `${item.title} · ${item.appId}`)));
  $('#task-picker').value = taskId ?? '';
  $('#language-picker').value = language;
  const running = state.runs.filter(run => run.state === 'running').length;
  $('#statusbar').replaceChildren(h('span', {}, `${labels[status.state] ?? status.state} · ${running} 款运行中`), h('span', {}, `文章：${language === 'schinese' ? '简体中文' : 'English'}`),
    h('span', { class: 'spacer' }), h('span', {}, state.fixture ? '所有运行和写入结果仅为演示' : '工作区与凭据分别保存在本机'));
}
function render() {
  main.replaceChildren();
  if (state.fixture) main.append(note('当前为演示模式：游戏、资料与成就均为合成样本，不会连接 Steam 或计入真实时长。', true));
  ({ library: renderLibrary, tasks: renderTasks, runs: renderRuns, achievements: renderAchievements, research: renderResearch, editor: renderEditor, settings: renderSettings })[activeTab]();
}
function requireTask() {
  if (task()) return true;
  main.append(empty('先选择一个评测任务', '从游戏库或商店搜索添加游戏。任务将为运行、成就、资料和草稿提供明确的 AppID 与唯一投稿群组。', button('前往游戏库', () => navigate('library'), 'primary')));
  return false;
}
function dialog(title, content, actions = []) {
  const box = $('#modal'); if (box.open) box.close();
  $('#modal-content').replaceChildren(h('h2', {}, title), content, h('div', { class: 'actions' }, button('关闭', () => box.close(), 'subtle'), ...actions)); box.showModal();
}
function connectDialog() {
  if (state.steam.state === 'connected') {
    dialog('Steam 连接', h('div', {}, h('p', {}, `当前协议账号：${state.steam.steamId}`), h('p', { class: 'muted' }, '网页发布与本机成就进程仍会分别核对账号。退出后保留本地草稿，并停止此协议会话的运行请求。')),
      [button('退出连接并停止运行', async () => { $('#modal').close(); await window.hub.invoke('steam.logout'); await refresh(); })]); return;
  }
  const name = h('input', { autocomplete: 'username', placeholder: 'Steam 账号名' });
  const password = h('input', { type: 'password', autocomplete: 'current-password', placeholder: '密码仅在内存中使用' });
  dialog('连接 Steam', h('div', { class: 'stack' }, field('账号名', name), field('密码', password), h('p', { class: 'inline-note' }, 'Steam Guard 由你本人完成。此应用不保存密码、refresh token 或 Steam 登录会话文件。')),
    [button('连接', async () => {
      const credentials = { accountName: name.value, password: password.value }; password.value = '';
      $('#modal').close(); toast('正在连接 Steam，可能需要 Steam Guard…');
      await window.hub.invoke('steam.login', credentials); credentials.password = ''; await refresh();
      library = await window.hub.invoke('steam.library'); if (activeTab === 'library') render();
    }, 'primary')]);
}
function guardDialog(prompt) {
  const code = h('input', { autocomplete: 'one-time-code', placeholder: 'Steam Guard 验证码' });
  dialog('Steam Guard 验证', h('div', { class: 'stack' }, h('p', { class: 'muted' }, prompt.kind === 'email' ? `请输入邮件验证码${prompt.domain ? `（${prompt.domain}）` : ''}。` : '请输入手机验证码；如果手机要求确认登录，请由本人完成。'),
    prompt.lastCodeWrong ? note('上一个验证码无效，请重新输入。') : null, code),
    [button('提交验证码', async () => { const value = code.value; code.value = ''; $('#modal').close(); await window.hub.invoke('steam.guard', { code: value }); }, 'primary'),
      button('取消登录', async () => { $('#modal').close(); await window.hub.invoke('steam.logout'); await refresh(); }, 'subtle')]);
}
function createTaskDialog(game) {
  const groups = select(state.workspace.groups.map(g => [g.id, g.name]), state.workspace.groups[0]?.id);
  const target = h('input', { type: 'number', min: 0, step: .5, placeholder: '可留空' });
  const source = h('input', { placeholder: '例如：Curator Connect / Key / 自购，可留空' });
  dialog('新建评测任务', h('div', { class: 'stack' }, h('p', {}, `${game.name} · AppID ${game.appId}`), field('唯一投稿群组', groups), field('Steam 累计目标（小时）', target, '累计目标与每次开启时的自动结束时间分开保存。'), field('领取来源 / 备注', source)),
    [button('创建任务', async () => {
      await flushAll(); const created = await window.hub.invoke('task.create', { appId: game.appId, title: game.name, groupId: groups.value,
        targetMinutes: target.value === '' ? null : Number(target.value) * 60, source: source.value });
      $('#modal').close(); taskId = created.id; activeTab = 'tasks'; await refresh();
    }, 'primary')]);
}
async function gameDetails(game) {
  const info = await window.hub.invoke('steam.details', { appId: game.appId });
  dialog(info.name || game.name, h('div', { class: 'stack' }, h('div', { class: 'actions' }, pill(`AppID ${game.appId}`), ...Object.entries(info.platforms ?? {}).filter(([, value]) => value).map(([key]) => pill(key))),
    h('p', { class: 'source-content' }, info.description || '暂无可读取的商店介绍。'), h('p', { class: 'muted' }, `开发商：${(info.developers ?? []).join('、') || '未知'}`)),
    [button('打开 Steam 商店', () => openLink(info.storeUrl || `https://store.steampowered.com/app/${game.appId}/`)),
      state.workspace.activeSteamId?button('查看我的个人评测',()=>openLink(`https://steamcommunity.com/profiles/${state.workspace.activeSteamId}/recommended/${game.appId}/`)):null,
      button('创建任务', () => createTaskDialog(game), 'primary')].filter(Boolean));
}
function renderLibrary() {
  main.append(heading('游戏库', '从你的 Steam 库与商店发现游戏，将资料、运行与评测归于同一任务。', button('刷新游戏库', async () => { library = await window.hub.invoke('steam.library'); catalogMode = 'library'; await refresh(false); render(); }, 'primary')));
  if (state.steam.state !== 'connected') main.append(note('连接 Steam 后可读取拥有的游戏与服务器累计分钟。商店搜索可独立使用；设置中的手动 ID 绑定只确定草稿归属。', true));
  const query = h('input', { type: 'search', value: libraryQuery, placeholder: '按游戏名搜索，或直接输入 AppID', 'aria-label': '搜索游戏' });
  const mode = select([['library','我的游戏库'],['store','Steam 商店']], catalogMode);
  const sort = select([['name','按名称'],['time','按 Steam 时长']], librarySort, event => { librarySort = event.target.value; render(); });
  const search = async () => {
    libraryQuery = query.value.trim(); catalogMode = mode.value;
    if (catalogMode === 'store') {
      if (/^[1-9]\d{0,9}$/.test(libraryQuery)) {
        const details = await window.hub.invoke('steam.details', { appId: Number(libraryQuery) }); library = [{ ...details, name: details.name, steamMinutes: null }];
      } else library = await window.hub.invoke('steam.search', { query: libraryQuery });
    } else if (!library.length) library = await window.hub.invoke('steam.library');
    render();
  };
  query.addEventListener('keydown', event => { if (event.key === 'Enter') search().catch(fail); });
  mode.addEventListener('change', () => { catalogMode = mode.value; library = []; libraryQuery = ''; render(); });
  main.append(h('div', { class: 'filters' }, query, mode, sort, button('搜索', search)));
  let games = library.filter(game => catalogMode === 'store' || !libraryQuery || game.name.toLowerCase().includes(libraryQuery.toLowerCase()) || String(game.appId) === libraryQuery);
  games = [...games].sort(librarySort === 'time' ? (a,b) => (b.steamMinutes ?? -1) - (a.steamMinutes ?? -1) : (a,b) => a.name.localeCompare(b.name, 'zh-CN'));
  const count = games.length;
  if (!count) { main.append(empty('你的下一篇评测，从这里开始', catalogMode === 'library' ? '连接 Steam 并刷新游戏库，或切换到商店搜索。' : '输入游戏名称或 AppID 搜索商店。', state.steam.state === 'connected' ? button('读取我的游戏', async () => { library = await window.hub.invoke('steam.library'); render(); }) : button('连接 Steam', connectDialog, 'primary'))); return; }
  main.append(h('p', { class: 'muted tiny' }, `${count} 款游戏${count > 100 ? ' · 当前显示前 100 项，请搜索缩小范围' : ''}`));
  main.append(h('div', { class: 'grid' }, games.slice(0,100).map(game => {
    const existing = state.workspace.tasks.find(item => item.appId === game.appId);
    return h('article', { class: 'card game-card' }, h('div', { class: 'game-image' }, game.headerImage ? h('img', { src: game.headerImage, alt: '', loading: 'lazy' }) : '◇'),
      h('div', { class: 'game-content' }, h('h3', {}, game.name), h('div', { class: 'actions' }, pill(`AppID ${game.appId}`), pill(game.steamMinutes === null || game.steamMinutes === undefined ? '时长未知' : fmtMinutes(game.steamMinutes))),
        h('div', { class: 'actions' }, button('商店与介绍', () => gameDetails(game), 'small subtle'), button(existing ? '进入任务' : '添加任务', async () => { if (existing) { await chooseTask(existing.id); await navigate('tasks'); } else createTaskDialog(game); }, 'small primary'))));
  })));
}
function renderTasks() {
  main.append(heading('评测任务', '一款游戏，一个投稿群组。任务进度、Steam 事实和发布结果分别保存。', button('添加游戏', () => navigate('library'), 'primary')));
  if (!state.workspace.tasks.length) { main.append(empty('还没有评测任务', '先从游戏库或商店添加游戏。目标时长和来源备注均可留空。', button('选择游戏', () => navigate('library'), 'primary'))); return; }
  const rows = state.workspace.tasks.map(item => h('tr', { class: 'selectable', onClick: () => chooseTask(item.id).catch(fail) },
    h('td', {}, h('div', { class: 'game-title' }, item.title), h('span', { class: 'muted tiny' }, `AppID ${item.appId}`)), h('td', {}, state.workspace.groups.find(g => g.id === item.groupId)?.name),
    h('td', {}, item.targetMinutes === null ? '未设置' : fmtMinutes(item.targetMinutes)), h('td', {}, pill(item.status, item.status === 'completed' ? 'good' : '')), h('td', {}, item.id === taskId ? pill('当前任务', 'good') : '→')));
  main.append(h('div', { class: 'card table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, ...['游戏','投稿群组','累计目标','进度',''].map(v => h('th', {}, v)))), h('tbody', {}, rows))));
  if (!task()) return;
  const current = task(); const groupSelect = select(state.workspace.groups.map(g => [g.id,g.name]), current.groupId);
  const target = h('input', { type: 'number', min: 0, step: .5, value: current.targetMinutes === null ? '' : current.targetMinutes / 60, placeholder: '可留空' });
  const source = h('input', { value: current.source, placeholder: '可留空' });
  const status = select([['active','进行中'],['completed','已完成'],['archived','已归档']], current.status);
  main.append(h('div', { class: 'columns section-gap' }, h('section', { class: 'card' }, h('h2', {}, current.title), h('div', { class: 'form-grid' }, field('唯一投稿群组',groupSelect),field('Steam 累计目标（小时）',target),field('来源 / 备注',source),field('工作进度',status)),
    h('div', { class: 'actions section-gap' }, button('保存任务设置', async () => { await window.hub.invoke('task.update', { taskId: current.id, groupId: groupSelect.value, source: source.value, targetMinutes: target.value === '' ? null : Number(target.value)*60, status: status.value }); await refresh(); toast('任务设置已保存'); }, 'primary'))),
    h('section', { class: 'card' }, h('h2', {}, '继续工作'), h('p', { class: 'muted' }, `当前归属：${current.ownerSteamId}`), h('div', { class: 'stack' }, button('运行与时长', () => navigate('runs')), button('成就与统计', () => navigate('achievements')), button('搜集研究资料', () => navigate('research')), button('撰写与发布评测', () => navigate('editor'))))));
}
function renderRuns() {
  main.append(heading('运行与时长', 'Steam 累计分钟来自回读，本地会话时长单独显示。每款游戏独立启停。', button('刷新状态', () => refresh(), 'subtle')));
  if (requireTask()) {
    const current = task(); const duration = h('input', { type:'number', min:1, max:100000, step:1, value:60, 'aria-label':'本次自动结束分钟' });
    const timed = h('input', { type:'checkbox', checked:true });
    const quick = select([['15','15 分钟'],['30','30 分钟'],['60','1 小时'],['120','2 小时'],['240','4 小时']], '60', event => { duration.value = event.target.value; });
    main.append(h('div', { class:'card' }, h('div',{class:'row-between'},h('h2',{},`开启：${current.title}`),pill(`AppID ${current.appId}`)),
      h('p',{class:'muted'},`任务累计目标：${current.targetMinutes === null ? '未设置' : fmtMinutes(current.targetMinutes)}。同时有本次定时结束时，先达到的条件停止本游戏。`),
      h('div',{class:'filters'},h('label',{class:'check'},timed,'本次自动结束'),duration,h('span',{class:'muted'},'分钟'),quick,button('开始本游戏', async()=>{
        await window.hub.invoke('runs.start',{taskId:current.id,stopAfterMinutes:timed.checked?Number(duration.value):null});await refresh();toast('已发送本游戏运行请求，Steam 计入结果以回读为准');
      },'primary')), h('p',{class:'inline-note'},'关窗退出会清理运行请求。断线、睡眠或其他会话正在游戏时停止，不会自动踢出其他会话或恢复运行。')));
  }
  const eligible = state.workspace.tasks.filter(t=>t.status==='active');
  if (eligible.length>1) main.append(h('div',{class:'card'},h('h2',{},'并行开启多个任务'),h('p',{class:'muted'},'分别使用各自累计目标，本次均在 60 分钟后结束；启动结果逐款保留。'),button(`并行开启 ${eligible.length} 款`,async()=>{
    const results=await Promise.allSettled(eligible.map(t=>window.hub.invoke('runs.start',{taskId:t.id,stopAfterMinutes:60})));
    await refresh();toast(`运行请求：${results.filter(r=>r.status==='fulfilled').length} 款已处理，${results.filter(r=>r.status==='rejected').length} 款失败。`);
  })));
  for (const run of state.runs) {
    const name=state.workspace.tasks.find(t=>t.appId===run.appId)?.title??`AppID ${run.appId}`;
    main.append(h('section',{class:'card'},h('div',{class:'row-between'},h('div',{},h('h2',{},name),pill(run.state,run.state==='running'?'good':run.state==='unknown'?'warn':'')),
      button('单独停止',async()=>{await window.hub.invoke('runs.stop',{appId:run.appId});await refresh();},'subtle')),
      h('div',{class:'metric-row'},h('div',{class:'metric'},h('strong',{},fmtMinutes(run.steamMinutes)),h('span',{},'Steam 已记录总时长')),
        h('div',{class:'metric'},h('strong',{},`${Math.floor((run.localElapsedSeconds??0)/60)} 分钟`),h('span',{},'本地会话经过时间')),
        h('div',{class:'metric'},h('strong',{},run.totalTargetMinutes==null?'—':fmtMinutes(run.totalTargetMinutes)),h('span',{},'任务累计目标'))),
      h('div',{class:'actions'},pill(`回读：${run.readbackStatus??'待核实'}`),run.deadline?pill(`结束时间 ${new Date(run.deadline).toLocaleTimeString()}`):pill('本次无定时结束'),run.reason?pill(run.reason,'warn'):null),
      h('p',{class:'inline-note'},'运行请求与停止请求没有服务器持久化确认；不得将本地计时加到 Steam 总分钟。')));
  }
  if (!state.runs.length) main.append(empty('当前没有运行会话','开启当前任务，或并行开启多个任务。已达累计目标的游戏不会继续为追赶目标启动。'));
  const liveIds=new Set(state.runs.map(run=>run.id));
  const history=(state.workspace.runHistory??[]).filter(run=>!liveIds.has(run.id)).slice().reverse();
  if(history.length){
    const rows=history.map(run=>h('tr',{},
      h('td',{},run.name||`AppID ${run.appId}`,h('div',{class:'description'},run.startedAt??'未发送运行请求'),run.imported?pill('导入历史，待核实','warn'):run.fixture?pill('合成历史','warn'):null),
      h('td',{},pill(run.state,run.state.includes('unknown')?'warn':''),h('div',{class:'description'},run.reason??'')),
      h('td',{},`${Math.floor(run.localElapsedSeconds/60)} 分钟`),h('td',{},fmtMinutes(run.steamMinutes),h('div',{class:'description'},run.lastReadbackAt??'尚无回读')),
      h('td',{},run.deadline?new Date(run.deadline).toLocaleString():'无定时结束',h('div',{class:'description'},run.totalTargetMinutes==null?'无累计目标':`累计目标 ${fmtMinutes(run.totalTargetMinutes)}`))));
    main.append(h('section',{class:'card section-gap'},h('h2',{},'已保存的运行历史'),h('p',{class:'inline-note'},'每 30 秒及状态/Steam 回读改变时保存。重启不会恢复运行请求；中断会话的停止结果保持待核实。'),
      h('div',{class:'table-wrap'},h('table',{},h('thead',{},h('tr',{},...['游戏 / 开始时间','结果 / 原因','本地会话','Steam 回读','本次结束条件'].map(text=>h('th',{},text)))),h('tbody',{},rows)))));
  }
}
function renderAchievements() {
  main.append(heading('成就与统计', '按 AppID 读取本机 Steam 状态，先预览改动，再提交与回读。'));
  if (!requireTask()) return;
  const current=task();
  if (state.runs.some(run=>run.state==='running')) main.append(note('请先停止协议运行，再读取或修改成就。原生 Steam 上下文可能与运行会话冲突。'));
  main.append(h('div',{class:'actions'},pill(current.title),pill(`AppID ${current.appId}`),button('读取 / 刷新 Steam 状态',async()=>{
    const context=captureContext(current),sequence=++nativeReadSequence;
    const result=await window.hub.invoke('stats.read',{taskId:current.id});
    if(!isCurrent(context)||sequence!==nativeReadSequence)return;
    nativeSnapshot=result; nativeTaskId=current.id; selectedAchievements.clear();statChanges.clear();if(activeTab==='achievements')render();
  },'primary')));
  if (!nativeSnapshot || nativeTaskId!==current.id) main.append(empty('成就与统计尚未读取','需要本机 Steam 已登录，并且账号与任务绑定一致。读取和写入均在独立 AppID 原生进程内执行；不支持的项目会显示原因。'));
  else {
    const snapshot=nativeSnapshot;
    main.append(h('p',{class:'inline-note'},`来源：${snapshot.source} · 账号：${snapshot.accountId}${snapshot.capabilities?.privateAbiLocallyVerified===false?' · 此客户端私有接口尚待本机真实验证':''}`));
    const query=h('input',{type:'search',value:achievementQuery,placeholder:'搜索名称、说明或内部 ID'});
    query.addEventListener('input',()=>{achievementQuery=query.value;renderAchievementTable();});
    const filter=select([['all','全部状态'],['unlocked','已解锁'],['locked','未解锁']],achievementFilter,e=>{achievementFilter=e.target.value;renderAchievementTable();});
    const sort=select([['global','全球解锁率从低到高'],['global-desc','全球解锁率从高到低'],['name','按名称'],['time','按解锁时间']],achievementSort,e=>{achievementSort=e.target.value;renderAchievementTable();});
    const hidden=h('input',{type:'checkbox',checked:hideHidden});hidden.addEventListener('change',()=>{hideHidden=hidden.checked;renderAchievementTable();});
    main.append(h('div',{class:'filters section-gap'},query,filter,sort,h('label',{class:'check'},hidden,'折叠隐藏成就')));
    main.append(h('div',{id:'achievement-table',class:'card table-wrap'}));renderAchievementTable();
    const action=select([['unlock','解锁所选'],['relock','重新锁定所选']],achievementAction?'unlock':'relock',e=>{achievementAction=e.target.value==='unlock';});
    main.append(h('div',{class:'actions section-gap'},action,button('选择当前筛选的可修改项',()=>{
      for(const row of filteredAchievements()) if(row.writable!==false)selectedAchievements.add(row.id);renderAchievementTable();
    },'small'),button('清除选择',()=>{selectedAchievements.clear();renderAchievementTable();},'small subtle')));
    main.append(h('h2',{class:'section-gap'},'统计值'));
    const statRows=snapshot.stats.map(row=>{
      const input=h('input',{type:'number',value:statChanges.has(row.id)?statChanges.get(row.id):(row.value??''),step:row.type==='int'?1:'any',disabled:!row.writable});
      input.addEventListener('input',()=>{if(input.value!==''&&Number(input.value)!==row.value)statChanges.set(row.id,Number(input.value));else statChanges.delete(row.id);});
      return h('tr',{},h('td',{},h('div',{class:'game-title'},row.id),row.reason?h('div',{class:'description'},row.reason):null),h('td',{},row.type),h('td',{},row.value??'未知'),h('td',{},input),h('td',{},pill(row.writable?'可修改':'不可修改',row.writable?'good':'')));
    });
    main.append(h('div',{class:'card table-wrap'},snapshot.stats.length?h('table',{},h('thead',{},h('tr',{},...['统计项','类型','当前值','拟提交值','能力'].map(v=>h('th',{},v)))),h('tbody',{},statRows)):h('p',{class:'muted'},snapshot.capabilities?.statsReason??'没有可枚举的统计项。Schema 缺失或条目受限不表示所有能力可用。')));
    main.append(h('div',{class:'actions section-gap'},button('预览成就与统计更改',async()=>{
      const context=captureContext(current);
      const changes={achievements:[...selectedAchievements].map(id=>({id,unlocked:achievementAction})),stats:[...statChanges].map(([id,value])=>({id,value}))};
      const preview=await window.hub.invoke('stats.preview',{taskId:current.id,changes});
      if(!isCurrent(context)||activeTab!=='achievements')return;
      const rows=[...preview.changes.achievements.map(v=>`${v.id}: ${v.before?'已解锁':'未解锁'} → ${v.unlocked?'已解锁':'未解锁'}`),...preview.changes.stats.map(v=>`${v.id} (${v.type}): ${v.before} → ${v.value}`)];
      dialog(state.fixture?'预览合成更改':'预览 Steam 更改',h('div',{class:'stack'},h('p',{class:'muted'},`${current.title} · ${current.ownerSteamId}`),h('pre',{class:'json'},rows.join('\n')),note('逐项提交结果、StoreStats 回调与新进程回读分别记录。超时后保留未知结果，不自动重试。')),
        [button(state.fixture?'提交合成更改':'确认提交到 Steam',async()=>{
          if(!isCurrent(context))throw new Error('operation_context_changed');
          $('#modal').close(); const result=await window.hub.invoke('stats.apply',{taskId:current.id,previewId:preview.id});await refresh(false);
          if(!isCurrent(context))return;
          nativeSnapshot=result.readback??nativeSnapshot;nativeTaskId=nativeSnapshot?current.id:null;
          if(result.items){for(const item of result.items.filter(item=>item.status==='verified')){if(item.kind==='achievement')selectedAchievements.delete(item.id);else statChanges.delete(item.id);}}
          else if(result.status==='verified'){selectedAchievements.clear();statChanges.clear();}
          if(taskId===current.id&&activeTab==='achievements')render();toast(`逐项结果已保存：${labels[result.status]??result.status}${state.fixture?'（仅合成结果）':''}`);
        },'danger')]);
    },'primary')));
  }
  main.append(h('h2',{class:'section-gap'},'更改历史与未知结果'));
  for(const operation of (current.operations??[]).slice().reverse()) {
    main.append(h('div',{class:'card'},h('div',{class:'row-between'},pill(operation.state,['outcome_unknown','applying'].includes(operation.state)?'warn':''),h('span',{class:'muted tiny'},operation.updatedAt??operation.createdAt)),
      h('details',{class:'section-gap'},h('summary',{},'查看修改前值、提交和回读记录'),h('pre',{class:'json'},JSON.stringify(operation.result??operation.changes,null,2))),
      operation.state==='outcome_unknown'?h('div',{},h('p',{class:'inline-note'},'请核对新读回值。人工确认只解除重试门禁，不会把该次结果改为自动核实。'),button('刷新并确认已检查未知结果',async()=>{
        const context=captureContext(current),sequence=++nativeReadSequence;
        const result=await window.hub.invoke('stats.acknowledge',{taskId:current.id,operationId:operation.id});await refresh(false);
        if(!isCurrent(context)||sequence!==nativeReadSequence)return;
        nativeSnapshot=result;nativeTaskId=current.id;selectedAchievements.clear();statChanges.clear();render();
      },'small')):null));
  }
}
function filteredAchievements() {
  if(!nativeSnapshot)return[];
  return nativeSnapshot.achievements.filter(row=>{
    const match=`${row.name??''} ${row.description??''} ${row.id}`.toLowerCase().includes(achievementQuery.toLowerCase());
    return match&&(achievementFilter==='all'||(achievementFilter==='unlocked')===Boolean(row.unlocked));
  }).sort((a,b)=>achievementSort==='name'?(a.name??a.id).localeCompare(b.name??b.id):achievementSort==='time'?(b.unlockedAt??0)-(a.unlockedAt??0):
    a.globalPercent==null?1:b.globalPercent==null?-1:(achievementSort==='global-desc'?-1:1)*(a.globalPercent-b.globalPercent));
}
function renderAchievementTable() {
  const container=$('#achievement-table');if(!container)return;
  const rows=filteredAchievements();
  const table=h('table',{},h('thead',{},h('tr',{},h('th',{},'选择'),h('th',{},`成就 · ${rows.length} 项`),h('th',{},'状态'),h('th',{},'全球解锁率'))),h('tbody',{},rows.map(row=>{
    const checked=h('input',{type:'checkbox',checked:selectedAchievements.has(row.id),disabled:row.writable===false,'aria-label':`选择 ${row.id}`});
    checked.addEventListener('change',()=>checked.checked?selectedAchievements.add(row.id):selectedAchievements.delete(row.id));
    const masked=hideHidden&&row.hidden&&!row.unlocked;
    return h('tr',{},h('td',{},checked),h('td',{},h('div',{class:'game-title'},masked?'隐藏成就（取消折叠可查看）':row.name||row.id),h('div',{class:'description'},masked?row.id:row.description||row.id),row.reason?h('div',{class:'description error'},row.reason):null),
      h('td',{},pill(row.unlocked?'已解锁':'未解锁',row.unlocked?'good':'')),h('td',{},Number.isFinite(row.globalPercent)?`${row.globalPercent.toFixed(1)}%`:'未知'));
  })));
  container.replaceChildren(rows.length?table:h('p',{class:'muted'},'没有匹配的成就。'));
}
function renderResearch() {
  main.append(heading('资料研究', '以游戏名检索，实际取得资料后生成初稿；来源观点与作者经历分别呈现。'));
  if(!requireTask())return;
  const current=task();const busy=busyTasks.has(current.id);
  main.append(h('div',{class:'card'},h('div',{class:'row-between'},h('h2',{},current.title),pill(`AppID ${current.appId}`)),
    h('p',{class:'muted'},'读取 Steam 评论，再按游戏名查找媒体。资料不足、同名歧义、付费墙与读取失败会保留状态。生成内容供你编辑，不代表你有相应游玩经历。'),
    h('div',{class:'actions'},button(busy?'正在搜集资料…':'搜集资料并生成长评初稿',async()=>{
      if(busyTasks.has(current.id))return;const context=captureContext(current);await flushAll();if(!isCurrent(context))return;busyTasks.add(current.id);render();
      const lang=context.language;
      try {
        const result=await window.hub.invoke('research.run',{taskId:current.id,language:lang});
        const origin=result.origin;
        if(origin?.taskId===current.id&&origin.language===lang)suggestions.set(`${current.id}:${lang}:long`,{text:result.draft,taskId:current.id,baseRevision:origin.baseRevision,kind:'long',language:lang});
        await refresh(false);
      } finally {busyTasks.delete(current.id);if(taskId===current.id&&activeTab==='research')render();}
    },'primary'),button('取消当前研究',async()=>{await window.hub.invoke('research.cancel',{taskId:current.id});toast('已请求取消，已保存草稿与资料保留');},'subtle'),button('AI 设置',()=>navigate('settings'),'subtle'))));
  const research=current.research;
  if(!research){main.append(empty('把零散观点整理成可追溯的评测资料','Steam 评论和站外媒体会分开记录。未配置 AI 时仍可获取 Steam 评论；API 配置完成后可使用翻译、摘要与初稿生成。'));return;}
  if(research.warnings?.length)main.append(note(research.warnings.map(researchWarning).join('；')));
  if(research.usage?.calls?.length)main.append(h('details',{class:'card section-gap'},h('summary',{},'本次 API 调用与用量'),...research.usage.calls.map(call=>h('p',{class:'inline-note'},`${call.provider} · ${call.operation} · ${call.requests??1} 次请求 · ${call.status??'未知'} · 输入 ${call.inputTokens??'未知'} / 输出 ${call.outputTokens??'未知'} token`)),h('p',{class:'inline-note'},'实际费用与额度请在服务商账号中核对。缺少计量数据时显示未知。')));
  if(research.summary)main.append(h('section',{class:'card'},h('h2',{},'资料摘要'),h('div',{class:'source-content'},research.summary)));
  if(research.draft){
    const origin=research.origin;
    const suggestion=suggestions.get(`${current.id}:${language}:long`)??(origin?.taskId===current.id&&origin.ownerSteamId===current.ownerSteamId&&origin.language===language?{text:research.draft,taskId:current.id,baseRevision:origin.baseRevision,language:origin.language,kind:'long'}:null);
    main.append(h('section',{class:'card'},h('div',{class:'row-between'},h('h2',{},'长评建议稿'),pill('待作者编辑','warn')),h('div',{class:'suggestion'},research.draft),
      h('div',{class:'actions section-gap'},suggestion?button('将建议稿保存为当前语言长评',async()=>{await applySuggestion(suggestion);await navigate('editor');},'primary'):note('该建议属于其他语言或缺少原始版本信息，请复制后人工核对。'),button('复制建议稿',()=>window.hub.invoke('clipboard.write',{text:research.draft})) )));
  }
  main.append(h('h2',{class:'section-gap'},`来源 · ${research.sources?.length??0}`));
  for(const source of research.sources??[])main.append(h('article',{class:'card source-card'},h('div',{class:'source-title'},h('div',{},h('h3',{},source.title??source.id),h('div',{class:'source-url'},source.url??'')),pill(source.status??source.acquisitionStatus??source.kind,'warn')),
    h('p',{class:'inline-note'},`${source.kind??''} · ${source.date??'日期未知'} · ${typeof source.identity==='object'?JSON.stringify(source.identity):source.identity??'身份待核'}`),
    source.content?h('details',{},h('summary',{},'查看实际取得的内容'),h('div',{class:'source-content'},source.content)):h('p',{class:'muted'},'未取得可展示正文；不将搜索链接视为已读文章。'),
    source.url?button('打开来源',()=>openLink(source.url),'small subtle'):null));
  if(research.reviews?.length)main.append(h('details',{class:'card section-gap'},h('summary',{},`Steam 评论样本 · ${research.reviews.length} 条`),...research.reviews.slice(0,30).map(review=>h('div',{class:'attempt'},h('span',{class:'muted tiny'},`${review.language??''} · ${(review.recommended??review.voted_up)===true?'推荐':(review.recommended??review.voted_up)===false?'不推荐':'样本'}`),h('p',{class:'source-content'},review.review??review.text??''),button('翻译此评论',async()=>{
    const result=await window.hub.invoke('ai.translate',{taskId:current.id,text:review.review??review.text??'',language,kind:'long'});dialog('评论译文',h('div',{class:'source-content'},result.text));
  },'small subtle')))));
}
function buffer(kind) {
  const current=task();const key=`${current.id}:${language}:${kind}`;const saved=current.drafts[language][kind];
  let value=buffers.get(key);
  if(!value){value={key,taskId:current.id,language,kind,text:saved.text,revision:saved.revision,dirty:false,saving:null,timer:null};buffers.set(key,value);}
  else if(!value.dirty&&!value.saving&&value.revision!==saved.revision){value.text=saved.text;value.revision=saved.revision;}
  return value;
}
async function flushDraft(value) {
  clearTimeout(value.timer);
  if(value.saving)return value.saving;
  value.saving=(async()=>{
    while(value.dirty){
      if(value.conflict)throw new Error('draft_changed');
      const text=value.text;
      try {
        const saved=await window.hub.invoke('draft.save',{taskId:value.taskId,language:value.language,kind:value.kind,text,expectedRevision:value.revision});
        value.revision=saved.drafts[value.language][value.kind].revision;
        if(value.text===text)value.dirty=false;
        const index=state.workspace.tasks.findIndex(item=>item.id===saved.id);if(index>=0)state.workspace.tasks[index]=saved;
        const status=value.taskId===taskId&&value.language===language?document.querySelector(`[data-save-status="${value.kind}"]`):null;
        if(status)status.textContent=value.dirty?'待保存…':'已保存到本机';
      } catch(error){value.conflict=error.code==='draft_changed'||error.message==='draft_changed';throw error;}
    }
  })();
  try{return await value.saving;}finally{value.saving=null;}
}
async function flushAll(){
  do { for(const value of buffers.values())if(value.dirty||value.saving)await flushDraft(value); }
  while([...buffers.values()].some(value=>value.dirty||value.saving));
}
function edited(value,textarea){
  value.text=textarea.value;value.dirty=true;
  const status=document.querySelector(`[data-save-status="${value.kind}"]`);if(status)status.textContent='待保存…';
  clearTimeout(value.timer);value.timer=setTimeout(()=>flushDraft(value).catch(fail),700);
}
function previewLong(text){
  const known=state.workspace.groups.flatMap(item=>Object.values(item.footers)).map(v=>v.trim()).filter(Boolean);
  let body=text.trim(),match;do{match=known.find(value=>body.endsWith(value));if(match)body=body.slice(0,-match.length).trim();}while(match);
  const footer=group()?.footers[language]?.trim();return footer?`${body}\n\n${footer}`:body;
}
async function applySuggestion(value){
  const current=task();if(!current||!value.text)throw new Error('empty_ai_suggestion');
  if(value.taskId!==current.id)throw new Error('operation_context_changed');
  const context=captureContext(current);
  await flushAll();
  if(!isCurrent(context))throw new Error('operation_context_changed');
  const saved=await window.hub.invoke('draft.save',{taskId:current.id,language:value.language??language,kind:value.kind??'long',text:value.text,expectedRevision:value.baseRevision});
  buffers.delete(`${current.id}:${value.language??language}:${value.kind??'long'}`);await refresh(false);toast('建议已保存为新草稿');return saved;
}
function renderEditor(){
  main.append(heading('评测编辑', '正文与群组尾巴分别保存；短评、全文链接与发布结果各自独立。',button('立即保存',async()=>{await flushAll();toast('草稿已保存');},'primary')));
  if(!requireTask())return;
  const current=task(),long=buffer('long'),short=buffer('short');
  const longArea=h('textarea',{class:'editor-text',value:long.text,'aria-label':'个人长评正文',spellcheck:'false'});
  const shortArea=h('textarea',{class:'short-text',value:short.text,'aria-label':'鉴赏家短评正文'});
  const longPreview=h('div',{class:'preview'});renderBBCode(previewLong(long.text),longPreview,url=>openLink(url).catch(fail));
  const count=h('span',{},`${Array.from(short.text).length} / 199 字`);
  const updateCount=()=>{const n=Array.from(shortArea.value).length;count.textContent=`${n} / 199 字`;count.classList.toggle('error',n>=200);};
  longArea.addEventListener('input',()=>{edited(long,longArea);renderBBCode(previewLong(longArea.value),longPreview,url=>openLink(url).catch(fail));});
  shortArea.addEventListener('input',()=>{edited(short,shortArea);updateCount();});updateCount();
  const wrap=(opening,closing)=>{
    const start=longArea.selectionStart,end=longArea.selectionEnd;
    longArea.setRangeText(`${opening}${longArea.value.slice(start,end)||'文字'}${closing}`,start,end,'select');longArea.focus();longArea.dispatchEvent(new Event('input'));
  };
  const toolbar=h('div',{class:'toolbar'},...[['标题','[h2]','[/h2]'],['粗体','[b]','[/b]'],['斜体','[i]','[/i]'],['列表','[list]\n[*]','\n[/list]'],['引用','[quote]','[/quote]'],['剧透','[spoiler]','[/spoiler]'],['代码','[code]','[/code]'],['表格','[table][tr][th]','[/th][/tr][/table]'],['链接','[url=https://example.com/]','[/url]']].map(([name,start,end])=>button(name,()=>wrap(start,end),'small subtle')));
  const longStatus=h('span',{'data-save-status':'long'},long.dirty?'待保存…':'已保存到本机');
  const shortStatus=h('span',{'data-save-status':'short'},short.dirty?'待保存…':'已保存到本机');
  const editor=h('section',{class:'card'},h('div',{class:'row-between'},h('h2',{},'个人长评'),pill(group()?.name??'群组未设置')),toolbar,longArea,
    h('div',{class:'editor-meta'},longStatus,h('span',{},'正文不包含自动群组尾巴')),
    h('div',{class:'actions'},button('AI 格式处理',()=>transformDraft('format','long',long)),button('翻译到当前文章语言',()=>transformDraft('translate','long',long)),button('从资料生成初稿',()=>navigate('research'),'subtle')),
    h('h3',{class:'section-gap'},'本地预览'),longPreview,h('p',{class:'inline-note'},'预览按已识别的 BBCode 显示，最终格式与字段限制以 Steam 页面为准。群组尾巴在组合时添加，不写回正文。'));
  const link=h('input',{type:'url',value:draft().fullReviewUrl,placeholder:'https://steamcommunity.com/profiles/…/recommended/…/','aria-label':'全文链接'});
  const recommendation=select([['recommended','推荐'],['not_recommended','不推荐'],['informational','信息性']],draft().recommendation);
  const shortCard=h('section',{class:'card'},h('h2',{},'鉴赏家短评'),shortArea,h('div',{class:'editor-meta'},shortStatus,count),
    h('p',{class:'inline-note'},'严格少于 200 字。中英文、符号、空格、换行按 Unicode 码点计数；不添加尾巴或链接。'),
    button('从长评生成短评建议',()=>transformDraft('shorten','short',long),'primary'),h('div',{class:'divider'}),field('个人长评全文链接（独立字段）',link),field('鉴赏家推荐类型',recommendation),
    h('div',{class:'actions section-gap'},button('保存链接与推荐类型',async()=>{await window.hub.invoke('draft.options',{taskId:current.id,language,fullReviewUrl:link.value,recommendation:recommendation.value});await refresh(false);toast('独立链接与推荐类型已保存');})),
    h('p',{class:'inline-note'},'个人长评在 Steam 上使用正面/负面评价；信息性类型仅供鉴赏家短评。发布前由你在页面确认。'));
  main.append(h('div',{class:'columns'},editor,h('div',{class:'stack'},shortCard,h('section',{class:'card'},h('h2',{},'AI 修改建议'),suggestionPanel('long'),suggestionPanel('short')))));
  for(const value of [long,short].filter(v=>v.conflict))main.prepend(h('section',{class:'banner'},h('p',{},`${value.kind==='long'?'长评':'短评'}保存版本已改变，本地文字仍保留。请复制或合并后重新载入保存版本。`),h('div',{class:'actions'},
    button('复制本地未保存文字',()=>window.hub.invoke('clipboard.write',{text:value.text}),'small'),button('比较并处理冲突',async()=>{
      await refresh(false);const saved=state.workspace.tasks.find(t=>t.id===value.taskId)?.drafts[value.language][value.kind];
      dialog('草稿版本冲突',h('div',{},h('h3',{},'本地未保存文字'),h('pre',{class:'json'},value.text),h('h3',{},'磁盘保存版本'),h('pre',{class:'json'},saved?.text??'')),[
        button('复制本地文字',()=>window.hub.invoke('clipboard.write',{text:value.text})),button('放弃本地修改，载入保存版本',()=>{clearTimeout(value.timer);buffers.delete(value.key);$('#modal').close();render();},'danger')]);
    },'small'))));
  main.append(h('section',{class:'card section-gap'},h('h2',{},'发布准备'),h('p',{class:'muted'},'冻结当前内容和目标，打开独立 Steam 会话。你本人登录、核对页面并最终提交；打开、填入和点击都不会直接标为已发布。'),
    h('div',{class:'actions'},button('准备个人长评',()=>prepare('long'),'primary'),button('准备鉴赏家短评',()=>prepare('short')),button('刷新发布记录',async()=>{await refresh(false);updatePublications();},'subtle')),
    h('div',{id:'publication-attempts'})));
  updatePublications();
}
function suggestionPanel(kind){
  const suggestion=suggestions.get(`${taskId}:${language}:${kind}`);
  if(!suggestion?.text)return h('p',{class:'inline-note'},`${kind==='long'?'长评':'短评'}：尚无待应用建议。`);
  return h('div',{class:'section-gap'},h('h3',{},`${kind==='long'?'长评':'短评'}建议 · 不会自动覆盖草稿`),h('div',{class:'suggestion'},suggestion.text),
    h('div',{class:'actions section-gap'},button('将此建议保存到草稿',async()=>{await applySuggestion(suggestion);suggestions.delete(`${taskId}:${language}:${kind}`);render();},'small primary'),button('复制',()=>window.hub.invoke('clipboard.write',{text:suggestion.text}),'small')));
}
async function transformDraft(method,kind,source){
  const current=task(),context=captureContext(current);const lang=language;await flushAll();await refresh(false);
  if(!isCurrent(context))return;
  const baseRevision=state.workspace.tasks.find(item=>item.id===current.id).drafts[lang][kind].revision;
  toast('AI 正在处理，已有草稿保留…');
  const result=await window.hub.invoke(`ai.${method}`,{taskId:current.id,text:source.text,language:lang,kind,baseRevision});
  suggestions.set(`${current.id}:${lang}:${kind}`,{text:result.text,taskId:current.id,baseRevision,kind,language:lang});
  if(taskId===current.id&&language===lang&&activeTab==='editor')render();toast('修改建议已生成，请预览后应用');
}
async function prepare(kind){
  const context=captureContext();
  await flushAll();await refresh(false);
  if(!isCurrent(context))throw new Error('operation_context_changed');
  const attempt=await window.hub.invoke('publication.prepare',{taskId:context.taskId,language:context.language,kind});await refresh(false);updatePublications();toast(`已冻结${kind==='long'?'长评':'短评'}内容，发布前请核对目标与页面。`);return attempt;
}
function updatePublications(){
  const container=$('#publication-attempts');if(!container||!task())return;
  const current=task();const attempts=(current.publications??[]).slice().reverse();
  container.replaceChildren(...attempts.map(attempt=>{
    const url=h('input',{type:'url',value:attempt.resultUrl||`https://steamcommunity.com/profiles/${current.ownerSteamId}/recommended/${current.appId}/`,placeholder:'发布结果 URL','aria-label':'发布结果链接'});
    if(attempt.kind==='short'&&!attempt.resultUrl)url.value=`https://store.steampowered.com/curator/${attempt.groupId}/`;
    const invoke=async(command,extra={})=>{const result=await window.hub.invoke(command,{taskId:current.id,attemptId:attempt.id,...extra});await refresh(false);updatePublications();return result;};
    const frozen=h('details',{},h('summary',{},`查看冻结的${attempt.kind==='long'?'长评':'短评'} · ${attempt.language}`),h('pre',{class:'json'},attempt.text??''),attempt.fullReviewUrl?h('p',{class:'source-url'},`全文链接：${attempt.fullReviewUrl}`):null);
    const actions=h('div',{class:'actions'},button('打开官方页面',async()=>{if(state.fixture){toast('演示模式不会打开真实 Steam 发布页面');return;}await invoke('publication.open');},'small'),
      button('检查页面身份与字段',async()=>{const result=await invoke('publication.inspect');toast(result.success?'账号与目标字段检查通过':`页面未就绪：${result.code}`,!result.success);},'small'),
      button('填入冻结内容（不提交）',async()=>{const result=await invoke('publication.fill');toast(result.success?'已填入。请本人核对推荐、披露、全文链接并提交':`未填入：${result.code}。可复制后在官方页面人工填写。`,!result.success);},'small'),
      button('复制正文',()=>window.hub.invoke('clipboard.write',{text:attempt.text??''}),'small subtle'),
      attempt.fullReviewUrl?button('复制独立全文链接',()=>window.hub.invoke('clipboard.write',{text:attempt.fullReviewUrl}),'small subtle'):null);
    const recordActions=h('div',{class:'actions'},button('记录已提交 / 结果待核实',async()=>{await invoke('publication.record',{state:'submitted_unknown'});toast('已记录待核实，不会盲目再次填充或发布。');},'small'),
      button('本人已核对此结果 URL',async()=>{await invoke('publication.record',{state:'user_confirmed',resultUrl:url.value});toast('已记录用户确认，未标为应用自动回读核实。');},'small'),
      attempt.kind==='long'?button('从 Steam 回读核验长评',async()=>{const result=await invoke('publication.verify',{resultUrl:url.value});toast(result.verified?'Steam 回读与冻结内容匹配':`仍待核实：${result.reason}`,!result.verified);},'small primary'):null,
      button('确认本次未成功',async()=>{await invoke('publication.record',{state:'failed'});toast('已记录用户确认失败，允许修正后重新准备。');},'small subtle'));
    return h('div',{class:'attempt'},h('div',{class:'row-between'},h('h3',{},`${attempt.kind==='long'?'个人长评':'鉴赏家短评'} · ${attempt.language}`),pill(attempt.state,['submitted_unknown','imported_unverified'].includes(attempt.state)?'warn':attempt.state==='verified'?'good':'')),
      h('p',{class:'inline-note'},`AppID ${attempt.appId} · 群组 ${attempt.groupId} · ${attempt.updatedAt??attempt.createdAt??''} · ${attempt.evidenceSource||'尚无发布证据'}`),frozen,actions,url,recordActions,
      h('p',{class:'inline-note'},'如果手动粘贴并提交，请主动记录待核实或真实结果。字段无法识别时可使用复制辅助流程；全文链接始终单独填写。'));
  }));
  if(!attempts.length)container.append(h('p',{class:'inline-note section-gap'},'尚未准备发布。长评与短评分别记录，不会因任一成功把另一项标成成功。'));
}
function renderSettings(){
  main.append(heading('设置','凭据与工作区分别保存。界面语言固定简体中文，文章语言独立。'));
  const settings=state.workspace.settings;
  const steam=h('input',{value:state.workspace.activeSteamId??'',placeholder:'7656119…','aria-label':'手动绑定SteamID64'});
  main.append(h('section',{class:'card'},h('h2',{},'账号与数据归属'),h('p',{class:'muted'},'连接后自动按实际 SteamID64 显示该账号的任务。也可先绑定 ID 离线编辑；手动绑定不表示已登录。'),h('div',{class:'filters'},steam,button('绑定此 SteamID64',async()=>{await flushAll();await window.hub.invoke('account.bind',{steamId:steam.value.trim()});await refresh();toast('工作区账号已绑定');}),button(state.steam.state==='connected'?'连接详情':'连接 Steam',connectDialog,'primary'))));
  const base=h('input',{type:'url',value:settings.aiBaseUrl,'aria-label':'AI API base URL'}),model=h('input',{value:settings.aiModel,placeholder:'填写此 API 账号可用的模型 ID','aria-label':'AI 模型'});
  const key=h('input',{type:'password',autocomplete:'off',placeholder:state.credentials.aiKeyConfigured?'已配置；留空保留，删除请点清除':'API 密钥仅保存在本机','aria-label':'AI API 密钥'});
  const searchKey=h('input',{type:'password',autocomplete:'off',placeholder:state.credentials.searchKeyConfigured?'已配置；留空保留':'用于站外按游戏名搜索','aria-label':'Brave Search API 密钥'});
  const webKey=h('input',{type:'password',autocomplete:'off',placeholder:state.credentials.webApiKeyConfigured?'已配置；留空保留':'可选；原生接口不依赖此 Key','aria-label':'Steam Web API key'});
  const sources=h('input',{type:'number',min:1,max:6,step:1,value:settings.maxSources});
  const tokens=h('input',{type:'number',min:500,max:8000,step:100,value:settings.maxOutputTokens});
  const persist=h('input',{type:'checkbox',checked:state.credentials.encryptionAvailable});
  const save=async()=>{
    const secrets={};if(key.value)secrets.aiKey=key.value;if(webKey.value)secrets.webApiKey=webKey.value;if(searchKey.value)secrets.searchKey=searchKey.value;
    await window.hub.invoke('settings.save',{settings:{aiBaseUrl:base.value.trim(),aiModel:model.value.trim(),maxSources:Number(sources.value),maxOutputTokens:Number(tokens.value),articleLanguage:language},secrets,persistSecrets:persist.checked});
    key.value='';webKey.value='';searchKey.value='';await refresh();toast('设置已保存');
  };
  main.append(h('section',{class:'card section-gap'},h('div',{class:'row-between'},h('h2',{},'AI 与 API'),pill(state.credentials.encryptionAvailable?'系统加密可用':'仅本次会话可用',state.credentials.encryptionAvailable?'good':'warn')),
    h('p',{class:'muted'},'首版使用 Gemini API 生成与翻译，Brave Search API 按游戏名检索，再独立读取媒体正文。费用按各服务账号计量，搜索次数、资料与输出均有上限。'),
    h('div',{class:'form-grid'},field('API 地址',base),field('Gemini 模型 ID',model),field('Gemini API 密钥',key),field('Steam Web API key（可选）',webKey),field('Brave Search API 密钥',searchKey),h('p',{class:'inline-note'},'只有实际取得的网页内容参与媒体初稿；搜索返回内容仅用于临时定位，不保存为媒体正文。'),field('最多媒体来源数',sources),field('最多输出 token',tokens)),
    h('label',{class:'check section-gap'},persist,'使用系统加密持久保存密钥'),h('p',{class:'inline-note'},'改变 API 地址时请重新填写对应密钥。密钥绑定目标服务地址，工作区备份不包含凭据。'),
    h('div',{class:'actions section-gap'},button('保存 API 设置',save,'primary'),button('清除 AI 密钥',async()=>{await window.hub.invoke('settings.save',{secrets:{aiKey:''},persistSecrets:state.credentials.encryptionAvailable});await refresh();},'subtle'))));
  const groupCards=state.workspace.groups.map(item=>{
    const name=h('input',{value:item.name});const zh=h('textarea',{value:item.footers.schinese,rows:3}),en=h('textarea',{value:item.footers.english,rows:3});
    return {id:item.id,name,zh,en,node:h('div',{class:'section-gap'},h('h3',{},`群组 ${item.id}`),h('div',{class:'form-grid'},field('显示名称',name),h('div',{},button('打开鉴赏家页',()=>openLink(`https://store.steampowered.com/curator/${item.id}/`),'small subtle')),field('中文长评尾巴（BBCode）',zh),field('英文长评尾巴（BBCode）',en)))};
  });
  main.append(h('section',{class:'card section-gap'},h('h2',{},'群组尾巴模板'),h('p',{class:'muted'},'以下是可编辑的首版链接模板。请在正式发布前替换为你的正式尾巴；短评始终不添加尾巴。'),groupCards.map(item=>item.node),h('div',{class:'actions section-gap'},button('保存群组模板',async()=>{
    await flushAll();await window.hub.invoke('settings.save',{groups:groupCards.map(item=>({id:item.id,name:item.name.value,footers:{schinese:item.zh.value,english:item.en.value}}))});await refresh();toast('群组模板已保存');
  },'primary'))));
  main.append(h('section',{class:'card section-gap'},h('h2',{},'本地备份'),h('p',{class:'muted'},'导出任务、语言独立草稿、来源与发布记录；不含密钥或 Steam 网页会话。导入会替换工作区，已有发布成功记录需重新核实。'),
    h('div',{class:'actions'},button('导出工作区备份',async()=>{await flushAll();const result=await window.hub.invoke('backup.export');if(!result.cancelled)toast('备份已导出');}),button('从备份恢复',async()=>{await flushAll();const result=await window.hub.invoke('backup.import');if(!result.cancelled){buffers.clear();nativeSnapshot=null;await refresh();toast('工作区已恢复');}},'subtle'))));
}

$('#account-button').addEventListener('click',connectDialog);
$('#task-picker').addEventListener('change',event=>chooseTask(event.target.value||null).catch(fail));
$('#language-picker').addEventListener('change',async event=>{const lang=event.target.value;try{await flushAll();uiEpoch++;language=lang;renderTop();render();}catch(error){renderTop();fail(error);}});
async function requestClose(){
  document.body.inert=true;
  try { await flushAll();await window.hub.invoke('app.close-ready',{allow:true}); }
  catch(error){
    document.body.inert=false;
    await window.hub.invoke('app.close-ready',{allow:false});fail(error);
    const pending=[...buffers.values()].filter(value=>value.dirty);
    dialog('草稿尚未保存，已取消退出',h('div',{},h('p',{},'返回编辑器处理版本冲突或保存错误。退出前可复制全部未保存文字。'),...pending.map(value=>h('div',{},h('h3',{},`${value.taskId} · ${value.language} · ${value.kind}`),h('pre',{class:'json'},value.text)))),[
      button('复制全部未保存文字',()=>window.hub.invoke('clipboard.write',{text:pending.map(value=>`${value.taskId} / ${value.language} / ${value.kind}\n${value.text}`).join('\n\n')})),
      button('放弃本次未保存修改并退出',async()=>{for(const value of buffers.values()){clearTimeout(value.timer);value.dirty=false;value.conflict=false;}$('#modal').close();await window.hub.invoke('app.request-close');},'danger')]);
  }
}
window.hub.onEvent(event=>{
  if(event.type==='app.requestClose'){requestClose().catch(fail);return;}
  if(!state)return;
  if(event.type==='steam.guard')guardDialog(event.data);
  else if(event.type==='steam.status'){state.steam=event.data;renderTop();}
  else if(event.type==='steam.runs'){state.runs=event.data;renderTop();if(activeTab==='runs'&&!main.querySelector('input:focus'))render();}
  else if(event.type==='workspace.changed')refresh(false).then(()=>{if(activeTab==='editor')updatePublications();}).catch(fail);
  else if(event.type==='app.error')toast('工作区保存未完成，请检查本机磁盘与权限。',true);
});
window.addEventListener('unhandledrejection',event=>{event.preventDefault();fail(event.reason);});
window.addEventListener('beforeunload',event=>{if([...buffers.values()].some(value=>value.dirty)){event.preventDefault();event.returnValue=false;}});
try{
  state=await window.hub.invoke('init');language=state.workspace.settings.articleLanguage;hideHidden=state.workspace.settings.hideHiddenAchievements;
  taskId=state.workspace.tasks.find(item=>item.status==='active')?.id??state.workspace.tasks[0]?.id??null;
  activeTab=taskId?'tasks':'library';renderTop();render();
  if(state.fixture){library=await window.hub.invoke('steam.library');}
}catch(error){main.replaceChildren(empty('工作区暂时无法打开',errors[error.code??error.message]??error.message));}
