# VantageWX video tools

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
