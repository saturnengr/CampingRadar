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
const R = Math.round(radiusMi * MI / mpp), c = R / fineCellPx, thresh = +(process.env.THRESH || 20), hitMi = 3;   // THRESH = the app's alert threshold (10 any echo / 15 light / 20 / 30 moderate); radar truth uses the same bar
const times = fs.readdirSync(dir).filter(f => /^\d+\.png$/.test(f)).map(f => +f.slice(0, -4)).sort((a, b) => a - b);
const tz = 'America/Chicago', clock = ms => new Date(ms).toLocaleTimeString('en-US', { timeZone: tz, hour: '2-digit', minute: '2-digit', hour12: false });

// motion-field variants (env): MOTION_REGIONS=3|4|5, MOTION_REGIONS_AUTO=1, MOTION_SEGMENTS=1 (+ MOTION_SEG_KM, MOTION_SEG_MIN_KM)
const MOPTS = { maxKmh: 110, regions: +(process.env.MOTION_REGIONS || 0) || undefined, regionsAuto: !!process.env.MOTION_REGIONS_AUTO, segments: !!process.env.MOTION_SEGMENTS, segKm: +(process.env.MOTION_SEG_KM || 50), segMinKm: +(process.env.MOTION_SEG_MIN_KM || 100), thresh };
const cache = new Map();
function frame(t) {
  if (cache.has(t)) return cache.get(t);
  const png = PNG.sync.read(fs.readFileSync(path.join(dir, t + '.png')));
  // crop the central 2R px (the page builds its composite exactly 2R wide around the point)
  const w = 2 * R, out = Buffer.alloc(w * w * 4), x0 = Math.round(half - R), y0 = Math.round(half - R);
  for (let y = 0; y < w; y++) png.data.copy(out, y * w * 4, ((y0 + y) * png.width + x0) * 4, ((y0 + y) * png.width + x0 + w) * 4);
  const fine = C.despeckle(C.gridFromRGBA(out, w, w, fineCellPx), Math.min(15, thresh));
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
  let motion = dt1 >= 10 ? C.estimateMotion(coarse[0], coarse[2], dt1, coarseKm, MOPTS) : { ok: false, regions: [], global: { conf: 0 }, reason: 'radar history too short' };
  if (dt2 >= 6) { const m2 = C.estimateMotion(coarse[1], coarse[2], dt2, coarseKm, MOPTS); if (m2.ok && (!motion.ok || m2.global.conf > motion.global.conf * 1.15)) { m2.baselineMin = dt2; motion = m2; } else motion.baselineMin = dt1; }
  const vel = C.makeVelocityField(motion, coarseF);
  const labelled = F.map(f => C.labelBlobs(f.cleaned, thresh));
  const res = C.predictRain(F[2].cleaned, fineKm, c, c, vel, { thresh, hitRadiusMi: hitMi, horizonMin: 120, maxRingMi: 50 }, labelled[2].labels);
  const trends = C.blobTrends(labelled, [dt1, dt2], vel, fineKm, thresh);
  return { fr, dt1, dt2, motion, res, trends };
}

// ---------------- run ----------------
const tracks = coreTracks(35);
console.log('motion variant: ' + JSON.stringify(MOPTS));
console.log(`${times.length} frames ${clock(times[0])}–${clock(times[times.length - 1])}, window ±${radiusMi} mi, ${tracks.length} core tracks (≥35 dBZ, ≥25 min)`);
const rainSeries = times.map(t => ({ t, dbz: rainAt(frame(t)) }));
const firstRain = rainSeries.find(r => r.dbz >= thresh);
console.log('rain (≥' + thresh + ' dBZ) at the centre: ' + (firstRain ? 'first at ' + clock(firstRain.t) : 'never') + ' — ' + rainSeries.map(r => r.dbz >= thresh ? '#' : r.dbz >= 5 ? '.' : ' ').join(''));

console.log('\n time   | page motion (baseline)          conf | 60-min NCC        | core tracks        | Δhdg Δspd | page ETA → actual');
const rows = []; const hist = []; let segFrames = 0, segRegs = 0, regGrid = {}; let fizzleSeen = 0; const DRY = process.argv.includes('--dry');
/* ---- V4 candidate rules (harness only; nothing in the app changes until these win on the whole batch) ----
   --v4 scores a third colour column:
   1. fizzle escape: the 20-minute "it fizzled" memory holds only while the edge is NOT observed closing (edge rate < 5 mph);
      a rain edge that resumes advancing is believed again at once.
   2. grazing gate: a projected hit that only clips the circle (in and out within 12 min with few cells) and carries no heavy
      core is a passing storm, not an arrival → blue instead of orange.
   3. horizon colour: an arrival projected more than V4_BLUE_MIN (75) minutes out is blue ("could reach you"), not orange —
      orange means within about an hour. Far-out projections are where the motion estimate's errors compound. Not applied when
      echo is already within V4_BLUE_NEAR_MI (25) miles: nearby echo develops and accelerates faster than the projection sees.
   4. receding edge: when the nearest echo has been moving AWAY at V4_RECEDE_MPH (5) or more for 20+ minutes, a projected hit is
      not believed (blue) — the storm is dissipating or sliding by whatever the motion field says.
   5. observed closing: when the nearest echo is within V4_NEAR_MI (15) and has been closing at V4_CLOSE_MPH (10) or more for
      20+ minutes, that observation outranks the projection: orange (red inside 10 min) with ETA = distance / observed rate.
      This is the "forming / light rain the projection under-reads" miss. The stalled-edge yellow also only applies when the
      projected arrival is more than 30 min out.
   6. the edge rules (stalled / slow / receding) describe the NEAREST echo; they only speak for the arrival when that echo is part of
      the storm projected to hit, and never override a big strong arrival (≥ 40 dBZ or ≥ 100 cells, which the projection gets right).
   Tunables via env: V4_GRAZE_MIN (12), V4_GRAZE_CELLS (6), V4_RESUME_MPH (5), V4_BLUE_MIN (75), V4_RECEDE_MPH (5), V4_NEAR_MI (15), V4_CLOSE_MPH (10). */
const V4 = process.argv.includes('--v4'), GRAZE_MIN = +(process.env.V4_GRAZE_MIN || 12), GRAZE_CELLS = +(process.env.V4_GRAZE_CELLS || 6), RESUME_MPH = +(process.env.V4_RESUME_MPH || 5), BLUE_MIN = +(process.env.V4_BLUE_MIN || 75), RECEDE_MPH = +(process.env.V4_RECEDE_MPH || 5), NEAR_MI = +(process.env.V4_NEAR_MI || 15), CLOSE_MPH = +(process.env.V4_CLOSE_MPH || 10), BLUE_NEAR_MI = +(process.env.V4_BLUE_NEAR_MI || 25);
let fizzleSeen4 = 0;
const SWEEP = [];   // name → settings; scored in the summary when --v4 is given
for (const blueNear of [0, 15, 25]) for (const blueMin of [75, 90]) for (const close of [10, 15])
  SWEEP.push({ name: `bn${blueNear}_bm${blueMin}_cl${close}`, blueNear, blueMin, close, near: NEAR_MI, grazeMin: GRAZE_MIN, grazeCells: GRAZE_CELLS, stallMin: 30, recede: RECEDE_MPH });
const fizzOld = (hit, p) => { const tr = p.trends.get(hit.blob); return !!(tr && tr.word === 'weakening' && tr.fizzleMin != null && tr.fizzleMin < hit.etaMin - 2); };
for (const t of times) {
  if (t - times[0] < 30 * 60000) continue;
  const p = pageRun(t), m = p.motion;
  { const ns = (m.regions || []).filter(r => r.seg && r.ok).length; if (ns) { segFrames++; segRegs += ns; } if (m.regionsUsed) regGrid[m.regionsUsed] = (regGrid[m.regionsUsed] || 0) + 1; }
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
  let fizz4 = false;
  if (p.res.hit) { const tr = p.trends.get(p.res.hit.blob); const now = !!(tr && tr.word === 'weakening' && tr.fizzleMin != null && tr.fizzleMin < p.res.hit.etaMin - 2); if (now) { fizz4 = true; fizzleSeen4 = t; } else if (fizzleSeen4 && t - fizzleSeen4 < 20 * 60000 && !(tr && tr.word === 'strengthening') && p.res.hit.maxDbz < 40 && !(edge && edge.rate >= RESUME_MPH)) fizz4 = true; }
  const hit = p.res.hit; let oldC = 'green', newC = 'green', v4C = 'green', etaNew = hit ? hit.etaMin : null, graze = false, etaV4 = null;
  const closingNear = edge && edge.rate >= CLOSE_MPH && nr && nr.distMi <= NEAR_MI;
  if (closingNear) { etaV4 = nr.distMi / edge.rate * 60; v4C = etaV4 <= 10 ? 'red' : 'orange'; }
  if (hit) {
    graze = (hit.endMin - hit.etaMin) < GRAZE_MIN && hit.cells < GRAZE_CELLS && hit.maxDbz < 40;
    const far = hit.distMi > 50;
    oldC = (fizzOld(hit, p) && hit.etaMin > 10 && hit.maxDbz < 40) ? 'yellow' : far ? 'blue' : hit.etaMin <= 10 ? 'red' : 'orange';
    const lowConf = DRY && hit.maxDbz < 35;
    if (fizz && hit.etaMin > 10 && hit.maxDbz < 40) newC = 'yellow';
    else if (!lowConf && edge && edge.verdict === 'stalled' && hit.maxDbz < 40 && hit.etaMin > 5) newC = 'yellow';
    else if (far && !lowConf) newC = 'blue';
    else if (lowConf) newC = 'yellow';
    else { if (edge && edge.verdict === 'slow' && edge.etaMin > hit.etaMin) etaNew = Math.min(120, edge.etaMin); newC = etaNew <= 10 ? 'red' : 'orange'; }
    // V4 column (an observed-closing verdict set above stands)
    const edgeApplies = edge && (p.res.nearestBlob == null || hit.blob == null || p.res.nearestBlob === hit.blob) && !(hit.maxDbz >= 40 || hit.cells >= 100);
    let eta4 = hit.etaMin; if (edgeApplies && edge.verdict === 'slow' && edge.etaMin > hit.etaMin) eta4 = Math.min(120, edge.etaMin);
    if (closingNear) { if (hit.etaMin < etaV4) { etaV4 = hit.etaMin; v4C = etaV4 <= 10 ? 'red' : 'orange'; } }
    else if (fizz4 && hit.etaMin > 10 && hit.maxDbz < 40) v4C = 'yellow';
    else if (!lowConf && edgeApplies && edge.verdict === 'stalled' && hit.etaMin > 30) v4C = 'yellow';
    else if (graze) v4C = 'blue';
    else if (edgeApplies && edge.rate <= -RECEDE_MPH && hit.etaMin > 10) v4C = 'blue';
    else if (far && !lowConf) v4C = 'blue';
    else if (lowConf) v4C = 'yellow';
    else if (eta4 > BLUE_MIN && !(nr && nr.distMi <= BLUE_NEAR_MI)) v4C = 'blue';
    else { etaV4 = eta4; v4C = eta4 <= 10 ? 'red' : 'orange'; }
  }
  // --sweep: the same V4 logic under several settings at once (rule evaluation is cheap; the frame work above is not)
  const sweep = {};
  if (V4) for (const cfg of SWEEP) {
    let c = 'green'; const cn = edge && edge.rate >= cfg.close && nr && nr.distMi <= cfg.near;
    if (cn) { const e = nr.distMi / edge.rate * 60; c = e <= 10 ? 'red' : 'orange'; }
    if (hit) {
      const ea = edge && (p.res.nearestBlob == null || hit.blob == null || p.res.nearestBlob === hit.blob) && !(hit.maxDbz >= 40 || hit.cells >= 100);
      let e4 = hit.etaMin; if (ea && edge.verdict === 'slow' && edge.etaMin > hit.etaMin) e4 = Math.min(120, edge.etaMin);
      const gz = (hit.endMin - hit.etaMin) < cfg.grazeMin && hit.cells < cfg.grazeCells && hit.maxDbz < 40;
      const lc = DRY && hit.maxDbz < 35, fr = hit.distMi > 50;
      if (cn) { if (hit.etaMin < nr.distMi / edge.rate * 60) c = hit.etaMin <= 10 ? 'red' : 'orange'; }
      else if (fizz4 && hit.etaMin > 10 && hit.maxDbz < 40) c = 'yellow';
      else if (!lc && ea && edge.verdict === 'stalled' && hit.etaMin > cfg.stallMin) c = 'yellow';
      else if (gz) c = 'blue';
      else if (ea && edge.rate <= -cfg.recede && hit.etaMin > 10) c = 'blue';
      else if (fr && !lc) c = 'blue';
      else if (lc) c = 'yellow';
      else if (e4 > cfg.blueMin && !(nr && nr.distMi <= cfg.blueNear)) c = 'blue';
      else c = e4 <= 10 ? 'red' : 'orange';
    }
    sweep[cfg.name] = c;
  }
  rows.push({ t, m, truth, dH, dS, eta: p.res.hit ? p.res.hit.etaMin : null, etaNew, etaV4: closingNear ? etaV4 : etaNew, oldC, newC, v4C, sweep, graze, closingNear, fizz, fizz4, hitCells: hit ? hit.cells : null, hitDur: hit ? hit.endMin - hit.etaMin : null, actMin, rainingNow: rainAt(frame(t)) >= thresh, edge, nearest: nr, maxDbz: p.res.hit ? p.res.hit.maxDbz : null });
  const eTxt = ` | ${oldC.padEnd(6)} → ${newC.padEnd(6)}` + (edge ? ` edge ${edge.rate.toFixed(0)} vs ${edge.closing.toFixed(0)} mph ${edge.verdict}` : '');
  console.log(`${clock(t)}  | ${m.ok ? (compass(m.headingDeg) + ' ' + m.headingDeg.toFixed(0) + '° ' + m.speedMph.toFixed(0) + ' mph').padEnd(18) + ('(' + (m.baselineMin || 0).toFixed(0) + ' min)').padEnd(10) : ('— ' + (m.reason || '')).padEnd(28)} ${m.global.conf.toFixed(2)} | ${lb ? (compass(lb.headingDeg) + ' ' + lb.headingDeg.toFixed(0) + '° ' + lb.speedMph.toFixed(0) + ' mph').padEnd(17) : ''.padEnd(17)} | ${ct ? (compass(ct.headingDeg) + ' ' + ct.headingDeg.toFixed(0) + '° ' + ct.speedMph.toFixed(0) + ' mph n=' + ct.n).padEnd(18) : ''.padEnd(18)} | ${dH == null ? '    ' : (dH >= 0 ? '+' : '') + dH.toFixed(0).padStart(3) + '°'} ${dS == null ? '     ' : (dS >= 0 ? '+' : '') + dS.toFixed(0).padStart(3) + ' '} | ${etaTxt} → ${actTxt}${eTxt}`);
}
const scored = rows.filter(r => r.dH != null);
if (scored.length) {
  const absH = scored.map(r => Math.abs(r.dH)).sort((a, b) => a - b), absS = scored.map(r => Math.abs(r.dS)).sort((a, b) => a - b);
  console.log(`\nmotion field: segments active in ${segFrames} frames (${segRegs} segment vectors), region grid use ${JSON.stringify(regGrid)}`);
console.log(`\nheading: median |error| ${absH[absH.length >> 1].toFixed(0)}°, 90th pct ${absH[Math.floor(absH.length * 0.9)].toFixed(0)}°, reversed (>120°) in ${scored.filter(r => Math.abs(r.dH) > 120).length}/${scored.length}`);
  console.log(`speed:   median |error| ${absS[absS.length >> 1].toFixed(0)} mph, 90th pct ${absS[Math.floor(absS.length * 0.9)].toFixed(0)} mph`);
}
const etaRows = rows.filter(r => !r.rainingNow && r.eta != null && r.actMin != null);
if (etaRows.length) { const e = etaRows.map(r => r.eta - r.actMin); console.log(`ETA: ${etaRows.length} forecasts with a real arrival; mean bias ${(e.reduce((a, b) => a + b, 0) / e.length).toFixed(0)} min, median |error| ${e.map(Math.abs).sort((a, b) => a - b)[e.length >> 1].toFixed(0)} min`); }

const alarm = c => c === 'orange' || c === 'red';
const score = key => { const fa = rows.filter(r => !r.rainingNow && alarm(r[key]) && (r.actMin == null || r.actMin > 90)).length; const miss = rows.filter(r => !r.rainingNow && !alarm(r[key]) && r.actMin != null && r.actMin <= 60).length; const red = rows.filter(r => !r.rainingNow && r[key] === 'red' && (r.actMin == null || r.actMin > 30)).length; return `${fa} false alarms (orange/red, no rain within 90 min), ${red} false reds, ${miss} misses (rain within 60 min, no alarm)`; };
console.log('old rules: ' + score('oldC'));
console.log('new rules: ' + score('newC') + (DRY ? '  [with NWS POP < 10% / dry air]' : '  [without forecast info; add --dry to apply the 1% POP the forecast gave that night]'));
if (V4 && process.argv.includes('--why')) {
  const alarm = c => c === 'orange' || c === 'red';
  console.log('\n-- misses (rain within 60 min, no alarm) under v4 --');
  for (const r of rows.filter(r => !r.rainingNow && r.actMin != null && r.actMin <= 60 && !alarm(r.v4C)))
    console.log(`${clock(r.t)} rain in ${Math.round(r.actMin)} min | old ${r.oldC} new ${r.newC} v4 ${r.v4C} | hit ${r.eta == null ? 'none' : Math.round(r.eta) + ' min ' + r.maxDbz + ' dBZ ' + r.hitCells + ' cells ' + Math.round(r.hitDur) + ' min long'} | fizz ${r.fizz}/${r.fizz4} | edge ${r.edge ? r.edge.verdict + ' ' + r.edge.rate.toFixed(0) + '/' + r.edge.closing.toFixed(0) : '-'} | nearest ${r.nearest ? Math.round(r.nearest.distMi) + ' mi ' + Math.round(r.nearest.dbz) + ' dBZ' : '-'}`);
  console.log('-- false alarms under v4 --');
  for (const r of rows.filter(r => !r.rainingNow && alarm(r.v4C) && (r.actMin == null || r.actMin > 90)))
    console.log(`${clock(r.t)} ${r.actMin == null ? 'no rain' : 'rain in ' + Math.round(r.actMin)} | hit ${Math.round(r.eta)} min ${r.maxDbz} dBZ ${r.hitCells} cells ${Math.round(r.hitDur)} min long | edge ${r.edge ? r.edge.verdict + ' ' + r.edge.rate.toFixed(0) + '/' + r.edge.closing.toFixed(0) : '-'} | nearest ${r.nearest ? Math.round(r.nearest.distMi) + ' mi' : '-'}`);
}
if (V4) { console.log('sweep:'); for (const cfg of SWEEP) { const key = cfg.name; const alarm = c => c === 'orange' || c === 'red'; const fa = rows.filter(r => !r.rainingNow && alarm(r.sweep[key]) && (r.actMin == null || r.actMin > 90)).length, miss = rows.filter(r => !r.rainingNow && !alarm(r.sweep[key]) && r.actMin != null && r.actMin <= 60).length; console.log(`  ${key}: ${fa} false alarms, ${miss} misses`); } }
if (V4) console.log('v4 rules:  ' + score('v4C') + `  [fizzle escape ${RESUME_MPH} mph; graze < ${GRAZE_MIN} min & < ${GRAZE_CELLS} cells (${rows.filter(r => r.graze).length} rows); blue beyond ${BLUE_MIN} min; receding ${RECEDE_MPH} mph]`);

/* ---- ground truth from gauges, when the recording carries them ----
   gauge-madis.jsonl: one MADIS report per line {t, id, lat, lon, distMi, pcp1hIn, rateInHr, wx} (backend verify worker / backfill);
   gauge.jsonl: the point's own airport {t, station, precipMm, wx, present}. A report is "wet" when its hourly total is ≥ 0.01 in or
   present weather says rain/drizzle/thunder/snow. The hour it covers ends at t, so rain "arrived" somewhere inside (t − 60 min, t];
   we take the midpoint. Radar truth (≥20 dBZ at the centre) and gauge truth disagree exactly where virga and fine drizzle live. */
const GAUGE_MI = +(process.argv.find(a => a.startsWith('--gauge-mi='))?.slice(11) || 5);
function loadGauges() {
  const out = [];
  for (const [file, kind] of [['gauge-madis.jsonl', 'madis'], ['gauge.jsonl', 'airport']]) {
    const fp = path.join(dir, file); if (!fs.existsSync(fp)) continue;
    for (const line of fs.readFileSync(fp, 'utf8').split('\n')) {
      if (!line.trim()) continue;
      try {
        const g = JSON.parse(line);
        const dist = kind === 'madis' ? g.distMi : 0;
        const inches = kind === 'madis' ? g.pcp1hIn : g.precipMm != null ? g.precipMm / 25.4 : null;
        const wx = (kind === 'madis' ? g.wx : (g.present || g.wx)) || '';
        out.push({ t: g.t, id: g.id || g.station, dist, inches, wx, wet: (inches != null && inches >= 0.01) || /RA|DZ|TS|SN|UP/.test(wx), kind });
      } catch (e) { }
    }
  }
  return out;
}
const gauges = loadGauges();
if (gauges.length) {
  const near = gauges.filter(g => g.dist <= GAUGE_MI);
  const ids = new Set(near.map(g => g.id)), wet = near.filter(g => g.wet).sort((a, b) => a.t - b.t);
  const wetMid = wet.map(g => g.t - 30 * 60000);   // midpoint of the hour the report covers
  const gaugeActual = t => { const w = wetMid.find(x => x >= t - 30 * 60000); return w == null ? null : Math.max(0, (w - t) / 60000); };
  const gaugeNow = t => wet.some(g => g.t >= t && g.t - 60 * 60000 <= t);
  console.log(`\ngauges within ${GAUGE_MI} mi: ${ids.size} stations, ${near.length} reports, ${wet.length} wet` + (wet.length ? `; first wet report ${clock(wet[0].t)} (${wet[0].id}${wet[0].inches != null ? ' ' + wet[0].inches.toFixed(2) + ' in' : ''}${wet[0].wx ? ' ' + wet[0].wx : ''})` : ''));
  const radarWetHours = new Set(rainSeries.filter(r => r.dbz >= thresh).map(r => Math.floor(r.t / 3600000)));
  const gaugeWetHours = new Set(wet.map(g => Math.floor((g.t - 1) / 3600000)));
  const allHours = [...new Set([...radarWetHours, ...gaugeWetHours])].sort();
  if (allHours.length) console.log('hour-by-hour (UTC) radar≥20 dBZ vs gauge wet: ' + allHours.map(h => new Date(h * 3600000).toISOString().slice(11, 13) + 'Z ' + (radarWetHours.has(h) ? 'R' : '-') + (gaugeWetHours.has(h) ? 'G' : '-')).join('  ') + '   (R- = radar only → virga/evaporation; -G = gauge only → light rain radar missed)');
  const gScore = key => { const fa = rows.filter(r => !gaugeNow(r.t) && alarm(r[key]) && (gaugeActual(r.t) == null || gaugeActual(r.t) > 90)).length; const miss = rows.filter(r => !gaugeNow(r.t) && !alarm(r[key]) && gaugeActual(r.t) != null && gaugeActual(r.t) <= 60).length; return `${fa} false alarms, ${miss} misses (gauge truth, ±30 min hour-midpoint)`; };
  console.log('old rules vs gauges: ' + gScore('oldC'));
  console.log('new rules vs gauges: ' + gScore('newC'));
  if (V4) console.log('v4 rules vs gauges:  ' + gScore('v4C'));
} else console.log('\n(no gauge files in the recording — gauge-madis.jsonl arrives with backend builds from 9 Nov on, or via /admin/verify/madis backfill)');
