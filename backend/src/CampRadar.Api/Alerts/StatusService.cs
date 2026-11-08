using System.Collections.Concurrent;
using CampRadar.Api.Lightning;
using CampRadar.Api.Nowcast;
using CampRadar.Api.Nws;
using CampRadar.Api.Obs;
using CampRadar.Api.Radar;

namespace CampRadar.Api.Alerts;

public sealed record StatusOptions(double HitMi = 3, float ThreshDbz = 20, double LightningAlarmMi = 10);

public sealed class RainStatus
{
    public string Level { get; set; } = "none";   // none | passing | low | incoming | imminent | atyou
    public string Main { get; set; } = "";
    public string Sub { get; set; } = "";
    public string Color { get; set; } = "grey";
    public double? EtaMin { get; set; }
    public double? EndMin { get; set; }
    public double? MaxDbz { get; set; }
    public double? FromBearing { get; set; }
    public bool Ring10 { get; set; }
    public bool Ring30 { get; set; }
    public bool Ring50 { get; set; }
    public Nearest? Nearest { get; set; }
    public Nearest? Faint { get; set; }    // nearest echo of any strength (≥5 dBZ) within 50 mi
    public Passing? Passing { get; set; }
    public object? Edge { get; set; }      // how the nearest rain's edge has really been closing (rate vs what the motion implies)
    public object? Motion { get; set; }
    // typed copies for the verification ledger
    public bool RainNow { get; set; }        // rain at or above the threshold inside the hit radius on the latest scan
    public bool MotionOk { get; set; }
    public double SpeedMph { get; set; }
    public double HeadingDeg { get; set; }
    public double MotionConf { get; set; }
    public string? EdgeVerdict { get; set; }
    public double? EdgeRateMph { get; set; }
    public bool Fizzles { get; set; }
    public int? Pop2h { get; set; }
    public double? DewSpread { get; set; }
    public object? Trends { get; set; }
    public long LatestRadarMs { get; set; }
    public double RadarLagMin { get; set; }
    public string? Error { get; set; }
}

public sealed class Status
{
    public long AsOfMs { get; set; }
    public double Lat { get; set; }
    public double Lon { get; set; }
    public RainStatus Rain { get; set; } = new();
    public LightningSummary? Lightning { get; set; }
    public string LightningLevel { get; set; } = "none";   // none | near30 | near20 | alarm
    public List<NwsAlert> Alerts { get; set; } = new();
    public bool AlertsUnknown { get; set; }   // the NWS alert fetch failed this cycle; do not treat the empty list as "all clear"
    public string Level { get; set; } = "none";            // overall: max of rain and lightning, warnings separate
    public string Color { get; set; } = "grey";
    public string Main { get; set; } = "";
    public string Sub { get; set; } = "";
    public Status Clone() => (Status)MemberwiseClone();
}

/// <summary>Computes the full "what's coming for this spot" picture; shared by the API and the alert worker.</summary>
public sealed class StatusService
{
    private const int Zoom = 7;
    private const double AlertRangeMi = 50;   // "it will reach you" alerts wait until the arriving rain is this close
    private const double RadiusMi = 120;
    private const int FineCellPx = 2, Coarse = 2;
    private readonly MrmsClient _mrms;
    private readonly LightningStore _lightning;
    private readonly NwsClient _nws;
    private readonly NwsForecastClient _fc;
    private readonly StationObsClient _obs;
    private readonly ILogger<StatusService> _log;
    private readonly ConcurrentDictionary<string, (long At, Status S)> _cache = new();
    // per point: where the nearest rain has been over the last hour, and when a "fizzle" verdict was last given (it flickers on decaying shields)
    private sealed class PointMemory { public readonly List<(long T, double D, double B)> Edge = new(); public long FizzleSeen; public long Touched; }
    private readonly ConcurrentDictionary<string, PointMemory> _mem = new();

    public StatusService(MrmsClient mrms, LightningStore lightning, NwsClient nws, NwsForecastClient fc, StationObsClient obs, ILogger<StatusService> log)
    { _mrms = mrms; _lightning = lightning; _nws = nws; _fc = fc; _obs = obs; _log = log; }

    public async Task<Status> GetAsync(double lat, double lon, StatusOptions opt, CancellationToken ct, bool allowCached = true)
    {
        // Devices within ~0.5 mi share one computation per scan.
        var key = $"{Math.Round(lat, 2)},{Math.Round(lon, 2)}|{opt.HitMi}|{opt.ThreshDbz}";
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (allowCached && _cache.TryGetValue(key, out var hit) && hit.S.Rain.LatestRadarMs == _mrms.Latest && now - hit.At < 150_000)
            return Refresh(hit.S.Clone(), lat, lon, opt, now);   // a copy per caller: the cached one must not be mutated under other readers

        var s = new Status { AsOfMs = now, Lat = lat, Lon = lon };
        try { s.Rain = await RainAsync(lat, lon, opt, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { s.Rain = new RainStatus { Error = ex.Message, Main = "Rain tracking unavailable", Sub = ex.Message }; _log.LogWarning(ex, "rain status failed"); }
        var alerts = await _nws.ActiveAlertsAsync(lat, lon, ct);
        s.Alerts = alerts ?? new List<NwsAlert>(); s.AlertsUnknown = alerts is null;
        var done = Refresh(s, lat, lon, opt, now);
        _cache[key] = (now, done);
        if (_cache.Count > 5000) _cache.Clear();
        return done;
    }

    /// <summary>Lightning changes faster than radar, so it is recomputed on every call.</summary>
    private Status Refresh(Status s, double lat, double lon, StatusOptions opt, long now)
    {
        s.Lightning = _lightning.Summarize(lat, lon, now);
        var L = s.Lightning;
        s.LightningLevel = L.Within10 > 0 && (L.LastStrikeAgeMin ?? 99) <= 15 ? "alarm" : L.Within20 > 0 ? "near20" : L.Within30 > 0 ? "near30" : "none";
        // Overall ranking: lightning alarm outranks everything but NWS warnings (handled separately by the client/worker).
        var rank = new Dictionary<string, int> { ["none"] = 0, ["passing"] = 1, ["low"] = 2, ["incoming"] = 3, ["imminent"] = 4, ["atyou"] = 4 };
        s.Level = s.Rain.Level; s.Color = s.Rain.Color; s.Main = s.Rain.Main; s.Sub = s.Rain.Sub;
        if (s.LightningLevel == "alarm")
        {
            s.Level = "lightning"; s.Color = "red";
            s.Main = $"Lightning {Fmt(L.NearestMi)} mi {Compass(L.NearestBearing ?? 0)} — {L.Within10} strikes within 10 mi";
            s.Sub = (L.Trend == "approaching" ? "Getting closer" : L.Trend == "receding" ? "Moving away" : "Holding") + (string.IsNullOrEmpty(s.Rain.Main) ? "" : " · " + s.Rain.Main);
        }
        else if (L.Within30 > 0)
        {
            var line = $"Lightning {Fmt(L.NearestMi)} mi {Compass(L.NearestBearing ?? 0)}" + (L.Trend == "approaching" ? ", approaching" : L.Trend == "receding" ? ", moving away" : "");
            if (rank.GetValueOrDefault(s.Level) <= 1) { s.Level = L.Within20 > 0 ? "incoming" : "passing"; s.Color = L.Within20 > 0 ? "orange" : "yellow"; s.Sub = string.IsNullOrEmpty(s.Main) ? s.Sub : s.Main; s.Main = line; }
            else s.Sub = line + (string.IsNullOrEmpty(s.Sub) ? "" : " · " + s.Sub);
        }
        if (!L.DataFresh && s.Lightning.AsOfMs > 0) s.Sub = (string.IsNullOrEmpty(s.Sub) ? "" : s.Sub + " · ") + "lightning feed stale";
        return s;
    }

    private async Task<RainStatus> RainAsync(double lat, double lon, StatusOptions opt, CancellationToken ct)
    {
        var r = new RainStatus();
        if (_mrms.Times.Count == 0) await _mrms.RefreshTimesAsync(ct);
        if (_mrms.Times.Count == 0) { r.Error = "no radar times from NOAA"; r.Main = "Rain tracking unavailable"; r.Sub = r.Error; return r; }
        long latest = _mrms.Latest;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        r.LatestRadarMs = latest; r.RadarLagMin = Math.Max(0, (now - latest) / 60000.0);
        var tA = _mrms.NearestTime(latest - 30 * 60000L); var tB = _mrms.NearestTime(latest - 15 * 60000L);
        var frames = await Task.WhenAll(new[] { tA, tB, latest }.Select(t => _mrms.AnalysisFrameAsync(t, lat, lon, Zoom, RadiusMi, ct)));
        double dt1 = (latest - tA) / 60000.0, dt2 = (latest - tB) / 60000.0;
        var fine = frames.Select(f => NowcastEngine.Despeckle(NowcastEngine.GridFromRgba(f.Rgba, f.Size, f.Size, FineCellPx), 15)).ToArray();
        var coarse = fine.Select(g => NowcastEngine.DownsampleMax(g, Coarse)).ToArray();
        double fineKm = frames[2].MetersPerPx * FineCellPx / 1000, coarseKm = fineKm * Coarse;
        Motion motion = dt1 >= 10 ? NowcastEngine.EstimateMotion(coarse[0], coarse[2], (int)Math.Round(dt1), coarseKm) : new Motion { Reason = "radar history too short" };
        if (dt2 >= 6) { var m2 = NowcastEngine.EstimateMotion(coarse[1], coarse[2], (int)Math.Round(dt2), coarseKm); if (m2.Ok && (!motion.Ok || m2.Conf > motion.Conf * 1.15)) motion = m2; }   // keep the more confident baseline
        var vel = NowcastEngine.MakeVelocityField(motion, Coarse);
        double c = frames[2].Size / 2.0 / FineCellPx;
        var cleaned = fine.Select(g => NowcastEngine.Despeckle(g, opt.ThreshDbz)).ToArray();
        var labelled = cleaned.Select(g => NowcastEngine.LabelBlobs(g, opt.ThreshDbz)).ToList();
        var trends = NowcastEngine.BlobTrends(labelled, new[] { dt1, dt2 }, vel, fineKm, opt.ThreshDbz);
        var p = NowcastEngine.PredictRain(cleaned[2], fineKm, c, c, vel, opt.ThreshDbz, opt.HitMi, labels: labelled[2].Labels);
        if (p.Hit is not null)
        {
            trends.TryGetValue(p.Hit.Blob, out var tr);
            var big = p.Hit.BlobCells.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
            if (big != 0 && big != p.Hit.Blob && trends.TryGetValue(big, out var trBig) && (tr is null || tr.Word == "new")) tr = trBig;
            p.Hit.Trend = tr;
            p.Hit.Fizzles = tr is { Word: "weakening", FizzleMin: not null } && tr.FizzleMin < p.Hit.EtaMin - 2;
        }
        // edge history and fizzle memory for this point (±0.01°)
        var mem = _mem.GetOrAdd($"{Math.Round(lat, 2)},{Math.Round(lon, 2)}", _ => new PointMemory());
        EdgeCheck? edge;
        lock (mem)
        {
            mem.Touched = now;
            if (p.Nearest is not null && !mem.Edge.Any(e => e.T == latest)) mem.Edge.Add((latest, p.Nearest.DistMi, p.Nearest.Bearing));
            mem.Edge.RemoveAll(e => latest - e.T > 60 * 60000L);
            edge = EdgeRate(mem.Edge, latest, p.Nearest, motion);
            if (p.Hit is not null)
            {
                if (p.Hit.Fizzles) mem.FizzleSeen = now;
                else if (mem.FizzleSeen > 0 && now - mem.FizzleSeen < 20 * 60000L && p.Hit.Trend?.Word != "strengthening" && p.Hit.MaxDbz < 40) p.Hit.Fizzles = true;
            }
        }
        if (_mem.Count > 5000) foreach (var k in _mem.Where(kv => now - kv.Value.Touched > 2 * 3600_000L).Select(kv => kv.Key).ToList()) _mem.TryRemove(k, out _);
        r.Edge = edge is null ? null : new { edge.RateMph, edge.ClosingMph, edge.SpanMin, edge.Verdict, edge.EtaMin };
        // light echoes under a dry forecast or dry air mostly evaporate before reaching the ground: only worth two cheap cached lookups when that could matter
        int? pop2h = null; double? dewSpread = null;
        if (p.Hit is not null && p.Hit.MaxDbz < 35 && p.Hit.DistMi <= AlertRangeMi)
        {
            try { var fc = await _fc.GetAsync(lat, lon, ct); var hrs = fc?.Hours.Where(h => h.T <= DateTimeOffset.UtcNow.AddHours(2) && h.T.AddHours(1) > DateTimeOffset.UtcNow).ToList(); if (hrs is { Count: > 0 }) pop2h = hrs.Max(h => h.Pop); } catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "pop lookup"); }
            try { var ob = (await _obs.NearbyAsync(lat, lon, ct))?.Best; if (ob is { TempF: not null, DewF: not null }) dewSpread = ob.TempF - ob.DewF; } catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "obs lookup"); }
        }
        if (p.NearestBlob != 0 && trends.TryGetValue(p.NearestBlob, out var nt)) p.NearestTrend = nt;
        r.Trends = new { strengthening = trends.Values.Count(t => t.Word == "strengthening"), weakening = trends.Values.Count(t => t.Word == "weakening"), steady = trends.Values.Count(t => t.Word == "steady"), fresh = trends.Values.Count(t => t.Word == "new"), arriving = p.Hit?.Trend?.Word, fizzles = p.Hit?.Fizzles ?? false };
        r.Ring10 = p.Ring10; r.Ring30 = p.Ring30; r.Ring50 = p.Ring50; r.Nearest = p.Nearest; r.Passing = p.Passing;
        r.Faint = NearestEcho(fine[2], fineKm, c, c, 5, 50);
        r.Motion = new { motion.Ok, motion.Reason, motion.SpeedMph, motion.HeadingDeg, motion.Conf, Regions = motion.Regions.Count(x => x.Ok) };
        r.RainNow = p.Nearest is not null && p.Nearest.DistMi <= opt.HitMi; r.MotionOk = motion.Ok; r.SpeedMph = motion.SpeedMph; r.HeadingDeg = motion.HeadingDeg; r.MotionConf = motion.Conf;
        r.EdgeVerdict = edge?.Verdict; r.EdgeRateMph = edge?.RateMph; r.Fizzles = p.Hit?.Fizzles ?? false; r.Pop2h = pop2h; r.DewSpread = dewSpread;
        Describe(r, p, motion, edge, pop2h, dewSpread);
        return r;
    }

    private sealed record EdgeCheck(double RateMph, double ClosingMph, double SpanMin, string Verdict, double? EtaMin);
    /// <summary>
    /// Is the nearest rain really closing at the rate the storm motion implies? Broad shields evaporate at their leading edge: the cells
    /// inside move at 35 mph while the edge creeps or retreats. Mirrors the page's edgeCheck(); null until there is 20 min of history.
    /// </summary>
    private static EdgeCheck? EdgeRate(List<(long T, double D, double B)> hist, long now, Nearest? nearest, Motion m)
    {
        if (nearest is null || !m.Ok) return null;
        var same = hist.Where(h => now - h.T <= 40 * 60000L && Math.Abs(((h.B - nearest.Bearing) % 360 + 540) % 360 - 180) <= 60).OrderBy(h => h.T).ToList();
        if (same.Count < 4 || now - same[0].T < 20 * 60000L) return null;
        int n = same.Count; long t0 = same[0].T; double st = 0, sd = 0, stt = 0, std = 0;
        foreach (var h in same) { double u = (h.T - t0) / 60000.0; st += u; sd += h.D; stt += u * u; std += u * h.D; }
        double den = n * stt - st * st; if (den <= 0) return null;
        double rate = -(n * std - st * sd) / den * 60;                                                   // mph, positive = getting closer
        double closing = m.SpeedMph * Math.Cos((m.HeadingDeg - (nearest.Bearing + 180)) * Math.PI / 180);   // mph the motion says it should close at
        double span = (now - t0) / 60000.0;
        if (closing >= 12 && rate <= 5) return new EdgeCheck(rate, closing, span, "stalled", null);
        if (closing > 0 && rate < 0.6 * closing) return new EdgeCheck(rate, closing, span, "slow", nearest.DistMi / Math.Max(rate, 3) * 60);
        return new EdgeCheck(rate, closing, span, "ok", null);
    }

    /// <summary>Same wording rules as the page's computeStatus().</summary>
    private static void Describe(RainStatus r, Prediction p, Motion m, EdgeCheck? edge, int? pop2h, double? dewSpread)
    {
        double lag = r.RadarLagMin;
        string moving = m.Ok && m.SpeedMph >= 3 ? $"Storms moving {Compass(m.HeadingDeg)} at {Math.Round(m.SpeedMph)} mph" : m.Ok ? "Rain nearly stationary" : "";
        var hit = p.Hit;
        string trendWord = hit?.Trend?.Word == "strengthening" ? ", strengthening" : hit?.Trend?.Word == "weakening" ? ", weakening" : "";
        bool dryAir = dewSpread is >= 15;
        bool lowConf = hit is not null && pop2h is < 10 && (dryAir ? hit.MaxDbz < 35 : hit.EtaMin > 15 && hit.MaxDbz < 30);
        bool edgeSlow = hit is not null && edge is { Verdict: "slow", EtaMin: not null } && edge.EtaMin > hit.EtaMin;
        if (hit is not null && hit.Fizzles && hit.EtaMin - lag > 10 && hit.MaxDbz < 40)
        {
            r.Color = "yellow"; r.Level = "low"; r.EtaMin = Math.Max(0, hit.EtaMin - lag); r.MaxDbz = hit.MaxDbz; r.FromBearing = hit.FromBearing;
            r.Main = $"Rain to the {CompassLong(hit.FromBearing)} is weakening — may fizzle before it reaches you";
            r.Sub = $"If it holds together: {Intensity(hit.MaxDbz).ToLowerInvariant()} rain in {FmtDur(Math.Max(0, hit.EtaMin - lag))}" + (moving.Length > 0 ? " · " + moving : "");
            return;
        }
        if (hit is not null && !lowConf && edge is { Verdict: "stalled" } && hit.MaxDbz < 40 && hit.EtaMin - lag > 5 && p.Nearest is not null)
        {
            r.Color = "yellow"; r.Level = "low"; r.EtaMin = Math.Max(0, hit.EtaMin - lag); r.MaxDbz = hit.MaxDbz; r.FromBearing = hit.FromBearing;
            r.Main = $"Rain to the {CompassLong(p.Nearest.Bearing)} is not getting closer — likely evaporating at its edge";
            r.Sub = $"Its cells move {Math.Round(m.SpeedMph)} mph but the edge has held near {Math.Round(p.Nearest.DistMi)} mi for {Math.Round(edge.SpanMin)} min" + (p.Nearest.DistMi <= 15 ? $" · {Intensity(hit.MaxDbz).ToLowerInvariant()} rain possible if it holds together" : "");
            return;
        }
        if (hit is not null && lowConf)
        {
            r.Color = "yellow"; r.Level = "low"; r.EtaMin = Math.Max(0, hit.EtaMin - lag); r.MaxDbz = hit.MaxDbz; r.FromBearing = hit.FromBearing;
            r.Main = $"Light rain may reach you in {FmtDur(Math.Max(0, hit.EtaMin - lag))}";
            r.Sub = $"Low confidence — NWS shows {pop2h}% chance in the next 2 hours" + (dryAir ? $" and the air is dry (dew point {Math.Round(dewSpread!.Value)}° below the temperature), so it may evaporate before reaching the ground" : "");
            return;
        }
        if (hit is not null && hit.DistMi > AlertRangeMi)
        {   // heading this way but still far out: informational only, no alert until it is inside the alert range
            double eta = Math.Max(0, hit.EtaMin - lag);
            r.Color = "blue"; r.Level = "watch"; r.EtaMin = eta; r.MaxDbz = hit.MaxDbz; r.FromBearing = hit.FromBearing;
            r.Main = $"Rain {Math.Round(hit.DistMi)} mi {Compass(hit.FromBearing)} is heading this way";
            r.Sub = $"About {FmtDur(eta)} out at current speed" + (moving.Length > 0 ? " · " + moving : "") + $" · alerts start inside {AlertRangeMi} mi";
            return;
        }
        if (hit is not null)
        {
            // when the rain's edge is closing more slowly than its cells move, the edge decides the arrival time
            double etaRaw = edgeSlow ? Math.Min(120, edge!.EtaMin!.Value) : hit.EtaMin, eta = Math.Max(0, etaRaw - lag); string word = Intensity(hit.MaxDbz) + trendWord;
            string edgeNote = edgeSlow ? $" · edge closing at {Math.Max(1, Math.Round(edge!.RateMph))} mph" : "";
            r.EtaMin = eta; r.EndMin = Math.Max(0, hit.EndMin - lag); r.MaxDbz = hit.MaxDbz; r.FromBearing = hit.FromBearing;
            if (hit.EtaMin <= 0.5) { r.Color = "red"; r.Level = "atyou"; r.Main = $"{word} rain at your location"; r.Sub = hit.EndMin < 115 ? $"Likely ending in about {Math.Round(Math.Max(1, hit.EndMin - lag))} min" : "May continue for a while"; }
            else if (eta <= 10) { r.Color = "red"; r.Level = "imminent"; r.Main = $"Rain arriving in ~{Math.Max(1, Math.Round(eta))} min"; r.Sub = $"{word} rain from the {CompassLong(hit.FromBearing)}" + (moving.Length > 0 ? " · " + moving : ""); }
            else { r.Color = "orange"; r.Level = "incoming"; r.Main = $"Rain arriving in {FmtDur(eta)}"; r.Sub = $"{word} rain from the {CompassLong(hit.FromBearing)}" + (moving.Length > 0 ? " · " + moving : "") + edgeNote; }
        }
        else if (!m.Ok && p.Nearest is not null && p.Nearest.DistMi <= 10)
        { r.Color = "orange"; r.Level = "incoming"; r.Main = $"Rain {Math.Round(p.Nearest.DistMi)} mi {Compass(p.Nearest.Bearing)}"; r.Sub = $"Movement unclear ({m.Reason ?? "not enough data"}) — keep an eye on it"; }
        else if (p.Passing is not null)
        {
            var q = p.Passing; bool mv = m.Ok && m.SpeedMph >= 3;
            r.Color = "blue"; r.Level = "passing";
            r.Main = $"Rain {Math.Round(q.DistMi)} mi {Compass(q.Bearing)} — " + (mv ? $"passing to the {CompassLong(q.PassBearing)}" : "not heading your way");
            r.Sub = (mv ? $"Closest ≈ {Math.Max(1, Math.Round(q.ClosestMi))} mi · " : "") + (moving.Length > 0 ? moving : "Movement unclear")
                + (p.NearestTrend?.Word == "strengthening" ? " · nearest storm strengthening" : p.NearestTrend?.Word == "weakening" ? " · nearest storm weakening" : "");
        }
        else { r.Color = "green"; r.Level = "none"; r.Main = "No rain heading your way"; r.Sub = p.Nearest is not null ? "" : r.Faint is { } fe ? $"Only very light echoes within 50 mi (nearest {Math.Max(1, Math.Round(fe.DistMi))} mi {Compass(fe.Bearing)}) — below the rain threshold" : "Clear within 50 miles"; }
    }

    /// <summary>Nearest cell at or above thresh within maxRing miles, any strength; mirrors the page's nearestEcho().</summary>
    private static Nearest? NearestEcho(DbzGrid g, double cellKm, double cx, double cy, double thresh, double maxRing)
    {
        double cellMi = cellKm * 0.621371; Nearest? best = null;
        for (int y = 0; y < g.H; y++) for (int x = 0; x < g.W; x++)
        {
            double d = g.D[y * g.W + x]; if (d < thresh) continue;
            double e = (x + 0.5 - cx) * cellMi, n = -(y + 0.5 - cy) * cellMi, dist = Math.Sqrt(e * e + n * n);
            if (dist <= maxRing && (best is null || dist < best.DistMi)) best = new Nearest(dist, (Math.Atan2(e, n) * 180 / Math.PI + 360) % 360, d);
        }
        return best;
    }

    private static readonly string[] Dirs = { "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW" };
    private static readonly string[] DirsLong = { "north", "northeast", "northeast", "east", "east", "southeast", "southeast", "south", "south", "southwest", "southwest", "west", "west", "northwest", "northwest", "north" };
    public static string Compass(double d) => Dirs[(int)Math.Round(((d % 360) + 360) % 360 / 22.5) % 16];
    public static string CompassLong(double d) => DirsLong[(int)Math.Floor(((d % 360) + 360) % 360 / 22.5) % 16];
    private static string Intensity(double dbz) => dbz < 30 ? "Light" : dbz < 40 ? "Moderate" : dbz < 50 ? "Heavy" : "Very heavy";
    private static string FmtDurExact(double min) { min = Math.Round(min); if (min < 60) return min + " min"; int h = (int)(min / 60), mm = (int)(min % 60); return h + " h" + (mm > 0 ? " " + mm + " min" : ""); }
    // 10-minute steps with "just over" (1–4 past) / "just under" (5–9 past), matching the page; under 10 minutes stays exact.
    private static string FmtDur(double min) { min = Math.Round(min); if (min < 10) return FmtDurExact(min); double d = min % 10; return d == 0 ? FmtDurExact(min) : d < 5 ? "just over " + FmtDurExact(min - d) : "just under " + FmtDurExact(min - d + 10); }
    private static string Fmt(double? mi) => mi is null ? "?" : Math.Max(1, Math.Round(mi.Value)).ToString();
}
