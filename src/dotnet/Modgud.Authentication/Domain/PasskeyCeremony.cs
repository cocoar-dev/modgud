namespace Modgud.Authentication.Domain;

/// <summary>
/// Cookieless WebAuthn assertion ceremony for the native (ADR-0010 Phase 2)
/// <c>urn:cocoar:passkey</c> grant. The web cookie passkey flow stashes the
/// challenge in an HttpOnly cookie; a native client has no cookie, so the
/// dedicated <c>POST /connect/passkey/begin</c> endpoint persists the issued
/// <see cref="AssertionOptions"/> server-side here, keyed by a server-generated
/// Guid (the <c>ceremonyId</c> returned to the client), and the
/// <c>urn:cocoar:passkey</c> grant loads + single-use-deletes it before verifying
/// the assertion.
///
/// <para>One-time use (deleted on consume) + short TTL bound replay. Tenancy is
/// the physical per-realm DB (the document rides the tenant-scoped
/// <c>IDocumentSession</c>) — there is deliberately NO realm field, mirroring
/// <see cref="StoredPasskeyCredential"/>.</para>
/// </summary>
public class PasskeyCeremony
{
    public Guid Id { get; set; }

    /// <summary>The verbatim FIDO2 <c>AssertionOptions.ToJson()</c> issued at
    /// begin. Rehydrated via <c>AssertionOptions.FromJson</c> as the
    /// <c>OriginalOptions</c> (expected challenge) for the assertion verify.</summary>
    public string OptionsJson { get; set; } = "";

    /// <summary>
    /// The OAuth <c>client_id</c> this ceremony was begun for (ADR-0009 per-client
    /// RP-ID). <c>null</c> = a realm-scoped begin (no <c>client_id</c> sent). When
    /// non-null, the <c>urn:cocoar:passkey</c> grant asserts it equals the redeeming
    /// <c>request.ClientId</c> so a ceremony begun for one app cannot be redeemed by
    /// another (keeps token-authorization provenance unambiguous even when two
    /// clients share one RP ID).
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// The WebAuthn RP ID resolved at begin and PINNED here (ADR-0009). The grant
    /// rebuilds its <c>IFido2</c> with exactly this value and verifies the assertion
    /// against it — never re-resolving — so an admin editing the client's RP ID
    /// mid-ceremony cannot cause a begin/redeem RP-ID drift. <c>null</c> = legacy /
    /// realm-scoped (effective RP ID = realm <c>PrimaryDomain</c>).
    /// </summary>
    public string? RpId { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public const int ExpirationMinutes = 5;

    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
}
