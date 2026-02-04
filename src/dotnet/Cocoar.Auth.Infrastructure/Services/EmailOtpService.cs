using System.Security.Cryptography;
using Cocoar.Auth.Application.DTOs.Auth;
using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Auth.Domain.Events;
using ErrorOr;
using Marten;
using Microsoft.AspNetCore.Identity;

namespace Cocoar.Auth.Infrastructure.Services;

/// <summary>
/// Service for managing email-based OTP two-factor authentication.
/// </summary>
public class EmailOtpService : IEmailOtpService
{
    private readonly IDocumentSession _session;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IEmailSender _emailSender;

    private const int CodeLength = 6;
    private const int OtpExpirationMinutes = 10;
    private const int RateLimitMinutes = 2;

    public EmailOtpService(
        IDocumentSession session,
        UserManager<ApplicationUser> userManager,
        IEmailSender emailSender)
    {
        _session = session;
        _userManager = userManager;
        _emailSender = emailSender;
    }

    public async Task<ErrorOr<bool>> RequestOtpAsync(Guid userId, string? ipAddress, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return AuthErrors.UserNotFound;
        }

        if (string.IsNullOrEmpty(user.Email))
        {
            return EmailOtpErrors.EmailRequired;
        }

        // Check for existing challenge and rate limiting
        var existingChallenge = await _session.LoadAsync<EmailOtpChallenge>(userId, cancellationToken);
        if (existingChallenge is not null && !existingChallenge.IsExpired)
        {
            var timeSinceCreation = DateTimeOffset.UtcNow - existingChallenge.CreatedAt;
            if (timeSinceCreation < TimeSpan.FromMinutes(RateLimitMinutes))
            {
                return EmailOtpErrors.AlreadySent;
            }
        }

        // Generate OTP code
        var code = GenerateOtpCode();
        var codeHash = HashCode(code);

        // Create or update challenge
        var challenge = EmailOtpChallenge.Create(
            userId,
            codeHash,
            user.Email,
            user.UserName,
            TimeSpan.FromMinutes(OtpExpirationMinutes));

        _session.Store(challenge);

        // Record event
        _session.Events.Append(userId, new UserEmailOtpRequested(userId, ipAddress));

        await _session.SaveChangesAsync(cancellationToken);

        // Send OTP email
        await _emailSender.SendEmailOtpAsync(
            user.Email,
            user.UserName ?? user.Email,
            code,
            cancellationToken);

        return true;
    }

    public async Task<ErrorOr<bool>> VerifyOtpAsync(Guid userId, string code, CancellationToken cancellationToken = default)
    {
        var challenge = await _session.LoadAsync<EmailOtpChallenge>(userId, cancellationToken);
        if (challenge is null)
        {
            return EmailOtpErrors.NoPendingChallenge;
        }

        if (challenge.IsExpired)
        {
            _session.Delete(challenge);
            await _session.SaveChangesAsync(cancellationToken);
            return EmailOtpErrors.Expired;
        }

        if (challenge.HasExceededAttempts)
        {
            _session.Delete(challenge);
            await _session.SaveChangesAsync(cancellationToken);
            return EmailOtpErrors.TooManyAttempts;
        }

        // Verify code
        var normalizedCode = NormalizeCode(code);
        var codeHash = HashCode(normalizedCode);

        if (!string.Equals(codeHash, challenge.CodeHash, StringComparison.Ordinal))
        {
            challenge.IncrementAttempts();
            _session.Store(challenge);
            await _session.SaveChangesAsync(cancellationToken);

            if (challenge.HasExceededAttempts)
            {
                return EmailOtpErrors.TooManyAttempts;
            }

            return EmailOtpErrors.InvalidCode;
        }

        // Success - delete challenge and record event
        _session.Delete(challenge);
        _session.Events.Append(userId, new UserEmailOtpVerified(userId));
        await _session.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<ErrorOr<EmailOtpStatusDto>> GetStatusAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var challenge = await _session.LoadAsync<EmailOtpChallenge>(userId, cancellationToken);

        if (challenge is null || challenge.IsExpired || challenge.HasExceededAttempts)
        {
            // Check if we can request a new one (rate limiting)
            var canRequestNew = challenge is null ||
                               challenge.IsExpired ||
                               challenge.HasExceededAttempts ||
                               (DateTimeOffset.UtcNow - challenge.CreatedAt) >= TimeSpan.FromMinutes(RateLimitMinutes);

            return new EmailOtpStatusDto
            {
                IsPending = false,
                ExpiresInSeconds = null,
                AttemptsRemaining = null,
                CanRequestNew = canRequestNew
            };
        }

        var expiresIn = challenge.ExpiresAt - DateTimeOffset.UtcNow;
        var timeSinceCreation = DateTimeOffset.UtcNow - challenge.CreatedAt;

        return new EmailOtpStatusDto
        {
            IsPending = true,
            ExpiresInSeconds = (int)Math.Max(0, expiresIn.TotalSeconds),
            AttemptsRemaining = EmailOtpChallenge.MaxAttempts - challenge.Attempts,
            CanRequestNew = timeSinceCreation >= TimeSpan.FromMinutes(RateLimitMinutes)
        };
    }

    public async Task ClearChallengeAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var challenge = await _session.LoadAsync<EmailOtpChallenge>(userId, cancellationToken);
        if (challenge is not null)
        {
            _session.Delete(challenge);
            await _session.SaveChangesAsync(cancellationToken);
        }
    }

    private static string GenerateOtpCode()
    {
        // Generate a cryptographically secure random 6-digit code
        var bytes = RandomNumberGenerator.GetBytes(4);
        var number = BitConverter.ToUInt32(bytes, 0) % 1000000;
        return number.ToString("D6");
    }

    private static string HashCode(string code)
    {
        // Use SHA256 for hashing the OTP code
        var bytes = System.Text.Encoding.UTF8.GetBytes(code);
        var hash = SHA256.HashData(bytes);
        return Convert.ToBase64String(hash);
    }

    private static string NormalizeCode(string code)
    {
        // Remove any spaces or dashes that might have been added for readability
        return new string(code.Where(char.IsDigit).ToArray());
    }
}
