using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using CampRadar.Api.Alerts;

namespace CampRadar.Api.Radar;

/// <summary>
/// Records radar around a point for a few hours: every new MRMS scan is saved as the NOAA PNG (exact pixels the trackers see)
/// together with the engine's own status for that scan, so the motion and arrival maths can be replayed and checked offline.
/// Files land in App_Data/recordings/{name}/ and can be downloaded as one zip from the admin page.
/// </summary>
public sealed class Recorder
{
    public sealed class Job
    {
        public string Name { get; init; } = ""; public double Lat { get; init; } public double Lon { get; init; } public double RadiusMi { get; init; } public int Zoom { get; init; }
        public DateTimeOffset StartedAt { get; init; } public DateTimeOffset EndsAt { get; init; }
        public int Frames { get; set; } public long LastT { get; set; } public string? LastError { get; set; } public bool Running { get; set; } public long Bytes { get; set; }
    }
    private readonly MrmsClient _mrms; private readonly StatusService _status; private readonly ILogger<Recorder> _log; private readonly string _dir;
    private readonly ConcurrentDictionary<string, Job> _jobs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cts = new();

    public Recorder(MrmsClient mrms, StatusService status, IConfiguration cfg, ILogger<Recorder> log)
    { _mrms = mrms; _status = status; _log = log; _dir = Path.Combine(cfg["DataDir"] ?? "App_Data", "recordings"); Directory.CreateDirectory(_dir); }

    public IEnumerable<object> List()
    {
        var onDisk = Directory.EnumerateDirectories(_dir).Select(Path.GetFileName).Where(n => n is not null).Select(n => n!).ToHashSet();
        foreach (var n in onDisk.Union(_jobs.Keys).OrderByDescending(n => n))
        {
            _jobs.TryGetValue(n, out var j); var d = Path.Combine(_dir, n);
            int frames = Directory.Exists(d) ? Directory.EnumerateFiles(d, "*.png").Count() : 0;
            long bytes = Directory.Exists(d) ? new DirectoryInfo(d).EnumerateFiles().Sum(f => f.Length) : 0;
            yield return new { name = n, running = j?.Running ?? false, frames, mb = Math.Round(bytes / 1048576.0, 1), lat = j?.Lat, lon = j?.Lon, radiusMi = j?.RadiusMi, endsAt = j?.EndsAt, lastError = j?.LastError, lastFrame = j?.LastT > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(j.LastT) : (DateTimeOffset?)null };
        }
    }

    public static string SafeName(string name) { var n = new string((name ?? "").Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray()); return n.Length == 0 ? "rec-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmm") : n[..Math.Min(40, n.Length)]; }
    public Job Start(string name, double lat, double lon, double hours, double radiusMi = 200, int zoom = 8)
    {
        name = SafeName(name);
        if (_jobs.TryGetValue(name, out var existing) && existing.Running) return existing;
        var now = DateTimeOffset.UtcNow;
        var job = new Job { Name = name, Lat = lat, Lon = lon, RadiusMi = radiusMi, Zoom = zoom, StartedAt = now, EndsAt = now.AddHours(Math.Clamp(hours, 0.1, 48)), Running = true };
        var dir = Path.Combine(_dir, name); Directory.CreateDirectory(dir);
        double mpp = 156543.03392 * Math.Cos(lat * Math.PI / 180) / Math.Pow(2, zoom);
        File.WriteAllText(Path.Combine(dir, "meta.json"), JsonSerializer.Serialize(new { name, lat, lon, radiusMi, zoom, metersPerPx = mpp, sizePx = 2 * (int)Math.Round(radiusMi * 1609.344 / mpp), startedAt = job.StartedAt, endsAt = job.EndsAt, note = "frames are NOAA MRMS exportImage PNGs (Web Mercator, centred on lat/lon, radiusMi each side); {t}.status.json is the server engine's output for that scan with default options" }, new JsonSerializerOptions { WriteIndented = true }));
        Prune();
        Launch(job, dir);
        return job;
    }

    private void Launch(Job job, string dir)
    {
        _jobs[job.Name] = job;
        var cts = new CancellationTokenSource(); _cts[job.Name] = cts;
        _ = Task.Run(() => RunAsync(job, dir, cts.Token));
    }

    /// <summary>
    /// After a restart (the host recycles the app pool without warning), pick up every recording whose window has not closed: same
    /// folder, same meta.json, frames from the last half hour onward. Called once at startup; returns the names resumed.
    /// </summary>
    public List<string> ResumeInterrupted()
    {
        var resumed = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(_dir))
        {
            try
            {
                var mf = Path.Combine(dir, "meta.json"); if (!File.Exists(mf)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(mf)); var r = doc.RootElement;
                var endsAt = r.GetProperty("endsAt").GetDateTimeOffset();
                if (endsAt <= DateTimeOffset.UtcNow.AddMinutes(5)) continue;
                var name = Path.GetFileName(dir); if (_jobs.TryGetValue(name, out var j) && j.Running) continue;
                var job = new Job { Name = name, Lat = r.GetProperty("lat").GetDouble(), Lon = r.GetProperty("lon").GetDouble(), RadiusMi = r.GetProperty("radiusMi").GetDouble(), Zoom = r.TryGetProperty("zoom", out var z) ? z.GetInt32() : 8,
                    StartedAt = DateTimeOffset.UtcNow.AddMinutes(-30), EndsAt = endsAt, Running = true };
                Launch(job, dir); resumed.Add(name);
            }
            catch (Exception ex) { _log.LogDebug(ex, "resume {Dir}", dir); }
        }
        if (resumed.Count > 0) _log.LogInformation("resumed recordings after restart: {Names}", string.Join(", ", resumed));
        return resumed;
    }

    /// <summary>Running jobs with their geometry (for the verify worker to attach ground truth to each one).</summary>
    public IEnumerable<Job> RunningJobs() => _jobs.Values.Where(j => j.Running).ToList();
    /// <summary>A recording's geometry and window from its meta.json — works for finished recordings and across restarts. Frame
    /// timestamps bound the window when they exist (a restarted recording of the same name appends to the folder).</summary>
    public (double Lat, double Lon, double RadiusMi, DateTimeOffset From, DateTimeOffset To)? Meta(string name)
    {
        try
        {
            var dir = Path.Combine(_dir, SafeName(name)); var mf = Path.Combine(dir, "meta.json"); if (!File.Exists(mf)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(mf)); var r = doc.RootElement;
            var from = r.GetProperty("startedAt").GetDateTimeOffset(); var to = r.GetProperty("endsAt").GetDateTimeOffset();
            var ts = Directory.EnumerateFiles(dir, "*.png").Select(f => long.TryParse(Path.GetFileNameWithoutExtension(f), out var t) ? t : 0).Where(t => t > 0).ToList();
            if (ts.Count > 0) { from = DateTimeOffset.FromUnixTimeMilliseconds(ts.Min()); var last = DateTimeOffset.FromUnixTimeMilliseconds(ts.Max()); if (last < to) to = last; }
            return (r.GetProperty("lat").GetDouble(), r.GetProperty("lon").GetDouble(), r.GetProperty("radiusMi").GetDouble(), from, to);
        }
        catch (Exception ex) { _log.LogDebug(ex, "meta {Name}", name); return null; }
    }
    public bool IsRunning(string prefix) => _jobs.Values.Any(j => j.Running && j.Name.StartsWith(prefix, StringComparison.Ordinal));
    public int RunningCount() => _jobs.Values.Count(j => j.Running);
    public IEnumerable<string> RunningNames() => _jobs.Values.Where(j => j.Running).Select(j => j.Name).ToList();
    /// <summary>Append a line to a side file inside a recording (ground-truth observations, notes).</summary>
    public void AppendAux(string name, string file, string line)
    {
        try { var d = Path.Combine(_dir, SafeName(name)); if (Directory.Exists(d)) File.AppendAllText(Path.Combine(d, SafeName(Path.GetFileNameWithoutExtension(file)) + Path.GetExtension(file)), line + "\n"); }
        catch (Exception ex) { _log.LogDebug(ex, "aux {Name}", name); }
    }
    public void Stop(string name) { name = SafeName(name); if (_cts.TryRemove(name, out var c)) c.Cancel(); if (_jobs.TryGetValue(name, out var j)) j.Running = false; }

    private async Task RunAsync(Job job, string dir, CancellationToken ct)
    {
        var seen = new HashSet<long>(Directory.EnumerateFiles(dir, "*.png").Select(f => long.TryParse(Path.GetFileNameWithoutExtension(f), out var t) ? t : 0));
        var failures = new Dictionary<long, int>();
        try
        {
            while (!ct.IsCancellationRequested && DateTimeOffset.UtcNow < job.EndsAt)
            {
                try
                {
                    await _mrms.RefreshTimesAsync(ct);
                    foreach (var t in _mrms.Times.Where(t => !seen.Contains(t) && t >= job.StartedAt.AddMinutes(-35).ToUnixTimeMilliseconds()).OrderBy(t => t))
                    {
                        byte[] png;
                        try { png = await _mrms.ExportPngAsync(t, job.Lat, job.Lon, job.Zoom, job.RadiusMi, ct); }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {   // a scan NOAA will not render (too old, or a hiccup) is retried twice, then skipped so newer scans still land
                            failures[t] = failures.GetValueOrDefault(t) + 1; job.LastError = "frame " + t + ": " + ex.Message;
                            if (failures[t] >= 3) seen.Add(t);
                            continue;
                        }
                        await File.WriteAllBytesAsync(Path.Combine(dir, t + ".png"), png, ct);
                        seen.Add(t); job.Frames = seen.Count; job.LastT = t; job.Bytes += png.Length;
                        if (t == _mrms.Latest)
                        {
                            try { var s = await _status.GetAsync(job.Lat, job.Lon, new StatusOptions(), ct); await File.WriteAllTextAsync(Path.Combine(dir, t + ".status.json"), JsonSerializer.Serialize(s), ct); }
                            catch (Exception ex) when (ex is not OperationCanceledException) { job.LastError = "status: " + ex.Message; }
                        }
                    }
                    job.LastError = null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { job.LastError = ex.Message; _log.LogWarning(ex, "recording {Name}", job.Name); }
                await Task.Delay(TimeSpan.FromSeconds(90), ct);
            }
        }
        catch (OperationCanceledException) { }
        finally { job.Running = false; _cts.TryRemove(job.Name, out _); }
    }

    public string? ZipPath(string name)
    {
        name = SafeName(name);
        var dir = Path.GetFullPath(Path.Combine(_dir, name)); if (!dir.StartsWith(Path.GetFullPath(_dir) + Path.DirectorySeparatorChar) || !Directory.Exists(dir)) return null;
        string zip = Path.Combine(_dir, name + ".zip"), tmp = zip + "." + Environment.TickCount64 + ".tmp";
        ZipFile.CreateFromDirectory(dir, tmp, CompressionLevel.Fastest, includeBaseDirectory: true);
        File.Move(tmp, zip, overwrite: true);   // a download in flight keeps its open handle on the old file
        return zip;
    }
    /// <summary>Housekeeping: at most the newest 40 finished recordings (running ones are never pruned; the host has gigabytes to spare), and no zips older than a day.</summary>
    public void Prune()
    {
        try
        {
            foreach (var z in Directory.EnumerateFiles(_dir, "*.zip").Where(f => DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromDays(1))) File.Delete(z);
            var dirs = Directory.EnumerateDirectories(_dir).OrderByDescending(Directory.GetCreationTimeUtc).Skip(40).ToList();
            foreach (var d in dirs) if (!(_jobs.TryGetValue(Path.GetFileName(d), out var j) && j.Running)) Directory.Delete(d, true);
        }
        catch (Exception ex) { _log.LogDebug(ex, "recording prune"); }
    }
}
