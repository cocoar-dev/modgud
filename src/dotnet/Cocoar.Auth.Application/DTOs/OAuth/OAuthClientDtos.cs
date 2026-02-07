namespace Cocoar.Auth.Application.DTOs.OAuth;

/// <summary>
/// OAuth client information for API responses.
/// </summary>
public record OAuthClientDto
{
	public required string Id { get; init; }
	public required string ClientId { get; init; }
	public string? DisplayName { get; init; }
	public required string ClientType { get; init; }
	public required string ConsentType { get; init; }
	public required List<string> RedirectUris { get; init; }
	public required List<string> PostLogoutRedirectUris { get; init; }
	public required List<string> Permissions { get; init; }
	public DateTimeOffset? CreatedAt { get; init; }
}

/// <summary>
/// Request to create a new OAuth client.
/// </summary>
public record CreateOAuthClientDto
{
	public required string ClientId { get; init; }
	public string? DisplayName { get; init; }
	public required string ClientType { get; init; }
	public string? ClientSecret { get; init; }
	public string ConsentType { get; init; } = "implicit";
	public List<string> RedirectUris { get; init; } = [];
	public List<string> PostLogoutRedirectUris { get; init; } = [];
	public List<string> Scopes { get; init; } = [];
}

/// <summary>
/// Request to update an existing OAuth client.
/// </summary>
public record UpdateOAuthClientDto
{
	public string? DisplayName { get; init; }
	public string? ConsentType { get; init; }
	public List<string>? RedirectUris { get; init; }
	public List<string>? PostLogoutRedirectUris { get; init; }
	public List<string>? Scopes { get; init; }
}

/// <summary>
/// Response containing a list of OAuth clients.
/// </summary>
public record OAuthClientListDto
{
	public required List<OAuthClientDto> Items { get; init; }
	public int TotalCount { get; init; }
}

/// <summary>
/// Response containing a newly generated client secret.
/// </summary>
public record ClientSecretDto
{
	public required string ClientSecret { get; init; }
}

/// <summary>
/// Response containing the created client with its secret.
/// </summary>
public record OAuthClientCreatedDto
{
	public required OAuthClientDto Client { get; init; }
	public string? ClientSecret { get; init; }
}
