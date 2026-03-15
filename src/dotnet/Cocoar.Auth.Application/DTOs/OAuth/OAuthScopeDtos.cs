namespace Cocoar.Auth.Application.DTOs.OAuth;

/// <summary>
/// OAuth scope information for API responses.
/// </summary>
public record OAuthScopeDto
{
	public required string Id { get; init; }
	public required string Name { get; init; }
	public string? DisplayName { get; init; }
	public string? Description { get; init; }
	public List<string> Resources { get; init; } = [];
	public bool Enabled { get; init; } = true;
	public bool Required { get; init; }
	public bool Emphasize { get; init; }
	public bool ShowInDiscoveryDocument { get; init; } = true;
	public List<string> UserClaims { get; init; } = [];
}

/// <summary>
/// Request to create a new OAuth scope.
/// </summary>
public record CreateOAuthScopeDto
{
	public required string Name { get; init; }
	public string? DisplayName { get; init; }
	public string? Description { get; init; }
	public List<string> Resources { get; init; } = [];
	public bool Enabled { get; init; } = true;
	public bool Required { get; init; }
	public bool Emphasize { get; init; }
	public bool ShowInDiscoveryDocument { get; init; } = true;
	public List<string> UserClaims { get; init; } = [];
}

/// <summary>
/// Request to update an existing OAuth scope.
/// </summary>
public record UpdateOAuthScopeDto
{
	public string? DisplayName { get; init; }
	public string? Description { get; init; }
	public List<string>? Resources { get; init; }
	public bool? Enabled { get; init; }
	public bool? Required { get; init; }
	public bool? Emphasize { get; init; }
	public bool? ShowInDiscoveryDocument { get; init; }
	public List<string>? UserClaims { get; init; }
}

/// <summary>
/// Response containing a list of OAuth scopes.
/// </summary>
public record OAuthScopeListDto
{
	public required List<OAuthScopeDto> Items { get; init; }
	public int TotalCount { get; init; }
}
