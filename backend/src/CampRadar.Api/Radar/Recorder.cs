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

    public Job Start(string name, double lat, double lon, double hours, double radiusMi = 200, int zoom = 8)
    {
        name = new string(name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray()); if (name.Length == 0) name = "rec-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmm");
        if (_jobs.TryGetValue(name, out var existing) && existing.Running) return existing;
        var job = new Job { Name = name, Lat = lat, Lon = lon, RadiusMi = radiusMi, Zoom = zoom, StartedAt = DateTimeOffset.UtcNow, EndsAt = DateTimeOffset.UtcNow.AddHours(Math.Clamp(hours, 0.1, 48)), Running = true };
        _jobs[name] = job;
        var cts = new CancellationTokenSource(); _cts[name] = cts;
        var dir = Path.Combine(_dir, name); Directory.CreateDirectory(dir);
        double mpp = 156543.03392 * Math.Cos(lat * Math.PI / 180) / Math.Pow(2, zoom);
        File.WriteAllText(Path.Combine(dir, "meta.json"), JsonSerializer.Serialize(new { name, lat, lon, radiusMi, zoom, metersPerPx = mpp, sizePx = 2 * (int)Math.Round(radiusMi * 1609.344 / mpp), startedAt = job.StartedAt, endsAt = job.EndsAt, note = "frames are NOAA MRMS exportImage PNGs (Web Mercator, centred on lat/lon, radiusMi each side); {t}.status.json is the server engine's output for that scan with default options" }, new JsonSerializerOptions { WriteIndented = true }));
        _ = Task.Run(() => RunAsync(job, dir, cts.Token));
        return job;
    }

    public void Stop(string name) { if (_cts.TryRemove(name, out var c)) c.Cancel(); if (_jobs.TryGetValue(name, out var j)) j.Running = false; }

    private async Task RunAsync(Job job, string dir, CancellationToken ct)
    {
        var seen = new HashSet<long>(Directory.EnumerateFiles(dir, "*.png").Select(f => long.TryParse(Path.GetFileNameWithoutExtension(f), out var t) ? t : 0));
        try
        {
            while (!ct.IsCancellationRequested && DateTimeOffset.UtcNow < job.EndsAt)
            {
                try
                {
                    await _mrms.RefreshTimesAsync(ct);
                    foreach (var t in _mrms.Times.Where(t => !seen.Contains(t) && t >= job.StartedAt.AddMinutes(-35).ToUnixTimeMilliseconds()).OrderBy(t => t))
                    {
                        var png = await _mrms.ExportPngAsync(t, job.Lat, job.Lon, job.Zoom, job.RadiusMi, ct);
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
        var dir = Path.Combine(_dir, name); if (!Directory.Exists(dir)) return null;
        var zip = Path.Combine(_dir, name + ".zip");
        if (File.Exists(zip)) File.Delete(zip);
        ZipFile.CreateFromDirectory(dir, zip, CompressionLevel.Fastest, includeBaseDirectory: true);
        return zip;
    }
}
