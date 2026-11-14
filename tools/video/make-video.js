#!/usr/bin/env node
/* VantageWX marketing video from a recording: the real radar frames over a basemap, with the app's own answers recreated
   frame by frame from the same nowcast core the app runs (tools/replay/core.js) — status banner, arrival time, rings,
   storm arrow and 15/30/60-minute outlines — then a title and an end card. One encode.

   usage: node make-video.js <recordingDir> [options]
     --basemap=<png>      1236x1236 basemap aligned with the frames (fetch-basemap.js); without it a plain dark map is drawn
     --from=<epoch ms>    first radar frame to show (default: first frame + 30 min, so the tracker has history)
     --to=<epoch ms>      last radar frame (default: 25 min after rain first reaches the point, or the end)
     --hold=0.7           seconds each radar frame is held (video pace)
     --title=…            title card text   --sub=…  second line   --place=…  place name on the map ("Rochester, IL")
     --out=<mp4>          output (default <recordingDir>/vantagewx.mp4)
     --radius=170         miles shown each side of the point (crop of the 200-mi recording)
     --thresh=20          rain threshold in dBZ for the status and the arrival moment
     --fps=30 --size=1080 --tz=America/Chicago
   Needs: pngjs (tools/replay/node_modules), playwright + chromium, ffmpeg. */
const fs = require('fs'), path = require('path'), os = require('os'), { execFileSync } = require('child_process');
module.paths.push(path.join(__dirname, '..', 'replay', 'node_modules'));
const { PNG } = require('pngjs');
const C = require(path.join(__dirname, '..', 'replay', 'core.js'));
const { chromium } = require('playwright');

const dir = process.argv[2]; if (!dir) { console.error('usage: node make-video.js <recordingDir> [--basemap=png] …'); process.exit(1); }
const opt = (n, d) => { const a = process.argv.find(x => x.startsWith('--' + n + '=')); return a ? a.slice(n.length + 3) : d; };
const meta = JSON.parse(fs.readFileSync(path.join(dir, 'meta.json')));
const MI = 1609.344, mpp = meta.metersPerPx, SIZE = meta.sizePx, HALF = SIZE / 2;
const fineCellPx = 4, coarseF = 2, fineKm = mpp * fineCellPx / 1000, coarseKm = fineKm * coarseF;
const RADIUS_MI = +opt('radius', 170), R = Math.round(RADIUS_MI * MI / mpp), c = R / fineCellPx;   // analysis window (px, fine cells)
const thresh = +opt('thresh', 20), hitMi = 3, HOLD = +opt('hold', 0.7), FPS = +opt('fps', 30), OUT_PX = +opt('size', 1080), TZ = opt('tz', 'America/Chicago');
const PLACE = opt('place', meta.name), OUT = opt('out', path.join(dir, 'vantagewx.mp4'));
const times = fs.readdirSync(dir).filter(f => /^\d+\.png$/.test(f)).map(f => +f.slice(0, -4)).sort((a, b) => a - b);
const clock = ms => new Date(ms).toLocaleTimeString('en-US', { timeZone: TZ, hour: 'numeric', minute: '2-digit' });
const dateLine = ms => new Date(ms).toLocaleDateString('en-US', { timeZone: TZ, weekday: 'short', month: 'short', day: 'numeric' });
const DIRS = ['N', 'NNE', 'NE', 'ENE', 'E', 'ESE', 'SE', 'SSE', 'S', 'SSW', 'SW', 'WSW', 'W', 'WNW', 'NW', 'NNW'];
const compass = d => DIRS[Math.round(((d % 360) + 360) % 360 / 22.5) % 16];
const fmtDurExact = m => { m = Math.round(m); if (m < 60) return m + ' min'; const h = Math.floor(m / 60), r = m % 60; return h + ' h' + (r ? ' ' + r + ' min' : ''); };
const fmtDur = m => { m = Math.round(m); if (m < 10) return fmtDurExact(m); const d = m % 10; return d === 0 ? fmtDurExact(m) : d < 5 ? 'just over ' + fmtDurExact(m - d) : 'just under ' + fmtDurExact(m - d + 10); };

// ---- frames → grids (as the replay harness does) ----
const cache = new Map();
function frame(t) {
  if (cache.has(t)) return cache.get(t);
  const png = PNG.sync.read(fs.readFileSync(path.join(dir, t + '.png')));
  const w = 2 * R, out = Buffer.alloc(w * w * 4), x0 = Math.round(HALF - R), y0 = Math.round(HALF - R);
  for (let y = 0; y < w; y++) png.data.copy(out, y * w * 4, ((y0 + y) * png.width + x0) * 4, ((y0 + y) * png.width + x0 + w) * 4);
  const fine = C.despeckle(C.gridFromRGBA(out, w, w, fineCellPx), thresh);
  const f = { t, fine, coarse: C.downsampleMax(fine, coarseF), cleaned: C.despeckle(fine, thresh) };
  cache.set(t, f); if (cache.size > 12) cache.delete(cache.keys().next().value); return f;
}
const nearestTime = (T, target) => { let b = T[0]; for (const t of T) if (Math.abs(t - target) < Math.abs(b - target)) b = t; return b; };
function rainAt(f) {
  const g = f.cleaned, cellMi = fineKm * 0.621371, Rh = hitMi + 0.7 * cellMi; let m = -99;
  for (let y = 0; y < g.h; y++) for (let x = 0; x < g.w; x++) { const e = (x + 0.5 - c) * cellMi, n = (y + 0.5 - c) * cellMi; if (e * e + n * n <= Rh * Rh && g.d[y * g.w + x] > m) m = g.d[y * g.w + x]; }
  return m;
}
let memHist = [];
function analyse(latest) {
  const T = times.filter(t => t <= latest);
  const snap = ms => nearestTime(T, Math.floor(ms / 300000) * 300000);
  const fr = [snap(latest - 30 * 60000), snap(latest - 15 * 60000), latest];
  const dt1 = (fr[2] - fr[0]) / 60000, dt2 = (fr[2] - fr[1]) / 60000;
  const F = fr.map(frame), coarse = F.map(f => f.coarse);
  let motion = dt1 >= 10 ? C.estimateMotion(coarse[0], coarse[2], dt1, coarseKm, { maxKmh: 110 }) : { ok: false, regions: [], global: { conf: 0 } };
  if (dt2 >= 6) { const m2 = C.estimateMotion(coarse[1], coarse[2], dt2, coarseKm, { maxKmh: 110 }); if (m2.ok && (!motion.ok || m2.global.conf > motion.global.conf * 1.15)) motion = m2; }
  const rec = C.reconcileMotion(memHist, motion, latest); memHist = C.rememberMotion(memHist, rec, latest); if (rec.held) motion = rec.motion;
  const vel = C.makeVelocityField(motion, coarseF);
  const lab = C.labelBlobs(F[2].cleaned, thresh);
  const res = C.predictRain(F[2].cleaned, fineKm, c, c, vel, { thresh, hitRadiusMi: hitMi, horizonMin: 120, maxRingMi: 50, ringRadii: [10, 30, 50] }, lab.labels);
  let far = null;
  if (!res.hit) { const fr2 = C.predictRain(F[2].cleaned, fineKm, c, c, vel, { thresh, hitRadiusMi: hitMi, horizonMin: 360, maxRingMi: RADIUS_MI, minHitCells: 6 }, lab.labels); far = fr2.hit && fr2.hit.etaMin > 115 ? fr2.hit : null; }
  return { fr, motion, vel, res, far, lab, grid: F[2].cleaned, now: rainAt(F[2]) };
}

// ---- the app's status wording (the same branches as computeStatus, trimmed to what a recording can show) ----
function status(a, t) {
  const { res, far, motion, now } = a;
  if (now >= thresh) { const w = now < 30 ? 'Light' : now < 40 ? 'Moderate' : now < 50 ? 'Heavy' : 'Very heavy'; return { cls: 'red', main: w + ' rain at your location', sub: res.hit && res.hit.endMin > 0 ? 'rain ending in ~' + Math.round(res.hit.endMin) + ' min' : 'raining now' }; }
  if (res.hit) {
    const h = res.hit, eta = h.etaMin, when = t + eta * 60000;
    const cls = eta <= 15 ? 'red' : 'orange';
    return { cls, main: eta < 10 ? 'Rain arriving in ~' + Math.round(eta) + ' min' : 'Rain arriving ≈ ' + clock(when) + ' (in ' + fmtDur(eta) + ')', sub: (h.maxDbz < 30 ? 'Light' : h.maxDbz < 40 ? 'Moderate' : h.maxDbz < 50 ? 'Heavy' : 'Very heavy') + ' rain ' + Math.round(h.distMi) + ' mi ' + compass(h.fromBearing) + (motion.ok ? ' · moving ' + compass(motion.headingDeg) + ' ' + Math.round(motion.speedMph) + ' mph' : ''), eta: when };
  }
  if (far && motion.ok) { const eta = far.etaMin; return { cls: 'blue', main: 'Rain ' + Math.round(far.distMi) + ' mi ' + compass(far.fromBearing) + ' could reach you in ' + (/^just/.test(fmtDur(eta)) ? '' : 'about ') + fmtDur(eta), sub: 'Straight-line track, ≈ ' + clock(t + eta * 60000) + ' — beyond the two hours the tracker trusts', eta: t + eta * 60000 }; }
  if (res.passing) return { cls: 'blue', main: 'Rain ' + Math.round(res.passing.distMi) + ' mi ' + compass(res.passing.bearing) + ' — passing to the ' + compass(res.passing.passBearing).toLowerCase(), sub: 'Not on track for you' };
  return { cls: 'green', main: 'No rain heading your way', sub: res.nearest ? 'Nearest rain ' + Math.round(res.nearest.distMi) + ' mi ' + compass(res.nearest.bearing) : 'Clear within 50 miles' };
}

// ---- geometry for the overlay, in recording pixels (origin top-left of the 1236 frame) ----
const pxPerMi = MI / mpp;
function hull(pts) { if (pts.length < 3) return pts; pts = pts.slice().sort((a, b) => a[0] - b[0] || a[1] - b[1]); const cr = (o, a, b) => (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0]); const lo = [], up = []; for (const p of pts) { while (lo.length >= 2 && cr(lo[lo.length - 2], lo[lo.length - 1], p) <= 0) lo.pop(); lo.push(p); } for (const p of pts.slice().reverse()) { while (up.length >= 2 && cr(up[up.length - 2], up[up.length - 1], p) <= 0) up.pop(); up.push(p); } return lo.slice(0, -1).concat(up.slice(0, -1)); }
function overlay(a, t) {
  const { lab, vel, grid, motion, res, far } = a, o = { blobs: [], rings: {}, arrival: null };
  const toPx = (gx, gy) => [HALF + (gx - c) * fineCellPx, HALF + (gy - c) * fineCellPx];
  const big = lab.blobs.filter(b => !b.tiny && b.n >= 40).sort((p, q) => q.n - p.n).slice(0, 4);
  const hitBlob = res.hit ? res.hit.blob : far ? far.blob : null;
  for (const b of big) {
    // sample the blob's cells (every 3rd) and advect through the velocity field for 15/30/60 min
    const cells = []; for (let y = 0; y < grid.h; y += 2) for (let x = 0; x < grid.w; x += 2) if (lab.labels[y * grid.w + x] === b.id) cells.push([x + 0.5, y + 0.5]);
    if (cells.length < 6) continue;
    const v = vel(b.cx, b.cy), spd = Math.hypot(v.vx, v.vy) * 60 * 0.621371, hd = (Math.atan2(v.vx, -v.vy) * 180 / Math.PI + 360) % 360;
    const outlines = {};
    for (const m of [15, 30, 60]) { const pts = cells.map(([x, y]) => { let px = x, py = y; for (let k = 0; k < m / 5; k++) { const vv = vel(px, py); px += vv.vx * 5 / fineKm; py += vv.vy * 5 / fineKm; } return toPx(px, py); }); outlines[m] = hull(pts); }
    const [ax, ay] = toPx(b.cx, b.cy), len = spd * 0.5 * pxPerMi;   // arrow = 30 minutes of travel
    o.blobs.push({ id: b.id, toward: b.id === hitBlob, arrow: { x: ax, y: ay, dx: Math.sin(hd * Math.PI / 180) * len, dy: -Math.cos(hd * Math.PI / 180) * len, label: compass(hd) + ' ' + Math.round(spd) + ' mph' }, outlines });
  }
  // rings: solid inside now, dotted projected within 60, dashed otherwise; colour by strength of the hitting part
  for (const r of [10, 30, 50]) { const q = res.ringIn && res.ringIn[r]; const st = q ? (q.t <= 0 ? 'solid' : q.t <= 60 ? 'dotted' : 'dashed') : 'dashed'; const col = q && q.t <= 60 ? (q.maxDbz >= 50 ? '#ff5252' : q.maxDbz >= 40 ? '#ff9100' : q.maxDbz >= 30 ? '#ffd54f' : '#ffffff') : res.ringMiss && res.ringMiss[r] != null && res.ringMiss[r] <= 60 ? '#64b5f6' : '#ffffff'; o.rings[r] = { style: q && q.t <= 60 ? st : res.ringMiss && res.ringMiss[r] != null && res.ringMiss[r] <= 60 ? 'dotted' : 'dashed', col }; }
  const h = res.hit || far; if (h && h.fromBearing != null) { const a2 = h.fromBearing * Math.PI / 180, d = hitMi * pxPerMi; o.arrival = { x: HALF + Math.sin(a2) * d, y: HALF - Math.cos(a2) * d, label: clock(t + h.etaMin * 60000) }; }
  return o;
}

// ---- the page that draws one moment ----
const PAGE = `<!doctype html><meta charset=utf-8><style>html,body{margin:0;background:#0b0f13;width:${OUT_PX}px;height:${OUT_PX}px;overflow:hidden;font-family:system-ui,-apple-system,Segoe UI,Roboto,sans-serif}
canvas{position:absolute;left:0;top:0}#card{position:absolute;inset:0;display:none;align-items:center;justify-content:center;flex-direction:column;text-align:center;color:#e8eef3;background:#0b0f13;padding:80px;box-sizing:border-box}
#card h1{font-size:72px;margin:0 0 18px;font-weight:800;letter-spacing:-1px}#card p{font-size:34px;margin:0;color:#9fb3c2;line-height:1.35;max-width:920px;white-space:pre-line}#card .brand{position:absolute;bottom:70px;font-size:30px;color:#4fc3f7;font-weight:700;letter-spacing:2px}</style>
<canvas id=c width=${OUT_PX} height=${OUT_PX}></canvas><div id=card><h1 id=h1></h1><p id=p></p><div class=brand>VANTAGEWX</div></div>
<script>
const cv=document.getElementById('c'),g=cv.getContext('2d');let base=null;
function img(src){return new Promise(r=>{const i=new Image();i.onload=()=>r(i);i.onerror=()=>r(null);i.src=src;});}
async function setBase(src){base=src?await img(src):null;}
async function draw(d){
  document.getElementById('card').style.display='none';
  const S=${OUT_PX}, sc=S/d.cropPx, off=(d.size-d.cropPx)/2, X=x=>(x-off)*sc, Y=y=>(y-off)*sc;
  g.clearRect(0,0,S,S); g.fillStyle='#1b232b'; g.fillRect(0,0,S,S);
  if(base){g.drawImage(base,off,off,d.cropPx,d.cropPx,0,0,S,S); g.fillStyle='rgba(8,12,16,.35)'; g.fillRect(0,0,S,S);} else { g.strokeStyle='rgba(255,255,255,.06)'; for(let i=0;i<S;i+=S/12){g.beginPath();g.moveTo(i,0);g.lineTo(i,S);g.moveTo(0,i);g.lineTo(S,i);g.stroke();} }
  const radar=await img(d.radar); if(radar){g.globalAlpha=.78; g.drawImage(radar,off,off,d.cropPx,d.cropPx,0,0,S,S); g.globalAlpha=1;}
  const cx=X(d.size/2), cy=Y(d.size/2), ppm=d.pxPerMi*sc;
  // rings
  for(const r of [10,30,50]){const q=d.ov.rings[r]; g.beginPath(); g.arc(cx,cy,r*ppm,0,Math.PI*2); g.lineWidth=q.style==='solid'?5:3.5; g.strokeStyle=q.col; g.setLineDash(q.style==='solid'?[]:q.style==='dotted'?[3,9]:[14,10]); g.globalAlpha=.95; g.stroke(); g.setLineDash([]); g.globalAlpha=1;
    g.font='600 22px system-ui'; g.fillStyle=q.col; g.fillText(r+' mi',cx+r*ppm*0.707+8,cy-r*ppm*0.707-8);}
  // outlines + arrows
  const GC={15:'#ffd54f',30:'#ff9100',60:'#f48fb1'};
  for(const b of d.ov.blobs){ for(const m of [15,30,60]){const h=b.outlines[m]; if(!h||h.length<3)continue; g.beginPath(); h.forEach((p,i)=>i?g.lineTo(X(p[0]),Y(p[1])):g.moveTo(X(p[0]),Y(p[1]))); g.closePath(); g.setLineDash(m===15?[3,7]:m===30?[7,7]:[14,9]); g.lineWidth=m===60?3.5:2.5; g.strokeStyle='rgba(0,0,0,.55)'; g.lineWidth+=2; g.stroke(); g.lineWidth-=2; g.strokeStyle=GC[m]; g.globalAlpha=b.toward?.95:.55; g.stroke(); g.globalAlpha=1; g.setLineDash([]);}
    const a=b.arrow, x0=X(a.x),y0=Y(a.y),x1=X(a.x+a.dx),y1=Y(a.y+a.dy); if(y0<175||y1<175||x1>S-40||x1<40||y1>S-100)continue; g.lineWidth=7; g.strokeStyle='rgba(0,0,0,.6)'; g.beginPath(); g.moveTo(x0,y0); g.lineTo(x1,y1); g.stroke(); g.lineWidth=4; g.strokeStyle='#fff'; g.beginPath(); g.moveTo(x0,y0); g.lineTo(x1,y1); g.stroke();
    const an=Math.atan2(y1-y0,x1-x0); g.fillStyle='#fff'; g.beginPath(); g.moveTo(x1,y1); g.lineTo(x1-16*Math.cos(an-.5),y1-16*Math.sin(an-.5)); g.lineTo(x1-16*Math.cos(an+.5),y1-16*Math.sin(an+.5)); g.closePath(); g.fill();
    g.font='700 24px system-ui'; const lbl=a.label+(b.toward?' · toward you':''); const tw=g.measureText(lbl).width; g.fillStyle='rgba(0,0,0,.6)'; g.fillRect(x1+10,y1-18,tw+14,32); g.fillStyle=b.toward?'#ffd54f':'#fff'; g.fillText(lbl,x1+17,y1+6);}
  // you
  g.beginPath(); g.arc(cx,cy,11,0,Math.PI*2); g.fillStyle='#4fc3f7'; g.fill(); g.lineWidth=4; g.strokeStyle='#fff'; g.stroke();
  g.font='700 24px system-ui'; g.fillStyle='#fff'; g.shadowColor='#000'; g.shadowBlur=6; g.fillText(d.place,cx+16,cy+34); g.shadowBlur=0;
  if(d.ov.arrival){const p=d.ov.arrival; g.beginPath(); g.arc(X(p.x),Y(p.y),8,0,Math.PI*2); g.fillStyle='#ff9100'; g.fill(); g.lineWidth=3; g.strokeStyle='#fff'; g.stroke(); g.font='700 22px system-ui'; g.fillStyle='#ffb74d'; g.shadowColor='#000'; g.shadowBlur=6; g.fillText('arrives ≈ '+p.label,X(p.x)+14,Y(p.y)-16); g.shadowBlur=0;}
  // banner
  const BC={green:'#1b5e20',blue:'#0d47a1',orange:'#e65100',red:'#b71c1c'}; g.fillStyle=BC[d.st.cls]; g.globalAlpha=.94; g.fillRect(0,0,S,150); g.globalAlpha=1;
  g.fillStyle='#fff'; g.font='800 44px system-ui'; g.fillText(d.st.main,32,62); g.font='500 27px system-ui'; g.fillStyle='rgba(255,255,255,.88)'; g.fillText(d.st.sub,32,108);
  // clock + legend
  g.fillStyle='rgba(0,0,0,.6)'; g.fillRect(0,S-84,S,84); g.fillStyle='#fff'; g.font='800 40px system-ui'; g.fillText(d.clock,32,S-30); const cw=g.measureText(d.clock).width; g.font='500 24px system-ui'; g.fillStyle='#9fb3c2'; g.fillText(d.date,32+cw+22,S-30);
  { const lg='NOAA MRMS radar · dotted outlines: where the rain will be in 15 / 30 / 60 min'; g.font='500 20px system-ui'; const w=g.measureText(lg).width; g.fillStyle='rgba(0,0,0,.55)'; g.fillRect(S-w-40,S-126,w+28,36); g.fillStyle='#cfd8dc'; g.textAlign='right'; g.fillText(lg,S-26,S-101); g.textAlign='left'; }
  if(d.note){g.font='800 34px system-ui'; const w=g.measureText(d.note).width; g.fillStyle='rgba(0,0,0,.72)'; g.fillRect(S/2-w/2-26,S-230,w+52,70); g.fillStyle='#ffd54f'; g.textAlign='center'; g.fillText(d.note,S/2,S-183); g.textAlign='left';}
}
function card(h,p){document.getElementById('h1').textContent=h;document.getElementById('p').textContent=p;document.getElementById('card').style.display='flex';}
</script>`;

(async () => {
  const first = times[0] + 30 * 60000;
  const from = +opt('from', 0) || times.find(t => t >= first), toOpt = +opt('to', 0);
  // find when rain first reaches the point, for the default end and the payoff caption
  // first SUSTAINED rain at the point (three scans in a row), so a stray light cell does not end the story early
  let arrivedAt = null; for (let i = 0; i + 2 < times.length; i++) { const t = times[i]; if (t <= from) continue; if ([0, 1, 2].every(k => rainAt(frame(times[i + k])) >= thresh)) { arrivedAt = t; break; } }
  const to = toOpt || (arrivedAt ? Math.min(times[times.length - 1], arrivedAt + 25 * 60000) : times[times.length - 1]);
  const show = times.filter(t => t >= from && t <= to);
  console.log(`${show.length} radar frames ${clock(from)}–${clock(to)}; rain reaches the point ${arrivedAt ? 'at ' + clock(arrivedAt) : 'never'}`);
  const work = fs.mkdtempSync(path.join(os.tmpdir(), 'vwx-video-')), pageFile = path.join(work, 'page.html');
  fs.writeFileSync(pageFile, PAGE);
  const browser = await chromium.launch(); const page = await browser.newPage({ viewport: { width: OUT_PX, height: OUT_PX }, deviceScaleFactor: 1 });
  await page.goto('file://' + pageFile);
  const basemap = opt('basemap', ''); if (basemap && fs.existsSync(basemap)) await page.evaluate(src => setBase(src), 'file://' + path.resolve(basemap));
  const cropPx = Math.round(2 * RADIUS_MI * pxPerMi);
  const shots = [];   // {file, dur}
  const shot = async (name, dur) => { const f = path.join(work, name + '.jpg'); await page.screenshot({ path: f, type: 'jpeg', quality: 92 }); shots.push({ file: f, dur }); };
  // title
  const title = opt('title', 'A storm five hours away.'), sub = opt('sub', 'Watch what VantageWX told a camper in ' + PLACE + ' — and when the rain actually came.');
  await page.evaluate(([h, p]) => card(h, p), [title, sub]); await shot('title', 3.6);
  let firstCall = null, lastSt = null; const calls = [];
  for (let i = 0; i < show.length; i++) {
    const t = show[i], a = analyse(t), st = status(a, t), ov = overlay(a, t);
    if (st.eta && (!arrivedAt || t < arrivedAt)) { calls.push({ t, eta: st.eta, main: st.main }); if (!firstCall) firstCall = calls[0]; }
    let note = null;
    if (arrivedAt && t >= arrivedAt && t < arrivedAt + 26 * 60000 && calls.length) { const b = bestCall(); const off = Math.round((arrivedAt - b.eta) / 60000); note = 'Rain arrived ' + clock(arrivedAt) + ' — called ' + fmtDurExact((arrivedAt - b.t) / 60000) + ' ahead' + (Math.abs(off) <= 3 ? ', on time' : ', ' + Math.abs(off) + ' min ' + (off > 0 ? 'late' : 'early')); }
    await page.evaluate(d => draw(d), { radar: 'file://' + path.join(dir, t + '.png'), size: SIZE, cropPx, pxPerMi, ov, st, clock: clock(t), date: dateLine(t), place: PLACE, note });
    const hold = note ? HOLD * 2.2 : (i === 0 ? HOLD * 2.5 : HOLD);
    await shot('f' + String(i).padStart(4, '0'), hold); lastSt = st;
    process.stdout.write(`\r${i + 1}/${show.length} ${clock(t)}  ${st.main.padEnd(60)}`);
  }
  console.log();
  function bestCall() { if (!arrivedAt) return calls[0]; const good = calls.filter(k => Math.abs(k.eta - arrivedAt) <= 10 * 60000); return (good.length ? good : calls).reduce((a, k) => k.t < a.t ? k : a); }
  const best = calls.length ? bestCall() : null;
  const end1 = best ? 'At ' + clock(best.t) + ' VantageWX said: “' + (best.main.includes('could reach you') ? 'rain ≈ ' + clock(best.eta) : best.main) + '”' + (arrivedAt ? '\n' + fmtDurExact((arrivedAt - best.t) / 60000) + ' before it arrived.' : '') + '\n\nKnow when. Know what to do.\nvantagewx.com' : 'Know when. Know what to do.\nvantagewx.com';
  const end2 = arrivedAt ? 'Rain arrived ' + clock(arrivedAt) + '.' : '';
  await page.evaluate(([h, p]) => card(h, p), [end2 || 'VantageWX', end1]); await shot('end', 5);
  await browser.close();
  // one encode: concat demuxer over the stills with per-image durations
  const list = path.join(work, 'list.txt');
  fs.writeFileSync(list, shots.map(s => `file '${s.file}'\nduration ${s.dur.toFixed(3)}`).join('\n') + `\nfile '${shots[shots.length - 1].file}'\n`);
  const tmp = OUT.replace(/\.mp4$/, '.tmp.mp4');
  execFileSync('ffmpeg', ['-y', '-loglevel', 'error', '-f', 'concat', '-safe', '0', '-i', list, '-vf', `fps=${FPS},format=yuv420p,fade=t=in:st=0:d=0.6,fade=t=out:st=${(shots.reduce((s, x) => s + x.dur, 0) - 0.8).toFixed(2)}:d=0.8`, '-c:v', 'libx264', '-crf', '18', '-preset', 'veryfast', '-pix_fmt', 'yuv420p', '-movflags', '+faststart', tmp], { stdio: 'inherit' });
  fs.renameSync(tmp, OUT);
  const total = shots.reduce((s, x) => s + x.dur, 0);
  console.log(`→ ${OUT}  (${total.toFixed(0)} s, ${shots.length} stills; work files in ${work})`);
})();
