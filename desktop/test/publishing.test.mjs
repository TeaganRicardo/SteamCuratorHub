import test from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { runInNewContext } from 'node:vm';
import { Workspace } from '../workspace.mjs';
import { PublishingService, fillSteamForm, steamNavigation, publicationPage } from '../publishing.mjs';

const ACCOUNT = '76561198000000000';
const OTHER = '76561198000000001';
const URL_RESULT = `https://steamcommunity.com/profiles/${ACCOUNT}/recommended/1001/`;
const settle = async () => { for (let i = 0; i < 8; i++) await Promise.resolve(); };

function domFixture(kind = 'long') {
  class Field {
    constructor(value = '') { this._value = value; this.disabled = false; this.readOnly = false; this.maxLength = -1; this.events = []; this.type = 'text'; }
    get value() { return this._value; }
    set value(value) { this._value = String(value); }
    dispatchEvent(event) { this.events.push(event.type); this.onDispatch?.(event); return true; }
  }
  class Input extends Field {}
  class Textarea extends Field {}
  const body = new Textarea('Existing body');
  const link = new Input('https://example.com/old'); link.type = 'url';
  const selectedApp = new Input('1001'); selectedApp.type = 'hidden';
  const selectors = new Map();
  for (const selector of kind === 'long' ? ['textarea#review_text', 'textarea[name="review_text"]'] :
    ['textarea#blurb', 'textarea[name="blurb"]']) selectors.set(selector, [body]);
  if (kind === 'short') {
    selectors.set('input[name="link_url"]', [link]); selectors.set('input#link_url', [link]);
    selectors.set('input[name="appid"]', [selectedApp]); selectors.set('input#appid', [selectedApp]);
  }
  const context = { URL, HTMLInputElement: Input, HTMLTextAreaElement: Textarea,
    Event: class { constructor(type) { this.type = type; } }, g_steamID: ACCOUNT,
    location: new URL(kind === 'long' ? 'https://store.steampowered.com/app/1001/' :
      'https://store.steampowered.com/curator/45134494/admin/review_create?appid=1001'),
    document: { querySelectorAll: selector => selectors.get(selector) ?? [], querySelector: () => null } };
  const expected = { kind, ownerSteamId: ACCOUNT, appId: 1001, groupId: '45134494', language: 'english',
    text: kind === 'long' ? 'A review' : 'Short review', fullReviewUrl: '', recommendation: 'recommended' };
  return { body, link, selectedApp, selectors, context, expected, Input, Textarea,
    execute(code) { return runInNewContext(code, context, { timeout: 1000 }); },
    fill(override = {}, inspect = false) { return this.execute(`(${fillSteamForm.toString()})(${JSON.stringify({ ...expected, ...override })},${JSON.stringify(inspect)})`); } };
}

function setup({ kind = 'long', fetchImpl } = {}) {
  let saveError = false;
  const workspace = new Workspace({ load: () => null, save() { if (saveError) throw new Error('disk failed private path'); } });
  workspace.bindAccount(ACCOUNT);
  const task = workspace.createTask({ appId: 1001, title: 'Synthetic Game', groupId: '45134494' });
  workspace.saveDraft({ taskId: task.id, language: 'english', kind, text: kind === 'long' ? 'A review' : 'Short review', expectedRevision: 0 });
  const attempt = workspace.preparePublication(task.id, 'english', kind);
  const dom = domFixture(kind);
  const windows = []; const partitions = new Map(); const events = [];
  class Window extends EventEmitter {
    constructor(options) {
      super(); this.options = options; this.destroyed = false; windows.push(this);
      this.webContents = new EventEmitter();
      this.webContents.setWindowOpenHandler = callback => { this.openHandler = callback; };
      this.webContents.getURL = () => this.url;
      this.webContents.executeJavaScript = async code => this.execute ? this.execute(code) : dom.execute(code);
    }
    async loadURL(url) { this.url = url; dom.context.location = new URL(url); if (this.loading) await this.loading; }
    isDestroyed() { return this.destroyed; }
    close() { if (!this.destroyed) { this.destroyed = true; this.emit('closed'); } }
    show() { this.shown = true; }
    focus() { this.focused = true; }
  }
  const session = { fromPartition(name) {
    if (!partitions.has(name)) partitions.set(name, { name, clears: [],
      setPermissionRequestHandler(fn) { this.permissionRequest = fn; }, setPermissionCheckHandler(fn) { this.permissionCheck = fn; },
      async clearStorageData() { this.clears.push('storage'); if (this.clearing) await this.clearing; },
      async clearCache() { this.clears.push('cache'); } });
    return partitions.get(name);
  } };
  const service = new PublishingService({ BrowserWindow: Window, session, workspace,
    fetchImpl: fetchImpl ?? (async () => { throw new Error('unexpected real HTTP'); }), onEvent: event => events.push(event) });
  return { service, workspace, task, attempt, dom, windows, partitions, events,
    setSaveError(value) { saveError = value; },
    saved() { return workspace.task(task.id).publications.find(item => item.id === attempt.id); } };
}
function review(attempt, override = {}) {
  return { recommendationid: '123456789012', author: { steamid: ACCOUNT }, review: attempt.text,
    voted_up: true, language: attempt.language, ...override };
}
function response(reviews, cursor = '') { return new Response(JSON.stringify({ response: { reviews, cursor } })); }

test('DOM fill uses real field setters/events and keeps independent link empty by clearing stale URL', () => {
  const f = domFixture('short');
  const result = f.fill();
  assert.equal(result.success, true); assert.equal(result.code, 'filled_user_review_required');
  assert.equal(f.body.value, 'Short review'); assert.equal(f.link.value, '');
  assert.deepEqual(f.body.events, ['input', 'change']); assert.deepEqual(f.link.events, ['input', 'change']);
  assert.equal(result.fullLinkFilled, true); assert.equal(result.permissionRequiresUser, true);
});

test('DOM inspect leaves fields unchanged and reports current field constraints', () => {
  const f = domFixture('long'); f.body.maxLength = 8000;
  assert.equal(f.fill({}, true).bodyMaxLength, 8000);
  assert.equal(f.body.value, 'Existing body'); assert.deepEqual(f.body.events, []);
});

test('DOM rejects wrong account/host/AppID, ambiguous fields and curator selected-app mismatch before mutation', () => {
  const cases = [
    f => { f.context.g_steamID = OTHER; },
    f => { f.context.location = new URL('https://store.steampowered.com.attacker.com/app/1001/'); },
    f => { f.context.location = new URL('https://store.steampowered.com/app/10010/'); },
    f => { f.selectors.set('textarea#ReviewText', [new f.Textarea()]); },
    f => { f.body.readOnly = true; }
  ];
  for (const change of cases) {
    const f = domFixture(); change(f);
    const result = f.fill(); assert.equal(result.success, false); assert.equal(result.mutationPossible, false);
    assert.equal(f.body.value, 'Existing body');
  }
  const f = domFixture('short'); f.selectedApp.value = '1002';
  assert.equal(f.fill().code, 'form_app_unverified'); assert.equal(f.link.value, 'https://example.com/old');
});

test('DOM refuses missing/ambiguous links even when blank; enforces platform limits and rechecks cross-field changes', () => {
  const f = domFixture('short'); f.selectors.delete('input[name="link_url"]'); f.selectors.delete('input#link_url');
  assert.equal(f.fill().code, 'full_link_field_missing_or_ambiguous'); assert.deepEqual(f.body.events, []);
  const limited = domFixture('short'); limited.body.maxLength = 2;
  assert.equal(limited.fill().code, 'body_platform_limit'); assert.deepEqual(limited.body.events, []);
  const changed = domFixture('short'); changed.link.onDispatch = () => { changed.body.value = 'Page changed body'; };
  const result = changed.fill(); assert.equal(result.code, 'field_readback_changed'); assert.equal(result.mutationPossible, true);
});

test('DOM accepts only an official profile identity fallback and refuses URL-bearing short text', () => {
  const f = domFixture(); delete f.context.g_steamID;
  f.context.document.querySelector = () => ({ href: `https://attacker.example/profiles/${ACCOUNT}/` });
  assert.equal(f.fill().code, 'account_unverified');
  f.context.document.querySelector = () => ({ href: `https://steamcommunity.com/profiles/${ACCOUNT}/` });
  assert.equal(f.fill().success, true);
  const short = domFixture('short');
  assert.equal(short.fill({ text: 'https://example.com/review' }).code, 'invalid_short_content');
});

test('safe navigation and authoritative attempt prevent arbitrary hosts, altered partitions and duplicate windows', async () => {
  for (const value of ['file:///etc/passwd', 'steam://run/1001', 'https://steamcommunity.com.attacker.com/',
    'https://user:secret@steamcommunity.com/', 'https://steamcommunity.com:8443/']) assert.equal(steamNavigation(value), null);
  const s = setup();
  await s.service.open({ ...s.attempt, ownerSteamId: OTHER, appId: 2002 });
  assert.equal(s.windows[0].url, publicationPage(s.attempt));
  assert.equal(s.windows[0].options.webPreferences.nodeIntegration, false);
  assert.equal(s.windows[0].options.webPreferences.sandbox, true);
  assert.equal(s.partitions.has(`persist:steam-publishing-${ACCOUNT}`), true);
  await s.service.open(s.attempt); assert.equal(s.windows.length, 1); assert.equal(s.windows[0].focused, true);
  let denied = false;
  s.windows[0].webContents.emit('will-navigate', { preventDefault() { denied = true; } }, 'https://attacker.example/');
  assert.equal(denied, true); assert.deepEqual(s.windows[0].openHandler({ url: 'https://attacker.example/' }), { action: 'deny' });
  await s.service.close();
});

test('fill records only field evidence; close preserves unknown outcome and prevents blind refill/reopen', async () => {
  const s = setup(); await s.service.open(s.attempt);
  const result = await s.service.fill(s.task.id, s.attempt.id);
  assert.equal(result.success, true); assert.equal(s.saved().state, 'filled');
  assert.equal(s.saved().evidenceSource, 'page_field_readback');
  await assert.rejects(s.service.fill(s.task.id, s.attempt.id), { code: 'publication_needs_verification' });
  s.windows[0].close();
  assert.equal(s.saved().state, 'submitted_unknown');
  assert.throws(() => s.workspace.preparePublication(s.task.id, 'english', 'long'), /needs_verification/);
  await assert.rejects(s.service.open(s.attempt), { code: 'publication_needs_verification' });
  await s.service.close();
});

test('closing a window during asynchronous fill cannot be overwritten by a late filled result', async () => {
  const s = setup(); await s.service.open(s.attempt);
  let resolve;
  s.windows[0].execute = () => new Promise(done => { resolve = done; });
  const filling = s.service.fill(s.task.id, s.attempt.id);
  const rejected = assert.rejects(filling, { code: 'publication_fill_failed_or_context_changed' });
  assert.equal(s.saved().state, 'submitted_unknown');
  s.windows[0].close();
  resolve({ success: true, code: 'filled_user_review_required', accountMatched: true, targetMatched: true });
  await rejected; assert.equal(s.saved().state, 'submitted_unknown');
  await s.service.close();
});

test('fill refuses stale draft and disk failure before running page mutation', async () => {
  const s = setup(); await s.service.open(s.attempt);
  s.workspace.saveDraft({ taskId: s.task.id, language: 'english', kind: 'long', text: 'New draft', expectedRevision: 1 });
  await assert.rejects(s.service.fill(s.task.id, s.attempt.id), /publication_changed/);
  assert.equal(s.dom.body.value, 'Existing body');
  const t = setup(); await t.service.open(t.attempt); t.setSaveError(true);
  await assert.rejects(t.service.fill(t.task.id, t.attempt.id), { code: 'publication_fill_failed_or_context_changed' });
  assert.equal(t.dom.body.value, 'Existing body');
  t.setSaveError(false); await t.service.close(); await s.service.close();
});

test('async fill invalidation across account switch/logout never records a late success', async () => {
  const s = setup(); await s.service.open(s.attempt);
  let resolve;
  s.windows[0].execute = () => new Promise(done => { resolve = done; });
  const pending = s.service.fill(s.task.id, s.attempt.id);
  const rejected = assert.rejects(pending, { code: 'publication_fill_failed_or_context_changed' });
  await s.service.logout(ACCOUNT); s.workspace.bindAccount(OTHER); s.workspace.bindAccount(ACCOUNT);
  resolve({ success: true, code: 'filled_user_review_required', accountMatched: true, targetMatched: true });
  await rejected; assert.equal(s.saved().state, 'submitted_unknown');
  assert.deepEqual([...s.partitions.values()][0].clears, ['storage', 'cache']);
  await s.service.close();
});

test('long verification requires exact author/AppID URL, boolean vote, language, ID and frozen text', async () => {
  const s = setup(); let calls = 0;
  s.service.fetchImpl = async () => { calls++; return response([review(s.attempt)]); };
  for (const url of [`https://steamcommunity.com/profiles/${OTHER}/recommended/1001/`,
    `https://steamcommunity.com/profiles/${ACCOUNT}/recommended/10010/`, `${URL_RESULT}extra`, `${URL_RESULT}?x=1`]) {
    await assert.rejects(s.service.verifyLong(s.task.id, s.attempt.id, url), { code: 'result_author_or_app_mismatch' });
  }
  assert.equal(calls, 0);
  for (const override of [{ voted_up: 'true' }, { language: 'other' }, { recommendationid: '0' }, { recommendationid: undefined }]) {
    s.service.fetchImpl = async () => response([review(s.attempt, override)]);
    await assert.rejects(s.service.verifyLong(s.task.id, s.attempt.id, URL_RESULT), { code: 'review_readback_invalid' });
    assert.equal(s.saved().state, 'prepared');
  }
  for (const override of [{ review: `${s.attempt.text} ` }, { voted_up: false }, { language: 'schinese' }]) {
    s.service.fetchImpl = async () => response([review(s.attempt, override)]);
    assert.equal((await s.service.verifyLong(s.task.id, s.attempt.id, URL_RESULT)).verified, false);
  }
  await s.service.close();
});

test('official readback can verify an older frozen publication without replacing a later draft', async () => {
  const s = setup(); const requests = [];
  s.service.fetchImpl = async (url, options) => { requests.push({ url, options }); return response([review(s.attempt)]); };
  s.workspace.saveDraft({ taskId: s.task.id, language: 'english', kind: 'long', text: 'Later edited draft', expectedRevision: 1 });
  const result = await s.service.verifyLong(s.task.id, s.attempt.id, URL_RESULT);
  assert.equal(result.verified, true); assert.equal(s.saved().state, 'verified');
  assert.equal(s.workspace.task(s.task.id).drafts.english.long.text, 'Later edited draft');
  const input = JSON.parse(new URL(requests[0].url).searchParams.get('input_json'));
  assert.equal(input.filter, 2); assert.equal(input.purchase_type, 1); assert.deepEqual(input.languages, ['all']);
  assert.equal(requests[0].options.redirect, 'error'); assert.equal(requests[0].options.credentials, 'omit');
  await s.service.close();
});

test('readback pagination is bounded, rejects duplicate own results and stops repeated cursors', async () => {
  const s = setup(); let count = 0;
  const other = review(s.attempt, { author: { steamid: OTHER } });
  s.service.fetchImpl = async () => { count++; return response([other], String(count)); };
  const result = await s.service.verifyLong(s.task.id, s.attempt.id, URL_RESULT);
  assert.equal(result.reason, 'search_limit_or_cached_retry_readback_later'); assert.equal(count, 8);
  count = 0;
  s.service.fetchImpl = async () => { count++; return response([other], 'same'); };
  assert.equal((await s.service.verifyLong(s.task.id, s.attempt.id, URL_RESULT)).verified, false); assert.equal(count, 2);
  s.service.fetchImpl = async () => response([review(s.attempt), review(s.attempt)]);
  await assert.rejects(s.service.verifyLong(s.task.id, s.attempt.id, URL_RESULT), { code: 'review_readback_ambiguous' });
  await s.service.close();
});

test('late HTTP readback cannot verify after logout and same-account rebinding', async () => {
  const s = setup(); let resolve;
  s.service.fetchImpl = () => new Promise(done => { resolve = done; });
  const pending = s.service.verifyLong(s.task.id, s.attempt.id, URL_RESULT);
  const rejected = assert.rejects(pending, { code: 'review_readback_cancelled_or_timeout' });
  await settle(); await s.service.logout(ACCOUNT);
  s.workspace.bindAccount(OTHER); s.workspace.bindAccount(ACCOUNT);
  // Cancellation settles even when the injected transport has not returned a response yet.
  await rejected;
  resolve(response([review(s.attempt)]));
  await settle(); assert.equal(s.saved().state, 'prepared');
  await s.service.close();
});

test('public readback byte limit, invalid payload and upstream errors stay bounded and sanitized', async () => {
  const cases = [
    [async () => new Response('x'.repeat(2 * 1024 * 1024 + 1)), 'review_readback_too_large'],
    [async () => new Response('{}', { headers: { 'content-length': String(3 * 1024 * 1024) } }), 'review_readback_too_large'],
    [async () => new Response('not json'), 'review_readback_invalid'],
    [async () => new Response('{}', { status: 429 }), 'review_readback_rate_limited'],
    [async () => { throw new Error('private token upstream'); }, 'review_readback_unavailable']
  ];
  for (const [fetchImpl, code] of cases) {
    const s = setup({ fetchImpl });
    await assert.rejects(s.service.verifyLong(s.task.id, s.attempt.id, URL_RESULT), { code });
    assert.equal(s.saved().state, 'prepared'); await s.service.close();
  }
});

test('same-account logout blocks an opening window until storage clearing settles; close revokes pending work', async () => {
  const s = setup(); await s.service.open(s.attempt);
  const partition = [...s.partitions.values()][0]; let finishClear;
  partition.clearing = new Promise(resolve => { finishClear = resolve; });
  const loggingOut = s.service.logout(ACCOUNT); await settle();
  const opening = s.service.open(s.attempt); await settle();
  assert.equal(s.windows.length, 1);
  finishClear(); await loggingOut; await opening;
  assert.equal(s.windows.length, 2);
  await s.service.close();
  await assert.rejects(s.service.open(s.attempt), { code: 'publishing_closed' });
});
