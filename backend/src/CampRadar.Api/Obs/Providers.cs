using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CampRadar.Api.Obs;

/// <summary>A source of nearby surface observations. The app does not care which one answered.</summary>
public interface IStationProvider
{
    string Name { get; }
    bool Enabled { get; }
    Task<List<StationOb>> NearbyAsync(double lat, double lon, double radiusMi, CancellationToken ct);
    /// <summary>Three-hour pressure change at one station, when the provider can give history.</summary>
    Task<PressureTrend?> TrendAsync(StationOb station, CancellationToken ct);
}

/// <summary>
/// NOAA MADIS Text/XML viewer (madisXmlPublicDir), public anonymous tier: surface mesonet (CWOP backyard stations, HADS gauges,
/// state networks, RWIS) plus METAR, by bounding box, for the current hour or any past nominal time. One query takes 15–40 s on
/// NOAA's side (the result is then cached there), so this provider caches by half-degree cell for twenty minutes, keeps to an hourly
/// query budget, and is normally warmed by the alert/verify workers before a person asks. SI in, US units out. Values carry MADIS QC flags; Q/B/X are dropped.
/// </summary>
public sealed class MadisProvider : IStationProvider
{
    public sealed record Ob(string Id, string Provider, string Sub, double Lat, double Lon, double? ElevM, DateTimeOffset At,
        double? TempF, double? DewF, double? Rh, double? DirDeg, double? WindMph, double? GustMph, double? PressureInHg, double? Rain1hIn, double? RateInHr, string PresentWx);
    private readonly HttpClient _http; private readonly string _base; private readonly bool _enabled; private readonly int _budget; private readonly ILogger _log;
    private readonly System.Collections.Concurrent.ConcurrentQueue<DateTimeOffset> _recent = new();   // query times in the last hour (Madis:MaxQueriesPerHour, default 80)
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, List<Ob> List)> _cache = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<List<Ob>>> _inflight = new();
    public string Name => "madis";
    public bool Enabled => _enabled;
    public MadisProvider(IHttpClientFactory f, IConfiguration cfg, ILogger<MadisProvider> log)
    {
        _http = f.CreateClient("madis"); _log = log;
        _enabled = !string.Equals(cfg["Madis:Enabled"], "false", StringComparison.OrdinalIgnoreCase);
        _budget = int.TryParse(cfg["Madis:MaxQueriesPerHour"], out var b) ? Math.Clamp(b, 5, 1000) : 80;
        _base = (string.IsNullOrWhiteSpace(cfg["Madis:QueryUrl"]) ? "https://madis-data.ncep.noaa.gov/madisPublic1/cgi-bin/madisXmlPublicDir" : cfg["Madis:QueryUrl"]!).TrimEnd('/');
    }

    /// <summary>True when the half-degree cell around the point has a fresh answer (so a caller can decide to wait or fall back).</summary>
    public bool IsWarm(double lat, double lon) => _cache.TryGetValue(CellKey(lat, lon), out var c) && DateTimeOffset.UtcNow - c.At < CellTtl;
    private static readonly TimeSpan CellTtl = TimeSpan.FromMinutes(20);
    public int QueriesLastHour { get { while (_recent.TryPeek(out var t) && DateTimeOffset.UtcNow - t > TimeSpan.FromHours(1)) _recent.TryDequeue(out _); return _recent.Count; } }
    private bool HasBudget() => QueriesLastHour < _budget;
    private static string CellKey(double lat, double lon) => $"{Math.Round(lat * 2) / 2:F1},{Math.Round(lon * 2) / 2:F1}";

    public async Task<List<StationOb>> NearbyAsync(double lat, double lon, double radiusMi, CancellationToken ct)
    {
        var obs = await CellAsync(lat, lon, ct);
        return obs.Select(o => ToStation(o, lat, lon)).Where(x => x.DistMi <= radiusMi).OrderBy(x => x.DistMi).ToList();
    }

    /// <summary>Latest observation per station in the ~±1° cell box around the point, cached ten minutes; concurrent callers share one fetch.</summary>
    public Task<List<Ob>> CellAsync(double lat, double lon, CancellationToken ct)
    {
        var key = CellKey(lat, lon);
        if (_cache.TryGetValue(key, out var c) && DateTimeOffset.UtcNow - c.At < CellTtl) return Task.FromResult(c.List);
        if (!_inflight.ContainsKey(key) && !HasBudget())
        {   // over the hourly budget: a stale cell is better than nothing; a cold one tells the caller to fall back
            if (c.List is not null) return Task.FromResult(c.List);
            throw new InvalidOperationException($"MADIS query budget ({_budget}/hour) spent");
        }
        return _inflight.GetOrAdd(key, _ => Fetch());
        async Task<List<Ob>> Fetch()
        {
            try
            {
                double cLat = Math.Round(lat * 2) / 2, cLon = Math.Round(lon * 2) / 2;
                var list = await RegionAsync(cLat - 0.95, cLon - 1.15, cLat + 0.95, cLon + 1.15, null, ct);
                _cache[key] = (DateTimeOffset.UtcNow, list); if (_cache.Count > 400) _cache.Clear();
                return list;
            }
            finally { _inflight.TryRemove(key, out _); }
        }
    }

    /// <summary>
    /// One viewer query: stations in the box reporting in the hour ending at <paramref name="nominal"/> (null = now), latest report per
    /// station. Variables: position, T, TD, RH, wind, gust, altimeter/SLP, 1-h precipitation, precipitation rate, present weather.
    /// </summary>
    public async Task<List<Ob>> RegionAsync(double latS, double lonW, double latN, double lonE, DateTimeOffset? nominal, CancellationToken ct)
    {
        string F(double v) => v.ToString("F3", CultureInfo.InvariantCulture);
        var url = $"{_base}?rdr=&time={(nominal is null ? "0" : nominal.Value.ToUniversalTime().ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture))}&minbck=-59&minfwd=0&recwin=3&timefilter=0&dfltrsel=1&state=TX" +
                  $"&latll={F(latS)}&lonll={F(lonW)}&latur={F(latN)}&lonur={F(lonE)}&stanam=&stasel=0&pvdrsel=0&varsel=1&qctype=0&qcsel=99&xml=5&csvmiss=0" +
                  "&nvars=LAT&nvars=LON&nvars=ELEV&nvars=T&nvars=TD&nvars=RH&nvars=DD&nvars=FF&nvars=FFGUST&nvars=ALTSE&nvars=SLP&nvars=PCP1H&nvars=PCPRATE&nvars=PRESWEA";
        var sw = System.Diagnostics.Stopwatch.StartNew(); _recent.Enqueue(DateTimeOffset.UtcNow);
        var text = await _http.GetStringAsync(url, ct);
        var list = ParseCsv(text);
        _log.LogInformation("MADIS box {LatS:F1},{LonW:F1}–{LatN:F1},{LonE:F1} {When}: {N} stations in {Ms} ms", latS, lonW, latN, lonE, nominal?.ToString("yyyy-MM-dd HH:mm") ?? "now", list.Count, sw.ElapsedMilliseconds);
        return list;
    }

    /// <summary>
    /// The viewer wraps its CSV in an HTML page. Header: STAID,OBDATE,OBTIME,PVDR,SUBPVDR then each variable followed by a "D" QC-flag column.
    /// Missing is -99999. Flags: V verified, S screened, C coarse, Z not checked keep their value; Q questioned, B bad, X rejected drop it.
    /// </summary>
    public static List<Ob> ParseCsv(string text)
    {
        var plain = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", "\n");
        var lines = plain.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        int hi = lines.FindIndex(l => l.StartsWith("STAID", StringComparison.OrdinalIgnoreCase));
        if (hi < 0) return new();
        var cols = lines[hi].Split(',').Select(c => c.Trim().ToUpperInvariant()).ToList();
        int Col(string n) => cols.IndexOf(n);
        int cId = Col("STAID"), cDate = Col("OBDATE"), cTime = Col("OBTIME"), cProv = Col("PVDR"), cSub = Col("SUBPVDR");
        int cLat = Col("LAT"), cLon = Col("LON"), cEl = Col("ELEV"), cT = Col("T"), cTd = Col("TD"), cRh = Col("RH"), cDd = Col("DD"), cFf = Col("FF"), cG = Col("FFGUST"),
            cAlt = Col("ALTSE"), cSlp = Col("SLP"), cP = Col("PCP1H"), cRate = Col("PCPRATE"), cWx = Col("PRESWEA");
        var latest = new Dictionary<string, Ob>();
        foreach (var line in lines.Skip(hi + 1))
        {
            if (!line.Contains(',')) continue;
            var f = line.Split(',').Select(x => x.Trim()).ToList(); if (f.Count < cols.Count - 1) continue;
            string S(int i) => i >= 0 && i < f.Count ? f[i] : "";
            double? G(int i)
            {
                if (i < 0 || i >= f.Count || !double.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v <= -9000) return null;
                var flag = i + 1 < f.Count && i + 1 < cols.Count && cols[i + 1] == "D" ? f[i + 1] : "";
                return flag is "Q" or "B" or "X" ? null : v;
            }
            var la = G(cLat); var lo = G(cLon); if (la is null || lo is null) continue;
            if (!DateTimeOffset.TryParseExact(S(cDate) + " " + S(cTime), "MM/dd/yyyy HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) continue;
            double? K2F(double? k) => k is { } v ? (v > 150 ? (v - 273.15) * 9 / 5 + 32 : v * 9 / 5 + 32) : null;
            double? pres = G(cSlp) is { } slp ? (slp > 2000 ? slp / 3386.39 : slp / 33.8639) : G(cAlt) is { } alt ? (alt > 2000 ? alt / 3386.39 : alt / 33.8639) : null;
            var ob = new Ob(S(cId), S(cProv), S(cSub), la.Value, lo.Value, G(cEl), at, K2F(G(cT)), K2F(G(cTd)), G(cRh), G(cDd),
                G(cFf) is { } w ? w * 2.23694 : null, G(cG) is { } g ? g * 2.23694 : null, pres,
                G(cP) is { } r ? r * 1000 / 25.4 : null, G(cRate) is { } rr ? rr * 3600 / 25.4 : null, S(cWx));   // PCP1H is metres, PCPRATE mm/s (checked against -RA METARs: 0.0018 m ↔ 0.0005 mm/s)
            if (!latest.TryGetValue(ob.Id, out var prev) || prev.At < ob.At) latest[ob.Id] = ob;
        }
        return latest.Values.ToList();
    }

    private static bool Official(Ob o) => o.Provider.Contains("ASOS", StringComparison.OrdinalIgnoreCase) || o.Provider.Contains("MTR", StringComparison.OrdinalIgnoreCase) || o.Provider.Contains("AWOS", StringComparison.OrdinalIgnoreCase) || o.Provider.Contains("RAWS", StringComparison.OrdinalIgnoreCase);
    private static string NetName(Ob o) => o.Provider switch { "APRSWXNET" => "CWOP", "ASOS-HFM" or "ASOS" => "ASOS", "OTHER-MTR" or "OTHER-HFM" or "NonFedAWOS" => "AWOS", "HADS" => "HADS gauge", var p => p };
    public static StationOb ToStation(Ob o, double lat0, double lon0) => new(o.Id, o.Id, NetName(o), Math.Round(Haversine(lat0, lon0, o.Lat, o.Lon), 1), o.Lat, o.Lon, (o.ElevM ?? 0) * 3.28084,
        (int)Math.Max(0, (DateTimeOffset.UtcNow - o.At).TotalMinutes), o.TempF, o.Rh, o.DewF, o.WindMph, o.GustMph, o.DirDeg, o.PressureInHg, o.Rain1hIn, Official(o));
    public Task<PressureTrend?> TrendAsync(StationOb station, CancellationToken ct) => Task.FromResult<PressureTrend?>(null);   // the viewer is point-in-time; airports fall through to IEM history
    public static double Haversine(double lat1, double lon1, double lat2, double lon2)
    { double R = 3958.8, p = Math.PI / 180, a = Math.Sin((lat2 - lat1) * p / 2); a *= a; double b = Math.Sin((lon2 - lon1) * p / 2); b *= b; return 2 * R * Math.Asin(Math.Sqrt(a + Math.Cos(lat1 * p) * Math.Cos(lat2 * p) * b)); }
}

/// <summary>Iowa Environmental Mesonet current conditions: airports (ASOS/AWOS) and RAWS by state. Free, no key; no backyard stations.</summary>
public sealed class IemProvider : IStationProvider
{
    private readonly HttpClient _http; private readonly ILogger _log;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, List<StationOb> List)> _cache = new();
    public string Name => "iem";
    public bool Enabled => true;
    public IemProvider(IHttpClientFactory f, ILogger<IemProvider> log) { _http = f.CreateClient("obs"); _log = log; }

    public async Task<List<StationOb>> NearbyAsync(double lat, double lon, double radiusMi, CancellationToken ct)
    {
        var states = StatesNear(lat, lon);
        var all = new List<StationOb>();
        foreach (var st in states) foreach (var net in new[] { st + "_ASOS", st + "_RAWS" })
        {
            try
            {
                if (!_cache.TryGetValue(net, out var c) || DateTimeOffset.UtcNow - c.At > TimeSpan.FromMinutes(5))
                {
                    using var doc = JsonDocument.Parse(await _http.GetStringAsync($"https://mesonet.agron.iastate.edu/api/1/currents.json?network={net}", ct));
                    var list = new List<StationOb>();
                    foreach (var d in doc.RootElement.GetProperty("data").EnumerateArray())
                    {
                        double? V(string n) => d.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
                        string S(string n) => d.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                        var la = V("lat"); var lo = V("lon"); if (la is null || lo is null) continue;
                        var when = DateTimeOffset.TryParse(S("utc_valid"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : DateTimeOffset.UtcNow;
                        list.Add(new StationOb(S("station"), S("name"), net.EndsWith("RAWS") ? "RAWS" : "ASOS", 0, la.Value, lo.Value, 0, (int)Math.Max(0, (DateTimeOffset.UtcNow - when).TotalMinutes),
                            V("tmpf"), V("relh"), V("dwpf"), V("sknt") is { } s ? s * 1.15078 : null, V("gust") is { } g ? g * 1.15078 : null, V("drct"), V("mslp") is { } m ? m / 33.8639 : V("alti"), V("phour"), true));
                    }
                    c = (DateTimeOffset.UtcNow, list); _cache[net] = c;
                }
                all.AddRange(c.List);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "IEM {Net} failed", net); }
        }
        return all.Select(x => x with { DistMi = Math.Round(MadisProvider.Haversine(lat, lon, x.Lat, x.Lon), 1) }).Where(x => x.DistMi <= radiusMi).OrderBy(x => x.DistMi).ToList();
    }

    public async Task<PressureTrend?> TrendAsync(StationOb s, CancellationToken ct)
    {
        try
        {
            var url = $"https://mesonet.agron.iastate.edu/api/1/obhistory.json?station={s.Id}&network={StateOf(s)}_{(s.Network == "RAWS" ? "RAWS" : "ASOS")}&date={DateTimeOffset.UtcNow:yyyy-MM-dd}&full=false";
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(url, ct));
            var pts = new List<(DateTimeOffset T, double P)>();
            foreach (var d in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                double? p = d.TryGetProperty("mslp", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetDouble() / 33.8639 : d.TryGetProperty("alti", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetDouble() : null;
                if (p is null || !d.TryGetProperty("utc_valid", out var tv) || !DateTimeOffset.TryParse(tv.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)) continue;
                pts.Add((t, p.Value));
            }
            if (pts.Count < 2) return null;
            var last = pts[^1]; var target = last.T.AddHours(-3); var ago = pts.MinBy(x => Math.Abs((x.T - target).TotalMinutes));
            double hrs = Math.Max(0.5, (last.T - ago.T).TotalHours), rate = (last.P - ago.P) / hrs * 3;
            return new PressureTrend(Math.Round(last.P, 2), Math.Round(rate, 3), rate > 0.06 ? "rising" : rate < -0.06 ? "falling" : "steady", Math.Abs(rate) > 0.12);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "IEM trend failed for {Id}", s.Id); return null; }
    }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _stateOf = new();
    private string StateOf(StationOb s) => _stateOf.TryGetValue(s.Id, out var st) ? st : StatesNear(s.Lat, s.Lon)[0];

    /// <summary>States whose bounding boxes come within ~40 mi of the point (so stations across a state line still count).</summary>
    public static List<string> StatesNear(double lat, double lon)
    {
        var hits = new List<(string St, double D)>();
        foreach (var (st, s, w, n, e) in StateBoxes)
        {
            double dLat = lat < s ? s - lat : lat > n ? lat - n : 0, dLon = lon < w ? w - lon : lon > e ? lon - e : 0;
            double d = Math.Sqrt(dLat * dLat * 69 * 69 + dLon * dLon * 69 * 69 * Math.Cos(lat * Math.PI / 180) * Math.Cos(lat * Math.PI / 180));
            if (d <= 40) hits.Add((st, d));
        }
        if (hits.Count == 0) hits.Add(("TX", 0));
        return hits.OrderBy(h => h.D).Select(h => h.St).Take(3).ToList();
    }
    // (state, south, west, north, east)
    static readonly (string, double, double, double, double)[] StateBoxes =
    {
        ("AL",30.2,-88.5,35.0,-84.9),("AK",51.2,-179.1,71.4,-129.9),("AZ",31.3,-114.8,37.0,-109.0),("AR",33.0,-94.6,36.5,-89.6),("CA",32.5,-124.4,42.0,-114.1),("CO",37.0,-109.1,41.0,-102.0),
        ("CT",41.0,-73.7,42.1,-71.8),("DE",38.4,-75.8,39.8,-75.0),("FL",24.5,-87.6,31.0,-80.0),("GA",30.4,-85.6,35.0,-80.8),("HI",18.9,-160.3,22.3,-154.8),("ID",42.0,-117.2,49.0,-111.0),
        ("IL",37.0,-91.5,42.5,-87.5),("IN",37.8,-88.1,41.8,-84.8),("IA",40.4,-96.6,43.5,-90.1),("KS",37.0,-102.1,40.0,-94.6),("KY",36.5,-89.6,39.1,-82.0),("LA",28.9,-94.0,33.0,-88.8),
        ("ME",43.1,-71.1,47.5,-66.9),("MD",37.9,-79.5,39.7,-75.0),("MA",41.2,-73.5,42.9,-69.9),("MI",41.7,-90.4,48.3,-82.4),("MN",43.5,-97.2,49.4,-89.5),("MS",30.2,-91.7,35.0,-88.1),
        ("MO",36.0,-95.8,40.6,-89.1),("MT",44.4,-116.0,49.0,-104.0),("NE",40.0,-104.1,43.0,-95.3),("NV",35.0,-120.0,42.0,-114.0),("NH",42.7,-72.6,45.3,-70.6),("NJ",38.9,-75.6,41.4,-73.9),
        ("NM",31.3,-109.1,37.0,-103.0),("NY",40.5,-79.8,45.0,-71.9),("NC",33.8,-84.3,36.6,-75.5),("ND",45.9,-104.1,49.0,-96.6),("OH",38.4,-84.8,42.0,-80.5),("OK",33.6,-103.0,37.0,-94.4),
        ("OR",42.0,-124.6,46.3,-116.5),("PA",39.7,-80.5,42.3,-74.7),("RI",41.1,-71.9,42.0,-71.1),("SC",32.0,-83.4,35.2,-78.5),("SD",42.5,-104.1,45.9,-96.4),("TN",35.0,-90.3,36.7,-81.6),
        ("TX",25.8,-106.6,36.5,-93.5),("UT",37.0,-114.1,42.0,-109.0),("VT",42.7,-73.4,45.0,-71.5),("VA",36.5,-83.7,39.5,-75.2),("WA",45.5,-124.8,49.0,-116.9),("WV",37.2,-82.6,40.6,-77.7),
        ("WI",42.5,-92.9,47.1,-86.8),("WY",41.0,-111.1,45.0,-104.1),("DC",38.8,-77.1,39.0,-76.9)
    };
}
