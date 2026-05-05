namespace Cocoar.Auth.Application.DTOs.Realms;

public record RealmDto
{
    public Guid Id { get; init; }
    public string Slug { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string[] Domains { get; init; } = [];
    public bool IsControlPlane { get; init; }
    public bool IsActive { get; init; }
    public bool NeedsSetup { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public record CreateRealmDto
{
    public string Slug { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string[]? Domains { get; init; }
    public bool IsControlPlane { get; init; }
}

public record UpdateRealmDto
{
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public string[]? Domains { get; init; }
    public bool? IsControlPlane { get; init; }
    public bool? IsActive { get; init; }
}

public record RealmListDto
{
    public List<RealmDto> Items { get; init; } = [];
    public int TotalCount { get; init; }
}
