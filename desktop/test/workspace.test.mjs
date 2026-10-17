import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { Workspace } from '../workspace.mjs';
import { JsonRepository } from '../repository.mjs';

const account = '76561198000000000';
test('task and draft ownership survives restart; browsing or another account cannot overwrite it', () => {
  const dir = mkdtempSync(join(tmpdir(), 'sch-workspace-'));
  try {
    const workspace = new Workspace(new JsonRepository(join(dir, 'workspace.json')));
    workspace.bindAccount(account);
    const task = workspace.createTask({ appId: 1001, title: 'Example', groupId: '45134494' });
    workspace.saveDraft({ taskId: task.id, language: 'schinese', kind: 'long', text: 'My review', expectedRevision: 0 });
    assert.throws(() => workspace.saveDraft({ taskId: task.id, language: 'schinese', kind: 'long', text: 'Late AI', expectedRevision: 0 }), /draft_changed/);
    const restored = new Workspace(new JsonRepository(join(dir, 'workspace.json')));
    assert.equal(restored.task(task.id).drafts.schinese.long.text, 'My review');
    restored.bindAccount('76561198000000001');
    assert.deepEqual(restored.snapshot().tasks, []);
    assert.throws(() => restored.task(task.id), /task_not_found/);
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test('long footer is composed once; short text and independent URL never receive it', () => {
  const repository = { load: () => null, save() {} };
  const workspace = new Workspace(repository); workspace.bindAccount(account);
  const task = workspace.createTask({ appId: 1001, title: 'Example', groupId: '45134494' });
  workspace.saveDraft({ taskId: task.id, language: 'schinese', kind: 'long', text: '正文', expectedRevision: 0 });
  workspace.saveDraft({ taskId: task.id, language: 'schinese', kind: 'short', text: '中文 and symbols!', expectedRevision: 0 });
  workspace.saveDraftOptions({ taskId: task.id, language: 'schinese', fullReviewUrl: 'https://steamcommunity.com/profiles/76561198000000000/recommended/1001/', recommendation: 'recommended' });
  const first = workspace.content(task.id, 'schinese', 'long');
  assert.equal(first.text.match(/浮生评测/g).length, 1);
  assert.equal(workspace.content(task.id, 'schinese', 'long').text, first.text);
  const short = workspace.content(task.id, 'schinese', 'short');
  assert.equal(short.text, '中文 and symbols!'); assert.ok(short.fullReviewUrl.includes('/1001/'));
  workspace.saveDraft({ taskId: task.id, language: 'schinese', kind: 'short', text: '中'.repeat(200), expectedRevision: 1 });
  assert.throws(() => workspace.content(task.id, 'schinese', 'short'), /under_200/);
});

test('publication freezes identity/content and blocks late fill or blind retry after unknown result', () => {
  const workspace = new Workspace({ load: () => null, save() {} }); workspace.bindAccount(account);
  const task = workspace.createTask({ appId: 1001, title: 'Example', groupId: '45134494' });
  workspace.saveDraft({ taskId: task.id, language: 'english', kind: 'long', text: 'Review', expectedRevision: 0 });
  const attempt = workspace.preparePublication(task.id, 'english', 'long');
  workspace.saveDraft({ taskId: task.id, language: 'english', kind: 'long', text: 'Revised', expectedRevision: 1 });
  assert.throws(() => workspace.assertCurrentAttempt(task.id, attempt.id), /publication_changed/);
  workspace.recordPublication({ taskId: task.id, attemptId: attempt.id, state: 'submitted_unknown' });
  assert.throws(() => workspace.preparePublication(task.id, 'english', 'long'), /needs_verification/);
  assert.throws(() => workspace.recordPublication({ taskId: task.id, attemptId: attempt.id, state: 'verified', resultUrl: 'https://steamcommunity.com/x' }), /evidence_required/);
});

test('disk failure preserves last in-memory state and imported success is not trusted as live verification', () => {
  let fail = false;
  const workspace = new Workspace({ load: () => null, save() { if (fail) throw new Error('disk_full'); } });
  workspace.bindAccount(account);
  const task = workspace.createTask({ appId: 1001, title: 'Example', groupId: '45134494' });
  fail = true;
  assert.throws(() => workspace.updateTask({ taskId: task.id, status: 'completed' }), /disk_full/);
  assert.equal(workspace.task(task.id).status, 'active');
  fail = false;
  const backup = workspace.exportBackup();
  backup.settings.apiKey = 'SHOULD_NOT_PERSIST';
  backup.tasks[0].publications.push({ id: 'imported', kind: 'long', state: 'verified' });
  workspace.importBackup(backup);
  assert.equal(workspace.task(task.id).publications[0].state, 'imported_unverified');
  assert.equal(JSON.stringify(workspace.exportBackup()).includes('SHOULD_NOT_PERSIST'), false);
});

test('switching groups replaces any known footer and backup evidence cannot carry credential fields', () => {
  const workspace = new Workspace({ load: () => null, save() {} }); workspace.bindAccount(account);
  const task = workspace.createTask({ appId: 1001, title: 'Example', groupId: '45134494' });
  const oldFooter = workspace.snapshot().groups[0].footers.schinese;
  workspace.saveDraft({ taskId: task.id, language: 'schinese', kind: 'long', text: `正文\n\n${oldFooter}`, expectedRevision: 0 });
  workspace.updateTask({ taskId: task.id, groupId: '45525570' });
  const value = workspace.content(task.id, 'schinese', 'long').text;
  assert.equal(value.includes('浮生评测'), false); assert.equal(value.includes('桃你欢心'), true);
  const backup = workspace.exportBackup();
  backup.tasks[0].research = { content: 'Source text is preserved', apiKey: 'secret-A', nested: { headers: { Authorization: 'secret-B' } } };
  backup.tasks[0].publications = [{ id: 'x', state: 'verified', password: 'secret-C' }];
  backup.settings.maxSources = 1.5; backup.settings.maxOutputTokens = 1000.5;
  workspace.importBackup(backup);
  assert.equal(JSON.stringify(workspace.exportBackup()).includes('secret-'), false);
  assert.equal(workspace.task(task.id).research.content, 'Source text is preserved');
  assert.equal(workspace.snapshot().settings.maxSources, 1);
  assert.equal(workspace.snapshot().settings.maxOutputTokens, 1000);
});
