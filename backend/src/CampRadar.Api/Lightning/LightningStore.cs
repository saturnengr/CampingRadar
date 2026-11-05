namespace CampRadar.Api.Lightning;

public readonly record struct Flash(long TimeMs, float Lat, float Lon, float EnergyJ);

public sealed record LightningSummary(
    int Within10, int Within20, int Within30,
    double? NearestMi, double? NearestBearing, double? LastStrikeAgeMin,
    string Trend,                     // "approaching" | "receding" | "steady" | "none"
    int WindowMin, long AsOfMs, bool DataFresh);

/// <summary>In-memory ring of recent flashes (last hour) with point queries.</summary>
public sealed class LightningStore
{
    private readonly object _lock = new();
    private readonly List<Flash> _flashes = new();
    private readonly HashSet<string> _seenFiles = new();
    public long LastIngestMs { get; private set; }
    public long NewestFlashMs { get; private set; }
    public int Count { get { lock (_lock) return _flashes.Count; } }

    public bool MarkFileSeen(string key) { lock (_lock) return _seenFiles.Add(key); }

    public void Add(IEnumerable<Flash> flashes, long nowMs)
    {
        lock (_lock)
        {
            foreach (var f in flashes) { _flashes.Add(f); if (f.TimeMs > NewestFlashMs) NewestFlashMs = f.TimeMs; }
            LastIngestMs = nowMs;
            long cutoff = nowMs - 60 * 60000L;
            _flashes.RemoveAll(f => f.TimeMs < cutoff);
            if (_seenFiles.Count > 2000) _seenFiles.Clear();
        }
    }

    public static double DistMi(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 3958.8, r = Math.PI / 180;
        double dLat = (lat2 - lat1) * r, dLon = (lon2 - lon1) * r;
        double s = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1 * r) * Math.Cos(lat2 * r) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Sqrt(s));
    }

    public static double Bearing(double lat1, double lon1, double lat2, double lon2)
    {
        double r = Math.PI / 180, dLon = (lon2 - lon1) * r;
        double y = Math.Sin(dLon) * Math.Cos(lat2 * r);
        double x = Math.Cos(lat1 * r) * Math.Sin(lat2 * r) - Math.Sin(lat1 * r) * Math.Cos(lat2 * r) * Math.Cos(dLon);
        return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
    }

    public LightningSummary Summarize(double lat, double lon, long nowMs, int windowMin = 30)
    {
        Flash[] snap; long lastIngest;
        lock (_lock) { snap = _flashes.ToArray(); lastIngest = LastIngestMs; }
        long cutoff = nowMs - windowMin * 60000L;
        int n10 = 0, n20 = 0, n30 = 0; double? nearest = null, nearestBearing = null; long newest = 0;
        double recentMin = double.PositiveInfinity, olderMin = double.PositiveInfinity;   // nearest in last 10 min vs the 10 before
        // quick bounding box: 30 mi ≈ 0.44° lat
        double dLat = 0.45, dLon = 0.45 / Math.Max(0.2, Math.Cos(lat * Math.PI / 180));
        foreach (var f in snap)
        {
            if (f.TimeMs < cutoff) continue;
            if (Math.Abs(f.Lat - lat) > dLat || Math.Abs(f.Lon - lon) > dLon) continue;
            double d = DistMi(lat, lon, f.Lat, f.Lon);
            if (d > 30) continue;
            n30++; if (d <= 20) n20++; if (d <= 10) n10++;
            if (nearest is null || d < nearest) { nearest = d; nearestBearing = Bearing(lat, lon, f.Lat, f.Lon); }
            if (f.TimeMs > newest) newest = f.TimeMs;
            long age = nowMs - f.TimeMs;
            if (age <= 10 * 60000L) recentMin = Math.Min(recentMin, d);
            else if (age <= 20 * 60000L) olderMin = Math.Min(olderMin, d);
        }
        string trend = "none";
        if (n30 > 0)
        {
            if (double.IsFinite(recentMin) && double.IsFinite(olderMin))
                trend = recentMin < olderMin - 2 ? "approaching" : recentMin > olderMin + 2 ? "receding" : "steady";
            else if (double.IsFinite(recentMin)) trend = "steady";
            else trend = "receding";
        }
        bool fresh = lastIngest > 0 && nowMs - lastIngest < 5 * 60000L;
        return new LightningSummary(n10, n20, n30, nearest, nearestBearing,
            newest > 0 ? (nowMs - newest) / 60000.0 : null, trend, windowMin, nowMs, fresh);
    }

    /// <summary>Recent flashes inside a box, for drawing on the map.</summary>
    public IReadOnlyList<Flash> Recent(double minLat, double minLon, double maxLat, double maxLon, long sinceMs, int limit = 2000)
    {
        lock (_lock)
            return _flashes.Where(f => f.TimeMs >= sinceMs && f.Lat >= minLat && f.Lat <= maxLat && f.Lon >= minLon && f.Lon <= maxLon)
                           .OrderByDescending(f => f.TimeMs).Take(limit).ToList();
    }
}
