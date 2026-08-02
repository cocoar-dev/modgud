using System.Security.Cryptography;
using System.Text;
using ErrorOr;
using Marten;
using Marten.Patching;
using Modgud.Authentication.Applications;
using Modgud.Authentication.Domain;
using Modgud.Infrastructure.Email;

namespace Modgud.Authentication.Identity;

public class EmailOtpService(
    IDocumentSession session,
    IEmailService emailService,
    EmailOtpConfiguration config,
    IEmailBrandingResolver emailBranding) : IEmailOtpService
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

        return await IssueChallengeAsync(user, ct);
    }

    // ADR-0010 — native passwordless login. email-OTP acts here as a PRIMARY
    // factor, so (unlike the 2FA RequestOtpAsync) it does NOT require the user's
    // EmailOtpEnabled opt-in. It DOES require a confirmed, active mailbox:
    // emailing a login code to an unverified address would let a typo'd or
    // attacker-controlled mailbox become a login factor.
    public async Task<ErrorOr<bool>> RequestNativeOtpAsync(Guid userId, CancellationToken ct)
    {
        var user = await session.LoadAsync<ApplicationUser>(userId, ct);
        if (user is null)
            return Error.NotFound("EmailOtp.UserNotFound", "User not found.");
        if (string.IsNullOrEmpty(user.Email) || !user.EmailConfirmed)
            return Error.Forbidden("EmailOtp.EmailNotConfirmed", "A confirmed email address is required.");
        if (!user.IsActive || user.IsDeleted)
            return Error.Forbidden("EmailOtp.AccountInactive", "The account cannot sign in.");

        return await IssueChallengeAsync(user, ct);
    }

    // ADR-0011 — native passwordless REGISTRATION. Same as the native-login issue
    // but WITHOUT the EmailConfirmed gate: the user was just JIT-created
    // (passwordless, unconfirmed) and this code is the mailbox proof that confirms
    // it on redeem. Still requires a present email + an active, non-deleted
    // account. The endpoint only routes here under the App's JIT posture for an
    // unknown email or a still-unconfirmed passwordless registration.
    public async Task<ErrorOr<bool>> RequestNativeRegistrationOtpAsync(Guid userId, CancellationToken ct)
    {
        var user = await session.LoadAsync<ApplicationUser>(userId, ct);
        if (user is null)
            return Error.NotFound("EmailOtp.UserNotFound", "User not found.");
        if (string.IsNullOrEmpty(user.Email))
            return Error.Validation("EmailOtp.EmailRequired", "An email address is required.");
        if (!user.IsActive || user.IsDeleted)
            return Error.Forbidden("EmailOtp.AccountInactive", "The account cannot sign in.");

        return await IssueChallengeAsync(user, ct);
    }

    // Shared core: rate-limit, generate + hash + store the challenge (overwriting
    // any existing one for this user), and email the code. The per-method gates
    // (2FA opt-in vs. native confirmed-mailbox) run before this is reached.
    private async Task<ErrorOr<bool>> IssueChallengeAsync(ApplicationUser user, CancellationToken ct)
    {
        // Rate limiting: check if a recent challenge exists. A CONSUMED challenge
        // must not throttle the next request — consuming now leaves the row in
        // place (a version-checked Store is what makes the one-time use atomic;
        // see VerifyOtpAsync), whereas it used to be deleted. Without the
        // IsConsumed exemption a user who just logged in with a code would be
        // locked out of requesting a new one for the whole rate-limit window.
        var existing = await session.LoadAsync<EmailOtpChallenge>(user.Id, ct);
        if (existing is not null && !existing.IsExpired && !existing.IsConsumed)
        {
            var timeSinceCreation = DateTimeOffset.UtcNow - existing.CreatedAt;
            if (timeSinceCreation.TotalMinutes < config.RateLimitMinutes)
                return Error.Validation("EmailOtp.AlreadySent",
                    "A verification code was recently sent. Please wait before requesting a new one.");
        }

        // Generate 6-digit OTP
        var code = GenerateOtpCode();
        var codeHash = HashCode(code);

        // Store the challenge, overwriting any existing one for this user. The
        // document is version-checked (see MartenStoreOptionsExtensions), so a
        // re-issue MUST mutate the row we just loaded rather than storing a
        // fresh instance — a fresh instance carries no version and the update
        // would be rejected. Resetting ConsumedAt/Attempts is what makes the
        // new code usable after a previous one was consumed.
        if (existing is not null)
        {
            existing.CodeHash = codeHash;
            existing.Attempts = 0;
            existing.ConsumedAt = null;
            existing.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(config.ExpirationMinutes);
            existing.CreatedAt = DateTimeOffset.UtcNow;
            existing.Email = user.Email!;
            session.Store(existing);
        }
        else
        {
            session.Store(new EmailOtpChallenge
            {
                Id = user.Id,
                CodeHash = codeHash,
                Attempts = 0,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(config.ExpirationMinutes),
                CreatedAt = DateTimeOffset.UtcNow,
                Email = user.Email!,
            });
        }

        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (JasperFx.ConcurrencyException)
        {
            // Two issue requests raced. The other one won and its code is already
            // in the user's mailbox — don't send a second, conflicting code.
            return Error.Validation("EmailOtp.AlreadySent",
                "A verification code was recently sent. Please wait before requesting a new one.");
        }

        // Send email
        await emailService.SendTemplatedEmailAsync(
            user.Email!,
            EmailTemplate.EmailOtp,
            await emailBranding.ApplyAsync(new Dictionary<string, string>
            {
                ["DisplayName"] = user.Firstname ?? user.UserName ?? "",
                ["Code"] = code,
                ["ExpirationMinutes"] = config.ExpirationMinutes.ToString(),
            }, ct: ct),
            ct);

        return true;
    }

    public async Task<ErrorOr<bool>> VerifyOtpAsync(Guid userId, string code, CancellationToken ct)
    {
        var challenge = await session.LoadAsync<EmailOtpChallenge>(userId, ct);

        if (challenge is null || challenge.IsConsumed)
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
            // Audit #24 — increment the attempt counter ATOMICALLY. The endpoint is
            // anonymous and unthrottled, so this counter is the brute-force defense
            // for a 6-digit code. A read-then-Store increment lets concurrent wrong
            // guesses all read Attempts=N and overwrite each other (last writer wins
            // at N+1), so the MaxAttempts lockout never trips and the code space can
            // be exhausted. A server-side jsonb increment lands every attempt, so the
            // lockout is reliable regardless of concurrency.
            session.Patch<EmailOtpChallenge>(userId).Increment(c => c.Attempts, 1);
            await session.SaveChangesAsync(ct);
            return Error.Validation("EmailOtp.InvalidCode",
                "The verification code is invalid.");
        }

        // Success — consume the challenge with a VERSION-CHECKED Store rather than
        // a Delete. Marten does not enforce optimistic concurrency on deletes, so
        // two concurrent redemptions of the same correct code would both
        // delete-and-proceed and both authenticate. Storing ConsumedAt makes the
        // losing racer's SaveChangesAsync throw, and any later replay is rejected
        // by the IsConsumed gate above.
        challenge.ConsumedAt = DateTimeOffset.UtcNow;
        session.Store(challenge);
        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (JasperFx.ConcurrencyException)
        {
            return Error.Validation("EmailOtp.InvalidCode",
                "The verification code is invalid.");
        }

        return true;
    }

    private static string GenerateOtpCode()
    {
        // Audit #34 — rejection-sampled, modulo-bias-free. The old `4 CSPRNG bytes %
        // 1_000_000` skewed codes 0..967_295 ~0.023% higher (2^32 mod 10^6); GetInt32
        // draws uniformly over the exact range.
        return RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
    }

    private static string HashCode(string code)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(code));
        return Convert.ToHexStringLower(bytes);
    }
}
