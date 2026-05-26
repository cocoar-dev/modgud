using MailKit.Net.Smtp;
using MimeKit;

namespace Modgud.Infrastructure.Email;

/// <summary>
/// Sends emails via SMTP using MailKit.
/// Reads options on each send via the factory — supports reactive config updates.
/// </summary>
public class SmtpEmailService : IEmailService
{
    private readonly Func<SmtpEmailServiceOptions> _optionsFactory;

    public SmtpEmailService(SmtpEmailServiceOptions options)
        : this(() => options) { }

    public SmtpEmailService(Func<SmtpEmailServiceOptions> optionsFactory)
    {
        _optionsFactory = optionsFactory;
    }

    public async Task SendEmailAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        var options = _optionsFactory();
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(options.FromName, options.FromAddress));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new TextPart("html") { Text = htmlBody };

        using var client = new SmtpClient();
        await client.ConnectAsync(options.Host, options.Port, options.UseSsl, ct);

        if (!string.IsNullOrEmpty(options.UserName))
        {
            if (options.Password is null)
                throw new InvalidOperationException("SMTP Password must be configured when UserName is set.");
            await client.AuthenticateAsync(options.UserName, options.Password, ct);
        }

        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }

    public Task SendTemplatedEmailAsync(string to, EmailTemplate template, Dictionary<string, string> model, CancellationToken ct = default)
    {
        var (subject, htmlBody) = EmailTemplateStore.Render(template, model);
        return SendEmailAsync(to, subject, htmlBody, ct);
    }

    public async Task SendTemplatedEmailAsync(IReadOnlyList<string> recipients, EmailTemplate template, Dictionary<string, string> model, CancellationToken ct = default)
    {
        if (recipients is null || recipients.Count == 0) return;

        var options = _optionsFactory();
        var (subject, htmlBody) = EmailTemplateStore.Render(template, model);

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(options.FromName, options.FromAddress));
        foreach (var addr in recipients)
        {
            if (!string.IsNullOrWhiteSpace(addr))
                message.To.Add(MailboxAddress.Parse(addr));
        }
        if (message.To.Count == 0) return;

        message.Subject = subject;
        message.Body = new TextPart("html") { Text = htmlBody };

        using var client = new SmtpClient();
        await client.ConnectAsync(options.Host, options.Port, options.UseSsl, ct);
        if (!string.IsNullOrEmpty(options.UserName))
        {
            if (options.Password is null)
                throw new InvalidOperationException("SMTP Password must be configured when UserName is set.");
            await client.AuthenticateAsync(options.UserName, options.Password, ct);
        }
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }
}

public class SmtpEmailServiceOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 25;
    public bool UseSsl { get; set; } = false;
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string FromAddress { get; set; } = "noreply@cocoar.dev";
    public string FromName { get; set; } = "Modgud";
}
