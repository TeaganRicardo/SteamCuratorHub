import { createHash } from 'node:crypto';
import { appId, publicHttps, steamId } from './workspace.mjs';

const STEAM_HOSTS = ['store.steampowered.com', 'steamcommunity.com', 'login.steampowered.com', 'help.steampowered.com'];
const BODY_LIMIT = 2 * 1024 * 1024;
const PAGE_LIMIT = 8;
function fail(code) { return Object.assign(new Error(code), { code }); }
export function steamNavigation(value) {
  try { return publicHttps(value, STEAM_HOSTS); } catch { return null; }
}
function validateAttempt(attempt) {
  if (!attempt || !['long', 'short'].includes(attempt.kind)) throw fail('invalid_publication_kind');
  appId(attempt.appId); steamId(attempt.ownerSteamId);
  if (typeof attempt.groupId !== 'string' || !/^[1-9]\d{0,19}$/.test(attempt.groupId)) throw fail('invalid_publication_group');
  if (!['schinese', 'english'].includes(attempt.language) || typeof attempt.text !== 'string' ||
      !attempt.text.trim() || attempt.text.length > 110000 || attempt.text.includes('\0') ||
      !['recommended', 'not_recommended', 'informational'].includes(attempt.recommendation)) throw fail('invalid_publication_content');
  if (attempt.fullReviewUrl) publicHttps(attempt.fullReviewUrl);
  return attempt;
}
function fingerprint(attempt) {
  return createHash('sha256').update(JSON.stringify([attempt.id, attempt.taskId, attempt.ownerSteamId,
    attempt.appId, attempt.groupId, attempt.language, attempt.kind, attempt.text,
    attempt.fullReviewUrl ?? '', attempt.recommendation, attempt.digest])).digest('hex');
}
async function abortable(promise, signal) {
  if (signal.aborted) throw fail('review_readback_cancelled');
  let onAbort;
  const cancelled = new Promise((_, reject) => {
    onAbort = () => reject(fail('review_readback_cancelled'));
    signal.addEventListener('abort', onAbort, { once: true });
  });
  try { return await Promise.race([promise, cancelled]); }
  finally { signal.removeEventListener('abort', onAbort); }
}
export function publicationPage(attempt) {
  validateAttempt(attempt);
  return attempt.kind === 'long' ? `https://store.steampowered.com/app/${attempt.appId}/` :
    `https://store.steampowered.com/curator/${attempt.groupId}/admin/review_create?appid=${attempt.appId}`;
}

// Serialized into an unprivileged official Steam page. This function must be self-contained.
// Current form selectors remain candidates until a user verifies the authenticated Steam form.
export function fillSteamForm(expected, inspectOnly = false) {
  const fail = (code, mutationPossible = false) => ({ success: false, code, mutationPossible });
  if (!expected || !['long', 'short'].includes(expected.kind) || !Number.isInteger(expected.appId) ||
      expected.appId <= 0 || expected.appId > 0xffffffff || !/^7656119\d{10}$/.test(expected.ownerSteamId) ||
      !/^[1-9]\d{0,19}$/.test(expected.groupId) || typeof expected.text !== 'string' ||
      !expected.text.trim() || expected.text.length > 110000 || expected.text.includes('\0')) return fail('invalid_expected_content');
  if (expected.kind === 'short' && (Array.from(expected.text).length >= 200 || /https?:\/\//i.test(expected.text))) return fail('invalid_short_content');
  const fullReviewUrl = expected.fullReviewUrl ?? '';
  if (typeof fullReviewUrl !== 'string' || fullReviewUrl.length > 2000) return fail('invalid_full_link');
  if (fullReviewUrl) {
    try {
      const url = new URL(fullReviewUrl);
      if (url.protocol !== 'https:' || url.username || url.password || url.port) return fail('invalid_full_link');
    } catch { return fail('invalid_full_link'); }
  }
  if (location.protocol !== 'https:' || location.hostname !== 'store.steampowered.com') return fail('wrong_page');
  const validPath = expected.kind === 'long' ? new RegExp(`^/app/${expected.appId}(?:/|$)`).test(location.pathname) :
    location.pathname.replace(/\/$/, '') === `/curator/${expected.groupId}/admin/review_create` &&
      new URL(location.href).searchParams.get('appid') === String(expected.appId);
  if (!validPath) return fail('wrong_target');
  let actualId = String(globalThis.g_steamID ?? '');
  if (!/^7656119\d{10}$/.test(actualId) && Number.isInteger(globalThis.g_AccountID) && globalThis.g_AccountID > 0 && globalThis.g_AccountID <= 0xffffffff) {
    actualId = String(BigInt(globalThis.g_AccountID) + 76561197960265728n);
  }
  if (!/^7656119\d{10}$/.test(actualId)) {
    const profile = document.querySelector('#global_header a.user_avatar, #global_header .playerAvatar a, #global_actions a[href*="/profiles/"]');
    try {
      const url = new URL(profile?.href ?? '');
      actualId = url.protocol === 'https:' && url.hostname === 'steamcommunity.com' ?
        url.pathname.match(/^\/profiles\/(7656119\d{10})(?:\/|$)/)?.[1] ?? '' : '';
    } catch { actualId = ''; }
  }
  if (actualId !== expected.ownerSteamId) return fail(actualId ? 'account_mismatch' : 'account_unverified');
  if (expected.kind === 'short') {
    const ids = new Set(['input[name="appid"]', 'input#appid', 'select[name="appid"]', 'select#appid', 'input[name="app_id"]']
      .flatMap(selector => [...document.querySelectorAll(selector)]));
    if (ids.size !== 1 || String([...ids][0].value) !== String(expected.appId)) return fail('form_app_unverified');
  }
  const findUnique = selectors => {
    const matches = new Set(selectors.flatMap(selector => [...document.querySelectorAll(selector)]));
    const editable = [...matches].filter(node => (node instanceof HTMLTextAreaElement || node instanceof HTMLInputElement) &&
      !node.disabled && !node.readOnly && !(node instanceof HTMLInputElement && !['text', 'url', ''].includes(node.type)));
    return editable.length === 1 ? editable[0] : null;
  };
  const body = findUnique(expected.kind === 'long' ? ['textarea#review_text', 'textarea#ReviewText', 'textarea[name="review_text"]'] :
    ['textarea#blurb', 'textarea#recommendation_blurb', 'textarea[name="blurb"]', 'textarea[name="recommendation_blurb"]']);
  if (!body) return fail('body_field_missing_or_ambiguous');
  // Resolve the independent link even when empty: an old URL must be explicitly cleared.
  const link = expected.kind === 'short' ? findUnique(['input[name="link_url"]', 'input[name="review_url"]',
    'input#link_url', 'input#review_url', 'input[name="external_url"]']) : null;
  if (expected.kind === 'short' && !link) return fail('full_link_field_missing_or_ambiguous');
  if (body === link) return fail('fields_ambiguous');
  if (inspectOnly) return { success: true, code: 'inspected', bodyMaxLength: body.maxLength,
    linkFieldFound: Boolean(link), accountMatched: true, targetMatched: true, mutationPossible: false };
  if (Number.isInteger(body.maxLength) && body.maxLength >= 0 && expected.text.length > body.maxLength) return fail('body_platform_limit');
  if (link && Number.isInteger(link.maxLength) && link.maxLength >= 0 && fullReviewUrl.length > link.maxLength) return fail('full_link_platform_limit');
  const setValue = (field, value) => {
    let prototype = Object.getPrototypeOf(field);
    let setter;
    while (prototype && !setter) { setter = Object.getOwnPropertyDescriptor(prototype, 'value')?.set; prototype = Object.getPrototypeOf(prototype); }
    if (!setter) throw new Error('field_readonly');
    setter.call(field, value);
    field.dispatchEvent(new Event('input', { bubbles: true }));
    field.dispatchEvent(new Event('change', { bubbles: true }));
    return field.value === value;
  };
  try {
    if (!setValue(body, expected.text)) return fail('body_rejected', true);
    if (link && !setValue(link, fullReviewUrl)) return fail('full_link_rejected', true);
    if (body.value !== expected.text || (link && link.value !== fullReviewUrl)) return fail('field_readback_changed', true);
  } catch { return fail('field_write_failed', true); }
  return { success: true, code: 'filled_user_review_required', accountMatched: true, targetMatched: true,
    bodyMaxLength: body.maxLength, fullLinkFilled: Boolean(link), mutationPossible: true,
    recommendationRequiresUser: expected.recommendation, permissionRequiresUser: true };
}

export class PublishingService {
  constructor({ BrowserWindow, session, workspace, onEvent = () => {}, fetchImpl = fetch }) {
    Object.assign(this, { BrowserWindow, session, workspace, onEvent, fetchImpl });
    this.windows = new Map(); this._contexts = new Map(); this._epochs = new Map();
    this._controllers = new Map(); this._clearing = new Map(); this._closed = false;
  }
  _emit(type, data) { try { this.onEvent({ type, data }); } catch {} }
  _account() { return this.workspace.snapshot().activeSteamId; }
  _attempt(taskId, attemptId, requireCurrent = false) {
    if (this._closed) throw fail('publishing_closed');
    const task = this.workspace.task(taskId);
    const attempt = requireCurrent ? this.workspace.assertCurrentAttempt(taskId, attemptId) : task.publications.find(item => item.id === attemptId);
    validateAttempt(attempt);
    if (attempt.taskId !== taskId || attempt.ownerSteamId !== task.ownerSteamId || attempt.ownerSteamId !== this._account() ||
        attempt.appId !== task.appId) throw fail('publication_identity_mismatch');
    return structuredClone(attempt);
  }
  _token(attempt) {
    if (!this._epochs.has(attempt.ownerSteamId)) this._epochs.set(attempt.ownerSteamId, 0);
    return { attempt, accountId: attempt.ownerSteamId, epoch: this._epochs.get(attempt.ownerSteamId), fingerprint: fingerprint(attempt) };
  }
  _assert(token, requireCurrent = false) {
    if (this._closed || token.epoch !== (this._epochs.get(token.accountId) ?? 0) || this._account() !== token.accountId) throw fail('publication_context_changed');
    const current = this._attempt(token.attempt.taskId, token.attempt.id, requireCurrent);
    if (fingerprint(current) !== token.fingerprint) throw fail('publication_changed');
    return current;
  }
  _record(token, fields) {
    this._assert(token);
    const result = this.workspace.recordPublication({ taskId: token.attempt.taskId, attemptId: token.attempt.id, ...fields });
    this._emit('workspace.changed'); return result;
  }
  _markUnknown(context, source) {
    try {
      const current = this._attempt(context.attempt.taskId, context.attempt.id);
      if (fingerprint(current) === context.fingerprint && ['filled', 'submitted_unknown'].includes(current.state)) {
        this.workspace.recordPublication({ taskId: current.taskId, attemptId: current.id,
          state: 'submitted_unknown', evidenceSource: source });
        this._emit('workspace.changed');
      }
    } catch { this._emit('publishing.error', { code: 'publication_result_needs_verification' }); }
  }
  async open(input) {
    const attempt = this._attempt(input?.taskId, input?.id, true);
    if (['verified', 'user_confirmed'].includes(attempt.state)) throw fail('publication_already_finished');
    if (attempt.state === 'submitted_unknown') throw fail('publication_needs_verification');
    const token = this._token(attempt);
    await this._clearing.get(token.accountId);
    this._assert(token, true);
    const existing = this.windows.get(attempt.id);
    if (existing && !existing.isDestroyed()) { existing.show?.(); existing.focus?.(); return { opened: true, url: publicationPage(attempt) }; }
    for (const context of this._contexts.values()) {
      if (!context.closed && context.accountId === token.accountId && context.attempt.taskId === attempt.taskId && context.attempt.kind === attempt.kind) throw fail('publication_window_already_open');
    }
    const partition = this.session.fromPartition(`persist:steam-publishing-${attempt.ownerSteamId}`);
    partition.setPermissionRequestHandler((_contents, _permission, callback) => callback(false));
    partition.setPermissionCheckHandler(() => false);
    const win = new this.BrowserWindow({ width: 1100, height: 820, title: 'Steam 发布 · 请核对后由本人提交',
      webPreferences: { session: partition, nodeIntegration: false, contextIsolation: true, sandbox: true,
        webSecurity: true, allowRunningInsecureContent: false, webviewTag: false } });
    const context = { ...token, win, closed: false, filling: false };
    this._contexts.set(attempt.id, context); this.windows.set(attempt.id, win);
    const allow = url => { try { this._assert(context); return steamNavigation(url); } catch { return null; } };
    win.webContents.setWindowOpenHandler(({ url }) => {
      const safe = allow(url);
      if (safe) win.loadURL(safe).catch(() => this._emit('publishing.error', { code: 'steam_page_load_failed' }));
      return { action: 'deny' };
    });
    for (const name of ['will-navigate', 'will-redirect']) win.webContents.on(name, (event, url) => { if (!allow(url)) event.preventDefault(); });
    win.on('closed', () => {
      context.closed = true;
      if (this.windows.get(attempt.id) === win) this.windows.delete(attempt.id);
      this._markUnknown(context, context.filling ? 'window_closed_during_fill' : 'window_closed_after_fill');
      if (this._contexts.get(attempt.id) === context) this._contexts.delete(attempt.id);
    });
    try {
      await win.loadURL(publicationPage(attempt)); this._assert(context, true);
      if (context.closed || win.isDestroyed()) throw fail('publication_window_closed');
    } catch {
      if (!win.isDestroyed()) win.close();
      throw fail('steam_page_load_failed_or_context_changed');
    }
    return { opened: true, url: publicationPage(attempt) };
  }
  async fill(taskId, attemptId, inspectOnly = false) {
    if (typeof inspectOnly !== 'boolean') throw fail('invalid_inspection_mode');
    const attempt = this._attempt(taskId, attemptId, true);
    if (['verified', 'user_confirmed', 'submitted_unknown'].includes(attempt.state) ||
        (!inspectOnly && attempt.state === 'filled')) throw fail('publication_needs_verification');
    const context = this._contexts.get(attemptId);
    if (!context || context.closed || context.win.isDestroyed()) throw fail('publication_window_closed');
    this._assert(context, true);
    if (context.filling) throw fail('publication_fill_in_progress');
    if (!steamNavigation(context.win.webContents.getURL())) throw fail('wrong_page');
    context.filling = true;
    try {
      if (!inspectOnly) this._record(context, { state: 'submitted_unknown', evidenceSource: 'field_fill_in_progress' });
      const result = await context.win.webContents.executeJavaScript(`(${fillSteamForm.toString()})(${JSON.stringify(attempt)},${JSON.stringify(inspectOnly)})`);
      this._assert(context, true);
      if (context.closed || context.win.isDestroyed()) throw fail('publication_window_closed');
      if (!result || typeof result.success !== 'boolean' || typeof result.code !== 'string' || result.code.length > 100) throw fail('invalid_page_result');
      const safeResult = { success: result.success, code: result.code,
        bodyMaxLength: Number.isInteger(result.bodyMaxLength) ? result.bodyMaxLength : null,
        linkFieldFound: result.linkFieldFound === true, accountMatched: result.accountMatched === true,
        targetMatched: result.targetMatched === true, fullLinkFilled: result.fullLinkFilled === true,
        mutationPossible: result.mutationPossible !== false, recommendationRequiresUser: attempt.recommendation,
        permissionRequiresUser: true };
      if (result.success && (!safeResult.accountMatched || !safeResult.targetMatched ||
          result.code !== (inspectOnly ? 'inspected' : 'filled_user_review_required') ||
          (!inspectOnly && attempt.kind === 'short' && !safeResult.fullLinkFilled))) throw fail('invalid_page_result');
      if (!inspectOnly) {
        if (result.success) this._record(context, { state: 'filled', evidenceSource: 'page_field_readback' });
        else if (result.mutationPossible === false) this._record(context, { state: 'failed', evidenceSource: `fill_precheck_${result.code}`.slice(0, 100) });
        else this._markUnknown(context, 'field_write_outcome_unknown');
      }
      return safeResult;
    } catch {
      if (!inspectOnly) this._markUnknown(context, 'field_fill_outcome_unknown');
      throw fail('publication_fill_failed_or_context_changed');
    } finally { context.filling = false; }
  }
  async _reviewPage(url, signal) {
    const response = await this.fetchImpl(url, { signal, redirect: 'error', credentials: 'omit', headers: { Accept: 'application/json' } });
    if (!response.ok) throw fail(response.status === 429 ? 'review_readback_rate_limited' : 'review_readback_unavailable');
    if (Number(response.headers?.get?.('content-length')) > BODY_LIMIT) { await response.body?.cancel?.(); throw fail('review_readback_too_large'); }
    let raw;
    if (response.body?.getReader) {
      const reader = response.body.getReader(); const chunks = []; let size = 0;
      try {
        while (true) {
          if (signal.aborted) throw fail('review_readback_cancelled');
          const next = await reader.read(); if (next.done) break;
          size += next.value.byteLength;
          if (size > BODY_LIMIT) { await reader.cancel(); throw fail('review_readback_too_large'); }
          chunks.push(next.value);
        }
      } finally { reader.releaseLock(); }
      raw = Buffer.concat(chunks).toString('utf8');
    } else { raw = await response.text(); if (Buffer.byteLength(raw) > BODY_LIMIT) throw fail('review_readback_too_large'); }
    if (signal.aborted) throw fail('review_readback_cancelled');
    let payload; try { payload = JSON.parse(raw); } catch { throw fail('review_readback_invalid'); }
    const data = payload?.response ?? payload;
    if (!Array.isArray(data?.reviews) || data.reviews.length > 100) throw fail('review_readback_invalid');
    return data;
  }
  async verifyLong(taskId, attemptId, resultUrl) {
    const attempt = this._attempt(taskId, attemptId);
    if (attempt.kind !== 'long' || !['recommended', 'not_recommended'].includes(attempt.recommendation)) throw fail('long_review_attempt_required');
    const url = publicHttps(resultUrl, ['steamcommunity.com']); const parsed = new URL(url);
    if (parsed.search || parsed.hash || parsed.pathname !== `/profiles/${attempt.ownerSteamId}/recommended/${attempt.appId}/` &&
        parsed.pathname !== `/profiles/${attempt.ownerSteamId}/recommended/${attempt.appId}`) throw fail('result_author_or_app_mismatch');
    const token = this._token(attempt); const controller = new AbortController();
    this._controllers.set(controller, token.accountId);
    const timeout = setTimeout(() => controller.abort(), 30000);
    const seenCursors = new Set(); let cursor = '*';
    const normalize = value => value.replace(/\r\n/g, '\n');
    try {
      for (let page = 0; page < PAGE_LIMIT; page++) {
        this._assert(token);
        if (controller.signal.aborted) throw fail('review_readback_cancelled');
        seenCursors.add(cursor);
        const input = { appid: attempt.appId, filter: 2, languages: ['all'], review_type: 0, purchase_type: 1, num_per_page: 100, cursor };
        const request = `https://api.steampowered.com/IUserReviewsService/GetAppReviews/v1/?input_json=${encodeURIComponent(JSON.stringify(input))}`;
        const signal = AbortSignal.any([controller.signal, AbortSignal.timeout(12000)]);
        const data = await abortable(this._reviewPage(request, signal), signal);
        this._assert(token);
        const own = data.reviews.filter(review => review?.author?.steamid === attempt.ownerSteamId);
        if (own.length > 1) throw fail('review_readback_ambiguous');
        if (own.length === 1) {
          const review = own[0]; const reviewId = typeof review.recommendationid === 'string' ? review.recommendationid :
            Number.isSafeInteger(review.recommendationid) ? String(review.recommendationid) : '';
          if (typeof review.review !== 'string' || review.review.length > 110000 || typeof review.voted_up !== 'boolean' ||
              !/^[1-9]\d{0,19}$/.test(reviewId) || !['schinese', 'english'].includes(review.language)) throw fail('review_readback_invalid');
          if (normalize(review.review) !== normalize(attempt.text) || review.voted_up !== (attempt.recommendation === 'recommended') ||
              review.language !== attempt.language) return { verified: false, reason: 'content_language_or_recommendation_mismatch' };
          const canonicalUrl = `https://steamcommunity.com/profiles/${attempt.ownerSteamId}/recommended/${attempt.appId}/`;
          this._record(token, { state: 'verified', resultUrl: canonicalUrl, evidenceSource: 'steam_api_readback' });
          return { verified: true, resultUrl: canonicalUrl, reviewId, evidenceSource: 'steam_api_readback' };
        }
        if (!data.reviews.length || typeof data.cursor !== 'string' || !data.cursor || data.cursor.length > 2048 || seenCursors.has(data.cursor)) {
          return { verified: false, reason: 'not_found_or_cached_retry_readback_later' };
        }
        cursor = data.cursor;
      }
      return { verified: false, reason: 'search_limit_or_cached_retry_readback_later' };
    } catch (error) {
      const allowed = ['publication_context_changed', 'publication_changed', 'review_readback_rate_limited',
        'review_readback_unavailable', 'review_readback_too_large', 'review_readback_invalid', 'review_readback_ambiguous'];
      throw fail(controller.signal.aborted ? 'review_readback_cancelled_or_timeout' : allowed.includes(error?.code) ? error.code : 'review_readback_unavailable');
    } finally { clearTimeout(timeout); controller.abort(); this._controllers.delete(controller); }
  }
  async logout(accountId) {
    if (accountId !== undefined && accountId !== null) steamId(accountId);
    const accounts = accountId ? new Set([accountId]) : new Set([
      ...this._epochs.keys(), ...[...this._contexts.values()].map(context => context.accountId), ...this._controllers.values()
    ]);
    for (const account of accounts) this._epochs.set(account, (this._epochs.get(account) ?? 0) + 1);
    for (const [controller, account] of this._controllers) if (accounts.has(account)) controller.abort();
    for (const context of this._contexts.values()) {
      if (accounts.has(context.accountId) && !context.closed) { this._markUnknown(context, 'account_session_closed_after_fill'); context.win.close(); }
    }
    await Promise.all([...accounts].map(account => {
      const prior = this._clearing.get(account) ?? Promise.resolve();
      const clearing = prior.catch(() => {}).then(async () => {
        const partition = this.session.fromPartition(`persist:steam-publishing-${account}`);
        try { await partition.clearStorageData(); await partition.clearCache(); }
        catch { throw fail('steam_session_clear_failed'); }
      });
      this._clearing.set(account, clearing); return clearing;
    }));
    return { cleared: true };
  }
  async close() {
    if (this._closePromise) return this._closePromise;
    // Record uncertainty while the workspace is still accessible, then revoke all async work.
    const clearing = this.logout();
    this._closed = true;
    this._closePromise = clearing.finally(() => this.windows.clear());
    return this._closePromise;
  }
}
