namespace Cocoar.Auth.Application.DTOs.OAuth;

public record ApiSecretEntryDto
{
    public required string SecretId { get; init; }
    public required string Type { get; init; }
    public string? Description { get; init; }
    public DateTimeOffset? Expiration { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public record OAuthApiDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; }
    public required List<string> Scopes { get; init; }
    public required List<string> UserClaims { get; init; }

    public List<ApiSecretEntryDto> Secrets { get; init; } = new();
}

public record CreateOAuthApiDto
{
    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; } = true;
    public List<string> Scopes { get; init; } = new();
    public List<string> UserClaims { get; init; } = new();
}

public record UpdateOAuthApiDto
{
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public bool? Enabled { get; init; }
    public List<string>? Scopes { get; init; }
    public List<string>? UserClaims { get; init; }
}

public record OAuthApiListDto
{
    public required List<OAuthApiDto> Items { get; init; }
    public int TotalCount { get; init; }
}

public record OAuthApiCreatedDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; }
    public required List<string> Scopes { get; init; }
    public required List<string> UserClaims { get; init; }

    public required string ApiSecret { get; init; }
}

public record ApiSecretDto
{
    public required string ApiSecret { get; init; }
}

public record CreateApiSecretDto
{
    public string Type { get; init; } = "SharedSecret";
    public string? Description { get; init; }
    public DateTimeOffset? Expiration { get; init; }
}

public record ApiSecretCreatedDto
{
    public required string SecretId { get; init; }
    public required string ApiSecret { get; init; }
}

public record PaginationRequest
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}
