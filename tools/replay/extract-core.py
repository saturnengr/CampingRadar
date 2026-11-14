#!/usr/bin/env python3
"""Pull the page's pure nowcast functions out of index.html into core.js so the replay runs the exact code the app runs."""
import re, pathlib
root = pathlib.Path(__file__).resolve().parents[2]
s = (root / 'index.html').read_text()
a = s.index('/* Pure functions (no DOM)'); b = s.index('// ==NOWCAST-CORE-END==')
(pathlib.Path(__file__).parent / 'core.js').write_text(s[a:b] + "\nmodule.exports={colorToDbz,gridFromRGBA,downsampleMax,despeckle,intensityField,bestShift,centroidOf,estimateMotion,reconcileMotion,rememberMotion,makeVelocityField,predictRain,labelBlobs,matchBlobs,blobTrends,nearestEcho,edgeCheck};\n")
print('core.js written')
