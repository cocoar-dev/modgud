namespace Cocoar.Auth.Application.DTOs.OAuth;

/// <summary>
/// DTO for an API secret entry (metadata only, never includes the plaintext value).
/// </summary>
public record ApiSecretEntryDto
{
	/// <summary>
	/// Unique identifier for this secret entry.
	/// </summary>
	public required string SecretId { get; init; }

	/// <summary>
	/// The type of secret (e.g., "SharedSecret").
	/// </summary>
	public required string Type { get; init; }

	/// <summary>
	/// Optional description of this secret.
	/// </summary>
	public string? Description { get; init; }

	/// <summary>
	/// Optional expiration date for this secret.
	/// </summary>
	public DateTimeOffset? Expiration { get; init; }

	/// <summary>
	/// When this secret was created.
	/// </summary>
	public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// DTO for OAuth API resource responses.
/// </summary>
public record OAuthApiResourceDto
{
	public required string Id { get; init; }
	public required string Name { get; init; }
	public string? DisplayName { get; init; }
	public string? Description { get; init; }
	public bool Enabled { get; init; }
	public required List<string> Scopes { get; init; }
	public required List<string> UserClaims { get; init; }

	/// <summary>
	/// Metadata about the API secrets (never includes plaintext values).
	/// </summary>
	public List<ApiSecretEntryDto> Secrets { get; init; } = new();
}

/// <summary>
/// DTO for creating an OAuth API resource.
/// </summary>
public record CreateOAuthApiResourceDto
{
	/// <summary>
	/// The unique name/identifier for this API resource.
	/// This is the audience value that will appear in tokens.
	/// </summary>
	public required string Name { get; init; }

	/// <summary>
	/// Human-readable display name.
	/// </summary>
	public string? DisplayName { get; init; }

	/// <summary>
	/// Description of this API resource.
	/// </summary>
	public string? Description { get; init; }

	/// <summary>
	/// Whether this API resource is enabled.
	/// </summary>
	public bool Enabled { get; init; } = true;

	/// <summary>
	/// Scopes that grant access to this API resource.
	/// </summary>
	public List<string> Scopes { get; init; } = new();

	/// <summary>
	/// User claims to include in tokens for this API.
	/// </summary>
	public List<string> UserClaims { get; init; } = new();
}

/// <summary>
/// DTO for updating an OAuth API resource.
/// </summary>
public record UpdateOAuthApiResourceDto
{
	/// <summary>
	/// Human-readable display name.
	/// </summary>
	public string? DisplayName { get; init; }

	/// <summary>
	/// Description of this API resource.
	/// </summary>
	public string? Description { get; init; }

	/// <summary>
	/// Whether this API resource is enabled.
	/// </summary>
	public bool? Enabled { get; init; }

	/// <summary>
	/// Scopes that grant access to this API resource.
	/// </summary>
	public List<string>? Scopes { get; init; }

	/// <summary>
	/// User claims to include in tokens for this API.
	/// </summary>
	public List<string>? UserClaims { get; init; }
}

/// <summary>
/// DTO for API resource list responses.
/// </summary>
public record OAuthApiResourceListDto
{
	public required List<OAuthApiResourceDto> Items { get; init; }
	public int TotalCount { get; init; }
}

/// <summary>
/// DTO returned when creating an API resource, includes the generated secret.
/// </summary>
public record OAuthApiResourceCreatedDto
{
	public required string Id { get; init; }
	public required string Name { get; init; }
	public string? DisplayName { get; init; }
	public string? Description { get; init; }
	public bool Enabled { get; init; }
	public required List<string> Scopes { get; init; }
	public required List<string> UserClaims { get; init; }

	/// <summary>
	/// The generated API secret. Only returned once during creation.
	/// </summary>
	public required string ApiSecret { get; init; }
}

/// <summary>
/// DTO for API secret regeneration response.
/// </summary>
public record ApiSecretDto
{
	/// <summary>
	/// The newly generated API secret.
	/// </summary>
	public required string ApiSecret { get; init; }
}

/// <summary>
/// DTO for creating a new API secret.
/// </summary>
public record CreateApiSecretDto
{
	/// <summary>
	/// The type of secret (e.g., "SharedSecret"). Defaults to "SharedSecret".
	/// </summary>
	public string Type { get; init; } = "SharedSecret";

	/// <summary>
	/// Optional description of this secret.
	/// </summary>
	public string? Description { get; init; }

	/// <summary>
	/// Optional expiration date for this secret.
	/// </summary>
	public DateTimeOffset? Expiration { get; init; }
}

/// <summary>
/// DTO returned after creating a new API secret, includes the plaintext value.
/// </summary>
public record ApiSecretCreatedDto
{
	/// <summary>
	/// The unique identifier for this secret entry.
	/// </summary>
	public required string SecretId { get; init; }

	/// <summary>
	/// The plaintext API secret. Only returned once during creation.
	/// </summary>
	public required string ApiSecret { get; init; }
}
