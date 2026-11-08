# Radar replay harness

Replays a recording made by the backend (admin → Diagnostics → Radar recording) through the page's own
nowcast code and scores it: storm heading/speed against independent references (tracks of ≥35 dBZ cores,
60-minute displacement) and the arrival forecast against whether ≥20 dBZ rain actually reached the point.

```
cd tools/replay && npm install
python3 extract-core.py                      # copies the nowcast functions out of index.html
unzip ~/Downloads/dallas.zip -d /tmp/rec     # a recording: {t}.png frames + {t}.status.json + meta.json
node replay.js /tmp/rec/dallas 120           # 120-mile window (the app's default); 200 = overview rings
node replay.js /tmp/rec/dallas 120 --dry     # also apply the "NWS POP < 10% / dry air" rule
```

Recordings on hand
- `dallas` — 7–8 Nov 2026, 20:55–04:09 CST, 63 frames, 200 mi around 32.8954,-96.8424 (kept at
  `C:\Jeff\GitHub\LeadTime\dallas.zip`, 15 MB). A broad decaying rain shield moving ENE at 30–40 mph whose
  leading edge evaporated short of Dallas: no ≥20 dBZ rain ever reached the point. Baseline 2026.11.08k:
  heading median error 12°, speed 6 mph, 14 false "rain arriving" verdicts; with the edge/fizzle rules 5,
  with the dry-air rule 0.
