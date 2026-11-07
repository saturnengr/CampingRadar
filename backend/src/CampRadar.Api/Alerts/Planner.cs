using CampRadar.Api.Nws;

namespace CampRadar.Api.Alerts;

/// <summary>
/// Turns an hourly forecast (plus live radar status) into plans people can act on:
/// dry windows, a "leave by" time, and threshold hits (gusts, cold, heat, visibility, snow, rain chance).
/// All times are in the site's local offset, as the NWS reports them.
/// </summary>
public static class Planner
{
    public static readonly Dictionary<string, double> DefaultThresholds = new() { ["gustMph"] = 30, ["lowF"] = 32, ["highF"] = 95, ["visMi"] = 1, ["snowIn"] = 1, ["popPct"] = 60 };

    public sealed record Window(DateTimeOffset From, DateTimeOffset To, int Hours, int MaxPop, double MaxGust, double Hi, double Lo, string Sky, string Label);
    public sealed record LeaveBy(DateTimeOffset At, string Reason, string ReasonEs, DateTimeOffset Trouble, double? Confidence);
    public sealed record Hit(string Key, string Label, string LabelEs, double Value, DateTimeOffset At, bool Now);

    static bool Dry(ForecastHour h, double gustMax) => h.Pop < 25 && h.Thunder < 15 && Math.Max(h.WindMph, h.GustMph) <= gustMax && h.SnowIn < 0.05;

    /// <summary>Stretches of at least minLen consecutive dry hours within the next <paramref name="hours"/> hours.</summary>
    public static List<Window> DryWindows(Forecast f, DateTimeOffset now, int hours = 48, int minLen = 2, double gustMax = 25)
    {
        var hs = f.Hours.Where(h => h.T.AddHours(1) > now && h.T < now.AddHours(hours)).ToList();
        var list = new List<Window>(); int i = 0;
        while (i < hs.Count)
        {
            if (!Dry(hs[i], gustMax)) { i++; continue; }
            int j = i; while (j + 1 < hs.Count && Dry(hs[j + 1], gustMax)) j++;
            var run = hs.GetRange(i, j - i + 1);
            if (run.Count >= minLen)
            {
                var from = run[0].T < now ? now : run[0].T; var to = run[^1].T.AddHours(1);
                list.Add(new Window(from, to, run.Count, run.Max(h => h.Pop), Math.Round(run.Max(h => Math.Max(h.WindMph, h.GustMph))), run.Max(h => h.Temp), run.Min(h => h.Temp), run[run.Count / 2].Sky, Label(from, to, now)));
            }
            i = j + 1;
        }
        return list;
    }

    static string Label(DateTimeOffset from, DateTimeOffset to, DateTimeOffset now)
    {
        string day = from.Date == now.Date ? "today" : from.Date == now.Date.AddDays(1) ? "tomorrow" : from.ToString("dddd", System.Globalization.CultureInfo.InvariantCulture);
        return $"{day} {BriefingFactsBuilder.Say(from)} – {BriefingFactsBuilder.Say(to)}";
    }

    /// <summary>
    /// When to start heading back so you're under cover before trouble. Trouble is the first hour with a real rain chance,
    /// thunder, strong gusts, or snow — or radar rain already on the way, whichever is sooner. backMin is how long the walk/paddle back takes.
    /// </summary>
    public static LeaveBy? LeaveByTime(Forecast f, Status? s, DateTimeOffset now, int backMin, Dictionary<string, double>? thr = null, int horizonH = 12)
    {
        double gustMax = thr is not null && thr.TryGetValue("gustMph", out var g) ? g : DefaultThresholds["gustMph"];
        double popMax = thr is not null && thr.TryGetValue("popPct", out var p) ? p : 50;
        DateTimeOffset? trouble = null; string why = "", whyEs = ""; double? conf = null;
        foreach (var h in f.Hours.Where(h => h.T.AddHours(1) > now && h.T < now.AddHours(horizonH)))
        {
            if (h.Thunder >= 30) { trouble = h.T; why = "thunderstorms likely"; whyEs = "tormentas probables"; conf = h.Thunder / 100.0; break; }
            if (h.Pop >= popMax) { trouble = h.T; why = $"rain likely ({h.Pop}%)"; whyEs = $"lluvia probable ({h.Pop}%)"; conf = h.Pop / 100.0; break; }
            if (Math.Max(h.WindMph, h.GustMph) >= gustMax) { trouble = h.T; why = $"gusts to {Math.Round(Math.Max(h.WindMph, h.GustMph))} mph"; whyEs = $"ráfagas de {Math.Round(Math.Max(h.WindMph, h.GustMph))} mph"; conf = 0.7; break; }
            if (h.SnowIn >= 0.1) { trouble = h.T; why = "snow"; whyEs = "nieve"; conf = 0.6; break; }
        }
        // Radar beats the forecast when rain is already moving in.
        if (s is { Rain.Level: "incoming" or "imminent" or "atyou" } && s.Rain.EtaMin is { } eta)
        {
            var radarT = now.AddMinutes(Math.Max(0, eta));
            if (trouble is null || radarT < trouble) { trouble = radarT; why = eta <= 5 ? "rain is here" : $"rain on radar, about {Math.Round(eta)} min out"; whyEs = eta <= 5 ? "la lluvia ya llegó" : $"lluvia en el radar, a unos {Math.Round(eta)} min"; conf = 0.9; }
        }
        if (s is not null) foreach (var a in s.Alerts.Where(a => a.Critical)) { trouble = now; why = a.Event; whyEs = a.Event; conf = 1; }
        if (trouble is null) return null;
        var leave = trouble.Value.AddMinutes(-backMin - 10);   // ten minutes of slack
        if (leave < now) leave = now;
        return new LeaveBy(leave, why, whyEs, trouble.Value, conf);
    }

    /// <summary>Which personal thresholds the next <paramref name="hours"/> hours cross, first crossing of each.</summary>
    public static List<Hit> ThresholdHits(Forecast f, DateTimeOffset now, Dictionary<string, double> thr, int hours = 12)
    {
        var hs = f.Hours.Where(h => h.T.AddHours(1) > now && h.T < now.AddHours(hours)).ToList();
        var hits = new List<Hit>();
        void Check(string key, Func<ForecastHour, double> val, Func<double, double, bool> cross, string label, string labelEs, string unit)
        {
            if (!thr.TryGetValue(key, out var limit)) return;
            var h = hs.FirstOrDefault(x => cross(val(x), limit)); if (h is null) return;
            var v = Math.Round(val(h), unit == "mi" ? 1 : 0);
            hits.Add(new Hit(key, $"{label} {v}{unit} around {BriefingFactsBuilder.Say(h.T)}", $"{labelEs} {v}{unit} alrededor de las {BriefingFactsBuilder.Say(h.T)}", v, h.T, h.T <= now));
        }
        Check("gustMph", h => Math.Max(h.WindMph, h.GustMph), (v, l) => v >= l, "Gusts to", "Ráfagas de", " mph");
        Check("lowF", h => h.Temp, (v, l) => v <= l, "Down to", "Baja a", "°");
        Check("highF", h => h.Feels, (v, l) => v >= l, "Feels like", "Sensación de", "°");
        Check("visMi", h => h.VisMi ?? 99, (v, l) => v <= l, "Visibility", "Visibilidad", " mi");
        Check("popPct", h => h.Pop, (v, l) => v >= l, "Rain chance", "Prob. de lluvia", "%");
        if (thr.TryGetValue("snowIn", out var snowLimit))
        {
            double acc = 0; foreach (var h in hs) { acc += h.SnowIn; if (acc >= snowLimit) { hits.Add(new Hit("snowIn", $"Snow {Math.Round(acc, 1)} in by {BriefingFactsBuilder.Say(h.T.AddHours(1))}", $"Nieve {Math.Round(acc, 1)} pulg para las {BriefingFactsBuilder.Say(h.T.AddHours(1))}", Math.Round(acc, 1), h.T, false)); break; } }
        }
        return hits;
    }

    /// <summary>One spoken line about a saved place for the morning digest.</summary>
    public static string DigestLine(string name, Forecast f, DateTimeOffset now, string lang)
    {
        bool es = lang == "es";
        var day = f.Periods.FirstOrDefault(p => p.Start.Date == now.Date && p.IsDaytime) ?? f.Periods.FirstOrDefault(p => p.IsDaytime);
        var hs = f.Hours.Where(h => h.T.Date == now.Date && h.T.AddHours(1) > now).ToList();
        var parts = new List<string>();
        if (day is not null) parts.Add(es ? $"{day.ShortForecast.ToLowerInvariant()}, máxima de {Math.Round(day.Temperature)}" : $"{day.ShortForecast.ToLowerInvariant()}, high of {Math.Round(day.Temperature)}");
        if (hs.Count > 0)
        {
            var rain = hs.Where(h => h.Pop >= 40).ToList();
            if (rain.Count > 0) parts.Add(es ? $"lluvia posible desde las {BriefingFactsBuilder.Say(rain[0].T)}" : $"rain possible from {BriefingFactsBuilder.Say(rain[0].T)}");
            var th = hs.FirstOrDefault(h => h.Thunder >= 20); if (th is not null) parts.Add(es ? $"tormentas alrededor de las {BriefingFactsBuilder.Say(th.T)}" : $"storms around {BriefingFactsBuilder.Say(th.T)}");
            var gust = hs.Max(h => Math.Max(h.WindMph, h.GustMph)); if (gust >= 25) parts.Add(es ? $"ráfagas de {Math.Round(gust)}" : $"gusts to {Math.Round(gust)}");
            var dry = DryWindows(f, now, 16, 3, 30).FirstOrDefault(); if (rain.Count > 0 && dry is not null) parts.Add(es ? $"ventana seca {dry.Label.Replace("today", "hoy").Replace("tomorrow", "mañana")}" : $"dry window {dry.Label}");
        }
        return parts.Count == 0 ? "" : (es ? $"En {name}: " : $"At {name}: ") + string.Join(", ", parts) + ".";
    }
}
