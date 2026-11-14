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
node replay.js /tmp/rec/waco 120 --gauge-mi=8  # gauge truth radius (default 5 mi) when the recording has gauge files
```

Ground truth from gauges: when the recording folder holds `gauge-madis.jsonl` (every MADIS mesonet report near the point,
written by the backend's verify worker during the recording, or afterwards with `POST /admin/verify/madis?name=<rec>`) and/or
`gauge.jsonl` (the airport), the harness also prints an hour-by-hour radar-vs-gauge comparison and scores both rule sets
against gauge truth. `R-` hours are radar echo with dry gauges (virga, evaporation); `-G` hours are wet gauges with no ≥20 dBZ
echo (drizzle the radar under-reads).

Recordings on hand
- `dallas` — 7–8 Nov 2026, 20:55–04:09 CST, 63 frames, 200 mi around 32.8954,-96.8424 (kept at
  `C:\Jeff\GitHub\LeadTime\dallas.zip`, 15 MB). A broad decaying rain shield moving ENE at 30–40 mph whose
  leading edge evaporated short of Dallas: no ≥20 dBZ rain ever reached the point. Baseline 2026.11.08k:
  heading median error 12°, speed 6 mph, 14 false "rain arriving" verdicts; with the edge/fizzle rules 5,
  with the dry-air rule 0.
- `abilene`, `waco`, `houston`, `sanantonio`, `maine` — 8–9 Nov 2026, 12:16–00:16 CST, ~108 frames each (one every
  ~6–7 min), 200 mi (kept at `C:\Jeff\GitHub\LeadTime\Training\20261108\`). Old-build recordings: no POP / dew
  spread in the status files, so `--dry` is NOT valid for them (it hard-codes the Dallas night's 1% POP).
  Results 2026.11.10i, 120 mi, edge/fizzle rules without forecast input — false alarms old→new / misses old→new:
  Abilene 11→7 / 0→0 (no rain reached the point); Waco 12→9 / 6→6 (ETA median error 42 min, ~44 min late);
  Houston 2→2 / 3→4 (ETA 9 min); San Antonio 12→11 / 1→1 (ETA 12 min); Maine 5→4 / 3→3 (ETA 12 min; motion
  poor, 53° median heading error, stratiform with few cores). Misses are cells forming close in with no
  history; the remaining false alarms are passing storms. V4 targets: a forming-nearby rule, a tighter
  closest-approach gate, and reading recorded POP/dew spread + gauge.jsonl per frame.

### Rule versions scored by the harness (12 Nov 2026)

- **old rules** — page 2026.11.08k: projection only, fizzle watch.
- **new rules** — pages 2026.11.08l … 2026.11.12c: edge rules (stalled / slow) and the 20-minute fizzle memory, applied to whatever
  the nearest echo was.
- **v4 rules** (`--v4`) — page 2026.11.12d, "V4 strict horizon": edge verdicts apply only to the storm projected to hit and never to a
  strong or large arrival; the fizzle memory releases once the edge is observed closing (5 mph); observed closing at 10 mph+ inside
  15 mi outranks the projection; a grazing hit (< 12 min, < 6 cells, < 40 dBZ) and a receding edge (≤ −5 mph) are passes (blue);
  arrivals projected beyond 75 min are "could reach you" (blue). 71-case batch: false alarms 412 → 349 (new) → 322 (v4);
  misses 206 → 244 (new) → 222 (v4); gauge misses 74 → 84 → 71. `--v4` also prints a 12-setting sweep (horizon 75/90 min,
  near-echo exception off/15/25 mi, closing 10/15 mph) for choosing the next adjustment. The near-echo exception at 25 mi gave
  misses 202 but false alarms 388 — rejected for now in favour of the strict horizon.

## Webcam verdicts (`cam.jsonl`)

Backend builds from 13 Nov 2026 on write `cam.jsonl` beside `gauge-madis.jsonl` while a recording runs: one line per classified frame from
the nearest public webcams within 10 mi — `{t, img, cam, name, lat, lon, distMi, day, precip, intensity, ground, vis, sky, lens, wind, conf, radarDbz}`.
The harness prints an agreement table (camera vs radar over the camera; wet ground with dry radar = drizzle reaching the ground unseen;
radar over dry ground = not reaching the ground; fog frames). Verdicts at night or with a bad lens or low confidence are excluded.

## Sites: many evaluation points from one recording (`--site`)

A recording is the whole radar disc around its centre, so any point inside it can be scored as "you":

    node replay.js <recording> 100 --v4 --site=43.08,-79.48:niagara-s40

`--site=lat,lon[:name]` re-centres the crop window on that point (Web Mercator at the recording's zoom), recomputes gauge
distances from it, and labels the output. The window must stay inside the frame: with a 200-mile recording, a 100-mile
evaluation radius allows sites up to ~100 miles from the centre. Score a line of sites across a storm's track — direct hit,
two flanks, two misses — from one set of frames rather than recording three overlapping discs.

Caveat: gauge and camera ground truth are collected by the backend around the recording's sample points, so a site far from
the centre may have little gauge truth nearby; the radar-truth columns are always available. Site names appear in the output
header; the batch scripts can loop over `--site` values per recording.
