namespace CampRadar.Api.Nowcast;

/// <summary>A grid of max reflectivity (dBZ) per cell; -99 = no echo. Row 0 is north.</summary>
public sealed class DbzGrid
{
    public int W { get; }
    public int H { get; }
    public float[] D { get; }
    public DbzGrid(int w, int h, float[]? d = null)
    {
        W = w; H = h; D = d ?? new float[w * h];
        if (d is null) Array.Fill(D, -99f);
    }
    public float this[int x, int y] { get => D[y * W + x]; set => D[y * W + x] = value; }
}

public sealed record RegionMotion(double Cx, double Cy, bool Ok, double Vx, double Vy, double Conf);

/// <summary>Storm motion. Velocities in km/min; x = east, y = south (image orientation).</summary>
public sealed class Motion
{
    public bool Ok { get; set; }
    public string? Reason { get; set; }
    public double Vx { get; set; }
    public double Vy { get; set; }
    public double Conf { get; set; }
    public double SpeedMph { get; set; }
    public double HeadingDeg { get; set; }
    public double RegionSize { get; set; } = 1;
    public List<RegionMotion> Regions { get; } = new();
    public int DtMin { get; set; }
}

public sealed record Nearest(double DistMi, double Bearing, double Dbz);
public sealed record Passing(double DistMi, double Bearing, double PassBearing, double ClosestMi);
public sealed record Hit(double EtaMin, double EndMin, double MaxDbz, double FromBearing, int Cells, int Blob, Dictionary<int, int> BlobCells)
{
    public BlobTrend? Trend { get; set; }
    public bool Fizzles { get; set; }
}
public sealed class Blob { public int Id; public int N; public float Peak; public double Vol; public double Cx; public double Cy; public bool Tiny; }
public sealed class Labelled { public int[] Labels = Array.Empty<int>(); public List<Blob> Blobs = new(); }
public sealed record BlobTrend(string Word, double VolRatio, double AreaRatio, double PeakDelta, double SpanMin, double? FizzleMin, int Points);

public sealed class Prediction
{
    public bool Ring10 { get; set; }
    public bool Ring30 { get; set; }
    public bool Ring50 { get; set; }
    public Nearest? Nearest { get; set; }
    public Passing? Passing { get; set; }
    public Hit? Hit { get; set; }
    public int NearestBlob { get; set; }
    public BlobTrend? NearestTrend { get; set; }
}

/// <summary>
/// Pure functions for tracking storm motion between radar frames and predicting whether rain
/// reaches a point. A line-for-line port of the NOWCAST-CORE block in index.html so the
/// server and the standalone page agree.
/// </summary>
public static class NowcastEngine
{
    // Reflectivity colour scale used by NOAA MRMS mapservices and the IEM composite.
    private static readonly (double Dbz, double R, double G, double B)[] Ramp =
    {
        (-10, 207, 209, 175), (0, 154, 160, 181), (10, 67, 94, 159), (15, 111, 214, 232),
        (20, 14, 214, 20), (30, 9, 94, 9), (30.5, 34, 107, 8), (40, 255, 226, 0),
        (45, 255, 177, 0), (50, 255, 0, 0), (60, 193, 0, 0), (65, 255, 0, 255), (70, 150, 80, 200), (75, 255, 255, 255)
    };

    public static float ColorToDbz(int r, int g, int b, int a = 255)
    {
        if (a < 40) return -99f;
        double best = -99, bd = double.PositiveInfinity;
        for (int i = 0; i < Ramp.Length - 1; i++)
        {
            var (d0, r0, g0, b0) = Ramp[i];
            var (d1, r1, g1, b1) = Ramp[i + 1];
            double vr = r1 - r0, vg = g1 - g0, vb = b1 - b0, len = vr * vr + vg * vg + vb * vb;
            double t = len > 0 ? ((r - r0) * vr + (g - g0) * vg + (b - b0) * vb) / len : 0;
            t = Math.Clamp(t, 0, 1);
            double dist = Sq(r - r0 - t * vr) + Sq(g - g0 - t * vg) + Sq(b - b0 - t * vb);
            if (dist < bd) { bd = dist; best = d0 + t * (d1 - d0); }
        }
        return bd > 45 * 45 ? -99f : (float)(Math.Round(best * 2) / 2);
    }

    /// <summary>RGBA pixels (row-major, 4 bytes per pixel) to a grid of max dBZ per cellPx×cellPx block.</summary>
    public static DbzGrid GridFromRgba(ReadOnlySpan<byte> px, int w, int h, int cellPx)
    {
        int gw = w / cellPx, gh = h / cellPx;
        var g = new DbzGrid(gw, gh);
        var cache = new Dictionary<int, float>();
        for (int y = 0; y < gh * cellPx; y++)
        {
            int row = (y / cellPx) * gw;
            for (int x = 0; x < gw * cellPx; x++)
            {
                int i = (y * w + x) * 4;
                if (px[i + 3] < 40) continue;
                int key = (px[i] << 16) | (px[i + 1] << 8) | px[i + 2];
                if (!cache.TryGetValue(key, out var v)) { v = ColorToDbz(px[i], px[i + 1], px[i + 2]); cache[key] = v; }
                int gi = row + x / cellPx;
                if (v > g.D[gi]) g.D[gi] = v;
            }
        }
        return g;
    }

    public static DbzGrid DownsampleMax(DbzGrid g, int f)
    {
        int w = g.W / f, h = g.H / f;
        var o = new DbzGrid(w, h);
        for (int y = 0; y < h * f; y++)
            for (int x = 0; x < w * f; x++)
            {
                float v = g.D[y * g.W + x]; int i = (y / f) * w + x / f;
                if (v > o.D[i]) o.D[i] = v;
            }
        return o;
    }

    /// <summary>Remove isolated single cells (radar speckle).</summary>
    public static DbzGrid Despeckle(DbzGrid g, float thresh)
    {
        var d = (float[])g.D.Clone();
        for (int y = 0; y < g.H; y++)
            for (int x = 0; x < g.W; x++)
            {
                int i = y * g.W + x;
                if (g.D[i] < thresh) continue;
                bool any = false;
                for (int dy = -1; dy <= 1 && !any; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || yy < 0 || xx >= g.W || yy >= g.H) continue;
                        if (g.D[yy * g.W + xx] >= thresh) { any = true; break; }
                    }
                if (!any) d[i] = -99f;
            }
        return new DbzGrid(g.W, g.H, d);
    }

    private static float[] IntensityField(DbzGrid g)
    {
        var a = new float[g.D.Length];
        for (int i = 0; i < a.Length; i++) { float v = g.D[i]; a[i] = v >= 15 ? Math.Min(v, 55) - 15 : 0; }
        return a;
    }

    private static int CountRain(float[] I, int w, int x0, int y0, int x1, int y1)
    {
        int n = 0;
        for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) if (I[y * w + x] > 0) n++;
        return n;
    }

    private readonly record struct Shift(double U, double V, double Conf, bool Edge);

    /// <summary>Block matching: find (u,v) such that B(x,y) ≈ A(x-u, y-v) within the box.</summary>
    private static Shift BestShift(float[] A, float[] B, int w, int x0, int y0, int x1, int y1, int S)
    {
        // Normalised cross-correlation (see the page's bestShift): absolute difference was biased toward shifts that slide an
        // edge-of-window rain mass out of the window, which could reverse the motion.
        int n = 2 * S + 1;
        var scores = new double[n * n];
        double bb = 0; for (int y = y0; y <= y1; y++) { int rb = y * w; for (int x = x0; x <= x1; x++) bb += (double)B[rb + x] * B[rb + x]; }
        if (bb <= 0) return new Shift(0, 0, 0, false);
        double best = double.NegativeInfinity, sum = 0; int bu = 0, bv = 0;
        for (int v = -S; v <= S; v++)
            for (int u = -S; u <= S; u++)
            {
                double ab = 0, aa = 0;
                for (int y = y0; y <= y1; y++)
                {
                    int rb = y * w, ra = (y - v) * w - u;
                    for (int x = x0; x <= x1; x++) { double a = A[ra + x]; if (a > 0) { aa += a * a; ab += a * B[rb + x]; } }
                }
                double sc = aa > 0 ? ab / Math.Sqrt(aa * bb) : 0;
                scores[(v + S) * n + (u + S)] = sc; sum += sc;
                if (sc > best || (sc == best && Math.Abs(u) + Math.Abs(v) < Math.Abs(bu) + Math.Abs(bv))) { best = sc; bu = u; bv = v; }
            }
        double mean = sum / (n * n);
        double conf = best > 0 ? Math.Max(0, (best - mean) / Math.Max(1e-6, 1 - mean)) * best : 0;
        bool edge = Math.Abs(bu) == S || Math.Abs(bv) == S;
        double du = 0, dv = 0;
        if (!edge)
        {
            double At(int u, int v) => scores[(v + S) * n + (u + S)];
            double em = At(bu - 1, bv), ep = At(bu + 1, bv), den = em - 2 * best + ep;
            if (den < 0) du = Math.Clamp((em - ep) / (2 * den), -0.5, 0.5);
            double fm = At(bu, bv - 1), fp = At(bu, bv + 1), den2 = fm - 2 * best + fp;
            if (den2 < 0) dv = Math.Clamp((fm - fp) / (2 * den2), -0.5, 0.5);
        }
        return new Shift(bu + du, bv + dv, conf, edge);
    }

    public static Motion EstimateMotion(DbzGrid gA, DbzGrid gB, int dtMin, double cellKm, double maxKmh = 110, int minCells = 10, double minConf = 0.25)
    {
        int w = gB.W, h = gB.H;
        int S = Math.Max(2, (int)Math.Ceiling((maxKmh / 60) * dtMin / cellKm));
        var m = new Motion { DtMin = dtMin };
        int x0 = S, y0 = S, x1 = w - 1 - S, y1 = h - 1 - S;
        if (x1 - x0 < 8 || y1 - y0 < 8) { m.Reason = "domain too small"; return m; }
        var A = IntensityField(gA); var B = IntensityField(gB);
        if (CountRain(A, w, x0, y0, x1, y1) < minCells || CountRain(B, w, x0, y0, x1, y1) < minCells) { m.Reason = "too little rain to track"; return m; }
        double ToV(double u) => u * cellKm / dtMin;
        var g = BestShift(A, B, w, x0, y0, x1, y1, S);
        m.Vx = ToV(g.U); m.Vy = ToV(g.V); m.Conf = g.Conf;
        double gSpeed = Math.Sqrt(m.Vx * m.Vx + m.Vy * m.Vy) * 60;
        m.Ok = g.Conf >= minConf && !g.Edge && gSpeed <= maxKmh;
        if (!m.Ok) m.Reason = g.Edge ? "motion off scale" : "no clear motion";
        const int nx = 3, ny = 3;
        double rw = (x1 - x0 + 1) / (double)nx, rh = (y1 - y0 + 1) / (double)ny;
        m.RegionSize = Math.Max(rw, rh);
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                int rx0 = x0 + (int)Math.Round(i * rw), rx1 = x0 + (int)Math.Round((i + 1) * rw) - 1;
                int ry0 = y0 + (int)Math.Round(j * rh), ry1 = y0 + (int)Math.Round((j + 1) * rh) - 1;
                double cx = (rx0 + rx1) / 2.0, cy = (ry0 + ry1) / 2.0;
                bool ok = false; double vx = 0, vy = 0, conf = 0;
                if (CountRain(A, w, rx0, ry0, rx1, ry1) >= minCells && CountRain(B, w, rx0, ry0, rx1, ry1) >= minCells)
                {
                    var r = BestShift(A, B, w, rx0, ry0, rx1, ry1, S);
                    vx = ToV(r.U); vy = ToV(r.V); conf = r.Conf;
                    double sp = Math.Sqrt(vx * vx + vy * vy) * 60;
                    double devKmh = Math.Sqrt(Sq(vx - m.Vx) + Sq(vy - m.Vy)) * 60;
                    ok = r.Conf >= minConf && !r.Edge && sp <= maxKmh && (!m.Ok || devKmh <= 60);
                }
                m.Regions.Add(new RegionMotion(cx, cy, ok, vx, vy, conf));
            }
        if (!m.Ok && m.Regions.Any(r => r.Ok))
        {
            var rs = m.Regions.Where(r => r.Ok).ToList();
            m.Vx = rs.Average(r => r.Vx); m.Vy = rs.Average(r => r.Vy); m.Conf = minConf;
            m.Ok = true; m.Reason = null;
        }
        m.SpeedMph = Math.Sqrt(m.Vx * m.Vx + m.Vy * m.Vy) * 60 * 0.621371;
        m.HeadingDeg = (Math.Atan2(m.Vx, -m.Vy) * 180 / Math.PI + 360) % 360;
        return m;
    }

    /// <summary>Velocity at a fine-grid cell, blended from regional vectors (inverse distance).</summary>
    public static Func<int, int, (double Vx, double Vy)> MakeVelocityField(Motion? motion, double fineToCoarse)
    {
        if (motion is null || !motion.Ok) return (_, _) => (0, 0);
        var regs = motion.Regions.Where(r => r.Ok).ToList();
        double gvx = motion.Vx, gvy = motion.Vy;
        if (regs.Count == 0) return (_, _) => (gvx, gvy);
        double s2 = motion.RegionSize * motion.RegionSize * 0.25;
        return (x, y) =>
        {
            double X = x / fineToCoarse, Y = y / fineToCoarse;
            double sw = 0.1 / s2, vx = gvx * sw, vy = gvy * sw;
            foreach (var r in regs)
            {
                double dx = X - r.Cx, dy = Y - r.Cy, w = 1 / (dx * dx + dy * dy + s2);
                sw += w; vx += w * r.Vx; vy += w * r.Vy;
            }
            return (vx / sw, vy / sw);
        };
    }

    public static double BearingDeg(double e, double n) => (Math.Atan2(e, n) * 180 / Math.PI + 360) % 360;

    /// <summary>Project every rain cell along its motion and see if it passes over (cx, cy) (fine-cell units).</summary>
    public static Prediction PredictRain(DbzGrid grid, double cellKm, double cx, double cy, Func<int, int, (double Vx, double Vy)> vel,
        float thresh, double hitRadiusMi, double horizonMin = 120, double maxRingMi = 50, int minHitCells = 2, int[]? labels = null)
    {
        double cellMi = cellKm * 0.621371, R = hitRadiusMi + 0.7 * cellMi, R2 = R * R;
        var p = new Prediction();
        int hits = 0, hitBlob = 0; double eta = double.PositiveInfinity, end = 0, maxDbz = -99, from = 0;
        var blobCells = new Dictionary<int, int>();
        for (int y = 0; y < grid.H; y++)
            for (int x = 0; x < grid.W; x++)
            {
                float d = grid.D[y * grid.W + x];
                if (d < thresh) continue;
                double e = (x + 0.5 - cx) * cellMi, n = -(y + 0.5 - cy) * cellMi;
                double dist = Math.Sqrt(e * e + n * n);
                if (dist <= maxRingMi)
                {
                    if (dist <= 10) p.Ring10 = true;
                    if (dist <= 30) p.Ring30 = true;
                    p.Ring50 = true;
                    if (p.Nearest is null || dist < p.Nearest.DistMi) { p.Nearest = new Nearest(dist, BearingDeg(e, n), d); p.NearestBlob = labels is null ? 0 : labels[y * grid.W + x]; }
                }
                var (vx, vy) = vel(x, y);
                double ve = vx * 0.621371, vn = -vy * 0.621371;
                double a = ve * ve + vn * vn;
                double? tIn = null, tOut = null;
                if (dist * dist <= R2)
                {
                    tIn = 0;
                    if (a < 1e-9) tOut = horizonMin;
                    else { double b = 2 * (e * ve + n * vn), c = dist * dist - R2; tOut = Math.Min(horizonMin, (-b + Math.Sqrt(b * b - 4 * a * c)) / (2 * a)); }
                }
                else if (a > 1e-9)
                {
                    double b = 2 * (e * ve + n * vn), c = dist * dist - R2, disc = b * b - 4 * a * c;
                    if (disc >= 0)
                    {
                        double sq = Math.Sqrt(disc), t1 = (-b - sq) / (2 * a), t2 = (-b + sq) / (2 * a);
                        if (t2 >= 0 && t1 <= horizonMin) { tIn = Math.Max(0, t1); tOut = Math.Min(horizonMin, t2); }
                    }
                }
                if (tIn is not null)
                {
                    hits++;
                    if (tIn.Value < eta) { eta = tIn.Value; from = BearingDeg(e, n); hitBlob = labels is null ? 0 : labels[y * grid.W + x]; }
                    if (labels is not null) { int lb = labels[y * grid.W + x]; blobCells[lb] = blobCells.GetValueOrDefault(lb) + 1; }
                    if (tOut!.Value > end) end = tOut.Value;
                    if (d > maxDbz) maxDbz = d;
                }
                else if (dist <= maxRingMi)
                {
                    double t = a > 1e-9 ? Math.Max(0, Math.Min(horizonMin, -(e * ve + n * vn) / a)) : 0;
                    double pe = e + ve * t, pn = n + vn * t;
                    if (p.Passing is null || dist < p.Passing.DistMi)
                        p.Passing = new Passing(dist, BearingDeg(e, n), BearingDeg(pe, pn), Math.Sqrt(pe * pe + pn * pn));
                }
            }
        if (hits >= minHitCells) p.Hit = new Hit(eta, end, maxDbz, from, hits, hitBlob, blobCells);
        return p;
    }

    /* ---- intensity trends: track each storm cell across frames ---- */

    /// <summary>Connected rain blobs (8-neighbour) above thresh.</summary>
    public static Labelled LabelBlobs(DbzGrid g, float thresh, int minCells = 3)
    {
        int w = g.W, h = g.H; var labels = new int[w * h]; var blobs = new List<Blob>(); var stack = new Stack<int>();
        for (int i = 0; i < g.D.Length; i++)
        {
            if (g.D[i] < thresh || labels[i] != 0) continue;
            int id = blobs.Count + 1, n = 0; float peak = -99; double vol = 0, sx = 0, sy = 0;
            labels[i] = id; stack.Push(i);
            while (stack.Count > 0)
            {
                int k = stack.Pop(), x = k % w, y = k / w; float v = g.D[k];
                n++; vol += v - thresh + 1; sx += x; sy += y; if (v > peak) peak = v;
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int xx = x + dx, yy = y + dy; if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                    int kk = yy * w + xx; if (g.D[kk] >= thresh && labels[kk] == 0) { labels[kk] = id; stack.Push(kk); }
                }
            }
            blobs.Add(new Blob { Id = id, N = n, Peak = peak, Vol = vol, Cx = sx / n, Cy = sy / n, Tiny = n < minCells });
        }
        return new Labelled { Labels = labels, Blobs = blobs };
    }

    /// <summary>For each blob in the newest frame, its earlier self: back-project the centroid by the motion, pick the nearest blob of comparable size.</summary>
    public static Dictionary<int, Blob> MatchBlobs(Labelled newB, Labelled oldB, double dtMin, Func<int, int, (double Vx, double Vy)> vel, double cellKm)
    {
        var o = new Dictionary<int, Blob>();
        foreach (var b in newB.Blobs)
        {
            if (b.Tiny) continue;
            var (vx, vy) = vel((int)Math.Round(b.Cx), (int)Math.Round(b.Cy));
            double px = b.Cx - vx * dtMin / cellKm, py = b.Cy - vy * dtMin / cellKm;
            double reach = Math.Max(6, 1.5 * Math.Sqrt(b.N / Math.PI));
            Blob? best = null; double bd = double.PositiveInfinity;
            foreach (var ob in oldB.Blobs)
            {
                if (ob.Tiny) continue;
                double dd = Math.Sqrt(Sq(ob.Cx - px) + Sq(ob.Cy - py));
                if (dd < reach + Math.Sqrt(ob.N / Math.PI) && dd < bd) { bd = dd; best = ob; }
            }
            if (best is not null) o[b.Id] = best;
        }
        return o;
    }

    /// <summary>Trend for every blob in the newest frame from the earlier frames (dts = minutes back for each earlier frame).</summary>
    public static Dictionary<int, BlobTrend> BlobTrends(IReadOnlyList<Labelled> frames, IReadOnlyList<double> dts, Func<int, int, (double Vx, double Vy)> vel, double cellKm, float thresh)
    {
        var newest = frames[^1]; var trends = new Dictionary<int, BlobTrend>();
        var matches = frames.Take(frames.Count - 1).Select((f, i) => MatchBlobs(newest, f, dts[i], vel, cellKm)).ToList();
        foreach (var b in newest.Blobs)
        {
            if (b.Tiny) continue;
            var pts = new List<(double T, double Vol, double Peak, int N)> { (0, b.Vol, b.Peak, b.N) };
            for (int i = 0; i < matches.Count; i++) if (matches[i].TryGetValue(b.Id, out var ob)) pts.Add((-dts[i], ob.Vol, ob.Peak, ob.N));
            if (pts.Count < 2) { trends[b.Id] = new BlobTrend("new", 1, 1, 0, 0, null, 1); continue; }
            var oldest = pts[^1]; double span = -oldest.T;
            double volRatio = b.Vol / Math.Max(1, oldest.Vol), peakDelta = b.Peak - oldest.Peak, areaRatio = b.N / Math.Max(1.0, oldest.N);
            string word = "steady";
            if ((volRatio >= 1.35 && areaRatio >= 1.1) || peakDelta >= 7) word = "strengthening";
            else if ((volRatio <= 0.7 && areaRatio <= 0.9) || peakDelta <= -7) word = "weakening";
            double? fizzle = null;
            if (word == "weakening")
            {
                double peakRate = peakDelta / span, volRate = Math.Log(Math.Max(0.05, volRatio)) / span;
                double tPeak = peakRate < -0.05 ? (thresh - b.Peak) / peakRate : double.PositiveInfinity;
                double tVol = volRate < -0.005 ? Math.Log(0.15) / volRate : double.PositiveInfinity;
                double f = Math.Min(tPeak, tVol); if (double.IsFinite(f)) fizzle = f;
            }
            trends[b.Id] = new BlobTrend(word, volRatio, areaRatio, peakDelta, span, fizzle, pts.Count);
        }
        return trends;
    }

    private static double Sq(double v) => v * v;
}
