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
        /// <summary>ADR 0019 — Modgud client capabilities (not an OpenIddict prefix).</summary>
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

    /// <summary>ADR 0019 — per-client capabilities a realm admin grants explicitly.
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

    public static bool IsKnown(string? value) => value is Web or Native;

    /// <summary>
    /// The application type OpenIddict has to see for a client — which decides whether a
    /// loopback redirect URI matches with ANY port (RFC 8252 §7.3: a native app takes an
    /// ephemeral port at request time, so the server MUST accept whatever port it got).
    ///
    /// <para>A loopback <c>http</c> redirect URI is only legal for a native app in the first
    /// place (OIDC DCR §2: web clients use https and never localhost), so its presence IS
    /// the evidence: such a client is <c>native</c> whatever it declared or omitted. That
    /// keeps every MCP client working — Claude Code, Cursor, VS Code, the MCP Inspector all
    /// register <c>http://localhost/callback</c> port-less, without an
    /// <c>application_type</c>, and connect on a random port. Nothing is loosened for the
    /// other URIs: OpenIddict relaxes the port only when both sides are loopback and the
    /// registered one carries none. Without a loopback URI the declared type stands.</para>
    /// </summary>
    public static string? Effective(string? declared, IEnumerable<string>? redirectUris)
        => redirectUris?.Any(IsLoopbackHttp) == true ? Native : declared;

    /// <summary>
    /// RFC 8252 §7.3: the server MUST allow any port for a loopback redirect URI —
    /// including when the client registered one WITH a port. VS Code's metadata
    /// names <c>http://127.0.0.1:33418/</c> and Zed registers the ephemeral port it
    /// happened to get; the next run comes back on another one. OpenIddict relaxes
    /// the port only against a registered URI that carries none, so every loopback
    /// URI with a port is registered alongside its port-less twin. Scheme, host and
    /// path still match exactly; non-loopback URIs pass through untouched.
    /// </summary>
    public static List<string> WithPortlessLoopbackTwins(IEnumerable<string> redirectUris)
    {
        var result = new List<string>();
        foreach (var raw in redirectUris)
        {
            if (!result.Contains(raw, StringComparer.Ordinal)) result.Add(raw);
            if (!IsLoopbackHttp(raw) || !Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.IsDefaultPort)
                continue;
            var twin = new UriBuilder(uri) { Port = -1 }.Uri.GetComponents(
                UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped);
            if (!result.Contains(twin, StringComparer.Ordinal)) result.Add(twin);
        }
        return result;
    }

    /// <summary>RFC 8252 §7.3 loopback redirect: <c>http</c> on <c>localhost</c>,
    /// <c>127.0.0.1</c> or <c>[::1]</c> — the one place plain http is a valid redirect.</summary>
    public static bool IsLoopbackHttp(string? raw)
        => Uri.TryCreate(raw, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttp
           && uri.IsLoopback;
}

/// <summary>
/// When a dynamically registered client (DCR or CIMD) may reuse a remembered consent.
///
/// <para>RFC 8252 §8.6: an authorization server should not process a request without the
/// user's interaction unless the client's identity can be assured. A dynamic client's
/// <c>client_id</c> is public — a CIMD document URL, a DCR id in a config file — so any
/// process can send the user's browser to <c>/connect/authorize</c> with it. What still
/// assures the identity is where the code goes and who can redeem it:</para>
/// <list type="bullet">
///   <item>an <c>https</c> redirect delivers the code only to the host that published the
///   metadata (claude.ai's callback, not a local process), and</item>
///   <item>a confidential client cannot redeem a code without its own credential
///   (<c>private_key_jwt</c>, a DCR secret), whoever caught it.</item>
/// </list>
/// <para>A PUBLIC client redirecting to loopback (Claude Code, VS Code, Zed) has neither:
/// any local process can listen on a loopback port — on any port since the RFC 8252 §7.3
/// port tolerance — and redeem the code with its own PKCE verifier. Such a client is asked
/// every time; the authorization itself is still reused, so <c>oi_au_id</c> stays stable.</para>
///
/// <para>Admin-created clients keep their own <c>AllowRememberConsent</c> flag; this rule
/// replaces it for dynamic clients, whose flag nobody chose.</para>
/// </summary>
public static class DynamicClientConsent
{
    public static bool MayRemember(bool isConfidential, string? requestRedirectUri)
        => isConfidential
           || (Uri.TryCreate(requestRedirectUri, UriKind.Absolute, out var uri)
               && uri.Scheme == Uri.UriSchemeHttps
               && !uri.IsLoopback);
}
