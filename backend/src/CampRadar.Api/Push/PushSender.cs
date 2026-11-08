using System.Text.Json;
using CampRadar.Api.Spots;
using WebPush;

namespace CampRadar.Api.Push;

/// <summary>Web Push (VAPID) to the home-screen app. Keys come from configuration.</summary>
public sealed class PushSender
{
    private readonly WebPushClient _client = new(new HttpClient(new SocketsHttpHandler { ConnectCallback = async (ctx, ct) => { var s = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true }; try { var addrs = await System.Net.Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, System.Net.Sockets.AddressFamily.InterNetwork, ct); await s.ConnectAsync(addrs, ctx.DnsEndPoint.Port, ct); return new System.Net.Sockets.NetworkStream(s, true); } catch { s.Dispose(); throw; } } }) { Timeout = TimeSpan.FromSeconds(15) });   // IPv4 only (googleapis hangs on v6 here) and a short timeout so one dead endpoint cannot stall the loop
    private readonly VapidDetails? _vapid;
    private readonly ILogger<PushSender> _log;
    public string? PublicKey { get; }
    public bool Enabled => _vapid is not null;

    public PushSender(IConfiguration cfg, ILogger<PushSender> log)
    {
        _log = log;
        var pub = cfg["Vapid:PublicKey"]; var priv = cfg["Vapid:PrivateKey"]; var subject = cfg["Vapid:Subject"] ?? "mailto:admin@example.com";
        if (!string.IsNullOrWhiteSpace(pub) && !string.IsNullOrWhiteSpace(priv)) { _vapid = new VapidDetails(subject, pub, priv); PublicKey = pub; }
        else log.LogWarning("VAPID keys not configured; push disabled. Run: GET /admin/vapid/new (one time) and put the keys in appsettings or environment.");
    }

    public static (string PublicKey, string PrivateKey) GenerateKeys()
    {
        var k = VapidHelper.GenerateVapidKeys();
        return (k.PublicKey, k.PrivateKey);
    }

    /// <summary>Returns false if the subscription is gone (404/410) and should be deleted.</summary>
    private readonly Queue<long> _sent = new();
    /// <summary>Notifications sent in the last 24 hours (for the admin page).</summary>
    public int SentLast24h { get { lock (_sent) { long cut = DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeMilliseconds(); while (_sent.Count > 0 && _sent.Peek() < cut) _sent.Dequeue(); return _sent.Count; } } }
    private void NoteSent() { lock (_sent) { _sent.Enqueue(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); if (_sent.Count > 100000) _sent.Dequeue(); } }

    public async Task<bool> SendAsync(Spot spot, object payload, string tag, bool urgent, CancellationToken ct)
    {
        if (_vapid is null) return true;
        var sub = new PushSubscription(spot.Endpoint, spot.P256dh, spot.Auth);
        var json = JsonSerializer.Serialize(payload);
        // Urgency and Topic are Web Push headers (RFC 8030); the WebPush library only knows vapidDetails, TTL, headers and gcmAPIKey.
        var headers = new Dictionary<string, object> { ["Urgency"] = urgent ? "high" : "normal", ["Topic"] = tag };
        var opts = new Dictionary<string, object> { ["vapidDetails"] = _vapid, ["TTL"] = urgent ? 600 : 1800, ["headers"] = headers };
        try { await _client.SendNotificationAsync(sub, json, opts, ct); NoteSent(); return true; }
        catch (WebPushException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
        { _log.LogInformation("subscription gone: {Id}", spot.Id); return false; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { _log.LogWarning(ex, "push failed for {Id}", spot.Id); throw; }
    }
}
