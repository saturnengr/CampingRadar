using Microsoft.Data.Sqlite;

namespace CampRadar.Api.Spots;

/// <summary>A device that asked for alerts at a location.</summary>
public sealed class Spot
{
    public string Id { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string P256dh { get; set; } = "";
    public string Auth { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
    public string Prefs { get; set; } = "{}";     // JSON: hitMi, threshDbz, lightningMi, quietFrom, quietTo ...
    public long SnoozeUntil { get; set; }
    public string LastState { get; set; } = "{}"; // JSON: last levels sent, for edge detection
    public long CreatedMs { get; set; }
    public long UpdatedMs { get; set; }
    public int Failures { get; set; }
}

/// <summary>SQLite-backed store of alert subscriptions (one file under App_Data).</summary>
public sealed class SpotStore
{
    private readonly string _cs;
    public SpotStore(IConfiguration cfg)
    {
        var dir = cfg["DataDir"] ?? "App_Data";
        Directory.CreateDirectory(dir);
        _cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, "campradar.db"), Cache = SqliteCacheMode.Shared }.ToString();
        using var c = Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS spots(
              id TEXT PRIMARY KEY, endpoint TEXT NOT NULL UNIQUE, p256dh TEXT NOT NULL, auth TEXT NOT NULL,
              lat REAL NOT NULL, lon REAL NOT NULL, prefs TEXT NOT NULL DEFAULT '{}', snooze_until INTEGER NOT NULL DEFAULT 0,
              last_state TEXT NOT NULL DEFAULT '{}', created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, failures INTEGER NOT NULL DEFAULT 0)
            """);
    }

    private SqliteConnection Open() { var c = new SqliteConnection(_cs); c.Open(); return c; }
    private static void Exec(SqliteConnection c, string sql, params (string, object?)[] p)
    {
        using var cmd = c.CreateCommand(); cmd.CommandText = sql;
        foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Insert or update by push endpoint; returns the spot id.</summary>
    public string Upsert(string endpoint, string p256dh, string auth, double lat, double lon, string prefs)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var c = Open();
        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT id FROM spots WHERE endpoint=$e"; q.Parameters.AddWithValue("$e", endpoint);
            if (q.ExecuteScalar() is string existing)
            {
                Exec(c, "UPDATE spots SET p256dh=$p, auth=$a, lat=$lat, lon=$lon, prefs=$prefs, updated_ms=$now, failures=0 WHERE id=$id",
                    ("$p", p256dh), ("$a", auth), ("$lat", lat), ("$lon", lon), ("$prefs", prefs), ("$now", now), ("$id", existing));
                return existing;
            }
        }
        var id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        Exec(c, "INSERT INTO spots(id,endpoint,p256dh,auth,lat,lon,prefs,created_ms,updated_ms) VALUES($id,$e,$p,$a,$lat,$lon,$prefs,$now,$now)",
            ("$id", id), ("$e", endpoint), ("$p", p256dh), ("$a", auth), ("$lat", lat), ("$lon", lon), ("$prefs", prefs), ("$now", now));
        return id;
    }

    public void SetSnooze(string id, long untilMs) { using var c = Open(); Exec(c, "UPDATE spots SET snooze_until=$u WHERE id=$id", ("$u", untilMs), ("$id", id)); }
    public void SetLocation(string id, double lat, double lon) { using var c = Open(); Exec(c, "UPDATE spots SET lat=$lat, lon=$lon, last_state='{}', updated_ms=$now WHERE id=$id", ("$lat", lat), ("$lon", lon), ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("$id", id)); }
    public void SetState(string id, string json) { using var c = Open(); Exec(c, "UPDATE spots SET last_state=$s WHERE id=$id", ("$s", json), ("$id", id)); }
    public void Delete(string id) { using var c = Open(); Exec(c, "DELETE FROM spots WHERE id=$id", ("$id", id)); }
    public void RecordFailure(string id, bool gone)
    {
        using var c = Open();
        if (gone) Exec(c, "DELETE FROM spots WHERE id=$id", ("$id", id));
        else Exec(c, "UPDATE spots SET failures=failures+1 WHERE id=$id", ("$id", id));
        Exec(c, "DELETE FROM spots WHERE failures>=10");
    }

    public List<Spot> All()
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = "SELECT id,endpoint,p256dh,auth,lat,lon,prefs,snooze_until,last_state,created_ms,updated_ms,failures FROM spots";
        using var r = q.ExecuteReader(); var list = new List<Spot>();
        while (r.Read()) list.Add(new Spot
        {
            Id = r.GetString(0), Endpoint = r.GetString(1), P256dh = r.GetString(2), Auth = r.GetString(3), Lat = r.GetDouble(4), Lon = r.GetDouble(5),
            Prefs = r.GetString(6), SnoozeUntil = r.GetInt64(7), LastState = r.GetString(8), CreatedMs = r.GetInt64(9), UpdatedMs = r.GetInt64(10), Failures = r.GetInt32(11)
        });
        return list;
    }

    public Spot? Get(string id) => All().FirstOrDefault(s => s.Id == id);
}
