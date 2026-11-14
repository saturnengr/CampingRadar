namespace CampRadar.Api.Admin;

/// <summary>
/// /admin/record/page — the national radar loop on a map; click a spot to start a recording there (name suggested from the nearest
/// town), see running recordings with their capture radius, stop them, download finished ones. Same admin key as /admin.
/// </summary>
public static class RecordPage
{
    public const string Html = """
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>VantageWX · recordings</title>
<link rel="stylesheet" href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css">
<style>
:root{--bg:#0f1317;--card:#171d23;--fg:#e6ecf1;--muted:#8fa0ae;--line:#263038;--blue:#4fc3f7;--warn:#ffb74d}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:14px system-ui,sans-serif}
header{display:flex;gap:14px;align-items:center;padding:10px 16px;border-bottom:1px solid var(--line)}header h1{font-size:16px;margin:0}header a{color:var(--blue)}
.sub{color:var(--muted);font-size:12px}
#wrap{display:grid;grid-template-columns:1fr 380px;height:calc(100vh - 46px)}@media(max-width:900px){#wrap{grid-template-columns:1fr;height:auto}#map{height:60vh}}
#map{height:100%;min-height:400px;background:#0b0f13}
#side{overflow:auto;border-left:1px solid var(--line);padding:12px}
.card{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:10px 12px;margin-bottom:10px}
.card h2{font-size:12px;letter-spacing:.08em;text-transform:uppercase;color:var(--muted);margin:0 0 6px}
button{font:inherit;background:#223040;color:var(--fg);border:1px solid #34506a;border-radius:6px;padding:6px 10px;cursor:pointer}button:hover{background:#2a3c50}button.primary{background:#1976d2;border-color:#1976d2}button.small{padding:3px 8px;font-size:12px}
input,select{font:inherit;background:#0f1317;color:var(--fg);border:1px solid var(--line);border-radius:6px;padding:6px 8px;width:100%}
label{display:block;font-size:12px;color:var(--muted);margin:8px 0 3px}
.row{display:grid;grid-template-columns:1fr 1fr;gap:8px}
.rec{padding:8px 0;border-top:1px solid var(--line)}.rec b{cursor:pointer}.rec .sub{display:block;margin-top:2px}
#login{max-width:360px;margin:60px auto;padding:20px}
.loopbar{position:absolute;left:12px;bottom:12px;z-index:800;background:rgba(15,19,23,.85);border:1px solid var(--line);border-radius:8px;padding:6px 10px;display:flex;gap:8px;align-items:center;font-size:12px}
.loopbar input[type=range]{width:160px;padding:0}
.leaflet-container{font:inherit}
</style></head><body>
<header><h1>VantageWX · recordings</h1><span class="sub" id="status">–</span><span style="flex:1"></span><a href="/admin/cams/page">cameras</a><a href="/admin">← admin</a></header>
<div id="login" hidden><div class="card"><h2>Admin key</h2><input id="key" type="password" placeholder="X-Admin-Key"><p><button id="go">Open</button> <span id="err" class="sub"></span></p></div></div>
<div id="wrap" hidden>
  <div style="position:relative"><div id="map"></div>
    <div class="loopbar"><button class="small" id="play">⏸</button><input type="range" id="slider" min="0" max="10" value="10"><span id="frameLbl">now</span><label style="margin:0;display:inline-flex;gap:4px;align-items:center;font-size:12px"><input type="checkbox" id="showCams" style="width:auto"> cameras</label></div>
  </div>
  <div id="side">
    <div class="card"><h2>New recording</h2>
      <p class="sub" style="margin:0 0 4px">Click the map where the storm is heading. The recorder captures the radar within the radius every scan.</p>
      <label>Name</label><input id="name" placeholder="click the map…">
      <div class="row"><div><label>Latitude</label><input id="lat"></div><div><label>Longitude</label><input id="lon"></div></div>
      <div class="row"><div><label>Hours</label><select id="hours"><option>3</option><option>6</option><option selected>12</option><option>18</option><option>24</option></select></div><div><label>Radius (mi)</label><select id="radius"><option>100</option><option>150</option><option selected>200</option><option>300</option></select></div></div>
      <p style="margin:10px 0 0"><button class="primary" id="start">Start recording</button> <span id="startOut" class="sub"></span></p>
    </div>
    <div class="card"><h2>Recordings</h2><div id="list" class="sub">–</div></div>
  </div>
</div>
<script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js"></script>
<script>
const $=s=>document.querySelector(s), esc=s=>String(s??'').replace(/[&<>"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
let key=sessionStorage.getItem('adminKey')||'', map, pick, pickRing, recLayer, camLayer, frames=[], cur=10, playing=true, timer;
const H=()=>({'X-Admin-Key':key});
const MI=1609.344;
async function init(){
  const r=await fetch('/admin/record',{headers:H()});
  if(r.status===401){sessionStorage.removeItem('adminKey');$('#login').hidden=false;$('#wrap').hidden=true;$('#err').textContent=key?'Wrong key.':'';return false}
  sessionStorage.setItem('adminKey',key);$('#login').hidden=true;$('#wrap').hidden=false;
  if(!map){
    map=L.map('map',{zoomControl:true}).setView([38.5,-96],5);
    L.tileLayer('https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png',{attribution:'© OpenStreetMap © CARTO',subdomains:'abcd',maxZoom:12}).addTo(map);
    // IEM NEXRAD composite, 5-minute steps back to 50 minutes: 11 frames, newest last
    const steps=['m50m','m45m','m40m','m35m','m30m','m25m','m20m','m15m','m10m','m05m',''];
    frames=steps.map(s=>L.tileLayer('https://mesonet.agron.iastate.edu/cache/tile.py/1.0.0/nexrad-n0q-900913'+(s?'-'+s:'')+'/{z}/{x}/{y}.png',{opacity:0,attribution:'Radar: IEM NEXRAD',maxZoom:12}).addTo(map));
    recLayer=L.layerGroup().addTo(map); camLayer=L.layerGroup();
    showFrame(10); timer=setInterval(()=>{ if(playing) showFrame((cur+1)%11); },700);
    map.on('click',e=>choose(e.latlng.lat,e.latlng.lng));
  }
  renderList(await r.json()); return true;
}
function showFrame(i){ cur=i; frames.forEach((f,k)=>f.setOpacity(k===i?0.7:0)); $('#slider').value=i; $('#frameLbl').textContent=i===10?'now':'−'+(10-i)*5+' min'; }
$('#slider').oninput=e=>{playing=false;$('#play').textContent='▶';showFrame(+e.target.value)};
$('#play').onclick=()=>{playing=!playing;$('#play').textContent=playing?'⏸':'▶'};
async function choose(lat,lon){
  $('#lat').value=lat.toFixed(4); $('#lon').value=lon.toFixed(4);
  if(!pick){pick=L.marker([lat,lon],{draggable:true}).addTo(map); pick.on('dragend',()=>{const p=pick.getLatLng();choose(p.lat,p.lng)});} else pick.setLatLng([lat,lon]);
  drawRing();
  $('#name').value=''; $('#startOut').textContent='looking up the nearest town…';
  try{ const r=await fetch(`https://nominatim.openstreetmap.org/reverse?format=jsonv2&lat=${lat}&lon=${lon}&zoom=10`,{headers:{'Accept':'application/json'}}); const j=await r.json(); const a=j.address||{};
    const town=a.city||a.town||a.village||a.county||a.state||'spot', st=a.state_code||(a['ISO3166-2-lvl4']||'').split('-')[1]||a.state||'';
    $('#name').value=(town+(st?'-'+st:'')).replace(/[^A-Za-z0-9-]+/g,'-').replace(/-+/g,'-').replace(/^-|-$/g,''); $('#startOut').textContent='';
  }catch{ $('#name').value='spot'; $('#startOut').textContent=''; }
}
function drawRing(){ const lat=+$('#lat').value, lon=+$('#lon').value, r=+$('#radius').value; if(!isFinite(lat)) return; if(pickRing) pickRing.setLatLng([lat,lon]).setRadius(r*MI); else pickRing=L.circle([lat,lon],{radius:r*MI,color:'#4fc3f7',weight:1.5,dashArray:'6 6',fillOpacity:.04}).addTo(map); }
$('#radius').onchange=drawRing; $('#lat').onchange=$('#lon').onchange=()=>choose(+$('#lat').value,+$('#lon').value);
$('#start').onclick=async()=>{ const name=$('#name').value.trim(), lat=+$('#lat').value, lon=+$('#lon').value; if(!name||!isFinite(lat)||!isFinite(lon)){$('#startOut').textContent='click the map first';return}
  $('#startOut').textContent='starting…'; const q=new URLSearchParams({name,lat,lon,hours:$('#hours').value,radiusMi:$('#radius').value});
  const r=await fetch('/admin/record/start?'+q,{method:'POST',headers:H()}); if(!r.ok){$('#startOut').textContent='failed: '+r.status;return} const j=await r.json();
  $('#startOut').textContent='recording '+j.name+' until '+new Date(j.endsAt).toLocaleTimeString(); if(pick){map.removeLayer(pick);pick=null} if(pickRing){map.removeLayer(pickRing);pickRing=null} $('#name').value='';
  renderList(await (await fetch('/admin/record',{headers:H()})).json()); };
function renderList(l){
  recLayer.clearLayers();
  const running=l.filter(x=>x.running), done=l.filter(x=>!x.running);
  for(const x of running) if(x.lat!=null){ L.circle([x.lat,x.lon],{radius:(x.radiusMi||200)*MI,color:'#ffb74d',weight:1.5,fillOpacity:.05}).addTo(recLayer); L.circleMarker([x.lat,x.lon],{radius:6,color:'#ffb74d',fillOpacity:.9}).bindTooltip(esc(x.name)+' · '+x.frames+' frames').addTo(recLayer); }
  const row=x=>`<div class="rec"><b data-lat="${x.lat??''}" data-lon="${x.lon??''}">${esc(x.name)}</b>${x.running?` <button class="small" data-stop="${esc(x.name)}">stop</button>`:''}<span class="sub">${x.running?'running · ends '+(x.endsAt?new Date(x.endsAt).toLocaleString():'?')+' · ':''}${x.frames} frames · ${x.mb} MB${x.lastError?' · <span style="color:var(--warn)">'+esc(x.lastError)+'</span>':''}${x.running?'':' · <button class="small" data-zip="'+esc(x.name)+'">zip</button>'}</span></div>`;
  $('#list').innerHTML=(running.length?'<div class="sub" style="margin-bottom:4px">Running</div>'+running.map(row).join(''):'<div class="sub">nothing running</div>')+(done.length?'<div class="sub" style="margin:10px 0 4px">Finished (newest first)</div>'+done.slice(0,30).map(row).join(''):'');
  $('#status').textContent=running.length+' running · '+done.length+' on disk · '+new Date().toLocaleTimeString();
}
$('#list').addEventListener('click',async e=>{ const s=e.target.closest('[data-stop]'); if(s){ if(!confirm('Stop '+s.dataset.stop+'?')) return; await fetch('/admin/record/'+encodeURIComponent(s.dataset.stop)+'/stop',{method:'POST',headers:H()}); renderList(await (await fetch('/admin/record',{headers:H()})).json()); return; }
  const z=e.target.closest('[data-zip]'); if(z){ z.textContent='…'; const r=await fetch('/admin/record/'+encodeURIComponent(z.dataset.zip)+'.zip',{headers:H()}); if(!r.ok){z.textContent='failed';return} const u=URL.createObjectURL(await r.blob()); const a=document.createElement('a'); a.href=u; a.download=z.dataset.zip+'.zip'; a.click(); setTimeout(()=>URL.revokeObjectURL(u),60000); z.textContent='zip'; return; }
  const b=e.target.closest('b[data-lat]'); if(b&&b.dataset.lat) map.setView([+b.dataset.lat,+b.dataset.lon],7); });
$('#showCams').onchange=async e=>{ if(!e.target.checked){map.removeLayer(camLayer);return} camLayer.addTo(map); const c=map.getCenter(); const r=await fetch(`/admin/cams/list?lat=${c.lat}&lon=${c.lng}&mi=150`,{headers:H()}); if(!r.ok) return; camLayer.clearLayers(); for(const cam of await r.json()) L.circleMarker([cam.lat,cam.lon],{radius:3,color:'#9fb3c2',weight:1,fillOpacity:.7}).bindTooltip(esc(cam.name)).addTo(camLayer); };
$('#go').onclick=async()=>{key=$('#key').value.trim(); await init();};
(async()=>{ if(key) await init(); else $('#login').hidden=false; })();
setInterval(async()=>{ if(!$('#wrap').hidden){ const r=await fetch('/admin/record',{headers:H()}); if(r.ok) renderList(await r.json()); } },60000);
</script></body></html>
""";
}
