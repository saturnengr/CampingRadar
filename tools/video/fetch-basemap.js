#!/usr/bin/env node
/* Stitch a basemap that lines up pixel-for-pixel with a recording's frames (Web Mercator, the recording's zoom, centred on its point).
   usage: node fetch-basemap.js <recordingDir> [--style=osm|dark|light] [--out=basemap.png]
   Tiles: osm = tile.openstreetmap.org (attribution © OpenStreetMap contributors), dark/light = CARTO basemaps (© CARTO, © OSM). */
const fs = require('fs'), path = require('path'), { PNG } = require('pngjs');
const dir = process.argv[2]; const opt = (n, d) => { const a = process.argv.find(x => x.startsWith('--' + n + '=')); return a ? a.slice(n.length + 3) : d; };
const style = opt('style', 'osm'), out = opt('out', path.join(dir, 'basemap-' + style + '.png'));
const meta = JSON.parse(fs.readFileSync(path.join(dir, 'meta.json')));
const z = meta.zoom, size = meta.sizePx, n = 256 * 2 ** z;
const merc = (lat, lon) => { const x = (lon + 180) / 360 * n, s = Math.sin(lat * Math.PI / 180), y = (0.5 - Math.log((1 + s) / (1 - s)) / (4 * Math.PI)) * n; return [x, y]; };
const [cx, cy] = merc(meta.lat, meta.lon), x0 = Math.round(cx - size / 2), y0 = Math.round(cy - size / 2);
const url = (tx, ty) => style === 'osm' ? `https://tile.openstreetmap.org/${z}/${tx}/${ty}.png` : `https://a.basemaps.cartocdn.com/${style === 'dark' ? 'dark_all' : 'light_all'}/${z}/${tx}/${ty}.png`;
(async () => {
  const outPng = new PNG({ width: size, height: size });
  const tx0 = Math.floor(x0 / 256), ty0 = Math.floor(y0 / 256), tx1 = Math.floor((x0 + size - 1) / 256), ty1 = Math.floor((y0 + size - 1) / 256);
  let got = 0, total = 0;
  for (let ty = ty0; ty <= ty1; ty++) for (let tx = tx0; tx <= tx1; tx++) {
    total++;
    const r = await fetch(url(tx, ty), { headers: { 'User-Agent': 'VantageWX-video/1.0 (+https://vantagewx.com)' } });
    if (!r.ok) { console.error('tile', tx, ty, r.status); continue; }
    const tile = PNG.sync.read(Buffer.from(await r.arrayBuffer())); got++;
    for (let y = 0; y < 256; y++) for (let x = 0; x < 256; x++) {
      const X = tx * 256 + x - x0, Y = ty * 256 + y - y0; if (X < 0 || Y < 0 || X >= size || Y >= size) continue;
      const si = (y * tile.width + x) * 4, di = (Y * size + X) * 4;
      if (tile.width === 256) { outPng.data[di] = tile.data[si]; outPng.data[di + 1] = tile.data[si + 1]; outPng.data[di + 2] = tile.data[si + 2]; outPng.data[di + 3] = 255; }
      else { const s2 = ((y * 2) * tile.width + x * 2) * 4; outPng.data[di] = tile.data[s2]; outPng.data[di + 1] = tile.data[s2 + 1]; outPng.data[di + 2] = tile.data[s2 + 2]; outPng.data[di + 3] = 255; }   // @2x tiles
    }
    await new Promise(r => setTimeout(r, 120));   // be polite to the tile server
  }
  fs.writeFileSync(out, PNG.sync.write(outPng));
  console.log(`basemap ${size}x${size} z${z} from ${got}/${total} tiles → ${out}`);
})();
