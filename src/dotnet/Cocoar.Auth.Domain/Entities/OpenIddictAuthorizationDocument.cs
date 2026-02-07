using System.Text.Json.Serialization;

namespace Cocoar.Auth.Domain.Entities;

/// <summary>
/// Document entity for OpenIddict authorizations.
/// This data is NOT event-sourced - authorizations are security-sensitive and ephemeral.
/// </summary>
public class OpenIddictAuthorizationDocument
{
	/// <summary>
	/// The unique identifier for this authorization.
	/// </summary>
	[JsonInclude]
	public string Id { get; set; } = Guid.NewGuid().ToString();

	/// <summary>
	/// The identifier of the application associated with this authorization.
	/// </summary>
	[JsonInclude]
	public string? ApplicationId { get; set; }

	/// <summary>
	/// The creation date of this authorization.
	/// </summary>
	[JsonInclude]
	public DateTimeOffset? CreationDate { get; set; }

	/// <summary>
	/// The additional properties associated with this authorization.
	/// </summary>
	[JsonInclude]
	public Dictionary<string, object?> Properties { get; set; } = new();

	/// <summary>
	/// The scopes associated with this authorization.
	/// </summary>
	[JsonInclude]
	public HashSet<string> Scopes { get; set; } = new();

	/// <summary>
	/// The status of this authorization.
	/// </summary>
	[JsonInclude]
	public string? Status { get; set; }

	/// <summary>
	/// The subject associated with this authorization.
	/// </summary>
	[JsonInclude]
	public string? Subject { get; set; }

	/// <summary>
	/// The type of this authorization.
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
