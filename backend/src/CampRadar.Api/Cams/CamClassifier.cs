using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CampRadar.Api.Alerts;

namespace CampRadar.Api.Cams;

/// <summary>
/// Turns one webcam frame into a weather verdict. Cheap checks first (fetch, size, placeholder/identical-frame hash, day/night from the
/// sun), then one vision-model call (Gemini, the briefing model's family) with a fixed question and a JSON answer. No per-camera
/// thresholds: the model reads the scene; the camera's reference frame and cues are passed as context when we have them.
/// </summary>
public sealed class CamClassifier
{
    private readonly IHttpClientFactory _f; private readonly IConfiguration _cfg; private readonly ILogger<CamClassifier> _log;
    public CamClassifier(IHttpClientFactory f, IConfiguration cfg, ILogger<CamClassifier> log) { _f = f; _cfg = cfg; _log = log; }

    public bool Enabled => !string.IsNullOrEmpty(_cfg["Gemini:ApiKey"]) && (_cfg["Cams:Enabled"] ?? "true") != "false";
    public string Model => _cfg["Cams:Model"] ?? _cfg["Gemini:Model"] ?? "gemini-3.5-flash-lite";

    public sealed record Frame(byte[] Bytes, string Hash, string ContentType, long ImgMs);

    /// <summary>Fetch the current frame. Null when the camera gives nothing usable (error, tiny placeholder, not an image).</summary>
    public async Task<Frame?> FetchAsync(Cam cam, CancellationToken ct)
    {
        var frame = await FetchUrlAsync(cam.Source == "otcm" && !cam.Url.Contains('?') ? cam.Url + "?_t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() : cam.Url, ct);
        if (frame is null && cam.Source == "iem")
        {   // the stills path can lag or move; the network geojson always names the current archive frame
            var cur = await IemCurrentUrlAsync(cam.Id["iem:".Length..], ct);
            if (cur is not null) frame = await FetchUrlAsync(cur.Value.Url, ct) is { } f ? f with { ImgMs = cur.Value.Ms } : null;
        }
        return frame;
    }

    private readonly Dictionary<string, (long At, Dictionary<string, (string Url, long Ms)> Map)> _iemCache = new();
    private async Task<(string Url, long Ms)?> IemCurrentUrlAsync(string cid, CancellationToken ct)
    {
        var net = cid.Split('-')[0]; long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_iemCache) if (_iemCache.TryGetValue(net, out var c) && now - c.At < 240_000) return c.Map.TryGetValue(cid, out var hit) ? hit : null;
        try
        {
            var json = await _f.CreateClient("obs").GetStringAsync($"https://mesonet.agron.iastate.edu/geojson/webcam.py?network={Uri.EscapeDataString(net)}", ct);
            using var doc = JsonDocument.Parse(json); var map = new Dictionary<string, (string, long)>();
            foreach (var f in doc.RootElement.GetProperty("features").EnumerateArray())
            {
                var p = f.GetProperty("properties"); var id = p.GetProperty("cid").GetString() ?? ""; var u = p.TryGetProperty("imgurl", out var iu) ? iu.GetString() : null;
                long ms = p.TryGetProperty("utc_valid", out var v) && DateTimeOffset.TryParse(v.GetString(), null, System.Globalization.DateTimeStyles.AssumeUniversal, out var dt) ? dt.ToUnixTimeMilliseconds() : now;
                if (id.Length > 0 && u is not null) map[id] = (u, ms);
            }
            lock (_iemCache) _iemCache[net] = (now, map);
            return map.TryGetValue(cid, out var hit) ? hit : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "iem current {Net}", net); return null; }
    }

    private async Task<Frame?> FetchUrlAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "VantageWX/1.0 (+https://vantagewx.com; weather verification)");
            using var resp = await _f.CreateClient("obs").SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var type = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (!type.StartsWith("image/")) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length < 3000 || bytes.Length > 6_000_000) return null;   // placeholders are tiny; streams are not frames
            long imgMs = resp.Content.Headers.LastModified?.ToUnixTimeMilliseconds() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return new Frame(bytes, Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant(), type, imgMs);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "cam fetch {Url}", url); return null; }
    }

    /// <summary>day | night | twilight at the camera, from NOAA sun times.</summary>
    public static string DayPart(double lat, double lon, DateTimeOffset at)
    {
        var sun = Sun.Times(lat, lon, at.UtcDateTime.Date, TimeSpan.Zero) ?? Sun.Times(lat, lon, at.UtcDateTime.Date.AddDays(-1), TimeSpan.Zero);
        if (sun is null) return Math.Abs(lat) > 60 ? "night" : "day";
        // the UTC date's rise/set may straddle midnight for western longitudes: test against both the given day's and the neighbours'
        foreach (var d in new[] { 0, -1, 1 })
        {
            var s = d == 0 ? sun : Sun.Times(lat, lon, at.UtcDateTime.Date.AddDays(d), TimeSpan.Zero); if (s is null) continue;
            if (at >= s.Rise && at <= s.Set) return "day";
            if (at >= s.Dawn && at < s.Rise || at > s.Set && at <= s.Dusk) return "twilight";
        }
        return "night";
    }

    public sealed record Verdict(string Precip, string Intensity, string Ground, string Vis, string Sky, string Lens, string Wind, double Conf, string Cues, string Json);

    private const string System = """
        You are classifying a single still image from a public outdoor webcam for weather verification. Answer ONLY with a JSON object, no prose.
        Fields and allowed values:
          precip: none | rain | snow | mixed | unsure         (precipitation falling NOW in the scene; streaks, drops in the air, snowflakes, haze from heavy rain)
          intensity: none | light | heavy
          ground: dry | wet | puddled | snow | ice | unsure | n/a   (n/a when no ground surface is visible; wet = darkened/reflective pavement or ground)
          vis: clear | reduced | fog | dense | unsure         (clear > 5 mi; reduced 1–5 mi, haze; fog 1/4–1 mi; dense < 1/4 mi)
          sky: clear | partly | overcast | lowdark | towering | shaft | unsure | n/a   (lowdark = low dark cloud base; towering = cumulonimbus; shaft = visible precipitation shaft in the distance)
          lens: ok | drops | obstructed | dirty | dark        (dark = image too dark to judge anything; drops = water on the lens itself)
          wind: calm | breezy | strong | severe | unsure | n/a   (only from visible cues: flags, trees, whitecaps, blowing snow/dust; n/a if no cue)
          conf: 0..1 overall confidence
          cues: array of which of [sky, pavement, flag, trees, water, markers] are visible in this camera's view
          note: at most 12 words
        Be conservative: when the image is dark, blurred or a placeholder, say unsure or n/a and lower conf. Do not infer rain from wet ground alone; wet ground is its own field.
        """;

    public async Task<Verdict?> ClassifyAsync(Frame frame, Cam cam, string dayPart, CancellationToken ct)
    {
        var model = Model;
        var context = $"Camera: {cam.Name}. Local sun: {dayPart}." + (cam.HeadingDeg is { } h ? $" Camera faces {Math.Round(h)}°." : "") + (cam.Cues.Length > 0 ? $" Previously visible cues: {cam.Cues}." : "");
        object gen = model.Contains("2.5") ? new { maxOutputTokens = 300, temperature = 0.1, responseMimeType = "application/json", thinkingConfig = new { thinkingBudget = 0 } }
                                            : new { maxOutputTokens = 300, temperature = 0.1, responseMimeType = "application/json" };
        var body = new
        {
            system_instruction = new { parts = new[] { new { text = System } } },
            contents = new[] { new { role = "user", parts = new object[] { new { text = context }, new { inline_data = new { mime_type = frame.ContentType, data = Convert.ToBase64String(frame.Bytes) } } } } },
            generationConfig = gen
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent");
        req.Headers.Add("x-goog-api-key", _cfg["Gemini:ApiKey"]);
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await _f.CreateClient("llm").SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) { _log.LogWarning("cam classify {Id}: Gemini {Code} {Body}", cam.Id, (int)resp.StatusCode, json.Length > 300 ? json[..300] : json); return null; }
        string text;
        using (var doc = JsonDocument.Parse(json))
        {
            var sb = new StringBuilder();
            foreach (var cand in doc.RootElement.GetProperty("candidates").EnumerateArray())
                if (cand.TryGetProperty("content", out var c) && c.TryGetProperty("parts", out var parts))
                    foreach (var part in parts.EnumerateArray()) if (part.TryGetProperty("text", out var t)) sb.Append(t.GetString());
            text = sb.ToString().Trim();
        }
        if (text.StartsWith("```")) { text = text.Trim('`'); if (text.StartsWith("json")) text = text[4..]; text = text.Trim(); }
        try
        {
            using var v = JsonDocument.Parse(text); var r = v.RootElement;
            string S(string k, string dflt = "unsure") => r.TryGetProperty(k, out var e) && e.ValueKind == JsonValueKind.String ? (e.GetString() ?? dflt).ToLowerInvariant() : dflt;
            double conf = r.TryGetProperty("conf", out var ce) && ce.ValueKind == JsonValueKind.Number ? Math.Clamp(ce.GetDouble(), 0, 1) : 0.5;
            string cues = r.TryGetProperty("cues", out var cu) && cu.ValueKind == JsonValueKind.Array ? string.Join(",", cu.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString())) : "";
            return new Verdict(S("precip"), S("intensity", "none"), S("ground"), S("vis"), S("sky"), S("lens", "ok"), S("wind", "n/a"), conf, cues, text);
        }
        catch (JsonException) { _log.LogDebug("cam classify {Id}: unparsable answer {Text}", cam.Id, text.Length > 200 ? text[..200] : text); return null; }
    }
}
