namespace Modgud.Domain.OAuth.Common;

/// <summary>
/// String constants mirroring OpenIddict's permission prefix scheme. Kept in
/// Domain so the admin slice can build permission lists without taking a
/// dependency on the OpenIddict abstractions package (which only enters in
/// etappe 3b alongside the runtime).
/// </summary>
public static class OAuthPermissions
{
    public static class Prefixes
    {
        public const string Scope = "scp:";
        public const string GrantType = "gt:";
        public const string ResponseType = "rst:";
        public const string Endpoint = "ept:";
        /// <summary>ADR 0007 — Modgud client capabilities (not an OpenIddict prefix).</summary>
        public const string Capability = "cap:";
    }

    public static class Endpoints
    {
        public const string Authorization = "ept:authorization";
        public const string Token = "ept:token";
        public const string EndSession = "ept:logout";
        public const string Introspection = "ept:introspection";
        public const string Revocation = "ept:revocation";
        public const string DeviceAuthorization = "ept:device_authorization";
        public const string PushedAuthorization = "ept:pushed_authorization";
    }

    /// <summary>Per-client requirement flags (OpenIddict application
    /// <c>Requirements</c>). Mirrors OpenIddict's <c>ft:</c>-prefixed constants;
    /// inlined to keep OpenIddict.Abstractions out of the Domain/Application
    /// layers, pinned against drift by the OAuth constants tests.</summary>
    public static class Requirements
    {
        /// <summary>RFC 9126 — when present on a client, that client's
        /// authorization requests MUST go through <c>/connect/par</c>; a direct
        /// (non-PAR) authorize request is rejected. OpenIddict's
        /// <c>Requirements.Features.PushedAuthorizationRequests</c>.</summary>
        public const string PushedAuthorizationRequests = "ft:par";
    }

    /// <summary>ADR 0007 — per-client capabilities a realm admin grants explicitly.
    /// Stored as <c>cap:</c>-prefixed entries in the client's permission list next to
    /// the grant-type permissions (OpenIddict ignores prefixes it does not know).
    /// A capability may shift a rate-limit dimension, never lift a limit.</summary>
    public static class Capabilities
    {
        /// <summary>The confidential client may convey the end user's address in the
        /// <c>Modgud-Forwarded-For</c> header on public auth endpoints. It shifts ONLY the
        /// source rate-limit dimensions (a BFF is limited per browser instead of per
        /// egress address); target, client and app limits apply unchanged.</summary>
        public const string TrustedForwarder = Prefixes.Capability + "trusted-forwarder";

        public static readonly IReadOnlyList<string> All = [TrustedForwarder];

        public static bool IsKnown(string value) => All.Contains(value, StringComparer.Ordinal);
    }

    public static class GrantTypes
    {
        public const string AuthorizationCode = "gt:authorization_code";
        public const string ClientCredentials = "gt:client_credentials";
        public const string RefreshToken = "gt:refresh_token";
        // NB: no Implicit or Password grant. OAuth 2.1 removes both, and the
        // OpenIddict server never enables their flows — they were also removed
        // from the admin surface so a client can't even be configured with them
        // (rejected by OAuthAdminMapping.ValidateGrantTypes).
        public const string DeviceCode = "gt:urn:ietf:params:oauth:grant-type:device_code";

        // ADR-0010 — native (cookieless) passwordless token grants. The per-client
        // opt-in IS the presence of one of these gt: permissions on the client
        // (IgnoreGrantTypePermissions is not set, so OpenIddict natively rejects a
        // client that lacks it). Value = "gt:" + the raw URN, mirroring DeviceCode.
        public const string CocoarOtp = Prefixes.GrantType + CocoarGrantTypes.Otp;
        public const string CocoarMagic = Prefixes.GrantType + CocoarGrantTypes.Magic;
        public const string CocoarPasskey = Prefixes.GrantType + CocoarGrantTypes.Passkey;

        // MG-FT — the staffing grant a position terminal redeems a passkey tap
        // with (MG-FT-05). Same gt:-URN convention as the native grants.
        public const string Staffing =
            Prefixes.GrantType + Modgud.Domain.PositionTerminals.PositionGrantTypes.StaffingSession;
    }

    public static class ResponseTypes
    {
        public const string Code = "rst:code";
    }
}

/// <summary>
/// Raw <c>grant_type</c> URNs for the native (cookieless) passwordless token
/// grants (ADR-0010). These are the wire values a native client sends to
/// <c>/connect/token</c> and the values passed to
/// <c>options.AllowCustomFlow(...)</c>. The matching per-client OpenIddict
/// permission is <see cref="OAuthPermissions.Prefixes.GrantType"/> + the URN
/// (see <see cref="OAuthPermissions.GrantTypes.CocoarOtp"/> / <c>CocoarMagic</c>).
/// </summary>
public static class CocoarGrantTypes
{
    public const string Otp = "urn:cocoar:otp";
    public const string Magic = "urn:cocoar:magic";
    public const string Passkey = "urn:cocoar:passkey";
}

public static class OAuthClientTypes
{
    public const string Public = "public";
    public const string Confidential = "confidential";
}

public static class OAuthConsentTypes
{
    public const string Explicit = "explicit";
    public const string Implicit = "implicit";
    public const string External = "external";
    public const string Systematic = "systematic";
}

/// <summary>
/// Wire-format values for the OIDC <c>application_type</c> client metadata
/// field. Per the OIDC Dynamic Client Registration spec — must be the literal
/// strings, lowercase, exact.
/// </summary>
public static class OAuthApplicationTypes
{
    public const string Web = "web";
    public const string Native = "native";
}
