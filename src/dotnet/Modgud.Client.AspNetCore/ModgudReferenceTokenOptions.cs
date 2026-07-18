using Microsoft.AspNetCore.Authentication;

namespace Modgud.Client.AspNetCore;

/// <summary>Well-known scheme name for the Modgud reference-token
/// (introspection) authentication handler.</summary>
public static class ModgudReferenceTokenDefaults
{
    /// <summary>The default authentication-scheme name registered by
    /// <c>AddModgudReferenceTokenClient</c>.</summary>
    public const string AuthenticationScheme = "ModgudIntrospection";
}

/// <summary>
/// Options for the reference-token (opaque access token) validation mode.
///
/// <para>Modgud's default access-token format is a <b>reference token</b> —
/// an opaque handle with no self-contained claims, validated by calling the
/// IdP's <c>/connect/introspect</c> endpoint (RFC 7662). This mode lets a
/// resource server accept those tokens directly, instead of requiring the
/// OAuth client to be switched to JWT access tokens for the JWKS-based
/// <c>AddModgudClient</c> path.</para>
///
/// <para><b>Introspection identity.</b> The IdP only reveals a token — its
/// <c>active</c> status and any per-audience <c>resource_access</c> block —
/// to a caller that is one of the token's audiences or its authorised
/// presenter. A resource server therefore introspects with a confidential
/// client whose <c>client_id</c> equals its own <see cref="Audience"/> (the
/// RFC 8707 <c>resource=</c> value already carried in the token's <c>aud</c>).
/// That single introspection call both validates the token and returns the
/// audience-scoped roles/permissions — no separate UserInfo round-trip.</para>
///
/// <para><b>No caching, by design.</b> Every request introspects. A reference
/// token's defining advantage is instant revocation; a TTL cache would trade
/// that away. Caching may be added later as an explicit opt-in.</para>
/// </summary>
public sealed class ModgudReferenceTokenOptions : AuthenticationSchemeOptions
{
    /// <summary>IdP base URL, e.g. <c>https://auth.example.com</c> — the realm
    /// host root, no realm path segment. Used to build the
    /// <c>{Authority}/connect/introspect</c> URL. Required.</summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>The resource server's audience — the same value used as the
    /// RFC 8707 <c>resource=</c> indicator when tokens are minted for this RS
    /// (an <c>OAuthApi</c> name in Modgud). The audience block read out of the
    /// introspection response is keyed by this value. Required.</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>The <c>client_id</c> used to authenticate the introspection
    /// call. Defaults to <see cref="Audience"/> — the RS registers a
    /// confidential client under its own audience id so the IdP treats it as
    /// an authorised introspector. Override only if the introspection client
    /// is registered under a different id that is nonetheless one of the
    /// token's audiences.</summary>
    public string? IntrospectionClientId { get; set; }

    /// <summary>The client secret for <see cref="ResolvedClientId"/>. Required.
    /// Sent as a form-body credential (<c>client_secret_post</c>), which works
    /// for both URL-shaped and plain audience ids — HTTP Basic would break on
    /// the scheme colon of a URL <c>client_id</c>.</summary>
    public string? IntrospectionClientSecret { get; set; }

    /// <summary>The effective introspection <c>client_id</c>:
    /// <see cref="IntrospectionClientId"/> if set, otherwise
    /// <see cref="Audience"/>.</summary>
    public string ResolvedClientId =>
        string.IsNullOrEmpty(IntrospectionClientId) ? Audience : IntrospectionClientId!;
}
