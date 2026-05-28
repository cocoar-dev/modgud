namespace Modgud.Infrastructure.OpenIddict;

/// <summary>
/// Revokes OpenIddict grants (tokens, authorizations) for a given subject —
/// the OAuth half of the user-lifecycle "kill switch" invoked when a user is
/// deactivated, deleted, or GDPR-erased.
/// <para>
/// Lives in Infrastructure so the Authentication slice can depend on it
/// without taking a direct OpenIddict reference. The subject is always
/// <c>userId.ToString()</c> ("D"-format GUID, lowercase) — the exact value the
/// auth pipeline stamps on the <c>sub</c> claim, so an ordinal match hits every
/// grant. Service-account (client_credentials) tokens carry the SA id as
/// subject and are therefore never touched by a user-id revoke.
/// </para>
/// <para>
/// Revocation flips the stored token's status to revoked. For the server
/// default — reference access + refresh tokens — that invalidates outstanding
/// tokens on the next validation/introspection immediately (no need to delete
/// the documents; PruneAsync GCs them later). NOTE the residual window: a
/// client that opts into <c>AccessTokenType.Jwt</c> receives self-validating
/// JWT access tokens with no revocable store document, so an already-issued JWT
/// access token stays valid at the resource server until its (short) lifetime
/// expires. Refresh tokens stay reference-typed and the authorize/refresh paths
/// re-check the user state, so no NEW token can be minted — only the live JWT
/// survives. Revoking refresh + authorization still cuts off continuation.
/// </para>
/// </summary>
public interface IOAuthGrantRevoker
{
    /// <summary>Revoke every access/refresh token issued for the subject, across
    /// all clients. Returns the number revoked.</summary>
    Task<int> RevokeTokensBySubjectAsync(string subject, CancellationToken ct = default);

    /// <summary>Revoke every authorization (consent grant) for the subject,
    /// across all clients. Returns the number revoked.</summary>
    Task<int> RevokeAuthorizationsBySubjectAsync(string subject, CancellationToken ct = default);
}
