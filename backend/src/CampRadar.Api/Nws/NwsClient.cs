using System.Collections.Concurrent;
using System.Text.Json;

namespace CampRadar.Api.Nws;

public sealed record NwsAlert(string Id, string Event, string? Headline, string? Ends, bool Critical);

/// <summary>Active NWS alerts for a point, cached briefly per rounded location.</summary>
public sealed class NwsClient
{
    public static readonly string[] Critical =
    {
        "Tornado Warning", "Severe Thunderstorm Warning", "Flash Flood Warning", "Flood Warning",
        "Hurricane Warning", "Tropical Storm Warning", "Storm Surge Warning", "Extreme Wind Warning", "Tsunami Warning",
        "Blizzard Warning", "Ice Storm Warning", "Winter Storm Warning", "Extreme Cold Warning", "Avalanche Warning"
    };
    private readonly HttpClient _http;
    private readonly ILogger<NwsClient> _log;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, List<NwsAlert> Alerts)> _cache = new();

    public NwsClient(HttpClient http, ILogger<NwsClient> log) { _http = http; _log = log; }

    public async Task<List<NwsAlert>> ActiveAlertsAsync(double lat, double lon, CancellationToken ct)
    {
        var key = $"{lat:F2},{lon:F2}";
        if (_cache.TryGetValue(key, out var hit) && DateTimeOffset.UtcNow - hit.At < TimeSpan.FromMinutes(2)) return hit.Alerts;
        var list = new List<NwsAlert>();
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync($"https://api.weather.gov/alerts/active?point={lat:F4},{lon:F4}", ct));
            foreach (var f in doc.RootElement.GetProperty("features").EnumerateArray())
            {
                var p = f.GetProperty("properties");
                string ev = p.GetProperty("event").GetString() ?? "";
                list.Add(new NwsAlert(p.GetProperty("id").GetString() ?? "", ev,
                    p.TryGetProperty("headline", out var h) ? h.GetString() : null,
                    p.TryGetProperty("ends", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() :
                    p.TryGetProperty("expires", out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null,
                    Critical.Contains(ev)));
            }
            _cache[key] = (DateTimeOffset.UtcNow, list);
            if (_cache.Count > 5000) _cache.Clear();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "NWS alerts failed for {Key}", key);
            if (_cache.TryGetValue(key, out hit)) return hit.Alerts;
        }
        return list;
    }
}
