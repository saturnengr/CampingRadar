/* The deployed arrival rules (V4 strict, bn0_bm75_cl10 + proximity 5 mi) as one pure function, so the replay harness and the video
   renderer judge a frame exactly the same way. Mirrors computeStatus in index.html for everything a recording can know (no NWS
   forecast, no dew point, no lead-time-by-activity, no travel frame). Keep this and the app in step when a rule changes.

   decide(f, state, opts) → { cls, level, etaMin, main, sub, prox, closingNear, reason }
     f: { res, trends (Map from blobTrends), edge (edgeCheck), nearest, rainingNow, nowDbz, motion, far (long projection hit or null), t }
     state: { fizzleSeen } — mutated: the 20-minute fizzle memory
     opts: { thresh, resumeMph 5, closeMph 10, nearMi 15, blueMin 75, blueNearMi 0, grazeMin 12, grazeCells 6, recedeMph 5, proxMi 5, clock(ms) → "3:48 PM" } */
const DIRS = ['N', 'NNE', 'NE', 'ENE', 'E', 'ESE', 'SE', 'SSE', 'S', 'SSW', 'SW', 'WSW', 'W', 'WNW', 'NW', 'NNW'];
const DIRS_LONG = ['north', 'north-northeast', 'northeast', 'east-northeast', 'east', 'east-southeast', 'southeast', 'south-southeast', 'south', 'south-southwest', 'southwest', 'west-southwest', 'west', 'west-northwest', 'northwest', 'north-northwest'];
const compass = d => DIRS[Math.round(((d % 360) + 360) % 360 / 22.5) % 16];
const compassLong = d => DIRS_LONG[Math.floor(((d % 360) + 360) % 360 / 22.5) % 16];
const fmtDurExact = m => { m = Math.round(m); if (m < 60) return m + ' min'; const h = Math.floor(m / 60), r = m % 60; return h + ' h' + (r ? ' ' + r + ' min' : ''); };
const fmtDur = m => { m = Math.round(m); if (m < 10) return fmtDurExact(m); const d = m % 10; return d === 0 ? fmtDurExact(m) : d < 5 ? 'just over ' + fmtDurExact(m - d) : 'just under ' + fmtDurExact(m - d + 10); };
const word = dbz => dbz < 20 ? 'Very light' : dbz < 30 ? 'Light' : dbz < 40 ? 'Moderate' : dbz < 50 ? 'Heavy' : 'Very heavy';

function decide(f, state, opts) {
  const o = Object.assign({ resumeMph: 5, closeMph: 10, nearMi: 15, blueMin: 75, blueNearMi: 0, grazeMin: 12, grazeCells: 6, recedeMph: 5, proxMi: 5, clock: ms => new Date(ms).toISOString().slice(11, 16) }, opts || {});
  const { res, edge, nearest: nr, rainingNow, nowDbz, motion, far, t } = f, hit = res.hit;
  const mv = motion && motion.ok ? ' · moving ' + compass(motion.headingDeg) + ' ' + Math.round(motion.speedMph) + ' mph' : '';
  const at = (eta) => eta < 10 ? 'Rain arriving in ~' + Math.round(eta) + ' min' : 'Rain arriving ≈ ' + o.clock(t + eta * 60000) + ' (in ' + fmtDur(eta) + ')';
  // fizzle memory (20 min, released by an edge seen closing again)
  let fizz = false;
  if (hit) { const tr = f.trends && f.trends.get(hit.blob); const now = !!(tr && tr.word === 'weakening' && tr.fizzleMin != null && tr.fizzleMin < hit.etaMin - 2); if (now) { fizz = true; state.fizzleSeen = t; } else if (state.fizzleSeen && t - state.fizzleSeen < 20 * 60000 && !(tr && tr.word === 'strengthening') && hit.maxDbz < 40 && !(edge && edge.rate >= o.resumeMph)) fizz = true; }
  const closingNear = !!(edge && edge.rate >= o.closeMph && nr && nr.distMi <= o.nearMi);
  if (rainingNow) {
    const w = word(nowDbz);
    return { cls: 'red', level: 'atyou', etaMin: 0, main: w + ' rain at your location', sub: hit && hit.endMin > 0 ? 'rain ending in ~' + Math.round(hit.endMin) + ' min' + mv : 'raining now' + mv, reason: 'atyou' };
  }
  if (closingNear) {
    let eta = nr.distMi / edge.rate * 60; if (hit && hit.etaMin < eta) eta = hit.etaMin;
    return { cls: eta <= 10 ? 'red' : 'orange', level: eta <= 10 ? 'imminent' : 'incoming', etaMin: eta, closingNear: true, main: at(eta), sub: 'Edge observed closing at ' + Math.round(edge.rate) + ' mph, ' + Math.round(nr.distMi) + ' mi ' + compass(nr.bearing) + mv, reason: 'closing' };
  }
  if (hit) {
    const graze = (hit.endMin - hit.etaMin) < o.grazeMin && hit.cells < o.grazeCells && hit.maxDbz < 40;
    const farOut = hit.distMi > 50;
    const edgeApplies = edge && (res.nearestBlob == null || hit.blob == null || res.nearestBlob === hit.blob) && !(hit.maxDbz >= 40 || hit.cells >= 100);
    let eta = hit.etaMin; if (edgeApplies && edge.verdict === 'slow' && edge.etaMin > hit.etaMin) eta = Math.min(120, edge.etaMin);
    const dir = compass(hit.fromBearing), dist = Math.round(hit.distMi), strength = word(hit.maxDbz);
    if (fizz && hit.etaMin > 10 && hit.maxDbz < 40) return { cls: 'yellow', level: 'fizzle', etaMin: hit.etaMin, main: 'Rain to the ' + compassLong(hit.fromBearing) + ' is weakening — may fizzle', sub: strength + ' rain ' + dist + ' mi ' + dir + ', on track but fading' + mv, reason: 'fizzle' };
    if (edgeApplies && edge.verdict === 'stalled' && hit.etaMin > 30) return { cls: 'yellow', level: 'stalled', etaMin: hit.etaMin, main: 'Rain to the ' + compassLong(hit.fromBearing) + ' is not getting closer — likely evaporating at its edge', sub: 'Edge has held its distance for ' + Math.round(edge.spanMin || 20) + '+ min' + mv, reason: 'stalled' };
    if (graze) return { cls: 'blue', level: 'passing', etaMin: hit.etaMin, main: 'Rain ' + dist + ' mi ' + dir + ' — brushing past', sub: 'Only the edge clips your spot' + mv, reason: 'graze' };
    if (edgeApplies && edge.rate <= -o.recedeMph && hit.etaMin > 10) return { cls: 'blue', level: 'receding', etaMin: hit.etaMin, main: 'Rain ' + dist + ' mi ' + dir + ' — moving away', sub: 'Nearest edge receding at ' + Math.round(-edge.rate) + ' mph' + mv, reason: 'recede' };
    if (farOut) return { cls: 'blue', level: 'watch', etaMin: eta, main: 'Rain ' + dist + ' mi ' + dir + ' is heading this way · about ' + fmtDurExact(Math.round(eta / 10) * 10) + ' out', sub: strength + ' rain, ≈ ' + o.clock(t + eta * 60000) + ' — alerts start inside 50 miles' + mv, reason: 'far' };
    if (eta > o.blueMin && !(nr && nr.distMi <= o.blueNearMi)) return { cls: 'blue', level: 'watch', etaMin: eta, main: 'Rain ' + dist + ' mi ' + dir + ' could reach you ≈ ' + o.clock(t + eta * 60000), sub: strength + ' rain, ' + fmtDur(eta) + ' out on its current track' + mv, reason: 'horizon' };
    return { cls: eta <= 10 ? 'red' : 'orange', level: eta <= 10 ? 'imminent' : 'incoming', etaMin: eta, main: at(eta), sub: strength + ' rain ' + dist + ' mi ' + dir + mv, reason: 'hit' };
  }
  if (o.proxMi > 0 && nr && nr.distMi <= o.proxMi && nr.dbz >= (o.thresh || 0) && !(edge && edge.rate <= -o.recedeMph))
    return { cls: 'orange', level: 'incoming', etaMin: null, prox: true, main: 'Rain ' + Math.round(nr.distMi) + ' mi ' + compass(nr.bearing) + ' — may drift over you', sub: word(nr.dbz) + ' rain close by with no clear track' + mv, reason: 'prox' };
  if (far && motion && motion.ok) {
    const eta = far.etaMin;
    return { cls: 'blue', level: 'watch', etaMin: eta, main: 'Rain ' + Math.round(far.distMi) + ' mi ' + compass(far.fromBearing) + ' could reach you in ' + (/^just/.test(fmtDur(eta)) ? '' : 'about ') + fmtDur(eta), sub: 'Straight-line track, ≈ ' + o.clock(t + eta * 60000) + ' — beyond the two hours the tracker trusts', reason: 'far6h' };
  }
  if (res.passing) return { cls: 'blue', level: 'passing', etaMin: null, main: 'Rain ' + Math.round(res.passing.distMi) + ' mi ' + compass(res.passing.bearing) + ' — passing to the ' + compassLong(res.passing.passBearing), sub: 'Not on track for you' + mv, reason: 'passing' };
  return { cls: 'green', level: 'clear', etaMin: null, main: 'No rain heading your way', sub: nr ? 'Nearest rain ' + Math.round(nr.distMi) + ' mi ' + compass(nr.bearing) + mv : 'Clear within 50 miles', reason: 'clear' };
}

/* ringStates as the app draws them (index.html ringStates), from a decision and the predictRain result. lagMin = radar age. */
function ringStates(d, res, radii, lagMin) {
  const out = {}, SOON = 60, lag = lagMin || 0;
  if (!res) return out;
  const hitOk = res.hit && ['atyou', 'imminent', 'incoming', 'low', 'watch'].includes(d.level);
  const missOk = !!(res.passing || (res.hit && d.level === 'passing'));
  const col = res.hit ? (res.hit.maxDbz >= 50 ? 'red' : res.hit.maxDbz >= 40 ? 'orange' : res.hit.maxDbz >= 30 ? 'yellow' : 'white') : 'white';
  if (d.prox && res.nearest) { const pc = res.nearest.dbz >= 50 ? 'red' : res.nearest.dbz >= 40 ? 'orange' : res.nearest.dbz >= 30 ? 'yellow' : 'white'; for (const r of radii) out[r] = res.nearest.distMi <= r ? { style: 'solid', c: pc } : { style: 'dashed', c: 'white' }; return out; }
  for (const r of radii) {
    let style = 'dashed', c = 'white';
    const hi = hitOk && res.ringIn ? res.ringIn[r] : null, mi = res.ringMiss ? res.ringMiss[r] : null;
    if (hi && hi.t - lag <= SOON) { style = hi.t - lag <= 0.5 ? 'solid' : 'dotted'; c = col; }
    else if (missOk && mi != null && mi - lag <= SOON) { style = 'dotted'; c = 'blue'; }
    out[r] = { style, c };
  }
  return out;
}
module.exports = { decide, ringStates, compass, compassLong, fmtDur, fmtDurExact, word };
