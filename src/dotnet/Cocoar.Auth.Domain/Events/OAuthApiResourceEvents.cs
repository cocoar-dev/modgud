namespace Cocoar.Auth.Domain.Events;

/// <summary>
/// Event raised when a new OAuth API resource is created.
/// </summary>
public record OAuthApiResourceCreated(
	Guid ApiResourceId,
	string Name,
	string? DisplayName,
	string? Description,
	bool Enabled,
	IReadOnlyList<string> Scopes);

/// <summary>
/// Event raised when an API resource's display name is changed.
/// </summary>
public record OAuthApiResourceDisplayNameChanged(Guid ApiResourceId, string? DisplayName);

/// <summary>
/// Event raised when an API resource's description is changed.
/// </summary>
public record OAuthApiResourceDescriptionChanged(Guid ApiResourceId, string? Description);

/// <summary>
/// Event raised when an API resource is enabled.
/// </summary>
public record OAuthApiResourceEnabled(Guid ApiResourceId);

/// <summary>
/// Event raised when an API resource is disabled.
/// </summary>
public record OAuthApiResourceDisabled(Guid ApiResourceId);

/// <summary>
/// Event raised when an API resource's scopes are changed.
/// </summary>
public record OAuthApiResourceScopesChanged(Guid ApiResourceId, IReadOnlyList<string> Scopes);

/// <summary>
/// Event raised when an API resource's user claims are changed.
/// These are claims that should be included in tokens for this API.
/// </summary>
public record OAuthApiResourceUserClaimsChanged(Guid ApiResourceId, IReadOnlyList<string> UserClaims);

/// <summary>
/// Event raised when an API resource's properties are changed.
/// </summary>
public record OAuthApiResourcePropertiesChanged(Guid ApiResourceId, IReadOnlyDictionary<string, object?> Properties);

/// <summary>
/// Event raised when an API resource is deleted.
/// </summary>
public record OAuthApiResourceDeleted(Guid ApiResourceId);
