// Replay harness: runs the page's own nowcast core over a recorded set of NOAA MRMS frames and scores it
// against motion worked out independently (long-baseline displacement and storm-core tracks) and against
// whether rain actually reached the centre point.
const fs = require('fs'), path = require('path');
const { PNG } = require('pngjs');
const C = require('./core.js');

const dir = process.argv[2]; const radiusMi = +(process.argv[3] || 120);
const meta = JSON.parse(fs.readFileSync(path.join(dir, 'meta.json')));
const MI = 1609.344, mpp = meta.metersPerPx, size = meta.sizePx, half = size / 2;
const fineCellPx = 4, coarseF = 2;                 // zoom 8 × 4 px = the page's zoom 7 × 2 px (≈2.05 km)
const fineKm = mpp * fineCellPx / 1000, coarseKm = fineKm * coarseF;
const R = Math.round(radiusMi * MI / mpp), c = R / fineCellPx, thresh = 20, hitMi = 3;
const times = fs.readdirSync(dir).filter(f => /^\d+\.png$/.test(f)).map(f => +f.slice(0, -4)).sort((a, b) => a - b);
const tz = 'America/Chicago', clock = ms => new Date(ms).toLocaleTimeString('en-US', { timeZone: tz, hour: '2-digit', minute: '2-digit', hour12: false });

const cache = new Map();
function frame(t) {
  if (cache.has(t)) return cache.get(t);
  const png = PNG.sync.read(fs.readFileSync(path.join(dir, t + '.png')));
  // crop the central 2R px (the page builds its composite exactly 2R wide around the point)
  const w = 2 * R, out = Buffer.alloc(w * w * 4), x0 = Math.round(half - R), y0 = Math.round(half - R);
  for (let y = 0; y < w; y++) png.data.copy(out, y * w * 4, ((y0 + y) * png.width + x0) * 4, ((y0 + y) * png.width + x0 + w) * 4);
  const fine = C.despeckle(C.gridFromRGBA(out, w, w, fineCellPx), 15);
  const f = { t, fine, coarse: C.downsampleMax(fine, coarseF), cleaned: C.despeckle(fine, thresh) };
  cache.set(t, f); return f;
}
const nearestTime = (T, target) => { let b = T[0]; for (const t of T) if (Math.abs(t - target) < Math.abs(b - target)) b = t; return b; };
const heading = (vx, vy) => (Math.atan2(vx, -vy) * 180 / Math.PI + 360) % 360;
const angDiff = (a, b) => { let d = ((a - b) % 360 + 540) % 360 - 180; return d; };
const compass = d => ['N', 'NNE', 'NE', 'ENE', 'E', 'ESE', 'SE', 'SSE', 'S', 'SSW', 'SW', 'WSW', 'W', 'WNW', 'NW', 'NNW'][Math.round(d / 22.5) % 16];

// rain at the centre: max dBZ within the hit radius on the cleaned fine grid
function rainAt(f) {
  const g = f.cleaned, cellMi = fineKm * 0.621371, Rh = hitMi + 0.7 * cellMi; let m = -99;
  for (let y = 0; y < g.h; y++) for (let x = 0; x < g.w; x++) { const e = (x + 0.5 - c) * cellMi, n = (y + 0.5 - c) * cellMi; if (e * e + n * n <= Rh * Rh && g.d[y * g.w + x] > m) m = g.d[y * g.w + x]; }
  return m;
}

// ---- independent motion 1: long-baseline (≈60 min) whole-window NCC on the coarse grid, mean removed so broad shields don't flatten the peak
function longBaseline(tA, tB) {
  const A = frame(tA).coarse, B = frame(tB).coarse, w = A.w, dt = (tB - tA) / 60000;
  const S = Math.ceil((110 / 60) * dt / coarseKm), x0 = S, y0 = S, x1 = w - 1 - S, y1 = w - 1 - S;
  const IA = C.intensityField(A), IB = C.intensityField(B);
  const mr = I => { let s = 0, n = 0; for (let y = y0; y <= y1; y++) for (let x = x0; x <= x1; x++) { s += I[y * w + x]; n++; } const m = s / n; const o = new Float32Array(I.length); for (let i = 0; i < I.length; i++) o[i] = I[i] - m; return o; };
  const a = mr(IA), b = mr(IB);
  let best = -Infinity, bu = 0, bv = 0;
  for (let v = -S; v <= S; v++) for (let u = -S; u <= S; u++) {
    let ab = 0, aa = 0, bb = 0;
    for (let y = y0; y <= y1; y++) { const rb = y * w, ra = (y - v) * w - u; for (let x = x0; x <= x1; x++) { const p = a[ra + x], q = b[rb + x]; aa += p * p; bb += q * q; ab += p * q; } }
    const sc = ab / Math.sqrt(aa * bb); if (sc > best) { best = sc; bu = u; bv = v; }
  }
  const vx = bu * coarseKm / dt, vy = bv * coarseKm / dt;
  return { speedMph: Math.hypot(vx, vy) * 60 * 0.621371, headingDeg: heading(vx, vy), score: best, edge: Math.abs(bu) === S || Math.abs(bv) === S };
}

// ---- independent motion 2: tracks of strong cores (≥ coreDbz) followed frame to frame
function coreTracks(coreDbz) {
  const tracks = []; let live = [];
  for (const t of times) {
    const f = frame(t), lb = C.labelBlobs(f.fine, coreDbz, 3), blobs = lb.blobs.filter(b => !b.tiny && b.n >= 4);
    const next = [];
    for (const b of blobs) {
      let best = null, bd = Infinity;
      for (const tr of live) {
        const last = tr[tr.length - 1], dt = (t - last.t) / 60000;
        // expected position from the track's own velocity (or stay put for a new track); accept within 12 km + 10 km per 10 min
        const ex = last.x + (tr.vx || 0) * dt / fineKm, ey = last.y + (tr.vy || 0) * dt / fineKm;
        const d = Math.hypot(b.cx - ex, b.cy - ey) * fineKm;
        if (d < 12 + dt && d < bd) { bd = d; best = tr; }
      }
      if (best && !best.taken) { best.push({ t, x: b.cx, y: b.cy, n: b.n }); best.taken = true; fit(best); next.push(best); }
      else { const tr = [{ t, x: b.cx, y: b.cy, n: b.n }]; tracks.push(tr); next.push(tr); }
    }
    for (const tr of next) tr.taken = false; live = next;
  }
  function fit(tr) {   // least squares x(t), y(t) over the track
    const n = tr.length, t0 = tr[0].t; let st = 0, sx = 0, sy = 0, stt = 0, stx = 0, sty = 0;
    for (const p of tr) { const u = (p.t - t0) / 60000; st += u; sx += p.x; sy += p.y; stt += u * u; stx += u * p.x; sty += u * p.y; }
    const den = n * stt - st * st; if (den <= 0) return;
    tr.vx = (n * stx - st * sx) / den * fineKm; tr.vy = (n * sty - st * sy) / den * fineKm;   // km/min
  }
  return tracks.filter(tr => tr.length >= 4 && (tr[tr.length - 1].t - tr[0].t) >= 25 * 60000);
}
function coreTruthAt(tracks, t) {   // median of tracks alive around t
  const vs = tracks.filter(tr => tr[0].t <= t + 15 * 60000 && tr[tr.length - 1].t >= t - 15 * 60000).map(tr => ({ vx: tr.vx, vy: tr.vy }));
  if (!vs.length) return null;
  const med = a => { a = a.slice().sort((p, q) => p - q); return a[a.length >> 1]; };
  const vx = med(vs.map(v => v.vx)), vy = med(vs.map(v => v.vy));
  return { speedMph: Math.hypot(vx, vy) * 60 * 0.621371, headingDeg: heading(vx, vy), n: vs.length };
}

// ---- the page's pipeline for "latest = t"
function pageRun(latest) {
  const T = times.filter(t => t <= latest);
  const snap = ms => nearestTime(T, Math.floor(ms / 300000) * 300000);
  const fr = [snap(latest - 30 * 60000), snap(latest - 15 * 60000), latest];
  const dt1 = (fr[2] - fr[0]) / 60000, dt2 = (fr[2] - fr[1]) / 60000;
  const F = fr.map(frame), coarse = F.map(f => f.coarse);
  let motion = dt1 >= 10 ? C.estimateMotion(coarse[0], coarse[2], dt1, coarseKm, { maxKmh: 110 }) : { ok: false, regions: [], global: { conf: 0 }, reason: 'radar history too short' };
  if (dt2 >= 6) { const m2 = C.estimateMotion(coarse[1], coarse[2], dt2, coarseKm, { maxKmh: 110 }); if (m2.ok && (!motion.ok || m2.global.conf > motion.global.conf * 1.15)) { m2.baselineMin = dt2; motion = m2; } else motion.baselineMin = dt1; }
  const vel = C.makeVelocityField(motion, coarseF);
  const labelled = F.map(f => C.labelBlobs(f.cleaned, thresh));
  const res = C.predictRain(F[2].cleaned, fineKm, c, c, vel, { thresh, hitRadiusMi: hitMi, horizonMin: 120, maxRingMi: 50 }, labelled[2].labels);
  const trends = C.blobTrends(labelled, [dt1, dt2], vel, fineKm, thresh);
  return { fr, dt1, dt2, motion, res, trends };
}

// ---------------- run ----------------
const tracks = coreTracks(35);
console.log(`${times.length} frames ${clock(times[0])}–${clock(times[times.length - 1])}, window ±${radiusMi} mi, ${tracks.length} core tracks (≥35 dBZ, ≥25 min)`);
const rainSeries = times.map(t => ({ t, dbz: rainAt(frame(t)) }));
const firstRain = rainSeries.find(r => r.dbz >= thresh);
console.log('rain (≥20 dBZ) at the centre: ' + (firstRain ? 'first at ' + clock(firstRain.t) : 'never') + ' — ' + rainSeries.map(r => r.dbz >= thresh ? '#' : r.dbz >= 5 ? '.' : ' ').join(''));

console.log('\n time   | page motion (baseline)          conf | 60-min NCC        | core tracks        | Δhdg Δspd | page ETA → actual');
const rows = []; const hist = []; let fizzleSeen = 0; const DRY = process.argv.includes('--dry');
const fizzOld = (hit, p) => { const tr = p.trends.get(hit.blob); return !!(tr && tr.word === 'weakening' && tr.fizzleMin != null && tr.fizzleMin < hit.etaMin - 2); };
for (const t of times) {
  if (t - times[0] < 30 * 60000) continue;
  const p = pageRun(t), m = p.motion;
  const tA = nearestTime(times.filter(x => x < t), t - 60 * 60000), lb = (t - tA) >= 40 * 60000 ? longBaseline(tA, t) : null;
  const ct = coreTruthAt(tracks, t);
  const truth = ct && ct.n >= 3 ? ct : lb;
  const dH = m.ok && truth ? angDiff(m.headingDeg, truth.headingDeg) : null, dS = m.ok && truth ? m.speedMph - truth.speedMph : null;
  const actual = rainSeries.find(r => r.t >= t && r.dbz >= thresh), actMin = actual ? (actual.t - t) / 60000 : null;
  const etaTxt = p.res.hit ? Math.round(p.res.hit.etaMin) + ' min max ' + p.res.hit.maxDbz + ' dBZ' : 'no hit';
  const actTxt = rainAt(frame(t)) >= thresh ? 'raining now' : actual ? 'rain in ' + Math.round(actMin) + ' min' : 'no rain in record';
  const nr = p.res.nearest; if (nr && !hist.some(h => h.t === t)) hist.push({ t, d: nr.distMi, b: nr.bearing });
  while (hist.length && t - hist[0].t > 60 * 60000) hist.shift();
  const edge = C.edgeCheck(hist, t, nr, m);
  let fizz = false;
  if (p.res.hit) { const tr = p.trends.get(p.res.hit.blob); fizz = !!(tr && tr.word === 'weakening' && tr.fizzleMin != null && tr.fizzleMin < p.res.hit.etaMin - 2); if (fizz) fizzleSeen = t; else if (fizzleSeen && t - fizzleSeen < 20 * 60000 && !(tr && tr.word === 'strengthening') && p.res.hit.maxDbz < 40) fizz = true; }
  const hit = p.res.hit; let oldC = 'green', newC = 'green', etaNew = hit ? hit.etaMin : null;
  if (hit) {
    const far = hit.distMi > 50;
    oldC = (fizzOld(hit, p) && hit.etaMin > 10 && hit.maxDbz < 40) ? 'yellow' : far ? 'blue' : hit.etaMin <= 10 ? 'red' : 'orange';
    const lowConf = DRY && hit.maxDbz < 35;
    if (fizz && hit.etaMin > 10 && hit.maxDbz < 40) newC = 'yellow';
    else if (!lowConf && edge && edge.verdict === 'stalled' && hit.maxDbz < 40 && hit.etaMin > 5) newC = 'yellow';
    else if (far && !lowConf) newC = 'blue';
    else if (lowConf) newC = 'yellow';
    else { if (edge && edge.verdict === 'slow' && edge.etaMin > hit.etaMin) etaNew = Math.min(120, edge.etaMin); newC = etaNew <= 10 ? 'red' : 'orange'; }
  }
  rows.push({ t, m, truth, dH, dS, eta: p.res.hit ? p.res.hit.etaMin : null, etaNew, oldC, newC, actMin, rainingNow: rainAt(frame(t)) >= thresh, edge, nearest: nr, maxDbz: p.res.hit ? p.res.hit.maxDbz : null });
  const eTxt = ` | ${oldC.padEnd(6)} → ${newC.padEnd(6)}` + (edge ? ` edge ${edge.rate.toFixed(0)} vs ${edge.closing.toFixed(0)} mph ${edge.verdict}` : '');
  console.log(`${clock(t)}  | ${m.ok ? (compass(m.headingDeg) + ' ' + m.headingDeg.toFixed(0) + '° ' + m.speedMph.toFixed(0) + ' mph').padEnd(18) + ('(' + (m.baselineMin || 0).toFixed(0) + ' min)').padEnd(10) : ('— ' + (m.reason || '')).padEnd(28)} ${m.global.conf.toFixed(2)} | ${lb ? (compass(lb.headingDeg) + ' ' + lb.headingDeg.toFixed(0) + '° ' + lb.speedMph.toFixed(0) + ' mph').padEnd(17) : ''.padEnd(17)} | ${ct ? (compass(ct.headingDeg) + ' ' + ct.headingDeg.toFixed(0) + '° ' + ct.speedMph.toFixed(0) + ' mph n=' + ct.n).padEnd(18) : ''.padEnd(18)} | ${dH == null ? '    ' : (dH >= 0 ? '+' : '') + dH.toFixed(0).padStart(3) + '°'} ${dS == null ? '     ' : (dS >= 0 ? '+' : '') + dS.toFixed(0).padStart(3) + ' '} | ${etaTxt} → ${actTxt}${eTxt}`);
}
const scored = rows.filter(r => r.dH != null);
if (scored.length) {
  const absH = scored.map(r => Math.abs(r.dH)).sort((a, b) => a - b), absS = scored.map(r => Math.abs(r.dS)).sort((a, b) => a - b);
  console.log(`\nheading: median |error| ${absH[absH.length >> 1].toFixed(0)}°, 90th pct ${absH[Math.floor(absH.length * 0.9)].toFixed(0)}°, reversed (>120°) in ${scored.filter(r => Math.abs(r.dH) > 120).length}/${scored.length}`);
  console.log(`speed:   median |error| ${absS[absS.length >> 1].toFixed(0)} mph, 90th pct ${absS[Math.floor(absS.length * 0.9)].toFixed(0)} mph`);
}
const etaRows = rows.filter(r => !r.rainingNow && r.eta != null && r.actMin != null);
if (etaRows.length) { const e = etaRows.map(r => r.eta - r.actMin); console.log(`ETA: ${etaRows.length} forecasts with a real arrival; mean bias ${(e.reduce((a, b) => a + b, 0) / e.length).toFixed(0)} min, median |error| ${e.map(Math.abs).sort((a, b) => a - b)[e.length >> 1].toFixed(0)} min`); }

const alarm = c => c === 'orange' || c === 'red';
const score = key => { const fa = rows.filter(r => !r.rainingNow && alarm(r[key]) && (r.actMin == null || r.actMin > 90)).length; const miss = rows.filter(r => !r.rainingNow && !alarm(r[key]) && r.actMin != null && r.actMin <= 60).length; const red = rows.filter(r => !r.rainingNow && r[key] === 'red' && (r.actMin == null || r.actMin > 30)).length; return `${fa} false alarms (orange/red, no rain within 90 min), ${red} false reds, ${miss} misses (rain within 60 min, no alarm)`; };
console.log('old rules: ' + score('oldC'));
console.log('new rules: ' + score('newC') + (DRY ? '  [with NWS POP < 10% / dry air]' : '  [without forecast info; add --dry to apply the 1% POP the forecast gave that night]'));
