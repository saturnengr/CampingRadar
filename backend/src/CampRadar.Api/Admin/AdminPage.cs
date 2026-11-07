namespace CampRadar.Api.Admin;

/// <summary>
/// The admin dashboard at /admin: a single HTML page that asks for the admin key once (kept in the browser's
/// sessionStorage), then reads /admin/overview with it. Devices on a map, counts, versions, server health.
/// </summary>
public static class AdminPage
{
    public const string Html = """
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Camp Radar · admin</title>
<link rel="stylesheet" href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css">
<style>
  :root{--bg:#0f1a24;--panel:#1b2d3b;--line:#2a4255;--text:#e9f0f5;--muted:#9fb3c2;--accent:#ffb74d;--blue:#4fc3f7;--green:#66bb6a}
  body{margin:0;background:var(--bg);color:var(--text);font:15px/1.5 system-ui,sans-serif}
  header{display:flex;align-items:center;gap:14px;padding:12px 18px;border-bottom:1px solid var(--line)}
  header h1{font-size:18px;margin:0}header .r{margin-left:auto;color:var(--muted);font-size:13px}
  main{padding:16px 18px;display:grid;gap:16px;grid-template-columns:1fr 1fr 1fr 1fr}
  .card{background:var(--panel);border:1px solid var(--line);border-radius:12px;padding:14px 16px}
  .card h2{font-size:13px;letter-spacing:.08em;text-transform:uppercase;color:var(--muted);margin:0 0 6px}
  .big{font-size:34px;font-weight:700;line-height:1.1}.sub{color:var(--muted);font-size:13px}
  #map{grid-column:1/-1;height:460px;border-radius:12px;border:1px solid var(--line)}
  table{width:100%;border-collapse:collapse;font-size:13.5px}th,td{text-align:left;padding:6px 8px;border-bottom:1px solid var(--line);white-space:nowrap}th{color:var(--muted);font-weight:600}
  .wide{grid-column:1/-1}.half{grid-column:span 2}
  .ok{color:var(--green)}.warn{color:var(--accent)}
  button{font:inherit;background:var(--accent);color:#1a1200;border:0;border-radius:8px;padding:8px 14px;font-weight:600;cursor:pointer}
  input{font:inherit;padding:8px 10px;border-radius:8px;border:1px solid var(--line);background:#0b1117;color:var(--text);width:360px;max-width:100%}
  #login{padding:40px 18px;max-width:520px}
  @media(max-width:900px){main{grid-template-columns:1fr 1fr}.half{grid-column:1/-1}}
</style></head>
<body>
<header><h1>Camp Radar · admin</h1><div class="r" id="when"></div><button id="refresh" hidden>Refresh</button><button id="logout" hidden style="background:var(--panel);color:var(--text);border:1px solid var(--line)">Sign out</button></header>
<div id="login"><p>Enter the admin key (the <code>AdminKey</code> from appsettings.Production.json).</p><p><input id="key" type="password" autocomplete="off" placeholder="admin key"> <button id="go">Open</button></p><p class="sub" id="err"></p></div>
<main id="main" hidden>
  <div class="card"><h2>Active now</h2><div class="big" id="now">–</div><div class="sub">app open in the last 10 minutes</div></div>
  <div class="card"><h2>Today</h2><div class="big" id="today">–</div><div class="sub">distinct devices, last 24 h</div></div>
  <div class="card"><h2>This week</h2><div class="big" id="week">–</div><div class="sub">distinct devices, last 7 days</div></div>
  <div class="card"><h2>Alerts on</h2><div class="big" id="push">–</div><div class="sub">devices with push subscriptions</div></div>
  <div id="map"></div>
  <div class="card half"><h2>Activities (7 days)</h2><table id="acts"></table></div>
  <div class="card half"><h2>Server</h2><table id="health"></table></div>
  <div class="card wide"><h2>Pro access</h2>
    <p class="sub">Grant Pro to an email and hand the person the activation code (Settings → Account in the app). Months add to any time they already have; 0 months removes Pro and signs their devices out.</p>
    <p><input id="gEmail" type="email" placeholder="email" style="width:260px"> <input id="gMonths" type="number" value="12" min="0" max="120" style="width:80px"> months <input id="gNote" placeholder="note (optional)" style="width:200px"> <button id="grant">Grant Pro</button> <span id="gOut" class="sub"></span></p>
    <div style="overflow:auto;max-height:320px"><table id="ents"><thead><tr><th>Email</th><th>Plan</th><th>Status</th><th>Expires</th><th>Devices</th><th>Source</th><th>Note</th><th></th></tr></thead><tbody></tbody></table></div>
  </div>
  <div class="card wide"><h2>Devices (30 days)</h2><div style="overflow:auto;max-height:420px"><table id="devs"><thead><tr><th>Last seen</th><th>First seen</th><th>Opens</th><th>Activity</th><th>Lang</th><th>Platform</th><th>Version</th><th>Alerts</th><th>Near</th></tr></thead><tbody></tbody></table></div></div>
</main>
<script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js"></script>
<script>
const $=s=>document.querySelector(s);
let key=sessionStorage.getItem('adminKey')||'', map, layer;
const ago=ms=>{const m=Math.round((Date.now()-ms)/60000);return m<1?'just now':m<60?m+' min ago':m<1440?Math.round(m/60)+' h ago':Math.round(m/1440)+' d ago'};
async function load(){
  const r=await fetch('/admin/overview',{headers:{'X-Admin-Key':key}});
  if(r.status===401){sessionStorage.removeItem('adminKey');$('#err').textContent='Wrong key.';$('#login').hidden=false;$('#main').hidden=true;return}
  const o=await r.json(); sessionStorage.setItem('adminKey',key);
  $('#login').hidden=true;$('#main').hidden=false;$('#refresh').hidden=false;$('#logout').hidden=false; loadEnts();
  $('#when').textContent='as of '+new Date().toLocaleTimeString();
  $('#now').textContent=o.activeNow;$('#today').textContent=o.today;$('#week').textContent=o.week;$('#push').textContent=o.pushSpots;
  $('#acts').innerHTML=Object.entries(o.activities).sort((a,b)=>b[1]-a[1]).map(([k,v])=>'<tr><td>'+(k||'—')+'</td><td>'+v+'</td></tr>').join('')||'<tr><td class="sub">none yet</td></tr>';
  const h=o.health; $('#health').innerHTML=[
    ['Radar scans cached',h.radar.scans+(h.radar.latest?' · latest '+ago(Date.parse(h.radar.latest)):'')],
    ['Lightning flashes in store',h.lightning.flashes+(h.lightning.lastIngest?' · ingested '+ago(Date.parse(h.lightning.lastIngest)):'')],
    ['Worker last cycle',h.worker.lastCycle?ago(Date.parse(h.worker.lastCycle)):'—'],
    ['Push',h.push.enabled?'<span class="ok">enabled</span>':'<span class="warn">disabled</span>'],
    ['Model briefings',h.briefings.enabled?'<span class="ok">enabled</span>':'off (no API key)'],
    ['Alerts sent (24 h)',o.alerts24],
  ].map(([k,v])=>'<tr><th>'+k+'</th><td>'+v+'</td></tr>').join('');
  if(!map){map=L.map('map',{worldCopyJump:true}).setView([38,-96],4);L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png',{attribution:'© OpenStreetMap'}).addTo(map);layer=L.layerGroup().addTo(map)}
  layer.clearLayers();
  const now=Date.now();
  for(const d of o.devices){const age=now-d.lastMs;const col=age<600000?'#66bb6a':age<86400000?'#4fc3f7':'#9fb3c2';
    L.circleMarker([d.lat,d.lon],{radius:age<600000?8:6,color:col,fillColor:col,fillOpacity:.7,weight:1}).bindTooltip((d.activity||'—')+' · '+d.platform+' · '+ago(d.lastMs)+(d.push?' · alerts on':'')).addTo(layer)}
  $('#devs tbody').innerHTML=o.devices.map(d=>'<tr><td>'+ago(d.lastMs)+'</td><td>'+new Date(d.firstMs).toLocaleDateString()+'</td><td>'+d.opens+'</td><td>'+(d.activity||'—')+'</td><td>'+d.lang+'</td><td>'+d.platform+'</td><td>'+d.version+'</td><td>'+(d.push?'yes':'')+'</td><td>'+d.lat.toFixed(1)+', '+d.lon.toFixed(1)+'</td></tr>').join('')||'<tr><td colspan="9" class="sub">No devices have reported yet.</td></tr>';
}
async function loadEnts(){const r=await fetch('/admin/entitlements',{headers:{'X-Admin-Key':key}});if(!r.ok)return;const l=await r.json();
  $('#ents tbody').innerHTML=l.map(e=>'<tr><td>'+e.email+'</td><td>'+e.plan+'</td><td>'+(e.active?'<span class="ok">active</span>':'<span class="warn">inactive</span>')+'</td><td>'+(e.expiresMs?new Date(e.expiresMs).toLocaleDateString():'—')+'</td><td>'+e.devices+'</td><td>'+e.source+'</td><td>'+(e.note||'')+'</td><td>'+(e.active?'<button data-code="'+e.email+'" style="padding:4px 10px;font-size:12px">New code</button>':'')+'</td></tr>').join('')||'<tr><td colspan="8" class="sub">No one has Pro yet.</td></tr>'}
$('#grant').onclick=async()=>{const body={email:$('#gEmail').value.trim(),months:+$('#gMonths').value,note:$('#gNote').value.trim()};const r=await fetch('/admin/grant',{method:'POST',headers:{'X-Admin-Key':key,'Content-Type':'application/json'},body:JSON.stringify(body)});const j=await r.json();$('#gOut').innerHTML=r.ok?(j.code?'Code for '+j.email+': <b style="color:var(--accent);font-size:18px">'+j.code+'</b> (valid 14 days, single use)':'Pro removed for '+j.email):(j.error||'failed');loadEnts()};
$('#ents').addEventListener('click',async e=>{const b=e.target.closest('[data-code]');if(!b)return;const r=await fetch('/admin/code',{method:'POST',headers:{'X-Admin-Key':key,'Content-Type':'application/json'},body:JSON.stringify({email:b.dataset.code,months:0})});const j=await r.json();$('#gOut').innerHTML=r.ok?'Code for '+b.dataset.code+': <b style="color:var(--accent);font-size:18px">'+j.code+'</b>':(j.error||'failed')});
$('#go').onclick=()=>{key=$('#key').value.trim();load()};$('#key').addEventListener('keydown',e=>{if(e.key==='Enter')$('#go').click()});
$('#refresh').onclick=load;$('#logout').onclick=()=>{sessionStorage.removeItem('adminKey');location.reload()};
if(key)load();
setInterval(()=>{if(key&&!$('#main').hidden)load()},60000);
</script>
</body></html>
""";
}
