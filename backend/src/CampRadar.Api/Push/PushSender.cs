using System.Text.Json;
using CampRadar.Api.Spots;
using WebPush;

namespace CampRadar.Api.Push;

/// <summary>Web Push (VAPID) to the home-screen app. Keys come from configuration.</summary>
public sealed class PushSender
{
    private readonly WebPushClient _client = new();
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
    public async Task<bool> SendAsync(Spot spot, object payload, string tag, bool urgent, CancellationToken ct)
    {
        if (_vapid is null) return true;
        var sub = new PushSubscription(spot.Endpoint, spot.P256dh, spot.Auth);
        var json = JsonSerializer.Serialize(payload);
        var opts = new Dictionary<string, object> { ["vapidDetails"] = _vapid, ["TTL"] = urgent ? 600 : 1800, ["urgency"] = urgent ? "high" : "normal", ["topic"] = tag };
        try { await _client.SendNotificationAsync(sub, json, opts, ct); return true; }
        catch (WebPushException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
        { _log.LogInformation("subscription gone: {Id}", spot.Id); return false; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { _log.LogWarning(ex, "push failed for {Id}", spot.Id); throw; }
    }
}
