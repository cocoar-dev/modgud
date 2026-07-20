namespace Modgud.Authentication.Domain.Saml;

/// <summary>
/// A SAML AuthnRequest this SP issued and is still willing to accept a Response
/// for. Persisting it is what makes <c>InResponseTo</c> checkable: without a
/// record of the requests we sent, any correctly-signed Response is acceptable
/// at any time, which is exactly what replay needs.
///
/// <para>
/// Stored per-realm (the tenant-scoped Marten session routes it to the calling
/// realm's database), so a Response solicited in one realm can't be presented in
/// another. <see cref="Id"/> is the AuthnRequest's own ID — the natural key, and
/// the value the IdP echoes back in <c>InResponseTo</c>.
/// </para>
///
/// <para>
/// The consume is a version-checked <c>Store</c> of <see cref="ConsumedAt"/>,
/// NOT a delete: Marten does not version-check deletes, so two concurrent
/// redemptions of one captured Response would both pass a load-then-delete
/// check. Keeping the consumed row until it expires also gives the ACS a
/// truthful "already used" signal instead of an indistinguishable "unknown id".
/// </para>
/// </summary>
public sealed class SamlPendingAuthnRequest
{
    /// <summary>
    /// How long we accept a Response for a request we sent. Generous enough for a
    /// slow interactive IdP login (MFA prompts, password resets at the IdP), short
    /// enough to bound the window in which a captured Response is worth anything.
    /// </summary>
    public const int ExpirationMinutes = 15;

    /// <summary>The AuthnRequest ID we generated — echoed back as <c>InResponseTo</c>.</summary>
    public string Id { get; set; } = default!;

    /// <summary>
    /// The provider this request went to. Checked at the ACS so a Response
    /// solicited from one IdP cannot be presented at another provider's ACS
    /// within the same realm.
    /// </summary>
    public Guid LoginProviderId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Set when a Response for this request was accepted. Single-use marker.</summary>
    public DateTimeOffset? ConsumedAt { get; set; }

    public bool IsConsumed => ConsumedAt.HasValue;

    public bool IsExpired => DateTimeOffset.UtcNow > ExpiresAt;
}
