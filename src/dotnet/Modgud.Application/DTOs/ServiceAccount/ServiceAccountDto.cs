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
}

public class ServiceAccountCreateDto
{
    public string AccountName { get; set; } = string.Empty;
    public string? Purpose { get; set; }
}

public class ServiceAccountUpdateDto
{
    public string? AccountName { get; set; }
    public string? Purpose { get; set; }
    public bool? IsActive { get; set; }
}
