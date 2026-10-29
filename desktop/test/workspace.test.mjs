import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { Workspace, validateFullReviewUrl, normalizeWorkspace } from '../workspace.mjs';
import { JsonRepository } from '../repository.mjs';

const account = '76561198000000000';
const ownReview = `https://steamcommunity.com/profiles/${account}/recommended/1001/`;

test('an explicitly owned existing draft buffer can finish CAS saving after active account changes', () => {
  const workspace = new Workspace({ load: () => null, save() {} }); workspace.bindAccount(account);
  const old = workspace.createTask({ appId: 1001, title: 'Original owner task', groupId: '45134494' });
  const other = '76561198000000001'; workspace.bindAccount(other);
  const current = workspace.createTask({ appId: 1002, title: 'Current owner task', groupId: '45525570' });
  assert.throws(() => workspace.saveDraft({ taskId: old.id, language: 'english', kind: 'long', text: 'Implicit old task write', expectedRevision: 0 }), /task_not_found/);
  assert.throws(() => workspace.saveDraft({ taskId: old.id, ownerSteamId: other, language: 'english', kind: 'long', text: 'Wrong claimed owner', expectedRevision: 0 }), /task_not_found|draft_owner_mismatch/);
  const saved = workspace.saveDraft({ taskId: old.id, ownerSteamId: account, language: 'english', kind: 'long', text: 'Original buffer preserved', expectedRevision: 0 });
  assert.equal(saved.ownerSteamId, account); assert.equal(saved.drafts.english.long.text, 'Original buffer preserved');
  assert.equal(saved.drafts.schinese.long.text, '');
  assert.throws(() => workspace.saveDraft({ taskId: old.id, ownerSteamId: account, language: 'english', kind: 'long', text: 'Stale revision', expectedRevision: 0 }), /draft_changed/);
  assert.equal(workspace.snapshot().activeSteamId, other);
  assert.deepEqual(workspace.snapshot().tasks.map(task => task.id), [current.id]);
  assert.equal(workspace.task(current.id).drafts.english.long.text, '');
});

test('full review URL requires the exact numeric owner and AppID, while blank clears and a missing trailing slash canonicalizes', () => {
  assert.equal(validateFullReviewUrl('', account, 1001), '');
  assert.equal(validateFullReviewUrl(ownReview.slice(0, -1), account, 1001), ownReview);
  for (const url of [ownReview.replace(account, '76561198000000001'), ownReview.replace('/1001/', '/999/'),
    'https://example.com/review', `${ownReview}?tracking=1`, `${ownReview}#fragment`,
    ownReview.replace('https:', 'http:'), ownReview.replace('steamcommunity.com', 'steamcommunity.com.attacker.com')]) {
    assert.throws(() => validateFullReviewUrl(url, account, 1001), /long_review_result_url_mismatch/);
  }
  assert.throws(() => validateFullReviewUrl('https://steamcommunity.com/id/vanity/recommended/1001/', account, 1001), /requires_numeric_profile/);
});

test('wrong full review URLs cannot be saved or frozen and do not change prior draft options', () => {
  const workspace = new Workspace({ load: () => null, save() {} }); workspace.bindAccount(account);
  const task = workspace.createTask({ appId: 1001, title: 'Synthetic', groupId: '45134494' });
  workspace.saveDraft({ taskId: task.id, language: 'schinese', kind: 'short', text: 'Short body', expectedRevision: 0 });
  workspace.saveDraftOptions({ taskId: task.id, language: 'schinese', fullReviewUrl: ownReview, recommendation: 'recommended' });
  const before = workspace.task(task.id).drafts.schinese;
  for (const fullReviewUrl of [ownReview.replace(account, '76561198000000001'), ownReview.replace('/1001/', '/999/'), 'https://example.com/review']) {
    assert.throws(() => workspace.saveDraftOptions({ taskId: task.id, language: 'schinese', fullReviewUrl, recommendation: 'not_recommended' }), /long_review_result_url_mismatch/);
    assert.deepEqual(workspace.task(task.id).drafts.schinese, before);
  }
  assert.equal(workspace.preparePublication(task.id, 'schinese', 'short').fullReviewUrl, ownReview);
  workspace.saveDraftOptions({ taskId: task.id, language: 'schinese', fullReviewUrl: '', recommendation: 'recommended' });
  assert.equal(workspace.content(task.id, 'schinese', 'short').fullReviewUrl, '');
});

test('legacy wrong URLs survive local reload and backup import with a repair flag but cannot prepare a short review', () => {
  const source = new Workspace({ load: () => null, save() {} }); source.bindAccount(account);
  const task = source.createTask({ appId: 1001, title: 'Synthetic', groupId: '45134494' });
  source.saveDraft({ taskId: task.id, language: 'schinese', kind: 'short', text: 'Existing short draft', expectedRevision: 0 });
  source.saveDraft({ taskId: task.id, language: 'schinese', kind: 'long', text: 'Unrelated long draft remains usable', expectedRevision: 0 });
  for (const fullReviewUrl of [ownReview.replace(account, '76561198000000001'), ownReview.replace('/1001/', '/999/'), 'https://example.com/review', 'https://steamcommunity.com/id/vanity/recommended/1001/']) {
    const saved = source.exportBackup(); saved.tasks[0].drafts.schinese.fullReviewUrl = fullReviewUrl;
    for (const imported of [false, true]) {
      const workspace = imported ? new Workspace({ load: () => null, save() {} }) : new Workspace({ load: () => saved, save() {} });
      if (imported) workspace.importBackup(saved);
      const draft = workspace.task(task.id).drafts.schinese;
      assert.equal(draft.fullReviewUrl, fullReviewUrl); assert.equal(draft.fullReviewUrlNeedsRepair, true);
      assert.equal(draft.short.text, 'Existing short draft');
      assert.throws(() => workspace.preparePublication(task.id, 'schinese', 'short'), /url_mismatch|requires_numeric_profile/);
      assert.match(workspace.content(task.id, 'schinese', 'long').text, /Unrelated long draft/);
      workspace.saveDraftOptions({ taskId: task.id, language: 'schinese', fullReviewUrl: ownReview, recommendation: 'recommended' });
      assert.equal(workspace.task(task.id).drafts.schinese.fullReviewUrlNeedsRepair, false);
      assert.equal(workspace.preparePublication(task.id, 'schinese', 'short').fullReviewUrl, ownReview);
    }
  }
});
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

test('manual long publication confirmation requires the frozen account and game result URL', () => {
  const workspace = new Workspace({ load: () => null, save() {} }); workspace.bindAccount(account);
  const task = workspace.createTask({ appId: 1001, title: 'Example', groupId: '45134494' });
  workspace.saveDraft({ taskId: task.id, language: 'english', kind: 'long', text: 'Review', expectedRevision: 0 });
  const attempt = workspace.preparePublication(task.id, 'english', 'long');
  for (const resultUrl of [ownReview.replace(account, '76561198000000001'), ownReview.replace('1001', '1002'), 'https://store.steampowered.com/app/1001/']) {
    assert.throws(() => workspace.recordPublication({ taskId: task.id, attemptId: attempt.id, state: 'user_confirmed', resultUrl }), /long_review_result_url_mismatch/);
  }
  assert.equal(workspace.task(task.id).publications[0].state, 'prepared');
  const saved = workspace.recordPublication({ taskId: task.id, attemptId: attempt.id, state: 'user_confirmed', resultUrl: ownReview.slice(0, -1) });
  assert.equal(saved.resultUrl, ownReview);
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

test('imported publication success keeps its downgrade through preview and restore and gates only its own kind and language', () => {
  const workspace = new Workspace({ load: () => null, save() {} }); workspace.bindAccount(account);
  const task = workspace.createTask({ appId: 1001, title: 'Example', groupId: '45134494' });
  workspace.saveDraft({ taskId: task.id, language: 'english', kind: 'long', text: 'Review', expectedRevision: 0 });
  workspace.saveDraft({ taskId: task.id, language: 'english', kind: 'short', text: 'Short review', expectedRevision: 0 });
  workspace.saveDraft({ taskId: task.id, language: 'schinese', kind: 'long', text: '中文长评', expectedRevision: 0 });
  const attempt = workspace.preparePublication(task.id, 'english', 'long');
  const resultUrl = `https://steamcommunity.com/profiles/${account}/recommended/1001/`;
  workspace.recordPublication({ taskId: task.id, attemptId: attempt.id, state: 'user_confirmed', resultUrl });
  const preview = normalizeWorkspace(workspace.exportBackup());
  assert.equal(preview.tasks[0].publications[0].state, 'imported_unverified');
  workspace.importBackup(preview);
  assert.equal(workspace.task(task.id).publications[0].state, 'imported_unverified');
  assert.throws(() => workspace.preparePublication(task.id, 'english', 'long'), /needs_verification/);
  assert.equal(workspace.preparePublication(task.id, 'english', 'short').state, 'prepared');
  assert.equal(workspace.preparePublication(task.id, 'schinese', 'long').state, 'prepared');
  workspace.recordPublication({ taskId: task.id, attemptId: attempt.id, state: 'user_confirmed', resultUrl });
  assert.equal(workspace.preparePublication(task.id, 'english', 'long').state, 'prepared');
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

test('imported native success is explicitly unverified while local evidence and unknown retry gates survive reload',()=>{
  const repository={load:()=>null,save(){}};const workspace=new Workspace(repository);workspace.bindAccount(account);
  const task=workspace.createTask({appId:1001,title:'Synthetic',groupId:'45134494'});
  workspace.recordOperation(task.id,account,{id:'local',state:'verified',result:{status:'verified',items:[{id:'ACH',status:'verified'}],verification:{steamVerified:true,scope:'client-refresh'}}});
  const local=new Workspace({load:()=>workspace.exportBackup(),save(){}});
  assert.equal(local.task(task.id).operations[0].state,'verified');
  workspace.recordOperation(task.id,account,{id:'pending',state:'applying'});
  workspace.importBackup(workspace.exportBackup());
  const [success,pending]=workspace.task(task.id).operations;
  assert.equal(success.state,'imported_unverified');assert.equal(success.imported,true);
  assert.equal(success.evidenceSource,'imported_backup');assert.equal(success.result.status,'imported_unverified');
  assert.equal(success.result.verification.steamVerified,false);assert.equal(success.result.items[0].status,'imported_unverified');
  assert.equal(pending.state,'outcome_unknown');
  const restored=new Workspace({load:()=>workspace.exportBackup(),save(){}});
  assert.equal(restored.task(task.id).operations[0].state,'imported_unverified');
});
