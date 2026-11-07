using System.Globalization;
using System.Text.Json;
using CampRadar.Api.Nws;
using CampRadar.Api.Spots;

namespace CampRadar.Api.Alerts;

/// <summary>
/// Builds the same "facts" object the app builds for its briefing, but on the server, from the NWS forecast plus the
/// spot's status (radar, lightning, alerts). Used for the opt-in morning briefing push, and reusable for richer alerts.
/// </summary>
public sealed class BriefingFactsBuilder
{
    private readonly NwsForecastClient _fc;
    private readonly StatusService _status;
    public BriefingFactsBuilder(NwsForecastClient fc, StatusService status) { _fc = fc; _status = status; }

    public async Task<JsonElement?> BuildAsync(double lat, double lon, string activity, string lang, CancellationToken ct)
    {
        var f = await _fc.GetAsync(lat, lon, ct);
        if (f is null || f.Hours.Count == 0) return null;
        var s = await _status.GetAsync(lat, lon, new StatusOptions(), ct);
        var now = DateTimeOffset.UtcNow.ToOffset(f.Offset);
        var today = now.Date; var tomorrow = today.AddDays(1);
        bool morning = now.Hour < 12;
        var todayHours = f.Hours.Where(h => h.T.Date == today && h.T.AddHours(1) > now).ToList();
        var next = todayHours.Take(8).ToList();
        var outlookHours = morning ? f.Hours.Where(h => h.T.Date == today && h.T.Hour >= 12).ToList() : f.Hours.Where(h => h.T.Date == tomorrow).ToList();
        ForecastPeriod? Day(DateTime d) => f.Periods.FirstOrDefault(p => p.Start.Date == d && p.IsDaytime);
        ForecastPeriod? Night(DateTime d) => f.Periods.FirstOrDefault(p => p.Start.Date == d && !p.IsDaytime);
        object? Period(ForecastPeriod? p) => p is null ? null : new { hi = p.IsDaytime ? p.Temperature : (double?)null, lo = p.IsDaytime ? (double?)null : p.Temperature, text = p.ShortForecast, pop = p.Pop, wind = p.WindSpeed, windDir = p.WindDirection };
        var sun = Sun.Times(lat, lon, today, f.Offset); var sunTm = Sun.Times(lat, lon, tomorrow, f.Offset);
        var h0 = next.FirstOrDefault() ?? f.Hours[0];
        string precip = h0.Sky.Contains("freezing", StringComparison.OrdinalIgnoreCase) ? "freezing rain" : h0.Sky.Contains("sleet", StringComparison.OrdinalIgnoreCase) ? "sleet" : h0.Sky.Contains("snow", StringComparison.OrdinalIgnoreCase) || h0.Temp <= 33 ? "snow" : h0.Temp <= 36 ? "wintry mix" : "rain";

        var facts = new
        {
            place = f.Place, activity, language = lang == "es" ? "es-US" : "en-US", time = Say(now), morning,
            now = new { temp = $"{Math.Round(h0.Temp)}°", cond = h0.Sky, wind = $"Wind {h0.Dir} {Math.Round(h0.WindMph)} mph" },
            status = new { level = s.Level, main = s.Main, sub = s.Sub },
            rain = s.Rain.Level is "incoming" or "imminent" or "atyou" ? new { etaMin = s.Rain.EtaMin.HasValue ? Math.Max(0, Math.Round(s.Rain.EtaMin.Value)) : 0, endMin = s.Rain.EndMin.HasValue ? Math.Round(s.Rain.EndMin.Value) : (double?)null, intensity = Intensity(s.Rain.MaxDbz), from = s.Rain.FromBearing.HasValue ? CompassLong(s.Rain.FromBearing.Value) : "west", trend = (string?)null, fizzles = false } : null,
            passing = s.Rain.Level == "passing" && s.Rain.Passing is { } ps ? new { distMi = Math.Round(ps.DistMi), dir = Compass(ps.Bearing), passDir = CompassLong(ps.PassBearing) } : null,
            lightning = s.Lightning is { } l ? new { within10 = l.Within10 > 0, within30 = l.Within30 > 0, nearestMi = l.NearestMi.HasValue ? Math.Round(l.NearestMi.Value) : (double?)null, dir = l.NearestBearing.HasValue ? Compass(l.NearestBearing.Value) : null, trend = l.Trend } : null,
            alerts = s.Alerts.Select(a => new { @event = a.Event, ends = a.Ends is { } e && DateTimeOffset.TryParse(e, CultureInfo.InvariantCulture, DateTimeStyles.None, out var et) ? Say(et.ToOffset(f.Offset)) : null, critical = a.Critical }),
            hazards = Array.Empty<string>(),
            next = next.Select(h => new { at = Say(h.T), temp = h.Temp, feels = Math.Round(h.Feels), pop = h.Pop, thunder = h.Thunder, wind = h.WindMph, gust = h.GustMph, dir = h.Dir, sky = h.Sky }),
            nextStats = Stats(next),
            today = new { day = Period(Day(today)), night = Period(Night(today)) },
            outlook = new
            {
                label = morning ? "this afternoon and evening" : "tomorrow", stats = Stats(outlookHours),
                day = morning ? null : Period(Day(tomorrow)), night = morning ? Period(Night(today)) : Period(Night(tomorrow)),
                sun = morning ? (sun is { } su ? new { set = Say(su.Set), dusk = Say(su.Dusk), rise = (string?)null, dawn = (string?)null } : null) : (sunTm is { } st ? new { set = Say(st.Set), dusk = (string?)null, rise = Say(st.Rise), dawn = Say(st.Dawn) } : null)
            },
            sun = sun is { } s2 ? new { rise = Say(s2.Rise), set = Say(s2.Set), dusk = Say(s2.Dusk), minsToSunset = Math.Round((s2.Set - now).TotalMinutes) } : null,
            moon = Moon.Phase(now),
            pressure = (object?)null, precipType = precip, visibilityMi = h0.VisMi.HasValue ? Math.Round(h0.VisMi.Value, 1) : (double?)null,
            beach = (object?)null, tropical = s.Alerts.Where(a => a.Event.Contains("Hurricane") || a.Event.Contains("Tropical") || a.Event.Contains("Storm Surge")).Select(a => new { @event = a.Event, headline = a.Headline }),
            snowGround = (object?)null
        };
        return JsonSerializer.SerializeToElement(facts);
    }

    private static object? Stats(List<ForecastHour> hs)
    {
        if (hs.Count == 0) return null;
        var rainy = hs.Where(h => h.Pop >= 40).ToList(); var thunder = hs.Where(h => h.Thunder >= 20).ToList();
        var gust = hs.MaxBy(h => Math.Max(h.WindMph, h.GustMph))!;
        var snowIn = Math.Round(hs.Sum(h => h.SnowIn), 1); var firstSnow = hs.FirstOrDefault(h => h.SnowIn >= 0.05);
        var vis = hs.Where(h => h.VisMi.HasValue).Select(h => h.VisMi!.Value).ToList();
        return new
        {
            from = Say(hs[0].T), to = Say(hs[^1].T.AddHours(1)), maxPop = hs.Max(h => h.Pop),
            rainFrom = rainy.Count > 0 ? Say(rainy[0].T) : null, rainTo = rainy.Count > 0 ? Say(rainy[^1].T.AddHours(1)) : null, allRain = rainy.Count >= hs.Count - 1 && rainy.Count > 0,
            thunderFrom = thunder.Count > 0 ? Say(thunder[0].T) : null, thunderTo = thunder.Count > 0 ? Say(thunder[^1].T.AddHours(1)) : null, maxThunder = hs.Max(h => h.Thunder),
            hi = hs.Max(h => h.Temp), lo = hs.Min(h => h.Temp), feelsHi = Math.Round(hs.Max(h => h.Feels)), feelsLo = Math.Round(hs.Min(h => h.Feels)),
            topGust = Math.Round(Math.Max(gust.WindMph, gust.GustMph)), gustAt = Say(gust.T), windDir = hs[0].Dir, windDirLater = hs[Math.Min(hs.Count - 1, 5)].Dir, windNow = Math.Round(hs[0].WindMph), sky = hs[hs.Count / 2].Sky,
            snowIn, snowFrom = firstSnow is null ? null : Say(firstSnow.T), visMin = vis.Count > 0 ? Math.Round(vis.Min(), 1) : (double?)null, waveMax = (double?)null
        };
    }

    /// <summary>Spoken time: "4 PM", "4:30 PM".</summary>
    public static string Say(DateTimeOffset t) => t.Minute == 0 ? t.ToString("h tt", CultureInfo.InvariantCulture) : t.ToString("h:mm tt", CultureInfo.InvariantCulture);
    private static string Intensity(double? dbz) => dbz is null ? "Light" : dbz >= 50 ? "Very heavy" : dbz >= 40 ? "Heavy" : dbz >= 30 ? "Moderate" : "Light";
    private static readonly string[] Dirs16 = { "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW" };
    private static readonly string[] Dirs8 = { "north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest" };
    public static string Compass(double deg) => Dirs16[(int)Math.Round(((deg % 360) + 360) % 360 / 22.5) % 16];
    public static string CompassLong(double deg) => Dirs8[(int)Math.Round(((deg % 360) + 360) % 360 / 45) % 8];
}

/// <summary>Sunrise / sunset / civil dawn and dusk (NOAA solar equations), returned in the given offset.</summary>
public static class Sun
{
    public sealed record Times(DateTimeOffset Rise, DateTimeOffset Set, DateTimeOffset Dawn, DateTimeOffset Dusk);
    public static Times? Times(double lat, double lon, DateTime date, TimeSpan offset)
    {
        DateTimeOffset? Calc(double zenith, bool rising)
        {
            // Julian day at local noon
            var noonUtc = new DateTimeOffset(date.Year, date.Month, date.Day, 12, 0, 0, offset).ToUniversalTime();
            double jd = noonUtc.ToUnixTimeSeconds() / 86400.0 + 2440587.5;
            double n = jd - 2451545.0 + 0.0008;
            double jStar = n - lon / 360.0;
            double M = (357.5291 + 0.98560028 * jStar) % 360; double Mr = M * Math.PI / 180;
            double C = 1.9148 * Math.Sin(Mr) + 0.02 * Math.Sin(2 * Mr) + 0.0003 * Math.Sin(3 * Mr);
            double lambda = (M + C + 180 + 102.9372) % 360; double lr = lambda * Math.PI / 180;
            double jTransit = 2451545.0 + jStar + 0.0053 * Math.Sin(Mr) - 0.0069 * Math.Sin(2 * lr);
            double decl = Math.Asin(Math.Sin(lr) * Math.Sin(23.44 * Math.PI / 180));
            double latr = lat * Math.PI / 180;
            double cosH = (Math.Cos(zenith * Math.PI / 180) - Math.Sin(latr) * Math.Sin(decl)) / (Math.Cos(latr) * Math.Cos(decl));
            if (cosH < -1 || cosH > 1) return null;
            double H = Math.Acos(cosH) * 180 / Math.PI / 360.0;
            double j = rising ? jTransit - H : jTransit + H;
            return DateTimeOffset.FromUnixTimeSeconds((long)((j - 2440587.5) * 86400)).ToOffset(offset);
        }
        var rise = Calc(90.833, true); var set = Calc(90.833, false); var dawn = Calc(96, true); var dusk = Calc(96, false);
        return rise is null || set is null ? null : new Times(rise.Value, set.Value, dawn ?? rise.Value, dusk ?? set.Value);
    }
}

public static class Moon
{
    public static object Phase(DateTimeOffset t)
    {
        double days = (t.ToUnixTimeSeconds() - 947182440) / 86400.0;   // new moon 2000-01-06 18:14 UTC
        double age = ((days % 29.530588853) + 29.530588853) % 29.530588853;
        int ill = (int)Math.Round((1 - Math.Cos(age / 29.530588853 * 2 * Math.PI)) / 2 * 100);
        string name = age < 1.85 ? "New moon" : age < 5.5 ? "Waxing crescent" : age < 9.2 ? "First quarter" : age < 12.9 ? "Waxing gibbous" : age < 16.6 ? "Full moon" : age < 20.3 ? "Waning gibbous" : age < 24 ? "Last quarter" : age < 27.7 ? "Waning crescent" : "New moon";
        return new { ill, name };
    }
}

/// <summary>Plain-English / Spanish fallback used when no model provider answers, so the morning push still says something useful.</summary>
public static class PlainBriefing
{
    public static string Compose(JsonElement f, string lang)
    {
        string S(string path) { var cur = f; foreach (var k in path.Split('.')) { if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(k, out cur)) return ""; } return cur.ValueKind == JsonValueKind.Null ? "" : cur.ValueKind == JsonValueKind.String ? cur.GetString() ?? "" : cur.ToString(); }
        bool es = lang == "es";
        var sb = new List<string>();
        var place = S("place"); var temp = S("now.temp").Replace("°", es ? " grados" : " degrees"); var cond = S("now.cond");
        sb.Add(es ? $"Informe de la mañana{(place.Length > 0 ? " para " + place : "")}: ahora hay {temp}, {cond.ToLowerInvariant()}." : $"Morning briefing{(place.Length > 0 ? " for " + place : "")}: right now it's {temp} and {cond.ToLowerInvariant()}.");
        var eta = S("rain.etaMin");
        if (eta.Length > 0) sb.Add(es ? $"Viene lluvia; debería llegar en unos {eta} minutos." : $"Rain is on the way and should reach you in about {eta} minutes.");
        else { var pop = S("nextStats.maxPop"); var rf = S("nextStats.rainFrom"); sb.Add(rf.Length > 0 ? (es ? $"Lluvia probable alrededor de las {rf}, hasta {pop} por ciento." : $"Rain likely around {rf}, up to {pop} percent.") : (es ? "Las próximas horas se ven secas." : "The next few hours look dry.")); }
        var th = S("nextStats.thunderFrom"); if (th.Length > 0) sb.Add(es ? $"Posibles tormentas alrededor de las {th}." : $"Thunderstorms possible around {th}.");
        var gust = S("nextStats.topGust"); var gustAt = S("nextStats.gustAt"); if (double.TryParse(gust, out var g) && g >= 20) sb.Add(es ? $"Ráfagas de {Math.Round(g)} alrededor de las {gustAt}." : $"Gusts to {Math.Round(g)} around {gustAt}.");
        var hi = S("nextStats.hi"); var lo = S("nextStats.lo"); if (hi.Length > 0) sb.Add(es ? $"Temperaturas de {lo} a {hi}." : $"Temperatures {lo} to {hi}.");
        var nl = S("today.night.lo"); if (nl.Length > 0) sb.Add(es ? $"Esta noche baja a unos {nl}." : $"Tonight drops to around {nl}.");
        var set = S("sun.set"); if (set.Length > 0) sb.Add(es ? $"El sol se pone a las {set}." : $"Sunset is at {set}.");
        foreach (var a in f.GetProperty("alerts").EnumerateArray()) if (a.GetProperty("critical").GetBoolean()) sb.Insert(0, es ? $"Atención: {a.GetProperty("event").GetString()} en vigor." : $"Heads up: a {a.GetProperty("event").GetString()} is in effect.");
        return string.Join(" ", sb);
    }
}
