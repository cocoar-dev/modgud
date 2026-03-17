namespace Cocoar.Auth.Domain.Events;

/// <summary>
/// Event raised when a new OAuth API is created.
/// </summary>
public record OAuthApiCreated(
	Guid ApiId,
	string Name,
	string? DisplayName,
	string? Description,
	bool Enabled,
	IReadOnlyList<string> Scopes);

/// <summary>
/// Event raised when an API's display name is changed.
/// </summary>
public record OAuthApiDisplayNameChanged(Guid ApiId, string? DisplayName);

/// <summary>
/// Event raised when an API's description is changed.
/// </summary>
public record OAuthApiDescriptionChanged(Guid ApiId, string? Description);

/// <summary>
/// Event raised when an API is enabled.
/// </summary>
public record OAuthApiEnabled(Guid ApiId);

/// <summary>
/// Event raised when an API is disabled.
/// </summary>
public record OAuthApiDisabled(Guid ApiId);

/// <summary>
/// Event raised when an API's scopes are changed.
/// </summary>
public record OAuthApiScopesChanged(Guid ApiId, IReadOnlyList<string> Scopes);

/// <summary>
/// Event raised when an API's user claims are changed.
/// These are claims that should be included in tokens for this API.
/// </summary>
public record OAuthApiUserClaimsChanged(Guid ApiId, IReadOnlyList<string> UserClaims);

/// <summary>
/// Event raised when an API's properties are changed.
/// </summary>
public record OAuthApiPropertiesChanged(Guid ApiId, IReadOnlyDictionary<string, object?> Properties);

/// <summary>
/// Event raised when an API is deleted.
/// </summary>
public record OAuthApiDeleted(Guid ApiId);
