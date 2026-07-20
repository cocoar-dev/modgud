using Marten;
using Modgud.Authentication.Domain.Saml;

namespace Modgud.Authentication.Identity.LoginProviders.Saml;

/// <summary>
/// Tracks the AuthnRequests this SP has issued so an inbound SAML Response can be
/// correlated to one (<c>InResponseTo</c>) and accepted exactly once.
///
/// <para>
/// Backed by the tenant-scoped Marten session rather than a cookie or a
/// process-local cache, for two reasons: the ACS POST is cross-site, so a
/// <c>SameSite=Lax</c> cookie does not arrive (the same constraint that already
/// degrades the SAML link-flow); and the check has to hold across every instance
/// pointed at the same realm database, not just the one that issued the request.
/// </para>
/// </summary>
public interface ISamlAuthnRequestStore
{
    /// <summary>
    /// Record an AuthnRequest we are about to send, so the matching Response can
    /// be correlated back to it.
    /// </summary>
    Task RecordAsync(string requestId, Guid loginProviderId, CancellationToken ct);

    /// <summary>
    /// Atomically claim the pending request identified by <paramref name="requestId"/>.
    /// Returns the outcome so the caller can tell an unsolicited Response from a
    /// replayed one — they warrant different audit reasons, though both are refused.
    /// </summary>
    Task<SamlAuthnRequestConsumeResult> TryConsumeAsync(
        string? requestId, Guid loginProviderId, CancellationToken ct);
}

public enum SamlAuthnRequestConsumeResult
{
    /// <summary>Correlated to a live pending request, now spent.</summary>
    Consumed,

    /// <summary>No <c>InResponseTo</c> at all — an unsolicited (IdP-initiated) Response.</summary>
    Unsolicited,

    /// <summary>The referenced request is unknown to this realm, or already pruned.</summary>
    Unknown,

    /// <summary>The referenced request was already answered — a replay.</summary>
    AlreadyConsumed,

    /// <summary>The request is ours but too old to still answer.</summary>
    Expired,

    /// <summary>The request was sent to a different provider than the ACS it arrived at.</summary>
    ProviderMismatch,
}

internal sealed class MartenSamlAuthnRequestStore(IDocumentSession session) : ISamlAuthnRequestStore
{
    public async Task RecordAsync(string requestId, Guid loginProviderId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        // Opportunistic prune of requests that can no longer be answered anyway —
        // keeps the table bounded without a background job. The ExpiresAt index
        // makes this a small range delete, usually zero rows.
        session.DeleteWhere<SamlPendingAuthnRequest>(x => x.ExpiresAt < now);

        // Insert, not Store: the ID is generated per request, so a collision would
        // mean something is badly wrong and should fail loudly rather than
        // silently overwrite a pending request.
        session.Insert(new SamlPendingAuthnRequest
        {
            Id = requestId,
            LoginProviderId = loginProviderId,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(SamlPendingAuthnRequest.ExpirationMinutes),
        });

        await session.SaveChangesAsync(ct);
    }

    public async Task<SamlAuthnRequestConsumeResult> TryConsumeAsync(
        string? requestId, Guid loginProviderId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            return SamlAuthnRequestConsumeResult.Unsolicited;

        var pending = await session.LoadAsync<SamlPendingAuthnRequest>(requestId, ct);
        if (pending is null)
            return SamlAuthnRequestConsumeResult.Unknown;

        if (pending.LoginProviderId != loginProviderId)
            return SamlAuthnRequestConsumeResult.ProviderMismatch;

        if (pending.IsConsumed)
            return SamlAuthnRequestConsumeResult.AlreadyConsumed;

        if (pending.IsExpired)
            return SamlAuthnRequestConsumeResult.Expired;

        // Version-checked Store of the consume marker (see the type doc for why
        // this is not a Delete). Mutating the LOADED instance keeps the version
        // chain intact; storing a freshly-built instance would carry no version
        // and the concurrency check would not fire.
        pending.ConsumedAt = DateTimeOffset.UtcNow;
        session.Store(pending);
        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (JasperFx.ConcurrencyException)
        {
            // A concurrent replay of the same captured Response claimed it first.
            return SamlAuthnRequestConsumeResult.AlreadyConsumed;
        }

        return SamlAuthnRequestConsumeResult.Consumed;
    }
}
