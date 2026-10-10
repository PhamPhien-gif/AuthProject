using AuthProject.Configuration;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace AuthProject.Services.Email;

public sealed class SmtpEmailTransport(IOptions<EmailOptions> options, IOptions<RegistrationOptions> registration) : IEmailTransport
{
    public async Task SendAsync(Guid messageId, string recipient, EmailContent content, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.Enabled) throw new InvalidOperationException("Email transport is disabled.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(registration.Value.SmtpTimeoutSeconds));
        using var message = new MimeMessage();
        var sender = MailboxAddress.Parse(settings.Sender);
        message.From.Add(sender);
        message.To.Add(MailboxAddress.Parse(recipient));
        message.Subject = content.Subject;
        message.MessageId = messageId.ToString("N") + "@" + sender.Address.Split('@')[1];
        message.Body = new TextPart("plain") { Text = content.TextBody };
        using var client = new SmtpClient { Timeout = registration.Value.SmtpTimeoutSeconds * 1000 };
        // Mandatory TLS, framework certificate validation, no protocol logger or permissive cert callback.
        await client.ConnectAsync(settings.Host, settings.Port, settings.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, timeout.Token);
        await client.AuthenticateAsync(settings.Username, settings.Password, timeout.Token);
        await client.SendAsync(message, timeout.Token);
        await client.DisconnectAsync(true, timeout.Token);
    }
}
