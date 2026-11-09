using Modgud.Infrastructure.Email;

namespace Modgud.Api.Tests.Infrastructure;

/// <summary>
/// Test seam for B3-EI-04: makes the next <see cref="FailTimes"/> sends of
/// <see cref="FailTemplate"/> throw, the way an unreachable SMTP server or a Postmark error would.
/// Other templates pass, so unrelated mails of the same flow do not consume the fault.
/// Reset between test classes.
/// </summary>
public sealed class EmailFaults
{
    private int _failTimes;

    public EmailTemplate? FailTemplate { get; set; }

    public int FailTimes
    {
        get => Volatile.Read(ref _failTimes);
        set => Volatile.Write(ref _failTimes, value);
    }

    public bool TryConsume(EmailTemplate template)
    {
        if (FailTemplate != template) return false;
        while (true)
        {
            var current = Volatile.Read(ref _failTimes);
            if (current <= 0) return false;
            if (Interlocked.CompareExchange(ref _failTimes, current - 1, current) == current) return true;
        }
    }

    public void Reset()
    {
        FailTimes = 0;
        FailTemplate = null;
    }
}

internal sealed class FaultInjectingEmailService(IEmailService inner, EmailFaults faults) : IEmailService
{
    public Task SendEmailAsync(string to, string subject, string htmlBody, CancellationToken ct = default) =>
        inner.SendEmailAsync(to, subject, htmlBody, ct);

    public Task SendTemplatedEmailAsync(string to, EmailTemplate template, Dictionary<string, string> model, CancellationToken ct = default) =>
        faults.TryConsume(template) ? throw Injected(template) : inner.SendTemplatedEmailAsync(to, template, model, ct);

    public Task SendTemplatedEmailAsync(IReadOnlyList<string> recipients, EmailTemplate template, Dictionary<string, string> model, CancellationToken ct = default) =>
        faults.TryConsume(template) ? throw Injected(template) : inner.SendTemplatedEmailAsync(recipients, template, model, ct);

    private static InvalidOperationException Injected(EmailTemplate template) =>
        new($"Injected failure of the mail transport for {template}.");
}
