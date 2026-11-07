using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace CampRadar.Api.Spots;

/// <summary>
/// Who has Pro. An entitlement is keyed by email (plan + expiry + where it came from: admin grant, Stripe, app store).
/// A device gets onto an entitlement by redeeming a short one-time activation code, which gives it a long-lived token.
/// Codes are what the admin page hands out today and what the storefront will email later; the app side is identical.
/// </summary>
public sealed record Entitlement(string Email, string Plan, long ExpiresMs, string Source, long CreatedMs, long UpdatedMs, string Note)
{
    public bool Active => Plan != "free" && ExpiresMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
public sealed record DeviceToken(string Token, string Email, string DeviceId, long CreatedMs, long LastMs);

public sealed class EntitlementStore
{
    private readonly string _cs;
    public EntitlementStore(IConfiguration cfg)
    {
        var dir = cfg["DataDir"] ?? "App_Data";
        Directory.CreateDirectory(dir);
        _cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, "campradar.db"), Cache = SqliteCacheMode.Shared }.ToString();
        using var c = Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS entitlements(email TEXT PRIMARY KEY, plan TEXT NOT NULL, expires_ms INTEGER NOT NULL, source TEXT NOT NULL,
              created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, note TEXT NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS codes(code TEXT PRIMARY KEY, email TEXT NOT NULL, expires_ms INTEGER NOT NULL, used_ms INTEGER NOT NULL DEFAULT 0, created_ms INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS tokens(token TEXT PRIMARY KEY, email TEXT NOT NULL, device_id TEXT NOT NULL, created_ms INTEGER NOT NULL, last_ms INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS tokens_email ON tokens(email);
            CREATE TABLE IF NOT EXISTS settings(email TEXT PRIMARY KEY, json TEXT NOT NULL, updated_ms INTEGER NOT NULL);
            """);
    }

    private SqliteConnection Open() { var c = new SqliteConnection(_cs); c.Open(); return c; }
    private static void Exec(SqliteConnection c, string sql, params (string, object)[] p) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v); cmd.ExecuteNonQuery(); }
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public static string NormalizeEmail(string e) => e.Trim().ToLowerInvariant();

    /// <summary>Create or extend an entitlement. Months add to the later of now and the current expiry.</summary>
    public Entitlement Grant(string email, string plan, int months, string source, string note)
    {
        email = NormalizeEmail(email);
        var cur = Get(email);
        long baseMs = Math.Max(Now, cur?.ExpiresMs ?? 0);
        long exp = months <= 0 ? Now : DateTimeOffset.FromUnixTimeMilliseconds(baseMs).AddMonths(months).ToUnixTimeMilliseconds();
        using var c = Open();
        Exec(c, """
            INSERT INTO entitlements(email, plan, expires_ms, source, created_ms, updated_ms, note) VALUES($e, $p, $x, $s, $n, $n, $note)
            ON CONFLICT(email) DO UPDATE SET plan=$p, expires_ms=$x, source=$s, updated_ms=$n, note=$note
            """, ("$e", email), ("$p", plan), ("$x", exp), ("$s", source), ("$n", Now), ("$note", note ?? ""));
        return Get(email)!;
    }

    public Entitlement? Get(string email)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT email, plan, expires_ms, source, created_ms, updated_ms, note FROM entitlements WHERE email=$e";
        cmd.Parameters.AddWithValue("$e", NormalizeEmail(email));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new Entitlement(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetInt64(4), r.GetInt64(5), r.GetString(6)) : null;
    }

    public List<(Entitlement E, int Devices)> All()
    {
        var list = new List<(Entitlement, int)>();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT e.email, e.plan, e.expires_ms, e.source, e.created_ms, e.updated_ms, e.note, (SELECT COUNT(*) FROM tokens t WHERE t.email=e.email) FROM entitlements e ORDER BY e.updated_ms DESC";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((new Entitlement(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetInt64(4), r.GetInt64(5), r.GetString(6)), r.GetInt32(7)));
        return list;
    }

    /// <summary>A short code the person types into the app; valid for `days`, single use.</summary>
    public string NewCode(string email, int days = 14)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";   // no 0/O/1/I
        var bytes = RandomNumberGenerator.GetBytes(8);
        var code = new string(bytes.Select(b => alphabet[b % alphabet.Length]).ToArray());
        code = code[..4] + "-" + code[4..];
        using var c = Open();
        Exec(c, "INSERT INTO codes(code, email, expires_ms, created_ms) VALUES($c, $e, $x, $n)", ("$c", code), ("$e", NormalizeEmail(email)), ("$x", DateTimeOffset.UtcNow.AddDays(days).ToUnixTimeMilliseconds()), ("$n", Now));
        return code;
    }

    /// <summary>Six-digit sign-in code, 10 minutes, bound to the email. Older unused login codes for the email are voided.</summary>
    public string NewLoginCode(string email)
    {
        email = NormalizeEmail(email);
        var code = RandomNumberGenerator.GetInt32(0, 1000000).ToString("D6");
        using var c = Open();
        Exec(c, "UPDATE codes SET used_ms=$n WHERE email=$e AND used_ms=0 AND length(code)=6", ("$n", Now), ("$e", email));
        Exec(c, "INSERT INTO codes(code, email, expires_ms, created_ms) VALUES($c, $e, $x, $n)", ("$c", email + ":" + code), ("$e", email), ("$x", DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds()), ("$n", Now));
        if (Get(email) is null) Grant(email, "free", 0, "signin", "");
        return code;
    }
    /// <summary>How many login codes were issued for this email in the last 10 minutes (rate limiting).</summary>
    public int RecentLoginCodes(string email)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM codes WHERE email=$e AND created_ms > $t AND code LIKE '%:%'";
        cmd.Parameters.AddWithValue("$e", NormalizeEmail(email)); cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds());
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
    /// <summary>Forget a login code whose email could not be sent, so it neither works nor counts toward the limit.</summary>
    public void VoidLoginCode(string email, string code) { using var c = Open(); Exec(c, "DELETE FROM codes WHERE code=$c", ("$c", NormalizeEmail(email) + ":" + code)); }
    public DeviceToken? Verify(string email, string code, string deviceId) => RedeemKey(NormalizeEmail(email) + ":" + code.Trim(), deviceId);

    public string? GetSettings(string email, out long updatedMs)
    {
        updatedMs = 0;
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json, updated_ms FROM settings WHERE email=$e"; cmd.Parameters.AddWithValue("$e", NormalizeEmail(email));
        using var r = cmd.ExecuteReader(); if (!r.Read()) return null; updatedMs = r.GetInt64(1); return r.GetString(0);
    }
    public long PutSettings(string email, string json)
    {
        using var c = Open(); long now = Now;
        Exec(c, "INSERT INTO settings(email, json, updated_ms) VALUES($e, $j, $n) ON CONFLICT(email) DO UPDATE SET json=$j, updated_ms=$n", ("$e", NormalizeEmail(email)), ("$j", json), ("$n", now));
        return now;
    }
    /// <summary>Delete everything held for an email: tokens, settings, codes, the entitlement row.</summary>
    public void DeleteAccount(string email)
    {
        email = NormalizeEmail(email);
        using var c = Open();
        foreach (var t in new[] { "tokens", "settings", "codes", "entitlements" }) Exec(c, $"DELETE FROM {t} WHERE email=$e", ("$e", email));
    }

    /// <summary>Exchange a code for a device token. Returns null when the code is unknown, used or expired.</summary>
    public DeviceToken? Redeem(string code, string deviceId)
    {
        code = code.Trim().ToUpperInvariant().Replace(" ", "");
        if (code.Length == 8) code = code[..4] + "-" + code[4..];
        return RedeemKey(code, deviceId);
    }
    private DeviceToken? RedeemKey(string code, string deviceId)
    {
        using var c = Open();
        string? email = null;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT email FROM codes WHERE code=$c AND used_ms=0 AND expires_ms > $n";
            cmd.Parameters.AddWithValue("$c", code); cmd.Parameters.AddWithValue("$n", Now);
            email = cmd.ExecuteScalar() as string;
        }
        if (email is null) return null;
        Exec(c, "UPDATE codes SET used_ms=$n WHERE code=$c", ("$n", Now), ("$c", code));
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        Exec(c, "INSERT INTO tokens(token, email, device_id, created_ms, last_ms) VALUES($t, $e, $d, $n, $n)", ("$t", token), ("$e", email), ("$d", deviceId), ("$n", Now));
        return new DeviceToken(token, email, deviceId, Now, Now);
    }

    public DeviceToken? Token(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT token, email, device_id, created_ms, last_ms FROM tokens WHERE token=$t";
        cmd.Parameters.AddWithValue("$t", token);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var t = new DeviceToken(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4));
        r.Close();
        if (Now - t.LastMs > 3600000) Exec(c, "UPDATE tokens SET last_ms=$n WHERE token=$t", ("$n", Now), ("$t", token));
        return t;
    }

    public void Revoke(string token) { using var c = Open(); Exec(c, "DELETE FROM tokens WHERE token=$t", ("$t", token)); }
    public void RevokeAll(string email) { using var c = Open(); Exec(c, "DELETE FROM tokens WHERE email=$e", ("$e", NormalizeEmail(email))); }

    /// <summary>Is this bearer token attached to an active Pro entitlement?</summary>
    public (bool Pro, Entitlement? E, DeviceToken? T) Check(string? bearer)
    {
        var t = Token(bearer ?? "");
        if (t is null) return (false, null, null);
        var e = Get(t.Email);
        return (e?.Active == true, e, t);
    }
}
