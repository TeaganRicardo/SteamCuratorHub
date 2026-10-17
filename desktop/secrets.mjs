import { existsSync, renameSync, rmSync, copyFileSync } from 'node:fs';
import { randomUUID } from 'node:crypto';
import { JsonRepository } from './repository.mjs';

const KEYS = ['aiKey', 'webApiKey', 'searchKey'];
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
  patch(changes, persist = true, endpoint = 'https://generativelanguage.googleapis.com/v1beta') {
    const prepared = this.preparePatch(changes, persist, endpoint);
    try { return prepared.commit(); } finally { prepared.cancel(); }
  }
  preparePatch(changes, persist = true, endpoint = 'https://generativelanguage.googleapis.com/v1beta') {
    for (const [key, value] of Object.entries(changes)) {
      if (!KEYS.includes(key) || typeof value !== 'string' || value.length > 10000) throw new Error('invalid_secret');
    }
    if (!Object.keys(changes).length) return { commit() {}, cancel() {} };
    if (persist && !this.available()) throw new Error('credential_encryption_unavailable_use_session_only');
    let saved;
    try { saved = readSaved(this.repository); } catch { throw new Error('credential_store_unreadable'); }
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
    // JsonRepository writes/fsyncs a mode-0600 file. The live ciphertext and
    // runtime values stay untouched until the settings owner commits them.
    new JsonRepository(stagedPath).save(saved);
    let settled = false;
    const cancel = () => { if (!settled) { settled = true; rmSync(stagedPath, { force: true }); } };
    return {
      commit: () => {
        if (settled) throw new Error('credential_patch_already_settled');
        try {
          if (existsSync(this.repository.path)) copyFileSync(this.repository.path, `${this.repository.path}.previous`);
          renameSync(stagedPath, this.repository.path);
        } catch { throw new Error('credential_store_write_failed'); }
        settled = true;
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
      ...(credentialStoreError ? { credentialStoreError } : {}) };
  }
  resetCorrupt() {
    if (!this.status().credentialStoreError) throw new Error('credential_store_not_corrupt');
    const archivePath = `${this.repository.path}.corrupt-${randomUUID()}`;
    try { renameSync(this.repository.path, archivePath); }
    catch { throw new Error('credential_store_reset_failed'); }
    this.ephemeral.clear(); this.ephemeralAiOrigin = null;
    return { reset: true, archivePath };
  }
}
