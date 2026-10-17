import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { SecretStore } from '../secrets.mjs';

// The OS keychain is replaced by a deterministic synthetic codec. These tests verify
// routing, transactional state and fallback policy; they do not prove OS encryption.
function setup(t) {
  const dir = mkdtempSync(join(tmpdir(), 'sch-secret-fixture-')); t.after(() => rmSync(dir, { recursive: true, force: true }));
  const path = join(dir, 'credentials.json'); let available = true; let backend = 'keychain';
  const codec = { encrypts: [], decrypts: [],
    isEncryptionAvailable: () => available, getSelectedStorageBackend: () => backend,
    encryptString(value) { this.encrypts.push(value); return Buffer.from(`synthetic:${Buffer.from(value).toString('base64')}`); },
    decryptString(value) { this.decrypts.push(value); return Buffer.from(value.toString().slice('synthetic:'.length), 'base64').toString(); } };
  return { path, codec, store: new SecretStore(path, codec), setAvailable(value) { available = value; }, setBackend(value) { backend = value; } };
}

test('persisted synthetic credentials are decoded only for the configured AI origin', t => {
  const s = setup(t); const origin = 'https://provider.example/v1';
  s.store.patch({ aiKey: 'fake-ai-secret', searchKey: 'fake-search-secret', webApiKey: 'fake-web-secret' }, true, origin);
  const raw = readFileSync(s.path, 'utf8');
  for (const value of ['fake-ai-secret', 'fake-search-secret', 'fake-web-secret']) assert.equal(raw.includes(value), false);
  assert.equal(s.store.read('aiKey', 'https://provider.example/other-path'), 'fake-ai-secret');
  const decrypts = s.codec.decrypts.length;
  assert.throws(() => s.store.read('aiKey', 'https://different.example/v1'), /ai_key_endpoint_mismatch/);
  assert.equal(s.codec.decrypts.length, decrypts);
  assert.equal(s.store.read('searchKey'), 'fake-search-secret');
  assert.equal(new SecretStore(s.path, s.codec).read('webApiKey'), 'fake-web-secret');
  assert.deepEqual(s.store.status(), { encryptionAvailable: true, aiKeyConfigured: true, searchKeyConfigured: true, webApiKeyConfigured: true });
});

test('session-only AI replacement removes its persisted predecessor and remains origin-bound only in memory', t => {
  const s = setup(t); s.store.patch({ aiKey: 'fake-old', searchKey: 'fake-persisted-search' }, true, 'https://old.example/v1');
  s.store.patch({ aiKey: 'fake-session' }, false, 'https://new.example/v1');
  assert.equal(s.store.read('aiKey', 'https://new.example/v1'), 'fake-session');
  assert.throws(() => s.store.read('aiKey', 'https://old.example/v1'), /ai_key_endpoint_mismatch/);
  assert.equal(new SecretStore(s.path, s.codec).read('aiKey', 'https://new.example/v1'), '');
  assert.equal(s.store.read('searchKey'), 'fake-persisted-search');
  const raw = JSON.parse(readFileSync(s.path, 'utf8')); assert.equal(raw.aiKey, undefined); assert.equal(raw.aiKeyOrigin, undefined);
  s.store.patch({ aiKey: '' }, false); assert.equal(s.store.read('aiKey'), '');
});

test('unavailable or basic_text storage refuses persistence but permits session-only synthetic credentials', t => {
  const s = setup(t); s.setBackend('basic_text');
  assert.equal(s.store.status().encryptionAvailable, false);
  assert.throws(() => s.store.patch({ aiKey: 'fake-no-plaintext' }, true), /credential_encryption_unavailable_use_session_only/);
  assert.equal(s.codec.encrypts.length, 0);
  s.store.patch({ aiKey: 'fake-memory-only' }, false);
  assert.equal(s.store.read('aiKey', 'https://generativelanguage.googleapis.com/v1beta'), 'fake-memory-only');
  assert.equal(readFileSync(s.path, 'utf8').includes('fake-memory-only'), false);
  s.setBackend('keychain'); s.setAvailable(false);
  assert.throws(() => s.store.write('webApiKey', 'fake-api'), /credential_encryption_unavailable_use_session_only/);
});

test('patch validates every entry before encryption or modifying persisted/runtime values', t => {
  const s = setup(t); s.store.patch({ aiKey: 'fake-before' }, false, 'https://provider.example/v1');
  const raw = readFileSync(s.path, 'utf8');
  assert.throws(() => s.store.patch({ aiKey: 'fake-after', unexpectedKey: 'invalid' }, true, 'https://provider.example/v1'), /invalid_secret/);
  assert.equal(s.codec.encrypts.length, 0); assert.equal(readFileSync(s.path, 'utf8'), raw);
  assert.equal(s.store.read('aiKey', 'https://provider.example/v1'), 'fake-before');
  assert.throws(() => s.store.patch({ searchKey: 'x'.repeat(10001) }), /invalid_secret/);
  assert.throws(() => s.store.read('unknown'), /invalid_secret_key/);
});

test('encryption failure halfway through a patch preserves all old disk and session state', t => {
  const s = setup(t); s.store.patch({ aiKey: 'fake-before', searchKey: 'fake-old-search' }, false, 'https://provider.example/v1');
  const raw = readFileSync(s.path, 'utf8'); const encrypt = s.codec.encryptString;
  s.codec.encryptString = function (value) { if (value === 'fake-fails') throw new Error('synthetic encryption failure'); return encrypt.call(this, value); };
  assert.throws(() => s.store.patch({ aiKey: 'fake-after', searchKey: 'fake-fails' }, true, 'https://new.example/v1'), /synthetic encryption failure/);
  assert.equal(readFileSync(s.path, 'utf8'), raw);
  assert.equal(s.store.read('aiKey', 'https://provider.example/v1'), 'fake-before');
  assert.equal(s.store.read('searchKey'), 'fake-old-search');
});

test('repository failure does not install new session values or their endpoint binding', t => {
  const s = setup(t); s.store.patch({ aiKey: 'fake-before' }, false, 'https://old.example/v1');
  const raw = readFileSync(s.path, 'utf8');
  s.store.repository.save = () => { throw new Error('synthetic disk failure'); };
  assert.throws(() => s.store.patch({ aiKey: 'fake-after' }, false, 'https://new.example/v1'), /synthetic disk failure/);
  assert.equal(readFileSync(s.path, 'utf8'), raw);
  assert.equal(s.store.read('aiKey', 'https://old.example/v1'), 'fake-before');
  assert.throws(() => s.store.read('aiKey', 'https://new.example/v1'), /ai_key_endpoint_mismatch/);
});

test('unreadable or undecryptable saved credentials expose stable errors without ciphertext content', t => {
  const s = setup(t); writeFileSync(s.path, '{corrupt-synthetic-ciphertext');
  assert.throws(() => s.store.read('aiKey'), { message: 'credential_store_unreadable' });
  s.store.repository.save({ aiKey: 'synthetic-ciphertext', aiKeyOrigin: 'https://provider.example' });
  s.codec.decryptString = () => { throw new Error('raw keychain details: synthetic-ciphertext'); };
  assert.throws(() => s.store.read('aiKey', 'https://provider.example/v1'), { message: 'credential_decryption_failed' });
  s.setAvailable(false);
  assert.throws(() => s.store.read('aiKey', 'https://provider.example/v1'), { message: 'credential_encryption_unavailable' });
});

test('legacy AI ciphertext without origin metadata is restricted to the original Google origin', t => {
  const s = setup(t); s.store.repository.save({ aiKey: s.codec.encryptString('fake-legacy').toString('base64') });
  assert.equal(s.store.read('aiKey', 'https://generativelanguage.googleapis.com/v1beta'), 'fake-legacy');
  assert.throws(() => s.store.read('aiKey', 'https://new.example/v1'), /ai_key_endpoint_mismatch/);
});
