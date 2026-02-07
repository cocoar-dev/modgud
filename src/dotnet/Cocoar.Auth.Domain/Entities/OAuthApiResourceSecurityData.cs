using System.Text.Json.Serialization;

namespace Cocoar.Auth.Domain.Entities;

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
	/// APIs use this secret to authenticate when calling the introspection endpoint.
	/// </summary>
	[JsonInclude]
	public string? ApiSecret { get; set; }

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
