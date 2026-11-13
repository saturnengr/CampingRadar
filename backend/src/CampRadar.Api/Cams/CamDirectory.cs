using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CampRadar.Api.Cams;

/// <summary>
/// Our own camera directory, rebuilt from public registries on a schedule (weekly by default) and never deleted from — a camera a
/// source stops listing is retired. Sources:
///   • OpenTrafficCamMap (github.com/AidanWelch/OpenTrafficCamMap, MIT): ~4,000 US DOT cameras with a plain JPEG snapshot URL
///     (format IMAGE_STREAM); Ohio, California, Colorado, Indiana, Alaska, Kentucky, Arizona at the time of writing.
///   • IEM webcams (mesonet.agron.iastate.edu/geojson/webcam.py?network=…): TV-station and DOT cameras in the upper Midwest with a
///     heading and the frame's own timestamp — the calibration set, next to mesonet gauges.
///   • Caltrans CCTV status JSON (cwwp2.dot.ca.gov, no key): every district's cameras with a current still URL.
///   • Configured feeds (Cams:Sources:N:…): the "511 developer API" that many state DOTs share (511wi, 511ny, fl511, 511ga, 511ia,
///     511mn, 511ks, 511ne, 511pa, 511la, 511sc, nd/sd, nc, virginia…; a free key per state), WSDOT HighwayCameras (free access
///     code), or any JSON list with a mapping (Kind json + field names). Example entries live in appsettings.json; an entry without a
///     key is skipped, so the list can be filled in state by state as keys arrive.
/// Config: Cams:OtcmUrl, Cams:RefreshDays (7), Cams:Sources.
/// </summary>
public sealed class CamDirectory
{
    private readonly IHttpClientFactory _f; private readonly IConfiguration _cfg; private readonly CamStore _store; private readonly ILogger<CamDirectory> _log;
    public string LastRun { get; private set; } = "never";
    private readonly Dictionary<string, int> _perNet = new();
    public IReadOnlyDictionary<string, int> IemPerNetwork => _perNet;
    public DateTimeOffset LastRunAt { get; private set; }

    public CamDirectory(IHttpClientFactory f, IConfiguration cfg, CamStore store, ILogger<CamDirectory> log) { _f = f; _cfg = cfg; _store = store; _log = log; }

    public bool Due => (DateTimeOffset.UtcNow - LastRunAt).TotalDays >= double.Parse(_cfg["Cams:RefreshDays"] ?? "7", System.Globalization.CultureInfo.InvariantCulture);

    public async Task<string> RefreshAsync(CancellationToken ct)
    {
        var sb = new StringBuilder(); long started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try { var n = await OtcmAsync(ct); sb.Append($"otcm {n} cams"); _store.RetireMissing("otcm", started); }
        catch (Exception ex) when (ex is not OperationCanceledException) { sb.Append("otcm failed: " + ex.Message); _log.LogWarning(ex, "otcm registry"); }
        try { var n = await IemAsync(ct); sb.Append($" · iem {n} online now (directory accumulates every cycle)"); }   // never retire IEM cams on one read: most are simply not reporting this minute
        catch (Exception ex) when (ex is not OperationCanceledException) { sb.Append(" · iem failed: " + ex.Message); _log.LogWarning(ex, "iem registry"); }
        try { var n = await CaltransAsync(ct); sb.Append($" · caltrans {n} cams"); if (n > 0) _store.RetireMissing("caltrans", started); }
        catch (Exception ex) when (ex is not OperationCanceledException) { sb.Append(" · caltrans failed: " + ex.Message); _log.LogWarning(ex, "caltrans registry"); }
        foreach (var src in _cfg.GetSection("Cams:Sources").GetChildren())
        {
            var name = src["Name"] ?? src.Key, url = src["Url"] ?? "", kind = (src["Kind"] ?? "511").ToLowerInvariant();
            if (url.Length == 0 || url.Contains("YOUR_KEY") || url.Contains("{key}")) { sb.Append($" · {name}: no key yet"); continue; }
            try { var n = await ConfiguredAsync(name, url, kind, src, ct); sb.Append($" · {name} {n} cams"); if (n > 0) _store.RetireMissing(name, started); }
            catch (Exception ex) when (ex is not OperationCanceledException) { sb.Append($" · {name} failed: {Trim(ex.Message)}"); _log.LogWarning(ex, "{Name} registry", name); }
        }
        LastRun = sb.ToString(); LastRunAt = DateTimeOffset.UtcNow; return LastRun;
    }

    private async Task<int> OtcmAsync(CancellationToken ct)
    {
        var url = _cfg["Cams:OtcmUrl"] ?? "https://raw.githubusercontent.com/AidanWelch/OpenTrafficCamMap/master/cameras/USA.json";
        var json = await _f.CreateClient("obs").GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        var cams = new List<Cam>();
        foreach (var state in doc.RootElement.EnumerateObject())
        {
            if (state.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (var county in state.Value.EnumerateObject())
            {
                if (county.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var c in county.Value.EnumerateArray())
                {
                    if (!c.TryGetProperty("format", out var f) || f.GetString() != "IMAGE_STREAM") continue;
                    if (c.TryGetProperty("markedForReview", out var mr) && mr.ValueKind == JsonValueKind.True) continue;
                    if (!c.TryGetProperty("latitude", out var la) || la.ValueKind != JsonValueKind.Number || !c.TryGetProperty("longitude", out var lo) || lo.ValueKind != JsonValueKind.Number || !c.TryGetProperty("url", out var u) || u.ValueKind != JsonValueKind.String) continue;
                    double lat = la.GetDouble(), lon = lo.GetDouble(); var snap = u.GetString() ?? "";
                    if (Math.Abs(lat) < 1 && Math.Abs(lon) < 1 || snap.Length < 12 || !snap.StartsWith("http")) continue;
                    var desc = c.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
                    var hdg = c.TryGetProperty("direction", out var dir) && dir.ValueKind == JsonValueKind.String ? HeadingFrom(dir.GetString()) : null;
                    var rate = c.TryGetProperty("updateRate", out var ur) && ur.ValueKind == JsonValueKind.Number && ur.TryGetInt64(out var ms) && ms > 0 ? (int)Math.Clamp(ms / 1000, 30, 3600) : 300;
                    cams.Add(new Cam
                    {
                        Id = "otcm:" + Hash(snap), Source = "otcm", Name = (state.Name + " · " + (county.Name == "other" ? "" : county.Name + " · ") + desc).Trim(' ', '·'),
                        Lat = lat, Lon = lon, HeadingDeg = hdg, Url = snap, RefreshSec = rate, Licence = "State DOT public camera via OpenTrafficCamMap (MIT)"
                    });
                }
            }
        }
        _store.UpsertMany(cams); return cams.Select(c => c.Id).Distinct().Count();
    }

    /// <summary>
    /// IEM's webcam GeoJSON lists the cameras that reported in the last few minutes (the network parameter is ignored), so one weekly
    /// read sees a handful. The worker calls this every cycle instead; the directory fills in over a day as cameras come and go.
    /// </summary>
    public async Task<int> IemAsync(CancellationToken ct)
    {
        var cams = new List<Cam>();
        foreach (var net in new[] { "all" })
        {
            ct.ThrowIfCancellationRequested();
            string json;
            try { json = await _f.CreateClient("obs").GetStringAsync("https://mesonet.agron.iastate.edu/geojson/webcam.py", ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "iem webcams"); continue; }
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("features", out var feats)) continue;
            int before = cams.Count;
            foreach (var f in feats.EnumerateArray())
            {
                if (!f.TryGetProperty("properties", out var p) || !f.TryGetProperty("geometry", out var geo) || geo.ValueKind != JsonValueKind.Object || !geo.TryGetProperty("coordinates", out var g) || g.GetArrayLength() < 2) continue;
                var cid = p.TryGetProperty("cid", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : ""; if (cid.Length == 0) continue;
                if (cams.Any(x => x.Id == "iem:" + cid)) continue;   // the service may answer the same set for several network names
                cams.Add(new Cam
                {
                    Id = "iem:" + cid, Source = "iem", Name = (p.TryGetProperty("name", out var n) ? n.GetString() : cid) + (p.TryGetProperty("state", out var st) ? ", " + st.GetString() : ""),
                    Lon = g[0].GetDouble(), Lat = g[1].GetDouble(), HeadingDeg = p.TryGetProperty("angle", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetDouble() : null,
                    // the current frame is always at this path; the geojson's imgurl is the archive copy of the same frame
                    Url = $"https://mesonet.agron.iastate.edu/data/camera/stills/{cid}.jpg", RefreshSec = 300, Licence = "Iowa Environmental Mesonet webcam (" + cid.Split('-')[0] + ")"
                });
            }
            _perNet[net] = cams.Count - before;
        }
        _store.UpsertMany(cams); return cams.Count;
    }

    /// <summary>Caltrans publishes a CCTV status file per district (d1…d12), no key, with a current still per camera.</summary>
    private async Task<int> CaltransAsync(CancellationToken ct)
    {
        var cams = new List<Cam>();
        for (int d = 1; d <= 12; d++)
        {
            ct.ThrowIfCancellationRequested();
            string json;
            try { json = await _f.CreateClient("obs").GetStringAsync($"https://cwwp2.dot.ca.gov/data/d{d}/cctv/cctvStatusD{d:00}.json", ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "caltrans d{D}", d); continue; }
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)) continue;
            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("cctv", out var c)) continue;
                var loc = c.TryGetProperty("location", out var l) ? l : default; var img = c.TryGetProperty("imageData", out var i) && i.TryGetProperty("static", out var st) ? st : default;
                var url = img.ValueKind == JsonValueKind.Object && img.TryGetProperty("currentImageURL", out var cu) ? cu.GetString() ?? "" : "";
                if (loc.ValueKind != JsonValueKind.Object || url.Length < 12 || !url.StartsWith("http")) continue;
                if (!double.TryParse(loc.TryGetProperty("latitude", out var la) ? la.GetString() : null, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lat)) continue;
                if (!double.TryParse(loc.TryGetProperty("longitude", out var lo) ? lo.GetString() : null, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lon)) continue;
                if (c.TryGetProperty("inService", out var sv) && sv.GetString() == "false") continue;
                var name = string.Join(" · ", new[] { loc.TryGetProperty("district", out var di) ? "Caltrans D" + di.GetString() : "Caltrans", loc.TryGetProperty("route", out var ro) ? ro.GetString() : null, loc.TryGetProperty("locationName", out var ln) ? ln.GetString() : null }.Where(x => !string.IsNullOrWhiteSpace(x)));
                var dir = loc.TryGetProperty("direction", out var dr) ? dr.GetString() : null;
                cams.Add(new Cam { Id = "caltrans:" + Hash(url), Source = "caltrans", Name = name, Lat = lat, Lon = lon, HeadingDeg = HeadingFrom(dir), Url = url, RefreshSec = 300, Licence = "Caltrans CCTV (public)" });
            }
        }
        _store.UpsertMany(cams); return cams.Count;
    }

    /// <summary>A configured feed: the shared 511 API shape, WSDOT's, or a generic JSON list with field names given in config.</summary>
    private async Task<int> ConfiguredAsync(string name, string url, string kind, IConfigurationSection src, CancellationToken ct)
    {
        var json = await _f.CreateClient("obs").GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        var cams = new List<Cam>(); var lic = src["Licence"] ?? (name + " public camera feed");
        string? S(JsonElement e, params string[] keys) { foreach (var k in keys) if (e.TryGetProperty(k, out var v)) return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null; return null; }
        double? D(JsonElement e, params string[] keys) { var t = S(e, keys); return t is not null && double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null; }
        IEnumerable<JsonElement> items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray()
            : src["Items"] is { } ip && ip.Length > 0 && doc.RootElement.TryGetProperty(ip, out var arr) && arr.ValueKind == JsonValueKind.Array ? arr.EnumerateArray()
            : doc.RootElement.EnumerateObject().Select(p => p.Value).FirstOrDefault(v => v.ValueKind == JsonValueKind.Array).EnumerateArray();
        foreach (var e in items)
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            string? id, nm, img, dir; double? lat, lon;
            if (kind == "wsdot")
            {
                var loc = e.TryGetProperty("CameraLocation", out var l) ? l : default;
                id = S(e, "CameraID"); nm = S(e, "Title") ?? (loc.ValueKind == JsonValueKind.Object ? S(loc, "Description") : null); img = S(e, "ImageURL");
                lat = loc.ValueKind == JsonValueKind.Object ? D(loc, "Latitude") : null; lon = loc.ValueKind == JsonValueKind.Object ? D(loc, "Longitude") : null; dir = loc.ValueKind == JsonValueKind.Object ? S(loc, "Direction") : null;
                if (e.TryGetProperty("IsActive", out var ia) && ia.ValueKind == JsonValueKind.False) continue;
            }
            else if (kind == "json")
            {
                id = S(e, src["IdField"] ?? "id"); nm = S(e, src["NameField"] ?? "name"); img = S(e, src["UrlField"] ?? "url");
                lat = D(e, src["LatField"] ?? "latitude"); lon = D(e, src["LonField"] ?? "longitude"); dir = S(e, src["HeadingField"] ?? "direction");
            }
            else   // 511 developer API (Iteris/Castle Rock shape shared by many states)
            {
                id = S(e, "ID", "Id", "id"); nm = string.Join(" · ", new[] { S(e, "RoadwayName"), S(e, "Name") }.Where(x => !string.IsNullOrWhiteSpace(x))); img = S(e, "Url", "ImageUrl", "url");
                lat = D(e, "Latitude", "latitude"); lon = D(e, "Longitude", "longitude"); dir = S(e, "DirectionOfTravel", "Direction");
                if (e.TryGetProperty("Disabled", out var dis) && dis.ValueKind == JsonValueKind.True) continue;
                if (e.TryGetProperty("Blocked", out var bl) && bl.ValueKind == JsonValueKind.True) continue;
            }
            if (img is null || img.Length < 12 || !img.StartsWith("http") || lat is null || lon is null || Math.Abs(lat.Value) < 1 && Math.Abs(lon.Value) < 1) continue;
            cams.Add(new Cam { Id = name + ":" + (id ?? Hash(img)), Source = name, Name = (name + " · " + (nm ?? "")).Trim(' ', '·'), Lat = lat.Value, Lon = lon.Value, HeadingDeg = HeadingFromWord(dir), Url = img, RefreshSec = 300, Licence = lic });
        }
        _store.UpsertMany(cams); return cams.Count;
    }

    private static string Trim(string s) => s.Length > 120 ? s[..120] + "…" : s;
    private static double? HeadingFromWord(string? w)
    {
        if (string.IsNullOrWhiteSpace(w)) return null; w = w.Trim().ToUpperInvariant();
        if (w.StartsWith("NORTHB") || w == "NORTH") return 0; if (w.StartsWith("SOUTHB") || w == "SOUTH") return 180; if (w.StartsWith("EASTB") || w == "EAST") return 90; if (w.StartsWith("WESTB") || w == "WEST") return 270;
        return HeadingFrom(w);
    }

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16].ToLowerInvariant();
    private static double? HeadingFrom(string? dir) => dir?.Trim().ToUpperInvariant() switch
    {
        "N" => 0, "NNE" => 22.5, "NE" => 45, "ENE" => 67.5, "E" => 90, "ESE" => 112.5, "SE" => 135, "SSE" => 157.5,
        "S" => 180, "SSW" => 202.5, "SW" => 225, "WSW" => 247.5, "W" => 270, "WNW" => 292.5, "NW" => 315, "NNW" => 337.5, _ => null
    };
}
