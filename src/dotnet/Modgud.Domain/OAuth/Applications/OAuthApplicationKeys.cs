namespace Modgud.Domain.OAuth.Applications;

/// <summary>Custom OAuth application setting keys (simple string values).</summary>
public static class OAuthApplicationSettingKeys
{
    public const string AccessTokenType = "modgud:access_token_type";
    public const string IdentityTokenLifetime = "modgud:identity_token_lifetime";
    public const string AccessTokenLifetime = "modgud:access_token_lifetime";
    public const string AuthorizationCodeLifetime = "modgud:authorization_code_lifetime";
    public const string SlidingRefreshTokenLifetime = "modgud:sliding_refresh_token_lifetime";
    public const string ClientSessionIdleLifetime = "modgud:client_session_idle_lifetime";
    public const string ClientSessionAbsoluteLifetime = "modgud:client_session_absolute_lifetime";
    public const string ClientClaimsPrefix = "modgud:client_claims_prefix";

    /// <summary>
    /// ADR-0009 — the admin-set per-client WebAuthn RP ID. When set, native passkey
    /// ceremonies (login begin/redeem + enrollment) for this client use this value
    /// as the WebAuthn relying-party id (and the derived origin) instead of the
    /// realm's <c>PrimaryDomain</c>. Absent/blank ⇒ realm-scoped (today's behaviour).
    /// High-trust admin-set value (not client-supplied), so there is no runtime
    /// public-suffix check.
    /// </summary>
    public const string WebAuthnRpId = "modgud:webauthn_rp_id";

    /// <summary>
    /// ADR 0009 — OpenID Connect Back-Channel Logout 1.0: the absolute URI Modgud POSTs a
    /// signed logout token to when a session of this client ends. Absent = the client
    /// receives no logout notifications by POST (it may still read them from the
    /// Application change feed). Admin-set; validated at registration and guarded
    /// against private / link-local targets again at send time.
    /// </summary>
    public const string BackChannelLogoutUri = "modgud:backchannel_logout_uri";
}

/// <summary>Custom OAuth application property keys (JSON-element values for complex types).</summary>
public static class OAuthApplicationPropertyKeys
{
    public const string Enabled = "modgud:enabled";
    public const string AllowAccessTokensViaBrowser = "modgud:allow_access_tokens_via_browser";
    public const string RequireClientSecret = "modgud:require_client_secret";
    public const string EnableLocalLogin = "modgud:enable_local_login";
    public const string RequireConsent = "modgud:require_consent";
    public const string AllowRememberConsent = "modgud:allow_remember_consent";
    public const string AllowedCorsOrigins = "modgud:allowed_cors_origins";
    public const string AlwaysSendClientClaims = "modgud:always_send_client_claims";
    public const string UpdateAccessTokenClaimsOnRefresh = "modgud:update_access_token_claims_on_refresh";
    public const string ClientClaims = "modgud:client_claims";
    public const string Roles = "modgud:roles";

    /// <summary>
    /// Versioned hash of the normalized terminal-provisioning request. The
    /// caller-chosen client id is the natural idempotency key: the same request
    /// may safely be replayed, while a different request for that id conflicts.
    /// This is server-owned metadata and is never accepted from generic client
    /// property input.
    /// </summary>
    public const string TerminalProvisioningFingerprint =
        "modgud:terminal_provisioning_fingerprint";

    /// <summary>
    /// RFC 9449 (#118) — boolean. When <c>true</c>, this client MUST present a
    /// valid DPoP proof at <c>/connect/token</c>; a tokenless request is rejected
    /// with <c>invalid_dpop_proof</c>. Off by default (DPoP stays offered-not-
    /// required for every client either way). Stored as a Property rather than an
    /// OpenIddict <c>Requirement</c> because OpenIddict has no DPoP requirement to
    /// hang it off (unlike RFC 9126 PAR, which uses <c>ft:par</c>).
    /// </summary>
    public const string RequireDpop = "modgud:require_dpop";

    /// <summary>
    /// RFC 9449 §8-9 (#118) — boolean. When <c>true</c>, a DPoP proof this client
    /// presents at <c>/connect/token</c> must carry a valid server-issued
    /// <c>nonce</c>; a first proof without one is answered with <c>use_dpop_nonce</c>
    /// and a fresh <c>DPoP-Nonce</c> header, and the client retries. Off by default;
    /// server-nonce hardening on top of DPoP, independent of <see cref="RequireDpop"/>.
    /// </summary>
    public const string RequireDpopNonce = "modgud:require_dpop_nonce";

    // ─────── Dynamic Client Registration (RFC 7591) ────────
    // Set on creation by the /connect/register handler — admin-created
    // clients never carry these keys, which is how the rest of the
    // system (consent screen, resource-indicator handler, GC service)
    // distinguishes DCR clients from admin-registered ones.

    /// <summary>Boolean — <c>true</c> for clients minted via the public
    /// <c>/connect/register</c> endpoint. Single source of truth for
    /// "is this a DCR client".</summary>
    public const string DcrIsDynamicallyRegistered = "modgud:dcr:is_dynamically_registered";

    /// <summary>ISO-8601 timestamp string of when the DCR registration
    /// happened. Stable for the lifetime of the client.</summary>
    public const string DcrRegisteredAt = "modgud:dcr:registered_at";

    /// <summary>Source IP that submitted the registration request. Stored
    /// for audit-log correlation; not used for any policy decision after
    /// the registration completes.</summary>
    public const string DcrRegisteredFromIp = "modgud:dcr:registered_from_ip";

    /// <summary>ISO-8601 timestamp string updated on each successful token
    /// issuance for this client. Drives the GC sweep — clients with
    /// <c>LastUsedAt</c> older than the per-realm DCR TTL get
    /// soft-deleted.</summary>
    public const string DcrLastUsedAt = "modgud:dcr:last_used_at";

    // ─────── Client ID Metadata Documents (CIMD) ──────
    // Set only on the synthesized, NON-persisted application a CIMD
    // client_id URL resolves to. CIMD clients also carry
    // DcrIsDynamicallyRegistered=true so the existing DCR audience
    // containment + "unverified" consent treatment apply unchanged.

    /// <summary>Boolean — <c>true</c> on the synthetic application produced
    /// when a CIMD <c>client_id</c> URL is resolved. Lets call sites tell a
    /// CIMD-resolved client apart from a DCR-registered one even though both
    /// set <see cref="DcrIsDynamicallyRegistered"/>.</summary>
    public const string CimdIsResolvedClient = "modgud:cimd:is_resolved_client";

    // ─────── Back-channel logout (ADR 0009) ──────

    /// <summary>Boolean, default <c>true</c> — logout tokens carry the <c>sid</c> claim
    /// (spec: <c>backchannel_logout_session_required</c>).</summary>
    public const string BackChannelLogoutSessionRequired = "modgud:backchannel_logout_session_required";
}
