namespace CampRadar.Api.Verify;

/// <summary>
/// Fixed points the verification worker watches: ASOS airports chosen to span the regimes the tracker has to cope with
/// (plains squall lines, Gulf-coast pop-ups, Southwest virga, Appalachian terrain, Northeast stratiform, Rockies, Pacific
/// Northwest). Each has an NWS station id so its own rain gauge can serve as ground truth. Override with Verify:Points in
/// appsettings ("KABI,32.4113,-99.6819;KSAT,29.5337,-98.4698").
/// </summary>
public sealed record SamplePoint(string Id, string Name, double Lat, double Lon);

public static class SamplePoints
{
    public static readonly SamplePoint[] Default =
    {
        new("KABI", "Abilene TX", 32.4113, -99.6819),
        new("KDAL", "Dallas Love TX", 32.8471, -96.8518),
        new("KSAT", "San Antonio TX", 29.5337, -98.4698),
        new("KHOU", "Houston Hobby TX", 29.6375, -95.2825),
        new("KCRP", "Corpus Christi TX", 27.7742, -97.5122),
        new("KMAF", "Midland TX", 31.9425, -102.2019),
        new("KAMA", "Amarillo TX", 35.2194, -101.7059),
        new("KOKC", "Oklahoma City OK", 35.3931, -97.6007),
        new("KLIT", "Little Rock AR", 34.7294, -92.2243),
        new("KMSY", "New Orleans LA", 29.9934, -90.2580),
        new("KBNA", "Nashville TN", 36.1245, -86.6782),
        new("KATL", "Atlanta GA", 33.6367, -84.4281),
        new("KCLT", "Charlotte NC", 35.2140, -80.9431),
        new("KTPA", "Tampa FL", 27.9755, -82.5332),
        new("KABQ", "Albuquerque NM", 35.0419, -106.6156),
        new("KPHX", "Phoenix AZ", 33.4343, -112.0116),
        new("KDEN", "Denver CO", 39.8617, -104.6732),
        new("KMCI", "Kansas City MO", 39.2976, -94.7139),
        new("KORD", "Chicago IL", 41.9742, -87.9073),
        new("KPIT", "Pittsburgh PA", 40.4915, -80.2329),
        new("KBOS", "Boston MA", 42.3606, -71.0097),
        new("KPWM", "Portland ME", 43.6462, -70.3093),
        new("KSEA", "Seattle WA", 47.4447, -122.3144),
        new("KSLC", "Salt Lake City UT", 40.7884, -111.9778),
    };

    public static SamplePoint[] FromConfig(IConfiguration cfg)
    {
        var raw = cfg["Verify:Points"];
        if (string.IsNullOrWhiteSpace(raw)) return Default;
        var list = new List<SamplePoint>();
        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var f = part.Split(',', StringSplitOptions.TrimEntries);
            if (f.Length >= 3 && double.TryParse(f[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var la) && double.TryParse(f[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lo))
                list.Add(new SamplePoint(f[0].ToUpperInvariant(), f.Length > 3 ? f[3] : f[0], la, lo));
        }
        return list.Count > 0 ? list.ToArray() : Default;
    }
}
