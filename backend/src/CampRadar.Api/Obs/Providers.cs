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
/// NOAA MADIS Text/XML viewer (madisXmlPublicDir): surface mesonet (CWOP backyard stations, state networks) plus METAR, by bounding box.
/// Needs the credentials MADIS Support issues after the free data application. Column names follow the viewer's CSV header; parsed by name.
/// </summary>
public sealed class MadisProvider : IStationProvider
{
    private readonly HttpClient _http; private readonly string _user, _pass, _base; private readonly ILogger _log;
    public string Name => "madis";
    public bool Enabled => _user.Length > 0 && _pass.Length > 0;
    public MadisProvider(IHttpClientFactory f, IConfiguration cfg, ILogger<MadisProvider> log)
    { _http = f.CreateClient("obs"); _user = cfg["Madis:User"] ?? ""; _pass = cfg["Madis:Password"] ?? ""; _base = (string.IsNullOrWhiteSpace(cfg["Madis:QueryUrl"]) ? "https://madis-data.ncep.noaa.gov/madisPublic1/cgi-bin/madisXmlPublicDir" : cfg["Madis:QueryUrl"]!).TrimEnd('/'); _log = log; }

    public async Task<List<StationOb>> NearbyAsync(double lat, double lon, double radiusMi, CancellationToken ct)
    {
        double dLat = radiusMi / 69.0, dLon = radiusMi / (69.0 * Math.Cos(lat * Math.PI / 180));
        string F(double v) => v.ToString("F3", CultureInfo.InvariantCulture);
        var list = new List<StationOb>();
        foreach (var dataset in new[] { "mesonet", "metar" })
        {
            var url = $"{_base}?rdr=&time=0&minbck=-60&minfwd=0&recwin=4&dfltrsel=0&latll={F(lat - dLat)}&lonll={F(lon - dLon)}&latur={F(lat + dLat)}&lonur={F(lon + dLon)}&stanam=&stasel=0&pvdrsel=0&varsel=1&qcsel=1&xml=2&csvmiss=0&dataset={dataset}" +
                      "&nvars=T&nvars=TD&nvars=RH&nvars=DD&nvars=FF&nvars=FFGUST&nvars=ALTSE&nvars=SLP&nvars=PCP1H&nvars=ELEV";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(_user + ":" + _pass)));
            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) { _log.LogWarning("MADIS {Dataset} query {Status}", dataset, res.StatusCode); continue; }
            var text = await res.Content.ReadAsStringAsync(ct);
            list.AddRange(ParseCsv(text, lat, lon, dataset == "metar"));
        }
        return list.Where(x => x.DistMi <= radiusMi).OrderBy(x => x.DistMi).ToList();
    }

    /// <summary>Tolerant CSV reader: finds the header row (contains STAID or LAT), maps columns by name, converts units (MADIS is SI).</summary>
    internal static List<StationOb> ParseCsv(string text, double lat0, double lon0, bool official)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        int hi = lines.FindIndex(l => l.Contains("LAT", StringComparison.OrdinalIgnoreCase) && l.Contains("LON", StringComparison.OrdinalIgnoreCase) && l.Contains(','));
        if (hi < 0) return new();
        var cols = lines[hi].Split(',').Select(c => c.Trim().Trim('"').ToUpperInvariant()).ToList();
        int Col(params string[] names) { foreach (var n in names) { int i = cols.FindIndex(c => c == n || c.StartsWith(n + "_") || c.StartsWith(n + " ")); if (i >= 0) return i; } return -1; }
        int cId = Col("STAID", "STATION", "ID"), cName = Col("STANAME", "NAME"), cLat = Col("LAT"), cLon = Col("LON"), cEl = Col("ELEV"), cTime = Col("OBSTIME", "TIME", "DATE"), cProv = Col("PROVIDER", "PVDR");
        int cT = Col("T"), cTd = Col("TD"), cRh = Col("RH"), cDd = Col("DD"), cFf = Col("FF"), cG = Col("FFGUST"), cAlt = Col("ALTSE"), cSlp = Col("SLP"), cP = Col("PCP1H");
        var outp = new List<StationOb>();
        foreach (var line in lines.Skip(hi + 1))
        {
            var f = line.Split(',').Select(x => x.Trim().Trim('"')).ToList(); if (f.Count < cols.Count - 2) continue;
            double? G(int i) => i >= 0 && i < f.Count && double.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > -900 && v < 90000 ? v : null;
            string S(int i) => i >= 0 && i < f.Count ? f[i] : "";
            var la = G(cLat); var lo = G(cLon); if (la is null || lo is null) continue;
            var when = cTime >= 0 && DateTimeOffset.TryParse(S(cTime), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : DateTimeOffset.UtcNow;
            double? tempF = G(cT) is { } k ? (k > 150 ? (k - 273.15) * 9 / 5 + 32 : k * 9 / 5 + 32) : null;   // Kelvin (viewer default) or °C
            double? dewF = G(cTd) is { } kd ? (kd > 150 ? (kd - 273.15) * 9 / 5 + 32 : kd * 9 / 5 + 32) : null;
            double? wind = G(cFf) is { } w ? w * 2.23694 : null, gust = G(cG) is { } g ? g * 2.23694 : null;   // m/s → mph
            double? pres = G(cSlp) is { } slp ? (slp > 2000 ? slp / 3386.39 : slp / 33.8639) : G(cAlt) is { } alt ? (alt > 2000 ? alt / 3386.39 : alt / 33.8639) : null;   // Pa or hPa → inHg
            double? rain = G(cP) is { } r ? r / 25.4 : null;   // mm → in
            double dist = Haversine(lat0, lon0, la.Value, lo.Value);
            outp.Add(new StationOb(S(cId), S(cName).Length > 0 ? S(cName) : S(cId), S(cProv).Length > 0 ? S(cProv) : (official ? "METAR" : "MESONET"), Math.Round(dist, 1), la.Value, lo.Value, (G(cEl) ?? 0) * 3.28084,
                (int)Math.Max(0, (DateTimeOffset.UtcNow - when).TotalMinutes), tempF, G(cRh), dewF, wind, gust, G(cDd), pres, rain, official || S(cProv).Contains("ASOS", StringComparison.OrdinalIgnoreCase) || S(cProv).Contains("RAWS", StringComparison.OrdinalIgnoreCase)));
        }
        return outp;
    }
    public Task<PressureTrend?> TrendAsync(StationOb station, CancellationToken ct) => Task.FromResult<PressureTrend?>(null);   // the viewer is point-in-time; the app keeps its own 6-h history
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
