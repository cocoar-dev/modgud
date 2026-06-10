using System.Security.Cryptography;
using System.Text;
using ErrorOr;
using Marten;
using Modgud.Authentication.Domain;
using Modgud.Infrastructure.Email;

namespace Modgud.Authentication.Identity;

public class EmailOtpService(IDocumentSession session, IEmailService emailService, EmailOtpConfiguration config) : IEmailOtpService
{
    public async Task<ErrorOr<bool>> RequestOtpAsync(Guid userId, CancellationToken ct)
    {
        var user = await session.LoadAsync<ApplicationUser>(userId, ct);
        if (user is null)
            return Error.NotFound("EmailOtp.UserNotFound", "User not found.");
        // Audit M2: only issue an email OTP to a user who actually enabled it as
        // a second factor. Without this, a TOTP-only account (the partial-2FA
        // cookie is set for any 2FA-enabled user) could be downgraded to a
        // mailbox-based factor the user never opted into.
        if (!user.EmailOtpEnabled)
            return Error.Forbidden("EmailOtp.NotEnabled", "Email OTP is not enabled for this account.");
        if (string.IsNullOrEmpty(user.Email))
            return Error.Validation("EmailOtp.EmailRequired", "A verified email address is required to use email OTP.");

        // Rate limiting: check if a recent challenge exists
        var existing = await session.LoadAsync<EmailOtpChallenge>(userId, ct);
        if (existing is not null && !existing.IsExpired)
        {
            var timeSinceCreation = DateTimeOffset.UtcNow - existing.CreatedAt;
            if (timeSinceCreation.TotalMinutes < config.RateLimitMinutes)
                return Error.Validation("EmailOtp.AlreadySent",
                    "A verification code was recently sent. Please wait before requesting a new one.");
        }

        // Generate 6-digit OTP
        var code = GenerateOtpCode();
        var codeHash = HashCode(code);

        // Store challenge (overwrites existing)
        var challenge = new EmailOtpChallenge
        {
            Id = userId,
            CodeHash = codeHash,
            Attempts = 0,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(config.ExpirationMinutes),
            CreatedAt = DateTimeOffset.UtcNow,
            Email = user.Email,
        };
        session.Store(challenge);
        await session.SaveChangesAsync(ct);

        // Send email
        await emailService.SendTemplatedEmailAsync(
            user.Email,
            EmailTemplate.EmailOtp,
            new Dictionary<string, string>
            {
                ["AppName"] = "Modgud",
                ["DisplayName"] = user.Firstname ?? user.UserName ?? "",
                ["Code"] = code,
                ["ExpirationMinutes"] = config.ExpirationMinutes.ToString(),
            },
            ct);

        return true;
    }

    public async Task<ErrorOr<bool>> VerifyOtpAsync(Guid userId, string code, CancellationToken ct)
    {
        var challenge = await session.LoadAsync<EmailOtpChallenge>(userId, ct);

        if (challenge is null)
            return Error.Validation("EmailOtp.NoPendingChallenge",
                "No pending verification code found. Please request a new one.");

        if (challenge.IsExpired)
        {
            session.Delete(challenge);
            await session.SaveChangesAsync(ct);
            return Error.Validation("EmailOtp.Expired",
                "The verification code has expired. Please request a new one.");
        }

        if (challenge.Attempts >= config.MaxAttempts)
        {
            session.Delete(challenge);
            await session.SaveChangesAsync(ct);
            return Error.Validation("EmailOtp.TooManyAttempts",
                "Too many failed attempts. Please request a new code.");
        }

        var codeHash = HashCode(code.Trim());
        if (codeHash != challenge.CodeHash)
        {
            challenge.Attempts++;
            session.Store(challenge);
            await session.SaveChangesAsync(ct);
            return Error.Validation("EmailOtp.InvalidCode",
                "The verification code is invalid.");
        }

        // Success — delete challenge
        session.Delete(challenge);
        await session.SaveChangesAsync(ct);

        return true;
    }

    private static string GenerateOtpCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(4);
        var number = BitConverter.ToUInt32(bytes, 0) % 1000000;
        return number.ToString("D6");
    }

    private static string HashCode(string code)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(code));
        return Convert.ToHexStringLower(bytes);
    }
}
