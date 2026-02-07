using System.Text.Json.Serialization;

namespace Cocoar.Auth.Domain.Entities;

/// <summary>
/// Document entity for OpenIddict tokens.
/// This data is NOT event-sourced - tokens are security-sensitive and ephemeral.
/// </summary>
public class OpenIddictTokenDocument
{
	/// <summary>
	/// The unique identifier for this token.
	/// </summary>
	[JsonInclude]
	public string Id { get; set; } = Guid.NewGuid().ToString();

	/// <summary>
	/// The identifier of the application associated with this token.
	/// </summary>
	[JsonInclude]
	public string? ApplicationId { get; set; }

	/// <summary>
	/// The identifier of the authorization associated with this token.
	/// </summary>
	[JsonInclude]
	public string? AuthorizationId { get; set; }

	/// <summary>
	/// The creation date of this token.
	/// </summary>
	[JsonInclude]
	public DateTimeOffset? CreationDate { get; set; }

	/// <summary>
	/// The expiration date of this token.
	/// </summary>
	[JsonInclude]
	public DateTimeOffset? ExpirationDate { get; set; }

	/// <summary>
	/// The payload of this token.
	/// </summary>
	[JsonInclude]
	public string? Payload { get; set; }

	/// <summary>
	/// The additional properties associated with this token.
	/// </summary>
	[JsonInclude]
	public Dictionary<string, object?> Properties { get; set; } = new();

	/// <summary>
	/// The redemption date of this token.
	/// </summary>
	[JsonInclude]
	public DateTimeOffset? RedemptionDate { get; set; }

	/// <summary>
	/// The reference identifier associated with this token.
	/// </summary>
	[JsonInclude]
	public string? ReferenceId { get; set; }

	/// <summary>
	/// The status of this token.
	/// </summary>
	[JsonInclude]
	public string? Status { get; set; }

	/// <summary>
	/// The subject associated with this token.
	/// </summary>
	[JsonInclude]
	public string? Subject { get; set; }

	/// <summary>
	/// The type of this token.
	/// </summary>
	[JsonInclude]
	public string? Type { get; set; }

	/// <summary>
	/// A random value that changes when the document is persisted.
	/// Used for optimistic concurrency.
	/// </summary>
	[JsonInclude]
	public string ConcurrencyToken { get; set; } = Guid.NewGuid().ToString();
}
