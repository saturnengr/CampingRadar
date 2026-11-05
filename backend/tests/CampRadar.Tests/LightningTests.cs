using CampRadar.Api.Lightning;
using PureHDF;
using Xunit;

namespace CampRadar.Tests;

public class LightningTests
{
    [Fact]
    public void Parses_start_time_from_key()
    {
        var t = GlmIngestor.ParseStartTime("GLM-L2-LCFA/2026/309/05/OR_GLM-L2-LCFA_G19_s20263090512000_e20263090512200_c20263090512217.nc");
        Assert.NotNull(t);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 5, 12, 0, TimeSpan.Zero), t!.Value);
    }

    [Fact]
    public void Summarizes_strikes_around_a_point()
    {
        var store = new LightningStore();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        double lat = 32.78, lon = -96.80;
        // 3 strikes ~8 mi north in the last 5 min, 2 strikes ~25 mi west 15 min ago, one far away
        var flashes = new List<Flash>
        {
            new(now - 2 * 60000, (float)(lat + 0.115), (float)lon, 1f),
            new(now - 3 * 60000, (float)(lat + 0.118), (float)(lon + 0.01), 1f),
            new(now - 4 * 60000, (float)(lat + 0.112), (float)(lon - 0.01), 1f),
            new(now - 15 * 60000, (float)lat, (float)(lon - 0.43), 1f),
            new(now - 16 * 60000, (float)(lat + 0.01), (float)(lon - 0.44), 1f),
            new(now - 1 * 60000, 40f, -100f, 1f),
        };
        store.Add(flashes, now);
        var s = store.Summarize(lat, lon, now);
        Assert.Equal(3, s.Within10);
        Assert.Equal(3, s.Within20);
        Assert.Equal(5, s.Within30);
        Assert.InRange(s.NearestMi!.Value, 7, 9);
        Assert.InRange(s.NearestBearing!.Value, 340, 360 + 20);  // north-ish (wraps)
        Assert.Equal("approaching", s.Trend);
        Assert.True(s.DataFresh);
        Assert.InRange(s.LastStrikeAgeMin!.Value, 1.9, 2.1);
    }

    [Fact]
    public void Old_flashes_are_pruned()
    {
        var store = new LightningStore();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        store.Add(new[] { new Flash(now - 90 * 60000, 33f, -97f, 1f), new Flash(now - 5 * 60000, 33f, -97f, 1f) }, now);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Parses_a_glm_style_hdf5_file()
    {
        // Build a minimal file shaped like NOAA's: lat/lon float32, time offsets int16 with scale/offset and a "seconds since" epoch.
        var times = new H5Dataset(new short[] { 0, 13107, -13107 });   // scale 0.00038 → 0 s, +5 s, −5 s  (plus offset −5 → −5, 0, −10)
        times.Attributes["scale_factor"] = new[] { 0.0003814756f };
        times.Attributes["add_offset"] = new[] { -5f };
        times.Attributes["units"] = "seconds since 2026-11-05T05:12:00.0Z";
        var file = new H5File
        {
            ["flash_lat"] = new[] { 32.8f, 45.0f, 10.0f },
            ["flash_lon"] = new[] { -96.8f, -100.0f, -80.0f },
            ["flash_time_offset_of_first_event"] = times,
            ["flash_quality_flag"] = new short[] { 0, 0, 0 },
        };
        file.Attributes["time_coverage_start"] = "2026-11-05T05:12:00.0Z";
        using var ms = new MemoryStream();
        file.Write(ms);
        var flashes = GlmIngestor.ParseFile(ms.ToArray(), (18, 55, -130, -60));
        Assert.Equal(2, flashes.Count);                    // the 10°N flash is outside the box
        var epoch = new DateTimeOffset(2026, 11, 5, 5, 12, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        Assert.Equal(epoch - 5000, flashes[0].TimeMs);
        Assert.Equal(epoch, flashes[1].TimeMs);
        Assert.Equal(32.8f, flashes[0].Lat, 3);
    }
}
