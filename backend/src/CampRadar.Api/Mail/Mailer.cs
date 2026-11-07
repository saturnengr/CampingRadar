using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace CampRadar.Api.Mail;

/// <summary>
/// Sends the sign-in code emails over SMTP. Configure Smtp:Host, Smtp:Port, Smtp:User, Smtp:Password and Smtp:From
/// (any mailbox on the hosting account works). Without them, email sign-in is off and the app says so.
/// </summary>
public sealed class Mailer
{
    private readonly IConfiguration _cfg; private readonly ILogger<Mailer> _log;
    public Mailer(IConfiguration cfg, ILogger<Mailer> log) { _cfg = cfg; _log = log; }
    public bool Enabled => !string.IsNullOrWhiteSpace(_cfg["Smtp:Host"]) && !string.IsNullOrWhiteSpace(_cfg["Smtp:From"]);
    public string ProductName => _cfg["ProductName"] ?? "LeadTime";

    public async Task SendAsync(string to, string subject, string text, CancellationToken ct)
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

    public Task SendCodeAsync(string to, string code, CancellationToken ct) => SendAsync(to, $"{code} is your {ProductName} sign-in code",
        $"Your {ProductName} sign-in code is:\n\n    {code}\n\nEnter it in the app within 10 minutes. If you didn't ask for this, you can ignore this email.\n\n— {ProductName}", ct);
}
