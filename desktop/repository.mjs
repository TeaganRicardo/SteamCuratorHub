import { mkdirSync, readFileSync, writeFileSync, renameSync, openSync, closeSync, fsyncSync, copyFileSync, existsSync, statSync, rmSync } from 'node:fs';
import { dirname } from 'node:path';
import { randomUUID } from 'node:crypto';

export class JsonRepository {
  constructor(path) { this.path = path; }
  load() {
    if (!existsSync(this.path)) return null;
    if (statSync(this.path).size > 20_000_000) throw new Error('workspace_too_large');
    const raw = readFileSync(this.path, 'utf8');
    if (Buffer.byteLength(raw) > 20_000_000) throw new Error('workspace_too_large');
    try { return JSON.parse(raw); }
    catch { throw new Error('workspace_corrupt_restore_backup'); }
  }
  save(value) {
    const content = JSON.stringify(value, null, 2) + '\n';
    if (Buffer.byteLength(content) > 20_000_000) throw new Error('workspace_too_large');
    mkdirSync(dirname(this.path), { recursive: true, mode: 0o700 });
    const temp = `${this.path}.${randomUUID()}.tmp`;
    let staged = false;
    try {
      // Windows FlushFileBuffers requires write access. Keep the exclusively
      // created writable handle through write and flush instead of reopening read-only.
      const fd = openSync(temp, 'wx', 0o600); staged = true;
      try { writeFileSync(fd, content); fsyncSync(fd); } finally { closeSync(fd); }
      if (existsSync(this.path)) copyFileSync(this.path, `${this.path}.previous`);
      renameSync(temp, this.path); staged = false;
    } finally { if (staged) rmSync(temp, { force: true }); }
  }
}
