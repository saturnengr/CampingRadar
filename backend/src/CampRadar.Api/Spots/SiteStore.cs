using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CampRadar.Api.Spots;

/// <summary>A saved place on an account: a campsite, trailhead, lake or cabin, separate from where the device is.</summary>
public sealed class Site
{
    public string Id { get; set; } = "";
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
    public string Activity { get; set; } = "camping";
    public bool Alerts { get; set; }          // push rain/lightning/warning/threshold alerts for this place
    public bool Digest { get; set; }          // include in the morning briefing
    public string Thresholds { get; set; } = "{}";   // JSON: gustMph, lowF, highF, visMi, snowIn, popPct (missing = off)
    public string LastState { get; set; } = "{}";
    public long CreatedMs { get; set; }
    public long UpdatedMs { get; set; }
}

public sealed class SiteStore
{
    public const int MaxPerAccount = 12;
    private readonly string _cs;
    public SiteStore(IConfiguration cfg)
    {
        var dir = cfg["DataDir"] ?? "App_Data";
        Directory.CreateDirectory(dir);
        _cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, "campradar.db") }.ToString();
        using var c = Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS sites(
              id TEXT PRIMARY KEY, email TEXT NOT NULL, name TEXT NOT NULL, lat REAL NOT NULL, lon REAL NOT NULL,
              activity TEXT NOT NULL DEFAULT 'camping', alerts INTEGER NOT NULL DEFAULT 0, digest INTEGER NOT NULL DEFAULT 0,
              thresholds TEXT NOT NULL DEFAULT '{}', last_state TEXT NOT NULL DEFAULT '{}', created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS sites_email ON sites(email);
            """);
    }

    private SqliteConnection Open() { var c = new SqliteConnection(_cs); c.Open(); using (var p = c.CreateCommand()) { p.CommandText = "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL;"; p.ExecuteNonQuery(); } return c; }
    private static void Exec(SqliteConnection c, string sql, params (string, object?)[] p)
    {
        using var cmd = c.CreateCommand(); cmd.CommandText = sql;
        foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static Site Row(SqliteDataReader r) => new()
    {
        Id = r.GetString(0), Email = r.GetString(1), Name = r.GetString(2), Lat = r.GetDouble(3), Lon = r.GetDouble(4), Activity = r.GetString(5),
        Alerts = r.GetInt32(6) == 1, Digest = r.GetInt32(7) == 1, Thresholds = r.GetString(8), LastState = r.GetString(9), CreatedMs = r.GetInt64(10), UpdatedMs = r.GetInt64(11)
    };
    private const string Cols = "id,email,name,lat,lon,activity,alerts,digest,thresholds,last_state,created_ms,updated_ms";

    public List<Site> All()
    {
        using var c = Open(); using var q = c.CreateCommand(); q.CommandText = $"SELECT {Cols} FROM sites ORDER BY created_ms";
        using var r = q.ExecuteReader(); var list = new List<Site>(); while (r.Read()) list.Add(Row(r)); return list;
    }
    public List<Site> ForEmail(string email)
    {
        using var c = Open(); using var q = c.CreateCommand(); q.CommandText = $"SELECT {Cols} FROM sites WHERE email=$e ORDER BY created_ms"; q.Parameters.AddWithValue("$e", EntitlementStore.NormalizeEmail(email));
        using var r = q.ExecuteReader(); var list = new List<Site>(); while (r.Read()) list.Add(Row(r)); return list;
    }
    public Site? Get(string id, string email) => ForEmail(email).FirstOrDefault(s => s.Id == id);

    /// <summary>Create (id empty) or update one of the account's sites. Returns null with a reason when the cap is hit.</summary>
    public Site? Upsert(string email, string? id, string name, double lat, double lon, string activity, bool alerts, bool digest, string thresholds, out string? error)
    {
        error = null; email = EntitlementStore.NormalizeEmail(email);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var mine = ForEmail(email);
        var existing = string.IsNullOrEmpty(id) ? null : mine.FirstOrDefault(s => s.Id == id);
        if (existing is null && mine.Count >= MaxPerAccount) { error = $"You can save up to {MaxPerAccount} places."; return null; }
        using var c = Open();
        if (existing is null)
        {
            id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            Exec(c, "INSERT INTO sites(id,email,name,lat,lon,activity,alerts,digest,thresholds,created_ms,updated_ms) VALUES($id,$e,$n,$lat,$lon,$a,$al,$dg,$th,$now,$now)",
                ("$id", id), ("$e", email), ("$n", name), ("$lat", lat), ("$lon", lon), ("$a", activity), ("$al", alerts ? 1 : 0), ("$dg", digest ? 1 : 0), ("$th", thresholds), ("$now", now));
        }
        else
        {
            bool moved = Math.Abs(existing.Lat - lat) > 0.0005 || Math.Abs(existing.Lon - lon) > 0.0005;
            Exec(c, "UPDATE sites SET name=$n, lat=$lat, lon=$lon, activity=$a, alerts=$al, digest=$dg, thresholds=$th, updated_ms=$now" + (moved ? ", last_state='{}'" : "") + " WHERE id=$id AND email=$e",
                ("$n", name), ("$lat", lat), ("$lon", lon), ("$a", activity), ("$al", alerts ? 1 : 0), ("$dg", digest ? 1 : 0), ("$th", thresholds), ("$now", now), ("$id", id), ("$e", email));
        }
        return Get(id!, email);
    }
    public void SetState(string id, string json) { using var c = Open(); Exec(c, "UPDATE sites SET last_state=$s WHERE id=$id", ("$s", json), ("$id", id)); }
    public void Delete(string id, string email) { using var c = Open(); Exec(c, "DELETE FROM sites WHERE id=$id AND email=$e", ("$id", id), ("$e", EntitlementStore.NormalizeEmail(email))); }
    public void DeleteFor(string email) { using var c = Open(); Exec(c, "DELETE FROM sites WHERE email=$e", ("$e", EntitlementStore.NormalizeEmail(email))); }
    public int Count() { using var c = Open(); using var q = c.CreateCommand(); q.CommandText = "SELECT COUNT(*) FROM sites"; return Convert.ToInt32(q.ExecuteScalar()); }

    public static Dictionary<string, double> ParseThresholds(string json)
    {
        var d = new Dictionary<string, double>();
        try { using var doc = JsonDocument.Parse(json); foreach (var p in doc.RootElement.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.Number) d[p.Name] = p.Value.GetDouble(); } catch { }
        return d;
    }
}
