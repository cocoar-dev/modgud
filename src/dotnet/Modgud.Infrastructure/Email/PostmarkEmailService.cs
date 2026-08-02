using PostmarkDotNet;

namespace Modgud.Infrastructure.Email;

/// <summary>
/// Sends emails via the Postmark API.
/// Reads options on each send via the factory — supports reactive config updates.
/// </summary>
public class PostmarkEmailService : IEmailService
{
    private readonly Func<PostmarkEmailServiceOptions> _optionsFactory;

    public PostmarkEmailService(PostmarkEmailServiceOptions options)
        : this(() => options) { }

    public PostmarkEmailService(Func<PostmarkEmailServiceOptions> optionsFactory)
    {
        _optionsFactory = optionsFactory;
    }

    public async Task SendEmailAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        var options = _optionsFactory();
        var client = new PostmarkClient(options.ServerToken);
        var from = string.IsNullOrEmpty(options.FromName)
            ? options.FromAddress
            : $"{options.FromName} <{options.FromAddress}>";

        var message = new PostmarkMessage
        {
            From = from,
            To = to,
            Subject = subject,
            HtmlBody = htmlBody,
            TextBody = EmailTemplateStore.ToPlainText(htmlBody),
            MessageStream = options.MessageStream,
        };

        var response = await client.SendMessageAsync(message);

        if (response.Status != PostmarkStatus.Success)
        {
            throw new InvalidOperationException(
                $"Postmark email failed: {response.Status} — {response.Message}");
        }
    }

    public async Task SendTemplatedEmailAsync(string to, EmailTemplate template, Dictionary<string, string> model, CancellationToken ct = default)
    {
        var options = _optionsFactory();

        if (options.TemplateIds.TryGetValue(template, out var templateId))
        {
            var client = new PostmarkClient(options.ServerToken);
            var fromName = model.GetValueOrDefault("FromName") ?? options.FromName;
            var from = string.IsNullOrEmpty(fromName)
                ? options.FromAddress
                : $"{fromName} <{options.FromAddress}>";

            var message = new TemplatedPostmarkMessage
            {
                From = from,
                To = to,
                TemplateId = templateId,
                TemplateModel = model,
                ReplyTo = model.GetValueOrDefault("ReplyTo"),
                MessageStream = options.MessageStream,
            };

            var response = await client.SendEmailWithTemplateAsync(message);

            if (response.Status != PostmarkStatus.Success)
            {
                throw new InvalidOperationException(
                    $"Postmark templated email failed: {response.Status} — {response.Message}");
            }
        }
        else
        {
            var rendered = EmailTemplateStore.RenderMessage(template, model);
            await SendRenderedAsync(to, rendered, ct);
        }
    }

    public async Task SendTemplatedEmailAsync(IReadOnlyList<string> recipients, EmailTemplate template, Dictionary<string, string> model, CancellationToken ct = default)
    {
        if (recipients is null || recipients.Count == 0) return;
        var valid = recipients.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (valid.Count == 0) return;

        var options = _optionsFactory();
        // Postmark expects recipients as a comma-separated string in the To field.
        var toHeader = string.Join(",", valid);

        if (options.TemplateIds.TryGetValue(template, out var templateId))
        {
            var client = new PostmarkClient(options.ServerToken);
            var fromName = model.GetValueOrDefault("FromName") ?? options.FromName;
            var from = string.IsNullOrEmpty(fromName)
                ? options.FromAddress
                : $"{fromName} <{options.FromAddress}>";
            var message = new TemplatedPostmarkMessage
            {
                From = from,
                To = toHeader,
                TemplateId = templateId,
                TemplateModel = model,
                ReplyTo = model.GetValueOrDefault("ReplyTo"),
                MessageStream = options.MessageStream,
            };
            var response = await client.SendEmailWithTemplateAsync(message);
            if (response.Status != PostmarkStatus.Success)
                throw new InvalidOperationException($"Postmark templated email failed: {response.Status} — {response.Message}");
        }
        else
        {
            var rendered = EmailTemplateStore.RenderMessage(template, model);
            await SendRenderedAsync(toHeader, rendered, ct);
        }
    }

    private async Task SendRenderedAsync(string to, RenderedEmail rendered, CancellationToken ct)
    {
        var options = _optionsFactory();
        var client = new PostmarkClient(options.ServerToken);
        var fromName = rendered.FromName ?? options.FromName;
        var from = string.IsNullOrEmpty(fromName)
            ? options.FromAddress
            : $"{fromName} <{options.FromAddress}>";
        var response = await client.SendMessageAsync(new PostmarkMessage
        {
            From = from,
            To = to,
            Subject = rendered.Subject,
            HtmlBody = rendered.HtmlBody,
            TextBody = rendered.TextBody,
            ReplyTo = rendered.ReplyTo,
            MessageStream = options.MessageStream,
        });
        if (response.Status != PostmarkStatus.Success)
            throw new InvalidOperationException($"Postmark email failed: {response.Status} — {response.Message}");
    }
}

public class PostmarkEmailServiceOptions
{
    public string ServerToken { get; set; } = "";
    public string FromAddress { get; set; } = "noreply@cocoar.dev";
    public string FromName { get; set; } = "Modgud";
    public string MessageStream { get; set; } = "outbound";
    public Dictionary<EmailTemplate, long> TemplateIds { get; set; } = new();
}
