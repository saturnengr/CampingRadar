using System.Text.Json;
using CampRadar.Api.Admin;
using CampRadar.Api.Alerts;
using CampRadar.Api.Lightning;
using CampRadar.Api.Mail;
using CampRadar.Api.Nws;
using CampRadar.Api.Obs;
using CampRadar.Api.Push;
using CampRadar.Api.Radar;
using CampRadar.Api.Spots;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables("CAMPRADAR_");

// Shared HttpClient: NOAA asks for a descriptive User-Agent; api.weather.gov requires one.
builder.Services.AddHttpClient("noaa", c =>
{
    c.Timeout = TimeSpan.FromSeconds(40);
    c.DefaultRequestHeaders.UserAgent.ParseAdd(builder.Configuration["UserAgent"] ?? "CampRadar/1.0 (+https://github.com/saturnengr/CampingRadar)");
    c.DefaultRequestHeaders.Accept.ParseAdd("application/geo+json, application/json, image/png, */*");
});
// AI providers and Graph: connect over IPv4 only. Shared Windows hosts often have no IPv6 route, and a dual-stack
// hostname (googleapis.com) then hangs on the IPv6 attempt until the timeout instead of falling back.
static SocketsHttpHandler IPv4Handler() => new()
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    ConnectCallback = async (ctx, ct) =>
    {
        var addrs = await System.Net.Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, System.Net.Sockets.AddressFamily.InterNetwork, ct);
        if (addrs.Length == 0) throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound);
        var sock = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
        try { await sock.ConnectAsync(addrs, ctx.DnsEndPoint.Port, ct); return new System.Net.Sockets.NetworkStream(sock, ownsSocket: true); }
        catch { sock.Dispose(); throw; }
    }
};
builder.Services.AddHttpClient("llm", c => c.Timeout = TimeSpan.FromSeconds(25)).ConfigurePrimaryHttpMessageHandler(IPv4Handler);
builder.Services.AddHttpClient("graph", c => c.Timeout = TimeSpan.FromSeconds(30)).ConfigurePrimaryHttpMessageHandler(IPv4Handler);
builder.Services.AddHttpClient("obs", c => c.Timeout = TimeSpan.FromSeconds(20)).ConfigurePrimaryHttpMessageHandler(IPv4Handler);
builder.Services.AddHttpClient("madis", c =>   // NOAA's viewer builds each answer on request: 15–40 s is normal
{
    c.Timeout = TimeSpan.FromSeconds(100);
    c.DefaultRequestHeaders.UserAgent.ParseAdd(builder.Configuration["UserAgent"] ?? "CampRadar/1.0 (+https://github.com/saturnengr/CampingRadar)");
}).ConfigurePrimaryHttpMessageHandler(IPv4Handler);
builder.Services.AddSingleton(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient("noaa"));
builder.Services.AddSingleton<MrmsClient>();
builder.Services.AddSingleton<LightningStore>();
builder.Services.AddSingleton<GlmIngestor>();
builder.Services.AddSingleton<NwsClient>();
builder.Services.AddSingleton<NwsForecastClient>();
builder.Services.AddSingleton<BriefingFactsBuilder>();
builder.Services.AddSingleton<SpotStore>();
builder.Services.AddSingleton<DeviceStore>();
builder.Services.AddSingleton<EntitlementStore>();
builder.Services.AddSingleton<GroupStore>();
builder.Services.AddSingleton<SiteStore>();
builder.Services.AddSingleton<SynopticProvider>();
builder.Services.AddSingleton<MadisProvider>();
builder.Services.AddSingleton<IemProvider>();
builder.Services.AddSingleton<StationObsClient>();
builder.Services.AddSingleton<Recorder>();
builder.Services.AddSingleton<Mailer>();
builder.Services.AddSingleton<PushSender>();
builder.Services.AddSingleton<StatusService>();
builder.Services.AddSingleton<BriefingWriter>();
builder.Services.AddSingleton<AlertWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AlertWorker>());
builder.Services.AddSingleton<CampRadar.Api.Cams.CamStore>();
builder.Services.AddSingleton<CampRadar.Api.Cams.CamDirectory>();
builder.Services.AddSingleton<CampRadar.Api.Cams.CamClassifier>();
builder.Services.AddSingleton<CampRadar.Api.Cams.CamWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CampRadar.Api.Cams.CamWorker>());
builder.Services.AddSingleton<CampRadar.Api.Verify.Ledger>();
builder.Services.AddSingleton<CampRadar.Api.Verify.VerifyWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CampRadar.Api.Verify.VerifyWorker>());
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    var origins = (builder.Configuration["AllowedOrigins"] ?? "https://app.vantagewx.com,https://app.getleadtime.app,https://saturnengr.github.io").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
}));
builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase; o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull; });

var app = builder.Build();
app.UseCors();
app.Services.GetRequiredService<Recorder>().ResumeInterrupted();   // recordings survive the host's app-pool recycles
app.Services.GetRequiredService<EntitlementStore>().OwnerResolver = email => app.Services.GetRequiredService<GroupStore>().GroupOf(email)?.OwnerEmail;
string adminKey = app.Configuration["AdminKey"] ?? "";
var ipCodes = new System.Collections.Concurrent.ConcurrentDictionary<string, Queue<long>>();   // small per-key rate limiters (codes, verify attempts, briefings)
static string ExtractEmail(string prefsJson) { try { using var d = JsonDocument.Parse(prefsJson); return d.RootElement.TryGetProperty("email", out var e) ? e.GetString() ?? "" : ""; } catch { return ""; } }
bool IsAdmin(HttpRequest r) => adminKey.Length > 0 && r.Headers["X-Admin-Key"] == adminKey;
string? Bearer(HttpRequest r) { var h = r.Headers.Authorization.ToString(); return h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..].Trim() : null; }
// Pro gate: 402 with a short reason the app shows next to the feature.
IResult? RequirePro(HttpRequest r, EntitlementStore ent) => ent.Check(Bearer(r)).Pro ? null : Results.Json(new { error = "pro", message = "This needs VantageWX Pro. Sign in or enter an activation code in Settings → Account." }, statusCode: 402);

// ---------- health / heartbeat ----------
app.MapGet("/", () => Results.Text("VantageWX API. See /health", "text/plain"));
app.MapGet("/health", (MrmsClient m, LightningStore l, AlertWorker w, PushSender p, SpotStore s) => new
{
    ok = true, utc = DateTimeOffset.UtcNow,
    radar = new { scans = m.Times.Count, latest = m.Latest > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(m.Latest) : (DateTimeOffset?)null, refreshed = m.TimesRefreshedAt },
    lightning = new { enabled = app.Services.GetRequiredService<GlmIngestor>().Enabled, flashes = l.Count, lastIngest = l.LastIngestMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(l.LastIngestMs) : (DateTimeOffset?)null, newestFlash = l.NewestFlashMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(l.NewestFlashMs) : (DateTimeOffset?)null },
    worker = new { lastCycle = w.LastCycle, note = w.LastCycleNote },
    push = new { enabled = p.Enabled, spots = s.All().Count },
    briefings = new { enabled = app.Services.GetRequiredService<BriefingWriter>().Enabled, providers = app.Services.GetRequiredService<BriefingWriter>().Active },
    email = new { enabled = app.Services.GetRequiredService<Mailer>().Enabled, transport = app.Services.GetRequiredService<Mailer>().Transport },
    observations = new { source = app.Services.GetRequiredService<StationObsClient>().Source }
});
// The host's Scheduled Task calls this every 1–2 minutes so alerts go out even if IIS idled the app.
app.MapGet("/tick", async (AlertWorker w) => { using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5)); return new { ran = await w.RunCycleAsync(cts.Token), utc = DateTimeOffset.UtcNow }; });   // a client timeout must not cancel a half-done cycle

// ---------- radar ----------
// Served under /mrms: on the production host the app lives in a folder named "radar", and IIS answers /radar/* itself (500) before the app sees it.
object RadarTimes(MrmsClient m) => new { times = m.Times, latest = m.Latest, loop = m.LoopFrames(new[] { 50, 40, 30, 20, 10, 0 }), refreshed = m.TimesRefreshedAt };
async Task<IResult> RadarTile(long t, int z, int x, int y, MrmsClient m, HttpResponse res, CancellationToken ct)
{
    if (z < 0 || z > 10) return Results.BadRequest("zoom 0-10");
    if (m.Times.Count == 0) await m.RefreshTimesAsync(ct);
    if (!m.Times.Contains(t)) t = m.NearestTime(t);
    if (t == 0) return Results.StatusCode(503);
    var png = await m.TileAsync(t, z, x, y, ct);
    res.Headers.CacheControl = t == m.Latest ? "public, max-age=60" : "public, max-age=86400, immutable";
    return Results.Bytes(png, "image/png");
}
app.MapGet("/mrms/times", (MrmsClient m) => RadarTimes(m));
app.MapGet("/mrms/tile/{t:long}/{z:int}/{x:int}/{y:int}.png", (long t, int z, int x, int y, MrmsClient m, HttpResponse res, CancellationToken ct) => z < 0 || z > 12 || x < 0 || y < 0 || x >= (1 << z) || y >= (1 << z) ? Task.FromResult(Results.BadRequest()) : RadarTile(t, z, x, y, m, res, ct));
app.MapGet("/radar/times", (MrmsClient m) => RadarTimes(m));
app.MapGet("/radar/tile/{t:long}/{z:int}/{x:int}/{y:int}.png", (long t, int z, int x, int y, MrmsClient m, HttpResponse res, CancellationToken ct) => z < 0 || z > 12 || x < 0 || y < 0 || x >= (1 << z) || y >= (1 << z) ? Task.FromResult(Results.BadRequest()) : RadarTile(t, z, x, y, m, res, ct));

// ---------- nearby surface observations (free; Synoptic token on the server) ----------
// Free: the best nearby station and the pressure trend (replaces the single NWS airport). Pro: the whole list for the map layer.
app.MapGet("/obs/nearby", async (HttpRequest http, EntitlementStore ent, double lat, double lon, StationObsClient obs, HttpResponse res, CancellationToken ct) =>
{
    try
    {
        var r = await obs.NearbyAsync(lat, lon, ct);
        bool pro = ent.Check(Bearer(http)).Pro;
        res.Headers.CacheControl = "private, max-age=120";
        return Results.Ok(new { enabled = true, source = obs.Source, at = r!.At, best = r.Best, trend = r.Trend, pro, stations = pro ? r.Stations : r.Stations.Take(1).ToList() });
    }
    catch (Exception ex) when (ex is not OperationCanceledException) { return Results.Json(new { enabled = true, error = ex.Message, stations = Array.Empty<object>() }, statusCode: 502); }
});

// ---------- lightning ----------
app.MapGet("/lightning", (HttpRequest req, EntitlementStore ent, double lat, double lon, LightningStore l, int minutes = 30) => RequirePro(req, ent) ?? Results.Ok(l.Summarize(lat, lon, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Math.Clamp(minutes, 5, 60))));
app.MapGet("/lightning/recent", (HttpRequest req, EntitlementStore ent, double minLat, double minLon, double maxLat, double maxLon, LightningStore l, int minutes = 30) =>
{
    if (RequirePro(req, ent) is { } gate) return gate;
    if (maxLat - minLat > 12 || maxLon - minLon > 16) return Results.BadRequest("box too large");
    long since = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Math.Clamp(minutes, 5, 60) * 60000L;
    return Results.Ok(l.Recent(minLat, minLon, maxLat, maxLon, since).Select(f => new { t = f.TimeMs, lat = f.Lat, lon = f.Lon }));
});

// ---------- combined status for a point ----------
app.MapGet("/status", async (double lat, double lon, StatusService s, CancellationToken ct, double hitMi = 3, float threshDbz = 20) =>
{
    if (lat is < 15 or > 72 || lon is < -170 or > -50) return Results.BadRequest("US coverage only");
    return Results.Ok(await s.GetAsync(lat, lon, new StatusOptions(Math.Clamp(hitMi, 1, 10), Math.Clamp(threshDbz, 10, 40)), ct));
});

// ---------- spoken briefing text ----------
app.MapPost("/brief", async (HttpRequest http, EntitlementStore ent, [FromBody] BriefRequest req, BriefingWriter w, CancellationToken ct) =>
{
    if (RequirePro(http, ent) is { } gate) return gate;
    if (!w.Enabled) return Results.StatusCode(503);
    if (req.Facts.ValueKind != JsonValueKind.Object || req.Facts.GetRawText().Length > 20000) return Results.BadRequest("facts object required");
    var bq = ipCodes.GetOrAdd("brief:" + (Bearer(http) ?? "anon"), _ => new Queue<long>()); long bnow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    lock (bq) { while (bq.Count > 0 && bnow - bq.Peek() > 3600_000) bq.Dequeue(); if (bq.Count >= 40) return Results.Json(new { error = "That is a lot of briefings in an hour — try again later." }, statusCode: 429); bq.Enqueue(bnow); }
    var (text, provider) = await w.WriteAsync(req.Facts, req.Fallback is { Length: < 4000 } ? req.Fallback : null, ct, null, req.Style is "now" or "day" ? req.Style : "briefing");
    return text is null ? Results.StatusCode(503) : Results.Ok(new { text, source = "backend", provider });
});

// ---------- push subscriptions ----------
app.MapGet("/push/vapid-public-key", (PushSender p) => p.Enabled ? Results.Text(p.PublicKey!, "text/plain") : Results.StatusCode(503));
app.MapPost("/push/subscribe", (HttpRequest http, EntitlementStore ent, [FromBody] SubscribeRequest req, SpotStore store, PushSender p) =>
{
    if (RequirePro(http, ent) is { } gate) return gate;
    if (!p.Enabled) return Results.StatusCode(503);
    if (req.Subscription?.Endpoint is null || req.Subscription.Keys is null) return Results.BadRequest("subscription required");
    if (req.Lat is < 15 or > 72 || req.Lon is < -170 or > -50) return Results.BadRequest("US coverage only");
    var who = ent.Check(Bearer(http));
    var prefs = JsonSerializer.Deserialize<Dictionary<string, object?>>(req.Prefs?.GetRawText() ?? "{}") ?? new();
    prefs["email"] = who.T!.Email;
    var id = store.Upsert(req.Subscription.Endpoint, req.Subscription.Keys.P256dh, req.Subscription.Keys.Auth, req.Lat, req.Lon, JsonSerializer.Serialize(prefs));
    return Results.Ok(new { id });
});
app.MapPost("/push/{id}/location", (string id, [FromBody] LocationRequest r, SpotStore store) => { if (store.Get(id) is null) return Results.NotFound(); store.SetLocation(id, r.Lat, r.Lon); return Results.Ok(); });
app.MapPost("/push/{id}/prefs", (HttpRequest http, string id, [FromBody] JsonElement body, SpotStore store, EntitlementStore ent) =>
{
    if (store.Get(id) is null) return Results.NotFound();
    if (body.ValueKind != JsonValueKind.Object || body.GetRawText().Length > 4000) return Results.BadRequest();
    var patch = new Dictionary<string, object?>();
    foreach (var p in body.EnumerateObject()) if (p.Name != "email") patch[p.Name] = p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.Clone();
    // the email never comes from the body — only from a valid sign-in token (heals subscriptions made before sign-in existed)
    var (_, _, tok) = ent.Check(Bearer(http)); if (tok is not null) patch["email"] = tok.Email;
    store.MergePrefs(id, patch); return Results.Ok();
});
app.MapPost("/push/{id}/snooze", (string id, [FromBody] SnoozeRequest r, SpotStore store) => { if (store.Get(id) is null) return Results.NotFound(); store.SetSnooze(id, r.UntilMs); return Results.Ok(); });
app.MapDelete("/push/{id}", (string id, SpotStore store) => { store.Delete(id); return Results.Ok(); });
app.MapPost("/push/{id}/test", async (string id, SpotStore store, PushSender p, CancellationToken ct) =>
{
    var spot = store.Get(id); if (spot is null) return Results.NotFound();
    try
    {
        var ok = await p.SendAsync(spot, new { title = "VantageWX test", body = "Alerts are working on this device.", tag = "test", at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, "test", false, ct);
        return ok ? Results.Ok() : Results.Json(new { error = "this device's subscription has expired; turn alerts off and on again" }, statusCode: 410);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {   // surfaced to the app's settings page so a host or key problem can be read without server logs
        var inner = ex.InnerException?.Message; return Results.Json(new { error = ex.Message + (inner is null ? "" : " — " + inner) }, statusCode: 500);
    }
});

// ---------- anonymous usage ping (the app calls this on open and every 10 minutes while open) ----------
app.MapPost("/app/ping", (HttpRequest http, [FromBody] PingRequest r, DeviceStore d, EntitlementStore ent) =>
{
    if (string.IsNullOrWhiteSpace(r.Id) || r.Id.Length > 40 || r.Lat is < -90 or > 90 || r.Lon is < -180 or > 180) return Results.BadRequest();
    string Clip(string? x, int n) => (x ?? "").Length > n ? x![..n] : (x ?? "");
    var (_, _, tok) = ent.Check(Bearer(http));   // the account comes from a valid sign-in token, never from the body; a device that signs out keeps its last known account
    d.Ping(r.Id, r.Lat, r.Lon, Clip(r.Activity, 16), Clip(r.Lang, 8), Clip(r.Platform, 24), Clip(r.Version, 16), r.Push, r.Opened, Clip(r.Name, 40), tok?.Email);
    return Results.Ok();
});

// ---------- account: email sign-in (6-digit code by email), activation codes, settings sync, deletion ----------
var EmailRx = new System.Text.RegularExpressions.Regex(@"^[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}$", System.Text.RegularExpressions.RegexOptions.Compiled);
app.MapGet("/auth/config", (Mailer mail) => new { emailSignIn = mail.Enabled });
app.MapPost("/auth/start", async (HttpRequest http, [FromBody] StartRequest r, EntitlementStore ent, Mailer mail, CancellationToken ct) =>
{
    if (!mail.Enabled) return Results.Json(new { error = "Email sign-in is not set up on this server yet. Use an activation code instead." }, statusCode: 503);
    var email = (r.Email ?? "").Trim();
    if (email.Length < 5 || email.Length > 254 || !EmailRx.IsMatch(email)) return Results.BadRequest(new { error = "Enter a valid email address." });
    // rate limit: 3 codes per email per 10 min, 10 per IP per 10 min
    var ip = http.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "?"; long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var q = ipCodes.GetOrAdd(ip, _ => new Queue<long>()); lock (q) { while (q.Count > 0 && now - q.Peek() > 600000) q.Dequeue(); if (q.Count >= 10) return Results.Json(new { error = "Too many codes requested. Try again in a few minutes." }, statusCode: 429); q.Enqueue(now); }
    if (ent.RecentLoginCodes(email) >= 3) return Results.Json(new { error = "A code was already sent. Check your email (and spam), or wait a few minutes." }, statusCode: 429);
    var code = ent.NewLoginCode(email);
    try { await mail.SendCodeAsync(email, code, ct); }
    catch (Exception ex) { ent.VoidLoginCode(email, code); app.Logger.LogWarning(ex, "sign-in mail failed"); return Results.Json(new { error = "Could not send the email right now. Try again in a minute." }, statusCode: 502); }
    return Results.Ok(new { sent = true });
});
app.MapPost("/auth/verify", ([FromBody] VerifyRequest r, EntitlementStore ent) =>
{
    if (string.IsNullOrWhiteSpace(r.Email) || string.IsNullOrWhiteSpace(r.Code) || string.IsNullOrWhiteSpace(r.DeviceId)) return Results.BadRequest(new { error = "email, code and deviceId required" });
    var vkey = EntitlementStore.NormalizeEmail(r.Email); long vnow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var vq = ipCodes.GetOrAdd("verify:" + vkey, _ => new Queue<long>());
    lock (vq) { while (vq.Count > 0 && vnow - vq.Peek() > 600000) vq.Dequeue(); if (vq.Count >= 8) return Results.Json(new { error = "Too many attempts. Request a new code in a few minutes." }, statusCode: 429); }
    var t = ent.Verify(r.Email, r.Code, r.DeviceId);
    if (t is null) { lock (vq) vq.Enqueue(vnow); if (ipCodes.Count > 5000) ipCodes.Clear(); return Results.Json(new { error = "That code is not right or has expired. Request a new one." }, statusCode: 400); }
    var e = ent.Get(t.Email);
    return Results.Ok(new { token = t.Token, email = t.Email, plan = e?.Plan ?? "free", expiresMs = e?.ExpiresMs ?? 0, pro = e?.Active == true });
});
app.MapGet("/me/settings", (HttpRequest r, EntitlementStore ent) =>
{
    var (_, _, t) = ent.Check(Bearer(r)); if (t is null) return Results.Unauthorized();
    var json = ent.GetSettings(t.Email, out var upd);
    return json is null ? Results.Ok(new { settings = (object?)null, updatedMs = 0L }) : Results.Content("{\"settings\":" + json + ",\"updatedMs\":" + upd + "}", "application/json");
});
app.MapPut("/me/settings", (HttpRequest r, [FromBody] JsonElement body, EntitlementStore ent) =>
{
    var (_, _, t) = ent.Check(Bearer(r)); if (t is null) return Results.Unauthorized();
    var raw = body.GetRawText(); if (body.ValueKind != JsonValueKind.Object || raw.Length > 32000) return Results.BadRequest(new { error = "settings object required (max 32 KB)" });
    return Results.Ok(new { updatedMs = ent.PutSettings(t.Email, raw) });
});
app.MapDelete("/me", (HttpRequest r, EntitlementStore ent, SpotStore spots, SiteStore sites) =>
{
    var (_, _, t) = ent.Check(Bearer(r)); if (t is null) return Results.Unauthorized();
    sites.DeleteFor(t.Email);
    foreach (var sp in spots.All().Where(sp => string.Equals(ExtractEmail(sp.Prefs), t.Email, StringComparison.OrdinalIgnoreCase))) spots.Delete(sp.Id);
    app.Services.GetRequiredService<GroupStore>().DeleteEverythingFor(t.Email);
    ent.DeleteAccount(t.Email);
    return Results.Ok(new { deleted = true });
});

// ---------- saved places, dry windows, leave-by ----------
object SiteView(Site s) => new { s.Id, s.Name, s.Lat, s.Lon, s.Activity, s.Alerts, s.Digest, thresholds = JsonDocument.Parse(s.Thresholds).RootElement.Clone(), s.UpdatedMs };
app.MapGet("/sites", (HttpRequest r, EntitlementStore ent, SiteStore sites) =>
{
    var (pro, _, t) = ent.Check(Bearer(r)); if (t is null) return Results.Unauthorized();
    return Results.Ok(new { pro, max = SiteStore.MaxPerAccount, sites = sites.ForEmail(t.Email).Select(SiteView) });
});
app.MapPost("/sites", (HttpRequest r, [FromBody] SiteRequest req, EntitlementStore ent, SiteStore sites) =>
{
    var (pro, _, t) = ent.Check(Bearer(r)); if (t is null) return Results.Unauthorized();
    if (!pro) return Results.Json(new { error = "pro", message = "Saved places need VantageWX Pro." }, statusCode: 402);
    var name = (req.Name ?? "").Trim(); if (name.Length is 0 or > 40) return Results.BadRequest(new { error = "name: 1 to 40 characters" });
    if (req.Lat is < -90 or > 90 || req.Lon is < -180 or > 180) return Results.BadRequest(new { error = "bad coordinates" });
    var thr = req.Thresholds is { ValueKind: JsonValueKind.Object } th && th.GetRawText().Length < 500 ? th.GetRawText() : "{}";
    var s = sites.Upsert(t.Email, req.Id, name, req.Lat, req.Lon, string.IsNullOrWhiteSpace(req.Activity) ? "camping" : req.Activity!, req.Alerts, req.Digest, thr, out var err);
    return s is null ? Results.BadRequest(new { error = err }) : Results.Ok(SiteView(s));
});
app.MapDelete("/sites/{id}", (string id, HttpRequest r, EntitlementStore ent, SiteStore sites) =>
{
    var (_, _, t) = ent.Check(Bearer(r)); if (t is null) return Results.Unauthorized();
    sites.Delete(id, t.Email); return Results.Ok();
});
// Plan for any point: dry windows over the next 48 h, personal threshold crossings in the next 12 h, and when to start back.
// backMin = how long the walk/paddle/drive back to shelter takes. Pro.
async Task<IResult> PlanAsync(HttpRequest r, EntitlementStore ent, NwsForecastClient fc, StatusService status, double lat, double lon, int backMin, JsonElement? thrJson, CancellationToken ct)
{
    if (RequirePro(r, ent) is { } gate) return gate;
    var f = await fc.GetAsync(lat, lon, ct); if (f is null) return Results.Json(new { error = "No forecast for that spot (outside NWS coverage?)" }, statusCode: 502);
    Status? s = null; try { s = await status.GetAsync(lat, lon, new StatusOptions(), ct); } catch { }
    var thr = new Dictionary<string, double>(Planner.DefaultThresholds);
    if (thrJson is { ValueKind: JsonValueKind.Object } tj) { thr.Clear(); foreach (var p in tj.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.Number) thr[p.Name] = p.Value.GetDouble(); }
    var now = DateTimeOffset.UtcNow.ToOffset(f.Offset);
    var windows = Planner.DryWindows(f, now, 48, 2, thr.TryGetValue("gustMph", out var g) ? Math.Min(g, 25) : 25);
    var leave = Planner.LeaveByTime(f, s, now, Math.Clamp(backMin, 0, 600), thr);
    return Results.Ok(new
    {
        place = f.Place, offsetMin = (int)f.Offset.TotalMinutes, nowMs = now.ToUnixTimeMilliseconds(),
        windows = windows.Select(w => new { fromMs = w.From.ToUnixTimeMilliseconds(), toMs = w.To.ToUnixTimeMilliseconds(), w.Hours, w.MaxPop, w.MaxGust, w.Hi, w.Lo, w.Sky, w.Label }),
        leaveBy = leave is null ? null : new { atMs = leave.At.ToUnixTimeMilliseconds(), troubleMs = leave.Trouble.ToUnixTimeMilliseconds(), leave.Reason, leave.ReasonEs, leave.Confidence },
        hits = Planner.ThresholdHits(f, now, thr, 12).Select(h => new { h.Key, h.Label, h.LabelEs, h.Value, atMs = h.At.ToUnixTimeMilliseconds(), h.Now }),
        hourly = f.Hours.Where(h => h.T.AddHours(1) > now && h.T < now.AddHours(48)).Select(h => new { tMs = h.T.ToUnixTimeMilliseconds(), temp = Math.Round(h.Temp), h.Pop, h.Thunder, gust = Math.Round(Math.Max(h.WindMph, h.GustMph)), h.Sky })
    });
}
app.MapGet("/plan", (HttpRequest r, EntitlementStore ent, NwsForecastClient fc, StatusService status, double lat, double lon, CancellationToken ct, int backMin = 60) => PlanAsync(r, ent, fc, status, lat, lon, backMin, null, ct));
app.MapPost("/plan", (HttpRequest r, [FromBody] PlanRequest req, EntitlementStore ent, NwsForecastClient fc, StatusService status, CancellationToken ct) => PlanAsync(r, ent, fc, status, req.Lat, req.Lon, req.BackMin ?? 60, req.Thresholds, ct));
app.MapGet("/sites/{id}/plan", async (string id, HttpRequest r, EntitlementStore ent, SiteStore sites, NwsForecastClient fc, StatusService status, CancellationToken ct, int backMin = 60) =>
{
    var (_, _, t) = ent.Check(Bearer(r)); if (t is null) return Results.Unauthorized();
    var s = sites.Get(id, t.Email); if (s is null) return Results.NotFound();
    return await PlanAsync(r, ent, fc, status, s.Lat, s.Lon, backMin, JsonDocument.Parse(s.Thresholds).RootElement.Clone(), ct);
});

// ---------- household: invites, members, location sharing, messages ----------
static string DisplayName(string? name, string email) => string.IsNullOrWhiteSpace(name) ? email.Split('@')[0] : name.Trim();
object GroupView(GroupStore groups, EntitlementStore ent, string me)
{
    var g = groups.GroupOf(me);
    if (g is null) return new { group = (object?)null, pro = ent.IsPro(me) };
    var members = groups.Members(g.Id);
    var mine = members.FirstOrDefault(m => m.Email == EntitlementStore.NormalizeEmail(me));
    return new
    {
        group = new { g.Id, owner = g.OwnerEmail, ownerName = DisplayName(members.FirstOrDefault(m => m.Role == "owner")?.Name, g.OwnerEmail), isOwner = g.OwnerEmail == EntitlementStore.NormalizeEmail(me), ownerPro = ent.Get(g.OwnerEmail)?.Active == true,
            me = mine is null ? null : new { mine.Email, name = DisplayName(mine.Name, mine.Email), mine.ShareLocation, mine.ShareUntilMs },
            members = members.Select(m => new { m.Email, name = DisplayName(m.Name, m.Email), m.Role, m.ShareLocation, m.ShareUntilMs, m.JoinedMs }) },
        pro = ent.IsPro(me)
    };
}
app.MapGet("/group", (HttpRequest r, EntitlementStore ent, GroupStore groups) => { var (_, _, t) = ent.Check(Bearer(r)); return t is null ? Results.Unauthorized() : Results.Ok(GroupView(groups, ent, t.Email)); });
app.MapPost("/group/invite", async (HttpRequest r, [FromBody] InviteRequest req, EntitlementStore ent, GroupStore groups, Mailer mail, CancellationToken ct) =>
{
    var (_, e, t) = ent.Check(Bearer(r)); if (t is null) return Results.Unauthorized();
    if (e?.Active != true) return Results.Json(new { error = "pro", message = "Inviting family and friends needs VantageWX Pro on your account." }, statusCode: 402);
    var existing = groups.GroupOf(t.Email);
    if (existing is not null && existing.OwnerEmail != t.Email) return Results.BadRequest(new { error = "You are a member of someone else's group. Leave it to start your own." });
    var g = groups.EnsureOwnerGroup(t.Email, req.MyName ?? "");
    if (!string.IsNullOrWhiteSpace(req.MyName)) groups.SetSharing(g.Id, t.Email, groups.MemberOf(g.Id, t.Email)?.ShareLocation ?? false, 1, req.MyName);
    if (groups.Members(g.Id).Count >= 12) return Results.BadRequest(new { error = "A group can have up to 12 people." });
    var code = groups.NewInvite(g.Id, string.IsNullOrWhiteSpace(req.Email) ? null : req.Email);
    bool emailed = false;
    if (!string.IsNullOrWhiteSpace(req.Email) && mail.Enabled)
    {
        var owner = DisplayName(req.MyName, t.Email);
        try { await mail.SendAsync(req.Email.Trim(), $"{owner} invited you to VantageWX", $"{owner} ({t.Email}) invited you to their VantageWX group.\n\nWith the invite you share weather alerts, can choose to share your location with the group for a few hours at a time, and can send each other short messages like \"dinner in 10\".\n\nTo accept: open the app (app.vantagewx.com), sign in with this email under Settings → Account, then Settings → Family → enter the invite code:\n\n    {code}\n\nThe code works for 7 days. If you don't know {owner}, ignore this email.\n\n— {mail.ProductName}", ct); emailed = true; }
        catch (Exception ex) { app.Logger.LogWarning(ex, "invite mail failed"); }
    }
    return Results.Ok(new { code, emailed, expiresDays = 7 });
});
app.MapPost("/group/join", (HttpRequest r, [FromBody] JoinRequest req, EntitlementStore ent, GroupStore groups) =>
{
    var (_, _, t) = ent.Check(Bearer(r)); if (t is null) return Results.Unauthorized();
    var (g, err) = groups.Join(req.Code ?? "", t.Email, req.MyName ?? "");
    return g is null ? Results.BadRequest(new { error = err }) : Results.Ok(GroupView(groups, ent, t.Email));
});
app.MapPost("/group/leave", (HttpRequest r, EntitlementStore ent, GroupStore groups) =>
{
    var (_, _, t) = ent.Check(Bearer(r)); if (t is null) return Results.Unauthorized();
    var g = groups.GroupOf(t.Email); if (g is null) return Results.Ok();
    if (g.OwnerEmail == t.Email) groups.DeleteEverythingFor(t.Email); else groups.Remove(g.Id, t.Email);
    return Results.Ok();
});
app.MapDelete("/group/member/{email}", (string email, HttpRequest r, EntitlementStore ent, GroupStore groups) =>
{
    var (_, _, t) = ent.Check(Bearer(r)); if (t is null) return Results.Unauthorized();
    var g = groups.GroupOf(t.Email); if (g is null || g.OwnerEmail != t.Email) return Results.StatusCode(403);
    groups.Remove(g.Id, email); return Results.Ok();
});
app.MapPost("/group/share", (HttpRequest r, [FromBody] ShareRequest req, EntitlementStore ent, GroupStore groups) =>
{
    var (_, _, t) = ent.Check(Bearer(r)); if (t is null) return Results.Unauthorized();
    var g = groups.GroupOf(t.Email); if (g is null) return Results.BadRequest(new { error = "not in a group" });
    groups.SetSharing(g.Id, t.Email, req.On, req.Hours <= 0 ? 8 : req.Hours, req.MyName);
    return Results.Ok(GroupView(groups, ent, t.Email));
});
app.MapPost("/me/position", (HttpRequest r, [FromBody] PositionRequest req, EntitlementStore ent, GroupStore groups) =>
{
    var (_, _, t) = ent.Check(Bearer(r)); if (t is null) return Results.Unauthorized();
    if (req.Lat is < -90 or > 90 || req.Lon is < -180 or > 180) return Results.BadRequest();
    var g = groups.GroupOf(t.Email); var m = g is null ? null : groups.MemberOf(g.Id, t.Email);
    if (m is null || !m.ShareLocation) return Results.Ok(new { stored = false, reason = "not sharing" });
    groups.AddPosition(t.Email, t.DeviceId, req.Lat, req.Lon, req.AccM ?? 0);
    return Results.Ok(new { stored = true, shareUntilMs = m.ShareUntilMs });
});
app.MapGet("/group/positions", (HttpRequest r, EntitlementStore ent, GroupStore groups) =>
{
    var (_, _, t) = ent.Check(Bearer(r)); if (t is null) return Results.Unauthorized();
    var g = groups.GroupOf(t.Email); if (g is null) return Results.Ok(Array.Empty<object>());
    return Results.Ok(groups.Positions(g.Id));
});
app.MapGet("/group/messages", (HttpRequest r, EntitlementStore ent, GroupStore groups, long since = 0) =>
{
    var (_, _, t) = ent.Check(Bearer(r)); if (t is null) return Results.Unauthorized();
    var g = groups.GroupOf(t.Email); if (g is null) return Results.Ok(Array.Empty<object>());
    return Results.Ok(groups.Messages(g.Id, since).Select(m => new { m.Id, from = m.FromEmail, fromName = m.FromName, m.Kind, m.Text, data = JsonDocument.Parse(m.DataJson).RootElement.Clone(), m.Ms, m.ReplyTo, mine = m.FromEmail == t.Email }));
});
// Send a short message to the group: pushed to every member's devices (not the sender's), with reply chips.
async Task<int> FanOutMessage(GroupStore groups, SpotStore spots, PushSender push, GroupInfo g, Message m, string senderEmail, CancellationToken ct, string? senderSpotId = null)
{
    int sent = 0; var replyToken = groups.ReplyToken(m.Id) ?? "";
    // every member device gets it, including the sender's *other* devices (one email can be a phone and a tablet); only the sending device is skipped
    var memberEmails = groups.Members(g.Id).Select(x => x.Email).ToHashSet();
    string device = ""; try { using var dd = JsonDocument.Parse(m.DataJson); if (dd.RootElement.TryGetProperty("_device", out var dv)) device = dv.GetString() ?? ""; } catch { }
    foreach (var spot in spots.All())
    {
        string email = ""; try { using var d = JsonDocument.Parse(spot.Prefs); email = d.RootElement.TryGetProperty("email", out var e) ? e.GetString() ?? "" : ""; } catch { }
        if (!memberEmails.Contains(email) || spot.Id == senderSpotId) continue;
        bool ok;
        try { ok = await push.SendAsync(spot, new { title = m.FromName + (device.Length > 0 ? " · " + device : ""), body = m.Text, tag = "msg", level = "info", msgId = m.Id, replyToken = ReplyKey(m.Id, email), toEmail = email, kind = m.Kind, data = JsonDocument.Parse(m.DataJson).RootElement.Clone(), lang = "", at = m.Ms }, "msg", m.Kind == "weather", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { app.Logger.LogWarning(ex, "message push to {Spot} failed", spot.Id); ok = false; }
        if (ok) sent++; else spots.RecordFailure(spot.Id, gone: false);
    }
    return sent;
}
// A reply token is bound to one recipient: HMAC(message id : recipient email) with a server secret, so a notification button can only reply as the device it was sent to.
string ReplyKey(long msgId, string email)
{
    var secret = System.Text.Encoding.UTF8.GetBytes((builder.Configuration["Vapid:PrivateKey"] ?? "") + "|" + adminKey + "|replies");
    using var h = new System.Security.Cryptography.HMACSHA256(secret);
    return Convert.ToHexString(h.ComputeHash(System.Text.Encoding.UTF8.GetBytes(msgId + ":" + EntitlementStore.NormalizeEmail(email)))).ToLowerInvariant();
}
app.MapPost("/group/message", async (HttpRequest r, [FromBody] MessageRequest req, EntitlementStore ent, GroupStore groups, SpotStore spots, PushSender push, CancellationToken ct) =>
{
    var (pro, _, t) = ent.Check(Bearer(r)); if (t is null) return Results.Unauthorized();
    if (!pro) return Results.Json(new { error = "pro", message = "Messages need VantageWX Pro (yours or your group owner's)." }, statusCode: 402);
    var g = groups.GroupOf(t.Email); if (g is null) return Results.BadRequest(new { error = "not in a group" });
    var text = (req.Text ?? "").Trim(); if (text.Length == 0 || text.Length > 160) return Results.BadRequest(new { error = "1 to 160 characters" });
    if (groups.RecentMessageCount(t.Email, 10) >= 20) return Results.Json(new { error = "Slow down — that's a lot of messages in ten minutes." }, statusCode: 429);
    var me = groups.MemberOf(g.Id, t.Email);
    var m = groups.AddMessage(g.Id, t.Email, DisplayName(me?.Name, t.Email), req.Kind is "preset" or "weather" ? req.Kind : "text", text, req.Data is { ValueKind: JsonValueKind.Object } d && d.GetRawText().Length < 2000 ? d.GetRawText() : "{}", req.ReplyTo);
    var sent = await FanOutMessage(groups, spots, push, g, m, t.Email, ct, req.FromSpot);
    return Results.Ok(new { id = m.Id, delivered = sent });
});
// Reply from a notification button: no sign-in token in the service worker, so the message's own reply token authorises it.
app.MapPost("/group/reply", async ([FromBody] ReplyRequest req, GroupStore groups, SpotStore spots, PushSender push, CancellationToken ct) =>
{
    if (req.MsgId is not { } mid || string.IsNullOrWhiteSpace(req.FromEmail) || string.IsNullOrWhiteSpace(req.ReplyToken)) return Results.BadRequest(new { error = "expired" });
    var expected = ReplyKey(mid, req.FromEmail);
    if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(req.ReplyToken.ToLowerInvariant()))) return Results.StatusCode(403);
    var orig = groups.MessageById(mid); if (orig is null || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - orig.Ms > 24 * 3600_000L) return Results.BadRequest(new { error = "expired" });
    var g = groups.GroupOf(orig.FromEmail); if (g is null) return Results.BadRequest();
    var replier = groups.Members(g.Id).FirstOrDefault(m => m.Email == EntitlementStore.NormalizeEmail(req.FromEmail)); if (replier is null) return Results.StatusCode(403);
    var text = (req.Text ?? "").Trim(); if (text.Length == 0 || text.Length > 160) return Results.BadRequest();
    var m = groups.AddMessage(g.Id, replier.Email, DisplayName(replier.Name, replier.Email), "preset", text, "{}", orig.Id);
    var sent = await FanOutMessage(groups, spots, push, g, m, replier.Email, ct);
    return Results.Ok(new { id = m.Id, delivered = sent });
});

// ---------- activation codes (admin-issued or from a purchase) → device tokens ----------
app.MapPost("/auth/redeem", ([FromBody] RedeemRequest r, EntitlementStore ent) =>
{
    if (string.IsNullOrWhiteSpace(r.Code) || string.IsNullOrWhiteSpace(r.DeviceId)) return Results.BadRequest(new { error = "code and deviceId required" });
    var t = ent.Redeem(r.Code, r.DeviceId);
    if (t is null) return Results.Json(new { error = "That code is not valid, was already used, or has expired." }, statusCode: 400);
    var e = ent.Get(t.Email);
    return Results.Ok(new { token = t.Token, email = t.Email, plan = e?.Plan ?? "free", expiresMs = e?.ExpiresMs ?? 0, pro = e?.Active == true });
});
app.MapGet("/auth/me", (HttpRequest r, EntitlementStore ent) =>
{
    var (pro, e, t) = ent.Check(Bearer(r));
    return t is null ? Results.Unauthorized() : Results.Ok(new { email = t.Email, plan = e?.Plan ?? "free", expiresMs = e?.ExpiresMs ?? 0, pro });
});
app.MapPost("/auth/signout", (HttpRequest r, EntitlementStore ent) => { var b = Bearer(r); if (b is not null) ent.Revoke(b); return Results.Ok(); });

// ---------- admin ----------
app.MapGet("/admin", () => Results.Content(AdminPage.Html, "text/html"));
app.MapPost("/admin/mail/test", async (HttpRequest r, [FromBody] MailTestRequest m, Mailer mail, CancellationToken ct) =>
{
    if (!IsAdmin(r)) return Results.Unauthorized();
    if (!mail.Enabled) return Results.Json(new { ok = false, error = "No mail transport configured (Smtp or Graph block in appsettings.Production.json)." });
    try { await mail.SendAsync(m.To, $"{mail.ProductName} test email", $"This is a test from the {mail.ProductName} server via {mail.Transport}. If you can read this, sign-in codes will arrive.", ct); return Results.Ok(new { ok = true, transport = mail.Transport }); }
    catch (Exception ex) { return Results.Json(new { ok = false, transport = mail.Transport, error = ex.Message, inner = ex.InnerException?.Message }); }
});
app.MapPost("/admin/brief/test", async (HttpRequest r, BriefingWriter w, CancellationToken ct) =>
{
    if (!IsAdmin(r)) return Results.Unauthorized();
    if (!w.Enabled) return Results.Json(new { ok = false, error = "No briefing provider configured (Gemini, Anthropic or OpenAI key)." });
    var facts = JsonDocument.Parse("""{"activity":"camping","language":"en-US","place":"Dallas, TX","time":"3:10 PM","morning":false,"now":{"temp":"71°","cond":"Partly Cloudy","wind":"Wind S 9 mph"},"rain":{"etaMin":42,"from":"southwest","intensity":"Moderate","trend":"strengthening"},"lightning":{"within10":false,"within30":true,"nearestMi":18,"dir":"SW","trend":"approaching"},"alerts":[],"hazards":[],"tropical":[],"precipType":"rain","nextStats":{"maxPop":70,"rainFrom":"4 PM","rainTo":"7 PM","thunderFrom":"4 PM","thunderTo":"6 PM","maxThunder":40,"hi":73,"lo":58,"topGust":28,"gustAt":"5 PM","windDir":"S","windNow":9},"today":{"night":{"lo":55,"text":"Clearing"}},"outlook":{"label":"tomorrow","day":{"text":"Sunny","hi":68,"pop":0},"night":{"lo":48}},"sun":{"rise":"6:55 AM","set":"5:28 PM","minsToSunset":138}}""").RootElement;
    var errors = new List<string>();
    var (text, provider) = await w.WriteAsync(facts, null, ct, errors);
    return Results.Ok(new { ok = text is not null, provider, providers = w.Active, text, errors });
});
// Which outside services can this host actually reach? (shared hosts block or black-hole some destinations)
// ---------- radar recordings for offline verification ----------
app.MapGet("/admin/record", (HttpRequest r, Recorder rec) => IsAdmin(r) ? Results.Ok(rec.List()) : Results.Unauthorized());
// ---- webcams as ground truth: our directory, what the worker is looking at, and one-off checks ----
app.MapGet("/admin/cams", (HttpRequest r, CampRadar.Api.Cams.CamStore cs, CampRadar.Api.Cams.CamDirectory dir, CampRadar.Api.Cams.CamWorker w, double? lat, double? lon, double mi = 15) =>
    !IsAdmin(r) ? Results.Unauthorized() : Results.Ok(new { directory = new { dir.LastRun, dir.LastRunAt, stats = cs.Stats() }, worker = new { w.LastCycle, w.LastCycleAt, targets = w.Targets(), recent = w.Recent.Values.OrderByDescending(x => x).Take(30) },
        near = lat is { } la && lon is { } lo ? cs.Near(la, lo, mi, 25).Select(c => new { c.Id, c.Name, c.Lat, c.Lon, c.HeadingDeg, c.Url, c.Quality, c.Attempts, c.Good, c.LastGoodMs, distMi = Math.Round(CampRadar.Api.Cams.CamStore.Haversine(la, lo, c.Lat, c.Lon), 1) }) : null }));
app.MapGet("/admin/cams/page", () => Results.Content(CampRadar.Api.Cams.CamsPage.Html, "text/html"));
app.MapGet("/admin/cams/list", (HttpRequest r, CampRadar.Api.Cams.CamStore cs, double? lat, double? lon, double mi = 25, string? id = null) =>
{
    if (!IsAdmin(r)) return Results.Unauthorized();
    var list = id is not null ? (cs.Get(id) is { } one ? new List<CampRadar.Api.Cams.Cam> { one } : new()) : lat is { } la && lon is { } lo ? cs.Near(la, lo, mi, 400) : cs.Near(42, -93.5, 400, 400, true);
    return Results.Ok(list.Select(c => new { c.Id, c.Name, c.Source, c.Lat, c.Lon, c.HeadingDeg, c.Url, c.Quality, c.Attempts, c.Good, c.LastGoodMs, c.Cues, c.Licence }));
});
// frames are loaded by <img>, which cannot send a header: the key rides as a query parameter on this one endpoint
app.MapGet("/admin/cams/frame", (HttpRequest r, CampRadar.Api.Cams.CamStore cs, string name, string? key) =>
{
    if (!(IsAdmin(r) || (adminKey.Length > 0 && key == adminKey))) return Results.Unauthorized();
    var p = cs.FramePath(name); if (p is null) return Results.NotFound();
    // Results.File resolves a relative path against the web root; the data folder is elsewhere, so hand it the full path
    return Results.File(Path.GetFullPath(p), name.EndsWith(".png") ? "image/png" : name.EndsWith(".gif") ? "image/gif" : "image/jpeg", enableRangeProcessing: false);
});
app.MapPost("/admin/cams/refresh", async (HttpRequest r, CampRadar.Api.Cams.CamDirectory dir, CancellationToken ct) => IsAdmin(r) ? Results.Ok(new { result = await dir.RefreshAsync(ct) }) : Results.Unauthorized());
app.MapPost("/admin/cams/classify", async (HttpRequest r, CampRadar.Api.Cams.CamWorker w, string id, CancellationToken ct) => IsAdmin(r) ? Results.Ok(await w.ClassifyNowAsync(id, ct)) : Results.Unauthorized());
app.MapGet("/admin/cams/obs", (HttpRequest r, CampRadar.Api.Cams.CamStore cs, string? id, int hours = 24) => IsAdmin(r) ? Results.Ok(cs.Obs(id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - hours * 3_600_000L)) : Results.Unauthorized());
// Verification: what the tracker said versus what happened (sample airports, subscriptions, saved places)
app.MapGet("/admin/verify", (HttpRequest r, CampRadar.Api.Verify.Ledger led, CampRadar.Api.Verify.VerifyWorker vw, int days = 30) => IsAdmin(r) ? Results.Ok(new { worker = new { vw.LastCycle, vw.LastNote, points = vw.Points.Select(p => new { p.Id, p.Name }) }, summary = led.Summary(Math.Clamp(days, 1, 400)) }) : Results.Unauthorized());
app.MapGet("/admin/verify.csv", (HttpRequest r, CampRadar.Api.Verify.Ledger led, int days = 90) => IsAdmin(r) ? Results.Text(led.Csv(Math.Clamp(days, 1, 400)), "text/csv") : Results.Unauthorized());
app.MapPost("/admin/verify/run", async (HttpRequest r, CampRadar.Api.Verify.VerifyWorker vw, CancellationToken ct) => IsAdmin(r) ? Results.Ok(new { note = await vw.RunOnceAsync(ct) }) : Results.Unauthorized());
// MADIS ground truth for a finished recording (one past-time query per hour of its window, in the background); GET shows progress.
app.MapPost("/admin/verify/madis", (HttpRequest r, CampRadar.Api.Verify.VerifyWorker vw, string name) => { if (!IsAdmin(r)) return Results.Unauthorized(); vw.StartBackfill(name); return Results.Ok(new { name = Recorder.SafeName(name), state = vw.Backfills.GetValueOrDefault(Recorder.SafeName(name)) }); });
app.MapGet("/admin/verify/madis", (HttpRequest r, CampRadar.Api.Verify.VerifyWorker vw, MadisProvider madis) => IsAdmin(r) ? Results.Ok(new { enabled = madis.Enabled, queriesLastHour = madis.QueriesLastHour, backfills = vw.Backfills }) : Results.Unauthorized());
// Live MADIS probe for a point (waits for the query): the stations the app would show there.
app.MapGet("/admin/obs", async (HttpRequest r, StationObsClient obs, double lat, double lon, CancellationToken ct) => IsAdmin(r) ? Results.Ok(await obs.NearbyAsync(lat, lon, ct, canWait: true)) : Results.Unauthorized());
app.MapPost("/admin/record/start", (HttpRequest r, Recorder rec, string name, double lat, double lon, double hours = 6, double radiusMi = 200) =>
{
    if (!IsAdmin(r)) return Results.Unauthorized();
    var j = rec.Start(name, lat, lon, hours, Math.Clamp(radiusMi, 20, 400));
    return Results.Ok(new { j.Name, j.Lat, j.Lon, j.RadiusMi, j.Zoom, j.EndsAt, j.Running });
});
app.MapPost("/admin/record/{name}/stop", (HttpRequest r, Recorder rec, string name) => { if (!IsAdmin(r)) return Results.Unauthorized(); rec.Stop(name); return Results.Ok(); });
app.MapGet("/admin/record/{name}.zip", (HttpRequest r, Recorder rec, string name) =>
{
    if (!IsAdmin(r)) return Results.Unauthorized();   // the admin page downloads with the header and a blob URL, so the key never rides in a URL
    var zip = rec.ZipPath(name); if (zip is null) return Results.NotFound();
    return Results.File(Path.GetFullPath(zip), "application/zip", Recorder.SafeName(name) + ".zip");
});

app.MapGet("/admin/net/test", async (HttpRequest r, IHttpClientFactory f, CancellationToken ct) =>
{
    if (!IsAdmin(r)) return Results.Unauthorized();
    var targets = new[] { "https://api.weather.gov/", "https://generativelanguage.googleapis.com/", "https://api.anthropic.com/", "https://api.groq.com/", "https://openrouter.ai/api/v1/models", "https://api.openai.com/", "https://api.mistral.ai/", "https://gateway.ai.cloudflare.com/", "https://graph.microsoft.com/" };
    var client = f.CreateClient("llm");
    var results = new List<object>();
    foreach (var t in targets)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(8000); using var resp = await client.GetAsync(t, HttpCompletionOption.ResponseHeadersRead, cts.Token); results.Add(new { url = t, reachable = true, status = (int)resp.StatusCode, ms = sw.ElapsedMilliseconds }); }
        catch (Exception ex) { results.Add(new { url = t, reachable = false, error = ex is OperationCanceledException ? "timed out (8 s)" : ex.GetBaseException().Message, ms = sw.ElapsedMilliseconds }); }
    }
    return Results.Ok(results);
});
// Send the morning briefing now to every device that has it switched on (admin test), or to one spot id.
app.MapPost("/admin/brief/send", async (HttpRequest r, SpotStore store, BriefingFactsBuilder fb, BriefingWriter w, PushSender push, CancellationToken ct) =>
{
    if (!IsAdmin(r)) return Results.Unauthorized();
    var results = new List<object>();
    foreach (var spot in store.All())
    {
        string lang = "en", activity = "camping"; bool on = false;
        try { using var d = JsonDocument.Parse(spot.Prefs); if (d.RootElement.TryGetProperty("brief", out var b) && b.ValueKind == JsonValueKind.Object) { on = true; lang = b.TryGetProperty("lang", out var l) ? l.GetString() ?? "en" : "en"; activity = b.TryGetProperty("activity", out var a) ? a.GetString() ?? "camping" : "camping"; } } catch { }
        if (!on) continue;
        var facts = await fb.BuildAsync(spot.Lat, spot.Lon, activity, lang, ct);
        if (facts is null) { results.Add(new { spot = spot.Id[..6], ok = false, error = "no NWS forecast" }); continue; }
        var plain = PlainBriefing.Compose(facts.Value, lang);
        var (text, provider) = await w.WriteAsync(facts.Value, plain, ct); text ??= plain;
        var sentences = System.Text.RegularExpressions.Regex.Split(text, @"(?<=[.!?])\s+");
        var ok = await push.SendAsync(spot, new { title = (lang == "es" ? "Informe de la mañana" : "Morning briefing") + " (test)", body = string.Join(" ", sentences.Take(2)), tag = "brief", level = "info", brief = text, lang, at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, "brief", false, ct);
        results.Add(new { spot = spot.Id[..6], ok, provider = provider ?? "plain", activity, lang, text });
    }
    return Results.Ok(new { devices = results.Count, results });
});
app.MapPost("/admin/brief/send/{id}", async (string id, HttpRequest r, SpotStore store, BriefingFactsBuilder fb, BriefingWriter w, PushSender push, CancellationToken ct) =>
{
    if (!IsAdmin(r)) return Results.Unauthorized();
    var spot = store.Get(id); if (spot is null) return Results.NotFound();
    string lang = "en", activity = "camping";
    try { using var d = JsonDocument.Parse(spot.Prefs); if (d.RootElement.TryGetProperty("brief", out var b)) { lang = b.TryGetProperty("lang", out var l) ? l.GetString() ?? "en" : "en"; activity = b.TryGetProperty("activity", out var a) ? a.GetString() ?? "camping" : "camping"; } } catch { }
    var facts = await fb.BuildAsync(spot.Lat, spot.Lon, activity, lang, ct);
    if (facts is null) return Results.Json(new { ok = false, error = "no NWS forecast for this spot" });
    var plain = PlainBriefing.Compose(facts.Value, lang);
    var (text, provider) = await w.WriteAsync(facts.Value, plain, ct); text ??= plain;
    var sentences = System.Text.RegularExpressions.Regex.Split(text, @"(?<=[.!?])\s+");
    var ok = await push.SendAsync(spot, new { title = (lang == "es" ? "Informe de la mañana" : "Morning briefing") + " (test)", body = string.Join(" ", sentences.Take(2)), tag = "brief", level = "info", brief = text, lang, at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, "brief", false, ct);
    return Results.Ok(new { ok, provider = provider ?? "plain", text });
});
app.MapGet("/admin/entitlements", (HttpRequest r, EntitlementStore ent) => IsAdmin(r) ? Results.Ok(ent.All().Select(x => new { x.E.Email, x.E.Plan, x.E.ExpiresMs, x.E.Source, x.E.Note, x.E.UpdatedMs, active = x.E.Active, devices = x.Devices })) : Results.Unauthorized());
app.MapPost("/admin/grant", (HttpRequest r, [FromBody] GrantRequest g, EntitlementStore ent) =>
{
    if (!IsAdmin(r)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(g.Email) || !g.Email.Contains('@')) return Results.BadRequest(new { error = "email required" });
    var e = ent.Grant(g.Email, g.Months <= 0 ? "free" : (g.Plan ?? "pro"), g.Months, "admin", g.Note ?? "");
    if (g.Months <= 0) ent.RevokeAll(g.Email);
    var code = g.Months > 0 ? ent.NewCode(g.Email) : null;
    return Results.Ok(new { e.Email, e.Plan, e.ExpiresMs, active = e.Active, code });
});
app.MapPost("/admin/code", (HttpRequest r, [FromBody] GrantRequest g, EntitlementStore ent) => IsAdmin(r) ? (ent.Get(g.Email) is { Active: true } ? Results.Ok(new { code = ent.NewCode(g.Email) }) : Results.BadRequest(new { error = "no active entitlement for that email" })) : Results.Unauthorized());
app.MapGet("/admin/overview", (HttpRequest r, DeviceStore d, SpotStore s, SiteStore sites, MrmsClient m, LightningStore l, AlertWorker w, PushSender p) =>
{
    if (!IsAdmin(r)) return Results.Unauthorized();
    d.Prune(90);
    var devices = d.Recent(30);
    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var week = devices.Where(x => now - x.LastMs < 7 * 86400000L).ToList();
    return Results.Ok(new
    {
        activeNow = devices.Count(x => now - x.LastMs < 600000),
        today = devices.Count(x => now - x.LastMs < 86400000L),
        week = week.Count,
        pushSpots = s.All().Count,
        savedSites = sites.Count(),
        groups = app.Services.GetRequiredService<GroupStore>().AdminSummary(),
        alerts24 = p.SentLast24h,
        activities = week.GroupBy(x => x.Activity).ToDictionary(g => g.Key, g => g.Count()),
        devices = devices.Select(x => new { x.Id, x.Lat, x.Lon, x.Activity, x.Lang, x.Platform, x.Version, x.Push, x.FirstMs, x.LastMs, x.Opens, x.Name, x.Email }),
        health = new
        {
            radar = new { scans = m.Times.Count, latest = m.Latest > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(m.Latest) : (DateTimeOffset?)null },
            lightning = new { flashes = l.Count, lastIngest = l.LastIngestMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(l.LastIngestMs) : (DateTimeOffset?)null },
            worker = new { lastCycle = w.LastCycle },
            push = new { enabled = p.Enabled },
            briefings = new { enabled = app.Services.GetRequiredService<BriefingWriter>().Enabled, providers = app.Services.GetRequiredService<BriefingWriter>().Active, last24h = app.Services.GetRequiredService<BriefingWriter>().Stats24h() },
            email = new { enabled = app.Services.GetRequiredService<Mailer>().Enabled, transport = app.Services.GetRequiredService<Mailer>().Transport }
        }
    });
});
app.MapGet("/admin/vapid/new", (HttpRequest r) =>
{
    if (!IsAdmin(r)) return Results.Unauthorized();
    var (pub, priv) = PushSender.GenerateKeys();
    return Results.Ok(new { note = "Put these in appsettings.Production.json under Vapid, restart, then keep them forever (changing them orphans every subscriber).", publicKey = pub, privateKey = priv });
});
app.MapGet("/admin/spots", (HttpRequest r, SpotStore s) => IsAdmin(r) ? Results.Ok(s.All().Select(x => new { x.Id, x.Lat, x.Lon, x.CreatedMs, x.UpdatedMs, x.Failures, x.SnoozeUntil, x.LastBrief, prefs = JsonDocument.Parse(x.Prefs).RootElement.Clone() })) : Results.Unauthorized());

app.Run();

public sealed record SubscribeRequest(PushSub? Subscription, double Lat, double Lon, JsonElement? Prefs);
public sealed record PushSub(string Endpoint, PushKeys? Keys);
public sealed record PushKeys(string P256dh, string Auth);
public sealed record LocationRequest(double Lat, double Lon);
public sealed record SnoozeRequest(long UntilMs);
public sealed record BriefRequest(JsonElement Facts, string? Fallback, string? Style);
public sealed record RedeemRequest(string Code, string DeviceId);
public sealed record InviteRequest(string? Email, string? MyName);
public sealed record JoinRequest(string Code, string? MyName);
public sealed record ShareRequest(bool On, int Hours, string? MyName);
public sealed record PositionRequest(double Lat, double Lon, double? AccM);
public sealed record MessageRequest(string Text, string? Kind, JsonElement? Data, long? ReplyTo, string? FromSpot);
public sealed record ReplyRequest(string? ReplyToken, string? FromEmail, string? Text, long? MsgId);
public sealed record StartRequest(string Email);
public sealed record MailTestRequest(string To);
public sealed record VerifyRequest(string Email, string Code, string DeviceId);
public sealed record GrantRequest(string Email, int Months, string? Plan, string? Note);
public sealed record SiteRequest(string? Id, string? Name, double Lat, double Lon, string? Activity, bool Alerts, bool Digest, JsonElement? Thresholds);
public sealed record PlanRequest(double Lat, double Lon, int? BackMin, JsonElement? Thresholds);
public sealed record PingRequest(string Id, double Lat, double Lon, string? Activity, string? Lang, string? Platform, string? Version, bool Push, bool Opened, string? Name = null);
