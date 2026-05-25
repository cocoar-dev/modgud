namespace Modgud.Domain.OAuth.Consent;

/// <summary>
/// Server-side consent-flow ticket. Created at <c>/connect/authorize</c> when
/// an OAuth client with <c>ConsentType=Explicit</c> needs the user to approve
/// scopes; consumed at <c>/connect/consent</c> when the user submits their
/// decision.
///
/// <para>
/// Why a server-side ticket and not a returnUrl round-trip:
/// </para>
/// <list type="bullet">
///   <item><description><b>OAUTH-02</b> — at submit time the server validates
///   <c>ApprovedScopes ⊆ RequestedScopes</c> against the locked-in
///   <see cref="RequestedScopes"/>; users can't smuggle in extra scopes
///   by editing the form payload.</description></item>
///   <item><description><b>OAUTH-03</b> — the ticket is bound to
///   <see cref="Subject"/> at creation; an attacker cross-site-POSTing a
///   consent decision can only act on tickets the victim's own session
///   created, which closes the consent-on-behalf vector.</description></item>
///   <item><description><b>OAUTH-08</b> — the OAuth-redirect target is
///   reconstructed server-side from <see cref="AuthorizeRequestQuery"/>
///   instead of being reflected from a user-controlled <c>returnUrl</c>,
///   removing the open-redirect vector.</description></item>
/// </list>
///
/// <para>
/// Lifecycle: single-use (<see cref="ConsumedAt"/>) + short TTL
/// (<see cref="ExpiresAt"/>). A janitor process trims expired tickets;
/// already-consumed tickets are kept until natural expiry as an audit
/// breadcrumb (so a duplicate-submit attempt can be diagnosed).
/// </para>
/// </summary>
public class ConsentTicket
{
    /// <summary>
    /// Random opaque identifier surfaced as the <c>ticket</c> URL param to
    /// the SPA. Use <see cref="Guid.CreateVersion7"/> for monotonic ordering
    /// + cryptographically random content.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// User id (matches the <c>sub</c> claim of the session) that initiated
    /// the authorize request. The submit endpoint MUST reject a decision
    /// from any other principal.
    /// </summary>
    public Guid Subject { get; set; }

    /// <summary>
    /// OAuth client id of the calling RP, taken from the original authorize
    /// request and locked here so it cannot be swapped in the consent POST.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Scopes the RP requested at /connect/authorize. The user-submitted
    /// <c>ApprovedScopes</c> at /connect/consent MUST be a subset of these.
    /// </summary>
    public string[] RequestedScopes { get; set; } = [];

    /// <summary>
    /// The original /connect/authorize request's query string (everything
    /// after the <c>?</c>). Used to reconstruct the redirect target after
    /// consent — the SPA never gets to control the URL the browser is
    /// pointed at.
    /// </summary>
    public string AuthorizeRequestQuery { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// Set on first successful POST. Subsequent POSTs against the same
    /// ticket are rejected — single-use enforced.
    /// </summary>
    public DateTimeOffset? ConsumedAt { get; set; }
}
