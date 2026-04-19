namespace Cocoar.Auth.Application.DTOs.Authorization;

public record PermissionRoleDto
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string ResourceType { get; init; }
    public List<string> Permissions { get; init; } = [];
}

public record CreatePermissionRoleInput
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string ResourceType { get; init; }
    public List<string> Permissions { get; init; } = [];
}

public record UpdatePermissionRoleInput
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string ResourceType { get; init; }
    public List<string> Permissions { get; init; } = [];
}
