using System.Collections.Concurrent;
using System.Text.Json;
using CampRadar.Api.Nowcast;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace CampRadar.Api.Radar;

/// <summary>
/// NOAA MRMS quality-controlled base reflectivity, via the NWS ArcGIS image service.
/// Fetches scan times, renders Web-Mercator tiles through exportImage, and caches them on disk
/// so every device shares one copy per tile per scan.
/// </summary>
public sealed class MrmsClient
{
    public const string Service = "https://mapservices.weather.noaa.gov/eventdriven/rest/services/radar/radar_base_reflectivity_time/ImageServer";
    public const double Merc = 20037508.342789244;

    private readonly HttpClient _http;
    private readonly string _cacheDir;
    private readonly ILogger<MrmsClient> _log;
    private readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> _inflight = new();
    private readonly SemaphoreSlim _noaaGate = new(6);   // be polite to NOAA

    public IReadOnlyList<long> Times { get; private set; } = Array.Empty<long>();
    public long Latest => Times.Count > 0 ? Times[^1] : 0;
    public DateTimeOffset TimesRefreshedAt { get; private set; }

    public MrmsClient(HttpClient http, IConfiguration cfg, ILogger<MrmsClient> log)
    {
        _http = http; _log = log;
        _cacheDir = Path.Combine(cfg["DataDir"] ?? "App_Data", "radar");
        Directory.CreateDirectory(_cacheDir);
    }

    /// <summary>Scan valid times (epoch ms, ascending) for the service's moving window.</summary>
    public async Task RefreshTimesAsync(CancellationToken ct)
    {
        try
        {
            var url = Service + "/query?where=1%3D1&outFields=idp_validtime&returnGeometry=false&f=json";
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(url, ct));
            var set = new SortedSet<long>();
            if (doc.RootElement.TryGetProperty("features", out var feats))
                foreach (var f in feats.EnumerateArray())
                    if (f.TryGetProperty("attributes", out var a) && a.TryGetProperty("idp_validtime", out var t) && t.ValueKind == JsonValueKind.Number)
                        set.Add(t.GetInt64());
            if (set.Count > 0) { Times = set.ToList(); TimesRefreshedAt = DateTimeOffset.UtcNow; }
            else _log.LogWarning("MRMS time query returned no features");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "MRMS time query failed");
        }
    }

    public long NearestTime(long target)
    {
        if (Times.Count == 0) return 0;
        long best = Times[0];
        foreach (var t in Times) if (Math.Abs(t - target) < Math.Abs(best - target)) best = t;
        return best;
    }

    /// <summary>Frames for the animation loop: the scan nearest each offset (minutes before latest).</summary>
    public IReadOnlyList<long> LoopFrames(int[] offsetsMin)
    {
        if (Times.Count == 0) return Array.Empty<long>();
        var latest = Latest;
        return offsetsMin.Select(o => NearestTime(latest - o * 60000L)).ToList();
    }

    public static string ExportUrl(double minx, double miny, double maxx, double maxy, int w, int h, long? t)
    {
        static string F(double v) => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        return $"{Service}/exportImage?bbox={F(minx)},{F(miny)},{F(maxx)},{F(maxy)}&bboxSR=3857&imageSR=3857&size={w},{h}" +
               "&format=png32&transparent=true&interpolation=RSP_NearestNeighbor" + (t is null ? "" : $"&time={t}") + "&f=image";
    }

    /// <summary>A 512-px tile at (z, x, y) in the standard Web-Mercator grid, as PNG bytes. Cached per scan time.</summary>
    public async Task<byte[]> TileAsync(long t, int z, int x, int y, CancellationToken ct)
    {
        bool live = t == Latest;      // newest scan has no end time yet; NOAA only returns it when no time is given
        var key = $"{t}/{z}/{x}/{y}";
        var path = Path.Combine(_cacheDir, t.ToString(), z.ToString(), $"{x}_{y}.png");
        if (File.Exists(path)) return await File.ReadAllBytesAsync(path, ct);
        var lazy = _inflight.GetOrAdd(key, _ => new Lazy<Task<byte[]>>(async () =>
        {
            double size = 2 * Merc / (256.0 * Math.Pow(2, z) / 512.0);
            double minx = -Merc + x * size, maxy = Merc - y * size;
            var url = ExportUrl(minx, maxy - size, minx + size, maxy, 512, 512, live ? null : t);
            var bytes = await FetchAsync(url, ct);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes, ct);
            return bytes;
        }));
        try { return await lazy.Value; }
        finally { _inflight.TryRemove(key, out _); }
    }

    /// <summary>Square radar image centered on a point: RGBA pixels, for the tracking engine.</summary>
    public async Task<(byte[] Rgba, int Size, double MetersPerPx)> AnalysisFrameAsync(long t, double lat, double lon, int zoom, double radiusMi, CancellationToken ct)
    {
        bool live = t == Latest;
        var (cx, cy) = LonLatToPx(lon, lat, zoom);
        double mpp = 156543.03392 * Math.Cos(lat * Math.PI / 180) / Math.Pow(2, zoom);
        int R = (int)Math.Round(radiusMi * 1609.344 / mpp), size = 2 * R;
        double res = 2 * Merc / (256 * Math.Pow(2, zoom));
        double ox = Math.Round(cx) - R, oy = Math.Round(cy) - R;
        var url = ExportUrl(ox * res - Merc, Merc - (oy + size) * res, (ox + size) * res - Merc, Merc - oy * res, size, size, live ? null : t);
        var bytes = await FetchAsync(url, ct);
        using var img = Image.Load<Rgba32>(bytes);
        var rgba = new byte[img.Width * img.Height * 4];
        img.CopyPixelDataTo(rgba);
        return (rgba, img.Width, mpp);
    }

    public static (double X, double Y) LonLatToPx(double lon, double lat, int z)
    {
        double n = 256 * Math.Pow(2, z), s = Math.Sin(lat * Math.PI / 180);
        return ((lon + 180) / 360 * n, (0.5 - Math.Log((1 + s) / (1 - s)) / (4 * Math.PI)) * n);
    }

    private async Task<byte[]> FetchAsync(string url, CancellationToken ct)
    {
        await _noaaGate.WaitAsync(ct);
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length < 8 || bytes[1] != (byte)'P' || bytes[2] != (byte)'N' || bytes[3] != (byte)'G')
                throw new InvalidOperationException("NOAA returned a non-PNG response");
            return bytes;
        }
        finally { _noaaGate.Release(); }
    }

    /// <summary>Delete cached tiles for scans older than the service window.</summary>
    public void PruneCache(TimeSpan keep)
    {
        try
        {
            long cutoff = DateTimeOffset.UtcNow.Subtract(keep).ToUnixTimeMilliseconds();
            foreach (var dir in Directory.EnumerateDirectories(_cacheDir))
                if (long.TryParse(Path.GetFileName(dir), out var t) && t < cutoff)
                    Directory.Delete(dir, true);
        }
        catch (Exception ex) { _log.LogWarning(ex, "radar cache prune failed"); }
    }
}
