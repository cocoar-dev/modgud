namespace Modgud.Domain.Realms;

/// <summary>
/// Per-realm configuration for Client ID Metadata Documents (CIMD,
/// <c>draft-ietf-oauth-client-id-metadata-document</c>) — the
/// MCP-preferred client-registration path. Lives as a sub-document on the
/// tenant-DB <see cref="RealmSettings.RealmSettings"/> aggregate alongside
/// <see cref="DcrSettings"/>. Owned by the realm-admin. Default-disabled:
/// every realm starts with <c>Enabled=false</c> so the authorization server
/// never fetches a stranger's metadata URL until an admin opts in.
///
/// <para>With CIMD the <c>client_id</c> <em>is</em> an HTTPS URL pointing
/// at a JSON metadata document the server fetches on demand and treats as
/// the (non-persisted) client registration — no open registration
/// endpoint, no client secret, identity bound to domain ownership. v1 is
/// public-only (<c>token_endpoint_auth_method=none</c> + PKCE);
/// <c>private_key_jwt</c> is deferred to v2.</para>
///
/// <para>Like DCR, this master toggle only gates whether the server
/// resolves CIMD <c>client_id</c>s at all and whether
/// <c>client_id_metadata_document_supported</c> shows up in the discovery
/// document. A CIMD client still has to clear the same per-resource-server
/// (<c>OAuthApi.AllowDynamicRegistration</c>) opt-in DCR clients do before
/// it can request a token for any audience — flipping this on does not, by
/// itself, expose anything.</para>
/// </summary>
public record CimdSettings
{
    /// <summary>
    /// Master toggle. When <c>false</c>, a CIMD <c>client_id</c> URL is not
    /// resolved (the authorize request fails as "unknown client", same as
    /// any unregistered client) and the realm's discovery document omits
    /// <c>client_id_metadata_document_supported</c> — no info-disclosure to
    /// drive-by visitors about whether the feature exists per realm.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Access-token lifetime for CIMD-resolved clients. Shorter than the
    /// admin-registered default so a leaked token from an unverified,
    /// domain-bound client has a smaller blast radius. Default 15 minutes,
    /// matching the DCR policy.
    /// </summary>
    public TimeSpan AccessTokenLifetime { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Refresh-token lifetime for CIMD clients. A refresh after the
    /// metadata cache expires re-fetches and re-validates the live
    /// document, so the effective trust window is bounded by both this and
    /// the document's availability. Default 7 days, matching DCR.
    /// </summary>
    public TimeSpan RefreshTokenLifetime { get; init; } = TimeSpan.FromDays(7);
}
