using System.Text.Json;
using CampRadar.Api.Lightning;
using CampRadar.Api.Nws;
using CampRadar.Api.Push;
using CampRadar.Api.Radar;
using CampRadar.Api.Spots;

using CampRadar.Api.Verify;

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
    private readonly BriefingFactsBuilder _facts;
    private readonly BriefingWriter _writer;
    private readonly GroupStore _groups;
    private readonly SiteStore _sites;
    private readonly NwsForecastClient _fc;
    private readonly Ledger _ledger;
    private long _lastBriefMs;
    private readonly PushSender _push;
    private readonly ILogger<AlertWorker> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _lastRadarMs, _lastGlmMs, _lastEvalMs, _lastPruneMs;
    public DateTimeOffset LastCycle { get; private set; }
    public string LastCycleNote { get; private set; } = "not yet";

    public AlertWorker(MrmsClient mrms, GlmIngestor glm, StatusService status, SpotStore spots, EntitlementStore ent, PushSender push, BriefingFactsBuilder facts, BriefingWriter writer, GroupStore groups, SiteStore sites, NwsForecastClient fc, Ledger ledger, ILogger<AlertWorker> log)
    { _ledger = ledger; _mrms = mrms; _glm = glm; _status = status; _spots = spots; _ent = ent; _push = push; _facts = facts; _writer = writer; _groups = groups; _sites = sites; _fc = fc; _log = log; }

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
            if (now - _lastEvalMs > 300_000) { int n = await EvaluateSpotsAsync(ct); int ns = await EvaluateSitesAsync(ct); _lastEvalMs = now; notes.Add($"evaluated {n} spots" + (ns > 0 ? $", {ns} site pushes" : "")); }
            if (now - _lastBriefMs > 60_000) { int b = await MorningBriefingsAsync(ct); _lastBriefMs = now; if (b > 0) notes.Add($"{b} morning briefings"); }
            if (now - _lastPruneMs > 3_600_000) { _mrms.PruneCache(TimeSpan.FromHours(5)); _groups.Prune(); _lastPruneMs = now; }
            LastCycle = DateTimeOffset.UtcNow; LastCycleNote = string.Join(", ", notes);
            return LastCycleNote;
        }
        finally { _gate.Release(); }
    }

    private sealed class SentState { public string Rain { get; set; } = "none"; public string Lightning { get; set; } = "none"; public List<string> Warnings { get; set; } = new(); public long RainSentMs { get; set; } public long LightningSentMs { get; set; } public long HailSentMs { get; set; } }

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
                var (activity, leadTime) = ParseLead(spot.Prefs);
                var opt = new StatusOptions(prefs.TryGetValue("hitMi", out var h) ? h : 3, prefs.TryGetValue("threshDbz", out var t) ? (float)t : 20, Activity: activity, LeadTime: leadTime);
                var s = await _status.GetAsync(spot.Lat, spot.Lon, opt, ct);
                _ledger.Log("spot", "s:" + Ledger.Key(spot.Id), spot.Lat, spot.Lon, s);
                var prev = JsonSerializer.Deserialize<SentState>(spot.LastState) ?? new SentState();
                var next = new SentState { Rain = prev.Rain, Lightning = prev.Lightning, Warnings = prev.Warnings, RainSentMs = prev.RainSentMs, LightningSentMs = prev.LightningSentMs, HailSentMs = prev.HailSentMs };
                bool snoozed = now < spot.SnoozeUntil;
                // Pro lapsed (or never set): critical NWS warnings still go out; rain and lightning alerts do not.
                string email = ExtractEmail(spot.Prefs);
                bool pro = email.Length > 0 && _ent.IsPro(email);
                if (!pro) snoozed = true;
                var msgs = new List<(string Tag, string Title, string Body, bool Urgent)>();

                // NWS critical warnings: always, once per alert id, never snoozed.
                var crit = s.Alerts.Where(a => a.Critical).ToList();
                foreach (var a in crit.Where(a => !prev.Warnings.Contains(a.Id)))
                    msgs.Add(("warning", a.Event.ToUpperInvariant() + (a.MaxHailIn >= 0.75 ? $" · hail to {a.MaxHailIn} in" : ""), (a.MaxHailIn >= 0.75 ? $"Hail to {a.MaxHailIn} in" + (a.MaxWindGust is { } g ? $", wind to {g}" : "") + ". " : "") + (a.Headline ?? "Take shelter now. Open the app for details."), true));
                next.Warnings = s.AlertsUnknown ? prev.Warnings : crit.Select(a => a.Id).ToList();

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

                // Hail: a 58+ dBZ core in rain that is heading here (Pro), at most once an hour.
                if (pro && s.Rain.Level is "incoming" or "imminent" or "atyou" && (s.Rain.MaxDbz ?? 0) >= 58 && now - prev.HailSentMs > 60 * 60000L)
                { msgs.Add(("hail", "Hail possible", $"{Math.Round(s.Rain.MaxDbz!.Value)} dBZ core in the storm heading your way" + (s.Rain.EtaMin is { } eta && eta > 1 ? $", about {Math.Round(eta)} min out" : "") + ". Get people and vehicles under cover.", true)); next.HailSentMs = now; }

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

    /// <summary>
    /// Opt-in morning briefing: prefs.brief = { at:"06:30", days:"all"|"weekdays"|"weekends", tz:"America/Chicago", offsetMin:-360, lang:"en"|"es", activity }.
    /// Sent once per local day, at or after the chosen time, to Pro devices seen in the last 30 days. The push carries
    /// the whole text; tapping it opens the app, which reads it aloud.
    /// </summary>
    private async Task<int> MorningBriefingsAsync(CancellationToken ct)
    {
        int sent = 0;
        var nowUtc = DateTimeOffset.UtcNow;
        foreach (var spot in _spots.All())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var doc = JsonDocument.Parse(spot.Prefs);
                if (!doc.RootElement.TryGetProperty("brief", out var b) || b.ValueKind != JsonValueKind.Object) continue;
                if (!b.TryGetProperty("at", out var atEl) || atEl.ValueKind != JsonValueKind.String) continue;
                int offsetMin = b.TryGetProperty("offsetMin", out var om) && om.ValueKind == JsonValueKind.Number ? om.GetInt32() : -360;
                var local = nowUtc.ToOffset(TimeSpan.FromMinutes(offsetMin));
                string today = local.ToString("yyyy-MM-dd");
                if (spot.LastBrief == today) continue;
                var parts = (atEl.GetString() ?? "06:30").Split(':'); int atH = int.Parse(parts[0]), atM = parts.Length > 1 ? int.Parse(parts[1]) : 0;
                if (local.Hour * 60 + local.Minute < atH * 60 + atM) continue;
                if (local.Hour * 60 + local.Minute > atH * 60 + atM + 180) { _spots.SetLastBrief(spot.Id, today); continue; }   // missed by 3 h (app offline?) → skip today rather than wake someone at noon
                string days = b.TryGetProperty("days", out var d) ? d.GetString() ?? "all" : "all";
                bool weekend = local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
                if ((days == "weekdays" && weekend) || (days == "weekends" && !weekend)) { _spots.SetLastBrief(spot.Id, today); continue; }
                if (nowUtc.ToUnixTimeMilliseconds() - spot.UpdatedMs > 30L * 86400000) continue;
                string email = ExtractEmail(spot.Prefs);
                if (email.Length == 0 || !_ent.IsPro(email)) continue;
                string lang = b.TryGetProperty("lang", out var l) ? l.GetString() ?? "en" : "en";
                string activity = b.TryGetProperty("activity", out var a) ? a.GetString() ?? "camping" : "camping";

                // saved places with the digest switch on ride along in the same briefing
                var lines = new List<string>();
                foreach (var site in _sites.ForEmail(email).Where(x => x.Digest).Take(6))
                {
                    try { var sf = await _fc.GetAsync(site.Lat, site.Lon, ct); if (sf is not null) { var ln = Planner.DigestLine(site.Name, sf, nowUtc.ToOffset(sf.Offset), lang); if (ln.Length > 0) lines.Add(ln); } }
                    catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "digest line failed for site {Id}", site.Id); }
                }
                var facts = await _facts.BuildAsync(spot.Lat, spot.Lon, activity, lang, ct, lines);
                if (facts is null) { _log.LogWarning("morning briefing: no forecast for spot {Id}", spot.Id); continue; }
                var plain = PlainBriefing.Compose(facts.Value, lang);
                var (text, provider) = await _writer.WriteAsync(facts.Value, plain, ct);
                text ??= plain;
                var place = facts.Value.TryGetProperty("place", out var pl) ? pl.GetString() : null;
                string title = (lang == "es" ? "Informe de la mañana" : "Morning briefing") + (string.IsNullOrEmpty(place) ? "" : " · " + place);
                // notification body: the first two sentences; the full text rides along for the app to read aloud
                var sentences = System.Text.RegularExpressions.Regex.Split(text, @"(?<=[.!?])\s+");
                string body = string.Join(" ", sentences.Take(2));
                var ok = await _push.SendAsync(spot, new { title, body, tag = "brief", level = "info", brief = text, lang, at = nowUtc.ToUnixTimeMilliseconds() }, "brief", false, ct);
                _spots.SetLastBrief(spot.Id, today);
                if (ok) { sent++; _log.LogInformation("morning briefing sent to {Id} via {Provider}", spot.Id, provider ?? "plain"); }
                else _spots.RecordFailure(spot.Id, gone: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "morning briefing failed for spot {Id}", spot.Id); }
        }
        return sent;
    }

    private sealed class SiteState { public string Rain { get; set; } = "none"; public string Lightning { get; set; } = "none"; public List<string> Warnings { get; set; } = new(); public long RainSentMs { get; set; } public long LightningSentMs { get; set; } public Dictionary<string, string> ThrSent { get; set; } = new(); }

    /// <summary>
    /// Saved places with alerts on: same rain/lightning/warning edge detection as a device, plus the owner's thresholds against the
    /// next 12 forecast hours (each threshold at most once per local day). Pushes go to every device signed in with the owner's email.
    /// </summary>
    private async Task<int> EvaluateSitesAsync(CancellationToken ct)
    {
        var sites = _sites.All().Where(s => s.Alerts).ToList();
        if (sites.Count == 0) return 0;
        var spots = _spots.All();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int n = 0;
        foreach (var site in sites)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!_ent.IsPro(site.Email)) continue;
                var devices = spots.Where(sp => string.Equals(ExtractEmail(sp.Prefs), site.Email, StringComparison.OrdinalIgnoreCase) && sp.Failures < 10).ToList();
                if (devices.Count == 0) continue;
                var thr = SiteStore.ParseThresholds(site.Thresholds);
                var s = await _status.GetAsync(site.Lat, site.Lon, new StatusOptions(3, 20), ct);
                _ledger.Log("site", "p:" + Ledger.Key(site.Id), site.Lat, site.Lon, s);
                var prev = JsonSerializer.Deserialize<SiteState>(site.LastState) ?? new SiteState();
                var next = new SiteState { Rain = prev.Rain, Lightning = prev.Lightning, Warnings = prev.Warnings, RainSentMs = prev.RainSentMs, LightningSentMs = prev.LightningSentMs, ThrSent = prev.ThrSent };
                var msgs = new List<(string Tag, string Title, string Body, bool Urgent)>();
                string pfx = site.Name + ": ";

                var crit = s.Alerts.Where(a => a.Critical).ToList();
                foreach (var a in crit.Where(a => !prev.Warnings.Contains(a.Id))) msgs.Add(("warning", pfx + a.Event.ToUpperInvariant(), a.Headline ?? "Take shelter now.", true));
                next.Warnings = s.AlertsUnknown ? prev.Warnings : crit.Select(a => a.Id).ToList();

                int Rank(string l) => l switch { "incoming" => 1, "imminent" => 2, "atyou" => 2, _ => 0 };
                if (Rank(s.Rain.Level) > Rank(prev.Rain) && now - prev.RainSentMs > 20 * 60000L) { msgs.Add(("rain", pfx + s.Rain.Main, s.Rain.Sub, Rank(s.Rain.Level) >= 2)); next.RainSentMs = now; }
                next.Rain = Rank(s.Rain.Level) == 0 ? "none" : (Rank(s.Rain.Level) >= Rank(prev.Rain) ? s.Rain.Level : prev.Rain);

                if (s.LightningLevel == "alarm" && prev.Lightning != "alarm" && now - prev.LightningSentMs > 30 * 60000L) { msgs.Add(("lightning", pfx + "Lightning within 10 miles", s.Main, true)); next.LightningSentMs = now; }
                next.Lightning = s.LightningLevel;

                if (thr.Count > 0)
                {
                    var f = await _fc.GetAsync(site.Lat, site.Lon, ct);
                    if (f is not null)
                    {
                        var local = DateTimeOffset.UtcNow.ToOffset(f.Offset); string today = local.ToString("yyyy-MM-dd");
                        if (local.Hour >= 6 && local.Hour < 22)
                            foreach (var hit in Planner.ThresholdHits(f, local, thr, 12))
                            {
                                if (next.ThrSent.TryGetValue(hit.Key, out var d) && d == today) continue;
                                msgs.Add(("thr-" + hit.Key, pfx + hit.Label, f.Place.Length > 0 ? "Forecast for " + f.Place : "From the hourly forecast", false));
                                next.ThrSent[hit.Key] = today;
                            }
                    }
                }

                foreach (var m in msgs)
                    foreach (var dev in devices)
                    {
                        var ok = await _push.SendAsync(dev, new { title = m.Title, body = m.Body, tag = m.Tag + ":" + site.Id, level = s.Level, lat = site.Lat, lon = site.Lon, site = site.Name, siteId = site.Id, at = now }, m.Tag, m.Urgent, ct);
                        if (!ok) _spots.RecordFailure(dev.Id, gone: true); else n++;
                    }
                _sites.SetState(site.Id, JsonSerializer.Serialize(next));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "site {Id} evaluation failed", site.Id); }
        }
        return n;
    }

    // activity + lead-time switch ride in the prefs the app posts (top level; the morning-brief block carries activity too, older apps only there)
    private static (string Activity, bool LeadTime) ParseLead(string json)
    {
        string act = "camping"; bool lead = true;
        try
        {
            using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
            if (root.TryGetProperty("activity", out var a) && a.ValueKind == JsonValueKind.String) act = a.GetString() ?? act;
            else if (root.TryGetProperty("brief", out var b) && b.ValueKind == JsonValueKind.Object && b.TryGetProperty("activity", out var ba) && ba.ValueKind == JsonValueKind.String) act = ba.GetString() ?? act;
            if (root.TryGetProperty("leadTime", out var l) && l.ValueKind == JsonValueKind.String) lead = l.GetString() != "off";
        }
        catch { }
        return (act, lead);
    }

    private static Dictionary<string, double> Parse(string json)
    {
        var d = new Dictionary<string, double>();
        try { using var doc = JsonDocument.Parse(json); foreach (var p in doc.RootElement.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.Number) d[p.Name] = p.Value.GetDouble(); } catch { }
        return d;
    }
}
