using Cocoar.Primitives;
using Cocoar.Primitives.OptionalAware;

namespace Cocoar.Auth.Application.DTOs.Roles;

/// <summary>
/// DTO for returning role information.
/// </summary>
public record RoleDto
{
    public required ShortGuid Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ModifiedAt { get; init; }
}

/// <summary>
/// DTO for creating a new role.
/// </summary>
public record CreateRoleDto
{
    public required string Name { get; init; }
    public string? Description { get; init; }
}

/// <summary>
/// DTO for updating a role.
/// </summary>
public record UpdateRoleDto
{
    public Optional<string> Name { get; init; }
    public Optional<string?> Description { get; init; }
}

/// <summary>
/// DTO for a list of roles.
/// </summary>
public record RoleListDto
{
    public required List<RoleDto> Items { get; init; }
    public required int TotalCount { get; init; }
}
