using Cocoar.Auth.Authorization.Apps;

namespace Cocoar.Auth.Authorization.Events;

public record AppCreatedEvent(
    Guid Id,
    string Slug,
    string DisplayName,
    string? Description,
    List<AppPermission> Permissions,
    bool IsSystem);

public record AppUpdatedEvent(
    Guid Id,
    string DisplayName,
    string? Description,
    List<AppPermission> Permissions);

public record AppDeletedEvent(Guid Id);
