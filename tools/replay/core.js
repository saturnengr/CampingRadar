/* Pure functions (no DOM) for reading radar pixels, tracking storm motion
   and predicting whether rain will reach a point. Unit-tested separately. */

/* Reflectivity colour scale used by BOTH NOAA MRMS (mapservices.weather.noaa.gov) and the
   IEM composite (same scale as radar.weather.gov): grey -> blue-grey -> blue -> cyan ->
   bright green (light rain, 20 dBZ) -> dark green -> olive -> yellow -> orange -> red.
   Stops measured from the live imagery; colours between stops are interpolated. */
const DBZ_RAMP = [
  [-10, 207, 209, 175], [0, 154, 160, 181], [10, 67, 94, 159], [15, 111, 214, 232],
  [20, 14, 214, 20], [30, 9, 94, 9], [30.5, 34, 107, 8], [40, 255, 226, 0],
  [45, 255, 177, 0], [50, 255, 0, 0], [60, 193, 0, 0], [65, 255, 0, 255], [70, 150, 80, 200], [75, 255, 255, 255]];

function colorToDbz(r, g, b, a) {
  if (a !== undefined && a < 40) return -99;
  let best = -99, bd = Infinity;
  for (let i = 0; i < DBZ_RAMP.length - 1; i++) {     // nearest point on the colour polyline
    const [d0, r0, g0, b0] = DBZ_RAMP[i], [d1, r1, g1, b1] = DBZ_RAMP[i + 1];
    const vr = r1 - r0, vg = g1 - g0, vb = b1 - b0, len = vr * vr + vg * vg + vb * vb;
    let t = len ? ((r - r0) * vr + (g - g0) * vg + (b - b0) * vb) / len : 0;
    t = Math.max(0, Math.min(1, t));
    const dist = (r - r0 - t * vr) ** 2 + (g - g0 - t * vg) ** 2 + (b - b0 - t * vb) ** 2;
    if (dist < bd) { bd = dist; best = d0 + t * (d1 - d0); }
  }
  return bd > 45 * 45 ? -99 : Math.round(best * 2) / 2;
}

// RGBA pixels -> grid of max dBZ per cell (cell = cellPx x cellPx pixels)
function gridFromRGBA(px, w, h, cellPx) {
  const gw = Math.floor(w / cellPx), gh = Math.floor(h / cellPx);
  const d = new Float32Array(gw * gh).fill(-99);
  const cache = new Map();
  for (let y = 0; y < gh * cellPx; y++) {
    const row = ((y / cellPx) | 0) * gw;
    for (let x = 0; x < gw * cellPx; x++) {
      const i = (y * w + x) * 4;
      if (px[i + 3] < 40) continue;
      const key = (px[i] << 16) | (px[i + 1] << 8) | px[i + 2];
      let v = cache.get(key);
      if (v === undefined) { v = colorToDbz(px[i], px[i + 1], px[i + 2], 255); cache.set(key, v); }
      const gi = row + ((x / cellPx) | 0);
      if (v > d[gi]) d[gi] = v;
    }
  }
  return { w: gw, h: gh, d };
}

function downsampleMax(g, f) {
  const w = Math.floor(g.w / f), h = Math.floor(g.h / f);
  const d = new Float32Array(w * h).fill(-99);
  for (let y = 0; y < h * f; y++) for (let x = 0; x < w * f; x++) {
    const v = g.d[y * g.w + x], i = ((y / f) | 0) * w + ((x / f) | 0);
    if (v > d[i]) d[i] = v;
  }
  return { w, h, d };
}

// remove isolated single cells (radar speckle)
function despeckle(g, thresh) {
  const d = new Float32Array(g.d);
  for (let y = 0; y < g.h; y++) for (let x = 0; x < g.w; x++) {
    const i = y * g.w + x;
    if (g.d[i] < thresh) continue;
    let n = 0;
    for (let dy = -1; dy <= 1 && !n; dy++) for (let dx = -1; dx <= 1; dx++) {
      if (!dx && !dy) continue;
      const xx = x + dx, yy = y + dy;
      if (xx < 0 || yy < 0 || xx >= g.w || yy >= g.h) continue;
      if (g.d[yy * g.w + xx] >= thresh) { n++; break; }
    }
    if (!n) d[i] = -99;
  }
  return { w: g.w, h: g.h, d };
}

function intensityField(g) {
  const a = new Float32Array(g.d.length);
  for (let i = 0; i < a.length; i++) { const v = g.d[i]; a[i] = v >= 15 ? Math.min(v, 55) - 15 : 0; }
  return a;
}

function countRain(I, w, x0, y0, x1, y1) {
  let n = 0;
  for (let y = y0; y <= y1; y++) for (let x = x0; x <= x1; x++) if (I[y * w + x] > 0) n++;
  return n;
}

// Block matching: find shift (u,v) such that B(x,y) ~ A(x-u, y-v) within the box.
function bestShift(A, B, w, x0, y0, x1, y1, S) {
  // Normalised cross-correlation of the newer field B with the older field A shifted by (u, v). Plain absolute difference was
  // biased: when a rain mass sits near the edge of the window, shifts that slide it *out* of the window score well simply
  // because less unmatched rain remains, which can point the motion the wrong way. NCC compares shape, not mass.
  const n = 2 * S + 1, scores = new Float64Array(n * n);
  let bb = 0; for (let y = y0; y <= y1; y++) { const rb = y * w; for (let x = x0; x <= x1; x++) bb += B[rb + x] * B[rb + x]; }
  if (bb <= 0) return { u: 0, v: 0, conf: 0, edge: false };
  let best = -Infinity, bu = 0, bv = 0, sum = 0;
  for (let v = -S; v <= S; v++) for (let u = -S; u <= S; u++) {
    let ab = 0, aa = 0;
    for (let y = y0; y <= y1; y++) {
      const rb = y * w, ra = (y - v) * w - u;
      for (let x = x0; x <= x1; x++) { const a = A[ra + x]; if (a > 0) { aa += a * a; ab += a * B[rb + x]; } }
    }
    const sc = aa > 0 ? ab / Math.sqrt(aa * bb) : 0;
    scores[(v + S) * n + (u + S)] = sc; sum += sc;
    if (sc > best || (sc === best && Math.abs(u) + Math.abs(v) < Math.abs(bu) + Math.abs(bv))) { best = sc; bu = u; bv = v; }
  }
  const mean = sum / (n * n);
  const conf = best > 0 ? Math.max(0, (best - mean) / Math.max(1e-6, 1 - mean)) * best : 0;   // how much the peak stands out, scaled by how good the match is
  const edge = Math.abs(bu) === S || Math.abs(bv) === S;
  let du = 0, dv = 0;   // sub-cell refinement (parabola fit on the peak)
  if (!edge) {
    const at = (u, v) => scores[(v + S) * n + (u + S)];
    const em = at(bu - 1, bv), ep = at(bu + 1, bv), den = em - 2 * best + ep;
    if (den < 0) du = Math.max(-0.5, Math.min(0.5, (em - ep) / (2 * den)));
    const fm = at(bu, bv - 1), fp = at(bu, bv + 1), den2 = fm - 2 * best + fp;
    if (den2 < 0) dv = Math.max(-0.5, Math.min(0.5, (fm - fp) / (2 * den2)));
  }
  return { u: bu + du, v: bv + dv, conf, edge };
}

/* Storm motion between an older grid gA and newer grid gB, dtMin apart.
   Velocities are km/min, x = east, y = south (image orientation). */
function centroidOf(F, w, x0, y0, x1, y1) {
  let sx = 0, sy = 0, n = 0;
  for (let y = y0; y <= y1; y++) for (let x = x0; x <= x1; x++) { const v = F[y * w + x]; if (v > 0) { sx += v * x; sy += v * y; n += v; } }
  return n > 0 ? { x: sx / n, y: sy / n, n } : null;
}
function estimateMotion(gA, gB, dtMin, cellKm, opts) {
  opts = opts || {};
  const maxKmh = opts.maxKmh || 110, minCells = opts.minCells || 10, minConf = opts.minConf || 0.25;
  const w = gB.w, h = gB.h;
  const S = Math.max(2, Math.ceil((maxKmh / 60) * dtMin / cellKm));
  const out = { ok: false, regions: [], dtMin, S, global: { vx: 0, vy: 0, conf: 0 }, speedMph: 0, headingDeg: 0, regionSize: 1 };
  const x0 = S, y0 = S, x1 = w - 1 - S, y1 = h - 1 - S;
  if (x1 - x0 < 8 || y1 - y0 < 8) { out.reason = 'domain too small'; return out; }
  const A = intensityField(gA), B = intensityField(gB);
  const cA = countRain(A, w, x0, y0, x1, y1), cB = countRain(B, w, x0, y0, x1, y1);
  if (cA < minCells || cB < minCells) { out.reason = 'too little rain to track'; return out; }
  const toV = u => u * cellKm / dtMin;
  const g = bestShift(A, B, w, x0, y0, x1, y1, S);
  out.global = { vx: toV(g.u), vy: toV(g.v), conf: g.conf };
  // Sanity check against the rain's centre of mass: if the mass clearly moved one way and the match says the opposite, trust neither.
  const ca = centroidOf(A, w, x0, y0, x1, y1), cb = centroidOf(B, w, x0, y0, x1, y1);
  if (ca && cb) {
    const mx = cb.x - ca.x, my = cb.y - ca.y, md = Math.hypot(mx, my);
    if (md >= 2.5 && Math.hypot(g.u, g.v) >= 1) { const cos = (mx * g.u + my * g.v) / (md * Math.hypot(g.u, g.v)); if (cos < -0.34) { out.global.conf *= 0.4; out.centroidDisagrees = true; } }
    out.centroid = { dx: mx, dy: my };
  }
  const gSpeed = Math.hypot(out.global.vx, out.global.vy) * 60;
  out.ok = out.global.conf >= minConf && !g.edge && gSpeed <= maxKmh;
  if (!out.ok) out.reason = g.edge ? 'motion off scale' : 'no clear motion';
  // Regional refinement grid. 3×3 by default; opts.regions fixes it (4 or 5), opts.regionsAuto picks a finer grid when rain covers
  // a large share of the window (big systems carry enough texture to support it; small storms do not).
  let nx = opts.regions || 3;
  if (opts.regionsAuto) { const cov = cB / ((x1 - x0 + 1) * (y1 - y0 + 1)); nx = cov >= 0.2 ? 5 : cov >= 0.1 ? 4 : 3; }
  const ny = nx, rw = (x1 - x0 + 1) / nx, rh = (y1 - y0 + 1) / ny;
  out.regionSize = Math.max(rw, rh); out.regionsUsed = nx;
  for (let j = 0; j < ny; j++) for (let i = 0; i < nx; i++) {
    const rx0 = x0 + Math.round(i * rw), rx1 = x0 + Math.round((i + 1) * rw) - 1;
    const ry0 = y0 + Math.round(j * rh), ry1 = y0 + Math.round((j + 1) * rh) - 1;
    const reg = { cx: (rx0 + rx1) / 2, cy: (ry0 + ry1) / 2, ok: false, vx: 0, vy: 0, conf: 0 };
    if (countRain(A, w, rx0, ry0, rx1, ry1) >= minCells && countRain(B, w, rx0, ry0, rx1, ry1) >= minCells) {
      const r = bestShift(A, B, w, rx0, ry0, rx1, ry1, S);
      reg.vx = toV(r.u); reg.vy = toV(r.v); reg.conf = r.conf;
      const sp = Math.hypot(reg.vx, reg.vy) * 60;
      const devKmh = Math.hypot(reg.vx - out.global.vx, reg.vy - out.global.vy) * 60;
      // a region only overrides the whole-window motion when it is at least as sure and broadly agrees with it (broad, featureless
      // rain gives flat correlation surfaces and random-looking local vectors otherwise)
      const gsp = Math.hypot(out.global.vx, out.global.vy) * 60;
      const cosA = gsp > 8 && sp > 8 ? (reg.vx * out.global.vx + reg.vy * out.global.vy) / (Math.hypot(reg.vx, reg.vy) * Math.hypot(out.global.vx, out.global.vy)) : 1;
      reg.ok = r.conf >= Math.max(minConf, 0.7 * out.global.conf) && !r.edge && sp <= maxKmh && (!out.ok || (devKmh <= 40 && cosA >= 0.5));
    }
    out.regions.push(reg);
  }
  // Long lines (opts.segments): a blob whose major axis is longer than opts.segMinKm is split along that axis into pieces of about
  // opts.segKm, and each piece gets its own match. A line whose ends move at different speeds, or that is pivoting, then gets a
  // velocity field that bends with it instead of one that only varies at the fixed-grid scale.
  if (opts.segments) {
    const segKm = opts.segKm || 50, segMinKm = opts.segMinKm || 100, segCells = segKm / cellKm;
    const lb = labelBlobs(gB, opts.thresh || 20, 3);
    const big = lb.blobs.filter(b => !b.tiny && b.n * cellKm * cellKm >= 800).sort((a, b) => b.n - a.n).slice(0, 4);
    for (const b of big) {
      // principal axis of the blob's cells
      let sxx = 0, syy = 0, sxy = 0; const pts = [];
      for (let i = 0; i < lb.labels.length; i++) if (lb.labels[i] === b.id) { const x = i % w, y = (i / w) | 0; pts.push([x, y]); const dx = x - b.cx, dy = y - b.cy; sxx += dx * dx; syy += dy * dy; sxy += dx * dy; }
      sxx /= pts.length; syy /= pts.length; sxy /= pts.length;
      const th = 0.5 * Math.atan2(2 * sxy, sxx - syy), ux = Math.cos(th), uy = Math.sin(th);
      let pmin = Infinity, pmax = -Infinity; for (const [x, y] of pts) { const p = (x - b.cx) * ux + (y - b.cy) * uy; if (p < pmin) pmin = p; if (p > pmax) pmax = p; }
      const lenKm = (pmax - pmin) * cellKm; if (lenKm < segMinKm) continue;
      const nseg = Math.max(2, Math.round((pmax - pmin) / segCells)), segLen = (pmax - pmin) / nseg;
      for (let k = 0; k < nseg; k++) {
        const p0 = pmin + k * segLen, p1 = p0 + segLen; let sx0 = Infinity, sy0 = Infinity, sx1 = -Infinity, sy1 = -Infinity, n = 0, mx = 0, my = 0;
        for (const [x, y] of pts) { const p = (x - b.cx) * ux + (y - b.cy) * uy; if (p < p0 || p >= p1) continue; n++; mx += x; my += y; if (x < sx0) sx0 = x; if (x > sx1) sx1 = x; if (y < sy0) sy0 = y; if (y > sy1) sy1 = y; }
        if (n < minCells) continue;
        const pad = Math.ceil(S * 0.5), rx0 = Math.max(x0, sx0 - pad), rx1 = Math.min(x1, sx1 + pad), ry0 = Math.max(y0, sy0 - pad), ry1 = Math.min(y1, sy1 + pad);
        if (rx1 - rx0 < 4 || ry1 - ry0 < 4) continue;
        const r = bestShift(A, B, w, rx0, ry0, rx1, ry1, S), seg = { cx: mx / n, cy: my / n, vx: toV(r.u), vy: toV(r.v), conf: r.conf, ok: false, seg: true, size: Math.max(rx1 - rx0, ry1 - ry0) };
        const sp = Math.hypot(seg.vx, seg.vy) * 60, devKmh = Math.hypot(seg.vx - out.global.vx, seg.vy - out.global.vy) * 60, gsp = Math.hypot(out.global.vx, out.global.vy) * 60;
        const cosA = gsp > 8 && sp > 8 ? (seg.vx * out.global.vx + seg.vy * out.global.vy) / (sp * gsp) : 1;
        seg.ok = r.conf >= Math.max(minConf, 0.7 * out.global.conf) && !r.edge && sp <= maxKmh && (!out.ok || (devKmh <= 40 && cosA >= 0.5));
        out.regions.push(seg);
      }
    }
  }
  if (!out.ok && out.regions.some(r => r.ok)) {   // global failed but some regions are solid
    const rs = out.regions.filter(r => r.ok);
    out.global = { vx: rs.reduce((s, r) => s + r.vx, 0) / rs.length, vy: rs.reduce((s, r) => s + r.vy, 0) / rs.length, conf: minConf };
    out.ok = true; delete out.reason;
  }
  out.speedMph = Math.hypot(out.global.vx, out.global.vy) * 60 * 0.621371;
  out.headingDeg = (Math.atan2(out.global.vx, -out.global.vy) * 180 / Math.PI + 360) % 360;
  return out;
}

// Velocity at a fine-grid cell, blended from regional vectors (inverse distance)
function makeVelocityField(motion, fineToCoarse) {
  const zero = { vx: 0, vy: 0 };
  if (!motion || !motion.ok) return () => zero;
  const regs = motion.regions.filter(r => r.ok);
  const g = { vx: motion.global.vx, vy: motion.global.vy };
  if (!regs.length) return () => g;
  const s2 = motion.regionSize * motion.regionSize * 0.25;
  return (x, y) => {
    const X = x / fineToCoarse, Y = y / fineToCoarse;
    let sw = Math.max(0.3, motion.global.conf) / s2, vx = g.vx * sw, vy = g.vy * sw;     // pull toward the whole-window motion, stronger when it is confident
    for (const r of regs) {
      const rs2 = r.size ? r.size * r.size * 0.25 : s2;   // line segments carry their own (smaller) footprint
      const dx = X - r.cx, dy = Y - r.cy, w = r.conf / (dx * dx + dy * dy + rs2);
      sw += w; vx += w * r.vx; vy += w * r.vy;
    }
    return { vx: vx / sw, vy: vy / sw };
  };
}

function bearingDeg(e, n) { return (Math.atan2(e, n) * 180 / Math.PI + 360) % 360; }

/* Project every rain cell forward along its motion and see if it passes over
   the user (cx, cy in fine-cell units). Returns arrival estimate + what's passing. */
// Nearest echo of any strength (the map draws everything from about 5 dBZ) — used so the status never says "clear" when the map shows returns.
function nearestEcho(grid, cellKm, cx, cy, thresh, maxRing) {
  const cellMi = cellKm * 0.621371; let best = null;
  for (let y = 0; y < grid.h; y++) for (let x = 0; x < grid.w; x++) {
    const d = grid.d[y * grid.w + x]; if (d < thresh) continue;
    const e = (x + 0.5 - cx) * cellMi, n = -(y + 0.5 - cy) * cellMi, dist = Math.hypot(e, n);
    if (dist <= maxRing && (!best || dist < best.distMi)) best = { distMi: dist, bearing: bearingDeg(e, n), dbz: d };
  }
  return best;
}
// Hail-capable cores: cells at or above thresh dBZ within maxRing miles, merged within ~3 mi, with the peak dBZ and a lat/lon.
function hailCores(grid, cellKm, cx, cy, thresh, maxRing, loc) {
  const cellMi = cellKm * 0.621371, cores = [];
  for (let y = 0; y < grid.h; y++) for (let x = 0; x < grid.w; x++) {
    const d = grid.d[y * grid.w + x]; if (d < thresh) continue;
    const e = (x + 0.5 - cx) * cellMi, n = -(y + 0.5 - cy) * cellMi, dist = Math.hypot(e, n); if (dist > maxRing) continue;
    let hit = null; for (const c of cores) if (Math.hypot(c.e - e, c.n - n) < 3) { hit = c; break; }
    if (hit) { hit.cells++; if (d > hit.dbz) { hit.dbz = d; hit.e = e; hit.n = n; hit.distMi = dist; hit.bearing = bearingDeg(e, n); } }
    else cores.push({ e, n, dbz: d, cells: 1, distMi: dist, bearing: bearingDeg(e, n) });
  }
  for (const c of cores) { c.lat = loc.lat + c.n * MI / 111320; c.lon = loc.lon + c.e * MI / (111320 * Math.cos(loc.lat * Math.PI / 180)); }
  return cores.filter(c => c.cells >= 2).sort((a, b) => a.distMi - b.distMi);
}
function predictRain(grid, cellKm, cx, cy, vel, opts, labels) {
  const thresh = opts.thresh, horizon = opts.horizonMin || 120, maxRing = opts.maxRingMi || 50;
  const cellMi = cellKm * 0.621371, R = opts.hitRadiusMi + 0.7 * cellMi, R2 = R * R;
  const rings = { 2: false, 5: false, 10: false, 30: false, 50: false, 100: false, 120: false, 150: false };
  const ringR = [2, 5, 10, 30, 50, 100, 120, 150], farRing = Math.max(opts.maxRingMi || 50, 150);
  let nearest = null, passing = null, hits = 0, eta = Infinity, end = 0, maxDbz = -99, from = null, hitBlob = null, nearestBlob = null, hitDist = null;
  const hitBlobCells = new Map();
  for (let y = 0; y < grid.h; y++) for (let x = 0; x < grid.w; x++) {
    const d = grid.d[y * grid.w + x];
    if (d < thresh) continue;
    const e = (x + 0.5 - cx) * cellMi, n = -(y + 0.5 - cy) * cellMi;
    const dist = Math.hypot(e, n);
    if (dist <= farRing) for (const r of ringR) if (dist <= r) rings[r] = true;
    if (dist <= maxRing) {
      if (!nearest || dist < nearest.distMi) { nearest = { distMi: dist, bearing: bearingDeg(e, n), dbz: d }; nearestBlob = labels ? labels[y * grid.w + x] : null; }
    }
    const v = vel(x, y), ve = v.vx * 0.621371, vn = -v.vy * 0.621371;   // mi/min
    const a = ve * ve + vn * vn;
    let tIn = null, tOut = null;
    if (dist * dist <= R2) {
      tIn = 0;
      if (a < 1e-9) tOut = horizon;
      else { const b = 2 * (e * ve + n * vn), c = dist * dist - R2; tOut = Math.min(horizon, (-b + Math.sqrt(b * b - 4 * a * c)) / (2 * a)); }
    } else if (a > 1e-9) {
      const b = 2 * (e * ve + n * vn), c = dist * dist - R2, disc = b * b - 4 * a * c;
      if (disc >= 0) {
        const sq = Math.sqrt(disc), t1 = (-b - sq) / (2 * a), t2 = (-b + sq) / (2 * a);
        if (t2 >= 0 && t1 <= horizon) { tIn = Math.max(0, t1); tOut = Math.min(horizon, t2); }
      }
    }
    if (tIn !== null) {
      hits++;
      if (tIn < eta) { eta = tIn; from = bearingDeg(e, n); hitDist = dist; hitBlob = labels ? labels[y * grid.w + x] : null; }
      if (labels) { const lb = labels[y * grid.w + x]; hitBlobCells.set(lb, (hitBlobCells.get(lb) || 0) + 1); }
      if (tOut > end) end = tOut;
      if (d > maxDbz) maxDbz = d;
    } else if (dist <= maxRing) {
      const t = a > 1e-9 ? Math.max(0, Math.min(horizon, -(e * ve + n * vn) / a)) : 0;
      const pe = e + ve * t, pn = n + vn * t;
      if (!passing || dist < passing.distMi)
        passing = { distMi: dist, bearing: bearingDeg(e, n), passBearing: bearingDeg(pe, pn), closestMi: Math.hypot(pe, pn) };
    }
  }
  const hit = hits >= (opts.minHitCells || 2) ? { etaMin: eta, endMin: end, maxDbz, fromBearing: from, distMi: hitDist, cells: hits, blob: hitBlob, blobCells: hitBlobCells } : null;
  return { rings, nearest, passing, hit, nearestBlob };
}
/* ---- intensity trends: track each storm cell across frames ---- */
// Connected rain blobs (8-neighbour) above thresh; returns labels per cell and per-blob stats.
function labelBlobs(grid, thresh, minCells) {
  minCells = minCells || 3;
  const { w, h, d } = grid, labels = new Int32Array(w * h), blobs = [];
  const stack = [];
  for (let i = 0; i < d.length; i++) {
    if (d[i] < thresh || labels[i]) continue;
    const id = blobs.length + 1; let n = 0, peak = -99, vol = 0, sx = 0, sy = 0, edge = false;
    labels[i] = id; stack.push(i);
    while (stack.length) {
      const k = stack.pop(), x = k % w, y = (k / w) | 0, v = d[k];
      n++; vol += v - thresh + 1; sx += x; sy += y; if (v > peak) peak = v; if (x < 2 || y < 2 || x >= w - 2 || y >= h - 2) edge = true;
      for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) {
        if (!dx && !dy) continue;
        const xx = x + dx, yy = y + dy; if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
        const kk = yy * w + xx; if (d[kk] >= thresh && !labels[kk]) { labels[kk] = id; stack.push(kk); }
      }
    }
    blobs.push({ id, n, peak, vol, cx: sx / n, cy: sy / n, edge });
  }
  // drop tiny blobs from the label map (they still count as rain for arrival, just not for trends)
  for (const b of blobs) if (b.n < minCells) b.tiny = true;
  return { labels, blobs };
}
// For each blob in the newest frame, find its earlier self: back-project the centroid by the motion and pick the nearest blob of comparable size.
function matchBlobs(newB, oldB, dtMin, vel, cellKm) {
  const out = new Map();
  for (const b of newB.blobs) {
    if (b.tiny) continue;
    const v = vel(Math.round(b.cx), Math.round(b.cy));
    const px = b.cx - v.vx * dtMin / cellKm, py = b.cy - v.vy * dtMin / cellKm;   // where it should have been
    const reach = Math.max(6, 1.5 * Math.sqrt(b.n / Math.PI));
    let best = null, bd = Infinity;
    for (const o of oldB.blobs) {
      if (o.tiny) continue;
      const dd = Math.hypot(o.cx - px, o.cy - py);
      if (dd < reach + Math.sqrt(o.n / Math.PI) && dd < bd) { bd = dd; best = o; }
    }
    if (best) out.set(b.id, best);
  }
  return out;
}
/* Trend for every blob in the newest frame from up to two earlier frames.
   word: 'strengthening' | 'weakening' | 'steady' | 'new' ; fizzleMin = minutes until peak drops below thresh (weakening only). */
function blobTrends(frames, dts, vel, cellKm, thresh) {
  const newest = frames[frames.length - 1], trends = new Map();
  const matches = frames.slice(0, -1).map((f, i) => matchBlobs(newest, f, dts[i], vel, cellKm));
  for (const b of newest.blobs) {
    if (b.tiny) continue;
    const pts = [{ t: 0, vol: b.vol, peak: b.peak, n: b.n }];
    matches.forEach((m, i) => { const o = m.get(b.id); if (o) pts.push({ t: -dts[i], vol: o.vol, peak: o.peak, n: o.n }); });
    if (pts.length < 2) { trends.set(b.id, { word: 'new', pts: 1 }); continue; }
    const oldest = pts[pts.length - 1], span = -oldest.t;
    const volRatio = b.vol / Math.max(1, oldest.vol), peakDelta = b.peak - oldest.peak, areaRatio = b.n / Math.max(1, oldest.n);
    let word = 'steady';
    if ((volRatio >= 1.35 && areaRatio >= 1.1) || peakDelta >= 7) word = 'strengthening';
    else if ((volRatio <= 0.7 && areaRatio <= 0.9) || peakDelta <= -7) word = 'weakening';
    let fizzleMin = null;
    if (word === 'weakening') {
      const peakRate = peakDelta / span;                       // dBZ per minute (negative)
      const volRate = Math.log(Math.max(0.05, volRatio)) / span;   // exponential decay rate
      const tPeak = peakRate < -0.05 ? (thresh - b.peak) / peakRate : Infinity;
      const tVol = volRate < -0.005 ? Math.log(0.15) / volRate : Infinity;   // down to 15% of current volume
      fizzleMin = Math.min(tPeak, tVol);
      if (!isFinite(fizzleMin)) fizzleMin = null;
    }
    trends.set(b.id, { word, volRatio, areaRatio, peakDelta, spanMin: span, fizzleMin, pts: pts.length });
  }
  return trends;
}
/* ---- edge check: is the nearest rain really closing at the rate the storm motion implies? ----
   Broad shields evaporate at their leading edge: the cells inside move at 35 mph while the rain's edge creeps or even retreats.
   hist = [{t, d (mi), b (bearing)}] of the nearest rain over the last hour. Returns null until there is 20 min of history. */
function edgeCheck(hist, now, nearest, motion) {
  if (!nearest || !motion || !motion.ok) return null;
  const same = hist.filter(h => now - h.t <= 40 * 60000 && Math.abs(((h.b - nearest.bearing) % 360 + 540) % 360 - 180) <= 60);
  if (same.length < 4 || now - same[0].t < 20 * 60000) return null;
  const n = same.length, t0 = same[0].t; let st_ = 0, sd = 0, stt = 0, std = 0;
  for (const h of same) { const u = (h.t - t0) / 60000; st_ += u; sd += h.d; stt += u * u; std += u * h.d; }
  const den = n * stt - st_ * st_; if (den <= 0) return null;
  const rate = -(n * std - st_ * sd) / den * 60;                                            // mph, positive = getting closer
  const closing = motion.speedMph * Math.cos((motion.headingDeg - (nearest.bearing + 180)) * Math.PI / 180);   // mph the motion says it should close at
  const spanMin = (now - t0) / 60000;
  const out = { rate, closing, spanMin, verdict: 'ok' };
  if (closing >= 12 && rate <= 5) out.verdict = 'stalled';
  else if (closing > 0 && rate < 0.6 * closing) { out.verdict = 'slow'; out.etaMin = nearest.distMi / Math.max(rate, 3) * 60; }
  return out;
}

module.exports={colorToDbz,gridFromRGBA,downsampleMax,despeckle,intensityField,bestShift,centroidOf,estimateMotion,makeVelocityField,predictRain,labelBlobs,matchBlobs,blobTrends,nearestEcho,edgeCheck};
