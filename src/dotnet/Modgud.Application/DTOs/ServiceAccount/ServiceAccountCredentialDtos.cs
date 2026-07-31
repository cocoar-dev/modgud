using Modgud.Application.DTOs.OAuth;
using Modgud.Domain.OAuth.Common;

namespace Modgud.Application.DTOs.ServiceAccount;

/// <summary>
/// Input shape for "Issue a new credential on this Service Account". A
/// credential is a confidential OAuth client pinned to the owning SA with
/// the single <c>client_credentials</c> grant — the only knobs the SA admin
/// needs to think about are how to identify the credential (DisplayName +
/// optional ClientId override), what it's allowed to ask for (Scopes +
/// AppIds), and the optional access-token lifetime override. Everything
/// else (grant types, secret-required, consent type, redirect URIs) is
/// system-pinned and not surfaced.
/// </summary>
public class IssueServiceAccountCredentialDto
{
    /// <summary>
    /// Optional client_id override. Defaults to <c>{sa.AccountName}.{8-char-suffix}</c>
    /// when omitted — keeps the link to the owning SA obvious in audit logs.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>Human-readable label shown in the admin UI.</summary>
    public string? DisplayName { get; set; }

    public List<string> Scopes { get; set; } = [];

    /// <summary>App-ids this credential is allowed to act on behalf of.</summary>
    public List<string> AppIds { get; set; } = [];

    /// <summary>Optional override for the access-token lifetime (seconds).</summary>
    public int? AccessTokenLifetime { get; set; }

    /// <summary>Whether the credential may issue tokens immediately.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Access-token format. Defaults to <see cref="AccessTokenType.Reference"/>
    /// (opaque, stored, INSTANTLY revocable) — so deactivating/deleting/rotating
    /// the credential immediately cuts off live M2M access (Audit #6/#7/#8). Opt
    /// into <see cref="AccessTokenType.Jwt"/> only for resource servers that must
    /// self-validate without an introspection round-trip; an already-issued JWT
    /// then survives a revoke until it expires, so keep its lifetime short.
    /// </summary>
    public AccessTokenType AccessTokenType { get; set; } = AccessTokenType.Reference;
}

/// <summary>
/// Input shape for editing an existing credential. Only the fields a SA
/// admin can meaningfully change are exposed — <c>ClientId</c>, grant
/// types, and the link to the SA are pinned by construction.
/// </summary>
public class UpdateServiceAccountCredentialDto
{
    public string? DisplayName { get; set; }
    public List<string>? Scopes { get; set; }
    public List<string>? AppIds { get; set; }
    public int? AccessTokenLifetime { get; set; }
    public bool? Enabled { get; set; }

    /// <summary>Switch the credential's access-token format (Reference ↔ Jwt).
    /// Null leaves it unchanged. See <see cref="IssueServiceAccountCredentialDto.AccessTokenType"/>.</summary>
    public AccessTokenType? AccessTokenType { get; set; }
}

/// <summary>
/// "Issue credential" response wraps the underlying <see cref="OAuthClientDto"/>
/// with the one-time plaintext secret. The secret is only ever returned in
/// this object and never persisted in plaintext — admin must capture it.
/// </summary>
public class ServiceAccountCredentialIssuedDto
{
    public required OAuthClientDto Credential { get; init; }
    public required string ClientSecret { get; init; }
}
