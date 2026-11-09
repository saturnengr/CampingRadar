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
- `abilene`, `waco`, `houston`, `sanantonio`, `maine` — 8–9 Nov 2026, 12:16–00:16 CST, ~108 frames each (one every
  ~6–7 min), 200 mi (kept at `C:\Jeff\GitHub\LeadTime\Training\20261108\`). Old-build recordings: no POP / dew
  spread in the status files, so `--dry` is NOT valid for them (it hard-codes the Dallas night's 1% POP).
  Results 2026.11.10i, 120 mi, edge/fizzle rules without forecast input — false alarms old→new / misses old→new:
  Abilene 11→7 / 0→0 (no rain reached the point); Waco 12→9 / 6→6 (ETA median error 42 min, ~44 min late);
  Houston 2→2 / 3→4 (ETA 9 min); San Antonio 12→11 / 1→1 (ETA 12 min); Maine 5→4 / 3→3 (ETA 12 min; motion
  poor, 53° median heading error, stratiform with few cores). Misses are cells forming close in with no
  history; the remaining false alarms are passing storms. V4 targets: a forming-nearby rule, a tighter
  closest-approach gate, and reading recorded POP/dew spread + gauge.jsonl per frame.
