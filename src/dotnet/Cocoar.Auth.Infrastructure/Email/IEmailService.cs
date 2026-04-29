namespace Cocoar.Auth.Infrastructure.Email;

public interface IEmailService
{
    Task SendEmailAsync(string to, string subject, string htmlBody, CancellationToken ct = default);

    Task SendTemplatedEmailAsync(string to, EmailTemplate template, Dictionary<string, string> model, CancellationToken ct = default);

    /// <summary>
    /// Send the same templated email to multiple recipients. Used for group-addressed
    /// notifications where the recipient list is resolved via <c>IPrincipalEmailResolver</c>
    /// (shared mailbox or expanded-to-members). A deduplicated, non-empty list of valid
    /// addresses should be passed — callers are responsible for resolving and filtering.
    /// </summary>
    Task SendTemplatedEmailAsync(IReadOnlyList<string> recipients, EmailTemplate template, Dictionary<string, string> model, CancellationToken ct = default);
}
