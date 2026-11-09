using CampRadar.Api.Obs;
using Xunit;

namespace CampRadar.Tests;

/// <summary>The MADIS viewer's HTML-wrapped CSV (public tier, xml=5), with rows copied from a live query on 9 Nov 2026.</summary>
public class MadisTests
{
    const string Sample = """
<html><head><title>Results</title></head><body><pre>
 STAID     ,OBDATE    ,OBTIME,PVDR     ,SUBPVDR    ,LAT          ,D,LON          ,D,ELEV         ,D,T            ,D,TD           ,D,RH           ,D,DD           ,D,FF           ,D,FFGUST       ,D,ALTSE        ,D,SLP          ,D,PCP1H        ,D,PCPRATE      ,D,PRESWEA                 ,
KSSF      ,11/09/2026,14:53,ASOS     ,           ,    29.340000,Z,   -98.470001,Z,   174.000000,Z,   289.250000,V,   288.149994,V,-99999.000000,Z,     0.000000,V,     0.000000,V,-99999.000000,Z,102370.000000,V,-99999.000000,Z,     0.001800,V,     0.000500,V,-RA BR                  ,
CW1234    ,11/09/2026,14:50,APRSWXNET,           ,    32.800000,Z,   -96.800000,Z,   150.000000,Z,   287.150000,V,   286.150000,V,    94.000000,V,   180.000000,V,     2.000000,V,     4.000000,V,102400.000000,V,-99999.000000,Z,     0.000000,V,     0.000000,V,                        ,
CW1234    ,11/09/2026,14:40,APRSWXNET,           ,    32.800000,Z,   -96.800000,Z,   150.000000,Z,   286.150000,V,   285.150000,V,    94.000000,V,   180.000000,V,     2.000000,V,     4.000000,V,102400.000000,V,-99999.000000,Z,     0.000000,V,     0.000000,V,                        ,
BAD1      ,11/09/2026,14:45,HADS     ,           ,    32.500000,Z,   -97.000000,Z,-99999.000000,Z,   400.000000,B,-99999.000000,Z,-99999.000000,Z,-99999.000000,Z,-99999.000000,Z,-99999.000000,Z,-99999.000000,Z,-99999.000000,Z,     0.002540,V,-99999.000000,Z,                        ,
</pre></body></html>
""";

    [Fact]
    public void ParsesUnitsFlagsAndLatestPerStation()
    {
        var obs = MadisProvider.ParseCsv(Sample);
        Assert.Equal(3, obs.Count);                                      // CW1234 twice → one, latest
        var k = obs.Single(o => o.Id == "KSSF");
        Assert.Equal("ASOS", k.Provider);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 14, 53, 0, TimeSpan.Zero), k.At);
        Assert.Equal(61.0, k.TempF!.Value, 1);                           // 289.25 K
        Assert.Equal(0.071, k.Rain1hIn!.Value, 3);                       // 0.0018 m = 1.8 mm
        Assert.Equal(0.071, k.RateInHr!.Value, 3);                       // 0.0005 mm/s over an hour
        Assert.Equal(30.23, k.PressureInHg!.Value, 2);                   // 102370 Pa
        Assert.Equal("-RA BR", k.PresentWx);
        Assert.Null(k.Rh);
        var cw = obs.Single(o => o.Id == "CW1234");
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 14, 50, 0, TimeSpan.Zero), cw.At);
        Assert.Equal(4.47, cw.WindMph!.Value, 2);                        // 2 m/s
        Assert.Equal(0.0, cw.Rain1hIn!.Value, 3);
        var bad = obs.Single(o => o.Id == "BAD1");
        Assert.Null(bad.TempF);                                          // QC flag B drops the value
        Assert.Equal(0.1, bad.Rain1hIn!.Value, 3);                       // 0.00254 m = 0.1 in
        Assert.Null(bad.ElevM);
    }

    [Fact]
    public void NoHeaderMeansNoRows() => Assert.Empty(MadisProvider.ParseCsv("<html><body>Results</body></html>"));

    [Fact]
    public void StationViewConvertsDistanceAndNetwork()
    {
        var k = MadisProvider.ParseCsv(Sample).Single(o => o.Id == "KSSF");
        var st = MadisProvider.ToStation(k, 29.4241, -98.4936);
        Assert.True(st.Official);
        Assert.Equal("ASOS", st.Network);
        Assert.InRange(st.DistMi, 5.5, 6.5);
        Assert.Equal(571, st.ElevFt, 0);
    }
}
