using TimeToDo.Domain.ValueObjects;

namespace TimeToDo.Application.DTOs.User;

public class UserDto
{
    public required string Id { get; set; }
    public string? Firstname { get; set; }
    public string? Lastname { get; set; }
    public string? Acronym { get; set; }
    public string? Email { get; set; }
    public string? UserName { get; set; }
    public bool IsActive { get; set; } = true;
    public bool HasPassword { get; set; }
    /// <summary>
    /// IdpConfig ids (ShortGuid strings) this user has an active external-identity
    /// link with. Empty = local-only. Frontend resolves ids → display names via
    /// the IdpConfig store for the Admin-list IdP-connected indicator.
    /// </summary>
    public List<string> ExternalIdpConfigIds { get; set; } = [];
    public EntityStatus Status { get; set; } = EntityStatus.Active;
}
