using System.Collections.Concurrent;
using System.Text.Json;
using CampRadar.Api.Alerts;
using CampRadar.Api.Radar;
using CampRadar.Api.Spots;

namespace CampRadar.Api.Cams;

/// <summary>
/// Looks at public webcams only where and when it matters, and writes what they show next to what radar said. Targets are the places
/// the status engine already watches: running recordings (always, for the training ledger) and push spots whose last state is incoming,
/// imminent or at-you. For each target the nearest one or two cameras within <c>Cams:RadiusMi</c> are fetched on a cadence that tightens
/// with the status (watch 30 min, incoming 15, imminent/at-you 5), pre-checked, classified, and stored as a cam_obs row; recordings
/// also get a <c>cam.jsonl</c> line beside gauge-madis.jsonl for the replay harness. A daily budget (<c>Cams:MaxPerDay</c>) caps the
/// vision calls. The directory itself is rebuilt weekly from the public registries.
/// </summary>
public sealed class CamWorker : BackgroundService
{
    private readonly CamStore _store; private readonly CamDirectory _dir; private readonly CamClassifier _cls; private readonly Recorder _rec;
    private readonly SpotStore _spots; private readonly StatusService _status; private readonly IConfiguration _cfg; private readonly ILogger<CamWorker> _log;
    public string LastCycle { get; private set; } = "not yet";
    public DateTimeOffset LastCycleAt { get; private set; }
    public ConcurrentDictionary<string, string> Recent { get; } = new();   // cam id → one-line last verdict, for the admin page

    public CamWorker(CamStore store, CamDirectory dir, CamClassifier cls, Recorder rec, SpotStore spots, StatusService status, IConfiguration cfg, ILogger<CamWorker> log)
    { _store = store; _dir = dir; _cls = cls; _rec = rec; _spots = spots; _status = status; _cfg = cfg; _log = log; }

    private double RadiusMi => double.Parse(_cfg["Cams:RadiusMi"] ?? "10", System.Globalization.CultureInfo.InvariantCulture);
    private int PerTarget => int.Parse(_cfg["Cams:PerTarget"] ?? "2");
    private int MaxPerDay => int.Parse(_cfg["Cams:MaxPerDay"] ?? "600");

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if ((_cfg["Cams:Enabled"] ?? "true") == "false") { _log.LogInformation("cams: disabled"); return; }
        await Task.Delay(TimeSpan.FromSeconds(90), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_dir.Due) { var r = await _dir.RefreshAsync(ct); _log.LogInformation("cam directory: {R}", r); _store.PruneFrames(); }
                else { try { await _dir.IemAsync(ct); } catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "iem online"); } }
                LastCycle = await CycleAsync(ct); LastCycleAt = DateTimeOffset.UtcNow;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "cam cycle"); LastCycle = "failed: " + ex.Message; }
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
        }
    }

    public sealed record Target(string Key, double Lat, double Lon, string Level, string? Recording);

    /// <summary>What the engine is watching right now, and how urgently.</summary>
    public List<Target> Targets()
    {
        var list = new List<Target>();
        foreach (var j in _rec.RunningJobs()) list.Add(new Target("rec:" + j.Name, j.Lat, j.Lon, "watch", j.Name));
        foreach (var s in _spots.All())
        {
            string rain = "none";
            try { using var d = JsonDocument.Parse(s.LastState); if (d.RootElement.TryGetProperty("Rain", out var r) && r.ValueKind == JsonValueKind.String) rain = r.GetString() ?? "none"; } catch { }
            if (rain is "incoming" or "imminent" or "atyou") list.Add(new Target("spot:" + Verify.Ledger.Key(s.Id), s.Lat, s.Lon, rain, null));
        }
        return list;
    }

    private static int CadenceMin(string level) => level switch { "atyou" or "imminent" => 5, "incoming" => 15, _ => 30 };

    private async Task<string> CycleAsync(CancellationToken ct)
    {
        if (!_cls.Enabled) return "classifier off (no Gemini key)";
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int usedToday = _store.ObsCountSince(now - 86_400_000L);
        if (usedToday >= MaxPerDay) return $"budget spent ({usedToday}/{MaxPerDay} today)";
        var targets = Targets(); if (targets.Count == 0) return "no targets";
        int looked = 0, classified = 0, stale = 0, skipped = 0;
        var done = new HashSet<string>();
        foreach (var t in targets)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var cam in _store.Near(t.Lat, t.Lon, RadiusMi, PerTarget))
            {
                if (!done.Add(cam.Id)) continue;
                long last = _store.LastObsMs(cam.Id);
                if (now - last < CadenceMin(t.Level) * 60_000L - 30_000) { skipped++; continue; }
                if (usedToday + classified >= MaxPerDay) break;
                looked++;
                var frame = await _cls.FetchAsync(cam, ct);
                if (frame is null) { _store.RecordFetch(cam.Id, false, null); continue; }
                bool identical = frame.Hash == cam.LastHash;
                _store.RecordFetch(cam.Id, true, frame.Hash);
                var obs = new CamObs { CamId = cam.Id, TMs = now, ImgMs = frame.ImgMs, Stale = identical, Day = CamClassifier.DayPart(cam.Lat, cam.Lon, DateTimeOffset.UtcNow), Target = t.Key, StatusLevel = t.Level, Model = _cls.Model };
                if (identical) { obs.Precip = obs.Ground = obs.Vis = obs.Sky = obs.Wind = "n/a"; obs.Intensity = "none"; obs.Lens = "stale"; obs.Json = "{}"; _store.AddObs(obs); stale++; continue; }
                // what radar says over the camera itself (cached status for that point; cheap when the frame is already in memory)
                try
                {
                    var s = await _status.GetAsync(cam.Lat, cam.Lon, new StatusOptions(), ct);
                    obs.RadarDbz = s.Rain.Nearest is { } n && n.DistMi <= 1.5 ? n.Dbz : (s.Rain.Faint is { } f && f.DistMi <= 1.5 ? f.Dbz : 0);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "cam radar {Id}", cam.Id); }
                var v = await _cls.ClassifyAsync(frame, cam, obs.Day, ct);
                if (v is null) { obs.Precip = obs.Ground = obs.Vis = obs.Sky = obs.Wind = "unsure"; obs.Intensity = "none"; obs.Lens = "error"; obs.Json = "{}"; _store.AddObs(obs); continue; }
                obs.Precip = v.Precip; obs.Intensity = v.Intensity; obs.Ground = v.Ground; obs.Vis = v.Vis; obs.Sky = v.Sky; obs.Lens = v.Lens; obs.Wind = v.Wind; obs.Conf = v.Conf; obs.Json = v.Json;
                try { obs.Frame = _store.SaveFrame(cam.Id, now, frame.Bytes, frame.ContentType); } catch (Exception ex) { _log.LogDebug(ex, "save frame"); }
                _store.AddObs(obs); classified++;
                if (v.Cues.Length > 0 && (cam.RefMs == 0 || now - cam.RefMs > 30L * 86_400_000) && v.Precip == "none" && v.Vis == "clear" && v.Lens == "ok" && obs.Day == "day")
                    _store.SetReference(cam.Id, frame.Hash, v.Cues);   // a clear daytime frame becomes the camera's reference
                Recent[cam.Id] = $"{DateTimeOffset.UtcNow:HH:mm}Z {cam.Name}: {v.Precip}/{v.Intensity}, ground {v.Ground}, vis {v.Vis}, sky {v.Sky}, lens {v.Lens}, wind {v.Wind}, conf {v.Conf:0.00}, radar {obs.RadarDbz:0} dBZ ({t.Key})";
                if (t.Recording is { } rec) _rec.AppendAux(rec, "cam.jsonl", JsonSerializer.Serialize(new
                {
                    t = now, img = frame.ImgMs, cam = cam.Id, name = cam.Name, lat = Math.Round(cam.Lat, 4), lon = Math.Round(cam.Lon, 4), distMi = Math.Round(CamStore.Haversine(t.Lat, t.Lon, cam.Lat, cam.Lon), 1),
                    day = obs.Day, precip = v.Precip, intensity = v.Intensity, ground = v.Ground, vis = v.Vis, sky = v.Sky, lens = v.Lens, wind = v.Wind, conf = Math.Round(v.Conf, 2), radarDbz = obs.RadarDbz
                }));
                await Task.Delay(800, ct);   // polite to the camera hosts and to the model's rate limit
            }
        }
        while (Recent.Count > 60) { var k = Recent.Keys.First(); Recent.TryRemove(k, out _); }
        return $"{targets.Count} targets, {looked} frames fetched, {classified} classified, {stale} unchanged, {skipped} not due; {usedToday + classified}/{MaxPerDay} today";
    }

    /// <summary>One camera, now, regardless of cadence or targets — for the admin page.</summary>
    public async Task<object> ClassifyNowAsync(string camId, CancellationToken ct)
    {
        var cam = _store.Get(camId); if (cam is null) return new { error = "no such camera" };
        var frame = await _cls.FetchAsync(cam, ct); _store.RecordFetch(cam.Id, frame is not null, frame?.Hash);
        if (frame is null) return new { cam.Id, cam.Name, cam.Url, error = "no usable frame (error, placeholder, or not an image)" };
        var day = CamClassifier.DayPart(cam.Lat, cam.Lon, DateTimeOffset.UtcNow);
        var v = await _cls.ClassifyAsync(frame, cam, day, ct);
        if (v is null) return new { cam.Id, cam.Name, cam.Url, bytes = frame.Bytes.Length, day, error = "classifier gave no answer" };
        var obs = new CamObs { CamId = cam.Id, TMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ImgMs = frame.ImgMs, Day = day, Precip = v.Precip, Intensity = v.Intensity, Ground = v.Ground, Vis = v.Vis, Sky = v.Sky, Lens = v.Lens, Wind = v.Wind, Conf = v.Conf, Model = _cls.Model, Target = "admin", Json = v.Json };
        try { var s = await _status.GetAsync(cam.Lat, cam.Lon, new StatusOptions(), ct); obs.RadarDbz = s.Rain.Nearest is { } n && n.DistMi <= 1.5 ? n.Dbz : (s.Rain.Faint is { } f && f.DistMi <= 1.5 ? f.Dbz : 0); } catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "cam radar"); }
        try { obs.Frame = _store.SaveFrame(cam.Id, obs.TMs, frame.Bytes, frame.ContentType); } catch (Exception ex) { _log.LogDebug(ex, "save frame"); }
        _store.AddObs(obs);
        return new { cam.Id, cam.Name, cam.Url, bytes = frame.Bytes.Length, hash = frame.Hash, identicalToLast = frame.Hash == cam.LastHash, day, radarDbz = obs.RadarDbz, frame = obs.Frame, t = obs.TMs, verdict = JsonDocument.Parse(v.Json).RootElement.Clone() };
    }
}
