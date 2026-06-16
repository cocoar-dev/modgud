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

    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public const int ExpirationMinutes = 5;

    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
}
