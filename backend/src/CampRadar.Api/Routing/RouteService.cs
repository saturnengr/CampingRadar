using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CampRadar.Api.Routing;

/// <summary>
/// Driving routes and place search for travel mode, through openrouteservice (key in Routing:OrsKey; never in the app). A route is
/// fetched once per trip and cached by rounded endpoints for an hour; the app projects the vehicle along it. Routing:Provider can
/// name "osrm" with Routing:OsrmUrl for a self-hosted OSRM instead.
/// </summary>
public sealed class RouteService
{
    private readonly IHttpClientFactory _f; private readonly IConfiguration _cfg; private readonly ILogger<RouteService> _log;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, RouteResult R)> _cache = new();
    public RouteService(IHttpClientFactory f, IConfiguration cfg, ILogger<RouteService> log) { _f = f; _cfg = cfg; _log = log; }

    private string OrsBase => (_cfg["Routing:OrsBase"] ?? "https://api.heigit.org/openrouteservice").TrimEnd('/');
    public bool Enabled => !string.IsNullOrEmpty(_cfg["Routing:OrsKey"]) || string.Equals(_cfg["Routing:Provider"], "osrm", StringComparison.OrdinalIgnoreCase);

    public sealed record RouteResult(double[][] Coords, double DistanceMi, double DurationMin, string Provider, string Summary);
    public sealed record Place(string Name, double Lat, double Lon, string Label);

    public async Task<RouteResult?> RouteAsync(double fromLat, double fromLon, double toLat, double toLon, CancellationToken ct)
    {
        string key = $"{Math.Round(fromLat, 2)},{Math.Round(fromLon, 2)}>{Math.Round(toLat, 3)},{Math.Round(toLon, 3)}";
        if (_cache.TryGetValue(key, out var hit) && DateTimeOffset.UtcNow - hit.At < TimeSpan.FromHours(1)) return hit.R;
        RouteResult? r = string.Equals(_cfg["Routing:Provider"], "osrm", StringComparison.OrdinalIgnoreCase) ? await OsrmAsync(fromLat, fromLon, toLat, toLon, ct) : await OrsAsync(fromLat, fromLon, toLat, toLon, ct);
        if (r is not null) { _cache[key] = (DateTimeOffset.UtcNow, r); if (_cache.Count > 500) foreach (var k in _cache.Keys.Take(100)) _cache.TryRemove(k, out _); }
        return r;
    }

    private async Task<RouteResult?> OrsAsync(double fromLat, double fromLon, double toLat, double toLon, CancellationToken ct)
    {
        var keyOrs = _cfg["Routing:OrsKey"]; if (string.IsNullOrEmpty(keyOrs)) return null;
        // openrouteservice now lives at api.heigit.org/openrouteservice (Routing:OrsBase overrides); the documented GET form: api_key, start, end as lon,lat
        var ci = CultureInfo.InvariantCulture;
        var url = $"{OrsBase}/v2/directions/driving-car?api_key={Uri.EscapeDataString(keyOrs)}&start={fromLon.ToString(ci)},{fromLat.ToString(ci)}&end={toLon.ToString(ci)},{toLat.ToString(ci)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url); req.Headers.TryAddWithoutValidation("Accept", "application/geo+json, application/json");
        using var resp = await _f.CreateClient("obs").SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) { _log.LogWarning("ors directions {Code}: {Body}", (int)resp.StatusCode, json.Length > 300 ? json[..300] : json); return null; }
        using var doc = JsonDocument.Parse(json);
        var feat = doc.RootElement.GetProperty("features")[0];
        var coords = feat.GetProperty("geometry").GetProperty("coordinates").EnumerateArray().Select(c => new[] { Math.Round(c[1].GetDouble(), 5), Math.Round(c[0].GetDouble(), 5) }).ToArray();   // [lat, lon]
        var sum = feat.GetProperty("properties").GetProperty("summary");
        double distMi = sum.GetProperty("distance").GetDouble() / 1609.344, durMin = sum.GetProperty("duration").GetDouble() / 60;   // metres and seconds
        return new RouteResult(Thin(coords, 400), distMi, durMin, "openrouteservice", $"{distMi:0} mi · {durMin:0} min by road");
    }

    private async Task<RouteResult?> OsrmAsync(double fromLat, double fromLon, double toLat, double toLon, CancellationToken ct)
    {
        var baseUrl = (_cfg["Routing:OsrmUrl"] ?? "https://router.project-osrm.org").TrimEnd('/');
        var url = $"{baseUrl}/route/v1/driving/{fromLon.ToString(CultureInfo.InvariantCulture)},{fromLat.ToString(CultureInfo.InvariantCulture)};{toLon.ToString(CultureInfo.InvariantCulture)},{toLat.ToString(CultureInfo.InvariantCulture)}?overview=full&geometries=geojson&steps=false";
        var json = await _f.CreateClient("obs").GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.GetProperty("code").GetString() != "Ok") return null;
        var route = doc.RootElement.GetProperty("routes")[0];
        var coords = route.GetProperty("geometry").GetProperty("coordinates").EnumerateArray().Select(c => new[] { Math.Round(c[1].GetDouble(), 5), Math.Round(c[0].GetDouble(), 5) }).ToArray();
        double distMi = route.GetProperty("distance").GetDouble() / 1609.344, durMin = route.GetProperty("duration").GetDouble() / 60;
        return new RouteResult(Thin(coords, 400), distMi, durMin, "osrm", $"{distMi:0} mi · {durMin:0} min by road");
    }

    /// <summary>Place search near the traveller: ORS geocoding (Pelias) first, Nominatim (OSM) when that errors or finds nothing.
    /// Returns a handful of candidates to pick from.</summary>
    public async Task<List<Place>> SearchAsync(string text, double? nearLat, double? nearLon, CancellationToken ct)
    {
        var list = new List<Place>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        text = text.Trim();
        var keyOrs = _cfg["Routing:OrsKey"];
        if (!string.IsNullOrEmpty(keyOrs))
        {
            var url = $"{OrsBase}/geocode/search?api_key={Uri.EscapeDataString(keyOrs)}&text={Uri.EscapeDataString(text)}&size=10&boundary.country=US,CA" +
                      (nearLat is { } la && nearLon is { } lo ? $"&focus.point.lat={la.ToString(CultureInfo.InvariantCulture)}&focus.point.lon={lo.ToString(CultureInfo.InvariantCulture)}" : "");
            try
            {
                using var resp = await _f.CreateClient("obs").GetAsync(url, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode) _log.LogWarning("ors geocode {Code}: {Body}", (int)resp.StatusCode, json.Length > 300 ? json[..300] : json);
                else
                {
                    using var doc = JsonDocument.Parse(json);
                    foreach (var f in doc.RootElement.GetProperty("features").EnumerateArray())
                    {
                        var p = f.GetProperty("properties"); var g = f.GetProperty("geometry").GetProperty("coordinates");
                        list.Add(new Place(p.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "", g[1].GetDouble(), g[0].GetDouble(), p.TryGetProperty("label", out var l) ? l.GetString() ?? "" : ""));
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "ors geocode failed"); }
        }
        // Pelias is fuzzy: keep the ones that actually contain what was typed (first word at least), fall back to the raw list
        var first = text.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var tight = list.Where(p => (p.Name + " " + p.Label).Contains(first, StringComparison.OrdinalIgnoreCase)).ToList();
        var picked = (tight.Count >= 1 ? tight : list).Take(6).ToList();
        if (picked.Count > 0) return picked;
        return await NominatimAsync(text, nearLat, nearLon, ct);
    }

    private async Task<List<Place>> NominatimAsync(string text, double? nearLat, double? nearLon, CancellationToken ct)
    {
        var list = new List<Place>();
        try
        {
            var ci = CultureInfo.InvariantCulture;
            var url = "https://nominatim.openstreetmap.org/search?format=jsonv2&limit=8&countrycodes=us,ca&q=" + Uri.EscapeDataString(text) +
                      (nearLat is { } la && nearLon is { } lo ? $"&viewbox={(lo - 3).ToString(ci)},{(la + 3).ToString(ci)},{(lo + 3).ToString(ci)},{(la - 3).ToString(ci)}" : "");   // soft bias, not bounded
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "VantageWX/1.0 (+https://vantagewx.com)"); req.Headers.TryAddWithoutValidation("Accept-Language", "en");
            using var resp = await _f.CreateClient("obs").SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) { _log.LogWarning("nominatim {Code}: {Body}", (int)resp.StatusCode, json.Length > 200 ? json[..200] : json); return list; }
            using var doc = JsonDocument.Parse(json);
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                var label = r.GetProperty("display_name").GetString() ?? "";
                var name = r.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString()!.Length > 0 ? n.GetString()! : label.Split(',')[0];
                list.Add(new Place(name, double.Parse(r.GetProperty("lat").GetString()!, ci), double.Parse(r.GetProperty("lon").GetString()!, ci), label));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "nominatim failed"); }
        return list;
    }

    /// <summary>Keep at most n points, dropping the least significant (the app only needs the shape to a few hundred metres).</summary>
    private static double[][] Thin(double[][] pts, int n)
    {
        if (pts.Length <= n) return pts;
        var step = (double)(pts.Length - 1) / (n - 1); var o = new double[n][];
        for (int i = 0; i < n; i++) o[i] = pts[(int)Math.Round(i * step)];
        return o;
    }
}
