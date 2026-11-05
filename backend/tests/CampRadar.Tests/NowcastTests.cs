using CampRadar.Api.Nowcast;
using Xunit;

namespace CampRadar.Tests;

/// <summary>Synthetic-storm scenarios; mirrors the JavaScript test suite used for the page.</summary>
public class NowcastTests
{
    const int FINE = 186; const double KM = 2.07; const double CX = 93, CY = 93;

    record Blob(double X, double Y, double Rx, double Ry);

    static DbzGrid Frame(IEnumerable<Blob> blobs)
    {
        var g = new DbzGrid(FINE, FINE);
        foreach (var b in blobs)
            for (int y = 0; y < FINE; y++)
                for (int x = 0; x < FINE; x++)
                {
                    double e = (x - b.X) * KM / b.Rx, n = (y - b.Y) * KM / b.Ry, r = e * e + n * n;
                    if (r < 1) { float v = (float)(45 - 20 * r + 6 * Math.Sin(x * 0.7) * Math.Cos(y * 0.5)); if (v > g[x, y]) g[x, y] = v; }
                }
        return g;
    }

    static (Motion M, Prediction P) Run(Func<int, IEnumerable<Blob>> at)
    {
        var f = new[] { -30, -15, 0 }.Select(t => NowcastEngine.Despeckle(Frame(at(t)), 15)).ToArray();
        var co = f.Select(g => NowcastEngine.DownsampleMax(g, 2)).ToArray();
        var m = NowcastEngine.EstimateMotion(co[0], co[2], 30, KM * 2);
        if (!m.Ok) { var m2 = NowcastEngine.EstimateMotion(co[1], co[2], 15, KM * 2); if (m2.Ok) m = m2; }
        var vel = NowcastEngine.MakeVelocityField(m, 2);
        var p = NowcastEngine.PredictRain(f[2], KM, CX, CY, vel, 20, 3);
        return (m, p);
    }

    static Func<int, IEnumerable<Blob>> Moving(double x0km, double y0km, double vxKmh, double vyKmh, double rx = 20, double ry = 30)
        => t => new[] { new Blob(CX + (x0km + vxKmh * t / 60.0) / KM, CY + (y0km + vyKmh * t / 60.0) / KM, rx, ry) };

    [Fact]
    public void Colors_decode_to_expected_dbz()
    {
        Assert.Equal(20f, NowcastEngine.ColorToDbz(14, 214, 20));
        Assert.Equal(50f, NowcastEngine.ColorToDbz(255, 0, 0));
        Assert.Equal(-99f, NowcastEngine.ColorToDbz(0, 0, 0, 0));
        Assert.True(NowcastEngine.ColorToDbz(72, 110, 169) < 15);      // blue = very light
        Assert.Equal(-99f, NowcastEngine.ColorToDbz(0, 0, 255));       // not on the ramp
    }

    [Fact]
    public void Grid_max_pools_pixels()
    {
        int w = 8; var px = new byte[w * w * 4];
        for (int i = 0; i < 4; i++) { px[i * 4] = 255; px[i * 4 + 1] = 177; px[i * 4 + 3] = 255; }
        var g = NowcastEngine.GridFromRgba(px, w, w, 2);
        Assert.Equal(4, g.W); Assert.Equal(45f, g.D[0]); Assert.Equal(45f, g.D[1]); Assert.Equal(-99f, g.D[5]);
    }

    [Fact]
    public void Storm_from_west_hits()
    {
        var (m, p) = Run(Moving(-90, 0, 45, 0));
        Assert.True(m.Ok);
        Assert.InRange(m.SpeedMph, 23, 33);
        Assert.InRange(m.HeadingDeg, 75, 105);
        Assert.NotNull(p.Hit);
        Assert.InRange(p.Hit!.EtaMin, 70, 110);
        Assert.InRange(p.Hit.FromBearing, 250, 290);
    }

    [Fact]
    public void Storm_passing_north_does_not_hit()
    {
        var (_, p) = Run(Moving(-60, -60, 45, 0));
        Assert.Null(p.Hit);
        Assert.NotNull(p.Passing);
        Assert.True(p.Passing!.PassBearing < 30 || p.Passing.PassBearing > 330);
    }

    [Fact]
    public void Stationary_rain_nearby_does_not_hit()
    {
        var (m, p) = Run(Moving(-60, 0, 0, 0));
        Assert.True(m.Ok); Assert.True(m.SpeedMph < 3); Assert.Null(p.Hit);
        Assert.True(p.Ring30);
    }

    [Fact]
    public void Storm_from_southwest_hits()
    {
        var (_, p) = Run(t => new[] { new Blob(CX + (-60 + 42 * t / 60.0) / KM, CY + (60 - 42 * t / 60.0) / KM, 15, 15) });
        Assert.NotNull(p.Hit);
        Assert.True(p.Hit!.EtaMin < 90);
        Assert.InRange(p.Hit.FromBearing, 200, 250);
    }

    [Fact]
    public void Rain_overhead_now()
    {
        var (_, p) = Run(Moving(0, 0, 30, 0));
        Assert.NotNull(p.Hit); Assert.Equal(0, p.Hit!.EtaMin); Assert.True(p.Ring10);
    }

    [Fact]
    public void Clear_sky()
    {
        var (m, p) = Run(_ => Array.Empty<Blob>());
        Assert.False(m.Ok); Assert.Null(p.Hit); Assert.Null(p.Passing); Assert.False(p.Ring50);
    }
}
