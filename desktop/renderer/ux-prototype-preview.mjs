// Throwaway #13 viewer. Serves only the synthetic prototype on loopback.
import { createServer } from 'node:http';
import { readFileSync } from 'node:fs';

const round = process.argv.includes('--round=2') ? 2 : 1;
const fragment = readFileSync(new URL(round === 2 ? './ux-workbench-round2.fragment.html' : './ux-prototype.fragment.html', import.meta.url), 'utf8');
const variants = round === 2 ? [['split', '资料与正文并排'], ['focus', '正文优先，资料按需展开']]
  : [['workspace', '游戏工作台'], ['board', '评测任务看板'], ['table', '多游戏操作表']];
const port = round === 2 ? 4318 : 4317;
const sceneControls = round === 2 ? '' : '<label>连接 <select id="connection"><option value="connected">已连接</option><option value="guard">等待 Guard</option><option value="reconnecting">断线重连</option><option value="error">会话失效</option></select></label><label>写入 <select id="write-result"><option value="complete">演示完成</option><option value="unknown">结果未知</option></select></label>';
const wrapper = `<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Steam Curator Hub · 第 ${round} 轮交互草图</title><style>
html{color-scheme:light dark}body{font-family:-apple-system,BlinkMacSystemFont,sans-serif;margin:0;padding:24px 24px 100px;background:light-dark(#eeeeef,#151618);color:light-dark(#242529,#e7e7e9)}.prototype-bar{position:fixed;bottom:16px;left:50%;transform:translateX(-50%);display:flex;align-items:center;justify-content:center;gap:10px;flex-wrap:wrap;max-width:calc(100% - 24px);width:max-content;border:1px solid light-dark(#bfc0c3,#606166);border-radius:8px;background:light-dark(#fff,#27282b);padding:10px 14px;z-index:5;font-size:13px}button,select{font:inherit;color:inherit;background:light-dark(#fff,#27282b);border:1px solid light-dark(#c9cacc,#55565b);border-radius:4px;padding:6px 9px}.prototype-bar label{display:flex;gap:5px;align-items:center}[hidden]{display:none!important}#sch-ux-prototype,#sch-writing-round2{max-width:1200px;margin:auto}@media(max-width:600px){body{padding:8px 8px 160px}}
</style>${fragment}<nav class="prototype-bar" aria-label="原型方案与演示场景"><button id="previous" type="button" aria-label="上一个方案">←</button><label>方案 <select id="variant">${variants.map(([value, label]) => `<option value="${value}">${label}</option>`).join('')}</select></label><button id="next" type="button" aria-label="下一个方案">→</button>${sceneControls}</nav><script>
const names=${JSON.stringify(variants.map(([value]) => value))},picker=document.getElementById('variant');
function show(value){picker.value=names.includes(value)?value:names[0];for(const panel of document.querySelectorAll('[data-layout]'))panel.parentElement.hidden=panel.dataset.layout!==picker.value;const url=new URL(location.href);url.searchParams.set('variant',picker.value);history.replaceState(null,'',url);}
show(new URL(location.href).searchParams.get('variant'));
picker.addEventListener('change',()=>show(picker.value));
function cycle(step){show(names[(names.indexOf(picker.value)+step+names.length)%names.length]);}
document.getElementById('previous').addEventListener('click',()=>cycle(-1));document.getElementById('next').addEventListener('click',()=>cycle(1));
document.addEventListener('keydown',event=>{if(event.target.closest('input,textarea,select,[contenteditable]'))return;if(event.key==='ArrowLeft'||event.key==='ArrowRight'){event.preventDefault();cycle(event.key==='ArrowLeft'?-1:1);}});
for(const id of ['connection','write-result'])document.getElementById(id)?.addEventListener('change',()=>document.getElementById('sch-ux-prototype').dispatchEvent(new CustomEvent('sch-prototype-scene',{detail:{connection:document.getElementById('connection').value,writeResult:document.getElementById('write-result').value}})));
</script></html>`;
createServer((request, response) => {
  const url = new URL(request.url, 'http://127.0.0.1');
  if (url.pathname !== '/') { response.writeHead(404); response.end(); return; }
  response.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store',
    'Content-Security-Policy': "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; connect-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'" });
  response.end(wrapper);
}).listen(port, '127.0.0.1', () => console.log(`Synthetic UX prototype: http://127.0.0.1:${port}/?variant=${variants[0][0]}`));
