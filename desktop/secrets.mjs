import { readFileSync, existsSync } from 'node:fs';
import { JsonRepository } from './repository.mjs';

const KEYS = ['aiKey', 'webApiKey', 'searchKey'];
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
    if (this.ephemeral.has(key)) {
      if (key === 'aiKey' && endpoint && this.ephemeralAiOrigin !== new URL(endpoint).origin) throw new Error('ai_key_endpoint_mismatch');
      return this.ephemeral.get(key);
    }
    if (!existsSync(this.repository.path)) return '';
    let saved;
    try { saved = JSON.parse(readFileSync(this.repository.path, 'utf8')); } catch { throw new Error('credential_store_unreadable'); }
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
    if (!Object.keys(changes).length) return;
    for (const [key, value] of Object.entries(changes)) {
      if (!KEYS.includes(key) || typeof value !== 'string' || value.length > 10000) throw new Error('invalid_secret');
    }
    if (persist && !this.available()) throw new Error('credential_encryption_unavailable_use_session_only');
    const saved = this.repository.load() ?? {};
    for (const [key, value] of Object.entries(changes)) {
      if (!value || !persist) delete saved[key];
      else saved[key] = this.safeStorage.encryptString(value).toString('base64');
      if (key === 'aiKey') {
        if (persist && value) saved.aiKeyOrigin = new URL(endpoint).origin;
        else delete saved.aiKeyOrigin;
      }
    }
    this.repository.save(saved);
    for (const [key, value] of Object.entries(changes)) {
      if (persist || !value) this.ephemeral.delete(key); else this.ephemeral.set(key, value);
      if (key === 'aiKey') this.ephemeralAiOrigin = !persist && value ? new URL(endpoint).origin : null;
    }
  }
  status() {
    const saved = this.repository.load() ?? {};
    return { encryptionAvailable: this.available(), aiKeyConfigured: Boolean(saved.aiKey || this.ephemeral.get('aiKey')),
      webApiKeyConfigured: Boolean(saved.webApiKey || this.ephemeral.get('webApiKey')),
      searchKeyConfigured: Boolean(saved.searchKey || this.ephemeral.get('searchKey')) };
  }
}
