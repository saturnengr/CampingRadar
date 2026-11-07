using System.Text.Json;
using CampRadar.Api.Admin;
using CampRadar.Api.Alerts;
using CampRadar.Api.Lightning;
using CampRadar.Api.Nws;
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
builder.Services.AddHttpClient("anthropic", c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient("noaa"));
builder.Services.AddSingleton<MrmsClient>();
builder.Services.AddSingleton<LightningStore>();
builder.Services.AddSingleton<GlmIngestor>();
builder.Services.AddSingleton<NwsClient>();
builder.Services.AddSingleton<SpotStore>();
builder.Services.AddSingleton<DeviceStore>();
builder.Services.AddSingleton<EntitlementStore>();
builder.Services.AddSingleton<PushSender>();
builder.Services.AddSingleton<StatusService>();
builder.Services.AddSingleton<BriefingWriter>();
builder.Services.AddSingleton<AlertWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AlertWorker>());
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    var origins = (builder.Configuration["AllowedOrigins"] ?? "https://saturnengr.github.io").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
}));
builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase; o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull; });

var app = builder.Build();
app.UseCors();
string adminKey = app.Configuration["AdminKey"] ?? "";
bool IsAdmin(HttpRequest r) => adminKey.Length > 0 && r.Headers["X-Admin-Key"] == adminKey;
string? Bearer(HttpRequest r) { var h = r.Headers.Authorization.ToString(); return h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..].Trim() : null; }
// Pro gate: 402 with a short reason the app shows next to the feature.
IResult? RequirePro(HttpRequest r, EntitlementStore ent) => ent.Check(Bearer(r)).Pro ? null : Results.Json(new { error = "pro", message = "This needs LeadTime Pro. Sign in or enter an activation code in Settings → Account." }, statusCode: 402);

// ---------- health / heartbeat ----------
app.MapGet("/", () => Results.Text("Camp Radar API. See /health", "text/plain"));
app.MapGet("/health", (MrmsClient m, LightningStore l, AlertWorker w, PushSender p, SpotStore s) => new
{
    ok = true, utc = DateTimeOffset.UtcNow,
    radar = new { scans = m.Times.Count, latest = m.Latest > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(m.Latest) : (DateTimeOffset?)null, refreshed = m.TimesRefreshedAt },
    lightning = new { flashes = l.Count, lastIngest = l.LastIngestMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(l.LastIngestMs) : (DateTimeOffset?)null, newestFlash = l.NewestFlashMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(l.NewestFlashMs) : (DateTimeOffset?)null },
    worker = new { lastCycle = w.LastCycle, note = w.LastCycleNote },
    push = new { enabled = p.Enabled, spots = s.All().Count },
    briefings = new { enabled = app.Services.GetRequiredService<BriefingWriter>().Enabled }
});
// The host's Scheduled Task calls this every 1–2 minutes so alerts go out even if IIS idled the app.
app.MapGet("/tick", async (AlertWorker w, CancellationToken ct) => new { ran = await w.RunCycleAsync(ct), utc = DateTimeOffset.UtcNow });

// ---------- radar ----------
app.MapGet("/radar/times", (MrmsClient m) => new { times = m.Times, latest = m.Latest, loop = m.LoopFrames(new[] { 50, 40, 30, 20, 10, 0 }), refreshed = m.TimesRefreshedAt });
app.MapGet("/radar/tile/{t:long}/{z:int}/{x:int}/{y:int}.png", async (long t, int z, int x, int y, MrmsClient m, HttpResponse res, CancellationToken ct) =>
{
    if (z < 0 || z > 10) return Results.BadRequest("zoom 0-10");
    if (m.Times.Count == 0) await m.RefreshTimesAsync(ct);
    if (!m.Times.Contains(t)) t = m.NearestTime(t);
    if (t == 0) return Results.StatusCode(503);
    var png = await m.TileAsync(t, z, x, y, ct);
    res.Headers.CacheControl = t == m.Latest ? "public, max-age=60" : "public, max-age=86400, immutable";
    return Results.Bytes(png, "image/png");
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
    var text = await w.WriteAsync(req.Facts, req.Fallback is { Length: < 4000 } ? req.Fallback : null, ct);
    return text is null ? Results.StatusCode(503) : Results.Ok(new { text, source = "backend" });
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
app.MapPost("/push/{id}/snooze", (string id, [FromBody] SnoozeRequest r, SpotStore store) => { if (store.Get(id) is null) return Results.NotFound(); store.SetSnooze(id, r.UntilMs); return Results.Ok(); });
app.MapDelete("/push/{id}", (string id, SpotStore store) => { store.Delete(id); return Results.Ok(); });
app.MapPost("/push/{id}/test", async (string id, SpotStore store, PushSender p, CancellationToken ct) =>
{
    var spot = store.Get(id); if (spot is null) return Results.NotFound();
    try
    {
        var ok = await p.SendAsync(spot, new { title = "Camp Radar test", body = "Alerts are working on this device.", tag = "test", at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, "test", false, ct);
        return ok ? Results.Ok() : Results.Json(new { error = "this device's subscription has expired; turn alerts off and on again" }, statusCode: 410);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {   // surfaced to the app's settings page so a host or key problem can be read without server logs
        var inner = ex.InnerException?.Message; return Results.Json(new { error = ex.Message + (inner is null ? "" : " — " + inner) }, statusCode: 500);
    }
});

// ---------- anonymous usage ping (the app calls this on open and every 10 minutes while open) ----------
app.MapPost("/app/ping", ([FromBody] PingRequest r, DeviceStore d) =>
{
    if (string.IsNullOrWhiteSpace(r.Id) || r.Id.Length > 40 || r.Lat is < -90 or > 90 || r.Lon is < -180 or > 180) return Results.BadRequest();
    string Clip(string? x, int n) => (x ?? "").Length > n ? x![..n] : (x ?? "");
    d.Ping(r.Id, r.Lat, r.Lon, Clip(r.Activity, 16), Clip(r.Lang, 8), Clip(r.Platform, 24), Clip(r.Version, 16), r.Push, r.Opened);
    return Results.Ok();
});

// ---------- account: activation codes → device tokens ----------
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
app.MapGet("/admin/overview", (HttpRequest r, DeviceStore d, SpotStore s, MrmsClient m, LightningStore l, AlertWorker w, PushSender p) =>
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
        alerts24 = p.SentLast24h,
        activities = week.GroupBy(x => x.Activity).ToDictionary(g => g.Key, g => g.Count()),
        devices = devices.Select(x => new { x.Id, x.Lat, x.Lon, x.Activity, x.Lang, x.Platform, x.Version, x.Push, x.FirstMs, x.LastMs, x.Opens }),
        health = new
        {
            radar = new { scans = m.Times.Count, latest = m.Latest > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(m.Latest) : (DateTimeOffset?)null },
            lightning = new { flashes = l.Count, lastIngest = l.LastIngestMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(l.LastIngestMs) : (DateTimeOffset?)null },
            worker = new { lastCycle = w.LastCycle },
            push = new { enabled = p.Enabled },
            briefings = new { enabled = app.Services.GetRequiredService<BriefingWriter>().Enabled }
        }
    });
});
app.MapGet("/admin/vapid/new", (HttpRequest r) =>
{
    if (!IsAdmin(r)) return Results.Unauthorized();
    var (pub, priv) = PushSender.GenerateKeys();
    return Results.Ok(new { note = "Put these in appsettings.Production.json under Vapid, restart, then keep them forever (changing them orphans every subscriber).", publicKey = pub, privateKey = priv });
});
app.MapGet("/admin/spots", (HttpRequest r, SpotStore s) => IsAdmin(r) ? Results.Ok(s.All().Select(x => new { x.Id, x.Lat, x.Lon, x.CreatedMs, x.UpdatedMs, x.Failures, x.SnoozeUntil })) : Results.Unauthorized());

app.Run();

public sealed record SubscribeRequest(PushSub? Subscription, double Lat, double Lon, JsonElement? Prefs);
public sealed record PushSub(string Endpoint, PushKeys? Keys);
public sealed record PushKeys(string P256dh, string Auth);
public sealed record LocationRequest(double Lat, double Lon);
public sealed record SnoozeRequest(long UntilMs);
public sealed record BriefRequest(JsonElement Facts, string? Fallback);
public sealed record RedeemRequest(string Code, string DeviceId);
public sealed record GrantRequest(string Email, int Months, string? Plan, string? Note);
public sealed record PingRequest(string Id, double Lat, double Lon, string? Activity, string? Lang, string? Platform, string? Version, bool Push, bool Opened);
