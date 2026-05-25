namespace Modgud.Domain.Realms;

/// <summary>
/// Per-realm configuration for Dynamic Client Registration (RFC 7591),
/// the MCP-flavoured variant. Lives as a sub-document on the tenant-DB
/// <see cref="RealmSettings.RealmSettings"/> aggregate alongside
/// <see cref="SelfRegistrationSettings"/>. Owned by the realm-admin
/// (not Control-Plane). Default-disabled: every realm starts with
/// <c>Enabled=false</c> and the public <c>/connect/register</c>
/// endpoint refuses anonymous registration until an admin opts in.
///
/// <para>The three-tier opt-in pattern (realm master + per-OAuthApi
/// <c>AllowDynamicRegistration</c> + per-OAuthScope
/// <c>AllowDynamicRegistrationClients</c>) means flipping this master
/// toggle ON does not, by itself, expose anything — until a resource
/// server AND a scope are individually opted in, DCR-registered
/// clients have no valid <c>resource=</c> target nor a requestable
/// scope. The master toggle just gates whether the
/// <c>/connect/register</c> endpoint exists at all and whether
/// <c>registration_endpoint</c> shows up in the discovery document.</para>
/// </summary>
public record DcrSettings
{
    /// <summary>
    /// Master toggle. When <c>false</c>, <c>/connect/register</c> returns
    /// 404 as if the endpoint doesn't exist, and the realm's
    /// authorization-server discovery document omits the
    /// <c>registration_endpoint</c> field — no info-disclosure to
    /// drive-by visitors.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Access-token lifetime for clients registered via DCR. Shorter than
    /// the admin-registered default so a leaked token from an unverified
    /// client has a smaller blast radius. Default 15 minutes (RFC 7591
    /// doesn't mandate; this is a Cocoar policy).
    /// </summary>
    public TimeSpan AccessTokenLifetime { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Refresh-token lifetime for DCR clients. Rotation is global-on at
    /// the server level (Cocoar default for all clients) — this just
    /// controls the absolute lifetime. Default 7 days.
    /// </summary>
    public TimeSpan RefreshTokenLifetime { get; init; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Soft-delete TTL for unused DCR clients. The GC background service
    /// soft-deletes DCR clients where <c>cocoar:dcr:last_used_at</c> is
    /// older than this. Default 90 days. Set lower for paranoid realms,
    /// higher for vendor pilots that need a longer warm-up period.
    /// </summary>
    public int GcTtlDays { get; init; } = 90;

    /// <summary>
    /// Per-IP rate-limit for <c>/connect/register</c>: max requests per
    /// hour from one source IP. In-memory limiter, resets on app
    /// restart — short enough that restart-cycling isn't a useful
    /// bypass. Default 5/h.
    /// </summary>
    public int PerIpRateLimitPerHour { get; init; } = 5;

    /// <summary>
    /// Per-realm rate-limit for <c>/connect/register</c>: max successful
    /// registrations per day across all IPs. Caps storage growth from
    /// realms that lose their reserved-names list and get sprayed.
    /// Default 100/d.
    /// </summary>
    public int PerRealmRateLimitPerDay { get; init; } = 100;

    /// <summary>
    /// Realm-configured reserved-names blocklist. The registration
    /// validator NFKC-normalises the requested <c>client_name</c>,
    /// downcases both sides, and rejects with
    /// <c>invalid_client_metadata</c> if any entry here appears as a
    /// substring. Stops "Cocoar", "Anthropic", tenant trademarks, etc.
    /// from being claimed by impersonators.
    ///
    /// <para>Substring (not exact) match so "Cl0ude Desktop" matches
    /// "Claude" after Latin-1 normalisation, and "Modgud Pro"
    /// matches "Cocoar". Cost: legitimate vendors with matching names
    /// hit the same wall — that's the price of the blocklist living
    /// per-realm and the rationale for the v2 <c>software_statement</c>
    /// path (deferred).</para>
    /// </summary>
    public string[]? ReservedNames { get; init; }
}
