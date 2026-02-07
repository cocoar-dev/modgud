using System.Text.Json.Serialization;

namespace Cocoar.Auth.Domain.Entities;

/// <summary>
/// Document entity for security-sensitive OAuth application data.
/// This data is NOT event-sourced to avoid storing sensitive information in the event history.
/// Uses the same ID as the OAuthApplicationAggregate for correlation.
/// </summary>
public class OAuthApplicationSecurityData
{
	/// <summary>
	/// The unique identifier for this application (same as OAuthApplicationAggregate.Id).
	/// </summary>
	[JsonInclude]
	public Guid Id { get; set; }

	/// <summary>
	/// The hashed client secret associated with the application.
	/// </summary>
	[JsonInclude]
	public string? ClientSecret { get; set; }

	/// <summary>
	/// The JSON Web Key Set associated with the application (serialized).
	/// May contain private keys, so must not be in event history.
	/// </summary>
	[JsonInclude]
	public string? JsonWebKeySet { get; set; }

	/// <summary>
	/// A random value that changes when the document is persisted.
	/// Used for optimistic concurrency.
	/// </summary>
	[JsonInclude]
	public string ConcurrencyToken { get; set; } = Guid.NewGuid().ToString();

	/// <summary>
	/// Creates a new OAuthApplicationSecurityData for an application.
	/// </summary>
	public static OAuthApplicationSecurityData Create(Guid applicationId)
	{
		return new OAuthApplicationSecurityData
		{
			Id = applicationId,
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
