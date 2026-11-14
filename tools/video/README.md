# VantageWX video tools

Marketing clips from a recording: the real NOAA MRMS frames over a basemap, with the app's own answers recreated frame by frame
from the same nowcast core the app runs (`tools/replay/core.js`) — status banner and arrival time, range rings, storm arrows and
the 15 / 30 / 60-minute outlines — then a title card and an end card quoting the call that was made and when the rain came.

```
node fetch-basemap.js <recordingDir> [--style=osm|dark|light]          # stitches tiles aligned pixel-for-pixel with the frames
node make-video.js  <recordingDir> --basemap=<png> --place="Rochester, IL" [--out=x.mp4] [--hold=0.7] [--radius=170] [--thresh=20]
```

Needs `pngjs` (already in `tools/replay/node_modules`), Playwright with Chromium, and ffmpeg. One encode per run (stills with
per-image durations through the concat demuxer). The story is picked automatically: it starts 30 minutes into the recording (the
tracker needs history), ends 25 minutes after rain first reaches the point for three scans in a row, and the end card quotes the
earliest call whose arrival time was within 10 minutes of the truth.

Attribution when a basemap is used: © OpenStreetMap contributors (and © CARTO for the dark/light styles).
