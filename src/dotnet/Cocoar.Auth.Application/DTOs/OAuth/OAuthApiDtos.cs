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
    /// <summary>
    /// FK to <c>App.Id</c> (Guid string). Null = unassigned (this RS exists
    /// but cannot authenticate against the distribution API yet).
    /// </summary>
    public string? AppId { get; init; }

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
    /// <summary>
    /// App.Id (Guid string) the resource server belongs to. Null = leave
    /// unassigned for now (must be set later before the RS can authenticate
    /// against the distribution API).
    /// </summary>
    public string? AppId { get; init; }
}

public record UpdateOAuthApiDto
{
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public bool? Enabled { get; init; }
    public List<string>? Scopes { get; init; }
    public List<string>? UserClaims { get; init; }
    /// <summary>
    /// PATCH semantics: null/missing = no change, "" = detach (mark
    /// unassigned), "<guid>" = assign or change.
    /// </summary>
    public string? AppId { get; init; }
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

    /// <summary>
    /// Builds a request from raw query-string ints, clamping non-positive values
    /// to the same defaults as the parameterless constructor (1 and 20). Use
    /// this from endpoints where <c>?page=</c> / <c>?pageSize=</c> are absent
    /// (binding to 0) or negative — both should land on page 1 with 20 rows.
    /// </summary>
    public static PaginationRequest WithDefaults(int page, int pageSize)
        => new() { Page = page <= 0 ? 1 : page, PageSize = pageSize <= 0 ? 20 : pageSize };
}
