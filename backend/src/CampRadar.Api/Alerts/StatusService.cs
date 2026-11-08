using System.Collections.Concurrent;
using CampRadar.Api.Lightning;
using CampRadar.Api.Nowcast;
using CampRadar.Api.Nws;
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
    public object? Motion { get; set; }
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
    public string Level { get; set; } = "none";            // overall: max of rain and lightning, warnings separate
    public string Color { get; set; } = "grey";
    public string Main { get; set; } = "";
    public string Sub { get; set; } = "";
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
    private readonly ILogger<StatusService> _log;
    private readonly ConcurrentDictionary<string, (long At, Status S)> _cache = new();

    public StatusService(MrmsClient mrms, LightningStore lightning, NwsClient nws, ILogger<StatusService> log)
    { _mrms = mrms; _lightning = lightning; _nws = nws; _log = log; }

    public async Task<Status> GetAsync(double lat, double lon, StatusOptions opt, CancellationToken ct, bool allowCached = true)
    {
        // Devices within ~0.5 mi share one computation per scan.
        var key = $"{Math.Round(lat, 2)},{Math.Round(lon, 2)}|{opt.HitMi}|{opt.ThreshDbz}";
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (allowCached && _cache.TryGetValue(key, out var hit) && hit.S.Rain.LatestRadarMs == _mrms.Latest && now - hit.At < 150_000)
            return Refresh(hit.S, lat, lon, opt, now);

        var s = new Status { AsOfMs = now, Lat = lat, Lon = lon };
        try { s.Rain = await RainAsync(lat, lon, opt, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { s.Rain = new RainStatus { Error = ex.Message, Main = "Rain tracking unavailable", Sub = ex.Message }; _log.LogWarning(ex, "rain status failed"); }
        s.Alerts = await _nws.ActiveAlertsAsync(lat, lon, ct);
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
        if (p.NearestBlob != 0 && trends.TryGetValue(p.NearestBlob, out var nt)) p.NearestTrend = nt;
        r.Trends = new { strengthening = trends.Values.Count(t => t.Word == "strengthening"), weakening = trends.Values.Count(t => t.Word == "weakening"), steady = trends.Values.Count(t => t.Word == "steady"), fresh = trends.Values.Count(t => t.Word == "new"), arriving = p.Hit?.Trend?.Word, fizzles = p.Hit?.Fizzles ?? false };
        r.Ring10 = p.Ring10; r.Ring30 = p.Ring30; r.Ring50 = p.Ring50; r.Nearest = p.Nearest; r.Passing = p.Passing;
        r.Faint = NearestEcho(fine[2], fineKm, c, c, 5, 50);
        r.Motion = new { motion.Ok, motion.Reason, motion.SpeedMph, motion.HeadingDeg, motion.Conf, Regions = motion.Regions.Count(x => x.Ok) };
        Describe(r, p, motion);
        return r;
    }

    /// <summary>Same wording rules as the page's computeStatus().</summary>
    private static void Describe(RainStatus r, Prediction p, Motion m)
    {
        double lag = r.RadarLagMin;
        string moving = m.Ok && m.SpeedMph >= 3 ? $"Storms moving {Compass(m.HeadingDeg)} at {Math.Round(m.SpeedMph)} mph" : m.Ok ? "Rain nearly stationary" : "";
        var hit = p.Hit;
        string trendWord = hit?.Trend?.Word == "strengthening" ? ", strengthening" : hit?.Trend?.Word == "weakening" ? ", weakening" : "";
        if (hit is not null && hit.Fizzles && hit.EtaMin - lag > 10 && hit.MaxDbz < 40)
        {
            r.Color = "yellow"; r.Level = "low"; r.EtaMin = Math.Max(0, hit.EtaMin - lag); r.MaxDbz = hit.MaxDbz; r.FromBearing = hit.FromBearing;
            r.Main = $"Rain to the {CompassLong(hit.FromBearing)} is weakening — may fizzle before it reaches you";
            r.Sub = $"If it holds together: {Intensity(hit.MaxDbz).ToLowerInvariant()} rain in {FmtDur(Math.Max(0, hit.EtaMin - lag))}" + (moving.Length > 0 ? " · " + moving : "");
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
            double eta = Math.Max(0, hit.EtaMin - lag); string word = Intensity(hit.MaxDbz) + trendWord;
            r.EtaMin = eta; r.EndMin = Math.Max(0, hit.EndMin - lag); r.MaxDbz = hit.MaxDbz; r.FromBearing = hit.FromBearing;
            if (hit.EtaMin <= 0.5) { r.Color = "red"; r.Level = "atyou"; r.Main = $"{word} rain at your location"; r.Sub = hit.EndMin < 115 ? $"Likely ending in about {Math.Round(Math.Max(1, hit.EndMin - lag))} min" : "May continue for a while"; }
            else if (eta <= 10) { r.Color = "red"; r.Level = "imminent"; r.Main = $"Rain arriving in ~{Math.Max(1, Math.Round(eta))} min"; r.Sub = $"{word} rain from the {CompassLong(hit.FromBearing)}" + (moving.Length > 0 ? " · " + moving : ""); }
            else { r.Color = "orange"; r.Level = "incoming"; r.Main = $"Rain arriving in {FmtDur(eta)}"; r.Sub = $"{word} rain from the {CompassLong(hit.FromBearing)}" + (moving.Length > 0 ? " · " + moving : ""); }
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
