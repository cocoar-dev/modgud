using System.Text.Json.Serialization;

namespace Cocoar.Auth.Application.Dcr;

/// <summary>
/// RFC 7591 §2 client metadata payload — the wire shape POSTed to
/// <c>/connect/register</c>. Cocoar accepts only the MCP-relevant subset
/// (public PKCE clients, code-grant, optional refresh-token). Fields
/// outside that subset that appear in the request are either echoed back
/// on success (when harmless, e.g. <c>client_uri</c>) or rejected with
/// <c>invalid_client_metadata</c> (when their presence implies a
/// capability we won't grant, e.g. <c>client_secret</c>).
///
/// <para>Nullable everywhere — RFC 7591 specifies that omitted fields
/// take server-defined defaults. The validator (<see cref="IDcrRegistrationValidator"/>)
/// fills in defaults and rejects with <c>invalid_redirect_uri</c> /
/// <c>invalid_client_metadata</c> for required fields that are missing.</para>
/// </summary>
public sealed record DcrRegistrationRequest
{
    [JsonPropertyName("redirect_uris")]
    public List<string>? RedirectUris { get; init; }

    [JsonPropertyName("token_endpoint_auth_method")]
    public string? TokenEndpointAuthMethod { get; init; }

    [JsonPropertyName("grant_types")]
    public List<string>? GrantTypes { get; init; }

    [JsonPropertyName("response_types")]
    public List<string>? ResponseTypes { get; init; }

    [JsonPropertyName("client_name")]
    public string? ClientName { get; init; }

    [JsonPropertyName("client_uri")]
    public string? ClientUri { get; init; }

    [JsonPropertyName("logo_uri")]
    public string? LogoUri { get; init; }

    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    [JsonPropertyName("contacts")]
    public List<string>? Contacts { get; init; }

    [JsonPropertyName("tos_uri")]
    public string? TosUri { get; init; }

    [JsonPropertyName("policy_uri")]
    public string? PolicyUri { get; init; }

    [JsonPropertyName("software_id")]
    public string? SoftwareId { get; init; }

    [JsonPropertyName("software_version")]
    public string? SoftwareVersion { get; init; }
}

/// <summary>RFC 7591 §3.2.1 success response. Echoes the sanitized
/// registration plus the assigned <c>client_id</c>. v1 deliberately omits
/// <c>registration_access_token</c> / <c>registration_client_uri</c> —
/// RFC 7592 management is out-of-scope.</summary>
public sealed record DcrRegistrationResponse
{
    [JsonPropertyName("client_id")]
    public required string ClientId { get; init; }

    [JsonPropertyName("client_id_issued_at")]
    public required long ClientIdIssuedAt { get; init; }

    [JsonPropertyName("token_endpoint_auth_method")]
    public required string TokenEndpointAuthMethod { get; init; }

    [JsonPropertyName("grant_types")]
    public required IReadOnlyList<string> GrantTypes { get; init; }

    [JsonPropertyName("response_types")]
    public required IReadOnlyList<string> ResponseTypes { get; init; }

    [JsonPropertyName("redirect_uris")]
    public required IReadOnlyList<string> RedirectUris { get; init; }

    [JsonPropertyName("client_name")]
    public string? ClientName { get; init; }

    [JsonPropertyName("client_uri")]
    public string? ClientUri { get; init; }

    [JsonPropertyName("logo_uri")]
    public string? LogoUri { get; init; }

    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    [JsonPropertyName("contacts")]
    public IReadOnlyList<string>? Contacts { get; init; }

    [JsonPropertyName("tos_uri")]
    public string? TosUri { get; init; }

    [JsonPropertyName("policy_uri")]
    public string? PolicyUri { get; init; }

    [JsonPropertyName("software_id")]
    public string? SoftwareId { get; init; }

    [JsonPropertyName("software_version")]
    public string? SoftwareVersion { get; init; }
}

/// <summary>RFC 7591 §3.2.2 error response. The <c>error</c> codes are
/// the RFC-defined values; <see cref="DcrErrorCodes"/> centralises them
/// to avoid string drift.</summary>
public sealed record DcrErrorResponse
{
    [JsonPropertyName("error")]
    public required string Error { get; init; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; init; }
}

/// <summary>RFC 7591 §3.2.2 + §3.2.1 error code vocabulary, plus
/// Cocoar's two custom <c>access_denied</c> sub-reasons (rate-limit +
/// realm-disabled, surfaced as plain 403/404 by the endpoint).</summary>
public static class DcrErrorCodes
{
    /// <summary>RFC 7591 §3.2.2 — the value of one or more <c>redirect_uri</c>
    /// entries is invalid.</summary>
    public const string InvalidRedirectUri = "invalid_redirect_uri";

    /// <summary>RFC 7591 §3.2.2 — the request contains an invalid or
    /// unrecognized client metadata field, OR the request is missing a
    /// required field.</summary>
    public const string InvalidClientMetadata = "invalid_client_metadata";

    /// <summary>RFC 7591 §3.2.2 — the request contains a software_statement
    /// that cannot be verified. Reserved for the v2 software-statement
    /// path; not raised in v1.</summary>
    public const string InvalidSoftwareStatement = "invalid_software_statement";
}

/// <summary>The DCR rejection reason recorded in the audit log when
/// validation fails. Coarser than the wire-level <see cref="DcrErrorCodes"/>
/// because rate-limit and reserved-name rejections both map to
/// <c>invalid_client_metadata</c> on the wire but matter as separate
/// signals for threat-hunting.</summary>
/// <summary>Metadata captured by the <c>/connect/register</c> handler
/// and stamped onto the new client's Properties + Settings dicts in
/// the same transaction as the rest of the create flow. Lives next to
/// the validator request because it's the validator's caller (the
/// endpoint) that produces it.
///
/// <para>The token-lifetime fields are persisted into the
/// OpenIddict-recognized Settings keys
/// (<c>OpenIddictConstants.Settings.TokenLifetimes.AccessToken</c> /
/// <c>RefreshToken</c>) so OpenIddict's own pipeline applies the
/// per-realm-DCR override at token-issue time. Without this the
/// server-global default would win (see bug #30).</para></summary>
public sealed record DcrMetadataInput(
    DateTimeOffset RegisteredAt,
    string SourceIp,
    TimeSpan AccessTokenLifetime,
    TimeSpan RefreshTokenLifetime);

public enum DcrRejectionReason
{
    RealmDisabled,
    PerIpRateLimit,
    PerRealmRateLimit,
    MissingRedirectUri,
    InvalidRedirectUri,
    InvalidTokenAuthMethod,
    InvalidGrantType,
    InvalidResponseType,
    ClientNameMissing,
    ClientNameTooLong,
    ClientNameNonLatin1,
    ClientNameReservedName,
}
