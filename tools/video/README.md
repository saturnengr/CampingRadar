# VantageWX video tools

## replay-app.js — the real display, replayed from a recording

`node replay-app.js <recordingDir | zip> [--basemap=…] [--size=1280x800] [--activity=camping] [--range=50] [--secs=1.0 | --realtime]`

Runs the app's own `index.html` in Chromium with its clock set to the recording's time and every outside request answered from the
recording: radar scans (NOAA ImageServer and the backend tile route), NWS points / hourly / daily / grid / alerts / observations,
lightning, basemap tiles cut from the stitched image. What it screenshots is exactly what a person watching the display would have
seen — banner and status, forecast cards, warnings, heads-up pills, the 12-hour outlook with the arrival curve, the map with rings,
arrows and outlines, the clock — one still per radar scan, then one encode. `--secs` is video seconds per scan (default 1, so a
10-hour recording is about 100 s); `--realtime` plays at real speed.

A recording carries everything needed when it was made by the backend Recorder from 14 Nov 2026 on: `<t>.png` scans, `points.json`,
`stations.json`, `<t>.nws-hourly/daily/grid.json` (hourly), `<t>.alerts.json` and `<t>.lightning.json` (every 5 min), `<t>.obs.json`
(every 15 min). Older recordings replay the radar and the tracker only; the run lists what is missing.

## make-video.js — the stylised marketing cut

Marketing clips from a recording: the real NOAA MRMS frames over a basemap, with the app's own answers recreated frame by frame
from the same nowcast core the app runs (`tools/replay/core.js`) — status banner and arrival time, range rings, storm arrows and
the 15 / 30 / 60-minute outlines — then a title card and an end card quoting the call that was made and when the rain came.

```
node fetch-basemap.js <recordingDir> [--style=osm|dark|light]          # stitches tiles aligned pixel-for-pixel with the frames
node make-video.js  <recordingDir> --basemap=<png> --place="Rochester, IL" [--out=x.mp4] [--hold=0.7] [--radius=170] [--thresh=20]
```

Install once: `cd tools/video && npm install && npx playwright install chromium` (pngjs and Playwright; the scripts also find pngjs in
`tools/replay/node_modules` if the harness is installed). `fetch-basemap.js` needs only pngjs; `make-video.js` also needs Chromium and ffmpeg on PATH. One encode per run (stills with
per-image durations through the concat demuxer). The story is picked automatically: it starts 30 minutes into the recording (the
tracker needs history), ends 25 minutes after rain first reaches the point for three scans in a row, and the end card quotes the
earliest call whose arrival time was within 10 minutes of the truth.

Attribution when a basemap is used: © OpenStreetMap contributors (and © CARTO for the dark/light styles).

**If this machine cannot reach the tile servers**, the same stitch runs in any browser: open a tile URL on the server (so fetches are
same-origin), paste the loop from `fetch-basemap.js` into the console with the recording's `z, x0, y0, size` and tile range,
`canvas.toDataURL('image/jpeg', 0.92)`, and save the result as `basemap-osm.jpg` in the recording folder. `make-video.js --basemap=`
takes PNG or JPEG.
