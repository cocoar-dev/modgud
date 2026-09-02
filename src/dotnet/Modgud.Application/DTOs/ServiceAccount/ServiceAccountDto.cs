using Modgud.Domain.Common;
using Modgud.Domain.ValueObjects;

namespace Modgud.Application.DTOs.ServiceAccount;

/// <summary>
/// Wire shape for a <see cref="Modgud.Authorization.Principals.ServiceAccount"/>.
/// Service accounts are the non-human leg of the Principal hierarchy — they don't
/// carry email/password/MFA. They authenticate through the OAuth client_credentials
/// flow (planned wire-up) and exist here so role/group membership and permission
/// grants can target machine identities without going through a human user record.
/// </summary>
public class ServiceAccountDto
{
    public required string Id { get; set; }
    public required string AccountName { get; set; }
    public string? Purpose { get; set; }
    public bool IsActive { get; set; } = true;
    public EntityStatus Status { get; set; } = EntityStatus.Active;

    /// <summary>
    /// Present only on create when an initial credential was requested. The
    /// plaintext secret is returned exactly once and is never persisted.
    /// </summary>
    public ServiceAccountCredentialIssuedDto? InitialCredential { get; set; }
}

public class ServiceAccountCreateDto
{
    public string AccountName { get; set; } = string.Empty;
    public string? Purpose { get; set; }
    public bool IsActive { get; set; } = true;
    public IssueServiceAccountCredentialDto? InitialCredential { get; set; }
}

public class ServiceAccountUpdateDto
{
    public string? AccountName { get; set; }
    /// <summary>v2 merge-patch: absent = unchanged, explicit null (or a blank
    /// string) clears, value sets.</summary>
    public Optional<string?> Purpose { get; set; }
    public bool? IsActive { get; set; }
}
