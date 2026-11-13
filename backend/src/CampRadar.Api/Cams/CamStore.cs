using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CampRadar.Api.Cams;

/// <summary>A public webcam in our own directory: where it is, how to fetch a frame, and how it has behaved.</summary>
public sealed class Cam
{
    public string Id { get; set; } = "";          // "otcm:<hash>" or "iem:<cid>"
    public string Source { get; set; } = "";      // otcm | iem
    public string Name { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
    public double? HeadingDeg { get; set; }
    public string Url { get; set; } = "";         // snapshot URL (a JPEG), possibly with a time pattern for IEM
    public int RefreshSec { get; set; } = 300;
    public string Licence { get; set; } = "";
    public long FirstSeenMs { get; set; }
    public long LastSeenMs { get; set; }          // last time the source registry listed it
    public long LastGoodMs { get; set; }          // last frame that passed the pre-checks
    public string? LastHash { get; set; }
    public int Attempts { get; set; }
    public int Good { get; set; }
    public string? RefHash { get; set; }
    public long RefMs { get; set; }
    public string Cues { get; set; } = "";        // comma list the classifier tagged: sky,pavement,flag,trees,water,markers
    public double Quality { get; set; }           // 0..1, good/attempts with a prior
    public bool Retired { get; set; }
}

/// <summary>One classified frame.</summary>
public sealed class CamObs
{
    public string CamId { get; set; } = "";
    public long TMs { get; set; }                 // when we looked
    public long ImgMs { get; set; }               // the frame's own time when known (IEM), else TMs
    public bool Stale { get; set; }
    public string Day { get; set; } = "";         // day | night | twilight
    public string Precip { get; set; } = "";      // none | rain | snow | mixed | unsure
    public string Intensity { get; set; } = "";   // none | light | heavy
    public string Ground { get; set; } = "";      // dry | wet | puddled | snow | ice | unsure | n/a
    public string Vis { get; set; } = "";         // clear | reduced | fog | dense | unsure
    public string Sky { get; set; } = "";         // clear | partly | overcast | lowdark | towering | shaft | unsure | n/a
    public string Lens { get; set; } = "";        // ok | drops | obstructed | dirty | dark
    public string Wind { get; set; } = "";        // calm | breezy | strong | severe | unsure | n/a
    public double Conf { get; set; }
    public string Model { get; set; } = "";
    public double? RadarDbz { get; set; }         // what radar said over the camera at the time
    public string? StatusLevel { get; set; }      // our status level at the target this camera was looked at for
    public string Target { get; set; } = "";      // recording name or spot key that triggered the look
    public string Json { get; set; } = "{}";      // the model's full answer
    public string? Frame { get; set; }            // file name of the saved frame under <DataDir>/cams, kept a few days for review
}

public sealed class CamStore
{
    private readonly string _cs;
    public CamStore(IConfiguration cfg)
    {
        var dir = cfg["DataDir"] ?? "App_Data"; Directory.CreateDirectory(dir);
        _cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, "sites.db") }.ToString();
        using var c = Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS cams(
              id TEXT PRIMARY KEY, source TEXT NOT NULL, name TEXT NOT NULL, lat REAL NOT NULL, lon REAL NOT NULL, heading REAL,
              url TEXT NOT NULL, refresh_s INTEGER NOT NULL DEFAULT 300, licence TEXT NOT NULL DEFAULT '',
              first_seen_ms INTEGER NOT NULL, last_seen_ms INTEGER NOT NULL, last_good_ms INTEGER NOT NULL DEFAULT 0, last_hash TEXT,
              attempts INTEGER NOT NULL DEFAULT 0, good INTEGER NOT NULL DEFAULT 0, ref_hash TEXT, ref_ms INTEGER NOT NULL DEFAULT 0,
              cues TEXT NOT NULL DEFAULT '', quality REAL NOT NULL DEFAULT 0.5, retired INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS cams_lat ON cams(lat);
            CREATE TABLE IF NOT EXISTS cam_obs(
              cam_id TEXT NOT NULL, t_ms INTEGER NOT NULL, img_ms INTEGER NOT NULL, stale INTEGER NOT NULL, day TEXT NOT NULL,
              precip TEXT NOT NULL, intensity TEXT NOT NULL, ground TEXT NOT NULL, vis TEXT NOT NULL, sky TEXT NOT NULL, lens TEXT NOT NULL, wind TEXT NOT NULL,
              conf REAL NOT NULL, model TEXT NOT NULL, radar_dbz REAL, status_level TEXT, target TEXT NOT NULL, json TEXT NOT NULL,
              PRIMARY KEY(cam_id, t_ms));
            CREATE INDEX IF NOT EXISTS cam_obs_t ON cam_obs(t_ms);
            """);
        try { Exec(c, "ALTER TABLE cam_obs ADD COLUMN frame TEXT"); } catch (SqliteException) { }
        FramesDir = Path.Combine(dir, "cams"); Directory.CreateDirectory(FramesDir);
    }

    public string FramesDir { get; }

    /// <summary>Keep the frame that produced a verdict so a person can check the classifier; pruned after <paramref name="keepDays"/>.</summary>
    public string SaveFrame(string camId, long tMs, byte[] bytes, string contentType)
    {
        var ext = contentType.Contains("png") ? ".png" : contentType.Contains("gif") ? ".gif" : ".jpg";
        var name = tMs + "_" + camId.Replace(':', '_').Replace('/', '_') + ext;
        File.WriteAllBytes(Path.Combine(FramesDir, name), bytes); return name;
    }
    public string? FramePath(string name) { if (name.Contains("..") || name.Contains('/') || name.Contains('\\')) return null; var p = Path.Combine(FramesDir, name); return File.Exists(p) ? p : null; }
    public int PruneFrames(int keepDays = 7)
    {
        int n = 0; var cutoff = DateTime.UtcNow.AddDays(-keepDays);
        foreach (var f in Directory.EnumerateFiles(FramesDir)) { try { if (File.GetLastWriteTimeUtc(f) < cutoff) { File.Delete(f); n++; } } catch { } }
        return n;
    }

    private SqliteConnection Open() { var c = new SqliteConnection(_cs); c.Open(); return c; }
    private static void Exec(SqliteConnection c, string sql) { using var q = c.CreateCommand(); q.CommandText = sql; q.ExecuteNonQuery(); }

    /// <summary>Insert or refresh a camera from a source registry; keeps our own behaviour fields.</summary>
    public int UpsertMany(IEnumerable<Cam> cams)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); int n = 0;
        using var c = Open(); using var tx = c.BeginTransaction();
        using var q = c.CreateCommand();
        q.CommandText = """
            INSERT INTO cams(id,source,name,lat,lon,heading,url,refresh_s,licence,first_seen_ms,last_seen_ms)
            VALUES($id,$src,$name,$lat,$lon,$hdg,$url,$ref,$lic,$now,$now)
            ON CONFLICT(id) DO UPDATE SET name=excluded.name, lat=excluded.lat, lon=excluded.lon, heading=COALESCE(excluded.heading, cams.heading),
              url=excluded.url, refresh_s=excluded.refresh_s, licence=excluded.licence, last_seen_ms=excluded.last_seen_ms, retired=0
            """;
        var p = new Dictionary<string, SqliteParameter>();
        foreach (var k in new[] { "$id", "$src", "$name", "$lat", "$lon", "$hdg", "$url", "$ref", "$lic", "$now" }) { var sp = q.CreateParameter(); sp.ParameterName = k; q.Parameters.Add(sp); p[k] = sp; }
        foreach (var cam in cams)
        {
            p["$id"].Value = cam.Id; p["$src"].Value = cam.Source; p["$name"].Value = cam.Name; p["$lat"].Value = cam.Lat; p["$lon"].Value = cam.Lon;
            p["$hdg"].Value = (object?)cam.HeadingDeg ?? DBNull.Value; p["$url"].Value = cam.Url; p["$ref"].Value = cam.RefreshSec; p["$lic"].Value = cam.Licence; p["$now"].Value = now;
            n += q.ExecuteNonQuery();
        }
        tx.Commit(); return n;
    }

    /// <summary>Cameras a source stopped listing: retired, never deleted.</summary>
    public int RetireMissing(string source, long seenBeforeMs)
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = "UPDATE cams SET retired=1 WHERE source=$s AND last_seen_ms<$t AND retired=0";
        q.Parameters.AddWithValue("$s", source); q.Parameters.AddWithValue("$t", seenBeforeMs); return q.ExecuteNonQuery();
    }

    public List<Cam> Near(double lat, double lon, double radiusMi, int limit = 5, bool includeRetired = false)
    {
        double dLat = radiusMi / 69.0, dLon = radiusMi / (69.0 * Math.Max(0.2, Math.Cos(lat * Math.PI / 180)));
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = "SELECT * FROM cams WHERE lat BETWEEN $a AND $b AND lon BETWEEN $c AND $d" + (includeRetired ? "" : " AND retired=0");
        q.Parameters.AddWithValue("$a", lat - dLat); q.Parameters.AddWithValue("$b", lat + dLat); q.Parameters.AddWithValue("$c", lon - dLon); q.Parameters.AddWithValue("$d", lon + dLon);
        var list = new List<(double d, Cam cam)>();
        using var r = q.ExecuteReader();
        while (r.Read()) { var cam = Read(r); var d = Haversine(lat, lon, cam.Lat, cam.Lon); if (d <= radiusMi) list.Add((d, cam)); }
        // quality first within the radius, then distance: a reliable camera 6 mi out beats a dead one 2 mi out
        return list.OrderByDescending(x => x.cam.Quality >= 0.3).ThenBy(x => x.d).Take(limit).Select(x => x.cam).ToList();
    }

    public Cam? Get(string id)
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = "SELECT * FROM cams WHERE id=$id"; q.Parameters.AddWithValue("$id", id);
        using var r = q.ExecuteReader(); return r.Read() ? Read(r) : null;
    }

    public void RecordFetch(string id, bool good, string? hash)
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = """
            UPDATE cams SET attempts=attempts+1, good=good+$g, last_hash=COALESCE($h,last_hash), last_good_ms=CASE WHEN $g=1 THEN $now ELSE last_good_ms END,
              quality=(good+$g+1.0)/(attempts+1+2.0) WHERE id=$id
            """;
        q.Parameters.AddWithValue("$g", good ? 1 : 0); q.Parameters.AddWithValue("$h", (object?)hash ?? DBNull.Value);
        q.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); q.Parameters.AddWithValue("$id", id); q.ExecuteNonQuery();
    }

    public void SetReference(string id, string hash, string cues)
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = "UPDATE cams SET ref_hash=$h, ref_ms=$now, cues=$cues WHERE id=$id";
        q.Parameters.AddWithValue("$h", hash); q.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); q.Parameters.AddWithValue("$cues", cues); q.Parameters.AddWithValue("$id", id); q.ExecuteNonQuery();
    }

    public void AddObs(CamObs o)
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = """
            INSERT OR REPLACE INTO cam_obs(cam_id,t_ms,img_ms,stale,day,precip,intensity,ground,vis,sky,lens,wind,conf,model,radar_dbz,status_level,target,json,frame)
            VALUES($cam,$t,$img,$stale,$day,$precip,$int,$ground,$vis,$sky,$lens,$wind,$conf,$model,$dbz,$lvl,$target,$json,$frame)
            """;
        q.Parameters.AddWithValue("$frame", (object?)o.Frame ?? DBNull.Value);
        q.Parameters.AddWithValue("$cam", o.CamId); q.Parameters.AddWithValue("$t", o.TMs); q.Parameters.AddWithValue("$img", o.ImgMs); q.Parameters.AddWithValue("$stale", o.Stale ? 1 : 0);
        q.Parameters.AddWithValue("$day", o.Day); q.Parameters.AddWithValue("$precip", o.Precip); q.Parameters.AddWithValue("$int", o.Intensity); q.Parameters.AddWithValue("$ground", o.Ground);
        q.Parameters.AddWithValue("$vis", o.Vis); q.Parameters.AddWithValue("$sky", o.Sky); q.Parameters.AddWithValue("$lens", o.Lens); q.Parameters.AddWithValue("$wind", o.Wind);
        q.Parameters.AddWithValue("$conf", o.Conf); q.Parameters.AddWithValue("$model", o.Model); q.Parameters.AddWithValue("$dbz", (object?)o.RadarDbz ?? DBNull.Value);
        q.Parameters.AddWithValue("$lvl", (object?)o.StatusLevel ?? DBNull.Value); q.Parameters.AddWithValue("$target", o.Target); q.Parameters.AddWithValue("$json", o.Json);
        q.ExecuteNonQuery();
    }

    public long LastObsMs(string camId)
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = "SELECT COALESCE(MAX(t_ms),0) FROM cam_obs WHERE cam_id=$id"; q.Parameters.AddWithValue("$id", camId);
        return Convert.ToInt64(q.ExecuteScalar());
    }

    public int ObsCountSince(long ms)
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM cam_obs WHERE t_ms>=$t AND stale=0"; q.Parameters.AddWithValue("$t", ms);
        return Convert.ToInt32(q.ExecuteScalar());
    }

    public List<CamObs> Obs(string? camId, long fromMs, int limit = 500)
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = "SELECT cam_id,t_ms,img_ms,stale,day,precip,intensity,ground,vis,sky,lens,wind,conf,model,radar_dbz,status_level,target,json,frame FROM cam_obs WHERE t_ms>=$t" + (camId is null ? "" : " AND cam_id=$id") + " ORDER BY t_ms DESC LIMIT $n";
        q.Parameters.AddWithValue("$t", fromMs); if (camId is not null) q.Parameters.AddWithValue("$id", camId); q.Parameters.AddWithValue("$n", limit);
        var list = new List<CamObs>(); using var r = q.ExecuteReader();
        while (r.Read()) list.Add(new CamObs
        {
            CamId = r.GetString(0), TMs = r.GetInt64(1), ImgMs = r.GetInt64(2), Stale = r.GetInt32(3) == 1, Day = r.GetString(4), Precip = r.GetString(5), Intensity = r.GetString(6),
            Ground = r.GetString(7), Vis = r.GetString(8), Sky = r.GetString(9), Lens = r.GetString(10), Wind = r.GetString(11), Conf = r.GetDouble(12), Model = r.GetString(13),
            RadarDbz = r.IsDBNull(14) ? null : r.GetDouble(14), StatusLevel = r.IsDBNull(15) ? null : r.GetString(15), Target = r.GetString(16), Json = r.GetString(17), Frame = r.IsDBNull(18) ? null : r.GetString(18)
        });
        return list;
    }

    /// <summary>Latest non-stale verdict per camera, for colouring the map.</summary>
    public Dictionary<string, CamObs> LatestByCam(long sinceMs)
    {
        var d = new Dictionary<string, CamObs>();
        foreach (var o in Obs(null, sinceMs, 5000)) if (!o.Stale && !d.ContainsKey(o.CamId)) d[o.CamId] = o;
        return d;
    }

    public object Stats()
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = "SELECT source, COUNT(*), SUM(retired), SUM(CASE WHEN last_good_ms>0 THEN 1 ELSE 0 END), ROUND(AVG(quality),2) FROM cams GROUP BY source";
        var rows = new List<object>(); using var r = q.ExecuteReader();
        while (r.Read()) rows.Add(new { source = r.GetString(0), cams = r.GetInt32(1), retired = r.GetInt32(2), everGood = r.GetInt32(3), quality = r.GetDouble(4) });
        return rows;
    }

    private static Cam Read(SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")), Source = r.GetString(r.GetOrdinal("source")), Name = r.GetString(r.GetOrdinal("name")),
        Lat = r.GetDouble(r.GetOrdinal("lat")), Lon = r.GetDouble(r.GetOrdinal("lon")), HeadingDeg = r.IsDBNull(r.GetOrdinal("heading")) ? null : r.GetDouble(r.GetOrdinal("heading")),
        Url = r.GetString(r.GetOrdinal("url")), RefreshSec = r.GetInt32(r.GetOrdinal("refresh_s")), Licence = r.GetString(r.GetOrdinal("licence")),
        FirstSeenMs = r.GetInt64(r.GetOrdinal("first_seen_ms")), LastSeenMs = r.GetInt64(r.GetOrdinal("last_seen_ms")), LastGoodMs = r.GetInt64(r.GetOrdinal("last_good_ms")),
        LastHash = r.IsDBNull(r.GetOrdinal("last_hash")) ? null : r.GetString(r.GetOrdinal("last_hash")), Attempts = r.GetInt32(r.GetOrdinal("attempts")), Good = r.GetInt32(r.GetOrdinal("good")),
        RefHash = r.IsDBNull(r.GetOrdinal("ref_hash")) ? null : r.GetString(r.GetOrdinal("ref_hash")), RefMs = r.GetInt64(r.GetOrdinal("ref_ms")), Cues = r.GetString(r.GetOrdinal("cues")),
        Quality = r.GetDouble(r.GetOrdinal("quality")), Retired = r.GetInt32(r.GetOrdinal("retired")) == 1
    };

    public static double Haversine(double lat1, double lon1, double lat2, double lon2)
    {
        double R = 3958.8, dLat = (lat2 - lat1) * Math.PI / 180, dLon = (lon2 - lon1) * Math.PI / 180;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Sqrt(a));
    }
}
