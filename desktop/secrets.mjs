import { existsSync, renameSync, rmSync } from 'node:fs';
import { randomUUID } from 'node:crypto';
import { JsonRepository } from './repository.mjs';

const KEYS = ['aiKey', 'webApiKey', 'searchKey', 'steamSession'];
function savedRecord(value) {
  const saved = value;
  if (!saved || typeof saved !== 'object' || Array.isArray(saved) || KEYS.some(key => saved[key] !== undefined && typeof saved[key] !== 'string')) {
    throw new Error('credential_store_unreadable');
  }
  return saved;
}
function readSaved(repository) {
  return existsSync(repository.path) ? savedRecord(repository.load()) : {};
}
export class SecretStore {
  constructor(path, safeStorage) {
    this.repository = new JsonRepository(path);
    this.safeStorage = safeStorage;
    this.ephemeral = new Map();
    this.ephemeralAiOrigin = null;
    this.revision = 0;
  }
  available() {
    return this.safeStorage.isEncryptionAvailable() && this.safeStorage.getSelectedStorageBackend?.() !== 'basic_text';
  }
  read(key, endpoint) {
    if (!KEYS.includes(key)) throw new Error('invalid_secret_key');
    let saved;
    try { saved = readSaved(this.repository); } catch { throw new Error('credential_store_unreadable'); }
    if (this.ephemeral.has(key)) {
      if (key === 'aiKey' && endpoint && this.ephemeralAiOrigin !== new URL(endpoint).origin) throw new Error('ai_key_endpoint_mismatch');
      return this.ephemeral.get(key);
    }
    if (!saved[key]) return '';
    if (key === 'aiKey' && endpoint && (saved.aiKeyOrigin ?? 'https://generativelanguage.googleapis.com') !== new URL(endpoint).origin) throw new Error('ai_key_endpoint_mismatch');
    if (!this.available()) throw new Error('credential_encryption_unavailable');
    try { return this.safeStorage.decryptString(Buffer.from(saved[key], 'base64')); }
    catch { throw new Error('credential_decryption_failed'); }
  }
  write(key, value, persist = true, endpoint = 'https://generativelanguage.googleapis.com/v1beta') {
    return this.patch({ [key]: value }, persist, endpoint);
  }
  readSteamSession() {
    const value = this.read('steamSession');
    if (!value) return null;
    try {
      const session = JSON.parse(value);
      if (!session || typeof session.accountName !== 'string' || !session.accountName.trim() || session.accountName.length > 128 ||
          !/^\d{17}$/.test(session.steamId) || typeof session.refreshToken !== 'string' || !session.refreshToken) throw new Error();
      return { accountName: session.accountName, steamId: session.steamId, refreshToken: session.refreshToken };
    } catch { throw new Error('invalid_steam_session'); }
  }
  writeSteamSession({ accountName, steamId, refreshToken } = {}) {
    if (typeof accountName !== 'string' || !accountName.trim() || accountName.length > 128 ||
        typeof steamId !== 'string' || !/^\d{17}$/.test(steamId) || typeof refreshToken !== 'string' || !refreshToken) throw new Error('invalid_steam_session');
    this.write('steamSession', JSON.stringify({ accountName, steamId, refreshToken }));
  }
  clearSteamSession() { this.write('steamSession', ''); }
  patch(changes, persist = true, endpoint = 'https://generativelanguage.googleapis.com/v1beta') {
    const prepared = this.preparePatch(changes, persist, endpoint);
    try { return prepared.commit(); } finally { prepared.cancel(); }
  }
  preparePatch(changes, persist = true, endpoint = 'https://generativelanguage.googleapis.com/v1beta') {
    for (const [key, value] of Object.entries(changes)) {
      if (!KEYS.includes(key) || typeof value !== 'string' || value.length > 10000) throw new Error('invalid_secret');
    }
    if (!Object.keys(changes).length) return { commit() {}, cancel() {} };
    if (persist && Object.values(changes).some(Boolean) && !this.available()) throw new Error('credential_encryption_unavailable_use_session_only');
    let saved;
    try { saved = readSaved(this.repository); } catch { throw new Error('credential_store_unreadable'); }
    const original = JSON.stringify(saved), revision = this.revision;
    const previous = { ...saved }; delete previous.steamSession;
    const runtime = new Map(this.ephemeral);
    let runtimeOrigin = this.ephemeralAiOrigin;
    for (const [key, value] of Object.entries(changes)) {
      if (!value || !persist) delete saved[key];
      else saved[key] = this.safeStorage.encryptString(value).toString('base64');
      if (key === 'aiKey') {
        if (persist && value) saved.aiKeyOrigin = new URL(endpoint).origin;
        else delete saved.aiKeyOrigin;
      }
      if (persist || !value) runtime.delete(key); else runtime.set(key, value);
      if (key === 'aiKey') runtimeOrigin = !persist && value ? new URL(endpoint).origin : null;
    }
    const stagedPath = `${this.repository.path}.${randomUUID()}.prepared`;
    const backupPath = `${stagedPath}.backup`;
    // JsonRepository writes/fsyncs a mode-0600 file. The live ciphertext and
    // runtime values stay untouched until the settings owner commits them.
    new JsonRepository(stagedPath).save(saved);
    let settled = false;
    const cancel = () => { if (!settled) { settled = true; rmSync(stagedPath, { force: true }); rmSync(backupPath, { force: true }); } };
    return {
      commit: () => {
        if (settled) throw new Error('credential_patch_already_settled');
        if (this.revision !== revision || JSON.stringify(readSaved(this.repository)) !== original) throw new Error('credential_store_changed_retry');
        try {
          // Reusable Steam sessions never survive in credential rollback files.
          if (existsSync(this.repository.path)) {
            new JsonRepository(backupPath).save(previous);
            renameSync(backupPath, `${this.repository.path}.previous`);
          }
          renameSync(stagedPath, this.repository.path);
        } catch { throw new Error('credential_store_write_failed'); }
        settled = true;
        this.revision++;
        this.ephemeral = runtime; this.ephemeralAiOrigin = runtimeOrigin;
      },
      cancel,
    };
  }
  status() {
    let saved = {};
    let credentialStoreError;
    try {
      saved = readSaved(this.repository);
    } catch { saved = {}; credentialStoreError = 'credential_store_unreadable'; }
    return { encryptionAvailable: this.available(), aiKeyConfigured: Boolean(saved.aiKey || this.ephemeral.get('aiKey')),
      webApiKeyConfigured: Boolean(saved.webApiKey || this.ephemeral.get('webApiKey')),
      searchKeyConfigured: Boolean(saved.searchKey || this.ephemeral.get('searchKey')),
      steamSessionConfigured: Boolean(saved.steamSession),
      ...(credentialStoreError ? { credentialStoreError } : {}) };
  }
  resetCorrupt() {
    if (!this.status().credentialStoreError) throw new Error('credential_store_not_corrupt');
    const archivePath = `${this.repository.path}.corrupt-${randomUUID()}`;
    try { renameSync(this.repository.path, archivePath); }
    catch { throw new Error('credential_store_reset_failed'); }
    this.ephemeral.clear(); this.ephemeralAiOrigin = null;
    this.revision++;
    return { reset: true, archivePath };
  }
}
