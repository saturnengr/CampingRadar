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
/// Config: Cams:OtcmUrl, Cams:IemNetworks (comma list), Cams:RefreshDays (7).
/// </summary>
public sealed class CamDirectory
{
    private readonly IHttpClientFactory _f; private readonly IConfiguration _cfg; private readonly CamStore _store; private readonly ILogger<CamDirectory> _log;
    public string LastRun { get; private set; } = "never";
    public DateTimeOffset LastRunAt { get; private set; }

    public CamDirectory(IHttpClientFactory f, IConfiguration cfg, CamStore store, ILogger<CamDirectory> log) { _f = f; _cfg = cfg; _store = store; _log = log; }

    public bool Due => (DateTimeOffset.UtcNow - LastRunAt).TotalDays >= double.Parse(_cfg["Cams:RefreshDays"] ?? "7", System.Globalization.CultureInfo.InvariantCulture);

    public async Task<string> RefreshAsync(CancellationToken ct)
    {
        var sb = new StringBuilder(); long started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try { var n = await OtcmAsync(ct); sb.Append($"otcm {n} cams"); _store.RetireMissing("otcm", started); }
        catch (Exception ex) when (ex is not OperationCanceledException) { sb.Append("otcm failed: " + ex.Message); _log.LogWarning(ex, "otcm registry"); }
        try { var n = await IemAsync(ct); sb.Append($" · iem {n} cams"); _store.RetireMissing("iem", started); }
        catch (Exception ex) when (ex is not OperationCanceledException) { sb.Append(" · iem failed: " + ex.Message); _log.LogWarning(ex, "iem registry"); }
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
                    if (!c.TryGetProperty("latitude", out var la) || !c.TryGetProperty("longitude", out var lo) || !c.TryGetProperty("url", out var u)) continue;
                    double lat = la.GetDouble(), lon = lo.GetDouble(); var snap = u.GetString() ?? "";
                    if (Math.Abs(lat) < 1 && Math.Abs(lon) < 1 || snap.Length < 12 || !snap.StartsWith("http")) continue;
                    var desc = c.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                    var hdg = c.TryGetProperty("direction", out var dir) ? HeadingFrom(dir.GetString()) : null;
                    var rate = c.TryGetProperty("updateRate", out var ur) && ur.TryGetInt64(out var ms) && ms > 0 ? (int)Math.Clamp(ms / 1000, 30, 3600) : 300;
                    cams.Add(new Cam
                    {
                        Id = "otcm:" + Hash(snap), Source = "otcm", Name = (state.Name + " · " + (county.Name == "other" ? "" : county.Name + " · ") + desc).Trim(' ', '·'),
                        Lat = lat, Lon = lon, HeadingDeg = hdg, Url = snap, RefreshSec = rate, Licence = "State DOT public camera via OpenTrafficCamMap (MIT)"
                    });
                }
            }
        }
        return _store.UpsertMany(cams);
    }

    private async Task<int> IemAsync(CancellationToken ct)
    {
        var nets = (_cfg["Cams:IemNetworks"] ?? "KCCI,KCRG,KELO,IDOT,ISUC,WOI,MCFC,KCAU,KHSI").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cams = new List<Cam>();
        foreach (var net in nets)
        {
            ct.ThrowIfCancellationRequested();
            string json;
            try { json = await _f.CreateClient("obs").GetStringAsync($"https://mesonet.agron.iastate.edu/geojson/webcam.py?network={Uri.EscapeDataString(net)}", ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "iem webcams {Net}", net); continue; }
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("features", out var feats)) continue;
            foreach (var f in feats.EnumerateArray())
            {
                var p = f.GetProperty("properties"); var g = f.GetProperty("geometry").GetProperty("coordinates");
                var cid = p.TryGetProperty("cid", out var c) ? c.GetString() ?? "" : ""; if (cid.Length == 0) continue;
                cams.Add(new Cam
                {
                    Id = "iem:" + cid, Source = "iem", Name = (p.TryGetProperty("name", out var n) ? n.GetString() : cid) + (p.TryGetProperty("state", out var st) ? ", " + st.GetString() : ""),
                    Lon = g[0].GetDouble(), Lat = g[1].GetDouble(), HeadingDeg = p.TryGetProperty("angle", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetDouble() : null,
                    // the current frame is always at this path; the geojson's imgurl is the archive copy of the same frame
                    Url = $"https://mesonet.agron.iastate.edu/data/camera/stills/{cid}.jpg", RefreshSec = 300, Licence = "Iowa Environmental Mesonet webcam (" + net + ")"
                });
            }
        }
        return _store.UpsertMany(cams);
    }

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16].ToLowerInvariant();
    private static double? HeadingFrom(string? dir) => dir?.Trim().ToUpperInvariant() switch
    {
        "N" => 0, "NNE" => 22.5, "NE" => 45, "ENE" => 67.5, "E" => 90, "ESE" => 112.5, "SE" => 135, "SSE" => 157.5,
        "S" => 180, "SSW" => 202.5, "SW" => 225, "WSW" => 247.5, "W" => 270, "WNW" => 292.5, "NW" => 315, "NNW" => 337.5, _ => null
    };
}
