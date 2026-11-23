namespace ReflectoPatronus.Api.Updates;

/// <summary>
/// The "Updating ReflectoPatronus" page at /update-progress. One self-contained HTML file with no dependencies, so it still loads
/// while the API restarts: it polls /update-status, shows each step as it happens, rides out the restart ("reconnecting") and
/// sends you back to the console when the new version answers (or tells you plainly if it rolled back).
/// </summary>
public static class UpdatePage
{
    public const string Html = """
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Updating ReflectoPatronus</title><link rel="icon" href="/favicon.svg">
<style>
*{box-sizing:border-box}body{margin:0;min-height:100vh;display:grid;place-items:center;background:radial-gradient(circle at 50% 0,#17335f,#08111f 62%);
color:#eaf1fb;font:15px/1.45 system-ui,-apple-system,"Segoe UI",sans-serif}
.card{width:min(660px,94vw);background:#0e2038;border:1px solid #28508a;border-radius:16px;padding:28px 30px;box-shadow:0 20px 60px #0009}
.brand{display:flex;align-items:center;gap:12px;margin-bottom:18px}.brand img{height:34px;width:auto;filter:brightness(1.6)}
.brand span{color:#8fa6c6;font-size:12px;letter-spacing:.14em;text-transform:uppercase}
h1{margin:0 0 4px;font-size:22px}.sub{color:#9db3d0;margin:0 0 22px}
.bar{height:14px;background:#07152a;border-radius:99px;overflow:hidden;border:1px solid #28508a}
.fill{height:100%;width:0;background:linear-gradient(90deg,#2b62c9,#5b8fe8,#8ab0f2);transition:width .5s}
.fill.ind{width:35%!important;animation:ind 1.3s infinite ease-in-out}@keyframes ind{0%{margin-left:-35%}100%{margin-left:100%}}
.row{display:flex;justify-content:space-between;margin:8px 0 18px;color:#9db3d0;font-size:13px}
ol{list-style:none;padding:0;margin:0 0 18px}li{padding:6px 0 6px 32px;position:relative;color:#7189aa;transition:color .3s}
li small{display:block;font-weight:400;color:#7189aa;font-size:12px}
li:before{content:"";position:absolute;left:4px;top:10px;width:12px;height:12px;border-radius:50%;border:2px solid #3b5f93;transition:all .3s}
li.now{color:#fff;font-weight:600}li.now:before{border-color:#5b8fe8;background:#5b8fe8;box-shadow:0 0 0 4px #5b8fe833;animation:pulse 1.4s infinite}
@keyframes pulse{50%{box-shadow:0 0 0 7px #5b8fe811}}
li.ok{color:#bfe9d3}li.ok:before{border-color:#3ccf8e;background:#3ccf8e}li.ok:after{content:"";position:absolute;left:8px;top:13px;width:4px;height:7px;border:solid #07152a;border-width:0 2px 2px 0;transform:rotate(45deg)}
li.bad{color:#ffb3b3}li.bad:before{border-color:#e06c6c;background:#e06c6c}
.msg{padding:12px 14px;border-radius:9px;background:#0a1a2e;border-left:4px solid #5b8fe8;margin-bottom:12px}
.msg.ok{border-color:#3ccf8e}.msg.bad{border-color:#e06c6c;background:#2a1418}
details{font-size:12px;color:#9db3d0}pre{white-space:pre-wrap;max-height:220px;overflow:auto;background:#07111f;padding:10px;border-radius:8px}
a.btn{display:inline-block;margin-top:14px;background:#5b8fe8;color:#06142a;padding:9px 16px;border-radius:8px;font-weight:700;text-decoration:none}
@media(prefers-reduced-motion:reduce){*{animation:none!important;transition:none!important}}
</style></head><body><div class="card">
<div class="brand"><img src="/logo-mark.svg" alt=""><span>ReflectoPatronus</span></div>
<h1 id="t">Updating ReflectoPatronus</h1><p class="sub" id="s">Keep this page open. The program restarts for a few seconds; this page reconnects by itself.</p>
<div class="bar"><div class="fill" id="f"></div></div><div class="row"><span id="pct">0%</span><span id="el">0:00</span></div>
<ol id="st"></ol><div class="msg" id="m" role="status" aria-live="polite">Starting…</div>
<details><summary>Details</summary><pre id="log"></pre></details><div id="act"></div></div>
<script>
const t0=Date.now(),since=+new URLSearchParams(location.search).get('t')||0;
const STEPS=[['checking','Check the update','Make sure this server can take it'],['backup','Back up the database','Skipped if you turned it off'],
['downloading','Download the new version','From GitHub'],['building','Prepare the new version','Build the program and the console'],
['migrating','Update the database','Applies only the new changes'],['switching','Restart ReflectoPatronus','A few seconds'],
['verifying','Confirm the new version answers','Rolls back by itself if it does not']];
const ORDER=STEPS.map(x=>x[0]).concat(['done']);let offline=0,finished=false,last={};
const $=i=>document.getElementById(i);
const mb=b=>(b/1048576).toFixed(1)+' MB';
function draw(s,down){
  let state=down?'switching':(s.state||'checking');if(state==='staging'||state==='staged'||state==='idle')state='checking';
  const bad=['failed','rolled_back'].includes(state);
  let idx=ORDER.indexOf(state);if(bad)idx=Math.max(0,ORDER.indexOf(s.step||'checking'));
  $('st').innerHTML=STEPS.map((x,i)=>{
    let c=bad?(i<idx?'ok':i===idx?'bad':''):(state==='done'||i<idx?'ok':i===idx?'now':'');
    return `<li class="${c}">${x[1]}<small>${c==='now'&&s.message&&!down?s.message.replace(/</g,'&lt;'):x[2]}</small></li>`}).join('');
  let p=down?Math.max(90,+($('f').dataset.p||90)):+(s.percent||0);$('f').dataset.p=p;
  $('f').className='fill'+((state==='switching'||state==='verifying'||state==='building'||state==='migrating')&&!bad&&state!=='done'?' ind':'');
  $('f').style.width=(state==='done'?100:p)+'%';$('pct').textContent=Math.round(state==='done'?100:p)+'%';
  const m=$('m');m.className='msg'+(state==='done'?' ok':bad?' bad':'');
  m.textContent=down?'Restarting ReflectoPatronus… reconnecting.':(s.message||'Working…');
  if(s.log)$('log').textContent=s.log.map(x=>new Date(x[0]*1000).toLocaleTimeString()+'  '+x[1]).join('\n');
  if(s.version)$('t').textContent='Updating ReflectoPatronus to '+String(s.version).slice(0,7);
}
function end(ok,txt,title){finished=true;$('t').textContent=title||(ok?'ReflectoPatronus updated':'Update not installed');
  $('s').textContent=txt;$('f').className='fill';$('act').innerHTML='<a class="btn" href="/">Back to ReflectoPatronus</a>';
  if(ok)setTimeout(()=>location.href='/',6000)}
async function tick(){
  if(finished)return;const e=Math.floor((Date.now()-t0)/1000);$('el').textContent=Math.floor(e/60)+':'+String(e%60).padStart(2,'0');
  try{const r=await fetch('/update-status',{cache:'no-store'});if(!r.ok)throw 0;let st=await r.json();offline=0;
    if(since&&(+st.updated||0)<since-2)st={state:'checking',message:'Waiting for the updater to start…',percent:1,now:st.now};   // an older job's result, not ours
    last=st;draw(st,false);
    if(st.state==='done')return end(true,st.message||'Done.');
    if(st.state==='rolled_back')return end(false,st.message,'Rolled back to the previous version');
    if(st.state==='failed')return end(false,st.message);
    if(!st.state||st.state==='idle'||st.state==='staged')$('m').textContent='Waiting for the updater to start…';
  }catch(x){offline++;draw(last,true)}
  if(Date.now()-t0>25*60*1000)return end(false,'No answer after 25 minutes. On the server run: docker compose logs updater api   (or journalctl -u rp-update-apply -u reflectopatronus-api).');
  setTimeout(tick,offline?2000:1000)}
tick();
</script></body></html>
""";
}
