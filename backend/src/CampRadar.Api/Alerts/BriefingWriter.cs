using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CampRadar.Api.Alerts;

/// <summary>
/// Turns the app's structured weather facts into a short spoken-English briefing with a language model.
/// Configure Anthropic:ApiKey (and optionally Anthropic:Model) to enable; otherwise /brief returns 503 and the
/// device uses its own template text.
/// </summary>
public sealed class BriefingWriter
{
    private readonly HttpClient _http;
    private readonly string? _key;
    private readonly string _model;
    private readonly ILogger<BriefingWriter> _log;
    public bool Enabled => !string.IsNullOrWhiteSpace(_key);

    public BriefingWriter(IHttpClientFactory f, IConfiguration cfg, ILogger<BriefingWriter> log)
    {
        _http = f.CreateClient("anthropic"); _log = log;
        _key = cfg["Anthropic:ApiKey"]; _model = cfg["Anthropic:Model"] ?? "claude-haiku-5-5";
    }

    private const string System = """
        You write short spoken weather briefings for someone who is outdoors: camping, in an RV, hunting, or on a trail.
        You are given facts as JSON (current conditions, rain-arrival prediction, lightning, NWS alerts, nearby hazards,
        the next hours, sun times, pressure) and the person's activity. Write 80 to 140 words of plain spoken English,
        no markdown, no lists, no headings, numbers rounded, times as spoken ("about four thirty").
        Lead with anything dangerous (NWS warnings, lightning within ten miles, rain arriving within fifteen minutes,
        wildfire or flooding nearby). Then what matters for the activity: RV = wind and gusts, hail, awning and driving;
        hunting = wind direction and shifts, pressure trend, legal light, moon; trail = rain and thunder timing,
        feels-like temperature, daylight remaining; camping = rain timing, overnight low, wind, lightning.
        Give the arrival time of rain and say whether it is strengthening, weakening, or likely to fizzle, when the facts say so.
        Never contradict or soften an NWS warning. Never invent facts that are not in the JSON. End with one practical sentence.
        """;

    public async Task<string?> WriteAsync(JsonElement facts, string? fallback, CancellationToken ct)
    {
        if (!Enabled) return null;
        var body = new
        {
            model = _model, max_tokens = 400, system = System,
            messages = new[] { new { role = "user", content = "Facts:\n" + facts.GetRawText() + (fallback is null ? "" : "\n\nA plain template version, for reference only:\n" + fallback) } }
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        req.Headers.Add("x-api-key", _key); req.Headers.Add("anthropic-version", "2023-06-01");
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) { _log.LogWarning("briefing model returned {Status}: {Body}", resp.StatusCode, await resp.Content.ReadAsStringAsync(ct)); return null; }
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var sb = new StringBuilder();
        foreach (var block in doc.RootElement.GetProperty("content").EnumerateArray())
            if (block.TryGetProperty("text", out var t)) sb.Append(t.GetString());
        var text = sb.ToString().Trim();
        return text.Length > 0 ? text : null;
    }
}
