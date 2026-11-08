using System.Globalization;
using System.Text.Json;
using CampRadar.Api.Alerts;
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
    private readonly StatusService _status; private readonly Ledger _ledger; private readonly Recorder _rec; private readonly IHttpClientFactory _f; private readonly ILogger<VerifyWorker> _log;
    private readonly SamplePoint[] _points; private readonly bool _enabled; private readonly int _intervalMin, _maxAuto;
    private readonly Dictionary<string, long> _lastGauge = new();
    public DateTimeOffset LastCycle { get; private set; }
    public string LastNote { get; private set; } = "not yet";

    public VerifyWorker(StatusService status, Ledger ledger, Recorder rec, IHttpClientFactory f, IConfiguration cfg, ILogger<VerifyWorker> log)
    {
        _status = status; _ledger = ledger; _rec = rec; _f = f; _log = log;
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
        int resolved = _ledger.Resolve();
        return $"{logged} points logged, {gauges} gauge reports, {recs} recordings started, {resolved} outcomes resolved";
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
            _rec.AppendAux(name, "gauge.jsonl", JsonSerializer.Serialize(new { t, station = p.Id, precipMm = mm, wx, tempF = tc is null ? null : Math.Round(tc.Value * 9 / 5 + 32, 1), dewF = dc is null ? null : Math.Round(dc.Value * 9 / 5 + 32, 1), present }));
        return true;
    }
}
