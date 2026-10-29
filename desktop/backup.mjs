import { randomUUID } from 'node:crypto';
import { normalizeWorkspace } from './workspace.mjs';

// Run checkpoints can change while the operator stops runs after previewing.
// Drafts, targets, groups and settings must still match the reviewed workspace.
const fingerprint = state => JSON.stringify({ ...state, runHistory: [] });
export class BackupRestore {
  constructor({ workspace, controller, publishing }) { Object.assign(this, { workspace, controller, publishing }); }
  preview(value) {
    const imported = normalizeWorkspace(value);
    imported.settings = structuredClone(this.workspace.state.settings);
    imported.activeSteamId = this.workspace.state.activeSteamId;
    const previewId = randomUUID();
    this.pending = { previewId, imported, baseline: fingerprint(this.workspace.state) };
    const visible = structuredClone(imported);
    visible.tasks = visible.tasks.filter(task => task.ownerSteamId === visible.activeSteamId);
    visible.runHistory = visible.runHistory.filter(run => run.ownerSteamId === visible.activeSteamId);
    const otherCounts = state => ({
      tasks: state.tasks.filter(task => task.ownerSteamId !== visible.activeSteamId).length,
      runs: state.runHistory.filter(run => run.ownerSteamId !== visible.activeSteamId).length,
    });
    const currentOther = otherCounts(this.workspace.state), importedOther = otherCounts(imported);
    return { previewId, workspace: visible, scope: {
      currentOtherTasks: currentOther.tasks, importedOtherTasks: importedOther.tasks,
      currentOtherRuns: currentOther.runs, importedOtherRuns: importedOther.runs,
    } };
  }
  async restore(previewId) {
    const preview = this.pending;
    if (!preview || preview.previewId !== previewId) throw new Error('backup_preview_expired');
    const release = this.controller.acquireMaintenance();
    try {
      if (fingerprint(this.workspace.state) !== preview.baseline) throw new Error('backup_preview_changed');
      await this.publishing.logout();
      if (this.pending !== preview) throw new Error('backup_preview_expired');
      this.controller.assertIdle();
      if (fingerprint(this.workspace.state) !== preview.baseline) throw new Error('backup_preview_changed');
      this.workspace.importBackup(preview.imported);
      this.pending = null;
      this.controller.changed();
      return this.controller.snapshot();
    } finally { release(); }
  }
}
