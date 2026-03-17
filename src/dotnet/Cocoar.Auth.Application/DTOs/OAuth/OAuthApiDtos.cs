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
/// DTO for OAuth API responses.
/// </summary>
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
	/// Metadata about the API secrets (never includes plaintext values).
	/// </summary>
	public List<ApiSecretEntryDto> Secrets { get; init; } = new();
}

/// <summary>
/// DTO for creating an OAuth API.
/// </summary>
public record CreateOAuthApiDto
{
	/// <summary>
	/// The unique name/identifier for this API.
	/// This is the audience value that will appear in tokens.
	/// </summary>
	public required string Name { get; init; }

	/// <summary>
	/// Human-readable display name.
	/// </summary>
	public string? DisplayName { get; init; }

	/// <summary>
	/// Description of this API.
	/// </summary>
	public string? Description { get; init; }

	/// <summary>
	/// Whether this API is enabled.
	/// </summary>
	public bool Enabled { get; init; } = true;

	/// <summary>
	/// Scopes that grant access to this API.
	/// </summary>
	public List<string> Scopes { get; init; } = new();

	/// <summary>
	/// User claims to include in tokens for this API.
	/// </summary>
	public List<string> UserClaims { get; init; } = new();
}

/// <summary>
/// DTO for updating an OAuth API.
/// </summary>
public record UpdateOAuthApiDto
{
	/// <summary>
	/// Human-readable display name.
	/// </summary>
	public string? DisplayName { get; init; }

	/// <summary>
	/// Description of this API.
	/// </summary>
	public string? Description { get; init; }

	/// <summary>
	/// Whether this API is enabled.
	/// </summary>
	public bool? Enabled { get; init; }

	/// <summary>
	/// Scopes that grant access to this API.
	/// </summary>
	public List<string>? Scopes { get; init; }

	/// <summary>
	/// User claims to include in tokens for this API.
	/// </summary>
	public List<string>? UserClaims { get; init; }
}

/// <summary>
/// DTO for API list responses.
/// </summary>
public record OAuthApiListDto
{
	public required List<OAuthApiDto> Items { get; init; }
	public int TotalCount { get; init; }
}

/// <summary>
/// DTO returned when creating an API, includes the generated secret.
/// </summary>
public record OAuthApiCreatedDto
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
