using Modgud.Authentication.Applications;

namespace Modgud.Api;

/// <summary>
/// <see cref="IEmailSenderDefaults"/> backed by the live <see cref="EmailConfiguration"/>:
/// the same values <c>SmtpEmailService</c> / <c>PostmarkEmailService</c> read on every
/// send, so the admin preview's "From" line matches what actually goes out — for
/// whichever provider is configured, and reactively after a config change. Takes a
/// reader delegate (the config manager's own type is internal to its package).
/// </summary>
public sealed class ConfiguredEmailSenderDefaults(Func<EmailConfiguration?> current) : IEmailSenderDefaults
{
    public string FromAddress => current() switch
    {
        { Provider: EmailProvider.Postmark } c => c.Postmark.FromAddress,
        { } c => c.Smtp.FromAddress,
        null => "noreply@localhost",
    };

    public string FromName => current() switch
    {
        { Provider: EmailProvider.Postmark } c => c.Postmark.FromName,
        { } c => c.Smtp.FromName,
        null => "",
    };
}
