using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CampRadar.Api.Nws;

/// <summary>One forecast hour, in the spot's local time zone, in US units.</summary>
public sealed record ForecastHour(DateTimeOffset T, double Temp, double Feels, int Pop, int Thunder, double WindMph, double GustMph, string Dir, string Sky, double SnowIn, double? VisMi);
public sealed record ForecastPeriod(string Name, DateTimeOffset Start, bool IsDaytime, double Temperature, string ShortForecast, int Pop, string WindSpeed, string WindDirection);
/// <summary>Offset is the spot's local UTC offset as the NWS wrote it in the period times (the host runs with invariant globalization, so no IANA zone lookup).</summary>
public sealed record Forecast(string Place, string TimeZoneId, TimeSpan Offset, List<ForecastHour> Hours, List<ForecastPeriod> Periods, DateTimeOffset FetchedAt);

/// <summary>
/// NWS point forecast for the server-side briefing: hourly periods plus the gridpoint extras (gusts, thunder, feels-like,
/// snowfall, visibility), merged by hour. Cached per 0.05° cell for 30 minutes.
/// </summary>
public sealed class NwsForecastClient
{
    private readonly HttpClient _http;
    private readonly ILogger<NwsForecastClient> _log;
    private readonly ConcurrentDictionary<string, Forecast> _cache = new();
    public NwsForecastClient(HttpClient http, ILogger<NwsForecastClient> log) { _http = http; _log = log; }

    public async Task<Forecast?> GetAsync(double lat, double lon, CancellationToken ct)
    {
        var key = $"{Math.Round(lat * 20) / 20:F2},{Math.Round(lon * 20) / 20:F2}";
        if (_cache.TryGetValue(key, out var hit) && DateTimeOffset.UtcNow - hit.FetchedAt < TimeSpan.FromMinutes(30)) return hit;
        try
        {
            using var pts = JsonDocument.Parse(await _http.GetStringAsync($"https://api.weather.gov/points/{lat:F4},{lon:F4}", ct));
            var pp = pts.RootElement.GetProperty("properties");
            var tzId = pp.GetProperty("timeZone").GetString() ?? "UTC";
            string place = "";
            if (pp.TryGetProperty("relativeLocation", out var rl) && rl.TryGetProperty("properties", out var rp))
                place = (rp.GetProperty("city").GetString() ?? "") + ", " + (rp.GetProperty("state").GetString() ?? "");
            var hourlyUrl = pp.GetProperty("forecastHourly").GetString()!;
            var dailyUrl = pp.GetProperty("forecast").GetString()!;
            var gridUrl = pp.GetProperty("forecastGridData").GetString()!;

            var hourlyTask = _http.GetStringAsync(hourlyUrl, ct);
            var dailyTask = _http.GetStringAsync(dailyUrl, ct);
            var gridTask = _http.GetStringAsync(gridUrl, ct);
            await Task.WhenAll(hourlyTask, dailyTask, gridTask);

            // gridpoint series → per-hour lookups
            using var grid = JsonDocument.Parse(gridTask.Result);
            var gp = grid.RootElement.GetProperty("properties");
            var gust = Series(gp, "windGust", v => v * 0.621371);            // km/h → mph
            var thunder = Series(gp, "probabilityOfThunder", v => v);
            var feels = Series(gp, "apparentTemperature", v => v * 9 / 5 + 32); // °C → °F
            var snow = Series(gp, "snowfallAmount", v => v / 25.4);           // mm → in (per period; spread per hour below)
            var vis = Series(gp, "visibility", v => v / 1609.34);             // m → mi

            var hours = new List<ForecastHour>();
            using (var hd = JsonDocument.Parse(hourlyTask.Result))
            {
                foreach (var p in hd.RootElement.GetProperty("properties").GetProperty("periods").EnumerateArray())
                {
                    var t = DateTimeOffset.Parse(p.GetProperty("startTime").GetString()!, CultureInfo.InvariantCulture);   // keeps the local offset
                    var tu = t.ToUniversalTime();
                    double temp = p.GetProperty("temperature").GetDouble();
                    int pop = p.TryGetProperty("probabilityOfPrecipitation", out var pr) && pr.TryGetProperty("value", out var pv) && pv.ValueKind == JsonValueKind.Number ? (int)pv.GetDouble() : 0;
                    double wind = ParseMph(p.GetProperty("windSpeed").GetString());
                    string dir = p.GetProperty("windDirection").GetString() ?? "";
                    string sky = p.GetProperty("shortForecast").GetString() ?? "";
                    double g = Math.Max(wind, Lookup(gust, tu) ?? 0);
                    double f = Lookup(feels, tu) ?? temp;
                    hours.Add(new ForecastHour(t, temp, f, pop, (int)(Lookup(thunder, tu) ?? 0), wind, g, dir, sky, LookupSpread(snow, tu), Lookup(vis, tu)));
                }
            }
            var periods = new List<ForecastPeriod>();
            using (var dd = JsonDocument.Parse(dailyTask.Result))
            {
                foreach (var p in dd.RootElement.GetProperty("properties").GetProperty("periods").EnumerateArray())
                {
                    int pop = p.TryGetProperty("probabilityOfPrecipitation", out var pr) && pr.TryGetProperty("value", out var pv) && pv.ValueKind == JsonValueKind.Number ? (int)pv.GetDouble() : 0;
                    periods.Add(new ForecastPeriod(p.GetProperty("name").GetString() ?? "", DateTimeOffset.Parse(p.GetProperty("startTime").GetString()!, CultureInfo.InvariantCulture),
                        p.GetProperty("isDaytime").GetBoolean(), p.GetProperty("temperature").GetDouble(), p.GetProperty("shortForecast").GetString() ?? "", pop,
                        p.GetProperty("windSpeed").GetString() ?? "", p.GetProperty("windDirection").GetString() ?? ""));
                }
            }
            var offset = hours.Count > 0 ? hours[0].T.Offset : TimeSpan.Zero;
            var f2 = new Forecast(place, tzId, offset, hours, periods, DateTimeOffset.UtcNow);
            _cache[key] = f2; if (_cache.Count > 3000) _cache.Clear();
            return f2;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "NWS forecast failed for {Key}", key);
            return _cache.TryGetValue(key, out hit) ? hit : null;
        }
    }

    private static double ParseMph(string? s) { var m = s is null ? null : Regex.Match(s, @"(\d+)(?:\s*to\s*(\d+))?"); return m is { Success: true } ? double.Parse(m.Groups[m.Groups[2].Success ? 2 : 1].Value) : 0; }

    private static List<(DateTimeOffset Start, TimeSpan Len, double Value)> Series(JsonElement gp, string name, Func<double, double> conv)
    {
        var list = new List<(DateTimeOffset, TimeSpan, double)>();
        if (!gp.TryGetProperty(name, out var el) || !el.TryGetProperty("values", out var vals)) return list;
        foreach (var v in vals.EnumerateArray())
        {
            if (!v.TryGetProperty("value", out var val) || val.ValueKind != JsonValueKind.Number) continue;
            var vt = v.GetProperty("validTime").GetString()!; var slash = vt.IndexOf('/');
            var start = DateTimeOffset.Parse(vt[..slash], CultureInfo.InvariantCulture).ToUniversalTime();
            list.Add((start, ParseIsoDuration(vt[(slash + 1)..]), conv(val.GetDouble())));
        }
        return list;
    }
    private static TimeSpan ParseIsoDuration(string d)
    {
        var m = Regex.Match(d, @"P(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?)?");
        return TimeSpan.FromDays(m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0) + TimeSpan.FromHours(m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0) + TimeSpan.FromMinutes(m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
    }
    private static double? Lookup(List<(DateTimeOffset Start, TimeSpan Len, double Value)> s, DateTimeOffset tUtc)
    { foreach (var (st, len, v) in s) if (tUtc >= st && tUtc < st + len) return v; return null; }
    private static double LookupSpread(List<(DateTimeOffset Start, TimeSpan Len, double Value)> s, DateTimeOffset tUtc)
    { foreach (var (st, len, v) in s) if (tUtc >= st && tUtc < st + len) return len.TotalHours > 0 ? v / len.TotalHours : v; return 0; }
}
