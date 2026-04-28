using Cocoar.Auth.Domain.Identity.LoginProviders;

namespace Cocoar.Auth.Application.DTOs.LoginProviders;

public record LoginProviderDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public required LoginProviderType Type { get; init; }
    public required Dictionary<string, string> Configuration { get; init; }
    public bool IsBuiltIn { get; init; }
}

public record CreateLoginProviderDto
{
    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public LoginProviderType Type { get; init; } = LoginProviderType.Internal;
    public Dictionary<string, string> Configuration { get; init; } = new();
}

public record UpdateLoginProviderDto
{
    public string? Name { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public Dictionary<string, string>? Configuration { get; init; }
}

public record LoginProviderListDto
{
    public required List<LoginProviderDto> Items { get; init; }
    public int TotalCount { get; init; }
}
