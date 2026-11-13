namespace CampRadar.Api.Cams;

/// <summary>
/// /admin/cams/page — the camera directory on a map, coloured by the latest verdict; click a camera to classify it now and see the
/// frame beside the model's answer; recent verdicts with thumbnails. Uses the same admin key as /admin (sessionStorage).
/// </summary>
public static class CamsPage
{
    public const string Html = """
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>VantageWX · cameras</title>
<link rel="stylesheet" href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css">
<style>
:root{--bg:#0f1317;--card:#171d23;--fg:#e6ecf1;--muted:#8fa0ae;--line:#263038;--ok:#66bb6a;--warn:#ffb74d;--blue:#4fc3f7}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:14px system-ui,sans-serif}
header{display:flex;gap:14px;align-items:center;padding:10px 16px;border-bottom:1px solid var(--line)}header h1{font-size:16px;margin:0}header a{color:var(--blue)}
.sub{color:var(--muted);font-size:12px}
#wrap{display:grid;grid-template-columns:1fr 420px;height:calc(100vh - 46px)}@media(max-width:900px){#wrap{grid-template-columns:1fr;height:auto}#map{height:60vh}}
#map{height:100%;min-height:400px}
#side{overflow:auto;border-left:1px solid var(--line);padding:12px}
.card{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:10px 12px;margin-bottom:10px}
.card h2{font-size:12px;letter-spacing:.08em;text-transform:uppercase;color:var(--muted);margin:0 0 6px}
button{font:inherit;background:#223040;color:var(--fg);border:1px solid #34506a;border-radius:6px;padding:6px 10px;cursor:pointer}button:hover{background:#2a3c50}
input{font:inherit;background:#0f1317;color:var(--fg);border:1px solid var(--line);border-radius:6px;padding:6px 8px;width:100%}
img.frame{width:100%;border-radius:6px;display:block;background:#000}
.kv{display:grid;grid-template-columns:auto 1fr;gap:2px 10px;font-size:13px}.kv b{color:var(--muted);font-weight:500}
.chip{display:inline-block;padding:1px 7px;border-radius:999px;font-size:12px;background:#223040;margin:1px 2px 1px 0}
.rec{display:grid;grid-template-columns:96px 1fr;gap:8px;align-items:start;padding:8px 0;border-top:1px solid var(--line);cursor:pointer}.rec img{width:96px;height:64px;object-fit:cover;border-radius:4px;background:#000}
.rec small{color:var(--muted)}
#login{max-width:360px;margin:60px auto;padding:20px}
.legend span{display:inline-flex;align-items:center;gap:5px;margin-right:10px;font-size:12px;color:var(--muted)}.legend i{width:10px;height:10px;border-radius:50%;display:inline-block}
</style></head><body>
<header><h1>VantageWX · cameras</h1><span class="sub" id="status">–</span><span style="flex:1"></span><a href="/admin">← admin</a></header>
<div id="login" hidden><div class="card"><h2>Admin key</h2><input id="key" type="password" placeholder="X-Admin-Key"><p><button id="go">Open</button> <span id="err" class="sub"></span></p></div></div>
<div id="wrap" hidden>
  <div id="map"></div>
  <div id="side">
    <div class="card"><h2>Directory</h2><div id="dir" class="sub">–</div>
      <p style="margin:8px 0 0"><button id="loadHere">Load cameras in view</button> <button id="refresh" title="Re-scrape the public registries (takes a minute)">Refresh directory</button></p>
      <p class="legend" style="margin:8px 0 0"><span><i style="background:#9fb3c2"></i>no verdict yet</span><span><i style="background:#66bb6a"></i>dry</span><span><i style="background:#4fc3f7"></i>precip</span><span><i style="background:#26a69a"></i>wet ground</span><span><i style="background:#bdbdbd"></i>fog</span><span><i style="background:#ef5350"></i>bad frame</span></p>
    </div>
    <div class="card" id="pick" hidden><h2 id="pickName">Camera</h2><div id="pickSub" class="sub"></div>
      <p style="margin:8px 0"><button id="classify">Classify now</button> <span id="classifyOut" class="sub"></span></p>
      <img class="frame" id="pickImg" alt="" hidden>
      <div class="kv" id="pickKv" style="margin-top:8px"></div>
      <details style="margin-top:6px"><summary class="sub">model answer</summary><pre id="pickJson" style="white-space:pre-wrap;font-size:12px"></pre></details>
    </div>
    <div class="card"><h2>Worker</h2><div id="worker" class="sub">–</div></div>
    <div class="card"><h2>Recent verdicts (24 h)</h2><div id="recent"></div></div>
  </div>
</div>
<script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js"></script>
<script>
const $=s=>document.querySelector(s), esc=s=>String(s??'').replace(/[&<>"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
let key=sessionStorage.getItem('adminKey')||'', map, layer, cams={}, latest={}, current=null;
const H={get:()=>({'X-Admin-Key':key})};
const ago=ms=>{const m=Math.round((Date.now()-ms)/60000);return m<1?'just now':m<60?m+' min ago':m<1440?Math.round(m/60)+' h ago':Math.round(m/1440)+' d ago'};
const colour=o=>!o?'#9fb3c2':o.lens!=='ok'?'#ef5350':(o.precip==='rain'||o.precip==='snow'||o.precip==='mixed')?'#4fc3f7':(o.vis==='fog'||o.vis==='dense')?'#bdbdbd':(o.ground==='wet'||o.ground==='puddled')?'#26a69a':'#66bb6a';
const frameUrl=name=>'/admin/cams/frame?name='+encodeURIComponent(name)+'&key='+encodeURIComponent(key);
function verdictLine(o){return o?`${o.precip}${o.intensity&&o.intensity!=='none'?' ('+o.intensity+')':''} · ground ${o.ground} · vis ${o.vis} · sky ${o.sky} · lens ${o.lens}${o.wind&&o.wind!=='n/a'?' · wind '+o.wind:''} · conf ${(+o.conf).toFixed(2)}${o.radarDbz!=null?' · radar '+Math.round(o.radarDbz)+' dBZ':''}`:'no verdict yet'}
async function overview(){
  const r=await fetch('/admin/cams',{headers:H.get()});
  if(r.status===401){sessionStorage.removeItem('adminKey');$('#login').hidden=false;$('#wrap').hidden=true;$('#err').textContent=key?'Wrong key.':'';return false}
  const o=await r.json(); sessionStorage.setItem('adminKey',key); $('#login').hidden=true;$('#wrap').hidden=false;
  $('#dir').innerHTML=(o.directory.stats||[]).map(s=>`<b>${esc(s.source)}</b> ${s.cams} cams (${s.retired} retired, ${s.everGood} ever gave a frame, quality ${s.quality})`).join('<br>')+(o.directory.lastRun?'<br>last refresh: '+esc(o.directory.lastRun)+' · '+(o.directory.lastRunAt&&!o.directory.lastRunAt.startsWith('0001')?ago(Date.parse(o.directory.lastRunAt)):'never')+'':'');
  $('#worker').innerHTML='Last cycle: '+esc(o.worker.lastCycle)+(o.worker.lastCycleAt&&!o.worker.lastCycleAt.startsWith('0001')?' · '+ago(Date.parse(o.worker.lastCycleAt)):'')+'<br>Targets: '+(o.worker.targets.length?o.worker.targets.map(t=>esc(t.key)+' ('+esc(t.level)+')').join(', '):'none');
  $('#status').textContent='as of '+new Date().toLocaleTimeString();
  if(!map){map=L.map('map').setView([40.5,-84],6);L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png',{attribution:'© OpenStreetMap'}).addTo(map);layer=L.layerGroup().addTo(map);
    const t=o.worker.targets[0]; if(t) map.setView([t.lat,t.lon],9);}
  for(const t of o.worker.targets) L.circleMarker([t.lat,t.lon],{radius:9,color:'#ffb74d',fillOpacity:.2,weight:2}).bindTooltip('target: '+esc(t.key)+' · '+esc(t.level)).addTo(layer);
  return true;
}
async function loadRecent(){
  const r=await fetch('/admin/cams/obs?hours=24',{headers:H.get()}); if(!r.ok) return; const l=await r.json();
  latest={}; for(const o of l) if(!o.stale&&!latest[o.camId]) latest[o.camId]=o;
  $('#recent').innerHTML=l.filter(o=>!o.stale&&o.lens!=='error').slice(0,40).map(o=>`<div class="rec" data-cam="${esc(o.camId)}" data-t="${o.tMs}">${o.frame?`<img src="${frameUrl(o.frame)}" loading="lazy" alt="">`:'<div></div>'}<div><div>${esc(cams[o.camId]?cams[o.camId].name:o.camId)}</div><small>${ago(o.tMs)} · ${esc(o.day)} · ${esc(o.target)}</small><div style="font-size:12px">${esc(verdictLine(o))}</div></div></div>`).join('')||'<span class="sub">none yet</span>';
  paint();
}
function paint(){ for(const [id,m] of Object.entries(markers)){const o=latest[id];const c=colour(o);m.setStyle({color:c,fillColor:c});m.setTooltipContent(esc(cams[id].name)+'<br>'+esc(verdictLine(o)));} }
const markers={};
async function loadHere(){
  const c=map.getCenter(), b=map.getBounds(), mi=Math.min(150,Math.max(10,b.getNorthEast().distanceTo(b.getSouthWest())/1609/2));
  const r=await fetch(`/admin/cams/list?lat=${c.lat}&lon=${c.lng}&mi=${mi}`,{headers:H.get()}); if(!r.ok) return; const l=await r.json();
  for(const cam of l){cams[cam.id]=cam; if(markers[cam.id]) continue; const m=L.circleMarker([cam.lat,cam.lon],{radius:6,weight:1,fillOpacity:.8}).bindTooltip('').addTo(layer); m.on('click',()=>pick(cam.id)); markers[cam.id]=m;}
  paint(); $('#status').textContent=l.length+' cameras within '+Math.round(mi)+' mi of the map centre';
}
function pick(id,obs){ current=id; const cam=cams[id]; $('#pick').hidden=false; $('#pickName').textContent=cam?cam.name:id; $('#pickSub').innerHTML=cam?`${esc(cam.id)} · ${cam.lat.toFixed(3)}, ${cam.lon.toFixed(3)}${cam.headingDeg!=null?' · faces '+Math.round(cam.headingDeg)+'°':''} · quality ${(+cam.quality).toFixed(2)} (${cam.good}/${cam.attempts}) · <a href="${esc(cam.url)}" target="_blank" rel="noopener" style="color:var(--blue)">source</a>`:'';
  showObs(obs||latest[id]); $('#classifyOut').textContent=''; map.panTo([cam.lat,cam.lon]); }
function showObs(o){ if(!o){$('#pickImg').hidden=true;$('#pickKv').innerHTML='<span class="sub">no verdict yet — click Classify now</span>';$('#pickJson').textContent='';return}
  if(o.frame){$('#pickImg').src=frameUrl(o.frame);$('#pickImg').hidden=false}else $('#pickImg').hidden=true;
  $('#pickKv').innerHTML=[['when',ago(o.tMs)+' ('+new Date(o.tMs).toLocaleString()+')'],['sun',o.day],['precip',o.precip+(o.intensity&&o.intensity!=='none'?' · '+o.intensity:'')],['ground',o.ground],['visibility',o.vis],['sky',o.sky],['lens',o.lens],['wind',o.wind],['confidence',(+o.conf).toFixed(2)],['radar over camera',o.radarDbz!=null?Math.round(o.radarDbz)+' dBZ':'—'],['looked at for',o.target]].map(([k,v])=>`<b>${k}</b><span>${esc(v)}</span>`).join('');
  let j=o.json; try{j=JSON.stringify(JSON.parse(o.json),null,1)}catch{} $('#pickJson').textContent=j; }
$('#classify').onclick=async()=>{ if(!current) return; $('#classifyOut').textContent='fetching + classifying…'; const r=await fetch('/admin/cams/classify?id='+encodeURIComponent(current),{method:'POST',headers:H.get()}); const o=await r.json();
  if(o.error){$('#classifyOut').textContent=o.error;return} $('#classifyOut').textContent=(o.bytes/1024).toFixed(0)+' KB'+(o.identicalToLast?' · same frame as last time':'');
  const v=o.verdict||{}; const obs={tMs:o.t,day:o.day,precip:v.precip,intensity:v.intensity,ground:v.ground,vis:v.vis,sky:v.sky,lens:v.lens,wind:v.wind,conf:v.conf,radarDbz:o.radarDbz,target:'admin',frame:o.frame,json:JSON.stringify(v),camId:current};
  latest[current]=obs; showObs(obs); paint(); loadRecent(); };
$('#loadHere').onclick=loadHere;
$('#refresh').onclick=async()=>{$('#dir').textContent='refreshing… (a minute)';const r=await fetch('/admin/cams/refresh',{method:'POST',headers:H.get()});const o=await r.json();$('#dir').textContent=o.result;overview();};
$('#recent').addEventListener('click',e=>{const row=e.target.closest('.rec'); if(!row) return; const id=row.dataset.cam; if(!cams[id]){ fetch('/admin/cams/list?id='+encodeURIComponent(id),{headers:H.get()}).then(r=>r.json()).then(l=>{for(const cam of l){cams[cam.id]=cam; if(!markers[cam.id]){const m=L.circleMarker([cam.lat,cam.lon],{radius:6,weight:1,fillOpacity:.8}).bindTooltip('').addTo(layer); m.on('click',()=>pick(cam.id)); markers[cam.id]=m;}} pick(id);}); } else pick(id); });
$('#go').onclick=async()=>{key=$('#key').value.trim(); if(await overview()){await loadRecent(); loadHere();}};
(async()=>{ if(key){ if(await overview()){await loadRecent(); loadHere();} } else $('#login').hidden=false; })();
setInterval(()=>{ if(!$('#wrap').hidden){overview();loadRecent();} },120000);
</script></body></html>
""";
}
