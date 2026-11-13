using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace CampRadar.Api.Mail;

/// <summary>
/// Sends the sign-in code emails. Two transports, picked by configuration:
///  - Microsoft Graph (preferred with Microsoft 365): Graph:TenantId, Graph:ClientId, Graph:ClientSecret, Graph:From.
///    The app registration needs the Mail.Send application permission with admin consent; From is the mailbox to send as.
///  - SMTP: Smtp:Host, Smtp:Port, Smtp:User, Smtp:Password, Smtp:From (Office 365 SMTP AUTH, Resend, Postmark, any relay).
/// With neither configured, email sign-in is off and the app says so; activation codes still work.
/// </summary>
public sealed class Mailer
{
    private readonly IConfiguration _cfg; private readonly ILogger<Mailer> _log; private readonly HttpClient _http;
    private string? _token; private DateTimeOffset _tokenExpires;
    public Mailer(IConfiguration cfg, ILogger<Mailer> log, IHttpClientFactory f) { _cfg = cfg; _log = log; _http = f.CreateClient("graph"); }

    public bool GraphEnabled => !string.IsNullOrWhiteSpace(_cfg["Graph:TenantId"]) && !string.IsNullOrWhiteSpace(_cfg["Graph:ClientId"]) && !string.IsNullOrWhiteSpace(_cfg["Graph:ClientSecret"]) && !string.IsNullOrWhiteSpace(_cfg["Graph:From"]);
    public bool SmtpEnabled => !string.IsNullOrWhiteSpace(_cfg["Smtp:Host"]) && !string.IsNullOrWhiteSpace(_cfg["Smtp:From"]);
    public bool Enabled => GraphEnabled || SmtpEnabled;
    public string Transport => GraphEnabled ? "graph" : SmtpEnabled ? "smtp" : "none";
    public string ProductName => _cfg["ProductName"] ?? "VantageWX";
    public string SupportAddress => _cfg["SupportEmail"] ?? "support@vantagewx.com";

    public Task SendAsync(string to, string subject, string text, CancellationToken ct) => GraphEnabled ? SendGraphAsync(to, subject, text, ct) : SendSmtpAsync(to, subject, text, ct);

    public Task SendCodeAsync(string to, string code, CancellationToken ct) => SendAsync(to, $"{code} is your {ProductName} sign-in code",
        $"Your {ProductName} sign-in code is:\n\n    {code}\n\nEnter it in the app within 10 minutes. If you didn't ask for this, you can ignore this email.\n\nQuestions: {SupportAddress}\n— {ProductName}", ct);

    // ---- SMTP ----
    private async Task SendSmtpAsync(string to, string subject, string text, CancellationToken ct)
    {
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(_cfg["Smtp:From"]));
        msg.To.Add(MailboxAddress.Parse(to));
        msg.Subject = subject;
        msg.Body = new TextPart("plain") { Text = text };
        using var client = new SmtpClient();
        int port = int.TryParse(_cfg["Smtp:Port"], out var p) ? p : 587;
        var security = port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
        await client.ConnectAsync(_cfg["Smtp:Host"], port, security, ct);
        if (!string.IsNullOrEmpty(_cfg["Smtp:User"])) await client.AuthenticateAsync(_cfg["Smtp:User"], _cfg["Smtp:Password"], ct);
        await client.SendAsync(msg, ct);
        await client.DisconnectAsync(true, ct);
    }

    // ---- Microsoft Graph: client-credentials token, then POST /users/{from}/sendMail ----
    private async Task<string> GraphTokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _tokenExpires) return _token;
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _cfg["Graph:ClientId"]!, ["client_secret"] = _cfg["Graph:ClientSecret"]!,
            ["scope"] = "https://graph.microsoft.com/.default", ["grant_type"] = "client_credentials"
        });
        using var resp = await _http.PostAsync($"https://login.microsoftonline.com/{_cfg["Graph:TenantId"]}/oauth2/v2.0/token", form, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Graph token request failed ({(int)resp.StatusCode}): {body}");
        using var doc = JsonDocument.Parse(body);
        _token = doc.RootElement.GetProperty("access_token").GetString();
        _tokenExpires = DateTimeOffset.UtcNow.AddSeconds(doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() - 60 : 3000);
        return _token!;
    }

    private async Task SendGraphAsync(string to, string subject, string text, CancellationToken ct)
    {
        var from = _cfg["Graph:From"]!;
        var fromName = _cfg["Graph:FromName"] ?? ProductName;
        var payload = new
        {
            message = new
            {
                subject,
                body = new { contentType = "Text", content = text },
                from = new { emailAddress = new { address = from, name = fromName } },
                toRecipients = new[] { new { emailAddress = new { address = to } } }
            },
            saveToSentItems = false
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(from)}/sendMail");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GraphTokenAsync(ct));
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            if ((int)resp.StatusCode == 401) _token = null;
            throw new InvalidOperationException($"Graph sendMail failed ({(int)resp.StatusCode}): {body}");
        }
    }
}
