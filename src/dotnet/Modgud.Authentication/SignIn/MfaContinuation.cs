using System.Security.Cryptography;
using System.Text;
using JasperFx;
using Marten;

namespace Modgud.Authentication.SignIn;

/// <summary>
/// ADR 0025 §8 — a native sign-in that proved its first factor but still owes a second.
/// The App redeems the token either natively (the same native grant with <c>mfa_token</c>
/// and the second factor) or by starting its authorization request with
/// <c>mfa_token=…</c>, which opens the login page directly at the missing factor.
/// Short-lived, single-use, bound to the user, the client and the factors proven so far;
/// only the SHA-256 of the token is stored.
/// </summary>
public sealed class MfaContinuation
{
    public const int LifetimeMinutes = 10;

    public Guid Id { get; set; }
    public string TokenHash { get; set; } = "";
    public Guid UserId { get; set; }
    public string ClientId { get; set; } = "";
    public Dictionary<string, DateTimeOffset> Factors { get; set; } = new(StringComparer.Ordinal);
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

public interface IMfaContinuationService
{
    /// <summary>Issue a continuation and return the opaque token handed to the App.</summary>
    Task<string> IssueAsync(Guid userId, string clientId, IReadOnlyDictionary<string, DateTimeOffset> factors, CancellationToken ct);

    /// <summary>Consume a continuation for <paramref name="clientId"/>. Null when unknown,
    /// expired, already used, or issued to another client; a concurrent second redemption
    /// loses the version check and gets null too.</summary>
    Task<MfaContinuation?> RedeemAsync(string? token, string? clientId, CancellationToken ct);
}

public sealed class MfaContinuationService(IDocumentSession session) : IMfaContinuationService
{
    public async Task<string> IssueAsync(
        Guid userId, string clientId, IReadOnlyDictionary<string, DateTimeOffset> factors, CancellationToken ct)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = DateTimeOffset.UtcNow;
        session.DeleteWhere<MfaContinuation>(c => c.ExpiresAt < now);
        session.Store(new MfaContinuation
        {
            Id = Guid.NewGuid(),
            TokenHash = MfaContinuation.Hash(token),
            UserId = userId,
            ClientId = clientId,
            Factors = new Dictionary<string, DateTimeOffset>(factors, StringComparer.Ordinal),
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(MfaContinuation.LifetimeMinutes),
        });
        await session.SaveChangesAsync(ct);
        return token;
    }

    public async Task<MfaContinuation?> RedeemAsync(string? token, string? clientId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(clientId)) return null;
        var hash = MfaContinuation.Hash(token);
        var continuation = await session.Query<MfaContinuation>().FirstOrDefaultAsync(c => c.TokenHash == hash, ct);
        if (continuation is null
            || continuation.ConsumedAt is not null
            || continuation.ExpiresAt <= DateTimeOffset.UtcNow
            || !string.Equals(continuation.ClientId, clientId, StringComparison.Ordinal))
            return null;

        // Version-checked store, not a delete: Marten does not version-check deletes,
        // so two concurrent redemptions would both succeed.
        continuation.ConsumedAt = DateTimeOffset.UtcNow;
        session.Store(continuation);
        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (ConcurrencyException)
        {
            return null;
        }
        return continuation;
    }
}
