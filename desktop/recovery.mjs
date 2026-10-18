import { copyFileSync, chmodSync, constants, existsSync, lstatSync, readFileSync,
  readdirSync, renameSync, unlinkSync } from 'node:fs';
import { basename, dirname, join, resolve } from 'node:path';
import { randomUUID } from 'node:crypto';
import { JsonRepository } from './repository.mjs';
import { normalizeWorkspace, Workspace } from './workspace.mjs';

const MAX_BYTES = 20 * 1024 * 1024;
function failure(code) { return Object.assign(new Error(code), { code }); }
function signature(path) {
  try {
    const stat = lstatSync(path, { bigint: true });
    if (!stat.isFile()) throw failure('workspace_not_regular_file');
    return [stat.dev, stat.ino, stat.size, stat.mtimeNs, stat.ctimeNs].join(':');
  } catch (error) { if (error.code === 'ENOENT') return null; throw error; }
}
function readCandidate(path) {
  const stat = lstatSync(path);
  if (!stat.isFile() || stat.size > MAX_BYTES) throw failure('backup_missing_or_too_large');
  const raw = readFileSync(path);
  if (raw.byteLength > MAX_BYTES) throw failure('backup_too_large');
  const text = new TextDecoder('utf-8', { fatal: true }).decode(raw);
  return normalizeWorkspace(JSON.parse(text));
}
function cleanupStaging(path) {
  // JsonRepository may leave its own uniquely-prefixed temporary file after an IO failure.
  const prefix = basename(path);
  try {
    for (const file of readdirSync(dirname(path))) {
      if (file === prefix || file === `${prefix}.previous` || (file.startsWith(`${prefix}.`) && file.endsWith('.tmp'))) {
        try { unlinkSync(join(dirname(path), file)); } catch {}
      }
    }
  } catch {}
}
function restore(path, normalized, originalSignature) {
  const staging = `${path}.recovery-${randomUUID()}.tmp`;
  let archivePath;
  try {
    // Stage on the same filesystem. Do not call save(path): that would replace a good .previous
    // with the corrupt current file before recovery has completed.
    new JsonRepository(staging).save(normalized);
    if (signature(path) !== originalSignature) throw failure('workspace_changed_during_recovery');
    if (originalSignature !== null) {
      archivePath = `${path}.corrupt-${new Date().toISOString().replace(/[:.]/g, '-')}-${randomUUID()}`;
      copyFileSync(path, archivePath, constants.COPYFILE_EXCL | constants.COPYFILE_FICLONE);
      chmodSync(archivePath, 0o600);
      if (signature(path) !== originalSignature) throw failure('workspace_changed_during_recovery');
    }
    renameSync(staging, path);
    return archivePath ?? null;
  } finally { cleanupStaging(staging); }
}

/**
 * Returns Workspace, or null when the user chooses Exit. Dialog has Electron's
 * showMessageBox(options) and showOpenDialog(options) methods. No normal-start dialog.
 * Recovery metadata is on workspace.recovery, outside saved state and snapshots.
 */
export async function openWorkspace({ path, dialog }) {
  if (typeof path !== 'string' || !path || !dialog?.showMessageBox || !dialog?.showOpenDialog) throw failure('invalid_recovery_options');
  const target = resolve(path);
  const repository = new JsonRepository(target);
  while (true) {
    let originalSignature;
    try {
      originalSignature = signature(target);
      if (originalSignature !== null && lstatSync(target).size > MAX_BYTES) throw failure('workspace_too_large');
      return new Workspace(repository);
    } catch {
      // Retain the failed original's identity, not just its path, across asynchronous dialogs.
      try { originalSignature = signature(target); } catch { originalSignature = 'unreadable'; }
    }
    const previous = `${target}.previous`;
    const decision = await dialog.showMessageBox({ type: 'warning', title: '工作区需要恢复',
      message: '工作区文件无法读取或结构不合法。',
      detail: `${existsSync(previous) ? '检测到本机 .previous 备份。' : '没有找到本机 .previous 备份。'}\n选择恢复后，先验证备份，再将原文件保留为本机 .corrupt 文件。发布成功记录需要重新核实；凭据不会导入。`,
      buttons: ['退出', '恢复 .previous 备份', '选择备份文件'], defaultId: 0, cancelId: 0, noLink: true });
    if (decision.response !== 1 && decision.response !== 2) return null;
    let source = previous;
    if (decision.response === 2) {
      const selection = await dialog.showOpenDialog({ title: '选择工作区备份', properties: ['openFile'],
        filters: [{ name: 'JSON 工作区备份', extensions: ['json'] }] });
      if (selection.canceled || !Array.isArray(selection.filePaths) || selection.filePaths.length !== 1) continue;
      source = selection.filePaths[0];
    }
    try {
      const normalized = readCandidate(source);
      const archivePath = restore(target, normalized, originalSignature);
      const workspace = new Workspace(repository);
      workspace.recovery = { source: decision.response === 1 ? 'previous' : 'selected_backup', archivePath };
      return workspace;
    } catch (error) {
      await dialog.showMessageBox({ type: 'error', title: '恢复未完成',
        message: error?.code === 'workspace_changed_during_recovery' ? '恢复期间工作区文件已改变，请重新检查。' : '无法使用这个备份恢复工作区。',
        detail: '可能是备份缺失、超过 20 MiB、内容不合法，或本机文件写入失败。原工作区保持原样；请选择其他备份或退出。',
        buttons: ['返回恢复选项'], defaultId: 0, cancelId: 0, noLink: true });
    }
  }
}
