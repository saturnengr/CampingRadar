#!/usr/bin/env node
/* Replay the REAL display from a recording: the app's own index.html runs in Chromium with its clock set to the recording's time
   and every outside request answered from the recording — radar scans, NWS forecast/alerts/observations, lightning, map tiles —
   so what is rendered is exactly what a person watching the display would have seen: banner, forecast cards, warnings, heads-up
   pills, the 12-hour outlook, the map with rings, arrows and outlines. One screenshot per radar scan, then one encode.

   usage: node replay-app.js <recordingDir | zip> [options]
     --basemap=<png|jpg>  stitched basemap aligned with the frames (fetch-basemap.js); tiles are cut from it (any zoom, scaled)
     --app=<index.html>   the app to run (default ../../index.html in this repo)
     --size=1280x800      viewport: 1280x800 tablet (default), 1920x1080 TV, 412x915 phone
     --layout=left|right|auto   sidebar layout for the tablet look (default left)
     --activity=camping   activity the display is set to   --range=50  range button (10/30/50)   --night=auto|day|night
     --secs=1.0           video seconds per radar scan (≈ 5–6 min of real time); --realtime renders 1 s of video per 1 s real
     --from=<ms> --to=<ms>  limit the time span (defaults: whole recording)
     --tz=America/Chicago --fps=30 --out=<mp4>
   What a recording must hold for full fidelity (the backend Recorder writes these since 14 Nov 2026): <t>.png scans, points.json,
   stations.json, <t>.nws-hourly/daily/grid.json, <t>.alerts.json, <t>.obs.json, <t>.lightning.json. Missing pieces degrade to what
   the app shows without them (no forecast cards, no warnings) and are listed at the start of the run. */
const fs = require('fs'), path = require('path'), os = require('os'), http = require('http'), { execFileSync } = require('child_process'), { pathToFileURL } = require('url');
module.paths.push(path.join(__dirname, '..', 'replay', 'node_modules'));
const { PNG } = require('pngjs');
const { chromium } = require('playwright');
const opt = (n, d) => { const a = process.argv.find(x => x.startsWith('--' + n + '=')); return a ? a.slice(n.length + 3) : d; };
const flag = n => process.argv.includes('--' + n);

let dir = process.argv[2]; if (!dir) { console.error('usage: node replay-app.js <recordingDir | zip> [--basemap=…] …'); process.exit(1); }
if (/\.zip$/i.test(dir)) { const target = dir.replace(/\.zip$/i, ''); if (!fs.existsSync(path.join(target, 'meta.json'))) { fs.mkdirSync(target, { recursive: true }); execFileSync('unzip', ['-q', '-o', dir, '-d', target]); } const inner = fs.readdirSync(target).filter(n => fs.existsSync(path.join(target, n, 'meta.json'))); dir = inner.length === 1 ? path.join(target, inner[0]) : target; }
const meta = JSON.parse(fs.readFileSync(path.join(dir, 'meta.json')));
const APP = path.resolve(opt('app', path.join(__dirname, '..', '..', 'index.html'))), APP_DIR = path.dirname(APP);
const [VW, VH] = opt('size', '1280x800').split('x').map(Number), TZ = opt('tz', 'America/Chicago'), FPS = +opt('fps', 30);
const SECS = flag('realtime') ? null : +opt('secs', 1.0), OUT = opt('out', path.join(dir, 'display.mp4'));
const times = fs.readdirSync(dir).filter(f => /^\d+\.png$/.test(f)).map(f => +f.slice(0, -4)).sort((a, b) => a - b);
const from = +opt('from', 0) || times[0], to = +opt('to', 0) || times[times.length - 1];
const clock = ms => new Date(ms).toLocaleTimeString('en-US', { timeZone: TZ, hour: 'numeric', minute: '2-digit' });

// ---- the recording's files by kind, nearest at-or-before a time ----
const byKind = {};
for (const f of fs.readdirSync(dir)) { const m = /^(\d+)\.(.+)$/.exec(f); if (!m) continue; (byKind[m[2]] = byKind[m[2]] || []).push(+m[1]); }
for (const k in byKind) byKind[k].sort((a, b) => a - b);
const latestFile = (kind, t) => { const L = byKind[kind]; if (!L) return null; let b = null; for (const x of L) { if (x <= t) b = x; else break; } return b == null ? (L[0] <= t + 3600e3 ? L[0] : null) : b; };
const readKind = (kind, t) => { const b = latestFile(kind, t); return b == null ? null : fs.readFileSync(path.join(dir, b + '.' + kind), 'utf8'); };
const have = k => !!byKind[k] || fs.existsSync(path.join(dir, k));
const missing = ['points.json', 'stations.json', 'nws-hourly.json', 'nws-daily.json', 'nws-grid.json', 'alerts.json', 'obs.json', 'lightning.json'].filter(k => !have(k));
console.log(`${times.length} scans ${clock(times[0])}–${clock(times[times.length - 1])} at ${meta.name} (${meta.lat}, ${meta.lon})` + (missing.length ? `\n  recording lacks: ${missing.join(', ')} — those parts of the display will be empty (recordings made after the 14 Nov backend carry them)` : '\n  full replay bundle present'));

// ---- Web Mercator geometry of the recording and tile cutting ----
const MERC = 20037508.342789244, RES8 = (2 * MERC) / (256 * 2 ** meta.zoom);   // Mercator metres per recording pixel
const merc = (lat, lon) => [lon / 180 * MERC, Math.log(Math.tan(Math.PI / 4 + lat * Math.PI / 360)) * MERC / Math.PI];
const [cx, cy] = merc(meta.lat, meta.lon), SIZE = meta.sizePx;
const recMinX = cx - SIZE / 2 * RES8, recMaxY = cy + SIZE / 2 * RES8;
const pngCache = new Map();
function loadPng(file) { if (pngCache.has(file)) return pngCache.get(file); const p = PNG.sync.read(fs.readFileSync(file)); pngCache.set(file, p); if (pngCache.size > 10) pngCache.delete(pngCache.keys().next().value); return p; }
/* cut [minx,miny,maxx,maxy] (Mercator m) out of a recording-aligned image at w×h, nearest neighbour; transparent/blank outside */
function cut(img, minx, miny, maxx, maxy, w, h, blank) {
  const out = new PNG({ width: w, height: h }); const sx = (maxx - minx) / w, sy = (maxy - miny) / h;
  for (let y = 0; y < h; y++) { const my = maxy - (y + 0.5) * sy, py = Math.floor((recMaxY - my) / RES8); for (let x = 0; x < w; x++) { const mx = minx + (x + 0.5) * sx, px = Math.floor((mx - recMinX) / RES8); const o = (y * w + x) * 4; if (px < 0 || py < 0 || px >= img.width || py >= img.height) { out.data[o] = blank[0]; out.data[o + 1] = blank[1]; out.data[o + 2] = blank[2]; out.data[o + 3] = blank[3]; continue; } const i = (py * img.width + px) * 4; out.data[o] = img.data[i]; out.data[o + 1] = img.data[i + 1]; out.data[o + 2] = img.data[i + 2]; out.data[o + 3] = img.data[i + 3]; } }
  return PNG.sync.write(out);
}
const tileBox = (z, x, y, tilePx) => { const n = 256 * 2 ** z / tilePx, size = 2 * MERC / n; return [-MERC + x * size, MERC - (y + 1) * size, -MERC + (x + 1) * size, MERC - y * size]; };
let basemap = null; { const b = opt('basemap', ''); const cands = b ? [b] : ['basemap-osm.png', 'basemap-osm.jpg', 'basemap-dark.png'].map(n => path.join(dir, n)); const f = cands.find(p => fs.existsSync(p)); if (f) { if (/\.png$/i.test(f)) basemap = loadPng(f); else { /* jpeg → png via chromium later */ basemap = { jpeg: f }; } console.log('basemap: ' + f); } else console.log('no basemap (plain background); run fetch-basemap.js for this recording'); }

// ---- the replay clock ----
let now = from;
const scanIds = {}; times.forEach((t, i) => { scanIds[t] = i + 1; });
const idToTime = {}; for (const t in scanIds) idToTime[scanIds[t]] = +t;

(async () => {
  // serve the repo folder so index.html loads its relative assets
  const server = http.createServer((req, res) => { const u = decodeURIComponent(req.url.split('?')[0]); const f = path.join(APP_DIR, u === '/' ? 'index.html' : u); if (!f.startsWith(APP_DIR) || !fs.existsSync(f) || fs.statSync(f).isDirectory()) { res.writeHead(404); return res.end(); } const ext = path.extname(f).slice(1); res.writeHead(200, { 'Content-Type': { html: 'text/html', js: 'text/javascript', css: 'text/css', png: 'image/png', json: 'application/json', webmanifest: 'application/manifest+json', mp3: 'audio/mpeg', wav: 'audio/wav' }[ext] || 'application/octet-stream' }); fs.createReadStream(f).pipe(res); });
  await new Promise(r => server.listen(0, '127.0.0.1', r)); const origin = 'http://127.0.0.1:' + server.address().port;
  const browser = await chromium.launch(); const ctx = await browser.newContext({ viewport: { width: VW, height: VH }, deviceScaleFactor: 1, timezoneId: TZ, locale: 'en-US' });
  const BE = 'https://replay.local';
  // settings the display would have: manual location at the recording point, Pro, backend = our fake, activity/range/layout as asked
  const settings = { locMode: 'manual', manual: { lat: meta.lat, lon: meta.lon, name: opt('place', meta.name) }, backend: BE, source: 'mrms', activity: opt('activity', 'camping'), range: +opt('range', 50), rangeSet: '10/30/50', layout: opt('layout', 'left'), welcomed: true, sound: false,
    account: { token: 'replay', pro: true, plan: 'pro', email: 'replay@vantagewx.com', checkedAt: Date.now() * 2 }, night: opt('night', 'auto'), leadTime: 'on', travelMode: 'stationary' };
  await ctx.addInitScript(({ settings, from }) => {
    // clock: Date reports the replay time; window.__setNow moves it
    const R = Date; let off = from - R.now();
    const D = function (...a) { return a.length ? new R(...a) : new R(R.now() + off); }; D.now = () => R.now() + off; D.parse = R.parse; D.UTC = R.UTC; D.prototype = R.prototype; window.Date = D; window.__setNow = ms => { off = ms - R.now(); };
    try { localStorage.setItem('campRadar.v1', JSON.stringify(settings)); localStorage.setItem('campRadar.device', JSON.stringify({ welcomed: true, layout: settings.layout, activity: settings.activity, range: settings.range, rangeSet: settings.rangeSet })); localStorage.removeItem('campRadar.motionHist'); } catch (e) {}
    // no geolocation, no speech, no notifications
    try { Object.defineProperty(navigator, 'geolocation', { value: undefined }); } catch (e) {}
    try { window.speechSynthesis = { speak() {}, cancel() {}, getVoices: () => [], addEventListener() {} }; } catch (e) {}
  }, { settings, from });
  const page = await ctx.newPage();
  page.on('pageerror', e => console.log('  page error:', e.message.slice(0, 160)));
  const leaflet = path.join(__dirname, 'node_modules', 'leaflet', 'dist');
  await page.route('**/*', async route => {
    const url = new URL(route.request().url()), h = url.hostname, p = url.pathname;
    const json = o => route.fulfill({ status: 200, contentType: 'application/json', body: typeof o === 'string' ? o : JSON.stringify(o) });
    const file = (f, ct) => fs.existsSync(f) ? route.fulfill({ status: 200, contentType: ct, body: fs.readFileSync(f) }) : route.fulfill({ status: 404, body: '' });
    try {
      if (h === '127.0.0.1') return route.continue();
      if (h === 'cdnjs.cloudflare.com' && /leaflet/.test(p)) return file(path.join(leaflet, p.endsWith('.css') ? 'leaflet.css' : 'leaflet.js'), p.endsWith('.css') ? 'text/css' : 'text/javascript');
      // basemap tiles (OSM 256-px) from the stitched image
      if (h === 'tile.openstreetmap.org' || /basemaps\.cartocdn\.com$/.test(h) || h === 'opentopomap.org') {
        const m = /\/(\d+)\/(\d+)\/(\d+)(?:@2x)?\.png$/.exec(p); if (!m || !basemap || basemap.jpeg) return route.fulfill({ status: 404, body: '' });
        const [minx, miny, maxx, maxy] = tileBox(+m[1], +m[2], +m[3], 256); return route.fulfill({ status: 200, contentType: 'image/png', body: cut(basemap, minx, miny, maxx, maxy, 256, 256, [27, 35, 43, 255]) });
      }
      // radar: NOAA ImageServer (scan list by id, exportImage by locked id or time) and our fake backend's tile route
      if (h === 'mapservices.weather.noaa.gov' && /radar_base_reflectivity_time\/ImageServer\/query/.test(p)) return json({ features: times.filter(t => t <= now).map(t => ({ attributes: { objectid: scanIds[t], idp_validtime: t } })) });
      if (h === 'mapservices.weather.noaa.gov' && /radar_base_reflectivity_time\/ImageServer\/exportImage/.test(p)) {
        const q = url.searchParams, bbox = (q.get('bbox') || '').split(',').map(Number), [w, hh] = (q.get('size') || '512,512').split(',').map(Number);
        let t = null; const mr = q.get('mosaicRule'); if (mr) { const mm = /lockRasterIds":\[(\d+)\]/.exec(mr); if (mm) t = idToTime[+mm[1]]; } if (t == null && q.get('time')) { const want = +q.get('time'); t = times.filter(x => x <= Math.max(want, now)).reduce((a, x) => Math.abs(x - want) < Math.abs(a - want) ? x : a, times[0]); }
        if (t == null || bbox.length !== 4) return route.fulfill({ status: 404, body: '' });
        return route.fulfill({ status: 200, contentType: 'image/png', body: cut(loadPng(path.join(dir, t + '.png')), bbox[0], bbox[1], bbox[2], bbox[3], w, hh, [0, 0, 0, 0]) });
      }
      if (h === 'mapservices.weather.noaa.gov' || h === 'nowcoast.noaa.gov' || h === 'mesonet.agron.iastate.edu') return route.fulfill({ status: 404, body: '' });
      // NWS
      if (h === 'api.weather.gov') {
        if (/^\/points\//.test(p)) return file(path.join(dir, 'points.json'), 'application/geo+json');
        if (/\/stations$/.test(p)) return file(path.join(dir, 'stations.json'), 'application/geo+json');
        if (/\/observations\/latest/.test(p)) { const b = readKind('obs.json', now); return b ? json(b) : route.fulfill({ status: 404, body: '' }); }
        if (/\/forecast\/hourly$/.test(p)) { const b = readKind('nws-hourly.json', now); return b ? json(b) : route.fulfill({ status: 404, body: '' }); }
        if (/\/forecast$/.test(p)) { const b = readKind('nws-daily.json', now); return b ? json(b) : route.fulfill({ status: 404, body: '' }); }
        if (/^\/gridpoints\/[A-Z]+\/\d+,\d+$/.test(p)) { const b = readKind('nws-grid.json', now); return b ? json(b) : route.fulfill({ status: 404, body: '' }); }
        if (/^\/alerts\/active/.test(p)) { const b = readKind('alerts.json', now); return json(b || { features: [] }); }
        return route.fulfill({ status: 404, body: '' });
      }
      // our stand-in backend
      if (h === 'replay.local') {
        if (p === '/health') return json({ radar: { scans: times.length }, lightning: { flashes: 0 } });
        if (p === '/mrms/times') return json({ times: times.filter(t => t <= now) });
        const tm = /^\/mrms\/tile\/(\d+)\/(\d+)\/(\d+)\/(\d+)\.png$/.exec(p);
        if (tm) { const t = +tm[1]; if (!fs.existsSync(path.join(dir, t + '.png'))) return route.fulfill({ status: 404, body: '' }); const [minx, miny, maxx, maxy] = tileBox(+tm[2], +tm[3], +tm[4], 512); return route.fulfill({ status: 200, contentType: 'image/png', body: cut(loadPng(path.join(dir, t + '.png')), minx, miny, maxx, maxy, 512, 512, [0, 0, 0, 0]) }); }
        if (p === '/lightning') { const b = readKind('lightning.json', now); return b ? json(JSON.parse(b).summary) : route.fulfill({ status: 404, body: '' }); }
        if (p === '/lightning/recent') { const b = readKind('lightning.json', now); return json(b ? JSON.parse(b).recent : []); }
        if (p === '/auth/me') return json({ email: 'replay@vantagewx.com', plan: 'pro', pro: true, expiresMs: null });
        return route.fulfill({ status: 404, body: '' });
      }
      return route.fulfill({ status: 404, body: '' });   // everything else (open-meteo, nominatim, usgs, cdn.jsdelivr …): the display copes
    } catch (e) { return route.fulfill({ status: 500, body: '' }); }
  });
  // a jpeg basemap: decode once in the page to a png buffer
  if (basemap && basemap.jpeg) {
    await page.goto(origin + '/manifest.json').catch(() => {});
    const b64 = await page.evaluate(async src => { const i = new Image(); i.src = src; await i.decode(); const c = document.createElement('canvas'); c.width = i.width; c.height = i.height; c.getContext('2d').drawImage(i, 0, 0); return c.toDataURL('image/png').split(',')[1]; }, 'data:image/jpeg;base64,' + fs.readFileSync(basemap.jpeg).toString('base64'));
    basemap = PNG.sync.read(Buffer.from(b64, 'base64'));
  }
  await page.goto(origin + '/index.html', { waitUntil: 'load' });
  await page.waitForTimeout(4000);
  const work = fs.mkdtempSync(path.join(os.tmpdir(), 'vwx-replay-')); const shots = [];
  const show = times.filter(t => t >= from && t <= to);
  for (let i = 0; i < show.length; i++) {
    const t = show[i]; now = t + 90000;   // the display sees a scan about a minute and a half after its valid time
    await page.evaluate(ms => window.__setNow(ms), now);
    await page.evaluate(async (hourly) => {
      try { await refreshRadarTiles(); } catch (e) {}
      try { await runNowcast(); } catch (e) {}
      try { await refreshAlerts(); } catch (e) {}
      try { refreshLightning(); } catch (e) {}
      if (hourly) { try { await refreshForecast(); } catch (e) {} try { await refreshObs(); } catch (e) {} }
      try { renderHeadsUp(); } catch (e) {} try { renderOutlook(); } catch (e) {} try { renderStatus(); } catch (e) {} try { tickClock(); } catch (e) {} try { applyNight(); } catch (e) {}
      try { if (typeof renderDays === 'function') renderDays(); } catch (e) {}
    }, i === 0 || new Date(t).getUTCHours() !== new Date(show[i - 1]).getUTCHours());
    await page.waitForTimeout(i === 0 ? 3000 : 900);   // tiles
    const f = path.join(work, 'f' + String(i).padStart(5, '0') + '.jpg'); await page.screenshot({ path: f, type: 'jpeg', quality: 92 });
    const dur = SECS != null ? SECS : Math.max(0.5, ((show[i + 1] || t + 300000) - t) / 1000);
    shots.push({ file: f, dur });
    const st = await page.evaluate(() => (document.querySelector('#statusMain') || {}).textContent || '');
    process.stdout.write(`\r${i + 1}/${show.length} ${clock(t)}  ${st.slice(0, 70).padEnd(70)}`);
  }
  console.log();
  await browser.close(); server.close();
  const list = path.join(work, 'list.txt');
  fs.writeFileSync(list, shots.map(s => `file '${s.file}'\nduration ${s.dur.toFixed(3)}`).join('\n') + `\nfile '${shots[shots.length - 1].file}'\n`);
  const tmp = OUT.replace(/\.mp4$/, '.tmp.mp4');
  execFileSync('ffmpeg', ['-y', '-loglevel', 'error', '-f', 'concat', '-safe', '0', '-i', list, '-vf', `fps=${FPS},format=yuv420p`, '-c:v', 'libx264', '-crf', '18', '-preset', 'veryfast', '-pix_fmt', 'yuv420p', '-movflags', '+faststart', tmp], { stdio: 'inherit' });
  fs.renameSync(tmp, OUT);
  console.log(`→ ${OUT}  (${shots.reduce((s, x) => s + x.dur, 0).toFixed(0)} s, ${shots.length} scans; stills in ${work})`);
})();
