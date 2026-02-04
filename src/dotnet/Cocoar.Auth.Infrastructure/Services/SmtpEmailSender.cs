using System.Net;
using System.Net.Mail;
using Cocoar.Auth.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace Cocoar.Auth.Infrastructure.Services;

/// <summary>
/// SMTP-based email sender for production use.
/// </summary>
public class SmtpEmailSender : IEmailSender
{
    private readonly ILogger<SmtpEmailSender> _logger;
    private readonly SmtpEmailSenderOptions _options;

    public SmtpEmailSender(ILogger<SmtpEmailSender> logger, SmtpEmailSenderOptions options)
    {
        _logger = logger;
        _options = options;
    }

    public async Task SendEmailAsync(string email, string subject, string htmlMessage, CancellationToken cancellationToken = default)
    {
        using var client = CreateSmtpClient();
        using var message = new MailMessage
        {
            From = new MailAddress(_options.FromAddress, _options.FromName),
            Subject = subject,
            Body = htmlMessage,
            IsBodyHtml = true
        };
        message.To.Add(email);

        try
        {
            await client.SendMailAsync(message, cancellationToken);
            _logger.LogInformation("Email sent successfully to {Email} with subject: {Subject}", email, subject);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {Email} with subject: {Subject}", email, subject);
            throw;
        }
    }

    public Task SendEmailConfirmationAsync(string email, string userName, string confirmationLink, CancellationToken cancellationToken = default)
    {
        var subject = "Confirm your email";
        var body = $"""
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset="utf-8">
                <title>Email Confirmation</title>
            </head>
            <body style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; max-width: 600px; margin: 0 auto; padding: 20px;">
                <h1 style="color: #333;">Email Confirmation</h1>
                <p>Hello {userName},</p>
                <p>Please confirm your email address by clicking the button below:</p>
                <p style="margin: 30px 0;">
                    <a href="{confirmationLink}"
                       style="background-color: #007bff; color: white; padding: 12px 24px; text-decoration: none; border-radius: 4px; display: inline-block;">
                        Confirm Email
                    </a>
                </p>
                <p style="color: #666; font-size: 14px;">
                    Or copy and paste this link into your browser:<br>
                    <a href="{confirmationLink}" style="color: #007bff;">{confirmationLink}</a>
                </p>
                <hr style="border: none; border-top: 1px solid #eee; margin: 30px 0;">
                <p style="color: #999; font-size: 12px;">
                    If you did not create an account, please ignore this email.
                </p>
            </body>
            </html>
            """;

        return SendEmailAsync(email, subject, body, cancellationToken);
    }

    public Task SendPasswordResetAsync(string email, string userName, string resetLink, CancellationToken cancellationToken = default)
    {
        var subject = "Reset your password";
        var body = $"""
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset="utf-8">
                <title>Password Reset</title>
            </head>
            <body style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; max-width: 600px; margin: 0 auto; padding: 20px;">
                <h1 style="color: #333;">Password Reset</h1>
                <p>Hello {userName},</p>
                <p>You requested a password reset. Click the button below to reset your password:</p>
                <p style="margin: 30px 0;">
                    <a href="{resetLink}"
                       style="background-color: #007bff; color: white; padding: 12px 24px; text-decoration: none; border-radius: 4px; display: inline-block;">
                        Reset Password
                    </a>
                </p>
                <p style="color: #666; font-size: 14px;">
                    Or copy and paste this link into your browser:<br>
                    <a href="{resetLink}" style="color: #007bff;">{resetLink}</a>
                </p>
                <p style="color: #666; font-size: 14px;">
                    This link will expire in 24 hours.
                </p>
                <hr style="border: none; border-top: 1px solid #eee; margin: 30px 0;">
                <p style="color: #999; font-size: 12px;">
                    If you did not request this password reset, please ignore this email.<br>
                    Your password will remain unchanged.
                </p>
            </body>
            </html>
            """;

        return SendEmailAsync(email, subject, body, cancellationToken);
    }

    public Task SendEmailOtpAsync(string email, string userName, string code, CancellationToken cancellationToken = default)
    {
        var subject = "Your verification code";
        var body = $"""
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset="utf-8">
                <title>Verification Code</title>
            </head>
            <body style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; max-width: 600px; margin: 0 auto; padding: 20px;">
                <h1 style="color: #333;">Verification Code</h1>
                <p>Hello {userName},</p>
                <p>Your verification code is:</p>
                <p style="margin: 30px 0; text-align: center;">
                    <span style="font-size: 32px; font-weight: bold; letter-spacing: 8px; background-color: #f5f5f5; padding: 16px 32px; border-radius: 8px; display: inline-block;">
                        {code}
                    </span>
                </p>
                <p style="color: #666; font-size: 14px;">
                    This code will expire in 10 minutes.
                </p>
                <hr style="border: none; border-top: 1px solid #eee; margin: 30px 0;">
                <p style="color: #999; font-size: 12px;">
                    If you did not request this code, please ignore this email.<br>
                    Someone may have entered your email address by mistake.
                </p>
            </body>
            </html>
            """;

        return SendEmailAsync(email, subject, body, cancellationToken);
    }

    private SmtpClient CreateSmtpClient()
    {
        var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.UseSsl
        };

        if (!string.IsNullOrEmpty(_options.Username) && !string.IsNullOrEmpty(_options.Password))
        {
            client.Credentials = new NetworkCredential(_options.Username, _options.Password);
        }

        return client;
    }
}

/// <summary>
/// Options for configuring the SMTP email sender.
/// </summary>
public class SmtpEmailSenderOptions
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public bool UseSsl { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public required string FromAddress { get; init; }
    public required string FromName { get; init; }
}
