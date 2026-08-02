using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Modgud.Infrastructure.Email;

/// <summary>
/// In-memory email service that stores all sent emails in a thread-safe collection.
/// Optionally wraps an inner IEmailService (e.g. SmtpEmailService) to also deliver emails.
/// - Development: wraps SmtpEmailService → emails go to smtp4dev AND are queryable via dev endpoints
/// - Tests: standalone (no inner service) → emails only stored in memory
/// </summary>
public class InMemoryEmailService : IEmailService
{
    private readonly IEmailService? _inner;
    private readonly ILogger<InMemoryEmailService> _logger;
    private readonly ConcurrentQueue<SentEmail> _emails = new();

    public InMemoryEmailService(ILogger<InMemoryEmailService> logger, IEmailService? inner = null)
    {
        _logger = logger;
        _inner = inner;
    }

    public async Task SendEmailAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        _emails.Enqueue(new SentEmail(to, subject, htmlBody, DateTimeOffset.UtcNow, EmailTemplateStore.ToPlainText(htmlBody)));
        LogEmail(to, subject, htmlBody);

        if (_inner is not null)
            await TryInnerAsync(() => _inner.SendEmailAsync(to, subject, htmlBody, ct));
    }

    public async Task SendTemplatedEmailAsync(string to, EmailTemplate template, Dictionary<string, string> model, CancellationToken ct = default)
    {
        // Always store the rendered version for dev inspection
        var rendered = EmailTemplateStore.RenderMessage(template, model);
        _emails.Enqueue(new SentEmail(to, rendered.Subject, rendered.HtmlBody, DateTimeOffset.UtcNow, rendered.TextBody));
        LogEmail(to, rendered.Subject, rendered.HtmlBody);

        if (_inner is not null)
            await TryInnerAsync(() => _inner.SendTemplatedEmailAsync(to, template, model, ct));
    }

    public async Task SendTemplatedEmailAsync(IReadOnlyList<string> recipients, EmailTemplate template, Dictionary<string, string> model, CancellationToken ct = default)
    {
        if (recipients is null || recipients.Count == 0) return;
        var rendered = EmailTemplateStore.RenderMessage(template, model);
        // Store one SentEmail per recipient so GetLastEmailTo(address) still works in tests.
        foreach (var addr in recipients)
        {
            if (string.IsNullOrWhiteSpace(addr)) continue;
            _emails.Enqueue(new SentEmail(addr, rendered.Subject, rendered.HtmlBody, DateTimeOffset.UtcNow, rendered.TextBody));
            LogEmail(addr, rendered.Subject, rendered.HtmlBody);
        }

        if (_inner is not null)
            await TryInnerAsync(() => _inner.SendTemplatedEmailAsync(recipients, template, model, ct));
    }

    /// <summary>
    /// The inner service (SMTP to smtp4dev or Postmark in dev) is best-effort — a
    /// failed delivery attempt must not break the dev flow or E2E tests that only
    /// care about the captured in-memory email. Log and move on.
    /// </summary>
    private async Task TryInnerAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inner email service failed; message captured in memory only");
        }
    }

    private void LogEmail(string to, string subject, string htmlBody)
    {
        _logger.LogInformation("Email to {To}: {Subject}", to, subject);

        var urls = Regex.Matches(htmlBody, @"href=""(https?://[^""]+)""");
        foreach (Match url in urls)
        {
            _logger.LogInformation("Email Link: {Url}", url.Groups[1].Value);
        }
    }

    public IReadOnlyList<SentEmail> GetSentEmails()
        => _emails.Reverse().ToList();

    public SentEmail? GetLastEmailTo(string to)
        => _emails.Reverse().FirstOrDefault(e => e.To.Equals(to, StringComparison.OrdinalIgnoreCase));

    public void Clear() => _emails.Clear();
}

public record SentEmail(string To, string Subject, string HtmlBody, DateTimeOffset SentAt, string? TextBody = null);
