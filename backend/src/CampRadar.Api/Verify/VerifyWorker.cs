using System.Globalization;
using System.Text.Json;
using CampRadar.Api.Alerts;
using CampRadar.Api.Obs;
using CampRadar.Api.Radar;

namespace CampRadar.Api.Verify;

/// <summary>
/// Watches the sample airports: writes the tracker's verdict for each into the ledger every few minutes, pulls each
/// airport's own observation (rain gauge, present weather) as ground truth, starts a radar recording on its own when
/// something is tracked toward a point, and attaches outcomes to ledger rows once two hours have passed.
/// Settings: Verify:Enabled (true), Verify:IntervalMin (10), Verify:MaxAutoRecordings (6), Verify:Points.
/// </summary>
public sealed class VerifyWorker : BackgroundService
{
    private readonly StatusService _status; private readonly Ledger _ledger; private readonly Recorder _rec; private readonly IHttpClientFactory _f; private readonly MadisProvider _madis; private readonly ILogger<VerifyWorker> _log;
    private readonly Dictionary<string, HashSet<string>> _seenMadis = new();   // per recording: station|minute already written to gauge-madis.jsonl
    public System.Collections.Concurrent.ConcurrentDictionary<string, string> Backfills { get; } = new();
    private readonly SamplePoint[] _points; private readonly bool _enabled; private readonly int _intervalMin, _maxAuto;
    private readonly Dictionary<string, long> _lastGauge = new();
    public DateTimeOffset LastCycle { get; private set; }
    public string LastNote { get; private set; } = "not yet";

    public VerifyWorker(StatusService status, Ledger ledger, Recorder rec, IHttpClientFactory f, MadisProvider madis, IConfiguration cfg, ILogger<VerifyWorker> log)
    {
        _status = status; _ledger = ledger; _rec = rec; _f = f; _madis = madis; _log = log;
        _points = SamplePoints.FromConfig(cfg);
        _enabled = !string.Equals(cfg["Verify:Enabled"], "false", StringComparison.OrdinalIgnoreCase);
        _intervalMin = int.TryParse(cfg["Verify:IntervalMin"], out var i) ? Math.Clamp(i, 3, 60) : 10;
        _maxAuto = int.TryParse(cfg["Verify:MaxAutoRecordings"], out var m) ? Math.Clamp(m, 0, 20) : 6;
    }
    public IEnumerable<SamplePoint> Points => _points;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_enabled) { LastNote = "disabled"; return; }
        await Task.Delay(TimeSpan.FromSeconds(45), ct);   // let the radar times and the alert worker settle first
        while (!ct.IsCancellationRequested)
        {
            var started = DateTimeOffset.UtcNow;
            try { LastNote = await RunOnceAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "verify cycle"); LastNote = "failed: " + ex.Message; }
            LastCycle = DateTimeOffset.UtcNow;
            var wait = TimeSpan.FromMinutes(_intervalMin) - (DateTimeOffset.UtcNow - started);
            await Task.Delay(wait < TimeSpan.FromSeconds(20) ? TimeSpan.FromSeconds(20) : wait, ct);
        }
    }

    public async Task<string> RunOnceAsync(CancellationToken ct)
    {
        int logged = 0, recs = 0, gauges = 0;
        var pause = TimeSpan.FromMilliseconds(Math.Max(500, _intervalMin * 60000 / Math.Max(1, _points.Length) / 3));   // spread the NOAA calls over a third of the interval
        foreach (var p in _points)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var s = await _status.GetAsync(p.Lat, p.Lon, new StatusOptions(), ct);
                _ledger.Log("sample", p.Id, p.Lat, p.Lon, s);
                logged++;
                if (ShouldRecord(s) && _maxAuto > 0 && !_rec.IsRunning(p.Id + "-") && _rec.RunningCount() < _maxAuto + 5)
                {
                    _rec.Start(p.Id + "-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmm"), p.Lat, p.Lon, 6, 200);
                    recs++; _log.LogInformation("auto recording {Point}: {Main}", p.Id, s.Rain.Main);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "verify {Point}", p.Id); }
            try { if (await GaugeAsync(p, ct)) gauges++; } catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "gauge {Point}", p.Id); }
            await Task.Delay(pause, ct);
        }
        int madis = 0;
        try { madis = await MadisCycleAsync(ct); } catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "madis cycle"); }
        int resolved = _ledger.Resolve();
        return $"{logged} points logged, {gauges} gauge reports, {madis} MADIS gauge rows, {recs} recordings started, {resolved} outcomes resolved";
    }

    /// <summary>
    /// MADIS mesonet ground truth: every reporting station near each running recording goes into that recording's gauge-madis.jsonl
    /// (hundreds of rain gauges per case instead of one airport), and stations within 8 mi of a sample point go into the ledger as
    /// gauge reports for that point. The half-degree cell cache means a Texas cluster of recordings costs one or two NOAA queries.
    /// </summary>
    private async Task<int> MadisCycleAsync(CancellationToken ct)
    {
        if (!_madis.Enabled) return 0;
        int n = 0;
        foreach (var job in _rec.RunningJobs())
        {
            ct.ThrowIfCancellationRequested();
            var obs = await _madis.CellAsync(job.Lat, job.Lon, ct);
            var seen = _seenMadis.TryGetValue(job.Name, out var hs) ? hs : _seenMadis[job.Name] = new HashSet<string>();
            if (seen.Count > 200000) seen.Clear();
            foreach (var o in obs)
            {
                var key = o.Id + "|" + o.At.ToUnixTimeMilliseconds();
                if (!seen.Add(key)) continue;
                _rec.AppendAux(job.Name, "gauge-madis.jsonl", GaugeLine(o, job.Lat, job.Lon)); n++;
            }
        }
        foreach (var name in _seenMadis.Keys.Where(k => !_rec.IsRunning(k)).ToList()) _seenMadis.Remove(name);
        foreach (var p in _points)
        {
            if (!_madis.IsWarm(p.Lat, p.Lon)) continue;   // only cells some recording or status call already fetched; never a query of its own
            var obs = await _madis.CellAsync(p.Lat, p.Lon, ct);
            foreach (var o in obs.Where(o => MadisProvider.Haversine(p.Lat, p.Lon, o.Lat, o.Lon) <= 8).OrderByDescending(o => o.Rain1hIn ?? -1).ThenBy(o => MadisProvider.Haversine(p.Lat, p.Lon, o.Lat, o.Lon)).Take(3))
                _ledger.LogGauge(p.Id, o.At.ToUnixTimeMilliseconds(), o.Rain1hIn is { } r ? r * 25.4 : null, o.PresentWx.Length > 0 ? o.PresentWx : null, o.TempF, o.DewF, "madis:" + o.Id + (o.PresentWx.Length > 0 ? " " + o.PresentWx : ""));
        }
        return n;
    }

    private static string GaugeLine(MadisProvider.Ob o, double lat0, double lon0) => JsonSerializer.Serialize(new
    {
        t = o.At.ToUnixTimeMilliseconds(), id = o.Id, prov = o.Provider, lat = Math.Round(o.Lat, 4), lon = Math.Round(o.Lon, 4),
        distMi = Math.Round(MadisProvider.Haversine(lat0, lon0, o.Lat, o.Lon), 1), pcp1hIn = o.Rain1hIn is { } r ? Math.Round(r, 3) : (double?)null,
        rateInHr = o.RateInHr is { } rr ? Math.Round(rr, 3) : (double?)null, wx = o.PresentWx.Length > 0 ? o.PresentWx : null,
        tempF = o.TempF is { } t ? Math.Round(t, 1) : (double?)null, dewF = o.DewF is { } d ? Math.Round(d, 1) : (double?)null
    });

    /// <summary>
    /// Fill gauge-madis.jsonl for a finished recording from MADIS's past-time queries: one query per hour of the recording's window,
    /// ±100 mi box. Runs in the background (a 12-hour recording is a dozen 20–40 s queries); progress in <see cref="Backfills"/>.
    /// </summary>
    public void StartBackfill(string name)
    {
        name = Recorder.SafeName(name);
        var meta = _rec.Meta(name);
        if (meta is null) { Backfills[name] = "no such recording"; return; }
        if (Backfills.TryGetValue(name, out var cur) && cur.StartsWith("running")) return;
        Backfills[name] = "running: 0 hours";
        _ = Task.Run(async () =>
        {
            try
            {
                var (lat, lon, _, from, to) = meta.Value;
                double dLat = 100 / 69.0, dLon = 100 / (69.0 * Math.Cos(lat * Math.PI / 180));
                var seen = new HashSet<string>();
                int rows = 0, hours = 0;
                var start = new DateTimeOffset(from.Year, from.Month, from.Day, from.Hour, 0, 0, TimeSpan.Zero).AddHours(1);
                for (var t = start; t <= to.AddHours(1); t = t.AddHours(1))
                {
                    var obs = await _madis.RegionAsync(lat - dLat, lon - dLon, lat + dLat, lon + dLon, t, CancellationToken.None);
                    foreach (var o in obs) if (seen.Add(o.Id + "|" + o.At.ToUnixTimeMilliseconds())) { _rec.AppendAux(name, "gauge-madis.jsonl", GaugeLine(o, lat, lon)); rows++; }
                    hours++; Backfills[name] = $"running: {hours} hours, {rows} rows";
                    await Task.Delay(2000);
                }
                Backfills[name] = $"done: {hours} hours, {rows} rows";
            }
            catch (Exception ex) { Backfills[name] = "failed: " + ex.Message; _log.LogWarning(ex, "madis backfill {Name}", name); }
        });
    }

    /// <summary>Record when the tracker has something for this point: a hit at any range, or rain inside 60 mi with a motion fix.</summary>
    private static bool ShouldRecord(Status s)
    {
        var r = s.Rain;
        if (r.Error is not null) return false;
        if (r.Level is "watch" or "low" or "incoming" or "imminent" or "atyou") return true;
        return r.Nearest is not null && r.Nearest.DistMi <= 60 && r.MotionOk;
    }

    /// <summary>The airport's latest observation from the NWS API: hourly precipitation, present weather, temperature and dew point.</summary>
    private async Task<bool> GaugeAsync(SamplePoint p, CancellationToken ct)
    {
        var http = _f.CreateClient("noaa");
        using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.weather.gov/stations/{p.Id}/observations/latest", ct));
        var pr = doc.RootElement.GetProperty("properties");
        if (!DateTimeOffset.TryParse(pr.GetProperty("timestamp").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts)) return false;
        long t = ts.ToUnixTimeMilliseconds();
        if (_lastGauge.TryGetValue(p.Id, out var last) && last == t) return false;
        _lastGauge[p.Id] = t;
        double? Num(string name) => pr.TryGetProperty(name, out var e) && e.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        double? mm = Num("precipitationLastHour"), tc = Num("temperature"), dc = Num("dewpoint");
        string? wx = pr.TryGetProperty("textDescription", out var td) ? td.GetString() : null;
        string present = pr.TryGetProperty("presentWeather", out var pw) && pw.ValueKind == JsonValueKind.Array
            ? string.Join(" ", pw.EnumerateArray().Select(x => x.TryGetProperty("rawString", out var rs) ? rs.GetString() : null).Where(x => x is not null)) : "";
        _ledger.LogGauge(p.Id, t, mm, wx, tc is null ? null : tc * 9 / 5 + 32, dc is null ? null : dc * 9 / 5 + 32, present);
        // ground truth rides along inside any recording of this point
        foreach (var name in _rec.RunningNames().Where(n => n.StartsWith(p.Id + "-", StringComparison.Ordinal)))
            _rec.AppendAux(name, "gauge.jsonl", JsonSerializer.Serialize(new { t, station = p.Id, precipMm = mm, wx, tempF = tc is null ? (double?)null : Math.Round(tc.Value * 9 / 5 + 32, 1), dewF = dc is null ? (double?)null : Math.Round(dc.Value * 9 / 5 + 32, 1), present }));
        return true;
    }
}
