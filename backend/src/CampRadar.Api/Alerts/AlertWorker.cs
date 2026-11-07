using System.Text.Json;
using CampRadar.Api.Lightning;
using CampRadar.Api.Push;
using CampRadar.Api.Radar;
using CampRadar.Api.Spots;

namespace CampRadar.Api.Alerts;

/// <summary>
/// The heartbeat. Runs as a background loop while the site is alive, and can also be driven by
/// the host's Scheduled Task hitting /tick, so alerts still go out if IIS puts the app to sleep.
/// Both paths call RunCycleAsync, which never overlaps with itself.
/// </summary>
public sealed class AlertWorker : BackgroundService
{
    private readonly MrmsClient _mrms;
    private readonly GlmIngestor _glm;
    private readonly StatusService _status;
    private readonly SpotStore _spots;
    private readonly EntitlementStore _ent;
    private readonly PushSender _push;
    private readonly ILogger<AlertWorker> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _lastRadarMs, _lastGlmMs, _lastEvalMs, _lastPruneMs;
    public DateTimeOffset LastCycle { get; private set; }
    public string LastCycleNote { get; private set; } = "not yet";

    public AlertWorker(MrmsClient mrms, GlmIngestor glm, StatusService status, SpotStore spots, EntitlementStore ent, PushSender push, ILogger<AlertWorker> log)
    { _mrms = mrms; _glm = glm; _status = status; _spots = spots; _ent = ent; _push = push; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        while (!ct.IsCancellationRequested)
        {
            try { await RunCycleAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogError(ex, "cycle failed"); }
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }

    /// <summary>One pass: refresh radar times (2 min), ingest lightning (1 min), evaluate spots (5 min, or when a new scan arrives).</summary>
    public async Task<string> RunCycleAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) return "busy";
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var notes = new List<string>();
            if (now - _lastRadarMs > 120_000) { long before = _mrms.Latest; await _mrms.RefreshTimesAsync(ct); _lastRadarMs = now; if (_mrms.Latest != before) { notes.Add("new radar scan"); _lastEvalMs = 0; } }
            if (now - _lastGlmMs > 60_000) { await _glm.RunAsync(ct); _lastGlmMs = now; notes.Add("glm ok"); }
            if (now - _lastEvalMs > 300_000) { int n = await EvaluateSpotsAsync(ct); _lastEvalMs = now; notes.Add($"evaluated {n} spots"); }
            if (now - _lastPruneMs > 3_600_000) { _mrms.PruneCache(TimeSpan.FromHours(5)); _lastPruneMs = now; }
            LastCycle = DateTimeOffset.UtcNow; LastCycleNote = string.Join(", ", notes);
            return LastCycleNote;
        }
        finally { _gate.Release(); }
    }

    private sealed class SentState { public string Rain { get; set; } = "none"; public string Lightning { get; set; } = "none"; public List<string> Warnings { get; set; } = new(); public long RainSentMs { get; set; } public long LightningSentMs { get; set; } }

    private static string ExtractEmail(string prefsJson)
    {
        try { using var d = JsonDocument.Parse(prefsJson); return d.RootElement.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : ""; } catch { return ""; }
    }

    private async Task<int> EvaluateSpotsAsync(CancellationToken ct)
    {
        var spots = _spots.All();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var spot in spots)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var prefs = Parse(spot.Prefs);
                var opt = new StatusOptions(prefs.TryGetValue("hitMi", out var h) ? h : 3, prefs.TryGetValue("threshDbz", out var t) ? (float)t : 20);
                var s = await _status.GetAsync(spot.Lat, spot.Lon, opt, ct);
                var prev = JsonSerializer.Deserialize<SentState>(spot.LastState) ?? new SentState();
                var next = new SentState { Rain = prev.Rain, Lightning = prev.Lightning, Warnings = prev.Warnings, RainSentMs = prev.RainSentMs, LightningSentMs = prev.LightningSentMs };
                bool snoozed = now < spot.SnoozeUntil;
                // Pro lapsed (or never set): critical NWS warnings still go out; rain and lightning alerts do not.
                string email = ExtractEmail(spot.Prefs);
                bool pro = email.Length > 0 && (_ent.Get(email)?.Active ?? false);
                if (!pro) snoozed = true;
                var msgs = new List<(string Tag, string Title, string Body, bool Urgent)>();

                // NWS critical warnings: always, once per alert id, never snoozed.
                var crit = s.Alerts.Where(a => a.Critical).ToList();
                foreach (var a in crit.Where(a => !prev.Warnings.Contains(a.Id)))
                    msgs.Add(("warning", a.Event.ToUpperInvariant(), a.Headline ?? "Take shelter now. Open the app for details.", true));
                next.Warnings = crit.Select(a => a.Id).ToList();

                // Rain: notify on escalation (none/passing → incoming → imminent/atyou), re-arm after clearing; at most one per level per 2 h.
                int Rank(string l) => l switch { "incoming" => 1, "imminent" => 2, "atyou" => 2, "low" => 0, _ => 0 };
                string rl = s.Rain.Level;
                if (Rank(rl) > Rank(prev.Rain) && !snoozed && now - prev.RainSentMs > 20 * 60000L)
                {
                    msgs.Add(("rain", s.Rain.Main, s.Rain.Sub, Rank(rl) >= 2));
                    next.RainSentMs = now;
                }
                next.Rain = Rank(rl) == 0 && Rank(prev.Rain) > 0 && s.Rain.Level is "none" or "passing" ? "none" : (Rank(rl) >= Rank(prev.Rain) ? rl : prev.Rain);
                if (Rank(rl) == 0) next.Rain = "none";

                // Lightning: notify when strikes first appear within the alarm radius; re-notify after 30 quiet minutes. Not snoozed.
                int LRank(string l) => l switch { "alarm" => 2, "near20" => 1, _ => 0 };
                if (pro && LRank(s.LightningLevel) == 2 && LRank(prev.Lightning) < 2 && now - prev.LightningSentMs > 30 * 60000L)
                { msgs.Add(("lightning", "Lightning within 10 miles", s.Main, true)); next.LightningSentMs = now; }
                next.Lightning = s.LightningLevel;

                foreach (var m in msgs)
                {
                    var ok = await _push.SendAsync(spot, new { title = m.Title, body = m.Body, tag = m.Tag, level = s.Level, lat = spot.Lat, lon = spot.Lon, at = now }, m.Tag, m.Urgent, ct);
                    if (!ok) { _spots.RecordFailure(spot.Id, gone: true); break; }
                }
                _spots.SetState(spot.Id, JsonSerializer.Serialize(next));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "spot {Id} evaluation failed", spot.Id);
                _spots.RecordFailure(spot.Id, gone: false);
            }
        }
        return spots.Count;
    }

    private static Dictionary<string, double> Parse(string json)
    {
        var d = new Dictionary<string, double>();
        try { using var doc = JsonDocument.Parse(json); foreach (var p in doc.RootElement.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.Number) d[p.Name] = p.Value.GetDouble(); } catch { }
        return d;
    }
}
