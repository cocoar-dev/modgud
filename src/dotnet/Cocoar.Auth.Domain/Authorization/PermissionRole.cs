namespace Cocoar.Auth.Domain.Authorization;

/// <summary>
/// A named set of permissions bound to a specific resource type.
/// Roles define what actions are allowed, not which data is visible (that's on the Group's Access-Script).
/// </summary>
public class PermissionRole
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string ResourceType { get; set; } = string.Empty;
    public List<string> Permissions { get; set; } = [];
    public bool IsDeleted { get; set; }
}
