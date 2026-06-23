using System.Security.Cryptography;
using System.Text;
using JasperFx;
using Marten;
using Microsoft.Extensions.Logging;
using Modgud.Authentication.SelfRegistration.Domain;

namespace Modgud.Authentication.SelfRegistration;

/// <summary>
/// Mints, validates + consumes single-use registration invite codes (ADR-0012).
/// Tenant-scoped: the injected <see cref="IDocumentSession"/> tracks the current
/// realm DB. Minting is a privileged action (gated at the endpoint by the
/// <c>invite:write</c> OAuth scope or the <c>invite-code:write</c> permission);
/// redemption happens implicitly on the native sign-up path under the
/// <c>InviteCode</c> posture.
///
/// <para>Code shape mirrors <c>PendingAdminInvite</c>: 32 random bytes →
/// Base64Url (~128-bit, URL-safe, 43 chars), stored as a SHA-256 hex hash. The
/// plaintext is returned to the minting caller exactly once.</para>
/// </summary>
public interface IRegistrationInviteService
{
    /// <summary>Mint <paramref name="count"/> fresh codes for an App. Returns
    /// the plaintext codes (only available here — afterwards only the hash is
    /// recoverable). <paramref name="boundEmail"/> null = bearer codes (D2);
    /// <paramref name="expiresInDays"/> null = the Modgud default of 14 (D10).</summary>
    Task<IReadOnlyList<string>> MintAsync(
        Guid appId,
        string? boundEmail,
        int? expiresInDays,
        string createdBySubject,
        int count,
        CancellationToken ct = default);

    /// <summary>
    /// Atomic single-use gate (D4/§5). Hashes <paramref name="code"/>, looks it
    /// up for <paramref name="appId"/>, rejects if absent / used / expired /
    /// email-mismatched, then marks it used under optimistic concurrency and
    /// commits. Exactly one of two concurrent redemptions of the same bearer
    /// code wins; the loser's stale version-checked update throws and is treated
    /// as a rejection. MUST be called BEFORE user creation so the gate closes the
    /// bearer race (creating the user first would let two requests each make an
    /// account before either consumes).
    /// </summary>
    Task<InviteConsumeResult> TryConsumeAsync(
        Guid appId,
        string email,
        string code,
        CancellationToken ct = default);

    /// <summary>Best-effort audit linkage: records which user a just-consumed
    /// code created. Non-atomic and non-critical — a crash here leaves the code
    /// consumed with a null <c>UsedByUserId</c>, which is benign.</summary>
    Task AttachConsumerAsync(Guid inviteId, Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<RegistrationInviteCode>> ListAsync(Guid appId, CancellationToken ct = default);

    /// <summary>Realm-wide overview: every app's codes in the current tenant,
    /// newest first. Backs the admin grid (permission-gated, no M2M equivalent).</summary>
    Task<IReadOnlyList<RegistrationInviteCode>> ListAllAsync(CancellationToken ct = default);

    /// <summary>Revoke an unused code (delete before use). Returns false if the
    /// code doesn't exist for this App or is already consumed.</summary>
    Task<bool> RevokeAsync(Guid appId, Guid id, CancellationToken ct = default);

    /// <summary>Hygiene sweep (ADR-0012 §8): hard-delete used or expired codes in
    /// the current tenant. Not correctness-critical — expired-but-unpruned codes
    /// already fail validation. Returns the number deleted.</summary>
    Task<int> PruneAsync(CancellationToken ct = default);
}

/// <summary>Outcome of a consume attempt. Only <see cref="Consumed"/> permits
/// account creation; every other value routes the caller to the uniform no-op
/// (anti-enumeration — the reason is for logs only, never surfaced).</summary>
public enum InviteConsumeOutcome
{
    Consumed,
    NoCode,
    NotFound,
    AlreadyUsed,
    Expired,
    EmailMismatch,
    LostRace,
}

public readonly record struct InviteConsumeResult(InviteConsumeOutcome Outcome, Guid InviteId)
{
    public bool IsConsumed => Outcome == InviteConsumeOutcome.Consumed;
    public static readonly InviteConsumeResult NoCode = new(InviteConsumeOutcome.NoCode, Guid.Empty);
    public static InviteConsumeResult Rejected(InviteConsumeOutcome outcome) => new(outcome, Guid.Empty);
    public static InviteConsumeResult Ok(Guid inviteId) => new(InviteConsumeOutcome.Consumed, inviteId);
}

public sealed class RegistrationInviteService(
    IDocumentSession session,
    ILogger<RegistrationInviteService> logger) : IRegistrationInviteService
{
    public async Task<IReadOnlyList<string>> MintAsync(
        Guid appId,
        string? boundEmail,
        int? expiresInDays,
        string createdBySubject,
        int count,
        CancellationToken ct = default)
    {
        if (count < 1) count = 1;
        var days = expiresInDays is > 0 ? expiresInDays.Value : RegistrationInviteCode.DefaultExpirationDays;
        var normalizedEmail = Normalize(boundEmail);
        var now = DateTimeOffset.UtcNow;

        var plaintexts = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var code = GenerateCode();
            session.Store(new RegistrationInviteCode
            {
                Id = Guid.NewGuid(),
                AppId = appId,
                CodeHash = Hash(code),
                BoundEmail = normalizedEmail,
                ExpiresAt = now.AddDays(days),
                CreatedAt = now,
                CreatedBySubject = createdBySubject,
            });
            plaintexts.Add(code);
        }

        await session.SaveChangesAsync(ct);
        logger.LogInformation(
            "Minted {Count} invite code(s) for App {AppId} (bound={Bound}, expiresInDays={Days}) by {Subject}.",
            count, appId, normalizedEmail is not null, days, createdBySubject);
        return plaintexts;
    }

    public async Task<InviteConsumeResult> TryConsumeAsync(
        Guid appId,
        string email,
        string code,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            return InviteConsumeResult.NoCode;

        var hash = Hash(code);
        var invite = await session.Query<RegistrationInviteCode>()
            .FirstOrDefaultAsync(c => c.AppId == appId && c.CodeHash == hash, ct);

        if (invite is null)
            return InviteConsumeResult.Rejected(InviteConsumeOutcome.NotFound);
        if (invite.IsUsed)
            return InviteConsumeResult.Rejected(InviteConsumeOutcome.AlreadyUsed);
        if (invite.IsExpired)
            return InviteConsumeResult.Rejected(InviteConsumeOutcome.Expired);
        if (invite.BoundEmail is not null && invite.BoundEmail != Normalize(email))
            return InviteConsumeResult.Rejected(InviteConsumeOutcome.EmailMismatch);

        invite.UsedAt = DateTimeOffset.UtcNow;
        session.Update(invite); // version-checked (optimistic concurrency)
        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (ConcurrencyException)
        {
            // Lost the single-use race: another redemption committed first. Treat
            // as already-used so the caller routes to the uniform no-op.
            return InviteConsumeResult.Rejected(InviteConsumeOutcome.LostRace);
        }

        return InviteConsumeResult.Ok(invite.Id);
    }

    public async Task AttachConsumerAsync(Guid inviteId, Guid userId, CancellationToken ct = default)
    {
        var invite = await session.LoadAsync<RegistrationInviteCode>(inviteId, ct);
        if (invite is null) return;
        invite.UsedByUserId = userId;
        session.Update(invite);
        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (ConcurrencyException)
        {
            // Best-effort audit linkage only; a lost race here is benign.
        }
    }

    public async Task<IReadOnlyList<RegistrationInviteCode>> ListAllAsync(CancellationToken ct = default)
    {
        var codes = await session.Query<RegistrationInviteCode>()
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);
        return codes;
    }

    public async Task<IReadOnlyList<RegistrationInviteCode>> ListAsync(Guid appId, CancellationToken ct = default)
    {
        var codes = await session.Query<RegistrationInviteCode>()
            .Where(c => c.AppId == appId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);
        return codes;
    }

    public async Task<bool> RevokeAsync(Guid appId, Guid id, CancellationToken ct = default)
    {
        var invite = await session.LoadAsync<RegistrationInviteCode>(id, ct);
        if (invite is null || invite.AppId != appId || invite.IsUsed)
            return false;
        session.Delete(invite);
        await session.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> PruneAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var stale = await session.Query<RegistrationInviteCode>()
            .Where(c => c.UsedAt != null || c.ExpiresAt < now)
            .ToListAsync(ct);
        if (stale.Count == 0) return 0;
        foreach (var code in stale)
            session.Delete(code);
        await session.SaveChangesAsync(ct);
        return stale.Count;
    }

    // 32 random bytes → Base64Url (~128-bit entropy after the 256-bit draw is
    // fine; mirrors PendingAdminInvite). URL-safe so it embeds in an app link.
    private static string GenerateCode() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string Hash(string code) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    private static string? Normalize(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();
}
