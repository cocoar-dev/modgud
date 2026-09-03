using System.Security.Cryptography;
using System.Text;
using ErrorOr;
using Marten;
using Marten.Patching;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Modgud.Authentication.Applications;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Identity;
using Modgud.Authentication.SelfRegistration;
using Modgud.Authorization.Principals;
using Modgud.Domain.Users.Events;
using Modgud.Infrastructure.Email;

namespace Modgud.Authentication.Registration;

/// <summary>What a sign-up path hands to the pipeline. Everything the user will need at
/// creation time is decided here, at request time, and travels on the pending record.</summary>
public sealed record RegistrationRequest(
    string Email,
    string UserName,
    string? Firstname,
    string? Lastname,
    string? PasswordHash,
    RegistrationProofKind ProofKind,
    string Source,
    Guid? ApplicationId = null,
    string? ClientId = null,
    string? ReturnUrl = null,
    string? LinkBaseUrl = null,
    string[]? DefaultGroupIds = null,
    bool RequireAdminApproval = false,
    Guid? ConsumedInviteId = null);

/// <summary>
/// Outcome of a request. Every value except <see cref="Sent"/> is SILENT by design —
/// the public endpoints answer uniformly regardless (anti-enumeration); the outcome
/// exists for logs, metrics and tests.
/// </summary>
public enum RegistrationRequestOutcome
{
    /// <summary>Pending record written (or refreshed) and the proof mailed.</summary>
    Sent,

    /// <summary>A proof was mailed to this address less than the cooldown ago; nothing sent.</summary>
    Cooldown,

    /// <summary>A concurrent request for the same address won the write; nothing sent.</summary>
    LostRace,

    /// <summary>The address already belongs to a user; the pipeline is never entered.</summary>
    AddressTaken,

    /// <summary>The silent per-source registration ceiling (ADR 0007) was hit: this
    /// source sprayed too many unknown addresses. Nothing written, nothing sent.</summary>
    Throttled,
}

/// <summary>The account that a successful proof materialised.</summary>
public sealed record RegisteredUser(
    ApplicationUser User,
    bool RequiresAdminApproval,
    string Source,
    RegistrationProofKind ProofKind,
    string? ReturnUrl);

/// <summary>
/// ADR 0006 — one registration pipeline for every public sign-up path.
/// <list type="bullet">
///   <item><see cref="RequestAsync"/> upserts the address's <see cref="PendingRegistration"/>
///   and mails the proof. No user exists afterwards.</item>
///   <item><see cref="ProveCodeAsync"/> / <see cref="ProveLinkAsync"/> verify the proof and
///   create the <see cref="ApplicationUser"/> exactly once, confirmed, in the same unit of
///   work that consumes the pending record.</item>
///   <item><see cref="RegisterWithoutProofAsync"/> is the explicit opt-out for realms that
///   disabled email verification: the user is created immediately, no pending record.</item>
/// </list>
/// Requests for an address that already belongs to a user are refused with
/// <see cref="RegistrationRequestOutcome.AddressTaken"/>; login / resend semantics for
/// existing accounts stay with the callers.
/// </summary>
public interface IRegistrationPipeline
{
    Task<RegistrationRequestOutcome> RequestAsync(RegistrationRequest request, CancellationToken ct = default);

    Task<ErrorOr<RegisteredUser>> ProveCodeAsync(string email, string code, CancellationToken ct = default);

    Task<ErrorOr<RegisteredUser>> ProveLinkAsync(string token, CancellationToken ct = default);

    Task<ErrorOr<RegisteredUser>> RegisterWithoutProofAsync(RegistrationRequest request, CancellationToken ct = default);

    /// <summary>Hard-deletes expired and consumed pending records. Returns the count.</summary>
    Task<int> SweepAsync(CancellationToken ct = default);
}

public sealed class RegistrationPipeline(
    IDocumentSession session,
    UserManager<ApplicationUser> userManager,
    IEmailService emailService,
    IEmailBrandingResolver emailBranding,
    IRegistrationInviteService inviteService,
    Modgud.Authentication.RateLimiting.IRegistrationThrottle throttle,
    ILogger<RegistrationPipeline> logger) : IRegistrationPipeline
{
    /// <summary>Lifetime of a numeric code — same as the login OTP.</summary>
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(EmailOtpChallenge.ExpirationMinutes);

    /// <summary>Lifetime of a verification link — same as the previous web pending token.</summary>
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(24);

    /// <summary>Minimum gap between two proofs to the same address — same as the login OTP.</summary>
    public static readonly TimeSpan ResendCooldown = TimeSpan.FromMinutes(EmailOtpChallenge.RateLimitMinutes);

    public const int CodeMaxAttempts = EmailOtpChallenge.MaxAttempts;

    public const string ErrorNoPendingProof = "Registration.NoPendingProof";
    public const string ErrorExpired = "Registration.Expired";
    public const string ErrorTooManyAttempts = "Registration.TooManyAttempts";
    public const string ErrorInvalidProof = "Registration.InvalidProof";
    public const string ErrorAlreadyConsumed = "Registration.AlreadyConsumed";
    public const string ErrorRejected = "Registration.Rejected";
    public const string ErrorAddressTaken = "Registration.AddressTaken";

    // ── Request ──────────────────────────────────────────────────────────────

    public async Task<RegistrationRequestOutcome> RequestAsync(RegistrationRequest request, CancellationToken ct = default)
    {
        var normalized = PendingRegistration.NormalizeEmail(request.Email);
        if (await AddressBelongsToUserAsync(normalized, ct))
            return RegistrationRequestOutcome.AddressTaken;

        // ADR 0007 — entering the pipeline for an unknown address IS the spraying
        // signal; the ceiling is silent so a 429 never reveals existence.
        if (!await throttle.AllowAsync(ct))
            return RegistrationRequestOutcome.Throttled;

        var id = PendingRegistration.IdFor(normalized);
        var now = DateTimeOffset.UtcNow;
        var existing = await session.LoadAsync<PendingRegistration>(id, ct);

        if (existing is not null && !existing.IsExpired && !existing.IsConsumed
            && now - existing.LastSentAt < ResendCooldown)
        {
            logger.LogInformation(
                "Registration: cooldown, no proof sent (source={Source}, email={Email})",
                request.Source, LogPiiMasking.MaskEmail(request.Email));
            return RegistrationRequestOutcome.Cooldown;
        }

        var (secret, hash) = NewSecret(request.ProofKind);
        // Re-issue MUST mutate the loaded row (it carries the version); a fresh instance
        // for an existing id would be rejected by the optimistic-concurrency check.
        var pending = existing ?? new PendingRegistration { Id = id, CreatedAt = now };
        var fresh = existing is null || existing.IsExpired || existing.IsConsumed;
        Fill(pending, request, normalized, now);
        pending.SecretHash = hash;
        pending.Attempts = 0;
        pending.ExpiresAt = now + Lifetime(request.ProofKind);
        pending.LastSentAt = now;
        pending.SendCount = fresh ? 1 : existing!.SendCount + 1;
        pending.ConsumedAt = null;
        if (fresh) pending.CreatedAt = now;

        session.Store(pending);
        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is JasperFx.ConcurrencyException or JasperFx.DocumentAlreadyExistsException)
        {
            // The other request for this address won and its proof is on the way.
            session.EjectAllPendingChanges();
            return RegistrationRequestOutcome.LostRace;
        }

        await SendProofAsync(pending, secret, ct);
        return RegistrationRequestOutcome.Sent;
    }

    // ── Prove ────────────────────────────────────────────────────────────────

    public async Task<ErrorOr<RegisteredUser>> ProveCodeAsync(string email, string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(code))
            return Error.Validation(ErrorNoPendingProof, "No pending registration for this address.");

        var id = PendingRegistration.IdFor(email);
        var pending = await session.LoadAsync<PendingRegistration>(id, ct);
        if (pending is null || pending.IsConsumed || pending.ProofKind != RegistrationProofKind.Code)
            return Error.Validation(ErrorNoPendingProof, "No pending registration for this address.");

        var gate = await GateAsync(pending, ct);
        if (gate is not null) return gate.Value;

        if (Hash(code.Trim()) != pending.SecretHash)
        {
            // Atomic server-side increment: concurrent wrong guesses must all count
            // (a read-then-store would let them overwrite each other).
            session.Patch<PendingRegistration>(id).Increment(p => p.Attempts, 1);
            await session.SaveChangesAsync(ct);
            return Error.Validation(ErrorInvalidProof, "The code is invalid.");
        }

        return await MaterializeAsync(pending, persisted: true, ct);
    }

    public async Task<ErrorOr<RegisteredUser>> ProveLinkAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Error.Validation(ErrorNoPendingProof, "Verification token is invalid.");

        var hash = Hash(token);
        var pending = await session.Query<PendingRegistration>()
            .FirstOrDefaultAsync(p => p.SecretHash == hash && p.ProofKind == RegistrationProofKind.Link, ct);
        if (pending is null)
            return Error.Validation(ErrorNoPendingProof, "Verification token is invalid.");
        if (pending.IsConsumed)
            return Error.Validation(ErrorAlreadyConsumed, "Verification token has already been used.");

        var gate = await GateAsync(pending, ct);
        if (gate is not null) return gate.Value;

        return await MaterializeAsync(pending, persisted: true, ct);
    }

    public async Task<ErrorOr<RegisteredUser>> RegisterWithoutProofAsync(RegistrationRequest request, CancellationToken ct = default)
    {
        var normalized = PendingRegistration.NormalizeEmail(request.Email);
        if (await AddressBelongsToUserAsync(normalized, ct))
            return Error.Conflict(ErrorAddressTaken, "The address already belongs to an account.");

        var now = DateTimeOffset.UtcNow;
        var transient = new PendingRegistration { Id = PendingRegistration.IdFor(normalized), CreatedAt = now };
        Fill(transient, request, normalized, now);
        transient.ExpiresAt = now;
        return await MaterializeAsync(transient, persisted: false, ct);
    }

    // ── Sweep ────────────────────────────────────────────────────────────────

    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var count = await session.Query<PendingRegistration>()
            .CountAsync(p => p.ExpiresAt < now || p.ConsumedAt != null, ct);
        if (count == 0) return 0;
        session.DeleteWhere<PendingRegistration>(p => p.ExpiresAt < now || p.ConsumedAt != null);
        await session.SaveChangesAsync(ct);
        return count;
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private Task<bool> AddressBelongsToUserAsync(string normalizedEmail, CancellationToken ct) =>
        session.Query<ApplicationUser>()
            .AnyAsync(u => u.NormalizedEmail == normalizedEmail && !u.IsDeleted, ct);

    /// <summary>Expiry / attempt gates shared by both proofs. A failed gate deletes the
    /// record: nothing can be proved against it any more.</summary>
    private async Task<ErrorOr<RegisteredUser>?> GateAsync(PendingRegistration pending, CancellationToken ct)
    {
        if (pending.IsExpired)
        {
            session.Delete(pending);
            await session.SaveChangesAsync(ct);
            return Error.Validation(ErrorExpired, "The registration has expired. Please request a new one.");
        }
        if (pending.HasExceededAttempts)
        {
            session.Delete(pending);
            await session.SaveChangesAsync(ct);
            return Error.Validation(ErrorTooManyAttempts, "Too many failed attempts. Please request a new code.");
        }
        return null;
    }

    /// <summary>
    /// Creates the user from the pending record — the ONLY place a public sign-up
    /// materialises an <see cref="ApplicationUser"/>. Atomicity: the pending consume is a
    /// version-checked <c>Store</c> queued on the same session that
    /// <c>UserManager.CreateAsync</c> flushes, so two concurrent proofs of one record end
    /// with exactly one user and one <c>ConcurrencyException</c>. A registration event is
    /// appended to the new stream and the pending row is hard-deleted.
    /// </summary>
    private async Task<ErrorOr<RegisteredUser>> MaterializeAsync(PendingRegistration pending, bool persisted, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (persisted)
        {
            pending.ConsumedAt = now;
            session.Store(pending);
        }

        var user = new ApplicationUser(pending.UserName, pending.Email)
        {
            Id = Guid.NewGuid(),
            Firstname = pending.Firstname,
            Lastname = pending.Lastname,
            IsActive = !pending.RequireAdminApproval,
            EmailConfirmed = true,
            PasswordHash = pending.PasswordHash,
            RegistrationSource = pending.Source,
            RegisteredAt = now,
        };

        IdentityResult created;
        try
        {
            // Flushes the consume + the user creation in one transaction.
            created = await userManager.CreateAsync(user);
        }
        catch (Exception ex) when (IsLostRace(ex))
        {
            // The other proof of this record committed first: version check on the
            // consume, or the unique address/username index on the user insert —
            // whichever the batch hit first. Either way exactly one user exists.
            session.EjectAllPendingChanges();
            return Error.Conflict(ErrorAlreadyConsumed, "This registration has already been completed.");
        }

        if (!created.Succeeded)
        {
            // Validation refused (e.g. the username was taken between request and proof).
            // Nothing was written; drop the queued consume so the record stays provable
            // after the person picks another name — and never leave a half-created user.
            session.EjectAllPendingChanges();
            var codes = string.Join(';', created.Errors.Select(e => e.Code));
            logger.LogInformation(
                "Registration: Identity refused user creation (source={Source}, email={Email}, errors={Errors})",
                pending.Source, LogPiiMasking.MaskEmail(pending.Email), codes);
            return Error.Validation(ErrorRejected, $"The account could not be created: {codes}.");
        }

        foreach (var raw in pending.DefaultGroupIds)
        {
            if (!Guid.TryParse(raw, out var gid)) continue;
            var group = await session.LoadAsync<Group>(gid, ct);
            if (group is null || group.IsDeleted) continue;
            if (!group.MemberIds.Contains(user.Id))
            {
                group.MemberIds.Add(user.Id);
                session.Store(group);
            }
        }

        var proofKind = persisted ? pending.ProofKind.ToString() : "None";
        session.Events.Append(user.Id, new UserRegisteredEvent(user.Id, pending.Source, proofKind));
        if (persisted) session.Delete(pending);
        await session.SaveChangesAsync(ct);

        if (pending.ConsumedInviteId is { } inviteId)
        {
            try
            {
                await inviteService.AttachConsumerAsync(inviteId, user.Id, ct);
            }
            catch (Exception ex)
            {
                // The account exists and the code was already consumed at request time;
                // the back-link is bookkeeping only.
                logger.LogWarning(ex, "Registration: could not link invite {InviteId} to user {UserId}", inviteId, user.Id);
            }
        }

        logger.LogInformation(
            "Registration: user {UserId} created (source={Source}, proof={Proof}, approval-required={Approval})",
            user.Id, pending.Source, proofKind, pending.RequireAdminApproval);

        return new RegisteredUser(user, pending.RequireAdminApproval, pending.Source, pending.ProofKind, pending.ReturnUrl);
    }

    private static void Fill(PendingRegistration pending, RegistrationRequest request, string normalizedEmail, DateTimeOffset now)
    {
        pending.Email = request.Email.Trim();
        pending.NormalizedEmail = normalizedEmail;
        pending.UserName = request.UserName.Trim();
        pending.Firstname = string.IsNullOrWhiteSpace(request.Firstname) ? null : request.Firstname.Trim();
        pending.Lastname = string.IsNullOrWhiteSpace(request.Lastname) ? null : request.Lastname.Trim();
        pending.PasswordHash = request.PasswordHash;
        pending.ApplicationId = request.ApplicationId;
        pending.ClientId = request.ClientId;
        pending.ReturnUrl = request.ReturnUrl;
        pending.LinkBaseUrl = request.LinkBaseUrl;
        pending.DefaultGroupIds = request.DefaultGroupIds ?? [];
        pending.RequireAdminApproval = request.RequireAdminApproval;
        pending.ConsumedInviteId = request.ConsumedInviteId;
        pending.Source = request.Source;
        pending.ProofKind = request.ProofKind;
        pending.MaxAttempts = request.ProofKind == RegistrationProofKind.Code ? CodeMaxAttempts : 0;
        _ = now;
    }

    private async Task SendProofAsync(PendingRegistration pending, string secret, CancellationToken ct)
    {
        var displayName = !string.IsNullOrWhiteSpace(pending.Firstname)
            ? $"{pending.Firstname} {pending.Lastname}".Trim()
            : pending.UserName;

        try
        {
            if (pending.ProofKind == RegistrationProofKind.Code)
            {
                await emailService.SendTemplatedEmailAsync(
                    pending.Email,
                    EmailTemplate.EmailOtp,
                    await emailBranding.ApplyAsync(new Dictionary<string, string>
                    {
                        ["DisplayName"] = displayName,
                        ["Code"] = secret,
                        ["ExpirationMinutes"] = ((int)CodeLifetime.TotalMinutes).ToString(),
                    }, pending.ApplicationId, pending.ClientId, ct),
                    ct);
            }
            else
            {
                var url = $"{pending.LinkBaseUrl}/verify-email?token={Uri.EscapeDataString(secret)}";
                if (!string.IsNullOrEmpty(pending.ReturnUrl))
                    url += $"&redirect={Uri.EscapeDataString(pending.ReturnUrl)}";

                await emailService.SendTemplatedEmailAsync(
                    pending.Email,
                    EmailTemplate.EmailVerification,
                    await emailBranding.ApplyAsync(new Dictionary<string, string>
                    {
                        ["DisplayName"] = displayName,
                        ["ActionUrl"] = url,
                        ["ExpirationHours"] = ((int)LinkLifetime.TotalHours).ToString(),
                    }, pending.ApplicationId, pending.ClientId, ct),
                    ct);
            }
        }
        catch (Exception ex)
        {
            // Delivery problems must not turn into a response-shape difference.
            logger.LogWarning(ex,
                "Registration: proof delivery failed (source={Source}, email={Email})",
                pending.Source, LogPiiMasking.MaskEmail(pending.Email));
        }
    }

    /// <summary>Optimistic-concurrency loss or a unique-index violation (23505) on the
    /// user insert — both mean a concurrent writer won.</summary>
    private static bool IsLostRace(Exception ex) => ex switch
    {
        JasperFx.ConcurrencyException => true,
        JasperFx.DocumentAlreadyExistsException => true,
        Marten.Exceptions.MartenCommandException { InnerException: Npgsql.PostgresException { SqlState: "23505" } } => true,
        Npgsql.PostgresException { SqlState: "23505" } => true,
        _ => false,
    };

    private static TimeSpan Lifetime(RegistrationProofKind kind) =>
        kind == RegistrationProofKind.Code ? CodeLifetime : LinkLifetime;

    private static (string Secret, string Hash) NewSecret(RegistrationProofKind kind)
    {
        // Code: rejection-sampled 6 digits (no modulo bias). Link: 32 CSPRNG bytes, base64url.
        var secret = kind == RegistrationProofKind.Code
            ? RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6")
            : Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return (secret, Hash(secret));
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
