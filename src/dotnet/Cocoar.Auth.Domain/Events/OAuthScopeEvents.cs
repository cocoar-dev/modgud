namespace Cocoar.Auth.Domain.Events;

/// <summary>
/// Event raised when an OAuth scope is created.
/// </summary>
public record OAuthScopeCreated(
	Guid ScopeId,
	string Name,
	string? DisplayName,
	string? Description,
	IReadOnlyList<string> Resources);

/// <summary>
/// Event raised when an OAuth scope's display name is changed.
/// </summary>
public record OAuthScopeDisplayNameChanged(
	Guid ScopeId,
	string? DisplayName);

/// <summary>
/// Event raised when an OAuth scope's description is changed.
/// </summary>
public record OAuthScopeDescriptionChanged(
	Guid ScopeId,
	string? Description);

/// <summary>
/// Event raised when an OAuth scope's resources are changed.
/// </summary>
public record OAuthScopeResourcesChanged(
	Guid ScopeId,
	IReadOnlyList<string> Resources);

/// <summary>
/// Event raised when an OAuth scope's localized display names are changed.
/// </summary>
public record OAuthScopeDisplayNamesChanged(
	Guid ScopeId,
	IReadOnlyDictionary<string, string> DisplayNames);

/// <summary>
/// Event raised when an OAuth scope's localized descriptions are changed.
/// </summary>
public record OAuthScopeDescriptionsChanged(
	Guid ScopeId,
	IReadOnlyDictionary<string, string> Descriptions);

/// <summary>
/// Event raised when an OAuth scope's properties are changed.
/// </summary>
public record OAuthScopePropertiesChanged(
	Guid ScopeId,
	IReadOnlyDictionary<string, object?> Properties);

/// <summary>
/// Event raised when an OAuth scope is deleted.
/// </summary>
public record OAuthScopeDeleted(Guid ScopeId);
