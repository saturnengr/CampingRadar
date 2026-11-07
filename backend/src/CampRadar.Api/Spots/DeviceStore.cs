using Microsoft.Data.Sqlite;

namespace CampRadar.Api.Spots;

/// <summary>
/// Anonymous usage record: one row per app install, refreshed by a small ping when the app is open.
/// Holds a random id the app made up, coarse location (rounded to ~7 miles), activity, language, platform and
/// version — enough to answer "who is using this and where" for the admin page, nothing that identifies a person.
/// </summary>
public sealed record DeviceRow(string Id, double Lat, double Lon, string Activity, string Lang, string Platform, string Version, bool Push, long FirstMs, long LastMs, int Opens);

public sealed class DeviceStore
{
    private readonly string _cs;
    public DeviceStore(IConfiguration cfg)
    {
        var dir = cfg["DataDir"] ?? "App_Data";
        Directory.CreateDirectory(dir);
        _cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, "campradar.db"), Cache = SqliteCacheMode.Shared }.ToString();
        using var c = new SqliteConnection(_cs); c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS devices(
              id TEXT PRIMARY KEY, lat REAL NOT NULL, lon REAL NOT NULL, activity TEXT NOT NULL DEFAULT '', lang TEXT NOT NULL DEFAULT '',
              platform TEXT NOT NULL DEFAULT '', version TEXT NOT NULL DEFAULT '', push INTEGER NOT NULL DEFAULT 0,
              first_ms INTEGER NOT NULL, last_ms INTEGER NOT NULL, opens INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS devices_last ON devices(last_ms);
            """;
        cmd.ExecuteNonQuery();
    }

    public void Ping(string id, double lat, double lon, string activity, string lang, string platform, string version, bool push, bool opened)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var c = new SqliteConnection(_cs); c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO devices(id, lat, lon, activity, lang, platform, version, push, first_ms, last_ms, opens)
            VALUES($id, $lat, $lon, $act, $lang, $plat, $ver, $push, $now, $now, $open)
            ON CONFLICT(id) DO UPDATE SET lat=$lat, lon=$lon, activity=$act, lang=$lang, platform=$plat, version=$ver, push=$push, last_ms=$now, opens=opens+$open
            """;
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$lat", Math.Round(lat, 1)); cmd.Parameters.AddWithValue("$lon", Math.Round(lon, 1));
        cmd.Parameters.AddWithValue("$act", activity); cmd.Parameters.AddWithValue("$lang", lang); cmd.Parameters.AddWithValue("$plat", platform); cmd.Parameters.AddWithValue("$ver", version);
        cmd.Parameters.AddWithValue("$push", push ? 1 : 0); cmd.Parameters.AddWithValue("$now", now); cmd.Parameters.AddWithValue("$open", opened ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public List<DeviceRow> Recent(int days)
    {
        long since = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeMilliseconds();
        var list = new List<DeviceRow>();
        using var c = new SqliteConnection(_cs); c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, lat, lon, activity, lang, platform, version, push, first_ms, last_ms, opens FROM devices WHERE last_ms >= $since ORDER BY last_ms DESC";
        cmd.Parameters.AddWithValue("$since", since);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new DeviceRow(r.GetString(0), r.GetDouble(1), r.GetDouble(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetInt32(7) == 1, r.GetInt64(8), r.GetInt64(9), r.GetInt32(10)));
        return list;
    }

    public int Prune(int days)
    {
        using var c = new SqliteConnection(_cs); c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM devices WHERE last_ms < $cut";
        cmd.Parameters.AddWithValue("$cut", DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeMilliseconds());
        return cmd.ExecuteNonQuery();
    }
}
