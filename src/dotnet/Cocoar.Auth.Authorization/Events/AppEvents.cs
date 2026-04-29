namespace Cocoar.Auth.Authorization.Events;

public record AppCreatedEvent(
    Guid Id,
    string Slug,
    string DisplayName,
    string? Description,
    List<string> Resources,
    bool IsSystem);

public record AppUpdatedEvent(
    Guid Id,
    string DisplayName,
    string? Description,
    List<string> Resources);

public record AppDeletedEvent(Guid Id);
