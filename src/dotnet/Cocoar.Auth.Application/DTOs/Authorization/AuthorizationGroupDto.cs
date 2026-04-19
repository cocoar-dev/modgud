using Cocoar.Auth.Domain.Authorization;
using Cocoar.Auth.Domain.Principals;

namespace Cocoar.Auth.Application.DTOs.Authorization;

public record AuthorizationGroupDto
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public List<Guid> MemberIds { get; init; } = [];
    public List<Guid> RoleIds { get; init; } = [];
    public List<ResourceAccessScriptDto> AccessScripts { get; init; } = [];
    public MembershipMode MembershipMode { get; init; }
    public string? MembershipScript { get; init; }
    public List<string>? MembershipScriptDependencies { get; init; }
    public string? MembershipLastError { get; init; }
    public string? Email { get; init; }
    public EmailMode EmailMode { get; init; }
}

public record ResourceAccessScriptDto
{
    public required string ResourceType { get; init; }
    public string? Script { get; init; }
}

public record CreateAuthorizationGroupInput
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public List<Guid> MemberIds { get; init; } = [];
    public List<Guid> RoleIds { get; init; } = [];
    public List<ResourceAccessScriptDto> AccessScripts { get; init; } = [];
    public MembershipMode MembershipMode { get; init; } = MembershipMode.Manual;
    public string? MembershipScript { get; init; }
    public string? Email { get; init; }
    public EmailMode EmailMode { get; init; } = EmailMode.Shared;
}

public record UpdateAuthorizationGroupInput
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public List<Guid> MemberIds { get; init; } = [];
    public List<Guid> RoleIds { get; init; } = [];
    public List<ResourceAccessScriptDto> AccessScripts { get; init; } = [];
    public MembershipMode MembershipMode { get; init; } = MembershipMode.Manual;
    public string? MembershipScript { get; init; }
    public string? Email { get; init; }
    public EmailMode EmailMode { get; init; } = EmailMode.Shared;
}
