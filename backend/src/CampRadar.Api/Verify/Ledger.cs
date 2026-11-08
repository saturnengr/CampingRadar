using System.Globalization;
using System.Text.Json;
using CampRadar.Api.Alerts;
using Microsoft.Data.Sqlite;

namespace CampRadar.Api.Verify;

/// <summary>
/// The verification ledger: one row per status the server computed for a point (sample airport, subscription or saved
/// place) with what the tracker said, and later what happened — did rain reach the point by radar, and what did the
/// nearest gauge measure. Aggregates only: subscriptions are keyed by a hash and rounded to about three miles.
/// </summary>
public sealed class Ledger
{
    private readonly string _cs;
    private readonly ILogger<Ledger> _log;
    public Ledger(IConfiguration cfg, ILogger<Ledger> log)
    {
        _log = log;
        var dir = cfg["DataDir"] ?? "App_Data"; Directory.CreateDirectory(dir);
        _cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, "verify.db") }.ToString();
        using var c = Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS forecasts(
              id INTEGER PRIMARY KEY AUTOINCREMENT, t_ms INTEGER NOT NULL, radar_ms INTEGER NOT NULL, src TEXT NOT NULL, point TEXT NOT NULL,
              lat REAL NOT NULL, lon REAL NOT NULL, month INTEGER NOT NULL, hour_utc INTEGER NOT NULL,
              level TEXT NOT NULL, color TEXT NOT NULL, eta_min REAL, end_min REAL, max_dbz REAL, from_bearing REAL,
              nearest_mi REAL, nearest_dbz REAL, rain_now INTEGER NOT NULL, motion_ok INTEGER NOT NULL, speed_mph REAL, heading_deg REAL, conf REAL,
              edge_verdict TEXT, edge_rate REAL, fizzles INTEGER NOT NULL, pop2h INTEGER, dew_spread REAL, main TEXT,
              resolved INTEGER NOT NULL DEFAULT 0, actual_min REAL, gauge_wet INTEGER, gauge_mm REAL);
            CREATE INDEX IF NOT EXISTS fc_point_t ON forecasts(point, t_ms);
            CREATE INDEX IF NOT EXISTS fc_unresolved ON forecasts(resolved, t_ms);
            CREATE TABLE IF NOT EXISTS gauge(
              point TEXT NOT NULL, t_ms INTEGER NOT NULL, precip_mm REAL, wx TEXT, temp_f REAL, dew_f REAL, raw_present TEXT,
              PRIMARY KEY(point, t_ms));
            """);
    }

    /// <summary>Short stable hash of an internal id, so subscriptions can be followed over time without being identifiable.</summary>
    public static string Key(string id) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id)))[..10].ToLowerInvariant();

    private SqliteConnection Open() { var c = new SqliteConnection(_cs); c.Open(); using (var p = c.CreateCommand()) { p.CommandText = "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL;"; p.ExecuteNonQuery(); } return c; }
    private static void Exec(SqliteConnection c, string sql, params (string, object?)[] p)
    { using var cmd = c.CreateCommand(); cmd.CommandText = sql; foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value); cmd.ExecuteNonQuery(); }

    /// <summary>Record what the tracker said for this point right now. Skips duplicates for the same point and radar scan.</summary>
    public void Log(string src, string point, double lat, double lon, Status s)
    {
        var r = s.Rain; if (r.Error is not null || r.LatestRadarMs == 0) return;
        try
        {
            using var c = Open();
            using (var q = c.CreateCommand())
            {
                q.CommandText = "SELECT 1 FROM forecasts WHERE point=$p AND radar_ms=$r LIMIT 1"; q.Parameters.AddWithValue("$p", point); q.Parameters.AddWithValue("$r", r.LatestRadarMs);
                if (q.ExecuteScalar() is not null) return;
            }
            var now = DateTimeOffset.UtcNow;
            Exec(c, """
                INSERT INTO forecasts(t_ms,radar_ms,src,point,lat,lon,month,hour_utc,level,color,eta_min,end_min,max_dbz,from_bearing,nearest_mi,nearest_dbz,rain_now,motion_ok,speed_mph,heading_deg,conf,edge_verdict,edge_rate,fizzles,pop2h,dew_spread,main)
                VALUES($t,$r,$src,$p,$lat,$lon,$mo,$h,$lv,$col,$eta,$end,$dbz,$fb,$nmi,$ndbz,$rn,$mok,$sp,$hd,$cf,$ev,$er,$fz,$pop,$dew,$main)
                """,
                ("$t", now.ToUnixTimeMilliseconds()), ("$r", r.LatestRadarMs), ("$src", src), ("$p", point), ("$lat", Math.Round(lat, 2)), ("$lon", Math.Round(lon, 2)), ("$mo", now.Month), ("$h", now.Hour),
                ("$lv", r.Level), ("$col", r.Color), ("$eta", r.EtaMin), ("$end", r.EndMin), ("$dbz", r.MaxDbz), ("$fb", r.FromBearing), ("$nmi", r.Nearest?.DistMi), ("$ndbz", r.Nearest?.Dbz),
                ("$rn", r.RainNow ? 1 : 0), ("$mok", r.MotionOk ? 1 : 0), ("$sp", r.MotionOk ? r.SpeedMph : null), ("$hd", r.MotionOk ? r.HeadingDeg : null), ("$cf", r.MotionConf),
                ("$ev", r.EdgeVerdict), ("$er", r.EdgeRateMph), ("$fz", r.Fizzles ? 1 : 0), ("$pop", r.Pop2h), ("$dew", r.DewSpread), ("$main", r.Main));
        }
        catch (Exception ex) { _log.LogDebug(ex, "ledger log"); }
    }

    public void LogGauge(string point, long tMs, double? precipMm, string? wx, double? tempF, double? dewF, string? rawPresent)
    {
        try { using var c = Open(); Exec(c, "INSERT OR IGNORE INTO gauge(point,t_ms,precip_mm,wx,temp_f,dew_f,raw_present) VALUES($p,$t,$mm,$wx,$tf,$df,$rp)", ("$p", point), ("$t", tMs), ("$mm", precipMm), ("$wx", wx), ("$tf", tempF), ("$df", dewF), ("$rp", rawPresent)); }
        catch (Exception ex) { _log.LogDebug(ex, "ledger gauge"); }
    }

    /// <summary>Gauge rows for one point in a time window (used to pack ground truth into a recording).</summary>
    public List<object> GaugeRows(string point, long fromMs, long toMs)
    {
        var list = new List<object>();
        try
        {
            using var c = Open(); using var q = c.CreateCommand();
            q.CommandText = "SELECT t_ms,precip_mm,wx,temp_f,dew_f,raw_present FROM gauge WHERE point=$p AND t_ms BETWEEN $a AND $b ORDER BY t_ms";
            q.Parameters.AddWithValue("$p", point); q.Parameters.AddWithValue("$a", fromMs); q.Parameters.AddWithValue("$b", toMs);
            using var r = q.ExecuteReader();
            while (r.Read()) list.Add(new { t = r.GetInt64(0), precipMm = r.IsDBNull(1) ? (double?)null : r.GetDouble(1), wx = r.IsDBNull(2) ? null : r.GetString(2), tempF = r.IsDBNull(3) ? (double?)null : r.GetDouble(3), dewF = r.IsDBNull(4) ? (double?)null : r.GetDouble(4), present = r.IsDBNull(5) ? null : r.GetString(5) });
        }
        catch (Exception ex) { _log.LogDebug(ex, "gauge rows"); }
        return list;
    }

    /// <summary>
    /// Attach outcomes to forecasts older than two hours: actual_min = minutes until the first later row for the same point with
    /// rain at the point (null if none within 120 min and the point kept being evaluated); gauge_wet from any gauge report in the
    /// following 2.5 hours with measurable precipitation or rain/drizzle/snow in the present weather.
    /// </summary>
    public int Resolve()
    {
        int n = 0;
        try
        {
            using var c = Open();
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), cutoff = now - 125 * 60000L;
            var pending = new List<(long Id, string Point, long T)>();
            using (var q = c.CreateCommand())
            {
                q.CommandText = "SELECT id,point,t_ms FROM forecasts WHERE resolved=0 AND t_ms<$c ORDER BY t_ms LIMIT 2000"; q.Parameters.AddWithValue("$c", cutoff);
                using var r = q.ExecuteReader(); while (r.Read()) pending.Add((r.GetInt64(0), r.GetString(1), r.GetInt64(2)));
            }
            foreach (var (id, point, t) in pending)
            {
                // radar outcome: first later row with rain at the point; was the point still being watched through the window?
                long? firstRain = null, lastSeen = null;
                using (var q = c.CreateCommand())
                {
                    q.CommandText = "SELECT MIN(CASE WHEN rain_now=1 THEN t_ms END), MAX(t_ms) FROM forecasts WHERE point=$p AND t_ms>$t AND t_ms<=$t2";
                    q.Parameters.AddWithValue("$p", point); q.Parameters.AddWithValue("$t", t); q.Parameters.AddWithValue("$t2", t + 120 * 60000L);
                    using var r = q.ExecuteReader(); if (r.Read()) { firstRain = r.IsDBNull(0) ? null : r.GetInt64(0); lastSeen = r.IsDBNull(1) ? null : r.GetInt64(1); }
                }
                bool watched = lastSeen is not null && lastSeen >= t + 100 * 60000L;
                if (firstRain is null && !watched) { Exec(c, "UPDATE forecasts SET resolved=2 WHERE id=$id", ("$id", id)); n++; continue; }   // 2 = unknowable (point stopped being evaluated)
                double? actual = firstRain is null ? null : (firstRain.Value - t) / 60000.0;
                // gauge outcome
                int? wet = null; double? mm = null;
                using (var q = c.CreateCommand())
                {
                    q.CommandText = "SELECT COUNT(*), SUM(COALESCE(precip_mm,0)), SUM(CASE WHEN COALESCE(precip_mm,0)>0 OR raw_present LIKE '%RA%' OR raw_present LIKE '%DZ%' OR raw_present LIKE '%SN%' OR raw_present LIKE '%TS%' THEN 1 ELSE 0 END) FROM gauge WHERE point=$p AND t_ms>$t AND t_ms<=$t2";
                    q.Parameters.AddWithValue("$p", point); q.Parameters.AddWithValue("$t", t); q.Parameters.AddWithValue("$t2", t + 150 * 60000L);
                    using var r = q.ExecuteReader(); if (r.Read() && r.GetInt64(0) > 0) { mm = r.IsDBNull(1) ? 0 : r.GetDouble(1); wet = r.GetInt64(2) > 0 ? 1 : 0; }
                }
                Exec(c, "UPDATE forecasts SET resolved=1, actual_min=$a, gauge_wet=$w, gauge_mm=$mm WHERE id=$id", ("$a", actual), ("$w", wet), ("$mm", mm), ("$id", id)); n++;
            }
            // keep the file in check: raw rows older than 400 days go
            Exec(c, "DELETE FROM forecasts WHERE t_ms<$old", ("$old", now - 400L * 86400000L));
            Exec(c, "DELETE FROM gauge WHERE t_ms<$old", ("$old", now - 400L * 86400000L));
        }
        catch (Exception ex) { _log.LogWarning(ex, "ledger resolve"); }
        return n;
    }

    /// <summary>Admin summary: volume, and a contingency table of alarms (orange/red, ETA ≤ 60) against what happened.</summary>
    public object Summary(int days = 30)
    {
        long since = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeMilliseconds();
        using var c = Open();
        object Scalar(string sql) { using var q = c.CreateCommand(); q.CommandText = sql; q.Parameters.AddWithValue("$s", since); return q.ExecuteScalar() ?? 0; }
        var byColor = new List<object>();
        using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT color, COUNT(*), SUM(CASE WHEN actual_min IS NOT NULL AND actual_min<=90 THEN 1 ELSE 0 END), SUM(CASE WHEN gauge_wet=1 THEN 1 ELSE 0 END), SUM(CASE WHEN gauge_wet IS NOT NULL THEN 1 ELSE 0 END)
                FROM forecasts WHERE resolved=1 AND rain_now=0 AND t_ms>$s GROUP BY color ORDER BY COUNT(*) DESC
                """; q.Parameters.AddWithValue("$s", since);
            using var r = q.ExecuteReader();
            while (r.Read()) byColor.Add(new { color = r.GetString(0), n = r.GetInt64(1), radarRainWithin90 = r.GetInt64(2), gaugeWet = r.GetInt64(3), gaugeKnown = r.GetInt64(4) });
        }
        var byPoint = new List<object>();
        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT point, COUNT(*), SUM(resolved=1), MAX(t_ms) FROM forecasts WHERE t_ms>$s GROUP BY point ORDER BY point"; q.Parameters.AddWithValue("$s", since);
            using var r = q.ExecuteReader(); while (r.Read()) byPoint.Add(new { point = r.GetString(0), rows = r.GetInt64(1), resolved = r.GetInt64(2), last = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)) });
        }
        return new
        {
            days, rows = Scalar("SELECT COUNT(*) FROM forecasts WHERE t_ms>$s"), resolved = Scalar("SELECT COUNT(*) FROM forecasts WHERE resolved=1 AND t_ms>$s"), gaugeRows = Scalar("SELECT COUNT(*) FROM gauge WHERE t_ms>$s"),
            alarms = Scalar("SELECT COUNT(*) FROM forecasts WHERE resolved=1 AND rain_now=0 AND color IN ('orange','red') AND eta_min<=60 AND t_ms>$s"),
            alarmsVerifiedByRadar = Scalar("SELECT COUNT(*) FROM forecasts WHERE resolved=1 AND rain_now=0 AND color IN ('orange','red') AND eta_min<=60 AND actual_min<=90 AND t_ms>$s"),
            alarmsVerifiedByGauge = Scalar("SELECT COUNT(*) FROM forecasts WHERE resolved=1 AND rain_now=0 AND color IN ('orange','red') AND eta_min<=60 AND gauge_wet=1 AND t_ms>$s"),
            alarmsWithGauge = Scalar("SELECT COUNT(*) FROM forecasts WHERE resolved=1 AND rain_now=0 AND color IN ('orange','red') AND eta_min<=60 AND gauge_wet IS NOT NULL AND t_ms>$s"),
            misses = Scalar("SELECT COUNT(*) FROM forecasts WHERE resolved=1 AND rain_now=0 AND color NOT IN ('orange','red') AND actual_min<=60 AND t_ms>$s"),
            byColor, byPoint
        };
    }

    /// <summary>Raw rows as CSV for offline analysis.</summary>
    public string Csv(int days = 90)
    {
        long since = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeMilliseconds();
        var sb = new System.Text.StringBuilder("t_utc,radar_utc,src,point,lat,lon,level,color,eta_min,end_min,max_dbz,from_bearing,nearest_mi,nearest_dbz,rain_now,motion_ok,speed_mph,heading_deg,conf,edge_verdict,edge_rate,fizzles,pop2h,dew_spread,resolved,actual_min,gauge_wet,gauge_mm\n");
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = "SELECT t_ms,radar_ms,src,point,lat,lon,level,color,eta_min,end_min,max_dbz,from_bearing,nearest_mi,nearest_dbz,rain_now,motion_ok,speed_mph,heading_deg,conf,edge_verdict,edge_rate,fizzles,pop2h,dew_spread,resolved,actual_min,gauge_wet,gauge_mm FROM forecasts WHERE t_ms>$s ORDER BY t_ms";
        q.Parameters.AddWithValue("$s", since);
        using var r = q.ExecuteReader();
        while (r.Read())
        {
            for (int i = 0; i < r.FieldCount; i++)
            {
                if (i > 0) sb.Append(',');
                if (r.IsDBNull(i)) continue;
                if (i < 2) sb.Append(DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(i)).ToString("yyyy-MM-ddTHH:mm:ssZ"));
                else { var v = r.GetValue(i); sb.Append(v is double d ? d.ToString("0.###", CultureInfo.InvariantCulture) : v is string str ? '"' + str.Replace("\"", "\"\"") + '"' : Convert.ToString(v, CultureInfo.InvariantCulture)); }
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
