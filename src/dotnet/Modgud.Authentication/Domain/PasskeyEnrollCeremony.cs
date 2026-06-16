namespace Modgud.Authentication.Domain;

/// <summary>
/// Cookieless WebAuthn ATTESTATION (registration) ceremony for the native
/// per-client passkey enrollment (ADR-0009). The web cookie register flow stashes
/// the issued <c>CredentialCreateOptions</c> in the server-side session; a native
/// client (already signed in via a <c>urn:cocoar:*</c> grant, holding a Bearer
/// access token) has no session, so the dedicated
/// <c>POST /connect/passkey/enroll/begin</c> endpoint persists the options
/// server-side here, keyed by a server-generated Guid (the <c>ceremonyId</c>
/// returned to the client). <c>POST /connect/passkey/enroll</c> loads +
/// single-use-deletes it before verifying the attestation and storing the
/// <see cref="StoredPasskeyCredential"/>.
///
/// <para>Distinct from <see cref="PasskeyCeremony"/> (the ASSERTION/login ceremony)
/// so attestation options can never be redeemed on the assertion path or vice
/// versa. One-time use (deleted on consume) + short TTL bound replay. Tenancy is
/// the physical per-realm DB (the document rides the tenant-scoped
/// <c>IDocumentSession</c>) — no realm field, mirroring
/// <see cref="StoredPasskeyCredential"/>.</para>
/// </summary>
public class PasskeyEnrollCeremony
{
    public Guid Id { get; set; }

    /// <summary>The verbatim FIDO2 <c>CredentialCreateOptions.ToJson()</c> issued at
    /// begin. Rehydrated via <c>CredentialCreateOptions.FromJson</c> as the
    /// <c>OriginalOptions</c> for the attestation verify.</summary>
    public string OptionsJson { get; set; } = "";

    /// <summary>The user the credential is being enrolled for — taken from the
    /// authenticated Bearer principal at begin (loaded via the UserManager store
    /// path so the record, not just the token claims, is authoritative).</summary>
    public Guid UserId { get; set; }

    /// <summary>The OAuth <c>client_id</c> (the token's <c>azp</c>) the enrollment
    /// was requested by. Pinned so begin and finish agree.</summary>
    public string? ClientId { get; set; }

    /// <summary>The WebAuthn RP ID resolved at begin and PINNED here (ADR-0009).
    /// finish rebuilds its <c>IFido2</c> with exactly this value, and the stored
    /// credential records this RP ID — so the value a credential is enrolled under
    /// is byte-identical to what login later demands. <c>null</c> = realm-scoped
    /// (effective RP ID = realm <c>PrimaryDomain</c>).</summary>
    public string? RpId { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public const int ExpirationMinutes = 5;

    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
}
