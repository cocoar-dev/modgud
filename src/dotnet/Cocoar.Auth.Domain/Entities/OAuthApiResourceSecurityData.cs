using System.Text.Json.Serialization;

namespace Cocoar.Auth.Domain.Entities;

/// <summary>
/// Represents a single API secret entry with metadata.
/// The Value field stores the BCrypt hash, never the plaintext.
/// </summary>
public class ApiSecretEntry
{
	/// <summary>
	/// Unique identifier for this secret entry.
	/// </summary>
	[JsonInclude]
	public Guid SecretId { get; set; } = Guid.NewGuid();

	/// <summary>
	/// The type of secret (e.g., "SharedSecret").
	/// </summary>
	[JsonInclude]
	public string Type { get; set; } = "SharedSecret";

	/// <summary>
	/// The hashed secret value (BCrypt).
	/// </summary>
	[JsonInclude]
	public string HashedValue { get; set; } = string.Empty;

	/// <summary>
	/// Optional description of this secret.
	/// </summary>
	[JsonInclude]
	public string? Description { get; set; }

	/// <summary>
	/// Optional expiration date for this secret.
	/// </summary>
	[JsonInclude]
	public DateTimeOffset? Expiration { get; set; }

	/// <summary>
	/// When this secret was created.
	/// </summary>
	[JsonInclude]
	public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Document entity for security-sensitive OAuth API resource data.
/// This data is NOT event-sourced to avoid storing sensitive information in the event history.
/// Uses the same ID as the OAuthApiResourceAggregate for correlation.
/// </summary>
public class OAuthApiResourceSecurityData
{
	/// <summary>
	/// The unique identifier for this API resource (same as OAuthApiResourceAggregate.Id).
	/// </summary>
	[JsonInclude]
	public Guid Id { get; set; }

	/// <summary>
	/// The hashed API secret used for introspection authentication.
	/// Kept for backward compatibility; new secrets should use the Secrets list.
	/// </summary>
	[JsonInclude]
	public string? ApiSecret { get; set; }

	/// <summary>
	/// Collection of API secrets with metadata.
	/// Supports multiple secrets for key rotation scenarios.
	/// </summary>
	[JsonInclude]
	public List<ApiSecretEntry> Secrets { get; set; } = new();

	/// <summary>
	/// A random value that changes when the document is persisted.
	/// Used for optimistic concurrency.
	/// </summary>
	[JsonInclude]
	public string ConcurrencyToken { get; set; } = Guid.NewGuid().ToString();

	/// <summary>
	/// Creates a new OAuthApiResourceSecurityData for an API resource.
	/// </summary>
	public static OAuthApiResourceSecurityData Create(Guid apiResourceId)
	{
		return new OAuthApiResourceSecurityData
		{
			Id = apiResourceId,
			ConcurrencyToken = Guid.NewGuid().ToString()
		};
	}

	/// <summary>
	/// Updates the concurrency token.
	/// </summary>
	public void UpdateConcurrencyToken()
	{
		ConcurrencyToken = Guid.NewGuid().ToString();
	}
}
