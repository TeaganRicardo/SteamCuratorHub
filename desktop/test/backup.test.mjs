import test from 'node:test';
import assert from 'node:assert/strict';
import { Workspace } from '../workspace.mjs';
import { BackupRestore } from '../backup.mjs';
const owner='76561198000000000';
function setup(){
  let writes=0;
  const workspace=new Workspace({load:()=>null,save(){writes++;}});workspace.bindAccount(owner);
  const task=workspace.createTask({appId:1001,title:'Author game',groupId:'45134494'});
  workspace.saveDraft({taskId:task.id,language:'schinese',kind:'long',text:'Current author text',expectedRevision:0});
  workspace.updateSettings({aiModel:'current-model'});
  const backup=workspace.exportBackup();backup.tasks[0].drafts.schinese.long.text='Backup author text';backup.settings.aiModel='old-model';backup.password='do not expose';
  let busy=false;
  const controller={acquireMaintenance(){if(busy)throw new Error('stop_operations_before_import');return ()=>{};},assertIdle(){if(busy)throw new Error('stop_operations_before_import');},snapshot(){return {workspace:workspace.snapshot()};},changed(){}};
  const publishing={async logout(){}};
  return {workspace,task,backup,controller,publishing,service:new BackupRestore({workspace,controller,publishing}),writes:()=>writes,setBusy:v=>busy=v};
}
test('backup preview does not replace author text and restores only after an explicit idle apply',async()=>{
  const s=setup();const before=s.writes();s.setBusy(true);
  const preview=s.service.preview(s.backup);
  assert.equal(s.writes(),before);assert.equal(s.workspace.task(s.task.id).drafts.schinese.long.text,'Current author text');
  assert.equal(preview.workspace.tasks[0].drafts.schinese.long.text,'Backup author text');
  assert.equal(preview.workspace.password,undefined);assert.equal(preview.workspace.settings.aiModel,'current-model');
  await assert.rejects(s.service.restore(preview.previewId),/stop_operations_before_import/);
  s.setBusy(false);await s.service.restore(preview.previewId);
  assert.equal(s.workspace.task(s.task.id).drafts.schinese.long.text,'Backup author text');
  assert.equal(s.workspace.state.settings.aiModel,'current-model');
  await assert.rejects(s.service.restore(preview.previewId),/backup_preview_expired/);
});
test('a late author edit during publication-session cleanup prevents replacement',async()=>{
  const s=setup();let finish;const cleanup=new Promise(resolve=>finish=resolve);
  s.publishing.logout=()=>cleanup;
  const preview=s.service.preview(s.backup);
  const pending=s.service.restore(preview.previewId);
  s.workspace.saveDraft({taskId:s.task.id,language:'schinese',kind:'long',text:'Late author edit',expectedRevision:1});
  finish();await assert.rejects(pending,/backup_preview_changed/);
  assert.equal(s.workspace.task(s.task.id).drafts.schinese.long.text,'Late author edit');
});
test('preview and restore keep current account and do not expose another owner\u2019s drafts',async()=>{
  const s=setup();const other='76561198000000001';s.backup.tasks[0].ownerSteamId=other;s.backup.activeSteamId=other;
  const preview=s.service.preview(s.backup);
  assert.equal(preview.workspace.activeSteamId,owner);assert.equal(preview.workspace.tasks.length,0);
  assert.deepEqual(preview.scope,{currentOtherTasks:0,importedOtherTasks:1,currentOtherRuns:0,importedOtherRuns:0});
  assert.equal(JSON.stringify(preview).includes(other),false);
  await s.service.restore(preview.previewId);
  assert.equal(s.workspace.snapshot().activeSteamId,owner);assert.equal(s.workspace.snapshot().tasks.length,0);
  assert.equal(s.workspace.exportBackup().tasks[0].ownerSteamId,other);
});
test('selecting a newer backup while restoration awaits cleanup revokes the earlier preview',async()=>{
  const s=setup();let finish;s.publishing.logout=()=>new Promise(resolve=>finish=resolve);
  const first=s.service.preview(s.backup),pending=s.service.restore(first.previewId);
  const newer=structuredClone(s.backup);newer.tasks[0].drafts.schinese.long.text='Newer backup';
  const second=s.service.preview(newer);finish();
  await assert.rejects(pending,/backup_preview_expired/);
  assert.equal(s.workspace.task(s.task.id).drafts.schinese.long.text,'Current author text');
  s.publishing.logout=async()=>{};await s.service.restore(second.previewId);
  assert.equal(s.workspace.task(s.task.id).drafts.schinese.long.text,'Newer backup');
});
