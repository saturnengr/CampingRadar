using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CampRadar.Api.Spots;

/// <summary>
/// Households: a Pro owner invites family and friends; members get Pro features through the owner while the owner's
/// Pro is active, can share their location for a chosen number of hours (off by default, always with an end time),
/// and exchange short messages. Positions and messages are kept 24 hours, then deleted.
/// </summary>
public sealed record Member(string Email, string Role, string Name, bool ShareLocation, long ShareUntilMs, long JoinedMs);
public sealed record GroupInfo(string Id, string OwnerEmail, string Name, long CreatedMs);
public sealed record Position(string Email, string DeviceId, double Lat, double Lon, double AccM, long Ms);
public sealed record Message(long Id, string GroupId, string FromEmail, string FromName, string Kind, string Text, string DataJson, long Ms, long? ReplyTo);

public sealed class GroupStore
{
    private readonly string _cs;
    public GroupStore(IConfiguration cfg)
    {
        var dir = cfg["DataDir"] ?? "App_Data";
        Directory.CreateDirectory(dir);
        _cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, "campradar.db") }.ToString();
        using var c = Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS groups(id TEXT PRIMARY KEY, owner_email TEXT NOT NULL UNIQUE, name TEXT NOT NULL DEFAULT '', created_ms INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS members(group_id TEXT NOT NULL, email TEXT NOT NULL, role TEXT NOT NULL, name TEXT NOT NULL DEFAULT '',
              share_location INTEGER NOT NULL DEFAULT 0, share_until_ms INTEGER NOT NULL DEFAULT 0, joined_ms INTEGER NOT NULL, PRIMARY KEY(group_id, email));
            CREATE INDEX IF NOT EXISTS members_email ON members(email);
            CREATE TABLE IF NOT EXISTS invites(code TEXT PRIMARY KEY, group_id TEXT NOT NULL, email TEXT NOT NULL DEFAULT '', expires_ms INTEGER NOT NULL, used_ms INTEGER NOT NULL DEFAULT 0, created_ms INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS positions(email TEXT NOT NULL, device_id TEXT NOT NULL, lat REAL NOT NULL, lon REAL NOT NULL, acc_m REAL NOT NULL DEFAULT 0, ms INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS positions_email_ms ON positions(email, ms);
            CREATE TABLE IF NOT EXISTS messages(id INTEGER PRIMARY KEY AUTOINCREMENT, group_id TEXT NOT NULL, from_email TEXT NOT NULL, from_name TEXT NOT NULL DEFAULT '',
              kind TEXT NOT NULL DEFAULT 'text', text TEXT NOT NULL, data TEXT NOT NULL DEFAULT '{}', ms INTEGER NOT NULL, reply_to INTEGER, reply_token TEXT NOT NULL DEFAULT '');
            CREATE INDEX IF NOT EXISTS messages_group_ms ON messages(group_id, ms);
            """);
    }

    private SqliteConnection Open() { var c = new SqliteConnection(_cs); c.Open(); using (var p = c.CreateCommand()) { p.CommandText = "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL;"; p.ExecuteNonQuery(); } return c; }
    private static void Exec(SqliteConnection c, string sql, params (string, object?)[] p) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value); cmd.ExecuteNonQuery(); }
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static string N(string e) => EntitlementStore.NormalizeEmail(e);

    // ---- groups ----
    /// <summary>The group this email belongs to (as owner or member), if any.</summary>
    public GroupInfo? GroupOf(string email)
    {
        email = N(email);
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT g.id, g.owner_email, g.name, g.created_ms FROM groups g JOIN members m ON m.group_id = g.id WHERE m.email = $e LIMIT 1";
        cmd.Parameters.AddWithValue("$e", email);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new GroupInfo(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3)) : null;
    }
    /// <summary>Owner's group, created on first use.</summary>
    public GroupInfo EnsureOwnerGroup(string ownerEmail, string ownerName)
    {
        ownerEmail = N(ownerEmail);
        var existing = GroupOf(ownerEmail);
        if (existing is not null) return existing;
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        using var c = Open();
        Exec(c, "INSERT INTO groups(id, owner_email, name, created_ms) VALUES($id, $e, $n, $now)", ("$id", id), ("$e", ownerEmail), ("$n", ""), ("$now", Now));
        Exec(c, "INSERT INTO members(group_id, email, role, name, joined_ms) VALUES($g, $e, 'owner', $n, $now)", ("$g", id), ("$e", ownerEmail), ("$n", ownerName), ("$now", Now));
        return new GroupInfo(id, ownerEmail, "", Now);
    }
    public List<Member> Members(string groupId)
    {
        var list = new List<Member>();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT email, role, name, share_location, share_until_ms, joined_ms FROM members WHERE group_id=$g ORDER BY role DESC, joined_ms";
        cmd.Parameters.AddWithValue("$g", groupId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new Member(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3) == 1 && r.GetInt64(4) > Now, r.GetInt64(4), r.GetInt64(5)));
        return list;
    }
    public Member? MemberOf(string groupId, string email) => Members(groupId).FirstOrDefault(m => m.Email == N(email));

    // ---- invites ----
    public string NewInvite(string groupId, string? forEmail)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var code = new string(RandomNumberGenerator.GetBytes(6).Select(b => alphabet[b % alphabet.Length]).ToArray());
        using var c = Open();
        Exec(c, "INSERT INTO invites(code, group_id, email, expires_ms, created_ms) VALUES($c, $g, $e, $x, $n)", ("$c", code), ("$g", groupId), ("$e", forEmail is null ? "" : N(forEmail)), ("$x", DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeMilliseconds()), ("$n", Now));
        return code;
    }
    /// <summary>Accept an invite: the signed-in email joins the invite's group. Returns the group, or null if the code is bad.</summary>
    public (GroupInfo? Group, string? Error) Join(string code, string email, string name)
    {
        code = code.Trim().ToUpperInvariant().Replace(" ", "").Replace("-", ""); email = N(email);
        using var c = Open();
        string? groupId = null, forEmail = null;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT group_id, email FROM invites WHERE code=$c AND used_ms=0 AND expires_ms > $n";
            cmd.Parameters.AddWithValue("$c", code); cmd.Parameters.AddWithValue("$n", Now);
            using var r = cmd.ExecuteReader(); if (r.Read()) { groupId = r.GetString(0); forEmail = r.GetString(1); }
        }
        if (groupId is null) return (null, "That invite code is not valid or has expired.");
        if (forEmail!.Length > 0 && forEmail != email) return (null, "That invite was sent to a different email address.");
        if (GroupOf(email) is { } already && already.Id != groupId) return (null, "You are already in another group. Leave it first.");
        Exec(c, "UPDATE invites SET used_ms=$n WHERE code=$c", ("$n", Now), ("$c", code));
        Exec(c, "INSERT OR IGNORE INTO members(group_id, email, role, name, joined_ms) VALUES($g, $e, 'member', $name, $n)", ("$g", groupId), ("$e", email), ("$name", name), ("$n", Now));
        using var q = c.CreateCommand(); q.CommandText = "SELECT id, owner_email, name, created_ms FROM groups WHERE id=$g"; q.Parameters.AddWithValue("$g", groupId);
        using var rr = q.ExecuteReader(); rr.Read();
        return (new GroupInfo(rr.GetString(0), rr.GetString(1), rr.GetString(2), rr.GetInt64(3)), null);
    }
    public void Remove(string groupId, string email)
    {
        email = N(email);
        using var c = Open();
        Exec(c, "DELETE FROM members WHERE group_id=$g AND email=$e AND role<>'owner'", ("$g", groupId), ("$e", email));
        Exec(c, "DELETE FROM positions WHERE email=$e", ("$e", email));
    }
    public void SetSharing(string groupId, string email, bool on, int hours, string? name)
    {
        email = N(email);
        using var c = Open();
        Exec(c, "UPDATE members SET share_location=$s, share_until_ms=$u" + (name is null ? "" : ", name=$name") + " WHERE group_id=$g AND email=$e",
            ("$s", on ? 1 : 0), ("$u", on ? DateTimeOffset.UtcNow.AddHours(Math.Clamp(hours, 1, 72)).ToUnixTimeMilliseconds() : 0), ("$name", name ?? ""), ("$g", groupId), ("$e", email));
        if (!on) Exec(c, "DELETE FROM positions WHERE email=$e", ("$e", email));
    }

    // ---- positions (only while sharing; the endpoint checks) ----
    public void AddPosition(string email, string deviceId, double lat, double lon, double accM)
    {
        using var c = Open();
        Exec(c, "INSERT INTO positions(email, device_id, lat, lon, acc_m, ms) VALUES($e, $d, $lat, $lon, $a, $n)", ("$e", N(email)), ("$d", deviceId), ("$lat", lat), ("$lon", lon), ("$a", accM), ("$n", Now));
    }
    /// <summary>Latest position per sharing member plus a thinned 24-hour trail.</summary>
    public List<object> Positions(string groupId)
    {
        var members = Members(groupId).Where(m => m.ShareLocation).ToList();
        var outList = new List<object>();
        using var c = Open();
        foreach (var m in members)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT lat, lon, acc_m, ms, device_id FROM positions WHERE email=$e AND ms > $t ORDER BY ms";
            cmd.Parameters.AddWithValue("$e", m.Email); cmd.Parameters.AddWithValue("$t", Now - 86400000L);
            using var r = cmd.ExecuteReader();
            var trail = new List<double[]>(); double[]? last = null; long lastMs = 0; string dev = "";
            while (r.Read()) { var p = new[] { Math.Round(r.GetDouble(0), 5), Math.Round(r.GetDouble(1), 5), r.GetInt64(3) }; trail.Add(p); last = p; lastMs = r.GetInt64(3); dev = r.GetString(4); }
            if (last is null) continue;
            // thin the trail to ~200 points
            if (trail.Count > 200) { int step = (int)Math.Ceiling(trail.Count / 200.0); trail = trail.Where((_, i) => i % step == 0 || i == trail.Count - 1).ToList(); }
            outList.Add(new { email = m.Email, name = m.Name, lat = last[0], lon = last[1], ms = lastMs, shareUntilMs = m.ShareUntilMs, trail });
        }
        return outList;
    }

    // ---- messages ----
    public Message AddMessage(string groupId, string fromEmail, string fromName, string kind, string text, string dataJson, long? replyTo)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        using var c = Open();
        Exec(c, "INSERT INTO messages(group_id, from_email, from_name, kind, text, data, ms, reply_to, reply_token) VALUES($g, $e, $n, $k, $t, $d, $ms, $r, $tok)",
            ("$g", groupId), ("$e", N(fromEmail)), ("$n", fromName), ("$k", kind), ("$t", text), ("$d", dataJson), ("$ms", Now), ("$r", replyTo), ("$tok", token));
        using var q = c.CreateCommand(); q.CommandText = "SELECT last_insert_rowid()"; long id = (long)q.ExecuteScalar()!;
        return new Message(id, groupId, N(fromEmail), fromName, kind, text, dataJson, Now, replyTo);
    }
    public string? ReplyToken(long messageId)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT reply_token FROM messages WHERE id=$id"; cmd.Parameters.AddWithValue("$id", messageId);
        return cmd.ExecuteScalar() as string;
    }
    public Message? MessageById(long id)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT id, group_id, from_email, from_name, kind, text, data, ms, reply_to FROM messages WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader(); return r.Read() ? Row(r) : null;
    }
    public Message? MessageByToken(string token)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT id, group_id, from_email, from_name, kind, text, data, ms, reply_to FROM messages WHERE reply_token=$t AND ms > $cut"; cmd.Parameters.AddWithValue("$t", token); cmd.Parameters.AddWithValue("$cut", Now - 86400000L);
        using var r = cmd.ExecuteReader(); return r.Read() ? Row(r) : null;
    }
    public List<Message> Messages(string groupId, long sinceMs)
    {
        var list = new List<Message>();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, group_id, from_email, from_name, kind, text, data, ms, reply_to FROM messages WHERE group_id=$g AND ms > $s ORDER BY ms LIMIT 200";
        cmd.Parameters.AddWithValue("$g", groupId); cmd.Parameters.AddWithValue("$s", Math.Max(sinceMs, Now - 86400000L));
        using var r = cmd.ExecuteReader(); while (r.Read()) list.Add(Row(r));
        return list;
    }
    private static Message Row(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetInt64(7), r.IsDBNull(8) ? null : r.GetInt64(8));
    public int RecentMessageCount(string email, int minutes)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM messages WHERE from_email=$e AND ms > $t"; cmd.Parameters.AddWithValue("$e", N(email)); cmd.Parameters.AddWithValue("$t", Now - minutes * 60000L);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>Retention: positions and messages older than 24 h, expired sharing, used/expired invites.</summary>
    public void Prune()
    {
        using var c = Open();
        Exec(c, "DELETE FROM positions WHERE ms < $t", ("$t", Now - 86400000L));
        Exec(c, "DELETE FROM messages WHERE ms < $t", ("$t", Now - 86400000L));
        Exec(c, "UPDATE members SET share_location=0 WHERE share_location=1 AND share_until_ms < $n", ("$n", Now));
        Exec(c, "DELETE FROM invites WHERE used_ms > 0 OR expires_ms < $n", ("$n", Now));
    }
    public void DeleteEverythingFor(string email)
    {
        email = N(email);
        using var c = Open();
        var g = GroupOf(email);
        if (g is not null && g.OwnerEmail == email) { Exec(c, "DELETE FROM members WHERE group_id=$g", ("$g", g.Id)); Exec(c, "DELETE FROM messages WHERE group_id=$g", ("$g", g.Id)); Exec(c, "DELETE FROM invites WHERE group_id=$g", ("$g", g.Id)); Exec(c, "DELETE FROM groups WHERE id=$g", ("$g", g.Id)); }
        else Exec(c, "DELETE FROM members WHERE email=$e", ("$e", email));
        Exec(c, "DELETE FROM positions WHERE email=$e", ("$e", email));
    }
    /// <summary>Admin view: groups with member counts.</summary>
    public List<object> AdminSummary()
    {
        var list = new List<object>();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT g.id, g.owner_email, g.created_ms, (SELECT COUNT(*) FROM members m WHERE m.group_id=g.id), (SELECT COUNT(*) FROM members m WHERE m.group_id=g.id AND m.share_location=1 AND m.share_until_ms > $n), (SELECT COUNT(*) FROM messages x WHERE x.group_id=g.id AND x.ms > $d) FROM groups g";
        cmd.Parameters.AddWithValue("$n", Now); cmd.Parameters.AddWithValue("$d", Now - 86400000L);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new { id = r.GetString(0), owner = r.GetString(1), createdMs = r.GetInt64(2), members = r.GetInt32(3), sharing = r.GetInt32(4), messages24h = r.GetInt32(5) });
        return list;
    }
}
