namespace Cocoar.Auth.Application.Interfaces;

/// <summary>
/// Interface for sending emails.
/// </summary>
public interface IEmailSender
{
    Task SendEmailAsync(string email, string subject, string htmlMessage, CancellationToken cancellationToken = default);
    Task SendEmailConfirmationAsync(string email, string userName, string confirmationLink, CancellationToken cancellationToken = default);
    Task SendPasswordResetAsync(string email, string userName, string resetLink, CancellationToken cancellationToken = default);
    Task SendEmailOtpAsync(string email, string userName, string code, CancellationToken cancellationToken = default);
}
