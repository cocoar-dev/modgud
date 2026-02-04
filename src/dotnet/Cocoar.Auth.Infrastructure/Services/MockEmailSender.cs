using Cocoar.Auth.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace Cocoar.Auth.Infrastructure.Services;

/// <summary>
/// Mock email sender that logs emails instead of sending them.
/// Useful for testing and development.
/// </summary>
public class MockEmailSender : IEmailSender
{
    private readonly ILogger<MockEmailSender> _logger;
    private readonly List<SentEmail> _sentEmails = [];
    private readonly object _lock = new();

    public MockEmailSender(ILogger<MockEmailSender> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets all emails that have been "sent" (for testing purposes).
    /// </summary>
    public IReadOnlyList<SentEmail> SentEmails
    {
        get
        {
            lock (_lock)
            {
                return _sentEmails.ToList().AsReadOnly();
            }
        }
    }

    /// <summary>
    /// Clears the list of sent emails (for testing purposes).
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _sentEmails.Clear();
        }
    }

    public Task SendEmailAsync(string email, string subject, string htmlMessage, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Mock Email Sent - To: {Email}, Subject: {Subject}, Body: {Body}",
            email, subject, htmlMessage);

        lock (_lock)
        {
            _sentEmails.Add(new SentEmail(email, subject, htmlMessage, DateTimeOffset.UtcNow));
        }

        return Task.CompletedTask;
    }

    public Task SendEmailConfirmationAsync(string email, string userName, string confirmationLink, CancellationToken cancellationToken = default)
    {
        var subject = "Confirm your email";
        var body = $"""
            <h1>Email Confirmation</h1>
            <p>Hello {userName},</p>
            <p>Please confirm your email by clicking the link below:</p>
            <p><a href="{confirmationLink}">Confirm Email</a></p>
            """;

        return SendEmailAsync(email, subject, body, cancellationToken);
    }

    public Task SendPasswordResetAsync(string email, string userName, string resetLink, CancellationToken cancellationToken = default)
    {
        var subject = "Reset your password";
        var body = $"""
            <h1>Password Reset</h1>
            <p>Hello {userName},</p>
            <p>You requested a password reset. Click the link below to reset your password:</p>
            <p><a href="{resetLink}">Reset Password</a></p>
            <p>If you did not request this, please ignore this email.</p>
            """;

        return SendEmailAsync(email, subject, body, cancellationToken);
    }

    public Task SendEmailOtpAsync(string email, string userName, string code, CancellationToken cancellationToken = default)
    {
        var subject = "Your verification code";
        var body = $"""
            <h1>Verification Code</h1>
            <p>Hello {userName},</p>
            <p>Your verification code is: <strong>{code}</strong></p>
            <p>This code will expire in 10 minutes.</p>
            <p>If you did not request this code, please ignore this email.</p>
            """;

        return SendEmailAsync(email, subject, body, cancellationToken);
    }
}

public record SentEmail(string To, string Subject, string Body, DateTimeOffset SentAt);
