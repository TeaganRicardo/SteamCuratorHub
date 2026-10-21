// Throwaway #13 viewer. Serves only the synthetic prototype on loopback.
import { createServer } from 'node:http';
import { readFileSync } from 'node:fs';

const round = process.argv.includes('--round=4') ? 4 : process.argv.includes('--round=3') ? 3 : process.argv.includes('--round=2') ? 2 : 1;
const fragment = readFileSync(new URL(round === 4 ? './ux-workbench-flows.fragment.html' : round === 3 ? './ux-workbench-live.fragment.html' : round === 2 ? './ux-workbench-round2.fragment.html' : './ux-prototype.fragment.html', import.meta.url), 'utf8');
const variants = round === 4 ? [['flows', '连接、查找与恢复']] : round === 3 ? [['live', '正文优先 · 输入即预览']] : round === 2 ? [['split', '资料与正文并排'], ['focus', '正文优先，资料按需展开']]
  : [['workspace', '游戏工作台'], ['board', '评测任务看板'], ['table', '多游戏操作表']];
const port = 4316 + round;
const sceneControls = round === 4 ? '<label>演示连接 <select id="workflow-connection"><option value="connected">已连接</option><option value="logged_out">首次登录</option><option value="restoring">打开应用恢复会话</option><option value="reconnecting">断线重连</option><option value="expired">会话失效</option><option value="session_only">连接成功但只在本次保存</option></select></label><label>成就写入 <select id="workflow-write"><option value="complete">完成</option><option value="unknown">结果未知</option></select></label><label>生成 <select id="workflow-ai"><option value="complete">完成</option><option value="failed">失败并保留正文</option></select></label>' : round !== 1 ? '' : '<label>连接 <select id="connection"><option value="connected">已连接</option><option value="guard">等待 Guard</option><option value="reconnecting">断线重连</option><option value="error">会话失效</option></select></label><label>写入 <select id="write-result"><option value="complete">演示完成</option><option value="unknown">结果未知</option></select></label>';
const wrapper = `<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Steam Curator Hub · 第 ${round} 轮交互草图</title><style>
html{color-scheme:light dark}body{font-family:-apple-system,BlinkMacSystemFont,sans-serif;margin:0;padding:24px 24px ${round === 3 ? 24 : 100}px;background:light-dark(#eeeeef,#151618);color:light-dark(#242529,#e7e7e9)}.prototype-bar{position:fixed;bottom:16px;left:50%;transform:translateX(-50%);display:flex;align-items:center;justify-content:center;gap:10px;flex-wrap:wrap;max-width:calc(100% - 24px);width:max-content;border:1px solid light-dark(#bfc0c3,#606166);border-radius:8px;background:light-dark(#fff,#27282b);padding:10px 14px;z-index:5;font-size:13px}button,select{font:inherit;color:inherit;background:light-dark(#fff,#27282b);border:1px solid light-dark(#c9cacc,#55565b);border-radius:4px;padding:6px 9px}.prototype-bar label{display:flex;gap:5px;align-items:center}[hidden]{display:none!important}#sch-ux-prototype,#sch-writing-round2,#sch-workbench-live,#sch-workbench-flows{max-width:1200px;margin:auto}@media(max-width:600px){body{padding:8px 8px ${round === 3 ? 8 : 160}px}}
</style>${fragment}<nav class="prototype-bar" aria-label="原型方案与演示场景" ${round === 3 ? 'hidden' : ''}><button id="previous" type="button" aria-label="上一个方案" ${round === 4 ? 'hidden' : ''}>←</button><label ${round === 4 ? 'hidden' : ''}>方案 <select id="variant">${variants.map(([value, label]) => `<option value="${value}">${label}</option>`).join('')}</select></label><button id="next" type="button" aria-label="下一个方案" ${round === 4 ? 'hidden' : ''}>→</button>${sceneControls}</nav><script>
const names=${JSON.stringify(variants.map(([value]) => value))},picker=document.getElementById('variant');
function show(value){picker.value=names.includes(value)?value:names[0];for(const panel of document.querySelectorAll('[data-layout]'))panel.parentElement.hidden=panel.dataset.layout!==picker.value;const url=new URL(location.href);url.searchParams.set('variant',picker.value);history.replaceState(null,'',url);}
show(new URL(location.href).searchParams.get('variant'));
picker.addEventListener('change',()=>show(picker.value));
function cycle(step){show(names[(names.indexOf(picker.value)+step+names.length)%names.length]);}
document.getElementById('previous').addEventListener('click',()=>cycle(-1));document.getElementById('next').addEventListener('click',()=>cycle(1));
document.addEventListener('keydown',event=>{if(event.defaultPrevented||event.target.closest('input,textarea,select,[contenteditable],[data-resize]'))return;if(event.key==='ArrowLeft'||event.key==='ArrowRight'){event.preventDefault();cycle(event.key==='ArrowLeft'?-1:1);}});
for(const id of ['connection','write-result'])document.getElementById(id)?.addEventListener('change',()=>document.getElementById('sch-ux-prototype').dispatchEvent(new CustomEvent('sch-prototype-scene',{detail:{connection:document.getElementById('connection').value,writeResult:document.getElementById('write-result').value}})));
for(const id of ['workflow-connection','workflow-write','workflow-ai'])document.getElementById(id)?.addEventListener('change',()=>document.getElementById('sch-workbench-flows').dispatchEvent(new CustomEvent('sch-workflow-scene',{detail:{connection:document.getElementById('workflow-connection').value,writeResult:document.getElementById('workflow-write').value,aiResult:document.getElementById('workflow-ai').value}})));
</script></html>`;
createServer((request, response) => {
  const url = new URL(request.url, 'http://127.0.0.1');
  if (url.pathname !== '/') { response.writeHead(404); response.end(); return; }
  response.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store',
    'Content-Security-Policy': "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; connect-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'" });
  response.end(wrapper);
}).listen(port, '127.0.0.1', () => console.log(`Synthetic UX prototype: http://127.0.0.1:${port}/?variant=${variants[0][0]}`));
