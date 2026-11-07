using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace CampRadar.Api.Obs;

/// <summary>
/// Nearby surface observations through Synoptic Data's Mesonet API: NWS/FAA airports, RAWS, state mesonets, road sensors and
/// CWOP backyard stations (which is where most Tempest, Ambient and Davis stations end up). One server token, results cached
/// per ~3 mi cell for 5 minutes so a campground full of devices costs one call.
/// </summary>
public sealed record StationOb(string Id, string Name, string Network, double DistMi, double Lat, double Lon, double ElevFt, int AgeMin,
    double? TempF, double? Rh, double? DewF, double? WindMph, double? GustMph, double? DirDeg, double? PressureInHg, double? Rain1hIn, bool Official);
public sealed record PressureTrend(double InHg, double Change3h, string Word, bool Fast);
public sealed record NearbyObs(List<StationOb> Stations, StationOb? Best, PressureTrend? Trend, DateTimeOffset At);

public sealed class StationObsClient
{
    public const string Api = "https://api.synopticdata.com/v2/stations";
    private readonly HttpClient _http;
    private readonly string _token;
    private readonly ILogger<StationObsClient> _log;
    private readonly ConcurrentDictionary<string, NearbyObs> _cache = new();
    public bool Enabled => _token.Length > 0;
    // Synoptic network ids that are professionally sited (preferred when close): 1 = ASOS/AWOS airports, 2 = RAWS. CWOP backyard stations are 65.
    private static readonly HashSet<string> OfficialNets = new() { "1", "2" };

    public StationObsClient(IHttpClientFactory f, IConfiguration cfg, ILogger<StationObsClient> log)
    { _http = f.CreateClient("obs"); _token = cfg["Synoptic:Token"] ?? ""; _log = log; }

    public async Task<NearbyObs?> NearbyAsync(double lat, double lon, CancellationToken ct)
    {
        if (!Enabled) return null;
        var key = $"{Math.Round(lat * 20) / 20:F2},{Math.Round(lon * 20) / 20:F2}";
        if (_cache.TryGetValue(key, out var hit) && DateTimeOffset.UtcNow - hit.At < TimeSpan.FromMinutes(5)) return hit;
        var url = $"{Api}/latest?token={_token}&radius={lat.ToString("F4", CultureInfo.InvariantCulture)},{lon.ToString("F4", CultureInfo.InvariantCulture)},25&limit=25&within=90&status=active&units=english,speed|mph,pres|inhg,precip|in&qc_checks=basic&qc_remove_data=on" +
                  "&vars=air_temp,relative_humidity,dew_point_temperature,wind_speed,wind_gust,wind_direction,sea_level_pressure,altimeter,precip_accum_one_hour";
        using var doc = JsonDocument.Parse(await _http.GetStringAsync(url, ct));
        var list = new List<StationOb>();
        if (doc.RootElement.TryGetProperty("STATION", out var sts))
            foreach (var s in sts.EnumerateArray())
            {
                if (!s.TryGetProperty("OBSERVATIONS", out var o)) continue;
                double? V(string name) => o.TryGetProperty(name + "_value_1", out var v) && v.TryGetProperty("value", out var x) && x.ValueKind == JsonValueKind.Number ? x.GetDouble() : null;
                DateTimeOffset? T(string name) => o.TryGetProperty(name + "_value_1", out var v) && v.TryGetProperty("date_time", out var x) && DateTimeOffset.TryParse(x.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;
                var temp = V("air_temp"); if (temp is null && V("wind_speed") is null && V("sea_level_pressure") is null && V("altimeter") is null) continue;
                var when = T("air_temp") ?? T("wind_speed") ?? T("sea_level_pressure") ?? T("altimeter"); if (when is null) continue;
                string net = s.TryGetProperty("MNET_ID", out var mn) ? mn.ToString() : "";
                double dist = s.TryGetProperty("DISTANCE", out var d) && double.TryParse(d.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dm) ? dm : 99;
                double P(string n) => double.TryParse(s.TryGetProperty(n, out var e) ? e.ToString() : "", NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : 0;
                list.Add(new StationOb(s.GetProperty("STID").GetString() ?? "", s.TryGetProperty("NAME", out var nm) ? nm.GetString() ?? "" : "", net, Math.Round(dist, 1), P("LATITUDE"), P("LONGITUDE"), P("ELEVATION"),
                    (int)Math.Max(0, (DateTimeOffset.UtcNow - when.Value).TotalMinutes), temp, V("relative_humidity"), V("dew_point_temperature"), V("wind_speed"), V("wind_gust"), V("wind_direction"),
                    V("sea_level_pressure") ?? V("altimeter"), V("precip_accum_one_hour"), OfficialNets.Contains(net)));
            }
        list = list.OrderBy(x => x.DistMi).ToList();
        // best = an official station within 8 mi, else the nearest station of any kind that reports temperature
        var best = list.FirstOrDefault(x => x.Official && x.DistMi <= 8 && x.TempF is not null && x.AgeMin <= 75) ?? list.FirstOrDefault(x => x.TempF is not null && x.AgeMin <= 75);
        PressureTrend? trend = null;
        var pstn = list.FirstOrDefault(x => x.PressureInHg is not null && x.AgeMin <= 75 && x.DistMi <= 30);
        if (pstn is not null) { try { trend = await TrendAsync(pstn.Id, ct); } catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "pressure trend failed for {Id}", pstn.Id); } }
        var result = new NearbyObs(list, best, trend, DateTimeOffset.UtcNow);
        _cache[key] = result; if (_cache.Count > 3000) _cache.Clear();
        return result;
    }

    /// <summary>Three-hour pressure change at one station from its last six hours.</summary>
    private async Task<PressureTrend?> TrendAsync(string stid, CancellationToken ct)
    {
        var url = $"{Api}/timeseries?token={_token}&stid={stid}&recent=360&vars=sea_level_pressure,altimeter&units=pres|inhg&qc_checks=basic&qc_remove_data=on";
        using var doc = JsonDocument.Parse(await _http.GetStringAsync(url, ct));
        var st = doc.RootElement.TryGetProperty("STATION", out var sts) && sts.GetArrayLength() > 0 ? sts[0] : default;
        if (st.ValueKind != JsonValueKind.Object || !st.TryGetProperty("OBSERVATIONS", out var o)) return null;
        var times = o.GetProperty("date_time").EnumerateArray().Select(x => DateTimeOffset.Parse(x.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)).ToList();
        var series = o.TryGetProperty("sea_level_pressure_set_1", out var slp) ? slp : o.TryGetProperty("altimeter_set_1", out var alt) ? alt : default;
        if (series.ValueKind != JsonValueKind.Array) return null;
        var pts = series.EnumerateArray().Select((v, i) => (T: times[i], P: v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN)).Where(x => !double.IsNaN(x.P)).ToList();
        if (pts.Count < 2) return null;
        var last = pts[^1]; var target = last.T.AddHours(-3);
        var ago = pts.MinBy(x => Math.Abs((x.T - target).TotalMinutes));
        double hrs = Math.Max(0.5, (last.T - ago.T).TotalHours), rate = (last.P - ago.P) / hrs * 3;
        return new PressureTrend(Math.Round(last.P, 2), Math.Round(rate, 3), rate > 0.06 ? "rising" : rate < -0.06 ? "falling" : "steady", Math.Abs(rate) > 0.12);
    }
}
