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
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openrouteservice.org/v2/directions/driving-car/geojson");
        req.Headers.TryAddWithoutValidation("Authorization", keyOrs); req.Headers.TryAddWithoutValidation("Accept", "application/geo+json");
        var body = JsonSerializer.Serialize(new { coordinates = new[] { new[] { fromLon, fromLat }, new[] { toLon, toLat } }, instructions = false, geometry_simplify = true, units = "mi" });
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await _f.CreateClient("obs").SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) { _log.LogWarning("ors directions {Code}: {Body}", (int)resp.StatusCode, json.Length > 300 ? json[..300] : json); return null; }
        using var doc = JsonDocument.Parse(json);
        var feat = doc.RootElement.GetProperty("features")[0];
        var coords = feat.GetProperty("geometry").GetProperty("coordinates").EnumerateArray().Select(c => new[] { Math.Round(c[1].GetDouble(), 5), Math.Round(c[0].GetDouble(), 5) }).ToArray();   // [lat, lon]
        var sum = feat.GetProperty("properties").GetProperty("summary");
        double distMi = sum.GetProperty("distance").GetDouble(), durMin = sum.GetProperty("duration").GetDouble() / 60;
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

    /// <summary>Place search near the traveller (ORS geocoding, Pelias). Returns a handful of candidates to pick from.</summary>
    public async Task<List<Place>> SearchAsync(string text, double? nearLat, double? nearLon, CancellationToken ct)
    {
        var keyOrs = _cfg["Routing:OrsKey"]; var list = new List<Place>();
        if (string.IsNullOrEmpty(keyOrs) || string.IsNullOrWhiteSpace(text)) return list;
        var url = $"https://api.openrouteservice.org/geocode/search?api_key={Uri.EscapeDataString(keyOrs)}&text={Uri.EscapeDataString(text.Trim())}&size=6&boundary.country=US,CA" +
                  (nearLat is { } la && nearLon is { } lo ? $"&focus.point.lat={la.ToString(CultureInfo.InvariantCulture)}&focus.point.lon={lo.ToString(CultureInfo.InvariantCulture)}" : "");
        try
        {
            var json = await _f.CreateClient("obs").GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            foreach (var f in doc.RootElement.GetProperty("features").EnumerateArray())
            {
                var p = f.GetProperty("properties"); var g = f.GetProperty("geometry").GetProperty("coordinates");
                list.Add(new Place(p.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "", g[1].GetDouble(), g[0].GetDouble(), p.TryGetProperty("label", out var l) ? l.GetString() ?? "" : ""));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "ors geocode"); }
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
