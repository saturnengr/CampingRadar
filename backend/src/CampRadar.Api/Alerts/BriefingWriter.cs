using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CampRadar.Api.Alerts;

/// <summary>
/// Turns the app's structured weather facts into a short spoken-English (or Spanish) briefing with a language model.
/// Providers are tried in the order given by Briefing:Providers (default "gemini,anthropic,openai"); each is used only
/// when its key is configured, and the next one is tried if a call fails. Configure any of:
///   Gemini:ApiKey    (+ Gemini:Model, default gemini-flash-latest)            — Google AI Studio key; free tier available
///   Anthropic:ApiKey (+ Anthropic:Model, default claude-haiku-5-5)
///   OpenAI:ApiKey    (+ OpenAI:Model, OpenAI:BaseUrl)                           — any OpenAI-compatible endpoint (Groq, OpenRouter, Cloudflare…)
/// With none configured, /brief returns 503 and the device uses its own template text.
/// </summary>
public sealed class BriefingWriter
{
    private readonly IHttpClientFactory _f;
    private readonly IConfiguration _cfg;
    private readonly ILogger<BriefingWriter> _log;
    public BriefingWriter(IHttpClientFactory f, IConfiguration cfg, ILogger<BriefingWriter> log) { _f = f; _cfg = cfg; _log = log; }

    private bool Has(string key) => !string.IsNullOrWhiteSpace(_cfg[key]);
    public IEnumerable<string> Providers => (_cfg["Briefing:Providers"] ?? "gemini,anthropic,openai").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(p => p switch { "gemini" => Has("Gemini:ApiKey"), "anthropic" => Has("Anthropic:ApiKey"), "openai" => Has("OpenAI:ApiKey"), _ => false });
    public bool Enabled => Providers.Any();

    // Rolling 24-hour tally of briefings written and failed, per provider, for the admin page.
    private readonly object _lock = new();
    private readonly Queue<(long Ms, string Provider, bool Ok)> _log24 = new();
    private void Tally(string provider, bool ok) { lock (_lock) { _log24.Enqueue((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), provider, ok)); if (_log24.Count > 50000) _log24.Dequeue(); } }
    public object Stats24h()
    {
        long cut = DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeMilliseconds();
        lock (_lock)
        {
            while (_log24.Count > 0 && _log24.Peek().Ms < cut) _log24.Dequeue();
            var written = _log24.Where(x => x.Ok).GroupBy(x => x.Provider).ToDictionary(g => g.Key, g => g.Count());
            var failed = _log24.Where(x => !x.Ok).GroupBy(x => x.Provider).ToDictionary(g => g.Key, g => g.Count());
            return new { written = written.Values.Sum(), failed = failed.Values.Sum(), byProvider = written, failedByProvider = failed };
        }
    }
    public string Active => string.Join(",", Providers);

    private const string System = """
        You write short spoken weather briefings for someone who is outdoors: camping, in an RV, hunting, on a trail, at the beach, or skiing.
        You are given facts as JSON (current conditions, rain-arrival prediction, lightning, NWS alerts, nearby hazards,
        the next hours, sun times, pressure) and the person's activity. Write 80 to 140 words of plain spoken English,
        no markdown, no lists, no headings, numbers rounded, times as spoken ("about four thirty").
        Lead with anything dangerous (NWS warnings, lightning within ten miles, rain arriving within fifteen minutes,
        wildfire or flooding nearby). Then what matters for the activity: RV = wind and gusts, hail, awning and driving;
        hunting = wind direction and shifts, pressure trend, legal light, moon; trail = rain and thunder timing,
        feels-like temperature, daylight remaining; camping = rain timing, overnight low, wind, lightning; fishing = wind and chop for small boats, lightning means off the water, pressure trend, solunar periods ("fishing" facts: rating 1–5, majors, minors) and water temperature; beach = rip current risk, tides ("beach" facts: next high
        and low tide times), UV, water temperature, wave height, lightning, and any tropical alerts ("tropical");
        skiing = snow on the ground ("snowGround": SNOTEL depth and recent change, or the modeled depth), new snow expected,
        wind chill, visibility and whiteout risk, snow level, wind holds on lifts; sentinel = a fixed display watching one spot: plain weather, no activity advice.
        When "precipType" is snow, sleet, freezing rain or wintry mix, say that instead of rain throughout.
        Give the arrival time of rain and say whether it is strengthening, weakening, or likely to fizzle, when the facts say so.
        Structure: (1) the current picture and the next few hours ("next"/"nextStats"); (2) the outlook in "outlook":
        when "morning" is true this is the rest of today, this afternoon and evening, so say when rain or storms arrive
        and the overnight low; when false it is tomorrow, so give tomorrow's shape, high, rain chance, wind, and for hunting
        the first light and morning wind. Write the outlook as its own short paragraph beginning "Looking at <label>".
        Make each activity's briefing sound different: an RV briefing is about wind, gusts, hail and the awning; a hunting
        briefing about wind direction and shifts, pressure, legal light and the moon; a trail briefing about rain and thunder
        timing, feels-like temperature and daylight; a camp briefing about rain timing, the overnight low, wind and lightning.
        Language: write the whole briefing in English, or in US Spanish when "language" in the facts is "es-US" (not Castilian;
        keep units as given in miles, miles per hour, degrees Fahrenheit and inches, named in Spanish; keep place names; NWS alert
        names may be translated but should stay recognisable).
        If the facts include "sites" (the person's saved places), add one short sentence per place near the end, using the given text; keep place names exactly.
        Never contradict or soften an NWS warning (tornado, severe thunderstorm, flood, hurricane, tropical storm, storm surge, blizzard, ice storm, winter storm, extreme cold, avalanche or tsunami warnings always come first). Never invent facts that are not in the JSON. End with one practical sentence.
        Target 110 to 170 words in total. Output only the briefing text.
        """;

    public async Task<(string? Text, string? Provider)> WriteAsync(JsonElement facts, string? fallback, CancellationToken ct, List<string>? errors = null)
    {
        var user = "Facts:\n" + facts.GetRawText() + (fallback is null ? "" : "\n\nA plain template version, for reference only:\n" + fallback);
        foreach (var p in Providers)
        {
            var sw = global::System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var text = p switch { "gemini" => await GeminiAsync(user, ct), "anthropic" => await AnthropicAsync(user, ct), "openai" => await OpenAiAsync(user, ct), _ => null };
                text = text?.Trim();
                if (!string.IsNullOrEmpty(text)) { Tally(p, true); _log.LogInformation("briefing written by {Provider} in {Ms} ms", p, sw.ElapsedMilliseconds); return (text, p); }
                _log.LogWarning("briefing provider {Provider} returned no text", p); errors?.Add(p + ": returned no text");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)   // a provider timeout is an ordinary failure; only the caller going away stops the loop
            { var msg = ex is TaskCanceledException ? "timed out after " + sw.ElapsedMilliseconds / 1000 + " s" : ex.Message; Tally(p, false); _log.LogWarning("briefing provider {Provider} failed after {Ms} ms: {Error}", p, sw.ElapsedMilliseconds, msg); errors?.Add(p + ": " + msg); }
        }
        return (null, null);
    }

    private HttpClient Client(string name) => _f.CreateClient(name);

    // ---- Google Gemini: POST generateContent with a system instruction ----
    private async Task<string?> GeminiAsync(string user, CancellationToken ct)
    {
        var model = _cfg["Gemini:Model"] ?? "gemini-3.5-flash-lite";
        // thinkingBudget 0 = answer straight away (2.5 family); other models take the plain config
        object gen = model.Contains("2.5") ? new { maxOutputTokens = 600, temperature = 0.6, thinkingConfig = new { thinkingBudget = 0 } } : new { maxOutputTokens = 600, temperature = 0.6 };
        var body = new
        {
            system_instruction = new { parts = new[] { new { text = System } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = user } } } },
            generationConfig = gen
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent");
        req.Headers.Add("x-goog-api-key", _cfg["Gemini:ApiKey"]);
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await Client("llm").SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Gemini {(int)resp.StatusCode}: {Trunc(json)}");
        using var doc = JsonDocument.Parse(json);
        var sb = new StringBuilder();
        foreach (var cand in doc.RootElement.GetProperty("candidates").EnumerateArray())
            if (cand.TryGetProperty("content", out var c) && c.TryGetProperty("parts", out var parts))
                foreach (var part in parts.EnumerateArray()) if (part.TryGetProperty("text", out var t)) sb.Append(t.GetString());
        return sb.ToString();
    }

    // ---- Anthropic Messages API ----
    private async Task<string?> AnthropicAsync(string user, CancellationToken ct)
    {
        var body = new { model = _cfg["Anthropic:Model"] ?? "claude-haiku-5-5", max_tokens = 400, system = System, messages = new[] { new { role = "user", content = user } } };
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        req.Headers.Add("x-api-key", _cfg["Anthropic:ApiKey"]); req.Headers.Add("anthropic-version", "2023-06-01");
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await Client("llm").SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Anthropic {(int)resp.StatusCode}: {Trunc(json)}");
        using var doc = JsonDocument.Parse(json);
        var sb = new StringBuilder();
        foreach (var block in doc.RootElement.GetProperty("content").EnumerateArray()) if (block.TryGetProperty("text", out var t)) sb.Append(t.GetString());
        return sb.ToString();
    }

    // ---- OpenAI-compatible chat completions (OpenAI, Groq, OpenRouter, Cloudflare, Mistral…) ----
    private async Task<string?> OpenAiAsync(string user, CancellationToken ct)
    {
        var baseUrl = (_cfg["OpenAI:BaseUrl"] ?? "https://api.openai.com/v1").TrimEnd('/');
        var body = new { model = _cfg["OpenAI:Model"] ?? "gpt-4o-mini", max_tokens = 400, temperature = 0.6, messages = new object[] { new { role = "system", content = System }, new { role = "user", content = user } } };
        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cfg["OpenAI:ApiKey"]);
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await Client("llm").SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"{baseUrl} {(int)resp.StatusCode}: {Trunc(json)}");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
    }

    private static string Trunc(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
