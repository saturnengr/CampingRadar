using System.Globalization;
using System.Xml.Linq;
using PureHDF;

namespace CampRadar.Api.Lightning;

/// <summary>
/// Pulls NOAA GOES Geostationary Lightning Mapper (GLM) Level-2 flash files from the
/// public AWS Open Data buckets (no credentials) and feeds flashes into the store.
/// Files arrive every 20 seconds; each is a small NetCDF-4 (HDF5) file.
/// </summary>
public sealed class GlmIngestor
{
    private readonly HttpClient _http;
    private readonly LightningStore _store;
    private readonly ILogger<GlmIngestor> _log;
    private readonly string[] _buckets;
    private readonly (double MinLat, double MaxLat, double MinLon, double MaxLon) _box;
    private bool _backfilled;
    public bool Enabled { get; }

    public GlmIngestor(HttpClient http, LightningStore store, IConfiguration cfg, ILogger<GlmIngestor> log)
    {
        _http = http; _store = store; _log = log;
        // GOES-19 is GOES-East (covers all of CONUS). Add "noaa-goes18" (GOES-West) for better far-west coverage.
        Enabled = cfg.GetValue("Glm:Enabled", true); store.Paused = !Enabled;
        _buckets = (cfg["Glm:Buckets"] ?? "noaa-goes19").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _box = (cfg.GetValue("Glm:MinLat", 18.0), cfg.GetValue("Glm:MaxLat", 55.0), cfg.GetValue("Glm:MinLon", -130.0), cfg.GetValue("Glm:MaxLon", -60.0));
    }

    /// <summary>Ingest anything new. First call also backfills the last 30 minutes.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (!Enabled) return;   // Glm:Enabled=false pauses the feed (it is the bulk of the server's inbound data)
        var now = DateTimeOffset.UtcNow;
        var hours = new List<DateTimeOffset> { now };
        if (!_backfilled || now.Minute < 3) hours.Add(now.AddHours(-1));
        long minFileMs = _backfilled ? 0 : now.AddMinutes(-30).ToUnixTimeMilliseconds();
        int added = 0;
        foreach (var bucket in _buckets)
            foreach (var h in hours.Distinct())
            {
                List<string> keys;
                try { keys = await ListHourAsync(bucket, h, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "GLM list failed for {Bucket} {Hour:yyyy-MM-dd HH}", bucket, h); continue; }
                foreach (var key in keys)
                {
                    var start = ParseStartTime(key);
                    if (start is null || start.Value.ToUnixTimeMilliseconds() < minFileMs) continue;
                    if (!_store.MarkFileSeen(bucket + "/" + key)) continue;
                    try
                    {
                        var bytes = await _http.GetByteArrayAsync($"https://{bucket}.s3.amazonaws.com/{key}", ct);
                        var flashes = ParseFile(bytes, _box);
                        _store.Add(flashes, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                        added += flashes.Count;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "GLM file failed: {Key}", key); }
                }
            }
        if (!_backfilled) { _backfilled = true; _log.LogInformation("GLM backfill done: {N} flashes in store", _store.Count); }
        else if (added > 0) _log.LogDebug("GLM: +{N} flashes", added);
        _store.Add(Array.Empty<Flash>(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());   // marks ingest time even on quiet hours
    }

    private async Task<List<string>> ListHourAsync(string bucket, DateTimeOffset h, CancellationToken ct)
    {
        var prefix = $"GLM-L2-LCFA/{h:yyyy}/{h.DayOfYear:000}/{h:HH}/";
        var keys = new List<string>();
        string? token = null;
        do
        {
            var url = $"https://{bucket}.s3.amazonaws.com/?list-type=2&prefix={Uri.EscapeDataString(prefix)}" + (token is null ? "" : "&continuation-token=" + Uri.EscapeDataString(token));
            var xml = XDocument.Parse(await _http.GetStringAsync(url, ct));
            XNamespace ns = xml.Root!.Name.Namespace;
            keys.AddRange(xml.Root.Elements(ns + "Contents").Select(c => (string)c.Element(ns + "Key")!).Where(k => k.EndsWith(".nc")));
            token = (string?)xml.Root.Element(ns + "NextContinuationToken");
        } while (token is not null);
        keys.Sort();
        return keys;
    }

    /// <summary>OR_GLM-L2-LCFA_G19_s20263090512000_e..._c....nc → the s-timestamp (yyyyDDDHHmmss, tenths).</summary>
    public static DateTimeOffset? ParseStartTime(string key)
    {
        int i = key.IndexOf("_s", StringComparison.Ordinal);
        if (i < 0 || key.Length < i + 16) return null;
        var s = key.Substring(i + 2, 14);
        if (!int.TryParse(s[..4], out var year) || !int.TryParse(s[4..7], out var doy) || !int.TryParse(s[7..9], out var hh) || !int.TryParse(s[9..11], out var mm) || !int.TryParse(s[11..13], out var ss)) return null;
        return new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(doy - 1).AddHours(hh).AddMinutes(mm).AddSeconds(ss);
    }

    /// <summary>Parse one GLM L2 LCFA file to flashes inside the box.</summary>
    public static List<Flash> ParseFile(byte[] bytes, (double MinLat, double MaxLat, double MinLon, double MaxLon) box)
    {
        using var ms = new MemoryStream(bytes);
        using var file = H5File.Open(ms);
        var result = new List<Flash>();
        if (!TryDataset(file, "flash_lat", out var dLat) || !TryDataset(file, "flash_lon", out var dLon)) return result;
        var lat = ReadFloats(dLat!); var lon = ReadFloats(dLon!);
        int n = Math.Min(lat.Length, lon.Length);
        if (n == 0) return result;

        // time = time_coverage_start + offset (int16 scaled) ; fall back to file start if anything is missing
        var start = ReadStringAttr(file, "time_coverage_start") is { } tcs && DateTimeOffset.TryParse(tcs, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts) ? ts : (DateTimeOffset?)null;
        double[]? offsets = null;
        if (TryDataset(file, "flash_time_offset_of_first_event", out var dT))
        {
            var units = ReadStringAttr(dT!, "units");   // "seconds since 2026-11-05T05:12:00.0Z"
            if (units is not null && units.StartsWith("seconds since ", StringComparison.OrdinalIgnoreCase) &&
                DateTimeOffset.TryParse(units[14..].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var epoch)) start = epoch;
            offsets = ReadScaled(dT!);
        }
        long startMs = (start ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();

        float[]? energy = TryDataset(file, "flash_energy", out var dE) ? ReadScaled(dE!).Select(v => (float)v).ToArray() : null;
        short[]? quality = TryDataset(file, "flash_quality_flag", out var dQ) ? ReadShorts(dQ!) : null;

        for (int i = 0; i < n; i++)
        {
            if (quality is not null && i < quality.Length && quality[i] != 0) continue;
            float la = lat[i], lo = lon[i];
            if (la < box.MinLat || la > box.MaxLat || lo < box.MinLon || lo > box.MaxLon) continue;
            long t = startMs + (long)Math.Round((offsets is not null && i < offsets.Length ? offsets[i] : 0) * 1000);
            result.Add(new Flash(t, la, lo, energy is not null && i < energy.Length ? energy[i] : 0f));
        }
        return result;
    }

    // ---- HDF5 helpers (tolerant of the few type variants NOAA uses) ----
    private static bool TryDataset(IH5Group file, string name, out IH5Dataset? ds)
    {
        try { ds = file.Dataset(name); return true; } catch { ds = null; return false; }
    }
    private static float[] ReadFloats(IH5Dataset ds)
    {
        try { return ds.Read<float[]>(); } catch { }
        try { return ds.Read<double[]>().Select(v => (float)v).ToArray(); } catch { }
        return Array.Empty<float>();
    }
    private static short[] ReadShorts(IH5Dataset ds)
    {
        try { return ds.Read<short[]>(); } catch { }
        try { return ds.Read<int[]>().Select(v => (short)v).ToArray(); } catch { }
        try { return ds.Read<byte[]>().Select(v => (short)v).ToArray(); } catch { }
        return Array.Empty<short>();
    }
    /// <summary>Raw values × scale_factor + add_offset, whatever the stored integer type.</summary>
    private static double[] ReadScaled(IH5Dataset ds)
    {
        double scale = ReadNumAttr(ds, "scale_factor") ?? 1, offset = ReadNumAttr(ds, "add_offset") ?? 0;
        double[] raw;
        try { raw = ds.Read<short[]>().Select(v => (double)v).ToArray(); }
        catch
        {
            try { raw = ds.Read<ushort[]>().Select(v => (double)v).ToArray(); }
            catch { try { raw = ds.Read<float[]>().Select(v => (double)v).ToArray(); } catch { raw = ds.Read<int[]>().Select(v => (double)v).ToArray(); } }
        }
        for (int i = 0; i < raw.Length; i++) raw[i] = raw[i] * scale + offset;
        return raw;
    }
    private static double? ReadNumAttr(IH5Dataset ds, string name)
    {
        IH5Attribute a;
        try { a = ds.Attribute(name); } catch { return null; }
        try { return a.Read<float[]>()[0]; } catch { }
        try { return a.Read<double[]>()[0]; } catch { }
        try { return a.Read<float>(); } catch { }
        try { return a.Read<double>(); } catch { }
        return null;
    }
    private static string? ReadStringAttr(IH5Object obj, string name)
    {
        IH5Attribute a;
        try { a = obj.Attribute(name); } catch { return null; }
        try { return a.Read<string>(); } catch { }
        try { return a.Read<string[]>().FirstOrDefault(); } catch { }
        return null;
    }
}
